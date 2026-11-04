// blob_tool.cpp - the Blob tool (vs_blob.h): threshold, connected components, per-blob measures, filters.

#include "vs_blob.h"

#include <opencv2/core/utility.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <exception>
#include <functional>
#include <mutex>
#include <new>
#include <string>
#include <vector>

namespace {

using Clock = std::chrono::steady_clock;
constexpr double kPi = 3.14159265358979323846;
constexpr size_t kMaxRejectedKept = 1000;   // rejected blobs held for display (largest first)
constexpr size_t kMaxMeasured = 20000;      // more blobs past the cheap filters than this: not checked

double ms(Clock::time_point t0) { return std::chrono::duration<double, std::milli>(Clock::now() - t0).count(); }

vs_blob_params sanitize(vs_blob_params p)
{
    p.threshold_mode = std::clamp(p.threshold_mode, 0, 3);
    p.polarity = p.polarity == VS_BLOB_LIGHT_ON_DARK ? VS_BLOB_LIGHT_ON_DARK : VS_BLOB_DARK_ON_LIGHT;
    p.threshold = std::clamp(p.threshold, 0, 255);
    p.threshold_high = std::clamp(p.threshold_high, p.threshold, 255);
    p.local_window = std::max(3, p.local_window) | 1;
    p.local_offset = std::clamp(p.local_offset, -255, 255);
    p.min_contrast = std::clamp(p.min_contrast, 0, 255);
    p.connectivity = p.connectivity == 4 ? 4 : 8;
    p.fill_holes = p.fill_holes ? 1 : 0;
    p.exclude_edge = p.exclude_edge ? 1 : 0;
    p.morphology = std::clamp(p.morphology, 0, 2);
    p.min_area = std::max(1.f, p.min_area);
    p.max_area = std::max(0.f, p.max_area);
    p.min_circularity = std::clamp(p.min_circularity, 0.f, 1.f);
    p.max_circularity = std::clamp(p.max_circularity, 0.f, 1.f);
    p.min_elongation = std::max(0.f, p.min_elongation);
    p.max_elongation = std::max(0.f, p.max_elongation);
    p.sort_by = std::clamp(p.sort_by, 0, 7);
    p.sort_descending = p.sort_descending ? 1 : 0;
    p.keep_first = std::max(0, p.keep_first);
    p.time_budget_ms = std::max(1, p.time_budget_ms);
    return p;
}

using Hist = std::array<double, 256>;

// grey histogram of a region (cv::calcHist and cv::mean took 9 ms on 5 MP): row stripes in parallel, four
// interleaved counters per stripe so a plain background does not stall on one bin
Hist histogram(const cv::Mat& roi)
{
    Hist h{};
    std::mutex m;
    cv::parallel_for_(cv::Range(0, roi.rows), [&](const cv::Range& rows) {
        std::array<std::array<uint32_t, 256>, 4> c{};
        for (int y = rows.start; y < rows.end; ++y) {
            const uint8_t* p = roi.ptr<uint8_t>(y);
            int x = 0;
            for (; x + 4 <= roi.cols; x += 4) { ++c[0][p[x]]; ++c[1][p[x + 1]]; ++c[2][p[x + 2]]; ++c[3][p[x + 3]]; }
            for (; x < roi.cols; ++x) ++c[0][p[x]];
        }
        std::lock_guard<std::mutex> lock(m);
        for (size_t g = 0; g < 256; ++g) h[g] += double(c[0][g]) + c[1][g] + c[2][g] + c[3][g];
    }, std::max(1.0, roi.rows / 64.0));
    return h;
}

// Otsu's level from a 256-bin histogram (grey <= level is the dark class): the first level of highest
// between-class variance, as cv::threshold, without another pass over the image
int otsuLevel(const Hist& hist)
{
    double n = 0, sum = 0;
    for (int g = 0; g < 256; ++g) { n += hist[size_t(g)]; sum += g * double(hist[size_t(g)]); }
    double w0 = 0, s0 = 0, best = -1;
    int level = 0;
    for (int g = 0; g < 255; ++g) {
        w0 += hist[size_t(g)];
        s0 += g * double(hist[size_t(g)]);
        const double w1 = n - w0;
        if (w0 <= 0 || w1 <= 0) continue;
        const double d = s0 / w0 - (sum - s0) / w1, v = w0 * w1 * d * d;
        if (v > best) { best = v; level = g; }
    }
    return level;
}

// keeps the kMaxRejectedKept largest rejected blobs, largest first
void trimRejected(std::vector<vs_blob_info>& r)
{
    auto larger = [](const vs_blob_info& x, const vs_blob_info& y) { return x.area > y.area; };
    if (r.size() > kMaxRejectedKept) {
        std::nth_element(r.begin(), r.begin() + kMaxRejectedKept, r.end(), larger);
        r.resize(kMaxRejectedKept);
    }
    std::stable_sort(r.begin(), r.end(), larger);
}

// boundary length: the chain through the edge pixels' centres with Vossepoel-Smeulders weights, plus pi
// for the half pixel between the centres and the true edge (exact for a disc, close for any convex shape)
double perimeter(const std::vector<cv::Point>& c)
{
    const size_t n = c.size();
    if (n < 2) return kPi;
    int even = 0, odd = 0, corners = 0, prev = -1, first = -1;
    for (size_t k = 0; k < n; ++k) {
        const cv::Point d = c[(k + 1) % n] - c[k];
        if (d.x == 0 && d.y == 0) continue;
        const int code = (d.y + 1) * 3 + (d.x + 1);   // one of 8 directions
        if (d.x != 0 && d.y != 0) ++odd; else ++even;
        if (prev >= 0 && code != prev) ++corners;
        if (first < 0) first = code;
        prev = code;
    }
    if (prev >= 0 && first >= 0 && prev != first) ++corners;
    return 0.980 * even + 1.406 * odd - 0.091 * corners + kPi;
}

double sortKey(const vs_blob_info& b, int by)
{
    switch (by) {
    case VS_BLOB_SORT_AREA: return b.area;
    case VS_BLOB_SORT_X: return b.cx;
    case VS_BLOB_SORT_Y: return b.cy;
    case VS_BLOB_SORT_PERIMETER: return b.perimeter;
    case VS_BLOB_SORT_CIRCULARITY: return b.circularity;
    case VS_BLOB_SORT_ELONGATION: return b.elongation;
    case VS_BLOB_SORT_MEAN_GREY: return b.mean_grey;
    default: return 0;
    }
}

// angle (degrees) folded into (-90, 90]
double fold90(double a)
{
    while (a > 90) a -= 180;
    while (a <= -90) a += 180;
    return a;
}

// central second moments of a mask's pixels (OpenCV 5 moved cv::moments out of imgproc)
struct Second { double n = 0, cx = 0, cy = 0, xx = 0, yy = 0, xy = 0; };
Second second(const cv::Mat& mask)
{
    double n = 0, sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
    for (int y = 0; y < mask.rows; ++y) {
        const uint8_t* m = mask.ptr<uint8_t>(y);
        for (int x = 0; x < mask.cols; ++x)
            if (m[x]) { n += 1; sx += x; sy += y; sxx += double(x) * x; syy += double(y) * y; sxy += double(x) * y; }
    }
    Second s;
    if (n <= 0) return s;
    s.n = n;
    s.cx = sx / n;
    s.cy = sy / n;
    s.xx = sxx / n - s.cx * s.cx;
    s.yy = syy / n - s.cy * s.cy;
    s.xy = sxy / n - s.cx * s.cy;
    return s;
}

// the smallest turned box round points (as cv::minAreaRect, moved out of imgproc in OpenCV 5): one side
// lies on an edge of the convex hull; returns centre, the side along `angle` (w), the other (h), and the
// angle of w in degrees clockwise in image coordinates
struct Box { cv::Point2d c; double w = 0, h = 0, deg = 0; };
Box smallestBox(std::vector<cv::Point> pts)
{
    std::sort(pts.begin(), pts.end(), [](cv::Point a, cv::Point b) { return a.x < b.x || (a.x == b.x && a.y < b.y); });
    pts.erase(std::unique(pts.begin(), pts.end()), pts.end());
    Box best;
    if (pts.size() == 1) { best.c = pts[0]; return best; }
    // convex hull, monotone chain
    auto cross = [](cv::Point o, cv::Point a, cv::Point b) { return double(a.x - o.x) * (b.y - o.y) - double(a.y - o.y) * (b.x - o.x); };
    std::vector<cv::Point> h(2 * pts.size());
    size_t k = 0;
    for (size_t i = 0; i < pts.size(); ++i) {
        while (k >= 2 && cross(h[k - 2], h[k - 1], pts[i]) <= 0) --k;
        h[k++] = pts[i];
    }
    for (size_t i = pts.size() - 1, t = k + 1; i-- > 0;) {
        while (k >= t && cross(h[k - 2], h[k - 1], pts[i]) <= 0) --k;
        h[k++] = pts[i];
    }
    h.resize(k - 1);
    double bestArea = -1;
    for (size_t i = 0; i < h.size(); ++i) {
        const cv::Point2d e = cv::Point2d(h[(i + 1) % h.size()] - h[i]);
        const double len = std::hypot(e.x, e.y);
        if (len <= 0) continue;
        const cv::Point2d u = e / len, v(-u.y, u.x);
        double u0 = 1e18, u1 = -1e18, v0 = 1e18, v1 = -1e18;
        for (const cv::Point& p : h) {
            const double a = p.x * u.x + p.y * u.y, b = p.x * v.x + p.y * v.y;
            u0 = std::min(u0, a); u1 = std::max(u1, a); v0 = std::min(v0, b); v1 = std::max(v1, b);
        }
        const double area = (u1 - u0) * (v1 - v0);
        if (bestArea < 0 || area < bestArea - 1e-9) {
            bestArea = area;
            const double mu = 0.5 * (u0 + u1), mv = 0.5 * (v0 + v1);
            best.c = u * mu + v * mv;
            best.w = u1 - u0;
            best.h = v1 - v0;
            best.deg = std::atan2(u.y, u.x) * 180 / kPi;
        }
    }
    return best;
}

// the full measures of one blob (label i, cheap measures in b) and its shape filters
vs_blob_info measure(vs_blob_info b, int i, const cv::Mat& labels, const cv::Mat& roi, const cv::Rect& area, const vs_blob_params& P)
{
    const cv::Rect box(b.box_x - area.x, b.box_y - area.y, b.box_w, b.box_h);
    cv::Mat mask = cv::Mat::zeros(box.height + 2, box.width + 2, CV_8U);
    cv::Mat inner = mask(cv::Rect(1, 1, box.width, box.height));
    cv::compare(labels(box), i, inner, cv::CMP_EQ);

    const Second m = second(inner);
    // each pixel a unit square: + 1/12 to the second moments (a disc comes out exact, a line gets a width)
    const double a = m.xx + 1.0 / 12, bb = m.xy, c = m.yy + 1.0 / 12;
    const double mid = 0.5 * (a + c), r = std::sqrt(0.25 * (a - c) * (a - c) + bb * bb);
    b.major_axis = float(4 * std::sqrt(mid + r));
    b.minor_axis = float(4 * std::sqrt(std::max(1e-12, mid - r)));
    b.elongation = b.major_axis / b.minor_axis;
    // image y points down: the clockwise angle from the moments, turned to counter-clockwise on screen
    b.angle_deg = float(fold90(-0.5 * std::atan2(2 * bb, a - c) * 180 / kPi));

    std::vector<std::vector<cv::Point>> contours;
    std::vector<cv::Vec4i> hier;
    cv::findContours(mask, contours, hier, cv::RETR_CCOMP, cv::CHAIN_APPROX_NONE);
    size_t outer = 0, longest = 0;
    for (size_t j = 0; j < contours.size(); ++j) {
        if (hier[j][3] >= 0) { ++b.holes; continue; }
        if (contours[j].size() >= longest) { longest = contours[j].size(); outer = j; }
    }
    if (P.connectivity == 4 && b.holes > 0) {
        // findContours sees the background 4-connected, the way round for 8-connected blobs; a 4-connected
        // blob's background is 8-connected, so a pocket open to the outside across a diagonal is no hole.
        // The mask has a free border, so the outside is one region: holes = background regions - 1.
        cv::Mat bg, bgLabels;
        cv::compare(mask, 0, bg, cv::CMP_EQ);
        b.holes = cv::connectedComponents(bg, bgLabels, 8, CV_32S) - 2;
    }
    if (!contours.empty()) {
        const auto& oc = contours[outer];
        b.perimeter = float(perimeter(oc));
        const Box rr = smallestBox(oc);
        double w = rr.w + 1, h = rr.h + 1;   // pixel centres to pixel edges
        double ang = -rr.deg;                // image y points down: clockwise there is counter-clockwise on screen
        if (w < h) { std::swap(w, h); ang += 90; }
        b.rbox_cx = float(rr.c.x - 1 + box.x + area.x);
        b.rbox_cy = float(rr.c.y - 1 + box.y + area.y);
        b.rbox_w = float(w);
        b.rbox_h = float(h);
        b.rbox_angle_deg = float(fold90(ang));
    }
    b.circularity = float(std::min(1.0, 4 * kPi * b.area / (double(b.perimeter) * b.perimeter)));
    b.compactness = float(double(b.perimeter) * b.perimeter / (4 * kPi * b.area));
    b.mean_grey = float(cv::mean(roi(box), inner)[0]);

    if (P.min_circularity > 0 && b.circularity < P.min_circularity) b.reject_reason = VS_BLOB_REJ_CIRC_LOW;
    else if (P.max_circularity > 0 && b.circularity > P.max_circularity) b.reject_reason = VS_BLOB_REJ_CIRC_HIGH;
    else if (P.min_elongation > 0 && b.elongation < P.min_elongation) b.reject_reason = VS_BLOB_REJ_ELONG_LOW;
    else if (P.max_elongation > 0 && b.elongation > P.max_elongation) b.reject_reason = VS_BLOB_REJ_ELONG_HIGH;
    return b;
}

// ---- contour analysis (tool spec 4.2) ----

using Pts = std::vector<cv::Point2d>;

// closed polyline with a grid of its segments (flat arrays), for the nearest point on it
struct SegGrid {
    Pts p;
    double x0 = 0, y0 = 0, cell = 1;
    int nx = 0, ny = 0;
    std::vector<int> start, seg;   // cell c holds segments seg[start[c] .. start[c + 1]); segment k runs p[k] .. p[k + 1]

