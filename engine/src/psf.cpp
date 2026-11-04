// psf.cpp - the lens and the printer as one kernel. A known code pattern is rendered through a
// point spread function (dark modules grown by the print growth, then a Gaussian blur of the
// lens) into image pixels, which gives two things the module-sampling decoders cannot:
//  - the blur of the line, measured on read codes: the sigma whose rendering fits the pixels
//    best, kept per zone of the image (a lens corner blurs more than the centre);
//  - a score for a serial-run candidate on an unread, defocused region, in pixel space: a
//    blurred code's sampled modules lose the pattern (neighbouring modules bleed into each
//    sample), while the pattern rendered through the measured blur still matches the pixels.
// Study 2026-10-27 (ml/experiments/psf.py): on the 48-bottle image the bent cap scored 0.65 by
// module sampling (rule 0.75) and 0.78 through its neighbours' kernel, lead unchanged; the
// non-code regions kept no lead at any blur.
#include "reader.hpp"

#include <opencv2/imgproc.hpp>

#include <cmath>

namespace cr {

cv::Mat homography4(const cv::Point2f s[4], const cv::Point2f d[4]); // quality.cpp

namespace {

constexpr int kSub = 10;         // sub-samples per module in the sharp pattern (growth in tenths)
constexpr float kFitPxPerModule = 4.f; // the measurement works at about this scale: sigma is in modules

float sideOf(const cv::Point2f quad[4])
{
    float s = 0.f;
    for (int k = 0; k < 4; ++k) s += static_cast<float>(cv::norm(quad[(k + 1) % 4] - quad[k]));
    return s / 4.f;
}

// the sharp pattern at kSub samples per module with a one-module margin, dark grown by `growth`
// modules each side (print growth: ink spreads)
cv::Mat sharpPattern(const uint8_t* pat, int n, float growth)
{
    cv::Mat img((n + 2) * kSub, (n + 2) * kSub, CV_32F, cv::Scalar(0.f));
    const int k = static_cast<int>(std::lround(growth * kSub));
    const cv::Rect all(0, 0, img.cols, img.rows);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            if (!pat[y * n + x]) continue;
            img(cv::Rect((x + 1) * kSub - k, (y + 1) * kSub - k, kSub + 2 * k, kSub + 2 * k) & all).setTo(1.f);
        }
    return img;
}

// which modules count, in the same frame
cv::Mat useAt(const cv::Mat& use, int n)
{
    cv::Mat img((n + 2) * kSub, (n + 2) * kSub, CV_8U, cv::Scalar(0));
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x)
            if (use.empty() || use.at<uint8_t>(y, x)) img(cv::Rect((x + 1) * kSub, (y + 1) * kSub, kSub, kSub)).setTo(1);
    return img;
}

// pattern frame -> box pixels, for the code corners TL, TR, BR, BL
cv::Mat patternToBox(const cv::Point2f quad[4], int n, const cv::Rect& box)
{
    const float a = static_cast<float>(kSub), b = static_cast<float>((n + 1) * kSub);
    const cv::Point2f src[4] = {{a, a}, {b, a}, {b, b}, {a, b}};
    cv::Point2f dst[4];
    for (int k = 0; k < 4; ++k) dst[k] = quad[k] - cv::Point2f(static_cast<float>(box.x), static_cast<float>(box.y));
    return homography4(src, dst);
}

cv::Rect boxOf(const cv::Point2f quad[4], int n, float marginModules, cv::Size size)
{
    float x0 = quad[0].x, y0 = quad[0].y, x1 = x0, y1 = y0;
    for (int k = 1; k < 4; ++k) {
        x0 = std::min(x0, quad[k].x); y0 = std::min(y0, quad[k].y);
        x1 = std::max(x1, quad[k].x); y1 = std::max(y1, quad[k].y);
    }
    const float m = marginModules * sideOf(quad) / static_cast<float>(n);
    const cv::Rect r(static_cast<int>(std::floor(x0 - m)), static_cast<int>(std::floor(y0 - m)),
                     static_cast<int>(std::ceil(x1 + m)) - static_cast<int>(std::floor(x0 - m)),
                     static_cast<int>(std::ceil(y1 + m)) - static_cast<int>(std::floor(y0 - m)));
    return r & cv::Rect(0, 0, size.width, size.height);
}

struct Sharp {
    cv::Mat image; // darkness 0..1 over the box, sharp (warped, averaged to the pixel grid)
    cv::Mat mask;  // pixels of the modules in use
};

