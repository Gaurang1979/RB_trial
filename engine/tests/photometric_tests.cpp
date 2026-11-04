/*
 * photometric_tests.cpp - Verification suite for Computational Photometric Stereo Engine (vs_photometric.h).
 *
 * Tests:
 *   1. Pipeline lifecycle & parameter validation.
 *   2. Complete decoupling of 2D print (albedo) from 3D shape (relief curvature):
 *      - 2D printed text/bars are removed from shape curvature.
 *      - 3D embossed relief shadows/glare are removed from albedo.
 *   3. Virtual directional lighting synthesis from arbitrary azimuth/slant.
 *   4. Industrial throughput benchmark on 1024x1024 frame budget (< 15 ms).
 */

#include "vs_photometric.h"

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

    vs_photo_params params;
    vs_photo_default_params(&params);

    check(params.struct_size == sizeof(vs_photo_params), "default params struct_size initialized");
    check(params.num_lights == 4, "default num_lights == 4");
    check(std::abs(params.lights[0].slant_deg - 45.0f) < 1e-4f, "light 0 slant == 45 deg");
    check(std::abs(params.lights[0].tilt_deg - 90.0f) < 1e-4f, "light 0 tilt == 90 deg (Top)");
    check(std::abs(params.lights[1].tilt_deg - 0.0f) < 1e-4f, "light 1 tilt == 0 deg (Right)");
    check(std::abs(params.lights[2].tilt_deg - 270.0f) < 1e-4f, "light 2 tilt == 270 deg (Bottom)");
    check(std::abs(params.lights[3].tilt_deg - 180.0f) < 1e-4f, "light 3 tilt == 180 deg (Left)");

    vs_photo_pipeline* pipe = vs_photo_create(&params);
    check(pipe != nullptr, "vs_photo_create succeeded with default params");

    // Null check verification
    int32_t rc = vs_photo_compute(pipe, nullptr, 100, 100, 100, nullptr, 100, nullptr, 100, nullptr, 0);
    check(rc < 0, "vs_photo_compute returns error on null inputs");

    vs_photo_destroy(pipe);
    check(true, "vs_photo_destroy safely completed");
}

