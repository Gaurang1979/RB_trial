/*
 * infrared_tests.cpp - Verification suite for Infrared Vision Inspection Engine (vs_infrared.h).
 *
 * Tests:
 *   1. Amber bottle liquid fill-level detection through dark glass with foam rejection.
 *   2. Metameric anti-counterfeit security ink verification (VIS vs NIR comparison).
 *   3. SWIR 1450nm moisture barrier breach & seam liquid ingress detection.
 *   4. Solar cell electroluminescence (EL) micro-crack fracture extraction.
 *   5. Industrial throughput benchmark on 1024x1024 frames (< 15 ms).
 */

#include "vs_infrared.h"

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

void test_amber_bottle_fill_level() {
    std::printf("--- test_amber_bottle_fill_level ---\n");

    const int32_t width = 120;
    const int32_t height = 150;
    const int32_t stride = width;
    std::vector<uint8_t> nir_image(width * height);

    // Synthesize amber bottle under 850nm NIR backlighting:
    // Headspace (y in 0..40): air is clear, bottle glass transmits NIR -> intensity ~210
    // Foam layer (y in 40..55): bubbly froth scatters NIR -> intensity ~180
    // Liquid meniscus at y = 55: sharp step down to liquid body -> intensity ~110
    for (int y = 0; y < height; ++y) {
        uint8_t val = 210;
        if (y >= 40 && y < 55) {
            val = static_cast<uint8_t>(180 + (y % 3) * 4); // mild foam ripple
        } else if (y >= 55) {
            val = 110; // liquid absorption
        }
        for (int x = 0; x < width; ++x) {
            nir_image[y * stride + x] = val;
        }
    }

    vs_ir_fill_params params;
    vs_ir_default_fill_params(&params);
    params.roi_x = 20;
    params.roi_y = 10;
    params.roi_w = 80;
    params.roi_h = 90;
    params.datum_y = 150.0f; // bottle base datum
    params.px_per_mm = 10.0f;
    params.target_fill_mm = 9.5f; // (150 - 55) / 10 = 9.5 mm
    params.tol_underfill_mm = 1.0f;
    params.tol_overfill_mm = 1.0f;
    params.ignore_foam = 1;

    vs_ir_fill_result result{};
    int32_t rc = vs_ir_measure_fill_level(nir_image.data(), width, height, stride, &params, &result);

    check(rc == 0, "vs_ir_measure_fill_level succeeded");
    check(std::abs(result.meniscus_y_px - 55.0f) <= 1.0f, "meniscus detected at y ~ 55px through amber glass");
    check(std::abs(result.fill_height_mm - 9.5f) <= 0.15f, "fill height measured within 0.15 mm of truth");
    check(result.is_ok == 1, "bottle fill level classified as PASS (within tolerance)");
    check(result.foam_thickness_mm > 0.8f, "foam layer thickness accurately estimated above liquid");

    // Test under-fill condition: target is higher (e.g. 12.0 mm)
    params.target_fill_mm = 12.0f;
    vs_ir_measure_fill_level(nir_image.data(), width, height, stride, &params, &result);
    check(result.is_underfill == 1, "correctly flags under-fill bottle as NG");
}

void test_metameric_security_ink() {
    std::printf("--- test_metameric_security_ink ---\n");

    const int32_t width = 100;
    const int32_t height = 100;
    const int32_t stride = width;
    std::vector<uint8_t> vis_img(width * height, 220); // white paper background
    std::vector<uint8_t> nir_img(width * height, 220); // white paper background

    // Region 1: Organic decorative dye logo (x in 10..40, y in 20..60)
    // Dark in visible light (val=50), but 100% transparent in NIR (val=220)
    for (int y = 20; y <= 60; ++y) {
        for (int x = 10; x <= 40; ++x) {
            vis_img[y * stride + x] = 50;
            nir_img[y * stride + x] = 220; // disappears in NIR!
        }
    }

    // Region 2: Genuine carbon-black security ink code (x in 60..80, y in 20..60)
    // Dark in both visible light (val=40) and NIR (val=45)
    for (int y = 20; y <= 60; ++y) {
        for (int x = 60; x <= 80; ++x) {
            vis_img[y * stride + x] = 40;
            nir_img[y * stride + x] = 45; // absorbs NIR!
        }
    }

    vs_ir_metameric_params params;
    vs_ir_default_metameric_params(&params);
    params.min_security_area_px = 100;

    std::vector<uint8_t> ndmi_map(width * height);
    std::vector<uint8_t> mask(width * height);
    vs_ir_metameric_result result{};

    int32_t rc = vs_ir_verify_metameric(
        vis_img.data(), stride,
        nir_img.data(), stride,
        width, height,
        &params,
        ndmi_map.data(), stride,
        mask.data(), stride,
        &result);

    check(rc == 0, "vs_ir_verify_metameric succeeded");
    check(result.is_authentic == 1, "authentic carbon security ink verified (is_authentic == 1)");
    check(result.verified_ink_pixels >= 800, "carbon-black code pixels accurately quantified");
    check(result.transparent_dye_pixels >= 1000, "decorative dye transparency accurately captured");
    check(mask[30 * stride + 25] == 0, "decorative dye masked out (0 in security mask)");
    check(mask[30 * stride + 70] == 255, "carbon security ink marked 255 in security mask");
}

