// measure_tests - the Measure tool (vs_measure.h) on drawn edges, bars, holes, ovals, corners, slots, pins
// and threads whose geometry is known to a fraction of a pixel. Each pixel is the share of its area on the bright side (16 x 16 samples), as a
// camera sees an edge, then optionally blurred and given noise.
//
// Goal: 0.05 px on a clean edge (TOOL-SPECIFICATIONS.md 2.1, as the locator work), 0.02 deg for a line;
// the same 0.05 px for an ellipse's centre, a corner's lines, gap widths, pin centres and thread edges.

#include "vs_measure.h"

#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <functional>
#include <string>
#include <vector>
#include "check.h"

namespace {

int failures = 0;

void check(bool ok, const std::string& what)
{
    std::printf("%s  %s\n", ok ? "ok  " : "FAIL", what.c_str());
    if (!ok) ++failures;
}

std::string f3(double v)
{
    char b[32];
    std::snprintf(b, sizeof b, "%.4f", v);
    return b;
}

constexpr double kPi = 3.14159265358979323846;

// inside(x, y) for a point in pixel coordinates (pixel (i, j) covers [i, i+1) x [j, j+1))
cv::Mat draw(int w, int h, const std::function<bool(double, double)>& inside, int lo = 40, int hi = 200, double blur = 0, double noise = 0, int seed = 1)
{
    cv::Mat f(h, w, CV_64F);
    constexpr int S = 16;
    for (int y = 0; y < h; ++y)
        for (int x = 0; x < w; ++x) {
            int n = 0;
            for (int j = 0; j < S; ++j)
                for (int i = 0; i < S; ++i) n += inside(x + (i + 0.5) / S, y + (j + 0.5) / S);
            f.at<double>(y, x) = lo + (hi - lo) * n / double(S * S);
        }
    if (blur > 0) cv::GaussianBlur(f, f, cv::Size(), blur);
    if (noise > 0) {
        cv::Mat nz(f.size(), CV_64F);
        cv::RNG rng(static_cast<uint64_t>(seed));
        rng.fill(nz, cv::RNG::NORMAL, 0, noise);
        f += nz;
    }
    cv::Mat out;
    f.convertTo(out, CV_8U); // rounds and saturates
    return out;
}

// pixel (i, j) has its centre at (i + 0.5, j + 0.5) in draw()'s coordinates but at (i, j) for the tool
constexpr double kHalf = 0.5;

// a result ready for the library: zeroed, struct_size set
template <class R> R result()
{
    R r{};
    r.struct_size = sizeof(R);
    return r;
}

vs_meas_caliper_result caliper(const cv::Mat& img, vs_meas_box b, int mode = VS_MEAS_SINGLE, int pol = VS_MEAS_ANY, const vs_meas_calib* cal = nullptr,
                               int* count = nullptr)
{
    vs_meas_caliper_params p;
    vs_meas_default_caliper(&p);
    p.mode = mode;
    p.polarity = pol;
    vs_meas_caliper_result r = result<vs_meas_caliper_result>();
    const int n = vs_meas_caliper(img.data, img.cols, img.rows, static_cast<int>(img.step), &b, &p, cal, nullptr, 0, &r);
    if (count) *count = n;
    return r;
}

// a straight edge through (px, py) at angle deg (the edge's normal points to the bright side)
void edgeAccuracy()
{
    for (double deg : {0.0, 10.0, 33.0, 90.0}) {
        double worst = 0;
        for (int k = 0; k < 20; ++k) {
            const double e = k * 0.05 + 0.013; // the edge's distance from the image middle (60, 60) along its normal
            const double a = deg * kPi / 180;
            const double nx = std::cos(a), ny = -std::sin(a); // normal to the bright side, counter-clockwise on the screen
            const cv::Mat img = draw(120, 120, [&](double x, double y) { return (x - 60) * nx + (y - 60) * ny >= e; });
            // a box along the normal centred on the edge: the edge lies at its middle, 20 px from its start
            const vs_meas_box b{static_cast<float>(60 + e * nx - kHalf), static_cast<float>(60 + e * ny - kHalf), 40.f, 20.f, static_cast<float>(deg)};
            const vs_meas_caliper_result r = caliper(img, b, VS_MEAS_SINGLE, VS_MEAS_DARK_TO_LIGHT);
            const double err = r.found ? std::abs(r.edge0.pos - 20.0) : 99;
            worst = std::max(worst, err);
        }
        check(worst <= 0.05, "clean edge at " + f3(deg) + " deg: worst error " + f3(worst) + " px (<= 0.05)");
    }
}

void blurredNoisy()
{
    // a soft (blurred 1.2 px) edge with noise 3 grey levels: mean error and 3 sigma over 40 images
    std::vector<double> errs;
    for (int seed = 1; seed <= 40; ++seed) {
        const double e = 50.37;
        const cv::Mat img = draw(100, 60, [&](double x, double) { return x >= e; }, 40, 200, 1.2, 3, seed);
        const vs_meas_box b{50, 30, 40, 20, 0};
        const vs_meas_caliper_result r = caliper(img, b);
        errs.push_back(r.found ? r.edge0.x - (e - kHalf) : 99);
    }
    double m = 0, s = 0;
    for (double e : errs) m += e;
    m /= errs.size();
    for (double e : errs) s += (e - m) * (e - m);
    s = std::sqrt(s / errs.size());
    check(std::abs(m) <= 0.05, "soft noisy edge: mean error " + f3(m) + " px (<= 0.05)");
    check(3 * s <= 0.1, "soft noisy edge: 3 sigma " + f3(3 * s) + " px (<= 0.1, spec repeatability)");
}

void polarityAndPairs()
{
    // a bright bar from 40.3 to 52.67 on dark
    const double l = 40.3, rr = 52.67;
    const cv::Mat img = draw(100, 60, [&](double x, double) { return x >= l && x < rr; });
    const vs_meas_box b{46, 30, 40, 20, 0};
    vs_meas_caliper_result d2l = caliper(img, b, VS_MEAS_SINGLE, VS_MEAS_DARK_TO_LIGHT);
    vs_meas_caliper_result l2d = caliper(img, b, VS_MEAS_SINGLE, VS_MEAS_LIGHT_TO_DARK);
    check(d2l.found && std::abs(d2l.edge0.x - (l - kHalf)) <= 0.05 && d2l.edge0.contrast > 0, "dark-to-light picks the left edge at " + f3(d2l.edge0.x));
    check(l2d.found && std::abs(l2d.edge0.x - (rr - kHalf)) <= 0.05 && l2d.edge0.contrast < 0, "light-to-dark picks the right edge at " + f3(l2d.edge0.x));
    check(std::abs(std::abs(d2l.edge0.contrast) - 160) <= 8, "contrast of a 160-grey step: " + f3(d2l.edge0.contrast));
    vs_meas_caliper_result any = caliper(img, b);
    check(any.found && any.ambiguous, "polarity any on a bar: two equal edges flagged ambiguous");
    vs_meas_caliper_result pr = caliper(img, b, VS_MEAS_PAIR, VS_MEAS_DARK_TO_LIGHT);
    check(pr.found && !pr.ambiguous && std::abs(pr.width_px - (rr - l)) <= 0.05, "pair width " + f3(pr.width_px) + " (true " + f3(rr - l) + ")");
    check(std::abs(pr.position - ((l + rr) / 2 - kHalf - 26)) <= 0.05, "pair position " + f3(pr.position));
    vs_meas_caliper_result none = caliper(img, b, VS_MEAS_PAIR, VS_MEAS_LIGHT_TO_DARK);
    check(!none.found, "a dark bar asked for on a bright bar: not found");

    vs_meas_calib cal{};
    cal.mm_per_px = 0.05f;
    vs_meas_caliper_result mm = caliper(img, b, VS_MEAS_PAIR, VS_MEAS_DARK_TO_LIGHT, &cal);
    check(std::abs(mm.width_mm - mm.width_px * 0.05f) < 1e-4, "width in mm = px x mm per px: " + f3(mm.width_mm));

    // an edge 1.2 px from the box's start and one 0.8 px before its end are both found (no blind ends)
    const cv::Mat endEdge = draw(100, 60, [&](double x, double) { return x >= 31.7; });
    vs_meas_caliper_result atStart = caliper(endEdge, vs_meas_box{50, 30, 40, 20, 0});
    check(atStart.found && std::abs(atStart.edge0.pos - 1.2) <= 0.05, "edge near the box's start at " + f3(atStart.edge0.pos) + " (true 1.2)");
    const cv::Mat endEdge2 = draw(100, 60, [&](double x, double) { return x >= 69.7; });
    vs_meas_caliper_result atEnd = caliper(endEdge2, vs_meas_box{50, 30, 40, 20, 0});
    check(atEnd.found && std::abs(atEnd.edge0.pos - 39.2) <= 0.05, "edge near the box's end at " + f3(atEnd.edge0.pos) + " (true 39.2)");

    const cv::Mat flat(60, 100, CV_8U, cv::Scalar(120));
    vs_meas_caliper_result fl = caliper(flat, b);
    check(!fl.found && fl.candidates == 0, "flat image: no edge");
    int n = 0;
    caliper(img, vs_meas_box{90, 30, 40, 20, 0}, VS_MEAS_SINGLE, VS_MEAS_ANY, nullptr, &n);
    check(n == -2, "box leaving the image: -2");
}

void lineFit()
{
    // edge through (100, 100) turned 7 deg, bright below; one dirt blob breaks it
    const double deg = 7, a = deg * kPi / 180;
    const double dx = std::cos(a), dy = -std::sin(a), nx = -dy, ny = dx; // along, and the normal (down for 0 deg)
    const double c0 = 100.0 * nx + 100.0 * ny + 0.31;
    const cv::Mat img = draw(220, 200, [&](double x, double y) {
        if (std::hypot(x - 150, y - 94) < 6) return true; // a blob on the dark side
        return x * nx + y * ny >= c0;
    });
    vs_meas_line_params p;
    vs_meas_default_line(&p);
    p.x0 = static_cast<float>(100 - 80 * dx - kHalf);
    p.y0 = static_cast<float>(100 - 80 * dy - kHalf);
    p.x1 = static_cast<float>(100 + 80 * dx - kHalf);
    p.y1 = static_cast<float>(100 + 80 * dy - kHalf);
    p.calipers = 20;
    p.caliper.polarity = VS_MEAS_DARK_TO_LIGHT;
    float pts[40];
    vs_meas_line_result r = result<vs_meas_line_result>();
    check(vs_meas_line(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, pts, 20, &r) == 0, "line runs");
    const double off = (r.mx0 + kHalf) * nx + (r.my0 + kHalf) * ny - c0, off1 = (r.mx1 + kHalf) * nx + (r.my1 + kHalf) * ny - c0;
    check(r.found && r.points_used >= 18 && r.points_used < r.points_found, "line: " + std::to_string(r.points_used) + " of " +
          std::to_string(r.points_found) + " points used (the blob left out)");
    check(std::abs(r.angle_deg - deg) <= 0.02, "line angle " + f3(r.angle_deg) + " deg (true 7, <= 0.02)");
    check(std::abs(off) <= 0.05 && std::abs(off1) <= 0.05, "line offset at its ends " + f3(off) + " / " + f3(off1) + " px (<= 0.05)");
    check(r.rms_px < 0.05, "line rms of the used points " + f3(r.rms_px));
    check(r.straightness_px > 4, "the blob left out of the fit still shows in straightness: " + f3(r.straightness_px));

    p.fit = VS_MEAS_FIT_LEAST_SQUARES;
    vs_meas_line_result ls = result<vs_meas_line_result>();
    vs_meas_line(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, nullptr, 0, &ls);
    check(ls.points_used == ls.points_found && ls.straightness_px > 1, "least squares keeps the blob: straightness " + f3(ls.straightness_px));
}

void circleFit()
{
    const double cx = 100.3, cy = 120.7, rad = 40.25;
    const cv::Mat img = draw(220, 240, [&](double x, double y) { return std::hypot(x - cx, y - cy) < rad; }, 200, 40); // dark hole on bright
    vs_meas_circle_params p;
    vs_meas_default_circle(&p);
    p.cx = 98; p.cy = 122; p.r_inner = 30; p.r_outer = 52;
    p.caliper.polarity = VS_MEAS_DARK_TO_LIGHT; // outward: dark hole to bright
    vs_meas_circle_result r = result<vs_meas_circle_result>();
    check(vs_meas_circle(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, nullptr, 0, &r) == 0, "circle runs");
    check(r.found && r.points_used == 36, "circle: " + std::to_string(r.points_used) + " of 36 points");
    check(std::hypot(r.cx - (cx - kHalf), r.cy - (cy - kHalf)) <= 0.05, "circle centre off by " + f3(std::hypot(r.cx - (cx - kHalf), r.cy - (cy - kHalf))) + " px");
    check(std::abs(r.radius_px - rad) <= 0.05, "circle radius " + f3(r.radius_px) + " (true " + f3(rad) + ")");
    check(r.roundness_px < 0.15, "circle roundness " + f3(r.roundness_px));
    p.inward = 1;
    p.caliper.polarity = VS_MEAS_LIGHT_TO_DARK; // inward: bright to the dark hole
    vs_meas_circle_result in = result<vs_meas_circle_result>();
    vs_meas_circle(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, nullptr, 0, &in);
    check(in.found && std::abs(in.radius_px - rad) <= 0.05, "inward circle radius " + f3(in.radius_px));
    p.caliper.polarity = VS_MEAS_DARK_TO_LIGHT;
    vs_meas_circle_result wrong = result<vs_meas_circle_result>();
    vs_meas_circle(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, nullptr, 0, &wrong);
    check(!wrong.found, "inward with the wrong polarity: not found");
}

void lens()
{
    vs_meas_calib c{};
    c.fx = 2400; c.fy = 2400; c.cx = 960; c.cy = 540; c.k1 = -0.12f; c.k2 = 0.03f; c.p1 = 0.001f; c.mm_per_px = 0.1f;
    double worst = 0;
    for (float x : {10.f, 500.f, 1900.f})
        for (float y : {5.f, 540.f, 1070.f}) {
            float mx, my, ix, iy;
            vs_meas_to_measure(&c, x, y, &mx, &my);
            vs_meas_to_image(&c, mx, my, &ix, &iy);
            worst = std::max(worst, static_cast<double>(std::hypot(ix - x, iy - y)));
        }
    check(worst < 0.01, "lens model: image -> measure -> image within " + f3(worst) + " px");
    float mx, my;
    vs_meas_to_measure(nullptr, 12.5f, 7.f, &mx, &my);
    check(mx == 12.5f && my == 7.f, "no calibration: measure space is image pixels");
}

void timing()
{
    const cv::Mat img = draw(100, 60, [&](double x, double) { return x >= 50.37; });
    const vs_meas_box b{50, 30, 40, 20, 12};
    vs_meas_caliper_params p;
    vs_meas_default_caliper(&p);
    vs_meas_caliper_result r = result<vs_meas_caliper_result>();
    const auto t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < 2000; ++i) vs_meas_caliper(img.data, img.cols, img.rows, static_cast<int>(img.step), &b, &p, nullptr, nullptr, 0, &r);
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / 2000;
    std::printf("info  caliper 40 x 20 px: %.4f ms (spec 0.2)\n", ms);
    check(VS_TIME_BUDGET(ms < 0.2), "caliper under 0.2 ms");
}

