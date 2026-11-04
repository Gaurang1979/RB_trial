// lines.cpp - find the text lines in a region and straighten each one (rotation and slant).
//
// Text is found on a reduced copy of the region where a capital is kSmallCharH px high: a top-hat
// (light text) or black-hat (dark text) as large as a character keeps the strokes and drops the
// background, however uneven its light. Rotation is the angle whose row profile is sharpest; lines are
// the bands of that profile; slant is the shear whose column profile is sharpest, telling the
// print's slant from diagonal strokes (slantOf). Each line is then cut
// from a copy where a capital is about kReadCharH px high and warped to the recogniser's 32 px height.

#include "ocr.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <numeric>
#include <string>
#include <unordered_map>

namespace ocr {
namespace {

constexpr float kSmallCharH = 20.f;  // capital height (px) where lines are found
constexpr float kReadCharH = 26.f;   // capital height (px) of the copy the lines are cut from
constexpr double kPi = 3.14159265358979323846;

struct Comp {
    cv::Rect box;
    int label = 0;
    int area = 0;
    cv::Point2f c;
};

struct Mask {
    cv::Mat bin;                 // CV_8U 0/255, kept components only
    cv::Mat labels;              // CV_32S
    std::vector<Comp> comps;     // kept components
    int charLike = 0;            // components of one character's size
};

int histPercentile(const cv::Mat& m8, double q)
{
    int hist[256] = {0};
    for (int y = 0; y < m8.rows; ++y) {
        const uint8_t* p = m8.ptr<uint8_t>(y);
        for (int x = 0; x < m8.cols; ++x) ++hist[p[x]];
    }
    const double target = q * double(m8.total());
    double acc = 0;
    for (int i = 0; i < 256; ++i) {
        acc += hist[i];
        if (acc >= target) return i;
    }
    return 255;
}

// Closing radius (small-image px) that joins dot print into strokes: from the dot size when known (a gap
// of one missing dot closes), else from the character height; times the join strength. 0 = not dot print
// (inkjet grain is averaged away at this scale; it is filled in the line image, findLines).
int joinRadius(const FindParams& p, float ch, double s, bool dots)
{
    const float join = p.join > 0 ? p.join : 1.f;
    // at most twice the letter height: wider gaps are not dot print, and a closing that large on a
    // whole image takes minutes (a dot size taught on other print, a character height set too small)
    if (dots)
        return std::clamp(int(std::lround(p.dotSize > 0 ? join * 1.2f * p.dotSize * float(s) : join * 0.15f * ch)), 1,
                          std::max(2, int(2.f * ch)));
    return 0;
}

// Stroke pixels of one polarity at the small scale (CV_8U 0/255).
cv::Mat strokeBin(const cv::Mat& small, float ch, bool light)
{
    const int k = std::max(3, int(std::lround(ch * 1.4f)) | 1);
    cv::Mat th;
    cv::morphologyEx(small, th, light ? cv::MORPH_TOPHAT : cv::MORPH_BLACKHAT,
                     cv::getStructuringElement(cv::MORPH_RECT, {k, k}));
    // threshold: above the noise of the flat background, and at least 40% of the strongest stroke
    // nearby (a letter-height window), so a faint line next to bright print is kept
    const int med = histPercentile(th, 0.5), p75 = histPercentile(th, 0.75);
    const int noise = med + 2 * (p75 - med) + 2;
    cv::Mat localMax;
    const int kw = std::max(3, int(std::lround(ch * 3.0f)) | 1), kh = std::max(3, int(std::lround(ch * 1.5f)) | 1);
    cv::dilate(th, localMax, cv::getStructuringElement(cv::MORPH_RECT, {kw, kh}));
    cv::Mat bin(th.size(), CV_8U);
    for (int y = 0; y < th.rows; ++y) {
        const uint8_t* t = th.ptr<uint8_t>(y);
        const uint8_t* l = localMax.ptr<uint8_t>(y);
        uint8_t* b = bin.ptr<uint8_t>(y);
        for (int x = 0; x < th.cols; ++x) b[x] = (t[x] > noise && t[x] * 5 > l[x] * 2) ? 255 : 0;
    }
    return bin;
}

// Along the line, the closing radius of tight dot print (FindParams::tight): about half a dot, so the
// dots of one character join along the line and two characters a dot gap apart mostly stay apart
// (a missing dot is still bridged across the line, by joinRadius); -1 = as joinRadius
int joinRadiusAlong(const FindParams& p, float ch, double s, int joinR)
{
    if (!p.tight || joinR <= 0) return -1;
    const float join = p.join > 0 ? p.join : 1.f;
    return std::min(joinR, std::max(1, int(std::lround(p.dotSize > 0 ? join * 0.5f * p.dotSize * float(s) : join * 0.06f * ch))));
}

// The parts of a stroke mask of one character's size or joined letters; joinR > 0 first closes gaps
// (dots, grain) that small (joinRx >= 0: that small along the line).
Mask keepParts(cv::Mat bin, float ch, int joinR, int joinRx = -1)
{
    Mask m;
    m.bin = std::move(bin);
    if (joinR > 0) {
        const int rx = joinRx >= 0 ? joinRx : joinR;
        cv::morphologyEx(m.bin, m.bin, cv::MORPH_CLOSE,
                         cv::getStructuringElement(cv::MORPH_ELLIPSE, {2 * rx + 1, 2 * joinR + 1}));
    }
    cv::Mat stats, cents;
    const int n = cv::connectedComponentsWithStats(m.bin, m.labels, stats, cents, 8, CV_32S);
    std::vector<uint8_t> keep(size_t(n), 0);
    const float minArea = std::max(2.f, 0.012f * ch * ch);
    for (int i = 1; i < n; ++i) {
        const int w = stats.at<int>(i, cv::CC_STAT_WIDTH), h = stats.at<int>(i, cv::CC_STAT_HEIGHT);
        const int area = stats.at<int>(i, cv::CC_STAT_AREA);
        if (area < minArea) continue;
        if (h > 2.2f * ch) continue;                            // borders, logos, pictures
        if (w > 4.0f * ch && h > 1.6f * ch && w < 4 * h) continue; // large blocks; joined letters (long, thin) stay
        if (h < 0.15f * ch && w > 1.5f * ch) continue;          // thin horizontal rules
        Comp c;
        c.box = {stats.at<int>(i, cv::CC_STAT_LEFT), stats.at<int>(i, cv::CC_STAT_TOP), w, h};
        c.label = i;
        c.area = area;
        c.c = {float(cents.at<double>(i, 0)), float(cents.at<double>(i, 1))};
        m.comps.push_back(c);
        keep[size_t(i)] = 1;
        if (h >= 0.45f * ch && h <= 1.6f * ch && w <= 1.6f * ch) ++m.charLike;
    }
    for (int y = 0; y < m.bin.rows; ++y) {
        uint8_t* b = m.bin.ptr<uint8_t>(y);
        const int* l = m.labels.ptr<int>(y);
        for (int x = 0; x < m.bin.cols; ++x) b[x] = keep[size_t(l[x])] ? 255 : 0;
    }
    return m;
}

// Strokes of one polarity at the small scale; joinR > 0 closes gaps (dots, grain) that small.
Mask textMask(const cv::Mat& small, float ch, bool light, int joinR, int joinRx = -1)
{
    return keepParts(strokeBin(small, ch, light), ch, joinR, joinRx);
}

// Embossed print: no ink, a relief lit from one side, so a stroke shows as a thin light edge on one
// side and a dark edge on the other, each only part of the letter (often a third of its height), and
// the relief is faint, so a top-hat as large as a letter keeps the uneven light around it too. A
// band-pass of stroke size (difference of Gaussians), either sign, keeps both edges and drops the
// light's slope; the edges of one stroke are closed into one part.
Mask reliefMask(const cv::Mat& small, float ch)
{
    cv::Mat f, a, b;
    small.convertTo(f, CV_32F);
    const double s1 = std::max(0.6, 0.06 * ch);
    cv::GaussianBlur(f, a, {0, 0}, s1);
    cv::GaussianBlur(f, b, {0, 0}, 3 * s1);
    cv::Mat d = cv::abs(a - b);
    // the flat ground's noise: the median of |band-pass| (the strokes are a small share of the region)
    cv::Mat d8;
    d.convertTo(d8, CV_8U, 8.0);
    const float noise = float(histPercentile(d8, 0.5)) / 8.f;
    cv::Mat localMax;
    const int kw = std::max(3, int(std::lround(ch * 3.0f)) | 1), kh = std::max(3, int(std::lround(ch * 1.5f)) | 1);
    cv::dilate(d, localMax, cv::getStructuringElement(cv::MORPH_RECT, {kw, kh}));
    cv::Mat bin(d.size(), CV_8U);
    for (int y = 0; y < d.rows; ++y) {
        const float* t = d.ptr<float>(y);
        const float* l = localMax.ptr<float>(y);
        uint8_t* o = bin.ptr<uint8_t>(y);
        for (int x = 0; x < d.cols; ++x) o[x] = (t[x] > 4.f * noise + 0.5f && t[x] > 0.3f * l[x]) ? 255 : 0;
    }
    return keepParts(bin, ch, std::max(1, int(std::lround(0.06f * ch))));
}

// Sum of squared bin counts of v along a 1 px grid: large when the values bunch up.
double sharpness(const std::vector<float>& v)
{
    if (v.empty()) return 0;
    const auto [lo, hi] = std::minmax_element(v.begin(), v.end());
    const int n = int(*hi - *lo) + 2;
    std::vector<int> h(size_t(n), 0);
    for (float x : v) ++h[size_t(int(x - *lo))];
    double s = 0;
    for (int c : h) s += double(c) * c;
    return s;
}

// As sharpness, each value split between its two nearest bins: smooth in the angle, so a peak is
// found to a fraction of a degree (1 degree of shear moves a stroke end by a third of a pixel here)
double softSharpness(const std::vector<float>& v)
{
    if (v.empty()) return 0;
    const float lo = *std::min_element(v.begin(), v.end()), hi = *std::max_element(v.begin(), v.end());
    std::vector<double> h(size_t(int(hi - lo) + 3), 0.0);
    for (float x : v) {
        const float f = x - lo;
        const int b = int(f);
        const double w = double(f - float(b));
        h[size_t(b)] += 1.0 - w;
        h[size_t(b + 1)] += w;
    }
    double s = 0;
    for (double c : h) s += c * c;
    return s;
}

// The rotation in [lo, hi] degrees whose row profile is sharpest.
double bestAngle(const std::vector<cv::Point2f>& pts, double lo, double hi, double coarse, double fine)
{
    std::vector<float> v(pts.size());
    auto score = [&](double deg) {
        const double a = deg * kPi / 180.0;
        const double c = std::cos(a), s = std::sin(a);
        for (size_t i = 0; i < pts.size(); ++i) v[i] = float(-pts[i].x * s + pts[i].y * c);
        return sharpness(v);
    };
    double best = 0.5 * (lo + hi), bestS = -1;
    for (double d = lo; d <= hi + 1e-9; d += coarse) {
        const double s = score(d);
        if (s > bestS) { bestS = s; best = d; }
    }
    const double c0 = best;
    for (double d = c0 - coarse; d <= c0 + coarse + 1e-9; d += fine) {
        if (d < lo - 1e-9 || d > hi + 1e-9) continue;
        const double s = score(d);
        if (s > bestS) { bestS = s; best = d; }
    }
    return best;
}

// The slant of one line: the shear that makes its strokes vertical. Upright stems give one peak of
// the column sharpness at the print's slant; the arms of V A W X lean both ways about it and give a
// pair of peaks either side; / Z 7 and the arms of X Y lean one way, steeper than any print's slant.
// The search runs past the job's limit so such a peak is seen for what it is, not taken for a slant
// at the limit. sure is false when no peak inside the limit can be trusted: the line then takes the
// slant of the region's other lines (one printer prints them all), or none. strength is how far the
// peak stands out of the curve, its share of the peak's height
double slantOf(const std::vector<cv::Point2f>& pts, double maxDeg, float yc, bool& sure, double& strength)
{
    // a peak standing less than this share of its height out of the curve is a ripple (the stems of
    // print stand 0.2 to 0.4 out, the arms of a line of V A W about 0.1)
    constexpr double kClear = 0.05;
    sure = false;
    strength = 0;
    if (maxDeg <= 0 || pts.empty()) return 0.0;
    constexpr double kStep = 0.5;
    const double ext = std::min(maxDeg + 20.0, 45.0);
    const int n = int(std::lround(2 * ext / kStep)) + 1;
    const auto deg = [&](int i) { return -ext + i * kStep; };
    std::vector<double> raw(static_cast<size_t>(n)), sm(static_cast<size_t>(n), 0.0);
    std::vector<float> v(pts.size());
    for (int i = 0; i < n; ++i) {
        const double t = std::tan(deg(i) * kPi / 180.0);
        for (size_t k = 0; k < pts.size(); ++k) v[k] = float(pts[k].x + (pts[k].y - yc) * t);
        raw[size_t(i)] = softSharpness(v);
    }
    // what jitter the pixel grid leaves, smoothed over a degree
    for (int i = 0; i < n; ++i) {
        int m = 0;
        for (int d = -1; d <= 1; ++d)
            if (i + d >= 0 && i + d < n) { sm[size_t(i)] += raw[size_t(i + d)]; ++m; }
        sm[size_t(i)] /= m;
    }
    // peaks and their prominence: height over the lowest point on the way to a higher peak (the
    // higher of the two when both sides have one; the end of the search is no peak, so a side that
    // runs out counts only when neither side has a higher peak)
    struct Peak { int i; double prom; };
    std::vector<Peak> peaks;
    for (int i = 0; i < n; ++i) {
        bool top = true;
        for (int d = -3; d <= 3 && top; ++d)
            if (d != 0 && i + d >= 0 && i + d < n && sm[size_t(i + d)] > sm[size_t(i)]) top = false;
        if (!top || (!peaks.empty() && i - peaks.back().i <= 3)) continue;
        double lo = sm[size_t(i)], ro = sm[size_t(i)];
        int j = i - 1, k = i + 1;
        for (; j >= 0 && sm[size_t(j)] <= sm[size_t(i)]; --j) lo = std::min(lo, sm[size_t(j)]);
        for (; k < n && sm[size_t(k)] <= sm[size_t(i)]; ++k) ro = std::min(ro, sm[size_t(k)]);
        const double col = j >= 0 && k < n ? std::max(lo, ro) : j >= 0 ? lo : k < n ? ro : std::min(lo, ro);
        peaks.push_back({i, sm[size_t(i)] - col});
    }
    if (peaks.empty()) return 0.0;
    const Peak top = *std::max_element(peaks.begin(), peaks.end(), [&](const Peak& a, const Peak& b) {
        return sm[size_t(a.i)] < sm[size_t(b.i)];
    });
    // a pair of peaks about as strong either side of a lower middle: the arms of one family of letters
    // (V A W), straightened one side at a time; the print's slant lies between them
    // (both measured over the lowest point between them)
    const auto low = [&](int a, int b) {
        return *std::min_element(sm.begin() + std::min(a, b), sm.begin() + std::max(a, b) + 1);
    };
    const Peak* mate = nullptr;
    double mateRise = 0;
    for (const Peak& q : peaks) {
        if (q.i == top.i || std::abs(deg(q.i) - deg(top.i)) < 8.0) continue;
        const double floor = low(q.i, top.i), rise = sm[size_t(q.i)] - floor;
        if (rise >= 0.5 * (sm[size_t(top.i)] - floor) && rise > mateRise) { mate = &q; mateRise = rise; }
    }
    if (mate) {
        const int mid = (top.i + mate->i) / 2;
        const double c = deg(mid);
        if (std::abs(c) <= maxDeg) {
            strength = mateRise / sm[size_t(top.i)];
            sure = strength >= kClear;
            return sure ? c : 0.0;
        }
    }
    // one strongest peak inside the limit; past it, it is a family of diagonal strokes (/ Z X lean
    // about 20 to 35 degrees), never taken for a slant at the limit
    const double a = deg(top.i);
    if (std::abs(a) > maxDeg) return 0.0;
    strength = top.prom / sm[size_t(top.i)];
    sure = strength >= kClear;
    return sure ? a : 0.0;
}

float percentile(std::vector<float> v, double q)
{
    if (v.empty()) return 0;
    const size_t k = std::min(v.size() - 1, size_t(q * double(v.size() - 1) + 0.5));
    std::nth_element(v.begin(), v.begin() + std::ptrdiff_t(k), v.end());
    return v[k];
}

Mask pickPolarity(const cv::Mat& small, float ch, int polarity, int joinR, bool& light)
{
    if (polarity == 1) { light = false; return textMask(small, ch, false, joinR); }
    if (polarity == 2) { light = true; return textMask(small, ch, true, joinR); }
    Mask a = textMask(small, ch, true, joinR), b = textMask(small, ch, false, joinR);
    light = a.charLike >= b.charLike;
    return light ? a : b;
}

} // namespace

float estimateCharHeight(const cv::Mat& gray, const FindParams& p)
{
    // at each trial scale, the letters: parts of one character's size with a neighbour of about their
    // size beside them, as letters of a line stand, and none on top of them (the dots of dot print,
    // and specks of a textured ground, as large as a letter at the small trial scales, crowd all
    // round). Dot print the job did not name is also tried joined, and embossed print as relief.
    // Specks of noise and texture pass as letters at the finest scales, in numbers, but only there
    // and only as a small share of the parts of a letter's size; letters stand out at every scale
    // near their own. So each scale's letters score their number times their share, and a height
    // wins with the scores of the other scales that found about the same height; it is the median of
    // its letters (a few letters, too few to pair: the scale with the most parts of a letter's size)
    const cv::Mat roi = gray(p.roi);
    struct Found { int scale; float h, score; };
    std::vector<Found> found;
    float lonelyH = 0;
    int lonelyN = 0;
    const float trials[] = {12.f, 18.f, 26.f, 38.f, 55.f, 80.f, 115.f};
    for (int ti = 0; ti < 7; ++ti) {
        const float h = trials[ti];
        const double s = std::min(1.0, double(kSmallCharH) / h);
        cv::Mat small;
        cv::resize(roi, small, {}, s, s, cv::INTER_AREA);
        bool light = false;
        const float chs = float(h * s);
        const int joinR = joinRadius(p, chs, s, p.print == 2);
        // joined dots, and both polarities of relief, make letters of specks too: they count two thirds
        std::vector<std::pair<Mask, float>> ms;
        ms.emplace_back(pickPolarity(small, chs, p.polarity, joinR, light), 1.f);
        if (joinR == 0 && ms[0].first.comps.size() >= 15 && ms[0].first.charLike * 3 <= int(ms[0].first.comps.size()))
            ms.emplace_back(pickPolarity(small, chs, p.polarity, std::max(1, int(std::lround(0.15f * chs))), light), 2.f / 3);
        if (p.polarity == 0 && p.print != 2) ms.emplace_back(reliefMask(small, chs), 2.f / 3);
        for (const auto& [m, weight] : ms) {
            std::vector<const Comp*> like;
            for (const Comp& c : m.comps)
                if (c.box.height >= 0.45f * chs && c.box.height <= 1.6f * chs && c.box.width <= 1.6f * chs) like.push_back(&c);
            if (int(like.size()) > lonelyN) {
                std::vector<float> lh;
                for (const Comp* c : like) lh.push_back(float(c->box.height));
                lonelyN = int(like.size());
                lonelyH = float(percentile(lh, 0.75) / s);
            }
            std::vector<float> hs;
            for (const Comp* a : like) {
                bool beside = false, onTop = false;
                for (const Comp* b : like) {
                    if (a == b) continue;
                    const float ha = float(a->box.height), hb = float(b->box.height), hm = std::max(ha, hb);
                    if (hb < 0.6f * ha || hb > 1.67f * ha) continue;
                    const float gx = float(std::max(a->box.x, b->box.x) - std::min(a->box.x + a->box.width, b->box.x + b->box.width));
                    const float gy = float(std::max(a->box.y, b->box.y) - std::min(a->box.y + a->box.height, b->box.y + b->box.height));
                    if (gx < 0 && gy < 0.3f * hm) { onTop = true; break; }
                    if (gx <= 1.2f * hm && std::abs(a->c.y - b->c.y) <= 0.4f * hm) beside = true;
                }
                if (beside && !onTop) hs.push_back(float(a->box.height));
            }
            if (hs.size() < 3) continue;
            const float n = float(hs.size());
            found.push_back({ti, float(percentile(hs, 0.5) / s), weight * n * n / float(like.size())});
        }
    }
    float bestH = 0, best = -1;
    for (const Found& f : found) {
        float near[7] = {0};
        for (const Found& g : found)
            if (g.scale != f.scale && std::abs(g.h - f.h) <= 0.25f * f.h) near[g.scale] = std::max(near[g.scale], g.score);
        float sc = f.score;
        for (float v : near) sc += v;
        if (sc > best) { best = sc; bestH = f.h; }
    }
    if (best < 0) return lonelyN >= 3 ? lonelyH : 0.f;
    // dot print (the job says so): a letter measures from the top edge of its top dots to the bottom
    // edge of its bottom ones (joined, or blurred together at the coarser scales), a dot more than the
    // capital height as the printer's matrix gives it (top to bottom dot centre, as taught). On the dot
    // pitch the capital spans a whole number of pitches (4, 6 or 8 for 5, 7 and 9 dot fonts). Not
    // under auto: dot print cannot be told from embossed or textured print here
    if (p.print == 2) {
        FindParams q = p;
        q.charHeight = bestH;
        float pitch = 0;
        std::string err;
        const float dot = p.dotSize > 0 ? p.dotSize : measureDots(gray, q, pitch, err);
        if (dot > 0 && dot < 0.4f * bestH) {
            bestH -= dot;
            const float rows = pitch > 0 ? std::round(bestH / pitch) : 0.f;
            if (rows >= 3 && rows <= 9 && std::abs(bestH - rows * pitch) <= 0.3f * pitch) bestH = rows * pitch;
        }
    }
    return bestH;
}

float measureDots(const cv::Mat& gray, const FindParams& p, float& pitch, std::string& err)
{
    pitch = 0;
    if (p.charHeight <= 0) { err = "no text found in the region"; return 0; }
    // a copy where a capital is at most 60 px high: the dots of a 7-dot font stay apart and several px wide
    const double s = std::min(1.0, 60.0 / p.charHeight);
    cv::Mat small;
    cv::resize(gray(p.roi), small, {}, s, s, cv::INTER_AREA);
    const float ch = float(p.charHeight * s);
    const int k = std::max(3, int(std::lround(ch * 1.4f)) | 1);
    std::vector<float> bestD;
    std::vector<cv::Point2f> bestC;
    int bestParts = 0;
    for (const bool light : {false, true}) {
        if ((light && p.polarity == 1) || (!light && p.polarity == 2)) continue;
        cv::Mat th, bin, labels, stats, cents;
        cv::morphologyEx(small, th, light ? cv::MORPH_TOPHAT : cv::MORPH_BLACKHAT,
                         cv::getStructuringElement(cv::MORPH_RECT, {k, k}));
        cv::threshold(th, bin, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
        const int n = cv::connectedComponentsWithStats(bin, labels, stats, cents, 8, CV_32S);
        std::vector<float> d;
        std::vector<cv::Point2f> c;
        int parts = 0;
        for (int i = 1; i < n; ++i) {
            const int w = stats.at<int>(i, cv::CC_STAT_WIDTH), h = stats.at<int>(i, cv::CC_STAT_HEIGHT);
            const int area = stats.at<int>(i, cv::CC_STAT_AREA);
            if (area < 3) continue;                                   // noise specks
            ++parts;
            // one dot: small against the letter, about round and filled
            if (w > 0.3f * ch || h > 0.3f * ch || std::max(w, h) > 2 * std::min(w, h) || area < 0.5 * w * h) continue;
            d.push_back(float(2.0 * std::sqrt(area / CV_PI)));
            c.emplace_back(float(cents.at<double>(i, 0)), float(cents.at<double>(i, 1)));
        }
        if (d.size() > bestD.size() || (bestD.empty() && parts > bestParts)) { bestD = std::move(d); bestC = std::move(c); bestParts = parts; }
    }
    // dot print: most marks are single dots (solid strokes give a few large parts and stray specks)
    if (bestD.size() < 20 || double(bestD.size()) < 0.6 * bestParts) {
        err = "no dot print found in the region (" + std::to_string(bestD.size()) + " dots in " + std::to_string(bestParts) + " marks)";
        return 0;
    }
    if (bestC.size() > 3000) bestC.resize(3000);
    std::vector<float> nn;
    for (size_t i = 0; i < bestC.size(); ++i) {
        float m = 1e9f;
        for (size_t j = 0; j < bestC.size(); ++j)
            if (j != i) m = std::min(m, float(cv::norm(bestC[i] - bestC[j])));
        nn.push_back(m);
    }
    pitch = float(percentile(nn, 0.5) / s);
    return float(percentile(bestD, 0.5) / s);
}

namespace {

// A candidate line from one polarity's mask, in that mask's rotated frame.
struct Cand {
    int mask = 0;                       // index into the masks
    double angle = 0;                   // degrees
    std::vector<cv::Point2f> pts;       // stroke pixels, rotated frame
    cv::Rect box;                       // small-image box (for overlap between polarities)
    float x0 = 0, x1 = 0, ymid = 0;
    float score = 0;                    // character-sized parts
    bool cutShort = false;              // Line::cutShort
    std::vector<std::pair<float, float>> pieces;   // a line the layout joined: each piece's ends (x, this frame)
};

// True when the box holds light marks on a darker ground: the bright tail is longer than the dark one.
bool lightMarks(const cv::Mat& small, const cv::Rect& box, float ch)
{
    // widened by a letter height so the ground around the marks takes part, not only the strokes
    const int m = int(std::lround(ch));
    const cv::Rect wide(box.x - m, box.y - m, box.width + 2 * m, box.height + 2 * m);
    const cv::Mat r = small(wide & cv::Rect(0, 0, small.cols, small.rows));
    if (r.empty()) return false;
    const int p2 = histPercentile(r, 0.02), p50 = histPercentile(r, 0.5), p98 = histPercentile(r, 0.98);
    return p98 - p50 > p50 - p2;
}

// One part of a mask in the frame turned by the region's angle.
struct Part {
    float x0 = 0, x1 = 0, y0 = 0, y1 = 0;
    float cx = 0, cy = 0, h = 0;
    float letters = 0;          // letters it holds (0: punctuation, a dot, a speck)
    bool edge = false;          // touches the region's edge
};

// A chain of letters: one line of text, followed letter by letter (a line bowed on a bottle, letters
// spaced wide, a line at its own angle all stay one chain).
struct Chain {
    std::vector<int> seeds;     // letters, left to right
    std::vector<int> all;       // letters and the punctuation in their band
    float x0 = 0, x1 = 0, hmed = 0;
    float gapMed = -1;          // median gap between neighbouring letters (-1: one letter)
    float score = 0;
    std::vector<float> joins;   // where the layout joined two pieces (the middle of the gap between them)
};

// The band a chain's letters fill at x: from the highest top to the lowest bottom of the (up to)
// three letters nearest to x, carried along the slope of the six nearest (a bowed line; with fewer
// than four letters, small letters and capitals would fake a slope). Capitals, small letters and
// descenders all lie in it; a letter of the line above or below does not (lines are at least 1.6
// capitals apart).
std::pair<float, float> bandAt(const Chain& c, const std::vector<Part>& parts, float x)
{
    std::vector<std::pair<float, int>> near;
    for (int i : c.seeds) near.push_back({std::abs(parts[size_t(i)].cx - x), i});
    const size_t k6 = std::min<size_t>(6, near.size()), k = std::min<size_t>(3, near.size());
    std::partial_sort(near.begin(), near.begin() + std::ptrdiff_t(k6), near.end());
    double slope = 0;
    if (k6 >= 4) {
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (size_t j = 0; j < k6; ++j) {
            const Part& p = parts[size_t(near[j].second)];
            sx += p.cx; sy += p.cy; sxx += double(p.cx) * p.cx; sxy += double(p.cx) * p.cy;
        }
        const double n = double(k6), d = n * sxx - sx * sx;
        if (d > 1e-6) slope = std::clamp((n * sxy - sx * sy) / d, -0.15, 0.15);
    }
    float t = 1e9f, b = -1e9f;
    for (size_t j = 0; j < k; ++j) {
        const Part& p = parts[size_t(near[j].second)];
        const float dy = float(slope * (x - p.cx));
        t = std::min(t, p.y0 + dy);
        b = std::max(b, p.y1 + dy);
    }
    return {t, b};
}

// Share of part p's height inside the band.
float inBand(const Part& p, std::pair<float, float> band)
{
    return std::max(0.f, std::min(p.y1, band.second) - std::max(p.y0, band.first) + 1) / p.h;
}

// The widest gap between letters that still joins them into one line: a word gap after a stop of the
// chain's own letter spacing (with tracking t, letters stand t apart and words about 3t plus a space
// and a stop: wide-tracked print has wide word gaps), at least 2.2 capitals, at most 4.5
float joinGap(const Chain& c, float ch)
{
    return c.gapMed < 0 ? 2.2f * ch : std::clamp(0.8f * ch + 3.5f * c.gapMed, 2.2f * ch, 4.5f * ch);
}

std::vector<Cand> candidates(const cv::Mat& small, const Mask& m, int mi, bool light, float ch, double maxDeg,
                             const Layout* layout, std::chrono::steady_clock::time_point deadline)
{
    std::vector<Cand> out;
    const auto late = [deadline] { return std::chrono::steady_clock::now() > deadline; };
    if (m.comps.empty()) return out;
    // pixel lists per component
    std::vector<std::vector<cv::Point2f>> compPts(m.comps.size());
    {
        std::unordered_map<int, int> pos;
        for (size_t i = 0; i < m.comps.size(); ++i) pos[m.comps[i].label] = int(i);
        for (int y = 0; y < m.labels.rows; ++y) {
            const int* l = m.labels.ptr<int>(y);
            const uint8_t* b = m.bin.ptr<uint8_t>(y);
            for (int x = 0; x < m.labels.cols; ++x) {
                if (!b[x]) continue;
                auto it = pos.find(l[x]);
                if (it != pos.end()) compPts[size_t(it->second)].emplace_back(float(x), float(y));
            }
        }
    }
    // letters a part holds: one, or (joined dots, touching letters) about its width in letters
    auto letters = [&](const Comp& c) -> float {
        if (c.box.height < 0.45f * ch || c.box.height > 1.6f * ch * (c.box.width > 4 * ch ? 2.f : 1.f)) return 0.f;
        return std::max(1.f, std::floor(c.box.width / (0.8f * ch)));
    };
    // the region's angle from the letters' strokes (specks of a textured ground blur the profile), or
    // from every stroke when there are few letters
    std::vector<cv::Point2f> pts;
    {
        int nl = 0, total = 0;
        for (const Comp& c : m.comps) if (letters(c) > 0) { ++nl; total += c.area; }
        const bool onlyLetters = nl >= 3;
        if (!onlyLetters) total = cv::countNonZero(m.bin);
        const int step = std::max(1, total / 20000);
        int k = 0;
        for (size_t i = 0; i < m.comps.size(); ++i) {
            if (onlyLetters && letters(m.comps[i]) <= 0) continue;
            for (const auto& q : compPts[i]) if ((k++ % step) == 0) pts.push_back(q);
        }
    }
    const double angle = maxDeg > 0 ? bestAngle(pts, -maxDeg, maxDeg, 1.0, 0.2) : 0.0;
    double a = angle * kPi / 180.0, ca = std::cos(a), sa = std::sin(a);
    auto rotX = [&](cv::Point2f q) { return float(q.x * ca + q.y * sa); };
    auto rotY = [&](cv::Point2f q) { return float(-q.x * sa + q.y * ca); };

    std::vector<Part> parts(m.comps.size());
    std::vector<int> seeds, rest;
    for (size_t i = 0; i < m.comps.size(); ++i) {
        Part& p = parts[i];
        p.x0 = p.y0 = 1e9f; p.x1 = p.y1 = -1e9f;
        for (const auto& q : compPts[i]) {
            const float x = rotX(q), y = rotY(q);
            p.x0 = std::min(p.x0, x); p.x1 = std::max(p.x1, x);
            p.y0 = std::min(p.y0, y); p.y1 = std::max(p.y1, y);
        }
        p.cx = 0.5f * (p.x0 + p.x1);
        p.cy = 0.5f * (p.y0 + p.y1);
        p.h = p.y1 - p.y0 + 1;
        const cv::Rect& b = m.comps[i].box;
        p.edge = b.x <= 0 || b.y <= 0 || b.x + b.width >= small.cols || b.y + b.height >= small.rows;
        p.letters = letters(m.comps[i]);
        (p.letters > 0 ? seeds : rest).push_back(int(i));
    }
    std::sort(seeds.begin(), seeds.end(), [&](int i, int j) { return parts[size_t(i)].x0 < parts[size_t(j)].x0; });

    // follow each line letter by letter, left to right: a letter joins the line whose right end it is
    // close to, at the height the line's last letters lead to, and of about their size
    std::vector<Chain> chains;
    for (int si : seeds) {
        if (late()) return {};
        const Part& s = parts[size_t(si)];
        int best = -1;
        float bestCost = 1e9f;
        for (size_t ci = 0; ci < chains.size(); ++ci) {
            const Chain& c = chains[ci];
            const float gap = s.x0 - c.x1;
            if (gap < -1.0f * ch || gap > joinGap(c, ch)) continue;
            if (s.h < 0.5f * c.hmed || s.h > 2.0f * c.hmed) continue;
            const auto band = bandAt(c, parts, s.cx);
            if (inBand(s, band) < 0.6f) continue;
            const float dy = std::abs(s.cy - 0.5f * (band.first + band.second));
            const float cost = std::max(0.f, gap) / ch + 2.f * dy / ch;
            if (cost < bestCost) { bestCost = cost; best = int(ci); }
        }
        if (best < 0) { chains.emplace_back(); best = int(chains.size()) - 1; chains.back().x0 = s.x0; }
        Chain& c = chains[size_t(best)];
        if (!c.seeds.empty()) {
            // running median of the letter gaps, from the gaps so far
            std::vector<float> g;
            for (size_t j = 1; j < c.seeds.size(); ++j)
                g.push_back(std::max(0.f, parts[size_t(c.seeds[j])].x0 - parts[size_t(c.seeds[j - 1])].x1));
            g.push_back(std::max(0.f, s.x0 - c.x1));
            c.gapMed = percentile(g, 0.5);
        }
        c.seeds.push_back(si);
        c.x0 = std::min(c.x0, s.x0);
        c.x1 = std::max(c.x1, s.x1);
        std::vector<float> hs;
        for (int j : c.seeds) hs.push_back(parts[size_t(j)].h);
        c.hmed = percentile(hs, 0.5);
    }
    for (Chain& c : chains) {
        c.all = c.seeds;
        for (int j : c.seeds) c.score += parts[size_t(j)].letters;
    }
    // punctuation, dots and broken strokes go to the line whose band holds them
    for (int ri : rest) {
        if (late()) return {};
        const Part& p = parts[size_t(ri)];
        int best = -1;
        float bestD = 1e9f;
        for (size_t ci = 0; ci < chains.size(); ++ci) {
            const Chain& c = chains[ci];
            if (p.cx < c.x0 - 0.6f * ch || p.cx > c.x1 + 0.6f * ch) continue;
            const auto band = bandAt(c, parts, p.cx);
            // a comma or a descender's tail hangs below the letters
            if (p.y0 < band.first - 0.15f * c.hmed || p.y1 > band.second + 0.35f * c.hmed) continue;
            const float d = std::abs(p.cy - 0.5f * (band.first + band.second));
            if (d < bestD) { bestD = d; best = int(ci); }
        }
        if (best >= 0) chains[size_t(best)].all.push_back(ri);
    }
    // join pieces of one line that a stop or a colon before a word gap kept apart: measured from the
    // punctuation, the gap is a word gap
    for (Chain& c : chains)
        for (int j : c.all) { c.x0 = std::min(c.x0, parts[size_t(j)].x0); c.x1 = std::max(c.x1, parts[size_t(j)].x1); }
    for (bool merged = true; merged;) {
        merged = false;
        for (size_t i = 0; i < chains.size() && !merged; ++i) {
            if (late()) return {};
            for (size_t j = 0; j < chains.size() && !merged; ++j) {
                Chain& A = chains[i];
                Chain& B = chains[j];
                if (i == j || A.seeds.empty() || B.seeds.empty()) continue;
                const float gap = B.x0 - A.x1;
                if (gap < 0 || gap > std::max(joinGap(A, ch), joinGap(B, ch))) continue;
                const Part& b0 = parts[size_t(B.seeds.front())];
                const Part& aN = parts[size_t(A.seeds.back())];
                if (b0.h < 0.5f * A.hmed || b0.h > 2.0f * A.hmed) continue;
                if (inBand(b0, bandAt(A, parts, b0.cx)) < 0.6f || inBand(aN, bandAt(B, parts, aN.cx)) < 0.6f) continue;
                A.seeds.insert(A.seeds.end(), B.seeds.begin(), B.seeds.end());
                A.all.insert(A.all.end(), B.all.begin(), B.all.end());
                A.x1 = B.x1;
                A.score += B.score;
                if (B.gapMed >= 0) A.gapMed = A.gapMed < 0 ? B.gapMed : 0.5f * (A.gapMed + B.gapMed);
                std::vector<float> hs;
                for (int k : A.seeds) hs.push_back(parts[size_t(k)].h);
                A.hmed = percentile(hs, 0.5);
                B = Chain();
                merged = true;
            }
        }
    }
    // the job's layout (FindParams::layout) lists fewer lines than this mask shows: pieces of one line
    // that a gap wider than any word gap above split (a wide-spaced group, a missing character, a
    // smudge wiped off) are joined, side by side in one band, the narrowest gap first, until the lines
    // are as many as the layout's or no pair is left. The joined line may hold no more letters than the
    // layout's longest line (letters counted as above: joined dots count a little over, punctuation
    // not at all, so with a margin). Only text of the mask's own polarity counts and joins
    if (layout && !layout->lines.empty()) {
        int longest = 0;
        for (const auto& l : layout->lines) longest = std::max(longest, l.maxChars());
        const float most = 1.3f * float(longest) + 1.f;
        std::vector<int> own(chains.size(), -1);   // 1 = the chain's print is the mask's polarity
        auto isOwn = [&](size_t i) {
            if (own[i] < 0) {
                cv::Rect b;
                for (int j : chains[i].all) b = b.area() ? (b | m.comps[size_t(j)].box) : m.comps[size_t(j)].box;
                own[i] = lightMarks(small, b, ch) == light ? 1 : 0;
            }
            return own[i] == 1;
        };
        auto text = [&](size_t i) { return chains[i].score >= 2 && isOwn(i); };
        for (;;) {
            int lines = 0;
            for (size_t i = 0; i < chains.size(); ++i) lines += text(i) ? 1 : 0;
            if (lines <= int(layout->lines.size())) break;
            size_t bi = 0, bj = 0;
            float bestGap = 8.f * ch;   // farther apart: not one line
            for (size_t i = 0; i < chains.size(); ++i)
                for (size_t j = 0; j < chains.size(); ++j) {
                    if (i == j || !text(i) || !text(j)) continue;
                    const Chain& A = chains[i];
                    const Chain& B = chains[j];
                    const float gap = B.x0 - A.x1;
                    if (gap <= 0 || gap > bestGap || A.score + B.score > most) continue;
                    const Part& b0 = parts[size_t(B.seeds.front())];
                    const Part& aN = parts[size_t(A.seeds.back())];
                    if (b0.h < 0.5f * A.hmed || b0.h > 2.0f * A.hmed) continue;
                    if (inBand(b0, bandAt(A, parts, b0.cx)) < 0.6f || inBand(aN, bandAt(B, parts, aN.cx)) < 0.6f) continue;
                    bestGap = gap;
                    bi = i;
                    bj = j;
                }
            if (bi == bj) break;
            Chain& A = chains[bi];
            Chain& B = chains[bj];
            A.joins.push_back(0.5f * (A.x1 + B.x0));
            A.joins.insert(A.joins.end(), B.joins.begin(), B.joins.end());
            A.seeds.insert(A.seeds.end(), B.seeds.begin(), B.seeds.end());
            A.all.insert(A.all.end(), B.all.begin(), B.all.end());
            A.x1 = B.x1;
            A.score += B.score;
            if (B.gapMed >= 0) A.gapMed = A.gapMed < 0 ? B.gapMed : 0.5f * (A.gapMed + B.gapMed);
            std::vector<float> hs;
            for (int k : A.seeds) hs.push_back(parts[size_t(k)].h);
            A.hmed = percentile(hs, 0.5);
            B = Chain();
            if (std::getenv("OCR_DEBUG")) std::fprintf(stderr, "layout: joined two pieces of one line, %.1f capitals apart\n", double(bestGap / ch));
        }
    }
    // parts next to a line's ends in its band join it, one after another: a faint or broken first or
    // last letter (too small to count as a letter), a lone letter, punctuation, a dash
    {
        std::vector<int> owner(parts.size(), -1);
        for (size_t ci = 0; ci < chains.size(); ++ci)
            if (chains[ci].score >= 2) for (int j : chains[ci].all) owner[size_t(j)] = int(ci);
        for (bool grew = true; grew;) {
            grew = false;
            for (size_t ci = 0; ci < chains.size(); ++ci) {
                if (late()) return {};
                Chain& c = chains[ci];
                if (c.score < 2) continue;
                for (size_t j = 0; j < parts.size(); ++j) {
                    if (owner[j] >= 0) continue;
                    const Part& p = parts[j];
                    if (p.h > 2.0f * c.hmed) continue;
                    const float gap = p.x0 > c.x1 ? p.x0 - c.x1 : c.x0 - p.x1;
                    if (gap > 1.5f * ch || (p.x1 >= c.x0 && p.x0 <= c.x1)) continue;
                    if (inBand(p, bandAt(c, parts, p.cx)) < 0.6f) continue;
                    c.all.push_back(int(j));
                    c.x0 = std::min(c.x0, p.x0);
                    c.x1 = std::max(c.x1, p.x1);
                    owner[j] = int(ci);
                    grew = true;
                }
            }
        }
    }
    if (std::getenv("OCR_DEBUG")) {
        std::fprintf(stderr, "mask %d light=%d comps %zu charLike %d angle %.1f chains:", mi, int(light), m.comps.size(), m.charLike, angle);
        for (const auto& c : chains) if (!c.seeds.empty()) std::fprintf(stderr, " [x %.0f..%.0f y %.0f n %zu s %.0f]", c.x0, c.x1, bandAt(c, parts, c.x0).first, c.seeds.size(), c.score);
        std::fprintf(stderr, "\n");
    }

    for (size_t ci = 0; ci < chains.size(); ++ci) {
        const Chain& ch0 = chains[ci];
        // drop specks: a line needs two letters (one wide part of joined letters counts as its letters)
        if (ch0.score < 2) continue;
        Cand c;
        c.mask = mi;
        // the line's own angle (a line printed apart from the others, or bowed): sharpest row profile of
        // its strokes near the region's angle, when clearly sharper than at the region's angle (a short
        // line's profile is ragged: it keeps the angle all the region's lines agree on)
        c.angle = angle;
        if (maxDeg > 0 && ch0.seeds.size() >= 4) {
            std::vector<cv::Point2f> lp;
            for (int j : ch0.seeds) lp.insert(lp.end(), compPts[size_t(j)].begin(), compPts[size_t(j)].end());
            const double own = bestAngle(lp, std::max(-maxDeg, angle - 4.0), std::min(maxDeg, angle + 4.0), 0.5, 0.1);
            auto rowSharp = [&](double deg) {
                const double r = deg * kPi / 180.0, cr = std::cos(r), sr = std::sin(r);
                std::vector<float> v(lp.size());
                for (size_t q = 0; q < lp.size(); ++q) v[q] = float(-lp[q].x * sr + lp[q].y * cr);
                return sharpness(v);
            };
            if (rowSharp(own) > 1.1 * rowSharp(angle)) c.angle = own;
        }
        const double la = c.angle * kPi / 180.0, lc = std::cos(la), ls = std::sin(la);
        c.x0 = 1e9f; c.x1 = -1e9f;
        cv::Rect box;
        float y0 = 1e9f, y1 = -1e9f;
        // a line the layout joined: the ends of each piece (Line::pieces)
        if (!ch0.joins.empty()) c.pieces.assign(ch0.joins.size() + 1, {1e9f, -1e9f});
        for (int j : ch0.all) {
            const Comp& cp = m.comps[size_t(j)];
            box = box.area() ? (box | cp.box) : cp.box;
            const size_t piece = size_t(std::count_if(ch0.joins.begin(), ch0.joins.end(), [&](float x) { return x < parts[size_t(j)].cx; }));
            for (const auto& q : compPts[size_t(j)]) {
                const cv::Point2f r(float(q.x * lc + q.y * ls), float(-q.x * ls + q.y * lc));
                if (!c.pieces.empty()) {
                    c.pieces[piece].first = std::min(c.pieces[piece].first, r.x);
                    c.pieces[piece].second = std::max(c.pieces[piece].second, r.x);
                }
                c.pts.push_back(r);
                c.x0 = std::min(c.x0, r.x);
                c.x1 = std::max(c.x1, r.x);
                y0 = std::min(y0, r.y);
                y1 = std::max(y1, r.y);
            }
        }
        c.box = box;
        c.ymid = 0.5f * (y0 + y1);
        c.score = ch0.score;
        // the gaps between light strokes look like dark characters (and the other way round):
        // keep a line only from the mask whose polarity the line itself shows
        if (lightMarks(small, c.box, ch) != light) continue;
        // cut short: a part of the line touches the region's edge (a letter, or what is left of one the
        // edge cuts: too small to count as a letter, it joined the line's end), or a piece of text of two
        // letters or more lies in line with it just past a gap too wide to join (a split line: each part
        // would read as a whole line)
        for (int j : ch0.all) if (parts[size_t(j)].edge) c.cutShort = true;
        const float reach = joinGap(ch0, ch) + 1.5f * ch;
        for (size_t oi = 0; oi < chains.size() && !c.cutShort; ++oi) {
            if (oi == ci || chains[oi].score < 2) continue;
            for (int j : chains[oi].seeds) {
                const Part& p = parts[size_t(j)];
                const float gap = p.x0 > ch0.x1 ? p.x0 - ch0.x1 : ch0.x0 - p.x1;
                if (gap <= 0 || gap > reach) continue;
                if (p.h < 0.6f * ch0.hmed || p.h > 1.6f * ch0.hmed) continue;
                if (inBand(p, bandAt(ch0, parts, p.cx)) < 0.6f) continue;
                c.cutShort = true;
                break;
            }
        }
        out.push_back(std::move(c));
    }
    return out;
}

// Stroke response (top-hat of light marks, black-hat of dark ones) as textMask makes it, and the mean
// level of the ground in it (most of a region is ground).
cv::Mat strokeResponse(const cv::Mat& small, float ch, bool light, float& noise)
{
    const int k = std::max(3, int(std::lround(ch * 1.4f)) | 1);
    cv::Mat th;
    cv::morphologyEx(small, th, light ? cv::MORPH_TOPHAT : cv::MORPH_BLACKHAT,
                     cv::getStructuringElement(cv::MORPH_RECT, {k, k}));
    noise = float(histPercentile(th, 0.5)) + 1.f;
    return th;
}

// Ink just past one end of a line that the stroke mask missed (a faint first or last letter): in the
// line's band, a column stretch of a letter's width beyond the end holding a third of the stroke
// response of the line's letter columns, and twice the ground's. Not ink of the line: a region border
// (the label's bright margin beside the dark print box), found by its ground. Within a capital from
// where it starts, 80% of the band over a letter's width, and again over the next letter's width, lies
// beyond the line's own ink level; text never fills that much (even bold strokes leave their ground
// showing between them). It must start as far from the end as the line's usual gap between letters
// plus 0.3 capitals (0.5 capitals at least), so that a next letter at the line's spacing would have
// shown before it; nothing past a border is the line's. Frame: the line's turned frame (angle a);
// [x0, x1] its ends, [y0, y1] its band; th the stroke response, gray the image it was made from, light
// the polarity.
bool inkPastEnds(const cv::Mat& th, const cv::Mat& gray, bool light, float noise, double a, float x0, float x1,
                 float y0, float y1, float ch)
{
    const double ca = std::cos(a), sa = std::sin(a);
    const int ny = std::max(1, int(y1 - y0 + 1));
    auto px = [&](float x, float y) { return cv::Point(int(std::lround(x * ca - y * sa)), int(std::lround(x * sa + y * ca))); };
    auto inside = [&](cv::Point p) { return p.x >= 0 && p.y >= 0 && p.x < th.cols && p.y < th.rows; };
    auto column = [&](float x, bool& ok) {
        double sum = 0;
        int in = 0;
        for (int j = 0; j < ny; ++j) {
            const cv::Point p = px(x, y0 + float(j));
            if (!inside(p)) continue;
            sum += th.at<uint8_t>(p);
            ++in;
        }
        ok = in * 2 > ny;
        return float(sum);
    };
    // grey of the band over columns [xa, xb], signed (+ toward the print's side of the ground)
    auto bandGrey = [&](float xa, float xb, float q) {
        std::vector<float> g;
        for (float x = std::min(xa, xb); x <= std::max(xa, xb); x += 1.f)
            for (int j = 0; j < ny; ++j) {
                const cv::Point p = px(x, y0 + float(j));
                if (inside(p)) g.push_back(light ? float(gray.at<uint8_t>(p)) : -float(gray.at<uint8_t>(p)));
            }
        return g.empty() ? -1e9f : percentile(g, q);
    };
    std::vector<float> cols;
    bool ok = false;
    for (float x = x0; x <= x1; x += 1.f) {
        const float v = column(x, ok);
        if (ok) cols.push_back(v);
    }
    if (cols.size() < 4) return false;
    const float letter = percentile(cols, 0.75);
    const float bar = std::max(0.33f * letter, 2.f * noise * float(ny));
    const float ink = bandGrey(x0, x1, 0.95f);   // the line's ink level (its brightest strokes)
    // how far a border must be: the line's usual gap between letters (median run of clean columns
    // between inked ones) plus 0.3 capitals, 0.5 capitals at least
    std::vector<float> gaps;
    for (size_t i = 0, run = 0; i < cols.size(); ++i) {
        if (cols[i] <= bar) { ++run; continue; }
        if (run > 0 && run < i) gaps.push_back(float(run));
        run = 0;
    }
    const float clean = std::max(0.5f * ch, (gaps.empty() ? 0.f : percentile(gaps, 0.5)) + 0.3f * ch);
    // a letter's worth of columns: the mean over 0.3 capitals, from 0.25 to 1.3 capitals past the end
    const int w = std::max(2, int(std::lround(0.3f * ch)));
    for (const int dir : {-1, 1}) {
        const float e = dir < 0 ? x0 : x1;
        auto at = [&](float d) { return e + float(dir) * d; };
        std::vector<float> v;
        for (float d = 0.25f * ch; d <= 1.3f * ch; d += 1.f) {
            const float c = column(at(d), ok);
            v.push_back(ok ? c : -1.f);
        }
        for (size_t i = 0; i + size_t(w) <= v.size(); ++i) {
            double m = 0;
            bool all = true;
            for (size_t j = i; j < i + size_t(w); ++j) { if (v[j] < 0) { all = false; break; } m += v[j]; }
            if (!all || m / w <= bar) continue;
            const float d = 0.25f * ch + float(i);
            if (d >= clean) {
                // filled: 80% of a letter's width beyond the ink level, and so again a letter's width on
                auto filled = [&](float k) { return bandGrey(at(k), at(k + float(w - 1)), 0.2f) > ink; };
                bool border = false;
                for (float k = 0; k <= ch && !border; k += 1.f)
                    border = filled(d + k) && filled(d + k + float(w));
                if (border) break;   // a region border: nothing past it is the line's
            }
            return true;
        }
    }
    return false;
}

} // namespace

std::vector<Line> findLines(const cv::Mat& gray, const FindParams& p, bool dots)
{
    std::vector<Line> out;
    const float chFull = p.charHeight > 0 ? p.charHeight : 30.f;
    const double s = std::min(1.0, double(kSmallCharH) / chFull);
    const float ch = float(chFull * s);
    const cv::Mat roi = gray(p.roi);
    cv::Mat small;
    cv::resize(roi, small, {}, s, s, cv::INTER_AREA);
    const double maxDeg = std::clamp(double(p.maxSkewDeg), 0.0, 45.0);

    // both polarities unless the job fixed one: light laser text on a dark patch and dark label print
    // often share a region; where their lines overlap, the one with more character-sized parts wins
    std::vector<Mask> masks;
    std::vector<bool> lightOf;
    const int joinR = joinRadius(p, ch, s, dots), joinRx = joinRadiusAlong(p, ch, s, joinR);
    if (p.polarity != 1) { masks.push_back(textMask(small, ch, true, joinR, joinRx)); lightOf.push_back(true); }
    if (p.polarity != 2) { masks.push_back(textMask(small, ch, false, joinR, joinRx)); lightOf.push_back(false); }
    // lines are followed letter by letter: dot print not joined (the job did not say dot print) shows
    // no letters, only dots. Joined as dot print is, where that shows far more letters
    if (joinR == 0)
        for (size_t i = 0; i < masks.size(); ++i) {
            if (masks[i].comps.size() < 15 || masks[i].charLike * 3 > int(masks[i].comps.size())) continue;
            Mask j = textMask(small, ch, lightOf[i], std::max(1, int(std::lround(0.15f * ch))));
            if (2 * j.charLike > 3 * masks[i].charLike) masks[i] = std::move(j);
        }
    std::vector<Cand> cands;
    for (size_t i = 0; i < masks.size(); ++i) {
        auto c = candidates(small, masks[i], int(i), lightOf[i], ch, maxDeg, p.layout, p.deadline);
        for (auto& x : c) cands.push_back(std::move(x));
    }
    std::sort(cands.begin(), cands.end(), [](const Cand& a1, const Cand& b1) { return a1.score > b1.score; });
    std::vector<Cand> kept;
    for (auto& c : cands) {
        bool clash = false;
        for (const auto& k : kept) {
            if (k.mask == c.mask) continue;
            const double inter = double((k.box & c.box).area());
            if (inter > 0.3 * double(std::min(k.box.area(), c.box.area()))) { clash = true; break; }
        }
        if (!clash) kept.push_back(std::move(c));
    }
    std::sort(kept.begin(), kept.end(), [&](const Cand& a1, const Cand& b1) {
        const float ya = float(a1.box.y + 0.5 * a1.box.height), yb = float(b1.box.y + 0.5 * b1.box.height);
        if (std::abs(ya - yb) > 0.5f * ch) return ya < yb;
        return a1.box.x < b1.box.x;
    });
    if (int(kept.size()) > p.maxLines) {
        // keep the most text-like lines, still in reading order
        std::vector<size_t> idx(kept.size());
        std::iota(idx.begin(), idx.end(), size_t(0));
        std::stable_sort(idx.begin(), idx.end(), [&](size_t i, size_t j) { return kept[i].score > kept[j].score; });
        idx.resize(size_t(p.maxLines));
        std::sort(idx.begin(), idx.end());
        std::vector<Cand> top;
        for (size_t i : idx) top.push_back(std::move(kept[i]));
        kept.swap(top);
    }

    // read copy: a capital about kReadCharH px high (never enlarged)
    const double r = std::min(1.0, double(kReadCharH) / chFull);
    cv::Mat readImg;
    if (r < 0.999) cv::resize(roi, readImg, {}, r, r, cv::INTER_AREA);
    else readImg = roi;

    // each line's slant. A line whose own strokes cannot tell (mostly diagonal letters) takes the
    // slant of the line in the region whose strokes tell most clearly, or none; so does a line that
    // disagrees with it on far weaker evidence (its diagonals straightened inside the limit). Lines
    // about as clear as each other keep their own: two prints of different slant in one region
    std::vector<double> slants(kept.size(), 0.0), strength(kept.size(), 0.0);
    std::vector<bool> sure(kept.size(), false);
    int clearest = -1;
    for (size_t i = 0; i < kept.size(); ++i) {
        std::vector<float> ys(kept[i].pts.size());
        for (size_t k = 0; k < ys.size(); ++k) ys[k] = kept[i].pts[k].y;
        const float yc = 0.5f * (percentile(ys, 0.02) + percentile(ys, 0.98));
        bool ok = false;
        slants[i] = slantOf(kept[i].pts, std::min(maxDeg, 30.0), yc, ok, strength[i]);
        sure[i] = ok;
        if (ok && (clearest < 0 || strength[i] > strength[size_t(clearest)])) clearest = int(i);
    }
    if (clearest >= 0) {
        const size_t c = size_t(clearest);
        for (size_t i = 0; i < kept.size(); ++i)
            if (!sure[i] || (std::abs(slants[i] - slants[c]) > 4.0 && strength[i] < 0.75 * strength[c])) slants[i] = slants[c];
    }

    // stroke response of each polarity in use, for ink past the lines' ends
    cv::Mat resp[2];
    float respNoise[2] = {0, 0};
    for (const Cand& g : kept) {
        const size_t pi = lightOf[size_t(g.mask)] ? 0 : 1;
        if (resp[pi].empty()) resp[pi] = strokeResponse(small, ch, pi == 0, respNoise[pi]);
    }
    for (size_t gi = 0; gi < kept.size(); ++gi) {
        const Cand& g = kept[gi];
        const bool light = lightOf[size_t(g.mask)];
        const double a = g.angle * kPi / 180.0, ca = std::cos(a), sa = std::sin(a);
        const std::vector<cv::Point2f>& lp = g.pts;
        std::vector<float> ys(lp.size());
        for (size_t i = 0; i < lp.size(); ++i) ys[i] = lp[i].y;
        const float y0 = percentile(ys, 0.02), y1 = percentile(ys, 0.98);
        const float lh = std::max(y1 - y0 + 1.f, 0.6f * ch);
        const float yc = 0.5f * (y0 + y1);
        const double slant = slants[gi];
        const double t = std::tan(slant * kPi / 180.0);
        bool cutShort = g.cutShort;
        if (!cutShort) {
            float gx0 = 1e9f, gx1 = -1e9f;
            for (const auto& q : lp) { gx0 = std::min(gx0, q.x); gx1 = std::max(gx1, q.x); }
            const size_t pi = light ? 0 : 1;
            cutShort = inkPastEnds(resp[pi], small, light, respNoise[pi], a, gx0, gx1, y0, y1, ch);
        }
        float x0 = 1e9f, x1 = -1e9f;
        for (const auto& q : lp) {
            const float xs = float(q.x + (q.y - yc) * t);
            x0 = std::min(x0, xs);
            x1 = std::max(x1, xs);
        }
        const float mv = 0.18f * lh, mh = 0.6f * lh;   // wide ends: a faint last letter is often below the stroke threshold
        const double so = double(kLineH) / (lh + 2 * mv);      // small px -> line px
        int W = int(std::ceil((x1 - x0 + 2 * mh) * so));
        W = std::clamp((W + kStride - 1) / kStride * kStride, 16, 2048);

        // (u, v) -> sheared frame x'' = A0 + u/so, y' = B0 + v/so -> rotated frame x' = x'' - (y' - yc) t
        // -> small region x = x' cos - y' sin, y = x' sin + y' cos -> full image (/ s, + roi origin)
        const double A0 = x0 - mh, B0 = y0 - mv;
        const double pu = 1.0 / so, pv = -t / so, px0 = A0 - (B0 - yc) * t;
        const double qv = 1.0 / so, qy0 = B0;
        const double xu = ca * pu, xv = ca * pv - sa * qv, xc = ca * px0 - sa * qy0;
        const double yu = sa * pu, yv = sa * pv + ca * qv, yc0 = sa * px0 + ca * qy0;
        Line L;
        L.toImage = cv::Matx23d(xu / s, xv / s, p.roi.x + xc / s, yu / s, yv / s, p.roi.y + yc0 / s);
        const cv::Matx23d toRead(xu / s * r, xv / s * r, xc / s * r, yu / s * r, yv / s * r, yc0 / s * r);
        cv::Mat li;
        cv::warpAffine(readImg, li, toRead, {W, kLineH}, cv::INTER_LINEAR | cv::WARP_INVERSE_MAP, cv::BORDER_REPLICATE);
        // contrast stretch, text dark on light
        const int lo = histPercentile(li, 0.01), hi = histPercentile(li, 0.99);
        const double scale = 255.0 / std::max(8, hi - lo);
        li.convertTo(li, CV_8U, light ? -scale : scale, light ? hi * scale : -lo * scale);
        // inkjet (TIJ): strokes of fine grain. Now dark on light, an opening fills the light specks inside a
        // stroke and keeps its width; a strong join takes a larger one
        if (p.print == kPrintInkjet && !dots) {
            const int k = p.join >= 2 ? 5 : 3;
            cv::morphologyEx(li, li, cv::MORPH_OPEN, cv::getStructuringElement(cv::MORPH_CROSS, {k, k}));
        }
        L.image = li;
        L.rawLo = float(lo);
        L.rawHi = float(std::max(lo + 8, hi));
        const cv::Point2d uv[4] = {{0, 0}, {double(W), 0}, {double(W), double(kLineH)}, {0, double(kLineH)}};
        for (int i = 0; i < 4; ++i) {
            const cv::Vec2d X = L.toImage * cv::Vec3d(uv[i].x, uv[i].y, 1);
            L.corners[i] = {float(X[0]), float(X[1])};
        }
        L.angleDeg = float(g.angle);
        L.slantDeg = float(slant);
        L.charHeight = float(lh / s);
        L.lightOnDark = light;
        L.cutShort = cutShort;
        for (const auto& [a0, a1] : g.pieces)
            if (a1 >= a0) L.pieces.emplace_back(float((a0 - A0) * so), float((a1 - A0) * so));
        out.push_back(std::move(L));
    }
    return out;
}

} // namespace ocr
