// multiframe.cpp - reading a case from several frames.
//
// Two or more frames of the same case (strobed, still or slowly moving) hold more than one:
// glare and film reflections move between frames, and at 2 px per module each frame samples the
// modules at another sub-pixel phase. Every frame is decoded as usual and the reads are united.
// A code unread in every frame is then read from the frames fused: the same region of each
// frame (aligned by the global shift), the code located in each (or, where a reflection hides a
// finder, the reference grid moved to where it fits best), and per module and frame a piece of
// evidence: how far the module's level lies from the threshold of its neighbourhood, in units of
// the distance from that threshold to the class mean on its side, so frames of different exposure
// count alike. A module clipped in a frame (a reflection) takes nothing from it. Frames are
// weighted by their quality on the code (the inverse variance of their evidence: noise, blur and
// grid error all lower it). The summed evidence is decoded softly, weak modules first as
// Reed-Solomon erasures with two check symbols in reserve, then with the content the case's codes
// share. After Cognex's multi-frame reading (EP3109792 / US9734376: per-cell confidences from
// Otsu class means, saturated 4 x 4 subregions dropped, frames summed with equal weight); ours
// works per module, with local thresholds and quality weights; see docs/PATENTS.md.

#include "reader.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <map>

namespace cr {

cv::Mat homography4(const cv::Point2f s[4], const cv::Point2f d[4]); // quality.cpp

namespace {

using Clock = std::chrono::steady_clock;

// the crop prepared as the finder search and the grid sampler expect: stretched, at about 4 px
// per module, slightly blurred; f = the scale applied
cv::Mat prepared(const cv::Mat& crop, int n, float codeSide, double& f)
{
    f = std::clamp(4.0 * n / codeSide, 0.5, 4.0);
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
    return u32;
}

cv::Mat standardised(const cv::Mat& m)
{
    cv::Scalar mean, sd;
    cv::meanStdDev(m, mean, sd);
    return (m - mean[0]) / (sd[0] + 1e-6);
}

double correlation(const cv::Mat& a, const cv::Mat& b)
{
    return cv::mean(a.mul(b))[0]; // both standardised
}

} // namespace

cv::Point2d frameShift(const cv::Mat& a, const cv::Mat& b)
{
    cv::Mat sa, sb, fa, fb;
    cv::resize(a, sa, cv::Size(), 0.25, 0.25, cv::INTER_AREA);
    cv::resize(b, sb, cv::Size(), 0.25, 0.25, cv::INTER_AREA);
    sa.convertTo(fa, CV_32F);
    sb.convertTo(fb, CV_32F);
    cv::Mat win;
    cv::createHanningWindow(win, fa.size(), CV_32F);
    const cv::Point2d d = cv::phaseCorrelate(fa, fb, win);
    return d * 4.0;
}

namespace {

// One frame's evidence on the code's modules: for each module, how dark it is relative to the
// frame's own threshold, in units of the distance from that threshold to the class mean on its
// side (positive dark, about +1 for a typical dark module, -1 for a typical light one); weighted
// by how much contrast its neighbourhood has, zero where its pixels are clipped. weight: the
// frame's quality on this code (1 / variance of the evidence about +-1).
struct FrameEvidence {
    cv::Mat e;      // n x n CV_32F, weighted evidence (0 where masked)
    double weight = 0;
    int masked = 0; // modules with no evidence from this frame
};

// module means of the raw crop (its own intensities), and which modules hold clipped pixels or
// fall outside the crop
void rawModules(const cv::Mat& crop8, const cv::Point2f quad[4], int n, double satLevel, cv::Mat& means, cv::Mat& bad, double smoothing)
{
    const cv::Point2f src[4] = {{0, 0}, {float(n), 0}, {float(n), float(n)}, {0, float(n)}};
    const cv::Mat H = homography4(src, quad);
    constexpr int sub = 3; // 3 x 3 samples in the central 60% of each module, as moduleMeans
    std::vector<cv::Point2f> pts, out;
    pts.reserve(static_cast<size_t>(n) * n * sub * sub);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x)
            for (int j = 0; j < sub; ++j)
                for (int i = 0; i < sub; ++i)
                    pts.emplace_back(x + 0.2f + 0.6f * (i + 0.5f) / sub, y + 0.2f + 0.6f * (j + 0.5f) / sub);
    cv::perspectiveTransform(pts, out, H);
    cv::Mat mapX(1, static_cast<int>(out.size()), CV_32F), mapY(1, static_cast<int>(out.size()), CV_32F);
    for (size_t k = 0; k < out.size(); ++k) {
        mapX.at<float>(0, static_cast<int>(k)) = out[k].x;
        mapY.at<float>(0, static_cast<int>(k)) = out[k].y;
    }
    cv::Mat c32, vals, smooth, svals;
    crop8.convertTo(c32, CV_32F);
    cv::remap(c32, vals, mapX, mapY, cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(-1));
    // the levels from the crop slightly smoothed (less noise; clipping is judged on the raw pixels)
    const double modPx = cv::norm(quad[1] - quad[0]) / n;
    if (smoothing > 0) {
        cv::GaussianBlur(c32, smooth, cv::Size(), std::max(0.4, smoothing * modPx));
        cv::remap(smooth, svals, mapX, mapY, cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(-1));
    } else svals = vals;
    means.create(n, n, CV_32F);
    bad.create(n, n, CV_8U);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            float s = 0;
            int clipped = 0, outside = 0;
            for (int k = 0; k < sub * sub; ++k) {
                const float v = vals.at<float>(0, (y * n + x) * sub * sub + k);
                if (v < 0) ++outside;
                else if (v >= satLevel) ++clipped;
                s += svals.at<float>(0, (y * n + x) * sub * sub + k);
            }
            means.at<float>(y, x) = s / (sub * sub);
            // 1 outside the crop, 2 clipped (a third of its samples at the clip level)
            bad.at<uint8_t>(y, x) = outside > 0 ? 1 : clipped * 3 >= sub * sub ? 2 : 0;
        }
    // a clipped module says only "at least this bright". In a frame whose light modules are not
    // clipped, that is a reflection: no evidence. In an overexposed frame (most light modules
    // clipped), a lone clipped module is a light one, and only clipped patches (a module whose
    // 3 x 3 neighbourhood is nearly all clipped: dark modules under a reflection too) are unknown,
    // with the clipped modules around them (the reflection's edge, where dark modules clip too).
    const int clippedAll = cv::countNonZero(bad == 2);
    const bool over = clippedAll * 5 > n * n; // over a fifth of the modules: the light class clips
    cv::Mat mask = bad.clone();
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            if (bad.at<uint8_t>(y, x) != 2) continue;
            bool unknown = true;
            if (over) {
                int c = 0, t = 0;
                for (int yy = std::max(0, y - 1); yy <= std::min(n - 1, y + 1); ++yy)
                    for (int xx = std::max(0, x - 1); xx <= std::min(n - 1, x + 1); ++xx) {
                        ++t;
                        c += bad.at<uint8_t>(yy, xx) == 2;
                    }
                unknown = c * 9 >= 8 * t; // 8 of 9
            }
            mask.at<uint8_t>(y, x) = unknown ? 1 : 3;
        }
    if (over) {
        cv::Mat grown = mask.clone();
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x) {
                if (mask.at<uint8_t>(y, x) != 3) continue;
                for (int yy = std::max(0, y - 1); yy <= std::min(n - 1, y + 1); ++yy)
                    for (int xx = std::max(0, x - 1); xx <= std::min(n - 1, x + 1); ++xx)
                        if (mask.at<uint8_t>(yy, xx) == 1) grown.at<uint8_t>(y, x) = 1;
            }
        mask = grown;
    }
    bad = mask; // 0 a level, 1 unknown, 3 clipped but light
}

