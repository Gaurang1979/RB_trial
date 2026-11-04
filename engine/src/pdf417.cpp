// pdf417.cpp - PDF417 with damaged rows: codewords fused across scan lines, Reed-Solomon decides.
//
// zxing reads a PDF417 scan line by scan line and votes; a scratch along the bars (vertical or
// steep) breaks the same codeword in every row, which is more than the check codewords can carry
// (synthetic labels, 3 px modules, EC level 2: two vertical scratches 6 of 20 read). Here:
//  1. the band of bars (findLinearBands) turned upright, both ways round; on every pixel row the
//     start and stop patterns by their run lengths; lines through them are the left and right
//     edges, and the width in modules gives the column count
//  2. every scan line sampled module by module between the edges; its row indicators give its row
//     (row / 3 from the value, row mod 3 from the cluster) and the symbol's rows, columns and level
//  3. per row, the median darkness of each module over the row's scan lines (a scratch over some
//     lines or a spot drops out), matched against the 929 patterns of the row's cluster
//  4. module columns that disagree with the best patterns in most rows (a scratch along the bars)
//     are masked and the codewords matched again on the other modules; a codeword whose best
//     pattern is not clearly better than the next is an erasure, never a guess
//  5. Reed-Solomon over GF(929) with the erasures, two check codewords kept in reserve; the
//     corrected codewords are drawn as a clean symbol for zxing, which decodes the text and checks
//     the Reed-Solomon again
#include "reader.hpp"

#include <ZXing/ReadBarcode.h>

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <map>

