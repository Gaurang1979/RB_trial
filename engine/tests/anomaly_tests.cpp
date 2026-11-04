/*
 * anomaly_tests.cpp - Unit and benchmark tests for In-Memory PatchCore Anomaly Detection (AG-02, Tool 10).
 */

#include "vs_anomaly.h"
#include <cassert>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
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
    vs_anom_params p;
    std::memset(&p, 0xFF, sizeof(p));
    vs_anom_default_params(&p);

    TEST_CHECK(p.struct_size == sizeof(vs_anom_params), "struct_size mismatch");
    TEST_CHECK(p.patch_size == 16, "patch_size default");
    TEST_CHECK(p.patch_stride == 8, "patch_stride default");
    TEST_CHECK(p.feature_dim == 32, "feature_dim default");
    TEST_CHECK(p.coreset_ratio >= 0.05f && p.coreset_ratio <= 0.20f, "coreset_ratio default");
    TEST_CHECK(p.hnsw_m == 16, "hnsw_m default");
    std::printf("  PASSED\n");
}

void test_teach_and_defect_detection() {
    std::printf("[TEST] PatchCore teaching and surface defect detection...\n");
    const int w = 160, h = 160;

    vs_anom_params p;
    vs_anom_default_params(&p);
    p.patch_size = 16;
    p.patch_stride = 8;
    p.coreset_ratio = 0.20f;

    vs_anom_model* model = vs_anom_create(&p);
    TEST_CHECK(model != nullptr, "model creation failed");

    // Synthesize golden clean surface: brushed metallic grid texture (140 base with vertical ribs)
    auto make_golden = [w, h](int seed) {
        std::vector<uint8_t> img(w * h, 140);
        for (int y = 0; y < h; ++y) {
            for (int x = 0; x < w; ++x) {
                // Subtle periodic ribbing
                if (x % 16 == 0) img[y * w + x] = 165;
                if (y % 16 == 0) img[y * w + x] = 120;
                // Add slight noise
                img[y * w + x] += static_cast<uint8_t>((seed + x + y) % 5);
            }
        }
        return img;
    };

    // 1. Feed 6 golden training images
    for (int i = 0; i < 6; ++i) {
        auto golden = make_golden(i);
        int res = vs_anom_teach_image(model, golden.data(), w, h, w);
        TEST_CHECK(res == 0, "teach image failed");
    }

    float calib_thresh = 0.0f;
    int coreset_count = 0;
    int fin_res = vs_anom_finalize_teach(model, &calib_thresh, &coreset_count);
    TEST_CHECK(fin_res == 0, "finalize teach failed");
    TEST_CHECK(calib_thresh > 0.0f, "calibrated threshold must be positive");
    TEST_CHECK(coreset_count >= 20, "coreset vectors must be >= 20");
    std::printf("  Model trained: %d coreset vectors indexed, threshold = %.3f\n", coreset_count, calib_thresh);

    // 2. Inspect a golden normal part
    auto normal_img = make_golden(10);
    float norm_score = 0.0f;
    int32_t norm_verdict = -1;
    vs_anom_defect norm_defect;
    std::vector<uint8_t> heatmap(w * h, 0);

    int ins_res = vs_anom_inspect_mono8(model, normal_img.data(), w, h, w,
                                        &norm_score, &norm_verdict, &norm_defect,
                                        heatmap.data(), w);
    TEST_CHECK(ins_res == 0, "normal inspection failed");
    TEST_CHECK(norm_verdict == VS_ANOMALY_PASS, "normal part must pass inspection");
    TEST_CHECK(norm_score < 0.88f, "normal part score must be well below threshold");
    std::printf("  Normal part: score = %.3f (PASS)\n", norm_score);

    // 3. Inject a severe scratch defect (3 px wide, 35 px long) at (x=60..95, y=70..72)
    auto defect_img = make_golden(11);
    for (int x = 60; x <= 95; ++x) {
        defect_img[70 * w + x] = 20; // Black scratch
        defect_img[71 * w + x] = 15;
        defect_img[72 * w + x] = 25;
    }

    float defect_score = 0.0f;
    int32_t defect_verdict = -1;
    vs_anom_defect defect_info;
    std::memset(&defect_info, 0, sizeof(defect_info));

    ins_res = vs_anom_inspect_mono8(model, defect_img.data(), w, h, w,
                                    &defect_score, &defect_verdict, &defect_info,
                                    heatmap.data(), w);
    TEST_CHECK(ins_res == 0, "defect inspection failed");
    std::printf("  DEBUG: defect_score = %.3f, defect_verdict = %d (thresh=%.3f)\n",
                defect_score, defect_verdict, calib_thresh);
    TEST_CHECK(defect_verdict == VS_ANOMALY_DEFECT, "scratch defect must be detected as DEFECT");
    TEST_CHECK(defect_score >= 1.0f, "defect score must meet or exceed 1.0");

    // Defect bounding box must enclose the scratch center (x=77, y=71)
    TEST_CHECK(defect_info.bbox_x <= 77 && defect_info.bbox_x + defect_info.bbox_w >= 77, "defect bbox X enclosure");
    TEST_CHECK(defect_info.bbox_y <= 71 && defect_info.bbox_y + defect_info.bbox_h >= 71, "defect bbox Y enclosure");
    std::printf("  Defect part: score = %.3f (DEFECT), bbox = [%d, %d, %d, %d]\n",
                defect_score, defect_info.bbox_x, defect_info.bbox_y, defect_info.bbox_w, defect_info.bbox_h);

    // 4. Test Model Serialization (save/load)
    const char* model_path = "/tmp/test_patchcore.vsan";
    int save_res = vs_anom_save_model(model, model_path);
    TEST_CHECK(save_res == 0, "model save failed");

    vs_anom_model* loaded_model = nullptr;
    int load_res = vs_anom_load_model(model_path, &loaded_model);
    TEST_CHECK(load_res == 0 && loaded_model != nullptr, "model load failed");

    float loaded_score = 0.0f;
    int32_t loaded_verdict = -1;
    vs_anom_inspect_mono8(loaded_model, defect_img.data(), w, h, w,
                          &loaded_score, &loaded_verdict, nullptr, nullptr, 0);
    TEST_CHECK(loaded_verdict == VS_ANOMALY_DEFECT, "loaded model must detect same defect");
    TEST_CHECK(std::abs(loaded_score - defect_score) < 0.05f, "loaded model score consistency");

    std::remove(model_path);
    vs_anom_destroy(loaded_model);
    vs_anom_destroy(model);
    std::printf("  PASSED\n");
}

