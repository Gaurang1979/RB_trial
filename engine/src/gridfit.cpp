// gridfit.cpp - model-based QR reading for tiny, noisy codes (shrink-wrapped cases: about 3 px
// per module, 20-30 grey levels of signal).
//
// The decoder alone must discover the module grid from noisy edges. Here the grid comes from the
// product instead: every code in a case has the same QR size (learned from codes already read).
//   1. find the code's corners from its three finder patterns (OpenCV QR detectors),
//   2. lay the known n x n grid on it and average the pixels inside each module (noise cancels),
//   3. threshold the module means (global and local variants) and redraw a clean code,
//   4. decode the clean code; Reed-Solomon error correction rejects every wrong guess, so the
//      search over the uncertain fourth corner and the thresholds cannot produce a false read.

#include "reader.hpp"
#include "zxformats.hpp"

#ifdef CR_HAVE_OBJDETECT
#include <ZXing/ReadBarcode.h>
#include <opencv2/imgproc.hpp>
#include <opencv2/objdetect.hpp>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <array>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <string>

namespace cr {

cv::Mat homography4(const cv::Point2f s[4], const cv::Point2f d[4]); // quality.cpp

namespace {

// Module means on a grid bent by a K x K net of displacements (module units), bilinear between
// net points: follows a wrinkled or curved label instead of a flat plane.
cv::Mat moduleMeansFlex(const cv::Mat& img32, const cv::Mat& H, int n, const std::vector<cv::Point2f>& net, int K)
{
    constexpr int sub = 3;
    std::vector<cv::Point2f> pts, out;
    pts.reserve(static_cast<size_t>(n) * n * sub * sub);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x)
            for (int j = 0; j < sub; ++j)
                for (int i = 0; i < sub; ++i) {
                    const float px = x + 0.3f + 0.2f * i, py = y + 0.3f + 0.2f * j;
                    const float u = std::clamp(px / n * (K - 1), 0.f, K - 1 - 1e-4f), v = std::clamp(py / n * (K - 1), 0.f, K - 1 - 1e-4f);
                    const int i0 = static_cast<int>(u), j0 = static_cast<int>(v);
                    const float fu = u - i0, fv = v - j0;
                    const cv::Point2f d = net[static_cast<size_t>(j0 * K + i0)] * (1 - fu) * (1 - fv) + net[static_cast<size_t>(j0 * K + i0 + 1)] * fu * (1 - fv) +
                                          net[static_cast<size_t>((j0 + 1) * K + i0)] * (1 - fu) * fv + net[static_cast<size_t>((j0 + 1) * K + i0 + 1)] * fu * fv;
                    pts.emplace_back(px + d.x, py + d.y);
                }
    cv::perspectiveTransform(pts, out, H);
    cv::Mat mapX(1, static_cast<int>(out.size()), CV_32F), mapY(1, static_cast<int>(out.size()), CV_32F);
    for (size_t k = 0; k < out.size(); ++k) {
        mapX.at<float>(0, static_cast<int>(k)) = out[k].x;
        mapY.at<float>(0, static_cast<int>(k)) = out[k].y;
    }
    cv::Mat vals;
    cv::remap(img32, vals, mapX, mapY, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    cv::Mat means(n, n, CV_32F);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            float s = 0;
            for (int k = 0; k < sub * sub; ++k) s += vals.at<float>(0, (y * n + x) * sub * sub + k);
            means.at<float>(y, x) = s / (sub * sub);
        }
    return means;
}

// Fit score of a grid: an aligned grid samples inside each module, so neighbouring modules differ
// strongly (a misaligned one straddles edges and averages them); plus the fixed patterns read
// the right way round. Tested against a known answer: this score, not module bimodality, finds
// the grid closest to the truth.
float gridScore(const cv::Mat& m, const cv::Mat& fixed)
{
    cv::Scalar mean, sd;
    cv::meanStdDev(m, mean, sd);
    const float sc = static_cast<float>(sd[0]) + 1e-3f;
    double nb = 0;
    int cnt = 0;
    for (int y = 0; y < m.rows; ++y)
        for (int x = 0; x < m.cols; ++x) {
            if (x + 1 < m.cols) { nb += std::abs(m.at<float>(y, x + 1) - m.at<float>(y, x)); ++cnt; }
            if (y + 1 < m.rows) { nb += std::abs(m.at<float>(y + 1, x) - m.at<float>(y, x)); ++cnt; }
        }
    cv::Mat loc;
    cv::blur(m, loc, cv::Size(7, 7));
    double agree = 0;
    int fn = 0;
    for (int y = 0; y < m.rows; ++y)
        for (int x = 0; x < m.cols; ++x) {
            const int8_t f = fixed.at<int8_t>(y, x);
            if (f < 0) continue;
            const float d = (m.at<float>(y, x) - loc.at<float>(y, x)) / sc;
            agree += std::clamp(f == 1 ? -d : d, -2.f, 2.f);
            ++fn;
        }
    return static_cast<float>(nb / std::max(1, cnt) * 2.0 / sc + (fn ? agree / fn : 0.0));
}

// A quad from a coarse finder search (about 2 px per module) refined at full scale: each of the
// three finder centres moves to the finder response peak in a window of +-3 modules around it,
// and the grid is rebuilt from them. Half a pixel at the coarse scale is a whole module here.
void refineQuad(const cv::Mat& img32, cv::Point2f q[4], int n, float m)
{
    const cv::Point2f ex = (q[1] - q[0]) * (1.f / n), ey = (q[3] - q[0]) * (1.f / n);
    cv::Point2f c[3] = {q[0] + (ex + ey) * 3.5f, q[1] - ex * 3.5f + ey * 3.5f, q[3] + ex * 3.5f - ey * 3.5f};
    const int half = static_cast<int>(std::ceil(3 * m + 9 * m * 0.75f)); // the window plus the template's reach
    for (auto& p : c) {
        const cv::Rect w = cv::Rect(static_cast<int>(p.x) - half, static_cast<int>(p.y) - half, 2 * half, 2 * half) & cv::Rect(0, 0, img32.cols, img32.rows);
        if (w.width < 9 * m * 1.5f + 2 || w.height < 9 * m * 1.5f + 2) continue;
        const cv::Mat resp = finderResponseFast(img32(w), m);
        if (resp.empty()) continue;
        const int off = (static_cast<int>(std::ceil(9 * m * 1.5f)) | 1) / 2;
        cv::Point best;
        double bv;
        cv::minMaxLoc(resp, nullptr, &bv, nullptr, &best);
        const cv::Point2f np(static_cast<float>(w.x + best.x + off), static_cast<float>(w.y + best.y + off));
        if (cv::norm(np - p) <= 3.5f * m) p = np; // within the window's intent; else keep the coarse centre
    }
    const cv::Point2f ex2 = (c[1] - c[0]) * (1.f / (n - 7)), ey2 = (c[2] - c[0]) * (1.f / (n - 7));
    const cv::Point2f tl = c[0] - (ex2 + ey2) * 3.5f;
    q[0] = tl;
    q[1] = tl + ex2 * static_cast<float>(n);
    q[2] = tl + (ex2 + ey2) * static_cast<float>(n);
    q[3] = tl + ey2 * static_cast<float>(n);
}

std::string decodeBits(const cv::Mat& dark) // CV_8U, 1 = dark module
{
    constexpr int cell = 6, qz = 4;
    const int n = dark.rows;
    cv::Mat img((n + 2 * qz) * cell, (n + 2 * qz) * cell, CV_8U, cv::Scalar(255));
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x)
            if (dark.at<uint8_t>(y, x)) img(cv::Rect((x + qz) * cell, (y + qz) * cell, cell, cell)).setTo(0);
    ZXing::ReaderOptions o;
    o.setFormats(ZXing::BarcodeFormat::QRCode);
    o.setIsPure(true);
    o.setTryRotate(false);
    o.setTryDownscale(false);
    o.setBinarizer(ZXing::Binarizer::BoolCast);
    o.setTextMode(ZXing::TextMode::Plain);
    const auto r = ZXing::ReadBarcode(ZXing::ImageView(img.data, img.cols, img.rows, ZXing::ImageFormat::Lum), o);
    return ownGridText(r);
}

} // namespace

