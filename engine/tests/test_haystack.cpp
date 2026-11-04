#include "vs_haystack.h"
#include "check.h"
#include <cstdio>
#include <cmath>
#include <vector>
#include <chrono>
#include <cstring>

static void test_creation_destruction() {
    printf("[vs_haystack] Running test_creation_destruction...\n");
    vs_haystack_t* engine = nullptr;
    
    // Bad params
    vs_haystack_params_t bad_params = {0, 100, 0.35f, 0.15f};
    VS_CHECK(vs_haystack_create(&bad_params, &engine) != 0);
    VS_CHECK(vs_haystack_create(nullptr, &engine) != 0);
    
    // Good params
    vs_haystack_params_t params = {32, 500, 0.35f, 0.15f};
    int rc = vs_haystack_create(&params, &engine);
    VS_CHECK(rc == 0);
    VS_CHECK(engine != nullptr);
    
    // Check initial clusters (should have 1 default: Golden OK)
    vs_haystack_cluster_t clusters[4];
    int count = 0;
    rc = vs_haystack_get_clusters(engine, clusters, 4, &count);
    VS_CHECK(rc == 0);
    VS_CHECK(count == 1);
    VS_CHECK(clusters[0].cluster_id == 0);
    VS_CHECK(strcmp(clusters[0].name, "Golden OK") == 0);
    
    vs_haystack_destroy(engine);
    printf("  -> PASS\n");
}

static void test_pca_fit_and_projection() {
    printf("[vs_haystack] Running test_pca_fit_and_projection...\n");
    const int D = 16;
    const int N = 100;
    vs_haystack_params_t params = {D, 500, 0.35f, 0.15f};
    vs_haystack_t* engine = nullptr;
    vs_haystack_create(&params, &engine);
    
    // Generate synthetic training set: primary variance on feature 0 and 1
    std::vector<float> data(N * D, 0.0f);
    for (int i = 0; i < N; ++i) {
        float angle = (float)i * 6.283185f / (float)N;
        data[i * D + 0] = std::cos(angle) * 2.0f;
        data[i * D + 1] = std::sin(angle) * 1.5f;
        for (int d = 2; d < D; ++d) {
            data[i * D + d] = 0.05f * std::sin((float)(i * d));
        }
    }
    
    int rc = vs_haystack_fit_pca(engine, data.data(), N, D);
    VS_CHECK(rc == 0);
    
    // Add known defect cluster 1: "Seal Channel Leak" at (0.8, 0.0)
    rc = vs_haystack_add_cluster(engine, 1, "Seal Channel Leak", 0.8f, 0.0f, 0.2f, 0xEF4444FF);
    VS_CHECK(rc == 0);
    
    // 1. Test Golden point: near mean (all zeros)
    std::vector<float> golden_emb(D, 0.0f);
    vs_haystack_point_t pt_golden;
    rc = vs_haystack_project(engine, golden_emb.data(), D, 1001, 100000, &pt_golden);
    VS_CHECK(rc == 0);
    VS_CHECK(pt_golden.image_id == 1001);
    VS_CHECK(std::abs(pt_golden.u) < 0.2f);
    VS_CHECK(std::abs(pt_golden.v) < 0.2f);
    VS_CHECK(pt_golden.label == VS_HAYSTACK_VERDICT_OK);
    VS_CHECK(pt_golden.cluster_id == 0);
    VS_CHECK(strcmp(pt_golden.defect_name, "Golden OK") == 0);
    VS_CHECK(pt_golden.confidence > 0.7f);
    
    // 2. Test Defect point: placed near the defect cluster
    // Setting feature 0 to 4.8 aligns with u ~ 0.8
    std::vector<float> defect_emb(D, 0.0f);
    defect_emb[0] = 4.8f;
    vs_haystack_point_t pt_defect;
    rc = vs_haystack_project(engine, defect_emb.data(), D, 1002, 100010, &pt_defect);
    VS_CHECK(rc == 0);
    VS_CHECK(pt_defect.label == VS_HAYSTACK_VERDICT_NG);
    VS_CHECK(pt_defect.cluster_id == 1);
    VS_CHECK(strcmp(pt_defect.defect_name, "Seal Channel Leak") == 0);
    
    vs_haystack_destroy(engine);
    printf("  -> PASS\n");
}

