/*
 * phase.cpp - Implementation of the Log-Polar Fourier Sub-Pixel Phase Correlator Engine.
 */
#include "vs_phase.h"

#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>

#include <cmath>
#include <cstring>
#include <vector>
#include <algorithm>

namespace {

// Shift zero-frequency DC component to image center
void fft_shift(cv::Mat& mag) {
    int cx = mag.cols / 2;
    int cy = mag.rows / 2;

    cv::Mat q0(mag, cv::Rect(0, 0, cx, cy));
    cv::Mat q1(mag, cv::Rect(cx, 0, cx, cy));
    cv::Mat q2(mag, cv::Rect(0, cy, cx, cy));
    cv::Mat q3(mag, cv::Rect(cx, cy, cx, cy));

    cv::Mat tmp;
    q0.copyTo(tmp);
    q3.copyTo(q0);
    tmp.copyTo(q3);

    q1.copyTo(tmp);
    q2.copyTo(q1);
    tmp.copyTo(q2);
}

// Construct 2x3 affine rotation matrix around center
cv::Mat get_rot_matrix_2d(cv::Point2f center, double angle_deg, double scale) {
    double rad = angle_deg * 3.141592653589793 / 180.0;
    double alpha = scale * std::cos(rad);
    double beta = scale * std::sin(rad);

    cv::Mat M(2, 3, CV_64F);
    M.at<double>(0, 0) = alpha;
    M.at<double>(0, 1) = beta;
    M.at<double>(0, 2) = (1.0 - alpha) * center.x - beta * center.y;
    M.at<double>(1, 0) = -beta;
    M.at<double>(1, 1) = alpha;
    M.at<double>(1, 2) = beta * center.x + (1.0 - alpha) * center.y;
    return M;
}

// Compute cross-power spectrum and sub-pixel peak
bool compute_phase_correlation(
    const cv::Mat& F1,
    const cv::Mat& F2,
    float& shift_x,
    float& shift_y,
    float& peak_val,
    float& psr)
{
    if (F1.empty() || F2.empty() || F1.size() != F2.size()) return false;

    // F1 * conj(F2)
    cv::Mat F1_conj_F2;
    cv::mulSpectrums(F1, F2, F1_conj_F2, 0, true);

    std::vector<cv::Mat> planes;
    cv::split(F1_conj_F2, planes);
    cv::Mat mag;
    cv::magnitude(planes[0], planes[1], mag);
    mag += 1e-7f;

    planes[0] /= mag;
    planes[1] /= mag;

    cv::Mat R;
    cv::merge(planes, R);

    cv::Mat corr;
    cv::idft(R, corr, cv::DFT_REAL_OUTPUT | cv::DFT_SCALE);

    double min_v, max_v;
    cv::Point min_loc, max_loc;
    cv::minMaxLoc(corr, &min_v, &max_v, &min_loc, &max_loc);

    peak_val = static_cast<float>(max_v);

    const int N = corr.cols;
    const int M = corr.rows;
    const int x0 = max_loc.x;
    const int y0 = max_loc.y;

    // Cyclic shift unwrap
    int dx = (x0 > N / 2) ? (x0 - N) : x0;
    int dy = (y0 > M / 2) ? (y0 - M) : y0;

    // Parabolic sub-pixel interpolation
    int xm = (x0 - 1 + N) % N;
    int xp = (x0 + 1) % N;
    int ym = (y0 - 1 + M) % M;
    int yp = (y0 + 1) % M;

    float c00 = corr.at<float>(y0, x0);
    float cm0 = corr.at<float>(y0, xm);
    float cp0 = corr.at<float>(y0, xp);
    float c0m = corr.at<float>(ym, x0);
    float c0p = corr.at<float>(yp, x0);

    float denom_x = 2.0f * (2.0f * c00 - cm0 - cp0);
    float sub_x = (std::abs(denom_x) > 1e-6f) ? (cp0 - cm0) / denom_x : 0.0f;

    float denom_y = 2.0f * (2.0f * c00 - c0m - c0p);
    float sub_y = (std::abs(denom_y) > 1e-6f) ? (c0p - c0m) / denom_y : 0.0f;

    // Shift of image 2 relative to image 1:
    // When F1 * conj(F2) is computed, peak at delta corresponds to shift = -delta
    shift_x = -(static_cast<float>(dx) + std::clamp(sub_x, -0.5f, 0.5f));
    shift_y = -(static_cast<float>(dy) + std::clamp(sub_y, -0.5f, 0.5f));

    // Compute Peak-to-Sidelobe Ratio (PSR) outside a 9x9 neighborhood
    double sum_sidelobe = 0.0;
    double sq_sidelobe = 0.0;
    int count_sidelobe = 0;
    const int r = 4;

    for (int y = 0; y < M; ++y) {
        int dy_circ = std::abs(y - y0);
        if (dy_circ > M / 2) dy_circ = M - dy_circ;
        if (dy_circ <= r) continue;

        const float* row = corr.ptr<float>(y);
        for (int x = 0; x < N; ++x) {
            int dx_circ = std::abs(x - x0);
            if (dx_circ > N / 2) dx_circ = N - dx_circ;
            if (dx_circ <= r) continue;

            float val = row[x];
            sum_sidelobe += val;
            sq_sidelobe += val * val;
            ++count_sidelobe;
        }
    }

    if (count_sidelobe > 0) {
        double mean = sum_sidelobe / count_sidelobe;
        double var = (sq_sidelobe / count_sidelobe) - (mean * mean);
        double stddev = (var > 1e-8) ? std::sqrt(var) : 1e-4;
        psr = static_cast<float>((peak_val - mean) / stddev);
    } else {
        psr = 1.0f;
    }

    return true;
}

// Convert image to Hann-windowed complex DFT
void prepare_dft(const cv::Mat& src, const cv::Mat& hann, cv::Mat& dst_dft) {
    cv::Mat float_img;
    src.convertTo(float_img, CV_32F);

    if (!hann.empty() && hann.size() == float_img.size()) {
        cv::multiply(float_img, hann, float_img);
    }

    cv::dft(float_img, dst_dft, cv::DFT_COMPLEX_OUTPUT);
}

// Extract log-polar magnitude spectrum (Size: width=radius, height=angular_bins)
void compute_log_polar_spectrum(const cv::Mat& F, cv::Mat& log_polar, int angular_bins = 360) {
    std::vector<cv::Mat> planes;
    cv::split(F, planes);
    cv::Mat mag;
    cv::magnitude(planes[0], planes[1], mag);
    mag += 1.0f;
    cv::log(mag, mag);
    fft_shift(mag);

    cv::Point2f center(static_cast<float>(mag.cols) / 2.0f, static_cast<float>(mag.rows) / 2.0f);
    double max_radius = std::min(center.x, center.y);

    // X is radius, Y is angle (0..360)
    cv::warpPolar(mag, log_polar, cv::Size(static_cast<int>(max_radius), angular_bins), center, max_radius, cv::WARP_POLAR_LOG);
}

} // namespace

