// dmmatch.cpp - candidate matching for Data Matrix (see match.cpp for the idea and safeguards).
//
// Liquor cap labels carry a Data Matrix whose quiet zone is broken by the label's printed border
// and whose print bleeds, worst where the lens blurs the case corners: the decoders cannot find
// or threshold them, but the module grid is still there. The code square is found as a dense
// dark block on the bright label (borders removed by an opening), jittered a little, sampled as
// an n x n grid, oriented by its L finder and clock tracks, and compared with the candidates'
// patterns over the data modules.
//
// Encoder: ECC200, square symbols with one data region (10 x 10 .. 26 x 26), ASCII encodation
// (digit pairs), Reed-Solomon over GF(256) / 0x12D, the standard module placement. The printer's
// encodation is checked against decoded codes before any match is trusted (learnDmEncoding).

#include "reader.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <chrono>
#include <cmath>
#include <limits>

namespace cr {

namespace {

struct DmSize {
    int n, data, ecc;
};
constexpr DmSize kSizes[] = {{10, 3, 5}, {12, 5, 7}, {14, 8, 10}, {16, 12, 12}, {18, 18, 14},
                             {20, 22, 18}, {22, 30, 20}, {24, 36, 24}, {26, 44, 28}};

struct Gf {
    std::array<int, 512> exp{};
    std::array<int, 256> log{};
    explicit Gf(int prim = 0x12D)
    {
        int x = 1;
        for (int i = 0; i < 255; ++i) {
            exp[static_cast<size_t>(i)] = x;
            log[static_cast<size_t>(x)] = i;
            x <<= 1;
            if (x & 0x100) x ^= prim;
        }
        for (int i = 255; i < 512; ++i) exp[static_cast<size_t>(i)] = exp[static_cast<size_t>(i - 255)];
    }
    int mul(int a, int b) const
    {
        return a == 0 || b == 0 ? 0 : exp[static_cast<size_t>(log[static_cast<size_t>(a)] + log[static_cast<size_t>(b)])];
    }
};

const Gf& gf() // Data Matrix field
{
    static const Gf g(0x12D);
    return g;
}

const Gf& gfQr() // QR field
{
    static const Gf g(0x11D);
    return g;
}

std::vector<int> reedSolomon(const std::vector<int>& data, int nEcc)
{
    const Gf& g = gf();
    std::vector<int> gen = {1}; // low to high degree
    for (int i = 1; i <= nEcc; ++i) {
        std::vector<int> next(gen.size() + 1, 0);
        for (size_t j = 0; j < gen.size(); ++j) {
            next[j] ^= g.mul(gen[j], g.exp[static_cast<size_t>(i)]);
            next[j + 1] ^= gen[j];
        }
        gen = next;
    }
    std::reverse(gen.begin(), gen.end()); // high to low, leading 1
    std::vector<int> msg(data);
    msg.resize(data.size() + static_cast<size_t>(nEcc), 0);
    for (size_t i = 0; i < data.size(); ++i) {
        const int coef = msg[i];
        if (!coef) continue;
        for (size_t j = 1; j < gen.size(); ++j) msg[i + j] ^= g.mul(gen[j], coef);
    }
    return std::vector<int>(msg.begin() + static_cast<long>(data.size()), msg.end());
}

std::vector<int> asciiCodewords(const std::string& t, bool gs1 = false)
{
    std::vector<int> cw;
    if (gs1) cw.push_back(232); // FNC1 first: GS1 data
    for (size_t i = 0; i < t.size();) {
        const auto d = [&](size_t k) { return std::isdigit(static_cast<unsigned char>(t[k])) != 0; };
        if (i + 1 < t.size() && d(i) && d(i + 1)) {
            cw.push_back(130 + (t[i] - '0') * 10 + (t[i + 1] - '0'));
            i += 2;
        } else {
            const unsigned char ch = static_cast<unsigned char>(t[i]);
            if (ch > 127) return {}; // extended ASCII needs an upper shift: not this printer
            cw.push_back(gs1 && ch == 29 ? 232 : ch + 1); // GS1: the separator is FNC1
            ++i;
        }
    }
    return cw;
}

// ECC200 module placement (ISO/IEC 16022 annex F) of codewords into an nrow x ncol mapping matrix
class Placement {
public:
    Placement(const std::vector<int>& cw, int nrow, int ncol)
        : cw_(cw), nrow_(nrow), ncol_(ncol), m_(static_cast<size_t>(nrow * ncol), -1), pos_(cw.size() * 8, -1)
    {
        int r = 4, c = 0, v = 0;
        for (;;) {
            if (r == nrow && c == 0) corner(v++, {{{nrow - 1, 0}, {nrow - 1, 1}, {nrow - 1, 2}, {0, ncol - 2}, {0, ncol - 1}, {1, ncol - 1}, {2, ncol - 1}, {3, ncol - 1}}});
            if (r == nrow - 2 && c == 0 && ncol % 4) corner(v++, {{{nrow - 3, 0}, {nrow - 2, 0}, {nrow - 1, 0}, {0, ncol - 4}, {0, ncol - 3}, {0, ncol - 2}, {0, ncol - 1}, {1, ncol - 1}}});
            if (r == nrow - 2 && c == 0 && ncol % 8 == 4) corner(v++, {{{nrow - 3, 0}, {nrow - 2, 0}, {nrow - 1, 0}, {0, ncol - 2}, {0, ncol - 1}, {1, ncol - 1}, {2, ncol - 1}, {3, ncol - 1}}});
            if (r == nrow + 4 && c == 2 && !(ncol % 8)) corner(v++, {{{nrow - 1, 0}, {nrow - 1, ncol - 1}, {0, ncol - 3}, {0, ncol - 2}, {0, ncol - 1}, {1, ncol - 3}, {1, ncol - 2}, {1, ncol - 1}}});
            do { // up and to the right
                if (r < nrow && c >= 0 && at(r, c) < 0) utah(r, c, v++);
                r -= 2;
                c += 2;
            } while (r >= 0 && c < ncol);
            r += 1;
            c += 3;
            do { // down and to the left
                if (r >= 0 && c < ncol && at(r, c) < 0) utah(r, c, v++);
                r += 2;
                c -= 2;
            } while (r < nrow && c >= 0);
            r += 3;
            c += 1;
            if (!(r < nrow || c < ncol)) break;
        }
        if (at(nrow - 1, ncol - 1) < 0) {
            at(nrow - 1, ncol - 1) = at(nrow - 2, ncol - 2) = 1;
            at(nrow - 1, ncol - 2) = at(nrow - 2, ncol - 1) = 0;
        }
    }
    int get(int r, int c) const { return m_[static_cast<size_t>(r * ncol_ + c)]; }
    // where bit `bit` (0 = most significant) of codeword v sits: row * ncol + col, -1 if unplaced
    int where(int v, int bit) const { return pos_[static_cast<size_t>(v * 8 + bit)]; }

private:
    int& at(int r, int c) { return m_[static_cast<size_t>(r * ncol_ + c)]; }
    void module(int r, int c, int v, int bit)
    {
        if (r < 0) {
            r += nrow_;
            c += 4 - ((nrow_ + 4) % 8);
        }
        if (c < 0) {
            c += ncol_;
            r += 4 - ((ncol_ + 4) % 8);
        }
        at(r, c) = v < static_cast<int>(cw_.size()) ? (cw_[static_cast<size_t>(v)] >> (7 - bit)) & 1 : 0;
        if (v < static_cast<int>(cw_.size())) pos_[static_cast<size_t>(v * 8 + bit)] = r * ncol_ + c;
    }
    void utah(int r, int c, int v)
    {
        static const int d[8][2] = {{-2, -2}, {-2, -1}, {-1, -2}, {-1, -1}, {-1, 0}, {0, -2}, {0, -1}, {0, 0}};
        for (int b = 0; b < 8; ++b) module(r + d[b][0], c + d[b][1], v, b);
    }
    void corner(int v, const std::array<std::array<int, 2>, 8>& pts)
    {
        for (int b = 0; b < 8; ++b) module(pts[static_cast<size_t>(b)][0], pts[static_cast<size_t>(b)][1], v, b);
    }
    const std::vector<int>& cw_;
    int nrow_, ncol_;
    std::vector<int> m_;
    std::vector<int> pos_;
};

// check symbols kept in reserve by every Data Matrix decoding path of the engine
constexpr int kDmReserve = 4;
// a symbol seen in part (maskedSoftDecodeDm): the share of seen data modules (percent) the
// corrected symbol may disagree with
constexpr int kDmMaxDisagreePct = 12;

const DmSize* sizeOf(int n)
{
    for (const auto& s : kSizes)
        if (s.n == n) return &s;
    return nullptr;
}

// the n x n symbol of all codewords (data + error correction), with the fixed L finder and clocks
void symbolFromCodewords(const std::vector<int>& all, int n, std::vector<uint8_t>& dark)
{
    const Placement pl(all, n - 2, n - 2);
    dark.assign(static_cast<size_t>(n) * n, 0);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            uint8_t v;
            if (x == 0 || y == n - 1) v = 1;                           // L finder: left, bottom
            else if (y == 0) v = x % 2 == 0 ? 1 : 0;                   // top clock track
            else if (x == n - 1) v = (n - 1 - y) % 2 == 0 ? 1 : 0;     // right clock track
            else v = pl.get(y - 1, x - 1) == 1 ? 1 : 0;
            dark[static_cast<size_t>(y) * n + x] = v;
        }
}

int gfPow(const Gf& g, int e) // alpha^e for any integer e
{
    e %= 255;
    if (e < 0) e += 255;
    return g.exp[static_cast<size_t>(e)];
}

int gfInv(const Gf& g, int a) { return g.exp[static_cast<size_t>(255 - g.log[static_cast<size_t>(a)])]; }

// polynomial with coefficient i at index i (low degree first) evaluated at x
int polyEval(const Gf& g, const std::vector<int>& p, int x)
{
    int y = 0;
    for (size_t i = p.size(); i-- > 0;) y = g.mul(y, x) ^ p[i];
    return y;
}


} // namespace

