#include "vs_metamorphic.h"
#include <iostream>
#include <vector>
#include <cmath>
#include <chrono>
#include <cassert>
#include <algorithm>
#include "check.h"

static int total_asserts = 0;
static int failed_asserts = 0;

#define TEST_ASSERT(cond, msg) do { \
    total_asserts++; \
    if (!(cond)) { \
        std::cerr << "FAIL: " << msg << " (" << __FILE__ << ":" << __LINE__ << ")" << std::endl; \
        failed_asserts++; \
    } else { \
        std::cout << "ok    " << msg << std::endl; \
    } \
} while(0)

// Context for simulated vision tools
struct RobustCircleDetector {
    int expected_radius;
};

// Robust detector: computes true sub-pixel centroid of dark circular blob
int robust_eval_fn(
    const uint8_t* img,
    int w,
    int h,
    int stride,
    void* user_data,
    float* confidence,
    float* out_x,
    float* out_y,
    int* is_ok)
{
    (void)user_data;
    double m00 = 0.0, m10 = 0.0, m01 = 0.0;
    int dark_pixels = 0;

    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            uint8_t v = img[y * stride + x];
            if (v < 128) {
                double weight = (128.0 - v) / 128.0;
                m00 += weight;
                m10 += weight * x;
                m01 += weight * y;
                dark_pixels++;
            }
        }
    }

    if (m00 > 10.0) {
        *out_x = (float)(m10 / m00);
        *out_y = (float)(m01 / m00);
        *confidence = (float)std::min(1.0, m00 / 300.0);
        *is_ok = (dark_pixels > 100) ? 1 : 0;
    } else {
        *out_x = (float)w * 0.5f;
        *out_y = (float)h * 0.5f;
        *confidence = 0.1f;
        *is_ok = 0;
    }
    return 0;
}

// Brittle detector: uses a rigid hard-coded threshold that fails under minor lighting shifts or noise
int brittle_eval_fn(
    const uint8_t* img,
    int w,
    int h,
    int stride,
    void* user_data,
    float* confidence,
    float* out_x,
    float* out_y,
    int* is_ok)
{
    (void)user_data;
    // Brittle: fails if center pixel is even slightly brighter than 44
    int cx = w / 2;
    int cy = h / 2;
    uint8_t center_val = img[cy * stride + cx];

    if (center_val > 44) {
        *confidence = 0.3f;
        *out_x = 0.0f;
        *out_y = 0.0f;
        *is_ok = 0; // Flip verdict to NG!
    } else {
        *confidence = 0.95f;
        *out_x = (float)cx;
        *out_y = (float)cy;
        *is_ok = 1;
    }
    return 0;
}

void test_lifecycle_and_defaults() {
    std::cout << "--- test_lifecycle_and_defaults ---" << std::endl;
    vs_metamorphic_config cfg;
    vs_metamorphic_default_config(&cfg);

    TEST_ASSERT(cfg.struct_size == sizeof(vs_metamorphic_config), "config struct_size initialized");
    TEST_ASSERT(cfg.max_rotation_deg == 3.0f, "default max_rotation_deg == 3.0");
    TEST_ASSERT(cfg.max_noise_sigma >= 5.0f, "default max_noise_sigma >= 5.0");
    TEST_ASSERT(cfg.max_allowed_jitter_px <= 1.0f, "default jitter allowance <= 1.0 px");

    vs_metamorphic_report report;
    uint8_t dummy[100];
    int rc = vs_metamorphic_run_fuzzing_suite(nullptr, 10, 10, 10, &cfg, robust_eval_fn, nullptr, &report);
    TEST_ASSERT(rc == VS_METAMORPHIC_ERR_NULL_PTR, "returns error on null pointer");

    rc = vs_metamorphic_run_fuzzing_suite(dummy, -5, 10, 10, &cfg, robust_eval_fn, nullptr, &report);
    TEST_ASSERT(rc == VS_METAMORPHIC_ERR_INVALID_DIM, "returns error on invalid dimension");
}

void test_affine_and_photometric_transforms() {
    std::cout << "--- test_affine_and_photometric_transforms ---" << std::endl;
    const int W = 80;
    const int H = 80;
    std::vector<uint8_t> src(W * H, 200);

    // Draw center dark spot
    for (int y = 30; y < 50; ++y) {
        for (int x = 30; x < 50; ++x) {
            src[y * W + x] = 30;
        }
    }

    std::vector<uint8_t> dst(W * H, 0);
    vs_metamorphic_transform t = {0};
    t.rot_angle_deg = 0.0f;
    t.scale = 1.0f;
    t.gain_gamma = 1.0f;

    // 1. Identity transform
    int rc = vs_metamorphic_apply_transform(src.data(), W, H, W, &t, dst.data(), W, H, W);
    TEST_ASSERT(rc == VS_METAMORPHIC_OK, "identity transform succeeded");
    TEST_ASSERT(dst[40 * W + 40] == 30, "center pixel value preserved under identity");

    // 2. Photometric brightness boost (+30 beta)
    t.bias_beta = 30.0f;
    vs_metamorphic_apply_transform(src.data(), W, H, W, &t, dst.data(), W, H, W);
    TEST_ASSERT(dst[40 * W + 40] == 60, "photometric bias adds 30 grey levels");

    // 3. Sensor noise injection (sigma = 10)
    t.bias_beta = 0.0f;
    t.noise_sigma = 10.0f;
    vs_metamorphic_apply_transform(src.data(), W, H, W, &t, dst.data(), W, H, W);
    // Noise should perturb pixels around 200
    int noisy_val = dst[10 * W + 10];
    TEST_ASSERT(noisy_val != 200, "sensor noise perturbs background value");
}