// the level at which this crop clips: its maximum, when a visible share of pixels sits there
// (8-bit frames clip at 255; a camera with a black-level offset or a 10-to-8 bit mapping may clip
// lower); above any real pixel when nothing is clipped
double clipLevel(const cv::Mat& crop8)
{
    double mn, mx;
    cv::minMaxLoc(crop8, &mn, &mx);
    if (mx < 160) return 1e9;
    const int atMax = cv::countNonZero(crop8 >= mx - 1);
    return atMax * 500 >= static_cast<int>(crop8.total()) ? mx - 1 : 1e9;
}

// two-means split of values: threshold and class means; false without both classes
bool twoMeans(const std::vector<float>& v, double& t, double& m0, double& m1)
{
    if (v.size() < 4) return false;
    const auto mm = std::minmax_element(v.begin(), v.end());
    t = (*mm.first + *mm.second) / 2.0;
    for (int it = 0; it < 8; ++it) {
        double s0 = 0, s1 = 0;
        int n0 = 0, n1 = 0;
        for (float x : v)
            if (x < t) { s0 += x; ++n0; } else { s1 += x; ++n1; }
        if (n0 < 2 || n1 < 2) return false;
        m0 = s0 / n0;
        m1 = s1 / n1;
        const double nt = (m0 + m1) / 2;
        if (std::abs(nt - t) < 0.01) break;
        t = nt;
    }
    return m1 - m0 > 1e-3;
}

