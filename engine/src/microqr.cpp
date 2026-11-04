// microqr.cpp - Micro QR grid fit: blurred or perspective-distorted Micro QR read through its
// module grid, after the QR grid fit (gridfit.cpp).
//
// zxing finds a Micro QR by its one finder pattern and samples it module by module; a blur over
// about 0.4 modules (synthetic 4 px modules, sigma 1.8 px: zxing 0 of 10) or a strong slant
// leaves it no edges to measure. Here:
//  1. candidates: dark blobs of about a square, closed over the module gaps; the corners from the
//     blob's outline
//  2. every size (13, 15, 17 modules: M2-M4) and turn: the module means under the grid, scored on
//     the fixed patterns (finder, separator, timing); the corners moved in shrinking steps while
//     the score rises
//  3. the blur undone in module space: each module's mean is a + b * its own darkness + c * its
//     four neighbours', a, b and c fitted on the fixed modules, the data modules solved in turn
//  4. a clean code redrawn from each threshold variant and read by zxing; Reed-Solomon and the
//     format information's BCH code reject every wrong guess, so the read stands only when they
//     pass. M1 (11 x 11, error detection only) is not tried: a guess would be checked by too little.
#include "reader.hpp"

#include <ZXing/ReadBarcode.h>

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <array>
#include <cmath>