void test_robust_vision_algorithm_passes() {
    std::cout << "--- test_robust_vision_algorithm_passes ---" << std::endl;
    const int W = 100;
    const int H = 100;
    std::vector<uint8_t> img(W * H, 220);

    // Draw a circular dark marker slightly off-center at (55, 45)
    float true_cx = 55.0f;
    float true_cy = 45.0f;
    float r = 12.0f;

    for (int y = 0; y < H; ++y) {
        for (int x = 0; x < W; ++x) {
            float d = std::sqrt((x - true_cx) * (x - true_cx) + (y - true_cy) * (y - true_cy));
            if (d < r) {
                img[y * W + x] = 30;
            }
        }
    }

    vs_metamorphic_config cfg;
    vs_metamorphic_default_config(&cfg);
    cfg.max_allowed_jitter_px = 0.75f;

    vs_metamorphic_report report;
    int rc = vs_metamorphic_run_fuzzing_suite(img.data(), W, H, W, &cfg, robust_eval_fn, nullptr, &report);

    TEST_ASSERT(rc == VS_METAMORPHIC_OK, "fuzzing suite ran successfully");
    std::cout << "      info: evals=" << report.total_evaluations
              << " flips=" << report.verdict_flips
              << " max_jitter=" << report.max_positional_jitter_px
              << " mean_jitter=" << report.mean_positional_jitter_px
              << " min_conf=" << report.min_observed_confidence
              << " is_robust=" << report.is_robust << std::endl;

    TEST_ASSERT(report.total_evaluations >= 15, "evaluated >= 15 metamorphic variants");
    TEST_ASSERT(report.verdict_flips == 0, "zero verdict flips across rotation, light, scale, noise");
    TEST_ASSERT(report.max_positional_jitter_px <= cfg.max_allowed_jitter_px, "positional jitter within sub-pixel bounds");
    TEST_ASSERT(report.is_robust == 1, "robust detector passes metamorphic certification");
}

void test_brittle_vision_algorithm_fails() {
    std::cout << "--- test_brittle_vision_algorithm_fails ---" << std::endl;
    const int W = 60;
    const int H = 60;
    std::vector<uint8_t> img(W * H, 200);

    // Draw dark center (value 40, which is just barely under the brittle threshold of 48)
    for (int y = 20; y < 40; ++y) {
        for (int x = 20; x < 40; ++x) {
            img[y * W + x] = 40;
        }
    }

    vs_metamorphic_config cfg;
    vs_metamorphic_default_config(&cfg);

    vs_metamorphic_report report;
    int rc = vs_metamorphic_run_fuzzing_suite(img.data(), W, H, W, &cfg, brittle_eval_fn, nullptr, &report);

    TEST_ASSERT(rc == VS_METAMORPHIC_OK, "fuzzing suite ran on brittle detector");
    std::cout << "      info: brittle flips=" << report.verdict_flips
              << " is_robust=" << report.is_robust
              << " reason=" << report.failure_reason << std::endl;

    TEST_ASSERT(report.verdict_flips > 0, "brittle heuristic exhibits verdict flips");
    TEST_ASSERT(report.is_robust == 0, "brittle detector fails metamorphic certification");
    TEST_ASSERT(report.failure_reason[0] != '\0', "failure reason explains violation");
}

void test_benchmark_performance() {
    std::cout << "--- test_benchmark_performance ---" << std::endl;
    const int W = 256;
    const int H = 256;
    std::vector<uint8_t> img(W * H, 200);

    for (int y = 100; y < 156; ++y) {
        for (int x = 100; x < 156; ++x) {
            img[y * W + x] = 40;
        }
    }

    vs_metamorphic_config cfg;
    vs_metamorphic_default_config(&cfg);

    vs_metamorphic_report report;
    auto t0 = std::chrono::high_resolution_clock::now();
    int rc = vs_metamorphic_run_fuzzing_suite(img.data(), W, H, W, &cfg, robust_eval_fn, nullptr, &report);
    auto t1 = std::chrono::high_resolution_clock::now();
    double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();

    std::cout << "      info: full metamorphic fuzzing (" << report.total_evaluations
              << " variants) took " << ms << " ms total (" << (ms / report.total_evaluations) << " ms/variant)" << std::endl;

    TEST_ASSERT(rc == VS_METAMORPHIC_OK, "fuzzing benchmark succeeded");
    TEST_ASSERT(VS_TIME_BUDGET(ms < 35.0), "full 20-perturbation suite completes in < 35 ms on CPU");
}

int main() {
    std::cout << "=== Running vs_metamorphic verification test suite ===" << std::endl;
    test_lifecycle_and_defaults();
    test_affine_and_photometric_transforms();
    test_robust_vision_algorithm_passes();
    test_brittle_vision_algorithm_fails();
    test_benchmark_performance();

    std::cout << "=== Finished: " << failed_asserts << " failures out of " << total_asserts << " assertions ===" << std::endl;
    return (failed_asserts == 0) ? 0 : 1;
}