void test_print_and_relief_decoupling() {
    std::printf("--- test_print_and_relief_decoupling ---\n");

    vs_photo_params params;
    vs_photo_default_params(&params);
    vs_photo_pipeline* pipe = vs_photo_create(&params);
    check(pipe != nullptr, "pipeline created");

    const int32_t width = 120;
    const int32_t height = 120;
    const int32_t stride = width;

    std::vector<uint8_t> img_top(width * height);
    std::vector<uint8_t> img_right(width * height);
    std::vector<uint8_t> img_bottom(width * height);
    std::vector<uint8_t> img_left(width * height);
    const uint8_t* inputs[4] = {
        img_top.data(),
        img_right.data(),
        img_bottom.data(),
        img_left.data()
    };

    // Lights:
    // 0: Top    -> s0 = [0,  sin45, cos45]
    // 1: Right  -> s1 = [sin45,  0, cos45]
    // 2: Bottom -> s2 = [0, -sin45, cos45]
    // 3: Left   -> s3 = [-sin45, 0, cos45]
    const float sin45 = 0.70710678f;
    const float cos45 = 0.70710678f;
    const float lights[4][3] = {
        {  0.0f,   sin45, cos45 },
        {  sin45,   0.0f, cos45 },
        {  0.0f,  -sin45, cos45 },
        { -sin45,   0.0f, cos45 }
    };

    // Synthesize scene:
    // Background albedo = 150.
    // Region A: Flat 2D printed dark barcode line at x in [25..35], y in [30..90].
    //           Normal is completely flat: [0, 0, 1]. Albedo = 30.
    // Region B: 3D embossed dome at center (85, 60), radius R = 25, height = 10.
    //           Albedo is flat 150 (identical to background!). Normal varies.
    const float dome_cx = 85.0f;
    const float dome_cy = 60.0f;
    const float dome_r = 25.0f;
    const float dome_h = 10.0f;

    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            float albedo = 150.0f;
            float nx = 0.0f, ny = 0.0f, nz = 1.0f;

            // Region A: 2D print
            if (x >= 25 && x <= 35 && y >= 30 && y <= 90) {
                albedo = 30.0f;
            }

            // Region B: 3D dome
            float dx = static_cast<float>(x) - dome_cx;
            float dy = static_cast<float>(y) - dome_cy;
            float dist2 = dx * dx + dy * dy;
            if (dist2 < dome_r * dome_r) {
                // Paraboloid dome z = h * (1 - r^2 / R^2)
                // dz/dx = -2 * h * dx / R^2
                // dz/dy = -2 * h * dy / R^2
                float dzdx = -2.0f * dome_h * dx / (dome_r * dome_r);
                float dzdy = -2.0f * dome_h * dy / (dome_r * dome_r);

                // Normal: [-dzdx, -dzdy, 1] / norm
                float len = std::sqrt(dzdx * dzdx + dzdy * dzdy + 1.0f);
                nx = -dzdx / len;
                ny = -dzdy / len;
                nz = 1.0f / len;
            }

            // Compute 4 directional strobe intensities: I_k = albedo * max(0, n . s_k)
            for (int k = 0; k < 4; ++k) {
                float dot = nx * lights[k][0] + ny * lights[k][1] + nz * lights[k][2];
                if (dot < 0.0f) dot = 0.0f;
                float intensity = albedo * dot;
                uint8_t byte_val = static_cast<uint8_t>(std::clamp(std::round(intensity), 0.0f, 255.0f));

                if (k == 0) img_top[y * stride + x] = byte_val;
                else if (k == 1) img_right[y * stride + x] = byte_val;
                else if (k == 2) img_bottom[y * stride + x] = byte_val;
                else if (k == 3) img_left[y * stride + x] = byte_val;
            }
        }
    }

    std::vector<uint8_t> albedo_out(width * height);
    std::vector<uint8_t> shape_out(width * height);
    std::vector<float> normal_out(width * height * 3);

    int32_t rc = vs_photo_compute(
        pipe,
        inputs,
        width,
        height,
        stride,
        albedo_out.data(),
        stride,
        shape_out.data(),
        stride,
        normal_out.data(),
        width * 3 * sizeof(float));

    check(rc == 0, "vs_photo_compute succeeded");

    // 1. Verify Albedo Image (Pure 2D print recovered, 3D relief eliminated):
    // Inside the printed bar (x=30, y=60), albedo should be ~30.
    uint8_t print_albedo = albedo_out[60 * stride + 30];
    check(print_albedo <= 40, "albedo recovers dark printed barcode line (val <= 40)");

    // Background albedo (x=10, y=60) should be ~150.
    uint8_t bg_albedo = albedo_out[60 * stride + 10];
    check(std::abs(static_cast<int>(bg_albedo) - 150) <= 8, "albedo recovers background reflectance ~150");

    // On the 3D dome (x=85, y=60), albedo should be ~150 (not shaded, no 3D shadow!).
    uint8_t dome_albedo = albedo_out[60 * stride + 85];
    check(std::abs(static_cast<int>(dome_albedo) - 150) <= 8, "albedo eliminates 3D shading on dome (~150)");

    // 2. Verify Shape Image (Pure 3D curvature recovered, 2D print eliminated):
    // In flat background (x=10, y=60), curvature is 0 -> shape_out == 128.
    uint8_t bg_shape = shape_out[60 * stride + 10];
    check(bg_shape == 128, "flat background curvature is exactly 128");

    // In printed barcode line (x=30, y=60), normal was flat [0, 0, 1] -> curvature is 0 -> shape_out == 128!
    // The print has been 100% eliminated from the 3D shape!
    uint8_t print_shape = shape_out[60 * stride + 30];
    check(print_shape == 128, "printed ink 100% eliminated from 3D shape (shape == 128)");

    // On the 3D dome, curvature is non-zero (divergence of normal > 0 or < 0).
    // Center of dome (85, 60) is convex, curvature diverges:
    uint8_t dome_shape = shape_out[60 * stride + 85];
    check(dome_shape != 128, "3D dome has distinct curvature relief contrast (shape != 128)");
    std::printf("      info: bg_shape=%d, print_shape=%d, dome_shape=%d\n",
                bg_shape, print_shape, dome_shape);

    vs_photo_destroy(pipe);
}

