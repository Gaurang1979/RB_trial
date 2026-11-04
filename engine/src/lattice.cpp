// lattice.cpp - inside-out Data Matrix reading, after Cognex's PowerGrid (US10956697).
//
// Readers find a Data Matrix by its L finder and clock tracks, which need a clear quiet zone. On
// liquor cap labels the printed border touches the code, the print bleeds and the lens blurs the
// case corners: the finder search fails although the module grid is plainly there. Here the grid
// comes from the texture instead:
//   1. module pitch and grid angle: the module edges repeat every module along both grid axes, so
//      the summed edge profile of the rotated patch has its strongest Fourier peak at the pitch
//      (searched within +-15% of the product's learned module size, as a trained HALCON model does);
//   2. the grid phase: sample points in the module centres give the widest spread of values;
//   3. every n x n window of the sampled lattice (4 rotations) is scored by its L finder and clock
//      tracks; the best few are refined (position, scale) and decoded from their module means;
//   4. when that fails, the warped grid: module-centre map, 5 x 5 seed grown 8 ways, polynomial
//      (wrinkled and curved labels; see "warped lattice" below).
// Reed-Solomon error correction rejects every wrong window, so this cannot invent a code.

#include "reader.hpp"
#include "zxformats.hpp"

#include <cstdio>
#include <cstdlib>

#include <ZXing/ReadBarcode.h>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cctype>
#include <array>
#include <chrono>
#include <cmath>
#include <tuple>