    // cells about 4 segments long, at most 256 a side
    void build(const Pts& pts)
    {
        p = pts;
        start.clear();
        seg.clear();
        if (p.empty()) return;
        double x1 = p[0].x, y1 = p[0].y, len = 0;
        x0 = x1; y0 = y1;
        for (size_t k = 0; k < p.size(); ++k) {
            const auto& q = p[k];
            x0 = std::min(x0, q.x); y0 = std::min(y0, q.y); x1 = std::max(x1, q.x); y1 = std::max(y1, q.y);
            len += cv::norm(p[(k + 1) % p.size()] - q);
        }
        cell = std::max({1.0, 4 * len / double(p.size()), std::max(x1 - x0, y1 - y0) / 256});
        nx = int((x1 - x0) / cell) + 1;
        ny = int((y1 - y0) / cell) + 1;
        std::vector<int> count(size_t(nx) * size_t(ny) + 1, 0);
        auto each = [&](auto&& fn) {
            for (size_t k = 0; k < p.size(); ++k) {
                const cv::Point2d a = p[k], b = p[(k + 1) % p.size()];
                for (int y = cy(std::min(a.y, b.y)); y <= cy(std::max(a.y, b.y)); ++y)
                    for (int x = cx(std::min(a.x, b.x)); x <= cx(std::max(a.x, b.x)); ++x) fn(size_t(y) * size_t(nx) + size_t(x), int(k));
            }
        };
        each([&](size_t c, int) { ++count[c + 1]; });
        for (size_t c = 1; c < count.size(); ++c) count[c] += count[c - 1];
        start = count;
        seg.assign(size_t(count.back()), 0);
        each([&](size_t c, int k) { seg[size_t(count[c]++)] = k; });
    }
    int cx(double x) const { return std::clamp(int((x - x0) / cell), 0, nx - 1); }
    int cy(double y) const { return std::clamp(int((y - y0) / cell), 0, ny - 1); }