bool encodeDm(const std::string& text, int n, std::vector<uint8_t>& dark, bool gs1)
{
    const DmSize* size = nullptr;
    for (const auto& s : kSizes)
        if (s.n == n) size = &s;
    if (!size) return false;
    std::vector<int> data = asciiCodewords(text, gs1);
    if (data.empty() || static_cast<int>(data.size()) > size->data) return false;
    // padding: 129 first, then the 253-state randomised pad
    if (static_cast<int>(data.size()) < size->data) data.push_back(129);
    while (static_cast<int>(data.size()) < size->data) {
        const int r = ((149 * (static_cast<int>(data.size()) + 1)) % 253) + 1;
        const int v = 129 + r;
        data.push_back(v > 254 ? v - 254 : v);
    }
    const std::vector<int> ecc = reedSolomon(data, size->ecc);
    std::vector<int> all(data);
    all.insert(all.end(), ecc.begin(), ecc.end());
    symbolFromCodewords(all, n, dark);
    return true;
}

// Reed-Solomon errors and erasures: codeword k is the coefficient of x^(N-1-k); the generator's
// roots are alpha^fcr .. alpha^(fcr + nEcc - 1)
static bool rsDecodeGf(const Gf& g, int fcr, std::vector<int>& cw, int nEcc, const std::vector<int>& erasures, int* errorsOut)
{
    const int N = static_cast<int>(cw.size());
    const int f = static_cast<int>(erasures.size());
    if (f > nEcc) return false;
    std::vector<int> S(static_cast<size_t>(nEcc) + 1, 0); // S[1..nEcc]
    bool clean = true;
    for (int j = 1; j <= nEcc; ++j) {
        int s = 0;
        const int a = gfPow(g, j - 1 + fcr);
        for (int k = 0; k < N; ++k) s = g.mul(s, a) ^ cw[static_cast<size_t>(k)];
        S[static_cast<size_t>(j)] = s;
        clean = clean && s == 0;
    }
    if (clean) {
        if (errorsOut) *errorsOut = 0;
        return true;
    }
    // erasure locator: product of (1 + X x), X = alpha^(N-1-k)
    std::vector<int> lambda = {1};
    for (int k : erasures) {
        const int X = gfPow(g, N - 1 - k);
        std::vector<int> next(lambda.size() + 1, 0);
        for (size_t i = 0; i < lambda.size(); ++i) {
            next[i] ^= lambda[i];
            next[i + 1] ^= g.mul(lambda[i], X);
        }
        lambda = next;
    }
    // Berlekamp-Massey started from the erasure locator (errors and erasures together)
    std::vector<int> B = lambda;
    int L = f;
    for (int r = f + 1; r <= nEcc; ++r) {
        int delta = 0;
        for (int i = 0; i <= L && i < static_cast<int>(lambda.size()); ++i)
            if (r - i >= 1) delta ^= g.mul(lambda[static_cast<size_t>(i)], S[static_cast<size_t>(r - i)]);
        std::vector<int> xB(B.size() + 1, 0);
        for (size_t i = 0; i < B.size(); ++i) xB[i + 1] = B[i];
        if (delta == 0) {
            B = xB;
            continue;
        }
        std::vector<int> T(std::max(lambda.size(), xB.size()), 0);
        for (size_t i = 0; i < lambda.size(); ++i) T[i] ^= lambda[i];
        for (size_t i = 0; i < xB.size(); ++i) T[i] ^= g.mul(delta, xB[i]);
        if (2 * L <= r + f - 1) {
            L = r + f - L;
            const int inv = gfInv(g, delta);
            B.assign(lambda.size(), 0);
            for (size_t i = 0; i < lambda.size(); ++i) B[i] = g.mul(lambda[i], inv);
        } else {
            B = xB;
        }
        lambda = T;
    }
    while (lambda.size() > 1 && lambda.back() == 0) lambda.pop_back();
    const int degree = static_cast<int>(lambda.size()) - 1;
    if (2 * (degree - f) + f > nEcc) return false;
    // Chien search: positions whose X^-1 is a root
    std::vector<int> pos;
    for (int k = 0; k < N; ++k)
        if (polyEval(g, lambda, gfPow(g, -(N - 1 - k))) == 0) pos.push_back(k);
    if (static_cast<int>(pos.size()) != degree) return false; // more errors than can be corrected
    // Forney: Omega = S(x) * Lambda(x) mod x^nEcc, S(x) = sum S[j+1] x^j
    std::vector<int> omega(static_cast<size_t>(nEcc), 0);
    for (int i = 0; i < nEcc; ++i)
        for (size_t j = 0; j < lambda.size() && static_cast<int>(j) <= i; ++j)
            omega[static_cast<size_t>(i)] ^= g.mul(S[static_cast<size_t>(i - static_cast<int>(j) + 1)], lambda[j]);
    std::vector<int> dLambda; // formal derivative: odd terms
    for (size_t i = 1; i < lambda.size(); ++i) dLambda.push_back(i % 2 ? lambda[i] : 0);
    for (int k : pos) {
        const int Xinv = gfPow(g, -(N - 1 - k));
        const int den = polyEval(g, dLambda, Xinv);
        if (den == 0) return false;
        // Forney: magnitude = X^(1 - fcr) * Omega(X^-1) / Lambda'(X^-1)
        const int mag = g.mul(polyEval(g, omega, Xinv), gfInv(g, den));
        cw[static_cast<size_t>(k)] ^= g.mul(mag, gfPow(g, (1 - fcr) * (N - 1 - k)));
    }
    // the corrected word must be a codeword
    for (int j = 1; j <= nEcc; ++j) {
        int s = 0;
        const int a = gfPow(g, j - 1 + fcr);
        for (int k = 0; k < N; ++k) s = g.mul(s, a) ^ cw[static_cast<size_t>(k)];
        if (s) return false;
    }
    if (errorsOut) {
        int e = 0;
        for (int k : pos) e += std::find(erasures.begin(), erasures.end(), k) == erasures.end();
        *errorsOut = e;
    }
    return true;
}

