// stress_tests - several readers at once, as on a two-camera line (225 parts/min).
// Run: build/stress_tests [seconds] (default 20; exit code 0 = passed). Built with
// -fsanitize=thread it finds data races and deadlocks; in the normal build it is a short soak that
// checks every result. Thread sanitizer build (run it under the heavy-test lock, AGENTS.md rule 13):
//   cmake -S engine -B engine/build-tsan -DCMAKE_BUILD_TYPE=RelWithDebInfo \
//     -DCMAKE_CXX_FLAGS="-fsanitize=thread -fno-omit-frame-pointer" \
//     -DCMAKE_EXE_LINKER_FLAGS=-fsanitize=thread -DCMAKE_SHARED_LINKER_FLAGS=-fsanitize=thread
//   cmake --build engine/build-tsan --target stress_tests
//   TSAN_OPTIONS=second_deadlock_stack=1 STRESS_READERS=4 engine/build-tsan/stress_tests 300
// With STRESS_ENHANCER=1 every reader also loads enhancer.onnx, runs the enhancer right after the plain
// attempt (CR_ENHANCE_EARLY) on defocused codes the plain attempt cannot read, and decodes with
// cr_params.threads at twice the core count, so several workers meet at one enhancer net (Req 17.1 ENG-04):
//   TSAN_OPTIONS=second_deadlock_stack=1 STRESS_ENHANCER=1 engine/build-tsan/stress_tests 120
//
// What runs at the same time, each handle on its own thread (the C APIs allow one thread per handle):
//   - N camera readers (2-4, STRESS_READERS), each decoding its own case with the locator model,
//     carrying its learned state across a re-create every few parts, as the runtime does on a job load;
//   - OCR tools reading a drawn line with ocrnet.onnx, and locate tools finding a drawn part;
//   - a churn thread creating and destroying readers, OCR and locate tools while the others run;
//   - every handle loads the same model files (codenet.onnx, ocrnet.onnx).
// Checks: no reader ever returns a code of another camera (each camera's codes carry its own
// prefix), every case is read in full most of the time, OCR and locate keep giving the right answer.

#include "codereader.h"
#include "vs_locate.h"
#include "vs_ocr.h"

#include "qrcodegen.hpp"

#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <mutex>
#include <set>
#include <string>
#include <thread>
#include <vector>