void resultSize()
{
    const cv::Mat img = draw(100, 60, [&](double x, double) { return x >= 50.37; });
    const vs_meas_box b{50, 30, 40, 20, 0};
    vs_meas_caliper_params p;
    vs_meas_default_caliper(&p);
    vs_meas_caliper_result r{};
    check(vs_meas_caliper(img.data, img.cols, img.rows, static_cast<int>(img.step), &b, &p, nullptr, nullptr, 0, &r) == -1,
          "a result without struct_size is refused");
    r.struct_size = sizeof r - 4;
    check(vs_meas_caliper(img.data, img.cols, img.rows, static_cast<int>(img.step), &b, &p, nullptr, nullptr, 0, &r) == -1,
          "a result smaller than the library's is refused");
    // a caller built against a larger (newer) header: the library fills its own size and says so
    struct Bigger {
        vs_meas_caliper_result r;
        float later;
    } big{};
    big.r.struct_size = sizeof big;
    big.later = 7;
    const int n = vs_meas_caliper(img.data, img.cols, img.rows, static_cast<int>(img.step), &b, &p, nullptr, nullptr, 0, &big.r);
    check(n >= 1 && big.r.found && big.r.struct_size == static_cast<int>(sizeof(vs_meas_caliper_result)) && big.later == 7,
          "a larger result: filled to the library's size, struct_size " + std::to_string(big.r.struct_size));
}