FrameEvidence frameEvidence(const cv::Mat& crop8, const cv::Point2f quad[4], int n, double smoothing)
{
    FrameEvidence fe;
    cv::Mat means, bad;
    rawModules(crop8, quad, n, clipLevel(crop8), means, bad, smoothing);
    std::vector<float> all;
    for (int i = 0; i < n * n; ++i)
        if (bad.at<uint8_t>(i / n, i % n) != 1) all.push_back(means.at<float>(i / n, i % n));
    double T, M0, M1;
    fe.e = cv::Mat::zeros(n, n, CV_32F);
    fe.masked = n * n - static_cast<int>(all.size()); // unknown modules
    if (static_cast<int>(all.size()) < n * n / 4 || !twoMeans(all, T, M0, M1)) {
        fe.masked = n * n;
        return fe;
    }
    const double gap = M1 - M0;
    // per module: threshold and class means of its neighbourhood (9 x 9 modules, unclipped ones),
    // so a reflection that brightens part of the code without clipping it moves the threshold with it
    constexpr int R = 4;
    double r2 = 0;
    int nr = 0;
    std::vector<float> win;
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            const uint8_t b = bad.at<uint8_t>(y, x);
            if (b == 1) continue;
            win.clear();
            for (int yy = std::max(0, y - R); yy <= std::min(n - 1, y + R); ++yy)
                for (int xx = std::max(0, x - R); xx <= std::min(n - 1, x + R); ++xx)
                    if (bad.at<uint8_t>(yy, xx) != 1) win.push_back(means.at<float>(yy, xx));
            double t = T, m0 = M0, m1 = M1;
            if (!twoMeans(win, t, m0, m1) || m1 - m0 < 0.25 * gap) {
                // no clear two classes nearby (all one colour, or washed out): the frame's own split
                t = T;
                m0 = M0;
                m1 = M1;
            }
            // the neighbourhood's contrast against the frame's: a washed-out area says less
            const double c = std::clamp((m1 - m0) / gap, 0.0, 1.0);
            if (b == 3) {
                // clipped but light: its level is not measured, so a moderate vote, and it says
                // nothing of the frame's quality
                fe.e.at<float>(y, x) = static_cast<float>(-0.5 * c * c);
                continue;
            }
            const double m = means.at<float>(y, x);
            double e = m < t ? (t - m) / std::max(t - m0, 1e-3) : -(m - t) / std::max(m1 - t, 1e-3);
            e = std::clamp(e, -2.0, 2.0);
            fe.e.at<float>(y, x) = static_cast<float>(e * c * c);
            r2 += (std::abs(e) - 1) * (std::abs(e) - 1);
            ++nr;
        }
    // quality: the spread of the evidence about +-1 (noise, blur, a misplaced grid all widen it);
    // the frames' evidence combines best weighted by the inverse of that variance
    const double var = nr ? r2 / nr : 1.0;
    fe.weight = 1.0 / std::max(var, 0.02);
    return fe;
}

// the quad moved by (dx, dy)
void moved(const cv::Point2f in[4], float dx, float dy, cv::Point2f out[4])
{
    for (int c = 0; c < 4; ++c) out[c] = in[c] + cv::Point2f(dx, dy);
}

} // namespace

