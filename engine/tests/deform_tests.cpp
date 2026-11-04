#include "vs_deform.h"
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

void test_lifecycle_and_defaults() {
    std::cout << "--- test_lifecycle_and_defaults ---" << std::endl;
    vs_deform_params params;
    vs_deform_default_params(&params);

    TEST_ASSERT(params.struct_size == sizeof(vs_deform_params), "params struct_size initialized");
    TEST_ASSERT(params.pyramid_levels == 3, "default pyramid_levels == 3");
    TEST_ASSERT(params.smoothness_weight > 0.2f, "default smoothness_weight > 0.2");
    TEST_ASSERT(params.rupture_strain_threshold >= 0.30f, "rupture threshold >= 30%");

    vs_deform_stats stats;
    float mesh_x[16], mesh_y[16];
    int rc = vs_deform_estimate(nullptr, 100, 100, 100, nullptr, 100, 100, 100, &params, mesh_x, mesh_y, 4, 4, &stats);
    TEST_ASSERT(rc == VS_DEFORM_ERR_NULL_PTR, "returns error on null pointer");

    uint8_t dummy[100];
    rc = vs_deform_estimate(dummy, -5, 100, 100, dummy, 100, 100, 100, &params, mesh_x, mesh_y, 4, 4, &stats);
    TEST_ASSERT(rc == VS_DEFORM_ERR_INVALID_DIM, "returns error on invalid dimension");

    rc = vs_deform_estimate(dummy, 100, 100, 100, dummy, 100, 100, 100, &params, mesh_x, mesh_y, 1, 4, &stats);
    TEST_ASSERT(rc == VS_DEFORM_ERR_INVALID_MESH, "returns error on mesh < 2x2");
}

void test_rigid_and_non_rigid_pouch_unwarping() {
    std::cout << "--- test_rigid_and_non_rigid_pouch_unwarping ---" << std::endl;
    const int W = 120;
    const int H = 120;

    std::vector<uint8_t> ref(W * H, 200);

    // Create a golden reference pouch with grid and text-like barcode bars
    for (int y = 20; y < 100; ++y) {
        for (int x = 20; x < 100; ++x) {
            if ((x % 12 < 4) || (y % 16 < 4)) {
                ref[y * W + x] = 40; // Dark grid lines
            }
        }
    }

    // Synthesize deformed test image with sinusoidal wrinkle wave
    // dx(y) = 4.0 * sin(2*pi*y / 60.0)
    std::vector<uint8_t> deformed(W * H, 200);
    for (int y = 0; y < H; ++y) {
        for (int x = 0; x < W; ++x) {
            float sy = (float)y;
            float sx = (float)x - 3.5f * std::sin(2.0f * 3.14159265f * (float)y / 50.0f);

            int x0 = (int)sx;
            int y0 = (int)sy;
            if (x0 >= 0 && x0 < W - 1 && y0 >= 0 && y0 < H - 1) {
                float fx = sx - (float)x0;
                float p0 = (float)ref[y0 * W + x0];
                float p1 = (float)ref[y0 * W + x0 + 1];
                deformed[y * W + x] = (uint8_t)(p0 + fx * (p1 - p0) + 0.5f);
            } else {
                deformed[y * W + x] = (x0 >= 0 && x0 < W) ? ref[y * W + x0] : 200;
            }
        }
    }

    const int cols = 9;
    const int rows = 9;
    std::vector<float> mesh_dx(cols * rows, 0.0f);
    std::vector<float> mesh_dy(cols * rows, 0.0f);
    vs_deform_stats stats;

    vs_deform_params params;
    vs_deform_default_params(&params);
    params.pyramid_levels = 3;
    params.max_iterations_per_level = 20;
    params.smoothness_weight = 0.25f;

    int rc = vs_deform_estimate(deformed.data(), W, H, W,
                                ref.data(), W, H, W,
                                &params,
                                mesh_dx.data(), mesh_dy.data(),
                                cols, rows,
                                &stats);

    TEST_ASSERT(rc == VS_DEFORM_OK, "vs_deform_estimate succeeded");
    std::cout << "      info: alignment=" << stats.final_alignment_score
              << " mean_disp=" << stats.mean_displacement_px
              << " max_disp=" << stats.max_displacement_px
              << " is_ok=" << stats.is_match_ok
              << " ruptured=" << stats.is_film_ruptured
              << " crumple=" << stats.is_excessive_crumple
              << " bending=" << stats.total_bending_energy
              << " stretch=" << stats.max_area_stretch << std::endl;

    TEST_ASSERT(stats.final_alignment_score >= 0.75f, "high alignment score on unwarped pouch");
    TEST_ASSERT(stats.mean_displacement_px > 1.0f, "non-rigid wrinkle displacement detected");
    TEST_ASSERT(stats.is_match_ok == 1, "pouch match verdict is OK");

    // Unwarp image
    std::vector<uint8_t> unwarped(W * H, 0);
    rc = vs_deform_unwarp(deformed.data(), W, H, W,
                          mesh_dx.data(), mesh_dy.data(),
                          cols, rows,
                          unwarped.data(), W, H, W);

    TEST_ASSERT(rc == VS_DEFORM_OK, "vs_deform_unwarp succeeded");

    // Compute MSE against reference: unwarped MSE should be significantly lower than deformed MSE
    double raw_err = 0.0;
    double unwarped_err = 0.0;
    int count = 0;
    for (int y = 25; y < 95; ++y) {
        for (int x = 25; x < 95; ++x) {
            int idx = y * W + x;
            float r = (float)ref[idx];
            float d = (float)deformed[idx];
            float u = (float)unwarped[idx];
            raw_err += (d - r) * (d - r);
            unwarped_err += (u - r) * (u - r);
            count++;
        }
    }
    raw_err /= count;
    unwarped_err /= count;

    std::cout << "      info: raw deformed MSE=" << raw_err << ", unwarped MSE=" << unwarped_err << std::endl;
    TEST_ASSERT(unwarped_err < raw_err * 0.5, "unwarping reduces wrinkle distortion MSE by > 50%");
}