struct vs_phase_correlator {
    vs_phase_params params{};
    int32_t width = 0;
    int32_t height = 0;
    cv::Mat template_img;
    cv::Mat hann_window;
    cv::Mat template_F;
    cv::Mat template_logpolar_F;
};

extern "C" {

void vs_phase_default_params(vs_phase_params* p) {
    if (!p) return;
    std::memset(p, 0, sizeof(vs_phase_params));
    p->struct_size = sizeof(vs_phase_params);
    p->estimate_rotation = 1;
    p->estimate_scale = 0;
    p->min_correlation_score = 0.15f;
    p->min_psr = 7.0f;
    p->min_scale = 0.85f;
    p->max_scale = 1.15f;
    p->apply_hann_window = 1;
}

vs_phase_correlator* vs_phase_create(const vs_phase_params* p) {
    auto* c = new (std::nothrow) vs_phase_correlator();
    if (!c) return nullptr;

    if (p && p->struct_size == sizeof(vs_phase_params)) {
        c->params = *p;
    } else {
        vs_phase_default_params(&c->params);
    }

    return c;
}

void vs_phase_destroy(vs_phase_correlator* c) {
    delete c;
}

int32_t vs_phase_teach(
    vs_phase_correlator* c,
    const uint8_t* golden_template,
    int32_t width,
    int32_t height,
    int32_t stride)
{
    if (!c || !golden_template || width <= 4 || height <= 4 || stride < width) return -1;

    c->width = width;
    c->height = height;

    c->template_img = cv::Mat(height, width, CV_8U);
    for (int y = 0; y < height; ++y) {
        std::memcpy(c->template_img.ptr(y), golden_template + y * stride, width);
    }

    if (c->params.apply_hann_window) {
        cv::createHanningWindow(c->hann_window, cv::Size(width, height), CV_32F);
    } else {
        c->hann_window.release();
    }

    prepare_dft(c->template_img, c->hann_window, c->template_F);

    if (c->params.estimate_rotation) {
        cv::Mat log_polar;
        compute_log_polar_spectrum(c->template_F, log_polar, 360);
        cv::Mat log_hann;
        cv::createHanningWindow(log_hann, log_polar.size(), CV_32F);
        prepare_dft(log_polar, log_hann, c->template_logpolar_F);
    }

    return 0;
}

int32_t vs_phase_match(
    const vs_phase_correlator* c,
    const uint8_t* test_image,
    int32_t width,
    int32_t height,
    int32_t stride,
    vs_phase_result* result)
{
    if (!c || !test_image || !result || width != c->width || height != c->height) return -1;

    std::memset(result, 0, sizeof(vs_phase_result));
    result->struct_size = sizeof(vs_phase_result);
    result->scale = 1.0f;

    cv::Mat live_img(height, width, CV_8U);
    for (int y = 0; y < height; ++y) {
        std::memcpy(live_img.ptr(y), test_image + y * stride, width);
    }

    cv::Mat live_F;
    prepare_dft(live_img, c->hann_window, live_F);

    float detected_rot = 0.0f;

    // 1. Rotation Estimation via Fourier-Mellin Log-Polar
    if (c->params.estimate_rotation && !c->template_logpolar_F.empty()) {
        cv::Mat live_log_polar;
        compute_log_polar_spectrum(live_F, live_log_polar, 360);

        cv::Mat log_hann;
        cv::createHanningWindow(log_hann, live_log_polar.size(), CV_32F);
        cv::Mat live_logpolar_F;
        prepare_dft(live_log_polar, log_hann, live_logpolar_F);

        float rot_shift_x = 0.0f, rot_shift_y = 0.0f, rot_peak = 0.0f, rot_psr = 0.0f;
        compute_phase_correlation(c->template_logpolar_F, live_logpolar_F, rot_shift_x, rot_shift_y, rot_peak, rot_psr);

        // Angle is along Y axis (360 bins)
        // Due to image coordinate inversion and central Fourier symmetry,
        // test the 4 candidate reflection angles:
        float a0 = rot_shift_y;
        float test_angles[4] = { a0, -a0, 180.0f - a0, a0 - 180.0f };

        float best_angle = 0.0f;
        float best_disambig_peak = -1.0f;
        cv::Point2f center(static_cast<float>(width) / 2.0f, static_cast<float>(height) / 2.0f);

        for (int a = 0; a < 4; ++a) {
            float ang = test_angles[a];
            while (ang > 180.0f) ang -= 360.0f;
            while (ang <= -180.0f) ang += 360.0f;

            cv::Mat rot_mat = get_rot_matrix_2d(center, ang, 1.0);
            cv::Mat rotated_template;
            cv::warpAffine(c->template_img, rotated_template, rot_mat, cv::Size(width, height), cv::INTER_LINEAR, cv::BORDER_REPLICATE);

            cv::Mat rot_F;
            prepare_dft(rotated_template, c->hann_window, rot_F);

            float sx = 0.0f, sy = 0.0f, peak = 0.0f, psr = 0.0f;
            compute_phase_correlation(rot_F, live_F, sx, sy, peak, psr);

            if (peak > best_disambig_peak) {
                best_disambig_peak = peak;
                best_angle = ang;
            }
        }

        detected_rot = best_angle;
    }

    // 2. Translation Estimation
    float shift_x = 0.0f, shift_y = 0.0f, peak = 0.0f, psr = 0.0f;

    if (std::abs(detected_rot) > 0.05f) {
        cv::Point2f center(static_cast<float>(width) / 2.0f, static_cast<float>(height) / 2.0f);
        cv::Mat rot_mat = get_rot_matrix_2d(center, detected_rot, 1.0);
        cv::Mat rotated_template;
        cv::warpAffine(c->template_img, rotated_template, rot_mat, cv::Size(width, height), cv::INTER_LINEAR, cv::BORDER_REPLICATE);

        cv::Mat rot_F;
        prepare_dft(rotated_template, c->hann_window, rot_F);
        compute_phase_correlation(rot_F, live_F, shift_x, shift_y, peak, psr);
    } else {
        compute_phase_correlation(c->template_F, live_F, shift_x, shift_y, peak, psr);
    }

    result->shift_x = shift_x;
    result->shift_y = shift_y;
    result->rotation_deg = detected_rot;
    result->correlation_score = peak;
    result->psr = psr;
    result->is_found = (peak >= c->params.min_correlation_score && psr >= c->params.min_psr) ? 1 : 0;

    return 0;
}

int32_t vs_phase_correlate_translation(
    const uint8_t* img1,
    int32_t stride1,
    const uint8_t* img2,
    int32_t stride2,
    int32_t width,
    int32_t height,
    float* shift_x,
    float* shift_y,
    float* score,
    float* psr)
{
    if (!img1 || !img2 || width <= 4 || height <= 4) return -1;

    cv::Mat m1(height, width, CV_8U);
    cv::Mat m2(height, width, CV_8U);
    for (int y = 0; y < height; ++y) {
        std::memcpy(m1.ptr(y), img1 + y * stride1, width);
        std::memcpy(m2.ptr(y), img2 + y * stride2, width);
    }

    cv::Mat hann;
    cv::createHanningWindow(hann, cv::Size(width, height), CV_32F);

    cv::Mat F1, F2;
    prepare_dft(m1, hann, F1);
    prepare_dft(m2, hann, F2);

    float sx = 0.0f, sy = 0.0f, p = 0.0f, s = 0.0f;
    compute_phase_correlation(F1, F2, sx, sy, p, s);

    if (shift_x) *shift_x = sx;
    if (shift_y) *shift_y = sy;
    if (score) *score = p;
    if (psr) *psr = s;

    return 0;
}

} // extern "C"