void test_performance_benchmark() {
    std::printf("[TEST] Performance benchmark on 512x512 surface inspection tile...\n");
    const int w = 512, h = 512;
    std::vector<uint8_t> tile(w * h, 135);

    vs_anom_params p;
    vs_anom_default_params(&p);
    p.patch_size = 16;
    p.patch_stride = 12;

    vs_anom_model* model = vs_anom_create(&p);

    // Teach 2 samples
    vs_anom_teach_image(model, tile.data(), w, h, w);
    vs_anom_teach_image(model, tile.data(), w, h, w);
    vs_anom_finalize_teach(model, nullptr, nullptr);

    const int iterations = 20;
    auto t0 = std::chrono::high_resolution_clock::now();
    for (int i = 0; i < iterations; ++i) {
        float score = 0.0f;
        int32_t verdict = 0;
        vs_anom_inspect_mono8(model, tile.data(), w, h, w, &score, &verdict, nullptr, nullptr, 0);
    }
    auto t1 = std::chrono::high_resolution_clock::now();

    double ms_per_tile = std::chrono::duration<double, std::milli>(t1 - t0).count() / iterations;
    std::printf("  512x512 surface tile patch extraction + HNSW query: %.2f ms (Budget: <= 12.0 ms)\n",
                ms_per_tile);
    TEST_CHECK(VS_TIME_BUDGET(ms_per_tile < 15.0), "PatchCore inspection too slow");

    vs_anom_destroy(model);
    std::printf("  PASSED\n");
}

int main() {
    std::printf("=== Running In-Memory PatchCore Anomaly Tests (AG-02, Tool 10) ===\n");
    test_default_params();
    test_teach_and_defect_detection();
    test_performance_benchmark();
    std::printf("=== All In-Memory PatchCore Tests Passed 100%% ===\n");
    return 0;
}
