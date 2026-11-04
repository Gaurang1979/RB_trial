// locate_tool.cpp - the Locate tool (vs_locate.h): find a taught grey pattern, its shift and turn.

#include "vs_locate.h"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <exception>
#include <new>
#include <string>
#include <vector>

namespace {

using Clock = std::chrono::steady_clock;
constexpr double kPi = 3.14159265358979323846;
constexpr double kCoarseSide = 64.0;   // pattern size (px) of the coarse search
constexpr double kKeep = 0.7;          // central part of a turned pattern used (corners are filled in)
constexpr double kSmooth = 1.0;        // Gaussian sigma before edge gradients, at teach and run alike
constexpr int kMaxEdgePoints = 800;    // edge model size (more adds time, not accuracy)

// as cv::getRotationMatrix2D (moved out of imgproc in OpenCV 5): counter-clockwise on screen, about c
cv::Matx23d rotation(cv::Point2d c, double deg)
{
    const double a = deg * kPi / 180.0, al = std::cos(a), be = std::sin(a);
    return {al, be, (1 - al) * c.x - be * c.y, -be, al, be * c.x + (1 - al) * c.y};
}

struct Turned {
    double deg = 0;
    cv::Mat img;          // turned, scaled, central crop
    cv::Point2d centre;   // where the pattern centre lies in img (edge coordinates)
};

Turned turned(const cv::Mat& pat, double deg, double scale)
{
    Turned t;
    t.deg = deg;
    cv::Mat r;
    cv::warpAffine(pat, r, rotation({(pat.cols - 1) / 2.0, (pat.rows - 1) / 2.0}, deg), pat.size(),
                   cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    cv::Mat s;
    if (scale < 0.999) cv::resize(r, s, {}, scale, scale, cv::INTER_AREA);
    else s = r;
    const int w = std::max(4, int(s.cols * kKeep)), h = std::max(4, int(s.rows * kKeep));
    const int x = (s.cols - w) / 2, y = (s.rows - h) / 2;
    t.img = s(cv::Rect(x, y, w, h)).clone();
    t.centre = {pat.cols * scale / 2.0 - x, pat.rows * scale / 2.0 - y};
    return t;
}

// sub-pixel offset of a peak from three samples (parabola through them)
double vertex(double l, double c, double r)
{
    const double d = l - 2 * c + r;
    return std::abs(d) < 1e-9 ? 0.0 : std::clamp(0.5 * (l - r) / d, -0.5, 0.5);
}

struct Hit {
    double score = -2;
    cv::Point loc;
    cv::Point2d sub;      // loc + sub-pixel offset
};

Hit best(const cv::Mat& img, const cv::Mat& tpl)
{
    Hit h;
    if (tpl.cols > img.cols || tpl.rows > img.rows) return h;
    cv::Mat res;
    cv::matchTemplate(img, tpl, res, cv::TM_CCOEFF_NORMED);
    double mx;
    cv::minMaxLoc(res, nullptr, &mx, nullptr, &h.loc);
    h.score = mx;
    h.sub = h.loc;
    const int x = h.loc.x, y = h.loc.y;
    if (x > 0 && x + 1 < res.cols)
        h.sub.x += vertex(res.at<float>(y, x - 1), res.at<float>(y, x), res.at<float>(y, x + 1));
    if (y > 0 && y + 1 < res.rows)
        h.sub.y += vertex(res.at<float>(y - 1, x), res.at<float>(y, x), res.at<float>(y + 1, x));
    return h;
}

// --- edge refinement: the pose from grey correlation is good to a few tenths of a pixel; a least-squares
// fit of the pattern's edge points to the image's edges brings it to a few hundredths. Each model point
// looks along its edge normal for the image edge (same polarity, sub-pixel peak of the directional
// derivative); the shift and turn that best close those gaps (point-to-line distances, Tukey weights
// so that occluded or cluttered points drop out) are solved for, and the search repeated until it stops
// moving. Edge positions do not move with brightness, contrast or a symmetric blur.

struct EdgePoint {
    cv::Point2d q;        // position relative to the pattern centre, teach image pixels
    cv::Point2d n;        // unit normal, dark to bright
    double strength;      // gradient magnitude at teach, grey levels per pixel
};

void gradients(const cv::Mat& grey, cv::Mat& gx, cv::Mat& gy)
{
    cv::Mat f;
    grey.convertTo(f, CV_32F);
    cv::GaussianBlur(f, f, {}, kSmooth, kSmooth, cv::BORDER_REPLICATE);
    cv::Sobel(f, gx, CV_32F, 1, 0, 3, 1.0 / 8, 0, cv::BORDER_REPLICATE);
    cv::Sobel(f, gy, CV_32F, 0, 1, 3, 1.0 / 8, 0, cv::BORDER_REPLICATE);
}

// bilinear sample of a float image; false outside it
bool sample(const cv::Mat& m, double x, double y, double& v)
{
    if (!(x >= 0 && y >= 0 && x < m.cols - 1 && y < m.rows - 1)) return false;
    const int x0 = int(x), y0 = int(y);
    const double fx = x - x0, fy = y - y0;
    const float* r0 = m.ptr<float>(y0) + x0;
    const float* r1 = m.ptr<float>(y0 + 1) + x0;
    v = (1 - fy) * ((1 - fx) * r0[0] + fx * r0[1]) + fy * ((1 - fx) * r1[0] + fx * r1[1]);
    return true;
}

constexpr double kStep = 0.5;   // samples along an edge normal, px

// the edge along the normal N through P: the sub-pixel peak (parabola) of the directional derivative,
// same polarity, within reach px either way. Teach and run use this same estimator, so its small bias,
// which depends on where the edge falls between pixels, cancels for a part that has not moved and
// averages out over the points of one that has. False when the peak is at the end of the reach.
bool edgeAlong(const cv::Mat& gx, const cv::Mat& gy, cv::Point2d P, cv::Point2d N, double reach, double& s, double& peak)
{
    const int ks = std::min(7, int(std::lround(reach / kStep)));
    double D[15];
    int best = 0;
    for (int k = -ks; k <= ks; ++k) {
        double vx, vy;
        const cv::Point2d Q = P + k * kStep * N;
        if (!sample(gx, Q.x, Q.y, vx) || !sample(gy, Q.x, Q.y, vy)) return false;
        D[k + ks] = vx * N.x + vy * N.y;
        if (D[k + ks] > D[best]) best = k + ks;
    }
    if (best == 0 || best == 2 * ks) return false;
    s = (best - ks + vertex(D[best - 1], D[best], D[best + 1])) * kStep;
    peak = D[best];
    return true;
}

// edge points of the pattern: ridge pixels of the gradient magnitude, moved to the sub-pixel ridge,
// the strongest one per cell so that they spread over the whole pattern. Positions are relative to
// centre (pattern pixels; the pattern centre of the full-size pattern is (cols / 2, rows / 2)).
std::vector<EdgePoint> edgeModel(const cv::Mat& pat, cv::Point2d centre)
{
    cv::Mat gx, gy, mag;
    gradients(pat, gx, gy);
    cv::magnitude(gx, gy, mag);
    std::vector<float> v(mag.begin<float>(), mag.end<float>());
    const size_t k = v.size() * 98 / 100;
    std::nth_element(v.begin(), v.begin() + std::ptrdiff_t(k), v.end());
    const double thr = std::max(3.0, 0.3 * v[k]);
    const int border = 4;   // the pattern's cut edge is not an edge of the part
    std::vector<EdgePoint> all;
    std::vector<cv::Point> px;
    for (int y = border; y < pat.rows - border; ++y)
        for (int x = border; x < pat.cols - border; ++x) {
            const double m = mag.at<float>(y, x);
            if (m < thr) continue;
            const double nx = gx.at<float>(y, x) / m, ny = gy.at<float>(y, x) / m;
            double l, r;
            if (!sample(mag, x - nx, y - ny, l) || !sample(mag, x + nx, y + ny, r) || m < l || m <= r) continue;
            // move to where the estimator sees the edge at its centre: that is where the fit at run time
            // settles, so both see the edge with the same samples
            cv::Point2d P(x, y);
            double off = 0, peak = 0;
            bool ok = true;
            for (int i = 0; i < 4 && ok; ++i) {
                ok = edgeAlong(gx, gy, P, {nx, ny}, 1.0, off, peak);
                P += off * cv::Point2d(nx, ny);
                if (std::abs(off) < 1e-3) break;
            }
            if (!ok || std::hypot(P.x - x, P.y - y) > 1.0) continue;
            all.push_back({P - centre, {nx, ny}, peak});
            px.emplace_back(x, y);
        }
    for (int cell = 1;; ++cell) {
        const int cw = pat.cols / cell + 1, ch = pat.rows / cell + 1;
        std::vector<int> pick(size_t(cw) * size_t(ch), -1);
        for (size_t i = 0; i < all.size(); ++i) {
            int& b = pick[size_t(px[i].y / cell) * size_t(cw) + size_t(px[i].x / cell)];
            if (b < 0 || all[i].strength > all[size_t(b)].strength) b = int(i);
        }
        std::vector<EdgePoint> out;
        for (int b : pick)
            if (b >= 0) out.push_back(all[size_t(b)]);
        if (int(out.size()) <= kMaxEdgePoints) return out;
    }
}

enum class Fit { Refined, NotPosed, Failed };

// fits the edge model to the image from a pose (centre c, turn deg) within about 2 px and 0.5 deg.
// NotPosed: the pattern's edges cannot fix a pose (too few, or all one way), pose untouched.
// Failed: too few of the image's edges match the pattern here, pose untouched. Either way the
// correlation pose stands and the found decision is the correlation's: the fit only refines.
Fit refine(const std::vector<EdgePoint>& model, const cv::Mat& img, cv::Point2d& c, double& deg)
{
    if (model.size() < 12) return Fit::NotPosed;
    double rad = 0;
    for (const auto& e : model) rad = std::max(rad, std::hypot(e.q.x, e.q.y));
    rad += 10;
    const cv::Rect win = cv::Rect(int(c.x - rad), int(c.y - rad), int(2 * rad) + 2, int(2 * rad) + 2) & cv::Rect(0, 0, img.cols, img.rows);
    if (win.width < 16 || win.height < 16) return Fit::Failed;
    cv::Mat gx, gy;
    gradients(img(win), gx, gy);

    cv::Point2d pc = c - cv::Point2d(win.x, win.y);   // window coordinates
    double a = deg * kPi / 180.0;
    const size_t n = model.size();
    std::vector<double> gap(n), w(n), absGap;
    std::vector<cv::Vec3d> J(n);
    double reach = 2.0;                                 // search along the normal, px either way
    size_t close = 0;                                   // points within 0.5 px of their edge, last pass
    for (int it = 0; it < 10; ++it) {
        const double ca = std::cos(a), sa = std::sin(a);
        absGap.clear();
        for (size_t i = 0; i < n; ++i) {
            const EdgePoint& e = model[i];
            const cv::Point2d P(pc.x + ca * e.q.x + sa * e.q.y, pc.y - sa * e.q.x + ca * e.q.y);
            const cv::Point2d N(ca * e.n.x + sa * e.n.y, -sa * e.n.x + ca * e.n.y);
            double peak;
            w[i] = 0;
            // an edge of the same polarity, at least 30% as strong as taught, peaking inside the reach
            if (!edgeAlong(gx, gy, P, N, reach, gap[i], peak) || peak < 0.3 * e.strength) continue;
            // derivative of the point's position with the turn a, along the normal
            const cv::Point2d dP(-sa * e.q.x + ca * e.q.y, -ca * e.q.x - sa * e.q.y);
            J[i] = {N.x, N.y, N.x * dP.x + N.y * dP.y};
            w[i] = 1;
            absGap.push_back(std::abs(gap[i]));
        }
        close = size_t(std::count_if(absGap.begin(), absGap.end(), [](double g) { return g < 0.5; }));
        if (absGap.size() < std::max<size_t>(12, n / 8)) return Fit::Failed;
        std::nth_element(absGap.begin(), absGap.begin() + std::ptrdiff_t(absGap.size() / 2), absGap.end());
        const double tukey = std::max(it < 2 ? 1.0 : 0.3, 4.685 * 1.4826 * absGap[absGap.size() / 2]);
        cv::Matx33d A = cv::Matx33d::zeros();
        cv::Vec3d b(0, 0, 0);
        double kept = 0;
        for (size_t i = 0; i < n; ++i) {
            if (w[i] == 0) continue;
            const double u = gap[i] / tukey;
            if (std::abs(u) >= 1) continue;
            const double wi = (1 - u * u) * (1 - u * u);
            A += wi * J[i] * J[i].t();
            b += wi * gap[i] * J[i];
            kept += wi;
        }
        if (kept < std::max(6.0, 0.1 * double(n))) return Fit::Failed;
        // a pattern of edges in one direction only fixes neither the shift along them nor the turn
        const cv::Matx33d S(1, 0, 0, 0, 1, 0, 0, 0, 1.0 / rad);   // turn in pixels at the rim, to compare
        cv::Vec3d ev;
        cv::eigen(S * A * S, ev);
        if (ev[2] < 1e-2 * ev[0]) return Fit::NotPosed;
        const cv::Vec3d d = A.solve(b, cv::DECOMP_CHOLESKY);
        pc += cv::Point2d(d[0], d[1]);
        a += d[2];
        if (std::hypot(d[0], d[1]) < 1e-4 && std::abs(d[2]) < 1e-6) break;
        if (it == 1) reach = 1.5;
    }
    // enough of the pattern must agree (variable print in it, such as a code or a lot number, does not)
    if (close < std::max<size_t>(12, n / 8)) return Fit::Failed;
    const cv::Point2d out = pc + cv::Point2d(win.x, win.y);
    // the fit is a refinement: a move of more than 5 px (shift plus turn at the pattern's rim; the
    // correlation is good to about 1 px and 0.7 deg) means it locked onto other edges
    if (std::hypot(out.x - c.x, out.y - c.y) + std::abs(a * 180.0 / kPi - deg) * kPi / 180.0 * (rad - 10) > 5.0) return Fit::Failed;
    c = out;
    deg = a * 180.0 / kPi;
    return Fit::Refined;
}

// scale of the fine correlation search: up to 3x the coarse one
double fineScaleFor(double coarseScale) { return std::min(1.0, coarseScale * 3.0); }

vs_loc_params sanitize(vs_loc_params p)
{
    p.struct_size = int32_t(sizeof(vs_loc_params));
    // NaN (which std::clamp passes through) takes the default; for the score limit the strictest (a
    // broken setting never loosens the check)
    p.max_angle_deg = std::isnan(p.max_angle_deg) ? 15.f : std::clamp(p.max_angle_deg, 0.f, 180.f);
    p.min_score = std::isnan(p.min_score) ? 0.99f : std::clamp(p.min_score, 0.05f, 0.99f);
    if (p.time_budget_ms <= 0) p.time_budget_ms = 50;
    if (p.search_w < 0 || p.search_h < 0) p.search_w = p.search_h = 0;
    return p;
}

} // namespace

struct vs_loc {
    vs_loc_params p{};
    cv::Mat pattern;                  // full resolution, 8-bit grey
    cv::Point2d origin;               // pattern centre in the teach image
    double coarseScale = 1, coarseStep = 1;
    std::vector<Turned> coarse;
    cv::Mat finePattern;              // pattern at the fine search scale
    std::vector<EdgePoint> edges;     // edge model for the least-squares refinement
    double fineScale = 0;
    // a large pattern is searched on a copy much smaller than the image (fine pixel over 2 image px):
    // the correlation pose is then off by more than the edge search reaches at full size, so the
    // edge fit first runs on copies at these scales (the fine search's, then about 2.5 times larger
    // each), each pose within reach of the next
    struct Level {
        double scale;
        std::vector<EdgePoint> edges;
    };
    std::vector<Level> levels;
    vs_loc_result res{};
    std::string error;