namespace cr {

namespace {

// stage times of the fused pass (CR_PROF=1), to stderr
struct Prof {
    const bool on = std::getenv("CR_PROF") != nullptr;
    std::chrono::steady_clock::time_point last = std::chrono::steady_clock::now();
    void mark(const char* what)
    {
        if (!on) return;
        const auto now = std::chrono::steady_clock::now();
        std::fprintf(stderr, "  pdf417 %-14s %6.1f ms\n", what, std::chrono::duration<double, std::milli>(now - last).count());
        last = now;
    }
};

constexpr int kVals = 929, kCw = 17;
const uint16_t kPatterns[3 * kVals] = {
#include "pdf417_patterns.inc"
};
constexpr uint8_t kStart[8] = {8, 1, 1, 1, 1, 1, 1, 3};    // bar first, 17 modules
constexpr uint8_t kStop[9] = {7, 1, 1, 3, 1, 1, 1, 2, 1}; // bar first, 18 modules

// the 17 modules (1 = bar) of every pattern, [cluster][value][module]
const std::vector<float>& patternModules()
{
    static const std::vector<float> m = [] {
        std::vector<float> v(static_cast<size_t>(3 * kVals * kCw));
        for (int i = 0; i < 3 * kVals; ++i)
            for (int k = 0; k < 16; ++k) v[static_cast<size_t>(i * kCw + k)] = (kPatterns[i] >> (15 - k)) & 1 ? 1.f : 0.f;
        return v;
    }();
    return m;
}

void appendRuns(std::vector<uint8_t>& row, const uint8_t* runs, int n)
{
    for (int i = 0; i < n; ++i) row.insert(row.end(), runs[i], i % 2 == 0 ? 1 : 0);
}

void appendCodeword(std::vector<uint8_t>& row, int cluster, int value)
{
    const uint16_t p = kPatterns[cluster * kVals + value];
    for (int k = 0; k < 16; ++k) row.push_back((p >> (15 - k)) & 1);
    row.push_back(0);
}

// ---- GF(929), generator 3
int gfMul(int a, int b) { return static_cast<int>(static_cast<long>(a) * b % kVals); }
int gfPow(int a, long e)
{
    int r = 1;
    e %= kVals - 1;
    if (e < 0) e += kVals - 1;
    for (; e; e >>= 1, a = gfMul(a, a))
        if (e & 1) r = gfMul(r, a);
    return r;
}
int gfInv(int a) { return gfPow(a, kVals - 2); }
int polyEval(const std::vector<int>& p, int x) // low degree first
{
    int r = 0;
    for (size_t i = p.size(); i-- > 0;) r = (gfMul(r, x) + p[i]) % kVals;
    return r;
}
std::vector<int> polyMul(const std::vector<int>& a, const std::vector<int>& b)
{
    std::vector<int> r(a.size() + b.size() - 1, 0);
    for (size_t i = 0; i < a.size(); ++i)
        for (size_t j = 0; j < b.size(); ++j) r[i + j] = (r[i + j] + gfMul(a[i], b[j])) % kVals;
    return r;
}

// syndromes S_1..S_k: the codewords as a polynomial, cw[0] the highest power, at 3^1..3^k
std::vector<int> syndromes(const std::vector<int>& cw, int k)
{
    std::vector<int> s(static_cast<size_t>(k));
    for (int i = 1; i <= k; ++i) {
        const int x = gfPow(3, i);
        int r = 0;
        for (const int c : cw) r = (gfMul(r, x) + c) % kVals;
        s[static_cast<size_t>(i - 1)] = r;
    }
    return s;
}

// ---- the symbol in an upright image (bars vertical, start pattern on the left)

struct Edges {
    float al = 0, bl = 0, ar = 0, br = 0; // left edge x = al + bl * y, right edge x = ar + br * y
    float top = 0, bottom = 0, module = 0;
    int cols = 0; // data columns
};

// x = a + b * y through the points: Theil-Sen (wrong points do not pull it), then least squares
// on the points within tol px; false with fewer than minPts of those
bool fitEdge(std::vector<cv::Point2f> pts, float& a, float& b, float tol = 1.5f, size_t minPts = 6)
{
    if (pts.size() < minPts) return false;
    std::sort(pts.begin(), pts.end(), [](const cv::Point2f& p, const cv::Point2f& q) { return p.y < q.y; });
    const size_t h = pts.size() / 2;
    std::vector<float> v;
    for (size_t i = 0; i + h < pts.size(); ++i)
        if (pts[i + h].y - pts[i].y > 1e-3f) v.push_back((pts[i + h].x - pts[i].x) / (pts[i + h].y - pts[i].y));
    if (v.empty()) return false;
    std::nth_element(v.begin(), v.begin() + static_cast<long>(v.size() / 2), v.end());
    b = v[v.size() / 2];
    v.clear();
    for (const auto& p : pts) v.push_back(p.x - b * p.y);
    std::nth_element(v.begin(), v.begin() + static_cast<long>(v.size() / 2), v.end());
    a = v[v.size() / 2];
    double sy = 0, sx = 0, syy = 0, sxy = 0, n = 0;
    for (const auto& p : pts) {
        if (std::abs(p.x - (a + b * p.y)) > tol) continue;
        sy += p.y; sx += p.x; syy += p.y * p.y; sxy += p.x * p.y; n += 1;
    }
    if (n < static_cast<double>(minPts)) return false;
    const double var = syy - sy * sy / n;
    if (var > 1e-6) {
        b = static_cast<float>((sxy - sx * sy / n) / var);
        a = static_cast<float>((sx - b * sy) / n);
    }
    return true;
}

// start and stop patterns on every row (run lengths at the row's mid level), edges fitted. Every
// 4th row first: a band without two of each there (a carton's print, text, a 1D code; most bands
// on a frame without a PDF417) is left after a quarter of the work. That first pass also counts
// the patterns read the other way (the symbol upside down): otherWay = false when the band turned
// half a turn has too few of them to try.
bool findEdges(const cv::Mat& u, Edges& e, bool* otherWay = nullptr)
{
    std::vector<cv::Point2f> starts, stops;
    std::vector<float> modStart;
    std::vector<float> s(static_cast<size_t>(u.cols)), xs;
    std::vector<bool> toBar;
    int revStarts = 0, revStops = 0;
    // the runs from edge i (sub-pixel), in modules of the pattern's own width, against `want`
    auto runsMatch = [&](size_t i, const uint8_t* want, int nr, int mods, float& m) {
        if (i + static_cast<size_t>(nr) >= xs.size()) return false;
        m = (xs[i + static_cast<size_t>(nr)] - xs[i]) / static_cast<float>(mods);
        if (m < 1.2f) return false;
        for (int k = 0; k < nr; ++k)
            if (std::abs((xs[i + static_cast<size_t>(k) + 1] - xs[i + static_cast<size_t>(k)]) / m - want[k]) >= (want[k] > 3 ? 1.2f : 0.7f)) return false;
        return true;
    };
    static const std::array<uint8_t, 8> revStart = {3, 1, 1, 1, 1, 1, 1, 8}; // space first
    static const std::array<uint8_t, 9> revStop = {1, 2, 1, 1, 1, 3, 1, 1, 7}; // bar first
    auto scanRow = [&](int y, bool countOther) {
        const float* r = u.ptr<float>(y);
        s.assign(r, r + u.cols);
        std::nth_element(s.begin(), s.begin() + static_cast<long>(s.size() / 10), s.end());
        const float lo = s[s.size() / 10];
        std::nth_element(s.begin(), s.begin() + static_cast<long>(s.size() * 9 / 10), s.end());
        const float hi = s[s.size() * 9 / 10];
        if (hi - lo < 20.f) return;
        const float t = (lo + hi) / 2;
        // edges at sub-pixel positions; edge i starts a bar when toBar[i]
        xs.clear();
        toBar.clear();
        for (int x = 1; x < u.cols; ++x) {
            const bool a = r[x - 1] < t, b = r[x] < t;
            if (a == b) continue;
            xs.push_back(static_cast<float>(x - 1) + (r[x - 1] - t) / (r[x - 1] - r[x]));
            toBar.push_back(b);
        }
        float m = 0.f;
        for (size_t i = 0; i + 8 < xs.size(); ++i) {
            if (!toBar[i]) {
                if (countOther && runsMatch(i, revStart.data(), 8, 17, m)) ++revStarts;
                continue;
            }
            if (runsMatch(i, kStart, 8, 17, m)) {
                starts.emplace_back(xs[i], static_cast<float>(y));
                modStart.push_back(m);
            }
            if (runsMatch(i, kStop, 9, 18, m)) stops.emplace_back(xs[i + 9], static_cast<float>(y));
            if (countOther && runsMatch(i, revStop.data(), 9, 18, m)) ++revStops;
        }
    };
    constexpr int kSparse = 4;
    for (int y = 0; y < u.rows; y += kSparse) scanRow(y, otherWay != nullptr);
    if (otherWay) *otherWay = revStarts >= 2 && revStops >= 2;
    if (starts.size() < 2 || stops.size() < 2) return false;
    for (int y = 0; y < u.rows; ++y)
        if (y % kSparse) scanRow(y, false);
    if (!fitEdge(starts, e.al, e.bl) || !fitEdge(stops, e.ar, e.br)) return false;
    std::nth_element(modStart.begin(), modStart.begin() + static_cast<long>(modStart.size() / 2), modStart.end());
    e.module = modStart[modStart.size() / 2];
    float top = 1e9f, bottom = -1e9f;
    for (const auto* set : {&starts, &stops})
        for (const auto& p : *set) {
            const bool left = set == &starts;
            if (std::abs(p.x - (left ? e.al + e.bl * p.y : e.ar + e.br * p.y)) > 1.5f) continue;
            top = std::min(top, p.y);
            bottom = std::max(bottom, p.y);
        }
    e.top = top;
    e.bottom = bottom;
    const float mid = (top + bottom) / 2, width = (e.ar + e.br * mid) - (e.al + e.bl * mid);
    e.cols = static_cast<int>(std::lround((width / e.module - 69.f) / 17.f));
    return e.cols >= 1 && e.cols <= 30 && bottom - top > 2 * e.module;
}

// darkness (0 light .. 1 dark) of every module of scan line y between the edges
std::vector<float> scanModules(const cv::Mat& u, const Edges& e, int y)
{
    const int total = 17 * e.cols + 69;
    const float xl = e.al + e.bl * static_cast<float>(y), xr = e.ar + e.br * static_cast<float>(y), m = (xr - xl) / total;
    const float* r = u.ptr<float>(y);
    auto at = [&](float x) {
        x = std::clamp(x, 0.f, static_cast<float>(u.cols - 1) - 0.001f);
        const int i = static_cast<int>(x);
        const float f = x - static_cast<float>(i);
        return r[i] * (1 - f) + r[std::min(i + 1, u.cols - 1)] * f;
    };
    std::vector<float> d(static_cast<size_t>(total));
    for (int k = 0; k < total; ++k) {
        const float c = xl + (static_cast<float>(k) + 0.5f) * m;
        d[static_cast<size_t>(k)] = (at(c - 0.25f * m) + at(c) + at(c + 0.25f * m)) / 3.f;
    }
    std::vector<float> s = d;
    std::nth_element(s.begin(), s.begin() + static_cast<long>(s.size() / 10), s.end());
    const float lo = s[s.size() / 10];
    std::nth_element(s.begin(), s.begin() + static_cast<long>(s.size() * 9 / 10), s.end());
    const float hi = s[s.size() * 9 / 10];
    for (auto& x : d) x = std::clamp((hi - x) / std::max(hi - lo, 1.f), 0.f, 1.f);
    return d;
}

struct Match {
    int value = -1, cluster = -1;
    float cost = 1e9f, margin = 0.f; // mean squared difference per module used; next best minus best
};

// best pattern for 17 module darknesses (weights: 0 = masked), over one cluster or all three
Match matchCodeword(const float* d, const float* w, int cluster)
{
    const auto& pm = patternModules();
    Match best;
    float second = 1e9f;
    float wsum = 0.f;
    for (int k = 0; k < kCw; ++k) wsum += w[k];
    if (wsum < 8.f) return best;
    for (int c = cluster < 0 ? 0 : cluster; c <= (cluster < 0 ? 2 : cluster); ++c)
        for (int v = 0; v < kVals; ++v) {
            const float* p = &pm[static_cast<size_t>((c * kVals + v) * kCw)];
            float s = 0.f;
            for (int k = 0; k < kCw; ++k) s += w[k] * (d[k] - p[k]) * (d[k] - p[k]);
            if (s < best.cost) {
                second = best.cost;
                best.cost = s;
                best.value = v;
                best.cluster = c;
            } else if (s < second) second = s;
        }
    best.cost /= wsum;
    best.margin = (second - best.cost * wsum) / wsum;
    return best;
}

// Regions of bars at any angle, as findLinearBands (linear.cpp) finds them, but the coherent area
// closed over about 12 px: a PDF417's rows and a scratch across it break the coherence, and
// findLinearBands would return the pieces. Largest first. The gradients at half size, the tensor
// smoothed and the regions found at a quarter (the same smoothing, a quarter of the work).
std::vector<LinearBand> findStackedBands(const cv::Mat& gray)
{
    std::vector<LinearBand> out;
    cv::Mat s, g, gx, gy;
    cv::resize(gray, s, cv::Size(), 0.5, 0.5, cv::INTER_AREA);
    s.convertTo(g, CV_32F);
    cv::Sobel(g, gx, CV_32F, 1, 0, 3);
    cv::Sobel(g, gy, CV_32F, 0, 1, 3);
    cv::Mat jxx, jyy, jxy;
    const cv::Size quarter((g.cols + 1) / 2, (g.rows + 1) / 2);
    cv::resize(gx.mul(gx), jxx, quarter, 0, 0, cv::INTER_AREA);
    cv::resize(gy.mul(gy), jyy, quarter, 0, 0, cv::INTER_AREA);
    cv::resize(gx.mul(gy), jxy, quarter, 0, 0, cv::INTER_AREA);
    cv::GaussianBlur(jxx, jxx, cv::Size(), 1.5);
    cv::GaussianBlur(jyy, jyy, cv::Size(), 1.5);
    cv::GaussianBlur(jxy, jxy, cv::Size(), 1.5);
    cv::Mat tr = jxx + jyy, diff = jxx - jyy, det;
    cv::sqrt(diff.mul(diff) + 4 * jxy.mul(jxy), det);
    cv::Mat coh = det / (tr + 1e-6), energy;
    cv::sqrt(tr, energy);
    std::vector<float> ev(energy.begin<float>(), energy.end<float>());
    const size_t k99 = ev.size() * 99 / 100;
    std::nth_element(ev.begin(), ev.begin() + static_cast<long>(k99), ev.end());
    const float thr = std::max(ev[k99] * 0.25f, 1e-3f);
    cv::Mat mask = (coh > 0.5) & (energy > thr);
    cv::morphologyEx(mask, mask, cv::MORPH_CLOSE, cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(7, 7)));
    cv::Mat lab, stats, cents;
    const int n = cv::connectedComponentsWithStats(mask, lab, stats, cents);
    std::vector<int> order;
    for (int i = 1; i < n; ++i)
        if (stats.at<int>(i, cv::CC_STAT_AREA) >= 75) order.push_back(i);
    std::sort(order.begin(), order.end(), [&](int a, int b) { return stats.at<int>(a, cv::CC_STAT_AREA) > stats.at<int>(b, cv::CC_STAT_AREA); });
    if (order.size() > 8) order.resize(8);
    for (const int id : order) {
        double sxx = 0, syy = 0, sxy = 0;
        std::vector<cv::Point2f> pts;
        const cv::Rect bb(stats.at<int>(id, cv::CC_STAT_LEFT), stats.at<int>(id, cv::CC_STAT_TOP), stats.at<int>(id, cv::CC_STAT_WIDTH), stats.at<int>(id, cv::CC_STAT_HEIGHT));
        for (int y = bb.y; y < bb.y + bb.height; ++y)
            for (int x = bb.x; x < bb.x + bb.width; ++x)
                if (lab.at<int>(y, x) == id) {
                    sxx += jxx.at<float>(y, x); syy += jyy.at<float>(y, x); sxy += jxy.at<float>(y, x);
                    pts.emplace_back(static_cast<float>(x * 4) + 1.5f, static_cast<float>(y * 4) + 1.5f);
                }
        const double theta = 0.5 * std::atan2(2 * sxy, sxx - syy);
        const cv::Point2f d(static_cast<float>(std::cos(theta)), static_cast<float>(std::sin(theta))), e(-d.y, d.x);
        float u0 = 1e9f, u1 = -1e9f, v0 = 1e9f, v1 = -1e9f;
        for (const auto& p : pts) {
            u0 = std::min(u0, p.dot(d)); u1 = std::max(u1, p.dot(d));
            v0 = std::min(v0, p.dot(e)); v1 = std::max(v1, p.dot(e));
        }
        LinearBand b;
        b.centre = d * ((u0 + u1) / 2) + e * ((v0 + v1) / 2);
        b.dir = d;
        b.length = u1 - u0;
        b.height = v1 - v0;
        if (b.length >= 60.f && b.height >= 12.f) out.push_back(b);
    }
    return out;
}

constexpr float kRiCost = 0.06f, kRiMargin = 0.03f;  // a row indicator taken as read
constexpr float kCwCost = 0.10f, kCwMargin = 0.025f; // a data codeword taken as read, else erased

// nCheck: the symbol's check codewords once its row indicators are read (else left as it was)
std::string readUpright(const cv::Mat& u, Edges& e, std::array<cv::Point2f, 4>& corners, Prof& prof, int* nCheckOut = nullptr)
{
    const int c = e.cols, total = 17 * c + 69;
    const std::array<float, kCw> ones = [] { std::array<float, kCw> a{}; a.fill(1.f); return a; }();
    const int y0 = std::max(0, static_cast<int>(e.top)), y1 = std::min(u.rows - 1, static_cast<int>(e.bottom));
    // every scan line: modules and the row its indicators give
    std::vector<std::vector<float>> lines;
    std::vector<int> lineY, lineRow;
    std::map<int, int> votesA, votesB, votesC; // (rows-1)/3, level*3+(rows-1)%3, columns-1
    std::vector<cv::Point2f> rowFit;           // (y, row) of the scan lines whose indicators agree
    for (int y = y0; y <= y1; ++y) {
        auto d = scanModules(u, e, y);
        const Match l = matchCodeword(&d[17], ones.data(), -1), r = matchCodeword(&d[static_cast<size_t>(17 + 17 * (c + 1))], ones.data(), -1);
        const bool lok = l.cost < kRiCost && l.margin > kRiMargin, rok = r.cost < kRiCost && r.margin > kRiMargin;
        int row = -1;
        if (lok) {
            (l.cluster == 0 ? votesA : l.cluster == 1 ? votesB : votesC)[l.value % 30]++;
            row = 3 * (l.value / 30) + l.cluster;
        }
        if (rok) {
            (r.cluster == 0 ? votesC : r.cluster == 1 ? votesA : votesB)[r.value % 30]++;
            const int rr = 3 * (r.value / 30) + r.cluster;
            row = lok && rr != row ? -2 : rr;
        }
        if (row >= 0) rowFit.emplace_back(static_cast<float>(y), static_cast<float>(row));
        lines.push_back(std::move(d));
        lineY.push_back(y);
        lineRow.push_back(row);
    }
    auto top = [](const std::map<int, int>& m) {
        int best = -1, n = 0;
        for (const auto& kv : m)
            if (kv.second > n) { n = kv.second; best = kv.first; }
        return best;
    };
    prof.mark("scan lines");
    const int A = top(votesA), B = top(votesB), C = top(votesC);
    if (A < 0 || B < 0 || C < 0 || C + 1 != c) return {};
    const int rows = 3 * A + B % 3 + 1, level = B / 3, nCheck = 2 << level, n = rows * c;
    if (rows < 3 || rows > 90 || n > 928 || nCheck >= n) return {};
    if (nCheckOut) *nCheckOut = nCheck;
    // y of row centres: y = a + b * (row + 0.5), from the scan lines whose indicators agree
    // one point per row, the median y of its scan lines (fitting every line would keep only the
    // lines near a first guess and agree with it)
    std::map<int, std::vector<float>> rowYs;
    for (const auto& q : rowFit) rowYs[static_cast<int>(q.y)].push_back(q.x);
    std::vector<cv::Point2f> centres;
    for (auto& kv : rowYs) {
        if (kv.second.size() < 3) continue;
        auto& v = kv.second;
        std::nth_element(v.begin(), v.begin() + static_cast<long>(v.size() / 2), v.end());
        centres.emplace_back(v[v.size() / 2], static_cast<float>(kv.first) + 0.5f);
    }
    float fa = 0, fb = 0;
    if (!fitEdge(centres, fa, fb, 3.f, 3) || fb <= 0.5f) return {};
    // fusion: the median of each module over the central scan lines of each row
    std::vector<std::vector<float>> rowMods(static_cast<size_t>(rows));
    for (int r = 0; r < rows; ++r) {
        std::vector<const std::vector<float>*> mine;
        for (size_t i = 0; i < lines.size(); ++i) {
            const float t = (static_cast<float>(lineY[i]) - fa) / fb; // row + 0.5 at the row centre
            if (static_cast<int>(std::floor(t)) != r) continue;
            const float frac = t - std::floor(t);
            if (frac < 0.2f || frac > 0.8f || (lineRow[i] >= 0 && lineRow[i] != r)) continue;
            mine.push_back(&lines[i]);
        }
        if (mine.empty()) continue;
        auto& out = rowMods[static_cast<size_t>(r)];
        out.resize(static_cast<size_t>(total));
        std::vector<float> col(mine.size());
        for (int k = 0; k < total; ++k) {
            for (size_t i = 0; i < mine.size(); ++i) col[i] = (*mine[i])[static_cast<size_t>(k)];
            std::nth_element(col.begin(), col.begin() + static_cast<long>(col.size() / 2), col.end());
            out[static_cast<size_t>(k)] = col[col.size() / 2];
        }
    }
    prof.mark("fuse rows");
    // votes of the single scan lines of a row, for a codeword the median leaves unclear (a scratch
    // over the middle of a row, clean lines at its edges): the value most lines read clearly
    const std::array<float, kCw> all = ones;
    auto lineVote = [&](int r, int k) {
        std::map<int, int> votes;
        const int m0 = 17 * (k + 2);
        for (size_t i = 0; i < lines.size(); ++i) {
            const float t = (static_cast<float>(lineY[i]) - fa) / fb;
            if (static_cast<int>(std::floor(t)) != r || (lineRow[i] >= 0 && lineRow[i] != r)) continue;
            const Match m = matchCodeword(&lines[i][static_cast<size_t>(m0)], all.data(), r % 3);
            if (m.value >= 0 && m.cost < kRiCost && m.margin > kRiMargin) votes[m.value]++;
        }
        int best = -1, n1 = 0, n2 = 0;
        for (const auto& kv : votes)
            if (kv.second > n1) { n2 = n1; n1 = kv.second; best = kv.first; }
            else n2 = std::max(n2, kv.second);
        return n1 >= 2 && n1 >= 2 * n2 ? best : -1;
    };
    // pass 1 on every module; then the module columns most rows disagree with are masked
    std::vector<float> weight(static_cast<size_t>(total), 1.f);
    std::vector<int> cw(static_cast<size_t>(n), 0);
    std::vector<int> erasures;
    for (int pass = 0; pass < 2; ++pass) {
        erasures.clear();
        std::vector<std::vector<float>> resid(static_cast<size_t>(total));
        for (int r = 0; r < rows; ++r)
            for (int k = 0; k < c; ++k) {
                const size_t at = static_cast<size_t>(r * c + k);
                const auto& rm = rowMods[static_cast<size_t>(r)];
                if (rm.empty()) { erasures.push_back(static_cast<int>(at)); continue; }
                const int m0 = 17 * (k + 2);
                const Match m = matchCodeword(&rm[static_cast<size_t>(m0)], &weight[static_cast<size_t>(m0)], r % 3);
                bool ok = m.value >= 0 && m.cost < kCwCost && m.margin > kCwMargin;
                cw[at] = ok ? m.value : 0;
                if (!ok) {
                    const int v = lineVote(r, k);
                    if (v >= 0) { cw[at] = v; ok = true; }
                }
                if (!ok) erasures.push_back(static_cast<int>(at));
                if (m.value >= 0) {
                    const float* p = &patternModules()[static_cast<size_t>(((r % 3) * kVals + m.value) * kCw)];
                    for (int j = 0; j < kCw; ++j) resid[static_cast<size_t>(m0 + j)].push_back(std::abs(rm[static_cast<size_t>(m0 + j)] - p[j]));
                }
            }
        if (pass == 1 || static_cast<int>(erasures.size()) <= nCheck / 2) break;
        bool masked = false;
        std::vector<float> med(static_cast<size_t>(total), 0.f);
        for (int k = 34; k < 34 + 17 * c; ++k) {
            auto& v = resid[static_cast<size_t>(k)];
            if (v.size() < 3) continue;
            // the upper quartile: a dark scratch shows only where the module is a space (about
            // half the rows), a light one only where it is a bar
            std::nth_element(v.begin(), v.begin() + static_cast<long>(v.size() * 3 / 4), v.end());
            med[static_cast<size_t>(k)] = v[v.size() * 3 / 4];
        }
        // a scratch along the bars: a module column far off the best patterns in many rows, or
        // the same in every row (data columns vary; a codeword's first module is always a bar and
        // its last a space); the columns beside it when partly off
        std::vector<float> lo(static_cast<size_t>(total), 0.f), hi(static_cast<size_t>(total), 1.f);
        int filled = 0;
        for (const auto& rm : rowMods) filled += rm.empty() ? 0 : 1;
        if (filled >= 5)
            for (int k = 34; k < 34 + 17 * c; ++k) {
                std::vector<float> v;
                for (const auto& rm : rowMods)
                    if (!rm.empty()) v.push_back(rm[static_cast<size_t>(k)]);
                std::sort(v.begin(), v.end());
                lo[static_cast<size_t>(k)] = v[v.size() / 10];
                hi[static_cast<size_t>(k)] = v[v.size() - 1 - v.size() / 10];
            }
        for (int k = 34; k < 34 + 17 * c; ++k) {
            const int pos = (k - 34) % 17;
            const bool same = pos != 0 && pos != 16 && (lo[static_cast<size_t>(k)] > 0.6f || hi[static_cast<size_t>(k)] < 0.4f);
            if (med[static_cast<size_t>(k)] <= 0.4f && !same) continue;
            for (int j = k - 1; j <= k + 1; ++j)
                if (j == k || med[static_cast<size_t>(j)] > 0.15f || lo[static_cast<size_t>(j)] > 0.4f || hi[static_cast<size_t>(j)] < 0.6f) {
                    weight[static_cast<size_t>(j)] = 0.f;
                    masked = true;
                }
        }
        if (!masked) break;
    }
    prof.mark("match");
    if (static_cast<int>(erasures.size()) > nCheck - 2) return {};
    if (!rs929Correct(cw, erasures, nCheck, 2)) return {};
    if (cw[0] != n - nCheck) return {}; // the symbol length descriptor
    // the clean symbol for zxing: 2 px modules, rows 3 modules tall, quiet zones
    const int X = 2, rowH = 3 * X, q = 4 * X;
    cv::Mat img(rows * rowH + 2 * q, total * X + 2 * q, CV_8U, cv::Scalar(255));
    for (int r = 0; r < rows; ++r) {
        const int cl = r % 3, base = 30 * (r / 3);
        const int left = base + (cl == 0 ? (rows - 1) / 3 : cl == 1 ? level * 3 + (rows - 1) % 3 : c - 1);
        const int right = base + (cl == 0 ? c - 1 : cl == 1 ? (rows - 1) / 3 : level * 3 + (rows - 1) % 3);
        std::vector<uint8_t> mods;
        appendRuns(mods, kStart, 8);
        appendCodeword(mods, cl, left);
        for (int k = 0; k < c; ++k) appendCodeword(mods, cl, cw[static_cast<size_t>(r * c + k)]);
        appendCodeword(mods, cl, right);
        appendRuns(mods, kStop, 9);
        for (size_t k = 0; k < mods.size(); ++k)
            if (mods[k]) img(cv::Rect(q + static_cast<int>(k) * X, q + r * rowH, X, rowH)).setTo(0);
    }
    ZXing::ReaderOptions o;
    o.setFormats(ZXing::BarcodeFormat::PDF417);
    o.setTextMode(ZXing::TextMode::Plain);
    o.setBinarizer(ZXing::Binarizer::BoolCast);
    const auto b = ZXing::ReadBarcode(ZXing::ImageView(img.data, img.cols, img.rows, ZXing::ImageFormat::Lum, static_cast<int>(img.step)), o);
    prof.mark("rs + zxing");
    if (!b.isValid()) return {};
    const float yt = fa, yb = fa + fb * static_cast<float>(rows);
    corners = {cv::Point2f(e.al + e.bl * yt, yt), cv::Point2f(e.ar + e.br * yt, yt), cv::Point2f(e.ar + e.br * yb, yb), cv::Point2f(e.al + e.bl * yb, yb)};
    return b.text();
}

} // namespace

