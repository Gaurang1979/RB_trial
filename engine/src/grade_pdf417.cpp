// grade_pdf417.cpp - PDF417 print quality after ISO/IEC 15415 (multi-row bar code symbols) and
// ISO/IEC 15438 (PDF417).
//
// The symbol's corners come from the decoder (zxing). The X-dimension comes from the start pattern
// (17 modules) on the middle scan line, the number of data columns from the symbol's width (69 +
// 17 per column), the rows from the clusters of the codewords: every row's codewords are of one
// cluster (0, 3, 6 down the rows), so scan lines down the symbol change cluster where a row ends.
// Each row is then scanned along its middle through a 0.8 X aperture and graded:
//   per scan (ISO/IEC 15416 style): Rmin, symbol contrast, minimum edge contrast, start and stop
//     patterns; the scans' grades are averaged;
//   per codeword (every row indicator and data codeword crossed): valid when its eight elements
//     make a codeword of the row's cluster in the ISO/IEC 15438 table; modulation, defects and
//     decodability graded on each, then for the symbol as ISO 15415 does for codeword-based
//     parameters: at each grade level the codewords below it are counted as erasures against the
//     error correction (notional UEC), the parameter's grade the best level that leaves enough;
//   codeword yield: valid codewords over codewords crossed (A >= 71 %, B 64, C 57, D 50);
//   unused error correction: the decoder's own (zxing, from the Reed-Solomon errors it corrected);
//   quiet zones: 2 X left and right.
// The overall grade is the lowest of these. A codeword is valid only when its rounded edge-to-
// similar-edge distances (the ISO 15438 reference decode) name a codeword of the row's cluster in
// the ISO/IEC 15438 codeword table (pdf417Pattern, pdf417.cpp): a misprint that keeps the structure and the
// cluster but makes no codeword is invalid; one that makes another codeword is valid here and
// counted by the decoder's UEC.
//
// Without a reflectance calibration the values are grey as a share of full scale (process
// control, not certified verification), as for the other types.
#include "reader.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <numeric>
#include <utility>
#include <vector>

