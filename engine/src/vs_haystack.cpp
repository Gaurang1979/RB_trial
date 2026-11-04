#include "vs_haystack.h"
#include <vector>
#include <string>
#include <mutex>
#include <cmath>
#include <cstring>
#include <algorithm>
#include <random>

struct vs_haystack_t {
    vs_haystack_params_t params;
    
    // Projection parameters: 2D projection from feature_dim
    std::vector<float> mean;
    std::vector<float> w1;
    std::vector<float> w2;
    float scale1;
    float scale2;
    bool has_projection;
    
    // Registered clusters (Cluster 0 = Golden OK)
    std::vector<vs_haystack_cluster_t> clusters;
    
    // Rolling circular points buffer
    std::vector<vs_haystack_point_t> points_buffer;
    size_t write_idx;
    size_t count;
    
    mutable std::mutex mtx;
};

static float dot_product(const float* a, const float* b, int n) {
    float sum = 0.0f;
    for (int i = 0; i < n; ++i) {
        sum += a[i] * b[i];
    }
    return sum;
}

static float l2_norm(const float* a, int n) {
    return std::sqrt(dot_product(a, a, n));
}

int vs_haystack_create(const vs_haystack_params_t* params, vs_haystack_t** out_engine) {
    if (!params || !out_engine || params->feature_dim <= 0 || params->max_points <= 0) {
        return -1;
    }
    
    auto* eng = new (std::nothrow) vs_haystack_t();
    if (!eng) return -2;
    
    eng->params = *params;
    if (eng->params.golden_radius <= 0.0f) eng->params.golden_radius = 0.35f;
    if (eng->params.ambiguity_margin <= 0.0f) eng->params.ambiguity_margin = 0.15f;
    
    eng->mean.assign(params->feature_dim, 0.0f);
    eng->w1.assign(params->feature_dim, 0.0f);
    eng->w2.assign(params->feature_dim, 0.0f);
    eng->scale1 = 1.0f;
    eng->scale2 = 1.0f;
    eng->has_projection = false;
    
    eng->points_buffer.resize(params->max_points);
    eng->write_idx = 0;
    eng->count = 0;
    
    // Register default Cluster 0: Golden OK
    vs_haystack_cluster_t golden;
    golden.cluster_id = 0;
    std::strncpy(golden.name, "Golden OK", VS_HAYSTACK_NAME_MAX - 1);
    golden.name[VS_HAYSTACK_NAME_MAX - 1] = '\0';
    golden.center_u = 0.0f;
    golden.center_v = 0.0f;
    golden.radius = eng->params.golden_radius;
    golden.point_count = 0;
    golden.color_rgba = 0x22C55EFF; // Bright emerald green
    eng->clusters.push_back(golden);
    
    *out_engine = eng;
    return 0;
}

void vs_haystack_destroy(vs_haystack_t* engine) {
    if (engine) {
        delete engine;
    }
}

int vs_haystack_set_projection(vs_haystack_t* engine,
                               const float* mean,
                               const float* w1,
                               const float* w2,
                               float scale1,
                               float scale2) {
    if (!engine || !mean || !w1 || !w2 || scale1 <= 0.0f || scale2 <= 0.0f) {
        return -1;
    }
    
    std::lock_guard<std::mutex> lock(engine->mtx);
    int dim = engine->params.feature_dim;
    std::memcpy(engine->mean.data(), mean, dim * sizeof(float));
    std::memcpy(engine->w1.data(), w1, dim * sizeof(float));
    std::memcpy(engine->w2.data(), w2, dim * sizeof(float));
    engine->scale1 = scale1;
    engine->scale2 = scale2;
    engine->has_projection = true;
    return 0;
}

