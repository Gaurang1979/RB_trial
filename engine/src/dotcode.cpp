// dotcode.cpp - AIM DotCode (ISS DotCode Rev 4.0): dots on every other point of a W x H grid
// (W + H odd), no finder pattern. zxing-cpp 3.1 has no DotCode reader, so it is read here.
//
// Reading: dark blobs of one size are grouped into clusters; a cluster's lattice (the dots sit at
// pitch x (+-1, +-1) from each other) gives the grid axes and pitch, refined by an affine fit grown
// out from the centre dot. Each grid point is sampled, and the symbol is tried in the rotations
// whose top-left point is a dot position. The dot stream (2 mask dots, then 9 dots per codeword
// from the 113 patterns of Annex C, the last 6 dots in the corners) is unfolded, Reed-Solomon over
// GF(113) corrects it, and the codewords are decoded after Annex F. Line safety: a block is
// corrected only while two or more check words stay unused (2e + f + spare <= checks), the data
// length is fixed by the grid size, a read whose rotations decode to two different texts is
// dropped, and Structured Append parts and reader-programming symbols are not reported.
//
// Layout, folding, masks and the codeword sets follow the specification as zint implements it
// (backend/dotcode.c, BSD-3-Clause); the tests decode the specification's own figures.
#include "reader.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <array>
#include <cmath>
#include <map>

