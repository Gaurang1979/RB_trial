// blob_tests - the Blob tool (vs_blob.h) on drawn images whose blobs are known.

#include "vs_blob.h"

#include <opencv2/imgproc.hpp>

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

namespace {

int failures = 0;

void check(bool ok, const std::string& what)
{
    std::printf("%s  %s\n", ok ? "ok  " : "FAIL", what.c_str());
    if (!ok) ++failures;
}

bool near(double v, double want, double tol) { return std::abs(v - want) <= tol; }

double kPiArea();
double kPi180() { return 3.14159265358979323846 / 180; }

std::string f(double v) { char b[32]; std::snprintf(b, sizeof b, "%.3f", v); return b; }

struct Run {
    vs_blob_result r{};
    std::vector<vs_blob_info> blobs, rejected;
    int rc = 0;
};

Run run(const cv::Mat& img, const vs_blob_params& p)
{
    Run out;
    vs_blob* t = vs_blob_create(&p);
    out.rc = vs_blob_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_blob_get_result(t, &out.r);
    out.blobs.resize(size_t(std::max(0, vs_blob_get_blobs(t, nullptr, 0))));
    vs_blob_get_blobs(t, out.blobs.data(), int32_t(out.blobs.size()));
    out.rejected.resize(size_t(std::max(0, vs_blob_get_rejected(t, nullptr, 0))));
    vs_blob_get_rejected(t, out.rejected.data(), int32_t(out.rejected.size()));
    vs_blob_destroy(t);
    return out;
}

vs_blob_params defaults()
{
    vs_blob_params p;
    vs_blob_default_params(&p);
    return p;
}

cv::Mat page(int w = 640, int h = 480, int bg = 210, double noise = 3)
{
    cv::Mat img(h, w, CV_8U, cv::Scalar(bg));
    if (noise > 0) {
        cv::Mat n(img.size(), CV_8U);
        cv::randn(n, 0, noise);
        cv::add(img, n, img);
    }
    return img;
}

void discs()
{
    cv::Mat img(480, 640, CV_8U, cv::Scalar(210));
    // listed in label order: by the first row each disc reaches, then left to right
    const std::vector<cv::Point> at = {{100, 100}, {500, 100}, {300, 120}, {200, 330}, {450, 350}};
    for (auto c : at) cv::circle(img, c, 20, 40, -1, cv::LINE_8);
    cv::Mat n(img.size(), CV_8U);
    cv::randn(n, 0, 3);
    cv::add(img, n, img);
    const Run r = run(img, defaults());
    check(r.rc == 0 && r.r.count == 5, "5 discs, auto threshold: count " + std::to_string(r.r.count));
    // two clean levels: every level between them is Otsu's optimum, and OpenCV takes the first
    check(r.r.threshold_used > 45 && r.r.threshold_used < 195, "Otsu level between the greys: " + std::to_string(r.r.threshold_used));
    bool areas = true, round = true, centres = true, single = true;
    for (size_t i = 0; i < r.blobs.size(); ++i) {
        const auto& b = r.blobs[i];
        areas &= near(b.area, kPiArea(), 0.03 * kPiArea());
        round &= b.circularity > 0.93 && b.elongation < 1.05 && b.compactness < 1.08;
        centres &= near(b.cx, at[i].x, 0.2) && near(b.cy, at[i].y, 0.2);
        single &= b.holes == 0 && near(b.major_axis, 41, 1.5) && b.touches_edge == 0 && b.mean_grey < 60;
    }
    check(areas, "disc areas within 3% of pi r^2 (" + f(r.blobs.empty() ? 0 : r.blobs[0].area) + ")");
    check(round, "disc circularity > 0.93 (" + f(r.blobs.empty() ? 0 : r.blobs[0].circularity) + "), elongation ~1");
    check(centres, "disc centroids within 0.2 px, in label order (top to bottom)");
    check(single, "discs: no holes, ellipse axis ~ diameter, dark mean grey");
}

double kPiArea() { return 3.14159265358979 * 20 * 20; }   // cv::circle fills x^2 + y^2 <= r^2

void squareAndEllipse()
{
    cv::Mat img = page();
    cv::rectangle(img, {100, 100, 40, 40}, 30, -1);
    cv::ellipse(img, {400, 240}, {60, 20}, -30, 0, 360, 30, -1, cv::LINE_8);   // -30 in OpenCV = 30 ccw on screen
    const Run r = run(img, defaults());
    check(r.r.count == 2, "square + ellipse: count " + std::to_string(r.r.count));
    if (r.r.count != 2) return;
    const auto& s = r.blobs[0];
    const auto& e = r.blobs[1];
    check(s.area == 1600 && s.box_w == 40 && s.box_h == 40, "square area 1600, box 40 x 40");
    // the + pi edge correction is exact for a disc; a square's corners want + 4, so it reads 0.83, not pi/4
    check(near(s.circularity, 0.83, 0.02), "square circularity ~ 0.83: " + f(s.circularity));
    check(near(s.perimeter, 160, 5), "square perimeter ~ 160: " + f(s.perimeter));
    check(near(s.rbox_w, 40, 1) && near(s.rbox_h, 40, 1), "square turned box 40 x 40");
    check(near(e.elongation, 3.0, 0.15), "ellipse 60/20: elongation " + f(e.elongation));
    check(near(e.angle_deg, 30, 1.5), "ellipse angle 30 ccw: " + f(e.angle_deg));
    check(near(e.major_axis, 121, 3) && near(e.minor_axis, 41, 2), "ellipse axes " + f(e.major_axis) + " x " + f(e.minor_axis));
    check(near(e.rbox_angle_deg, 30, 2) && near(e.rbox_w, 121, 3), "ellipse turned box angle " + f(e.rbox_angle_deg) + ", length " + f(e.rbox_w));
    check(near(e.cx, 400, 0.3) && near(e.cy, 240, 0.3), "ellipse centroid");
}

void holes()
{
    cv::Mat img = page();
    cv::circle(img, {200, 240}, 60, 30, -1, cv::LINE_8);
    cv::circle(img, {180, 240}, 15, 210, -1, cv::LINE_8);
    cv::circle(img, {230, 240}, 15, 210, -1, cv::LINE_8);
    vs_blob_params p = defaults();
    const Run a = run(img, p);
    check(a.r.count == 1 && a.blobs[0].holes == 2, "disc with two holes: holes " + std::to_string(a.r.count ? a.blobs[0].holes : -1));
    p.fill_holes = 1;
    const Run b = run(img, p);
    check(b.r.count == 1 && b.blobs[0].holes == 0 && b.blobs[0].area > a.blobs[0].area + 1300,
          "fill holes: no holes, area includes them (" + f(b.r.count ? b.blobs[0].area : 0) + ")");
}

void polarityAndBand()
{
    cv::Mat img = page(640, 480, 40);
    cv::rectangle(img, {50, 50, 60, 60}, 220, -1);
    cv::rectangle(img, {200, 50, 60, 60}, 130, -1);
    cv::rectangle(img, {350, 50, 60, 60}, 130, -1);
    vs_blob_params p = defaults();
    p.threshold_mode = VS_BLOB_THRESH_FIXED;
    p.polarity = VS_BLOB_LIGHT_ON_DARK;
    p.threshold = 180;
    check(run(img, p).r.count == 1, "light on dark, fixed 180: the white square only");
    p.threshold = 90;
    check(run(img, p).r.count == 3, "light on dark, fixed 90: all three");
    p.threshold_mode = VS_BLOB_THRESH_BAND;
    p.threshold = 110;
    p.threshold_high = 150;
    const Run b = run(img, p);
    check(b.r.count == 2 && near(b.r.total_area, 7200, 1), "band 110-150: the two mid-grey squares, total area " + f(b.r.total_area));
}

void localThreshold()
{
    // light falls off from left to right; dark spots are 40 below their surroundings everywhere
    cv::Mat img(400, 800, CV_8U);
    for (int x = 0; x < img.cols; ++x) img.col(x).setTo(250 - x * 200 / img.cols);
    for (int i = 0; i < 6; ++i) {
        const int x = 70 + i * 130;
        const int bg = 250 - x * 200 / img.cols;
        cv::circle(img, {x, 200}, 12, bg - 40, -1);
    }
    vs_blob_params p = defaults();
    const Run otsu = run(img, p);
    p.threshold_mode = VS_BLOB_THRESH_LOCAL;
    p.local_window = 51;
    p.local_offset = 15;
    const Run local = run(img, p);
    check(otsu.r.count != 6, "uneven light: Otsu does not find the 6 spots (" + std::to_string(otsu.r.count) + ")");
    check(local.r.count == 6, "uneven light: local threshold finds 6 (" + std::to_string(local.r.count) + ")");
}

void edgeAndRoi()
{
    cv::Mat img = page();
    cv::circle(img, {320, 240}, 20, 30, -1);
    cv::circle(img, {200, 240}, 20, 30, -1);   // cut by the region's left edge below
    vs_blob_params p = defaults();
    p.roi_x = 200; p.roi_y = 150; p.roi_w = 250; p.roi_h = 200;
    const Run a = run(img, p);
    check(a.r.count == 1 && near(a.blobs[0].cx, 320, 0.3) && near(a.blobs[0].cy, 240, 0.3),
          "region: centroid in image coordinates (" + f(a.r.count ? a.blobs[0].cx : 0) + ")");
    check(a.r.rejected == 1 && a.rejected[0].reject_reason == VS_BLOB_REJ_EDGE && a.rejected[0].touches_edge && a.r.edge_count == 1,
          "blob cut by the region's edge: rejected for the edge, counted in edge_count");
    p.exclude_edge = 0;
    check(run(img, p).r.count == 2, "exclude edge off: both");
}

void filtersAndSort()
{
    cv::Mat img = page();
    cv::circle(img, {100, 100}, 25, 30, -1);
    cv::circle(img, {300, 100}, 15, 30, -1);
    cv::circle(img, {500, 100}, 8, 30, -1);
    cv::rectangle(img, {100, 300, 200, 6}, 30, -1);   // a scratch
    for (int i = 0; i < 20; ++i) cv::rectangle(img, {350 + i * 12, 400, 2, 2}, 30, -1);   // specks
    vs_blob_params p = defaults();
    const Run all = run(img, p);
    check(all.r.count == 4, "min area 10 drops the 20 specks: count " + std::to_string(all.r.count));
    int specks = 0;
    for (const auto& b : all.rejected) specks += b.reject_reason == VS_BLOB_REJ_AREA_LOW;
    check(specks == 20 && all.r.rejected == 20, "the specks are rejected for low area");
    p.min_circularity = 0.8f;
    const Run round = run(img, p);
    check(round.r.count == 3 && round.rejected[0].reject_reason == VS_BLOB_REJ_CIRC_LOW,
          "min circularity 0.8 drops the scratch (rejected largest first)");
    p.min_circularity = 0;
    p.min_elongation = 5;
    const Run lines = run(img, p);
    check(lines.r.count == 1 && lines.blobs[0].elongation > 20, "min elongation 5 keeps only the scratch");
    p.min_elongation = 0;
    p.max_area = 1500;
    check(run(img, p).r.count == 3, "max area 1500 drops the large disc");
    p.max_area = 0;
    p.max_circularity = 0;
    p.sort_by = VS_BLOB_SORT_AREA;
    p.sort_descending = 1;
    p.keep_first = 2;
    const Run top = run(img, p);
    check(top.r.count == 2 && top.blobs[0].area > top.blobs[1].area && near(top.blobs[0].cx, 100, 0.5),
          "sort by area, largest first, keep 2");
    int kf = 0;
    for (const auto& b : top.rejected) kf += b.reject_reason == VS_BLOB_REJ_KEEP_FIRST;
    check(kf == 2, "the other two are rejected as past keep-first");
    p.keep_first = 0;
    p.sort_by = VS_BLOB_SORT_X;
    p.sort_descending = 0;
    const Run byX = run(img, p);
    bool ordered = true;
    for (size_t i = 1; i < byX.blobs.size(); ++i) ordered &= byX.blobs[i - 1].cx <= byX.blobs[i].cx;
    check(ordered && byX.blobs.size() == 4, "sort by x");
}

void connectivityAndMorphology()
{
    cv::Mat img(200, 200, CV_8U, cv::Scalar(220));
    cv::rectangle(img, {50, 50, 20, 20}, 20, -1);
    cv::rectangle(img, {70, 70, 20, 20}, 20, -1);    // touches the first only at a corner
    cv::rectangle(img, {120, 50, 20, 20}, 20, -1);
    cv::rectangle(img, {141, 50, 20, 20}, 20, -1);   // one-pixel gap
    vs_blob_params p = defaults();
    p.threshold_mode = VS_BLOB_THRESH_FIXED;
    p.connectivity = 8;
    check(run(img, p).r.count == 3, "8-connected: the corner pair is one blob");
    p.connectivity = 4;
    check(run(img, p).r.count == 4, "4-connected: the corner pair is two");
    p.connectivity = 8;
    p.morphology = VS_BLOB_MORPH_CLOSE3;
    check(run(img, p).r.count == 2, "close 3x3 bridges the one-pixel gap");
    cv::Mat specks = img.clone();
    for (int i = 0; i < 10; ++i) specks.at<uint8_t>(150, 20 + i * 15) = 20;
    p.morphology = VS_BLOB_MORPH_OPEN3;
    p.min_area = 1;
    const Run o = run(specks, p);
    check(o.r.count == 3, "open 3x3 removes single-pixel specks (" + std::to_string(o.r.count) + ")");
}

void flatAndBlack()
{
    const Run flat = run(page(640, 480, 200, 2), defaults());
    check(flat.r.flat == 1 && flat.r.count == 0, "clean region, auto: the level moves off the noise, nothing found");
    cv::Mat spot = page(640, 480, 200, 3);
    cv::circle(spot, {320, 240}, 4, 120, -1);   // 49 px of 307 200: far under 1% of the region
    const Run tiny = run(spot, defaults());
    check(tiny.r.count == 1 && near(tiny.blobs[0].cx, 320, 0.5), "one small spot on a clean region, auto: found (" + std::to_string(tiny.r.count) + ")");
    vs_blob_params lp = defaults();
    lp.polarity = VS_BLOB_LIGHT_ON_DARK;
    cv::Mat light = page(640, 480, 50, 3);
    cv::circle(light, {100, 100}, 4, 140, -1);
    check(run(light, lp).r.count == 1, "one small light spot on a dark clean region, auto: found");
    const Run black = run(cv::Mat(480, 640, CV_8U, cv::Scalar(0)), defaults());
    check(black.r.count == 0 && black.r.region_mean < 1, "black frame: no blobs, region mean 0 (the caller fails it)");
}

void badInput()
{
    vs_blob_params p = defaults();
    vs_blob* t = vs_blob_create(&p);
    check(vs_blob_run(t, nullptr, 10, 10, 10) < 0, "no pixels: error");
    uint8_t px[100] = {};
    check(vs_blob_run(t, px, 10, 10, 5) < 0, "stride below width: error");
    p.roi_x = 50; p.roi_w = 10; p.roi_h = 10;
    vs_blob_set_params(t, &p);
    check(vs_blob_run(t, px, 10, 10, 10) < 0, "region outside the image: error");
    vs_blob_params wrong = p;
    wrong.struct_size = 4;
    check(vs_blob_set_params(t, &wrong) < 0, "wrong struct size refused");
    vs_blob_destroy(t);
}

void speedAndTruncation()
{
    // 5 MP with 1 000 blobs: the spec's target is 10 ms on T1 (an i5 line PC)
    cv::Mat img(2048, 2448, CV_8U, cv::Scalar(200));
    for (int i = 0; i < 1000; ++i) cv::circle(img, {60 + (i % 40) * 58, 60 + (i / 40) * 78}, 10 + i % 7, 40, -1);
    vs_blob_params p = defaults();
    p.time_budget_ms = 500;
    run(img, p);   // warm-up
    const Run r = run(img, p);
    check(r.r.count == 1000 && !r.r.truncated, "5 MP, 1000 blobs: all found");
    std::printf("info  5 MP, 1000 blobs: %.1f ms (target 10 ms on T1)\n", r.r.time_ms);
    p.time_budget_ms = 1;
    const Run slow = run(img, p);
    check(slow.r.truncated == 1 && slow.r.count == 0, "1 ms budget: truncated, no partial list");
    cv::Mat dust(2048, 2448, CV_8U, cv::Scalar(200));
    for (int y = 4; y < dust.rows - 4; y += 8)
        for (int x = 4; x < dust.cols - 4; x += 8) cv::rectangle(dust, {x, y, 4, 4}, 30, -1);
    p.time_budget_ms = 5000;
    const Run d = run(dust, p);
    check(d.r.truncated == 1 && d.r.count == 0, "78 000 blobs past the filters: truncated (too many to measure)");
}

void holesFollowConnectivity()
{
    // a 1 px square frame without its top-left corner pixel: its two ends touch only across a diagonal
    cv::Mat img(200, 200, CV_8U, cv::Scalar(220));
    cv::rectangle(img, {60, 60, 40, 40}, 20, 1, cv::LINE_4);
    img.at<uint8_t>(60, 60) = 220;
    vs_blob_params p = defaults();
    p.threshold_mode = VS_BLOB_THRESH_FIXED;
    p.min_area = 1;
    p.connectivity = 8;
    const Run eight = run(img, p);
    check(eight.r.count == 1 && eight.blobs[0].holes == 1, "8-connected frame closed across the corner: 1 hole");
    p.connectivity = 4;
    const Run four = run(img, p);
    check(four.r.count == 1 && four.blobs[0].holes == 0,
          "4-connected frame open across the corner: no hole (" + std::to_string(four.r.count ? four.blobs[0].holes : -1) + ")");
    p.fill_holes = 1;
    const Run filled = run(img, p);
    check(filled.r.count == 1 && near(filled.blobs[0].area, four.blobs[0].area, 0.5), "4-connected, fill holes: the open frame is not filled");
    p.fill_holes = 0;
    cv::Mat closed(200, 200, CV_8U, cv::Scalar(220));
    cv::rectangle(closed, {60, 60, 40, 40}, 20, 1, cv::LINE_4);
    cv::circle(closed, {150, 150}, 20, 20, -1);
    cv::circle(closed, {143, 150}, 4, 220, -1);
    cv::circle(closed, {157, 150}, 4, 220, -1);
    const Run c4 = run(closed, p);
    check(c4.r.count == 2 && c4.blobs[0].holes == 1 && c4.blobs[1].holes == 2, "4-connected: closed frame 1 hole, disc 2 holes");
}

// ---- contour analysis ----

// paints grey where inside(x, y) holds, each pixel by its share of 4 x 4 samples (an exact anti-aliased edge;
// OpenCV's anti-aliased circles come out about half a pixel large)
template <class F> void paint(cv::Mat& img, cv::Rect box, uint8_t grey, F inside)
{
    box &= cv::Rect(0, 0, img.cols, img.rows);
    for (int y = box.y; y < box.y + box.height; ++y)
        for (int x = box.x; x < box.x + box.width; ++x) {
            int n = 0;
            for (int sy = 0; sy < 4; ++sy)
                for (int sx = 0; sx < 4; ++sx) n += inside(x - 0.375 + 0.25 * sx, y - 0.375 + 0.25 * sy) ? 1 : 0;
            if (n) img.at<uint8_t>(y, x) = cv::saturate_cast<uint8_t>(img.at<uint8_t>(y, x) + (int(grey) - img.at<uint8_t>(y, x)) * n / 16.0);
        }
}

void disc(cv::Mat& img, cv::Point2d c, double r, uint8_t grey)
{
    paint(img, cv::Rect(int(c.x - r) - 2, int(c.y - r) - 2, int(2 * r) + 5, int(2 * r) + 5), grey,
          [&](double x, double y) { return (x - c.x) * (x - c.x) + (y - c.y) * (y - c.y) <= r * r; });
}

struct Traced {
    Run run;
    std::vector<vs_blob_contour> c;
    std::vector<std::vector<cv::Point2f>> outline;
    std::vector<std::vector<vs_blob_defect>> defects;
    std::vector<float> taught;
};

vs_blob_contour_params contourOn()
{
    vs_blob_contour_params c;
    vs_blob_default_contour_params(&c);
    c.enabled = 1;
    return c;
}

// runs with contour analysis; teach = kept blob to teach after the run (-1 none); taught = outline to compare with
Traced trace(const cv::Mat& img, vs_blob_params p, const vs_blob_contour_params& c, const std::vector<float>& taught = {}, int teach = -1)
{
    Traced o;
    p.time_budget_ms = std::max(p.time_budget_ms, 1000);   // these test the outline, not the budget
    vs_blob* t = vs_blob_create(&p);
    vs_blob_set_contour_params(t, &c);
    if (!taught.empty()) vs_blob_set_taught_outline(t, taught.data(), int32_t(taught.size() / 2));
    o.run.rc = vs_blob_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_blob_get_result(t, &o.run.r);
    o.run.blobs.resize(size_t(std::max(0, vs_blob_get_blobs(t, nullptr, 0))));
    vs_blob_get_blobs(t, o.run.blobs.data(), int32_t(o.run.blobs.size()));
    for (int32_t i = 0; i < o.run.r.count; ++i) {
        vs_blob_contour k{};
        k.struct_size = int32_t(sizeof k);
        vs_blob_get_contour(t, i, &k);
        o.c.push_back(k);
        std::vector<float> xy(size_t(2 * std::max(0, vs_blob_get_outline(t, i, nullptr, 0))));
        vs_blob_get_outline(t, i, xy.data(), int32_t(xy.size() / 2));
        std::vector<cv::Point2f> pts;
        for (size_t j = 0; j + 1 < xy.size(); j += 2) pts.emplace_back(xy[j], xy[j + 1]);
        o.outline.push_back(pts);
        std::vector<vs_blob_defect> d(size_t(std::max(0, vs_blob_get_defects(t, i, nullptr, 0))));
        vs_blob_get_defects(t, i, d.data(), int32_t(d.size()));
        o.defects.push_back(d);
    }
    if (teach >= 0 && vs_blob_teach_outline(t, teach) == 0) {
        o.taught.resize(size_t(2 * vs_blob_get_taught_outline(t, nullptr, 0)));
        vs_blob_get_taught_outline(t, o.taught.data(), int32_t(o.taught.size() / 2));
    }
    vs_blob_destroy(t);
    return o;
}

// a part drawn anti-aliased, turned by deg (counter-clockwise on screen) round its centre and moved to (cx, cy):
// a 120 x 60 bar with a 30 x 20 notch in one long side near one end, so it has one clear pose
cv::Mat part(double cx, double cy, double deg, double scale = 1, cv::Point2d bite = {0, 0}, double biteR = 0)
{
    cv::Mat img = page(640, 480, 210, 2);
    const std::vector<cv::Point2d> shape = {{-60, -30}, {60, -30}, {60, 30}, {10, 30}, {10, 10}, {-20, 10}, {-20, 30}, {-60, 30}};
    const double a = -deg * kPi180(), c = std::cos(a), s = std::sin(a);
    std::vector<cv::Point> poly;
    for (const auto& q : shape)
        poly.emplace_back(int(std::lround((cx + scale * (c * q.x - s * q.y)) * 16)), int(std::lround((cy + scale * (s * q.x + c * q.y)) * 16)));
    cv::fillPoly(img, std::vector<std::vector<cv::Point>>{poly}, 40, cv::LINE_AA, 4);
    if (biteR > 0) disc(img, {cx + c * bite.x - s * bite.y, cy + s * bite.x + c * bite.y}, biteR, 210);
    return img;
}

void contourOfDisc()
{
    // anti-aliased disc, r 30, centre between pixels: the outline is placed to a fraction of a pixel
    cv::Mat img = page(400, 300, 210, 2);
    const cv::Point2d c0(200.25, 150.5);
    disc(img, c0, 30, 40);
    const Traced t = trace(img, defaults(), contourOn());
    check(t.run.r.count == 1 && t.c.size() == 1, "contour: one disc traced (" + std::to_string(t.run.r.count) + ")");
    if (t.c.empty()) return;
    const vs_blob_contour& k = t.c[0];
    double sum = 0, worst = 0;
    cv::Point2d centre;   // of the enclosed area (the points are not evenly spaced)
    double a2 = 0;
    const auto& ol = t.outline[0];
    for (size_t j = 0; j < ol.size(); ++j) {
        const cv::Point2d u = ol[j], v = ol[(j + 1) % ol.size()];
        const double c = u.x * v.y - v.x * u.y;
        a2 += c;
        centre += (u + v) * c;
    }
    centre *= 1 / (3 * a2);
    for (const auto& q : t.outline[0]) {
        const double r = std::hypot(q.x - c0.x, q.y - c0.y);
        sum += r;
        worst = std::max(worst, std::abs(r - 30));
    }
    const double mean = sum / double(t.outline[0].size());
    check(near(mean, 30, 0.1) && worst < 0.3, "disc outline radius 30: mean " + f(mean) + ", worst error " + f(worst) + " px");
    check(near(centre.x, c0.x, 0.05) && near(centre.y, c0.y, 0.05), "disc outline centre to 0.05 px (" + f(centre.x) + ", " + f(centre.y) + ")");
    check(near(k.length, 188.5, 2.5), "disc outline length ~ 2 pi 30 = 188.5: " + f(k.length));
    check(near(k.area, 2827.4, 30), "disc outline area ~ pi 30^2 = 2827: " + f(k.area));
    check(k.convexity > 0.99 && k.defects == 0 && k.edge_share > 0.98 && k.closed == 1 && k.taught == 0,
          "disc: convex (" + f(k.convexity) + "), no defects, edge share " + f(k.edge_share) + ", closed");
}

void contourDefects()
{
    // disc r 40 with a bite of r 10 centred 4 px inside its rim: deepest at (226, 200), 12.8 px below the hull chord
    cv::Mat img = page(400, 400, 210, 2);
    disc(img, {200, 200}, 40, 40);
    disc(img, {236, 200}, 10, 210);
    cv::rectangle(img, {60, 60, 50, 50}, 40, -1);
    const Traced t = trace(img, defaults(), contourOn());
    check(t.run.r.count == 2, "dented disc and square: 2 blobs (" + std::to_string(t.run.r.count) + ")");
    if (t.run.r.count != 2) return;
    const vs_blob_contour& sq = t.c[0];
    const vs_blob_contour& d = t.c[1];
    check(sq.defects == 0 && sq.convexity > 0.995, "square: no defects, convexity " + f(sq.convexity));
    check(d.defects == 1 && near(d.max_defect_depth, 12.8, 1.0), "bite: one defect, depth " + f(d.max_defect_depth) + " (12.8 below the sharp cusps' chord; smoothing rounds them)");
    check(near(d.max_defect_x, 226, 0.5) && near(d.max_defect_y, 200, 1.5),
          "bite: deepest point at (226, 200): (" + f(d.max_defect_x) + ", " + f(d.max_defect_y) + ")");
    check(d.convexity < 0.97 && !t.defects[1].empty() && t.defects[1][0].width > 15, "bite: convexity " + f(d.convexity) + ", hull chord over it");
    vs_blob_contour_params deep = contourOn();
    deep.min_defect_depth = 20;
    check(trace(img, defaults(), deep).c[1].defects == 0, "min defect depth 20: the 14 px bite is not listed");
}

void contourTaught()
{
    // teach the part at 0 degrees, then find it turned and moved
    const Traced ref = trace(part(200, 200, 0), defaults(), contourOn(), {}, 0);
    check(ref.taught.size() >= 600, "teach: outline stored (" + std::to_string(ref.taught.size() / 2) + " points)");
    if (ref.taught.size() < 6) return;
    double a2 = 0, mx = 0, my = 0;   // the enclosed area's centroid
    const size_t m = ref.taught.size() / 2;
    for (size_t k = 0; k < m; ++k) {
        const double x0 = ref.taught[2 * k], y0 = ref.taught[2 * k + 1], x1 = ref.taught[2 * ((k + 1) % m)], y1 = ref.taught[2 * ((k + 1) % m) + 1];
        const double c = x0 * y1 - x1 * y0;
        a2 += c; mx += (x0 + x1) * c; my += (y0 + y1) * c;
    }
    mx /= 3 * a2; my /= 3 * a2;
    check(std::abs(mx) < 0.01 && std::abs(my) < 0.01, "teach: stored round its centroid (" + f(mx) + ", " + f(my) + ")");

    const Traced same = trace(part(380.4, 250.7, 35), defaults(), contourOn(), ref.taught);
    if (same.c.empty()) { check(false, "taught: part found"); return; }
    const vs_blob_contour& s = same.c[0];
    check(s.taught == 1 && s.max_deviation < 0.6 && s.mean_deviation < 0.15,
          "same part turned 35 and moved: max deviation " + f(s.max_deviation) + ", mean " + f(s.mean_deviation));
    check(near(s.pose_angle_deg, 35, 0.3), "pose angle 35: " + f(s.pose_angle_deg));
    const Traced upside = trace(part(320, 240, -150), defaults(), contourOn(), ref.taught);
    check(!upside.c.empty() && upside.c[0].max_deviation < 0.6 && near(upside.c[0].pose_angle_deg, -150, 0.3),
          "turned -150 (past the axis' 180 ambiguity): max deviation " + f(upside.c.empty() ? -1 : upside.c[0].max_deviation)
              + ", angle " + f(upside.c.empty() ? 0 : upside.c[0].pose_angle_deg));

    // a 5 px dent in the far short side (x = 60), part turned 35
    const Traced dent = trace(part(380.4, 250.7, 35, 1, {64, 0}, 9), defaults(), contourOn(), ref.taught);
    if (!dent.c.empty()) {
        const vs_blob_contour& d = dent.c[0];
        const double a = -35 * kPi180();
        const cv::Point2d want(380.4 + std::cos(a) * 55, 250.7 + std::sin(a) * 55);
        check(near(d.max_deviation, 5, 0.8), "dent 5 px: max deviation " + f(d.max_deviation));
        check(std::hypot(d.max_deviation_x - want.x, d.max_deviation_y - want.y) < 3,
              "dent: marked at its deepest point (" + f(d.max_deviation_x) + ", " + f(d.max_deviation_y) + ")");
        check(near(d.pose_angle_deg, 35, 0.5), "dent does not pull the placement: angle " + f(d.pose_angle_deg));
    }
    const Traced big = trace(part(320, 240, 10, 1.06), defaults(), contourOn(), ref.taught);
    check(!big.c.empty() && big.c[0].max_deviation > 1.5, "part 6% larger: max deviation " + f(big.c.empty() ? 0 : big.c[0].max_deviation));
}

void contourNoAxis()
{
    // an L with equal arms has no clear axis: 12 start angles find it turned 120
    auto ell = [](double cx, double cy, double deg) {
        cv::Mat img = page(400, 400, 210, 2);
        const std::vector<cv::Point2d> shape = {{-40, -40}, {-10, -40}, {-10, 10}, {40, 10}, {40, 40}, {-40, 40}};
        const double a = -deg * kPi180(), c = std::cos(a), s = std::sin(a);
        std::vector<cv::Point> poly;
        for (const auto& q : shape)
            poly.emplace_back(int(std::lround((cx + c * q.x - s * q.y) * 16)), int(std::lround((cy + s * q.x + c * q.y) * 16)));
        cv::fillPoly(img, std::vector<std::vector<cv::Point>>{poly}, 40, cv::LINE_AA, 4);
        return img;
    };
    const Traced ref = trace(ell(200, 200, 0), defaults(), contourOn(), {}, 0);
    const Traced turned = trace(ell(210.3, 190.6, 120), defaults(), contourOn(), ref.taught);
    check(!turned.c.empty() && turned.c[0].max_deviation < 0.7 && near(turned.c[0].pose_angle_deg, 120, 0.5),
          "L turned 120: max deviation " + f(turned.c.empty() ? -1 : turned.c[0].max_deviation) + ", angle "
              + f(turned.c.empty() ? 0 : turned.c[0].pose_angle_deg));
}

void contourNotChecked()
{
    cv::Mat img = page();
    cv::circle(img, {200, 240}, 20, 30, -1);
    cv::circle(img, {320, 240}, 20, 30, -1);
    vs_blob_params p = defaults();
    p.roi_x = 200; p.roi_y = 150; p.roi_w = 250; p.roi_h = 200;
    p.exclude_edge = 0;
    p.sort_by = VS_BLOB_SORT_X;
    const Traced cut = trace(img, p, contourOn());
    check(cut.c.size() == 2 && cut.c[0].closed == 0 && cut.c[1].closed == 1, "outline cut by the region's edge: closed 0");
    vs_blob_contour_params high = contourOn();
    high.edge_level = 250;   // no grey reaches it: nothing to place the outline on
    const Traced weak = trace(img, defaults(), high);
    check(!weak.c.empty() && weak.c[0].edge_share < 0.05, "edge level no grey crosses: edge share " + f(weak.c.empty() ? 1 : weak.c[0].edge_share));
}

void contourApi()
{
    vs_blob_params p = defaults();
    vs_blob* t = vs_blob_create(&p);
    cv::Mat img = page();
    cv::circle(img, {320, 240}, 20, 30, -1);
    vs_blob_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_blob_contour k{};
    k.struct_size = int32_t(sizeof k);
    check(vs_blob_get_contour(t, 0, &k) < 0 && vs_blob_get_outline(t, 0, nullptr, 0) < 0, "contour analysis off: no contour");
    vs_blob_contour_params c = contourOn();
    c.struct_size = 8;
    check(vs_blob_set_contour_params(t, &c) < 0, "contour params: wrong struct size refused");
    c = contourOn();
    vs_blob_set_contour_params(t, &c);
    vs_blob_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    check(vs_blob_get_contour(t, 0, &k) == 0 && k.points > 50, "contour on: blob 0 traced");
    k.struct_size = 4;
    check(vs_blob_get_contour(t, 0, &k) < 0 && vs_blob_get_contour(t, 1, &k) < 0, "wrong out struct size, or index past the kept blobs: refused");
    const float two[4] = {0, 0, 1, 1};
    check(vs_blob_set_taught_outline(t, two, 2) < 0 && vs_blob_set_taught_outline(t, nullptr, 0) == 0, "taught outline of 2 points refused; 0 clears");
    check(vs_blob_teach_outline(t, 5) < 0, "teach a blob that is not there: refused");
    vs_blob_destroy(t);
}

void contourSpeed()
{
    // the spec's target: 5 ms for a 2 000-point outline (taught, so placement and deviation included)
    cv::Mat img = page(1000, 1000, 210, 2);
    cv::circle(img, {500, 500}, 400, 40, -1, cv::LINE_AA);
    cv::circle(img, {900, 500}, 30, 210, -1, cv::LINE_AA);
    vs_blob_params p = defaults();
    p.time_budget_ms = 1000;
    const Traced ref = trace(img, p, contourOn(), {}, 0);
    trace(img, p, contourOn(), ref.taught);   // warm-up
    const Traced r = trace(img, p, contourOn(), ref.taught);
    vs_blob_contour_params off = contourOn();
    off.enabled = 0;
    const Traced plain = trace(img, p, off);
    check(!r.c.empty() && r.c[0].points > 1800 && r.c[0].max_deviation < 0.5, "large outline traced and compared with itself");
    std::printf("info  outline of %d points, taught: contour %.1f ms (run %.1f ms with, %.1f ms without; target 5 ms)\n",
                r.c.empty() ? 0 : r.c[0].points, r.run.r.time_ms - plain.run.r.time_ms, r.run.r.time_ms, plain.run.r.time_ms);
}

} // namespace

int main()
{
    discs();
    squareAndEllipse();
    holes();
    holesFollowConnectivity();
    polarityAndBand();
    localThreshold();
    edgeAndRoi();
    filtersAndSort();
    connectivityAndMorphology();
    flatAndBlack();
    badInput();
    speedAndTruncation();
    contourOfDisc();
    contourDefects();
    contourTaught();
    contourNoAxis();
    contourNotChecked();
    contourApi();
    contourSpeed();
    std::printf("%s (%d failed)\n", failures ? "FAILED" : "all passed", failures);
    return failures ? 1 : 0;
}