bool rsDecodeDm(std::vector<int>& cw, int nEcc, const std::vector<int>& erasures, int* errorsOut)
{
    return rsDecodeGf(gf(), 1, cw, nEcc, erasures, errorsOut);
}

bool rsDecodeQr(std::vector<int>& cw, int nEcc, const std::vector<int>& erasures, int* errorsOut)
{
    return rsDecodeGf(gfQr(), 0, cw, nEcc, erasures, errorsOut);
}

std::string softDecodeDm(const cv::Mat& means, int n, int* erasuresUsed)
{
    const DmSize* size = sizeOf(n);
    if (!size || means.rows != n || means.cols != n) return {};
    // data modules only: the L finder and clocks are known ('virtual perimeter')
    const cv::Mat inner = means(cv::Rect(1, 1, n - 2, n - 2));
    cv::Mat i8, scratch;
    cv::normalize(inner, i8, 0, 255, cv::NORM_MINMAX, CV_8U);
    double mn, mx;
    cv::minMaxLoc(inner, &mn, &mx);
    const double t8 = cv::threshold(i8, scratch, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
    // Otsu splits the modules into dark and light; the threshold is midway between the two means
    // (Otsu's own value sits on the dark level when the image is two-valued)
    const double cut = mn + t8 / 255.0 * (mx - mn);
    double sumD = 0, sumL = 0;
    int nD = 0, nL = 0;
    for (int y = 0; y < inner.rows; ++y)
        for (int x = 0; x < inner.cols; ++x) {
            const float m = inner.at<float>(y, x);
            if (m <= cut) { sumD += m; ++nD; } else { sumL += m; ++nL; }
        }
    if (nD == 0 || nL == 0) return {};
    const double t = (sumD / nD + sumL / nL) / 2;
    cv::Scalar mean, sd;
    cv::meanStdDev(inner, mean, sd);
    const double spread = sd[0] + 1e-6;
    const int total = size->data + size->ecc;
    std::vector<int> layout(static_cast<size_t>(total), 0);
    const Placement pl(layout, n - 2, n - 2);
    std::vector<int> cw(static_cast<size_t>(total), 0);
    std::vector<std::pair<double, int>> conf; // least sure codewords first
    for (int k = 0; k < total; ++k) {
        int val = 0;
        double cmin = 1e9;
        for (int b = 0; b < 8; ++b) {
            const int w = pl.where(k, b);
            if (w < 0) return {};
            const float m = inner.at<float>(w / (n - 2), w % (n - 2));
            val |= (m < t ? 1 : 0) << (7 - b);
            cmin = std::min(cmin, std::abs(m - t) / spread);
        }
        cw[static_cast<size_t>(k)] = val;
        conf.emplace_back(cmin, k);
    }
    std::sort(conf.begin(), conf.end());
    // four check symbols stay in reserve (2e + f <= ecc - 4): a lattice search makes thousands
    // of attempts per region, and two symbols (16 bits) let one false correction through
    for (int f = 0; f <= size->ecc - kDmReserve; ++f) {
        std::vector<int> er;
        for (int i = 0; i < f; ++i) er.push_back(conf[static_cast<size_t>(i)].second);
        std::vector<int> fixed = cw;
        int e = 0;
        if (!rsDecodeDm(fixed, size->ecc, er, &e)) continue;
        if (2 * e + f > size->ecc - kDmReserve) continue;
        std::vector<uint8_t> dark;
        symbolFromCodewords(fixed, n, dark);
        cv::Mat d(n, n, CV_8U, dark.data());
        const std::string text = decodeDmBits(d.clone()); // the data must also parse
        if (text.empty()) continue;
        if (erasuresUsed) *erasuresUsed = f;
        return text;
    }
    return {};
}

// Known-content soft decoding: codes of one product share all but their serial, so the data
// codewords equal in every code already read (texts) are known. Their modules, with the L finder
// and the clock tracks, train a linear classifier on each module's 3 x 3 neighbourhood of means
// (it learns the local blur and print growth); the other modules are decided by it, and
// Reed-Solomon corrects the unknown part with the known codewords fixed, the least certain others
// erased and two check symbols in reserve. The redrawn symbol must parse.
std::string knownSoftDecodeDm(const cv::Mat& means, int n, const std::vector<std::string>& texts)
{
    const DmSize* size = sizeOf(n);
    if (!size || means.rows != n || means.cols != n || texts.size() < 2) return {};
    // data codewords of each text: ASCII encodation, then the standard padding
    std::vector<int> known;
    std::vector<uint8_t> isKnown;
    int used = 0;
    for (const auto& t : texts) {
        std::vector<int> d = asciiCodewords(t);
        if (d.empty() || static_cast<int>(d.size()) > size->data) continue;
        if (static_cast<int>(d.size()) < size->data) d.push_back(129);
        while (static_cast<int>(d.size()) < size->data) {
            const int r = ((149 * (static_cast<int>(d.size()) + 1)) % 253) + 1;
            const int v = 129 + r;
            d.push_back(v > 254 ? v - 254 : v);
        }
        if (used++ == 0) {
            known = d;
            isKnown.assign(d.size(), 1);
        } else
            for (size_t k = 0; k < d.size(); ++k)
                if (d[k] != known[k]) isKnown[k] = 0;
    }
    if (used < 2) return {};
    const int nKnown = static_cast<int>(std::count(isKnown.begin(), isKnown.end(), 1));
    if (nKnown * 3 < size->data || nKnown == size->data) return {};

    const int total = size->data + size->ecc, in = n - 2;
    std::vector<int> layout(static_cast<size_t>(total), 0);
    const Placement pl(layout, in, in);
    // known modules: 1 dark, 0 light, -1 unknown
    std::vector<int8_t> truth(static_cast<size_t>(n) * n, -1);
    auto at = [&](int y, int x) -> int8_t& { return truth[static_cast<size_t>(y) * n + x]; };
    for (int i = 0; i < n; ++i) {
        at(i, 0) = 1;                                  // L finder: left column
        at(n - 1, i) = 1;                              // and bottom row
        at(0, i) = static_cast<int8_t>(i % 2 == 0);    // clock: top row
        at(i, n - 1) = static_cast<int8_t>((n - 1 - i) % 2 == 0); // clock: right column
    }
    at(n - 1, 0) = 1;
    at(n - 1, n - 1) = 1;
    for (int k = 0; k < size->data; ++k) {
        if (!isKnown[static_cast<size_t>(k)]) continue;
        for (int b = 0; b < 8; ++b) {
            const int w = pl.where(k, b);
            if (w < 0) return {};
            at(w / in + 1, w % in + 1) = static_cast<int8_t>((known[static_cast<size_t>(k)] >> (7 - b)) & 1);
        }
    }
    // features: the 3 x 3 neighbourhood of means, standardised over the known modules
    cv::Mat v, padded;
    means.convertTo(v, CV_64F);
    cv::copyMakeBorder(v, padded, 1, 1, 1, 1, cv::BORDER_REPLICATE);
    double mu = 0, sq = 0;
    int cnt = 0;
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x)
            if (at(y, x) >= 0) { const double a = v.at<double>(y, x); mu += a; sq += a * a; ++cnt; }
    mu /= cnt;
    const double sd = std::sqrt(std::max(1e-9, sq / cnt - mu * mu));
    auto feat = [&](int y, int x, double* f) {
        int j = 0;
        for (int dy = 0; dy < 3; ++dy)
            for (int dx = 0; dx < 3; ++dx) f[j++] = (padded.at<double>(y + dy, x + dx) - mu) / sd;
        f[9] = 1;
    };
    for (double lambda : {0.1, 1.0}) {
        cv::Mat A = cv::Mat::zeros(10, 10, CV_64F), bv = cv::Mat::zeros(10, 1, CV_64F);
        double f[10];
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x) {
                if (at(y, x) < 0) continue;
                feat(y, x, f);
                const double t = at(y, x) ? 1 : -1;
                for (int a = 0; a < 10; ++a) {
                    bv.at<double>(a) += f[a] * t;
                    for (int b = 0; b < 10; ++b) A.at<double>(a, b) += f[a] * f[b];
                }
            }
        for (int a = 0; a < 10; ++a) A.at<double>(a, a) += lambda;
        cv::Mat w;
        if (!cv::solve(A, bv, w, cv::DECOMP_CHOLESKY) && !cv::solve(A, bv, w, cv::DECOMP_SVD)) continue;
        cv::Mat score(n, n, CV_64F);
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x) {
                feat(y, x, f);
                double s = 0;
                for (int a = 0; a < 10; ++a) s += w.at<double>(a) * f[a];
                score.at<double>(y, x) = s; // > 0 dark
            }
        std::vector<int> cw(static_cast<size_t>(total), 0);
        std::vector<std::pair<double, int>> unsure;
        for (int k = 0; k < total; ++k) {
            if (k < size->data && isKnown[static_cast<size_t>(k)]) {
                cw[static_cast<size_t>(k)] = known[static_cast<size_t>(k)];
                continue;
            }
            int val = 0;
            double cmin = 1e9;
            for (int b = 0; b < 8; ++b) {
                const int wpos = pl.where(k, b);
                if (wpos < 0) return {};
                const double s = score.at<double>(wpos / in + 1, wpos % in + 1);
                val |= (s > 0 ? 1 : 0) << (7 - b);
                cmin = std::min(cmin, std::abs(s));
            }
            cw[static_cast<size_t>(k)] = val;
            unsure.emplace_back(cmin, k);
        }
        std::sort(unsure.begin(), unsure.end());
        for (int fcount = 0; fcount <= std::min<int>(size->ecc - kDmReserve, static_cast<int>(unsure.size())); ++fcount) {
            std::vector<int> er;
            for (int i = 0; i < fcount; ++i) er.push_back(unsure[static_cast<size_t>(i)].second);
            std::vector<int> fixed = cw;
            int e = 0;
            if (!rsDecodeDm(fixed, size->ecc, er, &e) || 2 * e + fcount > size->ecc - kDmReserve) continue;
            bool keep = true;
            for (int k = 0; k < size->data; ++k)
                if (isKnown[static_cast<size_t>(k)] && fixed[static_cast<size_t>(k)] != known[static_cast<size_t>(k)]) keep = false;
            if (!keep) continue;
            std::vector<uint8_t> dark;
            symbolFromCodewords(fixed, n, dark);
            cv::Mat d(n, n, CV_8U, dark.data());
            const std::string text = decodeDmBits(d.clone());
            if (!text.empty() && plausibleAmong(text, texts)) return text;
        }
    }
    return {};
}

