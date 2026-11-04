// geometry_truth.hpp - the geometry truth set (study 04a, T): a label rendered at a known sub-pixel
// pose, with blur, noise, light change and occlusion, so a tool's pose can be compared with the truth.
//
// The label is drawn K times larger than the image, turned and shifted at that size, then averaged
// down K x K (a camera pixel integrates the light that falls on it), so edges are anti-aliased and the
// pose is known to about 1/(2K) px before averaging, far better after. Coordinates are OpenCV pixel
// centres: pixel (i, j) is the point (i, j). Turns are counter-clockwise on screen (vs_locate.h).

#pragma once

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <random>
#include <string>

namespace truth {

constexpr int kLabelW = 400, kLabelH = 240;
const cv::Point2d kLabelCentre((kLabelW - 1) / 2.0, (kLabelH - 1) / 2.0);

struct Pose {
    double x = 0, y = 0;   // where the label centre lies in the image
    double deg = 0;        // turn, counter-clockwise
};

struct Degrade {
    double blur = 0;                 // Gaussian sigma, px (0 = none)
    double noise = 0;                // sigma, grey levels
    double gain = 1, offset = 0;     // light: v' = gain * v + offset
    double rampX = 0, rampY = 0;     // light gradient: grey levels added from the left to the right edge, top to bottom
    cv::Rect occluder;               // something in front of the label (image pixels); empty = none
    double occluderGrey = 120;
    unsigned seed = 1;
    std::string lot = "LOT A12345";  // the first text line: variable print, as a lot number
};

// a point of the label (label pixel coordinates) in the image when the label is at pose p, drawn
// size times larger than kLabelW x kLabelH image pixels
inline cv::Point2d toImage(const Pose& p, cv::Point2d L, int size = 1)
{
    const double a = p.deg * CV_PI / 180.0, c = std::cos(a), s = std::sin(a);
    const cv::Point2d d = size * (L - kLabelCentre);
    return {p.x + c * d.x + s * d.y, p.y - s * d.x + c * d.y};
}

// the label, K times larger: border, bars of 2-6 px on an 8 px pitch, two lines of text, a ring and a dot
inline cv::Mat labelHi(int K, const std::string& lot = "LOT A12345")
{
    cv::Mat m(kLabelH * K, kLabelW * K, CV_8U, cv::Scalar(215));
    auto R = [K](int x, int y, int w, int h) { return cv::Rect(x * K, y * K, w * K, h * K); };
    cv::rectangle(m, R(0, 0, kLabelW, kLabelH), 40, 3 * K);
    for (int i = 0; i < 24; ++i) cv::rectangle(m, R(30 + i * 8, 30, 2 + (i * 7) % 5, 90), 20, cv::FILLED);
    cv::putText(m, lot, {30 * K, 170 * K}, cv::FONT_HERSHEY_SIMPLEX, 1.0 * K, 30, 2 * K, cv::LINE_AA);
    cv::putText(m, "EXP 09/28", {30 * K, 215 * K}, cv::FONT_HERSHEY_SIMPLEX, 0.9 * K, 30, 2 * K, cv::LINE_AA);
    cv::circle(m, {320 * K, 75 * K}, 40 * K, 35, 4 * K, cv::LINE_AA);
    cv::circle(m, {320 * K, 180 * K}, 20 * K, 25, cv::FILLED, cv::LINE_AA);
    return m;
}

// a w x h image of background bg holding the label at pose p, degraded by d; size: the label is drawn
// size times larger (a large pattern on a 20 MP camera)
inline cv::Mat render(const Pose& p, const Degrade& d, int w = 2448, int h = 2048, int bg = 60, int K = 8, int size = 1)
{
    static thread_local int cachedK = 0;
    static thread_local std::string cachedLot;
    static thread_local cv::Mat hi;
    if (cachedK != K * size || cachedLot != d.lot) { hi = labelHi(K * size, d.lot); cachedK = K * size; cachedLot = d.lot; }

    // the label's box in the image, whole pixels, with a margin
    cv::Rect2d box;
    for (const cv::Point2d L : {cv::Point2d(-0.5, -0.5), cv::Point2d(kLabelW - 0.5, -0.5),
                                cv::Point2d(-0.5, kLabelH - 0.5), cv::Point2d(kLabelW - 0.5, kLabelH - 0.5)}) {
        const cv::Point2d P = toImage(p, L, size);
        box = box.area() > 0 ? box | cv::Rect2d(P, cv::Size2d(1e-9, 1e-9)) : cv::Rect2d(P, cv::Size2d(1e-9, 1e-9));
    }
    const cv::Rect roi = cv::Rect(int(std::floor(box.x)) - 3, int(std::floor(box.y)) - 3, int(std::ceil(box.width)) + 7,
                                  int(std::ceil(box.height)) + 7) & cv::Rect(0, 0, w, h);

    cv::Mat img(h, w, CV_32F, cv::Scalar(bg));
    if (roi.area() > 0) {
        // large label pixel l is the label point (l + 0.5) / (K size) - 0.5; a point P of the image is
        // large pixel K (P + 0.5) - 0.5 - K roi.tl in the large copy of the roi
        const double a = p.deg * CV_PI / 180.0, c = std::cos(a), s = std::sin(a);
        const cv::Matx22d Rm(c, s, -s, c);
        const cv::Vec2d u = Rm * cv::Vec2d(0.5 - K * size * (0.5 + kLabelCentre.x), 0.5 - K * size * (0.5 + kLabelCentre.y));
        const cv::Matx23d M(c, s, u[0] + K * p.x + K * 0.5 - 0.5 - K * roi.x,
                            -s, c, u[1] + K * p.y + K * 0.5 - 0.5 - K * roi.y);
        cv::Mat big(roi.height * K, roi.width * K, CV_8U, cv::Scalar(bg));
        cv::warpAffine(hi, big, M, big.size(), cv::INTER_LINEAR, cv::BORDER_TRANSPARENT);
        cv::Mat small;
        cv::resize(big, small, roi.size(), 0, 0, cv::INTER_AREA);
        small.convertTo(img(roi), CV_32F);
    }
    if (!d.occluder.empty()) img(d.occluder & cv::Rect(0, 0, w, h)).setTo(d.occluderGrey);
    img = img * d.gain + d.offset;
    if (d.rampX != 0 || d.rampY != 0)
        for (int y = 0; y < h; ++y) {
            float* row = img.ptr<float>(y);
            const double ry = d.rampY * (y / double(h) - 0.5);
            for (int x = 0; x < w; ++x) row[x] += float(d.rampX * (x / double(w) - 0.5) + ry);
        }
    if (d.blur > 0) {
        // the background is flat, so blurring round the label (and the occluder) is enough
        cv::Rect br = roi;
        if (!d.occluder.empty()) br |= d.occluder;
        br = cv::Rect(br.x - 12, br.y - 12, br.width + 24, br.height + 24) & cv::Rect(0, 0, w, h);
        cv::Mat part = img(br).clone();
        cv::GaussianBlur(part, img(br), {}, d.blur, d.blur, cv::BORDER_REPLICATE);
    }
    if (d.noise > 0) {
        cv::Mat n(h, w, CV_32F);
        cv::RNG rng(d.seed);
        rng.fill(n, cv::RNG::NORMAL, 0, d.noise);
        img += n;
    }
    cv::Mat out;
    img.convertTo(out, CV_8U);   // rounds and saturates, as a camera does
    return out;
}

} // namespace truth