int vs_haystack_fit_pca(vs_haystack_t* engine,
                        const float* embeddings,
                        int num_samples,
                        int feature_dim) {
    if (!engine || !embeddings || num_samples < 2 || feature_dim != engine->params.feature_dim) {
        return -1;
    }
    
    std::lock_guard<std::mutex> lock(engine->mtx);
    int D = feature_dim;
    int N = num_samples;
    
    // 1. Compute empirical mean vector
    std::vector<float> mean_vec(D, 0.0f);
    for (int i = 0; i < N; ++i) {
        const float* row = embeddings + (i * D);
        for (int d = 0; d < D; ++d) {
            mean_vec[d] += row[d];
        }
    }
    for (int d = 0; d < D; ++d) {
        mean_vec[d] /= static_cast<float>(N);
    }
    
    // 2. Build centered matrix X in memory
    std::vector<float> X(N * D);
    for (int i = 0; i < N; ++i) {
        const float* row = embeddings + (i * D);
        for (int d = 0; d < D; ++d) {
            X[i * D + d] = row[d] - mean_vec[d];
        }
    }
    
    // 3. Power iteration for 1st Principal Component (v1)
    std::vector<float> v1(D, 0.0f);
    // Deterministic initialization based on sinusoids
    for (int d = 0; d < D; ++d) {
        v1[d] = std::sin(static_cast<float>(d + 1));
    }
    float norm1 = l2_norm(v1.data(), D);
    if (norm1 > 1e-6f) {
        for (int d = 0; d < D; ++d) v1[d] /= norm1;
    }
    
    std::vector<float> X_v(N, 0.0f);
    for (int iter = 0; iter < 30; ++iter) {
        // X_v = X * v1
        for (int i = 0; i < N; ++i) {
            X_v[i] = dot_product(&X[i * D], v1.data(), D);
        }
        // v1_new = X^T * X_v
        std::vector<float> v1_new(D, 0.0f);
        for (int i = 0; i < N; ++i) {
            float s = X_v[i];
            for (int d = 0; d < D; ++d) {
                v1_new[d] += s * X[i * D + d];
            }
        }
        float nrm = l2_norm(v1_new.data(), D);
        if (nrm < 1e-7f) break;
        for (int d = 0; d < D; ++d) {
            v1[d] = v1_new[d] / nrm;
        }
    }
    
    // Compute variance along PC 1
    float var1 = 0.0f;
    for (int i = 0; i < N; ++i) {
        float proj = dot_product(&X[i * D], v1.data(), D);
        var1 += proj * proj;
    }
    float std1 = std::sqrt(var1 / static_cast<float>(N));
    float s1 = (std1 > 1e-4f) ? (3.0f * std1) : 1.0f;
    
    // 4. Deflate X for 2nd Principal Component: X_def = X - (X * v1) * v1^T
    std::vector<float> X_def = X;
    for (int i = 0; i < N; ++i) {
        float proj = dot_product(&X[i * D], v1.data(), D);
        for (int d = 0; d < D; ++d) {
            X_def[i * D + d] -= proj * v1[d];
        }
    }
    
    // 5. Power iteration for 2nd Principal Component (v2)
    std::vector<float> v2(D, 0.0f);
    for (int d = 0; d < D; ++d) {
        v2[d] = std::cos(static_cast<float>(d + 1));
    }
    // Orthogonalize against v1
    float proj12 = dot_product(v2.data(), v1.data(), D);
    for (int d = 0; d < D; ++d) {
        v2[d] -= proj12 * v1[d];
    }
    float norm2 = l2_norm(v2.data(), D);
    if (norm2 > 1e-6f) {
        for (int d = 0; d < D; ++d) v2[d] /= norm2;
    }
    
    for (int iter = 0; iter < 30; ++iter) {
        for (int i = 0; i < N; ++i) {
            X_v[i] = dot_product(&X_def[i * D], v2.data(), D);
        }
        std::vector<float> v2_new(D, 0.0f);
        for (int i = 0; i < N; ++i) {
            float s = X_v[i];
            for (int d = 0; d < D; ++d) {
                v2_new[d] += s * X_def[i * D + d];
            }
        }
        // Orthogonalize against v1
        float p = dot_product(v2_new.data(), v1.data(), D);
        for (int d = 0; d < D; ++d) {
            v2_new[d] -= p * v1[d];
        }
        float nrm = l2_norm(v2_new.data(), D);
        if (nrm < 1e-7f) break;
        for (int d = 0; d < D; ++d) {
            v2[d] = v2_new[d] / nrm;
        }
    }
    
    float var2 = 0.0f;
    for (int i = 0; i < N; ++i) {
        float proj = dot_product(&X_def[i * D], v2.data(), D);
        var2 += proj * proj;
    }
    float std2 = std::sqrt(var2 / static_cast<float>(N));
    float s2 = (std2 > 1e-4f) ? (3.0f * std2) : 1.0f;
    
    engine->mean = mean_vec;
    engine->w1 = v1;
    engine->w2 = v2;
    engine->scale1 = s1;
    engine->scale2 = s2;
    engine->has_projection = true;
    return 0;
}

int vs_haystack_add_cluster(vs_haystack_t* engine,
                            int cluster_id,
                            const char* name,
                            float center_u,
                            float center_v,
                            float radius,
                            uint32_t color_rgba) {
    if (!engine || !name || radius <= 0.0f) {
        return -1;
    }
    
    std::lock_guard<std::mutex> lock(engine->mtx);
    for (auto& cl : engine->clusters) {
        if (cl.cluster_id == cluster_id) {
            std::strncpy(cl.name, name, VS_HAYSTACK_NAME_MAX - 1);
            cl.name[VS_HAYSTACK_NAME_MAX - 1] = '\0';
            cl.center_u = center_u;
            cl.center_v = center_v;
            cl.radius = radius;
            cl.color_rgba = color_rgba;
            return 0;
        }
    }
    
    if (engine->clusters.size() >= VS_HAYSTACK_MAX_CLUSTERS) {
        return -2;
    }
    
    vs_haystack_cluster_t cl;
    cl.cluster_id = cluster_id;
    std::strncpy(cl.name, name, VS_HAYSTACK_NAME_MAX - 1);
    cl.name[VS_HAYSTACK_NAME_MAX - 1] = '\0';
    cl.center_u = center_u;
    cl.center_v = center_v;
    cl.radius = radius;
    cl.point_count = 0;
    cl.color_rgba = color_rgba;
    engine->clusters.push_back(cl);
    return 0;
}