std::string decodeQrFrames(const std::vector<cv::Mat>& crops, int n, float codeSide, const std::vector<std::string>& known,
                           int budgetMs, cv::Point2f quad0[4], int* quadFrom)
{
    if (crops.empty() || n < 21 || codeSide <= 0) return {};
    const int version = (n - 17) / 4;
    if (17 + 4 * version != n || version > 6) return {};
    const auto deadline = Clock::now() + std::chrono::milliseconds(std::max(1, budgetMs));
    // each crop's grid: located in it and oriented like the reference (the first located crop);
    // a crop where the code is not located (a reflection over a finder) takes the reference's
    // grid, moved to where it correlates best (the crops are aligned to about a pixel already)
    const size_t K = crops.size();
    std::vector<std::array<cv::Point2f, 4>> quads(K);
    std::vector<char> have(K, 0), located(K, 0);
    std::vector<cv::Mat> z(K), u32s(K);
    std::vector<double> scale(K, 1.0);
    // the reference: the located crop whose grid fits the finders and timing best (a grid placed a
    // module off in one frame must not decide the orientation of the others)
    const cv::Mat fixed = qrFixed(n);
    cv::Mat ref;
    int refK = -1;
    double refFit = -2;
    for (size_t k = 0; k < K; ++k) {
        if (Clock::now() >= deadline) break;
        cv::Point2f q[4];
        if (!locateQrQuad(crops[k], n, codeSide, q)) continue;
        located[k] = 1;
        u32s[k] = prepared(crops[k], n, codeSide, scale[k]);
        std::copy(q, q + 4, quads[k].begin());
        cv::Point2f qs[4];
        for (int c = 0; c < 4; ++c) qs[c] = q[c] * static_cast<float>(scale[k]);
        z[k] = standardised(moduleMeans(u32s[k], qs, n));
        double fit = 0;
        int nf = 0;
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x) {
                const int8_t fv = fixed.at<int8_t>(y, x);
                if (fv < 0) continue;
                fit += (fv ? -1 : 1) * z[k].at<float>(y, x);
                ++nf;
            }
        fit /= std::max(1, nf);
        if (fit > refFit) {
            refFit = fit;
            refK = static_cast<int>(k);
        }
    }
    if (refK < 0) return {};
    ref = z[static_cast<size_t>(refK)];
    have[static_cast<size_t>(refK)] = 1;
    for (size_t k = 0; k < K; ++k) {
        if (!located[k] || have[k]) continue;
        // the corner order that agrees best with the reference (rotation or mirror)
        cv::Point2f qs[4];
        for (int c = 0; c < 4; ++c) qs[c] = quads[k][c] * static_cast<float>(scale[k]);
        double best = -2;
        int bo = 0;
        for (int o = 0; o < 8; ++o) {
            cv::Point2f qo[4];
            for (int c = 0; c < 4; ++c) qo[c] = qs[o < 4 ? (c + o) % 4 : (4 - c + o) % 4];
            const double sc = correlation(standardised(moduleMeans(u32s[k], qo, n)), ref);
            if (sc > best) {
                best = sc;
                bo = o;
            }
        }
        if (best < 0.3) continue; // not the same code, or not located: leave it out
        const std::array<cv::Point2f, 4> q = quads[k];
        for (int c = 0; c < 4; ++c) quads[k][c] = q[bo < 4 ? (c + bo) % 4 : (4 - c + bo) % 4];
        have[k] = 1;
    }
    for (size_t k = 0; k < K; ++k) {
        if (have[k] || located[k] || Clock::now() >= deadline) continue;
        if (crops[k].size() != crops[static_cast<size_t>(refK)].size()) continue;
        double f = 1;
        const cv::Mat u32 = prepared(crops[k], n, codeSide, f);
        double best = -2;
        cv::Point2f bq[4];
        const float step = std::max(0.25f, codeSide / n / 4.f); // a quarter module
        for (int j = -6; j <= 6; ++j)
            for (int i = -6; i <= 6; ++i) {
                cv::Point2f q[4], qs[4];
                moved(quads[static_cast<size_t>(refK)].data(), i * step, j * step, q);
                for (int c = 0; c < 4; ++c) qs[c] = q[c] * static_cast<float>(f);
                const double sc = correlation(standardised(moduleMeans(u32, qs, n)), ref);
                if (sc > best) {
                    best = sc;
                    std::copy(q, q + 4, bq);
                }
            }
        if (best < 0.5) continue; // a frame that is not located must agree well
        std::copy(bq, bq + 4, quads[k].begin());
        have[k] = 1;
    }
    // the evidence of every frame, weighted by its quality, summed per module; a module clipped
    // in a frame takes nothing from that frame, so it ends weaker and goes to Reed-Solomon first
    cv::Mat sum = cv::Mat::zeros(n, n, CV_32F);
    double wsum = 0;
    for (size_t k = 0; k < K; ++k) {
        if (!have[k]) continue;
        // sampled as it is and slightly smoothed: the one of better quality (smoothing lowers the
        // noise of a noisy frame, but spreads a reflection into the modules around it)
        FrameEvidence fe = frameEvidence(crops[k], quads[k].data(), n, 0.0);
        FrameEvidence fs = frameEvidence(crops[k], quads[k].data(), n, 0.15);
        if (fs.weight > fe.weight) fe = std::move(fs);
        if (fe.weight <= 0) continue;
        sum += fe.e * fe.weight;
        wsum += fe.weight;
    }
    if (wsum <= 0) return {};
    cv::Mat evidence = sum / wsum;
    // nearly balanced modules count as unknown (erasures first)
    for (auto it = evidence.begin<float>(); it != evidence.end<float>(); ++it)
        if (std::abs(*it) < 0.05f) *it = 0.f;
    if (quad0) std::copy(quads[static_cast<size_t>(refK)].begin(), quads[static_cast<size_t>(refK)].end(), quad0);
    if (quadFrom) *quadFrom = refK;
    std::string text = softDecodeQrEvidence(evidence, version);
    if (!text.empty() || known.size() < 2 || Clock::now() >= deadline) return text;
    // known content: the fused grid drawn as a clean image at 4 px per module, two modules of
    // quiet zone, and read like a crop
    const cv::Mat means = 128.0 - evidence * 60.0;
    constexpr int px = 4, qz = 2;
    cv::Mat img((n + 2 * qz) * px, (n + 2 * qz) * px, CV_32F, cv::Scalar(200));
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) img(cv::Rect((x + qz) * px, (y + qz) * px, px, px)).setTo(means.at<float>(y, x));
    cv::GaussianBlur(img, img, cv::Size(), 0.8);
    const cv::Point2f quad[4] = {{float(qz * px), float(qz * px)}, {float((qz + n) * px), float(qz * px)},
                                 {float((qz + n) * px), float((qz + n) * px)}, {float(qz * px), float((qz + n) * px)}};
    return knownDecodeQr(img, quad, n, known, deadline);
}

