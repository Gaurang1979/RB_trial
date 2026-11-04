// calibration.cpp - what a verifier knows about its own optics, for the print grades (after the
// calibration steps of NI Vision and DALSA verification tools, and ISO/IEC 15415 / 15416):
//  - flat field: one picture of a plain white card under the line's lighting; every grey level is
//    divided by the card's (smoothed) level at that point, so uneven light does not lower a grade;
//  - reflectance: a card with two patches of known reflectance (printed on the card, e.g. 5% and
//    85%) maps flat-fielded grey levels to reflectance %, the unit the ISO thresholds use;
//  - scale and lens: mm per pixel at the case's top, and the lens's radial / tangential distortion
//    from a chessboard (OpenCV), so module sizes come out in mm (the X-dimension).
// Without them the grades stay camera-relative (process control), as before.
#include "reader.hpp"

#include <opencv2/imgproc.hpp>
#if __has_include(<opencv2/calib.hpp>) && __has_include(<opencv2/objdetect.hpp>) && defined(CR_HAVE_CALIB)
#include <opencv2/calib.hpp>
#include <opencv2/objdetect.hpp>
#define CR_CALIB_OK 1
#endif

#include <algorithm>
#include <cmath>

namespace cr {

namespace {
constexpr int kFlatShrink = 8; // the flat field is kept at an eighth of the resolution: light varies slowly
}

bool setFlatField(Calibration& c, const cv::Mat& white)
{
    if (white.empty()) {
        c.flatGain.release();
        return true;
    }
    cv::Mat f, s;
    white.convertTo(f, CV_32F);
    cv::resize(f, s, cv::Size(std::max(1, f.cols / kFlatShrink), std::max(1, f.rows / kFlatShrink)), 0, 0, cv::INTER_AREA);
    cv::GaussianBlur(s, s, cv::Size(), 3.0); // the card's paper texture and dust are not light
    double lo = 0, hi = 0;
    cv::minMaxLoc(s, &lo, &hi);
    if (hi < 40.0 || lo < hi * 0.2) return false; // not a white card under working light
    // gain: brings every point to the card's brightest level
    c.flatGain = static_cast<float>(hi) / cv::max(s, 1.0);
    c.flatSize = white.size();
    return true;
}

float flatCorrect(const Calibration& c, cv::Point2f p, float grey)
{
    if (c.flatGain.empty() || c.flatSize.width <= 0) return grey;
    const float fx = p.x * c.flatGain.cols / c.flatSize.width, fy = p.y * c.flatGain.rows / c.flatSize.height;
    const int x = std::clamp(static_cast<int>(fx), 0, c.flatGain.cols - 1), y = std::clamp(static_cast<int>(fy), 0, c.flatGain.rows - 1);
    return grey * c.flatGain.at<float>(y, x);
}

float toReflectance(const Calibration& c, cv::Point2f p, float grey)
{
    const float g = flatCorrect(c, p, grey);
    if (!c.reflectance) return g / 255.f * 100.f; // camera-relative: grey as a share of full scale
    // the two card patches: a straight line through (grey, reflectance)
    const float t = (g - c.greyDark) / std::max(1e-3f, c.greyLight - c.greyDark);
    return c.reflDark + t * (c.reflLight - c.reflDark);
}

cv::Point2f undistort(const Calibration& c, cv::Point2f p)
{
    if (c.fx <= 0.f || c.fy <= 0.f) return p;
    // the inverse of the Brown-Conrady model by fixed-point iteration (as OpenCV undistortPoints)
    const float x0 = (p.x - c.cx) / c.fx, y0 = (p.y - c.cy) / c.fy;
    float x = x0, y = y0;
    for (int it = 0; it < 8; ++it) {
        const float r2 = x * x + y * y;
        const float radial = 1 + c.k1 * r2 + c.k2 * r2 * r2 + c.k3 * r2 * r2 * r2;
        const float dx = 2 * c.p1 * x * y + c.p2 * (r2 + 2 * x * x), dy = c.p1 * (r2 + 2 * y * y) + 2 * c.p2 * x * y;
        x = (x0 - dx) / radial;
        y = (y0 - dy) / radial;
    }
    return {x * c.fx + c.cx, y * c.fy + c.cy};
}

float lengthMm(const Calibration& c, cv::Point2f a, cv::Point2f b)
{
    if (c.mmPerPx <= 0.f) return 0.f;
    return static_cast<float>(cv::norm(undistort(c, a) - undistort(c, b))) * c.mmPerPx;
}

int calibrateChessboard(const std::vector<cv::Mat>& images, int cols, int rows, float squareMm, Calibration& c, float* rmsPx)
{
#ifdef CR_CALIB_OK
    std::vector<std::vector<cv::Point3f>> obj;
    std::vector<std::vector<cv::Point2f>> img;
    std::vector<cv::Point3f> board;
    for (int y = 0; y < rows; ++y)
        for (int x = 0; x < cols; ++x) board.emplace_back(x * squareMm, y * squareMm, 0.f);
    cv::Size size;
    float pxPerSquare = 0.f;
    for (const auto& im : images) {
        std::vector<cv::Point2f> corners;
        if (!cv::findChessboardCorners(im, cv::Size(cols, rows), corners, cv::CALIB_CB_ADAPTIVE_THRESH | cv::CALIB_CB_NORMALIZE_IMAGE)) continue;
        cv::cornerSubPix(im, corners, cv::Size(5, 5), cv::Size(-1, -1), cv::TermCriteria(cv::TermCriteria::EPS + cv::TermCriteria::COUNT, 30, 0.01));
        img.push_back(corners);
        obj.push_back(board);
        size = im.size();
    }
    if (img.empty()) return -1;
    cv::Mat K, D;
    std::vector<cv::Mat> rv, tv;
    double rms = 1e9;
    if (img.size() >= 3) rms = cv::calibrateCamera(obj, img, size, K, D, rv, tv);
    // three or more views ask for a lens model: one that reprojects worse than kMaxLensRmsPx is refused
    // (-3, take the views again), never quietly replaced by the scale alone, so a poor model is
    // never used for sizes in mm. Fewer than three views give the scale only (from the corner spacing)
    constexpr double kMaxRmsPx = kMaxLensRmsPx;
    if (img.size() >= 3 && rms > kMaxRmsPx) {
        if (rmsPx) *rmsPx = static_cast<float>(rms);
        return -3;
    }
    if (rms > kMaxRmsPx) {
        K = cv::Mat::zeros(3, 3, CV_64F);
        D = cv::Mat::zeros(1, 5, CV_64F);
    }
    c.fx = static_cast<float>(K.at<double>(0, 0)); c.fy = static_cast<float>(K.at<double>(1, 1));
    c.cx = static_cast<float>(K.at<double>(0, 2)); c.cy = static_cast<float>(K.at<double>(1, 2));
    const auto d = [&](int i) { return i < static_cast<int>(D.total()) ? static_cast<float>(D.at<double>(i)) : 0.f; };
    c.k1 = d(0); c.k2 = d(1); c.p1 = d(2); c.p2 = d(3); c.k3 = d(4);
    // scale at the board's plane (the case top): the undistorted corner spacing of the last view
    double sum = 0;
    int n = 0;
    const auto& last = img.back();
    for (int y = 0; y < rows; ++y)
        for (int x = 0; x + 1 < cols; ++x, ++n) sum += cv::norm(undistort(c, last[static_cast<size_t>(y * cols + x + 1)]) - undistort(c, last[static_cast<size_t>(y * cols + x)]));
    pxPerSquare = n ? static_cast<float>(sum / n) : 0.f;
    c.mmPerPx = pxPerSquare > 0.f ? squareMm / pxPerSquare : 0.f;
    if (rmsPx) *rmsPx = rms > kMaxRmsPx ? -1.f : static_cast<float>(rms); // -1: scale only, no lens model
    return static_cast<int>(img.size());
#else
    (void)images; (void)cols; (void)rows; (void)squareMm; (void)c; (void)rmsPx;
    return -2; // built without OpenCV's calib / objdetect modules
#endif
}

} // namespace cr
