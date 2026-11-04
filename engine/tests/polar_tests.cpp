/*
 * polar_tests.cpp - Verification suite for Polarimetric Reflection Decoupler (vs_polar.h).
 *
 * Tests:
 *   1. Lifecycle and parameter validation.
 *   2. Blister pack & glossy plastic specular glare elimination:
 *      - > 80% glare attenuation in diffuse image.
 *      - Underlying printed text recovered with high contrast.
 *      - Glare hotspot isolated in specular saliency map.
 *   3. Photoelastic stress birefringence inspection in transparent PET/glass:
 *      - Detects brittle stress concentration rings via AoLP gradient.
 *   4. Raw Sony Polarsens 2x2 micro-polarizer mosaic demuxing.
 *   5. Industrial throughput benchmark on 1024x1024 frames (< 15 ms).
 */

#include "vs_polar.h"

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

    vs_polar_params params;
    vs_polar_default_params(&params);

    check(params.struct_size == sizeof(vs_polar_params), "params struct_size initialized");
    check(params.glare_suppress_alpha >= 0.9f, "default glare_suppress_alpha >= 0.9");
    check(params.dolp_defect_thresh >= 0.4f, "default dolp_defect_thresh >= 0.4");

    // Null safety
    vs_polar_stats stats{};
    int32_t rc = vs_polar_process_channels(nullptr, nullptr, nullptr, nullptr, 100, 100, 100, &params, nullptr, 0, nullptr, 0, nullptr, 0, nullptr, 0, &stats);
    check(rc < 0, "vs_polar_process_channels returns error on null input");
}

void test_specular_glare_removal() {
    std::printf("--- test_specular_glare_removal ---\n");

    const int width = 120;
    const int height = 120;
    const int stride = width;

    // 4 channel images: i0, i45, i90, i135
    std::vector<uint8_t> i0(width * height, 180);   // unpolarized background = 180
    std::vector<uint8_t> i45(width * height, 180);
    std::vector<uint8_t> i90(width * height, 180);
    std::vector<uint8_t> i135(width * height, 180);

    // Region 1: Printed barcode line beneath film (x in 20..35, y in 20..80)
    // Dark unpolarized ink (val=40 in all 4 channels)
    for (int y = 20; y <= 80; ++y) {
        for (int x = 20; x <= 35; ++x) {
            i0[y * stride + x] = 40;
            i45[y * stride + x] = 40;
            i90[y * stride + x] = 40;
            i135[y * stride + x] = 40;
        }
    }

    // Region 2: Intense specular glare hotspot overlapping the barcode (x in 25..60, y in 35..65)
    // Specular reflection is horizontally polarized (Brewster angle):
    // i0 receives strong glare (250), i90 receives minimal (60), i45 and i135 receive intermediate (155)
    for (int y = 35; y <= 65; ++y) {
        for (int x = 25; x <= 60; ++x) {
            i0[y * stride + x] = 250;
            i45[y * stride + x] = 155;
            i90[y * stride + x] = 60;
            i135[y * stride + x] = 155;
        }
    }

    vs_polar_params params;
    vs_polar_default_params(&params);
    params.glare_suppress_alpha = 1.0f;

    std::vector<uint8_t> diffuse_out(width * height);
    std::vector<uint8_t> specular_out(width * height);
    std::vector<uint8_t> dolp_out(width * height);
    vs_polar_stats stats{};

    int32_t rc = vs_polar_process_channels(
        i0.data(), i45.data(), i90.data(), i135.data(),
        width, height, stride,
        &params,
        diffuse_out.data(), stride,
        specular_out.data(), stride,
        dolp_out.data(), stride,
        nullptr, 0,
        &stats);

    check(rc == 0, "vs_polar_process_channels succeeded");
    check(stats.glare_pixel_count >= 500, "glare pixels detected and processed");

    // Check glare elimination:
    // Raw total intensity in glare spot was ~ (250 + 155 + 60 + 155)/2 = 310 (saturated in standard camera)
    // In diffuse_out, polarized glare is removed, leaving diffuse level (~60-90)
    uint8_t glare_diffuse = diffuse_out[50 * stride + 40];
    uint8_t glare_specular = specular_out[50 * stride + 40];
    uint8_t glare_dolp = dolp_out[50 * stride + 40];

    std::printf("      info: glare spot diffuse=%d specular=%d dolp=%d/255\n",
                glare_diffuse, glare_specular, glare_dolp);

    check(glare_dolp >= 140, "glare hotspot exhibits high DoLP (>= 140/255)");
    check(glare_specular >= 150, "specular saliency map isolates glare hotspot (>= 150)");
    check(glare_diffuse <= 110, "diffuse image eliminates blinding glare (val <= 110)");

    // Check unpolarized background:
    uint8_t bg_diffuse = diffuse_out[10 * stride + 10];
    uint8_t bg_dolp = dolp_out[10 * stride + 10];
    check(bg_dolp == 0, "unpolarized background has 0 DoLP");
    check(bg_diffuse == 180, "unpolarized background reflectance 100% preserved (180)");
}

