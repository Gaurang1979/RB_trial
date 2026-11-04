/*
 * inspect_tests.cpp - Unit and benchmark tests for the Golden Template & Variation Model Engine.
 */
#include "vs_inspect.h"

#include "check.h"
#include <cmath>
#include <cstring>
#include <iostream>
#include <vector>
#include <chrono>

static void test_default_params() {
    std::cout << "[TEST] vs_inspect default parameters... " << std::flush;
    vs_inspect_params p{};
    vs_inspect_default_params(&p);

    VS_CHECK(p.struct_size == sizeof(vs_inspect_params));
    VS_CHECK(p.sensitivity >= 0.5f && p.sensitivity <= 2.0f);
    VS_CHECK(p.base_offset >= 5.0f && p.base_offset <= 30.0f);
    VS_CHECK(p.edge_tolerance_px >= 1.0f);
    VS_CHECK(p.edge_margin_weight >= 0.1f);
    VS_CHECK(p.min_defect_area_px >= 5);
    VS_CHECK(p.normalize_lighting == 1);
    std::cout << "PASS\n";
}

static void test_golden_teach_and_pass() {
    std::cout << "[TEST] Golden teaching and normal part pass... " << std::flush;
    const int32_t w = 128, h = 128;
    vs_inspect_params p{};
    vs_inspect_default_params(&p);
    p.base_offset = 10.0f;
    p.min_defect_area_px = 10;

    vs_inspect_model* model = vs_inspect_create(w, h, &p);
    VS_CHECK(model != nullptr);

    // Create golden reference image with a printed dark rectangle
    std::vector<uint8_t> golden(w * h, 200);
    for (int32_t y = 30; y < 70; ++y) {
        for (int32_t x = 30; x < 90; ++x) {
            golden[y * w + x] = 50;
        }
    }

    // Add 5 samples with natural noise
    for (int32_t i = 0; i < 5; ++i) {
        std::vector<uint8_t> sample = golden;
        for (size_t k = 0; k < sample.size(); ++k) {
            int32_t noise = (static_cast<int32_t>(k + i) % 3) - 1;
            sample[k] = static_cast<uint8_t>(std::clamp(static_cast<int32_t>(sample[k]) + noise, 0, 255));
        }
        int32_t res = vs_inspect_add_sample(model, sample.data(), w, h, w);
        VS_CHECK(res == 0);
    }

    int32_t trained_samples = 0;
    int32_t res = vs_inspect_finalize_teach(model, &trained_samples);
    VS_CHECK(res == 0);
    VS_CHECK(trained_samples == 5);

    // Inspect normal part
    vs_inspect_result out{};
    out.struct_size = sizeof(vs_inspect_result);
    res = vs_inspect_run(model, golden.data(), w, h, w, &out, nullptr, 0);
    VS_CHECK(res == 0);
    VS_CHECK(out.verdict == VS_INSPECT_PASS);
    VS_CHECK(out.defect_count == 0);
    VS_CHECK(out.total_defect_area == 0);
    VS_CHECK(out.confidence >= 80.0f);

    vs_inspect_destroy(model);
    std::cout << "PASS (confidence: " << out.confidence << ")\n";
}

static void test_dark_and_bright_defects() {
    std::cout << "[TEST] Detecting dark void and bright scratch defects... " << std::flush;
    const int32_t w = 128, h = 128;
    vs_inspect_params p{};
    vs_inspect_default_params(&p);
    p.base_offset = 12.0f;
    p.min_defect_area_px = 12;

    vs_inspect_model* model = vs_inspect_create(w, h, &p);

    // Golden part
    std::vector<uint8_t> golden(w * h, 180);
    for (int32_t y = 20; y < 60; ++y) {
        for (int32_t x = 20; x < 60; ++x) {
            golden[y * w + x] = 80;
        }
    }

    for (int32_t i = 0; i < 3; ++i) {
        vs_inspect_add_sample(model, golden.data(), w, h, w);
    }
    vs_inspect_finalize_teach(model, nullptr);

    // 1. Inject dark stain on the light background at (80, 80)
    std::vector<uint8_t> dark_defect = golden;
    for (int32_t y = 80; y < 86; ++y) {
        for (int32_t x = 80; x < 86; ++x) {
            dark_defect[y * w + x] = 30; // 36 pixels, area > 12
        }
    }

    vs_inspect_result res_dark{};
    res_dark.struct_size = sizeof(vs_inspect_result);
    std::vector<uint8_t> diff(w * h, 0);
    int32_t res = vs_inspect_run(model, dark_defect.data(), w, h, w, &res_dark, diff.data(), w);
    VS_CHECK(res == 0);
    VS_CHECK(res_dark.verdict == VS_INSPECT_DEFECT);
    VS_CHECK(res_dark.defect_count >= 1);
    VS_CHECK(res_dark.worst_defect.area_px == 36);
    VS_CHECK(res_dark.worst_defect.defect_type == VS_DEFECT_DARK);
    VS_CHECK(res_dark.worst_defect.bbox_x == 80 && res_dark.worst_defect.bbox_y == 80);

    // 2. Inject bright scratch across the dark square at (35, 30..45)
    std::vector<uint8_t> bright_defect = golden;
    for (int32_t y = 30; y <= 45; ++y) {
        bright_defect[y * w + 35] = 230;
        bright_defect[y * w + 36] = 230; // 32 pixels
    }

    vs_inspect_result res_bright{};
    res_bright.struct_size = sizeof(vs_inspect_result);
    res = vs_inspect_run(model, bright_defect.data(), w, h, w, &res_bright, nullptr, 0);
    VS_CHECK(res == 0);
    VS_CHECK(res_bright.verdict == VS_INSPECT_DEFECT);
    VS_CHECK(res_bright.worst_defect.defect_type == VS_DEFECT_BRIGHT);
    VS_CHECK(res_bright.worst_defect.area_px >= 30);

    vs_inspect_destroy(model);
    std::cout << "PASS (dark area: " << res_dark.worst_defect.area_px
              << ", bright area: " << res_bright.worst_defect.area_px << ")\n";
}