int pdf417Pattern(int cluster, int value)
{
    return cluster < 0 || cluster > 2 || value < 0 || value >= kVals ? -1 : kPatterns[cluster * kVals + value] << 1;
}

bool rs929Correct(std::vector<int>& cw, const std::vector<int>& erasures, int nCheck, int reserve, int* errorsOut)
{
    const int n = static_cast<int>(cw.size()), f = static_cast<int>(erasures.size());
    if (nCheck <= 0 || nCheck >= n || f > nCheck - reserve) return false;
    for (int& v : cw)
        if (v < 0 || v >= kVals) v = 0;
    auto locator = [&](int j) { return gfPow(3, n - 1 - j); }; // X_j of codeword j
    const std::vector<int> S = syndromes(cw, nCheck);
    if (std::all_of(S.begin(), S.end(), [](int s) { return s == 0; })) {
        if (errorsOut) *errorsOut = 0;
        return true;
    }
    // erasure locator, and the Forney syndromes (the errors alone) for Berlekamp-Massey
    std::vector<int> gamma = {1};
    for (const int j : erasures) gamma = polyMul(gamma, {1, (kVals - locator(j)) % kVals});
    const std::vector<int> sx(S.begin(), S.end()); // S(x) = sum S_{i+1} x^i
    std::vector<int> xi = polyMul(gamma, sx);
    xi.resize(static_cast<size_t>(nCheck));
    std::vector<int> T(xi.begin() + f, xi.end());
    std::vector<int> C = {1}, Bp = {1};
    int L = 0, m = 1, b = 1;
    for (int i = 0; i < static_cast<int>(T.size()); ++i) {
        int d = T[static_cast<size_t>(i)];
        for (int k = 1; k <= L && k < static_cast<int>(C.size()); ++k) d = (d + gfMul(C[static_cast<size_t>(k)], T[static_cast<size_t>(i - k)])) % kVals;
        if (d == 0) { ++m; continue; }
        const int coef = gfMul(d, gfInv(b));
        std::vector<int> next = C;
        if (next.size() < Bp.size() + static_cast<size_t>(m)) next.resize(Bp.size() + static_cast<size_t>(m), 0);
        for (size_t k = 0; k < Bp.size(); ++k)
            next[k + static_cast<size_t>(m)] = (next[k + static_cast<size_t>(m)] + kVals - gfMul(coef, Bp[k])) % kVals;
        if (2 * L <= i) {
            Bp = C;
            L = i + 1 - L;
            b = d;
            m = 1;
        } else ++m;
        C = next;
    }
    while (C.size() > 1 && C.back() == 0) C.pop_back();
    if (static_cast<int>(C.size()) - 1 != L) return false;
    const int errors = L;
    if (2 * errors + f > nCheck - reserve) return false;
    std::vector<int> lambda = polyMul(C, gamma);
    while (lambda.size() > 1 && lambda.back() == 0) lambda.pop_back();
    const int deg = static_cast<int>(lambda.size()) - 1;
    std::vector<int> omega = polyMul(lambda, sx);
    omega.resize(static_cast<size_t>(nCheck));
    std::vector<int> deriv(std::max<size_t>(1, lambda.size() - 1), 0);
    for (size_t i = 1; i < lambda.size(); ++i) deriv[i - 1] = gfMul(static_cast<int>(i % kVals), lambda[i]);
    int roots = 0;
    std::vector<int> fixedCw = cw;
    for (int j = 0; j < n; ++j) {
        const int xinv = gfInv(locator(j));
        if (polyEval(lambda, xinv) != 0) continue;
        ++roots;
        const int den = polyEval(deriv, xinv);
        if (den == 0) return false;
        const int mag = (kVals - gfMul(polyEval(omega, xinv), gfInv(den))) % kVals; // -Omega / Lambda'
        fixedCw[static_cast<size_t>(j)] = (fixedCw[static_cast<size_t>(j)] + kVals - mag) % kVals;
    }
    if (roots != deg) return false;
    const std::vector<int> S2 = syndromes(fixedCw, nCheck);
    if (!std::all_of(S2.begin(), S2.end(), [](int s) { return s == 0; })) return false;
    cw.swap(fixedCw);
    if (errorsOut) *errorsOut = errors;
    return true;
}