// Soft decoding of a symbol seen only in part: a codeword with any module not seen (outside the
// image, off the label) is an erasure before any unsure one is added. The threshold comes from
// the seen data modules only. Line safety on top of softDecodeDm's four symbols in reserve:
//   - at least half the data area is seen, and the hidden codewords fit in the check symbols
//     less the reserve (so data + 4 codewords at least are read from real modules);
//   - the corrected symbol, redrawn, agrees with nearly every seen data module (a miscorrection
//     changes whole codewords, and their modules disagree with what the camera saw);
//   - with the product's codes already read (two or more), the text must look like them.
std::string maskedSoftDecodeDm(const cv::Mat& means, const cv::Mat& seen, int n, const std::vector<std::string>* known,
                               int* erasuresUsed)
{
    const DmSize* size = sizeOf(n);
    if (!size || means.rows != n || means.cols != n || seen.rows != n || seen.cols != n || seen.type() != CV_8U) return {};
    const int in = n - 2, total = size->data + size->ecc;
    std::vector<int> layout(static_cast<size_t>(total), 0);
    const Placement pl(layout, in, in);
    std::vector<float> vals;
    for (int y = 1; y <= in; ++y)
        for (int x = 1; x <= in; ++x)
            if (seen.at<uint8_t>(y, x)) vals.push_back(means.at<float>(y, x));
    if (vals.size() * 2 < static_cast<size_t>(in * in)) return {};
    // two-means threshold over the seen modules (Otsu on a 1-D set, as softDecodeDm)
    std::vector<float> sorted(vals);
    std::sort(sorted.begin(), sorted.end());
    double t = (sorted.front() + sorted.back()) / 2;
    for (int it = 0; it < 20; ++it) {
        double sD = 0, sL = 0;
        int nD = 0, nL = 0;
        for (float v : sorted) {
            if (v < t) { sD += v; ++nD; } else { sL += v; ++nL; }
        }
        if (nD == 0 || nL == 0) return {};
        const double nt = (sD / nD + sL / nL) / 2;
        if (std::abs(nt - t) < 1e-3) break;
        t = nt;
    }
    double mean = 0, var = 0;
    for (float v : vals) mean += v;
    mean /= static_cast<double>(vals.size());
    for (float v : vals) var += (v - mean) * (v - mean);
    const double spread = std::sqrt(var / static_cast<double>(vals.size())) + 1e-6;

    std::vector<int> cw(static_cast<size_t>(total), 0), hidden;
    std::vector<std::pair<double, int>> conf; // seen codewords, least sure first
    for (int k = 0; k < total; ++k) {
        int val = 0;
        double cmin = 1e9;
        bool all = true;
        for (int b = 0; b < 8; ++b) {
            const int w = pl.where(k, b);
            if (w < 0) return {};
            const int y = w / in + 1, x = w % in + 1;
            if (!seen.at<uint8_t>(y, x)) { all = false; continue; }
            const float m = means.at<float>(y, x);
            val |= (m < t ? 1 : 0) << (7 - b);
            cmin = std::min(cmin, std::abs(m - t) / spread);
        }
        cw[static_cast<size_t>(k)] = val;
        if (all) conf.emplace_back(cmin, k);
        else hidden.push_back(k);
    }
    const int budget = size->ecc - kDmReserve;
    if (static_cast<int>(hidden.size()) > budget) return {};
    std::sort(conf.begin(), conf.end());
    for (int f = static_cast<int>(hidden.size()); f <= budget; ++f) {
        std::vector<int> er = hidden;
        for (size_t i = 0; er.size() < static_cast<size_t>(f); ++i) er.push_back(conf[i].second);
        std::vector<int> fixed = cw;
        int e = 0;
        if (!rsDecodeDm(fixed, size->ecc, er, &e)) continue;
        if (2 * e + f > budget) continue;
        std::vector<uint8_t> dark;
        symbolFromCodewords(fixed, n, dark);
        // the redrawn symbol against the seen data modules
        int seenN = 0, disagree = 0;
        for (int y = 1; y <= in; ++y)
            for (int x = 1; x <= in; ++x) {
                if (!seen.at<uint8_t>(y, x)) continue;
                ++seenN;
                disagree += (means.at<float>(y, x) < t) != (dark[static_cast<size_t>(y * n + x)] != 0);
            }
        if (disagree * 100 > seenN * kDmMaxDisagreePct) continue;
        cv::Mat d(n, n, CV_8U, dark.data());
        const std::string text = decodeDmBits(d.clone()); // the data must also parse
        if (text.empty()) continue;
        if (known && known->size() >= 2 && !plausibleAmong(text, *known)) continue;
        if (erasuresUsed) *erasuresUsed = f;
        return text;
    }
    return {};
}

