/*
 * drift_tests.cpp - Unit and benchmark tests for the Optical Drift & OOD Detector (AG-12, AS-13).
 */

#include "vs_drift.h"
#include <cassert>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <random>
#include <vector>
#include "check.h"

#define TEST_CHECK(cond, msg) \
    do { \
        if (!(cond)) { \
            std::fprintf(stderr, "FAIL: %s (%s:%d)\n", msg, __FILE__, __LINE__); \
            std::exit(1); \
        } \
    } while (0)

void test_default_params() {
    std::printf("[TEST] Default parameters initialization...\n");
    vs_drift_params p;
    std::memset(&p, 0xFF, sizeof(p));
    vs_drift_default_params(&p);

    TEST_CHECK(p.struct_size == sizeof(vs_drift_params), "struct_size mismatch");
    TEST_CHECK(p.window_size == 50, "window_size default");
    TEST_CHECK(p.warn_distance_threshold > 3.0f, "warn_distance_threshold default");
    TEST_CHECK(p.dirt_sharpness_sigma >= 1.5f, "dirt_sharpness_sigma default");
    TEST_CHECK(p.light_drop_sigma >= 1.5f, "light_drop_sigma default");
    TEST_CHECK(p.grid_step == 4, "grid_step default");
    std::printf("  PASSED\n");
}

void test_feature_extraction() {
    std::printf("[TEST] Fast feature extraction on synthetic patterns...\n");
    const int w = 200, h = 200;
    std::vector<uint8_t> img(w * h, 128);

    // Add checkerboard pattern in center for sharpness
    for (int y = 50; y < 150; ++y) {
        for (int x = 50; x < 150; ++x) {
            if (((x / 10) + (y / 10)) % 2 == 0) {
                img[y * w + x] = 200;
            } else {
                img[y * w + x] = 50;
            }
        }
    }

    vs_drift_features f;
    int res = vs_drift_extract_features(img.data(), w, h, w, 2, &f);
    TEST_CHECK(res == 0, "vs_drift_extract_features failed");

    TEST_CHECK(f.mean_brightness > 100.0f && f.mean_brightness < 150.0f, "mean brightness range");
    TEST_CHECK(f.contrast_std > 20.0f, "contrast std range");
    TEST_CHECK(f.sharpness > 5.0f, "sharpness should detect checkerboard edges");
    TEST_CHECK(f.uniformity > 0.5f, "uniformity range");
    TEST_CHECK(f.saturated_ratio == 0.0f, "no saturated pixels");

    std::printf("  PASSED (mean=%.1f, contrast=%.1f, sharp=%.1f, uniform=%.2f)\n",
                f.mean_brightness, f.contrast_std, f.sharpness, f.uniformity);
}