int vs_haystack_project(vs_haystack_t* engine,
                        const float* embedding,
                        int feature_dim,
                        uint64_t image_id,
                        uint64_t timestamp_ms,
                        vs_haystack_point_t* out_point) {
    if (!engine || !embedding || feature_dim != engine->params.feature_dim || !out_point) {
        return -1;
    }
    
    std::lock_guard<std::mutex> lock(engine->mtx);
    int D = feature_dim;
    
    float u = 0.0f;
    float v = 0.0f;
    
    if (engine->has_projection) {
        float sum1 = 0.0f;
        float sum2 = 0.0f;
        for (int d = 0; d < D; ++d) {
            float centered = embedding[d] - engine->mean[d];
            sum1 += engine->w1[d] * centered;
            sum2 += engine->w2[d] * centered;
        }
        u = sum1 / engine->scale1;
        v = sum2 / engine->scale2;
    } else {
        // Fallback default: use first 2 dimensions normalized
        u = (D > 0) ? embedding[0] : 0.0f;
        v = (D > 1) ? embedding[1] : 0.0f;
    }
    
    // Clamp to screen bounds [-1.0, 1.0]
    u = std::clamp(u, -1.0f, 1.0f);
    v = std::clamp(v, -1.0f, 1.0f);
    
    // Determine Golden OK cluster center (cluster 0)
    float golden_u = 0.0f, golden_v = 0.0f;
    float golden_r = engine->params.golden_radius;
    for (const auto& cl : engine->clusters) {
        if (cl.cluster_id == 0) {
            golden_u = cl.center_u;
            golden_v = cl.center_v;
            golden_r = cl.radius;
            break;
        }
    }
    
    float dist_golden = std::sqrt((u - golden_u) * (u - golden_u) + (v - golden_v) * (v - golden_v));
    float margin = engine->params.ambiguity_margin;
    
    int label = VS_HAYSTACK_VERDICT_OK;
    int cluster_id = 0;
    float confidence = 1.0f;
    float dist_center = dist_golden;
    char def_name[VS_HAYSTACK_NAME_MAX] = "Golden OK";
    
    if (dist_golden <= golden_r) {
        // Securely inside Golden manifold
        label = VS_HAYSTACK_VERDICT_OK;
        cluster_id = 0;
        confidence = std::clamp(1.0f - (dist_golden / (golden_r * 2.0f)), 0.5f, 1.0f);
        dist_center = dist_golden;
        std::strncpy(def_name, "Golden OK", VS_HAYSTACK_NAME_MAX - 1);
    } else if (dist_golden <= golden_r + margin) {
        // In the ambiguity gap: Active learning mining candidate!
        label = VS_HAYSTACK_VERDICT_AMBIGUOUS;
        cluster_id = -1; // Borderline uncommitted
        confidence = 0.50f;
        dist_center = dist_golden;
        std::strncpy(def_name, "Borderline Uncertainty", VS_HAYSTACK_NAME_MAX - 1);
    } else {
        // Defect (NG) - Match against known defect clusters
        label = VS_HAYSTACK_VERDICT_NG;
        cluster_id = -1;
        float min_defect_dist = 1e9f;
        int best_defect_id = -1;
        const char* best_name = "Uncategorized Defect";
        
        for (const auto& cl : engine->clusters) {
            if (cl.cluster_id <= 0) continue; // Skip Golden
            float d = std::sqrt((u - cl.center_u) * (u - cl.center_u) + (v - cl.center_v) * (v - cl.center_v));
            if (d < min_defect_dist) {
                min_defect_dist = d;
                if (d <= cl.radius * 1.5f) {
                    best_defect_id = cl.cluster_id;
                    best_name = cl.name;
                }
            }
        }
        
        if (best_defect_id >= 0) {
            cluster_id = best_defect_id;
            dist_center = min_defect_dist;
            std::strncpy(def_name, best_name, VS_HAYSTACK_NAME_MAX - 1);
            confidence = std::clamp(1.0f - (min_defect_dist / 2.0f), 0.5f, 1.0f);
        } else {
            cluster_id = -1;
            dist_center = dist_golden;
            std::strncpy(def_name, "Uncategorized Defect", VS_HAYSTACK_NAME_MAX - 1);
            confidence = 0.90f;
        }
    }
    
    // Fill out point struct
    out_point->image_id = image_id;
    out_point->u = u;
    out_point->v = v;
    out_point->label = label;
    out_point->cluster_id = cluster_id;
    out_point->confidence = confidence;
    out_point->distance_to_center = dist_center;
    std::strncpy(out_point->defect_name, def_name, VS_HAYSTACK_NAME_MAX - 1);
    out_point->defect_name[VS_HAYSTACK_NAME_MAX - 1] = '\0';
    out_point->is_triaged = 0;
    out_point->timestamp_ms = timestamp_ms;
    
    // Push into circular rolling buffer
    size_t idx = engine->write_idx;
    engine->points_buffer[idx] = *out_point;
    engine->write_idx = (idx + 1) % engine->params.max_points;
    if (engine->count < static_cast<size_t>(engine->params.max_points)) {
        engine->count++;
    }
    
    // Increment cluster tally if assigned
    if (cluster_id >= 0) {
        for (auto& cl : engine->clusters) {
            if (cl.cluster_id == cluster_id) {
                cl.point_count++;
                break;
            }
        }
    }
    
    return 0;
}