// Hard-decision decoding of an n x n module matrix (CV_8U, 1 = dark, canonical orientation)
// with two check symbols in reserve: zxing alone corrects up to the full Reed-Solomon capacity,
// and over the many windows, shifts and thresholds a lattice search tries, one false correction
// gets through (a crate cap read as "...Q49" next to its true "...184"). The codewords are read
// through the placement map, RS must succeed with 2e <= ecc - 2, and the corrected symbol parses.
std::string hardDecodeDm(const cv::Mat& dark, int n)
{
    const DmSize* size = sizeOf(n);
    if (!size || dark.rows != n || dark.cols != n) return {};
    const int total = size->data + size->ecc, in = n - 2;
    std::vector<int> layout(static_cast<size_t>(total), 0);
    const Placement pl(layout, in, in);
    std::vector<int> cw(static_cast<size_t>(total), 0);
    for (int k = 0; k < total; ++k)
        for (int b = 0; b < 8; ++b) {
            const int w = pl.where(k, b);
            if (w < 0) return {};
            cw[static_cast<size_t>(k)] |= (dark.at<uint8_t>(w / in + 1, w % in + 1) ? 1 : 0) << (7 - b);
        }
    int e = 0;
    if (!rsDecodeDm(cw, size->ecc, {}, &e) || 2 * e > size->ecc - kDmReserve) return {};
    std::vector<uint8_t> fixed;
    symbolFromCodewords(cw, n, fixed);
    cv::Mat d(n, n, CV_8U, fixed.data());
    return decodeDmBits(d.clone());
}

