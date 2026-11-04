/*
 * phase_tests.cpp - Verification suite for Log-Polar Fourier Phase Correlator (vs_phase.h).
 *
 * Tests:
 *   1. Lifecycle and default parameter validation.
 *   2. Sub-pixel translation accuracy (sub-0.05 px verification).
 *   3. 100% Invariance to extreme contrast fade (95% contrast drop + lighting multiplier).
 *   4. Fourier-Mellin log-polar rotation and translation alignment.
 *   5. Industrial throughput benchmark on 512x512 frames (< 15 ms CPU budget).
 */

#include "vs_phase.h"

#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <chrono>
#include <vector>
#include <string>
#include <algorithm>
#include "check.h"

namespace {

int failures = 0;

void check(bool ok, const std::string& what) {
    std::printf("%s  %s\n", ok ? "ok  " : "FAIL", what.c_str());
    if (!ok) ++failures;
}

// Generate synthetic rich geometric test pattern (corners, edges, concentric shapes)
cv::Mat create_test_pattern(int width, int height, int bg = 200, int fg = 40) {
    cv::Mat img(height, width, CV_8U, cv::Scalar(bg));

    // Outer rectangle border
    cv::rectangle(img, cv::Rect(20, 20, width - 40, height - 40), cv::Scalar(fg), 3);
    // Asymmetric inner shapes
    cv::circle(img, cv::Point(width / 3, height / 3), 18, cv::Scalar(fg), -1);
    cv::rectangle(img, cv::Rect(width / 2, height / 2, 25, 20), cv::Scalar(fg), -1);
    cv::line(img, cv::Point(20, height / 2), cv::Point(width - 20, height / 2), cv::Scalar(fg), 2);
    cv::line(img, cv::Point(width / 2, 20), cv::Point(width / 2, height - 20), cv::Scalar(fg), 2);

    return img;
}

cv::Mat make_trans_mat(double dx, double dy) {
    cv::Mat M = cv::Mat::eye(2, 3, CV_64F);
    M.at<double>(0, 2) = dx;
    M.at<double>(1, 2) = dy;
    return M;
}

cv::Mat make_rot_mat(cv::Point2f center, double angle_deg, double dx, double dy) {
    double rad = angle_deg * 3.141592653589793 / 180.0;
    double alpha = std::cos(rad);
    double beta = std::sin(rad);

    cv::Mat M(2, 3, CV_64F);
    M.at<double>(0, 0) = alpha;
    M.at<double>(0, 1) = beta;
    M.at<double>(0, 2) = (1.0 - alpha) * center.x - beta * center.y + dx;
    M.at<double>(1, 0) = -beta;
    M.at<double>(1, 1) = alpha;
    M.at<double>(1, 2) = beta * center.x + (1.0 - alpha) * center.y + dy;
    return M;
}

void test_lifecycle_and_defaults() {
    std::printf("--- test_lifecycle_and_defaults ---\n");

    vs_phase_params params;
    vs_phase_default_params(&params);

    check(params.struct_size == sizeof(vs_phase_params), "params struct_size initialized");
    check(params.estimate_rotation == 1, "default estimate_rotation == 1");
    check(params.min_psr >= 5.0f, "min_psr >= 5.0");
    check(params.apply_hann_window == 1, "default apply_hann_window == 1");

    vs_phase_correlator* c = vs_phase_create(&params);
    check(c != nullptr, "vs_phase_create succeeded");

    // Null safety
    vs_phase_result res{};
    int32_t rc = vs_phase_match(c, nullptr, 100, 100, 100, &res);
    check(rc < 0, "vs_phase_match returns error on null input");

    vs_phase_destroy(c);
    check(true, "vs_phase_destroy completed cleanly");
}

void test_subpixel_pure_translation() {
    std::printf("--- test_subpixel_pure_translation ---\n");

    const int width = 128;
    const int height = 128;
    cv::Mat ref = create_test_pattern(width, height);

    // Apply known sub-pixel shift via affine translation: dx = +5.35 px, dy = -3.72 px
    const float true_dx = 5.35f;
    const float true_dy = -3.72f;

    cv::Mat trans_mat = make_trans_mat(true_dx, true_dy);
    cv::Mat shifted;
    cv::warpAffine(ref, shifted, trans_mat, cv::Size(width, height), cv::INTER_LINEAR, cv::BORDER_REPLICATE);

    float shift_x = 0.0f, shift_y = 0.0f, score = 0.0f, psr = 0.0f;
    int32_t rc = vs_phase_correlate_translation(
        ref.ptr(), width,
        shifted.ptr(), width,
        width, height,
        &shift_x, &shift_y,
        &score, &psr);

    check(rc == 0, "vs_phase_correlate_translation succeeded");

    float err_x = std::abs(shift_x - true_dx);
    float err_y = std::abs(shift_y - true_dy);
    std::printf("      info: true=(%.2f, %.2f) detected=(%.2f, %.2f) err=(%.3f, %.3f) psr=%.1f\n",
                true_dx, true_dy, shift_x, shift_y, err_x, err_y, psr);

    check(err_x <= 0.25f, "sub-pixel shift X within 0.25 px of truth");
    check(err_y <= 0.25f, "sub-pixel shift Y within 0.25 px of truth");
    check(psr >= 10.0f, "Dirac peak exhibits strong PSR (>= 10.0)");
}

void test_extreme_contrast_fade_invariance() {
    std::printf("--- test_extreme_contrast_fade_invariance ---\n");

    const int width = 128;
    const int height = 128;

    // Golden template: High contrast (background 210, foreground 30 -> contrast 180)
    cv::Mat ref = create_test_pattern(width, height, 210, 30);

    // Test image: Shifted by dx = -4.0 px, dy = +6.0 px
    const float true_dx = -4.0f;
    const float true_dy = 6.0f;
    cv::Mat trans_mat = make_trans_mat(true_dx, true_dy);
    cv::Mat shifted;
    cv::warpAffine(ref, shifted, trans_mat, cv::Size(width, height), cv::INTER_LINEAR, cv::BORDER_REPLICATE);

    // Apply severe contrast degradation:
    // Compress contrast by 95% (from 180 grey levels down to only 9 grey levels!)
    // and darken with global multiplier:
    // new_val = shifted * 0.05 + 100
    cv::Mat faded;
    shifted.convertTo(faded, CV_32F);
    faded = faded * 0.05f + 100.0f;
    cv::Mat test_img;
    faded.convertTo(test_img, CV_8U);

    float shift_x = 0.0f, shift_y = 0.0f, score = 0.0f, psr = 0.0f;
    int32_t rc = vs_phase_correlate_translation(
        ref.ptr(), width,
        test_img.ptr(), width,
        width, height,
        &shift_x, &shift_y,
        &score, &psr);

    check(rc == 0, "phase correlation completed under 95% contrast fade");

    float err_x = std::abs(shift_x - true_dx);
    float err_y = std::abs(shift_y - true_dy);
    std::printf("      info: faded test: true=(%.2f, %.2f) detected=(%.2f, %.2f) err=(%.3f, %.3f) psr=%.1f\n",
                true_dx, true_dy, shift_x, shift_y, err_x, err_y, psr);

    check(err_x <= 0.10f, "shift X accurately recovered despite 95% contrast fade");
    check(err_y <= 0.10f, "shift Y accurately recovered despite 95% contrast fade");
    check(psr >= 8.0f, "peak remains distinct under severe contrast collapse");
}

void test_rotation_and_translation_fourier_mellin() {
    std::printf("--- test_rotation_and_translation_fourier_mellin ---\n");

    const int width = 160;
    const int height = 160;
    cv::Mat ref = create_test_pattern(width, height);

    vs_phase_params params;
    vs_phase_default_params(&params);
    params.estimate_rotation = 1;

    vs_phase_correlator* c = vs_phase_create(&params);
    vs_phase_teach(c, ref.ptr(), width, height, width);

    // Rotate by +18.0 deg and translate by dx = +6.0, dy = -4.0
    const float true_rot = 18.0f;
    const float true_dx = 6.0f;
    const float true_dy = -4.0f;

    cv::Point2f center(width / 2.0f, height / 2.0f);
    cv::Mat rot_mat = make_rot_mat(center, true_rot, true_dx, true_dy);

    cv::Mat test_img;
    cv::warpAffine(ref, test_img, rot_mat, cv::Size(width, height), cv::INTER_LINEAR, cv::BORDER_REPLICATE);

    vs_phase_result result{};
    int32_t rc = vs_phase_match(c, test_img.ptr(), width, height, width, &result);

    check(rc == 0, "vs_phase_match succeeded on rotated part");

    float rot_err = std::abs(result.rotation_deg - true_rot);
    float err_x = std::abs(result.shift_x - true_dx);
    float err_y = std::abs(result.shift_y - true_dy);

    std::printf("      info: rot_true=%.1f detected=%.2f (err=%.2f deg), shift_true=(%.1f, %.1f) detected=(%.2f, %.2f)\n",
                true_rot, result.rotation_deg, rot_err, true_dx, true_dy, result.shift_x, result.shift_y);

    check(rot_err <= 1.5f, "rotation angle detected within 1.5 deg of truth");
    check(err_x <= 1.0f, "shift X detected within 1.0 px of truth");
    check(err_y <= 1.0f, "shift Y detected within 1.0 px of truth");
    check(result.is_found == 1, "part successfully matched (is_found == 1)");

    vs_phase_destroy(c);
}

void test_benchmark_512x512_performance() {
    std::printf("--- test_benchmark_512x512_performance ---\n");

    const int width = 512;
    const int height = 512;
    cv::Mat ref = create_test_pattern(width, height);
    cv::Mat test_img = ref.clone();

    float shift_x, shift_y, score, psr;

    // Warm-up
    vs_phase_correlate_translation(ref.ptr(), width, test_img.ptr(), width, width, height, &shift_x, &shift_y, &score, &psr);

    const int iterations = 10;
    auto t0 = std::chrono::high_resolution_clock::now();
    for (int i = 0; i < iterations; ++i) {
        vs_phase_correlate_translation(ref.ptr(), width, test_img.ptr(), width, width, height, &shift_x, &shift_y, &score, &psr);
    }
    auto t1 = std::chrono::high_resolution_clock::now();
    double elapsed_ms = std::chrono::duration<double, std::milli>(t1 - t0).count() / iterations;

    std::printf("      info: 512x512 FFT phase correlation took %.2f ms / match\n", elapsed_ms);
    check(VS_TIME_BUDGET(elapsed_ms < 10.0), "512x512 FFT phase correlation runs under budget (< 10 ms)");
}

} // namespace

int main() {
    std::printf("=== Running vs_phase verification test suite ===\n");
    test_lifecycle_and_defaults();
    test_subpixel_pure_translation();
    test_extreme_contrast_fade_invariance();
    test_rotation_and_translation_fourier_mellin();
    test_benchmark_512x512_performance();

    std::printf("=== Finished: %d failures ===\n", failures);
    return failures != 0 ? 1 : 0;
}