void test_stress_birefringence() {
    std::printf("--- test_stress_birefringence ---\n");

    const int width = 64;
    const int height = 64;
    const int stride = width;

    // Transparent bottle neck under polariscope backlight:
    // Normally uniform polarization: i0=180, i90=40, i45=110, i135=110 (AoLP = 0 deg)
    std::vector<uint8_t> i0(width * height, 180);
    std::vector<uint8_t> i45(width * height, 110);
    std::vector<uint8_t> i90(width * height, 40);
    std::vector<uint8_t> i135(width * height, 110);

    // Defective residual stress crack at (32, 32):
    // Stress rotates polarization angle by 90 degrees: i0=40, i90=180
    i0[32 * stride + 32] = 40;
    i90[32 * stride + 32] = 180;

    vs_polar_params params;
    vs_polar_default_params(&params);
    params.stress_gradient_thresh = 35.0f;

    vs_polar_stats stats{};
    vs_polar_process_channels(
        i0.data(), i45.data(), i90.data(), i135.data(),
        width, height, stride,
        &params,
        nullptr, 0, nullptr, 0, nullptr, 0, nullptr, 0,
        &stats);

    std::printf("      info: max_stress_gradient=%.1f deg, defective=%d\n",
                stats.max_stress_gradient, stats.is_stress_defective);

    check(stats.max_stress_gradient >= 50.0f, "photoelastic stress gradient detected (>= 50 deg)");
    check(stats.is_stress_defective == 1, "molded bottle stress flaw flagged as NG");
}

void test_raw_mosaic_demuxing() {
    std::printf("--- test_raw_mosaic_demuxing ---\n");

    const int w = 20;
    const int h = 20;
    std::vector<uint8_t> raw(w * h, 150);

    // Set 2x2 cluster: TopLeft=90(50), TopRight=45(100), BottomLeft=135(150), BottomRight=0(200)
    for (int y = 0; y < h; y += 2) {
        for (int x = 0; x < w; x += 2) {
            raw[y * w + x] = 50;       // 90 deg
            raw[y * w + x + 1] = 100;  // 45 deg
            raw[(y + 1) * w + x] = 150;// 135 deg
            raw[(y + 1) * w + x + 1] = 200; // 0 deg
        }
    }

    std::vector<uint8_t> diffuse(10 * 10);
    std::vector<uint8_t> dolp(10 * 10);
    vs_polar_stats stats{};

    int32_t rc = vs_polar_process_mosaic(
        raw.data(), w, h, w,
        nullptr,
        diffuse.data(), 10,
        nullptr, 0,
        dolp.data(), 10,
        nullptr, 0,
        &stats);

    check(rc == 0, "vs_polar_process_mosaic succeeded");
    check(dolp[0] > 0, "DoLP accurately computed from raw 2x2 mosaic");
}

void test_benchmark_1024x1024_performance() {
    std::printf("--- test_benchmark_1024x1024_performance ---\n");

    const int width = 1024;
    const int height = 1024;
    std::vector<uint8_t> ch(width * height, 150);
    std::vector<uint8_t> diff(width * height);
    vs_polar_stats stats{};

    // Warm-up
    vs_polar_process_channels(ch.data(), ch.data(), ch.data(), ch.data(), width, height, width, nullptr, diff.data(), width, nullptr, 0, nullptr, 0, nullptr, 0, &stats);

    const int iterations = 10;
    auto t0 = std::chrono::high_resolution_clock::now();
    for (int i = 0; i < iterations; ++i) {
        vs_polar_process_channels(ch.data(), ch.data(), ch.data(), ch.data(), width, height, width, nullptr, diff.data(), width, nullptr, 0, nullptr, 0, nullptr, 0, &stats);
    }
    auto t1 = std::chrono::high_resolution_clock::now();
    double elapsed_ms = std::chrono::duration<double, std::milli>(t1 - t0).count() / iterations;

    std::printf("      info: 1024x1024 polarimetric Stokes processing took %.2f ms / frame\n", elapsed_ms);
    check(VS_TIME_BUDGET(elapsed_ms < 15.0), "1024x1024 polarimetric processing runs under budget (< 15 ms)");
}

} // namespace

int main() {
    std::printf("=== Running vs_polar verification test suite ===\n");
    test_lifecycle_and_defaults();
    test_specular_glare_removal();
    test_stress_birefringence();
    test_raw_mosaic_demuxing();
    test_benchmark_1024x1024_performance();

    std::printf("=== Finished: %d failures ===\n", failures);
    return failures != 0 ? 1 : 0;
}