    // distance from q to the polyline; rings of cells round q's cell until no nearer segment can lie further out
    double nearest(cv::Point2d q, cv::Point2d* at = nullptr) const
    {
        double best = 1e300;
        if (p.empty()) return best;
        const int qx = cx(q.x), qy = cy(q.y);
        for (int r = 0;; ++r) {
            for (int y = std::max(0, qy - r); y <= std::min(ny - 1, qy + r); ++y) {
                const bool edgeRow = y == qy - r || y == qy + r;
                for (int x = qx - r; x <= qx + r; x += edgeRow || r == 0 ? 1 : 2 * r) {
                    if (x < 0 || x >= nx) continue;
                    const size_t c = size_t(y) * size_t(nx) + size_t(x);
                    for (int i = start[c]; i < start[c + 1]; ++i) {
                        const size_t k = size_t(seg[size_t(i)]);
                        const cv::Point2d a = p[k], d = p[(k + 1) % p.size()] - a;
                        const double l2 = d.dot(d);
                        const double u = l2 > 0 ? std::clamp((q - a).dot(d) / l2, 0.0, 1.0) : 0.0;
                        const cv::Point2d e = a + u * d - q;
                        const double dist2 = e.dot(e);
                        if (dist2 < best) { best = dist2; if (at) *at = q + e; }
                    }
                }
            }
            // the nearest point any cell further out can hold: the block's border on a side with cells beyond it
            double lb = 1e300;
            bool more = false;
            if (qx - r > 0) { more = true; lb = std::min(lb, q.x - (x0 + (qx - r) * cell)); }
            if (qx + r < nx - 1) { more = true; lb = std::min(lb, x0 + (qx + r + 1) * cell - q.x); }
            if (qy - r > 0) { more = true; lb = std::min(lb, q.y - (y0 + (qy - r) * cell)); }
            if (qy + r < ny - 1) { more = true; lb = std::min(lb, y0 + (qy + r + 1) * cell - q.y); }
            lb = std::max(0.0, lb);
            if (!more || best <= lb * lb) return std::sqrt(best);
        }
    }
};

// every step-th point (an outline thinned for the coarse placement)
Pts thin(const Pts& p, size_t step)
{
    Pts t;
    for (size_t k = 0; k < p.size(); k += step) t.push_back(p[k]);
    return t;
}

// centroid, axis angle (radians, image coordinates) and axis ratio of the area a closed polygon encloses
struct Pose { cv::Point2d c; double angle = 0, ratio = 1, area = 0; };
Pose polygonPose(const Pts& p)
{
    Pose s;
    if (p.size() < 3) return s;
    const cv::Point2d o = p[0];   // moments round the first point, for precision
    double a = 0, sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
    for (size_t k = 0; k < p.size(); ++k) {
        const cv::Point2d u = p[k] - o, v = p[(k + 1) % p.size()] - o;
        const double c = u.x * v.y - v.x * u.y;
        a += c;
        sx += (u.x + v.x) * c;
        sy += (u.y + v.y) * c;
        sxx += (u.x * u.x + u.x * v.x + v.x * v.x) * c;
        syy += (u.y * u.y + u.y * v.y + v.y * v.y) * c;
        sxy += (u.x * v.y + 2 * u.x * u.y + 2 * v.x * v.y + v.x * u.y) * c;
    }
    a *= 0.5;
    if (std::abs(a) < 1e-9) { s.c = o; return s; }
    const double mx = sx / (6 * a), my = sy / (6 * a);
    const double m20 = sxx / (12 * a) - mx * mx, m02 = syy / (12 * a) - my * my, m11 = sxy / (24 * a) - mx * my;
    s.c = o + cv::Point2d(mx, my);
    s.area = std::abs(a);
    s.angle = 0.5 * std::atan2(2 * m11, m20 - m02);
    const double mid = 0.5 * (m20 + m02), r = std::sqrt(0.25 * (m20 - m02) * (m20 - m02) + m11 * m11);
    s.ratio = std::sqrt((mid + r) / std::max(1e-12, mid - r));
    return s;
}

// convex hull as indices into p, in index order (the outline's own order)
std::vector<size_t> hullIndices(const Pts& p)
{
    std::vector<size_t> idx(p.size());
    for (size_t k = 0; k < idx.size(); ++k) idx[k] = k;
    std::sort(idx.begin(), idx.end(), [&](size_t a, size_t b) { return p[a].x < p[b].x || (p[a].x == p[b].x && p[a].y < p[b].y); });
    auto cross = [&](size_t o, size_t a, size_t b) {
        return (p[a].x - p[o].x) * (p[b].y - p[o].y) - (p[a].y - p[o].y) * (p[b].x - p[o].x);
    };
    std::vector<size_t> h(2 * idx.size() + 1);
    size_t k = 0;
    for (size_t i = 0; i < idx.size(); ++i) {
        while (k >= 2 && cross(h[k - 2], h[k - 1], idx[i]) <= 0) --k;
        h[k++] = idx[i];
    }
    for (size_t i = idx.size() - 1, t = k + 1; i-- > 0;) {
        while (k >= t && cross(h[k - 2], h[k - 1], idx[i]) <= 0) --k;
        h[k++] = idx[i];
    }
    h.resize(k > 1 ? k - 1 : k);
    std::sort(h.begin(), h.end());
    h.erase(std::unique(h.begin(), h.end()), h.end());
    return h;
}

double shoelace(const Pts& p)
{
    double a = 0;
    for (size_t k = 0; k < p.size(); ++k) a += p[k].x * p[(k + 1) % p.size()].y - p[(k + 1) % p.size()].x * p[k].y;
    return 0.5 * a;
}

// the taught outline, centred on its centroid, with what is needed to place it
struct Taught {
    Pts pts;
    SegGrid grid;
    Pose pose;
    void set(Pts p)
    {
        pts.clear();
        grid.build({});
        if (p.size() < 3) return;
        const Pose s = polygonPose(p);
        for (auto& q : p) q -= s.c;
        pts = std::move(p);
        grid.build(pts);
        pose = polygonPose(pts);
    }
};

// rigid placement of the taught outline: image = R(angle) * taught + t
struct Fit { double angle = 0; cv::Point2d t; double mean = 1e300; };

cv::Point2d place(const Fit& f, cv::Point2d q)
{
    const double c = std::cos(f.angle), s = std::sin(f.angle);
    return {c * q.x - s * q.y + f.t.x, s * q.x + c * q.y + f.t.y};
}

// closest-point ICP of the taught points (every step-th) onto the outline; pairs past 2.5 x the median distance
// (+ 0.5 px) are left out of the fit, so a dent does not pull the placement
Fit icp(const Pts& src, const SegGrid& to, Fit f, int iters, size_t step)
{
    std::vector<cv::Point2d> a, b;
    std::vector<double> d;
    for (int it = 0; it < iters; ++it) {
        a.clear(); b.clear(); d.clear();
        for (size_t k = 0; k < src.size(); k += step) {
            cv::Point2d at;
            d.push_back(to.nearest(place(f, src[k]), &at));
            a.push_back(src[k]);
            b.push_back(at);
        }
        if (d.empty()) return f;
        std::vector<double> sorted = d;
        std::nth_element(sorted.begin(), sorted.begin() + long(sorted.size() / 2), sorted.end());
        const double cut = 2.5 * sorted[sorted.size() / 2] + 0.5;
        double sum = 0;
        for (double v : d) sum += v;
        f.mean = sum / double(d.size());
        cv::Point2d ma, mb;
        double n = 0;
        for (size_t k = 0; k < d.size(); ++k)
            if (d[k] <= cut) { ma += a[k]; mb += b[k]; n += 1; }
        if (n < 3) return f;
        ma *= 1 / n;
        mb *= 1 / n;
        double sc = 0, ss = 0;
        for (size_t k = 0; k < d.size(); ++k)
            if (d[k] <= cut) {
                const cv::Point2d u = a[k] - ma, v = b[k] - mb;
                sc += u.x * v.x + u.y * v.y;
                ss += u.x * v.y - u.y * v.x;
            }
        Fit g;
        g.angle = std::atan2(ss, sc);
        g.t = mb - place(Fit{g.angle, {0, 0}, 0}, ma);
        const bool still = std::abs(g.angle - f.angle) < 1e-5 && std::hypot(g.t.x - f.t.x, g.t.y - f.t.y) < 1e-4;
        g.mean = f.mean;
        f = g;
        if (still) break;
    }
    return f;
}

struct Outline {
    vs_blob_contour c{};
    std::vector<cv::Point2f> pts;
    std::vector<vs_blob_defect> defects;
    bool late = false;   // the time budget ran out while tracing: not checked
};

// bilinear grey of a float image, clamped to it
float sample(const cv::Mat& g, double x, double y)
{
    x = std::clamp(x, 0.0, double(g.cols - 1));
    y = std::clamp(y, 0.0, double(g.rows - 1));
    const int x0 = std::min(int(x), g.cols - 2 < 0 ? 0 : g.cols - 2), y0 = std::min(int(y), g.rows - 2 < 0 ? 0 : g.rows - 2);
    const int x1 = std::min(x0 + 1, g.cols - 1), y1 = std::min(y0 + 1, g.rows - 1);
    const double fx = x - x0, fy = y - y0;
    const float* r0 = g.ptr<float>(y0);
    const float* r1 = g.ptr<float>(y1);
    return float((r0[x0] * (1 - fx) + r0[x1] * fx) * (1 - fy) + (r1[x0] * (1 - fx) + r1[x1] * fx) * fy);
}

// the outline of kept blob (label) and its shape measures
Outline outline(const vs_blob_info& b, int label, const cv::Mat& labels, const cv::Mat& roi, const cv::Rect& area,
                const vs_blob_contour_params& C, const Taught& T, const std::function<bool()>& over)
{
    Outline o;
    o.c.struct_size = int32_t(sizeof(vs_blob_contour));
    o.c.closed = b.touches_edge ? 0 : 1;
    o.c.convexity = 1;
    const int pad = 4 + int(std::ceil(3 * C.smoothing));
    const cv::Rect box(b.box_x - area.x - pad, b.box_y - area.y - pad, b.box_w + 2 * pad, b.box_h + 2 * pad);
    const cv::Rect win = box & cv::Rect(0, 0, roi.cols, roi.rows);
    cv::Mat mask, padded;
    cv::compare(labels(win), label, mask, cv::CMP_EQ);
    cv::copyMakeBorder(mask, padded, 1, 1, 1, 1, cv::BORDER_CONSTANT, 0);
    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(padded, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_NONE);
    if (contours.empty()) return o;
    const auto& chain = *std::max_element(contours.begin(), contours.end(), [](const auto& x, const auto& y) { return x.size() < y.size(); });
    const size_t n = chain.size();

    cv::Mat g;
    roi(win).convertTo(g, CV_32F);
    if (C.smoothing > 0) cv::GaussianBlur(g, g, {0, 0}, C.smoothing, C.smoothing, cv::BORDER_REPLICATE);
    if (over()) { o.late = true; return o; }
    // edge level: halfway between the blob and a 2 px ring round it
    cv::Mat ring;
    cv::dilate(mask, ring, cv::getStructuringElement(cv::MORPH_RECT, {5, 5}));
    ring &= ~mask;
    const double in = cv::mean(roi(win), mask)[0];
    const double out = cv::countNonZero(ring) > 0 ? cv::mean(roi(win), ring)[0] : in;
    const double level = C.edge_level >= 0 ? C.edge_level : 0.5 * (in + out);
    const bool inAbove = in > level;

    // the chain's turn decides which normal points out
    double turn = 0;
    for (size_t k = 0; k < n; ++k) turn += double(chain[k].x) * chain[(k + 1) % n].y - double(chain[(k + 1) % n].x) * chain[k].y;
    const double outSign = turn >= 0 ? 1 : -1;
    const cv::Point2d shift(win.x + area.x, win.y + area.y);   // window to image px
    Pts pts;
    pts.reserve(n);
    size_t found = 0;
    for (size_t k = 0; k < n; ++k) {
        const cv::Point2d p(chain[k].x - 1, chain[k].y - 1);
        cv::Point2d t = cv::Point2d(chain[(k + 2) % n] - chain[(k + n - 2) % n]);
        if (t.x == 0 && t.y == 0) t = cv::Point2d(chain[(k + 1) % n] - chain[(k + n - 1) % n]);
        const double len = std::hypot(t.x, t.y);
        cv::Point2d nrm = len > 0 ? cv::Point2d(t.y, -t.x) * (outSign / len) : cv::Point2d(0, 0);
        double best = 1e9, at = 0.5;
        double s0 = -1.5;
        float f0 = sample(g, p.x + s0 * nrm.x, p.y + s0 * nrm.y);
        for (double s1 = -1.25; s1 <= 2.0 + 1e-9 && len > 0; s1 += 0.25) {
            const float f1 = sample(g, p.x + s1 * nrm.x, p.y + s1 * nrm.y);
            if ((f0 > level) == inAbove && (f1 > level) != inAbove && f0 != f1) {
                const double s = s0 + (s1 - s0) * (level - f0) / (double(f1) - f0);
                if (std::abs(s - 0.5) < std::abs(best - 0.5)) best = s;
            }
            s0 = s1;
            f0 = f1;
        }
        if (best < 1e8) { at = best; ++found; }
        pts.push_back(p + at * nrm + shift);
    }
    o.pts.assign(pts.begin(), pts.end());
    o.c.points = int32_t(n);
    o.c.edge_share = float(double(found) / double(n));
    for (size_t k = 0; k < n && n > 1; ++k) o.c.length += float(cv::norm(pts[(k + 1) % n] - pts[k]));
    o.c.area = float(std::abs(shoelace(pts)));
    if (n < 3) return o;

    // convexity and the indents under each hull edge
    const std::vector<size_t> h = hullIndices(pts);
    Pts hp;
    for (size_t k : h) hp.push_back(pts[k]);
    const double hullArea = std::abs(shoelace(hp));
    if (hullArea > 0) o.c.convexity = float(std::min(1.0, o.c.area / hullArea));
    vs_blob_defect worst{};   // the deepest indent, listed or not, so a dent rule below min_defect_depth still sees it
    for (size_t j = 0; j < h.size() && h.size() >= 2; ++j) {
        const size_t i0 = h[j], i1 = h[(j + 1) % h.size()];
        const cv::Point2d a = pts[i0], d = pts[i1] - a;
        const double w = std::hypot(d.x, d.y);
        if (w <= 0) continue;
        double deepest = 0;
        size_t at = i0;
        for (size_t k = (i0 + 1) % n; k != i1; k = (k + 1) % n) {
            const double depth = std::abs(d.x * (pts[k].y - a.y) - d.y * (pts[k].x - a.x)) / w;
            if (depth > deepest) { deepest = depth; at = k; }
        }
        const vs_blob_defect indent{float(deepest), float(pts[at].x), float(pts[at].y), float(w)};
        if (deepest > worst.depth) worst = indent;
        if (deepest >= C.min_defect_depth && deepest > 0) o.defects.push_back(indent);
    }
    std::stable_sort(o.defects.begin(), o.defects.end(), [](const vs_blob_defect& x, const vs_blob_defect& y) { return x.depth > y.depth; });
    o.c.defects = int32_t(o.defects.size());
    o.c.max_defect_depth = worst.depth;
    o.c.max_defect_x = worst.x;
    o.c.max_defect_y = worst.y;

    // deviation from the taught outline
    if (T.pts.size() >= 3) {
        SegGrid here, coarse;
        here.build(pts);
        coarse.build(thin(pts, std::max<size_t>(1, n / 256)));   // start angles on thinned outlines
        const Pose ps = polygonPose(pts);
        const double a0 = ps.angle - T.pose.angle;
        const bool axis = ps.ratio >= 1.15 && T.pose.ratio >= 1.15;
        const int starts = axis ? 2 : 12;
        const size_t step = std::max<size_t>(1, T.pts.size() / 100);
        Fit best;
        for (int s = 0; s < starts; ++s) {
            if (over()) { o.late = true; return o; }
            Fit f;
            f.angle = a0 + s * 2 * kPi / starts;
            f.t = ps.c - place(Fit{f.angle, {0, 0}, 0}, T.pose.c);
            f = icp(T.pts, coarse, f, 10, step);
            if (f.mean < best.mean) best = f;
        }
        if (over()) { o.late = true; return o; }
        best = icp(T.pts, here, best, 10, std::max<size_t>(1, T.pts.size() / 500));
        best = icp(T.pts, here, best, 3, 1);
        double worst = -1, sum = 0;
        cv::Point2d where;
        for (const auto& q : T.pts) {   // taught points away from the outline: a missing piece
            const cv::Point2d x = place(best, q);
            const double d = here.nearest(x);
            if (d > worst) { worst = d; where = x; }
        }
        const double c = std::cos(best.angle), s = std::sin(best.angle);
        for (const auto& x : pts) {     // outline points away from the taught one: a dent or a bump
            const cv::Point2d u = x - best.t, q(c * u.x + s * u.y, -s * u.x + c * u.y);
            const double d = T.grid.nearest(q);
            sum += d;
            if (d > worst) { worst = d; where = x; }
        }
        const cv::Point2d centre = place(best, {0, 0});
        o.c.taught = 1;
        o.c.max_deviation = float(worst);
        o.c.mean_deviation = float(sum / double(n));
        o.c.max_deviation_x = float(where.x);
        o.c.max_deviation_y = float(where.y);
        o.c.pose_x = float(centre.x);
        o.c.pose_y = float(centre.y);
        double deg = -best.angle * 180 / kPi;   // image y points down: clockwise there is counter-clockwise on screen
        while (deg > 180) deg -= 360;
        while (deg <= -180) deg += 360;
        o.c.pose_angle_deg = float(deg);
    }
    return o;
}

} // namespace