namespace {

using Clock = std::chrono::steady_clock;

std::atomic<int> failures{0};
std::mutex printMu;

void fail(const std::string& what)
{
    std::lock_guard<std::mutex> lock(printMu);
    std::printf("FAIL %s\n", what.c_str());
    ++failures;
}

void note(const std::string& what)
{
    std::lock_guard<std::mutex> lock(printMu);
    std::printf("%s\n", what.c_str());
}

const std::string kModels = CR_MODEL_DIR;
const std::string kCodeNet = kModels + "/codenet.onnx";
const std::string kEnhancer = kModels + "/enhancer.onnx";
bool g_enhancer = false; // STRESS_ENHANCER=1: enhancer loaded, threads above the cores, defocused codes
const std::string kOcrNet = kModels + "/ocrnet.onnx", kCharset = kModels + "/ocr_charset.txt";

bool exists(const std::string& path) { return static_cast<bool>(std::ifstream(path)); }

// One camera's case: four QR codes in a row on a label, texts "C<camera>-<serial>".
struct Case {
    cv::Mat img;
    std::set<std::string> texts;
};

Case drawCase(int camera, int part)
{
    Case c;
    // with STRESS_ENHANCER the defocused part carries 4 rows of codes: more regions than enhancer nets
    // (one per core) want the enhancer at once, so workers share a net
    const int rows = (g_enhancer && part == 2) ? 4 : 1;
    c.img = cv::Mat(480 + (rows - 1) * 160, 960, CV_8U, cv::Scalar(70));
    cv::rectangle(c.img, {20, 100, 920, 280 + (rows - 1) * 160}, 215, -1);
    for (int k = 0; k < 4 * rows; ++k) {
        char text[32];
        std::snprintf(text, sizeof text, "C%d-%06d", camera, part * 16 + k);
        c.texts.insert(text);
        const auto q = qrcodegen::QrCode::encodeText(text, qrcodegen::QrCode::Ecc::MEDIUM);
        const int n = q.getSize(), cell = 7, x0 = 60 + (k % 4) * 220, y0 = 150 + (k / 4) * 160;
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
                if (q.getModule(x, y)) cv::rectangle(c.img, {x0 + x * cell, y0 + y * cell, cell, cell}, 25, -1);
    }
    if (g_enhancer && part == 2) cv::GaussianBlur(c.img, c.img, {0, 0}, 3.0); // the plain attempt fails, the enhancer is tried
    cv::Mat noise(c.img.size(), CV_8U);
    cv::randn(noise, 0, 3);
    cv::add(c.img, noise, c.img);
    return c;
}

cr_params readerParams()
{
    cr_params p;
    cr_default_params(&p);
    p.format = CR_QR;
    p.expected_count = g_enhancer ? 16 : 4; // the defocused part carries 16
    p.time_budget_ms = 2000; // generous: under the thread sanitizer a decode is 5-15x slower
    if (g_enhancer) {
        p.enhancer_mode = CR_ENHANCE_EARLY;
        p.threads = 2 * static_cast<int32_t>(std::max(1u, std::thread::hardware_concurrency())); // more workers than nets
    }
    return p;
}

cr_reader* makeReader(const std::string& learned)
{
    const cr_params p = readerParams();
    cr_reader* r = cr_create(&p);
    if (!r) return nullptr;
    if (exists(kCodeNet) && cr_load_model(r, kCodeNet.c_str()) != 0) fail("codenet.onnx loads");
    if (g_enhancer && cr_load_enhancer(r, kEnhancer.c_str()) != 0) fail("enhancer.onnx loads");
    if (!learned.empty() && cr_set_learned(r, learned.c_str()) != 0) fail("learned state loads back");
    return r;
}

std::string learnedOf(const cr_reader* r)
{
    const int n = cr_get_learned(r, nullptr, 0); // the size with its terminating zero
    if (n <= 1) return {};
    std::string s(static_cast<size_t>(n), '\0');
    cr_get_learned(r, &s[0], n);
    s.resize(static_cast<size_t>(n) - 1);
    return s;
}

struct Counts {
    std::atomic<int> parts{0}, full{0}, recreated{0};
};

// One camera: decode its own parts in a loop; re-create the reader every 7 parts, carrying its
// learned state over. A code of another camera, or a code not on this part, is a mixed-up result.
void cameraLoop(int camera, Clock::time_point until, Counts& n)
{
    std::vector<Case> cases;
    for (int i = 0; i < 3; ++i) cases.push_back(drawCase(camera, i));
    cr_reader* r = makeReader({});
    if (!r) { fail("cr_create camera " + std::to_string(camera)); return; }
    for (int part = 0; Clock::now() < until; ++part) {
        const Case& c = cases[static_cast<size_t>(part) % cases.size()];
        if (cr_decode(r, c.img.data, c.img.cols, c.img.rows, static_cast<int32_t>(c.img.step)) < 0) {
            fail("camera " + std::to_string(camera) + " decode error: " + cr_last_error(r));
            continue;
        }
        std::set<std::string> got;
        for (int i = 0; i < cr_code_count(r); ++i) {
            cr_code code;
            if (cr_get_code(r, i, &code) != 0) continue;
            const std::string text = code.text;
            if (!c.texts.count(text)) fail("camera " + std::to_string(camera) + " part " + std::to_string(part) + ": code \"" + text + "\" is not on this part");
            got.insert(text);
        }
        ++n.parts;
        if (got == c.texts) ++n.full;
        if (part % 7 == 6) {
            const std::string learned = learnedOf(r);
            cr_destroy(r);
            r = makeReader(learned);
            if (!r) { fail("cr_create camera " + std::to_string(camera)); return; }
            ++n.recreated;
        }
    }
    cr_destroy(r);
}

// A drawn print line, as the OCR tests draw it (FONT_HERSHEY_SIMPLEX, dark on light).
cv::Mat drawLine(const std::string& text)
{
    cv::Mat img(200, 700, CV_8U, cv::Scalar(200));
    cv::putText(img, text, {60, 120}, cv::FONT_HERSHEY_SIMPLEX, 30 / 22.0, 80, 3, cv::LINE_AA);
    cv::Mat noise(img.size(), CV_8U);
    cv::randn(noise, 0, 2);
    cv::add(img, noise, img);
    return img;
}

vs_ocr* makeOcr()
{
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.char_height_px = 30;
    p.time_budget_ms = 4000;
    vs_ocr* t = vs_ocr_create(&p);
    if (!t) return nullptr;
    if (vs_ocr_load_model(t, kOcrNet.c_str(), kCharset.c_str()) != 0) fail(std::string("ocrnet.onnx loads: ") + vs_ocr_last_error(t));
    vs_ocr_set_expected(t, 0, "LOT A12345");
    return t;
}

void ocrLoop(Clock::time_point until, std::atomic<int>& runs, std::atomic<int>& passes)
{
    const cv::Mat img = drawLine("LOT A12345");
    vs_ocr* t = makeOcr();
    if (!t) { fail("vs_ocr_create"); return; }
    while (Clock::now() < until) {
        vs_ocr_run(t, img.data, img.cols, img.rows, static_cast<int32_t>(img.step));
        vs_ocr_stats st;
        vs_ocr_get_stats(t, &st);
        ++runs;
        if (st.pass == 1) ++passes;
    }
    vs_ocr_destroy(t);
}

// A part on a dark belt, shifted by (dx, dy): the locate tool's teach image and its runs.
cv::Mat drawPart(int dx, int dy)
{
    cv::Mat label(300, 500, CV_8U, cv::Scalar(215));
    cv::rectangle(label, {0, 0, 500, 300}, 40, 4);
    for (int i = 0; i < 30; ++i) cv::rectangle(label, {40 + i * 9, 40, 2 + (i * 7) % 5, 110}, 20, -1);
    cv::putText(label, "LOT A12345", {40, 210}, cv::FONT_HERSHEY_SIMPLEX, 1.3, 30, 3, cv::LINE_AA);
    cv::Mat img(600, 800, CV_8U, cv::Scalar(60));
    label.copyTo(img(cv::Rect(150 + dx, 150 + dy, 500, 300)));
    return img;
}

vs_loc* makeLocate()
{
    vs_loc_params p;
    vs_loc_default_params(&p);
    p.time_budget_ms = 2000;
    vs_loc* t = vs_loc_create(&p);
    if (!t) return nullptr;
    const cv::Mat teach = drawPart(0, 0);
    if (vs_loc_teach(t, teach.data, teach.cols, teach.rows, static_cast<int32_t>(teach.step), 180, 170, 300, 200) != 0)
        fail(std::string("locate teach: ") + vs_loc_last_error(t));
    return t;
}

void locateLoop(Clock::time_point until, std::atomic<int>& runs, std::atomic<int>& found)
{
    vs_loc* t = makeLocate();
    if (!t) { fail("vs_loc_create"); return; }
    for (int i = 0; Clock::now() < until; ++i) {
        const int dx = (i % 5) * 9 - 18, dy = (i % 3) * 11 - 11;
        const cv::Mat img = drawPart(dx, dy);
        vs_loc_run(t, img.data, img.cols, img.rows, static_cast<int32_t>(img.step));
        vs_loc_result res;
        vs_loc_get_result(t, &res);
        ++runs;
        if (res.found && std::abs(res.dx - dx) < 2.f && std::abs(res.dy - dy) < 2.f) ++found;
    }
    vs_loc_destroy(t);
}

// Handles made and dropped while the cameras run: a job load or a tool added in the designer.
void churnLoop(Clock::time_point until, std::atomic<int>& cycles)
{
    const Case c = drawCase(9, 0);
    const cv::Mat line = drawLine("LOT A12345");
    while (Clock::now() < until) {
        cr_reader* r = makeReader({});
        if (r) {
            cr_decode(r, c.img.data, c.img.cols, c.img.rows, static_cast<int32_t>(c.img.step));
            for (int i = 0; i < cr_code_count(r); ++i) {
                cr_code code;
                if (cr_get_code(r, i, &code) == 0 && !c.texts.count(code.text)) fail(std::string("churn reader read \"") + code.text + "\"");
            }
            cr_destroy(r);
        }
        if (vs_ocr* t = makeOcr()) {
            vs_ocr_run(t, line.data, line.cols, line.rows, static_cast<int32_t>(line.step));
            vs_ocr_destroy(t);
        }
        if (vs_loc* t = makeLocate()) vs_loc_destroy(t);
        ++cycles;
    }
}

} // namespace