// an oval: centre, semi-axes A (along ang, counter-clockwise on the screen) and B, in draw() coordinates
bool insideEllipse(double x, double y, double cx, double cy, double A, double B, double ang)
{
    const double a = ang * kPi / 180, dx = x - cx, dy = y - cy;
    const double s = dx * std::cos(a) - dy * std::sin(a), t = dx * std::sin(a) + dy * std::cos(a);
    return (s / A) * (s / A) + (t / B) * (t / B) < 1;
}

void ellipseFit()
{
    const double cx = 110.3, cy = 95.6, A = 45.2, B = 28.7;
    for (double ang : {25.0, -60.0, 90.0}) {
        const cv::Mat img = draw(220, 200, [&](double x, double y) { return insideEllipse(x, y, cx, cy, A, B, ang); }, 200, 40); // dark oval
        vs_meas_circle_params p;
        vs_meas_default_circle(&p);
        p.cx = 108; p.cy = 97; p.r_inner = 18; p.r_outer = 56;
        p.caliper.polarity = VS_MEAS_DARK_TO_LIGHT;
        vs_meas_ellipse_result r = result<vs_meas_ellipse_result>();
        const int rc = vs_meas_ellipse(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, nullptr, 0, &r);
        const double dc = std::hypot(r.cx - (cx - kHalf), r.cy - (cy - kHalf));
        double da = r.angle_deg - ang;
        while (da > 90) da -= 180;
        while (da <= -90) da += 180;
        check(rc == 0 && r.found && r.points_used >= 34, "ellipse at " + f3(ang) + " deg: " + std::to_string(r.points_used) + " of 36 points");
        check(dc <= 0.05, "ellipse centre off by " + f3(dc) + " px (<= 0.05)");
        check(std::abs(r.major_px - 2 * A) <= 0.1 && std::abs(r.minor_px - 2 * B) <= 0.1,
              "ellipse axes " + f3(r.major_px) + " x " + f3(r.minor_px) + " (true " + f3(2 * A) + " x " + f3(2 * B) + ", <= 0.1)");
        check(std::abs(da) <= 0.1, "ellipse angle " + f3(r.angle_deg) + " deg (true " + f3(ang) + ", <= 0.1)");
        check(r.fit_error_px < 0.2 && r.roundness_px < 0.3, "ellipse fit error " + f3(r.fit_error_px) + ", roundness " + f3(r.roundness_px));
    }
    // a blob on the rim is left out of the fit but shows in the fit error
    const cv::Mat img = draw(220, 200, [&](double x, double y) {
        if (std::hypot(x - (cx + A), y - cy) < 5) return true;
        return insideEllipse(x, y, cx, cy, A, B, 0);
    }, 200, 40);
    vs_meas_circle_params p;
    vs_meas_default_circle(&p);
    p.cx = 110; p.cy = 96; p.r_inner = 18; p.r_outer = 56;
    p.caliper.polarity = VS_MEAS_DARK_TO_LIGHT;
    vs_meas_ellipse_result r = result<vs_meas_ellipse_result>();
    vs_meas_ellipse(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, nullptr, 0, &r);
    check(r.found && r.points_used < r.points_found && std::abs(r.major_px - 2 * A) <= 0.1,
          "ellipse with a blob: " + std::to_string(r.points_used) + " of " + std::to_string(r.points_found) + " used, major " + f3(r.major_px));
    check(r.fit_error_px > 2 && r.roundness_px > 2, "the blob shows in the fit error " + f3(r.fit_error_px) + " and roundness " + f3(r.roundness_px));
    // a round hole: axes equal
    const cv::Mat round = draw(220, 200, [&](double x, double y) { return std::hypot(x - cx, y - cy) < 40.25; }, 200, 40);
    vs_meas_ellipse_result rr = result<vs_meas_ellipse_result>();
    vs_meas_ellipse(round.data, round.cols, round.rows, static_cast<int>(round.step), &p, nullptr, nullptr, 0, &rr);
    check(rr.found && std::abs(rr.major_px - 80.5) <= 0.1 && std::abs(rr.minor_px - 80.5) <= 0.1,
          "round hole as an ellipse: " + f3(rr.major_px) + " x " + f3(rr.minor_px) + " (true 80.5)");
    // a partial arc (spans of 100 to 220 deg): the fit comes out near the true oval
    for (double span : {100.0, 140.0, 220.0}) {
        const cv::Mat arc = draw(220, 200, [&](double x, double y) {
            return insideEllipse(x, y, cx, cy, A, B, 25);
        }, 200, 40);
        vs_meas_circle_params q;
        vs_meas_default_circle(&q);
        q.cx = 108; q.cy = 97; q.r_inner = 18; q.r_outer = 56;
        q.start_deg = 10; q.span_deg = static_cast<float>(span);
        q.caliper.polarity = VS_MEAS_DARK_TO_LIGHT;
        vs_meas_ellipse_result pr = result<vs_meas_ellipse_result>();
        vs_meas_ellipse(arc.data, arc.cols, arc.rows, static_cast<int>(arc.step), &q, nullptr, nullptr, 0, &pr);
        check(pr.found && std::abs(pr.major_px - 2 * A) <= 2.0 && std::abs(pr.minor_px - 2 * B) <= 2.0 &&
                  std::hypot(pr.cx - (cx - kHalf), pr.cy - (cy - kHalf)) <= 1.5,
              "partial arc " + f3(span) + " deg: axes " + f3(pr.major_px) + " x " + f3(pr.minor_px) + " centre " + f3(pr.cx) + "," + f3(pr.cy) + " used " + std::to_string(pr.points_used) + "/" + std::to_string(pr.points_found) +
                  " (true " + f3(2 * A) + " x " + f3(2 * B) + ")");
    }
    // an edge of a far larger circle on a short arc is no oval of this ring: the fit may not leave the ring
    {
        const cv::Mat edge = draw(220, 200, [&](double x, double y) { return std::hypot(x - (cx - 170), y - cy) < 200.3; }, 200, 40);
        vs_meas_circle_params q;
        vs_meas_default_circle(&q);
        q.cx = 108; q.cy = 97; q.r_inner = 18; q.r_outer = 56;
        q.start_deg = -30; q.span_deg = 60;
        q.caliper.polarity = VS_MEAS_DARK_TO_LIGHT;
        vs_meas_ellipse_result er = result<vs_meas_ellipse_result>();
        vs_meas_ellipse(edge.data, edge.cols, edge.rows, static_cast<int>(edge.step), &q, nullptr, nullptr, 0, &er);
        check(!er.found, "a nearly straight edge on a short arc: no oval that leaves the ring");
    }
    const cv::Mat flat(200, 220, CV_8U, cv::Scalar(120));
    vs_meas_ellipse_result fl = result<vs_meas_ellipse_result>();
    vs_meas_ellipse(flat.data, flat.cols, flat.rows, static_cast<int>(flat.step), &p, nullptr, nullptr, 0, &fl);
    check(!fl.found, "no oval: not found");
}

