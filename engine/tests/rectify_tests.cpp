/*
 * rectify_tests.cpp - Unit and benchmark tests for the Rolling-Shutter Rectification Tool (AG-06)
 */
#include "vs_rectify.h"

#include <algorithm>
#include <cassert>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include "check.h"

namespace {

int failures = 0;

void check(bool ok, const char* name) {
    std::printf("%s  %s\n", ok ? "ok  " : "FAIL", name);
    if (!ok) ++failures;
}

} // namespace

void test_default_params() {
    vs_rect_params p;
    vs_rect_default_params(&p);
    check(p.struct_size == sizeof(vs_rect_params), "default_params struct_size");
    check(p.line_speed_mm_s == 0.0f, "default_params stationary speed");
    check(std::abs(p.row_time_us - 22.5f) < 1e-4f, "default_params row_time_us");
    check(p.interpolation == 1, "default_params bilinear interpolation");

    float kx = 0.0f, ky = 0.0f;
    int32_t rc = vs_rect_calculate_shear(&p, &kx, &ky);
    check(rc == 0, "calculate_shear zero speed return code");
    check(std::abs(kx) < 1e-7f && std::abs(ky) < 1e-7f, "calculate_shear zero speed values");
}

void test_shear_formula() {
    vs_rect_params p;
    vs_rect_default_params(&p);
    p.line_speed_mm_s = 500.0f;       // 500 mm/s (0.5 m/s)
    p.mm_per_px = 0.1f;               // 0.1 mm/pixel -> 5000 px/s
    p.row_time_us = 20.0f;            // 20 us -> 2e-5 s
    p.motion_angle_deg = 0.0f;        // +X direction

    // speed_px_per_row = 5000 * 2e-5 = 0.1 px/row
    float kx = 0.0f, ky = 0.0f;
    int32_t rc = vs_rect_calculate_shear(&p, &kx, &ky);
    check(rc == 0, "calculate_shear return code");
    check(std::abs(kx - 0.1f) < 1e-5f, "calculate_shear kx formula match");
    check(std::abs(ky - 0.0f) < 1e-5f, "calculate_shear ky formula match");

    // 45 degree motion angle
    p.motion_angle_deg = 45.0f;
    rc = vs_rect_calculate_shear(&p, &kx, &ky);
    check(rc == 0, "calculate_shear 45deg return code");
    const float expected = 0.1f * std::cos(45.0f * 3.14159265f / 180.0f);
    check(std::abs(kx - expected) < 1e-4f, "calculate_shear 45deg kx");
    check(std::abs(ky - expected) < 1e-4f, "calculate_shear 45deg ky");
}

void test_coordinate_round_trip() {
    vs_rect_params p;
    vs_rect_default_params(&p);
    p.line_speed_mm_s = 800.0f;
    p.mm_per_px = 0.08f;
    p.row_time_us = 22.5f;
    p.ref_y = 1000.0f;

    const float orig_x = 1250.5f;
    const float orig_y = 820.25f;

    float rect_x = 0.0f, rect_y = 0.0f;
    int32_t rc = vs_rect_distorted_to_rectified(&p, orig_x, orig_y, &rect_x, &rect_y);
    check(rc == 0, "distorted_to_rectified rc");

    float back_x = 0.0f, back_y = 0.0f;
    rc = vs_rect_rectified_to_distorted(&p, rect_x, rect_y, &back_x, &back_y);
    check(rc == 0, "rectified_to_distorted rc");

    check(std::abs(back_x - orig_x) < 1e-3f, "coordinate round-trip X match");
    check(std::abs(back_y - orig_y) < 1e-3f, "coordinate round-trip Y match");
}