namespace cr {

namespace {

int gradeHigh(float v, float a, float b, float c, float d) { return v >= a ? 4 : v >= b ? 3 : v >= c ? 2 : v >= d ? 1 : 0; }
int gradeLow(float v, float a, float b, float c, float d) { return v <= a ? 4 : v <= b ? 3 : v <= c ? 2 : v <= d ? 1 : 0; }

float percentile(std::vector<float> v, float q)
{
    if (v.empty()) return 0.f;
    const size_t k = std::min(v.size() - 1, static_cast<size_t>(q * (v.size() - 1)));
    std::nth_element(v.begin(), v.begin() + static_cast<long>(k), v.end());
    return v[k];
}

// The ISO/IEC 15438 table looked up by the six edge-to-similar-edge distances (2..12 modules each,
// unique within a cluster): key E0..E5 in 4 bits each -> codeword, per cluster index 0..2
int tableValue(int clusterIndex, const int E[6])
{
    struct Index {
        std::vector<std::pair<int, int>> byKey[3]; // (key, codeword), sorted by key
        Index()
        {
            for (int cl = 0; cl < 3; ++cl) {
                for (int v = 0; v < 929; ++v) {
                    int w[8], k = 0, run = 0, last = 1;
                    for (int b = 16; b >= 0; --b) {
                        const int bit = pdf417Pattern(cl, v) >> b & 1;
                        if (bit != last && run) { w[k++] = run; run = 0; }
                        last = bit;
                        ++run;
                    }
                    w[k] = run;
                    int key = 0;
                    for (int i = 0; i < 6; ++i) key = key << 4 | (w[i] + w[i + 1]);
                    byKey[cl].emplace_back(key, v);
                }
                std::sort(byKey[cl].begin(), byKey[cl].end());
            }
        }
    };
    static const Index index;
    int key = 0;
    for (int i = 0; i < 6; ++i) {
        if (E[i] < 2 || E[i] > 12) return -1;
        key = key << 4 | E[i];
    }
    const auto& t = index.byKey[clusterIndex];
    const auto it = std::lower_bound(t.begin(), t.end(), std::make_pair(key, -1));
    return it != t.end() && it->first == key ? it->second : -1;
}

constexpr int kSub = 4;      // samples per module along a scan line
constexpr float kMargin = 3; // modules sampled beyond each side of the symbol

struct Edge {
    float u;     // position in modules from the symbol's left edge
    bool toDark; // light -> dark (a bar's leading edge)
};

// One scan line through the symbol in module coordinates: u from -kMargin to W + kMargin.
struct Line {
    std::vector<float> r; // reflectance % (or grey share) per sample
    std::vector<uint8_t> inside; // the sample lies inside the image (beyond it the edge pixels repeat)
    float rmin = 0, rmax = 0, gt = 0;
    std::vector<Edge> edges;
    float at(float u) const { return (u + kMargin) * kSub; } // sample index of u
};

// a decoded codeword's measures
struct Codeword {
    bool structure = false; // eight elements of 1..6 modules making a cluster 0, 3 or 6
    int cluster = -1;
    int value = -1;         // the codeword (0..928) its E-values name in its cluster's table, -1 none
    bool valid = false;     // structure, the row's cluster and a codeword of the table
    float decodability = 0, mod = 0, defects = 1;
    int grade[3] = {0, 0, 0}; // MOD, defects, decodability
};

struct Grid {
    cv::Mat H; // (u modules, v 0..1 down the symbol) -> image
    int W = 0; // modules across, start to stop
};

Line sampleLine(const cv::Mat& g32, const Grid& g, float v, const Calibration* cal)
{
    Line l;
    const int n = static_cast<int>((g.W + 2 * kMargin) * kSub);
    std::vector<cv::Point2f> in(static_cast<size_t>(n)), out;
    for (int i = 0; i < n; ++i) in[static_cast<size_t>(i)] = {-kMargin + (i + 0.5f) / kSub, v};
    cv::perspectiveTransform(in, out, g.H);
    cv::Mat mx(1, n, CV_32F), my(1, n, CV_32F), line;
    for (int i = 0; i < n; ++i) {
        mx.at<float>(0, i) = out[static_cast<size_t>(i)].x;
        my.at<float>(0, i) = out[static_cast<size_t>(i)].y;
    }
    cv::remap(g32, line, mx, my, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    l.inside.resize(static_cast<size_t>(n));
    for (int i = 0; i < n; ++i) {
        const float x = mx.at<float>(0, i), y = my.at<float>(0, i);
        l.inside[static_cast<size_t>(i)] = x >= 0.f && y >= 0.f && x <= static_cast<float>(g32.cols - 1) && y <= static_cast<float>(g32.rows - 1);
    }
    // the 0.8 X measuring aperture along the line (the row is at least 3 X tall across it)
    const int k = std::max(1, static_cast<int>(std::lround(0.8f * kSub)));
    l.r.resize(static_cast<size_t>(n));
    for (int i = 0; i < n; ++i) {
        double s = 0;
        int cnt = 0;
        for (int d = -k / 2; d <= k / 2; ++d)
            if (i + d >= 0 && i + d < n) { s += line.at<float>(0, i + d); ++cnt; }
        const float grey = static_cast<float>(s / std::max(1, cnt));
        l.r[static_cast<size_t>(i)] = cal ? toReflectance(*cal, out[static_cast<size_t>(i)], grey) : grey / 255.f * 100.f;
    }
    // the threshold from the symbol's own samples (not the quiet zones or what lies beyond)
    const auto b = l.r.begin() + static_cast<long>(kMargin * kSub), e = l.r.end() - static_cast<long>(kMargin * kSub);
    l.rmin = *std::min_element(b, e);
    l.rmax = *std::max_element(b, e);
    l.gt = (l.rmin + l.rmax) / 2.f;
    for (size_t i = 1; i < l.r.size(); ++i) {
        const bool d0 = l.r[i - 1] < l.gt, d1 = l.r[i] < l.gt;
        if (d0 == d1) continue;
        const float t = static_cast<float>(i - 1) + (l.gt - l.r[i - 1]) / (l.r[i] - l.r[i - 1]) + 0.5f;
        l.edges.push_back({t / kSub - kMargin, d1});
    }
    return l;
}

// the edge of the given polarity nearest u, within a module; -1 if none
int nearestEdge(const Line& l, float u, bool toDark)
{
    int best = -1;
    float bd = 1.0f;
    for (size_t i = 0; i < l.edges.size(); ++i)
        if (l.edges[i].toDark == toDark && std::abs(l.edges[i].u - u) < bd) {
            bd = std::abs(l.edges[i].u - u);
            best = static_cast<int>(i);
        }
    return best;
}

// an element's extreme (a bar its darkest, a space its brightest) and its reflectance
// non-uniformity (a valley inside a space, a peak inside a bar), between edges u0 and u1
void element(const Line& l, float u0, float u1, bool bar, float& ext, float& ern)
{
    size_t x0 = static_cast<size_t>(std::max(0.f, std::ceil(l.at(u0) - 0.5f)));
    size_t x1 = std::min(l.r.size(), static_cast<size_t>(std::max(0.f, std::floor(l.at(u1) - 0.5f))) + 1);
    if (x1 <= x0) { // narrower than a sample: the sample nearest its middle
        x0 = std::min(l.r.size() - 1, static_cast<size_t>(std::max(0.f, l.at((u0 + u1) / 2) - 0.5f)));
        x1 = x0 + 1;
    }
    float lo = 1e9f, hi = -1e9f;
    for (size_t i = x0; i < x1; ++i) { lo = std::min(lo, l.r[i]); hi = std::max(hi, l.r[i]); }
    ext = bar ? lo : hi;
    ern = 0.f;
    if (x1 > x0 + 2) {
        const size_t m = x1 - x0;
        std::vector<float> left(m), right(m);
        for (size_t i = 0; i < m; ++i) left[i] = i ? (bar ? std::min(left[i - 1], l.r[x0 + i]) : std::max(left[i - 1], l.r[x0 + i])) : l.r[x0];
        for (size_t i = m; i-- > 0;) right[i] = i + 1 < m ? (bar ? std::min(right[i + 1], l.r[x0 + i]) : std::max(right[i + 1], l.r[x0 + i])) : l.r[x0 + i];
        for (size_t i = 1; i + 1 < m; ++i)
            ern = std::max(ern, bar ? l.r[x0 + i] - std::max(left[i - 1], right[i + 1]) : std::min(left[i - 1], right[i + 1]) - l.r[x0 + i]);
    }
}

// The codeword starting at module u0 (a bar's leading edge): its eight elements up to the next
// leading edge 17 modules on. sc: the scan's symbol contrast, for modulation and defects.
Codeword readCodeword(const Line& l, float u0, float sc)
{
    Codeword c;
    const int a = nearestEdge(l, u0, true), b = nearestEdge(l, u0 + 17.f, true);
    if (a < 0 || b != a + 8) return c;
    float w[8], p = 0;
    for (int i = 0; i < 8; ++i) {
        w[i] = l.edges[static_cast<size_t>(a + i + 1)].u - l.edges[static_cast<size_t>(a + i)].u;
        p += w[i];
    }
    if (p <= 0) return c;
    // ISO/IEC 15438 reference decode: the six edge-to-similar-edge distances in modules, rounded;
    // decodability the least room left to a rounding decision (half a module)
    int E[6];
    float worst = 0.5f;
    for (int i = 0; i < 6; ++i) {
        const float t = (w[i] + w[i + 1]) * 17.f / p;
        E[i] = static_cast<int>(std::lround(t));
        worst = std::min(worst, 0.5f - std::abs(t - static_cast<float>(E[i])));
    }
    bool widths = true;
    for (float x : w) {
        const long m = std::lround(x * 17.f / p);
        widths = widths && m >= 1 && m <= 6;
    }
    const int K = ((E[0] - E[1] + E[4] - E[5]) % 9 + 9) % 9;
    c.structure = widths && K % 3 == 0;
    c.cluster = c.structure ? K : -1;
    c.value = c.structure ? tableValue(K / 3, E) : -1;
    c.decodability = std::max(0.f, worst / 0.5f);
    // modulation: the least edge contrast of its elements (from the element before it) over SC
    float ext[9], ern = 0;
    for (int i = -1; i < 8; ++i) {
        const float x0 = i < 0 ? std::max(-kMargin, l.edges[static_cast<size_t>(a)].u - 1.f) : l.edges[static_cast<size_t>(a + i)].u;
        const float x1 = l.edges[static_cast<size_t>(a + i + 1)].u;
        float e = 0, r = 0;
        element(l, x0, x1, i % 2 == 0, e, r); // even elements are bars, the one before a space
        ext[i + 1] = e;
        if (i >= 0) ern = std::max(ern, r);
    }
    float ec = 1e9f;
    for (int i = 0; i < 8; ++i) ec = std::min(ec, std::abs(ext[i] - ext[i + 1]));
    c.mod = sc > 0 ? ec / sc : 0.f;
    c.defects = sc > 0 ? ern / sc : 1.f;
    return c;
}

// the elements from the leading edge nearest u0 through `count` elements match `want` modules
bool patternAt(const Line& l, float u0, const int* want, int count, int modules)
{
    const int a = nearestEdge(l, u0, true);
    if (a < 0 || a + count >= static_cast<int>(l.edges.size())) return false;
    const float p = l.edges[static_cast<size_t>(a + count)].u - l.edges[static_cast<size_t>(a)].u;
    if (p <= 0 || std::abs(p - static_cast<float>(modules)) > 0.15f * modules) return false;
    for (int i = 0; i < count; ++i) {
        const float x = (l.edges[static_cast<size_t>(a + i + 1)].u - l.edges[static_cast<size_t>(a + i)].u) * modules / p;
        if (std::lround(x) != want[i]) return false;
    }
    return true;
}

constexpr int kStart[8] = {8, 1, 1, 1, 1, 1, 1, 3};
constexpr int kStop[9] = {7, 1, 1, 3, 1, 1, 1, 2, 1};

// the cluster most of a line's codewords have, -1 when too few agree
int lineCluster(const Line& l, int cols, float sc)
{
    int votes[9] = {};
    for (int j = 0; j < cols + 2; ++j) {
        const Codeword c = readCodeword(l, 17.f + 17.f * j, sc);
        if (c.structure) ++votes[c.cluster];
    }
    const int best = static_cast<int>(std::max_element(votes, votes + 9) - votes);
    return votes[best] >= std::max(2, (cols + 2) * 2 / 5) ? best : -1;
}

// ISO 15415 codeword-based grading: at each level, the codewords graded below it as erasures
// against the error correction (notional UEC); the best level min(level, grade of that UEC)
int erasureGrade(const std::vector<int>& grades, int ecCodewords)
{
    const int cap = ecCodewords - 2; // PDF417 keeps two for detection
    int best = 0;
    for (int level = 1; level <= 4; ++level) {
        const int e = static_cast<int>(std::count_if(grades.begin(), grades.end(), [&](int g) { return g < level; }));
        const float uec = cap > 0 ? 1.f - static_cast<float>(e) / cap : (e == 0 ? 1.f : 0.f);
        best = std::max(best, std::min(level, gradeHigh(uec, 0.62f, 0.50f, 0.37f, 0.25f)));
    }
    return best;
}

// grade with the corners in this order (TL, TR, BR, BL of the symbol); measured false when the
// start pattern is not where it should be
Quality gradeAs(const cv::Mat& g32, const cv::Point2f P[4], const Code& code, const Calibration* cal)
{
    Quality q;
    const float pxW = static_cast<float>((cv::norm(P[1] - P[0]) + cv::norm(P[2] - P[3])) / 2);
    const float pxH = static_cast<float>((cv::norm(P[3] - P[0]) + cv::norm(P[2] - P[1])) / 2);
    if (pxW < 40 || pxH < 6) return q;
    // the X-dimension from the start pattern on the middle line (its first 8 elements are 17
    // modules), sampled first at about a module per pixel; then the data columns from the width
    Grid g;
    {
        g.W = std::max(40, static_cast<int>(std::lround(pxW)));
        const cv::Point2f s1[4] = {{0, 0}, {static_cast<float>(g.W), 0}, {static_cast<float>(g.W), 1}, {0, 1}};
        g.H = homography4(s1, P);
        const Line l = sampleLine(g32, g, 0.5f, cal);
        // the start bar: the first leading edge, past the light margin
        const auto it = std::find_if(l.edges.begin(), l.edges.end(), [](const Edge& e) { return e.toDark; });
        const size_t a = static_cast<size_t>(it - l.edges.begin());
        if (it == l.edges.end() || a + 8 >= l.edges.size()) return q;
        const float x = (l.edges[a + 8].u - l.edges[a].u) / 17.f; // provisional units per module
        if (x <= 0) return q;
        const float wMod = static_cast<float>(g.W) / x;
        const int cols = static_cast<int>(std::lround((wMod - 69.f) / 17.f));
        if (cols < 1 || cols > 30 || std::abs(wMod - (69.f + 17.f * cols)) > 0.05f * wMod) return q;
        g.W = 69 + 17 * cols;
    }
    const int cols = (g.W - 69) / 17;
    const cv::Point2f s[4] = {{0, 0}, {static_cast<float>(g.W), 0}, {static_cast<float>(g.W), 1}, {0, 1}};
    g.H = homography4(s, P);
    const float xPx = pxW / g.W;
    if (xPx < 1.2f) return q; // too few pixels per module to grade
    // the start pattern on the middle line, or this is the wrong way round
    {
        const Line mid = sampleLine(g32, g, 0.5f, cal);
        if (!patternAt(mid, 0.f, kStart, 8, 17)) return q;
    }
    // rows: scan lines 4 per X down the symbol; each line's cluster; where it changes, a row ends
    const int L = std::clamp(static_cast<int>(std::lround(4.f * pxH / xPx)), 12, 1200);
    std::vector<int> cl(static_cast<size_t>(L));
    for (int i = 0; i < L; ++i) {
        const Line l = sampleLine(g32, g, (i + 0.5f) / L, cal);
        cl[static_cast<size_t>(i)] = lineCluster(l, cols, l.rmax - l.rmin);
    }
    // runs of one cluster (lines that read no cluster neither end nor extend a run)
    struct Run { int k, first, last; };
    std::vector<Run> runs;
    for (int i = 0; i < L; ++i) {
        const int k = cl[static_cast<size_t>(i)];
        if (k < 0) continue;
        if (!runs.empty() && runs.back().k == k) runs.back().last = i;
        else runs.push_back({k, i, i});
    }
    int R = 0;
    if (runs.size() >= 3) {
        // row height from the boundaries between runs; consecutive runs are 1 or 2 rows apart by
        // their clusters (a row that read nothing between them), 3 when the cluster repeats
        float b0 = -1, b1 = -1;
        int rowsBetween = 0;
        for (size_t i = 0; i + 1 < runs.size(); ++i) {
            const float b = (runs[i].last + runs[i + 1].first + 1) / 2.f;
            const int d = ((runs[i + 1].k - runs[i].k) / 3 + 3) % 3;
            if (b0 < 0) b0 = b;
            else rowsBetween += d == 0 ? 3 : d;
            b1 = b;
        }
        if (rowsBetween > 0) R = static_cast<int>(std::lround(L * rowsBetween / (b1 - b0)));
    }
    else if (runs.size() == 2 || runs.size() == 1)
        R = 3; // the fewest rows PDF417 has; more would show more runs
    if (R < 3 || R > 90) return q;

    // the error correction codewords: zxing states them as a share of the data region
    int ecCw = 0;
    if (!code.ecLevel.empty() && code.ecLevel.back() == '%') {
        const float n = std::strtof(code.ecLevel.c_str(), nullptr) / 100.f * R * cols;
        // 2^(s+1) for error correction level s (0..8)
        ecCw = 2;
        while (ecCw < 512 && std::abs(ecCw * 2 - n) < std::abs(ecCw - n)) ecCw *= 2;
    }

    // each row along its middle
    int scanGrades = 0, decoded = 0, cwN = 0, cwValid = 0, qzN = 0, qzDark = 0, qzOut = 0;
    float sumSC = 0, sumEC = 0, sumRmin = 0, sumRmax = 0, lightMin = 1e9f, lightMax = 0;
    std::vector<float> mods, defs, decs;
    std::vector<int> gMod, gDef, gDec; // data codewords only (the row indicators are not in the RS code)
    for (int r = 0; r < R; ++r) {
        const Line l = sampleLine(g32, g, (r + 0.5f) / R, cal);
        const float sc = l.rmax - l.rmin;
        // minimum edge contrast over the row's elements, the quiet zones as the outer spaces
        float ecmin = 1e9f, prev = 0;
        bool first = true;
        {
            const int a = nearestEdge(l, 0.f, true), b = nearestEdge(l, static_cast<float>(g.W), false);
            if (a >= 0 && b > a) {
                float ext, ern;
                element(l, -2.f, l.edges[static_cast<size_t>(a)].u, false, ext, ern);
                prev = ext;
                first = false;
                for (int i = a; i <= b; ++i) {
                    const float u1 = i < b ? l.edges[static_cast<size_t>(i + 1)].u : static_cast<float>(g.W) + 2.f;
                    element(l, l.edges[static_cast<size_t>(i)].u, u1, l.edges[static_cast<size_t>(i)].toDark, ext, ern);
                    ecmin = std::min(ecmin, std::abs(ext - prev));
                    prev = ext;
                }
            }
        }
        if (first) ecmin = 0;
        const bool start = patternAt(l, 0.f, kStart, 8, 17), stop = patternAt(l, static_cast<float>(g.W - 18), kStop, 9, 18);
        const int want = (r % 3) * 3;
        for (int j = 0; j < cols + 2; ++j) {
            Codeword c = readCodeword(l, 17.f + 17.f * j, sc);
            c.valid = c.structure && c.cluster == want && c.value >= 0;
            ++cwN;
            cwValid += c.valid;
            const int gm = c.valid ? gradeHigh(c.mod, 0.70f, 0.60f, 0.50f, 0.40f) : 0;
            const int gd = c.valid ? gradeLow(c.defects, 0.15f, 0.20f, 0.25f, 0.30f) : 0;
            const int gv = c.valid ? gradeHigh(c.decodability, 0.62f, 0.50f, 0.37f, 0.25f) : 0;
            mods.push_back(c.valid ? c.mod : 0.f);
            defs.push_back(c.valid ? c.defects : 1.f);
            decs.push_back(c.valid ? c.decodability : 0.f);
            if (j > 0 && j <= cols) { gMod.push_back(gm); gDef.push_back(gd); gDec.push_back(gv); }
        }
        // quiet zones: 2 X of light each side (from the module next to the symbol outwards, as
        // the matrix codes' ring of module centres). A sample off the image is no quiet zone: it
        // counts dark, and the quiet zone grades F (line safety)
        for (float u = -2.f + 0.5f / kSub; u <= -0.5f; u += 1.f / kSub)
            for (float uu : {u, static_cast<float>(g.W) - u}) {
                const size_t i = static_cast<size_t>(std::clamp(l.at(uu), 0.f, static_cast<float>(l.r.size() - 1)));
                ++qzN;
                qzOut += !l.inside[i];
                qzDark += !l.inside[i] || l.r[i] < l.gt;
            }
        const int gRmin = l.rmin <= 0.5f * l.rmax ? 4 : 0;
        const int gSC = gradeHigh(sc, 70, 55, 40, 20);
        const int gEC = ecmin >= 15.f ? 4 : 0;
        scanGrades += std::min({gRmin, gSC, gEC, start && stop ? 4 : 0});
        decoded += start && stop; // the row's own codewords count in the codeword yield
        sumSC += sc;
        sumEC += ecmin;
        sumRmin += l.rmin;
        sumRmax += l.rmax;
        lightMin = std::min(lightMin, l.rmax);
        lightMax = std::max(lightMax, l.rmax);
    }

    // reported in the 1D fields (per-scan contrast, edge contrast, defects, decodability, scans);
    // unused error correction, quiet zone and codeword yield in their own
    q.linear = true;
    q.scans = R;
    q.decodedScans = decoded;
    q.symbolContrast = sumSC / R;
    q.edgeContrastMin = sumEC / R;
    q.rMin = sumRmin / R;
    q.rMax = sumRmax / R;
    q.modulation = percentile(mods, 0.05f);
    q.defects = percentile(defs, 0.95f);
    q.decodability = percentile(decs, 0.05f);
    q.codewordYield = cwN ? static_cast<float>(cwValid) / cwN : 0.f;
    q.unusedErrorCorrection = code.uec >= 0.f ? std::clamp(code.uec, 0.f, 1.f) : -1.f; // -1: the decoder gave none
    q.quietZoneDark = qzN ? static_cast<float>(qzDark) / qzN : -1.f;
    q.moduleSizePx = xPx;
    q.xDimMm = cal && cal->mmPerPx > 0.f ? lengthMm(*cal, P[0], P[1]) / g.W : 0.f;
    q.calibrated = cal && cal->reflectance;
    q.lightingUniformity = lightMin / std::max(1e-3f, lightMax);

    q.gradeSC = gradeHigh(q.symbolContrast, 70, 55, 40, 20);
    q.gradeEC = q.edgeContrastMin >= 15.f ? 4 : 0;
    // without the error correction count, every codeword below a level counts against none
    const int ec = ecCw > 0 ? ecCw : 2;
    q.gradeMOD = erasureGrade(gMod, ec);
    q.gradeDefects = erasureGrade(gDef, ec);
    q.gradeDecodability = erasureGrade(gDec, ec);
    q.gradeCY = gradeHigh(q.codewordYield, 0.71f, 0.64f, 0.57f, 0.50f);
    q.gradeUEC = code.uec >= 0.f ? gradeHigh(q.unusedErrorCorrection, 0.62f, 0.50f, 0.37f, 0.25f) : -1;
    q.gradeQZ = q.quietZoneDark < 0 ? -1 : qzOut ? 0 : gradeLow(q.quietZoneDark, 0.0f, 0.05f, 0.10f, 0.20f);
    const float avg = static_cast<float>(scanGrades) / R;
    int overall = avg >= 3.5f ? 4 : avg >= 2.5f ? 3 : avg >= 1.5f ? 2 : avg >= 0.5f ? 1 : 0;
    for (int gr : {q.gradeMOD, q.gradeDefects, q.gradeDecodability, q.gradeCY, q.gradeUEC, q.gradeQZ})
        if (gr >= 0) overall = std::min(overall, gr);
    q.overall = overall;
    q.measured = true;
    return q;
}

} // namespace

Quality gradePdf417(const cv::Mat& gray, const Code& c, const Calibration* cal)
{
    if (c.format != CR_PDF417 || gray.empty()) return {};
    cv::Mat g32;
    gray.convertTo(g32, CV_32F);
    const cv::Point2f P[4] = {{c.x[0], c.y[0]}, {c.x[1], c.y[1]}, {c.x[2], c.y[2]}, {c.x[3], c.y[3]}};
    Quality q = gradeAs(g32, P, c, cal);
    if (q.measured) return q;
    // the corners the other way round (the start pattern on the right): turned half a circle
    const cv::Point2f T[4] = {P[2], P[3], P[0], P[1]};
    return gradeAs(g32, T, c, cal);
}

} // namespace cr
