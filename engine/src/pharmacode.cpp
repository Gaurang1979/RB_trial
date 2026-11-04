// pharmacode.cpp - Laetus Pharmacode (one-track) on folding boxes and leaflets.
//
// The symbol is 2-16 bars, narrow (1 unit, nominally 0.5 mm) or wide (3 units), 2 units apart. Read
// from the right, the n-th bar weighs 2^n (narrow) or 2 x 2^n (wide): values 3 to 131070. There is
// no start, stop or check digit, so any few bars of the right widths decode to some value. A read
// stands only when (a) the bars come out in two clean width classes, gaps all alike, (b) both quiet
// zones are clear for 3 gaps, (c) the bar count is inside the job's lock (cr_params min_length /
// max_length count bars). A covered or cut-off bar fails (a) or (b); a missing bar at an end that
// still leaves clean bars fails (c).
//
// Widths are measured gain-free: leading edge to leading edge (and trailing to trailing) of two
// neighbouring bars is one bar plus one gap, so ink spread and thin print shift both edges alike.
// narrow + gap = 3 units, wide + gap = 5 units.
//
// The bands come from the 1D band finder (linear.cpp). The job states the reading direction
// (cr_pharma_direction): across, the scan runs left to right in the image and a code more than 75
// degrees off upright is not read; down, it runs top to bottom and a code more than 75 degrees off
// lying flat is not read. Past that the direction would hang on a small tilt, and read the other
// way the bars give another valid value.
#include "reader.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>