namespace cr {

namespace {

using Clock = std::chrono::steady_clock;

// as cv::getRotationMatrix2D (OpenCV 5 moved it out of imgproc): counter-clockwise, about c
cv::Mat rotationMatrix(cv::Point2f c, double angleDeg)
{
    const double a = std::cos(angleDeg * CV_PI / 180.0), b = std::sin(angleDeg * CV_PI / 180.0);
    cv::Mat m(2, 3, CV_64F);
    m.at<double>(0, 0) = a;
    m.at<double>(0, 1) = b;
    m.at<double>(0, 2) = (1 - a) * c.x - b * c.y;
    m.at<double>(1, 0) = -b;
    m.at<double>(1, 1) = a;
    m.at<double>(1, 2) = b * c.x + (1 - a) * c.y;
    return m;
}

cv::Mat rotated(const cv::Mat& img, double angleDeg)
{
    cv::Mat out;
    const cv::Mat R = rotationMatrix(cv::Point2f(img.cols / 2.f, img.rows / 2.f), angleDeg);
    cv::warpAffine(img, out, R, img.size(), cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    return out;
}

// strength and period of the module edges along x, with the pitch searched in [lo, hi] pixels
std::pair<double, double> edgePeriod(const cv::Mat& img32, double lo, double hi)
{
    cv::Mat gx;
    cv::Sobel(img32, gx, CV_32F, 1, 0, 3);
    gx = cv::abs(gx);
    cv::Mat prof;
    cv::reduce(gx, prof, 0, cv::REDUCE_SUM, CV_32F);
    const int n = prof.cols;
    if (n < 8) return {0, 0};
    const double mean = cv::mean(prof)[0];
    cv::Mat sig(1, n, CV_32F);
    for (int i = 0; i < n; ++i)
        sig.at<float>(0, i) = static_cast<float>((prof.at<float>(0, i) - mean) * (0.5 - 0.5 * std::cos(2 * CV_PI * i / (n - 1))));
    cv::Mat spec;
    cv::dft(sig, spec, cv::DFT_COMPLEX_OUTPUT);
    double total = 0, best = 0;
    int bi = -1;
    std::vector<double> mag(static_cast<size_t>(n / 2 + 1));
    for (int k = 0; k <= n / 2; ++k) {
        const cv::Vec2f c = spec.at<cv::Vec2f>(0, k);
        mag[static_cast<size_t>(k)] = std::hypot(c[0], c[1]);
        total += mag[static_cast<size_t>(k)];
        const double period = k > 0 ? static_cast<double>(n) / k : 1e9;
        if (period >= lo && period <= hi && mag[static_cast<size_t>(k)] > best) {
            best = mag[static_cast<size_t>(k)];
            bi = k;
        }
    }
    if (bi <= 0) return {0, 0};
    double d = 0; // parabolic refinement of the peak
    if (bi > 0 && bi < n / 2) {
        const double a = mag[static_cast<size_t>(bi - 1)], b = mag[static_cast<size_t>(bi)], c = mag[static_cast<size_t>(bi + 1)];
        d = 0.5 * (a - c) / (a - 2 * b + c + 1e-9);
    }
    return {best / (total + 1e-9), n / (bi + d)};
}

// grid angle (degrees, 0..90) and pitch where the module edges are most periodic along both axes
std::pair<double, double> pitchAndAngle(const cv::Mat& patch, double m)
{
    const double lo = 0.85 * m, hi = 1.15 * m;
    double bestScore = -1, bestAng = 0, bestPitch = m;
    auto tryAngle = [&](double ang) {
        const cv::Mat r = rotated(patch, ang);
        const auto [sx, px] = edgePeriod(r, lo, hi);
        const cv::Mat rt = r.t();
        const auto [sy, py] = edgePeriod(rt, lo, hi);
        if (sx + sy > bestScore && px > 0 && py > 0) {
            bestScore = sx + sy;
            bestAng = ang;
            bestPitch = (px + py) / 2;
        }
    };
    // the module edges run along the two grid axes: the gradient directions, folded to 0..90
    // degrees (4 x angle), point at the grid angle; then a few rotations around it decide
    cv::Mat gx, gy;
    cv::Sobel(patch, gx, CV_32F, 1, 0, 3);
    cv::Sobel(patch, gy, CV_32F, 0, 1, 3);
    double sc = 0, ss = 0;
    for (int y = 0; y < patch.rows; ++y)
        for (int x = 0; x < patch.cols; ++x) {
            const float a = gx.at<float>(y, x), b = gy.at<float>(y, x);
            const double mag = a * a + b * b;
            if (mag < 1) continue;
            const double t = 4 * std::atan2(b, a);
            sc += mag * std::cos(t);
            ss += mag * std::sin(t);
        }
    double a0 = std::atan2(ss, sc) / 4 * 180 / CV_PI; // -45..45
    // the image is rotated by -angle to align the grid: the rotation that makes edges axis-aligned
    a0 = std::fmod(a0 + 90.0, 90.0);
    for (double a = a0 - 1.5; a <= a0 + 1.501; a += 0.5) tryAngle(a);
    if (bestScore <= 0) { // flat texture: fall back to the full search
        for (int a = 0; a < 90; a += 2) tryAngle(a);
    }
    return {bestAng, bestPitch};
}

// means of the central `frac` of each module of an axis-aligned n x n grid at (x0, y0), pitch p
cv::Mat moduleMeansAxis(const cv::Mat& img32, double x0, double y0, double p, int n, double frac = 0.4)
{
    constexpr int sub = 3;
    cv::Mat mapX(1, n * n * sub * sub, CV_32F), mapY(1, n * n * sub * sub, CV_32F);
    int k = 0;
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x)
            for (int j = 0; j < sub; ++j)
                for (int i = 0; i < sub; ++i, ++k) {
                    mapX.at<float>(0, k) = static_cast<float>(x0 + (x + (1 - frac) / 2 + frac * (i + 0.5) / sub) * p);
                    mapY.at<float>(0, k) = static_cast<float>(y0 + (y + (1 - frac) / 2 + frac * (j + 0.5) / sub) * p);
                }
    cv::Mat vals;
    cv::remap(img32, vals, mapX, mapY, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    cv::Mat means(n, n, CV_32F);
    for (int i = 0; i < n * n; ++i) {
        float s = 0;
        for (int t = 0; t < sub * sub; ++t) s += vals.at<float>(0, i * sub * sub + t);
        means.at<float>(i / n, i % n) = s / (sub * sub);
    }
    return means;
}


// threshold variants of module means: Otsu and shifted, local, and balanced (bled print darkens)
std::vector<cv::Mat> thresholdVariants(const cv::Mat& v)
{
    cv::Mat v8, scratch, loc;
    cv::normalize(v, v8, 0, 255, cv::NORM_MINMAX, CV_8U);
    const double t = cv::threshold(v8, scratch, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
    cv::Mat vf;
    v8.convertTo(vf, CV_32F);
    cv::blur(vf, loc, cv::Size(5, 5));
    std::vector<cv::Mat> out;
    for (double d : {0.0, -12.0, 12.0}) out.push_back((vf < t + d) / 255);
    out.push_back((vf < loc) / 255);
    std::vector<float> s(vf.begin<float>(), vf.end<float>());
    for (int q : {50, 42, 58}) {
        std::nth_element(s.begin(), s.begin() + static_cast<long>(s.size() * q / 100), s.end());
        out.push_back((vf < s[s.size() * q / 100]) / 255);
    }
    return out;
}

// Richardson-Lucy deconvolution with a Gaussian of sigma (pixels)
cv::Mat deconvolve(const cv::Mat& img32, double sigma)
{
    cv::Mat u = img32 / 255.0 + 1e-3, est = u.clone(), conv, ratio;
    for (int i = 0; i < 10; ++i) {
        cv::GaussianBlur(est, conv, cv::Size(), sigma);
        cv::divide(u, conv + 1e-6, ratio);
        cv::GaussianBlur(ratio, ratio, cv::Size(), sigma);
        est = est.mul(ratio);
    }
    cv::Mat out = est * 255.0;
    return cv::min(cv::max(out, 0.0), 255.0);
}

// a module shows the code when its 3 x 3 neighbourhood spans this share of the dark - light step
constexpr double kSeenRange = 0.33;
// a window seen in part needs this score (of 4) over its seen fixed modules; a whole window 3
constexpr double kPartScore = 3.4;

// Modules of an n x n grid (pitch pp at X0, Y0) that show the code: the centre inside the sampled
// area, and contrast among the 3 x 3 modules around it that are inside too. A module beyond the
// label's edge, under a cover or in glare has none, and is then not read but erased.
cv::Mat seenModules(const cv::Mat& mm, double X0, double Y0, double pp, const cv::Rect2d& inside, double minRange)
{
    const int rows = mm.rows, cols = mm.cols;
    cv::Mat in(rows, cols, CV_8U), seen(rows, cols, CV_8U, cv::Scalar(0));
    for (int y = 0; y < rows; ++y)
        for (int x = 0; x < cols; ++x)
            in.at<uint8_t>(y, x) = inside.contains(cv::Point2d(X0 + (x + 0.5) * pp, Y0 + (y + 0.5) * pp)) ? 1 : 0;
    for (int y = 0; y < rows; ++y)
        for (int x = 0; x < cols; ++x) {
            if (!in.at<uint8_t>(y, x)) continue;
            float lo = mm.at<float>(y, x), hi = lo;
            for (int j = std::max(0, y - 1); j <= std::min(rows - 1, y + 1); ++j)
                for (int i = std::max(0, x - 1); i <= std::min(cols - 1, x + 1); ++i)
                    if (in.at<uint8_t>(j, i)) {
                        lo = std::min(lo, mm.at<float>(j, i));
                        hi = std::max(hi, mm.at<float>(j, i));
                    }
            seen.at<uint8_t>(y, x) = hi - lo >= minRange ? 1 : 0;
        }
    return seen;
}

// The grid fit's measure for a window (canonical orientation): mean of the seen fixed modules
// that should be light (clock tracks) less the mean of those that should be dark (L arms, clocks).
// 0 when too few of either are seen.
double fixedContrast(const cv::Mat& mm, const cv::Mat& seen, int n)
{
    double sd = 0, sl = 0;
    int nd = 0, nl = 0;
    const auto at = [&](int y, int x, bool dark) {
        if (!seen.at<uint8_t>(y, x)) return;
        const float v = mm.at<float>(y, x);
        if (dark) { sd += v; ++nd; } else { sl += v; ++nl; }
    };
    for (int i = 0; i < n; ++i) {
        at(i, 0, true);
        at(n - 1, i, true);
        if (i > 0 && i < n - 1) {
            at(0, i, i % 2 == 0);
            at(i, n - 1, (n - 1 - i) % 2 == 0);
        }
    }
    if (nd < n / 2 || nl < n / 4) return 0;
    return sl / nl - sd / nd;
}

// a lattice read continues the serial run of the codes read within this many
constexpr long kSerialSpan = 64;

// A lattice read must continue the serial run of the codes already read: the same text around
// its last digit run (compared on up to 9 digits), and a serial within kSerialSpan of one of
// theirs. plausibleAmong checks only the length and the digit positions, and over the many
// windows of a lattice search a miscorrection passed it ("31100017558" on a case of
// 31100016688-711 and 31100017404-438). With fewer than two codes read there is no run: true.
bool inSerialRun(const std::string& t, const std::vector<std::string>* known)
{
    if (!known || known->size() < 2) return true;
    const auto split = [](const std::string& s, std::string& prefix, std::string& suffix, long& serial) {
        size_t e = s.size();
        while (e > 0 && !std::isdigit(static_cast<unsigned char>(s[e - 1]))) --e;
        if (e == 0) return false;
        size_t b = e;
        while (b > 0 && e - b < 9 && std::isdigit(static_cast<unsigned char>(s[b - 1]))) --b;
        prefix = s.substr(0, b);
        suffix = s.substr(e);
        serial = std::strtol(s.substr(b, e - b).c_str(), nullptr, 10);
        return true;
    };
    std::string p, x;
    long v = 0;
    if (!split(t, p, x, v)) return false;
    for (const auto& k : *known) {
        std::string kp, kx;
        long kv = 0;
        if (split(k, kp, kx, kv) && kp == p && kx == x && std::labs(kv - v) <= kSerialSpan) return true;
    }
    return false;
}


// ---- warped lattice: the full PowerGrid method (Cognex US10956697 / US11455482), our form ----
// A cap label wraps around the cap and wrinkles, so one straight grid drifts off the modules
// within a few modules. The patent's way, with our changes:
//   1. module-centre map: the edge image's spectrum is kept only near its four pitch peaks and
//      transformed back; the result is two crossed waves that bend with the label, with minima
//      on the module centres (our change: the bands are Gaussian, a third of the pitch frequency
//      wide, so the waves follow a bend of about a module over three modules);
//   2. a 5 x 5 seed of nodes at the patch centre, snapped to the map's minima, grown 8 ways:
//      each new node is predicted from its placed neighbours' steps and snapped again (our
//      change: a node without a clear minimum keeps its prediction so growth crosses blank
//      modules, but only clear nodes enter the fit);
//   3. a polynomial (cubic with enough nodes, else quadratic) from grid index to pixel, fitted
//      by least squares and refitted without nodes off by more than a quarter module.
// Module means are then taken at the fitted centres and decoded as the straight lattice is.

// minima of the centre map: modules centres where the two crossed waves are lowest
cv::Mat centreMap(const cv::Mat& img32, double pitch)
{
    cv::Mat gx, gy, e;
    cv::Sobel(img32, gx, CV_32F, 1, 0);
    cv::Sobel(img32, gy, CV_32F, 0, 1);
    cv::magnitude(gx, gy, e);
    e -= cv::mean(e)[0];
    const int W = cv::getOptimalDFTSize(e.cols), H = cv::getOptimalDFTSize(e.rows);
    cv::Mat pad;
    cv::copyMakeBorder(e, pad, 0, H - e.rows, 0, W - e.cols, cv::BORDER_CONSTANT, cv::Scalar(0));
    cv::Mat F;
    cv::dft(pad, F, cv::DFT_COMPLEX_OUTPUT);
    // the four pitch peaks, searched within 20 % of the expected frequency on each axis
    const auto mag2 = [&](int u, int v) {
        const cv::Vec2f c = F.at<cv::Vec2f>((v % H + H) % H, (u % W + W) % W);
        return c[0] * c[0] + c[1] * c[1];
    };
    cv::Mat mask(H, W, CV_32F, cv::Scalar(0));
    for (int axis = 0; axis < 2; ++axis) {
        const double f0 = (axis == 0 ? W : H) / pitch;
        int best = static_cast<int>(std::lround(f0));
        float bm = -1;
        for (int f = std::max(1, static_cast<int>(f0 * 0.8)); f <= static_cast<int>(f0 * 1.2) + 1; ++f) {
            const float v = axis == 0 ? mag2(f, 0) : mag2(0, f);
            if (v > bm) { bm = v; best = f; }
        }
        const double sig = std::max(1.0, best / 4.0);
        for (int sgn : {-1, 1}) {
            const double cu = axis == 0 ? sgn * best : 0, cv0 = axis == 0 ? 0 : sgn * best;
            for (int v = 0; v < H; ++v)
                for (int u = 0; u < W; ++u) {
                    const double du = (u > W / 2 ? u - W : u) - cu, dv = (v > H / 2 ? v - H : v) - cv0;
                    const double g = std::exp(-(du * du + dv * dv) / (2 * sig * sig));
                    mask.at<float>(v, u) = std::max(mask.at<float>(v, u), static_cast<float>(g));
                }
        }
    }
    cv::Mat planes[2];
    cv::split(F, planes);
    planes[0] = planes[0].mul(mask);
    planes[1] = planes[1].mul(mask);
    cv::merge(planes, 2, F);
    cv::Mat C;
    cv::idft(F, C, cv::DFT_REAL_OUTPUT | cv::DFT_SCALE);
    C = C(cv::Rect(0, 0, e.cols, e.rows)).clone();
    cv::Scalar mean, sd;
    cv::meanStdDev(C, mean, sd);
    return (C - mean[0]) / (sd[0] + 1e-9); // in standard deviations
}

// the deepest minimum of the centre map within r of `at`, to sub-pixel; depth in deviations
bool snapToCentre(const cv::Mat& C, cv::Point2d at, double r, cv::Point2d& out, double& depth)
{
    const int x0 = std::max(1, static_cast<int>(at.x - r)), x1 = std::min(C.cols - 2, static_cast<int>(at.x + r));
    const int y0 = std::max(1, static_cast<int>(at.y - r)), y1 = std::min(C.rows - 2, static_cast<int>(at.y + r));
    if (x0 > x1 || y0 > y1) return false;
    float best = 1e9f;
    int bx = x0, by = y0;
    for (int y = y0; y <= y1; ++y)
        for (int x = x0; x <= x1; ++x) {
            const float v = C.at<float>(y, x);
            if (v < best) { best = v; bx = x; by = y; }
        }
    if (bx == x0 || bx == x1 || by == y0 || by == y1) return false; // on the edge: no minimum inside
    const auto sub = [](float a, float b, float c) { const float d = a - 2 * b + c; return d > 1e-6f ? 0.5f * (a - c) / d : 0.f; };
    out = {bx + sub(C.at<float>(by, bx - 1), best, C.at<float>(by, bx + 1)), by + sub(C.at<float>(by - 1, bx), best, C.at<float>(by + 1, bx))};
    depth = -best;
    return true;
}

// terms of the polynomial in grid index (i, j) up to `deg`
std::vector<double> polyTerms(double i, double j, int deg)
{
    std::vector<double> t;
    for (int d = 0; d <= deg; ++d)
        for (int a = d; a >= 0; --a) t.push_back(std::pow(i, a) * std::pow(j, d - a));
    return t;
}

struct WarpGrid {
    int i0 = 0, j0 = 0, cols = 0, rows = 0, deg = 0; // index range: i in [i0, i0 + cols), j likewise
    cv::Mat ax, ay;                                  // coefficients (terms x 1)
    cv::Point2d at(double i, double j) const
    {
        const auto t = polyTerms(i, j, deg);
        double x = 0, y = 0;
        for (size_t k = 0; k < t.size(); ++k) {
            x += t[k] * ax.at<double>(static_cast<int>(k));
            y += t[k] * ay.at<double>(static_cast<int>(k));
        }
        return {x, y};
    }
};

// the warped grid of a patch whose straight lattice is pitch p at phase (ox, oy); false when too
// few nodes are clear
bool warpedGrid(const cv::Mat& rot, double p, double ox, double oy, WarpGrid& g)
{
    const cv::Mat C = centreMap(rot, p);
    const int cols = static_cast<int>((rot.cols - ox) / p), rows = static_cast<int>((rot.rows - oy) / p);
    if (cols < 7 || rows < 7) return false;
    cv::Mat px(rows, cols, CV_64FC2, cv::Scalar(0, 0));
    cv::Mat state(rows, cols, CV_8U, cv::Scalar(0)); // 0 not placed, 1 predicted, 2 clear
    const double r = 0.35 * p, minDepth = 0.4;
    // the seed: the map's minimum nearest the patch centre, at index (cols / 2, rows / 2); the
    // 5 x 5 seed around it grows from it like the rest (a straight grid is off by more than
    // the snap radius a few modules away on a wrinkled label)
    const int ci = cols / 2, cj = rows / 2;
    {
        cv::Point2d q;
        double d = 0;
        if (!snapToCentre(C, cv::Point2d(rot.cols / 2.0, rot.rows / 2.0), 0.5 * p, q, d) || d < minDepth) return false;
        px.at<cv::Vec2d>(cj, ci) = {q.x, q.y};
        state.at<uint8_t>(cj, ci) = 2;
    }
    // grown 8 ways: breadth first from the seed
    std::vector<cv::Point> queue;
    for (int j = 0; j < rows; ++j)
        for (int i = 0; i < cols; ++i)
            if (state.at<uint8_t>(j, i)) queue.emplace_back(i, j);
    for (size_t h = 0; h < queue.size(); ++h) {
        const cv::Point nd = queue[h];
        for (int dj = -1; dj <= 1; ++dj)
            for (int di = -1; di <= 1; ++di) {
                const int i = nd.x + di, j = nd.y + dj;
                if ((!di && !dj) || i < 0 || j < 0 || i >= cols || j >= rows || state.at<uint8_t>(j, i)) continue;
                // the local steps along i and j from the placed neighbours of nd
                cv::Point2d ei(0, 0), ej(0, 0);
                int ni = 0, nj = 0;
                for (int s : {-1, 1}) {
                    if (nd.x + s >= 0 && nd.x + s < cols && state.at<uint8_t>(nd.y, nd.x + s)) {
                        const cv::Vec2d a = px.at<cv::Vec2d>(nd.y, nd.x + s), b = px.at<cv::Vec2d>(nd.y, nd.x);
                        ei += cv::Point2d(a[0] - b[0], a[1] - b[1]) * s;
                        ++ni;
                    }
                    if (nd.y + s >= 0 && nd.y + s < rows && state.at<uint8_t>(nd.y + s, nd.x)) {
                        const cv::Vec2d a = px.at<cv::Vec2d>(nd.y + s, nd.x), b = px.at<cv::Vec2d>(nd.y, nd.x);
                        ej += cv::Point2d(a[0] - b[0], a[1] - b[1]) * s;
                        ++nj;
                    }
                }
                ei = ni ? ei / ni : cv::Point2d(p, 0);
                ej = nj ? ej / nj : cv::Point2d(0, p);
                const cv::Vec2d b = px.at<cv::Vec2d>(nd.y, nd.x);
                const cv::Point2d pred = cv::Point2d(b[0], b[1]) + ei * di + ej * dj;
                if (pred.x < 0 || pred.y < 0 || pred.x >= rot.cols || pred.y >= rot.rows) continue;
                cv::Point2d q;
                double d = 0;
                const bool clear = snapToCentre(C, pred, r, q, d) && d >= minDepth;
                // a predicted node only grows from clear ones: two blank modules in a row stop it
                if (!clear && state.at<uint8_t>(nd.y, nd.x) != 2) continue;
                px.at<cv::Vec2d>(j, i) = clear ? cv::Vec2d(q.x, q.y) : cv::Vec2d(pred.x, pred.y);
                state.at<uint8_t>(j, i) = clear ? 2 : 1;
                queue.emplace_back(i, j);
            }
    }
    // the 5 x 5 seed must be clear for the most part, or the map shows no grid here
    int seeded = 0;
    for (int j = cj - 2; j <= cj + 2; ++j)
        for (int i = ci - 2; i <= ci + 2; ++i) seeded += j >= 0 && i >= 0 && j < rows && i < cols && state.at<uint8_t>(j, i) == 2;
    if (seeded < 17) return false;
    // the polynomial over the clear nodes, refitted without the ones off by a quarter module
    std::vector<cv::Point> nodes;
    for (int j = 0; j < rows; ++j)
        for (int i = 0; i < cols; ++i)
            if (state.at<uint8_t>(j, i) == 2) nodes.emplace_back(i, j);
    g.deg = nodes.size() >= 60 ? 3 : 2;
    const int nt = (g.deg + 1) * (g.deg + 2) / 2;
    for (int pass = 0; pass < 2; ++pass) {
        if (static_cast<int>(nodes.size()) < 3 * nt) return false;
        cv::Mat A(static_cast<int>(nodes.size()), nt, CV_64F), bx(static_cast<int>(nodes.size()), 1, CV_64F), by = bx.clone();
        for (int k = 0; k < static_cast<int>(nodes.size()); ++k) {
            const auto t = polyTerms(nodes[k].x, nodes[k].y, g.deg);
            for (int c = 0; c < nt; ++c) A.at<double>(k, c) = t[static_cast<size_t>(c)];
            const cv::Vec2d v = px.at<cv::Vec2d>(nodes[k].y, nodes[k].x);
            bx.at<double>(k) = v[0];
            by.at<double>(k) = v[1];
        }
        cv::solve(A, bx, g.ax, cv::DECOMP_SVD);
        cv::solve(A, by, g.ay, cv::DECOMP_SVD);
        if (pass == 1) break;
        std::vector<cv::Point> keep;
        for (const auto& nd : nodes) {
            const cv::Vec2d v = px.at<cv::Vec2d>(nd.y, nd.x);
            if (cv::norm(g.at(nd.x, nd.y) - cv::Point2d(v[0], v[1])) <= 0.25 * p) keep.push_back(nd);
        }
        nodes.swap(keep);
    }
    // the index range the placed nodes cover
    int i0 = cols, i1 = -1, j0 = rows, j1 = -1;
    for (int j = 0; j < rows; ++j)
        for (int i = 0; i < cols; ++i)
            if (state.at<uint8_t>(j, i)) { i0 = std::min(i0, i); i1 = std::max(i1, i); j0 = std::min(j0, j); j1 = std::max(j1, j); }
    g.i0 = i0;
    g.j0 = j0;
    g.cols = i1 - i0 + 1;
    g.rows = j1 - j0 + 1;
    return true;
}

// module means over the warped grid's index range: 3 x 3 samples in the central 40 % of each
// module, along the grid's own local axes
cv::Mat moduleMeansWarped(const cv::Mat& img32, const WarpGrid& g)
{
    constexpr int sub = 3;
    constexpr double frac = 0.4;
    const int n = g.rows * g.cols * sub * sub;
    cv::Mat mapX(1, n, CV_32F), mapY(1, n, CV_32F);
    int k = 0;
    for (int y = 0; y < g.rows; ++y)
        for (int x = 0; x < g.cols; ++x)
            for (int j = 0; j < sub; ++j)
                for (int i = 0; i < sub; ++i, ++k) {
                    const cv::Point2d q = g.at(g.i0 + x + frac * ((i + 0.5) / sub - 0.5), g.j0 + y + frac * ((j + 0.5) / sub - 0.5));
                    mapX.at<float>(0, k) = static_cast<float>(q.x);
                    mapY.at<float>(0, k) = static_cast<float>(q.y);
                }
    cv::Mat vals;
    cv::remap(img32, vals, mapX, mapY, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    cv::Mat means(g.rows, g.cols, CV_32F);
    for (int c = 0; c < g.rows * g.cols; ++c) {
        float s = 0;
        for (int t = 0; t < sub * sub; ++t) s += vals.at<float>(0, c * sub * sub + t);
        means.at<float>(c / g.cols, c % g.cols) = s / (sub * sub);
    }
    return means;
}

// the n x n windows of a module-mean lattice, ranked by L finder and clock tracks (best of three
// thresholds), best first: score, y, x, rotation
std::vector<std::tuple<double, int, int, int>> rankWindows(const cv::Mat& M, int n)
{
    cv::Mat M8, scratch;
    cv::normalize(M, M8, 0, 255, cv::NORM_MINMAX, CV_8U);
    const double t = cv::threshold(M8, scratch, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
    std::vector<std::tuple<double, int, int, int>> wins;
    for (int y = 0; y + n <= M8.rows; ++y)
        for (int x = 0; x + n <= M8.cols; ++x)
            for (int k = 0; k < 4; ++k) wins.emplace_back(0.0, y, x, k);
    for (const double dt : {0.0, -25.5, 25.5}) {
        const cv::Mat D = M8 < t + dt;
        size_t wi = 0;
        for (int y = 0; y + n <= D.rows; ++y)
            for (int x = 0; x + n <= D.cols; ++x) {
                cv::Mat w = D(cv::Rect(x, y, n, n)).clone();
                for (int k = 0; k < 4; ++k, ++wi) {
                    if (k) cv::rotate(w, w, cv::ROTATE_90_COUNTERCLOCKWISE);
                    double l = 0, b = 0, tc = 0, rc = 0;
                    for (int i = 0; i < n; ++i) {
                        l += w.at<uint8_t>(i, 0) != 0;
                        b += w.at<uint8_t>(n - 1, i) != 0;
                        tc += (w.at<uint8_t>(0, i) != 0) == (i % 2 == 0);
                        rc += (w.at<uint8_t>(i, n - 1) != 0) == ((n - 1 - i) % 2 == 0);
                    }
                    auto& sc = std::get<0>(wins[wi]);
                    sc = std::max(sc, (l + b + tc + rc) / n);
                }
            }
    }
    std::sort(wins.begin(), wins.end(), [](const auto& a, const auto& b) { return std::get<0>(a) > std::get<0>(b); });
    return wins;
}

// one window's module means (canonical orientation) through every decoder of the lattice, as the
// straight lattice tries them; every read must continue the case's serial run
std::string decodeWindow(const cv::Mat& mm, int n, double minRange, const std::vector<std::string>* known)
{
    std::string s;
    for (const auto& bits : thresholdVariants(mm)) {
        s = hardDecodeDm(bits, n);
        if (!s.empty()) break;
    }
    if (s.empty()) s = softDecodeDm(mm, n);
    if (s.empty() && known && known->size() >= 2) s = knownSoftDecodeDm(mm, n, *known);
    if (s.empty()) {
        const cv::Mat seen = seenModules(mm, 0, 0, 1, cv::Rect2d(0, 0, n, n), minRange);
        if (cv::countNonZero(seen) < n * n) s = maskedSoftDecodeDm(mm, seen, n, known);
    }
    return !s.empty() && inSerialRun(s, known) ? s : std::string();
}

} // namespace

std::string decodeDmBits(const cv::Mat& dark) // CV_8U, 1 = dark module, canonical orientation
{
    constexpr int cell = 6, qz = 3;
    const int n = dark.rows;
    cv::Mat img((n + 2 * qz) * cell, (n + 2 * qz) * cell, CV_8U, cv::Scalar(255));
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x)
            if (dark.at<uint8_t>(y, x)) img(cv::Rect((x + qz) * cell, (y + qz) * cell, cell, cell)).setTo(0);
    ZXing::ReaderOptions o;
    o.setFormats(ZXing::BarcodeFormat::DataMatrix);
    o.setIsPure(true);
    o.setTryRotate(false);
    o.setTryDownscale(false);
    o.setBinarizer(ZXing::Binarizer::BoolCast);
    o.setTextMode(ZXing::TextMode::Plain);
    const auto r = ZXing::ReadBarcode(ZXing::ImageView(img.data, img.cols, img.rows, ZXing::ImageFormat::Lum), o);
    return ownGridText(r);
}

bool latticeDm(const cv::Mat& crop, float codeSide, int n, int budgetMs, std::string& text, cv::Point2f quadOut[4],
               const std::vector<std::string>* known)
{
    if (n < 10 || codeSide <= 0 || budgetMs <= 0 || crop.empty()) return false;
    const auto deadline = Clock::now() + std::chrono::milliseconds(budgetMs);
    const auto late = [&]() { return Clock::now() >= deadline; };

    // about 6 px per module, stretched
    const double m0 = codeSide / n, up = std::max(1.0, 6.0 / m0);
    cv::Mat c8, s8;
    cv::resize(crop, c8, cv::Size(), up, up, cv::INTER_CUBIC);
    std::vector<uint8_t> v(c8.begin<uint8_t>(), c8.end<uint8_t>());
    std::nth_element(v.begin(), v.begin() + v.size() / 100, v.end());
    const double lo = v[v.size() / 100];
    std::nth_element(v.begin(), v.begin() + v.size() * 995 / 1000, v.end());
    const double hi = std::max(lo + 4, static_cast<double>(v[v.size() * 995 / 1000]));
    c8.convertTo(s8, CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
    const double side = codeSide * up, m = m0 * up;

    // the patch around the code: the dark block if one is found; else the densest module-scale
    // texture in the crop, then the crop centre (a defocused code has no dark block, and the AI
    // box can sit off the code on a cap's label)
    std::vector<std::pair<cv::Point2f, float>> centres; // centre, half side
    const auto squares = dmCodeSquares(s8, static_cast<float>(side), n);
    if (!squares.empty())
        centres.emplace_back(squares[0].center, std::max(squares[0].size.width, squares[0].size.height) * 0.85f);
    else {
        cv::Mat gx, gy, mag, dens;
        cv::Sobel(s8, gx, CV_32F, 1, 0);
        cv::Sobel(s8, gy, CV_32F, 0, 1);
        cv::magnitude(gx, gy, mag);
        // edges inside the paper only: the label's own border on the dark background is a strong
        // edge, so the paper mask is eroded by two modules
        cv::Mat paper, inside;
        const int k3 = static_cast<int>(3 * m) | 1, k5 = static_cast<int>(5 * m) | 1;
        cv::dilate(s8, paper, cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(k3, k3)));
        cv::threshold(paper, paper, 127, 255, cv::THRESH_BINARY);
        cv::erode(paper, inside, cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(k5, k5)));
        mag.setTo(0, inside == 0);
        const int k = std::max(3, static_cast<int>(side * 0.8) | 1);
        cv::boxFilter(mag, dens, CV_32F, cv::Size(k, k));
        cv::Point mx;
        cv::minMaxLoc(dens, nullptr, nullptr, nullptr, &mx);
        centres.emplace_back(cv::Point2f(static_cast<float>(mx.x), static_cast<float>(mx.y)), static_cast<float>(side * 0.85));
        const cv::Point2f mid(s8.cols / 2.f, s8.rows / 2.f);
        if (cv::norm(mid - centres[0].first) > side * 0.1) centres.emplace_back(mid, static_cast<float>(side * 0.85));
    }
    for (auto [ctr, r] : centres) {
        if (late()) return false;
        // the whole patch inside the crop: a centre near the crop edge is moved inward rather
        // than the patch clipped (a clipped patch loses the code's edge rows)
        ctr.x = std::clamp(ctr.x, std::min(r, s8.cols / 2.f), std::max(s8.cols - r, s8.cols / 2.f));
        ctr.y = std::clamp(ctr.y, std::min(r, s8.rows / 2.f), std::max(s8.rows - r, s8.rows / 2.f));
        const cv::Rect pr = cv::Rect(static_cast<int>(ctr.x - r), static_cast<int>(ctr.y - r), static_cast<int>(2 * r), static_cast<int>(2 * r)) &
                            cv::Rect(0, 0, s8.cols, s8.rows);
        if (pr.width < side || pr.height < side) continue;
        cv::Mat patch;
        s8(pr).convertTo(patch, CV_32F);

        const auto [ang, p] = pitchAndAngle(patch, m);
        if (late()) return false;

        for (double sig : {0.0, 0.3, 0.45}) { // deblur in module units: the lens blurs the case corners
            const cv::Mat img = sig == 0 ? patch : deconvolve(patch, sig * p);
            const cv::Mat rot = rotated(img, ang);
            // the phase: module-centre samples spread widest
            double bestStd = -1, ox = 0, oy = 0;
            cv::Mat M;
            for (int j = 0; j < 8; ++j)
                for (int i = 0; i < 8; ++i) {
                    const double x0 = i * p / 8, y0 = j * p / 8;
                    // every module whose centre lies inside the patch
                    const int cols = static_cast<int>((rot.cols - x0 - p) / p) + 1, rows = static_cast<int>((rot.rows - y0 - p) / p) + 1;
                    if (cols < n || rows < n) continue;
                    cv::Mat mapX(rows, cols, CV_32F), mapY(rows, cols, CV_32F);
                    for (int y = 0; y < rows; ++y)
                        for (int x = 0; x < cols; ++x) {
                            mapX.at<float>(y, x) = static_cast<float>(x0 + (x + 0.5) * p);
                            mapY.at<float>(y, x) = static_cast<float>(y0 + (y + 0.5) * p);
                        }
                    cv::Mat vals;
                    cv::remap(rot, vals, mapX, mapY, cv::INTER_LINEAR);
                    cv::Scalar mean, sd;
                    cv::meanStdDev(vals, mean, sd);
                    if (sd[0] > bestStd) {
                        bestStd = sd[0];
                        M = vals;
                        ox = x0;
                        oy = y0;
                    }
                }
            if (M.empty()) return false;

            // windows ranked by L finder (left column, bottom row dark) and clock tracks. A defocused
            // L finder sits right at the Otsu split, so each window takes its best score over three
            // thresholds (Otsu and a tenth of the range either side).
            cv::Mat M8, scratch;
            cv::normalize(M, M8, 0, 255, cv::NORM_MINMAX, CV_8U);
            const double t = cv::threshold(M8, scratch, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
            std::vector<std::tuple<double, int, int, int>> wins; // score, y, x, rotation
            for (int y = 0; y + n <= M8.rows; ++y)
                for (int x = 0; x + n <= M8.cols; ++x)
                    for (int k = 0; k < 4; ++k) wins.emplace_back(0.0, y, x, k);
            for (const double dt : {0.0, -25.5, 25.5}) {
                const cv::Mat D = M8 < t + dt; // 255 = dark
                size_t wi = 0;
                for (int y = 0; y + n <= D.rows; ++y)
                    for (int x = 0; x + n <= D.cols; ++x) {
                        cv::Mat w = D(cv::Rect(x, y, n, n)).clone();
                        for (int k = 0; k < 4; ++k, ++wi) {
                            if (k) cv::rotate(w, w, cv::ROTATE_90_COUNTERCLOCKWISE);
                            double l = 0, b = 0, tc = 0, rc = 0;
                            for (int i = 0; i < n; ++i) {
                                l += w.at<uint8_t>(i, 0) != 0;
                                b += w.at<uint8_t>(n - 1, i) != 0;
                                tc += (w.at<uint8_t>(0, i) != 0) == (i % 2 == 0);
                                rc += (w.at<uint8_t>(i, n - 1) != 0) == ((n - 1 - i) % 2 == 0);
                            }
                            auto& sc = std::get<0>(wins[wi]);
                            sc = std::max(sc, (l + b + tc + rc) / n);
                        }
                    }
            }
            std::sort(wins.begin(), wins.end(), [](const auto& a, const auto& b) { return std::get<0>(a) > std::get<0>(b); });

            // the modules that show the code (seenModules): contrast needed around a module, a
            // third of the step between the dark and light modules
            const double split = cv::mean(M, M8 < t)[0], light = cv::mean(M, M8 >= t)[0];
            const double minRange = kSeenRange * std::max(1.0, light - split);
            const cv::Rect2d inside(ox, oy, M.cols * p, M.rows * p); // the sampled lattice in rot

            // the corners of a window that read: rotated patch -> patch -> upscaled crop -> crop
            const auto found = [&](const std::string& s, double X0, double Y0, double pp) {
                text = s;
                const cv::Mat Rinv = rotationMatrix(cv::Point2f(rot.cols / 2.f, rot.rows / 2.f), -ang);
                const double S = n * pp;
                std::vector<cv::Point2f> q = {{float(X0), float(Y0)}, {float(X0 + S), float(Y0)},
                                              {float(X0 + S), float(Y0 + S)}, {float(X0), float(Y0 + S)}};
                cv::transform(q, q, Rinv);
                for (int c = 0; c < 4; ++c)
                    quadOut[c] = (q[static_cast<size_t>(c)] + cv::Point2f(static_cast<float>(pr.x), static_cast<float>(pr.y))) *
                                 static_cast<float>(1.0 / up);
                return true;
            };

            // the three best windows: unshifted first, then the jitter around them
            static const std::array<std::array<double, 3>, 19> kJitter = {{{0, 0, 1}, {0, 0, 0.98}, {0, 0, 1.02},
                {-0.25, 0, 1}, {0.25, 0, 1}, {0, -0.25, 1}, {0, 0.25, 1}, {-0.25, -0.25, 1}, {0.25, 0.25, 1},
                {-0.25, 0.25, 1}, {0.25, -0.25, 1}, {-0.5, 0, 1}, {0.5, 0, 1}, {0, -0.5, 1}, {0, 0.5, 1},
                {-0.5, -0.5, 1}, {0.5, 0.5, 1}, {-0.5, 0.5, 1}, {0.5, -0.5, 1}}};
            const size_t nWin = std::min<size_t>(3, wins.size());
            for (const auto& jit : kJitter)
                for (size_t wi = 0; wi < nWin; ++wi) {
                    const auto [score, wy, wx, k] = wins[wi];
                    if (score < 3.0) continue;
                    {
                            const double dx = jit[0], dy = jit[1], ds = jit[2];
                            if (late()) return false;
                            const double pp = p * ds, X0 = ox + (wx + dx) * p, Y0 = oy + (wy + dy) * p;
                            cv::Mat mm = moduleMeansAxis(rot, X0, Y0, pp, n);
                            for (int j = 0; j < k; ++j) cv::rotate(mm, mm, cv::ROTATE_90_COUNTERCLOCKWISE);
                            // hard decisions first, then soft: uncertain codewords as erasures
                            std::string s;
                            for (const auto& bits : thresholdVariants(mm)) {
                                s = hardDecodeDm(bits, n);
                                if (!s.empty()) break;
                            }
                            if (s.empty()) s = softDecodeDm(mm, n);
                            // the content the case's codes share: all but the serial is known
                            if (s.empty() && known && known->size() >= 2) {
                                s = knownSoftDecodeDm(mm, n, *known);
                                if (!inSerialRun(s, known)) s.clear();
                            }
                            // a covered or glared patch: its modules as erasures, not guesses
                            if (s.empty()) {
                                cv::Mat seen = seenModules(moduleMeansAxis(rot, X0, Y0, pp, n), X0, Y0, pp, inside, minRange);
                                for (int j = 0; j < k; ++j) cv::rotate(seen, seen, cv::ROTATE_90_COUNTERCLOCKWISE);
                                if (cv::countNonZero(seen) < n * n) s = maskedSoftDecodeDm(mm, seen, n, known);
                                if (!inSerialRun(s, known)) s.clear();
                            }
                            // every lattice read, hard ones too: the search makes thousands of
                            // attempts per region, and a miscorrection must not leave the run
                            if (!s.empty() && inSerialRun(s, known)) return found(s, X0, Y0, pp);
                    }
                }

            // A wrinkled or curved label: the warped grid (above) and its windows
            if (late()) return false;
            if (WarpGrid g; warpedGrid(rot, p, ox, oy, g) && g.cols >= n && g.rows >= n) {
                const cv::Mat Mw = moduleMeansWarped(rot, g);
                const auto wwins = rankWindows(Mw, n);
                for (size_t wi = 0; wi < std::min<size_t>(3, wwins.size()); ++wi) {
                    const auto [score, wy, wx, k] = wwins[wi];
                    if (score < 3.0 || late()) break;
                    cv::Mat mm = Mw(cv::Rect(wx, wy, n, n)).clone();
                    for (int j = 0; j < k; ++j) cv::rotate(mm, mm, cv::ROTATE_90_COUNTERCLOCKWISE);
                    const std::string s = decodeWindow(mm, n, minRange, known);
                    if (s.empty()) continue;
                    text = s;
                    const double i0 = g.i0 + wx - 0.5, j0 = g.j0 + wy - 0.5;
                    std::vector<cv::Point2f> q;
                    for (const auto& c : {cv::Point2d(i0, j0), cv::Point2d(i0 + n, j0), cv::Point2d(i0 + n, j0 + n), cv::Point2d(i0, j0 + n)})
                        q.emplace_back(g.at(c.x, c.y));
                    cv::transform(q, q, rotationMatrix(cv::Point2f(rot.cols / 2.f, rot.rows / 2.f), -ang));
                    for (int c = 0; c < 4; ++c)
                        quadOut[c] = (q[static_cast<size_t>(c)] + cv::Point2f(static_cast<float>(pr.x), static_cast<float>(pr.y))) *
                                     static_cast<float>(1.0 / up);
                    return true;
                }
            }

            // A symbol seen in part: its L arm or clock track runs past the sampled lattice (off the
            // label, out of the image) or is covered. Windows up to half a symbol outside, scored
            // by the fixed modules that are seen; one horizontal and one vertical side must be seen
            // for three quarters of their length to fix the window's place. The grid of the best
            // few is fitted to their seen L arm and clock track (position and scale, by the fixed
            // pattern's contrast), and the modules not seen are erasures in the Reed-Solomon decode.
            if (late()) return false;
            const int P = n / 2;
            cv::Mat seenM = seenModules(M, ox, oy, p, inside, minRange); // the lattice's own modules
            std::vector<std::tuple<double, int, int, int>> part; // score, y, x, rotation (lattice cells)
            {
                cv::Mat Sp(M.rows + 2 * P, M.cols + 2 * P, CV_8U, cv::Scalar(0));
                seenM.copyTo(Sp(cv::Rect(P, P, M.cols, M.rows)));
                std::vector<cv::Mat> Dp;
                for (const double dt : {0.0, -25.5, 25.5}) {
                    cv::Mat d(Sp.size(), CV_8U, cv::Scalar(0));
                    cv::Mat(M8 < t + dt).copyTo(d(cv::Rect(P, P, M.cols, M.rows)));
                    Dp.push_back(d);
                }
                const int need = (3 * n + 3) / 4;
                for (int y = 0; y + n <= Sp.rows; ++y)
                    for (int x = 0; x + n <= Sp.cols; ++x) {
                        const bool within = y >= P && x >= P && y - P + n <= M.rows && x - P + n <= M.cols;
                        cv::Mat sw = Sp(cv::Rect(x, y, n, n)).clone();
                        if (within && cv::countNonZero(sw) == n * n) continue; // a whole window: tried above
                        std::vector<cv::Mat> dw;
                        for (const auto& d : Dp) dw.push_back(d(cv::Rect(x, y, n, n)).clone());
                        for (int k = 0; k < 4; ++k) {
                            if (k) {
                                cv::rotate(sw, sw, cv::ROTATE_90_COUNTERCLOCKWISE);
                                for (auto& d : dw) cv::rotate(d, d, cv::ROTATE_90_COUNTERCLOCKWISE);
                            }
                            // seen modules of the left L arm, bottom L arm, top and right clock tracks
                            int sl = 0, sb = 0, st = 0, sr = 0, dataSeen = 0;
                            for (int i = 0; i < n; ++i) {
                                sl += sw.at<uint8_t>(i, 0) != 0;
                                sb += sw.at<uint8_t>(n - 1, i) != 0;
                                st += sw.at<uint8_t>(0, i) != 0;
                                sr += sw.at<uint8_t>(i, n - 1) != 0;
                            }
                            if (std::max(sl, sr) < need || std::max(sb, st) < need) continue;
                            dataSeen = cv::countNonZero(sw(cv::Rect(1, 1, n - 2, n - 2)));
                            if (2 * dataSeen < (n - 2) * (n - 2)) continue;
                            double best = 0;
                            for (const auto& d : dw) {
                                int match = 0, fixedSeen = 0;
                                for (int i = 0; i < n; ++i) {
                                    const auto at = [&](int yy, int xx, bool dark) {
                                        if (!sw.at<uint8_t>(yy, xx)) return;
                                        ++fixedSeen;
                                        match += (d.at<uint8_t>(yy, xx) != 0) == dark;
                                    };
                                    at(i, 0, true);
                                    at(n - 1, i, true);
                                    at(0, i, i % 2 == 0);
                                    at(i, n - 1, (n - 1 - i) % 2 == 0);
                                }
                                best = std::max(best, fixedSeen ? 4.0 * match / fixedSeen : 0.0);
                            }
                            if (best >= kPartScore) part.emplace_back(best, y - P, x - P, k);
                        }
                    }
                std::sort(part.begin(), part.end(), [](const auto& a, const auto& b) { return std::get<0>(a) > std::get<0>(b); });
            }
            const size_t nPart = std::min<size_t>(3, part.size());
            for (size_t wi = 0; wi < nPart; ++wi) {
                const auto [score, wy, wx, k] = part[wi];
                // grid fit: the jitter whose seen fixed modules show the strongest dark - light split
                std::vector<std::pair<double, std::array<double, 3>>> fits;
                for (double ds : {0.97, 0.985, 1.0, 1.015, 1.03})
                    for (double dy = -0.5; dy <= 0.51; dy += 0.25)
                        for (double dx = -0.5; dx <= 0.51; dx += 0.25) {
                            const double pp = p * ds, X0 = ox + (wx + dx) * p, Y0 = oy + (wy + dy) * p;
                            cv::Mat mm = moduleMeansAxis(rot, X0, Y0, pp, n);
                            cv::Mat seen = seenModules(mm, X0, Y0, pp, inside, minRange);
                            for (int j = 0; j < k; ++j) {
                                cv::rotate(mm, mm, cv::ROTATE_90_COUNTERCLOCKWISE);
                                cv::rotate(seen, seen, cv::ROTATE_90_COUNTERCLOCKWISE);
                            }
                            fits.emplace_back(fixedContrast(mm, seen, n), std::array<double, 3>{dx, dy, ds});
                        }
                std::sort(fits.begin(), fits.end(), [](const auto& a, const auto& b) { return a.first > b.first; });
                for (size_t fi = 0; fi < std::min<size_t>(4, fits.size()); ++fi) {
                    if (late()) return false;
                    if (fits[fi].first <= 0) break;
                    const auto [dx, dy, ds] = fits[fi].second;
                    const double pp = p * ds, X0 = ox + (wx + dx) * p, Y0 = oy + (wy + dy) * p;
                    cv::Mat mm = moduleMeansAxis(rot, X0, Y0, pp, n);
                    cv::Mat seen = seenModules(mm, X0, Y0, pp, inside, minRange);
                    for (int j = 0; j < k; ++j) {
                        cv::rotate(mm, mm, cv::ROTATE_90_COUNTERCLOCKWISE);
                        cv::rotate(seen, seen, cv::ROTATE_90_COUNTERCLOCKWISE);
                    }
                    const std::string s = maskedSoftDecodeDm(mm, seen, n, known);
                    if (!s.empty() && inSerialRun(s, known)) return found(s, X0, Y0, pp);
                }
            }
        }
    } // next patch centre
    return false;
}

} // namespace cr