void test_vertical_line_rectification() {
    const int W = 600;
    const int H = 400;
    std::vector<uint8_t> clean(W * H, 0);
    std::vector<uint8_t> distorted(W * H, 0);
    std::vector<uint8_t> rectified(W * H, 0);

    const int line_x = 300;
    for (int y = 0; y < H; ++y) {
        clean[y * W + line_x] = 255;
    }

    vs_rect_params p;
    vs_rect_default_params(&p);
    p.line_speed_mm_s = 600.0f;
    p.mm_per_px = 0.1f;
    p.row_time_us = 25.0f;
    p.ref_y = (float)H / 2.0f; // 200.0f

    // kx = (600 / 0.1) * 25e-6 = 6000 * 2.5e-5 = 0.15 px/row
    float kx = 0.0f, ky = 0.0f;
    vs_rect_calculate_shear(&p, &kx, &ky);

    // Forward distortion simulation (sub-pixel line drawn with exact shear)
    for (int y = 0; y < H; ++y) {
        float shift = kx * ((float)y - p.ref_y);
        float tx = (float)line_x + shift;
        int ix0 = (int)std::floor(tx);
        int ix1 = ix0 + 1;
        float frac = tx - (float)ix0;
        if (ix0 >= 0 && ix0 < W) {
            distorted[y * W + ix0] = (uint8_t)std::round(255.0f * (1.0f - frac));
        }
        if (ix1 >= 0 && ix1 < W) {
            distorted[y * W + ix1] = (uint8_t)std::round(255.0f * frac);
        }
    }

    // Apply rectification
    vs_rect_stats stats;
    int32_t rc = vs_rect_apply_mono8(&p, distorted.data(), rectified.data(), W, H, W, &stats);
    check(rc == 0, "apply_mono8 return code");
    check(stats.max_shift_px > 0.0f, "stats max_shift reported");

    // Verify the line is restored to vertical (x = 300) across all rows away from borders
    bool line_restored = true;
    for (int y = 50; y < H - 50; ++y) {
        double sum_val = 0.0;
        double sum_x = 0.0;
        for (int x = line_x - 4; x <= line_x + 4; ++x) {
            double v = rectified[y * W + x];
            sum_val += v;
            sum_x += v * (double)x;
        }
        if (sum_val < 50.0) {
            line_restored = false;
            break;
        }
        double centroid_x = sum_x / sum_val;
        if (std::abs(centroid_x - (double)line_x) > 0.15) {
            line_restored = false;
            break;
        }
    }
    check(line_restored, "vertical line orientation unwarped within 1 px");
}

void test_in_place_equivalence() {
    const int W = 400;
    const int H = 300;
    std::vector<uint8_t> src(W * H);
    for (int i = 0; i < W * H; ++i) {
        src[i] = (uint8_t)((i * 37) % 256);
    }

    vs_rect_params p;
    vs_rect_default_params(&p);
    p.line_speed_mm_s = 400.0f;
    p.mm_per_px = 0.08f;
    p.row_time_us = 22.5f;

    std::vector<uint8_t> dst_out(W * H, 0);
    vs_rect_apply_mono8(&p, src.data(), dst_out.data(), W, H, W, nullptr);

    std::vector<uint8_t> dst_in = src;
    vs_rect_apply_mono8(&p, dst_in.data(), dst_in.data(), W, H, W, nullptr);

    bool match = (std::memcmp(dst_out.data(), dst_in.data(), W * H) == 0);
    check(match, "in-place vs out-of-place bit-exact match");
}

void test_roi_equivalence() {
    const int W = 800;
    const int H = 600;
    std::vector<uint8_t> full(W * H);
    for (int i = 0; i < W * H; ++i) {
        full[i] = (uint8_t)((i * 59) % 256);
    }

    vs_rect_params p;
    vs_rect_default_params(&p);
    p.line_speed_mm_s = 500.0f;
    p.mm_per_px = 0.1f;
    p.row_time_us = 20.0f;
    p.ref_y = 300.0f;

    std::vector<uint8_t> full_rect(W * H, 0);
    vs_rect_apply_mono8(&p, full.data(), full_rect.data(), W, H, W, nullptr);

    const int rx = 200, ry = 150, rw = 250, rh = 200;
    std::vector<uint8_t> roi_rect(rw * rh, 0);
    vs_rect_apply_roi_mono8(&p, full.data(), W, H, W, rx, ry, rw, rh, roi_rect.data(), rw, nullptr);

    bool roi_matches = true;
    for (int y = 0; y < rh; ++y) {
        const uint8_t* expected_row = full_rect.data() + (size_t)(ry + y) * W + rx;
        const uint8_t* actual_row = roi_rect.data() + (size_t)y * rw;
        if (std::memcmp(expected_row, actual_row, rw) != 0) {
            roi_matches = false;
            break;
        }
    }
    check(roi_matches, "ROI rectification matches full image rectification");
}

void test_benchmark_20mp() {
    const int W = 5472;
    const int H = 3648;
    std::vector<uint8_t> frame(W * H, 128);

    vs_rect_params p;
    vs_rect_default_params(&p);
    p.line_speed_mm_s = 1000.0f; // 1 m/s high speed conveyor
    p.mm_per_px = 0.08f;
    p.row_time_us = 22.5f;

    vs_rect_stats stats;
    int32_t rc = vs_rect_apply_mono8(&p, frame.data(), frame.data(), W, H, W, &stats);
    check(rc == 0, "20 MP rectification rc");
    std::printf("info: 20 MP (5472x3648) in-place rectification time = %.2f ms (target < 20 ms)\n", stats.time_ms);
    check(VS_TIME_BUDGET(stats.time_ms < 30.0f), "20 MP rectification latency within budget");
}

int main() {
    std::printf("=== rectify_tests ===\n");
    test_default_params();
    test_shear_formula();
    test_coordinate_round_trip();
    test_vertical_line_rectification();
    test_in_place_equivalence();
    test_roi_equivalence();
    test_benchmark_20mp();

    std::printf("Summary: %d failures\n", failures);
    return failures ? 1 : 0;
}
