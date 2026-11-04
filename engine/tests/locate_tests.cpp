// locate_tests - the Locate tool (vs_locate.h) on drawn parts with a known shift and turn, and on the
// geometry truth set (geometry_truth.hpp) at the requirement: 0.05 px and 0.05 deg.
//
//   locate_tests                       synthetic cases, the truth set and a large pattern on 20 MP
//   locate_tests large                 only the large pattern
//   LOC_TRUTH_OUT=<folder> locate_tests
//                                      also: write the truth set's study renders and truth.csv there
//   LOC_IMAGES=<folder> LOC_ROI=x,y,w,h locate_tests
//                                      also: teach on the first image of the folder, find it in all
//                                      (found rate, score, time, spread of the found angle)

#include "vs_locate.h"

#include "geometry_truth.hpp"

#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <random>
#include <string>
#include <thread>
#include <vector>
#include <limits>

#include "check.h"

// The tool's time budget is a limit of the optimized build. In a Debug build each locate tool made here gets
// vs_test_ms() of its budget, so the reads behind the correctness checks finish (Req 17.11 AUD-13).
static vs_loc* testLocCreate(const vs_loc_params* p)
{
    if (!p) return vs_loc_create(p);
    vs_loc_params q = *p;
    q.time_budget_ms = vs_test_ms(q.time_budget_ms);
    return vs_loc_create(&q);
}
#define vs_loc_create(p) testLocCreate(p)