int main(int argc, char** argv)
{
    const int seconds = argc > 1 ? std::max(1, std::atoi(argv[1])) : 20;
    const char* nr = std::getenv("STRESS_READERS");
    const int readers = std::min(4, std::max(2, nr ? std::atoi(nr) : 3));
    g_enhancer = std::getenv("STRESS_ENHANCER") && std::atoi(std::getenv("STRESS_ENHANCER")) != 0;
    if (g_enhancer && !exists(kEnhancer)) { note("STRESS_ENHANCER needs " + kEnhancer); return 1; }
    const bool haveOcr = exists(kOcrNet) && exists(kCharset);
    if (!exists(kCodeNet)) note("note: no " + kCodeNet + ": readers run without the locator model");
    if (!haveOcr) note("note: no " + kOcrNet + ": OCR threads skipped");
    note("stress: " + std::to_string(readers) + " camera readers, 2 OCR, 1 locate, 1 churn thread for " + std::to_string(seconds) + " s");

    const auto until = Clock::now() + std::chrono::seconds(seconds);
    std::vector<Counts> cams(static_cast<size_t>(readers));
    std::atomic<int> ocrRuns{0}, ocrPasses{0}, locRuns{0}, locFound{0}, churn{0};
    std::vector<std::thread> threads;
    for (int c = 0; c < readers; ++c) threads.emplace_back(cameraLoop, c, until, std::ref(cams[static_cast<size_t>(c)]));
    if (haveOcr)
        for (int i = 0; i < 2; ++i) threads.emplace_back(ocrLoop, until, std::ref(ocrRuns), std::ref(ocrPasses));
    threads.emplace_back(locateLoop, until, std::ref(locRuns), std::ref(locFound));
    threads.emplace_back(churnLoop, until, std::ref(churn));
    for (auto& t : threads) t.join();

    // Every part must be read in full most of the time: a race that loses codes shows here. The
    // margin allows a decode cut short by the time budget on a loaded machine.
    for (int c = 0; c < readers; ++c) {
        const Counts& n = cams[static_cast<size_t>(c)];
        char b[160];
        std::snprintf(b, sizeof b, "camera %d: %d parts, %d read in full, %d re-creates with learned state", c, n.parts.load(), n.full.load(),
                      n.recreated.load());
        note(b);
        // with STRESS_ENHANCER one part in three is defocused and may stay unread
        if (n.parts == 0 || n.full * 10 < n.parts * (g_enhancer ? 6 : 9)) fail(std::string("camera reads in full: ") + b);
    }
    char b[200];
    std::snprintf(b, sizeof b, "ocr: %d runs, %d OK; locate: %d runs, %d found in place; churn: %d cycles", ocrRuns.load(), ocrPasses.load(),
                  locRuns.load(), locFound.load(), churn.load());
    note(b);
    if (haveOcr && (ocrRuns == 0 || ocrPasses * 10 < ocrRuns * 9)) fail(std::string("ocr reads the line: ") + b);
    if (locRuns == 0 || locFound != locRuns) fail(std::string("locate finds every part: ") + b);
    if (churn == 0) fail("churn thread made no cycle");
    std::printf("%s (%d failures)\n", failures ? "FAILED" : "passed", failures.load());
    return failures ? 1 : 0;
}