// A text read with known content must look like the product's other codes: the same length
// when they all share one, and a digit wherever every other code has a digit (a serial run).
// Rejects the few false corrections that parse, such as "...Q49" beside "...184".
bool plausibleAmong(const std::string& text, const std::vector<std::string>& texts)
{
    if (texts.empty()) return true;
    const size_t len = texts[0].size();
    bool sameLen = true;
    for (const auto& t : texts) sameLen = sameLen && t.size() == len;
    if (sameLen && text.size() != len) return false;
    for (size_t i = 0; i < text.size(); ++i) {
        bool allDigits = true;
        for (const auto& t : texts) allDigits = allDigits && i < t.size() && std::isdigit(static_cast<unsigned char>(t[i])) != 0;
        if (allDigits && !std::isdigit(static_cast<unsigned char>(text[i]))) return false;
    }
    return true;
}

QrEncoding learnDmEncoding(const Code& c)
{
    QrEncoding none;
    if (c.format != CR_DATAMATRIX || c.modW < 10 || c.modW != c.modH || c.modules.empty()) return none;
    for (const int kind : {0, 1}) {
        std::vector<uint8_t> dark;
        if (!encodeDm(c.text, c.modW, dark, kind == 1) || dark.size() != c.modules.size()) continue;
        size_t same = 0;
        for (size_t k = 0; k < dark.size(); ++k) same += dark[k] == c.modules[k];
        // the decoder's grid is the sampled one (a few misread modules); other encodations agree on
        // about two thirds of the modules
        if (same * 100 < dark.size() * 95) continue;
        QrEncoding e;
        e.format = CR_DATAMATRIX;
        e.kind = kind;
        e.modules = c.modW;
        return e;
    }
    return none;
}

namespace {

float corrMask(const cv::Mat& z, const uint8_t* pat, const cv::Mat& use)
{
    const int n = z.rows;
    double sz = 0, sp = 0, szz = 0, spp = 0, szp = 0;
    int cnt = 0;
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            if (!use.at<uint8_t>(y, x)) continue;
            const double a = z.at<float>(y, x), b = pat[y * n + x];
            sz += a; sp += b; szz += a * a; spp += b * b; szp += a * b;
            ++cnt;
        }
    if (cnt < 2) return 0.f;
    const double cov = szp - sz * sp / cnt, va = szz - sz * sz / cnt, vb = spp - sp * sp / cnt;
    return va <= 0 || vb <= 0 ? 0.f : static_cast<float>(cov / std::sqrt(va * vb));
}

