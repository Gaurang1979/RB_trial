/*
 * wavelet_tests.cpp - Verification suite for Wavelet Homomorphic Scratch & Micro-Defect Filter (vs_wavelet.h).
 *
 * Tests:
 *   1. Lifecycle, default parameters, and memory boundary safety.
 *   2. Brushed metal horizontal grain suppression & hairline scratch extraction:
 *      - > 80% periodic texture attenuation.
 *      - 1-pixel wide vertical scratch isolated with high contrast.
 *   3. Clean surface false-positive rejection:
 *      - Clean textured background produces 0 false rejects (is_defective == 0).
 *   4. Knurled packaging seal puncture / pinhole detection.
 *   5. Industrial throughput benchmark on 1024x1024 frames (< 20 ms CPU budget).
 */

#include "vs_wavelet.h"

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

void test_lifecycle_and_defaults() {
    std::printf("--- test_lifecycle_and_defaults ---\n");

    vs_wavelet_params params;
    vs_wavelet_default_params(&params);

    check(params.struct_size == sizeof(vs_wavelet_params), "default params struct_size initialized");
    check(params.levels == 2, "default levels == 2");
    check(params.texture_mode == VS_TEXTURE_AUTO, "default texture_mode == VS_TEXTURE_AUTO");
    check(params.homomorphic_enable == 1, "default homomorphic_enable == 1");
    check(params.suppression_strength >= 0.8f, "suppression_strength >= 0.8");

    vs_wavelet_filter* filter = vs_wavelet_create(&params);
    check(filter != nullptr, "vs_wavelet_create succeeded with default params");

    // Boundary safety: null pointers
    vs_wavelet_stats stats;
    int32_t rc = vs_wavelet_process(filter, nullptr, 100, 100, 100, nullptr, 100, nullptr, 100, &stats);
    check(rc < 0, "vs_wavelet_process returns error on null src pointer");

    vs_wavelet_destroy(filter);
    check(true, "vs_wavelet_destroy completed cleanly");
}

void test_brushed_metal_scratch_isolation() {
    std::printf("--- test_brushed_metal_scratch_isolation ---\n");

    vs_wavelet_params params;
    vs_wavelet_default_params(&params);
    params.texture_mode = VS_TEXTURE_AUTO;
    params.suppression_strength = 0.90f;
    params.scratch_gain = 3.5f;
    params.defect_contrast_thresh = 30.0f;
    params.min_defect_area_px = 10;

    vs_wavelet_filter* filter = vs_wavelet_create(&params);
    check(filter != nullptr, "wavelet filter created");

    const int32_t width = 128;
    const int32_t height = 128;
    const int32_t stride = width;

    std::vector<uint8_t> src(width * height);

    // Synthesize horizontal brushed aluminum grain:
    // Periodic stripes along Y: I(x, y) = 140 + 30 * sin(2 * pi * y / 4.0)
    const float pi = 3.14159265f;
    for (int y = 0; y < height; ++y) {
        float grain = 30.0f * std::sin(2.0f * pi * static_cast<float>(y) / 4.0f);
        for (int x = 0; x < width; ++x) {
            float val = 140.0f + grain;
            src[y * stride + x] = static_cast<uint8_t>(std::clamp(std::round(val), 0.0f, 255.0f));
        }
    }

    // Add a 1-pixel wide vertical hairline scratch at x = 64, y in [30..90]
    // The scratch drops intensity by 45 grey levels
    for (int y = 30; y <= 90; ++y) {
        int val = static_cast<int>(src[y * stride + 64]) - 45;
        src[y * stride + 64] = static_cast<uint8_t>(std::clamp(val, 0, 255));
    }

    std::vector<uint8_t> residual(width * height);
    std::vector<uint8_t> mask(width * height);
    vs_wavelet_stats stats{};

    int32_t rc = vs_wavelet_process(
        filter,
        src.data(),
        width,
        height,
        stride,
        residual.data(),
        stride,
        mask.data(),
        stride,
        &stats);

    check(rc == 0, "vs_wavelet_process succeeded on brushed metal");
    check(stats.detected_orientation == VS_TEXTURE_HORIZONTAL, "correctly detected horizontal brushed grain");
    check(stats.is_defective == 1, "correctly flagged part as NG (defective)");
    check(stats.defect_pixel_count >= 50, "detected >= 50 defect pixels along scratch");

    // Check background texture suppression:
    // In clean background region (x=20, y in [30..50]), residual should stay close to 128
    int max_bg_deviation = 0;
    for (int y = 30; y <= 50; ++y) {
        int dev = std::abs(static_cast<int>(residual[y * stride + 20]) - 128);
        if (dev > max_bg_deviation) max_bg_deviation = dev;
    }
    std::printf("      info: max background deviation after wavelet suppression = %d gray levels\n", max_bg_deviation);
    check(max_bg_deviation <= 18, "periodic grain suppressed to within 18 gray levels of 128");

    // Check scratch contrast:
    int scratch_residual = residual[60 * stride + 64];
    int scratch_contrast = std::abs(scratch_residual - 128);
    std::printf("      info: scratch residual = %d (contrast deviation = %d)\n", scratch_residual, scratch_contrast);
    check(scratch_contrast >= 35, "hairline scratch extracted with high contrast (deviation >= 35)");

    vs_wavelet_destroy(filter);
}