namespace {

// a 2 x 3 matrix for warpAffine: a move by (dx, dy)
cv::Mat translation(double dx, double dy)
{
    cv::Mat M = cv::Mat::eye(2, 3, CV_64F);
    M.at<double>(0, 2) = dx;
    M.at<double>(1, 2) = dy;
    return M;
}

// the translation of b relative to a to a small part of a pixel (content at x in a is at x + d in
// b): phase correlation, then Lucas-Kanade steps on the standardised crops (frames of different
// exposure compare alike). The crops are aligned to about a pixel already: a larger answer is
// not trusted. ok = false when the crops do not match after the steps.
cv::Point2d subpixelShift(const cv::Mat& a8, const cv::Mat& b8, bool& ok)
{
    cv::Mat a, b, win;
    a8.convertTo(a, CV_32F);
    b8.convertTo(b, CV_32F);
    a = standardised(a);
    b = standardised(b);
    cv::createHanningWindow(win, a.size(), CV_32F);
    cv::Point2d d = cv::phaseCorrelate(a, b, win);
    if (std::abs(d.x) > 2 || std::abs(d.y) > 2) d = cv::Point2d(0, 0);
    cv::Mat gx, gy;
    cv::Sobel(a, gx, CV_32F, 1, 0, 3, 1.0 / 8);
    cv::Sobel(a, gy, CV_32F, 0, 1, 3, 1.0 / 8);
    const int m = 3; // the border, where the warp repeats edge pixels
    const cv::Rect in(m, m, a.cols - 2 * m, a.rows - 2 * m);
    double sxx = 0, sxy = 0, syy = 0;
    for (int y = in.y; y < in.br().y; ++y)
        for (int x = in.x; x < in.br().x; ++x) {
            const double u = gx.at<float>(y, x), v = gy.at<float>(y, x);
            sxx += u * u;
            sxy += u * v;
            syy += v * v;
        }
    const double det = sxx * syy - sxy * sxy;
    ok = det > 1e-9 * (sxx + syy) * (sxx + syy) && sxx + syy > 0;
    if (!ok) return d;
    cv::Mat bw;
    for (int it = 0; it < 12; ++it) {
        const cv::Mat M = translation(d.x, d.y);
        cv::warpAffine(b, bw, M, b.size(), cv::INTER_CUBIC | cv::WARP_INVERSE_MAP, cv::BORDER_REPLICATE);
        double rx = 0, ry = 0;
        for (int y = in.y; y < in.br().y; ++y)
            for (int x = in.x; x < in.br().x; ++x) {
                const double r = bw.at<float>(y, x) - a.at<float>(y, x);
                rx += gx.at<float>(y, x) * r;
                ry += gy.at<float>(y, x) * r;
            }
        const double ddx = -(syy * rx - sxy * ry) / det, ddy = -(sxx * ry - sxy * rx) / det;
        d += cv::Point2d(std::clamp(ddx, -0.5, 0.5), std::clamp(ddy, -0.5, 0.5));
        if (std::abs(ddx) < 0.005 && std::abs(ddy) < 0.005) break;
    }
    if (std::abs(d.x) > 2 || std::abs(d.y) > 2) {
        ok = false;
        return d;
    }
    const cv::Mat M = translation(d.x, d.y);
    cv::warpAffine(b, bw, M, b.size(), cv::INTER_CUBIC | cv::WARP_INVERSE_MAP, cv::BORDER_REPLICATE);
    ok = correlation(standardised(bw(in)), standardised(a(in))) > 0.5; // the same code, aligned
    return d;
}

} // namespace