// smallest box around points, by trying angles in 1 degree steps (OpenCV 5 moved minAreaRect out
// of imgproc; this works with both 4 and 5)
cv::RotatedRect smallestBox(const std::vector<cv::Point>& pts)
{
    cv::Point2f mid(0, 0);
    for (const auto& p : pts) mid += cv::Point2f(static_cast<float>(p.x), static_cast<float>(p.y));
    mid *= 1.f / static_cast<float>(pts.size());
    float bestArea = std::numeric_limits<float>::max();
    cv::RotatedRect best;
    for (int deg = 0; deg < 90; ++deg) {
        const float a = static_cast<float>(deg * CV_PI / 180.0), ca = std::cos(a), sa = std::sin(a);
        float u0 = 1e9f, u1 = -1e9f, v0 = 1e9f, v1 = -1e9f;
        for (const auto& p : pts) {
            const float x = p.x - mid.x, y = p.y - mid.y;
            const float u = x * ca + y * sa, v = -x * sa + y * ca;
            u0 = std::min(u0, u); u1 = std::max(u1, u);
            v0 = std::min(v0, v); v1 = std::max(v1, v);
        }
        const float area = (u1 - u0 + 1) * (v1 - v0 + 1);
        if (area < bestArea) {
            bestArea = area;
            const float cu = (u0 + u1) / 2, cv_ = (v0 + v1) / 2;
            best = cv::RotatedRect(mid + cv::Point2f(cu * ca - cv_ * sa, cu * sa + cv_ * ca), cv::Size2f(u1 - u0 + 1, v1 - v0 + 1),
                                   static_cast<float>(deg));
        }
    }
    return best;
}

} // namespace

// candidate code squares: dark modules on bright label paper merged into a block; label borders
// and text removed by an opening of a quarter code; a box made long by a border still merged on
// one side also gives the two squares at its ends
std::vector<cv::RotatedRect> dmCodeSquares(const cv::Mat& img8, float side, int n)
{
    const float m = side / n;
    // a block the size of a code is found as well at half resolution, and the morphology below
    // (elliptical kernels of a few modules) costs four times less there
    if (m >= 3.f && img8.cols >= 64 && img8.rows >= 64) {
        cv::Mat half;
        cv::resize(img8, half, cv::Size(), 0.5, 0.5, cv::INTER_AREA);
        auto out = dmCodeSquares(half, side / 2, n);
        for (auto& r : out) {
            r.center *= 2.f;
            r.size.width *= 2.f;
            r.size.height *= 2.f;
        }
        return out;
    }
    cv::Mat s, paper;
    img8.convertTo(s, CV_32F);
    cv::GaussianBlur(s, s, cv::Size(), 1.0);
    const int kp = static_cast<int>(3 * m) | 1;
    cv::dilate(s, paper, cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(kp, kp)));
    cv::GaussianBlur(paper, paper, cv::Size(), m);
    double maxV;
    cv::minMaxLoc(s, nullptr, &maxV);
    cv::Mat dark = (s < 0.62 * paper) & (paper > 0.5 * maxV);
    const int kc = static_cast<int>(1.6f * m) | 1;
    cv::Mat block, core;
    cv::morphologyEx(dark, block, cv::MORPH_CLOSE, cv::getStructuringElement(cv::MORPH_RECT, cv::Size(kc, kc)));
    const int ko = static_cast<int>(0.25f * side) | 1;
    const cv::Mat disk = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(ko, ko));
    cv::morphologyEx(block, core, cv::MORPH_OPEN, disk);
    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(core, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
    std::vector<cv::RotatedRect> out;
    for (const auto& c0 : contours) {
        // the square from the closed block near this core: the opening rounded its corners
        cv::Mat m0 = cv::Mat::zeros(core.size(), CV_8U), near;
        cv::drawContours(m0, std::vector<std::vector<cv::Point>>{c0}, -1, cv::Scalar(255), cv::FILLED);
        cv::dilate(m0, near, disk);
        near &= block;
        std::vector<cv::Point> pts;
        cv::findNonZero(near, pts);
        if (pts.size() < 10) continue;
        const cv::RotatedRect r = smallestBox(pts);
        const float lo = std::min(r.size.width, r.size.height), hi = std::max(r.size.width, r.size.height);
        if (lo < 0.6f * side || hi > 1.5f * side || hi > 1.4f * lo) continue;
        if (static_cast<double>(pts.size()) < 0.7 * r.size.area()) continue; // a solid block, not a frame
        out.push_back(r);
        if (hi > 1.06f * lo) {
            const float a = static_cast<float>(r.angle * CV_PI / 180.0);
            cv::Point2f u = r.size.width >= r.size.height ? cv::Point2f(std::cos(a), std::sin(a))
                                                          : cv::Point2f(-std::sin(a), std::cos(a));
            const float d = (hi - lo) / 2;
            for (int sg : {-1, 1})
                out.emplace_back(r.center + u * (sg * d), cv::Size2f(lo, lo), r.angle);
        }
    }
    return out;
}