namespace cr {

namespace {

constexpr int kSub = 4;          // profile samples per pixel
constexpr int kLines = 24;       // scan lines fused per profile
constexpr float kMaxTilt = 0.966f; // sin 75 degrees: the scan direction's largest part across the reading direction

struct Run { float a, b; }; // a dark run, in samples [a, b)

float median(std::vector<float> v)
{
    if (v.empty()) return 0.f;
    std::nth_element(v.begin(), v.begin() + static_cast<long>(v.size() / 2), v.end());
    return v[v.size() / 2];
}

// the median of `kLines` scan lines across the band, reaching `margin` px past each end of it;
// samples off the image are -1
std::vector<float> wideProfile(const cv::Mat& gray, const LinearBand& b, float margin)
{
    const cv::Point2f e(-b.dir.y, b.dir.x);
    const float half = b.length / 2 + margin;
    const int n = static_cast<int>(2 * half * kSub);
    cv::Mat mx(kLines, n, CV_32F), my(kLines, n, CV_32F);
    for (int l = 0; l < kLines; ++l) {
        const float off = (-0.3f + 0.6f * l / (kLines - 1)) * b.height;
        for (int i = 0; i < n; ++i) {
            const float t = -half + static_cast<float>(i) / kSub;
            mx.at<float>(l, i) = b.centre.x + t * b.dir.x + off * e.x;
            my.at<float>(l, i) = b.centre.y + t * b.dir.y + off * e.y;
        }
    }
    cv::Mat g, rows;
    gray.convertTo(g, CV_32F);
    cv::remap(g, rows, mx, my, cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(-1000));
    std::vector<float> p(static_cast<size_t>(n)), col(kLines);
    for (int i = 0; i < n; ++i) {
        for (int l = 0; l < kLines; ++l) col[static_cast<size_t>(l)] = rows.at<float>(l, i);
        std::nth_element(col.begin(), col.begin() + kLines / 2, col.end());
        p[static_cast<size_t>(i)] = std::max(-1.f, col[kLines / 2]);
    }
    return p;
}

} // namespace

int decodePharmacodeProfile(const std::vector<float>& p, float codeFrom, float codeTo, int minBars, int maxBars, bool reverse,
                            int* barsOut, std::string* why)
{
    const auto fail = [&](const char* w) { if (why) *why = w; return -1; };
    std::vector<float> valid;
    for (const float v : p) if (v >= 0.f) valid.push_back(v);
    if (valid.size() < 16) return fail("profile off the image");
    std::sort(valid.begin(), valid.end());
    const float lo = valid[valid.size() / 50], hi = valid[valid.size() * 49 / 50];
    if (hi - lo < 20.f) return fail("no contrast");
    const float t = (lo + hi) / 2;
    // dark runs with sub-sample edges
    std::vector<Run> runs;
    const auto cross = [&](size_t i) { // between samples i-1 and i
        const float a = p[i - 1], b = p[i];
        return static_cast<float>(i - 1) + (std::abs(b - a) > 1e-3f ? (t - a) / (b - a) : 0.5f);
    };
    float start = -1.f;
    for (size_t i = 1; i < p.size(); ++i) {
        const bool d0 = p[i - 1] >= 0.f && p[i - 1] < t, d1 = p[i] >= 0.f && p[i] < t;
        if (!d0 && d1) start = p[i - 1] >= 0.f ? cross(i) : static_cast<float>(i);
        if (d0 && !d1 && start >= 0.f) {
            const float end = p[i] >= 0.f ? cross(i) : static_cast<float>(i);
            if (end - start >= 0.75f * kSub) runs.push_back({start, end}); // specks under a pixel dropped
            start = -1.f;
        }
    }
    // the bars: the longest row of runs inside the band with gaps all alike (the band also takes in
    // a label's edges); the runs beside it must then leave the quiet zones clear
    const auto alike = [](const std::vector<Run>& r, size_t a, size_t b) { // runs a..b-1
        std::vector<float> gaps;
        for (size_t i = a + 1; i < b; ++i) gaps.push_back(r[i].a - r[i - 1].b);
        const float g = median(gaps);
        for (const float x : gaps)
            if (x < 0.6f * g || x > 1.5f * g) return 0.f;
        // bars are 0.5 (narrow) to 1.5 (wide) gaps wide: not a background beside a label
        for (size_t i = a; i < b; ++i)
            if (r[i].b - r[i].a > 2.5f * g || r[i].b - r[i].a < 0.25f * g) return 0.f;
        return g;
    };
    size_t first = runs.size(), len = 0;
    for (size_t a = 0; a < runs.size(); ++a) {
        if (runs[a].b <= codeFrom || runs[a].a >= codeTo) continue;
        for (size_t b = a + 2; b <= runs.size() && runs[b - 1].a < codeTo; ++b)
            if (b - a > len && alike(runs, a, b) > 0.f) { first = a; len = b - a; }
    }
    if (len < 2) return fail("fewer than 2 bars with gaps alike");
    const std::vector<Run> bars(runs.begin() + static_cast<long>(first), runs.begin() + static_cast<long>(first + len));
    const int n = static_cast<int>(bars.size());
    if (n > 16) return fail("more than 16 bars");
    const float g = alike(runs, first, first + len);
    if (g < kSub) return fail("gaps under a pixel");
    // quiet zones: 4 gaps of light (2 mm at nominal size), on the image, on both sides. More than
    // the 3.5 gaps a missing wide bar leaves, so a code with a bar gone never reads as a shorter one
    const float q = 4 * g;
    const float left = bars.front().a, right = bars.back().b;
    if (left - q < 0 || right + q >= static_cast<float>(p.size())) return fail("quiet zone off the profile");
    for (int i = static_cast<int>(left - q); i < static_cast<int>(left); ++i)
        if (p[static_cast<size_t>(i)] < 0.f) return fail("quiet zone off the image");
    for (int i = static_cast<int>(right) + 1; i <= static_cast<int>(right + q); ++i)
        if (p[static_cast<size_t>(i)] < 0.f) return fail("quiet zone off the image");
    if (first > 0 && runs[first - 1].b > left - q) return fail("a mark in the left quiet zone");
    if (first + static_cast<size_t>(n) < runs.size() && runs[first + static_cast<size_t>(n)].a < right + q)
        return fail("a mark in the right quiet zone");
    // gain-free bar + gap per bar
    std::vector<float> e(static_cast<size_t>(n)), w(static_cast<size_t>(n));
    for (int i = 0; i < n; ++i) {
        const auto& r = bars[static_cast<size_t>(i)];
        float s = 0.f;
        int k = 0;
        if (i + 1 < n) { s += bars[static_cast<size_t>(i + 1)].a - r.a; ++k; }
        if (i > 0) { s += r.b - bars[static_cast<size_t>(i - 1)].b; ++k; }
        e[static_cast<size_t>(i)] = s / static_cast<float>(k);
        w[static_cast<size_t>(i)] = r.b - r.a;
    }
    const float emin = *std::min_element(e.begin(), e.end()), emax = *std::max_element(e.begin(), e.end());
    std::vector<bool> wide(static_cast<size_t>(n));
    if (emax < 1.3f * emin) {
        // one width only: all narrow (bar 1 of 3 units with its gap) or all wide (3 of 5)
        std::vector<float> r;
        for (int i = 0; i < n; ++i) r.push_back(w[static_cast<size_t>(i)] / e[static_cast<size_t>(i)]);
        const float rm = median(r);
        // narrow: 1/3 nominal, 0.44 = a third of a unit of ink spread; wide: 3/5 nominal, 0.52 = 0.4
        // units thinner. Between them heavy spread on narrow bars looks like thin wide bars: no read
        if (rm > 0.44f && rm < 0.52f) return fail("bars all one width, narrow or wide unclear");
        std::fill(wide.begin(), wide.end(), rm >= 0.52f);
    } else {
        const float mid = (emin + emax) / 2;
        float sn = 0, sw = 0, bn = 0, bw = 0;
        int cn = 0, cw = 0;
        for (int i = 0; i < n; ++i) {
            wide[static_cast<size_t>(i)] = e[static_cast<size_t>(i)] > mid;
            if (wide[static_cast<size_t>(i)]) { sw += e[static_cast<size_t>(i)]; bw += w[static_cast<size_t>(i)]; ++cw; }
            else { sn += e[static_cast<size_t>(i)]; bn += w[static_cast<size_t>(i)]; ++cn; }
        }
        sn /= static_cast<float>(cn); sw /= static_cast<float>(cw); bn /= static_cast<float>(cn); bw /= static_cast<float>(cw);
        if (sw / sn < 1.4f || sw / sn > 2.0f) return fail("wide to narrow not about 5:3");
        if (bw < 1.8f * bn) return fail("wide bars not about 3 narrow");
        for (int i = 0; i < n; ++i) {
            const float m = wide[static_cast<size_t>(i)] ? sw : sn;
            if (std::abs(e[static_cast<size_t>(i)] - m) > 0.15f * m) return fail("a bar between narrow and wide");
        }
    }
    if ((minBars > 0 && n < minBars) || (maxBars > 0 && n > maxBars)) return fail("bar count outside the job's lock");
    int value = 0;
    for (int k = 0; k < n; ++k) {
        // k = the bar's weight index: from the right (standard) or from the left (reverse)
        const int i = reverse ? k : n - 1 - k;
        value += (wide[static_cast<size_t>(i)] ? 2 : 1) << k;
    }
    if (barsOut) *barsOut = n;
    return value;
}

std::vector<LinearRead> readPharmacode(const cv::Mat& gray, int formats, int direction, int minBars, int maxBars, int maxCodes,
                                       std::chrono::steady_clock::time_point deadline,
                                       const std::function<bool(int, std::string)>& accept)
{
    std::vector<LinearRead> out;
    if (!(formats & (CR_PHARMACODE | CR_PHARMACODE_REV))) return out;
    if (direction != CR_PHARMA_ACROSS && direction != CR_PHARMA_DOWN) return out;
    const bool down = direction == CR_PHARMA_DOWN;
    const bool reverse = !(formats & CR_PHARMACODE);
    const int format = reverse ? CR_PHARMACODE_REV : CR_PHARMACODE;
    // a large symbol's bars and gaps are too wide for the band finder's window: then a quarter size
    for (const double f : {1.0, 0.25}) {
        cv::Mat s;
        if (f != 1.0) cv::resize(gray, s, cv::Size(), f, f, cv::INTER_AREA);
        else s = gray;
        for (auto b : findLinearBands(s, std::max(3, 2 * maxCodes))) {
            if (static_cast<int>(out.size()) >= maxCodes || std::chrono::steady_clock::now() >= deadline) return out;
            b.centre *= static_cast<float>(1 / f);
            b.length *= static_cast<float>(1 / f);
            b.height *= static_cast<float>(1 / f);
            if (down) {
                if (b.dir.y < 0) b.dir = -b.dir; // top to bottom
                if (std::abs(b.dir.x) > kMaxTilt) continue;
            } else {
                if (b.dir.x < 0) b.dir = -b.dir; // left to right
                if (std::abs(b.dir.y) > kMaxTilt) continue;
            }
            const float margin = std::max(0.6f * b.length, 40.f);
            const std::vector<float> p = wideProfile(gray, b, margin);
            const float slack = 0.05f * b.length + 2.f;
            const float from = (margin - slack) * kSub, to = (margin + b.length + slack) * kSub;
            const int v = decodePharmacodeProfile(p, from, to, minBars, maxBars, reverse, nullptr, nullptr);
            if (v < 0) continue;
            LinearRead r;
            r.text = std::to_string(v);
            r.format = format;
            r.step = CR_STEP_PHARMACODE;
            const cv::Point2f e(-b.dir.y, b.dir.x), du = b.dir * (b.length / 2), dv = e * (b.height / 2);
            r.quad[0] = b.centre - du - dv; r.quad[1] = b.centre + du - dv; r.quad[2] = b.centre + du + dv; r.quad[3] = b.centre - du + dv;
            if (accept && !accept(r.format, r.text)) continue;
            bool dup = false;
            for (const auto& o : out) dup = dup || cv::norm((o.quad[0] + o.quad[2]) * 0.5f - b.centre) < 0.5f * b.length;
            if (!dup) out.push_back(r);
        }
        if (!out.empty()) break;
    }
    return out;
}

} // namespace cr