// a bright wedge with its tip at P between the rays at angles t1 and t2 (counter-clockwise on the screen, t1 < t2 < t1 + 180)
void cornerFit()
{
    const double px = 70.3, py = 130.6;
    for (auto [t1, t2] : {std::pair<double, double>{8, 98}, {-20, 55}}) {
        const double a1 = t1 * kPi / 180, a2 = t2 * kPi / 180;
        const double e1x = std::cos(a1), e1y = -std::sin(a1), e2x = std::cos(a2), e2y = -std::sin(a2);
        const double cr = e1x * e2y - e1y * e2x;
        const cv::Mat img = draw(200, 200, [&](double x, double y) {
            const double dx = x - px, dy = y - py, s = (dx * e2y - dy * e2x) / cr, t = (e1x * dy - e1y * dx) / cr;
            return s >= 0 && t >= 0;
        });
        vs_meas_corner_params p;
        vs_meas_default_corner(&p);
        const auto place = [&](vs_meas_line_params& l, double ex, double ey) {
            l.x0 = static_cast<float>(px + 15 * ex - kHalf); l.y0 = static_cast<float>(py + 15 * ey - kHalf);
            l.x1 = static_cast<float>(px + 85 * ex - kHalf); l.y1 = static_cast<float>(py + 85 * ey - kHalf);
            l.calipers = 20;
        };
        place(p.line1, e1x, e1y);
        place(p.line2, e2x, e2y);
        vs_meas_corner_result r = result<vs_meas_corner_result>();
        const int rc = vs_meas_corner(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, &r);
        const double d = std::hypot(r.x - (px - kHalf), r.y - (py - kHalf));
        check(rc == 0 && r.found && !r.parallel, "corner of " + f3(t2 - t1) + " deg found");
        check(d <= 0.1, "corner off by " + f3(d) + " px (<= 0.1)");
        check(std::abs(r.angle_deg - (t2 - t1)) <= 0.05, "corner angle " + f3(r.angle_deg) + " (true " + f3(t2 - t1) + ", <= 0.05)");
        check(r.line1.struct_size == static_cast<int>(sizeof(vs_meas_line_result)) && r.line1.points_used == 20 && r.line2.points_used == 20,
              "corner lines filled, 20 + 20 points");
        p.line2 = p.line1;
        p.line2.x0 += 5; p.line2.x1 += 5; // the same edge further along: parallel
        vs_meas_corner_result pr = result<vs_meas_corner_result>();
        vs_meas_corner(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, &pr);
        check(!pr.found && pr.parallel, "two lines on one edge: parallel, no corner");
    }
}