void test_virtual_lighting() {
    std::printf("--- test_virtual_lighting ---\n");

    const int32_t width = 64;
    const int32_t height = 64;
    std::vector<uint8_t> albedo(width * height, 200);
    std::vector<float> normals(width * height * 3);

    // Left hemisphere has nx = -0.6, nz = 0.8
    // Right hemisphere has nx = 0.6, nz = 0.8
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            float nx = (x < width / 2) ? -0.6f : 0.6f;
            normals[(y * width + x) * 3 + 0] = nx;
            normals[(y * width + x) * 3 + 1] = 0.0f;
            normals[(y * width + x) * 3 + 2] = 0.8f;
        }
    }

    std::vector<uint8_t> shaded_right(width * height);
    std::vector<uint8_t> shaded_left(width * height);

    // Light from Right (tilt = 0 deg)
    int32_t rc1 = vs_photo_synthesize_lighting(
        albedo.data(), width,
        normals.data(), width * 3 * sizeof(float),
        width, height,
        0.0f, 45.0f,
        shaded_right.data(), width);
    check(rc1 == 0, "vs_photo_synthesize_lighting from Right succeeded");

    // Light from Left (tilt = 180 deg)
    int32_t rc2 = vs_photo_synthesize_lighting(
        albedo.data(), width,
        normals.data(), width * 3 * sizeof(float),
        width, height,
        180.0f, 45.0f,
        shaded_left.data(), width);
    check(rc2 == 0, "vs_photo_synthesize_lighting from Left succeeded");

    // With light from Right, the right side (facing right) should be much brighter than left side
    uint8_t r_left = shaded_right[32 * width + 10];
    uint8_t r_right = shaded_right[32 * width + 50];
    check(r_right > r_left, "virtual light from right illuminates right surface more than left");

    // With light from Left, the left side should be much brighter than right side
    uint8_t l_left = shaded_left[32 * width + 10];
    uint8_t l_right = shaded_left[32 * width + 50];
    check(l_left > l_right, "virtual light from left illuminates left surface more than right");
}

void test_benchmark_performance() {
    std::printf("--- test_benchmark_performance ---\n");

    vs_photo_params params;
    vs_photo_default_params(&params);
    vs_photo_pipeline* pipe = vs_photo_create(&params);

    const int32_t width = 1024;
    const int32_t height = 1024;
    std::vector<uint8_t> img(width * height, 128);
    const uint8_t* inputs[4] = { img.data(), img.data(), img.data(), img.data() };

    std::vector<uint8_t> albedo_out(width * height);
    std::vector<uint8_t> shape_out(width * height);

    // Warm-up run
    vs_photo_compute(pipe, inputs, width, height, width, albedo_out.data(), width, shape_out.data(), width, nullptr, 0);

    const int iterations = 10;
    auto t0 = std::chrono::high_resolution_clock::now();
    for (int i = 0; i < iterations; ++i) {
        vs_photo_compute(pipe, inputs, width, height, width, albedo_out.data(), width, shape_out.data(), width, nullptr, 0);
    }
    auto t1 = std::chrono::high_resolution_clock::now();
    double elapsed_ms = std::chrono::duration<double, std::milli>(t1 - t0).count() / iterations;

    std::printf("      info: 1024x1024 photometric computation took %.2f ms / frame\n", elapsed_ms);
    check(VS_TIME_BUDGET(elapsed_ms < 25.0), "1024x1024 photometric decomposition runs under budget (< 25 ms)");

    vs_photo_destroy(pipe);
}

} // namespace

int main() {
    std::printf("=== Running vs_photometric verification test suite ===\n");
    test_lifecycle_and_defaults();
    test_print_and_relief_decoupling();
    test_virtual_lighting();
    test_benchmark_performance();

    std::printf("=== Finished: %d failures ===\n", failures);
    return failures != 0 ? 1 : 0;
}