struct vs_blob {
    vs_blob_params p{};
    vs_blob_result res{};
    std::vector<vs_blob_info> kept, rejected;
    vs_blob_contour_params cp{};
    std::vector<Outline> outlines;   // per kept blob when contour analysis is on
    Taught taught;
    std::string error;
};

extern "C" {

void vs_blob_default_params(vs_blob_params* p)
{
    if (!p) return;
    *p = vs_blob_params{};
    p->struct_size = int32_t(sizeof(vs_blob_params));
    p->threshold = 128;
    p->threshold_high = 255;
    p->local_window = 31;
    p->local_offset = 10;
    p->min_contrast = 15;
    p->connectivity = 8;
    p->exclude_edge = 1;
    p->min_area = 10.f;
    p->time_budget_ms = 50;
}

vs_blob* vs_blob_create(const vs_blob_params* p)
{
    vs_blob* t = new (std::nothrow) vs_blob();
    if (!t) return nullptr;
    vs_blob_params d;
    vs_blob_default_params(&d);
    t->p = sanitize(p && p->struct_size == int32_t(sizeof(vs_blob_params)) ? *p : d);
    vs_blob_default_contour_params(&t->cp);
    return t;
}

void vs_blob_destroy(vs_blob* t) { delete t; }

int32_t vs_blob_set_params(vs_blob* t, const vs_blob_params* p)
{
    if (!t || !p || p->struct_size != int32_t(sizeof(vs_blob_params))) return -1;
    t->p = sanitize(*p);
    return 0;
}

int32_t vs_blob_run(vs_blob* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride)
{
    if (!t) return -1;
    t->res = vs_blob_result{};
    t->kept.clear();
    t->rejected.clear();
    t->outlines.clear();
    if (!pixels || width <= 0 || height <= 0 || stride < width) { t->error = "bad image"; return -1; }
    try {
        const auto t0 = Clock::now();
        const vs_blob_params& P = t->p;
        vs_blob_result& R = t->res;
        const cv::Mat img(height, width, CV_8U, const_cast<uint8_t*>(pixels), size_t(stride));
        const cv::Rect area = (P.roi_w > 0 ? cv::Rect(P.roi_x, P.roi_y, P.roi_w, P.roi_h) : cv::Rect(0, 0, width, height))
                              & cv::Rect(0, 0, width, height);
        if (area.width < 1 || area.height < 1) { t->error = "the region is outside the image"; return -1; }
        const cv::Mat roi = img(area);
        const Hist hist = histogram(roi);
        double greySum = 0;
        for (int g = 0; g < 256; ++g) greySum += g * hist[size_t(g)];
        R.region_mean = float(greySum / double(roi.total()));

        // threshold: blob pixels become 255
        cv::Mat bin;
        const bool dark = P.polarity == VS_BLOB_DARK_ON_LIGHT;
        switch (P.threshold_mode) {
        case VS_BLOB_THRESH_AUTO: {
            // Otsu's level, kept at least min_contrast from the background class's mean: on a bimodal area
            // Otsu decides; on a clean area with one small spot (Otsu then splits the noise) the level
            // moves off the noise, and the spot is still found. Never "nothing" from a contrast test.
            const int otsu = otsuLevel(hist);
            double lo = 0, nlo = 0, hi = 0, nhi = 0;
            for (int g = 0; g < 256; ++g) {
                const double c = hist[size_t(g)];
                if (g <= otsu) { lo += c * g; nlo += c; } else { hi += c * g; nhi += c; }
            }
            const double all = (lo + hi) / std::max(1.0, nlo + nhi);
            const double darkMean = nlo > 0 ? lo / nlo : all, lightMean = nhi > 0 ? hi / nhi : all;
            int level;
            if (dark) {   // blob: grey <= level
                level = std::min(otsu, int(std::floor(lightMean - P.min_contrast)));
                R.flat = level < otsu ? 1 : 0;
                cv::threshold(roi, bin, level, 255, cv::THRESH_BINARY_INV);
                if (level < 0) bin.setTo(0);
            } else {      // blob: grey > level
                level = std::max(otsu, int(std::ceil(darkMean + P.min_contrast)));
                R.flat = level > otsu ? 1 : 0;
                cv::threshold(roi, bin, level, 255, cv::THRESH_BINARY);
            }
            R.threshold_used = level;
            break;
        }
        case VS_BLOB_THRESH_FIXED:
            cv::threshold(roi, bin, P.threshold, 255, dark ? cv::THRESH_BINARY_INV : cv::THRESH_BINARY);
            R.threshold_used = P.threshold;
            break;
        case VS_BLOB_THRESH_BAND:
            cv::inRange(roi, cv::Scalar(P.threshold), cv::Scalar(P.threshold_high), bin);
            R.threshold_used = P.threshold;
            break;
        default: {
            const int win = std::min(P.local_window, (std::min(roi.cols, roi.rows) - 1) | 1);
            if (win < 3) { bin = cv::Mat::zeros(roi.size(), CV_8U); R.threshold_used = -1; break; }
            // dark: grey <= mean - offset is a blob; light: grey > mean + offset
            cv::adaptiveThreshold(roi, bin, 255, cv::ADAPTIVE_THRESH_MEAN_C, dark ? cv::THRESH_BINARY_INV : cv::THRESH_BINARY,
                                  win, dark ? P.local_offset : -P.local_offset);
            R.threshold_used = -1;
            break;
        }
        }

        if (P.morphology != VS_BLOB_MORPH_NONE) {
            const cv::Mat k = cv::getStructuringElement(cv::MORPH_RECT, {3, 3});
            cv::morphologyEx(bin, bin, P.morphology == VS_BLOB_MORPH_OPEN3 ? cv::MORPH_OPEN : cv::MORPH_CLOSE, k,
                             {-1, -1}, 1, cv::BORDER_REPLICATE);
        }
        if (P.fill_holes) {
            // background reached from outside the region is not a hole; the rest is. Background is connected
            // the other way round from the blobs (4 for 8-connected blobs), so a hole never leaks diagonally.
            cv::Mat pad;
            cv::copyMakeBorder(bin, pad, 1, 1, 1, 1, cv::BORDER_CONSTANT, 0);
            cv::floodFill(pad, cv::Point(0, 0), 255, nullptr, 0, 0, P.connectivity == 8 ? 4 : 8);
            cv::Mat holes;
            cv::compare(pad(cv::Rect(1, 1, bin.cols, bin.rows)), 0, holes, cv::CMP_EQ);
            bin |= holes;
        }

        cv::Mat labels, stats, centroids;
        const int n = cv::connectedComponentsWithStats(bin, labels, stats, centroids, P.connectivity, CV_32S);

        // cheap filters first (edge, area), from the labelling's statistics
        std::vector<int> todo;
        size_t rejectedAll = 0;   // every blob left out; the list itself keeps only the largest
        for (int i = 1; i < n; ++i) {
            const int* s = stats.ptr<int>(i);
            vs_blob_info b{};
            b.area = float(s[cv::CC_STAT_AREA]);
            b.cx = float(centroids.at<double>(i, 0) + area.x);
            b.cy = float(centroids.at<double>(i, 1) + area.y);
            b.box_x = s[cv::CC_STAT_LEFT] + area.x;
            b.box_y = s[cv::CC_STAT_TOP] + area.y;
            b.box_w = s[cv::CC_STAT_WIDTH];
            b.box_h = s[cv::CC_STAT_HEIGHT];
            b.touches_edge = (s[cv::CC_STAT_LEFT] == 0 || s[cv::CC_STAT_TOP] == 0 || s[cv::CC_STAT_LEFT] + b.box_w == area.width
                              || s[cv::CC_STAT_TOP] + b.box_h == area.height) ? 1 : 0;
            const bool areaOk = b.area >= P.min_area && (P.max_area <= 0 || b.area <= P.max_area);
            if (P.exclude_edge && b.touches_edge) { b.reject_reason = VS_BLOB_REJ_EDGE; if (areaOk) ++R.edge_count; }
            else if (b.area < P.min_area) b.reject_reason = VS_BLOB_REJ_AREA_LOW;
            else if (P.max_area > 0 && b.area > P.max_area) b.reject_reason = VS_BLOB_REJ_AREA_HIGH;
            if (b.reject_reason) { ++rejectedAll; t->rejected.push_back(b); }
            else { todo.push_back(i); t->kept.push_back(b); }
            if (t->rejected.size() >= 4 * kMaxRejectedKept) trimRejected(t->rejected);
            if ((i & 4095) == 0 && ms(t0) > P.time_budget_ms) {
                R.truncated = 1;
                t->error = "time budget used up while labelling (" + std::to_string(n) + " blobs): check the threshold";
                break;
            }
        }
        if (!R.truncated && todo.size() > kMaxMeasured) {
            R.truncated = 1;
            t->error = "too many blobs to measure (" + std::to_string(todo.size()) + "): check the threshold";
        }

        // measure the rest in parallel: boundary, holes, moments, turned box, mean grey
        std::vector<vs_blob_info> all(R.truncated ? 0 : todo.size());
        std::atomic<bool> late{false};
        std::atomic<size_t> done{0};
        cv::parallel_for_(cv::Range(0, int(all.size())), [&](const cv::Range& range) {
            for (int k = range.start; k < range.end; ++k) {
                if (late.load(std::memory_order_relaxed)) return;
                if ((k & 15) == 15 && ms(t0) > P.time_budget_ms) { late = true; return; }
                all[size_t(k)] = measure(t->kept[size_t(k)], todo[size_t(k)], labels, roi, area, P);
                done.fetch_add(1, std::memory_order_relaxed);
            }
        }, std::max(1.0, double(all.size()) / 64));
        if (late) {
            R.truncated = 1;
            t->error = "time budget used up after " + std::to_string(done.load()) + " of " + std::to_string(todo.size()) + " blobs";
        }
        std::vector<vs_blob_info> measured;
        std::vector<int> labelOf;   // each kept blob's label, for its outline
        if (!R.truncated)
            for (size_t k = 0; k < all.size(); ++k) {
                if (all[k].reject_reason) { ++rejectedAll; t->rejected.push_back(all[k]); }
                else { measured.push_back(all[k]); labelOf.push_back(todo[k]); }
            }
        t->kept.swap(measured);
        if (R.truncated) { t->kept.clear(); labelOf.clear(); }   // never report a partial list as the answer

        if (P.sort_by != VS_BLOB_SORT_NONE) {
            std::vector<size_t> order(t->kept.size());
            for (size_t k = 0; k < order.size(); ++k) order[k] = k;
            std::stable_sort(order.begin(), order.end(), [&](size_t x, size_t y) {
                const double kx = sortKey(t->kept[x], P.sort_by), ky = sortKey(t->kept[y], P.sort_by);
                return P.sort_descending ? kx > ky : kx < ky;
            });
            std::vector<vs_blob_info> sorted;
            std::vector<int> sortedLabel;
            for (size_t k : order) { sorted.push_back(t->kept[k]); sortedLabel.push_back(labelOf[k]); }
            t->kept.swap(sorted);
            labelOf.swap(sortedLabel);
        }
        if (P.keep_first > 0 && t->kept.size() > size_t(P.keep_first)) {
            for (size_t k = size_t(P.keep_first); k < t->kept.size(); ++k) {
                t->kept[k].reject_reason = VS_BLOB_REJ_KEEP_FIRST;
                ++rejectedAll;
                t->rejected.push_back(t->kept[k]);
            }
            t->kept.resize(size_t(P.keep_first));
            labelOf.resize(size_t(P.keep_first));
        }

        // contour analysis of the kept blobs, within the same time budget
        if (t->cp.enabled && !t->kept.empty()) {
            t->outlines.assign(t->kept.size(), Outline{});
            std::atomic<size_t> traced{0};
            const std::function<bool()> over = [&] { return ms(t0) > P.time_budget_ms; };
            cv::parallel_for_(cv::Range(0, int(t->kept.size())), [&](const cv::Range& range) {
                for (int k = range.start; k < range.end; ++k) {
                    if (late.load(std::memory_order_relaxed)) return;
                    if (ms(t0) > P.time_budget_ms) { late = true; return; }
                    t->outlines[size_t(k)] = outline(t->kept[size_t(k)], labelOf[size_t(k)], labels, roi, area, t->cp, t->taught, over);
                    if (t->outlines[size_t(k)].late) { late = true; return; }
                    traced.fetch_add(1, std::memory_order_relaxed);
                }
            }, std::max(1.0, double(t->kept.size()) / 16));
            if (late) {
                R.truncated = 1;
                t->error = "time budget used up after " + std::to_string(traced.load()) + " of " + std::to_string(t->kept.size()) + " outlines";
                t->kept.clear();
                t->outlines.clear();
            }
        }

        R.count = int32_t(t->kept.size());
        R.rejected = int32_t(rejectedAll);
        for (const auto& b : t->kept) R.total_area += b.area;
        trimRejected(t->rejected);
        R.time_ms = float(ms(t0));
        return 0;
    } catch (const std::exception& e) {
        t->error = e.what();
        t->kept.clear();
        t->rejected.clear();
        t->outlines.clear();
        t->res.truncated = 1;
        return -2;
    }
}

void vs_blob_get_result(const vs_blob* t, vs_blob_result* out)
{
    if (t && out) *out = t->res;
}

int32_t vs_blob_get_blobs(const vs_blob* t, vs_blob_info* out, int32_t cap)
{
    if (!t) return -1;
    if (out) std::copy_n(t->kept.begin(), std::min(size_t(std::max(0, cap)), t->kept.size()), out);
    return int32_t(t->kept.size());
}

int32_t vs_blob_get_rejected(const vs_blob* t, vs_blob_info* out, int32_t cap)
{
    if (!t) return -1;
    if (out) std::copy_n(t->rejected.begin(), std::min(size_t(std::max(0, cap)), t->rejected.size()), out);
    return int32_t(t->rejected.size());
}

const char* vs_blob_last_error(const vs_blob* t) { return t ? t->error.c_str() : "no handle"; }

void vs_blob_default_contour_params(vs_blob_contour_params* p)
{
    if (!p) return;
    *p = vs_blob_contour_params{};
    p->struct_size = int32_t(sizeof(vs_blob_contour_params));
    p->edge_level = -1;
    p->smoothing = 1;
    p->min_defect_depth = 1;
}

int32_t vs_blob_set_contour_params(vs_blob* t, const vs_blob_contour_params* p)
{
    if (!t || !p || p->struct_size != int32_t(sizeof(vs_blob_contour_params))) return -1;
    vs_blob_contour_params c = *p;
    c.enabled = c.enabled ? 1 : 0;
    c.edge_level = c.edge_level < 0 || !std::isfinite(c.edge_level) ? -1.f : std::min(c.edge_level, 255.f);
    c.smoothing = std::isfinite(c.smoothing) ? std::clamp(c.smoothing, 0.f, 5.f) : 1.f;
    c.min_defect_depth = std::isfinite(c.min_defect_depth) ? std::max(0.f, c.min_defect_depth) : 1.f;
    t->cp = c;
    return 0;
}

int32_t vs_blob_get_contour(const vs_blob* t, int32_t blob, vs_blob_contour* out)
{
    if (!t || !out || out->struct_size != int32_t(sizeof(vs_blob_contour)) || blob < 0 || size_t(blob) >= t->outlines.size()) return -1;
    *out = t->outlines[size_t(blob)].c;
    return 0;
}

int32_t vs_blob_get_outline(const vs_blob* t, int32_t blob, float* xy, int32_t cap)
{
    if (!t || blob < 0 || size_t(blob) >= t->outlines.size()) return -1;
    const auto& p = t->outlines[size_t(blob)].pts;
    if (xy)
        for (size_t k = 0; k < std::min(size_t(std::max(0, cap)), p.size()); ++k) { xy[2 * k] = p[k].x; xy[2 * k + 1] = p[k].y; }
    return int32_t(p.size());
}

int32_t vs_blob_get_defects(const vs_blob* t, int32_t blob, vs_blob_defect* out, int32_t cap)
{
    if (!t || blob < 0 || size_t(blob) >= t->outlines.size()) return -1;
    const auto& d = t->outlines[size_t(blob)].defects;
    if (out) std::copy_n(d.begin(), std::min(size_t(std::max(0, cap)), d.size()), out);
    return int32_t(d.size());
}

int32_t vs_blob_teach_outline(vs_blob* t, int32_t blob)
{
    if (!t || blob < 0 || size_t(blob) >= t->outlines.size() || t->outlines[size_t(blob)].pts.size() < 3) return -1;
    const auto& p = t->outlines[size_t(blob)].pts;
    t->taught.set(Pts(p.begin(), p.end()));
    return 0;
}

int32_t vs_blob_set_taught_outline(vs_blob* t, const float* xy, int32_t n)
{
    if (!t || n < 0 || (n > 0 && (!xy || n < 3))) return -1;
    Pts p;
    for (int32_t k = 0; k < n; ++k) {
        if (!std::isfinite(xy[2 * k]) || !std::isfinite(xy[2 * k + 1])) return -1;
        p.emplace_back(xy[2 * k], xy[2 * k + 1]);
    }
    t->taught.set(std::move(p));
    return 0;
}

int32_t vs_blob_get_taught_outline(const vs_blob* t, float* xy, int32_t cap)
{
    if (!t) return -1;
    const auto& p = t->taught.pts;
    if (xy)
        for (size_t k = 0; k < std::min(size_t(std::max(0, cap)), p.size()); ++k) { xy[2 * k] = float(p[k].x); xy[2 * k + 1] = float(p[k].y); }
    return int32_t(p.size());
}

} // extern "C"