namespace cr {

namespace {

// Micro QR fixed patterns for an n x n symbol: 1 dark, 0 light, -1 data (format information too)
cv::Mat microFixed(int n)
{
    cv::Mat f(n, n, CV_8S, cv::Scalar(-1));
    for (int y = 0; y <= 7; ++y)
        for (int x = 0; x <= 7; ++x) {
            const int d = std::max(std::abs(x - 3), std::abs(y - 3));
            f.at<int8_t>(y, x) = static_cast<int8_t>(x == 7 || y == 7 ? 0 : (d == 2 ? 0 : 1));
        }
    for (int k = 8; k < n; ++k) {
        f.at<int8_t>(0, k) = static_cast<int8_t>(k % 2 == 0);
        f.at<int8_t>(k, 0) = static_cast<int8_t>(k % 2 == 0);
    }
    return f;
}

// fixed-pattern score of module means: dark modules low and light ones high, in units of the
// spread of the means; plus the neighbour contrast of an aligned grid (gridfit.cpp's measure)
float fixedScore(const cv::Mat& m, const cv::Mat& fixed)
{
    double sd = 0, dk = 0, lt = 0;
    int nd = 0, nl = 0;
    for (int y = 0; y < m.rows; ++y)
        for (int x = 0; x < m.cols; ++x) {
            const int8_t f = fixed.at<int8_t>(y, x);
            if (f == 1) { dk += m.at<float>(y, x); ++nd; }
            if (f == 0) { lt += m.at<float>(y, x); ++nl; }
        }
    if (!nd || !nl) return -1e9f;
    dk /= nd;
    lt /= nl;
    for (int y = 0; y < m.rows; ++y)
        for (int x = 0; x < m.cols; ++x) {
            const int8_t f = fixed.at<int8_t>(y, x);
            if (f < 0) continue;
            const double e = m.at<float>(y, x) - (f ? dk : lt);
            sd += e * e;
        }
    sd = std::sqrt(sd / (nd + nl)) + 1.0;
    double nb = 0;
    int cnt = 0;
    for (int y = 0; y < m.rows; ++y)
        for (int x = 0; x + 1 < m.cols; ++x) {
            nb += std::abs(m.at<float>(y, x + 1) - m.at<float>(y, x)) + std::abs(m.at<float>(x + 1, y) - m.at<float>(x, y));
            cnt += 2;
        }
    cv::Scalar mean, all;
    cv::meanStdDev(m, mean, all);
    return static_cast<float>((lt - dk) / sd + 0.5 * nb / cnt / (all[0] + 1.0));
}

// the quad turned so that corner `turn` of the blob is the finder's (TL, TR, BR, BL order kept)
std::array<cv::Point2f, 4> turned(const std::array<cv::Point2f, 4>& q, int turn)
{
    return {q[static_cast<size_t>(turn % 4)], q[static_cast<size_t>((turn + 1) % 4)], q[static_cast<size_t>((turn + 2) % 4)], q[static_cast<size_t>((turn + 3) % 4)]};
}

// corners moved in shrinking steps (modules) while the fixed-pattern score rises
float refine(const cv::Mat& img32, std::array<cv::Point2f, 4>& q, int n, const cv::Mat& fixed)
{
    float best = fixedScore(moduleMeans(img32, q.data(), n), fixed);
    const float side = static_cast<float>(cv::norm(q[1] - q[0]) + cv::norm(q[3] - q[0])) / 2.f / static_cast<float>(n);
    for (const float step : {0.5f, 0.25f, 0.12f})
        for (int round = 0; round < 3; ++round) {
            bool moved = false;
            for (int k = 0; k < 4; ++k)
                for (int dir = 0; dir < 8; ++dir) {
                    const float a = static_cast<float>(dir) * 3.14159265f / 4.f;
                    auto t = q;
                    t[static_cast<size_t>(k)] += cv::Point2f(std::cos(a), std::sin(a)) * (step * side);
                    const float s = fixedScore(moduleMeans(img32, t.data(), n), fixed);
                    if (s > best + 1e-4f) { best = s; q = t; moved = true; }
                }
            if (!moved) break;
        }
    return best;
}

std::string decodeMicro(const cv::Mat& dark) // CV_8U n x n, 1 = dark module
{
    constexpr int cell = 6, qz = 3;
    const int n = dark.rows;
    cv::Mat img((n + 2 * qz) * cell, (n + 2 * qz) * cell, CV_8U, cv::Scalar(255));
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x)
            if (dark.at<uint8_t>(y, x)) img(cv::Rect((x + qz) * cell, (y + qz) * cell, cell, cell)).setTo(0);
    ZXing::ReaderOptions o;
    o.setFormats(ZXing::BarcodeFormat::MicroQRCode);
    o.setIsPure(true);
    o.setTryRotate(false);
    o.setTryDownscale(false);
    o.setBinarizer(ZXing::Binarizer::BoolCast);
    o.setTextMode(ZXing::TextMode::Plain);
    const auto r = ZXing::ReadBarcode(ZXing::ImageView(img.data, img.cols, img.rows, ZXing::ImageFormat::Lum), o);
    return r.isValid() && r.format() == ZXing::BarcodeFormat::MicroQRCode ? r.text() : std::string();
}

// darkness estimates of every module (1 = dark) from the means: plain, and with the blur undone
// (mean = a + b * own + c * four neighbours, fitted on the fixed modules, solved in turn)
std::vector<cv::Mat> darkness(const cv::Mat& m, const cv::Mat& fixed)
{
    const int n = m.rows;
    double dk = 0, lt = 0;
    int nd = 0, nl = 0;
    for (int i = 0; i < n * n; ++i) {
        const int8_t f = fixed.at<int8_t>(i / n, i % n);
        if (f == 1) { dk += m.at<float>(i / n, i % n); ++nd; }
        if (f == 0) { lt += m.at<float>(i / n, i % n); ++nl; }
    }
    dk /= std::max(nd, 1);
    lt /= std::max(nl, 1);
    std::vector<cv::Mat> out;
    cv::Mat d0 = (lt - m) / std::max(lt - dk, 1.0); // 0 light .. 1 dark
    out.push_back(d0);
    // unmixing: x = current estimate (fixed modules known), quiet zone light outside
    cv::Mat x(n, n, CV_32F);
    for (int i = 0; i < n * n; ++i) {
        const int8_t f = fixed.at<int8_t>(i / n, i % n);
        x.at<float>(i / n, i % n) = f >= 0 ? static_cast<float>(f) : (d0.at<float>(i / n, i % n) > 0.5f ? 1.f : 0.f);
    }
    auto nbSum = [&](const cv::Mat& v, int y, int xx) {
        float s = 0.f;
        if (y > 0) s += v.at<float>(y - 1, xx);
        if (y + 1 < n) s += v.at<float>(y + 1, xx);
        if (xx > 0) s += v.at<float>(y, xx - 1);
        if (xx + 1 < n) s += v.at<float>(y, xx + 1);
        return s;
    };
    for (int it = 0; it < 4; ++it) {
        // least squares of m on [1, own, neighbours] over every module with the current estimate
        cv::Mat A(n * n, 3, CV_64F), b(n * n, 1, CV_64F);
        for (int i = 0; i < n * n; ++i) {
            A.at<double>(i, 0) = 1;
            A.at<double>(i, 1) = x.at<float>(i / n, i % n);
            A.at<double>(i, 2) = nbSum(x, i / n, i % n);
            b.at<double>(i) = m.at<float>(i / n, i % n);
        }
        cv::Mat coef;
        if (!cv::solve(A, b, coef, cv::DECOMP_SVD)) break;
        const double a0 = coef.at<double>(0), b1 = coef.at<double>(1), c1 = coef.at<double>(2);
        if (b1 > -1.0) break; // dark must be darker
        cv::Mat est(n, n, CV_32F);
        for (int y = 0; y < n; ++y)
            for (int xx = 0; xx < n; ++xx)
                est.at<float>(y, xx) = static_cast<float>((m.at<float>(y, xx) - a0 - c1 * nbSum(x, y, xx)) / b1);
        out.push_back(est.clone());
        for (int i = 0; i < n * n; ++i)
            if (fixed.at<int8_t>(i / n, i % n) < 0) x.at<float>(i / n, i % n) = est.at<float>(i / n, i % n) > 0.5f ? 1.f : 0.f;
    }
    return out;
}

// read the code under quad q (n modules, finder at q[0]): darkness variants, thresholds; the
// bottom-right corner, which no fixed pattern pins, also tried half a module off each way
std::string readAt(const cv::Mat& img32, const std::array<cv::Point2f, 4>& q, int n, const cv::Mat& fixed,
                   std::chrono::steady_clock::time_point deadline)
{
    const float side = static_cast<float>(cv::norm(q[1] - q[0]) + cv::norm(q[3] - q[0])) / 2.f / static_cast<float>(n);
    const std::array<int, 9> order = {4, 1, 3, 5, 7, 0, 2, 6, 8}; // the fitted corner first
    for (const int j : order) {
        if (std::chrono::steady_clock::now() > deadline) break;
        auto t = q;
        t[2] += cv::Point2f(static_cast<float>(j % 3 - 1), static_cast<float>(j / 3 - 1)) * (0.5f * side);
        const cv::Mat m = moduleMeans(img32, t.data(), n);
        for (const cv::Mat& d : darkness(m, fixed))
            for (const float level : {0.5f, 0.4f, 0.6f}) {
                cv::Mat bits(n, n, CV_8U);
                for (int i = 0; i < n * n; ++i) {
                    const int8_t f = fixed.at<int8_t>(i / n, i % n);
                    bits.at<uint8_t>(i / n, i % n) = static_cast<uint8_t>(f >= 0 ? f : d.at<float>(i / n, i % n) > level);
                }
                const std::string text = decodeMicro(bits);
                if (!text.empty()) return text;
            }
    }
    return {};
}

} // namespace