namespace {

int failures = 0;

void check(bool ok, const std::string& what)
{
    std::printf("%s  %s\n", ok ? "ok  " : "FAIL", what.c_str());
    if (!ok) ++failures;
}

// a label: border, a few bars and text, on a grey background with noise
cv::Mat part(double dx, double dy, double deg, int bg = 60)
{
    cv::Mat label(300, 500, CV_8U, cv::Scalar(215));
    cv::rectangle(label, {0, 0, 500, 300}, 40, 4);
    for (int i = 0; i < 30; ++i) cv::rectangle(label, {40 + i * 9, 40, 2 + (i * 7) % 5, 110}, 20, -1);
    cv::putText(label, "LOT A12345", {40, 210}, cv::FONT_HERSHEY_SIMPLEX, 1.3, 30, 3, cv::LINE_AA);
    cv::putText(label, "EXP 09/28", {40, 265}, cv::FONT_HERSHEY_SIMPLEX, 1.1, 30, 2, cv::LINE_AA);
    cv::Mat img(900, 1200, CV_8U, cv::Scalar(bg));
    const cv::Point2d c(600 + dx, 450 + dy);
    const double a = deg * CV_PI / 180.0, al = std::cos(a), be = std::sin(a);
    // label centre (250, 150) goes to c, turned counter-clockwise by deg
    cv::Matx23d M(al, be, c.x - (al * 250 + be * 150), -be, al, c.y - (-be * 250 + al * 150));
    cv::warpAffine(label, img, M, img.size(), cv::INTER_LINEAR, cv::BORDER_TRANSPARENT);
    cv::Mat noise(img.size(), CV_8U);
    cv::randn(noise, 0, 4);
    cv::add(img, noise, img);
    return img;
}

void synthetic()
{
    const cv::Mat teach = part(0, 0, 0);
    vs_loc_params p;
    vs_loc_default_params(&p);
    p.max_angle_deg = 20;
    vs_loc* t = vs_loc_create(&p);
    // pattern: the bars and LOT line (part of the label), centre at (600 - 250 + 260, 450 - 150 + 120)
    check(vs_loc_teach(t, teach.data, teach.cols, teach.rows, int(teach.step), 380, 330, 300, 200) == 0, "teach");
    const double ox = 380 + 150, oy = 330 + 100;
    struct Case { double dx, dy, deg; };
    const Case cases[] = {{0, 0, 0}, {37.3, -21.6, 0}, {-120, 80, 6.5}, {55, 40, -12.3}, {-10, 5, 19}};
    for (const auto& c : cases) {
        const cv::Mat img = part(c.dx, c.dy, c.deg);
        vs_loc_run(t, img.data, img.cols, img.rows, int(img.step));
        vs_loc_result r;
        vs_loc_get_result(t, &r);
        // where the taught centre should be now: turned about the label centre (600, 450) then shifted
        const double a = c.deg * CV_PI / 180.0;
        const double px = ox - 600, py = oy - 450;
        const double ex = 600 + c.dx + std::cos(a) * px + std::sin(a) * py;
        const double ey = 450 + c.dy - std::sin(a) * px + std::cos(a) * py;
        const double err = std::hypot(r.cx - ex, r.cy - ey), aerr = std::abs(r.angle_deg - c.deg);
        char b[200];
        std::snprintf(b, sizeof b, "shift (%.1f, %.1f) turn %.1f: found %d score %.2f, error %.2f px %.2f deg, %.1f ms",
                      c.dx, c.dy, c.deg, r.found, r.score, err, aerr, r.time_ms);
        check(r.found && err < 0.05 && aerr < 0.05, b);
    }
    // nothing to find: a blank image
    const cv::Mat blank(900, 1200, CV_8U, cv::Scalar(60));
    vs_loc_run(t, blank.data, blank.cols, blank.rows, int(blank.step));
    vs_loc_result r;
    vs_loc_get_result(t, &r);
    check(!r.found, "blank image: not found (score " + std::to_string(r.score) + ")");
    // stored pattern round trip
    int w = 0, h = 0;
    float sx = 0, sy = 0;
    const int need = vs_loc_get_pattern(t, nullptr, 0, &w, &h, &sx, &sy);
    std::vector<uint8_t> buf(size_t(std::max(need, 0)));
    vs_loc_get_pattern(t, buf.data(), need, &w, &h, &sx, &sy);
    vs_loc* t2 = vs_loc_create(&p);
    vs_loc_set_pattern(t2, buf.data(), w, h, w, sx, sy);
    const cv::Mat img = part(12, 7, 3);
    vs_loc_run(t, img.data, img.cols, img.rows, int(img.step));
    vs_loc_run(t2, img.data, img.cols, img.rows, int(img.step));
    vs_loc_result a, b2;
    vs_loc_get_result(t, &a);
    vs_loc_get_result(t2, &b2);
    check(std::abs(a.cx - b2.cx) < 1e-3 && std::abs(a.angle_deg - b2.angle_deg) < 1e-3, "stored pattern finds the same place");
    vs_loc_destroy(t);
    vs_loc_destroy(t2);
}

// Bad input through the C API, found by the fuzzers (tests/fuzz, scripts/fuzz-engine.sh, PR #174).
void badInput()
{
    const cv::Mat teach = part(0, 0, 0);
    vs_loc_params p;
    vs_loc_default_params(&p);
    // NaN turn: the number of turns was int(ceil(NaN)), undefined behaviour; now the default
    p.max_angle_deg = std::numeric_limits<float>::quiet_NaN();
    vs_loc* t = vs_loc_create(&p);
    check(vs_loc_teach(t, teach.data, teach.cols, teach.rows, int(teach.step), 380, 330, 300, 200) == 0, "NaN turn: teach");
    vs_loc_run(t, teach.data, teach.cols, teach.rows, int(teach.step));
    vs_loc_result r{};
    vs_loc_get_result(t, &r);
    check(r.found == 1, "NaN turn: found as with the default turn");
    vs_loc_destroy(t);
    // NaN score limit: never found before (score >= NaN is false); now the strictest limit, 0.99
    vs_loc_default_params(&p);
    p.min_score = std::numeric_limits<float>::quiet_NaN();
    t = vs_loc_create(&p);
    vs_loc_teach(t, teach.data, teach.cols, teach.rows, int(teach.step), 380, 330, 300, 200);
    const cv::Mat moved = part(20, 10, 4);
    vs_loc_run(t, moved.data, moved.cols, moved.rows, int(moved.step));
    vs_loc_get_result(t, &r);
    check(r.found == (r.score >= 0.99f ? 1 : 0), "NaN score limit: the strictest (score " + std::to_string(r.score) + ")");
    vs_loc_destroy(t);
    // a small pattern over a 20 MP image, turned either way: hundreds of full-resolution correlations
    // (minutes). The search stops at 10 times the budget, at least 500 ms, and reports not found
    vs_loc_default_params(&p);
    p.max_angle_deg = 180.f;
    t = vs_loc_create(&p);
    // on the label's bars (label corner at (350, 300), bars from (390, 340))
    check(vs_loc_teach(t, teach.data, teach.cols, teach.rows, int(teach.step), 400, 360, 16, 16) == 0, "teach a 16 px pattern");
    cv::Mat big(3648, 5472, CV_8U);
    cv::randu(big, 0, 256);
    const auto t0 = std::chrono::steady_clock::now();
    const int32_t rc = vs_loc_run(t, big.data, big.cols, big.rows, int(big.step));
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count();
    vs_loc_get_result(t, &r);
    check(rc == -1 && !r.found && VS_TIME_BUDGET(ms < 3000), "16 px pattern, 20 MP, turn 180: stops, not found (" + std::to_string(ms) + " ms)");
    vs_loc_destroy(t);
}

// --- the geometry truth set: a 2448 x 2048 (5 MP) image, a 320 x 202 px pattern, poses with a random
// sub-pixel shift and turn; errors against the rendered pose

struct Errors {
    std::vector<double> ex, ey, pos, ang, ms;
    int missed = 0;
};

double quantile(std::vector<double> v, double q)
{
    if (v.empty()) return 0;
    std::sort(v.begin(), v.end());
    return v[size_t(q * double(v.size() - 1))];
}

double mean(const std::vector<double>& v)
{
    double s = 0;
    for (double x : v) s += x;
    return v.empty() ? 0 : s / double(v.size());
}

double sd(const std::vector<double>& v)
{
    const double m = mean(v);
    double s = 0;
    for (double x : v) s += (x - m) * (x - m);
    return v.size() < 2 ? 0 : std::sqrt(s / double(v.size() - 1));
}

struct TruthRig {
    vs_loc* t = nullptr;
    truth::Pose teach{1224.3, 1024.2, 0};
    cv::Point2d origin;
    int kW = 2448, kH = 2048;   // image
    int size = 1, K = 8;        // label drawn size times larger, rendered K x K finer