void gapWidths()
{
    // a dark slot on bright, its left side leaning (width 12.37 - 0.02 (y - 30)), a bridge across it over slice 16 (pixel rows 68..71)
    const auto left = [](double y) { return 40.3 + 0.02 * (y - 30); };
    const double right = 52.67;
    const cv::Mat img = draw(100, 110, [&](double x, double y) { return !(x >= left(y) && x < right) || (y >= 68 && y < 72); });
    vs_meas_gap_params p;
    vs_meas_default_gap(&p);
    p.box = {46, 50, 40, 60, 0}; // y 20 .. 80, 20 slices of 3 px
    p.caliper.polarity = VS_MEAS_LIGHT_TO_DARK;
    float w[20];
    vs_meas_gap_result r = result<vs_meas_gap_result>();
    check(vs_meas_gap(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, w, 20, &r) == 0, "gap runs");
    double worst = 0;
    int missing = 0;
    for (int i = 0; i < 20; ++i) {
        const double yc = 20 + 3 * (i + 0.5) + kHalf; // the slice's middle in draw() coordinates
        if (std::isnan(w[i])) { ++missing; continue; }
        worst = std::max(worst, std::abs(w[i] - (right - left(yc))));
    }
    check(r.found == 19 && missing == 1 && std::isnan(w[16]), "gap: the slice over the bridge has no width (" + std::to_string(r.found) + " of 20)");
    check(worst <= 0.05, "gap widths worst error " + f3(worst) + " px (<= 0.05)");
    check(r.min_index == 19 && r.max_index == 0 && r.min_px < r.mean_px && r.mean_px < r.max_px,
          "gap narrowest at slice " + std::to_string(r.min_index) + ", widest at " + std::to_string(r.max_index));
    check(std::abs(r.min_y - (20 + 3 * 19.5)) <= 0.01 && std::abs(r.min_x - ((left(20 + 3 * 19.5 + kHalf) + right) / 2 - kHalf)) <= 0.05,
          "gap narrowest mark at (" + f3(r.min_x) + ", " + f3(r.min_y) + ")");
    // a burr inside the slot at 60 % of the slot's contrast: the slices across it give no width
    cv::Mat burr = img.clone();
    burr(cv::Rect(44, 40, 3, 7)).setTo(40 + 0.6 * 160);
    vs_meas_gap_result br = result<vs_meas_gap_result>();
    float bw[20];
    vs_meas_gap(burr.data, burr.cols, burr.rows, static_cast<int>(burr.step), &p, nullptr, bw, 20, &br);
    int lost = 0;
    for (int i = 0; i < 20; ++i) lost += std::isnan(bw[i]) ? 1 : 0;
    check(lost >= 3 && std::isnan(bw[7]) && br.found == 20 - lost, "a burr in the gap: " + std::to_string(lost) + " slices without an open width");
    p.box.height = 10;
    check(vs_meas_gap(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, nullptr, 0, &r) == -1, "slices under 1 px: refused");
}