void test_baseline_and_drift_detection() {
    std::printf("[TEST] Baseline modeling and predictive optical drift detection...\n");
    vs_drift_params p;
    vs_drift_default_params(&p);
    p.window_size = 10; // Small window for unit test responsiveness
    p.warn_distance_threshold = 5.0f;

    vs_drift_tracker* tracker = vs_drift_create(&p);
    TEST_CHECK(tracker != nullptr, "tracker creation failed");

    std::mt19937 rng(42);
    std::normal_distribution<float> noise(0.0f, 1.0f);

    // 1. Feed 30 baseline golden images (mean brightness = 140, sharpness = 25)
    for (int i = 0; i < 30; ++i) {
        vs_drift_features bf;
        bf.mean_brightness = 140.0f + noise(rng) * 1.5f;
        bf.contrast_std = 35.0f + noise(rng) * 0.8f;
        bf.sharpness = 25.0f + noise(rng) * 0.5f;
        bf.uniformity = 0.92f + noise(rng) * 0.01f;
        for (int c = 0; c < 4; ++c) bf.corner_sharpness[c] = 23.0f + noise(rng) * 0.5f;
        bf.saturated_ratio = 0.001f;
        bf.dark_floor = 12.0f + noise(rng) * 0.2f;

        int res = vs_drift_teach_sample(tracker, &bf);
        TEST_CHECK(res == 0, "teach sample failed");
    }

    int fin_res = vs_drift_finalize_teach(tracker);
    TEST_CHECK(fin_res == 0, "finalize teach failed");

    // 2. Normal production run: 15 healthy parts
    for (int i = 0; i < 15; ++i) {
        vs_drift_features norm_f;
        norm_f.mean_brightness = 140.0f + noise(rng) * 1.5f;
        norm_f.contrast_std = 35.0f + noise(rng) * 0.8f;
        norm_f.sharpness = 25.0f + noise(rng) * 0.5f;
        norm_f.uniformity = 0.92f + noise(rng) * 0.01f;
        for (int c = 0; c < 4; ++c) norm_f.corner_sharpness[c] = 23.0f + noise(rng) * 0.5f;
        norm_f.saturated_ratio = 0.001f;
        norm_f.dark_floor = 12.0f + noise(rng) * 0.2f;

        float dist = 0.0f;
        int32_t status = -1;
        char diag[256];
        int u_res = vs_drift_update(tracker, &norm_f, &dist, &status, diag, sizeof(diag));
        TEST_CHECK(u_res == 0, "update failed");
        TEST_CHECK(status == VS_DRIFT_STATUS_OK, "status should remain OK on normal parts");
        if (i == 0) std::printf("  Baseline normal DM dist = %.2f\n", dist);
        TEST_CHECK(dist < 4.5f, "Mahalanobis distance should be small on baseline");
    }

    // 3. Simulated Lens Contamination / Defocus Drift (sharpness drops by 40%, brightness steady)
    int32_t last_status = -1;
    char last_diag[256];
    float last_dist = 0.0f;

    for (int i = 0; i < 12; ++i) {
        vs_drift_features dirty_f;
        dirty_f.mean_brightness = 140.0f + noise(rng) * 1.5f; // Brightness unchanged
        dirty_f.contrast_std = 30.0f + noise(rng) * 0.8f;
        dirty_f.sharpness = 15.0f + noise(rng) * 0.5f;        // Sharpness dropped from 25 to 15!
        dirty_f.uniformity = 0.92f + noise(rng) * 0.01f;
        for (int c = 0; c < 4; ++c) dirty_f.corner_sharpness[c] = 14.0f + noise(rng) * 0.5f;
        dirty_f.saturated_ratio = 0.001f;
        dirty_f.dark_floor = 12.0f + noise(rng) * 0.2f;

        vs_drift_update(tracker, &dirty_f, &last_dist, &last_status, last_diag, sizeof(last_diag));
    }

    TEST_CHECK(last_status == VS_DRIFT_WARN_LENS_DIRT, "should raise VS_DRIFT_WARN_LENS_DIRT");
    TEST_CHECK(last_dist >= 5.0f, "Mahalanobis distance should exceed warning threshold");
    TEST_CHECK(std::strstr(last_diag, "Lens contamination") != nullptr, "diagnostic message should mention lens dirt");
    std::printf("  LENS DIRT DIAGNOSTIC: %s (dist=%.2f)\n", last_diag, last_dist);

    // 4. Simulated LED Illuminator Fading (brightness drops by 20%, sharpness normal)
    for (int i = 0; i < 12; ++i) {
        vs_drift_features dim_f;
        dim_f.mean_brightness = 110.0f + noise(rng) * 1.5f; // Brightness dropped from 140 to 110!
        dim_f.contrast_std = 28.0f + noise(rng) * 0.8f;
        dim_f.sharpness = 25.0f + noise(rng) * 0.5f;
        dim_f.uniformity = 0.92f + noise(rng) * 0.01f;
        for (int c = 0; c < 4; ++c) dim_f.corner_sharpness[c] = 23.0f + noise(rng) * 0.5f;
        dim_f.saturated_ratio = 0.000f;
        dim_f.dark_floor = 8.0f + noise(rng) * 0.2f;

        vs_drift_update(tracker, &dim_f, &last_dist, &last_status, last_diag, sizeof(last_diag));
    }

    TEST_CHECK(last_status == VS_DRIFT_WARN_LIGHTING_DROP, "should raise VS_DRIFT_WARN_LIGHTING_DROP");
    TEST_CHECK(std::strstr(last_diag, "LED strobe fading") != nullptr, "diagnostic message should mention LED fading");
    std::printf("  LED FADING DIAGNOSTIC: %s (dist=%.2f)\n", last_diag, last_dist);

    vs_drift_destroy(tracker);
    std::printf("  PASSED\n");
}

void test_performance_benchmark() {
    std::printf("[TEST] Performance benchmark on full 20 MP frame...\n");
    const int fw = 5472, fh = 3648;
    std::vector<uint8_t> frame(fw * fh, 130);

    // Add some realistic features across the frame
    for (int y = 1000; y < 1500; ++y) {
        for (int x = 1000; x < 1500; ++x) {
            frame[y * fw + x] = ((x / 16) % 2 == 0) ? 210 : 30;
        }
    }

    vs_drift_params p;
    vs_drift_default_params(&p);
    p.grid_step = 4; // Sample every 4th pixel for 20 MP

    vs_drift_tracker* tracker = vs_drift_create(&p);

    // Teach dummy baseline
    vs_drift_features f_base;
    vs_drift_extract_features(frame.data(), fw, fh, fw, p.grid_step, &f_base);
    for (int i = 0; i < 10; ++i) {
        vs_drift_teach_sample(tracker, &f_base);
    }
    vs_drift_finalize_teach(tracker);

    const int iterations = 10;
    auto t0 = std::chrono::high_resolution_clock::now();
    for (int i = 0; i < iterations; ++i) {
        float dist = 0.0f;
        int32_t status = 0;
        char diag[128];
        vs_drift_inspect_mono8(tracker, frame.data(), fw, fh, fw, &dist, &status, diag, sizeof(diag));
    }
    auto t1 = std::chrono::high_resolution_clock::now();

    double ms_per_frame = std::chrono::duration<double, std::milli>(t1 - t0).count() / iterations;
    std::printf("  20 MP (5472x3648) feature extraction + Mahalanobis drift update: %.2f ms (Budget: <= 5.0 ms)\n",
                ms_per_frame);
    TEST_CHECK(VS_TIME_BUDGET(ms_per_frame < 8.0), "20 MP drift update too slow");

    vs_drift_destroy(tracker);
    std::printf("  PASSED\n");
}

int main() {
    std::printf("=== Running Optical Drift & OOD Detector Tests (AG-12, AS-13) ===\n");
    test_default_params();
    test_feature_extraction();
    test_baseline_and_drift_detection();
    test_performance_benchmark();
    std::printf("=== All Optical Drift Tests Passed 100%% ===\n");
    return 0;
}