cv::Mat superResolve(const std::vector<cv::Mat>& crops, int s, std::vector<cv::Point2d>* shifts)
{
    if (crops.empty() || s < 2) return {};
    const cv::Size lr = crops[0].size(), hr(lr.width * s, lr.height * s);
    // each frame: its shift against the first, its levels mapped onto the first's (a gain and an
    // offset fitted on the aligned pixels), and its clipped pixels masked
    std::vector<cv::Mat> obs, mask;
    std::vector<cv::Point2d> d;
    cv::Mat ref32;
    crops[0].convertTo(ref32, CV_32F);
    for (size_t k = 0; k < crops.size(); ++k) {
        if (crops[k].size() != lr) continue;
        cv::Point2d dk(0, 0);
        bool ok = true;
        if (k) dk = subpixelShift(crops[0], crops[k], ok);
        if (!ok) continue;
        cv::Mat f, fw;
        crops[k].convertTo(f, CV_32F);
        const cv::Mat bad = crops[k] >= clipLevel(crops[k]);
        const cv::Mat M = translation(dk.x, dk.y);
        cv::warpAffine(f, fw, M, lr, cv::INTER_LINEAR | cv::WARP_INVERSE_MAP, cv::BORDER_REPLICATE);
        double sa = 0, sb = 0, saa = 0, sab = 0;
        int cnt = 0;
        for (int y = 2; y < lr.height - 2; ++y)
            for (int x = 2; x < lr.width - 2; ++x) {
                const float v = fw.at<float>(y, x), r = ref32.at<float>(y, x);
                if (v >= 250.f || r >= 250.f) continue;
                sa += v;
                sb += r;
                saa += double(v) * v;
                sab += double(v) * r;
                ++cnt;
            }
        double g = 1, o = 0;
        const double var = cnt ? saa / cnt - (sa / cnt) * (sa / cnt) : 0;
        if (k && cnt > 16 && var > 1) {
            g = (sab / cnt - (sa / cnt) * (sb / cnt)) / var;
            if (g < 0.2 || g > 5) continue; // not the same code
            o = sb / cnt - g * sa / cnt;
        }
        obs.push_back(f * g + o);
        cv::Mat mk(lr, CV_32F, cv::Scalar(1));
        mk.setTo(0, bad);
        mask.push_back(mk);
        d.push_back(dk);
    }
    if (obs.size() < 2) return {};
    if (shifts) *shifts = d;
    // the frame's pixel p sees the first frame's point p - d; on the fine grid P - s d
    const auto toFrame = [&](size_t k) { return translation(-s * d[k].x, -s * d[k].y); };
    const auto toFirst = [&](size_t k) { return translation(s * d[k].x, s * d[k].y); };
    const int flags = cv::INTER_LINEAR | cv::WARP_INVERSE_MAP;
    // start: every frame enlarged and moved back onto the first, averaged where unclipped
    cv::Mat X = cv::Mat::zeros(hr, CV_32F), W = cv::Mat::zeros(hr, CV_32F);
    std::vector<cv::Mat> wk(obs.size());
    for (size_t k = 0; k < obs.size(); ++k) {
        cv::Mat up, mu, back;
        cv::resize(obs[k].mul(mask[k]), up, hr, 0, 0, cv::INTER_CUBIC);
        cv::resize(mask[k], mu, hr, 0, 0, cv::INTER_LINEAR);
        cv::warpAffine(up, back, toFirst(k), hr, flags, cv::BORDER_REPLICATE);
        cv::warpAffine(mu, wk[k], toFirst(k), hr, flags, cv::BORDER_REPLICATE);
        X += back;
        W += wk[k];
    }
    X /= cv::max(W, 1e-3);
    double lo, hi;
    cv::minMaxLoc(obs[0], &lo, &hi);
    // iterative back-projection: each frame drawn from the estimate (moved, every s x s block
    // averaged as the sensor's pixel does), the difference spread back over the block it came from
    for (int it = 0; it < 10; ++it) {
        cv::Mat E = cv::Mat::zeros(hr, CV_32F);
        for (size_t k = 0; k < obs.size(); ++k) {
            cv::Mat xk, sim, up, back;
            cv::warpAffine(X, xk, toFrame(k), hr, flags, cv::BORDER_REPLICATE);
            cv::resize(xk, sim, lr, 0, 0, cv::INTER_AREA);
            cv::resize((obs[k] - sim).mul(mask[k]), up, hr, 0, 0, cv::INTER_NEAREST);
            cv::GaussianBlur(up, up, cv::Size(), 0.35 * s);
            cv::warpAffine(up, back, toFirst(k), hr, flags, cv::BORDER_REPLICATE);
            E += back;
        }
        X += E / cv::max(W, 1e-3);
        X = cv::max(cv::min(X, hi), lo);
    }
    return X;
}