int vs_haystack_triage_point(vs_haystack_t* engine,
                             uint64_t image_id,
                             int new_label,
                             int new_cluster_id,
                             const char* defect_name) {
    if (!engine) return -1;
    
    std::lock_guard<std::mutex> lock(engine->mtx);
    for (size_t i = 0; i < engine->count; ++i) {
        if (engine->points_buffer[i].image_id == image_id) {
            int old_cluster = engine->points_buffer[i].cluster_id;
            engine->points_buffer[i].label = new_label;
            engine->points_buffer[i].cluster_id = new_cluster_id;
            if (defect_name) {
                std::strncpy(engine->points_buffer[i].defect_name, defect_name, VS_HAYSTACK_NAME_MAX - 1);
                engine->points_buffer[i].defect_name[VS_HAYSTACK_NAME_MAX - 1] = '\0';
            }
            engine->points_buffer[i].is_triaged = 1;
            
            // Adjust cluster counts
            if (old_cluster >= 0) {
                for (auto& cl : engine->clusters) {
                    if (cl.cluster_id == old_cluster && cl.point_count > 0) {
                        cl.point_count--;
                        break;
                    }
                }
            }
            if (new_cluster_id >= 0) {
                for (auto& cl : engine->clusters) {
                    if (cl.cluster_id == new_cluster_id) {
                        cl.point_count++;
                        break;
                    }
                }
            }
            return 0;
        }
    }
    return -2; // Not found in rolling window
}

int vs_haystack_get_points(const vs_haystack_t* engine,
                           vs_haystack_point_t* out_points,
                           int max_count,
                           int* actual_count) {
    if (!engine || !out_points || !actual_count || max_count <= 0) return -1;
    
    std::lock_guard<std::mutex> lock(engine->mtx);
    int to_copy = std::min(max_count, static_cast<int>(engine->count));
    
    for (int i = 0; i < to_copy; ++i) {
        out_points[i] = engine->points_buffer[i];
    }
    *actual_count = to_copy;
    return 0;
}

int vs_haystack_get_clusters(const vs_haystack_t* engine,
                             vs_haystack_cluster_t* out_clusters,
                             int max_count,
                             int* actual_count) {
    if (!engine || !out_clusters || !actual_count || max_count <= 0) return -1;
    
    std::lock_guard<std::mutex> lock(engine->mtx);
    int to_copy = std::min(max_count, static_cast<int>(engine->clusters.size()));
    for (int i = 0; i < to_copy; ++i) {
        out_clusters[i] = engine->clusters[i];
    }
    *actual_count = to_copy;
    return 0;
}

int vs_haystack_get_ambiguous_points(const vs_haystack_t* engine,
                                     vs_haystack_point_t* out_points,
                                     int max_count,
                                     int* actual_count) {
    if (!engine || !out_points || !actual_count || max_count <= 0) return -1;
    
    std::lock_guard<std::mutex> lock(engine->mtx);
    int written = 0;
    for (size_t i = 0; i < engine->count && written < max_count; ++i) {
        if (engine->points_buffer[i].label == VS_HAYSTACK_VERDICT_AMBIGUOUS &&
            engine->points_buffer[i].is_triaged == 0) {
            out_points[written++] = engine->points_buffer[i];
        }
    }
    *actual_count = written;
    return 0;
}

int vs_haystack_clear_points(vs_haystack_t* engine) {
    if (!engine) return -1;
    std::lock_guard<std::mutex> lock(engine->mtx);
    engine->write_idx = 0;
    engine->count = 0;
    for (auto& cl : engine->clusters) {
        cl.point_count = 0;
    }
    return 0;
}