namespace {

// one band turned upright (both ways round when its start and stop patterns allow): the read, its
// corners in the image; nCheck as readUpright gives it. lightOnDark: the print is light bars on a
// dark ground (read on the band inverted)
bool readBand(const cv::Mat& gray, const LinearBand& band, std::chrono::steady_clock::time_point deadline,
              const std::function<bool(int, std::string)>& accept, Prof& prof, LinearRead& r, int* nCheckOut = nullptr,
              bool lightOnDark = false)
{
    // upright at about 3 px per module or more: 2x for small bands
    const float s = band.length < 700.f ? 2.f : 1.f;
    const float W = band.length * 1.15f + 40.f, H = band.height * 1.15f + 40.f;
    const int w = static_cast<int>(W * s), h = static_cast<int>(H * s);
    // one affine warp; the other way round is the same image turned half a turn
    const cv::Point2f d = band.dir, n(-d.y, d.x);
    const cv::Point2f o = band.centre - d * (W / 2) - n * (H / 2);
    // from the band's box in the image only (a frame in float is 80 MB at 20 MP)
    const cv::Point2f hx = d * (W / 2), hy = n * (H / 2);
    const float ex = std::abs(hx.x) + std::abs(hy.x) + 2, ey = std::abs(hx.y) + std::abs(hy.y) + 2;
    const cv::Rect box = cv::Rect(cv::Point(static_cast<int>(std::floor(band.centre.x - ex)), static_cast<int>(std::floor(band.centre.y - ey))),
                                  cv::Point(static_cast<int>(std::ceil(band.centre.x + ex)), static_cast<int>(std::ceil(band.centre.y + ey)))) &
                         cv::Rect(0, 0, gray.cols, gray.rows);
    if (box.empty() || w < 20 || h < 20) return false;
    cv::Mat g32;
    gray(box).convertTo(g32, CV_32F, lightOnDark ? -1.0 : 1.0, lightOnDark ? 255.0 : 0.0);
    const cv::Matx23f toImg(d.x / s, n.x / s, o.x - static_cast<float>(box.x), d.y / s, n.y / s, o.y - static_cast<float>(box.y));
    cv::Mat u;
    cv::warpAffine(g32, u, toImg, cv::Size(w, h), cv::INTER_LINEAR | cv::WARP_INVERSE_MAP, cv::BORDER_REPLICATE);
    prof.mark("upright");
    bool otherWay = true;
    for (const float turn : {1.f, -1.f}) {
        if (std::chrono::steady_clock::now() > deadline) break;
        // image point of upright pixel p (x, y); turned: of (w - 1 - x, h - 1 - y)
        auto toImage = [&](cv::Point2f p) {
            if (turn < 0) p = cv::Point2f(static_cast<float>(w - 1) - p.x, static_cast<float>(h - 1) - p.y);
            return o + d * (p.x / s) + n * (p.y / s);
        };
        if (turn < 0) {
            if (!otherWay) break;
            cv::flip(u, u, -1);
        }
        Edges e;
        const bool edges = findEdges(u, e, turn > 0 ? &otherWay : nullptr);
        prof.mark("edges");
        if (!edges) continue;
        std::array<cv::Point2f, 4> q;
        const std::string text = readUpright(u, e, q, prof, nCheckOut);
        if (text.empty() || (accept && !accept(CR_PDF417, text))) continue;
        r.text = text;
        r.format = CR_PDF417;
        r.step = CR_STEP_LINEAR;
        for (int k = 0; k < 4; ++k) r.quad[k] = toImage(q[static_cast<size_t>(k)]);
        return true;
    }
    return false;
}

} // namespace