void pinPitch()
{
    // 12 bright pins on dark, pitch 10.25, width 4.6; pin 7 missing, pin 3 moved 0.8 px
    std::vector<double> lefts;
    for (int i = 0; i < 12; ++i) lefts.push_back(20.4 + i * 10.25 + (i == 3 ? 0.8 : 0));
    const cv::Mat img = draw(160, 60, [&](double x, double y) {
        if (y < 15 || y >= 45) return false;
        for (int i = 0; i < 12; ++i)
            if (i != 7 && x >= lefts[static_cast<size_t>(i)] && x < lefts[static_cast<size_t>(i)] + 4.6) return true;
        return false;
    });
    vs_meas_pitch_params p;
    vs_meas_default_pitch(&p);
    p.box = {77, 30, 130, 20, 0}; // from x 12
    vs_meas_pin pins[16];
    vs_meas_pitch_result r = result<vs_meas_pitch_result>();
    const int n = vs_meas_pitch(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, pins, 16, &r);
    check(n == 11 && r.count == 11 && r.unpaired == 0, "pins: " + std::to_string(n) + " found (11 drawn), " + std::to_string(r.unpaired) + " edges unpaired");
    double worstPos = 0, worstW = 0;
    for (int k = 0, i = 0; k < n && i < 12; ++i) {
        if (i == 7) continue;
        const double c = lefts[static_cast<size_t>(i)] + 2.3 - kHalf;
        worstPos = std::max(worstPos, std::abs(pins[k].x - c) + std::abs(pins[k].pos - (c - 12)));
        worstW = std::max(worstW, std::abs(pins[k].width_px - 4.6));
        ++k;
    }
    check(worstPos <= 0.05, "pin centres worst error " + f3(worstPos) + " px (<= 0.05)");
    check(worstW <= 0.05, "pin widths worst error " + f3(worstW) + " px (<= 0.05)");
    check(std::abs(r.max_pitch_px - 20.5) <= 0.05 && std::abs(r.min_pitch_px - (10.25 - 0.8)) <= 0.05,
          "pitches " + f3(r.min_pitch_px) + " .. " + f3(r.max_pitch_px) + " (true 9.45 .. 20.5: the moved pin and the missing one)");
    p.polarity = VS_MEAS_LIGHT_TO_DARK; // dark pins asked for: the gaps between bright pins pair up instead
    vs_meas_pitch_result d = result<vs_meas_pitch_result>();
    vs_meas_pitch(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, nullptr, 0, &d);
    check(d.count == 10 && d.unpaired == 2, "dark pins on bright pins: " + std::to_string(d.count) + " gaps, " + std::to_string(d.unpaired) + " unpaired");
}