    // size 1: 5 MP, a 320 x 202 px pattern; size 3 on 20 MP: a 960 x 606 px pattern
    explicit TruthRig(int labelSize = 1, int imgW = 2448, int imgH = 2048, int k = 8) : kW(imgW), kH(imgH), size(labelSize), K(k)
    {
        teach = {imgW / 2.0 + 0.3, imgH / 2.0 + 0.2, 0};
        vs_loc_params p;
        vs_loc_default_params(&p);
        p.max_angle_deg = 20;
        t = vs_loc_create(&p);
        truth::Degrade d;
        d.blur = 1.0;
        d.noise = 2;
        d.seed = 7;
        const cv::Mat img = truth::render(teach, d, kW, kH, 60, K, size);
        // the bars, both lines of text and the ring: label x 15..335, y 20..222
        const int x = int(std::floor(teach.x + size * (15 - truth::kLabelCentre.x))), y = int(std::floor(teach.y + size * (20 - truth::kLabelCentre.y)));
        const int w = 320 * size, h = 202 * size;
        vs_loc_teach(t, img.data, img.cols, img.rows, int(img.step), x, y, w, h);
        origin = {x + w / 2.0, y + h / 2.0};
    }
    ~TruthRig() { vs_loc_destroy(t); }

    // where the taught centre lies when the label is at pose p
    cv::Point2d expected(const truth::Pose& p) const
    {
        const double a = p.deg * CV_PI / 180.0, c = std::cos(a), s = std::sin(a);
        const cv::Point2d d = origin - cv::Point2d(teach.x, teach.y);
        return {p.x + c * d.x + s * d.y, p.y - s * d.x + c * d.y};
    }