void test_strain_analysis_rupture_and_crumple() {
    std::cout << "--- test_strain_analysis_rupture_and_crumple ---" << std::endl;
    const int cols = 5;
    const int rows = 5;
    std::vector<float> mesh_dx(cols * rows, 0.0f);
    std::vector<float> mesh_dy(cols * rows, 0.0f);

    float cell_w = 20.0f;
    float cell_h = 20.0f;

    vs_deform_params params;
    vs_deform_default_params(&params);
    params.rupture_strain_threshold = 0.35f; // 35% area expansion
    params.crumple_bending_threshold = 30.0f;

    vs_deform_stats stats;

    // 1. Mild normal stretch (10% elongation)
    // dx = c * 2.0 -> dudx = 2.0 / 20 = 0.10 -> det(J) = 1.10
    for (int r = 0; r < rows; ++r) {
        for (int c = 0; c < cols; ++c) {
            mesh_dx[r * cols + c] = (float)c * 2.0f;
        }
    }

    int rc = vs_deform_analyze_strain(mesh_dx.data(), mesh_dy.data(), cols, rows, cell_w, cell_h, &params, &stats);
    TEST_ASSERT(rc == VS_DEFORM_OK, "vs_deform_analyze_strain succeeded on mild stretch");
    TEST_ASSERT(stats.is_film_ruptured == 0, "mild 10% stretch does not flag film rupture");
    TEST_ASSERT(stats.is_excessive_crumple == 0, "smooth stretch has 0 bending energy");

    // 2. Severe tear / hole puncture expansion (60% area expansion)
    // dx = c * 12.0 -> dudx = 12.0 / 20 = 0.60 -> det(J) = 1.60 > 1.35
    for (int r = 0; r < rows; ++r) {
        for (int c = 0; c < cols; ++c) {
            mesh_dx[r * cols + c] = (float)c * 12.0f;
        }
    }

    rc = vs_deform_analyze_strain(mesh_dx.data(), mesh_dy.data(), cols, rows, cell_w, cell_h, &params, &stats);
    TEST_ASSERT(rc == VS_DEFORM_OK, "strain analysis on severe stretch");
    TEST_ASSERT(stats.is_film_ruptured == 1, "severe stretch (>35%) correctly flagged as film rupture");
    TEST_ASSERT(stats.max_area_stretch > 0.50f, "detected stretch > 50%");

    // 3. Excessive jagged crumple (high second derivatives)
    std::fill(mesh_dx.begin(), mesh_dx.end(), 0.0f);
    std::fill(mesh_dy.begin(), mesh_dy.end(), 0.0f);
    // Center node sharply displaced by 25 px
    mesh_dx[2 * cols + 2] = 25.0f;
    mesh_dy[2 * cols + 2] = -25.0f;

    rc = vs_deform_analyze_strain(mesh_dx.data(), mesh_dy.data(), cols, rows, cell_w, cell_h, &params, &stats);
    TEST_ASSERT(rc == VS_DEFORM_OK, "strain analysis on crumple");
    TEST_ASSERT(stats.is_excessive_crumple == 1, "sharp localized crumple flagged as excessive crumple NG");
    TEST_ASSERT(stats.total_bending_energy > 30.0f, "bending energy exceeds crumple threshold");
}

void test_benchmark_performance() {
    std::cout << "--- test_benchmark_performance ---" << std::endl;
    const int W = 512;
    const int H = 512;

    std::vector<uint8_t> ref(W * H, 220);
    std::vector<uint8_t> test(W * H, 220);

    for (int y = 50; y < 450; y += 20) {
        for (int x = 50; x < 450; ++x) {
            ref[y * W + x] = 30;
            test[y * W + x] = 30;
        }
    }

    const int cols = 9;
    const int rows = 9;
    std::vector<float> mesh_dx(cols * rows, 0.0f);
    std::vector<float> mesh_dy(cols * rows, 0.0f);
    vs_deform_stats stats;

    vs_deform_params params;
    vs_deform_default_params(&params);
    params.pyramid_levels = 3;
    params.max_iterations_per_level = 10;

    auto t0 = std::chrono::high_resolution_clock::now();
    int rc = vs_deform_estimate(test.data(), W, H, W,
                                ref.data(), W, H, W,
                                &params,
                                mesh_dx.data(), mesh_dy.data(),
                                cols, rows,
                                &stats);
    auto t1 = std::chrono::high_resolution_clock::now();
    double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();

    std::cout << "      info: 512x512 non-rigid mesh registration took " << ms << " ms / frame" << std::endl;
    TEST_ASSERT(rc == VS_DEFORM_OK, "benchmark estimate succeeded");
    TEST_ASSERT(VS_TIME_BUDGET(ms < 30.0), "512x512 registration runs under industrial budget (< 30 ms)");
}

int main() {
    std::cout << "=== Running vs_deform verification test suite ===" << std::endl;
    test_lifecycle_and_defaults();
    test_rigid_and_non_rigid_pouch_unwarping();
    test_strain_analysis_rupture_and_crumple();
    test_benchmark_performance();

    std::cout << "=== Finished: " << failed_asserts << " failures out of " << total_asserts << " assertions ===" << std::endl;
    return (failed_asserts == 0) ? 0 : 1;
}