static void test_edge_aware_tolerance() {
    std::cout << "[TEST] Edge-aware tolerance suppressing jitter false rejects... " << std::flush;
    const int32_t w = 100, h = 100;
    vs_inspect_params p{};
    vs_inspect_default_params(&p);
    p.edge_tolerance_px = 2.0f;
    p.edge_margin_weight = 0.25f;
    p.base_offset = 10.0f;
    p.min_defect_area_px = 15;

    vs_inspect_model* model = vs_inspect_create(w, h, &p);

    // Sharp edge from x=0..49 (100) to x=50..99 (200)
    std::vector<uint8_t> golden(w * h, 100);
    for (int32_t y = 0; y < h; ++y) {
        for (int32_t x = 50; x < w; ++x) {
            golden[y * w + x] = 200;
        }
    }

    for (int32_t i = 0; i < 3; ++i) {
        vs_inspect_add_sample(model, golden.data(), w, h, w);
    }
    vs_inspect_finalize_teach(model, nullptr);

    // Image with 1-pixel edge jitter shift (x=51 instead of 50)
    std::vector<uint8_t> jittered(w * h, 100);
    for (int32_t y = 0; y < h; ++y) {
        for (int32_t x = 51; x < w; ++x) {
            jittered[y * w + x] = 200;
        }
    }

    vs_inspect_result out{};
    out.struct_size = sizeof(vs_inspect_result);
    int32_t res = vs_inspect_run(model, jittered.data(), w, h, w, &out, nullptr, 0);
    VS_CHECK(res == 0);
    // Edge tolerance prevents false reject
    VS_CHECK(out.verdict == VS_INSPECT_PASS);

    vs_inspect_destroy(model);
    std::cout << "PASS\n";
}

static void test_save_and_load_roundtrip() {
    std::cout << "[TEST] Model serialization save/load roundtrip... " << std::flush;
    const int32_t w = 80, h = 80;
    vs_inspect_params p{};
    vs_inspect_default_params(&p);

    vs_inspect_model* model1 = vs_inspect_create(w, h, &p);
    std::vector<uint8_t> golden(w * h, 140);
    vs_inspect_add_sample(model1, golden.data(), w, h, w);
    vs_inspect_finalize_teach(model1, nullptr);

    const char* path = "/tmp/test_variation.vsim";
    int32_t res = vs_inspect_save(model1, path);
    VS_CHECK(res == 0);
    vs_inspect_destroy(model1);

    vs_inspect_model* model2 = nullptr;
    res = vs_inspect_load(path, &model2);
    VS_CHECK(res == 0);
    VS_CHECK(model2 != nullptr);

    vs_inspect_result out{};
    out.struct_size = sizeof(vs_inspect_result);
    res = vs_inspect_run(model2, golden.data(), w, h, w, &out, nullptr, 0);
    VS_CHECK(res == 0);
    VS_CHECK(out.verdict == VS_INSPECT_PASS);

    vs_inspect_destroy(model2);
    std::cout << "PASS\n";
}

static void test_performance_benchmark() {
    std::cout << "[TEST] Industrial 1 MP performance benchmark... " << std::flush;
    const int32_t w = 1024, h = 1024; // 1 Megapixel
    vs_inspect_params p{};
    vs_inspect_default_params(&p);

    vs_inspect_model* model = vs_inspect_create(w, h, &p);
    VS_CHECK(model != nullptr);

    std::vector<uint8_t> golden(w * h, 128);
    // Draw some typical packaging features
    for (int32_t y = 200; y < 800; y += 10) {
        for (int32_t x = 100; x < 900; ++x) {
            golden[y * w + x] = 40;
        }
    }

    vs_inspect_add_sample(model, golden.data(), w, h, w);
    vs_inspect_finalize_teach(model, nullptr);

    // Warm-up run
    vs_inspect_result out{};
    out.struct_size = sizeof(vs_inspect_result);
    vs_inspect_run(model, golden.data(), w, h, w, &out, nullptr, 0);

    // 10 benchmark runs
    double total_ms = 0.0;
    const int iters = 10;
    for (int i = 0; i < iters; ++i) {
        vs_inspect_run(model, golden.data(), w, h, w, &out, nullptr, 0);
        total_ms += out.time_ms;
    }

    double avg_ms = total_ms / iters;
    std::cout << "PASS (Average 1 MP run: " << avg_ms << " ms; budget <= 15.0 ms)\n";
    VS_CHECK(VS_TIME_BUDGET(avg_ms < 15.0));

    vs_inspect_destroy(model);
}

int main() {
    std::cout << "========================================\n";
    std::cout << "Running Golden Template & Variation Tests\n";
    std::cout << "========================================\n";

    test_default_params();
    test_golden_teach_and_pass();
    test_dark_and_bright_defects();
    test_edge_aware_tolerance();
    test_save_and_load_roundtrip();
    test_performance_benchmark();

    std::cout << "\nAll Golden Template & Variation tests passed successfully!\n";
    return 0;
}