    void run(const truth::Pose& p, const truth::Degrade& d, Errors& e, const char* save = nullptr)
    {
        const cv::Mat img = truth::render(p, d, kW, kH, 60, K, size);
        vs_loc_run(t, img.data, img.cols, img.rows, int(img.step));
        vs_loc_result r;
        vs_loc_get_result(t, &r);
        const cv::Point2d x = expected(p);
        if (std::getenv("LOC_VERBOSE"))
            std::printf("    pose %.2f %.2f %.2f blur %.2f noise %.1f gain %.2f offset %.0f ramp %.0f %.0f occluder %dx%d: "
                        "found %d score %.3f error %.3f px %.3f deg\n", p.x, p.y, p.deg, d.blur, d.noise, d.gain, d.offset,
                        d.rampX, d.rampY, d.occluder.width, d.occluder.height, r.found, r.score,
                        std::hypot(r.cx - x.x, r.cy - x.y), r.angle_deg - p.deg);
        if (!r.found) { ++e.missed; return; }
        e.ex.push_back(r.cx - x.x);
        e.ey.push_back(r.cy - x.y);
        e.pos.push_back(std::hypot(r.cx - x.x, r.cy - x.y));
        e.ang.push_back(r.angle_deg - p.deg);
        e.ms.push_back(r.time_ms);
        if (save) {
            cv::imwrite(save, img);
        }
    }
};

std::string summary(const char* name, const Errors& e, int n)
{
    std::vector<double> aa;
    for (double a : e.ang) aa.push_back(std::abs(a));
    char b[400];
    std::snprintf(b, sizeof b,
                  "%s: found %d/%d; position error mean %.3f max %.3f px (bias %+.3f, %+.3f); "
                  "turn error mean %.3f max %.3f deg (bias %+.3f); time median %.1f max %.1f ms",
                  name, n - e.missed, n, mean(e.pos), quantile(e.pos, 1), mean(e.ex), mean(e.ey),
                  mean(aa), quantile(aa, 1), mean(e.ang), quantile(e.ms, 0.5), quantile(e.ms, 1));
    return b;
}

double maxAbs(const std::vector<double>& v)
{
    double m = 0;
    for (double x : v) m = std::max(m, std::abs(x));
    return m;
}

void truthSet()
{
    TruthRig rig;
    std::mt19937 rng(20261031);
    auto U = [&rng](double a, double b) { return std::uniform_real_distribution<double>(a, b)(rng); };
    auto pose = [&] { return truth::Pose{rig.teach.x + U(-300, 300), rig.teach.y + U(-300, 300), U(-15, 15)}; };
    const char* out = std::getenv("LOC_TRUTH_OUT");
    FILE* csv = nullptr;
    if (out) {
        std::filesystem::create_directories(out);
        csv = std::fopen((std::string(out) + "/truth.csv").c_str(), "w");
        if (csv) std::fprintf(csv, "file,cx,cy,angle_deg,blur,noise\n");
    }

    // the study's set (04a, 1.1): 100 renders, random sub-pixel shift, +-15 deg, blur 0.8-1.5, noise 2
    {
        Errors e;
        for (int i = 0; i < 100; ++i) {
            const truth::Pose p = pose();
            truth::Degrade d;
            d.blur = U(0.8, 1.5);
            d.noise = 2;
            d.seed = unsigned(100 + i);
            std::string file;
            if (csv) {
                char name[32];
                std::snprintf(name, sizeof name, "truth_%03d.png", i);
                file = std::string(out) + "/" + name;
                const cv::Point2d x = rig.expected(p);
                std::fprintf(csv, "%s,%.4f,%.4f,%.4f,%.3f,%.1f\n", name, x.x, x.y, p.deg, d.blur, d.noise);
            }
            rig.run(p, d, e, csv ? file.c_str() : nullptr);
        }
        check(e.missed == 0 && maxAbs(e.pos) <= 0.05 && maxAbs(e.ang) <= 0.05, summary("truth, study set (blur 0.8-1.5, noise 2)", e, 100));
    }
    if (csv) std::fclose(csv);

    // harder: blur 0.6-2, noise 1-4
    {
        Errors e;
        for (int i = 0; i < 50; ++i) {
            truth::Degrade d;
            d.blur = U(0.6, 2.0);
            d.noise = U(1, 4);
            d.seed = unsigned(300 + i);
            rig.run(pose(), d, e);
        }
        check(e.missed == 0 && maxAbs(e.pos) <= 0.05 && maxAbs(e.ang) <= 0.05, summary("truth, blur 0.6-2, noise 1-4", e, 50));
    }
    // light: gain 0.5-1.5, offset +-30, a gradient of up to 80 grey levels across the image, kept off
    // the camera's limits (an exposure that clips the white is reported below, not tested)
    {
        Errors e;
        for (int i = 0; i < 50; ++i) {
            truth::Degrade d;
            d.blur = 1.0;
            d.noise = 2;
            d.rampX = U(-80, 80);
            d.rampY = U(-80, 80);
            const double ramp = 0.5 * (std::abs(d.rampX) + std::abs(d.rampY)) + 6;
            d.gain = U(0.5, 1.5);
            // label white 215, darkest print 20, background 60
            d.gain = std::min(d.gain, (255 - ramp - 0) / 215.0);
            const double lo = ramp - 20 * d.gain, hi = 255 - ramp - 215 * d.gain;
            d.offset = std::clamp(U(-30, 30), lo, hi);
            d.seed = unsigned(400 + i);
            rig.run(pose(), d, e);
        }
        check(e.missed == 0 && maxAbs(e.pos) <= 0.05 && maxAbs(e.ang) <= 0.05, summary("truth, light change", e, 50));
    }
    // over-exposed: the label white clipped at 255 (gain 1.3-1.5); clipping moves edges, so only reported
    {
        Errors e;
        for (int i = 0; i < 20; ++i) {
            truth::Degrade d;
            d.blur = 1.0;
            d.noise = 2;
            d.gain = U(1.3, 1.5);
            d.offset = U(0, 30);
            d.seed = unsigned(450 + i);
            rig.run(pose(), d, e);
        }
        std::printf("info  %s\n", summary("truth, over-exposed (white clipped)", e, 20).c_str());
    }
    // occlusion: a block of another grey over 0-30% of the pattern. Reported, not checked: the find is
    // the correlation's, and with a quarter hidden it can miss, pick the bars one period away, or be
    // off by more than a degree, beyond what the edge fit corrects (a pending item)
    {
        Errors e;
        for (int i = 0; i < 50; ++i) {
            const truth::Pose p = pose();
            truth::Degrade d;
            d.blur = 1.0;
            d.noise = 2;
            d.seed = unsigned(500 + i);
            const double f = U(0, 0.3), sw = 320 * std::sqrt(f), sh = 202 * std::sqrt(f);
            const cv::Point2d c = rig.expected(p) + cv::Point2d(U(-1, 1) * (160 - sw / 2), U(-1, 1) * (101 - sh / 2));
            d.occluder = cv::Rect(int(c.x - sw / 2), int(c.y - sh / 2), int(sw), int(sh));
            d.occluderGrey = U(20, 235);
            rig.run(p, d, e);
        }
        int met = 0, off = 0, wrong = 0;
        for (size_t i = 0; i < e.pos.size(); ++i) {
            if (e.pos[i] > 2.0) ++wrong;
            else if (e.pos[i] > 0.05 || std::abs(e.ang[i]) > 0.05) ++off;
            else ++met;
        }
        std::printf("info  truth, occlusion 0-30%%: found %d/50: %d to the requirement, %d off by 0.05-2 px, %d at another place\n",
                    50 - e.missed, met, off, wrong);
    }
    // variable print: the taught lot number differs on every part (a fifth of the pattern's edges)
    {
        Errors e;
        for (int i = 0; i < 30; ++i) {
            truth::Degrade d;
            d.blur = U(0.8, 1.5);
            d.noise = 2;
            d.seed = unsigned(550 + i);
            char lot[16];
            std::snprintf(lot, sizeof lot, "LOT %c%05d", 'A' + i % 26, (i * 7919 + 1234) % 100000);
            d.lot = lot;
            rig.run(pose(), d, e);
        }
        check(e.missed == 0 && maxAbs(e.pos) <= 0.05 && maxAbs(e.ang) <= 0.05, summary("truth, variable lot number", e, 30));
    }
    // repeatability: one still part, 100 captures (noise 2, blur 1): 3 sigma within 0.05 px / 0.05 deg
    {
        Errors e;
        const truth::Pose p{rig.teach.x + 101.37, rig.teach.y - 57.81, 7.29};
        for (int i = 0; i < 100; ++i) {
            truth::Degrade d;
            d.blur = 1.0;
            d.noise = 2;
            d.seed = unsigned(600 + i);
            rig.run(p, d, e);
        }
        const double sx = sd(e.ex), sy = sd(e.ey), sa = sd(e.ang);
        char b[200];
        std::snprintf(b, sizeof b, "truth, repeatability on a still part: 3 sigma x %.4f y %.4f px, turn %.4f deg (%s)",
                      3 * sx, 3 * sy, 3 * sa, summary("", e, 100).c_str() + 2);
        check(e.missed == 0 && 3 * sx <= 0.05 && 3 * sy <= 0.05 && 3 * sa <= 0.05, b);
    }
}

// a large pattern on a 20 MP camera (5472 x 3648, the label 3 times larger: a 960 x 606 px pattern).
// The correlation then searches on a coarse copy (a fine pixel is about 5 image pixels) and the edge
// fit must still reach the image's edges: the requirement (0.05 px, 0.05 deg) holds as for a small
// pattern, within the default time budget
void largePattern()
{
    TruthRig rig(3, 5472, 3648, 4);
    std::mt19937 rng(20261103);
    auto U = [&rng](double a, double b) { return std::uniform_real_distribution<double>(a, b)(rng); };
    vs_loc_params p;
    vs_loc_default_params(&p);   // the rig keeps the default budget
    Errors e;
    for (int i = 0; i < 30; ++i) {
        truth::Degrade d;
        d.blur = U(0.8, 1.5);
        d.noise = 2;
        d.seed = unsigned(900 + i);
        rig.run({rig.teach.x + U(-1500, 1500), rig.teach.y + U(-800, 800), U(-15, 15)}, d, e);
    }
    check(e.missed == 0 && maxAbs(e.pos) <= 0.05 && maxAbs(e.ang) <= 0.05,
          summary("large pattern, 960 x 606 px on 20 MP (blur 0.8-1.5, noise 2)", e, 30));
    // time: the median within the budget, judged only on a quiet machine (load at most half the cores,
    // as the bench): reads run against the clock, so a busy machine slows good code
    double load[1] = {0};
    const unsigned cores = std::max(1u, std::thread::hardware_concurrency());
    char b[200];
    std::snprintf(b, sizeof b, "large pattern: median time %.1f ms, budget %d ms", quantile(e.ms, 0.5), p.time_budget_ms);
    if (getloadavg(load, 1) == 1 && load[0] > cores / 2.0)
        std::printf("skip  %s: not judged, the machine is busy (load %.1f, %u cores)\n", b, load[0], cores);
    else
        check(!e.ms.empty() && VS_TIME_BUDGET(quantile(e.ms, 0.5) <= p.time_budget_ms), b);
}

void realImages(const char* folder, const char* roi)
{
    namespace fs = std::filesystem;
    std::vector<fs::path> files;
    for (const auto& e : fs::directory_iterator(folder))
        if (e.path().extension() == ".bmp" || e.path().extension() == ".png") files.push_back(e.path());
    std::sort(files.begin(), files.end());
    int x, y, w, h;
    if (files.empty() || std::sscanf(roi, "%d,%d,%d,%d", &x, &y, &w, &h) != 4) return;
    vs_loc_params p;
    vs_loc_default_params(&p);
    vs_loc* t = vs_loc_create(&p);
    cv::Mat first = cv::imread(files[0].string(), cv::IMREAD_GRAYSCALE);
    if (vs_loc_teach(t, first.data, first.cols, first.rows, int(first.step), x, y, w, h) != 0) {
        std::printf("teach failed: %s\n", vs_loc_last_error(t));
        return;
    }
    int found = 0, n = 0;
    std::vector<double> ms, deg, dx, sc;
    for (const auto& f : files) {
        cv::Mat g = cv::imread(f.string(), cv::IMREAD_GRAYSCALE);
        if (g.empty()) continue;
        vs_loc_run(t, g.data, g.cols, g.rows, int(g.step));
        vs_loc_result r;
        vs_loc_get_result(t, &r);
        ++n;
        found += r.found;
        ms.push_back(r.time_ms);
        sc.push_back(r.score);
        if (r.found) { deg.push_back(r.angle_deg); dx.push_back(r.dx); }
        else std::printf("  not found: %s (score %.2f)\n", f.filename().string().c_str(), r.score);
    }
    auto pct = [](std::vector<double> v, double q) { if (v.empty()) return 0.0; std::sort(v.begin(), v.end()); return v[size_t(q * double(v.size() - 1))]; };
    std::printf("real: found %d / %d, score median %.2f, time median %.1f p95 %.1f max %.1f ms, turn %.1f..%.1f deg, shift x %.0f..%.0f px\n",
                found, n, pct(sc, 0.5), pct(ms, 0.5), pct(ms, 0.95), pct(ms, 1.0), pct(deg, 0), pct(deg, 1), pct(dx, 0), pct(dx, 1));
    vs_loc_destroy(t);
}

} // namespace

int main(int argc, char** argv)
{
    // locate_tests large: only the large pattern case
    const bool largeOnly = argc > 1 && std::string(argv[1]) == "large";
    if (!largeOnly) {
        synthetic();
        badInput();
        truthSet();
    }
    largePattern();
    if (const char* f = std::getenv("LOC_IMAGES")) realImages(f, std::getenv("LOC_ROI") ? std::getenv("LOC_ROI") : "");
    std::printf("\n%s (%d failed)\n", failures ? "FAILED" : "all passed", failures);
    return failures ? 1 : 0;
}