bool matchDm(const cv::Mat& crop, float codeSide, const MatchCandidates& mc, int budgetMs, std::string& text,
             cv::Point2f quadOut[4], float* scoreOut)
{
    const int n = mc.encoding.modules;
    if (mc.texts.empty() || n < 10 || codeSide <= 0 || budgetMs <= 0) return false;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(budgetMs);

    // stretch, to about 9 px per module (the square search needs a few pixels per module)
    std::vector<uint8_t> v(crop.begin<uint8_t>(), crop.end<uint8_t>());
    std::nth_element(v.begin(), v.begin() + v.size() / 100, v.end());
    const double lo = v[v.size() / 100];
    std::nth_element(v.begin(), v.begin() + v.size() * 995 / 1000, v.end());
    const double hi = std::max(lo + 4, static_cast<double>(v[v.size() * 995 / 1000]));
    cv::Mat s8, u8, u32;
    crop.convertTo(s8, CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
    const double f = std::clamp(9.0 * n / codeSide, 1.0, 4.0);
    cv::resize(s8, u8, cv::Size(), f, f, cv::INTER_CUBIC);
    u8.convertTo(u32, CV_32F);
    const float side = static_cast<float>(codeSide * f), m = side / n;

    const auto& pats = mc.patterns(0);
    if (pats.empty()) return false;
    const uint8_t* ref = nullptr; // finder and clocks are the same in every candidate
    for (const auto& p : pats)
        if (p.size() == static_cast<size_t>(n) * n) { ref = p.data(); break; }
    if (!ref) return false;
    cv::Mat inner(n, n, CV_8U, cv::Scalar(0)), border(n, n, CV_8U, cv::Scalar(1));
    inner(cv::Rect(1, 1, n - 2, n - 2)).setTo(1);
    border(cv::Rect(1, 1, n - 2, n - 2)).setTo(0);

    std::vector<float> best(mc.texts.size(), -2.f);
    std::vector<cv::Point2f> bestQuad(mc.texts.size() * 4);
    bool any = false;
    for (const auto& sq : dmCodeSquares(u8, side, n)) {
        const float s0 = std::min(sq.size.width, sq.size.height);
        for (float dy : {-0.5f, 0.f, 0.5f})
            for (float dx : {-0.5f, 0.f, 0.5f})
                for (float ds : {0.96f, 1.f, 1.04f})
                    for (float da : {-3.f, 0.f, 3.f}) {
                        if (std::chrono::steady_clock::now() >= deadline) goto done;
                        const cv::RotatedRect r(sq.center + cv::Point2f(dx * m, dy * m), cv::Size2f(s0 * ds, s0 * ds), sq.angle + da);
                        cv::Point2f q[4];
                        r.points(q);
                        cv::Mat mm = moduleMeans(u32, q, n);
                        cv::Scalar mean, sd;
                        cv::meanStdDev(mm, mean, sd);
                        cv::Mat z = (mean[0] - mm) / (sd[0] + 1e-6); // dark -> positive
                        for (int k = 0; k < 4; ++k) {
                            cv::Mat zr = z.clone();
                            for (int j = 0; j < k; ++j) cv::rotate(zr, zr, cv::ROTATE_90_COUNTERCLOCKWISE);
                            if (corrMask(zr, ref, border) < 0.5f) continue; // L finder and clocks first
                            // turning the grid k times counter-clockwise puts corner k at the top left
                            cv::Point2f oq[4];
                            for (int c = 0; c < 4; ++c) oq[c] = q[(c + k) % 4];
                            for (size_t i = 0; i < pats.size(); ++i) {
                                if (pats[i].size() != static_cast<size_t>(n) * n) continue;
                                const float sc = corrMask(zr, pats[i].data(), inner);
                                if (sc > best[i]) {
                                    best[i] = sc;
                                    for (int c = 0; c < 4; ++c) bestQuad[i * 4 + static_cast<size_t>(c)] = oq[c];
                                    any = true;
                                }
                            }
                        }
                    }
    }
done:
    if (!any) return false;
    size_t top = 0;
    for (size_t i = 1; i < best.size(); ++i)
        if (best[i] > best[top]) top = i;
    float second = -2.f;
    for (size_t i = 0; i < best.size(); ++i)
        if (i != top) second = std::max(second, best[i]);
    if (scoreOut) *scoreOut = best[top];
    // same thresholds as QR: on the crate caps true codes scored 0.80-0.95, wrong ones at most 0.60
    if (best[top] < 0.75f || best[top] - second < 0.15f) return false;
    text = mc.texts[top];
    for (int c = 0; c < 4; ++c) quadOut[c] = bestQuad[top * 4 + static_cast<size_t>(c)] * static_cast<float>(1.0 / f);
    return true;
}

std::string decodeDmAt(const cv::Mat& crop, const cv::Point2f quad[4], int n)
{
    if (!sizeOf(n) || crop.empty()) return {};
    try {
        std::vector<uint8_t> v(crop.begin<uint8_t>(), crop.end<uint8_t>());
        std::nth_element(v.begin(), v.begin() + v.size() / 100, v.end());
        const double lo = v[v.size() / 100];
        std::nth_element(v.begin(), v.begin() + v.size() * 995 / 1000, v.end());
        const double hi = std::max(lo + 4, static_cast<double>(v[v.size() * 995 / 1000]));
        cv::Mat st, b, u, u32;
        crop.convertTo(st, CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
        cv::GaussianBlur(st, b, cv::Size(), 0.6);
        const double f = std::min(4.0, 1400.0 / std::max(crop.cols, crop.rows));
        cv::resize(b, u, cv::Size(), f, f, cv::INTER_CUBIC);
        u.convertTo(u32, CV_32F);
        // the match's square is jittered by half a module and 4 % in size: a half-module shift
        // along each grid axis, and the grid a little smaller or larger about its centre
        const cv::Point2f ax = (quad[1] - quad[0]) * (0.5f / n), ay = (quad[3] - quad[0]) * (0.5f / n);
        const cv::Point2f mid = (quad[0] + quad[1] + quad[2] + quad[3]) * 0.25f;
        const cv::Point2f shifts[5] = {{0, 0}, -ax, ax, -ay, ay};
        for (const float sc : {1.f, 0.97f, 1.03f})
            for (const auto& sh : shifts) {
                cv::Point2f q[4];
                for (int k = 0; k < 4; ++k) q[k] = (mid + (quad[k] - mid) * sc + sh) * static_cast<float>(f);
                const std::string t = softDecodeDm(moduleMeans(u32, q, n), n);
                if (!t.empty()) return t;
            }
    } catch (const cv::Exception&) {
    }
    return {};
}

} // namespace cr