void test_swir_moisture_barrier_breach() {
    std::printf("--- test_swir_moisture_barrier_breach ---\n");

    const int32_t width = 120;
    const int32_t height = 60;
    const int32_t stride = width;

    // Heat seal flange runs horizontally along X (0..120), barrier height is Y (0..60)
    // Dry sealed plastic reflects SWIR 1450nm -> intensity ~180
    std::vector<uint8_t> swir_ok(width * height, 180);
    std::vector<uint8_t> swir_leak(width * height, 180);

    // Case 1: Minor isolated moisture drop at x in [50..55], y in [5..15] (height 10 out of 60)
    // Residual dry barrier is 50 pixels (> 25px safe threshold)
    for (int y = 5; y <= 15; ++y) {
        for (int x = 50; x <= 55; ++x) {
            swir_ok[y * stride + x] = 30; // water absorption
        }
    }

    vs_ir_moisture_params params;
    vs_ir_default_moisture_params(&params);
    params.absorption_thresh = 40.0f;
    params.min_nominal_barrier_px = 60.0f;
    params.min_safe_barrier_px = 25.0f;

    vs_ir_moisture_result res_ok{};
    vs_ir_detect_moisture_seam(swir_ok.data(), width, height, stride, 0, 0, width, height, &params, nullptr, 0, &res_ok);

    check(res_ok.has_complete_breach == 0, "no complete breach in minor drop case");
    check(res_ok.is_seal_ok == 1, "partial drop with sufficient remaining barrier passes (is_seal_ok == 1)");
    check(res_ok.min_remaining_barrier_px >= 45.0f, "min remaining barrier >= 45 px");

    // Case 2: Complete breach where liquid forms a channel across the entire flange (y from 0 to 59 at x=60)
    for (int y = 0; y < height; ++y) {
        swir_leak[y * stride + 60] = 30; // through-channel leak!
        swir_leak[y * stride + 61] = 30;
    }

    vs_ir_moisture_result res_leak{};
    vs_ir_detect_moisture_seam(swir_leak.data(), width, height, stride, 0, 0, width, height, &params, nullptr, 0, &res_leak);

    check(res_leak.has_complete_breach == 1, "through-channel liquid detected as complete breach (has_complete_breach == 1)");
    check(res_leak.is_seal_ok == 0, "breached seal flagged as NG");
    check(res_leak.min_remaining_barrier_px == 0.0f, "min remaining barrier is 0 px at leak channel");
}

void test_solar_cell_el_micro_crack() {
    std::printf("--- test_solar_cell_el_micro_crack ---\n");

    const int32_t width = 120;
    const int32_t height = 120;
    const int32_t stride = width;
    std::vector<uint8_t> el_img(width * height, 160); // glowing silicon under forward bias

    // Add a dark 1-pixel hairline fracture crack diagonally from (30, 30) to (70, 70)
    for (int i = 0; i <= 40; ++i) {
        el_img[(30 + i) * stride + (30 + i)] = 50; // dark non-emitting fracture
    }

    vs_ir_crack_params params;
    vs_ir_default_crack_params(&params);
    params.crack_response_thresh = 15.0f;
    params.min_crack_length_px = 8;

    std::vector<uint8_t> crack_mask(width * height);
    vs_ir_crack_result result{};

    int32_t rc = vs_ir_extract_cracks(el_img.data(), width, height, stride, &params, crack_mask.data(), stride, &result);

    check(rc == 0, "vs_ir_extract_cracks succeeded");
    check(result.is_cracked == 1, "solar cell micro-crack flagged as NG");
    check(result.crack_pixel_count >= 25, "fracture pixels accurately extracted");
    check(crack_mask[50 * stride + 50] == 255, "center of crack marked 255 in binary mask");
}

void test_benchmark_performance() {
    std::printf("--- test_benchmark_performance ---\n");

    const int32_t width = 1024;
    const int32_t height = 1024;
    std::vector<uint8_t> img(width * height, 160);
    std::vector<uint8_t> mask(width * height);

    vs_ir_crack_params params;
    vs_ir_default_crack_params(&params);
    vs_ir_crack_result result{};

    // Warm-up
    vs_ir_extract_cracks(img.data(), width, height, width, &params, mask.data(), width, &result);

    const int iterations = 10;
    auto t0 = std::chrono::high_resolution_clock::now();
    for (int i = 0; i < iterations; ++i) {
        vs_ir_extract_cracks(img.data(), width, height, width, &params, mask.data(), width, &result);
    }
    auto t1 = std::chrono::high_resolution_clock::now();
    double elapsed_ms = std::chrono::duration<double, std::milli>(t1 - t0).count() / iterations;

    std::printf("      info: 1024x1024 infrared crack extraction took %.2f ms / frame\n", elapsed_ms);
    check(VS_TIME_BUDGET(elapsed_ms < 15.0), "1024x1024 infrared processing runs under budget (< 15 ms)");
}

} // namespace

int main() {
    std::printf("=== Running vs_infrared verification test suite ===\n");
    test_amber_bottle_fill_level();
    test_metameric_security_ink();
    test_swir_moisture_barrier_breach();
    test_solar_cell_el_micro_crack();
    test_benchmark_performance();

    std::printf("=== Finished: %d failures ===\n", failures);
    return failures != 0 ? 1 : 0;
}