std::vector<LinearRead> readMicroQrFit(const cv::Mat& gray, int maxCodes, std::chrono::steady_clock::time_point deadline,
                                       const std::function<bool(int, std::string)>& accept)
{
    std::vector<LinearRead> out;
    cv::Mat img32;
    gray.convertTo(img32, CV_32F);
    // candidates: dark blobs closed over the module gaps, at two closing sizes
    std::vector<std::array<cv::Point2f, 4>> quads;
    cv::Mat bw;
    {
        // dark against the local paper: a label on a darker case would make one global level split
        // the label from the case instead of the modules from the paper
        cv::Mat paper;
        cv::blur(gray, paper, cv::Size(41, 41));
        cv::Mat diff;
        cv::subtract(paper, gray, diff, cv::noArray(), CV_16S);
        bw = diff > 12;
    }
    for (const int k : {5, 9, 15}) {
        cv::Mat closed, lab, stats, cents;
        cv::morphologyEx(bw, closed, cv::MORPH_CLOSE, cv::getStructuringElement(cv::MORPH_RECT, cv::Size(k, k)));
        const int nc = cv::connectedComponentsWithStats(closed, lab, stats, cents);
        for (int i = 1; i < nc; ++i) {
            const int w = stats.at<int>(i, cv::CC_STAT_WIDTH), h = stats.at<int>(i, cv::CC_STAT_HEIGHT), area = stats.at<int>(i, cv::CC_STAT_AREA);
            // about 2 px per module or more, and a filled square-ish blob
            if (w < 26 || h < 26 || w > 6 * h || h > 6 * w || area < 0.3 * w * h || w > gray.cols / 2 + 200) continue;
            std::vector<std::vector<cv::Point>> cs;
            cv::Mat one = lab(cv::Rect(stats.at<int>(i, cv::CC_STAT_LEFT), stats.at<int>(i, cv::CC_STAT_TOP), w, h)) == i;
            cv::findContours(one, cs, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE,
                             cv::Point(stats.at<int>(i, cv::CC_STAT_LEFT), stats.at<int>(i, cv::CC_STAT_TOP)));
            if (cs.empty()) continue;
            const std::vector<cv::Point>& hull = cs[0];
            // the four outline points farthest out along the diagonals are the corners
            const cv::Point2f c(static_cast<float>(cents.at<double>(i, 0)), static_cast<float>(cents.at<double>(i, 1)));
            std::array<cv::Point2f, 4> q;
            const cv::Point2f dirs[4] = {{-1, -1}, {1, -1}, {1, 1}, {-1, 1}};
            for (int a = 0; a < 4; ++a) {
                float best = -1e9f;
                for (const auto& p : hull) {
                    const float s = (cv::Point2f(p) - c).dot(dirs[a]);
                    if (s > best) { best = s; q[static_cast<size_t>(a)] = cv::Point2f(p) + dirs[a] * 0.5f; }
                }
            }
            bool dup = false;
            for (const auto& o : quads) dup = dup || cv::norm(o[0] - q[0]) + cv::norm(o[2] - q[2]) < 4.f;
            if (!dup) quads.push_back(q);
        }
    }
    // every candidate, size and turn scored on the fixed patterns; the most code-like first
    struct Try { float score; size_t quad; int n, turn; };
    std::vector<Try> tries;
    for (size_t k = 0; k < quads.size(); ++k)
        for (const int n : {13, 15, 17})
            for (int turn = 0; turn < 4; ++turn) {
                const auto q = turned(quads[k], turn);
                tries.push_back({fixedScore(moduleMeans(img32, q.data(), n), microFixed(n)), k, n, turn});
            }
    std::sort(tries.begin(), tries.end(), [](const Try& a, const Try& b) { return a.score > b.score; });
    std::vector<int> tried(quads.size(), 0);
    int attempts = 0;
    for (const Try& t : tries) {
        if (static_cast<int>(out.size()) >= maxCodes || std::chrono::steady_clock::now() > deadline) break;
        // background scores under 1; a code blurred to half a module about 2 before its corners
        // are refined. A few attempts per code wanted: each costs the corner search and reads
        if (t.score < 1.f || attempts >= 8 * maxCodes) break;
        if (tried[t.quad]++ >= 3) continue;
        ++attempts;
        const cv::Point2f mid = (quads[t.quad][0] + quads[t.quad][2]) * 0.5f;
        bool read = false;
        for (const auto& r : out) read = read || cv::norm((r.quad[0] + r.quad[2]) * 0.5f - mid) < cv::norm(r.quad[2] - r.quad[0]) * 0.5;
        if (read) continue;
        const cv::Mat fixed = microFixed(t.n);
        auto q = turned(quads[t.quad], t.turn);
        refine(img32, q, t.n, fixed);
        const std::string text = readAt(img32, q, t.n, fixed, deadline);
        if (text.empty() || (accept && !accept(CR_MICRO_QR, text))) continue;
        LinearRead r;
        r.text = text;
        r.format = CR_MICRO_QR;
        r.step = CR_STEP_GRIDFIT;
        for (int k = 0; k < 4; ++k) r.quad[k] = q[static_cast<size_t>(k)];
        out.push_back(r);
    }
    return out;
}

} // namespace cr