using GClock = std::chrono::steady_clock;
static bool gridFitImpl(const cv::Mat& crop, int modules, float codeSide, GClock::time_point deadline,
                        std::string& text, cv::Point2f quadOut[4], const std::vector<std::string>* known);

bool gridFitQR(const cv::Mat& crop, int modules, float codeSide, int budgetMs, std::string& text, cv::Point2f quadOut[4],
               const std::vector<std::string>* known)
{
    if (budgetMs <= 0) return false;
    // never let a failure here take a decode thread down
    try {
        return gridFitImpl(crop, modules, codeSide, GClock::now() + std::chrono::milliseconds(budgetMs), text, quadOut, known);
    } catch (const std::exception&) {
        return false;
    }
}

// decode a module-means grid with several thresholds; error correction rejects wrong guesses
static std::string decodeMeans(const cv::Mat& m)
{
    cv::Mat m8, scratch, loc7, loc5;
    m.convertTo(m8, CV_8U);
    const double otsu = cv::threshold(m8, scratch, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
    cv::blur(m, loc7, cv::Size(7, 7));
    cv::blur(m, loc5, cv::Size(5, 5));
    const cv::Mat variants[] = {m < otsu, m < loc7, m < loc5 - 2, m < otsu - 12, m < otsu + 12};
    for (const auto& v : variants) {
        cv::Mat dark = v / 255;
        std::string t = decodeBits(dark);
        if (!t.empty()) return t;
    }
    return {};
}

// Flexible grid: coordinate search over a K x K net of displacements (3x3, then 4x4: finer nets
// overfit the noise), keeping moves that raise gridScore; decode after each step size.
static bool flexibleFit(const cv::Mat& u32, const cv::Point2f quad[4], int n, std::string& text,
                        std::chrono::steady_clock::time_point deadline)
{
    const cv::Point2f src[4] = {{0, 0}, {float(n), 0}, {float(n), float(n)}, {0, float(n)}};
    const cv::Mat H = homography4(src, quad);
    const cv::Mat fixed = qrFixed(n);
    for (int K : {3, 4}) {
        std::vector<cv::Point2f> net(static_cast<size_t>(K * K), cv::Point2f(0, 0));
        float best = gridScore(moduleMeansFlex(u32, H, n, net, K), fixed);
        for (float step : {0.6f, 0.35f, 0.2f, 0.1f, 0.05f}) {
            bool improved = true;
            while (improved && std::chrono::steady_clock::now() < deadline) {
                improved = false;
                for (auto& p : net)
                    for (int ax = 0; ax < 2; ++ax)
                        for (float s : {step, -step}) {
                            (ax == 0 ? p.x : p.y) += s;
                            const float sc = gridScore(moduleMeansFlex(u32, H, n, net, K), fixed);
                            if (sc > best + 1e-5f) {
                                best = sc;
                                improved = true;
                                goto next;
                            }
                            (ax == 0 ? p.x : p.y) -= s;
                        }
            next:;
            }
            text = decodeMeans(moduleMeansFlex(u32, H, n, net, K));
            if (!text.empty()) return true;
            if (std::chrono::steady_clock::now() >= deadline) return false;
        }
    }
    return false;
}

static bool gridFitImpl(const cv::Mat& crop, int modules, float codeSide, GClock::time_point deadline,
                        std::string& text, cv::Point2f quadOut[4], const std::vector<std::string>* known)
{
    const auto late = [&]() { return GClock::now() >= deadline; };
    const auto flexBy = [&]() { return std::min(deadline, GClock::now() + std::chrono::milliseconds(150)); };
    // the content the product's other codes share (codes read in this image), when there is some
    const auto knownRead = [&](const cv::Mat& u32, const cv::Point2f q[4], int n) {
        if (!known || known->size() < 2) return std::string();
        // its own short limit: it is cheap (a few ms) and the strongest step when it applies
        return knownDecodeQr(u32, q, n, *known, std::min(deadline, GClock::now() + std::chrono::milliseconds(150)));
    };
    // under-resolved codes (below about 2.6 px per module in the crop): the crop's own pixels
    // modelled as blurred mixes of the modules under the quad, the modules solved as binary
    // (underres.cpp); q4 on the 4x image
    const auto modulePx = [&](const cv::Point2f q4[4], int n, double f4) { return static_cast<float>(cv::norm(q4[1] - q4[0]) / f4 / n); };
    const auto underRead = [&](const cv::Point2f q4[4], int n, double f4) {
        const float mod = modulePx(q4, n, f4);
        if (mod < 1.1f || mod > 2.6f) return std::string();
        cv::Point2f qc[4];
        for (int k = 0; k < 4; ++k) qc[k] = (q4[k] + cv::Point2f(0.5f, 0.5f)) * static_cast<float>(1.0 / f4) - cv::Point2f(0.5f, 0.5f);
        return underresDecodeQr(crop, qc, n, known, std::min(deadline, GClock::now() + std::chrono::milliseconds(300)));
    };
    // stretch, slight blur against noise, 4x so the detectors see clear finder rings
    double lo, hi;
    cv::minMaxLoc(crop, &lo, &hi);
    std::vector<uint8_t> v(crop.begin<uint8_t>(), crop.end<uint8_t>());
    std::nth_element(v.begin(), v.begin() + v.size() / 100, v.end());
    lo = v[v.size() / 100];
    std::nth_element(v.begin(), v.begin() + v.size() * 995 / 1000, v.end());
    hi = std::max(lo + 4, static_cast<double>(v[v.size() * 995 / 1000]));
    cv::Mat s, b, u;
    crop.convertTo(s, CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
    cv::GaussianBlur(s, b, cv::Size(), 0.6);
    const double f = std::min(4.0, 1400.0 / std::max(crop.cols, crop.rows));
    cv::resize(b, u, cv::Size(), f, f, cv::INTER_CUBIC);
    cv::Mat u32;
    u.convertTo(u32, CV_32F);

    std::vector<int> sizes;
    if (modules >= 21) sizes.push_back(modules);
    else sizes = {21, 25, 29};

    static thread_local cv::QRCodeDetector det;
    static thread_local cv::QRCodeDetectorAruco aru;
    const bool gprof = std::getenv("CR_GPROF") != nullptr;
    const auto tg0 = GClock::now();
    auto gms = [&]() { return std::chrono::duration<double, std::milli>(GClock::now() - tg0).count(); };
    struct GProfOut { bool on; std::function<double()> ms; std::string* log; ~GProfOut() { if (on) std::fprintf(stderr, "gprof%s total %.0f ms\n", log->c_str(), ms()); } };
    std::string glog;
    GProfOut gout{gprof, gms, &glog};
    for (int which = 0; which < 2 && !late(); ++which) {
        std::vector<cv::Point2f> pts;
        bool ok = false;
        const double tdet = gms();
        try {
            ok = which == 0 ? det.detect(u, pts) : aru.detect(u, pts);
        } catch (const cv::Exception&) {
            ok = false;
        }
        if (gprof) glog += " det" + std::to_string(which) + "=" + std::to_string(int(gms() - tdet)) + (ok ? "ok" : "-");
        if (!ok || pts.size() != 4) continue;
        for (int n : sizes) {
            if (late()) return false;
            // the content shared with codes already read, first: it reads what the rest cannot
            text = knownRead(u32, pts.data(), n);
            if (!text.empty()) {
                for (int k = 0; k < 4; ++k) quadOut[k] = pts[static_cast<size_t>(k)] * static_cast<float>(1.0 / f);
                return true;
            }
            // the fourth corner has no finder pattern: search around it
            for (int dy = -12; dy <= 12; dy += 4)
                for (int dx = -12; dx <= 12; dx += 4) {
                    cv::Point2f q[4] = {pts[0], pts[1], pts[2] + cv::Point2f(float(dx), float(dy)), pts[3]};
                    const cv::Mat m = moduleMeans(u32, q, n);
                    cv::Mat m8;
                    m.convertTo(m8, CV_8U);
                    cv::Mat scratch;
                    const double otsu = cv::threshold(m8, scratch, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
                    cv::Mat loc7, loc5;
                    cv::blur(m, loc7, cv::Size(7, 7));
                    cv::blur(m, loc5, cv::Size(5, 5));
                    const cv::Mat variants[] = {m < otsu, m < otsu - 12, m < otsu + 12, m < loc7, m < loc5 - 2};
                    for (const auto& dark8 : variants) {
                        cv::Mat dark = dark8 / 255;
                        text = decodeBits(dark);
                        if (!text.empty()) {
                            for (int k = 0; k < 4; ++k) quadOut[k] = q[k] * static_cast<float>(1.0 / f);
                            return true;
                        }
                    }
                }
            if (gprof) glog += " corners@" + std::to_string(int(gms()));
            // straight grid failed: a wrinkled or curved label needs a bent grid
            const cv::Point2f q0[4] = {pts[0], pts[1], pts[2], pts[3]};
            text = softDecodeQr(moduleMeans(u32, q0, n), (n - 17) / 4);
            // the under-resolved solver before the bent grid below about 2.1 px per module, after it above
            const bool tiny = modulePx(q0, n, f) < 2.1f;
            if (text.empty() && tiny) text = underRead(q0, n, f);
            if (!text.empty()) {
                for (int k = 0; k < 4; ++k) quadOut[k] = q0[k] * static_cast<float>(1.0 / f);
                return true;
            }
            if (flexibleFit(u32, q0, n, text, flexBy()) || (!tiny && !(text = underRead(q0, n, f)).empty())) {
                for (int k = 0; k < 4; ++k) quadOut[k] = q0[k] * static_cast<float>(1.0 / f);
                return true;
            }
            if (gprof) glog += " flex@" + std::to_string(int(gms()));
        }
    }
    if (gprof) glog += " own@" + std::to_string(int(gms()));

    // OpenCV could not find the finder patterns (tiny, noisy code): our own template search at
    // about 2 px per module (the template is small there, and the rings are still clear), the
    // candidates tried cheapest first, and the flexible grid once, on the first candidate
    if (codeSide > 0 && !late()) {
        const double fq = std::clamp(2.0 * (sizes.empty() ? 21 : sizes[0]) / codeSide, 0.25, f); // scale of the finder image
        cv::Mat uq, uqf;
        cv::resize(b, uq, cv::Size(), fq, fq, fq < 1 ? cv::INTER_AREA : cv::INTER_CUBIC);
        uq.convertTo(uqf, CV_32F);
        const float toU = static_cast<float>(f / fq); // finder image -> the 4x image
        struct Cand { int n; cv::Point2f q4[4]; };
        std::vector<Cand> cands;
        for (int n : sizes) {
            const float m0 = static_cast<float>(codeSide * fq / n);
            for (float m : {m0, m0 * 0.85f, m0 * 1.15f}) {
                if (m < 1.5f || late()) continue;
                const cv::Mat resp = finderResponseFast(uqf, m);
                const int off = (static_cast<int>(std::ceil(9 * m * 1.5f)) | 1) / 2;
                cv::Point2f q2[4];
                if (!quadFromFinders(resp, m, n, off, q2)) continue;
                Cand c{n, {}};
                for (int k = 0; k < 4; ++k) c.q4[k] = q2[k] * toU;
                refineQuad(u32, c.q4, n, static_cast<float>(codeSide * f / n));
                cands.push_back(c);
            }
        }
        if (gprof) glog += " cands=" + std::to_string(cands.size()) + "@" + std::to_string(int(gms()));
        for (size_t ci = 0; ci < cands.size(); ++ci) {
            if (late()) return false;
            const auto& c = cands[ci];
            const int n = c.n;
            text = knownRead(u32, c.q4, n);
            // below about 2.1 px per module module sampling rarely reads and the solver does: before
            // the corner search, so the time is there for it (two candidates at most)
            const bool tiny = modulePx(c.q4, n, f) < 2.1f;
            if (text.empty() && ci < 2 && tiny) text = underRead(c.q4, n, f);
            if (!text.empty()) {
                for (int k = 0; k < 4; ++k) quadOut[k] = c.q4[k] * static_cast<float>(1.0 / f);
                return true;
            }
            // straight grid with the fourth-corner search
            for (int dy = -8; dy <= 8; dy += 4)
                for (int dx = -8; dx <= 8; dx += 4) {
                    cv::Point2f q[4] = {c.q4[0], c.q4[1], c.q4[2] + cv::Point2f(float(dx), float(dy)), c.q4[3]};
                    text = decodeMeans(moduleMeans(u32, q, n));
                    if (!text.empty()) {
                        for (int k = 0; k < 4; ++k) quadOut[k] = q[k] * static_cast<float>(1.0 / f);
                        return true;
                    }
                }
            // hard decisions failed: soft, least certain codewords as erasures
            text = softDecodeQr(moduleMeans(u32, c.q4, n), (n - 17) / 4);
            // 2.1 - 2.6 px per module: the solver after the sampling, with what time is left
            if (text.empty() && ci < 2 && !tiny) text = underRead(c.q4, n, f);
            if (!text.empty()) {
                for (int k = 0; k < 4; ++k) quadOut[k] = c.q4[k] * static_cast<float>(1.0 / f);
                return true;
            }
        }
        if (gprof) glog += " straight@" + std::to_string(int(gms()));
        // the flexible grid last (a wrinkled or curved label), on each candidate in turn: the
        // module-size variants differ, and a code can need the second one; 100 ms each at most
        for (size_t ci = 0; ci < std::min<size_t>(3, cands.size()) && !late(); ++ci) {
            const auto by = std::min(deadline, GClock::now() + std::chrono::milliseconds(100));
            if (flexibleFit(u32, cands[ci].q4, cands[ci].n, text, by)) {
                for (int k = 0; k < 4; ++k) quadOut[k] = cands[ci].q4[k] * static_cast<float>(1.0 / f);
                return true;
            }
        }
    }
    return false;
}

// a real decode of an n x n QR at a known position (the corners a candidate match found): module
// means on a 4x upscale, a half-pixel shift each way, threshold variants; Reed-Solomon decides
std::string decodeQrAt(const cv::Mat& crop, const cv::Point2f quad[4], int n)
{
    try {
        std::vector<uint8_t> v(crop.begin<uint8_t>(), crop.end<uint8_t>());
        std::nth_element(v.begin(), v.begin() + v.size() / 100, v.end());
        const double lo = v[v.size() / 100];
        std::nth_element(v.begin(), v.begin() + v.size() * 995 / 1000, v.end());
        const double hi = std::max(lo + 4, static_cast<double>(v[v.size() * 995 / 1000]));
        cv::Mat st, b, u, u32;
        crop.convertTo(st, CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
        cv::GaussianBlur(st, b, cv::Size(), 0.6);
        const double f = std::min(4.0, 1400.0 / std::max(crop.cols, crop.rows));
        cv::resize(b, u, cv::Size(), f, f, cv::INTER_CUBIC);
        u.convertTo(u32, CV_32F);
        static const float kShift[5][2] = {{0, 0}, {-0.5f, 0}, {0.5f, 0}, {0, -0.5f}, {0, 0.5f}};
        for (const auto& sh : kShift) {
            cv::Point2f q[4];
            for (int k = 0; k < 4; ++k) q[k] = (quad[k] + cv::Point2f(sh[0], sh[1])) * static_cast<float>(f);
            const cv::Mat mm = moduleMeans(u32, q, n);
            std::string t = decodeMeans(mm);
            // hard decisions failed: soft, with the least certain codewords as erasures
            if (t.empty() && n >= 21) t = softDecodeQr(mm, (n - 17) / 4);
            if (!t.empty()) return t;
        }
    } catch (const cv::Exception&) {
    }
    return {};
}


bool locateQrQuad(const cv::Mat& crop, int n, float codeSide, cv::Point2f quad[4])
{
    if (n < 21 || codeSide <= 0 || crop.empty()) return false;
    try {
        const double f = std::clamp(4.0 * n / codeSide, 0.5, 4.0);
        std::vector<uint8_t> v(crop.begin<uint8_t>(), crop.end<uint8_t>());
        std::nth_element(v.begin(), v.begin() + v.size() / 100, v.end());
        const double lo = v[v.size() / 100];
        std::nth_element(v.begin(), v.begin() + v.size() * 995 / 1000, v.end());
        const double hi = std::max(lo + 4, static_cast<double>(v[v.size() * 995 / 1000]));
        cv::Mat st, u, u32, h32;
        crop.convertTo(st, CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
        cv::resize(st, u, cv::Size(), f, f, f > 1 ? cv::INTER_CUBIC : cv::INTER_AREA);
        cv::GaussianBlur(u, u, cv::Size(), 0.6);
        u.convertTo(u32, CV_32F);
        cv::resize(u32, h32, cv::Size(), 0.5, 0.5, cv::INTER_AREA);
        const float mh = static_cast<float>(codeSide * f / n / 2);
        if (mh < 1.f) return false;
        const cv::Mat resp = finderResponseFast(h32, mh);
        const int off = (static_cast<int>(std::ceil(9 * mh * 1.5f)) | 1) / 2;
        const auto fq = quadsFromFinders(resp, mh, n, off, 1);
        if (fq.empty()) return false;
        for (int k = 0; k < 4; ++k) quad[k] = fq[0][static_cast<size_t>(k)] * static_cast<float>(2.0 / f);
        return true;
    } catch (const std::exception&) {
        return false;
    }
}

int knownFitQR(const cv::Mat& crop, int n, float codeSide, const std::vector<std::string>& known, int budgetMs,
               std::vector<std::string>& texts, std::vector<std::array<cv::Point2f, 4>>& quads)
{
    if (known.size() < 2 || n < 21 || codeSide <= 0 || budgetMs <= 0) return 0;
    const auto deadline = GClock::now() + std::chrono::milliseconds(budgetMs);
    int got = 0;
    try {
        // about 4 px per module, stretched, slight blur against noise
        const double f = std::clamp(4.0 * n / codeSide, 0.5, 4.0);
        std::vector<uint8_t> v(crop.begin<uint8_t>(), crop.end<uint8_t>());
        std::nth_element(v.begin(), v.begin() + v.size() / 100, v.end());
        const double lo = v[v.size() / 100];
        std::nth_element(v.begin(), v.begin() + v.size() * 995 / 1000, v.end());
        const double hi = std::max(lo + 4, static_cast<double>(v[v.size() * 995 / 1000]));
        cv::Mat st, u, u32;
        crop.convertTo(st, CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
        cv::resize(st, u, cv::Size(), f, f, f > 1 ? cv::INTER_CUBIC : cv::INTER_AREA);
        cv::GaussianBlur(u, u, cv::Size(), 0.6);
        u.convertTo(u32, CV_32F);
        const float m0 = static_cast<float>(codeSide * f / n);
        // the finder search at half scale (about 2 px per module): the template is a quarter
        // the size and the image a quarter the pixels, and the rings are still clear there
        cv::Mat h32;
        cv::resize(u32, h32, cv::Size(), 0.5, 0.5, cv::INTER_AREA);
        std::vector<cv::Point2f> centres;
        const bool kprof = std::getenv("CR_KPROF") != nullptr;
        double msFinder = 0, msDecode = 0;
        int nQuads = 0;
        for (float m : {m0, m0 * 0.9f, m0 * 1.1f}) {
            if (GClock::now() >= deadline || got > 0) break; // other sizes only when the expected one found nothing
            const auto tf = GClock::now();
            const float mh = m / 2;
            const cv::Mat resp = finderResponseFast(h32, mh);
            const int off = (static_cast<int>(std::ceil(9 * mh * 1.5f)) | 1) / 2;
            auto fq = quadsFromFinders(resp, mh, n, off, 4);
            for (auto& q : fq)
                for (auto& pt : q) pt *= 2.f;
            msFinder += std::chrono::duration<double, std::milli>(GClock::now() - tf).count();
            for (const auto& q : fq) {
                if (GClock::now() >= deadline) break;
                const cv::Point2f c = (q[0] + q[2]) * 0.5f;
                bool done = false;
                for (const auto& o : centres) done = done || cv::norm(c - o) < m * n * 0.5f;
                if (done) continue;
                ++nQuads;
                const auto td = GClock::now();
                const std::string t = knownDecodeQr(u32, q.data(), n, known, std::min(deadline, GClock::now() + std::chrono::milliseconds(150)));
                msDecode += std::chrono::duration<double, std::milli>(GClock::now() - td).count();
                if (t.empty()) continue;
                centres.push_back(c);
                texts.push_back(t);
                std::array<cv::Point2f, 4> qc;
                for (int k = 0; k < 4; ++k) qc[static_cast<size_t>(k)] = q[static_cast<size_t>(k)] * static_cast<float>(1.0 / f);
                quads.push_back(qc);
                ++got;
            }
        }
        if (kprof) std::fprintf(stderr, "kprof crop %dx%d f %.2f finder %.1f ms decode %.1f ms quads %d got %d\n", crop.cols, crop.rows, f, msFinder, msDecode, nQuads, got);
    } catch (const std::exception&) {
    }
    return got;
}

} // namespace cr

#else

namespace cr {
std::string decodeQrAt(const cv::Mat&, const cv::Point2f[4], int) { return {}; }
bool gridFitQR(const cv::Mat&, int, float, int, std::string&, cv::Point2f[4], const std::vector<std::string>*) { return false; }
int knownFitQR(const cv::Mat&, int, float, const std::vector<std::string>&, int, std::vector<std::string>&, std::vector<std::array<cv::Point2f, 4>>&) { return 0; }
bool locateQrQuad(const cv::Mat&, int, float, cv::Point2f[4]) { return false; }
} // namespace cr

#endif