std::vector<LinearRead> readPdf417Fused(const cv::Mat& gray, int maxCodes, std::chrono::steady_clock::time_point deadline,
                                        const std::function<bool(int, std::string)>& accept)
{
    std::vector<LinearRead> out;
    // the gradient blobs split a PDF417 into pieces (its rows break the coherence): bands of one
    // direction that touch are united into one box
    Prof prof;
    std::vector<LinearBand> bands = findStackedBands(gray);
    prof.mark("bands");
    for (const LinearBand& band : bands) {
        if (static_cast<int>(out.size()) >= maxCodes || std::chrono::steady_clock::now() > deadline) break;
        bool read = false;
        for (const auto& r : out) {
            int side = 0; // inside the convex quad: on the same side of all four edges
            for (int k = 0; k < 4; ++k) {
                const cv::Point2f e = r.quad[(k + 1) % 4] - r.quad[k], q = band.centre - r.quad[k];
                side += e.x * q.y - e.y * q.x > 0 ? 1 : -1;
            }
            read = read || std::abs(side) == 4;
        }
        if (read) continue;
        LinearRead r;
        if (readBand(gray, band, deadline, accept, prof, r)) out.push_back(r);
    }
    return out;
}

bool pdf417ReadHolds(const cv::Mat& gray, const cv::Point2f quad[4], const std::string& text, float uec, bool lightOnDark)
{
    // no check codeword used: every syndrome was zero on the codewords as read
    if (uec >= 0.999f) return true;
    // the symbol again from zxing's corners: rows along quad[0] -> quad[1]
    LinearBand band;
    const cv::Point2f along = (quad[1] - quad[0]) + (quad[2] - quad[3]), across = (quad[3] - quad[0]) + (quad[2] - quad[1]);
    const float len = std::hypot(along.x, along.y);
    if (len < 1.f) return false;
    band.dir = along / len;
    band.centre = (quad[0] + quad[1] + quad[2] + quad[3]) / 4.f;
    band.length = len / 2;
    band.height = std::abs(across.dot(cv::Point2f(-band.dir.y, band.dir.x))) / 2;
    Prof prof;
    LinearRead r;
    int nCheck = 0;
    if (readBand(gray, band, std::chrono::steady_clock::now() + std::chrono::milliseconds(500), nullptr, prof, r, &nCheck, lightOnDark))
        return r.text == text; // two reads, each with check codewords to spare: they must agree
    // not read again with 2 check codewords to spare: zxing's own count must leave them. Without
    // the row indicators (the check codewords unknown) at most half used.
    if (uec < 0.f) return false;
    if (nCheck <= 0) return uec >= 0.5f;
    const float used = (1.f - uec) * static_cast<float>(nCheck);
    return used <= static_cast<float>(nCheck - 2) + 0.25f;
}

} // namespace cr