Sharp renderSharp(const uint8_t* pat, int n, const cv::Point2f quad[4], const cv::Rect& box, float growth, const cv::Mat& use)
{
    Sharp s;
    cv::Mat hi = sharpPattern(pat, n, growth);
    // average the samples that land in one pixel before the warp picks one
    const float scale = sideOf(quad) / static_cast<float>(n * kSub); // image pixels per sample
    cv::GaussianBlur(hi, hi, cv::Size(), std::max(0.3, 0.5 / scale));
    const cv::Mat H = patternToBox(quad, n, box);
    cv::warpPerspective(hi, s.image, H, box.size(), cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    cv::warpPerspective(useAt(use, n), s.mask, H, box.size(), cv::INTER_NEAREST, cv::BORDER_CONSTANT, cv::Scalar(0));
    return s;
}

cv::Mat blurred(const cv::Mat& image, float sigmaPx)
{
    if (sigmaPx < 0.05f) return image;
    cv::Mat b;
    cv::GaussianBlur(image, b, cv::Size(), sigmaPx);
    return b;
}

// standardised correlation of two images on a mask
float maskedCorr(const cv::Mat& a, const cv::Mat& b, const cv::Mat& mask)
{
    double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
    int cnt = 0;
    for (int y = 0; y < a.rows; ++y) {
        const float* pa = a.ptr<float>(y);
        const float* pb = b.ptr<float>(y);
        const uint8_t* pm = mask.ptr<uint8_t>(y);
        for (int x = 0; x < a.cols; ++x) {
            if (!pm[x]) continue;
            sa += pa[x]; sb += pb[x]; saa += pa[x] * pa[x]; sbb += pb[x] * pb[x]; sab += pa[x] * pb[x];
            ++cnt;
        }
    }
    if (cnt < 16) return -2.f;
    const double cov = sab - sa * sb / cnt, va = saa - sa * sa / cnt, vb = sbb - sb * sb / cnt;
    return va <= 0 || vb <= 0 ? 0.f : static_cast<float>(cov / std::sqrt(va * vb));
}

// pixels ~ light - (light - dark) * darkness: the residual rms of the closed-form fit
float levelsResidual(const cv::Mat& pix, const cv::Mat& darkness, const cv::Mat& mask)
{
    double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
    int cnt = 0;
    for (int y = 0; y < pix.rows; ++y) {
        const float* pp = pix.ptr<float>(y);
        const float* pd = darkness.ptr<float>(y);
        const uint8_t* pm = mask.ptr<uint8_t>(y);
        for (int x = 0; x < pix.cols; ++x) {
            if (!pm[x]) continue;
            sx += pd[x]; sy += pp[x]; sxx += pd[x] * pd[x]; sxy += pd[x] * pp[x]; syy += pp[x] * pp[x];
            ++cnt;
        }
    }
    if (cnt < 16) return -1.f;
    const double vx = sxx - sx * sx / cnt;
    if (vx <= 1e-9) return -1.f;
    const double slope = (sxy - sx * sy / cnt) / vx, icpt = (sy - slope * sx) / cnt;
    const double rss = syy - 2 * slope * sxy - 2 * icpt * sy + slope * slope * sxx + 2 * slope * icpt * sx + icpt * icpt * cnt;
    return static_cast<float>(std::sqrt(std::max(0.0, rss / cnt)));
}

cv::Mat asFloat(const cv::Mat& m)
{
    if (m.type() == CV_32F) return m;
    cv::Mat f;
    m.convertTo(f, CV_32F);
    return f;
}

} // namespace

float psfScore(const cv::Mat& img, const cv::Point2f quad[4], const uint8_t* pat, int n, const cv::Mat& use, float growth, float sigmaPx)
{
    const cv::Rect box = boxOf(quad, n, 1.f, img.size());
    if (box.width < 8 || box.height < 8) return -2.f;
    const Sharp s = renderSharp(pat, n, quad, box, growth, use);
    // dark modules are low pixel values: the correlation with the darkness is negative for a match
    return -maskedCorr(asFloat(img(box)), blurred(s.image, sigmaPx), s.mask);
}

float measurePsfSigma(const cv::Mat& img, const cv::Point2f quad[4], const uint8_t* pat, int n, float growth)
{
    // a big code is measured at about 4 px per module: the cost of the blurs would grow with its
    // area, and sigma in modules does not change with the scale
    const float mpx0 = sideOf(quad) / static_cast<float>(n);
    if (mpx0 > kFitPxPerModule * 1.25f) {
        const cv::Rect big = boxOf(quad, n, 1.5f, img.size());
        if (big.width < 8 || big.height < 8) return -1.f;
        const double f = kFitPxPerModule / mpx0;
        cv::Mat small;
        cv::resize(asFloat(img(big)), small, cv::Size(), f, f, cv::INTER_AREA);
        cv::Point2f q[4];
        for (int k = 0; k < 4; ++k) q[k] = (quad[k] - cv::Point2f(static_cast<float>(big.x), static_cast<float>(big.y))) * static_cast<float>(f);
        return measurePsfSigma(small, q, pat, n, growth);
    }
    const cv::Rect box = boxOf(quad, n, 1.5f, img.size());
    if (box.width < 8 || box.height < 8) return -1.f;
    const float mpx = mpx0;
    const Sharp s = renderSharp(pat, n, quad, box, growth, cv::Mat());
    const cv::Mat pix = asFloat(img(box));
    // the whole box counts (the quiet zone around the code constrains the blur as well)
    const cv::Mat all(box.size(), CV_8U, cv::Scalar(1));
    const float sigmas[] = {0.f, 0.12f, 0.2f, 0.28f, 0.38f, 0.5f, 0.65f, 0.85f, 1.1f};
    float bestRms = -1.f, bestSigma = 0.f, sharpRms = -1.f;
    cv::Mat bestRender;
    for (const float sm : sigmas) {
        const cv::Mat r = blurred(s.image, sm * mpx);
        const float rms = levelsResidual(pix, r, all);
        if (rms < 0) return -1.f;
        if (sm == 0.f) sharpRms = rms;
        if (bestRms < 0 || rms < bestRms) { bestRms = rms; bestSigma = sm; bestRender = r; }
    }
    // a fit no better than the sharp pattern says nothing about the blur; the widest blur of the
    // grid or a rendering that barely correlates with the pixels means wrong corners or pattern
    if (bestSigma == 0.f || bestSigma >= sigmas[sizeof(sigmas) / sizeof(sigmas[0]) - 1] || bestRms > sharpRms * 0.98f) return -1.f;
    if (-maskedCorr(pix, bestRender, all) < 0.8f) return -1.f;
    return bestSigma;
}

} // namespace cr