void edgeCount()
{
    // a thread: bright and dark stripes of period 6.4 px, blurred 1 px, 15 periods from x 20.3
    const double per = 6.4, x0 = 20.3;
    const cv::Mat img = draw(140, 40, [&](double x, double) {
        if (x < x0 || x >= x0 + 15 * per) return false;
        return std::fmod(x - x0, per) < per / 2;
    }, 40, 200, 1.0);
    vs_meas_edges_params p;
    vs_meas_default_edges(&p);
    p.box = {68, 20, 116, 10, 0}; // x 10 .. 126
    p.polarity = VS_MEAS_DARK_TO_LIGHT;
    vs_meas_edge e[40];
    vs_meas_edges_result r = result<vs_meas_edges_result>();
    const int n = vs_meas_edges(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, e, 40, &r);
    double worst = 0, ends = 0;
    for (int i = 0; i < n && i < 15; ++i) {
        const double err = std::abs(e[i].x - (x0 + i * per - kHalf));
        if (i == 0 || i == 14) ends = std::max(ends, err);
        else worst = std::max(worst, err);
    }
    check(n == 15, "thread: " + std::to_string(n) + " rising edges (15)");
    check(worst <= 0.05, "thread edges inside the row: worst error " + f3(worst) + " px (<= 0.05)");
    // the end edges' lean (0.35 px here before) is taken off (vs_measure.h)
    check(ends <= 0.1, "thread end edges (blurred 1 px, 3.2 px apart): worst error " + f3(ends) + " px (<= 0.1)");
    check(std::abs(r.mean_spacing_px - per) <= 0.01, "thread mean spacing " + f3(r.mean_spacing_px) + " (from the end edges; range " +
          f3(r.min_spacing_px) + " .. " + f3(r.max_spacing_px) + ")");
    check(std::abs(r.period_px - per) <= 0.1, "thread period from the autocorrelation " + f3(r.period_px) + " (true 6.4, <= 0.1)");
    p.polarity = VS_MEAS_ANY;
    vs_meas_edges_result a = result<vs_meas_edges_result>();
    vs_meas_edges(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, nullptr, 0, &a);
    check(a.count == 30 && std::abs(a.mean_spacing_px - per / 2) <= 0.01 && std::abs(a.period_px - per) <= 0.1,
          "any polarity: " + std::to_string(a.count) + " edges, spacing " + f3(a.mean_spacing_px) + ", period still " + f3(a.period_px));
    const cv::Mat flat(40, 140, CV_8U, cv::Scalar(120));
    vs_meas_edges_result f = result<vs_meas_edges_result>();
    check(vs_meas_edges(flat.data, flat.cols, flat.rows, static_cast<int>(flat.step), &p, nullptr, nullptr, 0, &f) == 0 && f.period_px == 0,
          "flat: no edges, no period");
}

// a row as a camera sees it: the stripes blurred first (Gaussian of sigma b, or a defocus disk of radius b), then
// area-sampled by the pixels, so every edge has the same shape at whatever sub-pixel place
cv::Mat cameraRow(double x0, double per, int crests, bool disk, double b)
{
    const auto esf = [&](double t) {
        if (disk) {
            const double z = std::clamp(t / b, -1.0, 1.0);
            return 0.5 + (z * std::sqrt(1 - z * z) + std::asin(z)) / kPi;
        }
        return 0.5 * std::erfc(-t / (b * std::sqrt(2.0)));
    };
    cv::Mat img(40, 140, CV_8U);
    for (int x = 0; x < 140; ++x) {
        double v = 0;
        for (int i = 0; i < 32; ++i) {
            const double t = x + (i + 0.5) / 32;
            double s = 40;
            for (int k = 0; k < crests; ++k) s += 160 * (esf(t - (x0 + k * per)) - esf(t - (x0 + k * per + per / 2)));
            v += s / 32;
        }
        for (int y = 0; y < 40; ++y) img.at<uint8_t>(y, x) = cv::saturate_cast<uint8_t>(v);
    }
    return img;
}