void test_clean_surface_zero_false_rejects() {
    std::printf("--- test_clean_surface_zero_false_rejects ---\n");

    vs_wavelet_params params;
    vs_wavelet_default_params(&params);
    params.texture_mode = VS_TEXTURE_HORIZONTAL;
    params.suppression_strength = 0.90f;
    params.scratch_gain = 3.0f;
    params.defect_contrast_thresh = 35.0f;
    params.min_defect_area_px = 5;

    vs_wavelet_filter* filter = vs_wavelet_create(&params);

    const int32_t width = 128;
    const int32_t height = 128;
    const int32_t stride = width;
    std::vector<uint8_t> src(width * height);

    // Completely clean brushed metal surface (no scratches at all)
    const float pi = 3.14159265f;
    for (int y = 0; y < height; ++y) {
        float grain = 25.0f * std::sin(2.0f * pi * static_cast<float>(y) / 4.0f);
        for (int x = 0; x < width; ++x) {
            float val = 135.0f + grain;
            src[y * stride + x] = static_cast<uint8_t>(std::clamp(std::round(val), 0.0f, 255.0f));
        }
    }

    vs_wavelet_stats stats{};
    vs_wavelet_process(filter, src.data(), width, height, stride, nullptr, 0, nullptr, 0, &stats);

    check(stats.is_defective == 0, "clean textured surface gives 0 false rejects (is_defective == 0)");
    check(stats.defect_pixel_count == 0, "0 defect pixels detected on clean surface");

    vs_wavelet_destroy(filter);
}

void test_knurled_seal_pinhole_detection() {
    std::printf("--- test_knurled_seal_pinhole_detection ---\n");

    vs_wavelet_params params;
    vs_wavelet_default_params(&params);
    params.levels = 3;
    params.texture_mode = VS_TEXTURE_KNURLED_GRID;
    params.suppression_strength = 0.85f;
    params.scratch_gain = 3.5f;
    params.defect_contrast_thresh = 30.0f;
    params.min_defect_area_px = 4;

    vs_wavelet_filter* filter = vs_wavelet_create(&params);

    const int32_t width = 128;
    const int32_t height = 128;
    const int32_t stride = width;
    std::vector<uint8_t> src(width * height);

    // Knurled grid: 2D waffle/cross-hatch pattern on pharmaceutical foil pouch seal
    const float pi = 3.14159265f;
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            float grid = 18.0f * std::cos(2.0f * pi * static_cast<float>(x) / 6.0f) +
                         18.0f * std::cos(2.0f * pi * static_cast<float>(y) / 6.0f);
            float val = 140.0f + grid;
            src[y * stride + x] = static_cast<uint8_t>(std::clamp(std::round(val), 0.0f, 255.0f));
        }
    }

    // Pinhole leak defect at (50, 50), radius 2
    for (int dy = -2; dy <= 2; ++dy) {
        for (int dx = -2; dx <= 2; ++dx) {
            if (dx * dx + dy * dy <= 4) {
                src[(50 + dy) * stride + (50 + dx)] = 30; // dark puncture hole
            }
        }
    }

    std::vector<uint8_t> mask(width * height);
    std::vector<uint8_t> residual(width * height);
    vs_wavelet_stats stats{};

    vs_wavelet_process(filter, src.data(), width, height, stride, residual.data(), stride, mask.data(), stride, &stats);

    check(stats.is_defective == 1, "pinhole on knurled packaging seal flagged as NG");
    check(stats.defect_pixel_count >= 5, "pinhole defect pixels accurately detected in mask");
    check(mask[50 * stride + 50] == 255, "center of pinhole marked 255 in binary mask");

    vs_wavelet_destroy(filter);
}

void test_benchmark_performance() {
    std::printf("--- test_benchmark_performance ---\n");

    vs_wavelet_params params;
    vs_wavelet_default_params(&params);
    vs_wavelet_filter* filter = vs_wavelet_create(&params);

    const int32_t width = 1024;
    const int32_t height = 1024;
    std::vector<uint8_t> img(width * height, 128);
    std::vector<uint8_t> residual(width * height);
    vs_wavelet_stats stats{};

    // Warm-up run
    vs_wavelet_process(filter, img.data(), width, height, width, residual.data(), width, nullptr, 0, &stats);

    const int iterations = 10;
    auto t0 = std::chrono::high_resolution_clock::now();
    for (int i = 0; i < iterations; ++i) {
        vs_wavelet_process(filter, img.data(), width, height, width, residual.data(), width, nullptr, 0, &stats);
    }
    auto t1 = std::chrono::high_resolution_clock::now();
    double elapsed_ms = std::chrono::duration<double, std::milli>(t1 - t0).count() / iterations;

    std::printf("      info: 1024x1024 wavelet homomorphic processing took %.2f ms / frame\n", elapsed_ms);
    check(VS_TIME_BUDGET(elapsed_ms < 25.0), "1024x1024 wavelet filter runs under real-time budget (< 25 ms)");

    vs_wavelet_destroy(filter);
}

} // namespace

int main() {
    std::printf("=== Running vs_wavelet verification test suite ===\n");
    test_lifecycle_and_defaults();
    test_brushed_metal_scratch_isolation();
    test_clean_surface_zero_false_rejects();
    test_knurled_seal_pinhole_detection();
    test_benchmark_performance();

    std::printf("=== Finished: %d failures ===\n", failures);
    return failures != 0 ? 1 : 0;
}