std::string superResolveQrFrames(const std::vector<cv::Mat>& crops, int n, float codeSide, const std::vector<std::string>& known,
                                 int budgetMs, cv::Point2f quad0[4])
{
    if (crops.size() < 2 || n < 21 || codeSide <= 0) return {};
    const auto deadline = Clock::now() + std::chrono::milliseconds(std::max(1, budgetMs));
    // the fine grid at about 4 px per module
    const int s = std::clamp(static_cast<int>(std::ceil(4.0 * n / codeSide)), 2, 4);
    const cv::Mat X = superResolve(crops, s);
    if (X.empty() || Clock::now() >= deadline) return {};
    cv::Mat sr;
    X.convertTo(sr, CV_8U);
    const float side = codeSide * static_cast<float>(s);
    std::string text;
    cv::Point2f q[4];
    const int left = static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now()).count());
    if (!gridFitQR(sr, n, side, std::max(1, left), text, q, known.size() >= 2 ? &known : nullptr) || text.empty()) {
        const int rest = static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now()).count());
        if (rest <= 0) return {};
        text = decodeQrFrames({sr}, n, side, known, rest, q);
    }
    if (text.empty()) return {};
    // fine-grid pixel P is the first crop's point (P + 0.5) / s - 0.5
    if (quad0)
        for (int c = 0; c < 4; ++c) quad0[c] = (q[c] + cv::Point2f(0.5f, 0.5f)) / static_cast<float>(s) - cv::Point2f(0.5f, 0.5f);
    return text;
}