void endEdgesAtEveryPhase()
{
    // the first and last edge of a soft row, at eight sub-pixel places: the lean (0.3 .. 0.6 px before) taken off;
    // inner edges are placed by the symmetric centroid (a few hundredths off on the closest rows)
    struct Case {
        const char* what;
        double per;
        int crests;
        bool disk;
        double b, limit, innerLimit;
    };
    for (const Case& c : {Case{"thread, 1 px blur, 3.2 px apart", 6.4, 15, false, 1.0, 0.08, 0.02},
                          Case{"lone pin 3.2 px wide, 1 px blur", 6.4, 1, false, 1.0, 0.1, 0.02},
                          Case{"thread, 2 px defocus disk, 4 px apart", 8, 12, true, 2.0, 0.05, 0.02},
                          Case{"thread, 1 px blur, 2.6 px apart", 5.2, 15, false, 1.0, 0.06, 0.02},
                          Case{"thread, 1 px blur, 2.4 px apart", 4.8, 15, false, 1.0, 0.1, 0.03},
                          Case{"thread, 1 px blur, 2.2 px apart", 4.4, 15, false, 1.0, 0.11, 0.04},
                          Case{"thread, 1.5 px defocus disk, 2.4 px apart", 4.8, 15, true, 1.5, 0.06, 0.02},
                          Case{"lone pin 4 px wide, 1.5 px blur", 8, 1, false, 1.5, 0.08, 0.02}}) {
        double worst = 0, inner = 0;
        bool counted = true;
        for (int k = 0; k < 8; ++k) {
            const double x0 = 20 + k / 8.0;
            const cv::Mat img = cameraRow(x0, c.per, c.crests, c.disk, c.b);
            vs_meas_edges_params p;
            vs_meas_default_edges(&p);
            p.box = {68, 20, 116, 10, 0};
            vs_meas_edge e[80];
            vs_meas_edges_result r = result<vs_meas_edges_result>();
            const int n = vs_meas_edges(img.data, img.cols, img.rows, static_cast<int>(img.step), &p, nullptr, e, 80, &r);
            counted = counted && n == 2 * c.crests;
            for (int i = 0; i < n && i < 2 * c.crests; ++i) {
                const double err = std::abs(e[i].x - (x0 + (i / 2) * c.per + (i % 2) * c.per / 2 - kHalf));
                if (i == 0 || i == n - 1) worst = std::max(worst, err);
                else inner = std::max(inner, err);
            }
        }
        check(counted && worst <= c.limit && inner <= c.innerLimit, std::string(c.what) + ": end edges worst " + f3(worst) + " px (<= " + f3(c.limit) +
                                                              "), inner " + f3(inner) + " px");
    }
}

double msPer(int reps, const std::function<void()>& f)
{
    const auto t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < reps; ++i) f();
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / reps;
}

void newToolTiming()
{
    const cv::Mat oval = draw(220, 200, [&](double x, double y) { return insideEllipse(x, y, 110.3, 95.6, 45.2, 28.7, 25); }, 200, 40);
    vs_meas_circle_params cp;
    vs_meas_default_circle(&cp);
    cp.cx = 108; cp.cy = 97; cp.r_inner = 18; cp.r_outer = 56;
    vs_meas_ellipse_result er = result<vs_meas_ellipse_result>();
    const double ell = msPer(200, [&] { vs_meas_ellipse(oval.data, oval.cols, oval.rows, static_cast<int>(oval.step), &cp, nullptr, nullptr, 0, &er); });

    const cv::Mat slot = draw(100, 110, [&](double x, double) { return !(x >= 40.3 && x < 52.67); });
    vs_meas_gap_params gp;
    vs_meas_default_gap(&gp);
    gp.box = {46, 50, 40, 60, 0};
    vs_meas_gap_result gr = result<vs_meas_gap_result>();
    const double gap = msPer(500, [&] { vs_meas_gap(slot.data, slot.cols, slot.rows, static_cast<int>(slot.step), &gp, nullptr, nullptr, 0, &gr); });

    const cv::Mat pins = draw(450, 40, [&](double x, double) { return x >= 20 && x < 420 && std::fmod(x - 20, 10) < 4.5; });
    vs_meas_pitch_params pp;
    vs_meas_default_pitch(&pp);
    pp.box = {220, 20, 410, 20, 0};
    vs_meas_pitch_result pr = result<vs_meas_pitch_result>();
    const double pitch = msPer(500, [&] { vs_meas_pitch(pins.data, pins.cols, pins.rows, static_cast<int>(pins.step), &pp, nullptr, nullptr, 0, &pr); });

    vs_meas_edges_params ep;
    vs_meas_default_edges(&ep);
    ep.box = {220, 20, 200, 10, 0};
    vs_meas_edges_result xr = result<vs_meas_edges_result>();
    const double edges = msPer(500, [&] { vs_meas_edges(pins.data, pins.cols, pins.rows, static_cast<int>(pins.step), &ep, nullptr, nullptr, 0, &xr); });

    std::printf("info  ellipse 36 calipers %.3f ms (spec 2), gap 20 slices %.3f ms (spec 1), %d pins %.3f ms (spec 1), %d edges %.3f ms (spec 0.5)\n",
                ell, gap, pr.count, pitch, xr.count, edges);
    check(er.found && VS_TIME_BUDGET(ell < 2), "ellipse under 2 ms");
    check(gr.found == 20 && gap < 1, "gap of 20 slices under 1 ms");
    check(pr.count == 40 && VS_TIME_BUDGET(pitch < 1), "40 pins under 1 ms");
    check(edges < 0.5, "edge count under 0.5 ms");
}

} // namespace

int main()
{
    edgeAccuracy();
    blurredNoisy();
    polarityAndPairs();
    lineFit();
    circleFit();
    lens();
    timing();
    resultSize();
    ellipseFit();
    cornerFit();
    gapWidths();
    pinPitch();
    edgeCount();
    endEdgesAtEveryPhase();
    newToolTiming();
    std::printf("%s (%d failed)\n", failures ? "FAILED" : "all passed", failures);
    return failures ? 1 : 0;
}