namespace cr {

namespace {

constexpr int kGF = 113;

// Annex C: the 9-dot pattern of each codeword value
constexpr std::array<uint16_t, 113> kPatterns = {
    0x155, 0x0ab, 0x0ad, 0x0b5, 0x0d5, 0x156, 0x15a, 0x16a, 0x1aa, 0x0ae, 0x0b6, 0x0ba, 0x0d6, 0x0da, 0x0ea, 0x12b,
    0x12d, 0x135, 0x14b, 0x14d, 0x153, 0x159, 0x165, 0x169, 0x195, 0x1a5, 0x1a9, 0x057, 0x05b, 0x05d, 0x06b, 0x06d,
    0x075, 0x097, 0x09b, 0x09d, 0x0a7, 0x0b3, 0x0b9, 0x0cb, 0x0cd, 0x0d3, 0x0d9, 0x0e5, 0x0e9, 0x12e, 0x136, 0x13a,
    0x14e, 0x15c, 0x166, 0x16c, 0x172, 0x174, 0x196, 0x19a, 0x1a6, 0x1ac, 0x1b2, 0x1b4, 0x1ca, 0x1d2, 0x1d4, 0x05e,
    0x06e, 0x076, 0x07a, 0x09e, 0x0bc, 0x0ce, 0x0dc, 0x0e6, 0x0ec, 0x0f2, 0x0f4, 0x117, 0x11b, 0x11d, 0x127, 0x133,
    0x139, 0x147, 0x163, 0x171, 0x18b, 0x18d, 0x193, 0x199, 0x1a3, 0x1b1, 0x1c5, 0x1c9, 0x1d1, 0x02f, 0x037, 0x03b,
    0x03d, 0x04f, 0x067, 0x073, 0x079, 0x08f, 0x0c7, 0x0e3, 0x0f1, 0x11e, 0x13c, 0x178, 0x18e, 0x19c, 0x1b8, 0x1c6,
    0x1cc};

struct Field {
    std::array<int, kGF - 1> exp{}; // 3^i
    std::array<int, kGF> log{};
    std::array<int, 512> value{};   // dot pattern -> codeword, -1 = not a pattern
    Field()
    {
        int x = 1;
        for (int i = 0; i < kGF - 1; ++i) { exp[static_cast<size_t>(i)] = x; log[static_cast<size_t>(x)] = i; x = x * 3 % kGF; }
        value.fill(-1);
        for (int v = 0; v < kGF; ++v) value[kPatterns[static_cast<size_t>(v)]] = v;
    }
    int mul(int a, int b) const { return a * b % kGF; }
    int inv(int a) const { return exp[static_cast<size_t>((kGF - 1 - log[static_cast<size_t>(a)]) % (kGF - 1))]; }
    int pow3(int e) const { return exp[static_cast<size_t>(((e % (kGF - 1)) + kGF - 1) % (kGF - 1))]; }
};

const Field& gf()
{
    static const Field f;
    return f;
}

// p(x) at x, coefficients highest power first
int evalPoly(const std::vector<int>& p, int x)
{
    int y = 0;
    for (const int c : p) y = (gf().mul(y, x) + c) % kGF;
    return y;
}

// one Reed-Solomon block (data then checks, first word = highest power; roots 3^1 .. 3^nc) with
// the words at `erased` unreadable (not a dot pattern): corrects e errors and f erasures in place
// while 2e + f + spare <= nc; false otherwise
//
// spare check words: 2, and one more per 16 checks on large blocks. With one spare (2e + f + 1),
// a small symbol (3 checks, 2 erasures) is left one syndrome to verify, so about 1 random word in 113
// passes: random grids, a smeared symbol, or dot-matrix text seen at 45 degrees read as DotCode
int spareChecks(int nc) { return 2 + nc / 16; }

bool correctBlock(std::vector<int>& w, int nc, const std::vector<int>& erased, int* fixed)
{
    const Field& f = gf();
    const int n = static_cast<int>(w.size()), ne = static_cast<int>(erased.size());
    if (ne + spareChecks(nc) > nc) return false;
    std::vector<int> s(static_cast<size_t>(nc));
    bool clean = true;
    for (int j = 0; j < nc; ++j) {
        s[static_cast<size_t>(j)] = evalPoly(w, f.pow3(j + 1));
        clean = clean && s[static_cast<size_t>(j)] == 0;
    }
    if (clean) return true;
    // the erasure locator, then Berlekamp-Massey from it: the errata locator, lowest power first
    std::vector<int> lam = {1};
    for (const int idx : erased) {
        const int x = f.pow3(n - 1 - idx);
        std::vector<int> t(lam.size() + 1, 0);
        for (size_t i = 0; i < lam.size(); ++i) {
            t[i] = (t[i] + lam[i]) % kGF;
            t[i + 1] = (t[i + 1] + kGF - f.mul(lam[i], x)) % kGF;
        }
        lam = t;
    }
    std::vector<int> prev = lam;
    int L = ne, m = 1, bb = 1;
    for (int k = ne; k < nc; ++k) {
        int d = 0;
        for (size_t i = 0; i < lam.size() && static_cast<int>(i) <= k; ++i)
            d = (d + f.mul(lam[i], s[static_cast<size_t>(k) - i])) % kGF;
        if (d == 0) { ++m; continue; }
        const int coef = f.mul(d, f.inv(bb));
        std::vector<int> t = lam;
        if (lam.size() < prev.size() + static_cast<size_t>(m)) lam.resize(prev.size() + static_cast<size_t>(m), 0);
        for (size_t i = 0; i < prev.size(); ++i)
            lam[i + static_cast<size_t>(m)] = (lam[i + static_cast<size_t>(m)] + kGF - f.mul(coef, prev[i])) % kGF;
        if (2 * L <= k + ne) { L = k + 1 + ne - L; prev = t; bb = d; m = 1; }
        else ++m;
    }
    while (lam.size() > 1 && lam.back() == 0) lam.pop_back();
    if (L != static_cast<int>(lam.size()) - 1 || 2 * (L - ne) + ne + spareChecks(nc) > nc) return false;
    // error positions: lambda(3^-e) = 0, e = the power of the word (n - 1 - index)
    std::vector<int> pos;
    for (int e = 0; e < n; ++e) {
        const int xi = f.pow3(-e);
        int y = 0;
        for (size_t i = lam.size(); i-- > 0;) y = (f.mul(y, xi) + lam[i]) % kGF;
        if (y == 0) pos.push_back(e);
    }
    if (static_cast<int>(pos.size()) != L) return false;
    // Forney (first root 3^1): e = -omega(X^-1) / lambda'(X^-1), omega = S(x) lambda(x) mod x^nc
    std::vector<int> om(static_cast<size_t>(nc), 0);
    for (int i = 0; i < nc; ++i)
        for (size_t j = 0; j < lam.size() && static_cast<int>(j) <= i; ++j)
            om[static_cast<size_t>(i)] = (om[static_cast<size_t>(i)] + f.mul(s[static_cast<size_t>(i) - j], lam[j])) % kGF;
    for (const int e : pos) {
        const int xi = f.pow3(-e);
        int o = 0, dl = 0, xp = 1;
        for (int i = 0; i < nc; ++i) { o = (o + f.mul(om[static_cast<size_t>(i)], xp)) % kGF; xp = f.mul(xp, xi); }
        xp = 1;
        for (size_t i = 1; i < lam.size(); ++i) {
            dl = (dl + f.mul(f.mul(static_cast<int>(i % kGF), lam[i]), xp)) % kGF;
            xp = f.mul(xp, xi);
        }
        if (dl == 0) return false;
        const int mag = (kGF - f.mul(o, f.inv(dl))) % kGF;
        auto& v = w[static_cast<size_t>(n - 1 - e)];
        v = (v + kGF - mag) % kGF;
    }
    for (int j = 0; j < nc; ++j)
        if (evalPoly(w, f.pow3(j + 1)) != 0) return false;
    if (fixed) *fixed += L;
    return true;
}

// words: nd data words then nc checks, interleaved over ceil(total / 112) blocks as the encoder does
template <typename Fn>
void forBlocks(int nd, int nc, Fn fn)
{
    const int nw = nd + nc, step = (nw + kGF - 2) / (kGF - 1);
    for (int start = 0; start < step; ++start) {
        const int bd = (nd - start + step - 1) / step, bw = (nw - start + step - 1) / step;
        std::vector<int> idx;
        for (int i = 0; i < bd; ++i) idx.push_back(start + i * step);
        for (int i = 0; i < bw - bd; ++i) idx.push_back(start + bd * step + i * step);
        fn(idx, bw - bd);
    }
}

bool isCorner(int c, int r, int w, int h)
{
    if (c == 0 && r == 0) return true;
    if (h & 1) {
        if ((c == w - 2 && r == 0) || (c == w - 1 && r == 1)) return true;
        if (c == 0 && r == h - 1) return true;
    } else {
        if (c == w - 1 && r == 0) return true;
        if ((c == 0 && r == h - 2) || (c == 1 && r == h - 1)) return true;
    }
    return (c == w - 2 && r == h - 1) || (c == w - 1 && r == h - 2);
}

char charA(int v) { return static_cast<char>(v < 64 ? v + 32 : v - 64); }

} // namespace

int dotcodePattern(int value) { return value >= 0 && value < kGF ? kPatterns[static_cast<size_t>(value)] : -1; }

std::vector<int> dotcodeOrder(int w, int h)
{
    std::vector<int> o;
    if (h & 1) {
        // horizontal folding: rows from the bottom, the corners last
        for (int r = 0; r < h; ++r)
            for (int c = 0; c < w; ++c)
                if (!((c + r) & 1) && !isCorner(c, r, w, h)) o.push_back((h - r - 1) * w + c);
        for (const int i : {w - 2, h * w - 2, 2 * w - 1, (h - 1) * w - 1, 0, (h - 1) * w}) o.push_back(i);
    } else {
        // vertical folding: columns from the left
        for (int c = 0; c < w; ++c)
            for (int r = 0; r < h; ++r)
                if (!((c + r) & 1) && !isCorner(c, r, w, h)) o.push_back(r * w + c);
        for (const int i : {(h - 1) * w - 1, (h - 2) * w, h * w - 2, (h - 1) * w + 1, w - 1, 0}) o.push_back(i);
    }
    return o;
}

int dotcodeDataLength(int w, int h)
{
    // the encoder pads until no further codeword fits: the data length follows from the size alone
    const int dots = w * h / 2;
    int d = 0;
    while (9 * ((d + 1) + 3 + (d + 1) / 2) + 2 <= dots) ++d;
    return d;
}

bool decodeDotCodeMessage(const std::vector<int>& cw, std::string* text, bool* gs1Out)
{
    const int n = static_cast<int>(cw.size());
    if (n == 0) return false;
    std::string out, suffix;
    enum { A, B, C, X } set = C;
    const bool gs1 = cw[0] <= 100; // data that starts with digits and no FNC1 is GS1 (FNC1 first marks other data)
    int i = 0;
    if (cw[0] == 107) i = 1;
    else if (cw[0] == 109) return false; // FNC3: reader programming, not a part's code
    std::vector<int> group;
    int eciSkip = 0; // binary-mode ECI values still to skip
    const auto next = [&](int& v) { if (i >= n) return false; v = cw[static_cast<size_t>(i++)]; return true; };
    const auto pairs = [&](int k) {
        for (int j = 0; j < k; ++j) {
            int v;
            if (!next(v) || v > 99) return false;
            out += static_cast<char>('0' + v / 10);
            out += static_cast<char>('0' + v % 10);
        }
        return true;
    };
    const auto charB = [&](int v) {
        if (v < 96) out += static_cast<char>(v + 32);
        else if (v == 96) out += "\r\n";
        else if (v <= 100) out += "\t\x1c\x1d\x1e"[v - 97];
        else return false;
        return true;
    };
    const auto flush = [&]() {
        if (group.empty()) return true;
        if (group.size() == 1) return false;
        uint64_t val = 0;
        for (const int g : group) val = val * 103 + static_cast<uint64_t>(g);
        const size_t k = group.size() - 1;
        std::vector<int> bytes(k);
        for (size_t j = k; j-- > 0;) { bytes[j] = static_cast<int>(val % 259); val /= 259; }
        group.clear();
        if (val != 0) return false;
        for (const int b : bytes) {
            if (eciSkip > 0) { --eciSkip; continue; }
            if (b >= 256) { eciSkip = b - 255; continue; } // ECI in binary: 1-3 values follow
            out += static_cast<char>(b);
        }
        return true;
    };
    // FNC2, FNC3, the upper shifts and the binary latch mean the same in sets A, B and C
    const auto common = [&](int c) {
        int v;
        switch (c) {
        case 107: out += '\x1d'; return true; // FNC1 inside the data: the GS1 separator
        case 108:
            if (i >= n) return false; // FNC2 last: a Structured Append part, not the whole message
            if (!next(v)) return false;
            if (v >= 40) { int b2, c2; if (!next(b2) || !next(c2)) return false; } // ECI noted, text kept as bytes
            return true;
        case 109: return false; // FNC3 inside the data
        case 110: if (!next(v) || v > 95) return false; out += static_cast<char>(charA(v) + 128); return true;
        case 111: if (!next(v) || v > 95) return false; out += static_cast<char>(v + 32 + 128); return true;
        case 112: set = X; return true;
        default: return false;
        }
    };
    while (i < n) {
        const int c = cw[static_cast<size_t>(i++)];
        if (c < 0 || c >= kGF) return false;
        if (set == C) {
            if (c < 100) { out += static_cast<char>('0' + c / 10); out += static_cast<char>('0' + c % 10); }
            else if (c == 100) { out += "17"; if (!pairs(3)) return false; out += "10"; }
            else if (c == 101) set = A;
            else if (c <= 105) {
                for (int k = 0; k < c - 101; ++k) { int v; if (!next(v) || !charB(v)) return false; }
            } else if (c == 106) {
                set = B;
                // a macro right after the first latch: the message header and trailer (Table 2)
                if (i == 1 && i < n && cw[1] >= 97 && cw[1] <= 100) {
                    const int mac = cw[static_cast<size_t>(i++)];
                    out += "[)>\x1e";
                    if (mac < 100) { out += mac == 97 ? "05" : mac == 98 ? "06" : "12"; out += '\x1d'; suffix = "\x1e\x04"; }
                    else suffix = "\x04";
                }
            } else if (!common(c)) return false;
        } else if (set == B) {
            if (c <= 100) { if (!charB(c)) return false; }
            else if (c == 101) { int v; if (!next(v) || v > 95) return false; out += charA(v); }
            else if (c == 102) set = A;
            else if (c <= 105) { if (!pairs(c - 101)) return false; }
            else if (c == 106) set = C;
            else if (!common(c)) return false;
        } else if (set == A) {
            if (c < 96) out += charA(c);
            else if (c <= 101) {
                for (int k = 0; k < c - 95; ++k) { int v; if (!next(v) || !charB(v)) return false; }
            } else if (c == 102) set = B;
            else if (c <= 105) { if (!pairs(c - 101)) return false; }
            else if (c == 106) set = C;
            else if (!common(c)) return false;
        } else { // binary: 6 codewords (base 103) carry 5 values (base 259)
            if (c < 103) {
                group.push_back(c);
                if (group.size() == 6 && !flush()) return false;
                continue;
            }
            if (!flush()) return false;
            if (c <= 108) { if (!pairs(c - 101)) return false; } // stays binary
            else if (c == 109) set = A;
            else if (c == 110) set = B;
            else if (c == 111) set = C;
            else return false;
        }
    }
    if (set == X && !flush()) return false;
    if (eciSkip > 0) return false;
    *text = out + suffix;
    if (gs1Out) *gs1Out = gs1;
    return true;
}

bool decodeDotCodeGrid(const std::vector<uint8_t>& dots, int w, int h, std::string* text, int* corrected, bool* gs1, float* uec)
{
    if (w < 5 || h < 5 || w > 200 || h > 200 || !((w + h) & 1) || dots.size() != static_cast<size_t>(w * h)) return false;
    const int d = dotcodeDataLength(w, h);
    if (d < 1) return false;
    const int nc = 3 + d / 2, nd = d + 1;
    const std::vector<int> order = dotcodeOrder(w, h);
    const auto bit = [&](size_t k) { return dots[static_cast<size_t>(order[k])] ? 1 : 0; };
    std::vector<int> words(static_cast<size_t>(nd + nc));
    std::vector<bool> bad(words.size(), false);
    words[0] = bit(0) << 1 | bit(1);
    for (int k = 1; k < nd + nc; ++k) {
        int pat = 0;
        for (int b = 0; b < 9; ++b) pat = pat << 1 | bit(static_cast<size_t>(2 + 9 * (k - 1) + b));
        const int v = gf().value[static_cast<size_t>(pat)];
        words[static_cast<size_t>(k)] = v < 0 ? 0 : v;
        bad[static_cast<size_t>(k)] = v < 0; // not a pattern: an erasure
    }
    bool ok = true;
    int fixed = 0;
    float worst = 1.f;
    forBlocks(nd, nc, [&](const std::vector<int>& idx, int bnc) {
        if (!ok) return;
        std::vector<int> b;
        for (const int k : idx) b.push_back(words[static_cast<size_t>(k)]);
        std::vector<int> er;
        for (size_t j = 0; j < idx.size(); ++j)
            if (bad[static_cast<size_t>(idx[j])]) er.push_back(static_cast<int>(j));
        const int before = fixed;
        if (!correctBlock(b, bnc, er, &fixed)) { ok = false; return; }
        // errata L = errors + erasures; an erasure that happened to hold the right word still used one check
        const int ne = static_cast<int>(er.size()), errors = std::max(0, fixed - before - ne);
        const int room = bnc - spareChecks(bnc);
        worst = std::min(worst, room > 0 ? std::max(0.f, 1.f - static_cast<float>(2 * errors + ne) / static_cast<float>(room)) : 0.f);
        for (size_t j = 0; j < idx.size(); ++j) words[static_cast<size_t>(idx[j])] = b[j];
    });
    if (!ok || words[0] > 3) return false;
    static constexpr int kMaskStep[4] = {0, 3, 7, 17};
    std::vector<int> data(static_cast<size_t>(d));
    for (int j = 0; j < d; ++j)
        data[static_cast<size_t>(j)] = ((words[static_cast<size_t>(j + 1)] - j * kMaskStep[words[0]]) % kGF + kGF) % kGF;
    if (!decodeDotCodeMessage(data, text, gs1)) return false;
    if (corrected) *corrected = fixed;
    if (uec) *uec = worst;
    return true;
}

std::vector<uint8_t> dotcodeSymbol(std::vector<int> data, int w, int h, int mask)
{
    const int d = dotcodeDataLength(w, h);
    if (static_cast<int>(data.size()) > d || mask < 0 || mask > 3 || !((w + h) & 1)) return {};
    while (static_cast<int>(data.size()) < d) data.push_back(106);
    static constexpr int kMaskStep[4] = {0, 3, 7, 17};
    const int nc = 3 + d / 2, nd = d + 1;
    std::vector<int> words(static_cast<size_t>(nd + nc), 0);
    words[0] = mask;
    for (int j = 0; j < d; ++j) words[static_cast<size_t>(j + 1)] = (data[static_cast<size_t>(j)] + j * kMaskStep[mask]) % kGF;
    const Field& f = gf();
    forBlocks(nd, nc, [&](const std::vector<int>& idx, int bnc) {
        // generator (x - 3)(x - 9)...: remainder of data x^nc, negated
        std::vector<int> g = {1};
        for (int j = 1; j <= bnc; ++j) {
            std::vector<int> ng(g.size() + 1, 0);
            for (size_t k = 0; k < g.size(); ++k) {
                ng[k] = (ng[k] + g[k]) % kGF;
                ng[k + 1] = (ng[k + 1] + kGF - f.mul(g[k], f.pow3(j))) % kGF;
            }
            g = ng;
        }
        const size_t bd = idx.size() - static_cast<size_t>(bnc);
        std::vector<int> rem(static_cast<size_t>(bnc), 0);
        for (size_t k = 0; k < bd; ++k) {
            const int fb = (words[static_cast<size_t>(idx[k])] + rem[0]) % kGF;
            for (int j = 0; j + 1 < bnc; ++j) rem[static_cast<size_t>(j)] = (rem[static_cast<size_t>(j + 1)] + kGF - f.mul(fb, g[static_cast<size_t>(j + 1)])) % kGF;
            rem[static_cast<size_t>(bnc - 1)] = (kGF - f.mul(fb, g[static_cast<size_t>(bnc)])) % kGF;
        }
        for (int j = 0; j < bnc; ++j) words[static_cast<size_t>(idx[bd + static_cast<size_t>(j)])] = (kGF - rem[static_cast<size_t>(j)]) % kGF;
    });
    std::vector<int> stream = {words[0] >> 1 & 1, words[0] & 1};
    for (size_t k = 1; k < words.size(); ++k)
        for (int b = 8; b >= 0; --b) stream.push_back(kPatterns[static_cast<size_t>(words[k])] >> b & 1);
    const std::vector<int> order = dotcodeOrder(w, h);
    stream.resize(order.size(), 1);
    std::vector<uint8_t> dots(static_cast<size_t>(w * h), 0);
    for (size_t k = 0; k < order.size(); ++k) dots[static_cast<size_t>(order[k])] = static_cast<uint8_t>(stream[k]);
    return dots;
}

namespace {

struct Blob { cv::Point2f c; float d; };

// the symbol's dot grid in one cluster: grid axes, pitch and an affine fit, then the grid sampled
// and decoded in every rotation whose top-left point is a dot position
bool readCluster(const cv::Mat& gray, const std::vector<Blob>& blobs, std::string* text, bool* gs1, cv::Point2f quad[4], LinearRead* grid)
{
    const int nb = static_cast<int>(blobs.size());
    // nearest neighbours: the dots' diagonal neighbours lie pitch x sqrt 2 away
    std::vector<cv::Point2f> vecs;
    std::vector<float> nn;
    for (int i = 0; i < nb; ++i) {
        float best = 1e30f;
        for (int j = 0; j < nb; ++j)
            if (j != i) best = std::min(best, static_cast<float>(cv::norm(blobs[static_cast<size_t>(j)].c - blobs[static_cast<size_t>(i)].c)));
        nn.push_back(best);
    }
    // the diagonal neighbour is the closest two dots can be: a low percentile, as in a sparse symbol
    // many dots' nearest is two points away along a row (2 pitches, not 1.41)
    std::vector<float> s = nn;
    std::nth_element(s.begin(), s.begin() + nb / 5, s.end());
    const float nnMed = s[static_cast<size_t>(nb / 5)];
    double c4 = 0, s4 = 0;
    std::vector<float> lens;
    for (int i = 0; i < nb; ++i)
        for (int j = i + 1; j < nb; ++j) {
            const cv::Point2f v = blobs[static_cast<size_t>(j)].c - blobs[static_cast<size_t>(i)].c;
            const float l = static_cast<float>(cv::norm(v));
            if (l < 0.8f * nnMed || l > 1.2f * nnMed) continue;
            const double a = std::atan2(v.y, v.x);
            c4 += std::cos(4 * a); s4 += std::sin(4 * a);
            lens.push_back(l);
        }
    if (lens.size() < 10) return false;
    std::nth_element(lens.begin(), lens.begin() + static_cast<long>(lens.size() / 2), lens.end());
    const double pitch = lens[lens.size() / 2] / std::sqrt(2.0);
    const double theta = std::atan2(s4, c4) / 4 + CV_PI / 4; // grid axis: 45 degrees off the neighbours
    // affine (c, r) -> (x, y), grown out from the dot nearest the cluster's centre
    cv::Point2f mean(0, 0);
    for (const auto& b : blobs) mean += b.c;
    mean *= 1.f / static_cast<float>(nb);
    int o = 0;
    for (int i = 1; i < nb; ++i)
        if (cv::norm(blobs[static_cast<size_t>(i)].c - mean) < cv::norm(blobs[static_cast<size_t>(o)].c - mean)) o = i;
    cv::Matx23d A(pitch * std::cos(theta), -pitch * std::sin(theta), blobs[static_cast<size_t>(o)].c.x,
                  pitch * std::sin(theta), pitch * std::cos(theta), blobs[static_cast<size_t>(o)].c.y);
    std::vector<cv::Point> cr(static_cast<size_t>(nb));
    std::vector<bool> in(static_cast<size_t>(nb));
    for (const double radius : {5.0, 10.0, 20.0, 40.0, 1e9}) {
        const cv::Matx22d M(A(0, 0), A(0, 1), A(1, 0), A(1, 1));
        // a refit on scattered blobs can collapse the lattice (no area per dot): no symbol here
        if (!(std::abs(cv::determinant(M)) > 1e-6 * pitch * pitch)) return false;
        const cv::Matx22d Mi = M.inv();
        cv::Matx<double, 6, 6> AtA = cv::Matx<double, 6, 6>::zeros();
        cv::Matx<double, 6, 1> Atb = cv::Matx<double, 6, 1>::zeros();
        int used = 0;
        for (int i = 0; i < nb; ++i) {
            const cv::Vec2d g = Mi * cv::Vec2d(blobs[static_cast<size_t>(i)].c.x - A(0, 2), blobs[static_cast<size_t>(i)].c.y - A(1, 2));
            const double ci = std::round(g[0]), ri = std::round(g[1]);
            // far off the lattice (a skewed fit): not a dot of it, and no int holds its place
            const bool onGrid = std::abs(ci) < 1e6 && std::abs(ri) < 1e6;
            in[static_cast<size_t>(i)] = onGrid && std::hypot(g[0] - ci, g[1] - ri) < 0.3 && std::hypot(ci, ri) <= radius &&
                                         ((static_cast<long>(ci) + static_cast<long>(ri)) & 1) == 0;
            cr[static_cast<size_t>(i)] = onGrid ? cv::Point(static_cast<int>(ci), static_cast<int>(ri)) : cv::Point(0, 0);
            if (!in[static_cast<size_t>(i)]) continue;
            const double row[2][6] = {{ci, ri, 1, 0, 0, 0}, {0, 0, 0, ci, ri, 1}};
            const double rhs[2] = {blobs[static_cast<size_t>(i)].c.x, blobs[static_cast<size_t>(i)].c.y};
            for (int k = 0; k < 2; ++k)
                for (int a = 0; a < 6; ++a) {
                    Atb(a, 0) += row[k][a] * rhs[k];
                    for (int b = 0; b < 6; ++b) AtA(a, b) += row[k][a] * row[k][b];
                }
            ++used;
        }
        if (used < 12) { // too few dots near the centre yet (an unprinted stretch): widen first
            if (radius > 1e8) return false;
            continue;
        }
        cv::Matx<double, 6, 1> x;
        if (!cv::solve(AtA, Atb, x, cv::DECOMP_SVD)) return false;
        A = cv::Matx23d(x(0), x(1), x(2), x(3), x(4), x(5));
    }
    int c0 = 1 << 20, c1 = -(1 << 20), r0 = 1 << 20, r1 = -(1 << 20), inliers = 0;
    for (int i = 0; i < nb; ++i)
        if (in[static_cast<size_t>(i)]) {
            ++inliers;
            c0 = std::min(c0, cr[static_cast<size_t>(i)].x); c1 = std::max(c1, cr[static_cast<size_t>(i)].x);
            r0 = std::min(r0, cr[static_cast<size_t>(i)].y); r1 = std::max(r1, cr[static_cast<size_t>(i)].y);
        }
    const int gw = c1 - c0 + 1, gh = r1 - r0 + 1;
    if (gw < 5 || gh < 5 || gw > 201 || gh > 201 || inliers * 5 < gw * gh / 2) return false;
    const auto at = [&](double c, double r) {
        return cv::Point2f(static_cast<float>(A(0, 0) * c + A(0, 1) * r + A(0, 2)), static_cast<float>(A(1, 0) * c + A(1, 1) * r + A(1, 2)));
    };
    const float rad = static_cast<float>(0.2 * pitch);
    const auto sample = [&](int c, int r) {
        const cv::Point2f p = at(c, r);
        float sum = 0;
        int k = 0;
        for (const float dy : {-rad, 0.f, rad})
            for (const float dx : {-rad, 0.f, rad}) {
                const int x = static_cast<int>(std::lround(p.x + dx)), y = static_cast<int>(std::lround(p.y + dy));
                if (x < 0 || y < 0 || x >= gray.cols || y >= gray.rows) return -1.f;
                sum += gray.at<uint8_t>(y, x);
                ++k;
            }
        return sum / static_cast<float>(k);
    };
    // the extents to try: as found, or one row / column more on a side when W + H comes out even
    // (an outer row or column with no dot printed)
    std::vector<cv::Vec4i> ext;
    if ((gw + gh) & 1) ext.push_back({c0, r0, gw, gh});
    else {
        ext.push_back({c0 - 1, r0, gw + 1, gh});
        ext.push_back({c0, r0, gw + 1, gh});
        ext.push_back({c0, r0 - 1, gw, gh + 1});
        ext.push_back({c0, r0, gw, gh + 1});
    }
    std::string found;
    cv::Point2f foundQuad[4];
    LinearRead foundGrid;
    int reads = 0;
    for (const auto& e : ext) {
        const int ec0 = e[0], er0 = e[1], ew = e[2], eh = e[3];
        // grey levels: dot positions, and the points between them (never printed) for the paper
        std::vector<float> dotv, gap;
        std::vector<float> grid(static_cast<size_t>(ew * eh), -1.f);
        bool off = false;
        for (int r = 0; r < eh && !off; ++r)
            for (int c = 0; c < ew; ++c) {
                const float v = sample(ec0 + c, er0 + r);
                if (v < 0) { off = true; break; }
                grid[static_cast<size_t>(r * ew + c)] = v;
                ((ec0 + c + er0 + r) & 1 ? gap : dotv).push_back(v);
            }
        if (off || dotv.empty() || gap.empty()) continue;
        std::sort(dotv.begin(), dotv.end());
        std::nth_element(gap.begin(), gap.begin() + static_cast<long>(gap.size() / 2), gap.end());
        const float paper = gap[gap.size() / 2], ink = dotv[dotv.size() / 5];
        if (paper - ink < 15.f) continue;
        const float thr = (paper + ink) / 2;
        // rotations: k quarter turns clockwise of the image grid
        for (int k = 0; k < 4; ++k) {
            const int sw = k % 2 ? eh : ew, sh = k % 2 ? ew : eh;
            const auto src = [&](int x, int y) { // symbol (x, y) -> image grid (c, r)
                switch (k) {
                case 0: return cv::Point(x, y);
                case 1: return cv::Point(y, eh - 1 - x);
                case 2: return cv::Point(ew - 1 - x, eh - 1 - y);
                default: return cv::Point(ew - 1 - y, x);
                }
            };
            const cv::Point tl = src(0, 0);
            if ((ec0 + tl.x + er0 + tl.y) & 1) continue; // the top-left point must be a dot position
            std::vector<uint8_t> dots(static_cast<size_t>(sw * sh), 0);
            for (int y = 0; y < sh; ++y)
                for (int x = 0; x < sw; ++x) {
                    const cv::Point g = src(x, y);
                    dots[static_cast<size_t>(y * sw + x)] = grid[static_cast<size_t>(g.y * ew + g.x)] < thr ? 1 : 0;
                }
            std::string t;
            bool g = false;
            float u = -1.f;
            if (!decodeDotCodeGrid(dots, sw, sh, &t, nullptr, &g, &u)) continue;
            if (reads > 0 && t != found) return false; // two readings of one symbol: neither is reported
            // a W x H symbol and its quarter turn (H x W, the other folding) carry the same dot stream:
            // the first reading, nearest upright in the image, gives the corners
            if (reads++ > 0) continue;
            found = t;
            *gs1 = g;
            foundGrid.modules = dots;
            foundGrid.modW = sw;
            foundGrid.modH = sh;
            foundGrid.uec = u;
            // corners in the symbol's own order (top left first), so corner 0 -> 1 is its reading direction
            const double cx[4] = {-0.5, sw - 0.5, sw - 0.5, -0.5}, cy[4] = {-0.5, -0.5, sh - 0.5, sh - 0.5};
            for (int q = 0; q < 4; ++q) {
                double gx = cx[q], gy = cy[q];
                if (k == 1) { gx = cy[q]; gy = eh - 1 - cx[q]; }
                else if (k == 2) { gx = ew - 1 - cx[q]; gy = eh - 1 - cy[q]; }
                else if (k == 3) { gx = ew - 1 - cy[q]; gy = cx[q]; }
                foundQuad[q] = at(ec0 + gx, er0 + gy);
            }
        }
    }
    if (reads == 0) return false;
    *text = found;
    for (int q = 0; q < 4; ++q) quad[q] = foundQuad[q];
    grid->modules = std::move(foundGrid.modules);
    grid->modW = foundGrid.modW;
    grid->modH = foundGrid.modH;
    grid->uec = foundGrid.uec;
    return true;
}

} // namespace

std::vector<LinearRead> readDotCode(const cv::Mat& gray, int maxCodes, std::chrono::steady_clock::time_point deadline,
                                    const std::function<bool(int, std::string)>& accept)
{
    std::vector<LinearRead> out;
    cv::Mat smooth;
    cv::GaussianBlur(gray, smooth, cv::Size(3, 3), 0.8);
    // global threshold; the same thinned (dots of heavy print touching their diagonal neighbours);
    // local threshold (uneven light)
    for (int pass = 0; pass < 3 && static_cast<int>(out.size()) < maxCodes; ++pass) {
        cv::Mat bin;
        if (pass < 2) cv::threshold(smooth, bin, 0, 255, cv::THRESH_BINARY_INV | cv::THRESH_OTSU);
        else cv::adaptiveThreshold(smooth, bin, 255, cv::ADAPTIVE_THRESH_MEAN_C, cv::THRESH_BINARY_INV, 51, 12);
        if (pass == 1) cv::erode(bin, bin, cv::getStructuringElement(cv::MORPH_CROSS, cv::Size(3, 3)));
        cv::Mat lab, stats, cents;
        const int n = cv::connectedComponentsWithStats(bin, lab, stats, cents, 8, CV_32S);
        // roundish blobs (a dot, or a speck); bars, text strokes and large shapes are not dots
        std::vector<Blob> blobs;
        for (int i = 1; i < n; ++i) {
            const int a = stats.at<int>(i, cv::CC_STAT_AREA), bw = stats.at<int>(i, cv::CC_STAT_WIDTH), bh = stats.at<int>(i, cv::CC_STAT_HEIGHT);
            if (a < 3 || a > 4000 || bw > 2.2 * bh + 2 || bh > 2.2 * bw + 2 || a < 0.35 * bw * bh) continue;
            blobs.push_back({cv::Point2f(static_cast<float>(cents.at<double>(i, 0)), static_cast<float>(cents.at<double>(i, 1))),
                             static_cast<float>(std::sqrt(4.0 * a / CV_PI))});
        }
        if (blobs.size() < 12 || blobs.size() > 20000) continue;
        // clusters: dots of a size within reach of each other (5 dot widths: a stretch of unprinted
        // points leaves the next dot a few pitches away; blobs off the lattice drop out in the fit)
        const float cell = 64.f;
        std::map<std::pair<int, int>, std::vector<int>> buckets;
        for (size_t i = 0; i < blobs.size(); ++i)
            buckets[{static_cast<int>(blobs[i].c.x / cell), static_cast<int>(blobs[i].c.y / cell)}].push_back(static_cast<int>(i));
        std::vector<int> parent(blobs.size());
        for (size_t i = 0; i < parent.size(); ++i) parent[i] = static_cast<int>(i);
        const auto root = [&](int x) { while (parent[static_cast<size_t>(x)] != x) x = parent[static_cast<size_t>(x)] = parent[static_cast<size_t>(parent[static_cast<size_t>(x)])]; return x; };
        for (size_t i = 0; i < blobs.size(); ++i) {
            const int bx = static_cast<int>(blobs[i].c.x / cell), by = static_cast<int>(blobs[i].c.y / cell);
            for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx) {
                    const auto it = buckets.find({bx + dx, by + dy});
                    if (it == buckets.end()) continue;
                    for (const int j : it->second) {
                        if (j <= static_cast<int>(i)) continue;
                        const Blob& a = blobs[i];
                        const Blob& b = blobs[static_cast<size_t>(j)];
                        const float dmax = std::max(a.d, b.d), dmin = std::min(a.d, b.d);
                        if (dmax > 2.2f * dmin + 1.f) continue;
                        if (cv::norm(a.c - b.c) <= std::min(5.f * dmax, cell)) parent[static_cast<size_t>(root(static_cast<int>(i)))] = root(j);
                    }
                }
        }
        std::map<int, std::vector<Blob>> clusters;
        for (size_t i = 0; i < blobs.size(); ++i) clusters[root(static_cast<int>(i))].push_back(blobs[i]);
        std::vector<std::vector<Blob>> order;
        for (auto& kv : clusters)
            if (kv.second.size() >= 12 && kv.second.size() <= 25000) order.push_back(std::move(kv.second));
        std::sort(order.begin(), order.end(), [](const auto& a, const auto& b) { return a.size() > b.size(); });
        for (const auto& cl : order) {
            if (static_cast<int>(out.size()) >= maxCodes || std::chrono::steady_clock::now() >= deadline) break;
            if (cl.size() > 3000) continue; // too many for the pairwise neighbour search: not a DotCode of 200 x 200
            LinearRead r;
            bool gs1 = false;
            if (!readCluster(gray, cl, &r.text, &gs1, r.quad, &r)) continue;
            r.format = CR_DOTCODE;
            r.gs1 = gs1 ? 1 : 0;
            r.symbologyId = gs1 ? "]J1" : "]J0"; // AIM: DotCode, GS1 or other data
            r.step = CR_STEP_DOTCODE;
            if (accept && !accept(r.format, r.text)) continue;
            bool dup = false;
            for (const auto& o : out) dup = dup || o.text == r.text;
            if (!dup) out.push_back(r);
        }
    }
    return out;
}

} // namespace cr