Result decodeFrames(const std::vector<cv::Mat>& frames, const cr_params& p, const Models& models, StepStats* learned)
{
    if (frames.size() == 1) return decode(frames[0], p, models, learned);
    const auto t0 = Clock::now();
    const auto msSince = [&]() { return std::chrono::duration<double, std::milli>(Clock::now() - t0).count(); };
    // the first frame gets half the budget, the others share three tenths, the fusion the rest
    cr_params p0 = p;
    p0.time_budget_ms = std::max(10, p.time_budget_ms / 2);
    Result res = decode(frames[0], p0, models, learned);
    std::map<std::string, Code> codes;
    for (const auto& c : res.codes) codes.emplace(c.text, c);
    std::vector<cv::Point2d> shift(frames.size(), cv::Point2d(0, 0));
    for (size_t k = 1; k < frames.size(); ++k) {
        if (frames[k].size() != frames[0].size()) continue;
        shift[k] = frameShift(frames[0], frames[k]);
        cr_params pk = p;
        pk.time_budget_ms = std::max(10, static_cast<int>(p.time_budget_ms * 0.3 / static_cast<int>(frames.size() - 1)));
        const Result rk = decode(frames[k], pk, models, learned);
        // an extra pack seen in any frame: the most copies over, every region left unchecked
        res.copiesOver = std::max(res.copiesOver, rk.copiesOver);
        res.extraUnchecked += rk.extraUnchecked;
        res.refusedEan += rk.refusedEan;
        res.refusedAddon += rk.refusedAddon;
        for (auto c : rk.codes) {
            if (auto known = codes.find(c.text); known != codes.end()) {
                known->second.copies = std::max(known->second.copies, c.copies);
            } else {
                // position in the first frame's coordinates
                for (int i = 0; i < 4; ++i) {
                    c.x[i] -= static_cast<float>(shift[k].x);
                    c.y[i] -= static_cast<float>(shift[k].y);
                }
                codes.emplace(c.text, c);
            }
        }
    }
    // fusion: QR regions of the first frame that no frame read
    if (p.format & CR_QR) {
        std::vector<int> mods;
        std::vector<float> sides;
        std::vector<std::string> texts;
        for (const auto& kv : codes) {
            const auto& c = kv.second;
            if (c.format != CR_QR) continue;
            texts.push_back(c.text);
            if (c.modW >= 21 && c.modW == c.modH) mods.push_back(c.modW);
            float s = 0.f;
            for (int k = 0; k < 4; ++k) s += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
            sides.push_back(s / 4.f);
        }
        int n = 0;
        float side = 0.f;
        if (!mods.empty()) {
            std::nth_element(mods.begin(), mods.begin() + mods.size() / 2, mods.end());
            n = mods[mods.size() / 2];
        } else if (learned && learned->qrModules.load() >= 21)
            n = learned->qrModules.load();
        if (!sides.empty()) {
            std::nth_element(sides.begin(), sides.begin() + sides.size() / 2, sides.end());
            side = sides[sides.size() / 2];
        }
        if (n >= 21 && side > 0.f) {
            const cv::Rect bounds(0, 0, frames[0].cols, frames[0].rows);
            for (const auto& g : res.regions) {
                if (!g.fromAI || g.read) continue;
                if (msSince() > p.time_budget_ms) break;
                const cv::Point2f mid(g.box.x + g.box.width / 2.f, g.box.y + g.box.height / 2.f);
                bool near = false;
                for (const auto& kv : codes) {
                    const auto& c = kv.second;
                    near = near || cv::norm(cv::Point2f((c.x[0] + c.x[2]) / 2.f, (c.y[0] + c.y[2]) / 2.f) - mid) < 0.6f * side;
                }
                if (near) continue;
                const int m = static_cast<int>(side * 0.5f);
                const cv::Rect r0 = cv::Rect(g.box.x - m, g.box.y - m, g.box.width + 2 * m, g.box.height + 2 * m) & bounds;
                if (r0.width < side || r0.height < side) continue;
                std::vector<cv::Mat> crops;
                std::vector<cv::Point2f> offset; // crop pixel -> first frame pixel, less r0
                for (size_t k = 0; k < frames.size(); ++k) {
                    const cv::Rect rk(r0.x + static_cast<int>(std::lround(shift[k].x)), r0.y + static_cast<int>(std::lround(shift[k].y)), r0.width, r0.height);
                    if ((rk & bounds) != rk) continue;
                    crops.push_back(frames[k](rk));
                    offset.emplace_back(static_cast<float>(rk.x - r0.x - shift[k].x), static_cast<float>(rk.y - r0.y - shift[k].y));
                }
                if (crops.size() < 2) continue;
                const int left = static_cast<int>(p.time_budget_ms - msSince());
                cv::Point2f q0[4];
                int from = 0;
                OwnReadScope ids;
                std::string text = decodeQrFrames(crops, n, side, texts, std::min(left, 300), q0, &from);
                // a code below about 2.6 px per module that the fused modules do not read: its
                // region reconstructed on a finer grid from the frames' sub-pixel shifts, then read
                if (text.empty() && side < 2.6f * n && offset[0] == cv::Point2f(0, 0)) {
                    const int rest = static_cast<int>(p.time_budget_ms - msSince());
                    if (rest > 0 && !(text = superResolveQrFrames(crops, n, side, texts, std::min(rest, 200), q0)).empty()) from = 0;
                }
                if (text.empty() || codes.count(text)) continue;
                for (int i = 0; i < 4; ++i) q0[i] += offset[static_cast<size_t>(from)];
                Code c;
                c.text = text;
                c.format = CR_QR;
                c.step = CR_STEP_FRAMES;
                c.modW = c.modH = n;
                ids.apply(c);
                for (int i = 0; i < 4; ++i) {
                    c.x[i] = q0[i].x + r0.x;
                    c.y[i] = q0[i].y + r0.y;
                }
                std::vector<Code> one{c};
                finishLinear(frames[0], one, p.check_digit); // its angle (a QR: no 1D checks)
                c = one[0];
                codes.emplace(c.text, c);
                texts.push_back(c.text);
            }
        }
    }
    res.codes.clear();
    for (auto& kv : codes) res.codes.push_back(kv.second);
    res.stats.codes = static_cast<int>(res.codes.size());
    // regions and misses as the union sees them
    for (auto& g : res.regions) {
        if (g.read) continue;
        for (const auto& c : res.codes)
            if (g.box.contains(cv::Point(static_cast<int>((c.x[0] + c.x[2]) / 2.f), static_cast<int>((c.y[0] + c.y[2]) / 2.f)))) g.read = true;
    }
    res.misses.clear();
    if (p.expected_count > 0 && res.stats.codes < p.expected_count)
        for (const auto& g : res.regions)
            if (!g.read) res.misses.push_back(g.box);
    res.stats.misses = static_cast<int>(res.misses.size());
    res.stats.total_ms = static_cast<float>(msSince());
    return res;
}

} // namespace cr
