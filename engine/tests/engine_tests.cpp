// engine_tests - unit tests for engine internals that the C API does not expose.
// Run: build/engine_tests (exit code 0 = all passed).

#include "reader.hpp"
#include "pool.hpp"
#include "integrity.hpp"
#include "codereader.h"

#include "qrcodegen.hpp"

#include <ZXing/ReadBarcode.h>
#include <ZXing/CreateBarcode.h>
#include <ZXing/Barcode.h>
#include <ZXing/WriteBarcode.h>

#ifdef CR_HAVE_DMTX
#include <dmtx.h>
#endif

#include <opencv2/imgproc.hpp>

#include <chrono>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <functional>
#include <limits>
#include <map>
#include <mutex>
#include <random>
#include <set>
#include <thread>
#include <tuple>

#include "check.h"

// The tool's time budget is a limit of the optimized build. In a Debug build each reader made here gets
// vs_test_ms() of its budget, so the reads behind the correctness checks finish (Req 17.11 AUD-13).
static cr_reader* testCrCreate(const cr_params* p)
{
    if (!p) return cr_create(p);
    cr_params q = *p;
    q.time_budget_ms = vs_test_ms(q.time_budget_ms);
    return cr_create(&q);
}
#define cr_create(p) testCrCreate(p)

namespace {

int failures = 0;

}

namespace cr { cv::Mat homography4(const cv::Point2f s[4], const cv::Point2f d[4]); } // quality.cpp

namespace {

void check(bool ok, const char* what)
{
    std::printf("%s %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) ++failures;
}

void check(bool ok, const std::string& what) { check(ok, what.c_str()); }

// n x n module means of a clean symbol: dark 50, light 200
cv::Mat means(const std::vector<uint8_t>& dark, int n)
{
    cv::Mat m(n, n, CV_32F);
    for (int i = 0; i < n * n; ++i) m.at<float>(i / n, i % n) = dark[static_cast<size_t>(i)] ? 50.f : 200.f;
    return m;
}

// A 16 x 16 Data Matrix seen in part (lattice.cpp, maskedSoftDecodeDm): its right columns off the
// label (on the dark cap) or out of the image. Modules not seen are Reed-Solomon erasures; random
// grids, a wrong-size symbol and another product's code must never read.
void testDmSeenInPart()
{
    using namespace cr;
    std::mt19937 rng(11);
    const std::string text = "CLUK A 060715180";
    std::vector<uint8_t> dark;
    check(encodeDm(text, 16, dark), "seen in part: encode 16 x 16");
    std::vector<std::string> known;
    for (int k = 181; k <= 186; ++k) known.push_back("CLUK A 060715" + std::to_string(k));
    std::normal_distribution<float> noise(0.f, 22.f);
    // means with the columns from `hideFrom` on the cap (dark, no texture) and not seen
    const auto partial = [&](const std::vector<uint8_t>& d, int n, int hideFrom, cv::Mat& seen) {
        cv::Mat m = means(d, n);
        seen = cv::Mat(n, n, CV_8U, cv::Scalar(1));
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x) {
                if (x >= hideFrom) {
                    m.at<float>(y, x) = 35.f;
                    seen.at<uint8_t>(y, x) = 0;
                }
                m.at<float>(y, x) += noise(rng);
            }
        return m;
    };
    {
        int soft = 0, masked = 0, wrong = 0;
        for (int t = 0; t < 60; ++t) {
            cv::Mat seen;
            const cv::Mat m = partial(dark, 16, 14, seen);
            const std::string s = softDecodeDm(m, 16), k = maskedSoftDecodeDm(m, seen, 16, &known);
            soft += s == text;
            masked += k == text;
            wrong += (!s.empty() && s != text) + (!k.empty() && k != text);
        }
        std::printf("     right clock + 1 column off the label: soft %d/60, seen-in-part %d/60, wrong %d\n", soft, masked, wrong);
        check(masked >= 55 && masked > soft && wrong == 0, "seen-in-part decoding reads a symbol whose clock track is off the label");
    }
    {
        // the bottom L arm and the row above it out of the image
        int masked = 0, wrong = 0;
        for (int t = 0; t < 60; ++t) {
            cv::Mat m = means(dark, 16), seen(16, 16, CV_8U, cv::Scalar(1));
            for (int y = 0; y < 16; ++y)
                for (int x = 0; x < 16; ++x) {
                    if (y >= 14) { m.at<float>(y, x) = 200.f; seen.at<uint8_t>(y, x) = 0; }
                    m.at<float>(y, x) += noise(rng);
                }
            const std::string k = maskedSoftDecodeDm(m, seen, 16, &known);
            masked += k == text;
            wrong += !k.empty() && k != text;
        }
        check(masked >= 55 && wrong == 0, "seen-in-part decoding reads a symbol whose L arm is out of the image");
    }
    {
        // adversarial: random grids, and the true L and clocks around random data, each with a
        // random strip of up to 3 columns or rows not seen
        std::uniform_real_distribution<float> u(0.f, 255.f);
        std::uniform_int_distribution<int> strip(0, 3), side(0, 3), bit(0, 1);
        int reads = 0, tries = 0;
        for (int t = 0; t < 40000; ++t) {
            cv::Mat m(16, 16, CV_32F), seen(16, 16, CV_8U, cv::Scalar(1));
            const bool frame = t % 2 == 1;
            const int w = strip(rng), sd = side(rng);
            for (int y = 0; y < 16; ++y)
                for (int x = 0; x < 16; ++x) {
                    const bool fixed = x == 0 || y == 15 || y == 0 || x == 15;
                    m.at<float>(y, x) = frame && fixed ? (dark[static_cast<size_t>(y * 16 + x)] ? 50.f : 200.f) + noise(rng)
                                        : frame ? (bit(rng) ? 50.f : 200.f) + noise(rng) : u(rng);
                    const int edge = sd == 0 ? x : sd == 1 ? 15 - x : sd == 2 ? y : 15 - y;
                    if (edge < w) seen.at<uint8_t>(y, x) = 0;
                }
            ++tries;
            reads += !maskedSoftDecodeDm(m, seen, 16).empty();
        }
        check(reads == 0, "seen-in-part decoding reads nothing from " + std::to_string(tries) + " random grids");
    }
    {
        // another product: a 16 x 16 code of another text with this product's codes known, and a
        // 14 x 14 code sitting in the 16 x 16 window's L corner with the rest not seen
        std::vector<uint8_t> other, small;
        check(encodeDm("8901030865278 26", 16, other) && encodeDm("31100017404", 14, small), "seen in part: encode other products");
        int reads = 0;
        for (int t = 0; t < 200; ++t) {
            cv::Mat seen;
            const cv::Mat m = partial(other, 16, 14 - t % 3, seen);
            reads += !maskedSoftDecodeDm(m, seen, 16, &known).empty();
            cv::Mat w(16, 16, CV_32F, cv::Scalar(200.f)), s2(16, 16, CV_8U, cv::Scalar(1));
            for (int y = 0; y < 14; ++y)
                for (int x = 0; x < 14; ++x) w.at<float>(y + 2, x) = small[static_cast<size_t>(y * 14 + x)] ? 50.f : 200.f;
            for (int y = 0; y < 16; ++y)
                for (int x = 0; x < 16; ++x) {
                    if (x >= 14 + t % 2) s2.at<uint8_t>(y, x) = 0;
                    w.at<float>(y, x) += noise(rng);
                }
            reads += !maskedSoftDecodeDm(w, s2, 16).empty();
        }
        check(reads == 0, "seen-in-part decoding never reads another product's code (400)");
    }

    // the lattice reader on a cap label: 6 px per module, turned 8 degrees; the label ends two
    // modules before the code's right edge (the rest on the dark cap), or the image ends there
    const auto scene = [&](bool offLabel, bool code) {
        constexpr int px = 6, n = 16;
        cv::Mat img(260, 260, CV_32F, cv::Scalar(30));
        const int x0 = 70, y0 = 80;
        cv::rectangle(img, cv::Rect(x0 - 4 * px, y0 - 4 * px, (n + 6) * px, (n + 8) * px), cv::Scalar(205), -1);
        std::uniform_int_distribution<int> bit(0, 1);
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
                if (code ? dark[static_cast<size_t>(y * n + x)] != 0 : bit(rng) != 0)
                    img(cv::Rect(x0 + x * px, y0 + y * px, px, px)).setTo(45);
        if (offLabel) img(cv::Rect(x0 + (n - 2) * px, 0, 260 - x0 - (n - 2) * px, 260)).setTo(30);
        // turned about the code's centre (as cv::getRotationMatrix2D, which OpenCV 5 moved)
        const double a = 8 * CV_PI / 180, ca = std::cos(a), sa = std::sin(a), cx = x0 + n * px / 2.0, cy = y0 + n * px / 2.0;
        const cv::Mat R = (cv::Mat_<double>(2, 3) << ca, sa, (1 - ca) * cx - sa * cy, -sa, ca, sa * cx + (1 - ca) * cy);
        cv::Mat r;
        cv::warpAffine(img, r, R, img.size(), cv::INTER_LINEAR, cv::BORDER_REPLICATE);
        cv::GaussianBlur(r, r, cv::Size(), 1.0);
        for (auto it = r.begin<float>(); it != r.end<float>(); ++it) *it += noise(rng) * 0.4f;
        cv::Mat g8;
        r.convertTo(g8, CV_8U);
        // out of the image: the crop ends two modules before the code's right edge
        return offLabel ? g8 : g8(cv::Rect(0, 0, x0 + (n - 2) * px, 260)).clone();
    };
    for (const bool offLabel : {true, false}) {
        int reads = 0, wrong = 0;
        for (int t = 0; t < 6; ++t) {
            std::string got;
            cv::Point2f q[4];
            if (latticeDm(scene(offLabel, true), 96.f, 16, 3000, got, q, &known)) {
                reads += got == text;
                wrong += got != text;
            }
        }
        std::printf("     lattice, %s: %d/6, wrong %d\n", offLabel ? "right columns off the label" : "right columns out of the image", reads, wrong);
        check(reads >= 5 && wrong == 0, offLabel ? "lattice reads a Data Matrix partly off its label" : "lattice reads a Data Matrix partly out of the image");
    }
    {
        int reads = 0;
        for (int t = 0; t < 20; ++t) {
            std::string got;
            cv::Point2f q[4];
            reads += latticeDm(scene(t % 2 == 0, false), 96.f, 16, 3000, got, q, &known);
        }
        check(reads == 0, "lattice reads nothing from random modules on a cap label (20)");
    }
    {
        // the same code cut by the image's edge (only the seen-in-part windows can read it), on a
        // case whose codes read so far are of another serial run (900-905): a read that fills in
        // hidden modules must continue their run, so none
        std::vector<std::string> farRun;
        for (int k = 900; k <= 905; ++k) farRun.push_back("CLUK A 060715" + std::to_string(k));
        int reads = 0;
        for (int t = 0; t < 6; ++t) {
            std::string got;
            cv::Point2f q[4];
            reads += latticeDm(scene(false, true), 96.f, 16, 3000, got, q, &farRun);
        }
        check(reads == 0, "lattice: a code seen in part outside the case's serial run is not read (6)");
    }
}

// A 16 x 16 Data Matrix on a wrinkled cap label (lattice.cpp, warped grid): the label bends the
// module grid by up to 0.35 module in waves of 12 modules (the local pitch changes by about 18 %),
// so one straight lattice drifts off.
void testDmWarpedGrid()
{
    using namespace cr;
    std::mt19937 rng(23);
    const std::string text = "CLUK A 060715200";
    std::vector<uint8_t> dark;
    check(encodeDm(text, 16, dark), "warped grid: encode 16 x 16");
    std::vector<std::string> known;
    for (int k = 195; k <= 205; k += 2) known.push_back("CLUK A 060715" + std::to_string(k));
    std::normal_distribution<float> noise(0.f, 6.f);
    std::uniform_real_distribution<float> phase(0.f, 6.283f);
    const auto scene = [&](bool code, double amp, double period) {
        constexpr int px = 7, n = 16;
        cv::Mat img(240, 240, CV_32F, cv::Scalar(30));
        const int x0 = 64, y0 = 64;
        cv::rectangle(img, cv::Rect(x0 - 3 * px, y0 - 3 * px, (n + 6) * px, (n + 6) * px), cv::Scalar(205), -1);
        std::uniform_int_distribution<int> bit(0, 1);
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
                if (code ? dark[static_cast<size_t>(y * n + x)] != 0 : bit(rng) != 0)
                    img(cv::Rect(x0 + x * px, y0 + y * px, px, px)).setTo(45);
        // the wrinkle: each axis shifted by a sine of the other
        const double a = amp * px, L = period * px, p1 = phase(rng), p2 = phase(rng);
        cv::Mat mx(img.size(), CV_32F), my(img.size(), CV_32F);
        for (int y = 0; y < img.rows; ++y)
            for (int x = 0; x < img.cols; ++x) {
                mx.at<float>(y, x) = static_cast<float>(x + a * std::sin(2 * CV_PI * y / L + p1));
                my.at<float>(y, x) = static_cast<float>(y + a * std::sin(2 * CV_PI * x / L + p2));
            }
        cv::Mat w;
        cv::remap(img, w, mx, my, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
        cv::GaussianBlur(w, w, cv::Size(), 1.0);
        for (auto it = w.begin<float>(); it != w.end<float>(); ++it) *it += noise(rng);
        cv::Mat g8;
        w.convertTo(g8, CV_8U);
        return g8;
    };
    const auto run = [&](double amp, double period, int& reads, int& wrong) {
        for (int t = 0; t < 8; ++t) {
            std::string got;
            cv::Point2f q[4];
            if (latticeDm(scene(true, amp, period), 112.f, 16, 4000, got, q, &known)) {
                reads += got == text;
                wrong += got != text;
            }
        }
    };
    int reads = 0, wrong = 0, hard = 0, hardWrong = 0;
    run(0.35, 12, reads, wrong);
    run(0.6, 9, hard, hardWrong);
    std::printf("     wrinkled label: 0.35 module / 12 modules %d/8, 0.6 / 9 %d/8, wrong %d\n", reads, hard, wrong + hardWrong);
    check(reads >= 6 && wrong + hardWrong == 0, "lattice reads a Data Matrix on a wrinkled label");
    int noise0 = 0;
    for (int t = 0; t < 20; ++t) {
        std::string got;
        cv::Point2f q[4];
        noise0 += latticeDm(scene(false, 0.35, 12), 112.f, 16, 4000, got, q, &known);
    }
    check(noise0 == 0, "lattice reads nothing from random modules on a wrinkled label (20)");
    // the case's codes read so far are another serial run: a read must continue their run
    std::vector<std::string> farRun;
    for (int k = 900; k <= 905; ++k) farRun.push_back("CLUK A 060715" + std::to_string(k));
    int far = 0;
    for (int t = 0; t < 6; ++t) {
        std::string got;
        cv::Point2f q[4];
        far += latticeDm(scene(true, 0.35, 12), 112.f, 16, 4000, got, q, &farRun);
    }
    check(far == 0, "lattice: a wrinkled code outside the case's serial run is not read (6)");
}

// A synthetic line image for the miss diagnosis: a version 1 QR (21 x 21) of m px per module on a
// white label (light) on a dark case, Gaussian noise. at = the code's top-left corner.
cv::Mat diagScene(float m, cv::Point2f at, int light = 200, int dark = 40, bool code = true)
{
    cv::Mat img(400, 400, CV_32F, cv::Scalar(60));
    const float side = 21 * m;
    cv::rectangle(img, cv::Rect(static_cast<int>(at.x - 4 * m), static_cast<int>(at.y - 4 * m), static_cast<int>(side + 8 * m),
                                static_cast<int>(side + 8 * m)), cv::Scalar(light), -1);
    if (code) {
        const qrcodegen::QrCode q = qrcodegen::QrCode::encodeText("RJ0187477804", qrcodegen::QrCode::Ecc::MEDIUM);
        for (int y = 0; y < q.getSize(); ++y)
            for (int x = 0; x < q.getSize(); ++x)
                if (q.getModule(x, y))
                    cv::rectangle(img, cv::Point2f(at.x + x * m, at.y + y * m), cv::Point2f(at.x + (x + 1) * m - 0.01f, at.y + (y + 1) * m - 0.01f),
                                  cv::Scalar(dark), -1);
    }
    // area-sample the module edges (a camera integrates over each pixel)
    cv::Mat out, noise(img.size(), CV_32F);
    cv::GaussianBlur(img, img, cv::Size(), 0.5);
    cv::RNG rng(5);
    rng.fill(noise, cv::RNG::NORMAL, 0, 3);
    img += noise;
    img.convertTo(out, CV_8U);
    return out;
}

// ---- drawn 1D labels for the symbology tests: modules (1 = bar), narrow = 1 module, wide = 3
std::vector<uint8_t> code39Modules(const std::string& text)
{
    static const char* kChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%*";
    // wide elements (bar, space, ... , bar) of each character, as zxing's table
    static const int kWide[44] = {0x034, 0x121, 0x061, 0x160, 0x031, 0x130, 0x070, 0x025, 0x124, 0x064, 0x109, 0x049, 0x148, 0x019,
                                  0x118, 0x058, 0x00D, 0x10C, 0x04C, 0x01C, 0x103, 0x043, 0x142, 0x013, 0x112, 0x052, 0x007, 0x106,
                                  0x046, 0x016, 0x181, 0x0C1, 0x1C0, 0x091, 0x190, 0x0D0, 0x085, 0x184, 0x0C4, 0x0A8, 0x0A2, 0x08A,
                                  0x02A, 0x094};
    std::vector<uint8_t> m;
    for (char ch : "*" + text + "*") {
        const int v = kWide[std::strchr(kChars, ch) - kChars];
        for (int e = 0; e < 9; ++e) m.insert(m.end(), (v >> (8 - e)) & 1 ? 3 : 1, static_cast<uint8_t>(e % 2 == 0));
        m.push_back(0); // gap between characters
    }
    m.pop_back();
    return m;
}

std::vector<uint8_t> itfModules(const std::string& digits)
{
    static const char* kPat[10] = {"nnwwn", "wnnnw", "nwnnw", "wwnnn", "nnwnw", "wnwnn", "nwwnn", "nnnww", "wnnwn", "nwnwn"};
    std::vector<uint8_t> m = {1, 0, 1, 0}; // start
    for (size_t i = 0; i + 1 < digits.size(); i += 2)
        for (int e = 0; e < 5; ++e) {
            m.insert(m.end(), kPat[digits[i] - '0'][e] == 'w' ? 3 : 1, 1);
            m.insert(m.end(), kPat[digits[i + 1] - '0'][e] == 'w' ? 3 : 1, 0);
        }
    m.insert(m.end(), {1, 1, 1, 0, 1}); // stop: wide bar, narrow space, narrow bar
    return m;
}

std::vector<uint8_t> ean8Modules(const std::string& digits)
{
    static const char* kL[10] = {"0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"};
    std::string bits = "101";
    for (int i = 0; i < 4; ++i) bits += kL[digits[static_cast<size_t>(i)] - '0'];
    bits += "01010";
    for (int i = 4; i < 8; ++i)
        for (char b : std::string(kL[digits[static_cast<size_t>(i)] - '0'])) bits += b == '0' ? '1' : '0'; // R = L inverted
    bits += "101";
    std::vector<uint8_t> m;
    for (char b : bits) m.push_back(b == '1');
    return m;
}

// EAN-13 modules, then (when addon is set) the 2- or 5-digit add-on 9 modules after it
std::vector<uint8_t> ean13Modules(const std::string& d, const std::string& addon = {})
{
    static const char* kL[10] = {"0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"};
    static const char* kParity[10] = {"LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"};
    const auto inv = [](std::string s) { for (char& c : s) c = c == '0' ? '1' : '0'; return s; };
    const auto g = [&](int v) { const std::string r = inv(kL[v]); return std::string(r.rbegin(), r.rend()); };
    std::string bits = "101";
    for (int i = 1; i < 7; ++i) bits += kParity[d[0] - '0'][i - 1] == 'L' ? kL[d[static_cast<size_t>(i)] - '0'] : g(d[static_cast<size_t>(i)] - '0');
    bits += "01010";
    for (int i = 7; i < 13; ++i) bits += inv(kL[d[static_cast<size_t>(i)] - '0']);
    bits += "101";
    if (!addon.empty()) {
        // EAN-5 parity from the weighted sum (3, 9, ...); EAN-2 from the value modulo 4
        static const char* k5[10] = {"GGLLL", "GLGLL", "GLLGL", "GLLLG", "LGGLL", "LLGGL", "LLLGG", "LGLGL", "LGLLG", "LLGLG"};
        static const char* k2[4] = {"LL", "LG", "GL", "GG"};
        int sum = 0;
        for (size_t i = 0; i < addon.size(); ++i) sum += (addon[i] - '0') * (i % 2 ? 9 : 3);
        const char* par = addon.size() == 5 ? k5[sum % 10] : k2[std::stoi(addon) % 4];
        bits += std::string(9, '0') + "1011";
        for (size_t i = 0; i < addon.size(); ++i) {
            if (i) bits += "01";
            bits += par[i] == 'L' ? std::string(kL[addon[i] - '0']) : g(addon[i] - '0');
        }
    }
    std::vector<uint8_t> m;
    for (char b : bits) m.push_back(b == '1');
    return m;
}

// Codabar modules (narrow 1, wide 3), start and stop characters in the text
std::vector<uint8_t> codabarModules(const std::string& text)
{
    static const char* kChars = "0123456789-$:/.+ABCD";
    static const int kWide[20] = {0x003, 0x006, 0x009, 0x060, 0x012, 0x042, 0x021, 0x024, 0x030, 0x048,
                                  0x00C, 0x018, 0x045, 0x051, 0x054, 0x015, 0x01A, 0x029, 0x00B, 0x00E};
    std::vector<uint8_t> m;
    for (size_t k = 0; k < text.size(); ++k) {
        const int v = kWide[std::strchr(kChars, text[k]) - kChars];
        for (int e = 0; e < 7; ++e) m.insert(m.end(), (v >> (6 - e)) & 1 ? 3 : 1, static_cast<uint8_t>(e % 2 == 0));
        if (k + 1 < text.size()) m.push_back(0);
    }
    return m;
}

// a symbol from its element widths in modules, starting with a bar
std::vector<uint8_t> runsModules(const std::vector<int>& runs)
{
    std::vector<uint8_t> m;
    for (size_t i = 0; i < runs.size(); ++i) m.insert(m.end(), static_cast<size_t>(runs[i]), uint8_t(i % 2 == 0));
    return m;
}

// GS1 DataBar symbols made by zint (zxing-cpp 3.1 CreateBarcode), from the left guard's bar to the
// right guard's bar (the left guard's light module and Limited's 5 trailing ones are left to the
// quiet zone), with the plain text zxing reads from them
const std::vector<int> kDataBarOmni = {1, 5, 1, 1, 1, 5, 1, 1, 1, 3, 5, 5, 1, 1, 4, 1, 2, 1, 1, 1, 3, 2, 1, 3, 2, 1, 3,
                                       1, 1, 3, 1, 1, 2, 8, 3, 2, 3, 1, 2, 1, 3, 2, 2, 1, 1}; // (01)09501101530010
const std::vector<int> kDataBarLimited = {1, 3, 2, 2, 2, 3, 2, 1, 2, 1, 1, 1, 1, 2, 3, 1, 1, 2, 1, 1, 1, 1, 2, 1, 1, 2, 2,
                                          1, 1, 2, 1, 2, 1, 1, 2, 3, 2, 1, 3, 2, 2, 2, 2, 1, 1}; // (01)15012345678907
const std::vector<int> kDataBarExpanded = {1, 2, 1, 4, 2, 3, 2, 1, 2, 1, 8, 4, 1, 1, 3, 4, 2, 2, 1, 1, 1, 3, 2, 1, 1, 6, 2, 3,
                                           1, 1, 1, 1, 6, 4, 3, 1, 1, 2, 3, 6, 1, 2, 1, 1, 1, 2, 5, 1, 3, 2, 2, 3, 6, 4, 1, 1,
                                           3, 1, 2, 5, 1, 2, 1, 2, 1, 3, 4, 2, 1, 1, 2, 3, 1, 1, 8, 2, 3, 3, 2, 1, 4, 1, 1, 4,
                                           1, 1, 1}; // (01)98898765432106(3202)012345(15)991231

// stacked GS1 DataBar symbols made by zint (zxing-cpp 3.1 CreateBarcode; Expanded Stacked with 2
// pairs a row): each run of identical module rows as (height in modules, row, 1 = dark), rows and
// separator patterns
using StackedRows = std::vector<std::pair<int, std::string>>;
const StackedRows kDataBarStk = {
    {5, "01000001010000010100011111000001011110110101110010"},
    {1, "00001100101010101010101010111010100001001010100000"},
    {7, "10100011011101000101100000000111001110110111001101"}}; // (01)09501101530010
const StackedRows kDataBarStkOmni = {
    {33, "01000001010000010100011111000001011110110101110010"},
    {1, "00001110101111101010100000101010100001001010000000"},
    {1, "00000101010101010101010101010101010101010101010000"},
    {1, "00001100100010111010010101010000110001001000110000"},
    {33, "10100011011101000101100000000111001110110111001101"}}; // (01)09501101530010
const StackedRows kDataBarExpStk = {
    {34, "010010000110001101101111111100001011100001100101000110100000011000101011111100001110100111000000100101"},
    {1, "000001111001110010010000000010100100011110011010111001011111100111010100000010100001011000111111010000"},
    {1, "000001010101010101010101010101010101010101010101010101010101010101010101010101010101010101010101010000"},
    {1, "000011101000010011100001000000001011100101100001110110110111110010001001010000001010011000100000110000"},
    {34, "101000010111101100011100111111110100011010011110001001001000001101110100001111110001100111011111001010"}}; // (01)98898765432106(3202)012345(15)991231

// a stacked symbol on a label with 12-module quiet zones left and right and 6 above and below, on a
// grey carton, slight blur and noise; `edit` changes the label (in label pixels) before the blur
cv::Mat drawStacked(const StackedRows& rows, int X, std::mt19937& rng, const std::function<void(cv::Mat&)>& edit = {})
{
    int hm = 0;
    for (const auto& r : rows) hm += r.first;
    const int w = static_cast<int>(rows[0].second.size() + 24) * X, h = (hm + 12) * X;
    cv::Mat img(h + 120, w + 160, CV_32F, cv::Scalar(110.f));
    cv::Mat lab = img(cv::Rect(80, 60, w, h));
    lab.setTo(220.f);
    int y = 6;
    for (const auto& [rh, row] : rows) {
        for (size_t i = 0; i < row.size(); ++i)
            if (row[i] == '1') lab(cv::Rect((static_cast<int>(i) + 12) * X, y * X, X, rh * X)).setTo(35.f);
        y += rh;
    }
    if (edit) edit(lab);
    cv::GaussianBlur(img, img, cv::Size(), 0.7);
    std::normal_distribution<float> nz(0.f, 3.f);
    for (auto it = img.begin<float>(); it != img.end<float>(); ++it) *it += nz(rng);
    cv::Mat g8;
    img.convertTo(g8, CV_8U);
    return g8;
}

// a label of X px per module with `quiet`-module quiet zones on a grey carton, slight blur and noise;
// gain px of print gain on each bar (bars wider, spaces narrower)
cv::Mat drawLinear(const std::vector<uint8_t>& mods, int X, std::mt19937& rng, int quiet = 12, int gain = 0)
{
    const int w = static_cast<int>(mods.size() + size_t(2 * quiet)) * X, h = 30 * X;
    cv::Mat img(h + 120, w + 160, CV_32F, cv::Scalar(110.f));
    cv::Mat lab = img(cv::Rect(80, 60, w, h));
    lab.setTo(220.f);
    for (size_t i = 0; i < mods.size(); ++i)
        if (mods[i]) lab(cv::Rect(static_cast<int>(i + size_t(quiet)) * X - gain / 2, 0, X + gain, h)).setTo(35.f);
    cv::GaussianBlur(img, img, cv::Size(), 0.7);
    std::normal_distribution<float> nz(0.f, 3.f);
    for (auto it = img.begin<float>(); it != img.end<float>(); ++it) *it += nz(rng);
    cv::Mat g8;
    img.convertTo(g8, CV_8U);
    return g8;
}

// a job reading `format`, classical locator, no AI, generous time
cr_params linearJob(int format)
{
    cr_params p{};
    p.struct_size = CR_PARAMS_SIZE;
    p.format = format;
    p.expected_count = 1;
    p.time_budget_ms = 3000;
    p.locator_scale = 4;
    p.max_upscale = 2;
    p.gamma = 0.5f;
    p.locator_density = 0.12f;
    p.full_pass = 1;
    p.locator_mode = CR_LOCATE_CLASSICAL;
    p.ai_threshold = 0.4f;
    p.enhancer_mode = CR_ENHANCE_OFF;
    return p;
}

std::string onlyText(const cr::Result& r, int format)
{
    return r.codes.size() == 1 && r.codes[0].format == format ? r.codes[0].text : r.codes.empty() ? "" : "<" + r.codes[0].text + ">";
}

// ---- 2D grading of Micro QR, Aztec (quality.cpp) and PDF417 (grade_pdf417.cpp)
// Symbols drawn from their module grids: the fixed patterns of the type and seeded random data
// (the graders compare the image with the decoder's grid and the standard's fixed patterns; they do
// not decode). px pixels per module, a light margin of `quiet` modules; the corners (symbol order:
// top left, top right, bottom right, bottom left) at the symbol's outer edges.
struct Drawn {
    cv::Mat img;
    cr::Code code;
};

Drawn drawGrid(const std::vector<uint8_t>& m, int w, int h, int format, int px, int dark, int light, int quiet, float rowH = 1.f)
{
    Drawn d;
    const int hpx = static_cast<int>(std::lround(h * rowH * px));
    d.img = cv::Mat(hpx + 2 * quiet * px, (w + 2 * quiet) * px, CV_8U, cv::Scalar(light));
    for (int y = 0; y < h; ++y)
        for (int x = 0; x < w; ++x)
            if (m[static_cast<size_t>(y) * w + x])
                cv::rectangle(d.img, cv::Rect((quiet + x) * px, quiet * px + static_cast<int>(std::lround(y * rowH * px)), px,
                                              static_cast<int>(std::lround(rowH * px))), cv::Scalar(dark), -1);
    const float x0 = static_cast<float>(quiet * px), y0 = x0, x1 = x0 + static_cast<float>(w * px), y1 = y0 + static_cast<float>(hpx);
    const float xs[4] = {x0, x1, x1, x0}, ys[4] = {y0, y0, y1, y1};
    for (int k = 0; k < 4; ++k) { d.code.x[k] = xs[k]; d.code.y[k] = ys[k]; }
    d.code.format = format;
    if (format != CR_PDF417) {
        d.code.modules = m;
        d.code.modW = w;
        d.code.modH = h;
    }
    return d;
}

// turned a quarter clockwise (k = 1) or half (k = 2), the corners staying in symbol order
Drawn turned(Drawn d, int k)
{
    d.img = d.img.clone(); // the copy shares its pixels with the original until then
    for (int i = 0; i < k; ++i) {
        const float H = static_cast<float>(d.img.rows);
        cv::rotate(d.img, d.img, cv::ROTATE_90_CLOCKWISE);
        for (int c = 0; c < 4; ++c) {
            const float x = d.code.x[c], y = d.code.y[c];
            d.code.x[c] = H - y;
            d.code.y[c] = x;
        }
    }
    return d;
}

void setFinder(std::vector<uint8_t>& m, int w, int x0, int y0)
{
    for (int y = -1; y <= 7; ++y)
        for (int x = -1; x <= 7; ++x)
            if (x0 + x >= 0 && y0 + y >= 0 && x0 + x < w && y0 + y < w) {
                const int d = std::max(std::abs(x - 3), std::abs(y - 3));
                m[static_cast<size_t>(y0 + y) * w + x0 + x] = d == 4 || d == 2 ? 0 : 1;
            }
}

std::vector<uint8_t> microQrGrid(int w, std::mt19937& rng)
{
    std::vector<uint8_t> m(static_cast<size_t>(w) * w);
    for (auto& v : m) v = static_cast<uint8_t>(rng() & 1);
    setFinder(m, w, 0, 0);
    for (int i = 8; i < w; ++i) m[static_cast<size_t>(i)] = m[static_cast<size_t>(i) * w] = i % 2 == 0;
    return m;
}

// Aztec after ISO/IEC 24778: bullseye, orientation marks; full-range: the reference grid
std::vector<uint8_t> aztecGrid(int w, bool full, std::mt19937& rng)
{
    std::vector<uint8_t> m(static_cast<size_t>(w) * w);
    for (auto& v : m) v = static_cast<uint8_t>(rng() & 1);
    const int c = w / 2, s = full ? 7 : 5;
    auto set = [&](int x, int y, int v) { if (x >= 0 && y >= 0 && x < w && y < w) m[static_cast<size_t>(y) * w + x] = static_cast<uint8_t>(v); };
    if (full)
        for (int k = 0; k < w; ++k)
            for (int j = 0; j <= c; j += 16)
                for (int line : {c - j, c + j}) {
                    set(line, k, (k - c) % 2 == 0);
                    set(k, line, (k - c) % 2 == 0);
                }
    for (int y = c - s + 1; y <= c + s - 1; ++y)
        for (int x = c - s + 1; x <= c + s - 1; ++x) set(x, y, std::max(std::abs(x - c), std::abs(y - c)) % 2 == 0);
    const int marks[12][3] = {{-s, -s, 1}, {-s + 1, -s, 1}, {-s, -s + 1, 1}, {s, -s, 1}, {s, -s + 1, 1}, {s - 1, -s, 0},
                              {s, s - 1, 1}, {s, s, 0}, {s - 1, s, 0}, {-s, s, 0}, {-s + 1, s, 0}, {-s, s - 1, 0}};
    for (const auto& k : marks) set(c + k[0], c + k[1], k[2]);
    return m;
}

// PDF417 after ISO/IEC 15438: rows of start, left row indicator, data, right row indicator, stop;
// each codeword a random codeword of its row's cluster from the ISO/IEC 15438 table. The codeword
// at (badRow, badCol) (0 = the left row indicator) is a pattern of the row's cluster - eight
// elements of 1..6 modules, 17 in all - that is no codeword of the table.
std::vector<uint8_t> pdf417Grid(int rows, int cols, std::mt19937& rng, int badRow = -1, int badCol = -1)
{
    auto elements = [](uint32_t p) {
        std::array<int, 8> e{};
        int k = 0, last = 1;
        for (int b = 16; b >= 0; --b) {
            const int bit = p >> b & 1;
            if (bit != last) { ++k; last = bit; }
            ++e[static_cast<size_t>(k)];
        }
        return e;
    };
    std::array<int, 8> outside{}; // the first structure-valid pattern of cluster 3 * (badRow % 3) not in the table
    if (badRow >= 0) {
        std::set<std::array<int, 8>> table;
        for (int v = 0; v < 929; ++v) table.insert(elements(static_cast<uint32_t>(cr::pdf417Pattern(badRow % 3, v))));
        std::array<int, 8> e{};
        bool found = false;
        const std::function<void(int, int)> walk = [&](int i, int left) {
            if (found) return;
            if (i == 7) {
                e[7] = left;
                if (left >= 1 && left <= 6 && ((e[0] - e[2] + e[4] - e[6]) % 9 + 9) % 9 == 3 * (badRow % 3) && !table.count(e)) {
                    outside = e;
                    found = true;
                }
                return;
            }
            for (int v = 1; v <= 6 && v < left; ++v) { e[static_cast<size_t>(i)] = v; walk(i + 1, left - v); }
        };
        walk(0, 17);
    }
    const int W = 69 + 17 * cols;
    std::vector<uint8_t> m(static_cast<size_t>(W) * rows, 0);
    for (int r = 0; r < rows; ++r) {
        int x = 0;
        auto put = [&](const int* el, int n) {
            for (int k = 0; k < n; ++k)
                for (int j = 0; j < el[k]; ++j) m[static_cast<size_t>(r) * W + x++] = k % 2 == 0;
        };
        const int start[8] = {8, 1, 1, 1, 1, 1, 1, 3}, stop[9] = {7, 1, 1, 3, 1, 1, 1, 2, 1};
        put(start, 8);
        for (int j = 0; j < cols + 2; ++j) {
            const auto e = r == badRow && j == badCol ? outside : elements(static_cast<uint32_t>(cr::pdf417Pattern(r % 3, static_cast<int>(rng() % 929))));
            put(e.data(), 8);
        }
        put(stop, 9);
    }
    return m;
}

void testGrading2d()
{
    using namespace cr;
    std::mt19937 rng(15415);
    auto soft = [](cv::Mat img) { cv::GaussianBlur(img, img, cv::Size(0, 0), 0.7); return img; };

    // Micro QR M2 (13 x 13): clean, faded, a damaged finder, a speck in the quiet zone, UEC
    {
        const int w = 13, px = 6;
        const auto m = microQrGrid(w, rng);
        Drawn d = drawGrid(m, w, w, CR_MICRO_QR, px, 30, 220, 3);
        d.code.ecLevel = "M";
        d.code.uec = 1.f;
        const Quality clean = gradeCode(soft(d.img.clone()), d.code);
        std::printf("     Micro QR clean: SC %.0f MOD %.2f FPD %.2f GN %.2f UEC %.2f QZ %.2f -> %d\n", clean.symbolContrast, clean.modulation,
                    clean.fixedPatternDamage, clean.gridNonUniformity, clean.unusedErrorCorrection, clean.quietZoneDark, clean.overall);
        check(clean.measured && clean.overall == 4 && clean.fixedPatternDamage == 0.f && clean.gradeQZ == 4, "Micro QR: a clean M2 grades A");
        check(gradeCode(soft(turned(d, 1).img), turned(d, 1).code).overall == 4 && gradeCode(soft(turned(d, 2).img), turned(d, 2).code).overall == 4,
              "Micro QR: turned a quarter and a half it still grades A (corners in symbol order)");
        const Quality faded = gradeCode(soft(drawGrid(m, w, w, CR_MICRO_QR, px, 100, 200, 3).img), d.code);
        check(faded.gradeSC == 2 && faded.overall == 2, "Micro QR: faded to 50 % contrast grades C (" + std::to_string(faded.overall) + ")");
        // two finder modules light: 2 of the 74 fixed modules wrong (2.7 %) is B
        cv::Mat hurt = d.img.clone();
        for (const auto& [x, y] : {std::pair{2, 2}, std::pair{4, 3}})
            cv::rectangle(hurt, cv::Rect((3 + x) * px, (3 + y) * px, px, px), cv::Scalar(220), -1);
        const Quality fp = gradeCode(soft(hurt), d.code);
        check(fp.gradeFPD == 3 && fp.overall == 3, "Micro QR: two finder modules lost grade B on fixed pattern damage (" +
                                                       std::to_string(fp.fixedPatternDamage) + ")");
        cv::Mat speck = d.img.clone();
        cv::rectangle(speck, cv::Rect(3 * px + 5 * px, 2 * px, px, px), cv::Scalar(30), -1); // in the 2-module ring above the symbol
        check(gradeCode(soft(speck), d.code).gradeQZ < 4, "Micro QR: a dark speck in the quiet zone lowers its grade");
        // the decoder's UEC after ISO/IEC 15415: M2-M has 6 check codewords, 2 of them misdecode
        // protection; zxing's 0.66 (1 error of 6 / 2) is 1 - 2/4 = 0.5, B; its 0.33 (2 errors) is
        // all 4 used, F
        Code used = d.code;
        used.uec = 0.66f;
        const Quality u1 = gradeCode(soft(d.img.clone()), used);
        used.uec = 0.33f;
        const Quality u2 = gradeCode(soft(d.img.clone()), used);
        check(std::abs(u1.unusedErrorCorrection - 0.5f) < 1e-4f && u1.gradeUEC == 3 && u2.unusedErrorCorrection == 0.f && u2.gradeUEC == 0,
              "Micro QR M2-M: the decoder's UEC less the misdecode protection (0.66 -> 0.50 B, 0.33 -> 0 F)");
        // M1 has no correction to spare and the decoder's UEC says nothing: from its own check
        // codewords; a grid whose format cannot be read is not measured, never A
        Drawn m1 = drawGrid(microQrGrid(11, rng), 11, 11, CR_MICRO_QR, px, 30, 220, 3);
        m1.code.uec = 0.f;
        const Quality q1 = gradeCode(soft(m1.img), m1.code);
        check(q1.measured && q1.gradeUEC <= 0, "Micro QR: a random M1 grid's UEC is not A (" + std::to_string(q1.gradeUEC) + ")");
        // a timing module printed 0.6 module along: grid non-uniformity C or worse, not hidden
        for (const float shift : {0.6f, -0.6f}) {
            cv::Mat moved = d.img.clone();
            cv::rectangle(moved, cv::Rect((3 + 10) * px, 3 * px, px, px), cv::Scalar(220), -1);
            cv::rectangle(moved, cv::Rect((3 + 10) * px + static_cast<int>(std::lround(shift * px)), 3 * px, px, px), cv::Scalar(30), -1);
            const Quality g = gradeCode(soft(moved), d.code);
            check(g.gridNonUniformity >= 0.5f && g.gradeGN <= 2, "Micro QR: a timing module 0.6 module off grades C or worse on grid non-uniformity (" +
                                                                       std::to_string(g.gridNonUniformity) + ")");
        }
    }

    // Aztec compact (19 x 19) and full-range (23 x 23, the same size range as compact 23): the
    // bullseye, orientation marks and reference grid as fixed patterns; no quiet zone graded
    for (const bool full : {false, true}) {
        const int w = full ? 23 : 19, px = 5;
        const auto m = aztecGrid(w, full, rng);
        Drawn d = drawGrid(m, w, w, CR_AZTEC, px, 30, 220, 2);
        d.code.ecLevel = "33%";
        d.code.uec = 0.9f;
        const std::string kind = full ? "Aztec full-range" : "Aztec compact";
        const Quality clean = gradeCode(soft(d.img.clone()), d.code);
        std::printf("     %s clean: SC %.0f MOD %.2f FPD %.2f GN %.2f UEC %.2f -> %d\n", kind.c_str(), clean.symbolContrast, clean.modulation,
                    clean.fixedPatternDamage, clean.gridNonUniformity, clean.unusedErrorCorrection, clean.overall);
        check(clean.measured && clean.overall == 4 && clean.fixedPatternDamage == 0.f && clean.gradeQZ == -1,
              kind + ": clean grades A, its own fixed patterns all found, no quiet zone grade");
        check(gradeCode(soft(turned(d, 1).img), turned(d, 1).code).fixedPatternDamage == 0.f, kind + ": turned a quarter, fixed patterns still found");
        // the bullseye's centre and its inner ring hit by a light blot (3 x 3 modules round the centre)
        cv::Mat blot = d.img.clone();
        cv::rectangle(blot, cv::Rect((2 + w / 2 - 1) * px, (2 + w / 2 - 1) * px, 3 * px, 3 * px), cv::Scalar(220), -1);
        const Quality hit = gradeCode(soft(blot), d.code);
        check(hit.gradeFPD < 4 && hit.overall < 4, kind + ": a blot on the bullseye grades lower on fixed pattern damage (" +
                                                        std::to_string(hit.gradeFPD) + ")");
        const Quality faded = gradeCode(soft(drawGrid(m, w, w, CR_AZTEC, px, 100, 200, 2).img), d.code);
        check(faded.overall == 2, kind + ": faded to 50 % contrast grades C");
    }

    // PDF417: 6 rows of 3 data columns, rows 3 X tall; 8 error correction codewords of the 18
    {
        const int rows = 6, cols = 3, px = 4, W = 69 + 17 * cols;
        const auto m = pdf417Grid(rows, cols, rng);
        Drawn d = drawGrid(m, W, rows, CR_PDF417, px, 30, 220, 4, 3.f);
        d.code.ecLevel = "44%";
        d.code.uec = 1.f;
        const Quality clean = gradePdf417(soft(d.img.clone()), d.code);
        std::printf("     PDF417 clean: %d scans (%d decoded) SC %.0f ECmin %.0f MOD %.2f defects %.2f decodability %.2f CY %.2f UEC %.2f X %.1f -> %d\n",
                    clean.scans, clean.decodedScans, clean.symbolContrast, clean.edgeContrastMin, clean.modulation, clean.defects,
                    clean.decodability, clean.codewordYield, clean.unusedErrorCorrection, clean.moduleSizePx, clean.overall);
        check(clean.measured && clean.scans == rows && clean.decodedScans == rows && clean.codewordYield == 1.f && clean.overall == 4,
              "PDF417: a clean symbol grades A, every row found and every codeword valid");
        check(gradePdf417(soft(turned(d, 1).img), turned(d, 1).code).overall == 4, "PDF417: turned a quarter it still grades A");
        // the corners as the image has them (start pattern on the right): graded the other way round
        Drawn back = turned(d, 2);
        const Drawn flat = d;
        for (int k = 0; k < 4; ++k) { back.code.x[k] = flat.code.x[k]; back.code.y[k] = flat.code.y[k]; }
        check(gradePdf417(soft(back.img), back.code).overall == 4, "PDF417: upside down with image-order corners it still grades A");
        // three data codewords blotted out: three erasures against the 6 correctable (8 less 2)
        // leave notional UEC 0.5, B; codeword yield 27 of 30 stays A
        cv::Mat blot = d.img.clone();
        for (const auto& [r, j] : {std::pair{1, 1}, std::pair{3, 2}, std::pair{4, 3}})
            cv::rectangle(blot, cv::Rect((4 + 17 + 17 * j + 2) * px, (4 + 3 * r) * px, 13 * px, 3 * px), cv::Scalar(220), -1);
        const Quality hit = gradePdf417(soft(blot), d.code);
        std::printf("     PDF417 3 codewords blotted: CY %.2f (%d) MOD %d decodability %d -> %d\n", hit.codewordYield, hit.gradeCY, hit.gradeMOD,
                    hit.gradeDecodability, hit.overall);
        check(hit.measured && std::abs(hit.codewordYield - 27.f / 30.f) < 0.01f && hit.gradeCY == 4 && hit.gradeMOD == 3 && hit.overall == 3,
              "PDF417: three codewords lost grade B (erasures against the error correction)");
        // a whole row lost (5 of its codewords): codeword yield 25/30 = 83 % still A; 3 data
        // erasures as above
        cv::Mat row = d.img.clone();
        cv::rectangle(row, cv::Rect((4 + 17) * px, (4 + 3 * 2) * px, 17 * (cols + 2) * px, 3 * px), cv::Scalar(220), -1);
        const Quality lost = gradePdf417(soft(row), d.code);
        check(lost.measured && lost.scans == rows && lost.overall <= 3, "PDF417: a row lost is still found as a row and grades lower (" +
                                                                            std::to_string(lost.overall) + ")");
        const Quality faded = gradePdf417(soft(drawGrid(m, W, rows, CR_PDF417, px, 80, 200, 4, 3.f).img), d.code);
        check(faded.measured && faded.gradeSC == 2 && faded.overall == 2, "PDF417: faded to 47 % contrast grades C (" + std::to_string(faded.overall) + ")");
        Code noUec = d.code;
        noUec.uec = -1.f;
        const Quality nu = gradePdf417(soft(d.img.clone()), noUec);
        check(nu.gradeUEC == -1 && nu.unusedErrorCorrection < 0, "PDF417: without the decoder's UEC it is not graded (not made up)");
        check(!gradePdf417(cv::Mat(200, 400, CV_8U, cv::Scalar(200)), d.code).measured, "PDF417: no symbol at the corners, no grade");
        // a data codeword with eight elements of 1..6 modules and the row's cluster, but no codeword
        // of the ISO/IEC 15438 table: not valid (codeword yield 29 of 30)
        std::mt19937 rngOdd(15438);
        const Quality odd = gradePdf417(soft(drawGrid(pdf417Grid(rows, cols, rngOdd, 2, 2), W, rows, CR_PDF417, px, 30, 220, 4, 3.f).img), d.code);
        check(odd.measured && std::abs(odd.codewordYield - 29.f / 30.f) < 0.01f,
              "PDF417: a pattern of the right cluster that is no codeword of the table is not valid (CY " + std::to_string(odd.codewordYield) + ")");
        // the image's edge 1 X from the start or stop pattern: the 2 X quiet zone runs off the image, F
        const auto cut = [&](int x0, int x1) {
            Code cc = d.code;
            for (int k = 0; k < 4; ++k) cc.x[k] -= static_cast<float>(x0);
            return gradePdf417(soft(d.img(cv::Rect(x0, 0, x1 - x0, d.img.rows)).clone()), cc);
        };
        const Quality cutL = cut(3 * px, d.img.cols), cutR = cut(0, d.img.cols - 3 * px), cutBoth = cut(px, d.img.cols - px);
        std::printf("     PDF417 quiet zone at the image's edge: left cut QZ %d -> %d, right cut QZ %d -> %d, 3 X kept QZ %d -> %d\n", cutL.gradeQZ,
                    cutL.overall, cutR.gradeQZ, cutR.overall, cutBoth.gradeQZ, cutBoth.overall);
        check(cutL.measured && cutL.gradeQZ == 0 && cutL.overall == 0 && cutR.measured && cutR.gradeQZ == 0 && cutR.overall == 0,
              "PDF417: a quiet zone the image's left or right edge cuts short grades F");
        check(cutBoth.measured && cutBoth.gradeQZ == clean.gradeQZ && cutBoth.overall == clean.overall,
              "PDF417: its quiet zone inside the image up to the edge grades as before");
    }
}

// ---- 1D grading of every type (decodability.cpp, grade1d.cpp)
// element edges of a module pattern (1 = bar) drawn at X samples per module, from the first bar;
// growth widens every bar by that many modules (print gain), shrinking the spaces
std::vector<float> edgesOfModules(const std::vector<uint8_t>& m, float X, float growth = 0.f)
{
    std::vector<float> e;
    size_t i = 0;
    while (i < m.size() && !m[i]) ++i;
    for (; i < m.size(); ++i)
        if (i == 0 || m[i] != m[i - 1]) e.push_back((float(i) + (m[i] ? -growth / 2 : growth / 2)) * X);
    e.push_back((float(m.size()) + growth / 2) * X); // the last bar's trailing edge
    return e;
}

void testGrading()
{
    using namespace cr;
    const auto c39 = code39Modules("VS-39AV"), itf = itfModules("15400141288763"), ean8 = ean8Modules("96385074");
    const float d39 = decodability1d(edgesOfModules(c39, 3), CR_CODE39), dItf = decodability1d(edgesOfModules(itf, 3), CR_ITF),
                dEan = decodability1d(edgesOfModules(ean8, 3), CR_EAN8);
    std::printf("     decodability of clean print: Code 39 %.2f, ITF %.2f, EAN-8 %.2f\n", d39, dItf, dEan);
    check(d39 > 0.95f && dItf > 0.95f && dEan > 0.95f, "decodability of clean Code 39, ITF and EAN-8 is about 1");
    // ink spread widens every bar: two-width codes lose decodability; EAN's edge-to-similar-edge
    // measures do not change (the reason the standard measures them so); one edge out of place does
    // (an EAN-8 without the digits 1, 2, 7 and 8, whose bar-width check ink spread does change)
    const float g39 = decodability1d(edgesOfModules(c39, 3, 0.6f), CR_CODE39),
                gEan = decodability1d(edgesOfModules(ean8Modules("96305064"), 3, 0.35f), CR_EAN8);
    std::vector<float> moved = edgesOfModules(ean8, 3);
    moved[10] += 0.35f * 3;
    const float mEan = decodability1d(moved, CR_EAN8);
    std::printf("     with ink spread: Code 39 (0.6 module) %.2f, EAN-8 (0.35 module) %.2f; EAN-8 with one edge 0.35 module out %.2f\n", g39, gEan, mEan);
    check(g39 < d39 - 0.2f && gEan > 0.95f && mEan < 0.4f, "ink spread lowers two-width decodability; a misplaced edge lowers EAN's");
    // one Code 39 narrow element drawn on the decision threshold (2 X, half way to 3 X): decodability ~0
    {
        std::vector<float> e = edgesOfModules(c39, 3);
        for (size_t k = 11; k < e.size(); ++k) e[k] += 3.f; // element 10 (the 2nd character's first bar) widened by 1 X
        const float onT = decodability1d(e, CR_CODE39);
        std::printf("     Code 39 with one narrow bar on the threshold: %.2f\n", onT);
        check(onT < 0.25f, "an element on the narrow/wide threshold gives decodability F (under 0.25)");
    }
    // Codabar A1A: the wide count of each character from the text (A: 3 wide, 1: 2 wide)
    {
        const char* pat[3] = {"nnwwnwn", "nnnnwwn", "nnwwnwn"};
        std::vector<uint8_t> m;
        for (int c = 0; c < 3; ++c) {
            for (int k = 0; k < 7; ++k) m.insert(m.end(), pat[c][k] == 'w' ? 3 : 1, uint8_t(k % 2 == 0));
            if (c < 2) m.push_back(0);
        }
        const float clean = decodability1d(edgesOfModules(m, 3), CR_CODABAR, "1"), withEnds = decodability1d(edgesOfModules(m, 3), CR_CODABAR, "A1A");
        std::vector<float> e = edgesOfModules(m, 3);
        for (size_t k = 10; k < e.size(); ++k) e[k] += 3.f; // the 1's first bar (narrow) widened to the threshold
        const float onT = decodability1d(e, CR_CODABAR, "1");
        std::printf("     Codabar: clean %.2f (%.2f with its ends in the text), a narrow bar on the threshold %.2f\n", clean, withEnds, onT);
        check(clean > 0.95f && withEnds > 0.95f && onT < 0.25f && decodability1d(edgesOfModules(m, 3), CR_CODABAR, "12") < 0,
              "Codabar decodability from the text's wide counts; a text that does not fit is not measured");
    }
    check(decodability1d(edgesOfModules(c39, 3), CR_EAN8) < 0 && decodability1d(edgesOfModules(ean8, 3), CR_ITF) < 0,
          "an element count that does not fit the type is not measured");
    check(std::abs(moduleFromEdges(edgesOfModules(c39, 3)) - 3.f) < 0.01f && std::abs(moduleFromEdges(edgesOfModules(ean8, 2.5f)) - 2.5f) < 0.01f &&
          std::abs(moduleFromEdges(edgesOfModules(c39, 8, 0.25f)) - 8.f) < 0.05f,
          "X-dimension from the narrow elements, print gain not changing it");

    // whole symbols drawn and graded: Code 39, ITF-14 and EAN-8 at 3 px per module
    std::mt19937 rng(15416);
    struct Case { const char* name; std::vector<uint8_t> mods; std::string text; int format; };
    for (const Case& k : {Case{"Code 39", c39, "VS-39AV", CR_CODE39}, Case{"ITF-14", itf, "15400141288763", CR_ITF},
                          Case{"EAN-8", ean8, "96385074", CR_EAN8}}) {
        const cv::Mat img = drawLinear(k.mods, 3, rng);
        Code c;
        c.text = k.text;
        c.format = k.format;
        const float w = float((k.mods.size() + 24) * 3), h = 90.f;
        const float xs[4] = {80, 80 + w, 80 + w, 80}, ys[4] = {60, 60, 60 + h, 60 + h};
        for (int i = 0; i < 4; ++i) { c.x[i] = xs[i]; c.y[i] = ys[i]; }
        const Quality q = gradeLinear(img, c);
        std::printf("     %s: overall %d, SC %.0f, MOD %.2f, decodability %.2f, decoded %d of %d scans, X %.2f px\n", k.name, q.overall,
                    q.symbolContrast, q.modulation, q.decodability, q.decodedScans, q.scans, q.moduleSizePx);
        check(q.measured && q.linear && q.overall >= 3 && q.decodability > 0.6f && q.decodedScans == 10 && std::abs(q.moduleSizePx - 3.f) < 0.4f,
              std::string(k.name) + " graded A or B with its decodability measured");
        // the same symbol faded to a third of its contrast: a lower grade
        cv::Mat faded;
        img.convertTo(faded, -1, 0.33, 140);
        const Quality qf = gradeLinear(faded, c);
        check(qf.measured && qf.overall < q.overall, std::string(k.name) + " faded grades lower (" + std::to_string(qf.overall) + ")");
    }
    // the band of bars found at any X dimension, from 3 to 16 px per module: wide bars (4 X of
    // ITF-14 at 16 px is 64 px) have no gradient inside them, so the band is looked for at a scale
    // where the bars are a few px wide; graded the same as at 3 px
    for (const Case& k : {Case{"Code 39", c39, "VS-39AV", CR_CODE39}, Case{"ITF-14", itf, "15400141288763", CR_ITF}}) {
        std::mt19937 rngX(15430); // its own, so the images of the tests after it do not change
        std::string seen;
        for (const int X : {3, 4, 6, 8, 10, 12, 16}) {
            const cv::Mat img = drawLinear(k.mods, X, rngX);
            const float w = float((k.mods.size() + 24) * size_t(X)), h = 30.f * float(X);
            const auto bands = findLinearBands(img, 3);
            const bool bandOk = !bands.empty() && std::abs(bands[0].dir.y) < 0.05f &&
                                bands[0].length > 0.95f * float(k.mods.size() * size_t(X)) && bands[0].length < 1.25f * float(k.mods.size() * size_t(X)) &&
                                std::abs(bands[0].centre.x - (80 + w / 2)) < 0.1f * float(k.mods.size() * size_t(X)) && bands[0].height > 0.5f * h;
            Code c;
            c.text = k.text;
            c.format = k.format;
            const float xs[4] = {80, 80 + w, 80 + w, 80}, ys[4] = {60, 60, 60 + h, 60 + h};
            for (int i = 0; i < 4; ++i) { c.x[i] = xs[i]; c.y[i] = ys[i]; }
            const Quality q = gradeLinear(img, c);
            seen += " " + std::to_string(X) + ":" + (bands.empty() ? std::string("no band") : std::to_string(int(bands[0].length)) + " of " + std::to_string(k.mods.size() * size_t(X)) + " px") + "/" +
                    std::to_string(q.overall) + "/X" + std::to_string(q.moduleSizePx).substr(0, 5);
            check(bandOk, std::string(k.name) + " at " + std::to_string(X) + " px per module: one band along the bars, the symbol's length");
            check(q.measured && q.overall >= 3 && q.decodedScans == 10 && std::abs(q.moduleSizePx - float(X)) < 0.15f * float(X),
                  std::string(k.name) + " at " + std::to_string(X) + " px per module graded A or B, X measured");
        }
        std::printf("     %s by px per module (band length / grade / X):%s\n", k.name, seen.c_str());
    }
    // a blank label (no bars) at any scale: no band, not graded (never a made-up pass)
    {
        cv::Mat blank(600, 1600, CV_8U, cv::Scalar(110));
        blank(cv::Rect(80, 60, 1440, 480)).setTo(220);
        Code c;
        c.text = "VS-39AV";
        c.format = CR_CODE39;
        const float xs[4] = {80, 1520, 1520, 80}, ys[4] = {60, 60, 540, 540};
        for (int i = 0; i < 4; ++i) { c.x[i] = xs[i]; c.y[i] = ys[i]; }
        const Quality q = gradeLinear(blank, c);
        check(!q.measured || q.overall == 0, "a blank label where a 1D code was: not graded or F");
    }
    // a symbol on a white label on a darker carton, the scan lines running onto the carton: the
    // carton's edge is not taken for a bar
    {
        cv::Mat onCarton = drawLinear(itf, 3, rng);
        Code c;
        c.text = "15400141288763";
        c.format = CR_ITF;
        const float w = float((itf.size() + 24) * 3);
        const float xs[4] = {80, 80 + w, 80 + w, 80}, ys[4] = {60, 60, 150, 150};
        for (int i = 0; i < 4; ++i) { c.x[i] = xs[i]; c.y[i] = ys[i]; }
        const Quality q = gradeLinear(onCarton, c);
        check(q.overall >= 3 && q.modulation > 0.5f, "a label on a darker carton grades on its own bars (MOD " + std::to_string(q.modulation) + ")");
    }
    // the image's edge cuts a quiet zone short: measured only inside the image (the edge pixels are not
    // repeated into a quiet zone), so 5 X of the 10 X needed grades F; cut at the label's edge (12 X
    // left in the image) the grade does not change
    {
        const cv::Mat img = drawLinear(c39, 3, rng);
        Code c;
        c.text = "VS-39AV";
        c.format = CR_CODE39;
        const int w = static_cast<int>(c39.size() + 24) * 3, bar0 = 80 + 12 * 3, bar1 = bar0 + static_cast<int>(c39.size()) * 3;
        const float xs[4] = {80, 80.f + w, 80.f + w, 80}, ys[4] = {60, 60, 150, 150};
        const auto cut = [&](int x0, int x1) {
            Code cc = c;
            for (int i = 0; i < 4; ++i) { cc.x[i] = xs[i] - static_cast<float>(x0); cc.y[i] = ys[i]; }
            return gradeLinear(img(cv::Rect(x0, 0, x1 - x0, img.rows)).clone(), cc);
        };
        const Quality full = gradeLinear(img, [&] { Code cc = c; for (int i = 0; i < 4; ++i) { cc.x[i] = xs[i]; cc.y[i] = ys[i]; } return cc; }());
        const Quality atLabel = cut(80, 80 + w), left = cut(bar0 - 5 * 3, img.cols), right = cut(0, bar1 + 5 * 3);
        std::printf("     quiet zone at the image's edge: full %d (QZ %d), cut at the label %d (QZ %d), 5 X left %d (QZ %d), 5 X right %d (QZ %d)\n",
                    full.overall, full.gradeQZ, atLabel.overall, atLabel.gradeQZ, left.overall, left.gradeQZ, right.overall, right.gradeQZ);
        check(full.gradeQZ == 4 && atLabel.measured && atLabel.gradeQZ == 4 && atLabel.overall == full.overall,
              "a full quiet zone up to the image's edge grades as before");
        check(left.measured && left.gradeQZ == 0 && left.overall == 0 && right.measured && right.gradeQZ == 0 && right.overall == 0,
              "a quiet zone the image's left or right edge cuts short grades F");
    }
    // quiet zones: a Code 39 with 9 X fails even with print gain; an EAN-8 with its own 7 X passes
    {
        const auto at = [&](const std::vector<uint8_t>& mods, int quiet, int gain, const std::string& text, int format) {
            const cv::Mat img = drawLinear(mods, 8, rng, quiet, gain);
            Code c;
            c.text = text;
            c.format = format;
            const float w = float((mods.size() + size_t(2 * quiet)) * 8);
            const float xs[4] = {80, 80 + w, 80 + w, 80}, ys[4] = {60, 60, 300, 300};
            for (int i = 0; i < 4; ++i) { c.x[i] = xs[i]; c.y[i] = ys[i]; }
            return gradeLinear(img, c);
        };
        const Quality short39 = at(c39, 9, 1, "VS-39AV", CR_CODE39), ok39 = at(c39, 11, 1, "VS-39AV", CR_CODE39), ean7 = at(ean8, 7, 0, "96385074", CR_EAN8);
        std::printf("     quiet zones: Code 39 9 X with gain: %d (X %.2f); 11 X: %d; EAN-8 7 X: %d\n", short39.overall, short39.moduleSizePx, ok39.overall, ean7.overall);
        check(short39.overall == 0 && ok39.overall >= 3 && ean7.overall >= 3, "quiet zone per type, measured in a true X under print gain");
    }
    // EAN / UPC characters 1, 2, 7 and 8 (ISO/IEC 15420): their pairs share the edge-to-similar-edge
    // distances; the two bars' total tells them apart, so ink spread (g modules on each bar, 2 g on
    // the total) lowers decodability to 1 - 2 g where it moves the total towards the threshold. Exact:
    // the drawn edges carry no noise
    {
        const auto dec = [](const std::vector<uint8_t>& m, float g, int format, const std::string& text) {
            return decodability1d(edgesOfModules(m, 4, g), format, text);
        };
        const auto a = ean8Modules("12783456"), c = ean8Modules("34561278"), b = ean13Modules("5072007345690");
        const float aClean = dec(a, 0, CR_EAN8, "12783456"), aGain = dec(a, 0.3f, CR_EAN8, "12783456"), aLoss = dec(a, -0.3f, CR_EAN8, "12783456"),
                    aWrong = dec(a, 0.6f, CR_EAN8, "12783456"), aWrongNoText = dec(a, 0.6f, CR_EAN8, ""), cGain = dec(c, 0.3f, CR_EAN8, "34561278"),
                    bGain = dec(b, 0.3f, CR_EAN13, "5072007345690"), bLoss = dec(b, -0.3f, CR_EAN13, "5072007345690"),
                    bWrong = dec(b, -0.6f, CR_EAN13, "5072007345690"), noPair = dec(ean8Modules("96305064"), 0.3f, CR_EAN8, "96305064");
        std::printf("     EAN bar-width check: set A clean %.3f, +0.3 %.3f, -0.3 %.3f, +0.6 %.3f (%.3f without the text); set C +0.3 %.3f; "
                    "set B +0.3 %.3f, -0.3 %.3f, -0.6 %.3f; no 1/2/7/8 +0.3 %.3f\n",
                    aClean, aGain, aLoss, aWrong, aWrongNoText, cGain, bGain, bLoss, bWrong, noPair);
        const auto near = [](float v, float want) { return std::abs(v - want) < 0.01f; };
        check(near(aClean, 1) && near(aGain, 0.4f) && near(aLoss, 0.4f) && near(cGain, 0.4f) && near(bGain, 0.4f) && near(bLoss, 0.4f) && near(noPair, 1),
              "EAN characters 1, 2, 7, 8: bar total against its threshold in sets A, B and C (0.3 module spread: 0.40, grade C)");
        check(near(aWrong, 0) && near(aWrongNoText, 0.2f) && near(bWrong, 0),
              "EAN: a bar total past the threshold reads as the pair's other digit: 0 when the text names the printed digit");
        // UPC-E: sets A and B, its 6 characters between the number system and the check digit
        std::vector<uint8_t> upce = {1, 0, 1};
        for (int i = 1; i <= 6; ++i) {
            const auto ch = ean13Modules(std::string("5") + "072007" + "345690"); // reuse its A / B characters
            upce.insert(upce.end(), ch.begin() + 3 + 7 * (i - 1), ch.begin() + 3 + 7 * i);
        }
        upce.insert(upce.end(), {0, 1, 0, 1, 0, 1});
        check(near(decodability1d(edgesOfModules(upce, 4, 0.3f), CR_UPCE, "00720070"), 0.4f), "UPC-E: the bar-width check on its characters too");
    }

    // GS1 DataBar (ISO/IEC 24724): each data character and finder by its edge-to-similar-edge
    // distances; the variant from the scan's own decode
    {
        const auto edges = [](const std::vector<int>& runs, float g = 0.f) { return edgesOfModules(runsModules(runs), 4, g); };
        const auto near = [](float v, float want) { return std::abs(v - want) < 0.01f; };
        std::vector<float> omniChar = edges(kDataBarOmni), omniFinder = edges(kDataBarOmni), ltdCheck = edges(kDataBarLimited),
                           expLast = edges(kDataBarExpanded);
        omniChar[3] += 0.3f * 4;      // inside the first character
        omniFinder[11] += 0.3f * 4;   // inside the left finder
        ltdCheck[20] -= 0.2f * 4;     // inside the check character
        expLast[expLast.size() - 6] += 0.35f * 4; // inside the last character
        const float oClean = decodabilityDataBar(edges(kDataBarOmni), DataBarKind::Omni), oGain = decodabilityDataBar(edges(kDataBarOmni, 0.3f), DataBarKind::Omni),
                    oChar = decodabilityDataBar(omniChar, DataBarKind::Omni), oFinder = decodabilityDataBar(omniFinder, DataBarKind::Omni),
                    lClean = decodabilityDataBar(edges(kDataBarLimited), DataBarKind::Limited), lCheck = decodabilityDataBar(ltdCheck, DataBarKind::Limited),
                    xClean = decodabilityDataBar(edges(kDataBarExpanded), DataBarKind::Expanded), xLast = decodabilityDataBar(expLast, DataBarKind::Expanded);
        std::printf("     DataBar: Omni clean %.3f, 0.3 ink spread %.3f, a character edge 0.3 out %.3f, a finder edge 0.3 out %.3f; "
                    "Limited clean %.3f, check character edge 0.2 out %.3f; Expanded clean %.3f, last character edge 0.35 out %.3f\n",
                    oClean, oGain, oChar, oFinder, lClean, lCheck, xClean, xLast);
        check(edges(kDataBarOmni).size() == 46 && edges(kDataBarLimited).size() == 46 && edges(kDataBarExpanded).size() == 88,
              "DataBar test symbols: 45, 45 and 87 elements");
        check(near(oClean, 1) && near(oGain, 1) && near(lClean, 1) && near(xClean, 1), "clean DataBar decodability 1, ink spread not changing it");
        check(near(oChar, 0.4f) && near(oFinder, 0.4f) && near(lCheck, 0.6f) && near(xLast, 0.3f),
              "DataBar: an edge out of place by d modules leaves 1 - 2 d, in characters and finders alike");
        const auto omniEdges = edges(kDataBarOmni);   // one vector: begin and end of two temporaries is undefined
        check(decodabilityDataBar(edges(kDataBarExpanded), DataBarKind::Omni) < 0 && decodabilityDataBar(omniEdges, DataBarKind::Limited) >= 0 &&
              decodabilityDataBar(std::vector<float>(omniEdges.begin(), omniEdges.end() - 1), DataBarKind::Omni) < 0,
              "DataBar: an element count that does not fit the variant is not measured");

        // drawn and graded whole: each variant read by every scan, graded A or B; faded lower
        struct DbCase { const char* name; std::vector<int> runs; std::string text; };
        for (const DbCase& k : {DbCase{"DataBar Omni", kDataBarOmni, "0109501101530010"}, DbCase{"DataBar Limited", kDataBarLimited, "0115012345678907"},
                                DbCase{"DataBar Expanded", kDataBarExpanded, "0198898765432106320201234515991231"}}) {
            const auto mods = runsModules(k.runs);
            const cv::Mat img = drawLinear(mods, 3, rng);
            Code c;
            c.text = k.text;
            c.format = CR_DATABAR;
            const float w = float((mods.size() + 24) * 3), h = 90.f;
            const float xs[4] = {80, 80 + w, 80 + w, 80}, ys[4] = {60, 60, 60 + h, 60 + h};
            for (int i = 0; i < 4; ++i) { c.x[i] = xs[i]; c.y[i] = ys[i]; }
            const Quality q = gradeLinear(img, c);
            std::printf("     %s: overall %d, SC %.0f, MOD %.2f, decodability %.2f, decoded %d of %d scans, X %.2f px\n", k.name, q.overall,
                        q.symbolContrast, q.modulation, q.decodability, q.decodedScans, q.scans, q.moduleSizePx);
            check(q.measured && q.overall >= 3 && q.decodability > 0.6f && q.decodedScans == 10, std::string(k.name) + " graded A or B with its decodability measured");
            cv::Mat faded;
            img.convertTo(faded, -1, 0.33, 140);
            const Quality qf = gradeLinear(faded, c);
            check(qf.measured && qf.overall < q.overall, std::string(k.name) + " faded grades lower (" + std::to_string(qf.overall) + ")");
        }
        // a row that reads as no single-row variant (a stacked symbol's top row: guard, character,
        // finder, character, guard) is not graded: no made-up grade
        {
            std::vector<int> top(kDataBarOmni.begin(), kDataBarOmni.begin() + 22);
            top.insert(top.end(), {1, 1});
            const auto mods = runsModules(top);
            Code c;
            c.text = "0109501101530010";
            c.format = CR_DATABAR;
            const float w = float((mods.size() + 24) * 3);
            const float xs[4] = {80, 80 + w, 80 + w, 80}, ys[4] = {60, 60, 150, 150};
            for (int i = 0; i < 4; ++i) { c.x[i] = xs[i]; c.y[i] = ys[i]; }
            check(!gradeLinear(drawLinear(mods, 3, rng), c).measured, "a lone stacked DataBar row (no second row) is not graded");
        }
    }
    // stacked GS1 DataBar (ISO/IEC 24724): each row by its own structure, read either way
    {
        const auto rowEdges = [](const std::string& row) {
            std::vector<uint8_t> m;
            for (char ch : row) m.push_back(ch == '1');
            while (!m.empty() && !m.back()) m.pop_back();
            return edgesOfModules(m, 4);
        };
        const auto top = rowEdges(kDataBarStkOmni.front().second), bottom = rowEdges(kDataBarStkOmni.back().second);
        std::vector<float> topRev;
        for (size_t i = top.size(); i-- > 0;) topRev.push_back(top.front() + top.back() - top[i]);
        const float dTop = decodabilityDataBar(top, DataBarKind::StackedTop), dBottom = decodabilityDataBar(bottom, DataBarKind::StackedBottom),
                    dTopRev = decodabilityDataBar(topRev, DataBarKind::StackedTop, true), dTopWrongWay = decodabilityDataBar(topRev, DataBarKind::StackedTop);
        float dExpRows = 1.f;
        for (const auto& r : {kDataBarExpStk.front().second, kDataBarExpStk.back().second})
            dExpRows = std::min(dExpRows, std::max(decodabilityDataBar(rowEdges(r), DataBarKind::Expanded), decodabilityDataBar(rowEdges(r), DataBarKind::Expanded, true)));
        std::printf("     DataBar stacked rows: top %.3f (%zu edges), bottom %.3f (%zu edges), top upside down %.3f (read the wrong way %.3f), Expanded Stacked rows %.3f\n",
                    dTop, top.size(), dBottom, bottom.size(), dTopRev, dTopWrongWay, dExpRows);
        check(top.size() == 24 && bottom.size() == 26 && std::abs(dTop - 1) < 0.01f && std::abs(dBottom - 1) < 0.01f && std::abs(dTopRev - 1) < 0.01f &&
                  std::abs(dExpRows - 1) < 0.01f, "stacked DataBar rows: clean decodability 1, upside down too");
        check(dTopWrongWay < 0.5f, "a stacked row read the wrong way round measures low (the orientation is chosen per row)");

        const auto codeOn = [](const cv::Mat& img, const std::string& text) {
            Code c;
            c.text = text;
            c.format = CR_DATABAR;
            const float xs[4] = {80, float(img.cols - 80), float(img.cols - 80), 80}, ys[4] = {60, 60, float(img.rows - 60), float(img.rows - 60)};
            for (int i = 0; i < 4; ++i) { c.x[i] = xs[i]; c.y[i] = ys[i]; }
            return c;
        };
        const auto rowGrade = [](const Quality& q, int row) {
            float s = 0;
            for (int l = 0; l < 10; ++l) s += float(q.scanLines[size_t(row * 10 + l)].grade);
            s /= 10;
            return s >= 3.5f ? 4 : s >= 2.5f ? 3 : s >= 1.5f ? 2 : s >= 0.5f ? 1 : 0;
        };
        struct StkCase { const char* name; const StackedRows* rows; std::string text; int X; };
        for (const StkCase& k : {StkCase{"DataBar Stacked", &kDataBarStk, "0109501101530010", 4}, StkCase{"DataBar Stacked Omni", &kDataBarStkOmni, "0109501101530010", 3},
                                 StkCase{"DataBar Expanded Stacked", &kDataBarExpStk, "0198898765432106320201234515991231", 3}}) {
            const cv::Mat img = drawStacked(*k.rows, k.X, rng);
            const Quality q = gradeLinear(img, codeOn(img, k.text));
            std::printf("     %s: measured %d, overall %d, %d scans, %d decoded, decodability %.2f, X %.2f px\n", k.name, int(q.measured), q.overall, q.scans,
                        q.decodedScans, q.decodability, q.moduleSizePx);
            check(q.measured && q.overall >= 3 && q.scans == 20 && q.decodedScans == 20 && q.decodability > 0.6f,
                  std::string(k.name) + ": two rows of ten scans, each decoded with the other row, graded A or B");
            cv::Mat faded;
            img.convertTo(faded, -1, 0.33, 140);
            const Quality qf = gradeLinear(faded, codeOn(faded, k.text));
            check(qf.measured && qf.overall < q.overall, std::string(k.name) + " faded grades lower (" + std::to_string(qf.overall) + ")");
        }
        // stacked symbols at large X dimensions (the band of bars in pieces at half size, or only the
        // label's and the rows' edges as bands there): the whole symbol found, both rows graded as at 3 px
        for (const StkCase& k : {StkCase{"DataBar Stacked", &kDataBarStk, "0109501101530010", 0}, StkCase{"DataBar Stacked Omni", &kDataBarStkOmni, "0109501101530010", 0},
                                 StkCase{"DataBar Expanded Stacked", &kDataBarExpStk, "0198898765432106320201234515991231", 0}}) {
            std::mt19937 rngX(24724); // its own, so the images of the tests after it do not change
            std::string seen;
            for (const int X : {6, 8, 10, 12, 16}) {
                const cv::Mat img = drawStacked(*k.rows, X, rngX);
                const Quality q = gradeLinear(img, codeOn(img, k.text));
                seen += " " + std::to_string(X) + ":" + std::to_string(int(q.measured)) + "/" + std::to_string(q.overall) + "/" + std::to_string(q.decodedScans) + "of" +
                        std::to_string(q.scans) + "/X" + std::to_string(q.moduleSizePx).substr(0, 5);
                check(q.measured && q.overall >= 3 && q.scans == 20 && q.decodedScans == 20 && std::abs(q.moduleSizePx - float(X)) < 0.15f * float(X),
                      std::string(k.name) + " at " + std::to_string(X) + " px per module: both rows graded A or B, X measured");
            }
            std::printf("     %s by px per module (measured / grade / decoded / X):%s\n", k.name, seen.c_str());
        }
        // a known defect in one row: a bar of the bottom row's first character a third of a module
        // too wide (decodability 0.33, D); the top row stays clean and the symbol takes the lowest
        // row's grade
        {
            const cv::Mat img = drawStacked(kDataBarStkOmni, 6, rng, [](cv::Mat& lab) { lab(cv::Rect((12 + 8) * 6, (6 + 36) * 6, 2, 33 * 6)).setTo(35.f); });
            const Quality q = gradeLinear(img, codeOn(img, "0109501101530010"));
            const int gTop = q.scans == 20 ? rowGrade(q, 0) : -1, gBottom = q.scans == 20 ? rowGrade(q, 1) : -1;
            std::printf("     DataBar Stacked Omni, a bottom-row bar a third of a module wide: overall %d, top row %d, bottom row %d, decodability %.2f, %d decoded\n",
                        q.overall, gTop, gBottom, q.decodability, q.decodedScans);
            check(q.measured && gTop >= 3 && gBottom <= 2 && q.overall == gBottom && std::abs(q.decodability - 0.33f) < 0.1f,
                  "stacked DataBar: a defect in one row lowers that row, and the symbol takes the lowest row's grade");
        }
        // the top row's left finder voided: the top row reads in no scan, so no scan of either row
        // decodes the symbol: F
        {
            const cv::Mat img = drawStacked(kDataBarStkOmni, 3, rng, [](cv::Mat& lab) { lab(cv::Rect((12 + 21) * 3, 6 * 3, 6 * 3, 33 * 3)).setTo(220.f); });
            const Quality q = gradeLinear(img, codeOn(img, "0109501101530010"));
            std::printf("     DataBar Stacked Omni, top finder voided: measured %d, overall %d, %d decoded\n", int(q.measured), q.overall, q.decodedScans);
            check(!q.measured || (q.overall == 0 && q.decodedScans == 0), "stacked DataBar: a row that cannot be read fails the symbol (F or not graded)");
        }
        // single-row DataBar upside down: read in the other orientation, graded as the right way up
        {
            const auto mods = runsModules(kDataBarExpanded);
            cv::Mat img = drawLinear(mods, 3, rng), flipped;
            cv::flip(img, flipped, -1);
            Code c;
            c.text = "0198898765432106320201234515991231";
            c.format = CR_DATABAR;
            const float xs[4] = {80, float(img.cols - 80), float(img.cols - 80), 80}, ys[4] = {60, 60, float(img.rows - 60), float(img.rows - 60)};
            for (int i = 0; i < 4; ++i) { c.x[i] = xs[i]; c.y[i] = ys[i]; }
            const Quality q = gradeLinear(flipped, c);
            std::printf("     DataBar Expanded upside down: overall %d, decodability %.2f\n", q.overall, q.decodability);
            check(q.measured && q.overall >= 3 && q.decodability > 0.6f, "DataBar upside down graded as the right way up");
        }
    }
    Code db;
    db.format = CR_DATABAR;
    check(!gradeLinear(cv::Mat(100, 100, CV_8U, cv::Scalar(200)), db).measured, "a DataBar not found is not graded");
}

// ---- The C API's growing structs, symbology identifiers and 1D scan lines (c_api.cpp)
// ---- PDF417 row fusion (pdf417.cpp) and Micro QR grid fit (microqr.cpp), on symbols made by zint
// (zxing-cpp CreateBarcode) with synthetic damage

// the module matrix of a zint symbol (1 = dark); PDF417 rows come 3 modules tall
cv::Mat zintModules(ZXing::BarcodeFormat f, const std::string& text, const std::string& options = {})
{
    const auto bc = ZXing::CreateBarcodeFromText(text, ZXing::CreatorOptions(f, options));
    const auto iv = bc.symbol();
    cv::Mat m(iv.height(), iv.width(), CV_8U);
    for (int y = 0; y < iv.height(); ++y)
        for (int x = 0; x < iv.width(); ++x) m.at<uint8_t>(y, x) = iv.data(x, y)[0] < 128;
    return m;
}

// modules drawn X px each on a white label (2-module quiet zone) on a grey carton, at (40, 40)
cv::Mat drawModules(const cv::Mat& mods, float X)
{
    const int q = 2, w = static_cast<int>((mods.cols + 2 * q) * X), h = static_cast<int>((mods.rows + 2 * q) * X);
    cv::Mat img(h + 80, w + 80, CV_32F, cv::Scalar(110));
    img(cv::Rect(40, 40, w, h)).setTo(220);
    for (int y = 0; y < mods.rows; ++y)
        for (int x = 0; x < mods.cols; ++x)
            if (mods.at<uint8_t>(y, x))
                cv::rectangle(img, cv::Point2f(40 + (x + q) * X, 40 + (y + q) * X), cv::Point2f(40 + (x + q + 1) * X - 0.01f, 40 + (y + q + 1) * X - 0.01f),
                              cv::Scalar(35), -1);
    return img;
}

cv::Mat finishLabel(cv::Mat img, float blur, std::mt19937& rng)
{
    if (blur > 0) cv::GaussianBlur(img, img, cv::Size(), blur);
    std::normal_distribution<float> nz(0.f, 3.f);
    for (auto it = img.begin<float>(); it != img.end<float>(); ++it) *it += nz(rng);
    cv::Mat g;
    img.convertTo(g, CV_8U);
    return g;
}

// ---- unused error correction from the decoder (quality.cpp isoUec, microQrM1Uec)
// Symbols made by zint with a known number of codewords damaged (one module each), read and graded
// through the C API: zxing's count of corrected errors less ISO's misdecode protection.

// QR (versions 1 to 6, one alignment pattern from 2): the modules of the data bits in placement
// order, two-module columns up and down from the bottom right, skipping column 6
std::vector<std::pair<int, int>> qrPlacement(int v)
{
    const int n = 17 + 4 * v, a = 4 * v + 10;
    auto fixed = [&](int x, int y) {
        return x == 6 || y == 6 || (x < 9 && y < 9) || (x >= n - 8 && y < 9) || (x < 9 && y >= n - 8) ||
               (v > 1 && std::abs(x - a) <= 2 && std::abs(y - a) <= 2);
    };
    std::vector<std::pair<int, int>> out;
    bool up = true;
    for (int right = n - 1; right >= 1; right -= 2, up = !up) {
        if (right == 6) right = 5;
        for (int k = 0; k < n; ++k)
            for (int j = 0; j < 2; ++j) {
                const int y = up ? n - 1 - k : k, x = right - j;
                if (!fixed(x, y)) out.emplace_back(x, y);
            }
    }
    return out;
}

// Micro QR: the same without the column skip; the finder, separator and format area (x, y <= 8)
// and the timing row and column 0 fixed
std::vector<std::pair<int, int>> microQrPlacement(int n)
{
    std::vector<std::pair<int, int>> out;
    bool up = true;
    for (int right = n - 1; right >= 1; right -= 2, up = !up)
        for (int k = 0; k < n; ++k)
            for (int j = 0; j < 2; ++j) {
                const int y = up ? n - 1 - k : k, x = right - j;
                if (!((x <= 8 && y <= 8) || x == 0 || y == 0)) out.emplace_back(x, y);
            }
    return out;
}

// the read's unused error correction through the C API; -2 when not read
float uecThroughApi(const cv::Mat& mods, int format, std::mt19937& rng)
{
    cr_params p;
    cr_default_params(&p);
    p.format = format;
    p.grade_quality = 1;
    p.expected_count = 1;
    // this measures the grade, not the speed: the 200 ms default ends in no read on a busy machine
    p.time_budget_ms = 3000;
    cr_reader* r = cr_create(&p);
    const cv::Mat img = finishLabel(drawModules(mods, 6.f), 0.6f, rng);
    cr_quality q{};
    const bool got = cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step)) >= 1 && cr_get_quality(r, 0, &q) == 0;
    cr_destroy(r);
    return got ? q.unused_error_correction : -2.f;
}

void testGradingUec()
{
    using namespace cr;
    using F = ZXing::BarcodeFormat;
    std::mt19937 rng(18004);
    struct Case {
        const char* name;
        F f;
        int format;
        const char* text;
        const char* options;
        std::vector<int> bits; // data bits to flip (placement order), one per damaged codeword
        float want;
    };
    // codeword k's bits are 8k..8k+7 in placement order; QR 5-Q interleaves 4 blocks, so its
    // block 0 holds data codewords 0, 4, 8, ...
    const std::vector<Case> cases = {
        {"QR 1-L clean", F::QRCode, CR_QR, "HELLO123", "version=1,ecLevel=L", {}, 1.f},
        {"QR 1-L 1 error (zxing 0.71)", F::QRCode, CR_QR, "HELLO123", "version=1,ecLevel=L", {19}, 0.5f},
        {"QR 1-L 2 errors (zxing 0.42)", F::QRCode, CR_QR, "HELLO123", "version=1,ecLevel=L", {19, 51}, 0.f},
        {"QR 1-L 3 errors (zxing 0.14)", F::QRCode, CR_QR, "HELLO123", "version=1,ecLevel=L", {19, 51, 83}, 0.f},
        {"QR 1-M 1 error (zxing 0.80)", F::QRCode, CR_QR, "HELLO123", "version=1,ecLevel=M", {19}, 0.75f},
        {"QR 1-M 3 errors (zxing 0.40)", F::QRCode, CR_QR, "HELLO123", "version=1,ecLevel=M", {19, 51, 83}, 0.25f},
        {"QR 5-Q 3 errors in block 0 of 4 (p = 0)", F::QRCode, CR_QR, "HELLO123", "version=5,ecLevel=Q", {34, 130, 226}, 1.f - 6.f / 18.f},
        {"QR 5-Q 1 error in each of blocks 0 and 1", F::QRCode, CR_QR, "HELLO123", "version=5,ecLevel=Q", {34, 42}, 1.f - 2.f / 18.f},
        {"Micro QR M2-L 1 error (zxing 0.60)", F::MicroQRCode, CR_MICRO_QR, "12345", "version=2,ecLevel=L", {3}, 0.f},
        {"Micro QR M4-L 1 error (zxing 0.75)", F::MicroQRCode, CR_MICRO_QR, "123456", "version=4,ecLevel=L", {3}, 1.f - 2.f / 6.f},
        {"Micro QR M4-L 2 errors (zxing 0.50)", F::MicroQRCode, CR_MICRO_QR, "123456", "version=4,ecLevel=L", {3, 11}, 1.f - 4.f / 6.f},
        {"Micro QR M1 clean (zxing 0.0)", F::MicroQRCode, CR_MICRO_QR, "12345", "version=1", {}, 1.f},
    };
    for (const Case& c : cases) {
        cv::Mat m = zintModules(c.f, c.text, c.options);
        const auto place = c.format == CR_QR ? qrPlacement((m.cols - 17) / 4) : microQrPlacement(m.cols);
        for (int b : c.bits) {
            const auto [x, y] = place[static_cast<size_t>(b)];
            m.at<uint8_t>(y, x) ^= 1;
        }
        const float u = uecThroughApi(m, c.format, rng);
        std::printf("     %s: UEC %.2f (want %.2f)\n", c.name, u, c.want);
        check(std::abs(u - c.want) < 0.012f, std::string("UEC from the decoder after ISO 15415: ") + c.name);
    }
    // Data Matrix: one module of the data region flipped damages exactly one codeword (10 x 10 and
    // 14 x 14 fill their data regions with codewords). 14 x 14: 10 check codewords, 1 - 2/10;
    // 10 x 10: 5, one of them misdecode protection, 1 - 2/4 (zxing says 0.60)
    for (const auto& [size, want] : {std::pair{10, 0.5f}, std::pair{14, 0.8f}}) {
        cv::Mat m = zintModules(F::DataMatrix, size == 10 ? "123" : "HELLO123", "version=" + std::string(size == 10 ? "1" : "3"));
        check(m.cols == size, "Data Matrix " + std::to_string(size) + " x " + std::to_string(size) + " made (" + std::to_string(m.cols) + ")");
        m.at<uint8_t>(size / 2, size / 2) ^= 1;
        const float u = uecThroughApi(m, CR_DATAMATRIX, rng);
        std::printf("     Data Matrix %dx%d 1 error: UEC %.2f (want %.2f)\n", size, size, u, want);
        check(std::abs(u - want) < 0.012f, "UEC from the decoder after ISO 15415: Data Matrix " + std::to_string(size) + " x " + std::to_string(size) + ", 1 error");
    }
    // Micro QR M1: zxing reads no M1 with a wrong codeword, so its check codewords are tested on
    // the grid: one data module of the decoder's grid wrong is UEC 0, F
    {
        const cv::Mat m = zintModules(F::MicroQRCode, "12345", "version=1");
        std::vector<uint8_t> grid(m.datastart, m.dataend);
        Drawn d = drawGrid(grid, 11, 11, CR_MICRO_QR, 6, 30, 220, 3);
        const Quality clean = gradeCode(d.img, d.code);
        const auto [x, y] = microQrPlacement(11)[5];
        grid[static_cast<size_t>(y) * 11 + x] ^= 1;
        Drawn bad = drawGrid(grid, 11, 11, CR_MICRO_QR, 6, 30, 220, 3);
        const Quality hurt = gradeCode(bad.img, bad.code);
        check(clean.unusedErrorCorrection == 1.f && clean.gradeUEC == 4 && hurt.unusedErrorCorrection == 0.f && hurt.gradeUEC == 0,
              "Micro QR M1: intact check codewords UEC A, a wrong codeword F");
    }
    // quiet zone width: QR needs 4 modules (ISO/IEC 18004), Micro QR 2. A dark module 3 modules
    // out (past the old 2-module ring) lowers a QR's grade and leaves a Micro QR's alone
    {
        const int px = 6, quiet = 6;
        const cv::Mat q = zintModules(F::QRCode, "HELLO123", "version=1,ecLevel=M");
        const cv::Mat mq = zintModules(F::MicroQRCode, "12345", "version=2,ecLevel=L");
        for (const auto& [mods, format] : {std::pair{q, CR_QR}, std::pair{mq, CR_MICRO_QR}}) {
            const int w = mods.cols;
            std::vector<uint8_t> grid(mods.datastart, mods.dataend);
            Drawn d = drawGrid(grid, w, w, format, px, 30, 220, quiet);
            const Quality clean = gradeCode(d.img, d.code);
            cv::Mat marked = d.img.clone();
            cv::rectangle(marked, cv::Rect((quiet + w / 2) * px, (quiet - 3) * px, px, px), cv::Scalar(30), -1); // 3 modules above
            const Quality hit = gradeCode(marked, d.code);
            const std::string name = format == CR_QR ? "QR" : "Micro QR";
            std::printf("     %s quiet zone: clean QZ %d, a mark 3 modules out QZ %d\n", name.c_str(), clean.gradeQZ, hit.gradeQZ);
            check(clean.measured && clean.gradeQZ == 4, name + ": a 6-module quiet zone grades A");
            check(format == CR_QR ? hit.gradeQZ < 4 : hit.gradeQZ == 4,
                  format == CR_QR ? "QR: a mark 3 modules out is inside its 4-module quiet zone and lowers the grade"
                                  : "Micro QR: a mark 3 modules out is outside its 2-module quiet zone");
            // the image cut 2 modules from the symbol: the rest of a QR's zone cannot be checked,
            // so it grades F, never clear; a Micro QR's 2-module zone is all there
            const int cut = (quiet - 2) * px;
            Drawn edge = d;
            edge.img = d.img(cv::Rect(cut, cut, d.img.cols - cut, d.img.rows - cut)).clone();
            for (int k = 0; k < 4; ++k) { edge.code.x[k] -= static_cast<float>(cut); edge.code.y[k] -= static_cast<float>(cut); }
            const Quality cutQ = gradeCode(edge.img, edge.code);
            std::printf("     %s quiet zone cut by the image 2 modules out: QZ %d\n", name.c_str(), cutQ.gradeQZ);
            check(format == CR_QR ? cutQ.gradeQZ == 0 : cutQ.gradeQZ == 4,
                  format == CR_QR ? "QR: a quiet zone the image cuts at 2 modules grades F (off-image counts dark)"
                                  : "Micro QR: a 2-module quiet zone up to the image edge is whole and grades A");
        }
    }
}

std::string zxingOnly(const cv::Mat& g, ZXing::BarcodeFormat f)
{
    ZXing::ReaderOptions o;
    o.setFormats(f);
    o.setTryHarder(true);
    o.setTryRotate(true);
    o.setTextMode(ZXing::TextMode::Plain);
    return ZXing::ReadBarcode(ZXing::ImageView(g.data, g.cols, g.rows, ZXing::ImageFormat::Lum, static_cast<int>(g.step)), o).text();
}

// ---- zxing's PDF417 reads cross-checked (pdf417.cpp pdf417ReadHolds)
// A miscorrection made on purpose: symbol B is symbol A with one data codeword changed, so B
// differs from A there and in (about) every check codeword. A printed with that data codeword and
// half the check codewords of B lies nCheck / 2 codewords from B and one more from A: zxing, which
// corrects to the last check codeword, reads B. The engine must read nothing (NG).

// the codeword values of a zint PDF417, row by row (-1 where no pattern matches)
std::vector<int> pdf417Codewords(const cv::Mat& sym)
{
    const int cols = (sym.cols - 69) / 17, rows = sym.rows / 3;
    std::vector<int> cw;
    for (int r = 0; r < rows; ++r)
        for (int k = 0; k < cols; ++k) {
            int bits = 0;
            for (int j = 0; j < 17; ++j) bits = bits * 2 + sym.at<uint8_t>(3 * r, 34 + 17 * k + j);
            int value = -1;
            for (int v = 0; v < 929 && value < 0; ++v)
                if (cr::pdf417Pattern(r % 3, v) == bits) value = v;
            cw.push_back(value);
        }
    return cw;
}

// ---- rMQR (ISO/IEC 23941): read by zxing-cpp under its own flag, never reported in a QR job
void testRmqr()
{
    std::mt19937 rng(23941);
    const ZXing::CreatorOptions opts(ZXing::BarcodeFormat::RMQRCode);
    const auto bc = ZXing::CreateBarcodeFromText("HELLO123", opts);
    const auto iv = bc.symbol();
    cv::Mat mods(iv.height(), iv.width(), CV_8U);
    for (int y = 0; y < iv.height(); ++y)
        for (int x = 0; x < iv.width(); ++x) mods.at<uint8_t>(y, x) = iv.data(x, y)[0] < 128;
    const cv::Mat img = finishLabel(drawModules(mods, 6.f), 0.6f, rng);
    const cr::Models none;
    const std::string own = onlyText(cr::decode(img, linearJob(CR_RMQR), none), CR_RMQR);
    const cr::Result qr = cr::decode(img, linearJob(CR_QR | CR_MICRO_QR), none);
    const cr::Result any = cr::decode(img, linearJob(CR_ANY), none);
    std::printf("     rMQR %dx%d: rMQR job '%s', QR job %zu codes, any-type job %zu codes\n", mods.rows, mods.cols, own.c_str(),
                qr.codes.size(), any.codes.size());
    check(own == "HELLO123", "rMQR: read in an rMQR job");
    check(qr.codes.empty(), "rMQR: never reported in a QR / Micro QR job");
    check(any.codes.size() == 1 && any.codes[0].format == CR_RMQR, "rMQR: an any-type job reports it as rMQR");
    // an older caller's "any" (0x3FFFF, before rMQR) is read as CR_ANY through the C API
    const cr_params old = linearJob(0x3FFFF);
    cr_reader* r = cr_create(&old);
    const int32_t n = r ? cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step)) : -1;
    cr_params now = linearJob(CR_ANY);
    const int32_t m = r && cr_set_params(r, &now) == 0 ? cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step)) : -1;
    cr_destroy(r);
    check(n == 1 && m == 1, "rMQR: an older caller's any-type job (0x3FFFF) is read as CR_ANY (" + std::to_string(n) + ", " +
                                std::to_string(m) + ")");
}

// ---- rMQR graded by ISO/IEC 15415 with its own fixed patterns (quality.cpp, Req 17.1 GRD-02)
// Every rMQR size (versions 1-32, 7 to 17 rows, 27 to 139 columns) drawn from zint's symbol: clean
// it grades A with no fixed pattern module wrong; damage to a fixed pattern, a mark in the 2-module
// quiet zone or faded print lower the grade; a size the standard has not is not graded (never A).
void testGradeRmqr()
{
    using namespace cr;
    std::mt19937 rng(23941);
    auto soft = [](cv::Mat img) { cv::GaussianBlur(img, img, cv::Size(0, 0), 0.7); return img; };
    auto symbolOf = [](const std::string& text, int version, int& w, int& h) {
        ZXing::CreatorOptions opts(ZXing::BarcodeFormat::RMQRCode);
        opts.options("version=" + std::to_string(version));
        const auto bc = ZXing::CreateBarcodeFromText(text, opts);
        const auto iv = bc.symbol(); // a view into bc: bc must outlive it
        w = iv.width();
        h = iv.height();
        std::vector<uint8_t> m(static_cast<size_t>(w) * h);
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x) m[static_cast<size_t>(y) * w + x] = iv.data(x, y)[0] < 128;
        return m;
    };
    std::set<std::pair<int, int>> sizes;
    int clean = 0;
    std::string worst;
    for (int v = 1; v <= 32; ++v) {
        int w = 0, h = 0;
        const auto m = symbolOf("R" + std::to_string(v), v, w, h);
        sizes.insert({w, h});
        Drawn d = drawGrid(m, w, h, CR_RMQR, 5, 30, 220, 3);
        d.code.ecLevel = "M";
        d.code.uec = 1.f;
        const Quality q = gradeCode(soft(d.img), d.code);
        if (q.measured && q.overall == 4 && q.fixedPatternDamage == 0.f && q.gradeQZ == 4 && q.gradeGN == 4) ++clean;
        else worst += " R" + std::to_string(h) + "x" + std::to_string(w) + " FPD " + std::to_string(q.fixedPatternDamage) + " GN " +
                      std::to_string(q.gridNonUniformity) + " -> " + std::to_string(q.overall);
    }
    std::printf("     rMQR: %zu sizes, %d of 32 clean graded A%s\n", sizes.size(), clean, worst.c_str());
    check(sizes.size() == 32 && clean == 32, "rMQR: every size (versions 1-32) clean grades A, no fixed pattern module wrong");

    // R11x43: a finder sub-pattern light ring module and an alignment centre printed dark; a mark in
    // the quiet zone; faded print
    int w = 0, h = 0;
    const auto m = symbolOf("HELLO123", 12, w, h); // version 12: 11 x 43
    const int px = 5;
    Drawn d = drawGrid(m, w, h, CR_RMQR, px, 30, 220, 3);
    d.code.ecLevel = "M";
    d.code.uec = 1.f;
    check(w == 43 && h == 11 && gradeCode(soft(turned(d, 1).img), turned(d, 1).code).overall == 4 &&
              gradeCode(soft(turned(d, 2).img), turned(d, 2).code).overall == 4,
          "rMQR: turned a quarter and a half it still grades A (corners in symbol order)");
    cv::Mat hurt = d.img.clone();
    for (const auto& [x, y] : {std::pair{w - 4, h - 4}, std::pair{21, 1}})
        cv::rectangle(hurt, cv::Rect((3 + x) * px, (3 + y) * px, px, px), cv::Scalar(30), -1);
    const Quality fp = gradeCode(soft(hurt), d.code);
    check(fp.fixedPatternDamage > 0.f && fp.gradeFPD < 4 && fp.overall < 4, "rMQR: a sub-pattern and an alignment module printed dark lower fixed pattern damage (" +
                                                                              std::to_string(fp.fixedPatternDamage) + ")");
    cv::Mat speck = d.img.clone();
    cv::rectangle(speck, cv::Rect((3 + 20) * px, (3 + h + 1) * px, 2 * px, px), cv::Scalar(30), -1); // in the 2-module ring below
    check(gradeCode(soft(speck), d.code).gradeQZ < 4, "rMQR: a dark mark in the 2-module quiet zone lowers its grade");
    cv::Mat beyond = d.img.clone();
    cv::rectangle(beyond, cv::Rect((3 + 20) * px, (3 + h + 2) * px + 2, 2 * px, px - 2), cv::Scalar(30), -1); // 2 modules out: no quiet zone
    check(gradeCode(soft(beyond), d.code).gradeQZ == 4, "rMQR: a mark beyond the 2-module quiet zone does not lower it");
    const Quality faded = gradeCode(soft(drawGrid(m, w, h, CR_RMQR, px, 100, 200, 3).img), d.code);
    check(faded.gradeSC == 2 && faded.overall == 2, "rMQR: faded to 50 % contrast grades C (" + std::to_string(faded.overall) + ")");
    // a size no rMQR has: its fixed patterns are unknown, so not graded, never A
    Drawn odd = drawGrid(std::vector<uint8_t>(static_cast<size_t>(31) * 11, 0), 31, 11, CR_RMQR, px, 30, 220, 3);
    odd.code.modules = m; // any grid; the size decides
    odd.code.modules.resize(static_cast<size_t>(31) * 11);
    check(!gradeCode(odd.img, odd.code).measured, "rMQR: a 11 x 31 grid (no rMQR size) is not graded");

    // read and graded end to end: the decoder's grid and corners give a grade
    cv::Mat mods(h, w, CV_8U);
    for (int y = 0; y < h; ++y)
        for (int x = 0; x < w; ++x) mods.at<uint8_t>(y, x) = m[static_cast<size_t>(y) * w + x];
    const cv::Mat img = finishLabel(drawModules(mods, 6.f), 0.6f, rng);
    cr_params p = linearJob(CR_RMQR);
    p.grade_quality = 1;
    const Result r = cr::decode(img, p, Models{});
    const bool read = r.codes.size() == 1 && r.codes[0].format == CR_RMQR && r.quality.size() == 1;
    std::printf("     rMQR read and graded: %s, FPD %.2f -> %d\n", read ? r.codes[0].text.c_str() : "-", read ? r.quality[0].fixedPatternDamage : -1.f,
                read ? r.quality[0].overall : -1);
    check(read && r.quality[0].measured && r.quality[0].overall >= 3 && r.quality[0].fixedPatternDamage == 0.f,
          "rMQR: read in an rMQR job with grading on, it is graded (B or better on a clean label)");
}

// ---- the 2D quiet-zone ring with a calibration (quality.cpp, Req 17.1 GRD-01)
// The ring is compared with the threshold in the threshold's own units: flat-field corrected grey,
// or reflectance. A dim camera (light 60 grey) with a reflectance card, and light falling to 30 %
// across the label with a flat field: the clear margin grades A, a mark in it still lowers it.
void testGradeQuietZoneCalibrated()
{
    using namespace cr;
    std::mt19937 rng(18004);
    auto soft = [](cv::Mat img) { cv::GaussianBlur(img, img, cv::Size(0, 0), 0.7); return img; };
    const int w = 13, px = 8;
    const auto m = microQrGrid(w, rng);
    Drawn d = drawGrid(m, w, w, CR_MICRO_QR, px, 10, 60, 4);
    d.code.ecLevel = "M";
    d.code.uec = 1.f;
    Calibration card;
    card.reflectance = true;
    card.greyDark = 10; card.greyLight = 60; card.reflDark = 5; card.reflLight = 85;
    const Quality dim = gradeCode(soft(d.img.clone()), d.code, &card);
    std::printf("     dim camera, reflectance card: SC %.0f QZ %.2f -> %d\n", dim.symbolContrast, dim.quietZoneDark, dim.overall);
    check(dim.measured && dim.calibrated && dim.gradeQZ == 4 && dim.overall == 4, "calibrated quiet zone: a clear margin under a dim camera grades A in reflectance");
    cv::Mat speck = d.img.clone();
    cv::rectangle(speck, cv::Rect((4 + 5) * px, 3 * px, px, px), cv::Scalar(10), -1);
    check(gradeCode(soft(speck), d.code, &card).gradeQZ < 4, "calibrated quiet zone: a dark mark in the margin still lowers its grade");

    // flat field: the light falls to 30 % from left to right; the white card shows the same fall
    Drawn f = drawGrid(m, w, w, CR_MICRO_QR, px, 30, 220, 4);
    f.code = d.code;
    cv::Mat lit(f.img.size(), CV_8U), white(f.img.size(), CV_8U);
    for (int y = 0; y < lit.rows; ++y)
        for (int x = 0; x < lit.cols; ++x) {
            const float fall = 1.f - 0.7f * static_cast<float>(x) / static_cast<float>(lit.cols - 1);
            lit.at<uint8_t>(y, x) = cv::saturate_cast<uint8_t>(f.img.at<uint8_t>(y, x) * fall);
            white.at<uint8_t>(y, x) = cv::saturate_cast<uint8_t>(230.f * fall);
        }
    Calibration flat;
    check(setFlatField(flat, white), "calibrated quiet zone: the white card is taken");
    const Quality ff = gradeCode(soft(lit.clone()), f.code, &flat);
    std::printf("     light falling to 30 %%, flat field: QZ %.2f -> %d\n", ff.quietZoneDark, ff.overall);
    check(ff.measured && ff.gradeQZ == 4, "calibrated quiet zone: a clear margin where the light falls grades A after the flat field");
    cv::Mat mark = lit.clone();
    cv::rectangle(mark, cv::Rect((4 + w) * px, (4 + 6) * px, px, px), cv::Scalar(15), -1); // right of the symbol, in the dim part
    check(gradeCode(soft(mark), f.code, &flat).gradeQZ < 4, "calibrated quiet zone: a mark in the dim part of the margin still lowers it");
}

void testPdf417CrossCheck()
{
    using F = ZXing::BarcodeFormat;
    const cr::Models none;
    std::mt19937 rng(4170);
    for (const int level : {2, 4}) {
        const int nCheck = 2 << level;
        // A and B: one letter apart, found so that exactly one data codeword differs
        const std::string a = "VS-LOT ABCDEFGHIJKLMNOPQRSTUVWXYZ EXP 2027";
        std::string b;
        cv::Mat ma, mb;
        std::vector<int> ca, cb;
        int j = -1;
        for (size_t at = 8; at < 30 && j < 0; ++at) {
            b = a;
            b[at] = b[at] == 'Q' ? 'R' : 'Q';
            ma = zintModules(F::PDF417, a, "ecLevel=" + std::to_string(level));
            mb = zintModules(F::PDF417, b, "ecLevel=" + std::to_string(level));
            if (ma.size() != mb.size()) continue;
            ca = pdf417Codewords(ma);
            cb = pdf417Codewords(mb);
            const int nData = static_cast<int>(ca.size()) - nCheck;
            std::vector<int> diff;
            for (int i = 0; i < nData; ++i)
                if (ca[static_cast<size_t>(i)] != cb[static_cast<size_t>(i)]) diff.push_back(i);
            if (diff.size() == 1) j = diff[0];
        }
        check(j >= 0, "PDF417 cross-check: two symbols one data codeword apart made (level " + std::to_string(level) + ")");
        if (j < 0) continue;
        const int cols = (ma.cols - 69) / 17, n = static_cast<int>(ca.size());
        std::vector<int> checkDiff;
        for (int i = n - nCheck; i < n; ++i)
            if (ca[static_cast<size_t>(i)] != cb[static_cast<size_t>(i)]) checkDiff.push_back(i);
        // A with codeword j and the first `fromB` differing check codewords taken from B
        auto mixed = [&](int fromB) {
            cv::Mat m = ma.clone();
            std::vector<int> take; // none when fromB < 0: A as it is
            if (fromB >= 0) take.push_back(j);
            for (int i = 0; i < fromB && i < static_cast<int>(checkDiff.size()); ++i) take.push_back(checkDiff[static_cast<size_t>(i)]);
            for (const int at : take) {
                const int r = at / cols, k = at % cols;
                mb(cv::Rect(34 + 17 * k, 3 * r, 17, 3)).copyTo(m(cv::Rect(34 + 17 * k, 3 * r, 17, 3)));
            }
            return finishLabel(drawModules(m, 3), 0.6f, rng);
        };
        auto engineTexts = [&](const cv::Mat& g) {
            std::string t;
            for (const auto& c : decode(g, linearJob(CR_PDF417), none).codes) t += (t.empty() ? "" : "|") + c.text;
            return t;
        };
        const std::string L = " (level " + std::to_string(level) + ", " + std::to_string(nCheck) + " check codewords)";
        // nCheck / 2 from B: zxing miscorrects to B, the engine reads nothing
        const cv::Mat mis = mixed(nCheck / 2);
        const std::string zx = zxingOnly(mis, F::PDF417), en = engineTexts(mis);
        std::printf("     PDF417 miscorrection%s: %zu check codewords differ, zxing '%s', engine '%s'\n", L.c_str(), checkDiff.size(), zx.c_str(), en.c_str());
        check(static_cast<int>(checkDiff.size()) >= nCheck - 1 && zx == b, "PDF417 cross-check: zxing alone miscorrects to the other symbol" + L);
        check(en.empty(), "PDF417 cross-check: the engine reads nothing from a miscorrected symbol, never the wrong text" + L);
        // A with codeword j and nCheck / 2 - 2 more wrong (nCheck - 2 used): read, A
        const cv::Mat near = mixed(nCheck / 2 - 2);
        check(engineTexts(near) == a, "PDF417 cross-check: " + std::to_string(nCheck / 2 - 1) + " wrong codewords (2 check codewords to spare) still read right" + L);
        // A clean: read (no check codeword used, no second read)
        check(engineTexts(mixed(-1)) == a, "PDF417 cross-check: the clean symbol reads" + L);
    }
}

void testStacked()
{
    using cr::rs929Correct;
    using F = ZXing::BarcodeFormat;
    // the pattern table: four bars and four spaces of 1-6 modules, each in its own cluster
    int good = 0;
    for (int cl = 0; cl < 3; ++cl)
        for (int v = 0; v < 929; ++v) {
            const int p = cr::pdf417Pattern(cl, v);
            std::vector<int> runs;
            int bit = -1;
            for (int k = 16; k >= 0; --k) {
                const int b = (p >> k) & 1;
                if (b == bit) ++runs.back();
                else { runs.push_back(1); bit = b; }
            }
            const bool shape = ((p >> 16) & 1) && !(p & 1) && runs.size() == 8 && *std::max_element(runs.begin(), runs.end()) <= 6;
            good += shape && (runs[0] - runs[2] + runs[4] - runs[6] + 9) % 9 == cl * 3;
        }
    check(good == 3 * 929, "PDF417 pattern table: 2787 patterns of four bars and four spaces in their own clusters");

    // codewords of a zint symbol read exactly through the table: zero syndromes; then errors and
    // erasures corrected, and too many refused with the codewords left as they were
    const cv::Mat sym = zintModules(F::PDF417, "LOT00001-EXP2027-12-SN12345678", "ecLevel=4");
    const int cols = (sym.cols - 69) / 17, rows = sym.rows / 3;
    std::vector<int> cw;
    for (int r = 0; r < rows; ++r)
        for (int k = 0; k < cols; ++k) {
            int bits = 0;
            for (int j = 0; j < 17; ++j) bits = bits * 2 + sym.at<uint8_t>(3 * r, 34 + 17 * k + j);
            int value = -1;
            for (int v = 0; v < 929 && value < 0; ++v)
                if (cr::pdf417Pattern(r % 3, v) == bits) value = v;
            cw.push_back(value);
        }
    const int nCheck = 32; // level 4
    std::vector<int> ok = cw;
    int errs = -1;
    const bool clean = std::find(cw.begin(), cw.end(), -1) == cw.end() && rs929Correct(ok, {}, nCheck, 2, &errs) && errs == 0 && ok == cw;
    check(clean && cw[0] == static_cast<int>(cw.size()) - nCheck, "zint's PDF417 read through the table: zero syndromes, length descriptor right");
    std::vector<int> bad = cw;
    for (const int at : {3, 17, 40}) bad[static_cast<size_t>(at)] = (bad[static_cast<size_t>(at)] + 123) % 929; // errors
    bad[5] = 0;
    bad[50] = 0;                                                                                        // erasures
    std::vector<int> fixedCw = bad;
    const bool corrected = rs929Correct(fixedCw, {5, 50}, nCheck, 2, &errs) && fixedCw == cw && errs == 3;
    std::vector<int> tooMany = bad;
    std::vector<int> er;
    for (int k = 0; k < 29; ++k) er.push_back(k * 2);
    const bool refused = !rs929Correct(tooMany, er, nCheck, 2) && tooMany == bad;
    check(corrected && refused, "GF(929) Reed-Solomon: 3 errors and 2 erasures corrected; 29 erasures (over the reserve) refused, codewords untouched");

    // PDF417 labels at 3 px per module, scratched along the bars (each scratch breaks one codeword
    // column in every row) or across, plus both; zxing alone, the fused reader, the whole decode
    const cr::Models none;
    struct Set { const char* name; int vertical, horizontal, level; };
    std::mt19937 rng(417);
    std::uniform_real_distribution<float> U(0, 1);
    int fusedWrong = 0;
    for (const Set& st : {Set{"clean", 0, 0, 2}, Set{"2 scratches along the bars, level 2", 2, 0, 2}, Set{"3 along, level 4", 3, 0, 4},
                          Set{"2 along and 2 across, level 4", 2, 2, 4}}) {
        constexpr int N = 10;
        int zx = 0, fused = 0, engine = 0, wrong = 0;
        for (int i = 0; i < N; ++i) {
            char t[64];
            std::snprintf(t, sizeof t, "LOT%05d-EXP2027-12-SN%08u", i, static_cast<unsigned>(rng() % 100000000u));
            const cv::Mat mods = zintModules(F::PDF417, t, "ecLevel=" + std::to_string(st.level));
            cv::Mat img = drawModules(mods, 3);
            const float x0 = 46, y0 = 46, W = mods.cols * 3.f, H = mods.rows * 3.f;
            for (int k = 0; k < st.vertical; ++k) {
                const float a = x0 + 60 + U(rng) * (W - 120); // not over the start and stop patterns
                cv::line(img, cv::Point2f(a, y0 - 5), cv::Point2f(a, y0 + H + 5), cv::Scalar(U(rng) < 0.5f ? 35 : 220), 4);
            }
            for (int k = 0; k < st.horizontal; ++k) {
                const float b = y0 + U(rng) * H;
                cv::line(img, cv::Point2f(x0 - 10, b), cv::Point2f(x0 + W + 10, b + (U(rng) - 0.5f) * 6), cv::Scalar(U(rng) < 0.5f ? 35 : 220), 5);
            }
            const cv::Mat g = finishLabel(img, 0.7f, rng);
            zx += zxingOnly(g, F::PDF417) == t;
            for (const auto& r : cr::readPdf417Fused(g, 1, std::chrono::steady_clock::now() + std::chrono::milliseconds(vs_test_ms(5000)))) (r.text == t ? fused : wrong)++;
            const cr::Result res = decode(g, linearJob(CR_PDF417), none);
            for (const auto& c : res.codes) (c.text == t && c.format == CR_PDF417 ? engine : wrong)++;
        }
        std::printf("     PDF417 %s: zxing %d/%d, fused %d/%d, engine %d/%d, wrong %d\n", st.name, zx, N, fused, N, engine, N, wrong);
        fusedWrong += wrong;
        if (st.vertical == 0) check(fused == N && engine == N, std::string("PDF417 ") + st.name + ": the fused reader reads every one");
        else check(engine >= zx && engine >= N * 7 / 10 && engine > zx, std::string("PDF417 ") + st.name + ": the engine reads more than zxing, 7 of 10 or more");
    }
    // beyond the check codewords: eight scratches on a level 2 symbol read nothing rather than wrong
    int over = 0;
    for (int i = 0; i < 6; ++i) {
        const std::string t = "LOT0000" + std::to_string(i) + "-EXP2027-12-SN0000000" + std::to_string(i);
        const cv::Mat mods = zintModules(F::PDF417, t, "ecLevel=2");
        cv::Mat img = drawModules(mods, 3);
        for (int k = 0; k < 8; ++k) {
            const float a = 106 + U(rng) * (mods.cols * 3.f - 120);
            cv::line(img, cv::Point2f(a, 40), cv::Point2f(a, 60 + mods.rows * 3.f), cv::Scalar(U(rng) < 0.5f ? 35 : 220), 5);
        }
        for (const auto& r : cr::readPdf417Fused(finishLabel(img, 0.7f, rng), 1, std::chrono::steady_clock::now() + std::chrono::milliseconds(vs_test_ms(5000))))
            over += r.text != t;
    }
    check(fusedWrong == 0 && over == 0, "PDF417 fusion: no wrong read, also past what Reed-Solomon can correct");

    // Micro QR (M2-M4) at 4 px per module, blurred past zxing's edges and slanted, in all four turns
    struct MSet { const char* name; float blur, persp; };
    int microWrong = 0;
    for (const MSet& st : {MSet{"sharp", 0.5f, 0.f}, MSet{"blur 1.8 px (0.45 module)", 1.8f, 0.f}, MSet{"blur 1.8 px, slanted", 1.8f, 0.25f},
                           MSet{"blur 2.2 px (0.55 module)", 2.2f, 0.f}}) {
        constexpr int N = 10;
        int zx = 0, fit = 0, engine = 0, wrong = 0;
        for (int i = 0; i < N; ++i) {
            const std::string t = std::to_string(10000000u + static_cast<unsigned>(rng() % 90000000u));
            cv::Mat img = drawModules(zintModules(F::MicroQRCode, t, i % 2 ? "ecLevel=M" : ""), 4);
            for (int r = 0; r < i % 4; ++r) cv::rotate(img, img, cv::ROTATE_90_CLOCKWISE);
            if (st.persp > 0) {
                std::uniform_real_distribution<float> P(-st.persp, st.persp);
                const float W = static_cast<float>(img.cols), H = static_cast<float>(img.rows);
                const cv::Point2f s4[4] = {{0, 0}, {W, 0}, {W, H}, {0, H}};
                cv::Point2f d4[4];
                for (int k = 0; k < 4; ++k) d4[k] = s4[k] + cv::Point2f(P(rng) * W * 0.4f + W * 0.3f, P(rng) * H * 0.4f + H * 0.3f);
                cv::Mat big(static_cast<int>(H * 1.6f), static_cast<int>(W * 1.6f), CV_32F, cv::Scalar(110));
                cv::warpPerspective(img, big, cr::homography4(s4, d4), big.size(), cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(110));
                img = big;
            }
            const cv::Mat g = finishLabel(img, st.blur, rng);
            zx += zxingOnly(g, F::MicroQRCode) == t;
            for (const auto& r : cr::readMicroQrFit(g, 1, std::chrono::steady_clock::now() + std::chrono::milliseconds(vs_test_ms(5000)))) (r.text == t ? fit : wrong)++;
            const cr::Result res = decode(g, linearJob(CR_MICRO_QR), none);
            for (const auto& c : res.codes) (c.text == t && c.format == CR_MICRO_QR ? engine : wrong)++;
        }
        std::printf("     Micro QR %s: zxing %d/%d, grid fit %d/%d, engine %d/%d, wrong %d\n", st.name, zx, N, fit, N, engine, N, wrong);
        microWrong += wrong;
        check(fit >= N * 8 / 10 && engine >= N * 8 / 10, std::string("Micro QR ") + st.name + ": 8 of 10 or more read");
    }
    // nothing that is not a Micro QR reads as one: a full QR, a Data Matrix-like blob field, noise
    int falseReads = 0;
    for (int i = 0; i < 8; ++i) {
        cv::Mat img(200, 260, CV_32F, cv::Scalar(110));
        img(cv::Rect(20, 20, 220, 160)).setTo(220);
        if (i == 0) {
            const qrcodegen::QrCode q = qrcodegen::QrCode::encodeText("RJ0187477804", qrcodegen::QrCode::Ecc::MEDIUM);
            for (int y = 0; y < q.getSize(); ++y)
                for (int x = 0; x < q.getSize(); ++x)
                    if (q.getModule(x, y)) img(cv::Rect(40 + 4 * x, 40 + 4 * y, 4, 4)).setTo(35);
        } else
            for (int k = 0; k < 60 * i; ++k) img(cv::Rect(30 + static_cast<int>(U(rng) * 190), 30 + static_cast<int>(U(rng) * 130), 4, 4)).setTo(35);
        const cv::Mat g = finishLabel(img, 1.5f, rng);
        falseReads += static_cast<int>(cr::readMicroQrFit(g, 1, std::chrono::steady_clock::now() + std::chrono::milliseconds(vs_test_ms(2000))).size());
    }
    check(microWrong == 0 && falseReads == 0, "Micro QR grid fit: no wrong read, and no read on a QR, scattered dots or noise");
}

// The 1D quiet-zone verdict follows the pixels, not the pass that read the code (PR #148's soak: a
// laser EAN-13 failed it on a fresh reader, whose pass gave corners a module short on a strip at the
// top of the bars, and passed on a reader whose learned step order found it with corners on the bars)
void testQuietZoneSamePixels()
{
    std::mt19937 rng(148);
    const int X = 6, q = 12, h = 30 * X;
    // print gain as on the laser label: bars 8 px, spaces 4 px, so the end guard's last bar edges are
    // 2 X apart and a corner a module short sits halfway between them
    const std::vector<uint8_t> ean = ean13Modules("8901396152002");
    const cv::Mat clear = drawLinear(ean, X, rng, q, 2);
    const float x0 = 80.f + q * X - 1.f, x1 = 80.f + (q + 95) * X + 1.f, y0 = 60.f, y1 = 60.f + h; // the bars' outer edges
    const int xs0 = static_cast<int>(x0), xs1 = static_cast<int>(x1);
    cv::Mat touching = clear.clone(), near = clear.clone(), inZone = clear.clone(), before = clear.clone(), shortMark = clear.clone();
    touching(cv::Rect(xs1, 60 + h / 3, 2 * X, h / 3)).setTo(35);           // 2 X wide, on the end bar
    near(cv::Rect(xs1 + X, 60 + h / 3, X, h / 3)).setTo(35);               // 1 X past the end bar
    inZone(cv::Rect(xs1 + 5 * X, 60 + h / 3, X, h / 3)).setTo(35);         // 5 X past (7 X needed)
    before(cv::Rect(xs0 - 6 * X, 60 + h / 3, X, h / 3)).setTo(35);         // 5 X before the start (11 X needed)
    shortMark(cv::Rect(xs1 + 4 * X, 60 + h / 2 - 5 * X / 2, X, 5 * X)).setTo(35); // 5 X tall, 4 X past
    struct Corners { const char* what; float l, r, top, bottom; };
    const Corners variants[] = {
        {"on the bars, full height", x0, x1, y0, y1},
        {"strip at the top, end 1 X short", x0, x1 - X, y0 + 2, y0 + 15},
        {"strip at the bottom, ends 0.8 X long", x0 - 0.8f * X, x1 + 0.8f * X, y1 - 20, y1 - 5},
        {"start 1 X short, end 0.8 X long", x0 + X, x1 + 0.8f * X, y0, y1},
        {"start 0.8 X long, end 1 X short", x0 - 0.8f * X, x1 - X, y0 + 40, y0 + 120},
    };
    const auto qz = [&](const cv::Mat& img, int format, const Corners& k, int lightOnDark = 0) {
        cr::Code c;
        c.format = format;
        c.lightOnDark = lightOnDark;
        const float xs[4] = {k.l, k.r, k.r, k.l}, ys[4] = {k.top, k.top, k.bottom, k.bottom};
        std::copy(xs, xs + 4, c.x);
        std::copy(ys, ys + 4, c.y);
        return cr::quietZoneOk(img, c);
    };
    bool clearOk = true, marksNg = true;
    for (const auto& k : variants) {
        const int a = qz(clear, CR_EAN13, k), b = qz(touching, CR_EAN13, k), n = qz(near, CR_EAN13, k), z = qz(inZone, CR_EAN13, k),
                  f = qz(before, CR_EAN13, k), t = qz(shortMark, CR_EAN13, k);
        std::printf("     EAN-13 quiet zone, corners %s: clear %d; mark touching %d, 1 X past %d, 5 X past %d, 5 X before %d, 5 X tall %d\n",
                    k.what, a, b, n, z, f, t);
        clearOk = clearOk && a == 1;
        marksNg = marksNg && b == 0 && n == 0 && z == 0 && f == 0 && t == 0;
    }
    check(clearOk, "EAN-13 quiet zone: clear zones pass whichever pass gave the corners (strip, a module short or long)");
    check(marksNg, "EAN-13 quiet zone: a mark on, 1 or 5 X past, 5 X before the bars, or 5 X tall fails for every corner variant");

    // the bars' side from the decoder, never from the zone: a label just the symbol's width on a darker
    // carton has no quiet zone, whatever the polarity says; light-on-dark print measures as dark
    const cv::Mat noZone = drawLinear(ean, X, rng, 0);
    const Corners nz{"", 80.f, 80.f + 95 * X, y0, y1};
    cv::Mat inverted;
    cv::bitwise_not(clear, inverted);
    const int nzKnown = qz(noZone, CR_EAN13, nz, 0), nzUnknown = qz(noZone, CR_EAN13, nz, -1);
    const int invKnown = qz(inverted, CR_EAN13, variants[0], 1), invUnknown = qz(inverted, CR_EAN13, variants[0], -1);
    std::printf("     EAN-13 on a darker carton, no quiet zone: %d (polarity unknown %d); light on dark: %d (unknown %d)\n", nzKnown, nzUnknown,
                invKnown, invUnknown);
    check(nzKnown == 0 && nzUnknown == 0, "1D quiet zone: no quiet zone on a darker carton fails, the carton never taken for spaces");
    check(invKnown == 1 && invUnknown == 1, "1D quiet zone: light-on-dark print measured with its own polarity");

    // other types: the one bar edge within 1.5 X of each corner, the same for corners up to half a
    // module off; a mark just past the end bar (a second edge there) fails for every corner, and a
    // corner a module off (two edges in reach) fails rather than guess
    const std::vector<uint8_t> m39 = code39Modules("VS-39AV");
    const cv::Mat c39 = drawLinear(m39, X, rng, q);
    cv::Mat c39mark = c39.clone(), c39near = c39.clone();
    const float l39 = 80.f + q * X, r39 = 80.f + static_cast<float>(q + m39.size()) * X;
    c39mark(cv::Rect(static_cast<int>(r39) + 5 * X, 60 + h / 3, X, h / 3)).setTo(35);
    c39near(cv::Rect(static_cast<int>(r39) + 3, 60 + h / 3, 5, h / 3)).setTo(35); // 0.8 X wide, 0.5 X past
    bool c39same = true, c39marks = true;
    for (const float d : {0.f, -0.45f, 0.45f, -1.f, 1.f}) {
        const Corners k{"", l39 - d * X, r39 + d * X, y0 + 4, y0 + 20};
        const int a = qz(c39, CR_CODE39, k), b = qz(c39mark, CR_CODE39, k), m = qz(c39near, CR_CODE39, k);
        std::printf("     Code 39 quiet zone, ends %+.2f X: clear %d, mark at 5 X %d, mark 0.5 X past %d\n", d, a, b, m);
        if (std::abs(d) < 0.5f) c39same = c39same && a == 1;
        c39marks = c39marks && b == 0 && m == 0;
    }
    // ITF, the end corner a module past the bars: a mark 0.7 X past the end bar is never taken for it
    const std::vector<uint8_t> mItf = itfModules("15400141288763");
    const cv::Mat itf = drawLinear(mItf, X, rng, q);
    cv::Mat itfNear = itf.clone();
    const float lItf = 80.f + q * X, rItf = 80.f + static_cast<float>(q + mItf.size()) * X;
    itfNear(cv::Rect(static_cast<int>(rItf) + 4, 60 + h / 3, X, h / 3)).setTo(35);
    bool itfMarks = true;
    for (const float d : {0.f, 0.45f, 1.f}) {
        const Corners k{"", lItf, rItf + d * X, y0, y1};
        const int a = qz(itf, CR_ITF, k), m = qz(itfNear, CR_ITF, k);
        std::printf("     ITF quiet zone, end %+.2f X: clear %d, mark 0.7 X past %d\n", d, a, m);
        itfMarks = itfMarks && m == 0 && (d > 0.5f || a == 1);
    }
    check(c39same, "Code 39 quiet zone: the same verdict for corners up to 0.45 X short or long");
    check(c39marks && itfMarks, "Code 39 / ITF quiet zone: a mark just past the end bar fails, also with the corner a module past the bars");

    // decoded after other histories: a fresh reader, then readers whose learned order puts each step
    // first in turn (the bright whole-image pass then reads it on some); the ROI ends 2 X past the end
    // bar. A label with 1 px of gain: at 2 px zxing also misreads it as another EAN-13 (a separate issue)
    std::mt19937 rng1(148);
    const cv::Mat g1 = drawLinear(ean, X, rng1, q, 1);
    const cr::Models none;
    cr_params p = linearJob(CR_EAN13);
    p.roi_w = xs1 + 2 * X;
    p.roi_h = g1.rows;
    const auto one = [](const cr::Result& r) { return r.codes.size() == 1 ? r.codes[0].quietZoneOk : 9; };
    const cr::Result fresh = cr::decode(g1, p, none);
    bool same = one(fresh) == 1;
    std::set<int> steps;
    if (fresh.codes.size() == 1) steps.insert(fresh.codes[0].step);
    for (int first = 0; first < cr::StepStats::kMax; ++first) {
        cr::StepStats learned;
        for (int s = 0; s < cr::StepStats::kMax; ++s) learned.tries[s] = 100;
        learned.wins[first] = 100;
        learned.cases = 100;
        const cr::Result r = cr::decode(g1, p, none, &learned);
        same = same && one(r) == one(fresh);
        if (r.codes.size() == 1) steps.insert(r.codes[0].step);
    }
    std::string seen;
    for (const int s : steps) seen += " " + std::to_string(s);
    std::printf("     EAN-13 read fresh and after %d other histories: quiet zone %d on every read; read by steps%s\n",
                cr::StepStats::kMax, same ? one(fresh) : -1, seen.c_str());
    check(same && steps.size() > 1, "EAN-13 quiet zone: the same verdict on the same pixels read by different passes after other histories");
}

// The 1D quiet zone of Code 39, Codabar, ITF, Code 93 and Code 128 follows the pixels too: their ends
// are the edges with a bar count the type can have between them, so a corner a module off (ITF's and
// Code 93's start, Codabar's characters: a second bar 2 X in) gives the verdict of corners on the
// bars; a mark touching the end bar widens it past its printed width, and one just past it fails
void testQuietZoneOtherTypes()
{
    std::mt19937 rng(196);
    const int X = 6, q = 12, h = 30 * X;
    const cv::Mat z93 = zintModules(ZXing::BarcodeFormat::Code93, "VS93-AB").row(0).clone();
    const std::vector<uint8_t> code93(z93.begin<uint8_t>(), z93.end<uint8_t>());
    struct Type { const char* name; int format; std::string text; std::vector<uint8_t> mods; };
    const Type types[] = {
        {"Code 39", CR_CODE39, "VS-39AV", code39Modules("VS-39AV")},
        {"Codabar", CR_CODABAR, "A40156B", codabarModules("A40156B")}, // its stop's last bar wide
        {"Codabar", CR_CODABAR, "B40156D", codabarModules("B40156D")},
        {"ITF", CR_ITF, "15400141288763", itfModules("15400141288763")},
        {"Code 93", CR_CODE93, "VS93-AB", code93},
        {"Code 128", CR_GS1_128, "00376123450000012347", cr::ssccModules("00376123450000012347")},
    };
    const auto qz = [](const cv::Mat& img, const Type& t, float l, float r, float top, float bottom) {
        cr::Code c;
        c.format = t.format;
        c.text = t.text;
        c.lightOnDark = 0;
        const float xs[4] = {l, r, r, l}, ys[4] = {top, top, bottom, bottom};
        std::copy(xs, xs + 4, c.x);
        std::copy(ys, ys + 4, c.y);
        return cr::quietZoneOk(img, c);
    };
    bool same = true, marks = true;
    for (const Type& t : types)
        for (const int gain : {0, 2}) {
            const cv::Mat clear = drawLinear(t.mods, X, rng, q, gain);
            // the bars' outer edges
            const float l = 80.f + q * X - gain / 2, r = 80.f + static_cast<float>(q + t.mods.size()) * X + gain / 2;
            const int ri = static_cast<int>(r), li = static_cast<int>(l);
            cv::Mat touching = clear.clone(), touchingStart = clear.clone(), past = clear.clone();
            // a bar short (a narrow space in the middle closed, as blur closes one) with a 1 X mark 0.5 X
            // past the end bar: the count fits again with the mark for the end bar
            std::vector<uint8_t> closed = t.mods;
            for (size_t i = closed.size() / 2; i + 1 < closed.size(); ++i)
                if (!closed[i] && closed[i - 1] && closed[i + 1]) { closed[i] = 1; break; }
            std::mt19937 crng(196);
            cv::Mat shortMark = drawLinear(closed, X, crng, q, gain);
            shortMark(cv::Rect(ri + X / 2, 60, X, h)).setTo(35);
            touching(cv::Rect(ri - 1, 60 + h / 3, X + 1, h / 3)).setTo(35);       // 1 X wide, on the end bar
            touchingStart(cv::Rect(li - X, 60 + h / 3, X + 1, h / 3)).setTo(35);  // 1 X wide, on the start bar
            past(cv::Rect(ri + X / 2, 60 + h / 3, 5, h / 3)).setTo(35);           // 0.8 X wide, 0.5 X past
            std::string line;
            for (const float d : {0.f, -0.45f, 0.45f, -1.f, 1.f})
                for (const float e : {0.f, -1.f, 1.f}) {
                    const float cl = l - d * X, cr = r + e * X, top = 64.f, bottom = 84.f;
                    const int a = qz(clear, t, cl, cr, top, bottom), b = qz(touching, t, cl, cr, top, bottom),
                              s0 = qz(touchingStart, t, cl, cr, top, bottom), m = qz(past, t, cl, cr, top, bottom),
                              k = qz(shortMark, t, cl, cr, top, bottom);
                    line += std::to_string(a) + std::to_string(b) + std::to_string(s0) + std::to_string(m) + std::to_string(k) + " ";
                    same = same && a == 1;
                    marks = marks && b == 0 && s0 == 0 && m == 0 && k == 0;
                }
            std::printf("     %s quiet zone, gain %d px, corners 0 to 1 X off (clear, touching end, touching start, 0.5 X past, a bar short with a mark past): %s\n",
                        t.name, gain, line.c_str());
        }
    check(same, "1D quiet zone (Code 39, Codabar, ITF, Code 93, Code 128): clear zones pass for corners up to a module short or long, with print gain");
    check(marks, "1D quiet zone (Code 39, Codabar, ITF, Code 93, Code 128): a mark touching an end bar or 0.5 X past it fails for every corner, also on a symbol a bar short");
}

// A wrong read is a line-safety failure. An EAN-13 under heavy print gain (X 6 px, 2 px of gain: bars
// 8 px, spaces 4 px) was also read as a second EAN-13 with a valid check digit (0961466152002 for
// 8901396152002, PR #157's test): zxing's bright passes (steps 10 and 11) take its left half's set B
// characters for set A ones. Every EAN / UPC read is now read again on the image after the reference
// decode (edge to similar edge, which print gain does not move): the wrong number is never reported,
// and the other passes still read the printed one
void testEanPrintGainNoMisread()
{
    const std::string want = "8901396152002", misread = "0961466152002";
    const std::vector<uint8_t> ean = ean13Modules(want);
    const int X = 6, q = 12;
    const cr::Models none;
    int wrong = 0, refused = 0;
    std::map<int, std::pair<int, int>> byGain; // gain: decodes that read it, decodes
    for (const int gain : {0, 1, 2, 3})
        for (int seed = 0; seed < 4; ++seed)
            for (const bool roi : {false, true})
                for (int first = -1; first < cr::StepStats::kMax; ++first) {
                    std::mt19937 rng(148 + seed);
                    const cv::Mat g = drawLinear(ean, X, rng, q, gain);
                    cr_params p = linearJob(CR_EAN13);
                    if (roi) {
                        p.roi_w = 80 + (q + 95 + 2) * X;
                        p.roi_h = g.rows;
                    }
                    cr::StepStats learned;
                    if (first >= 0) {
                        for (int s = 0; s < cr::StepStats::kMax; ++s) learned.tries[s] = 100;
                        learned.wins[first] = 100;
                        learned.cases = 100;
                    }
                    const cr::Result r = cr::decode(g, p, none, first >= 0 ? &learned : nullptr);
                    refused += r.refusedEan;
                    bool right = false;
                    for (const auto& c : r.codes) {
                        if (c.text == want) right = true;
                        else ++wrong;
                    }
                    byGain[gain].first += right;
                    byGain[gain].second++;
                }
    // the check itself on the label at 2 px of gain: the printed number holds, the misread does not
    std::mt19937 rng(148);
    const cv::Mat g2 = drawLinear(ean, X, rng, q, 2);
    const auto holds = [&](const std::string& text, float top, float bottom) {
        cr::Code c;
        c.format = CR_EAN13;
        c.text = text;
        c.lightOnDark = 0;
        const float l = 80.f + q * X, r = 80.f + (q + 95) * X, xs[4] = {l, r, r, l}, ys[4] = {top, top, bottom, bottom};
        std::copy(xs, xs + 4, c.x);
        std::copy(ys, ys + 4, c.y);
        return cr::eanReadHolds(g2, c);
    };
    const bool printedHolds = holds(want, 60.f, 240.f) && holds(want, 62.f, 62.f), misreadHolds = holds(misread, 60.f, 240.f) || holds(misread, 62.f, 62.f);
    std::printf("     EAN-13 under print gain, decodes that read it: 0 px %d/%d, 1 px %d/%d, 2 px %d/%d, 3 px %d/%d; wrong reads %d; "
                "on the image: printed number holds %d, misread holds %d\n",
                byGain[0].first, byGain[0].second, byGain[1].first, byGain[1].second, byGain[2].first, byGain[2].second, byGain[3].first,
                byGain[3].second, wrong, printedHolds, misreadHolds);
    check(wrong == 0 && printedHolds && !misreadHolds, "EAN-13 under heavy print gain: the misread never reported, the printed number holds on the image");
    std::printf("     zxing reads refused by the check (Result::refusedEan, cr_stats_ext.refused_ean): %d\n", refused);
    check(refused > 0, "the refused misreads are counted (cr_stats_ext.refused_ean)");
    // the other EAN / UPC types still read with the check: EAN-8, UPC-A and UPC-E (zxing gives these
    // two as their 13-digit GTIN, the reference decode as printed), X 3 and 6 px, 0 and 1 px of gain
    int others = 0, othersRead = 0;
    {
        const cv::Mat upca = zintModules(ZXing::BarcodeFormat::UPCA, "036000291452").row(0).clone(),
                      upce = zintModules(ZXing::BarcodeFormat::UPCE, "01234565").row(0).clone();
        struct Other { int format; std::string text; std::vector<uint8_t> mods; };
        const Other kinds[] = {{CR_EAN8, "96385074", ean8Modules("96385074")},
                               {CR_UPCA, "0036000291452", std::vector<uint8_t>(upca.begin<uint8_t>(), upca.end<uint8_t>())},
                               {CR_UPCE, "0012345000065", std::vector<uint8_t>(upce.begin<uint8_t>(), upce.end<uint8_t>())}};
        for (const Other& k : kinds)
            for (const int x : {3, 6})
                for (const int gain : {0, 1}) {
                    std::mt19937 orng(7);
                    const cr::Result r = cr::decode(drawLinear(k.mods, x, orng, q, gain), linearJob(k.format), none);
                    ++others;
                    othersRead += r.codes.size() == 1 && r.codes[0].text == k.text;
                }
    }
    std::printf("     EAN-8, UPC-A, UPC-E with the check: %d of %d read\n", othersRead, others);
    check(othersRead == others, "EAN-8, UPC-A and UPC-E read with the reference decode check (UPC as its 13-digit GTIN)");
    // 3 px of gain (bars 9 px, spaces 3 px) read before the check neither
    check(byGain[0].first == byGain[0].second && byGain[1].first == byGain[1].second && byGain[2].first == byGain[2].second,
          "EAN-13 under 0, 1 and 2 px of print gain: every decode reads it, the reference decode check in place");
}

// Two cameras inspecting at once: two readers, each with the AI locator, decode on two threads.
// A decode once lowered OpenCV's process-wide thread count and restored it after; with the TBB
// backend that rebuilds the thread arena under the other reader's forward pass and crashed the
// runtime (SIGBUS, about every second run of two parallel camera channels). The test is that this
// process survives, and that decoding leaves OpenCV's thread count alone.
void testTwoReadersAtOnce()
{
#ifdef CR_MODEL_DIR
    const std::string model = std::string(CR_MODEL_DIR) + "/codenet.onnx";
    cr_params p;
    cr_default_params(&p);
    p.format = CR_QR;
    p.expected_count = 1;
    p.locator_mode = CR_LOCATE_AI;
    p.time_budget_ms = 200;
    cr_reader* a = cr_create(&p);
    cr_reader* b = cr_create(&p);
    if (cr_load_model(a, model.c_str()) != 0 || cr_load_model(b, model.c_str()) != 0) {
        std::printf("skip  two readers at once: no locator model at %s\n", model.c_str());
        cr_destroy(a);
        cr_destroy(b);
        return;
    }
    cv::Mat img(480, 640, CV_8U);
    cv::randu(img, 0, 255);
    const int before = cv::getNumThreads();
    std::atomic<int> done{0}, changed{0};
    auto run = [&](cr_reader* r) {
        for (int i = 0; i < 40; ++i) {
            cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step));
            if (cv::getNumThreads() != before) changed++;
            done++;
        }
    };
    std::thread t1(run, a), t2(run, b);
    t1.join();
    t2.join();
    check(done == 80, "two readers decode on two threads at once (two cameras), 40 images each");
    check(changed == 0 && cv::getNumThreads() == before, "decoding leaves OpenCV's thread count alone");
    cr_destroy(a);
    cr_destroy(b);
#endif
}

// The models are encrypted at rest (AES-256-GCM, content key from the licence; LICENSING.md 7.4):
// the host decrypts them into memory and loads from the buffer, so no plaintext model file is on the
// shipped disk. These check the buffer load matches the file load, and that a bad or empty buffer is
// rejected without marking a model loaded (line safety: no reading without the AI model it was told to use).
void testLoadFromBuffer()
{
    // Empty / null buffers are refused for every buffer API, whatever the models on disk.
    {
        cr_params p;
        cr_default_params(&p);
        cr_reader* r = cr_create(&p);
        check(cr_load_model_buffer(r, nullptr, 0) == -1, "cr_load_model_buffer rejects a null buffer");
        const uint8_t one = 0;
        check(cr_load_model_buffer(r, &one, 0) == -1, "cr_load_model_buffer rejects a zero-length buffer");
        check(cr_load_enhancer_buffer(r, nullptr, 0) == -1, "cr_load_enhancer_buffer rejects a null buffer");
        const uint8_t junk[8] = {1, 2, 3, 4, 5, 6, 7, 8};
        check(cr_load_model_buffer(r, junk, sizeof junk) == -1, "cr_load_model_buffer rejects bytes that are not an ONNX model");
        cr_destroy(r);
    }
#ifdef CR_MODEL_DIR
    const std::string model = std::string(CR_MODEL_DIR) + "/codenet.onnx";
    const std::string enh = std::string(CR_MODEL_DIR) + "/enhancer.onnx";
    std::ifstream mf(model, std::ios::binary);
    std::ifstream ef(enh, std::ios::binary);
    if (!mf || !ef) {
        std::printf("skip  load-from-buffer model tests: no models at %s\n", CR_MODEL_DIR);
        return;
    }
    std::vector<uint8_t> mbytes((std::istreambuf_iterator<char>(mf)), std::istreambuf_iterator<char>());
    std::vector<uint8_t> ebytes((std::istreambuf_iterator<char>(ef)), std::istreambuf_iterator<char>());
    check(!mbytes.empty() && !ebytes.empty(), "model bytes read from disk (stand in for the decrypted buffer)");

    cr_params p;
    cr_default_params(&p);
    p.locator_mode = CR_LOCATE_AI;
    cr_reader* r = cr_create(&p);
    check(cr_load_model_buffer(r, mbytes.data(), mbytes.size()) == 0, "codenet loads from a buffer");
    check(cr_load_enhancer_buffer(r, ebytes.data(), ebytes.size()) == 0, "enhancer loads from a buffer");

    // A truncated model buffer must fail and must not leave a half-loaded model behind.
    std::vector<uint8_t> trunc(mbytes.begin(), mbytes.begin() + mbytes.size() / 2);
    cr_reader* r2 = cr_create(&p);
    check(cr_load_model_buffer(r2, trunc.data(), trunc.size()) == -1, "a truncated model buffer is refused");
    cr_destroy(r2);
    cr_destroy(r);
#endif
}

void testCApi()
{
    using namespace cr;
    // cr_params.struct_size: set by cr_default_params; an untagged struct (from before the field,
    // whose first field was the format) is refused, never read as settings
    cr_params p;
    cr_default_params(&p);
    check(p.struct_size == CR_PARAMS_SIZE && (p.struct_size & 0xFFFF) == static_cast<int32_t>(sizeof(cr_params)),
          "cr_default_params sets struct_size to the tag and the size");
    cr_reader* r = cr_create(&p);
    check(r != nullptr, "a tagged cr_params creates a reader");
    cr_params old = p;
    old.struct_size = CR_CODE39; // an old caller's format in the first slot
    check(cr_create(&old) == nullptr, "an untagged cr_params (an older binary's format) is refused");
    old.struct_size = CR_PARAMS_TAG | 8;
    check(cr_create(&old) == nullptr, "a tagged size smaller than the first cr_params with struct_size is refused");
    old.struct_size = 0x400 | 0x8000; // Codabar and a later flag: never a size with the tag
    check(cr_create(&old) == nullptr, "a format value never passes as a size");
    // a newer caller's bigger struct: the fields this engine knows are read, the rest ignored
    {
        std::vector<uint8_t> big(sizeof(cr_params) + 16, 0xAB);
        cr_params mine = p;
        mine.struct_size = static_cast<int32_t>(CR_PARAMS_TAG | big.size());
        mine.format = CR_CODE39;
        mine.check_digit = CR_CHECK_STRIP;
        std::memcpy(big.data(), &mine, sizeof(cr_params));
        cr_reader* rb = cr_create(reinterpret_cast<const cr_params*>(big.data()));
        check(rb != nullptr && cr_set_params(rb, reinterpret_cast<const cr_params*>(big.data())) == 0,
              "a newer caller's larger cr_params is accepted");
        cr_destroy(rb);
    }
    old.struct_size = CR_CODE39;
    check(cr_set_params(r, &old) == -1 && std::strstr(cr_last_error(r), "struct_size") != nullptr,
          "cr_set_params refuses an untagged struct and says why");

    // a Code 39 (its last character a valid check digit: zxing's "]A1") and a GS1-128 SSCC read and graded through the C API: their identifiers, and each scan line
    std::mt19937 rng(128);
    p = linearJob(CR_CODE39 | CR_GS1_128);
    p.grade_quality = 1;
    p.expected_count = 1;
    check(cr_set_params(r, &p) == 0, "cr_set_params takes a tagged struct");
    struct Want { cv::Mat img; const char* id; int gs1; const char* text; };
    for (const Want& w : {Want{drawLinear(code39Modules("VS-39AV"), 3, rng), "]A1", 0, "VS-39AV"},
                          Want{drawLinear(ssccModules("00376123450000012347"), 3, rng), "]C1", 1, "00376123450000012347"}}) {
        const int n = cr_decode(r, w.img.data, w.img.cols, w.img.rows, static_cast<int32_t>(w.img.step));
        cr_code c{};
        cr_code_ext e{};
        e.struct_size = sizeof(cr_code_ext);
        const bool got = n == 1 && cr_get_code(r, 0, &c) == 0 && cr_get_code_ext(r, 0, &e) == 0;
        std::printf("     %s: \"%s\" id \"%s\" gs1 %d, Rmin grade %d, %d scans\n", w.text, got ? c.text : "", e.symbology_id, e.gs1,
                    e.grade_rmin, e.scan_count);
        check(got && std::string(c.text) == w.text && std::string(e.symbology_id) == w.id && e.gs1 == w.gs1,
              std::string("symbology identifier ") + w.id + " and the GS1 flag reported");
        check(e.grade_rmin == 4 && e.scan_count == 10, "a clean 1D code: Rmin graded A over its 10 scans");
        cr_stats_ext sx{};
        sx.struct_size = sizeof(cr_stats_ext);
        check(e.check_digit_ok == (w.gs1 ? 1 : -1) && e.quiet_zone_ok == 1 && e.addon != nullptr && e.addon[0] == '\0' &&
                  (e.angle < 5.f || e.angle > 355.f) && cr_get_stats_ext(r, &sx) == 0 && sx.rejected_check_digit == 0,
              "spec 5.2 outputs through cr_get_code_ext and cr_get_stats_ext: angle, check digit, quiet zone, no add-on");
        bool scansOk = true;
        for (int k = 0; k < e.scan_count; ++k) {
            cr_scan s{};
            s.struct_size = sizeof(cr_scan);
            scansOk = scansOk && cr_get_scan(r, 0, k, &s) == 0 && s.quiet_zone_ok == 1 && s.grade_quiet_zone == 4 && s.grade_rmin == 4 &&
                      s.r_min < 0.5f * s.r_max && s.grade >= 0 && s.grade <= std::min(s.grade_rmin, s.grade_quiet_zone) && s.struct_size == static_cast<int32_t>(sizeof(cr_scan));
        }
        cr_scan none{};
        none.struct_size = sizeof(cr_scan);
        check(scansOk && cr_get_scan(r, 0, e.scan_count, &none) == -1 && cr_get_scan(r, 1, 0, &none) == -1,
              "each scan line reported with its Rmin and quiet-zone results; bad indexes refused");
        // an older caller's smaller struct: only what its size covers is written
        unsigned char small[12];
        std::memset(small, 0xCD, sizeof small);
        const int32_t eight = 8;
        std::memcpy(small, &eight, 4);
        check(cr_get_code_ext(r, 0, reinterpret_cast<cr_code_ext*>(small)) == 0 && std::memcmp(small + 4, w.id, 3) == 0 && small[8] == 0xCD,
              "cr_get_code_ext writes no more than the caller's struct_size");
        cr_code_ext zero{};
        check(cr_get_code_ext(r, 0, &zero) == -1, "a struct_size under 4 is refused");
    }
    cr_destroy(r);

    // 2D grades through cr_get_code_ext: PDF417's codeword yield and its grade (its other grades in the
    // 1D fields of cr_quality), and the module grid an Aztec was graded on, for the report's cell map
    {
        cr_params p2 = linearJob(CR_PDF417 | CR_AZTEC);
        p2.grade_quality = 1;
        cr_reader* r2 = cr_create(&p2);
        struct Sym { ZXing::BarcodeFormat f; int format; const char* text; int grid; };
        for (const Sym& w : {Sym{ZXing::BarcodeFormat::PDF417, CR_PDF417, "VS-PDF417 0109506000134352", 0},
                             Sym{ZXing::BarcodeFormat::Aztec, CR_AZTEC, "VS-AZTEC 0109506000134352", -1}}) {
            const auto bc = ZXing::CreateBarcodeFromText(w.text, ZXing::CreatorOptions(w.f));
            const auto zimg = ZXing::WriteBarcodeToImage(bc, ZXing::WriterOptions().scale(5).addQuietZones(false));
            cv::Mat sym(zimg.height(), zimg.width(), CV_8U, const_cast<uint8_t*>(zimg.data()), zimg.rowStride()), img;
            cv::copyMakeBorder(sym, img, 60, 60, 60, 60, cv::BORDER_CONSTANT, cv::Scalar(255));
            cv::GaussianBlur(img, img, cv::Size(3, 3), 0.8);
            const int n = cr_decode(r2, img.data, img.cols, img.rows, static_cast<int32_t>(img.step));
            cr_code c{};
            cr_quality q{};
            cr_code_ext e{};
            e.struct_size = sizeof(cr_code_ext);
            const bool got = n == 1 && cr_get_code(r2, 0, &c) == 0 && cr_get_code_ext(r2, 0, &e) == 0 && cr_get_quality(r2, 0, &q) == 0;
            std::printf("     %s: read %d, codeword yield %.2f (%d), grid %d x %d, overall %d\n", w.text, got, e.codeword_yield,
                        e.grade_codeword_yield, e.grid_rows, e.grid_cols, q.overall);
            if (w.format == CR_PDF417)
                check(got && c.format == CR_PDF417 && q.linear == 1 && e.codeword_yield > 0.9f && e.grade_codeword_yield == 4 &&
                          e.grid_rows == 0 && e.grid_cols == 0 && q.overall <= e.grade_codeword_yield,
                      "PDF417 through the C API: codeword yield and its grade in cr_code_ext, no module grid");
            else
                check(got && c.format == CR_AZTEC && q.linear == 0 && e.codeword_yield == -1.f && e.grade_codeword_yield == -1 &&
                          e.grid_rows == zimg.height() / 5 && e.grid_cols == zimg.width() / 5,
                      "Aztec through the C API: its module grid (rows and columns) for the cell map, no codeword yield");
        }
        // an older caller's struct (up to addon) is written only that far
        std::vector<unsigned char> old(sizeof(cr_code_ext), 0xCD);
        const int32_t upToAddon = static_cast<int32_t>(offsetof(cr_code_ext, codeword_yield));
        std::memcpy(old.data(), &upToAddon, 4);
        check(cr_get_code_ext(r2, 0, reinterpret_cast<cr_code_ext*>(old.data())) == 0 && old[static_cast<size_t>(upToAddon)] == 0xCD,
              "cr_get_code_ext: a caller from before the 2D fields gets none of them written");
        cr_destroy(r2);
    }

    // the quiet zones graded per scan reach the symbol: a Code 39 with 9 X quiet zones
    {
        const cv::Mat img = drawLinear(code39Modules("VS-39AV"), 8, rng, 9, 1);
        Code c;
        c.text = "VS-39AV";
        c.format = CR_CODE39;
        const float w = float((code39Modules("VS-39AV").size() + 18) * 8);
        const float xs[4] = {80, 80 + w, 80 + w, 80}, ys[4] = {60, 60, 300, 300};
        for (int i = 0; i < 4; ++i) { c.x[i] = xs[i]; c.y[i] = ys[i]; }
        const Quality q = gradeLinear(img, c);
        const bool allShort = std::all_of(q.scanLines.begin(), q.scanLines.end(), [](const cr_scan& s) { return s.quiet_zone_ok == 0 && s.grade_quiet_zone == 0; });
        check(q.overall == 0 && q.gradeQZ == 0 && q.gradeRmin == 4 && q.scanLines.size() == 10 && allShort,
              "short quiet zones: the quiet-zone grade F names the failure, Rmin stays A");
    }

#ifdef CR_HAVE_DMTX
    // libdmtx: a GS1 Data Matrix keeps its FNC1 separators as GS and is reported "]d2"; a plain one "]d1"
    for (const bool gs1 : {true, false}) {
        const std::string data = std::string(gs1 ? "\x1d" : "") + "0109506000134352\x1d" "10AB12";
        DmtxEncode* enc = dmtxEncodeCreate();
        dmtxEncodeSetProp(enc, DmtxPropFnc1, 29);
        dmtxEncodeSetProp(enc, DmtxPropScheme, DmtxSchemeAscii);
        std::string text, id;
        cv::Point2f q[4];
        bool read = false;
        if (dmtxEncodeDataMatrix(enc, static_cast<int>(data.size()), reinterpret_cast<unsigned char*>(const_cast<char*>(data.data()))) == DmtxPass) {
            const int w = dmtxImageGetProp(enc->image, DmtxPropWidth), h = dmtxImageGetProp(enc->image, DmtxPropHeight);
            cv::Mat rgb(h, w, CV_8UC3, enc->image->pxl), grey;
            cv::cvtColor(rgb, grey, cv::COLOR_RGB2GRAY);
            read = readDmtx(grey, 2000, text, q, &id);
        }
        dmtxEncodeDestroy(&enc);
        check(read && text == "0109506000134352\x1d" "10AB12" && id == (gs1 ? "]d2" : "]d1"),
              gs1 ? "libdmtx: GS1 Data Matrix read as ]d2 with its separator kept" : "libdmtx: a Data Matrix without FNC1 first read as ]d1");
    }
#endif
}

// ---- The engine's own decoders report the symbology identifier of their final zxing decode (OwnReadScope)
void testOwnReadIds()
{
    using namespace cr;
    // the scope: a text decoded in it takes its identifier; one not decoded there (a pattern match) stays
    // unknown; one decoded with and without FNC1 is unknown; a decode reaches every open scope
    {
        OwnReadScope outer;
        Code a, b, c;
        {
            OwnReadScope inner;
            OwnReadScope::note("0109506000134352", "]d2", 1);
            OwnReadScope::note("SAME", "]Q3", 1);
            OwnReadScope::note("SAME", "]Q1", 0);
            a.text = "0109506000134352";
            b.text = "MATCHED";
            c.text = "SAME";
            inner.apply(a);
            inner.apply(b);
            inner.apply(c);
        }
        Code d;
        d.text = "0109506000134352";
        outer.apply(d);
        check(a.symbologyId == "]d2" && a.gs1 == 1 && b.symbologyId.empty() && b.gs1 == -1 && c.symbologyId.empty() && c.gs1 == -1 &&
                  d.symbologyId == "]d2",
              "own decoders: identifier by text; not decoded or decoded two ways = unknown; outer scopes see inner decodes");
        Code z;
        z.text = "0109506000134352";
        z.symbologyId = "]d1";
        z.gs1 = 0;
        outer.apply(z);
        check(z.symbologyId == "]d1" && z.gs1 == 0, "a code that already has an identifier keeps it");
    }
    OwnReadScope::note("NOSCOPE", "]Q3", 1); // no scope open: nothing to note, nothing breaks

#ifdef CR_HAVE_DMTX
    // the Data Matrix grid decode the lattice, soft and known-content decoders end with: GS1 = ]d2, plain = ]d1
    for (const bool gs1 : {true, false}) {
        const std::string data = std::string(gs1 ? "\x1d" : "") + "0109506000134352\x1d" "10AB12";
        DmtxEncode* enc = dmtxEncodeCreate();
        dmtxEncodeSetProp(enc, DmtxPropFnc1, 29);
        dmtxEncodeSetProp(enc, DmtxPropScheme, DmtxSchemeAscii);
        dmtxEncodeSetProp(enc, DmtxPropModuleSize, 5);
        dmtxEncodeSetProp(enc, DmtxPropMarginSize, 10);
        Code c;
        if (dmtxEncodeDataMatrix(enc, static_cast<int>(data.size()), reinterpret_cast<unsigned char*>(const_cast<char*>(data.data()))) == DmtxPass) {
            const int w = dmtxImageGetProp(enc->image, DmtxPropWidth), h = dmtxImageGetProp(enc->image, DmtxPropHeight);
            cv::Mat rgb(h, w, CV_8UC3, enc->image->pxl), grey;
            cv::cvtColor(rgb, grey, cv::COLOR_RGB2GRAY);
            const int n = (w - 20) / 5;
            cv::Mat dark(n, n, CV_8U);
            for (int y = 0; y < n; ++y)
                for (int x = 0; x < n; ++x) dark.at<uint8_t>(y, x) = grey.at<uint8_t>(10 + y * 5 + 2, 10 + x * 5 + 2) < 128 ? 1 : 0;
            // canonical orientation: the solid L along the left and the bottom
            if (cv::countNonZero(dark.row(0)) == n) cv::flip(dark, dark, 0);
            OwnReadScope ids;
            c.text = decodeDmBits(dark);
            ids.apply(c);
        }
        dmtxEncodeDestroy(&enc);
        std::printf("     own Data Matrix decode: \"%s\" id \"%s\" gs1 %d\n", c.text.c_str(), c.symbologyId.c_str(), c.gs1);
        check(c.text == "0109506000134352\x1d" "10AB12" && c.symbologyId == (gs1 ? "]d2" : "]d1") && c.gs1 == (gs1 ? 1 : 0),
              gs1 ? "own Data Matrix decode: GS1 reported ]d2" : "own Data Matrix decode: no FNC1 first reported ]d1");
    }
#endif
}

// ---- A Data Matrix read by the serial-run pattern match gets its identifier from a verifying grid
// decode of its own modules (decodeDmAt); a match that decode does not confirm keeps none
void testMatchedDmIds()
{
    using namespace cr;
    constexpr int n = 14, px = 8, qz = 4;
    const std::string base = "10A";
    std::vector<std::string> texts;
    for (int k = 120; k < 126; ++k) texts.push_back(base + std::to_string(k));
    const std::string truth = texts[3];
    // the code drawn on a label with print growth, blurred; `flip` modules damaged
    const auto draw = [&](const std::vector<uint8_t>& dark, int flip) {
        std::vector<uint8_t> d = dark;
        std::mt19937 rng(11);
        std::uniform_int_distribution<int> at(1, n - 2);
        for (int i = 0; i < flip; ++i) {
            const size_t k = static_cast<size_t>(at(rng)) * n + static_cast<size_t>(at(rng));
            d[k] = d[k] ? 0 : 1;
        }
        cv::Mat img((n + 2 * qz) * px, (n + 2 * qz) * px, CV_8U, cv::Scalar(215));
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
                if (d[static_cast<size_t>(y) * n + x]) img(cv::Rect((x + qz) * px - 2, (y + qz) * px - 2, px + 4, px + 4)).setTo(35);
        cv::Mat out;
        cv::GaussianBlur(img, out, cv::Size(), 1.0);
        return out;
    };
    for (const bool gs1 : {true, false}) {
        std::vector<uint8_t> dark;
        check(encodeDm(truth, n, dark, gs1), "encode a GS1-style text as Data Matrix");
        Code learnFrom;
        learnFrom.text = truth;
        learnFrom.format = CR_DATAMATRIX;
        learnFrom.modW = learnFrom.modH = n;
        learnFrom.modules = dark;
        MatchCandidates mc;
        mc.encoding = learnDmEncoding(learnFrom);
        check(mc.encoding.valid() && mc.encoding.kind == (gs1 ? 1 : 0),
              gs1 ? "Data Matrix encoding learned: GS1 (FNC1 first)" : "Data Matrix encoding learned: plain ASCII");
        mc.texts = texts;
        // damaged past what Reed-Solomon corrects (8 modules) but still matched: the GS1 symbol
        for (const int flip : {0, 8}) {
            if (flip > 0 && !gs1) continue;
            const cv::Mat img = draw(dark, flip);
            std::string text;
            cv::Point2f q[4];
            float score = 0.f;
            const bool matched = matchDm(img, static_cast<float>(n * px), mc, vs_test_ms(500), text, q, &score);
            Code c;
            c.text = text;
            OwnReadScope ids;
            const std::string real = matched ? decodeDmAt(img, q, n) : std::string();
            ids.apply(c);
            std::printf("     matched DM (%s, %d modules damaged): match %d score %.2f text \"%s\" decode \"%s\" id \"%s\" gs1 %d\n",
                        gs1 ? "FNC1 first" : "no FNC1", flip, matched ? 1 : 0, score, text.c_str(), real.c_str(), c.symbologyId.c_str(), c.gs1);
            if (flip == 0) {
                check(matched && text == truth && real == truth && c.symbologyId == (gs1 ? "]d2" : "]d1") && c.gs1 == (gs1 ? 1 : 0),
                      gs1 ? "matched GS1 Data Matrix: grid decode confirms it, ]d2 with FNC1"
                          : "matched Data Matrix without FNC1: grid decode confirms it, ]d1 (a GS1 check fails it)");
            } else {
                check(matched && text == truth && real.empty() && c.symbologyId.empty() && c.gs1 == -1,
                      "matched GS1 Data Matrix the grid decode cannot confirm: no identifier (a GS1 check fails it)");
            }
        }
    }
    // the decode at known corners: the symbol's own text, nothing from noise
    {
        std::vector<uint8_t> dark;
        encodeDm(texts[0], n, dark, true);
        cv::Mat img((n + 2 * qz) * px, (n + 2 * qz) * px, CV_8U, cv::Scalar(215));
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
                if (dark[static_cast<size_t>(y) * n + x]) img(cv::Rect((x + qz) * px, (y + qz) * px, px, px)).setTo(35);
        // corners of the grid, top left first in the canonical orientation (the L along left and bottom)
        const float a = qz * px, b = (qz + n) * px;
        const cv::Point2f q[4] = {{a, a}, {b, a}, {b, b}, {a, b}};
        check(decodeDmAt(img, q, n) == texts[0], "Data Matrix decode at known corners reads the symbol's own text");
        cv::Mat blank(img.size(), CV_8U, cv::Scalar(215));
        cv::randu(blank, 0, 255);
        check(decodeDmAt(blank, q, n).empty(), "Data Matrix decode at known corners reads nothing from noise");
    }
}

} // namespace

// ---- Pharmacode and DotCode (pharmacode.cpp, dotcode.cpp)

// rotation by `deg` degrees about c (cv::getRotationMatrix2D, which OpenCV 5 moved out of imgproc)
cv::Mat turnAbout(cv::Point2f c, float deg)
{
    const double a = deg * CV_PI / 180, ca = std::cos(a), sa = std::sin(a);
    cv::Mat R(2, 3, CV_64F);
    R.at<double>(0, 0) = ca; R.at<double>(0, 1) = sa; R.at<double>(0, 2) = (1 - ca) * c.x - sa * c.y;
    R.at<double>(1, 0) = -sa; R.at<double>(1, 1) = ca; R.at<double>(1, 2) = sa * c.x + (1 - ca) * c.y;
    return R;
}

// a Pharmacode's bars, left to right (true = wide), from its value
std::vector<bool> pharmaBars(int v)
{
    std::vector<bool> b;
    while (v > 0) {
        if (v % 2 == 0) { b.push_back(true); v = (v - 2) / 2; }
        else { b.push_back(false); v = (v - 1) / 2; }
    }
    std::reverse(b.begin(), b.end());
    return b;
}

// a carton with the bars: u px per unit (narrow 1, wide 3, gap 2), `quiet` units of white each side,
// gain px of ink spread on each bar, turned by `angle` degrees, slight blur and noise; bars of grey `ink`
cv::Mat drawPharma(const std::vector<bool>& bars, float u, std::mt19937& rng, float angle = 0.f, float quiet = 14.f, float gain = 0.f,
                   float ink = 30.f)
{
    float len = 0;
    for (size_t i = 0; i < bars.size(); ++i) len += (bars[i] ? 3.f : 1.f) + (i + 1 < bars.size() ? 2.f : 0.f);
    const int w = static_cast<int>((len + 2 * quiet) * u), h = static_cast<int>(16 * u);
    cv::Mat img(h + 200, w + 200, CV_32F, cv::Scalar(120.f));
    img(cv::Rect(100, 100, w, h)).setTo(225.f);
    float x = 100 + quiet * u;
    for (const bool wide : bars) {
        const float bw = (wide ? 3.f : 1.f) * u;
        cv::rectangle(img, cv::Rect(static_cast<int>(std::lround(x - gain / 2)), 100 + static_cast<int>(2 * u),
                                    static_cast<int>(std::lround(bw + gain)), h - static_cast<int>(4 * u)), cv::Scalar(ink), -1);
        x += bw + 2 * u;
    }
    if (angle != 0.f) {
        const cv::Mat R = turnAbout(cv::Point2f(img.cols / 2.f, img.rows / 2.f), angle);
        cv::warpAffine(img, img, R, img.size(), cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(120.f));
    }
    cv::GaussianBlur(img, img, cv::Size(), 0.8);
    std::normal_distribution<float> nz(0.f, 3.f);
    for (auto it = img.begin<float>(); it != img.end<float>(); ++it) *it += nz(rng);
    cv::Mat g8;
    img.convertTo(g8, CV_8U);
    return g8;
}

// a DotCode grid (1 = dot) printed at `pitch` px, dots of 0.8 pitch, turned by `angle` degrees; dots of
// grey `ink`; the dot at grid index `moved` (-1 none) printed `by` pitches off its place
cv::Mat drawDots(const std::vector<uint8_t>& dots, int w, int h, float pitch, std::mt19937& rng, float angle = 0.f, float ink = 40.f,
                 int moved = -1, cv::Point2f by = {})
{
    const int q = 6;
    cv::Mat img(static_cast<int>((h + 2 * q) * pitch), static_cast<int>((w + 2 * q) * pitch), CV_32F, cv::Scalar(215.f));
    for (int r = 0; r < h; ++r)
        for (int c = 0; c < w; ++c)
            if (dots[static_cast<size_t>(r * w + c)]) {
                const cv::Point2f off = r * w + c == moved ? by : cv::Point2f();
                cv::circle(img, cv::Point(static_cast<int>((c + q + 0.5f + off.x) * pitch * 16), static_cast<int>((r + q + 0.5f + off.y) * pitch * 16)),
                           static_cast<int>(0.4f * pitch * 16), cv::Scalar(ink), -1, cv::LINE_AA, 4);
            }
    if (angle != 0.f) {
        const int side = static_cast<int>(std::hypot(img.cols, img.rows)) + 4;
        cv::Mat big(side, side, CV_32F, cv::Scalar(215.f));
        img.copyTo(big(cv::Rect((side - img.cols) / 2, (side - img.rows) / 2, img.cols, img.rows)));
        const cv::Mat R = turnAbout(cv::Point2f(side / 2.f, side / 2.f), angle);
        cv::warpAffine(big, img, R, big.size(), cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(215.f));
    }
    cv::GaussianBlur(img, img, cv::Size(), 0.7);
    std::normal_distribution<float> nz(0.f, 4.f);
    for (auto it = img.begin<float>(); it != img.end<float>(); ++it) *it += nz(rng);
    cv::Mat g8;
    img.convertTo(g8, CV_8U);
    return g8;
}

std::vector<int> hexWords(const char* s)
{
    std::vector<int> v;
    for (const char* p = s; *p;) {
        char* end = nullptr;
        v.push_back(static_cast<int>(std::strtol(p, &end, 16)));
        if (end == p) break;
        p = end;
    }
    return v;
}

// the specification's DotCode figures as dot grids (filled by testPharmacodeDotCode) and their sizes
std::vector<std::vector<uint8_t>> gFigDots;
std::vector<std::pair<int, int>> gFigSize;

void testPharmacodeDotCode()
{
    const cr::Models none;
    std::mt19937 rng(131070);
    const auto pharmaJob = [](int format, int bars) {
        cr_params p = linearJob(format);
        p.min_length = bars;
        p.max_length = bars;
        return p;
    };
    const auto readValue = [&](const cv::Mat& img, const cr_params& p, int format) { return onlyText(cr::decode(img, p, none), format); };
    // standard and reverse values of a few symbols, upright, tilted and with ink spread
    bool allOk = true;
    for (const int v : {3, 4, 1234, 12345, 65535, 131070, 131069}) {
        const auto bars = pharmaBars(v);
        int rev = 0;
        for (size_t k = 0; k < bars.size(); ++k) rev += (bars[k] ? 2 : 1) << k;
        for (const auto& [angle, gain] : {std::pair<float, float>{0.f, 0.f}, {18.f, 0.f}, {-35.f, 1.f}, {180.f, 0.f}}) {
            const cv::Mat img = drawPharma(bars, 4.f, rng, angle, 14.f, gain);
            const int n = static_cast<int>(bars.size());
            const std::string s = readValue(img, pharmaJob(CR_PHARMACODE, n), CR_PHARMACODE);
            const std::string r = readValue(img, pharmaJob(CR_PHARMACODE_REV, n), CR_PHARMACODE_REV);
            // turned round, the camera sees the reverse reading
            const std::string wantS = std::to_string(angle == 180.f ? rev : v), wantR = std::to_string(angle == 180.f ? v : rev);
            if (s != wantS || r != wantR) {
                std::printf("     Pharmacode %d at %.0f deg, gain %.1f: standard \"%s\" (want %s), reverse \"%s\" (want %s)\n", v, angle, gain,
                            s.c_str(), wantS.c_str(), r.c_str(), wantR.c_str());
                allOk = false;
            }
        }
    }
    check(allOk, "Pharmacode 3 .. 131070 read in both directions, upright, tilted 18 and 35 degrees, with ink spread, turned round");
    // bars lying flat, read down the image (cr_params.pharma_direction): turned clockwise the bar that
    // weighs least is at the bottom (standard), turned counter-clockwise at the top (reverse). Read
    // across, a flat code gives nothing, and read down an upright one gives nothing: never guessed.
    // (Tilted 15-75 degrees a code reads either way, each reading the value its stated direction gives.)
    const auto downJob = [&](int format, int bars) {
        cr_params p = pharmaJob(format, bars);
        p.pharma_direction = CR_PHARMA_DOWN;
        return p;
    };
    bool downOk = true, notGuessed = true;
    for (const int v : {3, 4, 1234, 12345, 131070, 131069}) {
        const auto bars = pharmaBars(v);
        const int n = static_cast<int>(bars.size());
        int rev = 0;
        for (size_t k = 0; k < bars.size(); ++k) rev += (bars[k] ? 2 : 1) << k;
        for (const auto& [tilt, gain] : {std::pair<float, float>{0.f, 0.f}, {18.f, 0.f}, {-35.f, 0.5f}}) {
            const cv::Mat up = drawPharma(bars, 4.f, rng, tilt, 14.f, gain);
            for (const bool clockwise : {true, false}) {
                cv::Mat img;
                cv::rotate(up, img, clockwise ? cv::ROTATE_90_CLOCKWISE : cv::ROTATE_90_COUNTERCLOCKWISE);
                const std::string s = readValue(img, downJob(CR_PHARMACODE, n), CR_PHARMACODE);
                const std::string r = readValue(img, downJob(CR_PHARMACODE_REV, n), CR_PHARMACODE_REV);
                const std::string wantS = std::to_string(clockwise ? v : rev), wantR = std::to_string(clockwise ? rev : v);
                // a code of one bar width, tilted, may give no read (narrow or wide unclear), never another value
                const bool oneWidth = std::all_of(bars.begin(), bars.end(), [&](bool w) { return w == bars[0]; });
                const auto ok = [&](const std::string& got, const std::string& want) { return got == want || (oneWidth && tilt != 0.f && got.empty()); };
                if (!ok(s, wantS) || !ok(r, wantR)) {
                    std::printf("     Pharmacode %d lying flat (%s, tilt %.0f deg, gain %.1f) read down: standard \"%s\" (want %s), reverse \"%s\" (want %s)\n",
                                v, clockwise ? "clockwise" : "counter-clockwise", tilt, gain, s.c_str(), wantS.c_str(), r.c_str(), wantR.c_str());
                    downOk = false;
                }
                const std::string across = readValue(img, pharmaJob(CR_PHARMACODE, n), CR_PHARMACODE) +
                                           readValue(img, pharmaJob(CR_PHARMACODE_REV, n), CR_PHARMACODE_REV);
                if (tilt == 0.f && !across.empty()) {
                    std::printf("     Pharmacode %d lying flat (tilt %.0f deg) read across: \"%s\" (want none)\n", v, tilt, across.c_str());
                    notGuessed = false;
                }
            }
            const std::string down = readValue(up, downJob(CR_PHARMACODE, n), CR_PHARMACODE) +
                                     readValue(up, downJob(CR_PHARMACODE_REV, n), CR_PHARMACODE_REV);
            if (tilt == 0.f && !down.empty()) {
                std::printf("     Pharmacode %d upright (tilt %.0f deg) read down: \"%s\" (want none)\n", v, tilt, down.c_str());
                notGuessed = false;
            }
        }
    }
    check(downOk, "Pharmacode lying flat read down: standard and reverse, turned clockwise and counter-clockwise, tilted 18 and 35 degrees");
    check(notGuessed, "Pharmacode direction never guessed: a flat code read across and an upright code read down give no value");
    {
        // an unknown direction reads nothing (NG); a caller whose cr_params ends before pharma_direction reads across
        const auto bars = pharmaBars(12345);
        const int n = static_cast<int>(bars.size());
        const cv::Mat up = drawPharma(bars, 4.f, rng);
        cr_params bad = pharmaJob(CR_PHARMACODE, n);
        bad.pharma_direction = 2;
        cr_reader* rb = cr_create(&bad);
        const int nb = rb ? cr_decode(rb, up.data, up.cols, up.rows, static_cast<int32_t>(up.step)) : -9;
        cr_destroy(rb);
        cr_params older = downJob(CR_PHARMACODE, n); // the bytes past an older struct are not read
        older.struct_size = CR_PARAMS_TAG | static_cast<int32_t>(offsetof(cr_params, pharma_direction));
        cr_reader* ro = cr_create(&older);
        cr_code c{};
        const bool oldReads = ro && cr_decode(ro, up.data, up.cols, up.rows, static_cast<int32_t>(up.step)) == 1 &&
                              cr_get_code(ro, 0, &c) == 0 && std::string(c.text) == "12345";
        cr_destroy(ro);
        check(nb == 0 && oldReads, "Pharmacode: an unknown reading direction reads nothing; an older cr_params (no direction) reads across");
    }
    {
        const auto bars = pharmaBars(12345);
        const int n = static_cast<int>(bars.size());
        const cv::Mat img = drawPharma(bars, 4.f, rng);
        const std::string unlocked = readValue(img, linearJob(CR_PHARMACODE), CR_PHARMACODE);
        const std::string wrongCount = readValue(img, pharmaJob(CR_PHARMACODE, n + 1), CR_PHARMACODE);
        cr_params range = pharmaJob(CR_PHARMACODE, 0);
        range.min_length = n - 1;
        range.max_length = n + 1;
        const std::string inRange = readValue(img, range, CR_PHARMACODE);
        const std::string teach = onlyText(cr::decode(img, linearJob(CR_ANY), none), CR_PHARMACODE);
        cr_params topOnly = pharmaJob(CR_PHARMACODE, 0);
        topOnly.max_length = 16; // a missing end bar would read as a shorter code
        const std::string halfOpen = readValue(img, topOnly, CR_PHARMACODE);
        std::printf("     Pharmacode 12345: unlocked \"%s\", wrong count \"%s\", top only \"%s\", range \"%s\", teaching \"%s\"\n",
                    unlocked.c_str(), wrongCount.c_str(), halfOpen.c_str(), inRange.c_str(), teach.c_str());
        check(unlocked.empty() && wrongCount.empty() && halfOpen.empty() && inRange == "12345" && teach == "12345",
              "Pharmacode: no read without a closed bar-count lock or outside it; read inside a range lock and when teaching (CR_ANY)");
        // the quiet zone: a mark 2 units left of the first bar
        cv::Mat marked = drawPharma(bars, 4.f, rng);
        cv::rectangle(marked, cv::Rect(100 + 14 * 4 - 3 * 4, 100 + 8, 4, 40), cv::Scalar(30), -1);
        const std::string qz = readValue(marked, pharmaJob(CR_PHARMACODE, n), CR_PHARMACODE);
        cv::Mat tight = drawPharma(bars, 4.f, rng, 0.f, 3.f);
        const std::string qzTight = readValue(tight, pharmaJob(CR_PHARMACODE, n), CR_PHARMACODE);
        // a bar covered by a label: the bars left give another valid value with one bar fewer
        std::vector<bool> fewer(bars.begin(), bars.end() - 1);
        cv::Mat covered = drawPharma(bars, 4.f, rng);
        float x = 100 + 14 * 4;
        for (size_t i = 0; i + 1 < bars.size(); ++i) x += (bars[i] ? 3.f : 1.f) * 4 + 8;
        cv::rectangle(covered, cv::Rect(static_cast<int>(x) - 2, 100, 30, 64), cv::Scalar(225), -1);
        const std::string cov = readValue(covered, pharmaJob(CR_PHARMACODE, n), CR_PHARMACODE);
        // a middle bar gone (a void): the gaps no longer alike
        cv::Mat gone = drawPharma(bars, 4.f, rng);
        float xm = 100 + 14 * 4;
        for (size_t i = 0; i < bars.size() / 2; ++i) xm += (bars[i] ? 3.f : 1.f) * 4 + 8;
        cv::rectangle(gone, cv::Rect(static_cast<int>(xm) - 1, 100, (bars[bars.size() / 2] ? 12 : 4) + 2, 64), cv::Scalar(225), -1);
        cr_params anyCount = pharmaJob(CR_PHARMACODE, 0);
        anyCount.min_length = 2;
        anyCount.max_length = 16;
        const std::string mid = readValue(gone, anyCount, CR_PHARMACODE);
        std::printf("     Pharmacode 12345: mark in the quiet zone \"%s\", 3-unit quiet zone \"%s\", last bar covered \"%s\", middle bar gone \"%s\"\n",
                    qz.c_str(), qzTight.c_str(), cov.c_str(), mid.c_str());
        check(qz.empty() && qzTight.empty() && cov.empty() && mid.empty(),
              "Pharmacode: a mark in the quiet zone, a short quiet zone, a covered end bar and a missing middle bar are not reported");
        // two narrow bars under half a unit of ink spread look like two thin wide bars: no read (past about 0.56 units
        // they read as all wide; the Read code tool's expected value fails that)
        const cv::Mat spread = drawPharma(pharmaBars(3), 4.f, rng, 0.f, 14.f, 2.f);
        const cv::Mat spread16 = drawPharma(pharmaBars(65535), 4.f, rng, 0.f, 14.f, 2.f);
        const std::string s3 = readValue(spread, pharmaJob(CR_PHARMACODE, 2), CR_PHARMACODE);
        const std::string s16 = readValue(spread16, pharmaJob(CR_PHARMACODE, 16), CR_PHARMACODE);
        std::printf("     Pharmacode all narrow under heavy ink spread: 3 -> \"%s\", 65535 -> \"%s\"\n", s3.c_str(), s16.c_str());
        check((s3.empty() || s3 == "3") && (s16.empty() || s16 == "65535"),
              "Pharmacode of one bar width: half a unit of ink spread gives no read (more is caught by the expected value)");
        // bars lying flat: read across (the default), the reading direction would hang on the tilt
        cv::Mat flat;
        cv::rotate(drawPharma(bars, 4.f, rng), flat, cv::ROTATE_90_CLOCKWISE);
        check(readValue(flat, pharmaJob(CR_PHARMACODE, n), CR_PHARMACODE).empty(), "Pharmacode with its bars lying flat (90 degrees) is not read across");
        // other bar codes are not Pharmacodes
        const cv::Mat c39 = drawLinear(code39Modules("VS-39AV"), 3, rng);
        const cv::Mat itf = drawLinear(itfModules("15400141288763"), 3, rng);
        check(readValue(c39, anyCount, CR_PHARMACODE).empty() && readValue(itf, anyCount, CR_PHARMACODE).empty(),
              "a Code 39 and an ITF-14 are not read as Pharmacodes (2-16 bars allowed)");
        check(onlyText(cr::decode(img, pharmaJob(CR_CODE39, n), none), CR_CODE39).empty(), "a Code 39 job does not report a Pharmacode");
    }

    // DotCode: the specification's figures (ISS DotCode Rev 4.0, as zint's tests print them)
    struct Fig { int h, w; const char* dots; };
    static const Fig figs[] = {
        // ISS DotCode Rev 4.0 Figure 1 (left), same: [01]00012345678905[17]201231[10]ABC123456
        {9, 64,
         "1010000000101000101010000010000010001010100010101000101000001010"
         "0100010001010001010001000001010100010100010001000100010101000001"
         "1010001010000000101010100010001010000010101000000010100010100000"
         "0000000101000101010001010100000001000100010001000100010100000001"
         "1000001000000010100010101000001010100000101010100000100010001010"
         "0101000101000001000001010001010100000001000100010101000101010001"
         "1000000010100010001000001000100010101000100010000010101000100000"
         "0001010100010001010100010001010000010001010000000101010001010101"
         "1000100010001000100010100010001010001000101000101000100010000010"},
        // ISS DotCode Rev 4.0 Figure 1 (right) (and Figure 10), same: [01]00012345678905[17]201231[10]ABC123456
        {20, 29,
         "10101000101010100010101000101"
         "00010100010100010100000001010"
         "00001010100010000000101010000"
         "01000001010101010101010101000"
         "10101010100000000010100010101"
         "01000100000000000101010101010"
         "00100010001010101000100000101"
         "00000101010000000001010000010"
         "10001010100010101010001010100"
         "01010000000000010000000100010"
         "00100000000010100000100000000"
         "00010000000101000001000001000"
         "10101000101000001010001010001"
         "01010001010001010101000000010"
         "00001000001010001010100000101"
         "01000100010100000000010100010"
         "10100010000010101000101010001"
         "00010101000001010100010100010"
         "10000010101000100000001000001"
         "01000100010101010000000101010"},
        // ISS DotCode Rev 4.0 Figure 5 (and Figure 6 top-left) when Mask = 0, same: [17]070620[10]ABC123456
        {16, 23,
         "10101000100010000000001"
         "01000101010001010000000"
         "00100010001000101000100"
         "01010001010000000101010"
         "00001000100010100010101"
         "00000101010101010000010"
         "00100010101000000010001"
         "00010100000101000100010"
         "00001000001000001010101"
         "01010101010001000001010"
         "10100000100010001000101"
         "01000100000100010101000"
         "10000010000010100010001"
         "00010000010100010101010"
         "10101000001000101010001"
         "01000001010101010000010"},
        // ISS DotCode Rev 4.0 Figure 6 top-right Mask = 1, same: [17]070620[10]ABC123456
        {16, 23,
         "10000000001010001000101"
         "01010101000100000101000"
         "00100010000000100000001"
         "01010001000001000001000"
         "10101010100000001010101"
         "00000100010100000100010"
         "00000000001010101010001"
         "00010001010001000001000"
         "00101010101000001010001"
         "01000100000001010000000"
         "10101000101000101000001"
         "00010101000100010101010"
         "10001000001010100000101"
         "01010001010001000001010"
         "10000010101010100010101"
         "01000101000101010101010"},
        // ISS DotCode Rev 4.0 Figure 6 bottom-left Mask = 2, same: [17]070620[10]ABC123456
        {16, 23,
         "10100000101010100010001"
         "01000101000100000000010"
         "10101010001010000010000"
         "01010100010000010101010"
         "00001000101000001000101"
         "00000000000001010000010"
         "00100010101010101000001"
         "00010101010100010000000"
         "00001000100010101010001"
         "01000000010101010101000"
         "10100010100000101000101"
         "00000000000101000001010"
         "10000010000010100010101"
         "01010100010100010001010"
         "10101010000000001010001"
         "01010101000001000101010"},
        // ISS DotCode Rev 4.0 Figure 6 bottom-right Mask = 3, same: [17]070620[10]ABC123456
        {16, 23,
         "10000000100000001010101"
         "01010001010100010001000"
         "10001000001010101010100"
         "01010101000101010000010"
         "10101010001000000010101"
         "00000100000100010101000"
         "00001000101010101000101"
         "00000001010000000101010"
         "00100010000000000000001"
         "01010100010101010101010"
         "10000000101010100010001"
         "01010101000001010000010"
         "10101010100000001000001"
         "01000001010001000001010"
         "10001000001010001000001"
         "01010100000101000100010"},
    };
    const std::string gs1a = "01000123456789051720123110ABC123456", gs1b = "1707062010ABC123456";
    bool figsOk = true;
    std::vector<std::vector<uint8_t>> figDots;
    for (size_t k = 0; k < std::size(figs); ++k) {
        const Fig& f = figs[k];
        std::vector<uint8_t> d(static_cast<size_t>(f.w * f.h));
        for (size_t i = 0; i < d.size(); ++i) d[i] = f.dots[i] == '1' ? 1 : 0;
        figDots.push_back(d);
        gFigDots.push_back(d);
        gFigSize.emplace_back(f.w, f.h);
        std::string t;
        const bool ok = cr::decodeDotCodeGrid(d, f.w, f.h, &t, nullptr);
        if (!ok || t != (k < 2 ? gs1a : gs1b)) {
            std::printf("     figure %zu (%d x %d): %s \"%s\"\n", k, f.w, f.h, ok ? "read" : "not read", t.c_str());
            figsOk = false;
        }
    }
    check(figsOk, "DotCode: the specification's figures 1, 5 and 6 (masks 0-3, both foldings) decode to their GS1 data");
    // the codeword sets, after zint's encoder tests (codewords in hex -> the text encoded)
    struct Msg { const char* words; std::string text; };
    const Msg msgs[] = {
        {"6B 64 0C 22 38", "1712345610"},
        {"5A 6A 21 22 23 6B 19 10 41 42 43 6B 19 67 01 17 6A", "90ABC\x1d" "90abc\x1d" "90123"},
        {"6B 63 6A 41 21 3B 5B 10 10 65 40", std::string("99aA[{00\0", 9)},
        {"66 60", "\r\n"},
        {"65 4D 4D 4A", "\r\r\n"},
        {"6A 21 22 23 24 25 69 0C 22 38 4E", "ABCDE12345678"},
        {"65 40 21 22 23 24 6A 0C 22 38 4E 5A 6A", std::string("\0ABCD1234567890", 15)},
        {"6A 41 42 43 44 45 70 31 5A 35 21 5A 5F 02 31", "abcde\x80\x81\x82\x83\x84\xff"},
        {"6E 40 0C 6F 00 22 70 03 10 42 6E 15 16", "\x80" "12\xa0" "34\x81\x82" "56"},
        {"70 13 56 0A 59 2C 67 0C 22", "\x80\x81\x82\x83" "1234"},
        {"70 05 4F 48 6E 46 6A", "\xec\x82" "f"},
        {"6A 61 21", "[)>\x1e" "05\x1d" "A\x1e\x04"},
        {"6A 64 10 11 22 4C 41 48 6A", "[)>\x1e" "01Blah\x04"},
        {"65 41 65 41 42 43 61 60 64", "\x01" "abc\t\r\n\x1e"},
        {"6C 03 66 21", "A"},
    };
    bool msgOk = true;
    for (const auto& m : msgs) {
        std::string t;
        if (!cr::decodeDotCodeMessage(hexWords(m.words), &t, nullptr) || t != m.text) {
            std::printf("     codewords %s: \"%s\"\n", m.words, t.c_str());
            msgOk = false;
        }
    }
    std::string sa;
    const bool structured = cr::decodeDotCodeMessage(hexWords("6B 0C 22 38 4E 5A 65 19 21 6C"), &sa, nullptr) ||
                            cr::decodeDotCodeMessage(hexWords("65 41 42 43 44 6A 12 13 6C"), &sa, nullptr);
    const bool program = cr::decodeDotCodeMessage(hexWords("6D 21"), &sa, nullptr);
    check(msgOk && !structured && !program,
          "DotCode codeword sets A, B, C, binary, shifts, macros and ECI decode; Structured Append parts and reader programming are not reported");

    // in images: the figures and generated symbols at angles; one dot pattern damaged, then too many
    const auto dotJob = [] { return linearJob(CR_DOTCODE); };
    bool imgOk = true;
    for (size_t k = 0; k < figDots.size(); k += 2)
        for (const float angle : {0.f, 90.f, 180.f, 23.f, -61.f}) {
            const std::string t = onlyText(cr::decode(drawDots(figDots[k], figs[k].w, figs[k].h, 6.f, rng, angle), dotJob(), none), CR_DOTCODE);
            if (t != (k < 2 ? gs1a : gs1b)) {
                std::printf("     figure %zu at %.0f deg: \"%s\"\n", k, angle, t.c_str());
                imgOk = false;
            }
        }
    const std::vector<int> abc = hexWords("6A 21 22 23 24 25 69 0C 22 38 4E"); // ABCDE12345678
    for (const auto& [w, h] : {std::pair<int, int>{21, 18}, {18, 21}, {40, 27}})
        for (int mask = 0; mask < 4; ++mask) {
            const auto d = cr::dotcodeSymbol(abc, w, h, mask);
            if (d.empty()) {
                std::printf("     generated %d x %d: the codewords do not fit\n", w, h);
                imgOk = false;
                continue;
            }
            const std::string t = onlyText(cr::decode(drawDots(d, w, h, 5.f + mask, rng, 12.f * mask), dotJob(), none), CR_DOTCODE);
            if (t != "ABCDE12345678") {
                std::printf("     generated %d x %d mask %d: \"%s\"\n", w, h, mask, t.c_str());
                imgOk = false;
            }
        }
    check(imgOk, "DotCode read in images: figures and generated symbols (both foldings, masks 0-3), turned 0-180 degrees, 5-8 px pitch");
    {
        // Figure 1 right (29 x 20): damage the dots of whole codewords, from the 10th codeword on
        const Fig& f = figs[1];
        const std::vector<int> order = cr::dotcodeOrder(f.w, f.h);
        const int d = cr::dotcodeDataLength(f.w, f.h), nc = 3 + d / 2;
        const auto damage = [&](int words) {
            std::vector<uint8_t> g = figDots[1];
            // each damaged word becomes the next value's pattern: always a different, valid word
            for (int k = 0; k < words; ++k) {
                const auto at = [&](int b) -> uint8_t& { return g[static_cast<size_t>(order[static_cast<size_t>(2 + 9 * (10 + 3 * k) + b)])]; };
                int pat = 0;
                for (int b = 0; b < 9; ++b) pat = pat << 1 | at(b);
                int v = 0;
                while (cr::dotcodePattern(v) != pat) ++v;
                const int np = cr::dotcodePattern((v + 1) % 113);
                for (int b = 0; b < 9; ++b) at(b) = static_cast<uint8_t>(np >> (8 - b) & 1);
            }
            return g;
        };
        const int ok = (nc - 2 - nc / 16) / 2; // two check words stay spare (dotcode.cpp)
        std::string t1, t2, t3;
        int fixed = 0;
        const bool r1 = cr::decodeDotCodeGrid(damage(ok), f.w, f.h, &t1, &fixed);
        const bool r2 = cr::decodeDotCodeGrid(damage(ok + 1), f.w, f.h, &t2, nullptr);
        const std::string t3img = onlyText(cr::decode(drawDots(damage(ok), f.w, f.h, 6.f, rng, 31.f), dotJob(), none), CR_DOTCODE);
        std::printf("     DotCode 29 x 20 (%d check words): %d words damaged -> \"%s\" (%d fixed); %d -> %s\n", nc, ok, t1.c_str(), fixed, ok + 1,
                    r2 ? ("\"" + t2 + "\"").c_str() : "not read");
        check(r1 && t1 == gs1a && fixed == ok && t3img == gs1a && (!r2 || t2 == gs1a),
              "DotCode: Reed-Solomon corrects damaged codewords while two check words stay spare; beyond that no wrong text");
        // a random dot field of DotCode size never reads; half the dots of a symbol flipped never reads
        int wrong = 0;
        for (int trial = 0; trial < 200; ++trial) {
            std::vector<uint8_t> g(static_cast<size_t>(f.w * f.h), 0);
            std::uniform_int_distribution<int> coin(0, 1);
            for (int r = 0; r < f.h; ++r)
                for (int c = 0; c < f.w; ++c)
                    if (!((r + c) & 1)) g[static_cast<size_t>(r * f.w + c)] = static_cast<uint8_t>(coin(rng));
            std::string t;
            wrong += cr::decodeDotCodeGrid(g, f.w, f.h, &t, nullptr) ? 1 : 0;
        }
        check(wrong == 0, "DotCode: 200 random dot fields of 29 x 20 give no read");
        const cv::Mat img = drawDots(figDots[0], figs[0].w, figs[0].h, 6.f, rng, 0.f);
        check(onlyText(cr::decode(img, linearJob(CR_QR), none), CR_QR).empty() && onlyText(cr::decode(img, linearJob(CR_ANY), none), CR_DOTCODE) == gs1a,
              "a QR job does not report a DotCode; every type (CR_ANY) reads it as DotCode");
        // corner 0 -> 1 runs along the symbol's top row. A W x H symbol and its quarter turn (H x W, the
        // other folding) carry the same dot stream, so its orientation is known only up to that turn:
        // the reading nearest upright in the image is kept
        const cr::Result up = cr::decode(img, dotJob(), none);
        check(up.codes.size() == 1 && up.codes[0].symbologyId == "]J1" && up.codes[0].gs1 == 1 && up.codes[0].x[1] - up.codes[0].x[0] > 300 && std::abs(up.codes[0].y[1] - up.codes[0].y[0]) < 3 &&
                  up.codes[0].y[3] > up.codes[0].y[0] + 40,
              "DotCode corners: an upright symbol's corner 0 is its top left, corner 1 its top right");
    }
}


// ---- Grading of Pharmacode (grade1d.cpp, as 1D) and DotCode (quality.cpp, as a matrix code)
void testGradePharmaDotCode()
{
    const cr::Models none;
    std::mt19937 rng(4711);
    const auto graded = [](int format, int bars) {
        cr_params p = linearJob(format);
        p.grade_quality = 1;
        if (bars > 0) { p.min_length = bars; p.max_length = bars; }
        return p;
    };
    const auto show = [](const char* what, const cr::Quality& q) {
        std::printf("     %s: measured %d overall %d SC %.0f (%d) MOD %.2f (%d) EC %.0f (%d) DEF %.2f (%d) DEC %.2f (%d) RM %.2f (%d) GN %.2f (%d) UEC %.2f (%d) QZ %d FPD %d scans %d/%d X %.1f\n",
                    what, int(q.measured), q.overall, q.symbolContrast, q.gradeSC, q.modulation, q.gradeMOD, q.edgeContrastMin, q.gradeEC,
                    q.defects, q.gradeDefects, q.decodability, q.gradeDecodability, q.reflectanceMargin, q.gradeRM, q.gridNonUniformity, q.gradeGN,
                    q.unusedErrorCorrection, q.gradeUEC, q.gradeQZ, q.gradeFPD, q.decodedScans, q.scans, q.moduleSizePx);
    };

    // Pharmacode: good symbols (6 px units) grade A in both directions, upright, tilted and turned round
    bool goodOk = true;
    for (const int v : {3, 1234, 12345, 65535, 131070})
        for (const int format : {CR_PHARMACODE, CR_PHARMACODE_REV})
            for (const float angle : {0.f, 15.f, 180.f}) {
                const auto bars = pharmaBars(v);
                const cr::Result r = cr::decode(drawPharma(bars, 6.f, rng, angle), graded(format, static_cast<int>(bars.size())), none);
                const bool ok = r.codes.size() == 1 && r.quality.size() == 1 && r.quality[0].measured && r.quality[0].linear &&
                                r.quality[0].overall == 4 && r.quality[0].decodedScans == 10 && std::abs(r.quality[0].moduleSizePx - 6.f) < 0.5f;
                if (!ok) {
                    std::printf("     Pharmacode %d %s at %.0f deg: %zu codes\n", v, format == CR_PHARMACODE ? "standard" : "reverse", angle, r.codes.size());
                    if (!r.quality.empty()) show("grade", r.quality[0]);
                    goodOk = false;
                }
            }
    check(goodOk, "Pharmacode grading: good symbols (2-16 bars, both directions, upright, 15 and 180 degrees) grade A on 10 decoded scans, X = the narrow bar");
    {
        const auto bars = pharmaBars(12345);
        const int n = static_cast<int>(bars.size());
        const float u = 5.f;
        const cv::Mat clean = drawPharma(bars, u, rng);
        const cr::Result r = cr::decode(clean, graded(CR_PHARMACODE, n), none);
        check(r.codes.size() == 1 && r.quality.size() == 1 && r.quality[0].overall == 4, "Pharmacode 12345 read and graded A");
        if (r.codes.size() == 1) {
            const cr::Code& c = r.codes[0];
            const cr::Quality q0 = r.quality[0];
            // bar k's left edge in the drawing
            const auto barX = [&](size_t k) {
                float x = 100 + 14 * u;
                for (size_t i = 0; i < k; ++i) x += (bars[i] ? 3.f : 1.f) * u + 2 * u;
                return x;
            };
            // a mark in the left quiet zone, 6 units before the first bar (clear of the bars, inside the
            // 8 X): the quiet zones fail every scan
            cv::Mat marked = clean.clone();
            cv::rectangle(marked, cv::Rect(static_cast<int>(barX(0) - 6 * u), 100 + 5, static_cast<int>(u), 70), cv::Scalar(30), -1);
            const cr::Quality qz = cr::gradeLinear(marked, c);
            show("Pharmacode, mark in the quiet zone", qz);
            // faded ink: bars of grey 150 on 225; the carton round the label made as light (else it is
            // the darkest thing on the scan line)
            cv::Mat fadedImg = drawPharma(bars, u, rng, 0.f, 14.f, 0.f, 150.f);
            fadedImg.setTo(225, fadedImg < 135);
            const cr::Quality faded = cr::gradeLinear(fadedImg, c);
            show("Pharmacode, faded", faded);
            // a void through the middle of a wide bar over half its height: the scans across it fail
            size_t wideBar = 0;
            while (!bars[wideBar]) ++wideBar;
            cv::Mat holed = clean.clone();
            cv::rectangle(holed, cv::Rect(static_cast<int>(barX(wideBar)) - 1, 100 + static_cast<int>(2 * u) + 20, static_cast<int>(3 * u) + 2, 25), cv::Scalar(225), -1);
            const cr::Quality hole = cr::gradeLinear(holed, c);
            show("Pharmacode, void across a wide bar", hole);
            // a bar after a narrow bar printed 0.6 units to the right: the narrow bar's pitch grows by
            // 0.3 units, two thirds of its decoder's tolerance (decodability), the decoder still reads it
            size_t narrow = 1;
            while (bars[narrow - 1]) ++narrow;
            cv::Mat moved = clean.clone();
            const int top = 100 + static_cast<int>(2 * u), hgt = static_cast<int>(16 * u) - static_cast<int>(4 * u);
            const int bw = static_cast<int>((bars[narrow] ? 3 : 1) * u);
            cv::rectangle(moved, cv::Rect(static_cast<int>(barX(narrow)) - 1, top - 1, bw + 2, hgt + 2), cv::Scalar(225), -1);
            cv::rectangle(moved, cv::Rect(static_cast<int>(barX(narrow) + 3), top, bw, hgt), cv::Scalar(30), -1);
            const cr::Quality dec = cr::gradeLinear(moved, c);
            show("Pharmacode, a narrow bar 0.6 units off", dec);
            show("Pharmacode, clean", q0);
            check(qz.measured && qz.gradeQZ == 0 && qz.overall == 0, "Pharmacode grading: a mark in the quiet zone grades F (its decoder's 8 X quiet zones)");
            check(faded.measured && faded.gradeSC <= 1 && faded.overall <= 1, "Pharmacode grading: faded bars grade D or F on symbol contrast");
            check(hole.measured && hole.overall < 4 && hole.decodedScans < hole.scans, "Pharmacode grading: a void across a bar fails the scans through it");
            check(dec.measured && q0.decodability > 0.85f && dec.decodability < 0.6f && dec.gradeDecodability < 4 && dec.decodedScans == dec.scans,
                  "Pharmacode grading: a bar off its place lowers decodability (bar + gap pitches) while it still decodes");
            const float gapWide = cr::decodability1d({0, 5, 17, 22}, CR_PHARMACODE, "3"); // 1 - 0.5 / (0.15 x 16.5)
            check(cr::decodability1d({0, 5, 15, 20}, CR_PHARMACODE, "3") > 0.99f && cr::decodability1d({0, 5, 15, 30}, CR_PHARMACODE, "4") > 0.99f &&
                      cr::decodability1d({0, 15, 25, 30}, CR_PHARMACODE_REV, "4") > 0.99f && std::abs(gapWide - 0.798f) < 0.01f &&
                      cr::decodability1d({0, 5, 15, 20}, CR_PHARMACODE, "4") >= 0.f && cr::decodability1d({0, 5, 15, 20}, CR_PHARMACODE, "1234") < 0,
                  "Pharmacode decodability: 3 two narrow bars, 4 narrow-wide standard and wide-narrow reverse; the share of its decoder's 15% left");
            // one width only: 7 (three narrow) with 0.3 units of ink spread has its bars at 0.43 of their
            // pitch, at the decoder's 0.44 limit (more and it reads all wide, 14): no good decodability
            const float seven = cr::decodability1d({0, 10, 30, 40, 60, 70}, CR_PHARMACODE, "7");
            const float sevenSpread = cr::decodability1d({-1.5f, 11.5f, 28.5f, 41.5f, 58.5f, 71.5f}, CR_PHARMACODE, "7");
            const float six = cr::decodability1d({0, 30, 50, 80}, CR_PHARMACODE, "6");
            std::printf("     Pharmacode one width: 7 clean %.2f, 7 with 0.3 units spread %.2f, 6 (two wide) clean %.2f\n", seven, sevenSpread, six);
            check(seven > 0.99f && six > 0.99f && sevenSpread >= 0.f && sevenSpread < 0.1f,
                  "Pharmacode decodability of a one-width code: its bars' width over pitch against the decoder's 0.44 / 0.52 limits (ink spread)");
        }
    }

    // DotCode: the specification's figures grade A, upright and turned; no fixed pattern grade
    const auto dotJob = [] { cr_params p = linearJob(CR_DOTCODE); p.grade_quality = 1; return p; };
    bool dotOk = true;
    for (size_t k = 0; k < gFigDots.size(); ++k)
        for (const float angle : {0.f, 90.f, 23.f}) {
            const auto [w, h] = gFigSize[k];
            const cr::Result r = cr::decode(drawDots(gFigDots[k], w, h, 8.f, rng, angle), dotJob(), none);
            const bool ok = r.codes.size() == 1 && r.quality.size() == 1 && r.quality[0].measured && !r.quality[0].linear && r.quality[0].overall == 4 &&
                            r.quality[0].gradeFPD == -1 && r.quality[0].fixedPatternDamage < 0.f && r.quality[0].unusedErrorCorrection == 1.f && r.quality[0].gradeGN == 4 &&
                            r.codes[0].modW * r.codes[0].modH == w * h;
            if (!ok) {
                std::printf("     DotCode figure %zu at %.0f deg: %zu codes\n", k, angle, r.codes.size());
                if (!r.quality.empty()) show("grade", r.quality[0]);
                dotOk = false;
            }
        }
    check(dotOk, "DotCode grading: the specification's figures at 8 px pitch grade A (0, 90, 23 degrees), UEC 1, dots on their grid, no fixed pattern grade");
    if (gFigDots.size() >= 2) {
        const auto [w, h] = gFigSize[1];
        const std::vector<uint8_t>& fig = gFigDots[1];
        // UEC from the decoder's own blocks: whole codewords damaged, from the 10th on
        const std::vector<int> order = cr::dotcodeOrder(w, h);
        const int d = cr::dotcodeDataLength(w, h), nc = 3 + d / 2, room = nc - 2 - nc / 16;
        const auto damage = [&](int words) {
            std::vector<uint8_t> g = fig;
            for (int k = 0; k < words; ++k) {
                const auto at = [&](int b) -> uint8_t& { return g[static_cast<size_t>(order[static_cast<size_t>(2 + 9 * (10 + 3 * k) + b)])]; };
                int pat = 0;
                for (int b = 0; b < 9; ++b) pat = pat << 1 | at(b);
                int v = 0;
                while (cr::dotcodePattern(v) != pat) ++v;
                const int np = cr::dotcodePattern((v + 1) % 113);
                for (int b = 0; b < 9; ++b) at(b) = static_cast<uint8_t>(np >> (8 - b) & 1);
            }
            return g;
        };
        std::string t;
        float u0 = -1.f, u1 = -1.f, uMax = -1.f;
        cr::decodeDotCodeGrid(fig, w, h, &t, nullptr, nullptr, &u0);
        cr::decodeDotCodeGrid(damage(1), w, h, &t, nullptr, nullptr, &u1);
        cr::decodeDotCodeGrid(damage(room / 2), w, h, &t, nullptr, nullptr, &uMax);
        std::printf("     DotCode 29 x 20 (%d checks, %d usable): UEC clean %.2f, 1 word %.2f, %d words %.2f\n", nc, room, u0, u1, room / 2, uMax);
        check(u0 == 1.f && std::abs(u1 - (1.f - 2.f / static_cast<float>(room))) < 1e-4f && uMax <= 0.f + 1.f / static_cast<float>(room),
              "DotCode UEC: 1 clean, 1 - 2e / (checks - 2 spare) with e words wrong, none left at the limit");
        const cr::Result hurt = cr::decode(drawDots(damage(room / 2), w, h, 8.f, rng), dotJob(), none);
        if (hurt.quality.size() == 1) show("DotCode, codewords damaged to the limit", hurt.quality[0]);
        check(hurt.codes.size() == 1 && hurt.quality.size() == 1 && hurt.quality[0].gradeUEC <= 1 && hurt.quality[0].overall <= 1,
              "DotCode grading: damage to its correction limit grades D or F on unused error correction");

        // on the clean symbol's grid: a mark in the quiet zone, faded dots, a dot off its place
        const cr::Result r = cr::decode(drawDots(fig, w, h, 8.f, rng), dotJob(), none);
        check(r.codes.size() == 1 && r.quality.size() == 1 && r.quality[0].overall == 4, "DotCode 29 x 20 read and graded A");
        if (r.codes.size() == 1) {
            const cr::Code& c = r.codes[0];
            show("DotCode, clean", r.quality[0]);
            cv::Mat marked = drawDots(fig, w, h, 8.f, rng);
            cv::circle(marked, cv::Point(static_cast<int>(6 * 8.f - 1.5f * 8.f), static_cast<int>((6 + h / 2) * 8.f)), 4, cv::Scalar(40), -1);
            const cr::Quality qz = cr::gradeCode(marked, c);
            show("DotCode, mark in the quiet zone", qz);
            const cr::Quality faded = cr::gradeCode(drawDots(fig, w, h, 8.f, rng, 0.f, 165.f), c);
            show("DotCode, faded", faded);
            int someDot = 0;
            while (!fig[static_cast<size_t>(someDot)] || someDot % w < 3 || someDot / w < 3) ++someDot;
            const cr::Quality off = cr::gradeCode(drawDots(fig, w, h, 8.f, rng, 0.f, 40.f, someDot, {0.5f, 0.f}), c);
            show("DotCode, a dot 0.5 pitch off", off);
            const cr::Quality far = cr::gradeCode(drawDots(fig, w, h, 8.f, rng, 0.f, 40.f, someDot, {0.f, 0.7f}), c);
            show("DotCode, a dot 0.7 pitch off", far);
            check(qz.measured && qz.gradeQZ < 4 && qz.overall < 4, "DotCode grading: a mark in the 3 X quiet zone lowers the grade");
            check(faded.measured && faded.gradeSC <= 1 && faded.overall <= 1, "DotCode grading: faded dots grade D or F on symbol contrast");
            check(off.measured && r.quality[0].gridNonUniformity < 0.2f && std::abs(off.gridNonUniformity - 0.5f) < 0.1f && off.gradeGN < 4 &&
                      far.measured && far.gridNonUniformity > 0.6f && far.gradeGN <= 1,
                  "DotCode grading: a dot printed 0.5 or 0.7 pitch off its place measures about that far in grid non-uniformity");
        }
    }
}

// a case of QR codes on a light label, `cols` across, 7 px modules, a slot apart
cv::Mat drawQrCase(const std::vector<std::string>& texts, int cols)
{
    const int cell = 7, pitch = 260;
    const int rows = (static_cast<int>(texts.size()) + cols - 1) / cols;
    cv::Mat img(rows * pitch + 80, cols * pitch + 80, CV_8U, cv::Scalar(70));
    cv::rectangle(img, {20, 20, img.cols - 40, img.rows - 40}, 215, -1);
    for (size_t k = 0; k < texts.size(); ++k) {
        const auto q = qrcodegen::QrCode::encodeText(texts[k].c_str(), qrcodegen::QrCode::Ecc::MEDIUM);
        const int n = q.getSize(), x0 = 70 + static_cast<int>(k % cols) * pitch, y0 = 70 + static_cast<int>(k / cols) * pitch;
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
                if (q.getModule(x, y)) cv::rectangle(img, {x0 + x * cell, y0 + y * cell, cell, cell}, 25, -1);
    }
    cv::Mat noise(img.size(), CV_8U);
    cv::randn(noise, 0, 3);
    cv::add(img, noise, img);
    return img;
}

// An extra pack in the case (line safety: more codes than expected is NG). Decoding stops at the
// expected count, so the extra code was read only when its region came before the last right one;
// it must be read wherever it lies. An extra pack carrying a copy of a code in the case merges
// with that code: copiesOver flags it, and a label that carries its code twice by design does not.
void testExtraPack()
{
    using namespace cr;
    const Models none;
    std::vector<std::string> twelve;
    for (int k = 1; k <= 12; ++k) twelve.push_back("SN" + std::to_string(100000 + k));
    cr_params p = linearJob(CR_QR);
    p.expected_count = 12;
    p.full_pass = 0;
    p.threads = 1; // one region after another: the slot read last is the one the old stop missed
    p.hint_symbols_per_code = 1.f; // the product's label carries its code once
    const Result good = decode(drawQrCase(twelve, 4), p, none);
    std::printf("     12 of 12: %zu codes, copies over %d, unchecked %d, %.0f ms\n", good.codes.size(), good.copiesOver, good.extraUnchecked,
                static_cast<double>(good.stats.total_ms));
    check(good.codes.size() == 12 && good.copiesOver == 0 && good.extraUnchecked == 0, "a full case of 12: 12 codes, no copies over");

    int seen = 0, copies = 0;
    for (size_t at = 0; at <= twelve.size(); ++at) {
        std::vector<std::string> extra = twelve, copy = twelve;
        extra.insert(extra.begin() + static_cast<long>(at), "SN999999");
        copy.insert(copy.begin() + static_cast<long>(at), twelve[(at + 5) % twelve.size()]);
        const Result re = decode(drawQrCase(extra, 4), p, none), rc = decode(drawQrCase(copy, 4), p, none);
        seen += re.codes.size() == 13;
        copies += rc.codes.size() == 12 && rc.copiesOver == 1;
        if (re.codes.size() != 13 || rc.copiesOver != 1)
            std::printf("     extra pack at slot %zu: %zu codes; a copy there: %zu codes, copies over %d\n", at, re.codes.size(), rc.codes.size(), rc.copiesOver);
    }
    std::printf("     extra pack in each of 13 slots: read %d/13; a copy of a code there flagged %d/13\n", seen, copies);
    check(seen == 13, "an extra pack's code is read wherever it lies, also after the expected count");
    check(copies == 13, "an extra pack carrying a copy of a code in the case: copies over 1");

    // labels that carry their code twice (hint 2): 6 packs, 12 symbols, no copies over; a third copy is
    std::vector<std::string> doubled;
    for (int k = 0; k < 6; ++k) { doubled.push_back(twelve[static_cast<size_t>(k)]); doubled.push_back(twelve[static_cast<size_t>(k)]); }
    cr_params two = p;
    two.expected_count = 6;
    two.hint_symbols_per_code = 2.f;
    const Result rd = decode(drawQrCase(doubled, 4), two, none);
    doubled.push_back(twelve[2]);
    const Result rt = decode(drawQrCase(doubled, 4), two, none);
    std::printf("     doubled labels: %zu codes, copies over %d; a third copy: copies over %d\n", rd.codes.size(), rd.copiesOver, rt.copiesOver);
    check(rd.codes.size() == 6 && rd.copiesOver == 0 && rt.copiesOver == 1, "a doubled label is not an extra pack; a third copy of its code is");
    // no hint: judged only on a product learned over 20+ cases to carry each code once; a learned
    // average between one and two (a doubled label whose second print does not always read) or
    // nothing learned leaves copies unjudged, so a good doubled case is never NG
    std::vector<std::string> copied = twelve;
    copied.push_back(twelve[4]);
    cr_params noHint = p;
    noHint.hint_symbols_per_code = 0.f;
    const auto overWith = [&](float spc, uint32_t cases) {
        StepStats l;
        l.cases.store(cases);
        l.symbolsPerCode.store(spc);
        return decode(drawQrCase(copied, 4), noHint, none, &l).copiesOver;
    };
    const int fresh = decode(drawQrCase(copied, 4), noHint, none).copiesOver;
    const int once = overWith(1.f, 25), between = overWith(1.4f, 25), young = overWith(1.f, 5);
    std::printf("     no hint, a copy: nothing learned %d, learned 1.0 %d, learned 1.4 %d, 5 cases %d\n", fresh, once, between, young);
    check(fresh == 0 && once == 1 && between == 0 && young == 0, "without a hint, copies are judged only on a product learned to carry each code once");

#ifdef CR_HAVE_DMTX
    // libdmtx corners in image rows (it counts y up from the bottom): a code near the top stays there,
    // or a re-read of it elsewhere would count as a copy and the region it lies in would stay open
    {
        std::vector<uint8_t> dm;
        check(encodeDm("CORNER-01", 16, dm), "encode 16 x 16 Data Matrix for the corner test");
        cv::Mat img(400, 400, CV_8U, cv::Scalar(230));
        for (int y = 0; y < 16; ++y)
            for (int x = 0; x < 16; ++x)
                if (dm[static_cast<size_t>(y * 16 + x)]) img(cv::Rect(40 + x * 6, 40 + y * 6, 6, 6)).setTo(30);
        std::string text;
        cv::Point2f q[4];
        const bool got = readDmtx(img, 500, text, q);
        float lo = 1e9f, hi = -1e9f;
        for (const auto& pt : q) { lo = std::min(lo, pt.y); hi = std::max(hi, pt.y); }
        std::printf("     libdmtx corners: \"%s\" y %.0f..%.0f (printed at 40..136)\n", text.c_str(), static_cast<double>(lo), static_cast<double>(hi));
        check(got && text == "CORNER-01" && lo > 30.f && hi < 146.f, "libdmtx corners are image rows (top-left origin)");
    }
#endif
}

// Bad input through the C API, found by the fuzzers (tests/fuzz, scripts/fuzz-engine.sh, PR #174). Each
// used to crash, hang or run into undefined behaviour (seen under the sanitizer build, engine/build-asan).
// Engine follow-ups (requirements 17.1, ENG-01..05): refused EAN / UPC reads and add-ons in
// cr_stats_ext; cr_set_lens refuses a lens over the calibration limit; the EAN-2 / EAN-5 add-on read
// again after the reference decode; the AI enhancer with more decode threads than nets; CR_CV_THREADS
// applied once, at the first reader
void testEngineFollowUps()
{
    using namespace cr;
    const cr::Models none;
    // ENG-02: a lens model over 0.5 px, NaN or with no error measured is refused and the lens cleared;
    // a good model, a scale alone and clearing are taken
    {
        cr_params p;
        cr_default_params(&p);
        cr_reader* r = cr_create(&p);
        cr_lens good{1000.f, 1000.f, 320.f, 240.f, -0.1f, 0.01f, 0.f, 0.f, 0.f, 0.05f, 0.3f};
        cr_lens poor = good, nan = good, unmeasured = good, scale{};
        poor.rms_px = 0.8f;
        nan.rms_px = std::numeric_limits<float>::quiet_NaN();
        unmeasured.rms_px = -1.f;
        scale.mm_per_px = 0.05f;
        scale.rms_px = -1.f;
        const int g = cr_set_lens(r, &good), bad = cr_set_lens(r, &poor);
        const std::string err = cr_last_error(r);
        const int n = cr_set_lens(r, &nan), u = cr_set_lens(r, &unmeasured), sc = cr_set_lens(r, &scale), clr = cr_set_lens(r, nullptr);
        good.rms_px = 0.5f;
        const int edge = cr_set_lens(r, &good);
        std::printf("     cr_set_lens: good %d, 0.8 px %d (\"%s\"), NaN %d, unmeasured %d, scale only %d, clear %d, 0.5 px %d\n", g, bad, err.c_str(), n, u,
                    sc, clr, edge);
        check(g == 0 && bad == -1 && !err.empty() && n == -1 && u == -1 && sc == 0 && clr == 0 && edge == 0,
              "cr_set_lens refuses a lens model over the calibration limit (0.5 px), NaN or unmeasured; a scale alone is taken");
        cr_destroy(r);
    }
    // ENG-03: the add-on read again on the image: the printed one holds (also under print gain and
    // turned round), another add-on of the same length never does
    {
        const std::string main = "4006381333931";
        const int X = 4, q = 12;
        int held = 0, tried = 0, wrongHeld = 0;
        for (const std::string& addon : {std::string("52495"), std::string("12"), std::string("07"), std::string("90000")})
            for (const int gain : {0, 1})
                for (const bool turned : {false, true}) {
                    std::mt19937 rng(31 + gain);
                    cv::Mat g = drawLinear(ean13Modules(main, addon), X, rng, q, gain);
                    float l = 80.f + q * X, rgt = 80.f + (q + 95) * X, top = 60.f, bottom = 60.f + 30 * X;
                    if (turned) {
                        cv::rotate(g, g, cv::ROTATE_180);
                        const float W = static_cast<float>(g.cols - 1), H = static_cast<float>(g.rows - 1);
                        const float l2 = W - rgt, r2 = W - l;
                        l = l2; rgt = r2;
                        const float t2 = H - bottom, b2 = H - top;
                        top = t2; bottom = b2;
                    }
                    cr::Code c;
                    c.format = CR_EAN13;
                    c.text = main;
                    c.lightOnDark = 0;
                    const float xs[4] = {l, rgt, rgt, l}, ys[4] = {top, top, bottom, bottom};
                    // turned round, the read's corners run from the right: the add-on lies before them
                    for (int k = 0; k < 4; ++k) {
                        c.x[k] = turned ? xs[(k + 2) % 4] : xs[k];
                        c.y[k] = turned ? ys[(k + 2) % 4] : ys[k];
                    }
                    int h = 0;
                    c.addon = addon;
                    const bool mainHolds = cr::eanReadHolds(g, c, &h);
                    ++tried;
                    held += mainHolds && h == 1;
                    std::string other = addon;
                    other.back() = static_cast<char>('0' + (other.back() - '0' + 1) % 10);
                    c.addon = other;
                    cr::eanReadHolds(g, c, &h);
                    wrongHeld += h;
                    c.addon = addon.substr(0, addon.size() == 5 ? 2 : 1); // a length no add-on has, or EAN-2 for an EAN-5
                    cr::eanReadHolds(g, c, &h);
                    wrongHeld += h;
                }
        std::printf("     add-on read again on the image: printed %d of %d hold; another add-on held %d times\n", held, tried, wrongHeld);
        check(held == tried && wrongHeld == 0, "EAN-2 / EAN-5 add-on: the printed one holds on the image (print gain, turned round), another never");
        // through the decode: the add-on reported, nothing refused; on a label with no add-on none reported
        std::mt19937 rng(5);
        const cr::Result r5 = cr::decode(drawLinear(ean13Modules(main, "52495"), 3, rng), linearJob(CR_EAN13), none);
        check(r5.codes.size() == 1 && r5.codes[0].addon == "52495" && r5.refusedAddon == 0 && r5.refusedEan == 0,
              "a printed EAN-5 add-on is reported through the decode, none refused");
        // the decoder's add-on text replaced by another (as a misread would give it): refused, reported as none
        std::mt19937 rng2(5);
        const cv::Mat g = drawLinear(ean13Modules(main, "52495"), 3, rng2);
        cr::Code c;
        c.format = CR_EAN13;
        c.text = main;
        c.addon = "52495";
        c.lightOnDark = 0;
        const float l = 80.f + 12 * 3, rgt = 80.f + (12 + 95) * 3, xs[4] = {l, rgt, rgt, l}, ys[4] = {60.f, 60.f, 150.f, 150.f};
        std::copy(xs, xs + 4, c.x);
        std::copy(ys, ys + 4, c.y);
        int h1 = 0, h2 = 0;
        cr::eanReadHolds(g, c, &h1);
        c.addon = "52496";
        cr::eanReadHolds(g, c, &h2);
        check(h1 == 1 && h2 == 0, "an add-on other than the printed one does not hold on the image");
    }
    // ENG-01: cr_stats_ext carries refused_ean and refused_addon; an older caller's struct is written
    // only as far as its size
    {
        cr_params p = linearJob(CR_EAN13);
        cr_reader* r = cr_create(&p);
        std::mt19937 rng(5);
        const cv::Mat g = drawLinear(ean13Modules("4006381333931", "12"), 3, rng);
        const int32_t n = cr_decode(r, g.data, g.cols, g.rows, static_cast<int32_t>(g.step));
        cr_stats_ext e{};
        e.struct_size = sizeof(cr_stats_ext);
        e.refused_ean = e.refused_addon = -7;
        const int ok = cr_get_stats_ext(r, &e);
        struct Old { int32_t size, a, b, c, d; int32_t after; } old{};
        old.size = 5 * 4;
        old.after = 0x5A5A;
        cr_get_stats_ext(r, reinterpret_cast<cr_stats_ext*>(&old));
        check(n == 1 && ok == 0 && e.refused_ean == 0 && e.refused_addon == 0 && old.after == 0x5A5A,
              "cr_stats_ext reports refused EAN reads and add-ons (none on a clean label); an older 5-field struct is written only that far");
        cr_destroy(r);
    }
    // ENG-04: more decode threads than enhancer nets: every enhancer call has its net to itself (a
    // data race under the thread sanitizer before); the read is the same as on one thread
#ifdef CR_MODEL_DIR
    {
        cr_params p;
        cr_default_params(&p);
        p.enhancer_mode = CR_ENHANCE_EARLY;
        p.expected_count = 6;
        const std::vector<std::string> texts = {"ENH-0001", "ENH-0002", "ENH-0003", "ENH-0004", "ENH-0005", "ENH-0006"};
        cv::Mat img = drawQrCase(texts, 3);
        cv::GaussianBlur(img, img, cv::Size(), 1.6);
        const auto run = [&](int threads) {
            cr_params pt = p;
            pt.threads = threads;
            cr_reader* r = cr_create(&pt);
            int32_t n = -9;
            if (r && cr_load_enhancer(r, (std::string(CR_MODEL_DIR) + "/enhancer.onnx").c_str()) == 0)
                n = cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step));
            cr_destroy(r);
            return n;
        };
        const int hw = static_cast<int>(std::max(1u, std::thread::hardware_concurrency()));
        const int one = run(1), many = run(3 * hw);
        std::printf("     enhancer early, blurred case: 1 thread %d codes, %d threads (%d nets) %d codes\n", one, 3 * hw, hw, many);
        if (one == -9) std::printf("skip  enhancer threads: no enhancer at %s\n", CR_MODEL_DIR);
        else check(many >= 0 && many == one, "enhancer with more decode threads than nets: same read as one thread");
    }
#endif
}

void testBadApiInput()
{
    using Ms = std::chrono::milliseconds;
    const auto since = [](std::chrono::steady_clock::time_point t0) {
        return std::chrono::duration_cast<Ms>(std::chrono::steady_clock::now() - t0).count();
    };
    // a QR to read, so the decode runs its parallel passes
    cv::Mat img(240, 320, CV_8U, cv::Scalar(220));
    const auto qr = qrcodegen::QrCode::encodeText("FUZZ 0001", qrcodegen::QrCode::Ecc::MEDIUM);
    for (int y = 0; y < qr.getSize(); ++y)
        for (int x = 0; x < qr.getSize(); ++x)
            if (qr.getModule(x, y)) cv::rectangle(img, {60 + 5 * x, 60 + 5 * y, 5, 5}, cv::Scalar(20), cv::FILLED);
    cr_params p;
    {   // threads unbounded: a thread per pass each, thousands of them (and a joinable thread left when
        // creation failed ended the process)
        cr_default_params(&p);
        p.threads = std::numeric_limits<int32_t>::max();
        cr_reader* r = cr_create(&p);
        const auto t0 = std::chrono::steady_clock::now();
        const int32_t n = r ? cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step)) : -9;
        check(r && n == 1 && since(t0) < 5000, "threads = INT_MAX: reads on at most 64 threads (" + std::to_string(since(t0)) + " ms)");
        cr_destroy(r);
    }
    {   // NaN settings (std::clamp passes NaN) and an expected count that overflowed count * 2 + 4
        cr_default_params(&p);
        const float nan = std::numeric_limits<float>::quiet_NaN();
        p.gamma = p.locator_density = p.ai_threshold = p.hint_symbols_per_code = nan;
        p.expected_count = std::numeric_limits<int32_t>::max();
        p.format = CR_ANY;
        cr_reader* r = cr_create(&p);
        check(r && cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step)) == 1,
              "NaN gamma, density, AI threshold, symbols per code and expected count INT_MAX: decodes");
        cr_destroy(r);
    }
    {   // chessboard: cols x rows board points without a bound (minutes, then out of memory); OpenCV's
        // exception on a picture too small for its thresholds went through the C API
        cr_lens out{};
        const uint8_t* views[1] = {img.data};
        const auto t0 = std::chrono::steady_clock::now();
        check(cr_calibrate_chessboard(views, 1, img.cols, img.rows, static_cast<int32_t>(img.step), std::numeric_limits<int32_t>::max(), 7,
                                      10.f, &out) == -1 && since(t0) < 1000,
              "chessboard cols INT_MAX: refused at once");
        check(cr_calibrate_chessboard(views, 1, img.cols, img.rows, static_cast<int32_t>(img.step), 7, 5,
                                      std::numeric_limits<float>::infinity(), &out) == -1,
              "chessboard square side infinite: refused");
        const cv::Mat tiny(3, 3, CV_8U, cv::Scalar(128));
        const uint8_t* tv[1] = {tiny.data};
        check(cr_calibrate_chessboard(tv, 1, 3, 3, 3, 7, 5, 10.f, &out) == -1, "chessboard on a 3 x 3 picture: none found, no exception");
    }
    {   // DPM: a closing disc of any size (a terabyte structuring element), NaN calibration
        cr_default_params(&p);
        cr_reader* r = cr_create(&p);
        cr_dpm_params d;
        cr_default_dpm_params(&d);
        d.enabled = 1;
        d.closing = 1 << 30;
        check(cr_set_dpm(r, &d) == -1, "DPM closing 2^30 px: refused");
        d.closing = 64;
        check(cr_set_dpm(r, &d) == 0, "DPM closing 64 px: accepted");
        d.closing = -5;
        check(cr_set_dpm(r, &d) == 0, "DPM closing negative: accepted as off");
        d.closing = 0;
        d.r_cal = std::numeric_limits<float>::quiet_NaN();
        check(cr_set_dpm(r, &d) == -1, "DPM reflectance NaN: refused");
        cr_destroy(r);
    }
    {   // DotCode: a lattice refit on scattered blobs collapsed, and a dot's grid place (about -2e13)
        // was cast to int (undefined behaviour; the sanitizer build stops on it)
        std::ifstream f(CR_FUZZ_DIR "/regress/dotcode-collapsed-lattice.pgm", std::ios::binary);
        std::string magic;
        int w = 0, h = 0, maxv = 0;
        f >> magic >> w >> h >> maxv;
        f.get();
        cv::Mat dc(h, w, CV_8U);
        f.read(reinterpret_cast<char*>(dc.data), static_cast<std::streamsize>(dc.total()));
        check(magic == "P5" && f.good(), "DotCode fixture loaded (" + std::to_string(w) + " x " + std::to_string(h) + ")");
        cr_default_params(&p);
        p.format = CR_DOTCODE;
        cr_reader* r = cr_create(&p);
        check(cr_decode(r, dc.data, dc.cols, dc.rows, static_cast<int32_t>(dc.step)) == 0, "DotCode on a collapsed lattice: no read");
        cr_destroy(r);
    }
}

// Runtime anti-tamper (integrity.hpp, requirements section 12, owner 2026-11-01). The line-safety
// requirement is the point: once tampering is latched, every decode is NG - never a code, never a
// crash or a hang. We drive the latch with the one-way simulateTamper() hook so the test is
// deterministic (it does not depend on whether a debugger is attached to the test binary), and we
// reset the process-global latch with the CR_TEST_HOOKS-only resetForTest() so other tests are
// unaffected. The real debugger/checksum detectors are exercised by cr_integrity_check() returning a
// valid, consistent state; their boolean outcome depends on the environment, so it is not asserted.
void testAntiTamper()
{
    using namespace cr;
    detail::resetForTest(); // start from a clean, un-latched state

    // A small valid grey image that decodes to nothing (0 codes); the point is the return code, not a
    // read. With no tamper latched, decode must NOT return the tamper code.
    cv::Mat img(64, 64, CV_8U, cv::Scalar(255));
    cr_params p;
    cr_default_params(&p);
    cr_reader* r = cr_create(&p);
    check(r != nullptr, "anti-tamper: reader created");
    check(cr_integrity_state() == CR_TAMPER_NONE, "anti-tamper: state is clean before any detection");
    int n = cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step));
    check(n != CR_DECODE_TAMPER && n >= 0, "anti-tamper: a clean engine decodes normally (not the tamper code)");

    // Simulate an in-memory code patch: every decode must now be NG with the tamper code and no codes.
    detail::simulateTamper(TamperReason::CodePatched);
    check(cr_integrity_state() == CR_TAMPER_CODE_PATCH, "anti-tamper: a code patch latches the state");
    for (int i = 0; i < 3; ++i) {
        n = cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step));
        check(n == CR_DECODE_TAMPER, "anti-tamper: while tampered, decode returns the tamper code (NG)");
        check(cr_code_count(r) == 0, "anti-tamper: while tampered, no code is ever returned (never a pass)");
    }
    check(std::strstr(cr_last_error(r), "tamper") != nullptr, "anti-tamper: the error names tampering for the alarm");

    // decode_frames takes the same gate.
    std::vector<const uint8_t*> frames = {img.data, img.data};
    check(cr_decode_frames(r, frames.data(), 2, img.cols, img.rows, static_cast<int32_t>(img.step)) == CR_DECODE_TAMPER,
          "anti-tamper: decode_frames also fails closed while tampered");

    // The latch is one-way in production; the test hook clears it so the engine decodes again,
    // proving the mechanism leaves a clean engine undamaged.
    detail::resetForTest();
    check(cr_integrity_state() == CR_TAMPER_NONE, "anti-tamper: reset clears the latch (test hook only)");
    n = cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step));
    check(n != CR_DECODE_TAMPER && n >= 0, "anti-tamper: after reset the engine decodes normally again");

    // The debugger reason also fails closed and is reported distinctly.
    detail::simulateTamper(TamperReason::Debugger);
    check(cr_integrity_state() == CR_TAMPER_DEBUGGER &&
              cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step)) == CR_DECODE_TAMPER,
          "anti-tamper: a debugger detection also forces NG");
    detail::resetForTest();

    // The real detectors run and report a valid, self-consistent state (environment decides the value).
    const int32_t checked = cr_integrity_check();
    check((checked == CR_TAMPER_NONE || checked == CR_TAMPER_DEBUGGER || checked == CR_TAMPER_CODE_PATCH) &&
              cr_integrity_state() == checked,
          "anti-tamper: cr_integrity_check runs and agrees with cr_integrity_state");

    cr_destroy(r);
    detail::resetForTest(); // leave the global latch clean for any later test
    std::printf("     anti-tamper: tamper -> NG + alarm, clean -> normal; real check state = %d\n", checked);
}

int main()
{
    using namespace cr;
    // The unit-test binary may be run under lldb/gdb by a developer; turn off the automatic per-decode
    // anti-tamper check so the debugger does not trip the latch for the ordinary decode tests. The
    // dedicated testAntiTamper() drives the latch directly instead.
    detail::setAutoChecks(false);
    const std::string text = "CLUK A 060715180";
    std::vector<uint8_t> dark;
    check(encodeDm(text, 16, dark), "encode 16 x 16 Data Matrix");
    check(decodeDmBits(cv::Mat(16, 16, CV_8U, dark.data()).clone()) == text, "own encoder output decodes (zxing)");

    // Reed-Solomon: the zero word is a codeword; corrupt it and correct it (16 x 16: 12 + 12 codewords)
    std::mt19937 rng(7);
    std::uniform_int_distribution<int> pos(0, 23), val(1, 255);
    {
        std::vector<int> cw(24, 0);
        for (int k : {3, 11, 20, 5, 17, 9}) cw[static_cast<size_t>(k)] = val(rng); // 6 errors = ecc / 2
        int e = -1;
        check(rsDecodeDm(cw, 12, {}, &e) && e == 6 && std::all_of(cw.begin(), cw.end(), [](int v) { return v == 0; }),
              "RS corrects 6 errors with 12 check symbols");
    }
    {
        std::vector<int> cw(24, 0);
        const std::vector<int> erased = {0, 4, 8, 12, 16, 20, 22, 23};
        for (int k : erased) cw[static_cast<size_t>(k)] = val(rng);
        cw[2] = val(rng);
        cw[14] = val(rng);
        int e = -1;
        check(rsDecodeDm(cw, 12, erased, &e) && e == 2 && std::all_of(cw.begin(), cw.end(), [](int v) { return v == 0; }),
              "RS corrects 8 erasures + 2 errors (2e + f = 12)");
    }
    {
        int wrongFixes = 0;
        for (int t = 0; t < 200; ++t) { // 7 errors exceed the capacity: must fail or (rarely) land on another codeword
            std::vector<int> cw(24, 0);
            for (int i = 0; i < 7; ++i) cw[static_cast<size_t>(pos(rng))] = val(rng);
            int e = 0;
            if (rsDecodeDm(cw, 12, {}, &e) && std::any_of(cw.begin(), cw.end(), [](int v) { return v != 0; })) ++wrongFixes;
        }
        check(wrongFixes < 10, "RS beyond capacity rarely miscorrects (kept in reserve by softDecodeDm)");
    }

    // soft decoding: clean, noisy (uncertain modules become erasures), random (must not read)
    check(softDecodeDm(means(dark, 16), 16) == text, "soft decode, clean symbol");
    {
        std::normal_distribution<float> noise(0.f, 40.f);
        int hard = 0, soft = 0, wrong = 0;
        for (int t = 0; t < 60; ++t) {
            cv::Mat m = means(dark, 16);
            for (int i = 0; i < 256; ++i) m.at<float>(i / 16, i % 16) += noise(rng);
            cv::Mat bits = (m < 125) / 255;
            hard += decodeDmBits(bits) == text;
            const std::string s = softDecodeDm(m, 16);
            soft += s == text;
            wrong += !s.empty() && s != text;
        }
        std::printf("     noise sd 40: hard %d/60, soft %d/60, wrong %d\n", hard, soft, wrong);
        check(soft > hard && wrong == 0, "soft decoding reads more under noise, never wrongly");
    }
    {
        std::uniform_real_distribution<float> u(0.f, 255.f);
        int reads = 0;
        for (int t = 0; t < 300; ++t) {
            cv::Mat m(16, 16, CV_32F);
            for (int i = 0; i < 256; ++i) m.at<float>(i / 16, i % 16) = u(rng);
            reads += !softDecodeDm(m, 16).empty();
        }
        check(reads == 0, "soft decoding reads nothing from random grids (300)");
    }
    // QR soft decoding (version 1-M, the shrink-wrap holograms' encoding)
    {
        const qrcodegen::QrCode q = qrcodegen::QrCode::encodeSegments(
            {qrcodegen::QrSegment::makeAlphanumeric("RJ"), qrcodegen::QrSegment::makeNumeric("0187477804")},
            qrcodegen::QrCode::Ecc::MEDIUM, 1, 1, -1, false);
        cv::Mat m(21, 21, CV_32F);
        for (int y = 0; y < 21; ++y)
            for (int x = 0; x < 21; ++x) m.at<float>(y, x) = q.getModule(x, y) ? 50.f : 250.f;
        check(softDecodeQr(m, 1) == "RJ0187477804", "QR soft decode, clean symbol");
        std::normal_distribution<float> noise(0.f, 45.f);
        int hard = 0, soft = 0, wrong = 0;
        for (int t = 0; t < 60; ++t) {
            cv::Mat nm = m.clone();
            for (int i = 0; i < 441; ++i) nm.at<float>(i / 21, i % 21) += noise(rng);
            cv::Mat dark = (nm < 150) / 255;
            std::vector<uint8_t> d(dark.begin<uint8_t>(), dark.end<uint8_t>());
            std::vector<uint8_t> sym(d);
            // hard: zxing on the thresholded grid
            cv::Mat img(29 * 6, 29 * 6, CV_8U, cv::Scalar(255));
            for (int y = 0; y < 21; ++y)
                for (int x = 0; x < 21; ++x)
                    if (sym[static_cast<size_t>(y * 21 + x)]) img(cv::Rect((x + 4) * 6, (y + 4) * 6, 6, 6)).setTo(0);
            ZXing::ReaderOptions o;
            o.setFormats(ZXing::BarcodeFormat::QRCode);
            o.setIsPure(true);
            o.setBinarizer(ZXing::Binarizer::BoolCast);
            hard += ZXing::ReadBarcode(ZXing::ImageView(img.data, img.cols, img.rows, ZXing::ImageFormat::Lum), o).text() == "RJ0187477804";
            const std::string s = softDecodeQr(nm, 1);
            soft += s == "RJ0187477804";
            wrong += !s.empty() && s != "RJ0187477804";
        }
        std::printf("     QR noise sd 45: hard %d/60, soft %d/60, wrong %d\n", hard, soft, wrong);
        check(soft > hard && wrong == 0, "QR soft decoding reads more under noise, never wrongly");
        std::uniform_real_distribution<float> u(0.f, 255.f);
        int reads = 0;
        for (int t = 0; t < 300; ++t) {
            cv::Mat r(21, 21, CV_32F);
            for (int i = 0; i < 441; ++i) r.at<float>(i / 21, i % 21) = u(rng);
            reads += !softDecodeQr(r, 1).empty();
        }
        check(reads == 0, "QR soft decoding reads nothing from random grids (300)");
    }
    // known-content QR decoding: a version 3-Q GS1-style label (numeric, GS byte, numeric serial)
    // printed with heavy ink spread, as on the owner's cans
    {
        const std::string prefix = "01089022460170691125031110000305";
        const auto label = [&](const std::string& pre, const std::string& serial, std::vector<std::string>* textOut) {
            const std::vector<qrcodegen::QrSegment> segs = {qrcodegen::QrSegment::makeNumeric(pre.c_str()),
                                                           qrcodegen::QrSegment::makeBytes({0x1d}),
                                                           qrcodegen::QrSegment::makeNumeric(("21" + serial).c_str())};
            const auto q = qrcodegen::QrCode::encodeSegments(segs, qrcodegen::QrCode::Ecc::QUARTILE, 3, 3, -1, false);
            if (textOut) textOut->push_back(pre + "\x1d" + "21" + serial);
            constexpr int px = 4, qz = 4, n = 29;
            cv::Mat img((n + 2 * qz) * px, (n + 2 * qz) * px, CV_8U, cv::Scalar(255));
            for (int y = 0; y < n; ++y)
                for (int x = 0; x < n; ++x)
                    if (q.getModule(x, y)) img(cv::Rect((x + qz) * px, (y + qz) * px, px, px)).setTo(0);
            return img;
        };
        std::uniform_int_distribution<int> digit(0, 9);
        const auto serial = [&]() {
            std::string s;
            for (int i = 0; i < 9; ++i) s += static_cast<char>('0' + digit(rng));
            return s;
        };
        std::vector<std::string> readTexts, otherProduct;
        for (int i = 0; i < 6; ++i) label(prefix, serial(), &readTexts);
        for (int i = 0; i < 6; ++i) label("01089022460999991125031110000305", serial(), &otherProduct);
        std::normal_distribution<float> noise(0.f, 10.f);
        const cv::Point2f quad[4] = {{16, 16}, {132, 16}, {132, 132}, {16, 132}};
        const cv::Point2f turned[4] = {quad[1], quad[2], quad[3], quad[0]};
        int hard = 0, known = 0, wrong = 0, otherWrong = 0;
        for (int t = 0; t < 20; ++t) {
            std::vector<std::string> truth;
            const cv::Mat clean = label(prefix, serial(), &truth);
            // ink spread: dark grows by a quarter module each side, then lens blur, noise, low contrast
            cv::Mat spread, f;
            cv::erode(clean, spread, cv::getStructuringElement(cv::MORPH_RECT, cv::Size(3, 3)));
            spread.convertTo(f, CV_32F, 80.0 / 255.0, 10.0);
            cv::GaussianBlur(f, f, cv::Size(), 1.3);
            for (auto it = f.begin<float>(); it != f.end<float>(); ++it) *it += noise(rng);
            cv::Mat g8;
            f.convertTo(g8, CV_8U);
            ZXing::ReaderOptions o;
            o.setFormats(ZXing::BarcodeFormat::QRCode);
            hard += ZXing::ReadBarcode(ZXing::ImageView(g8.data, g8.cols, g8.rows, ZXing::ImageFormat::Lum), o).isValid();
            const auto later = std::chrono::steady_clock::now() + std::chrono::milliseconds(vs_test_ms(2000));
            const std::string k = knownDecodeQr(f, t % 2 ? turned : quad, 29, readTexts, later);
            known += k == truth[0];
            wrong += !k.empty() && k != truth[0];
            const std::string w = knownDecodeQr(f, quad, 29, otherProduct, later);
            otherWrong += !w.empty() && w != truth[0];
        }
        std::printf("     ink-spread labels: zxing %d/20, known-content %d/20, wrong %d, wrong with another product's codes %d\n",
                    hard, known, wrong, otherWrong);
        check(known >= 18 && known > hard && wrong == 0, "known-content QR decoding reads ink-spread labels, never wrongly");
        check(otherWrong == 0, "known-content QR decoding never reads wrongly with another product's codes");
        std::uniform_real_distribution<float> u(0.f, 255.f);
        int reads = 0;
        for (int t = 0; t < 50; ++t) {
            cv::Mat r(148, 148, CV_32F);
            for (auto it = r.begin<float>(); it != r.end<float>(); ++it) *it = u(rng);
            cv::GaussianBlur(r, r, cv::Size(), 1.0);
            reads += !knownDecodeQr(r, quad, 29, readTexts, std::chrono::steady_clock::now() + std::chrono::milliseconds(vs_test_ms(2000))).empty();
        }
        check(reads == 0, "known-content QR decoding reads nothing from random images (50)");
    }
    // known-content Data Matrix decoding: 14 x 14 caps (11-digit serial run), module means blurred
    // into their neighbours and noisy
    {
        std::vector<std::string> readTexts;
        for (int k : {404, 405, 406, 408, 409, 410, 688, 689, 690}) readTexts.push_back("31100017" + std::to_string(k).substr(0, 3));
        readTexts.push_back("31100016688");
        readTexts.push_back("31100016700");
        std::normal_distribution<float> noise(0.f, 15.f);
        std::uniform_int_distribution<int> serial(100, 999);
        int soft = 0, known = 0, wrong = 0;
        for (int t = 0; t < 40; ++t) {
            const std::string truth = "31100017" + std::to_string(serial(rng));
            std::vector<uint8_t> dark;
            encodeDm(truth, 14, dark);
            cv::Mat m(14, 14, CV_32F);
            for (int i = 0; i < 196; ++i) m.at<float>(i / 14, i % 14) = dark[static_cast<size_t>(i)] ? 40.f : 210.f;
            cv::Mat b;
            cv::GaussianBlur(m, b, cv::Size(3, 3), 0.6, 0.6, cv::BORDER_REPLICATE);
            for (int i = 0; i < 196; ++i) b.at<float>(i / 14, i % 14) += noise(rng);
            const std::string s = softDecodeDm(b, 14), k = knownSoftDecodeDm(b, 14, readTexts);
            soft += s == truth;
            known += k == truth;
            wrong += (!k.empty() && k != truth) + (!s.empty() && s != truth);
        }
        std::printf("     blurred caps: soft %d/40, known-content %d/40, wrong %d\n", soft, known, wrong);
        check(known > soft && wrong == 0, "known-content Data Matrix decoding reads more, never wrongly");
        std::uniform_real_distribution<float> u(0.f, 255.f);
        int reads = 0;
        for (int t = 0; t < 300; ++t) {
            cv::Mat r(14, 14, CV_32F);
            for (int i = 0; i < 196; ++i) r.at<float>(i / 14, i % 14) = u(rng);
            reads += !knownSoftDecodeDm(r, 14, readTexts).empty();
        }
        check(reads == 0, "known-content Data Matrix decoding reads nothing from random grids (300)");
    }
    // multi-frame reading: a version 1-M QR at 1.9 px per module, each frame sampled at another
    // sub-pixel phase (the case moved a little), noisy; one frame against three fused
    {
        const char* truth = "RJ0187477812";
        const auto q = qrcodegen::QrCode::encodeSegments(
            {qrcodegen::QrSegment::makeAlphanumeric("RJ"), qrcodegen::QrSegment::makeNumeric("0187477812")},
            qrcodegen::QrCode::Ecc::MEDIUM, 1, 1, -1, false);
        constexpr int hi = 20, qz = 4, n = 21; // 20 px per module when rendered
        cv::Mat big((n + 2 * qz) * hi, (n + 2 * qz) * hi, CV_8U, cv::Scalar(215));
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
                if (q.getModule(x, y)) big(cv::Rect((x + qz) * hi, (y + qz) * hi, hi, hi)).setTo(35);
        std::vector<std::string> known = {"RJ0187477797", "RJ0187477804", "RJ0187477806", "RJ0187477809", "RJ0187477815", "RJ0187477820"};
        std::uniform_real_distribution<float> phase(0.f, float(hi));
        std::normal_distribution<float> noise(0.f, 30.f);
        const double down = 1.9 / hi;
        auto frame = [&](float sd = 30.f) {
            cv::Mat shifted, small, f;
            const cv::Mat M = (cv::Mat_<double>(2, 3) << 1, 0, phase(rng), 0, 1, phase(rng));
            cv::warpAffine(big, shifted, M, big.size(), cv::INTER_LINEAR, cv::BORDER_REPLICATE);
            cv::resize(shifted, small, cv::Size(), down, down, cv::INTER_AREA);
            cv::GaussianBlur(small, small, cv::Size(), 0.5);
            small.convertTo(f, CV_32F);
            for (auto it = f.begin<float>(); it != f.end<float>(); ++it) *it += noise(rng) * sd / 30.f;
            cv::Mat out;
            f.convertTo(out, CV_8U);
            return out;
        };
        int single = 0, fusedRead = 0, wrong = 0;
        for (int t = 0; t < 25; ++t) {
            std::vector<cv::Mat> frames = {frame(), frame(), frame()};
            const std::string s1 = decodeQrFrames({frames[0]}, n, n * 1.9f, known, 2000);
            const std::string s3 = decodeQrFrames(frames, n, n * 1.9f, known, 2000);
            single += s1 == truth;
            fusedRead += s3 == truth;
            wrong += (!s1.empty() && s1 != truth) + (!s3.empty() && s3 != truth);
        }
        std::printf("     1.9 px per module, noise sd 30: one frame %d/25, three frames fused %d/25, wrong %d\n", single, fusedRead, wrong);
        check(fusedRead > single && wrong == 0, "three frames fused read more than one, never wrongly");
        // a burst of varied exposure (1x, 0.6x, 1.6x: the light modules of the last clip) with a
        // hard reflection in a new place in each frame: clipped modules add no evidence, frames
        // count by their own class means and quality
        int bestSingle = 0, fusedBurst = 0, wrongBurst = 0;
        std::uniform_real_distribution<float> where(0.15f, 0.85f);
        auto burstFrame = [&](double gain) {
            cv::Mat f;
            frame(12.f).convertTo(f, CV_32F, gain);
            const cv::Point2f c(where(rng) * f.cols, where(rng) * f.rows);
            const float r = 0.12f * n * 1.9f; // clipped out to about 2 r: a quarter of the code
            for (int y = 0; y < f.rows; ++y)
                for (int x = 0; x < f.cols; ++x) {
                    const float d2 = ((x - c.x) * (x - c.x) + (y - c.y) * (y - c.y)) / (r * r);
                    if (d2 < 9) f.at<float>(y, x) += 400.f * std::exp(-d2 / 2);
                }
            cv::Mat out;
            f.convertTo(out, CV_8U);
            return out;
        };
        int singleReads[3] = {0, 0, 0};
        for (int t = 0; t < 25; ++t) {
            std::vector<cv::Mat> frames = {burstFrame(1.0), burstFrame(0.6), burstFrame(1.6)};
            for (int k = 0; k < 3; ++k) {
                const std::string s1 = decodeQrFrames({frames[static_cast<size_t>(k)]}, n, n * 1.9f, known, 2000);
                singleReads[k] += s1 == truth;
                wrongBurst += !s1.empty() && s1 != truth;
            }
            const std::string s3 = decodeQrFrames(frames, n, n * 1.9f, known, 2000);
            fusedBurst += s3 == truth;
            wrongBurst += !s3.empty() && s3 != truth;
        }
        bestSingle = std::max({singleReads[0], singleReads[1], singleReads[2]});
        std::printf("     exposure 1x / 0.6x / 1.6x with reflections: single frames %d, %d, %d of 25, fused %d, wrong %d\n",
                    singleReads[0], singleReads[1], singleReads[2], fusedBurst, wrongBurst);
        check(fusedBurst > bestSingle && wrongBurst == 0, "varied-exposure burst with reflections: fused reads more than any frame, never wrongly");
        // random bursts: noise, and a real code's crop with its blocks shuffled (finders may
        // survive, the data is noise): nothing may come out
        int randomReads = 0;
        std::uniform_int_distribution<int> lum8(0, 255);
        for (int t = 0; t < 60; ++t) {
            std::vector<cv::Mat> frames;
            for (int k = 0; k < 3; ++k) {
                cv::Mat r = frame();
                if (t % 2) {
                    for (auto it = r.begin<uint8_t>(); it != r.end<uint8_t>(); ++it) *it = static_cast<uint8_t>(lum8(rng));
                } else {
                    const int b = r.cols / 6;
                    std::uniform_int_distribution<int> at(0, r.cols - b - 1);
                    for (int s = 0; s < 60; ++s) {
                        const cv::Rect r1(at(rng), at(rng), b, b), r2(at(rng), at(rng), b, b);
                        const cv::Mat tmp = r(r1).clone();
                        r(r2).copyTo(r(r1));
                        tmp.copyTo(r(r2));
                    }
                }
                frames.push_back(r);
            }
            randomReads += !decodeQrFrames(frames, n, n * 1.9f, known, 2000).empty();
        }
        check(randomReads == 0, "multi-frame fusion reads nothing from random or shuffled bursts (60)");
        // super-resolution from the burst (TOO_SMALL): the same code at 1.0 - 1.2 px per module,
        // each frame at another sub-pixel phase, blurred (sigma 0.5 px) and noisy (sd 12), no
        // known content. Without it the reader has each frame's grid fit (the under-resolved solver
        // included) and the fused modules; with it, the super-resolved region after those
        {
            std::normal_distribution<float> noiseS(0.f, 12.f);
            auto smallFrame = [&](double pxPerModule) {
                cv::Mat shifted, small, f;
                const cv::Mat M = (cv::Mat_<double>(2, 3) << 1, 0, phase(rng), 0, 1, phase(rng));
                cv::warpAffine(big, shifted, M, big.size(), cv::INTER_LINEAR, cv::BORDER_REPLICATE);
                cv::resize(shifted, small, cv::Size(), pxPerModule / hi, pxPerModule / hi, cv::INTER_AREA);
                cv::GaussianBlur(small, small, cv::Size(), 0.5);
                small.convertTo(f, CV_32F);
                for (auto it = f.begin<float>(); it != f.end<float>(); ++it) *it += noiseS(rng);
                cv::Mat out;
                f.convertTo(out, CV_8U);
                return out;
            };
            // registration: a known sub-pixel move is measured to a tenth of a pixel
            {
                const cv::Mat a = smallFrame(1.5);
                cv::Mat b;
                const cv::Mat M = (cv::Mat_<double>(2, 3) << 1, 0, 0.37, 0, 1, -0.61);
                cv::warpAffine(a, b, M, a.size(), cv::INTER_CUBIC, cv::BORDER_REPLICATE);
                std::vector<cv::Point2d> sh;
                const cv::Mat X = superResolve({a, b}, 3, &sh);
                std::printf("     sub-pixel shift measured (%.2f, %.2f) for (0.37, -0.61)\n", sh.size() == 2 ? sh[1].x : 0.0,
                            sh.size() == 2 ? sh[1].y : 0.0);
                check(!X.empty() && X.size() == a.size() * 3 && sh.size() == 2 && std::abs(sh[1].x - 0.37) < 0.1 && std::abs(sh[1].y + 0.61) < 0.1,
                      "super-resolution: sub-pixel registration to a tenth of a pixel, grid 3x finer");
            }
            const std::vector<std::string> none;
            int before = 0, after = 0, wrongSr = 0, total = 0, srRuns = 0;
            double srMs = 0, srMax = 0;
            for (double ppm : {1.2, 1.1, 1.0}) {
                int b0 = 0, a0 = 0;
                for (int t = 0; t < 10; ++t) {
                    std::vector<cv::Mat> frames = {smallFrame(ppm), smallFrame(ppm), smallFrame(ppm)};
                    const float side = static_cast<float>(n * ppm);
                    bool read = false;
                    for (const auto& fr : frames) {
                        std::string s1;
                        cv::Point2f qq[4];
                        if (gridFitQR(fr, n, side, 300, s1, qq) && !s1.empty()) {
                            read = read || s1 == truth;
                            wrongSr += s1 != truth;
                        }
                    }
                    const std::string s3 = decodeQrFrames(frames, n, side, none, 300);
                    wrongSr += !s3.empty() && s3 != truth;
                    read = read || s3 == truth;
                    bool readSr = read;
                    if (!read) {
                        const auto t1 = std::chrono::steady_clock::now();
                        const std::string sr = superResolveQrFrames(frames, n, side, none, 200);
                        const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t1).count();
                        srMs += ms;
                        srMax = std::max(srMax, ms);
                        ++srRuns;
                        wrongSr += !sr.empty() && sr != truth;
                        readSr = sr == truth;
                    }
                    b0 += read;
                    a0 += readSr;
                    ++total;
                }
                std::printf("     %.1f px per module, 3 frames: frames and fused modules %d/10, with super-resolution %d/10\n", ppm, b0, a0);
                before += b0;
                after += a0;
            }
            std::printf("     super-resolution: %d/%d against %d without, wrong %d, %.0f ms per region (max %.0f)\n", after, total, before,
                        wrongSr, srRuns ? srMs / srRuns : 0.0, srMax);
            check(after > before && wrongSr == 0, "super-resolved burst reads TOO_SMALL codes the frames and fused modules do not, never wrongly");
            int srRandom = 0;
            for (int t = 0; t < 40; ++t) {
                std::vector<cv::Mat> frames;
                for (int k = 0; k < 3; ++k) {
                    cv::Mat r = smallFrame(1.4);
                    if (t % 2) {
                        for (auto it = r.begin<uint8_t>(); it != r.end<uint8_t>(); ++it) *it = static_cast<uint8_t>(lum8(rng));
                    } else {
                        const int b = std::max(2, r.cols / 6);
                        std::uniform_int_distribution<int> at(0, r.cols - b - 1);
                        for (int s = 0; s < 60; ++s) {
                            const cv::Rect r1(at(rng), at(rng), b, b), r2(at(rng), at(rng), b, b);
                            const cv::Mat tmp = r(r1).clone();
                            r(r2).copyTo(r(r1));
                            tmp.copyTo(r(r2));
                        }
                    }
                    frames.push_back(r);
                }
                srRandom += !superResolveQrFrames(frames, n, n * 1.4f, known, 200).empty();
            }
            check(srRandom == 0, "super-resolution reads nothing from random or shuffled bursts (40)");
        }
        // evidence decoding: modules with no evidence (clipped in every frame) become erasures;
        // version 1-M has 10 check symbols per block, so 8 erased codewords (two in reserve) correct
        {
            cv::Mat ev(n, n, CV_32F);
            for (int y = 0; y < n; ++y)
                for (int x = 0; x < n; ++x) ev.at<float>(y, x) = q.getModule(x, y) ? 1.f : -1.f;
            check(softDecodeQrEvidence(ev, 1) == truth, "QR evidence decode, clean symbol");
            cv::Mat holes = ev.clone();
            holes(cv::Rect(17, 13, 4, 8)).setTo(0.f); // 32 modules of the bottom-right columns: four codewords
            check(softDecodeQrEvidence(holes, 1) == truth, "QR evidence decode: unknown modules as erasures");
            int randomEv = 0;
            std::normal_distribution<float> g(0.f, 1.f);
            for (int t = 0; t < 300; ++t) {
                cv::Mat r(n, n, CV_32F);
                for (auto it = r.begin<float>(); it != r.end<float>(); ++it) *it = g(rng);
                randomEv += !softDecodeQrEvidence(r, 1).empty();
            }
            check(randomEv == 0, "QR evidence decode reads nothing from random evidence (300)");
        }
        // the frame shift: b is a moved right by 12 and down by 7 pixels
        cv::Mat a(240, 320, CV_8U), b;
        std::uniform_int_distribution<int> lum(0, 255);
        for (auto it = a.begin<uint8_t>(); it != a.end<uint8_t>(); ++it) *it = static_cast<uint8_t>(lum(rng));
        cv::GaussianBlur(a, a, cv::Size(), 3.0);
        const cv::Mat M = (cv::Mat_<double>(2, 3) << 1, 0, 12, 0, 1, 7);
        cv::warpAffine(a, b, M, a.size(), cv::INTER_LINEAR, cv::BORDER_REFLECT);
        const cv::Point2d d = frameShift(a, b);
        std::printf("     frame shift measured (%.1f, %.1f) for (12, 7)\n", d.x, d.y);
        check(std::abs(d.x - 12) < 1.5 && std::abs(d.y - 7) < 1.5, "frame shift: direction and size of the motion");
    }
    // the worker pool: every worker runs the job once with its index, jobs repeat, fewer workers
    // than the pool has, and work done on the workers is visible after runAll returns
    {
        // learned state round trip: what a product's next reader starts from
        StepStats a;
        a.cases.store(120);
        for (int i = 0; i < StepStats::kMax; ++i) { a.tries[i].store(10u + i); a.wins[i].store(i); }
        for (int z = 0; z < 16; ++z) { a.zoneTries[z][3].store(7); a.zoneWins[z][3].store(z % 8); a.zoneRead[z].store(z); a.zoneUnread[z].store(1); }
        a.qrModules.store(25);
        a.dmModules.store(16);
        a.dmSide.store(48.5f);
        a.fullPassMs.store(12.25f);
        a.psfSigmaAll.store(0.33f); a.psfSigma[5].store(0.41f);
        a.polarity.store(2);
        a.encKind.store(2); a.encSplit.store(5); a.encEcl.store(1); a.encModules.store(25); a.encFormat.store(CR_QR);
        a.encConfirmed.store(1);
        a.codeMin.store(40.f); a.codeMax.store(60.f); a.symbolsPerCode.store(2.f);
        const std::string blob = learnedToText(a);
        StepStats b;
        std::string err;
        check(learnedFromText(b, blob + "key product-x v3\n", &err), ("learned state loads (" + err + ")").c_str());
        bool same = b.cases.load() == 0 && b.qrModules.load() == 25 && b.dmModules.load() == 16 && std::abs(b.dmSide.load() - 48.5f) < 1e-3f
                    && std::abs(b.fullPassMs.load() - 12.25f) < 1e-3f && std::abs(b.psfSigmaAll.load() - 0.33f) < 1e-3f && std::abs(b.psfSigma[5].load() - 0.41f) < 1e-3f && b.encKind.load() == 2 && b.encSplit.load() == 5 && b.encModules.load() == 25;
        for (int i = 0; i < StepStats::kMax; ++i) same = same && b.tries[i].load() == 10u + i && b.wins[i].load() == static_cast<uint32_t>(i);
        for (int z = 0; z < 16; ++z) same = same && b.zoneTries[z][3].load() == 7 && b.zoneWins[z][3].load() == static_cast<uint32_t>(z % 8) && b.zoneRead[z].load() == 0 && b.zoneUnread[z].load() == 0;
        check(same, "learned state round trip keeps the tables, sizes, costs and encoding; the zone map and case count start at zero");
        StepStats history;
        history.tries[2].store(1000000); history.wins[2].store(5000); // pruned for months: 0.5%
        history.zoneTries[5][2].store(100000); history.zoneWins[5][2].store(50000);
        StepStats small;
        check(learnedFromText(small, learnedToText(history), &err) && small.tries[2].load() <= 200 && small.tries[2].load() > 100
              && small.wins[2].load() * 1000 / small.tries[2].load() <= 6 && small.zoneTries[5][2].load() <= 60 && small.zoneWins[5][2].load() * 10 / small.zoneTries[5][2].load() >= 4,
              "old history is scaled down to a few hundred tries, win rates kept");
        StepStats badEnc;
        check(learnedFromText(badEnc, "codereader-learned 1\nsteps 24 zones 4\nenc 0 0 1 23 1\n", &err) && badEnc.encKind.load() == -1, "an encoding the matchers cannot render is dropped");
        check(learnedFromText(badEnc, "codereader-learned 1\nsteps 24 zones 4\nenc 0 0 1 16 2\n", &err) && badEnc.encKind.load() == 0 && badEnc.encModules.load() == 16, "a Data Matrix encoding loads");
        check(b.encConfirmed.load() == 0, "a loaded encoding is a hint until a code confirms it");
        check(b.polarity.load() == 2, "the product's polarity (light on dark) is carried");
        check(b.codeMin.load() == 0.f && b.codeMax.load() == 0.f && b.symbolsPerCode.load() == 0.f, "the code size range and symbols per code are not carried");
        StepStats c;
        check(!learnedFromText(c, "garbage\n", &err) && c.cases.load() == 0, "a foreign blob is rejected and changes nothing");
        check(!learnedFromText(c, "codereader-learned 1\nsteps 12 zones 4\n", &err), "a blob of another engine layout is rejected");
        check(!learnedFromText(c, "codereader-learned 2\nsteps 24 zones 4\n", &err), "a blob of another version is rejected");
        check(learnedFromText(c, "codereader-learned 1\nsteps 24 zones 4\nwins 9 9 9 9 9 9 9 9 9 9 9 9 9 9 9 9 9 9 9 9 9 9 9 9\ntries 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4\ndm 15 30\n", &err)
              && c.wins[0].load() == 4 && c.dmModules.load() == 0, "wins clamp to tries; an odd Data Matrix size is dropped");

        // the lens as a kernel: the blur of a code is measured from its known pattern, and a
        // defocused code that module sampling loses is still scored through the measured blur
        {
            const auto qrPattern = [&](const std::string& serial, int mask) {
                const auto q = qrcodegen::QrCode::encodeSegments({qrcodegen::QrSegment::makeAlphanumeric("RSBCL"), qrcodegen::QrSegment::makeNumeric(serial.c_str())},
                                                                 qrcodegen::QrCode::Ecc::MEDIUM, 1, 1, mask, false);
                std::vector<uint8_t> d(21 * 21);
                for (int y = 0; y < 21; ++y)
                    for (int x = 0; x < 21; ++x) d[static_cast<size_t>(y * 21 + x)] = q.getModule(x, y) ? 1 : 0;
                return d;
            };
            constexpr float mpx = 3.6f; // pixels per module, as the bottle images
            const cv::Point2f quad[4] = {{30, 30}, {30 + 21 * mpx, 30}, {30 + 21 * mpx, 30 + 21 * mpx}, {30, 30 + 21 * mpx}};
            const auto picture = [&](const std::vector<uint8_t>& pat, float sigmaMod) {
                cv::Mat hi(21 * 8, 21 * 8, CV_32F, cv::Scalar(0.f));
                for (int y = 0; y < 21; ++y)
                    for (int x = 0; x < 21; ++x)
                        if (pat[static_cast<size_t>(y * 21 + x)]) hi(cv::Rect(x * 8 - 1, y * 8 - 1, 10, 10) & cv::Rect(0, 0, hi.cols, hi.rows)).setTo(1.f);
                cv::Mat img(140, 140, CV_32F, cv::Scalar(0.f));
                const cv::Point2f src[4] = {{0, 0}, {168, 0}, {168, 168}, {0, 168}};
                cv::warpPerspective(hi, img, homography4(src, quad), img.size(), cv::INTER_AREA, cv::BORDER_CONSTANT, cv::Scalar(0));
                if (sigmaMod > 0) cv::GaussianBlur(img, img, cv::Size(), sigmaMod * mpx);
                cv::Mat out = 200.f - 150.f * img;
                std::normal_distribution<float> noise(0.f, 6.f);
                for (auto it = out.begin<float>(); it != out.end<float>(); ++it) *it += noise(rng);
                return out;
            };
            const std::vector<uint8_t> truth = qrPattern("37818", 2);
            const float sharp = measurePsfSigma(picture(truth, 0.f), quad, truth.data(), 21, kPrintGrowth);
            const float soft = measurePsfSigma(picture(truth, 0.45f), quad, truth.data(), 21, kPrintGrowth);
            check(sharp < 0.25f && soft >= 0.3f && soft <= 0.6f, ("lens blur measured from a read code: sharp " + std::to_string(sharp) + ", 0.45 modules -> " + std::to_string(soft)).c_str());
            // a defocused code (0.7 modules): module sampling against the pattern vs the PSF score
            const cv::Mat blurredCode = picture(truth, 0.7f);
            cv::Mat use(21, 21, CV_8U, cv::Scalar(1));
            use(cv::Rect(0, 0, 9, 9)).setTo(0); use(cv::Rect(13, 0, 8, 9)).setTo(0); use(cv::Rect(0, 13, 9, 8)).setTo(0);
            use.row(6).setTo(0); use.col(6).setTo(0);
            const auto sampled = [&](const std::vector<uint8_t>& pat) {
                const cv::Mat mm = moduleMeans(blurredCode, quad, 21);
                double sz = 0, sp = 0, szz = 0, spp = 0, szp = 0; int cnt = 0;
                for (int y = 0; y < 21; ++y)
                    for (int x = 0; x < 21; ++x) {
                        if (!use.at<uint8_t>(y, x)) continue;
                        const double a = -mm.at<float>(y, x), b = pat[static_cast<size_t>(y * 21 + x)];
                        sz += a; sp += b; szz += a * a; spp += b * b; szp += a * b; ++cnt;
                    }
                const double cov = szp - sz * sp / cnt, va = szz - sz * sz / cnt, vb = spp - sp * sp / cnt;
                return static_cast<float>(cov / std::sqrt(va * vb));
            };
            float sampledTrue = sampled(truth), sampledImp = -2.f, psfTrue = psfScore(blurredCode, quad, truth.data(), 21, use, kPrintGrowth, 0.7f * mpx), psfImp = -2.f;
            for (const char* s : {"37817", "37819", "37828", "37881", "37718", "38818", "37808", "37848"}) {
                const std::vector<uint8_t> imp = qrPattern(s, 2);
                sampledImp = std::max(sampledImp, sampled(imp));
                psfImp = std::max(psfImp, psfScore(blurredCode, quad, imp.data(), 21, use, kPrintGrowth, 0.7f * mpx));
            }
            std::printf("     defocused code: sampled modules true %.2f impostor %.2f; through the blur true %.2f impostor %.2f\n", sampledTrue, sampledImp, psfTrue, psfImp);
            check(psfTrue >= 0.75f && psfTrue - psfImp >= 0.15f, "a defocused code passes the match rule through the measured blur");
            check(psfTrue > sampledTrue + 0.05f, "the PSF score beats module sampling on a defocused code");
            // the matcher itself: by module sampling the defocused code misses the rule; through the
            // blur it reads; with its text absent from the candidates nothing is accepted
            MatchCandidates mc;
            mc.encoding.format = CR_QR;
            mc.encoding.kind = 3;
            mc.encoding.split = 5;
            mc.encoding.ecl = 1;
            mc.encoding.modules = 21;
            const std::vector<std::string> impostors = {"RSBCL37817", "RSBCL37819", "RSBCL37828", "RSBCL37881", "RSBCL37718", "RSBCL38818", "RSBCL37808", "RSBCL37848", "RSBCL37838", "RSBCL37810"};
            cv::Mat crop8;
            blurredCode.convertTo(crop8, CV_8U);
            std::string text;
            cv::Point2f q[4];
            float sc = 0.f;
            mc.texts = impostors;
            mc.texts.push_back("RSBCL37818");
            const bool sampledOnly = matchQr(crop8, 21 * mpx, mc, 500, text, q, &sc, 0.f);
            const bool throughBlur = matchQr(crop8, 21 * mpx, mc, 500, text, q, &sc, 0.7f) && text == "RSBCL37818";
            MatchCandidates absent;
            absent.encoding = mc.encoding;
            absent.texts = impostors;
            const bool falseAccept = matchQr(crop8, 21 * mpx, absent, 500, text, q, &sc, 0.7f);
            std::printf("     matcher on the defocused code: sampling %s, through the blur %s, true text absent %s\n", sampledOnly ? "reads" : "misses", throughBlur ? "reads" : "misses", falseAccept ? "WRONG READ" : "nothing");
            check(!sampledOnly && throughBlur, "matchQr reads a defocused code through the blur where sampling misses");
            check(!falseAccept, "matchQr accepts nothing when the true text is not a candidate");
        }

        // 1D codes at any angle and past their bar edges (linear.cpp): a drawn SSCC, tilted,
        // defocused or moving, is read; a non-SSCC pattern and an empty carton give nothing
        {
            std::mt19937 lrng(1977); // its own generator: the shared one's sequence belongs to the tests after
            const auto label = [&](const std::vector<uint8_t>& mods, double X, double tilt, double defocus, double motion) {
                const int W = 1400, H = 700;
                // drawn 8x finer and averaged down: each pixel sees the share of bar it covers, as a
                // camera pixel does
                const int fine = static_cast<int>(std::lround((mods.size() + 20) * X * 8));
                cv::Mat hi(1, fine, CV_32F, cv::Scalar(225.f)), row;
                for (size_t i = 0; i < mods.size(); ++i)
                    if (mods[i]) {
                        const int a = static_cast<int>(std::lround((i + 10) * X * 8)), b = static_cast<int>(std::lround((i + 11) * X * 8));
                        hi(cv::Rect(a, 0, b - a, 1)).setTo(30.f);
                    }
                cv::resize(hi, row, cv::Size(static_cast<int>(std::lround((mods.size() + 20) * X)), 1), 0, 0, cv::INTER_AREA);
                cv::Mat lab;
                cv::repeat(row, static_cast<int>(40 * X), 1, lab);
                cv::Mat img(H, W, CV_32F, cv::Scalar(120.f));
                // rotation by `tilt` degrees about the label's centre, moved to the image centre
                const double a = tilt * CV_PI / 180, ca = std::cos(a), sa = std::sin(a), cx = lab.cols / 2.0, cy = lab.rows / 2.0;
                cv::Mat M = (cv::Mat_<double>(2, 3) << ca, sa, (1 - ca) * cx - sa * cy + W / 2.0 - cx, -sa, ca, sa * cx + (1 - ca) * cy + H / 2.0 - cy);
                cv::Mat ones(lab.size(), CV_32F, cv::Scalar(1.f)), mask, warped;
                cv::warpAffine(ones, mask, M, img.size());
                cv::warpAffine(lab, warped, M, img.size());
                img = img.mul(1 - mask) + warped.mul(mask);
                if (motion > 0) {
                    cv::Mat k(1, static_cast<int>(motion) | 1, CV_32F, cv::Scalar(1.f / (static_cast<int>(motion) | 1)));
                    cv::filter2D(img, img, -1, k);
                }
                if (defocus > 0) cv::GaussianBlur(img, img, cv::Size(), defocus);
                std::normal_distribution<float> nz(0.f, 4.f);
                for (auto it = img.begin<float>(); it != img.end<float>(); ++it) *it += nz(lrng);
                cv::Mat g8;
                img.convertTo(g8, CV_8U);
                return g8;
            };
            const std::string sscc = "00376123450801218856";
            const auto mods = ssccModules(sscc);
            check(mods.size() == 156, "SSCC drawn as 156 modules");
            const auto far = std::chrono::steady_clock::now() + std::chrono::milliseconds(vs_test_ms(5000));
            int ok = 0, wrong = 0, cases = 0, gs1 = 0;
            for (const auto& [X, tilt, defocus, motion] : std::vector<std::tuple<double, double, double, double>>{
                     {3.0, 0, 0.6, 0}, {3.0, 35, 0.6, 0}, {3.0, 0, 2.8, 0}, {3.0, 20, 0.6, 7}, {1.3, 0, 0.6, 0}}) {
                ++cases;
                const auto r = readLinear(label(mods, X, tilt, defocus, motion), CR_GS1_128, 1, far);
                if (r.empty() || r[0].text != sscc) std::printf("     not read: X %.1f tilt %.0f defocus %.1f motion %.0f\n", X, tilt, defocus, motion);
                ok += !r.empty() && r[0].text == sscc;
                wrong += !r.empty() && r[0].text != sscc;
                // zxing's read or the fitted SSCC (whose fit and check symbol confirm FNC1): both GS1-128
                gs1 += !r.empty() && r[0].symbologyId == "]C1" && r[0].gs1 == 1;
            }
            std::printf("     SSCC tilted 35 deg / defocus 2.8 px / motion 7 px / 1.3 px modules: %d of %d read, wrong %d, ]C1 %d\n", ok, cases, wrong, gs1);
            check(ok == cases && wrong == 0, "linear reader: tilted, defocused, moving and small SSCCs read");
            check(gs1 == cases, "linear reader: every SSCC read, fitted or by zxing, reported ]C1 (GS1)");
            // a Code 128 that is not an SSCC (its digits disturbed): the checks do not confirm it
            std::vector<uint8_t> other = mods;
            for (size_t i = 60; i < 80; ++i) other[i] = static_cast<uint8_t>(1 - other[i]);
            const auto r1 = readLinear(label(other, 3.0, 10, 2.5, 0), CR_GS1_128, 1, far);
            cv::Mat carton(700, 1400, CV_8U, cv::Scalar(120));
            const auto r2 = readLinear(carton, CR_GS1_128 | CR_EAN13, 1, far);
            check(r1.empty() && r2.empty(), "linear reader: a broken pattern and an empty carton give nothing");
        }

        // the case's slot map (slots.cpp): codes on a rotated lattice give their empty slots; scattered
        // labels give none; a missing edge row grows on the side with texture once the size is learned
        {
            std::mt19937 srng(42);
            std::normal_distribution<float> jit(0.f, 0.04f * 120.f);
            const float ang = 8.f * static_cast<float>(CV_PI) / 180.f;
            const cv::Point2f a(120.f * std::cos(ang), 120.f * std::sin(ang)), b(-120.f * std::sin(ang), 120.f * std::cos(ang)), o(300.f, 200.f);
            std::vector<cv::Point2f> pts;
            const std::set<std::pair<int, int>> gone = {{2, 3}, {5, 0}, {7, 5}};
            for (int i = 0; i < 8; ++i)
                for (int j = 0; j < 6; ++j)
                    if (!gone.count({i, j})) pts.push_back(o + a * static_cast<float>(i) + b * static_cast<float>(j) + cv::Point2f(jit(srng), jit(srng)));
            const SlotGrid g = fitSlots(pts, 6, 8, nullptr, 60.f);
            const auto empty = emptySlots(g, pts);
            std::set<cv::Point2f, bool (*)(const cv::Point2f&, const cv::Point2f&)> want([](const cv::Point2f& x, const cv::Point2f& y) { return x.x < y.x || (x.x == y.x && x.y < y.y); });
            bool same = g.valid && empty.size() == gone.size();
            for (const auto& [i, j] : empty) {
                const cv::Point2f c = slotCentre(g, i, j);
                bool hit = false;
                for (const auto& [gi, gj] : gone) hit = hit || cv::norm(c - (o + a * static_cast<float>(gi) + b * static_cast<float>(gj))) < 20.f;
                same = same && hit;
            }
            std::printf("     slot map: lattice %s, %d x %d, off median %.2f max %.2f pitch, %zu empty\n", g.valid ? "valid" : "invalid", g.ni, g.nj, g.offMedian, g.offMax, empty.size());
            check(same, "slot map: the three missing codes of a rotated 8 x 6 case are its empty slots");
            int row = 0, col = 0;
            slotRowCol(g, g.i0, g.j0, &row, &col);
            check(row >= 1 && col >= 1 && row <= 8 && col <= 8, "slot map: rows and columns count from 1");
            // scattered labels (shrink-wrapped cans): no lattice
            std::uniform_real_distribution<float> u(0.f, 900.f);
            std::vector<cv::Point2f> scatter;
            for (int k = 0; k < 24; ++k) scatter.emplace_back(u(srng), u(srng) * 0.7f);
            check(!fitSlots(scatter, 0, 0, nullptr, 60.f).valid, "slot map: scattered labels give no slot map");
            // a whole edge row unread: the learned 8 x 6 grows the read 7 x 6 on the side with texture
            cv::Mat img(1100, 1300, CV_8U, cv::Scalar(128));
            std::vector<cv::Point2f> rows7;
            for (int i = 0; i < 8; ++i)
                for (int j = 0; j < 6; ++j) {
                    const cv::Point2f c = o + a * static_cast<float>(i) + b * static_cast<float>(j);
                    // every slot has a label with texture; the codes of the last row did not read
                    for (int y = -25; y < 25; y += 6) cv::line(img, cv::Point(static_cast<int>(c.x) - 25, static_cast<int>(c.y) + y), cv::Point(static_cast<int>(c.x) + 25, static_cast<int>(c.y) + y), cv::Scalar(20), 3);
                    if (i < 7) rows7.push_back(c);
                }
            const SlotGrid g7 = fitSlots(rows7, 6, 8, &img, 60.f);
            const auto e7 = emptySlots(g7, rows7);
            bool lastRow = g7.valid && e7.size() == 6;
            for (const auto& [i, j] : e7) {
                bool onRow = false; // index order may differ (the reduced basis): compare positions
                for (int k = 0; k < 6; ++k) onRow = onRow || cv::norm(slotCentre(g7, i, j) - (o + a * 7.f + b * static_cast<float>(k))) < 20.f;
                lastRow = lastRow && onRow;
            }
            check(lastRow, "slot map: a missing edge row is found on the side with labels");
        }

        // verifier-style grading (calibration.cpp, grade1d.cpp): ISO 15416 on a drawn SSCC, the
        // reflectance card, the flat field and the chessboard scale
        {
            const std::string sscc = "00376123450801218856";
            const auto mods = ssccModules(sscc);
            const auto draw = [&](float dark, float light, double defocus, float rampTo) {
                const double X = 3.0;
                const int fine = static_cast<int>(std::lround((mods.size() + 30) * X * 8));
                cv::Mat hi(1, fine, CV_32F, cv::Scalar(1.f)), row;
                for (size_t i = 0; i < mods.size(); ++i)
                    if (mods[i]) hi(cv::Rect(static_cast<int>(std::lround((i + 15) * X * 8)), 0, static_cast<int>(std::lround(X * 8)), 1)).setTo(0.f);
                cv::resize(hi, row, cv::Size(static_cast<int>(std::lround((mods.size() + 30) * X)), 1), 0, 0, cv::INTER_AREA);
                cv::Mat img(260, 700, CV_32F, cv::Scalar(1.f));
                cv::Mat code;
                cv::repeat(row, 120, 1, code);
                code.copyTo(img(cv::Rect(100, 70, code.cols, code.rows)));
                img = dark + img * (light - dark);
                if (defocus > 0) cv::GaussianBlur(img, img, cv::Size(), defocus);
                // uneven light: a ramp from 1 on the left to rampTo on the right
                for (int x = 0; x < img.cols; ++x) img.col(x) *= 1.f + (rampTo - 1.f) * x / img.cols;
                cv::Mat g8;
                img.convertTo(g8, CV_8U);
                return g8;
            };
            Code c;
            c.text = sscc;
            c.format = CR_GS1_128;
            const float x0 = 100 + 15 * 3, x1 = 100 + (15 + 156) * 3.f;
            c.x[0] = x0; c.y[0] = 70; c.x[1] = x1; c.y[1] = 70; c.x[2] = x1; c.y[2] = 190; c.x[3] = x0; c.y[3] = 190;
            const Quality good = gradeLinear(draw(25, 225, 0.6, 1.f), c);
            const Quality faded = gradeLinear(draw(150, 225, 1.6, 1.f), c);
            std::printf("     15416 clean: SC %.0f MOD %.2f ECmin %.0f decodability %.2f, %d/%d scans decode, grade %d; faded: SC %.0f MOD %.2f grade %d\n",
                        good.symbolContrast, good.modulation, good.edgeContrastMin, good.decodability, good.decodedScans, good.scans, good.overall,
                        faded.symbolContrast, faded.modulation, faded.overall);
            check(good.linear && good.overall == 4 && good.decodedScans == 10, "ISO 15416: a clean SSCC grades A on every scan");
            check(faded.measured && faded.overall < good.overall && faded.symbolContrast < 40.f, "ISO 15416: a faded, soft SSCC grades lower");
            // the reflectance card: grey 40 -> 5%, grey 220 -> 85%
            Calibration cal;
            cal.reflectance = true; cal.greyDark = 40; cal.greyLight = 220; cal.reflDark = 5; cal.reflLight = 85;
            check(std::abs(toReflectance(cal, {0, 0}, 130.f) - 45.f) < 0.5f, "reflectance card: grey half way reads 45%");
            // the flat field: a white card under light falling to 60% across the image
            const cv::Mat white = draw(225, 225, 0, 0.6f);
            Calibration flat;
            check(setFlatField(flat, white), "flat field accepted from a white card");
            const float left = flatCorrect(flat, {20, 130}, white.at<uint8_t>(130, 20)), right = flatCorrect(flat, {680, 130}, white.at<uint8_t>(130, 680));
            check(std::abs(left - right) < 0.05f * left, "flat field: the card reads the same at both ends after correction");
            const Quality ramped = gradeLinear(draw(25, 225, 0.6, 0.6f), c, &flat);
            check(ramped.overall >= 3, "flat field: an evenly printed SSCC under uneven light still grades well");
#ifdef CR_HAVE_CALIB
            // the chessboard: 9 x 6 inner corners, 40 px squares of 10 mm -> 0.25 mm per pixel
            cv::Mat board(400, 520, CV_8U, cv::Scalar(255));
            for (int y = 0; y < 7; ++y)
                for (int x = 0; x < 10; ++x)
                    if ((x + y) % 2 == 0) board(cv::Rect(60 + x * 40, 60 + y * 40, 40, 40)).setTo(0);
            Calibration lens;
            float rms = 0;
            const int used = calibrateChessboard({board, board}, 9, 6, 10.f, lens, &rms);
            std::printf("     chessboard: %d views, %.3f mm/px, rms %.2f px\n", used, lens.mmPerPx, rms);
            check(used > 0 && std::abs(lens.mmPerPx - 0.25f) < 0.01f, "chessboard: 10 mm squares of 40 px give 0.25 mm per pixel");
            // three views ask for a lens model: from tilted clean views it fits; from views whose
            // corners wander about 1.5 px (a warped, badly printed board) it is refused (-3), and the
            // calibration is not written, never kept as the scale alone
            {
                std::mt19937 jr(1588);
                std::uniform_real_distribution<float> u(-1.f, 1.f);
                cv::Mat big(560, 680, CV_8U, cv::Scalar(255));
                board.copyTo(big(cv::Rect(80, 80, board.cols, board.rows)));
                const auto view = [&](float tilt, float amp) {
                    const cv::Point2f from[4] = {{0, 0}, {680, 0}, {680, 560}, {0, 560}};
                    const cv::Point2f to[4] = {{tilt, tilt * 0.5f}, {680 - tilt * 0.3f, 0}, {680, 560 - tilt}, {tilt * 0.2f, 560}};
                    cv::Mat v;
                    cv::warpPerspective(big, v, homography4(from, to), big.size(), cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(255));
                    if (amp > 0.f) { // a coarse random displacement field: each corner moved its own way
                        cv::Mat dx(7, 8, CV_32F), dy(7, 8, CV_32F);
                        for (auto it = dx.begin<float>(); it != dx.end<float>(); ++it) *it = amp * u(jr);
                        for (auto it = dy.begin<float>(); it != dy.end<float>(); ++it) *it = amp * u(jr);
                        cv::resize(dx, dx, v.size(), 0, 0, cv::INTER_CUBIC);
                        cv::resize(dy, dy, v.size(), 0, 0, cv::INTER_CUBIC);
                        cv::Mat mx(v.size(), CV_32F), my(v.size(), CV_32F);
                        for (int y = 0; y < v.rows; ++y)
                            for (int x = 0; x < v.cols; ++x) { mx.at<float>(y, x) = x + dx.at<float>(y, x); my.at<float>(y, x) = y + dy.at<float>(y, x); }
                        cv::remap(v, v, mx, my, cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(255));
                    }
                    cv::GaussianBlur(v, v, cv::Size(0, 0), 0.8);
                    return v;
                };
                Calibration good, poor;
                float rmsGood = 0, rmsPoor = 0;
                const int g = calibrateChessboard({view(0, 0), view(40, 0), view(80, 0)}, 9, 6, 10.f, good, &rmsGood);
                const int p = calibrateChessboard({view(0, 2.5f), view(40, 2.5f), view(80, 2.5f)}, 9, 6, 10.f, poor, &rmsPoor);
                std::printf("     chessboard, 3 views: clean %d (rms %.2f px), wandering corners %d (rms %.2f px)\n", g, rmsGood, p, rmsPoor);
                check(g == 3 && rmsGood >= 0.f && rmsGood <= 0.5f, "chessboard: three clean tilted views give a lens model within 0.5 px");
                check(p == -3 && rmsPoor > 0.5f && poor.mmPerPx == 0.f && poor.fx == 0.f,
                      "chessboard: a poor lens model is refused (-3), nothing written, never kept as the scale alone");
            }
#endif
        }

        WorkerPool pool(4);
        std::atomic<int> sum{0};
        std::vector<int> seen(4, 0);
        pool.runAll(4, [&](int i) { seen[static_cast<size_t>(i)]++; sum += i + 1; });
        check(sum == 10 && seen[0] == 1 && seen[3] == 1, "pool: every worker ran the job once");
        for (int k = 0; k < 50; ++k) pool.runAll(4, [&](int i) { sum += i; });
        check(sum == 10 + 50 * 6, "pool: repeated jobs");
        std::fill(seen.begin(), seen.end(), 0);
        pool.runAll(2, [&](int i) { seen[static_cast<size_t>(i)]++; });
        check(seen[0] == 1 && seen[1] == 1 && seen[2] == 0 && seen[3] == 0, "pool: a job on fewer workers than the pool has");
        std::vector<int> big(100000, 1);
        long total = 0;
        std::mutex mu;
        pool.runAll(4, [&](int i) {
            long part = 0;
            for (size_t k = static_cast<size_t>(i); k < big.size(); k += 4) part += big[k];
            std::lock_guard<std::mutex> lock(mu);
            total += part;
        });
        check(total == 100000, "pool: work partitioned by index adds up");
    }
    // glare correction: a QR with a reflection band across part of it, under which dark modules
    // turn light grey (dark 40 -> about 200 against light 230), sharp edge, slight blur and noise
    {
        std::uniform_real_distribution<float> ang(0.f, 3.14159f), off(-0.15f, 0.15f), strength(0.72f, 0.82f);
        int either = 0;
        std::normal_distribution<float> noise(0.f, 4.f);
        int plain = 0, clahe = 0, glare = 0, wrong = 0;
        const int trials = 40;
        for (int t = 0; t < trials; ++t) {
            const std::string truth = "RJ01874778" + std::to_string(10 + t);
            const auto q = qrcodegen::QrCode::encodeText(truth.c_str(), qrcodegen::QrCode::Ecc::MEDIUM);
            const int n = q.getSize(), px = 4, qz = 4, side = (n + 2 * qz) * px;
            cv::Mat img(side, side, CV_32F, cv::Scalar(230));
            for (int y = 0; y < n; ++y)
                for (int x = 0; x < n; ++x)
                    if (q.getModule(x, y)) img(cv::Rect((x + qz) * px, (y + qz) * px, px, px)).setTo(40);
            cv::GaussianBlur(img, img, cv::Size(), 0.8);
            // the band: a third of the code wide, across it at a random angle, soft edge of 1.5 px
            const float a = ang(rng), c0 = off(rng) * side, w = side * 0.17f, s = strength(rng);
            cv::Mat mask(side, side, CV_32F);
            for (int y = 0; y < side; ++y)
                for (int x = 0; x < side; ++x) {
                    const float d = std::abs((x - side / 2.f) * std::cos(a) + (y - side / 2.f) * std::sin(a) - c0);
                    mask.at<float>(y, x) = 1.f / (1.f + std::exp((d - w) / 1.5f));
                }
            for (int y = 0; y < side; ++y)
                for (int x = 0; x < side; ++x) {
                    float& v = img.at<float>(y, x);
                    v += s * mask.at<float>(y, x) * (255.f - v) + noise(rng);
                }
            cv::Mat g8;
            img.convertTo(g8, CV_8U);
            ZXing::ReaderOptions o;
            o.setFormats(ZXing::BarcodeFormat::QRCode);
            o.setTryHarder(true);
            const auto readText = [&](const cv::Mat& m) {
                const auto r = ZXing::ReadBarcode(ZXing::ImageView(m.data, m.cols, m.rows, ZXing::ImageFormat::Lum, static_cast<int>(m.step)), o);
                return r.isValid() ? r.text() : std::string();
            };
            const std::string a1 = readText(g8);
            cv::Mat cl;
            cv::createCLAHE(3.0, cv::Size(4, 4))->apply(g8, cl);
            const std::string a2 = readText(cl);
            const cv::Mat dg = deglare(g8, static_cast<float>(n * px));
            const std::string a3 = readText(dg);
            plain += a1 == truth;
            clahe += a2 == truth;
            glare += a3 == truth;
            either += a2 == truth || a3 == truth;
            wrong += (!a1.empty() && a1 != truth) + (!a2.empty() && a2 != truth) + (!a3.empty() && a3 != truth);
        }
        std::printf("     glare band: plain %d/%d, CLAHE %d/%d, glare correction %d/%d, either %d, wrong %d\n", plain, trials, clahe, trials, glare, trials, either, wrong);
        // it does not replace CLAHE (a step already): it reads codes CLAHE misses
        check(glare > plain && either > clahe && wrong == 0, "glare correction reads codes half under a reflection that CLAHE misses, never wrongly");
    }
    // the miss list: a 4 x 3 grid of codes 400 px apart (side 80), one unread. The finder's other
    // unread regions: the unread code's grown box, a label flag beside each of two read codes,
    // background of the wrong size; only the unread code is listed
    {
        std::vector<cr::Code> codes;
        for (int r = 0; r < 3; ++r)
            for (int c = 0; c < 4; ++c) {
                if (r == 1 && c == 2) continue; // the unread code at (1000, 600)
                cr::Code k;
                const float x = 200.f + 400.f * c, y = 200.f + 400.f * r;
                const float xs[4] = {x - 40, x + 40, x + 40, x - 40}, ys[4] = {y - 40, y - 40, y + 40, y + 40};
                std::copy(xs, xs + 4, k.x);
                std::copy(ys, ys + 4, k.y);
                codes.push_back(k);
            }
        const std::vector<cv::Rect> boxes = {
            {140, 60, 120, 120},   // flag 150 px above the code at (200, 200)
            {940, 540, 120, 120},  // the unread code
            {880, 480, 240, 240},  // its grown box
            {700, 760, 60, 60},    // background, a third of a code
            {1290, 290, 120, 120}, // flag beside the code at (1400, 200)
        };
        const std::vector<float> sides = {80, 70, 80, 20, 75}, scores = {3.f, 1.5f, 1.f, 2.f, 2.5f};
        const auto picked = cr::pickMisses(boxes, sides, scores, codes, 1, 1.f, 70.f, 90.f);
        check(picked.size() == 1 && picked[0] == 1, "miss list: the unread code once, not its flags, grown box or background");
        // nothing learned about the size and two codes missing: still no flags or duplicates
        const auto unknown = cr::pickMisses(boxes, sides, scores, codes, 2, 0.f, 0.f, 0.f);
        check(unknown.size() == 2 && unknown[0] == 3 && unknown[1] == 1, "miss list without a learned size: flags and duplicates dropped");
    }

    // decode-fail diagnostics: the reason for synthetic misses (blurred, low contrast, glare, tiny,
    // cut off, blank, finders gone) and none for a clean code
    {
        const float m = 4.f, side = 21 * m;
        const cv::Point2f at(150, 150);
        const cv::Rect box(static_cast<int>(at.x - 6 * m), static_cast<int>(at.y - 6 * m), static_cast<int>(side + 12 * m),
                           static_cast<int>(side + 12 * m));
        MissContext ctx;
        ctx.modules = 21;
        ctx.modulePx = m;
        const cv::Mat clean = diagScene(m, at);
        const cr_miss_info ref = diagnoseMiss(clean, box, side, ctx);
        std::printf("     clean: sharpness %.2f contrast %.0f texture %.2f finder %d quiet zone %d\n", ref.sharpness, ref.contrast, ref.texture, ref.finder, ref.quiet_zone);
        check(ref.measured && ref.reason == CR_MISS_UNKNOWN && ref.finder == 1 && ref.quiet_zone == 1, "diagnosis: a clean code has no reason, finder found, quiet zone clear");
        ctx.refSharpness = ref.sharpness;
        ctx.refContrast = ref.contrast;
        auto reason = [&](const cv::Mat& img, const cv::Rect& b, const MissContext& c, const char* what) {
            const cr_miss_info d = diagnoseMiss(img, b, side, c);
            std::printf("     %-12s reason %d sharpness %.2f contrast %.0f saturated %.2f texture %.2f module %.1f finder %d\n", what, d.reason,
                        d.sharpness, d.contrast, d.saturated, d.texture, d.module_px, d.finder);
            return d.reason;
        };
        int ok = 0, total = 0;
        auto expect = [&](int got, int want) { ok += got == want; ++total; return got == want; };
        cv::Mat blurred;
        cv::GaussianBlur(clean, blurred, cv::Size(), 2.5);
        check(expect(reason(blurred, box, ctx, "blurred"), CR_MISS_BLUR), "diagnosis: blurred code -> BLUR");
        check(expect(reason(diagScene(m, at, 135, 118), box, ctx, "low contrast"), CR_MISS_LOW_CONTRAST), "diagnosis: 17 grey levels -> LOW_CONTRAST");
        cv::Mat glare = clean.clone();
        cv::ellipse(glare, cv::Point(static_cast<int>(at.x + side / 2), static_cast<int>(at.y + side / 2)), cv::Size(static_cast<int>(side / 2), static_cast<int>(side / 4)), 30, 0, 360, cv::Scalar(255), -1);
        check(expect(reason(glare, box, ctx, "glare"), CR_MISS_GLARE), "diagnosis: code under a saturated reflection -> GLARE");
        {
            MissContext tiny = ctx;
            tiny.modulePx = 1.3f;
            const float ts = 21 * 1.3f;
            const cv::Rect tb(static_cast<int>(at.x - 8), static_cast<int>(at.y - 8), static_cast<int>(ts + 16), static_cast<int>(ts + 16));
            const cr_miss_info d = diagnoseMiss(diagScene(1.3f, at), tb, ts, tiny);
            std::printf("     %-12s reason %d module %.1f texture %.2f\n", "tiny", d.reason, d.module_px, d.texture);
            check(expect(d.reason, CR_MISS_TOO_SMALL), "diagnosis: 1.3 px per module -> TOO_SMALL");
        }
        {
            const cv::Point2f edgeAt(400 - side / 2, 150);
            const cv::Rect cb(static_cast<int>(edgeAt.x - 6 * m), box.y, box.width, box.height);
            check(expect(reason(diagScene(m, edgeAt), cb, ctx, "cut off"), CR_MISS_CUT_OFF), "diagnosis: half the code off the image -> CUT_OFF");
        }
        check(expect(reason(diagScene(m, at, 200, 40, false), box, ctx, "blank"), CR_MISS_COVERED), "diagnosis: blank label -> COVERED");
        {
            cv::Mat noFinder = clean.clone();
            for (const cv::Point2f f : {at, cv::Point2f(at.x + 14 * m, at.y), cv::Point2f(at.x, at.y + 14 * m)}) {
                // the finders replaced by a checkerboard of modules: still code texture, no finder
                for (int y = 0; y < 7; ++y)
                    for (int x = 0; x < 7; ++x)
                        cv::rectangle(noFinder, cv::Rect(static_cast<int>(f.x + x * m), static_cast<int>(f.y + y * m), static_cast<int>(m), static_cast<int>(m)),
                                      cv::Scalar((x + y) % 2 ? 40 : 200), -1);
            }
            check(expect(reason(noFinder, box, ctx, "no finder"), CR_MISS_NO_FINDER), "diagnosis: finders destroyed -> NO_FINDER");
        }
        {
            // nothing read in the image: the absolute thresholds
            MissContext none = ctx;
            none.refSharpness = none.refContrast = 0.f;
            check(expect(reason(blurred, box, none, "blurred*"), CR_MISS_BLUR) &&
                      expect(reason(diagScene(m, at, 135, 118), box, none, "low contr.*"), CR_MISS_LOW_CONTRAST) &&
                      expect(reason(clean, box, none, "clean*"), CR_MISS_UNKNOWN),
                  "diagnosis without read codes to compare: blurred, low contrast, clean");
        }
        {
            // Data Matrix: the L found on a clean symbol, not on a blank label
            std::vector<uint8_t> dm;
            encodeDm("CLUK A 060715180", 16, dm);
            cv::Mat img(400, 400, CV_8U, cv::Scalar(200));
            for (int i = 0; i < 16 * 16; ++i)
                if (dm[static_cast<size_t>(i)]) cv::rectangle(img, cv::Rect(150 + (i % 16) * 5, 150 + (i / 16) * 5, 5, 5), cv::Scalar(40), -1);
            MissContext dc;
            dc.format = CR_DATAMATRIX;
            dc.modules = 16;
            dc.modulePx = 5.f;
            const cr_miss_info d = diagnoseMiss(img, cv::Rect(130, 130, 120, 120), 80.f, dc);
            check(d.finder == 1 && d.reason == CR_MISS_UNKNOWN && d.quiet_zone == 1, "diagnosis: Data Matrix L found, quiet zone clear");
        }
        // the miss list: background without code evidence and regions whose code was read elsewhere
        // are left out; a real low-contrast code and a blurred code stay
        {
            const float side2 = 21 * m;
            auto list = [&](const cv::Mat& img, const cv::Rect& b, bool readHere) {
                Result res;
                res.misses = {b};
                res.missSide = {side2};
                res.missSlot = {cv::Point(2, 3)};
                if (readHere) {
                    Code c;
                    const float cx = b.x + b.width / 2.f, cy = b.y + b.height / 2.f;
                    for (int k = 0; k < 4; ++k) {
                        c.x[k] = cx + ((k == 1 || k == 2) ? side2 : -side2) / 2.f;
                        c.y[k] = cy + (k >= 2 ? side2 : -side2) / 2.f;
                    }
                    res.codes.push_back(c);
                }
                diagnoseMisses(img, res, ctx, 1e9);
                check(res.misses.size() == res.missInfo.size() && res.misses.size() == res.missSide.size() && res.misses.size() == res.missSlot.size(),
                      "miss list: the lists stay in step when regions are left out");
                return res.misses.size();
            };
            cv::Mat wrap(300, 400, CV_8U);
            cv::randn(wrap, 130, 4); // shrink wrap: flat, grainy, no code
            check(list(diagScene(m, at, 135, 118), box, false) == 1, "miss list: a low-contrast code stays listed");
            check(list(wrap, box, false) == 0, "miss list: background (no finder, no code texture) is not a LOW_CONTRAST miss");
            check(list(blurred, box, false) == 1, "miss list: a blurred code stays listed");
            check(list(clean, box, true) == 0, "miss list: a region whose code was read elsewhere is left out");
            {
                float mm = 0.f;
                const cr_miss_info bd = diagnoseMiss(blurred, box, side2, ctx, &mm);
                std::printf("     blurred: measured module %.2f texture %.2f finder %d\n", mm, bd.texture, bd.finder);
            }
            const cr_miss_info lc = diagnoseMiss(wrap, box, side2, ctx);
            check(lc.reason != CR_MISS_UNKNOWN, "miss list: the background still gets a reason when measured alone (diagnosis unchanged)");
        }
        std::printf("     diagnosis: %d of %d synthetic misses get the right reason\n", ok, total);
    }

    // under-resolved QR: version 1-M codes at 1.5 - 2 px per module, rotated, area-sampled, blurred
    // (sigma 0.5 px), dark labels (light 24, dark 4) with noise sd 2.5, corners off by up to 0.5 px
    {
        std::uniform_real_distribution<float> ang(0.f, 1.5708f), jit(-0.5f, 0.5f), frac(-0.5f, 0.5f);
        std::normal_distribution<float> noise(0.f, 2.5f);
        std::uniform_int_distribution<int> serial(0, 99999);
        const auto render = [&](const std::string& text, float mod, cv::Point2f quad[4]) {
            // as a label printer encodes: digit runs numeric, letters bytes
            const std::vector<uint8_t> letters(text.begin() + 1, text.begin() + 5);
            const std::vector<qrcodegen::QrSegment> segs = {qrcodegen::QrSegment::makeNumeric(text.substr(0, 1).c_str()),
                                                           qrcodegen::QrSegment::makeBytes(letters),
                                                           qrcodegen::QrSegment::makeNumeric(text.substr(5).c_str())};
            const auto q = qrcodegen::QrCode::encodeSegments(segs, qrcodegen::QrCode::Ecc::MEDIUM, 1, 1, -1, false);
            constexpr int W = 64, up = 16, n = 21;
            const float a = ang(rng);
            const cv::Point2f c(W / 2.f + frac(rng), W / 2.f + frac(rng));
            const cv::Point2f ex(std::cos(a) * mod, std::sin(a) * mod), ey(-std::sin(a) * mod, std::cos(a) * mod);
            const cv::Point2f tl = c - ex * (n / 2.f) - ey * (n / 2.f);
            const cv::Point2f corners[4] = {tl, tl + ex * n, tl + ex * n + ey * n, tl + ey * n};
            // render at 16x (pixel centre i covers [i - 0.5, i + 0.5]), area-average down
            cv::Mat big(W * up, W * up, CV_32F);
            const cv::Point2f src[4] = {{0, 0}, {float(n), 0}, {float(n), float(n)}, {0, float(n)}};
            const cv::Mat Hi = homography4(corners, src);
            std::vector<cv::Point2f> pts, mm;
            for (int y = 0; y < W * up; ++y)
                for (int x = 0; x < W * up; ++x) pts.emplace_back((x + 0.5f) / up - 0.5f, (y + 0.5f) / up - 0.5f);
            cv::perspectiveTransform(pts, mm, Hi);
            for (size_t k = 0; k < pts.size(); ++k) {
                const int mx = static_cast<int>(std::floor(mm[k].x)), my = static_cast<int>(std::floor(mm[k].y));
                big.at<float>(static_cast<int>(k)) = mx >= 0 && my >= 0 && mx < n && my < n && q.getModule(mx, my) ? 1.f : 0.f;
            }
            cv::Mat img;
            cv::resize(big, img, cv::Size(W, W), 0, 0, cv::INTER_AREA);
            cv::GaussianBlur(img, img, cv::Size(), 0.5);
            for (auto it = img.begin<float>(); it != img.end<float>(); ++it) *it = 24.f - 20.f * *it + noise(rng);
            for (int k = 0; k < 4; ++k) quad[k] = corners[k] + cv::Point2f(jit(rng), jit(rng));
            cv::Mat g8;
            img.convertTo(g8, CV_8U);
            return g8;
        };
        std::vector<std::string> caseTexts, otherProduct;
        for (int i = 0; i < 5; ++i) {
            caseTexts.push_back("1BRGW3800788" + std::to_string(10000 + serial(rng) % 90000));
            otherProduct.push_back("1KXQZ5512039" + std::to_string(10000 + serial(rng) % 90000));
        }
        int read = 0, readKnown = 0, wrong = 0, hard = 0, total = 0;
        for (float mod : {2.0f, 1.75f, 1.5f, 1.3f}) {
            for (int t = 0; t < 8; ++t) {
                const std::string truth = "1BRGW3800788" + std::to_string(10000 + serial(rng) % 90000);
                cv::Point2f quad[4];
                const cv::Mat g8 = render(truth, mod, quad);
                // the turned corner order must not matter
                const cv::Point2f turned[4] = {quad[2], quad[3], quad[0], quad[1]};
                const auto later = std::chrono::steady_clock::now() + std::chrono::milliseconds(vs_test_ms(5000));
                const std::string got = underresDecodeQr(g8, t % 2 ? turned : quad, 21, nullptr, later);
                const std::string kn = underresDecodeQr(g8, quad, 21, &caseTexts, later);
                const std::string other = underresDecodeQr(g8, quad, 21, &otherProduct, later);
                const std::string at = decodeQrAt(g8, quad, 21);
                read += got == truth;
                readKnown += kn == truth;
                wrong += (!got.empty() && got != truth) + (!kn.empty() && kn != truth) + (!other.empty() && other != truth);
                hard += at == truth;
                ++total;
            }
        }
        std::printf("     under-resolved (1.3 - 2 px/module): module sampling %d/%d, solver %d/%d, with the case's texts %d/%d, wrong %d\n",
                    hard, total, read, total, readKnown, total, wrong);
        check(read >= total * 2 / 3 && read > hard && readKnown >= read && wrong == 0,
              "under-resolved solver reads 1.3 - 2 px/module codes the module sampling cannot, never wrongly (also with known content)");
        int reads = 0;
        std::normal_distribution<float> grey(14.f, 6.f);
        const cv::Point2f quad[4] = {{15, 15}, {48.6f, 15}, {48.6f, 48.6f}, {15, 48.6f}};
        for (int t = 0; t < 30; ++t) {
            cv::Mat r(64, 64, CV_32F);
            for (auto it = r.begin<float>(); it != r.end<float>(); ++it) *it = grey(rng);
            cv::GaussianBlur(r, r, cv::Size(), 0.8);
            cv::Mat g8;
            r.convertTo(g8, CV_8U);
            reads += !underresDecodeQr(g8, quad, 21, nullptr, std::chrono::steady_clock::now() + std::chrono::milliseconds(vs_test_ms(5000))).empty();
        }
        check(reads == 0, "under-resolved solver reads nothing from random images (30)");
    }

    // Symbologies and 1D rules (zxformats.cpp, rules1d.cpp)
    {
        check(code39CheckOk("VS-39AV") && !code39CheckOk("VS-39AX") && !code39CheckOk("vs-39av"), "Code 39 modulo 43 check digit");
        check(gs1Mod10Ok("15400141288763") && !gs1Mod10Ok("15400141288764") && gs1Mod10Ok("96385074") && !gs1Mod10Ok("1540014128876A"),
              "GS1 modulo 10 check digit (ITF-14, EAN-8)");
        std::string t = "VS-39AV";
        check(applyLinearRules(CR_CODE39, t, 0, 0, CR_CHECK_STRIP) && t == "VS-39A", "strip removes a correct Code 39 check digit");
        t = "VS-39AX";
        check(!applyLinearRules(CR_CODE39, t, 0, 0, CR_CHECK_VERIFY), "verify drops a Code 39 read with a wrong check digit");
        t = "15400141288763";
        check(!applyLinearRules(CR_ITF, t, 16, 0, CR_CHECK_AS_READ) && applyLinearRules(CR_ITF, t, 14, 14, CR_CHECK_VERIFY),
              "ITF length lock and check digit");
        t = "540014";
        check(!applyLinearRules(CR_ITF, t, 14, 14, CR_CHECK_AS_READ), "a partial ITF read is dropped by the length lock");
        t = "SHORT";
        check(applyLinearRules(CR_QR, t, 20, 30, CR_CHECK_VERIFY), "2D codes ignore the 1D rules");
        t = "VS-39AV";
        check(applyLinearRules(CR_CODE39, t, 7, 7, CR_CHECK_STRIP) && t == "VS-39A", "the length lock counts the check digit as printed, before it is stripped");
        t = "96385074";
        check(applyLinearRules(CR_EAN8, t, 13, 13, CR_CHECK_AS_READ), "EAN/UPC keep their own fixed length: a lock left from another type is not applied");
        t = "AB\xC3\x84";
        check(applyLinearRules(CR_GS1_128, t, 3, 3, CR_CHECK_AS_READ), "length in characters, not UTF-8 bytes");

        std::mt19937 lrng(39);
        const Models none;
        const cv::Mat c39 = drawLinear(code39Modules("VS-39AV"), 3, lrng);
        const cv::Mat c39bad = drawLinear(code39Modules("VS-39AX"), 3, lrng);
        const cv::Mat itf = drawLinear(itfModules("15400141288763"), 3, lrng);
        const cv::Mat ean8 = drawLinear(ean8Modules("96385074"), 3, lrng);
        cr_params p = linearJob(CR_CODE39);
        const std::string r39 = onlyText(decode(c39, p, none), CR_CODE39);
        p.check_digit = CR_CHECK_STRIP;
        const std::string r39s = onlyText(decode(c39, p, none), CR_CODE39);
        p.check_digit = CR_CHECK_VERIFY;
        const std::string r39bad = onlyText(decode(c39bad, p, none), CR_CODE39);
        std::printf("     Code 39: as read \"%s\", stripped \"%s\", wrong check digit \"%s\"\n", r39.c_str(), r39s.c_str(), r39bad.c_str());
        check(r39 == "VS-39AV" && r39s == "VS-39A" && r39bad.empty(), "Code 39 read; check digit stripped; a wrong check digit not reported");
        const std::string rItf = onlyText(decode(itf, linearJob(CR_ITF), none), CR_ITF);
        cr_params lock = linearJob(CR_ITF);
        lock.min_length = 16;
        const std::string rItfLocked = onlyText(decode(itf, lock, none), CR_ITF);
        check(rItf == "15400141288763" && rItfLocked.empty(), "ITF-14 read; a length lock it breaks drops it");
        const std::string rEan8 = onlyText(decode(ean8, linearJob(CR_EAN8), none), CR_EAN8);
        const std::string rEan8as13 = onlyText(decode(ean8, linearJob(CR_EAN13), none), CR_EAN13);
        std::printf("     ITF-14 \"%s\", EAN-8 \"%s\", EAN-8 in an EAN-13 job \"%s\"\n", rItf.c_str(), rEan8.c_str(), rEan8as13.c_str());
        check(rEan8 == "96385074" && rEan8as13.empty(), "EAN-8 read as EAN-8; an EAN-13 job does not report it");
        const std::string r39in128 = onlyText(decode(c39, linearJob(CR_GS1_128), none), CR_GS1_128);
        const cr::Result many = decode(c39, linearJob(CR_ANY), none);
        check(r39in128.empty() && many.codes.size() == 1 && many.codes[0].format == CR_CODE39,
              "a Code 39 is not reported by a Code 128 job, and is named Code 39 when every type is read");
    }

    // Code Reader 5.2 outputs and checks (rules1d.cpp, linear.cpp): Codabar check digit, quiet zone,
    // reading angle, check-digit state, EAN/UPC add-ons
    {
        check(codabarCheckOk("A40155B") && !codabarCheckOk("A40156B") && codabarCheckOk("40156") && !codabarCheckOk("40155") && !codabarCheckOk("A5B") &&
                  !codabarCheckOk("A4A0155B"),
              "Codabar modulo 16 check digit (start and stop characters counted; one data character at least)");
        std::string t = "A40155B";
        int why = -1;
        check(applyLinearRules(CR_CODABAR, t, 0, 0, CR_CHECK_STRIP, &why) && t == "A4015B" && why == kRejectNone,
              "strip removes Codabar's check character before the stop character");
        t = "A40156B";
        check(!applyLinearRules(CR_CODABAR, t, 0, 0, CR_CHECK_VERIFY, &why) && why == kRejectCheckDigit,
              "verify drops a Codabar read with a wrong check character, and says why");
        t = "540014";
        check(!applyLinearRules(CR_ITF, t, 14, 14, CR_CHECK_AS_READ, &why) && why == kRejectLength, "a length lock drop says why");
        check(checkDigitState(CR_EAN13, CR_CHECK_AS_READ) == 1 && checkDigitState(CR_GS1_128, CR_CHECK_AS_READ) == 1 &&
                  checkDigitState(CR_CODE39, CR_CHECK_AS_READ) == -1 && checkDigitState(CR_CODABAR, CR_CHECK_VERIFY) == 1 &&
                  checkDigitState(CR_QR, CR_CHECK_VERIFY) == -1,
              "check-digit state: own checks always, optional ones when the job verifies, none for 2D");

        std::mt19937 qrng(52);
        const Models none;
        const cv::Mat cb = drawLinear(codabarModules("A40155B"), 3, qrng);
        const cv::Mat cbBad = drawLinear(codabarModules("A40156B"), 3, qrng);
        cr_params p = linearJob(CR_CODABAR);
        p.check_digit = CR_CHECK_VERIFY;
        const cr::Result rv = decode(cb, p, none);
        const cr::Result rBad = decode(cbBad, p, none);
        p.check_digit = CR_CHECK_STRIP;
        const cr::Result rs = decode(cb, p, none);
        std::printf("     Codabar: verified \"%s\" (check %d, quiet zone %d), stripped \"%s\", wrong check digit \"%s\" (%d dropped)\n",
                    onlyText(rv, CR_CODABAR).c_str(), rv.codes.empty() ? 9 : rv.codes[0].checkDigitOk, rv.codes.empty() ? 9 : rv.codes[0].quietZoneOk,
                    onlyText(rs, CR_CODABAR).c_str(), onlyText(rBad, CR_CODABAR).c_str(), rBad.rejectedCheckDigit);
        check(onlyText(rv, CR_CODABAR) == "A40155B" && rv.codes[0].checkDigitOk == 1 && onlyText(rs, CR_CODABAR) == "A4015B" &&
                  onlyText(rBad, CR_CODABAR).empty() && rBad.rejectedCheckDigit > 0,
              "Codabar check digit verified and stripped on a decoded label; a wrong one is dropped and counted");

        // quiet zone: 12 modules clear passes; a bar 5 modules before the start fails; the label's
        // edge 6 modules from the bars fails; a code at the image edge (zone off the image) fails
        const std::vector<uint8_t> m39 = code39Modules("VS-39AV");
        const cv::Mat clear = drawLinear(m39, 3, qrng);
        cv::Mat marked = clear.clone();
        marked(cv::Rect(80 + (12 - 5) * 3, 60, 3, 60)).setTo(35);
        const cv::Mat narrow = drawLinear(m39, 3, qrng, 6);
        const cr::Result rc = decode(clear, linearJob(CR_CODE39), none);
        const cr::Result rm = decode(marked, linearJob(CR_CODE39), none);
        const cr::Result rn = decode(narrow, linearJob(CR_CODE39), none);
        const cv::Mat cut = clear(cv::Rect(80 + 10 * 3, 0, clear.cols - 80 - 10 * 3, clear.rows)).clone();
        const cr::Result rcut = decode(cut, linearJob(CR_CODE39), none);
        const auto qz = [](const cr::Result& r) { return r.codes.size() == 1 ? r.codes[0].quietZoneOk : 9; };
        std::printf("     quiet zone: clear %d, mark at 5 X %d, label edge at 6 X %d, cut at 2 X %d (9 = not read)\n", qz(rc), qz(rm), qz(rn), qz(rcut));
        check(qz(rc) == 1 && qz(rm) != 1 && qz(rn) != 1 && qz(rcut) != 1,
              "quiet zone: clear passes; a mark, the label edge or the image edge inside 10 X never passes");
        check(qz(rm) == 0 || qz(rm) == 9, "a read with a mark in its quiet zone reports 0 (or is not read at all)");

        // reading direction: 0 as drawn, 180 upside down, 90 turned clockwise
        cv::Mat r180, r90;
        cv::rotate(clear, r180, cv::ROTATE_180);
        cv::rotate(clear, r90, cv::ROTATE_90_CLOCKWISE);
        const cr::Result a0 = rc, a180 = decode(r180, linearJob(CR_CODE39), none), a90 = decode(r90, linearJob(CR_CODE39), none);
        const auto ang = [](const cr::Result& r) { return r.codes.size() == 1 ? r.codes[0].angle : -1.f; };
        const auto near = [](float a, float want) { const float d = std::fmod(std::abs(a - want) + 360.f, 360.f); return a >= 0.f && std::min(d, 360.f - d) < 5.f; };
        std::printf("     angle: %.1f, upside down %.1f, turned %.1f; quiet zone upside down %d, turned %d\n", ang(a0), ang(a180), ang(a90), qz(a180), qz(a90));
        check(near(ang(a0), 0.f) && near(ang(a180), 180.f) && near(ang(a90), 90.f) && qz(a180) == 1 && qz(a90) == 1,
              "reading angle 0 / 180 / 90 by how the label lies; quiet zone measured at every angle");

        // EAN-13 with a 5-digit add-on: main text alone, the add-on apart, its quiet zone after the add-on
        const cv::Mat e5 = drawLinear(ean13Modules("4006381333931", "52495"), 3, qrng);
        const cv::Mat e2 = drawLinear(ean13Modules("4006381333931", "12"), 3, qrng);
        const cv::Mat e0 = drawLinear(ean13Modules("4006381333931"), 3, qrng);
        const cr::Result re5 = decode(e5, linearJob(CR_EAN13), none), re2 = decode(e2, linearJob(CR_EAN13), none),
                         re0 = decode(e0, linearJob(CR_EAN13), none);
        const auto addon = [](const cr::Result& r) { return r.codes.size() == 1 ? r.codes[0].addon : std::string("<none>"); };
        std::printf("     EAN-13 + 5 \"%s\" add-on \"%s\" (quiet zone %d); + 2 \"%s\" add-on \"%s\"; plain \"%s\" add-on \"%s\" (check %d)\n",
                    onlyText(re5, CR_EAN13).c_str(), addon(re5).c_str(), qz(re5), onlyText(re2, CR_EAN13).c_str(), addon(re2).c_str(),
                    onlyText(re0, CR_EAN13).c_str(), addon(re0).c_str(), re0.codes.empty() ? 9 : re0.codes[0].checkDigitOk);
        check(onlyText(re5, CR_EAN13) == "4006381333931" && addon(re5) == "52495" && qz(re5) == 1 && onlyText(re2, CR_EAN13) == "4006381333931" &&
                  addon(re2) == "12" && onlyText(re0, CR_EAN13) == "4006381333931" && addon(re0).empty() && re0.codes[0].checkDigitOk == 1,
              "EAN-13 add-ons (2 and 5 digits) reported apart from the main text; EAN check digit always verified");

        // two labels with the same EAN and different add-ons: the add-on is reported as none, never one of them
        cv::Mat both;
        cv::Mat e2w(e2.rows, e5.cols, CV_8U, cv::Scalar(110));
        e2.copyTo(e2w(cv::Rect(0, 0, e2.cols, e2.rows)));
        cv::vconcat(e5, e2w, both);
        cr_params two = linearJob(CR_EAN13);
        two.expected_count = 2;
        const cr::Result rboth = decode(both, two, none);
        std::printf("     two labels, add-ons 52495 and 12: %zu code(s), add-on \"%s\"\n", rboth.codes.size(), rboth.codes.empty() ? "" : rboth.codes[0].addon.c_str());
        check(rboth.codes.size() == 1 && rboth.codes[0].text == "4006381333931" && (rboth.codes[0].addon.empty() || rboth.codes[0].copies == 1),
              "add-ons that disagree for one main text are reported as none");

        // a stripped check digit still matches the grader's re-read (Code 39, ITF, Codabar)
        check(readMatchesKept(CR_CODABAR, "A40155B", "A4015B") && readMatchesKept(CR_CODE39, "VS-39AV", "VS-39A") &&
                  readMatchesKept(CR_ITF, "15400141288763", "1540014128876") && !readMatchesKept(CR_CODABAR, "A40156B", "A4015B") &&
                  !readMatchesKept(CR_EAN13, "4006381333931", "400638133393"),
              "re-read matches the kept text with a correct check digit stripped");
        cr_params graded = linearJob(CR_CODABAR);
        graded.check_digit = CR_CHECK_STRIP;
        graded.grade_quality = 1;
        const cr::Result rg = decode(cb, graded, none);
        const bool gradedOk = rg.codes.size() == 1 && rg.quality.size() == 1 && rg.quality[0].scans > 0 &&
                              rg.quality[0].decodedScans == rg.quality[0].scans;
        std::printf("     Codabar stripped and graded: %d of %d scans decode to the kept text\n", rg.quality.empty() ? -1 : rg.quality[0].decodedScans,
                    rg.quality.empty() ? -1 : rg.quality[0].scans);
        check(gradedOk, "a Codabar with its check digit stripped grades its scans as decoded");
    }

    testGrading2d();
    testGradingUec();
    testGrading();
    testCApi();
    testTwoReadersAtOnce();
    testLoadFromBuffer();
    testQuietZoneSamePixels();
    testQuietZoneOtherTypes();
    testEanPrintGainNoMisread();
    testOwnReadIds();
    testMatchedDmIds();
    testDmSeenInPart();
    testDmWarpedGrid();
    testStacked();
    testPdf417CrossCheck();
    testRmqr();
    testGradeRmqr();
    testGradeQuietZoneCalibrated();
    testPharmacodeDotCode();
    testGradePharmaDotCode();
    testExtraPack();
    testBadApiInput();
    testEngineFollowUps();
    testAntiTamper();
    std::printf("%s\n", failures ? "FAILED" : "all passed");
    return failures ? 1 : 0;
}