static void test_active_learning_triage() {
    printf("[vs_haystack] Running test_active_learning_triage...\n");
    const int D = 8;
    vs_haystack_params_t params = {D, 100, 0.30f, 0.20f}; // golden_r=0.30, margin=0.20 => ambiguous [0.30, 0.50]
    vs_haystack_t* engine = nullptr;
    vs_haystack_create(&params, &engine);
    
    // Set explicit identity projection: u = emb[0], v = emb[1]
    std::vector<float> mean(D, 0.0f);
    std::vector<float> w1(D, 0.0f); w1[0] = 1.0f;
    std::vector<float> w2(D, 0.0f); w2[1] = 1.0f;
    vs_haystack_set_projection(engine, mean.data(), w1.data(), w2.data(), 1.0f, 1.0f);
    
    // Point at u=0.40, v=0.0 => distance 0.40 from golden center (0,0).
    // Lies exactly in ambiguous interval [0.30, 0.50]!
    std::vector<float> amb_emb(D, 0.0f);
    amb_emb[0] = 0.40f;
    vs_haystack_point_t pt;
    vs_haystack_project(engine, amb_emb.data(), D, 555, 200000, &pt);
    VS_CHECK(pt.label == VS_HAYSTACK_VERDICT_AMBIGUOUS);
    VS_CHECK(pt.is_triaged == 0);
    
    // Check ambiguous points queue
    vs_haystack_point_t pending[10];
    int pending_count = 0;
    vs_haystack_get_ambiguous_points(engine, pending, 10, &pending_count);
    VS_CHECK(pending_count == 1);
    VS_CHECK(pending[0].image_id == 555);
    
    // Operator triages this point: Approves as OK (Golden variation)
    int rc = vs_haystack_triage_point(engine, 555, VS_HAYSTACK_VERDICT_OK, 0, "Approved Golden Variation");
    VS_CHECK(rc == 0);
    
    // Now pending should be 0
    vs_haystack_get_ambiguous_points(engine, pending, 10, &pending_count);
    VS_CHECK(pending_count == 0);
    
    // Read back points
    vs_haystack_point_t pts[10];
    int total_pts = 0;
    vs_haystack_get_points(engine, pts, 10, &total_pts);
    VS_CHECK(total_pts == 1);
    VS_CHECK(pts[0].image_id == 555);
    VS_CHECK(pts[0].label == VS_HAYSTACK_VERDICT_OK);
    VS_CHECK(pts[0].cluster_id == 0);
    VS_CHECK(pts[0].is_triaged == 1);
    VS_CHECK(strcmp(pts[0].defect_name, "Approved Golden Variation") == 0);
    
    vs_haystack_destroy(engine);
    printf("  -> PASS\n");
}

static void test_speed_benchmark() {
    printf("[vs_haystack] Running test_speed_benchmark (10,000 projections)...\n");
    const int D = 64;
    const int N = 10000;
    vs_haystack_params_t params = {D, 1000, 0.35f, 0.15f};
    vs_haystack_t* engine = nullptr;
    vs_haystack_create(&params, &engine);
    
    std::vector<float> mean(D, 0.0f);
    std::vector<float> w1(D, 0.1f);
    std::vector<float> w2(D, 0.1f);
    vs_haystack_set_projection(engine, mean.data(), w1.data(), w2.data(), 1.0f, 1.0f);
    
    std::vector<float> emb(D, 0.05f);
    vs_haystack_point_t pt;
    
    auto t0 = std::chrono::high_resolution_clock::now();
    for (int i = 0; i < N; ++i) {
        vs_haystack_project(engine, emb.data(), D, (uint64_t)i, (uint64_t)i, &pt);
    }
    auto t1 = std::chrono::high_resolution_clock::now();
    double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
    double us_per_call = (ms * 1000.0) / (double)N;
    printf("  -> Total time for %d projections: %.2f ms (%.3f us / projection)\n", N, ms, us_per_call);
    VS_CHECK(VS_TIME_BUDGET(us_per_call < 50.0)); // Budget is < 50 us (0.05 ms)
    
    vs_haystack_destroy(engine);
    printf("  -> PASS\n");
}

int main() {
    printf("=========================================\n");
    printf("Running vs_haystack Unit Tests\n");
    printf("=========================================\n");
    test_creation_destruction();
    test_pca_fit_and_projection();
    test_active_learning_triage();
    test_speed_benchmark();
    printf("All vs_haystack unit tests PASSED successfully!\n");
    return 0;
}