    void prepare()
    {
        coarse.clear();
        finePattern.release();
        fineScale = 0;
        edges = edgeModel(pattern, {pattern.cols / 2.0, pattern.rows / 2.0});
        const double side = std::max(pattern.cols, pattern.rows), small = std::min(pattern.cols, pattern.rows);
        coarseScale = std::min(1.0, std::max(kCoarseSide / side, 12.0 / small));
        levels.clear();
        for (double s = fineScaleFor(coarseScale); s < 0.5; s *= 2.5) {
            cv::Mat ps;
            cv::resize(pattern, ps, {}, s, s, cv::INTER_AREA);
            // the full pattern's centre (cols / 2, rows / 2) in the copy: pixel x goes to (x + 0.5) s - 0.5
            levels.push_back({s, edgeModel(ps, {(pattern.cols / 2.0 + 0.5) * s - 0.5, (pattern.rows / 2.0 + 0.5) * s - 0.5})});
        }
        // turn step: the pattern's far edge moves about 1.5 coarse pixels
        const double r = 0.5 * kKeep * side * coarseScale;
        coarseStep = std::clamp(1.5 / std::max(r, 1.0) * 180.0 / kPi, 0.5, 10.0);
        const double m = p.max_angle_deg;
        const int n = int(std::ceil(m / coarseStep));
        for (int i = -n; i <= n; ++i) coarse.push_back(turned(pattern, std::clamp(i * coarseStep, -m, m), coarseScale));
    }
};

extern "C" {

void vs_loc_default_params(vs_loc_params* p)
{
    if (!p) return;
    *p = vs_loc_params{};
    p->struct_size = int32_t(sizeof(vs_loc_params));
    p->max_angle_deg = 15.f;
    p->min_score = 0.6f;
    p->time_budget_ms = 50;
}

vs_loc* vs_loc_create(const vs_loc_params* p)
{
    vs_loc* t = new (std::nothrow) vs_loc();
    if (!t) return nullptr;
    vs_loc_params d;
    vs_loc_default_params(&d);
    t->p = sanitize(p && p->struct_size == int32_t(sizeof(vs_loc_params)) ? *p : d);
    return t;
}

void vs_loc_destroy(vs_loc* t) { delete t; }

int32_t vs_loc_set_params(vs_loc* t, const vs_loc_params* p)
{
    if (!t || !p || p->struct_size != int32_t(sizeof(vs_loc_params))) return -1;
    const bool turnChanged = p->max_angle_deg != t->p.max_angle_deg;
    t->p = sanitize(*p);
    if (turnChanged && !t->pattern.empty()) t->prepare();
    return 0;
}

int32_t vs_loc_teach(vs_loc* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                     int32_t x, int32_t y, int32_t w, int32_t h)
{
    if (!t || !pixels || width <= 0 || height <= 0 || stride < width) return -1;
    const cv::Rect r = cv::Rect(x, y, w, h) & cv::Rect(0, 0, width, height);
    if (r.width < 16 || r.height < 16) { t->error = "the pattern region is too small (16 px at least)"; return -1; }
    const cv::Mat img(height, width, CV_8U, const_cast<uint8_t*>(pixels), size_t(stride));
    t->pattern = img(r).clone();
    t->origin = {r.x + r.width / 2.0, r.y + r.height / 2.0};
    cv::Scalar mean, sd;
    cv::meanStdDev(t->pattern, mean, sd);
    if (sd[0] < 3) { t->error = "the pattern region is flat: mark something with detail (print, code, an edge)"; t->pattern.release(); return -1; }
    t->prepare();
    return 0;
}

int32_t vs_loc_set_pattern(vs_loc* t, const uint8_t* pattern, int32_t w, int32_t h, int32_t stride, float origin_x, float origin_y)
{
    if (!t || !pattern || w < 16 || h < 16 || stride < w) return -1;
    t->pattern = cv::Mat(h, w, CV_8U, const_cast<uint8_t*>(pattern), size_t(stride)).clone();
    t->origin = {origin_x, origin_y};
    t->prepare();
    return 0;
}

int32_t vs_loc_get_pattern(const vs_loc* t, uint8_t* buf, int32_t cap, int32_t* w, int32_t* h, float* origin_x, float* origin_y)
{
    if (!t || t->pattern.empty()) return -1;
    if (w) *w = t->pattern.cols;
    if (h) *h = t->pattern.rows;
    if (origin_x) *origin_x = float(t->origin.x);
    if (origin_y) *origin_y = float(t->origin.y);
    const int32_t need = int32_t(t->pattern.total());
    if (buf && cap >= need)
        for (int y = 0; y < t->pattern.rows; ++y)
            std::copy(t->pattern.ptr<uint8_t>(y), t->pattern.ptr<uint8_t>(y) + t->pattern.cols, buf + size_t(y) * size_t(t->pattern.cols));
    return need;
}

int32_t vs_loc_run(vs_loc* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride)
{
    if (!t) return -1;
    t->res = vs_loc_result{};
    if (!pixels || width <= 0 || height <= 0 || stride < width) { t->error = "bad image"; return -1; }
    if (t->pattern.empty()) { t->error = "no pattern taught"; return -1; }
    try {
        const auto t0 = Clock::now();
        const cv::Mat img(height, width, CV_8U, const_cast<uint8_t*>(pixels), size_t(stride));
        const vs_loc_params& P = t->p;
        const cv::Rect area = (P.search_w > 0 ? cv::Rect(P.search_x, P.search_y, P.search_w, P.search_h)
                                              : cv::Rect(0, 0, width, height)) & cv::Rect(0, 0, width, height);
        if (area.width < 16 || area.height < 16) { t->error = "search area outside the image"; return -1; }

        // coarse: every turn at about 64 px pattern size
        const double sc = t->coarseScale;
        cv::Mat small;
        cv::resize(img(area), small, {}, sc, sc, cv::INTER_AREA);
        Hit top;
        size_t topI = 0;
        // the search stops at 10 times the time budget (at least 500 ms) and reports not found: a small
        // pattern over a large image and a wide turn can take minutes, and a call that never returns
        // stops the line. Normal searches take about the budget, so this never cuts one short.
        const double stopMs = std::max(500.0, 10.0 * P.time_budget_ms);
        auto overTime = [&] { return std::chrono::duration<double, std::milli>(Clock::now() - t0).count() > stopMs; };
        auto timedOut = [&] {
            t->res.time_ms = float(std::chrono::duration<double, std::milli>(Clock::now() - t0).count());
            t->error = "the search ran over 10 times its time budget: not found (use a larger pattern or a smaller turn or area)";
            return -1;
        };
        for (size_t i = 0; i < t->coarse.size(); ++i) {
            if (overTime()) return timedOut();
            const Hit h = best(small, t->coarse[i].img);
            if (h.score > top.score) { top = h; topI = i; }
        }
        if (top.score < -1) { t->error = "the search area is smaller than the pattern"; return -1; }
        const Turned& ct = t->coarse[topI];
        cv::Point2d centre(area.x + (top.loc.x + ct.centre.x) / sc, area.y + (top.loc.y + ct.centre.y) / sc);

        // fine: up to 3x the coarse resolution, a window round the coarse hit, finer turns
        const double sf = fineScaleFor(sc);
        const double half = 0.5 * std::max(t->pattern.cols, t->pattern.rows) + 3.0 / sc + 4.0;
        const cv::Rect win = cv::Rect(int(centre.x - half), int(centre.y - half), int(2 * half), int(2 * half)) & area;
        cv::Mat fineImg;
        cv::resize(img(win), fineImg, {}, sf, sf, cv::INTER_AREA);
        // the pattern is scaled once and the small copy turned (turning the full pattern every step is slow)
        if (t->finePattern.empty() || t->fineScale != sf) {
            if (sf < 0.999) cv::resize(t->pattern, t->finePattern, {}, sf, sf, cv::INTER_AREA);
            else t->finePattern = t->pattern;
            t->fineScale = sf;
        }
        // two passes of finer turns round the best one: coarse step / 4, then / 16
        double deg = ct.deg;
        double score = top.score;
        double step = t->coarseStep;
        for (int pass = 0; pass < 2; ++pass) {
            const double fineStep = std::max(0.05, step / 4.0);
            std::vector<std::pair<double, double>> scores;    // (deg, score)
            Hit fineTop;
            Turned fineT;
            for (double d = deg - step; d <= deg + step + 1e-9; d += fineStep) {
                if (std::abs(d) > P.max_angle_deg + 1e-9) continue;
                if (overTime()) return timedOut();
                Turned ft = turned(t->finePattern, d, 1.0);
                const Hit h = best(fineImg, ft.img);
                scores.emplace_back(d, h.score);
                if (h.score > fineTop.score) { fineTop = h; fineT = std::move(ft); }
            }
            if (fineTop.score <= -1) break;
            deg = fineT.deg;
            score = fineTop.score;
            centre = {win.x + (fineTop.sub.x + fineT.centre.x) / sf, win.y + (fineTop.sub.y + fineT.centre.y) / sf};
            if (pass == 1)   // between the last steps: parabola through the best step and its neighbours
                for (size_t i = 1; i + 1 < scores.size(); ++i)
                    if (scores[i].first == fineT.deg)
                        deg += fineStep * vertex(scores[i - 1].second, scores[i].second, scores[i + 1].second);
            step = fineStep;
        }
        for (const auto& L : t->levels) {
            if (overTime()) return timedOut();
            // the image round the pose at the level's scale; a point x of the image is (x - tl + 0.5) s - 0.5
            const double s = L.scale;
            double rad = 0;
            for (const auto& e : L.edges) rad = std::max(rad, std::hypot(e.q.x, e.q.y));
            const double h = (rad + 12) / s;
            const cv::Rect lw = cv::Rect(int(centre.x - h), int(centre.y - h), int(2 * h) + 2, int(2 * h) + 2) & cv::Rect(0, 0, width, height);
            if (lw.width < 16 || lw.height < 16) break;
            cv::Mat li;
            cv::resize(img(lw), li, {}, s, s, cv::INTER_AREA);
            cv::Point2d lc((centre.x - lw.x + 0.5) * s - 0.5, (centre.y - lw.y + 0.5) * s - 0.5);
            if (refine(L.edges, li, lc, deg) == Fit::Refined)
                centre = {lw.x + (lc.x + 0.5) / s - 0.5, lw.y + (lc.y + 0.5) / s - 0.5};
        }
        if (overTime()) return timedOut();
        refine(t->edges, img, centre, deg);
        vs_loc_result& r = t->res;
        r.score = float(std::max(0.0, score));
        r.found = r.score >= P.min_score ? 1 : 0;
        r.cx = float(centre.x);
        r.cy = float(centre.y);
        r.angle_deg = float(deg);
        r.dx = float(centre.x - t->origin.x);
        r.dy = float(centre.y - t->origin.y);
        r.time_ms = float(std::chrono::duration<double, std::milli>(Clock::now() - t0).count());
        return 0;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
}

void vs_loc_get_result(const vs_loc* t, vs_loc_result* out)
{
    if (t && out) *out = t->res;
}

const char* vs_loc_last_error(const vs_loc* t) { return t ? t->error.c_str() : "no handle"; }

} // extern "C"
