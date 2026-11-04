/*
 * patchcore.cpp - Implementation of In-Memory PatchCore Anomaly Detection Engine (AG-02, Tool 10).
 *
 * Implements:
 * 1. Fast multi-quadrant oriented gradient patch feature extraction (32D descriptor).
 * 2. Minimax greedy Coreset manifold subsampling.
 * 3. In-memory HNSW graph indexing for logarithmic-time nearest-neighbor retrieval.
 * 4. Spatial 2D anomaly heatmap generation and defect bounding box localization.
 * 5. Automatic decision threshold calibration and fail-safe borderline gating.
 */

#include "vs_anomaly.h"
#include "hnsw.hpp"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <vector>

namespace {

// Extract 32D normalized gradient feature vector from a single S x S patch
void extract_patch_descriptor(
    const uint8_t* src,
    int stride,
    int x0,
    int y0,
    int s,
    float* out_vec)
{
    // Divide S x S patch into 2x2 quadrants, each computing an 8-bin orientation histogram
    std::memset(out_vec, 0, 32 * sizeof(float));

    int half_s = s / 2;

    for (int y = 1; y < s - 1; ++y) {
        int gy = y0 + y;
        const uint8_t* r_prev = src + (gy - 1) * stride;
        const uint8_t* r_curr = src + gy * stride;
        const uint8_t* r_next = src + (gy + 1) * stride;

        int q_y = (y < half_s) ? 0 : 1;

        for (int x = 1; x < s - 1; ++x) {
            int gx = x0 + x;
            int q_x = (x < half_s) ? 0 : 1;
            int q_idx = q_y * 2 + q_x; // Quadrant index 0..3

            // Gradient components
            int dx = r_curr[gx + 1] - r_curr[gx - 1];
            int dy = r_next[gx] - r_prev[gx];
            float mag = std::sqrt(static_cast<float>(dx * dx + dy * dy));

            if (mag < 1.0f) continue;

            // Angle in [0, 2π)
            float angle = std::atan2(static_cast<float>(dy), static_cast<float>(dx));
            if (angle < 0.0f) angle += 2.0f * 3.14159265f;

            // 8 orientation bins
            int bin = static_cast<int>(angle / (2.0f * 3.14159265f / 8.0f)) % 8;
            out_vec[q_idx * 8 + bin] += mag;
        }
    }

    // L2 normalization
    float norm_sq = 0.0f;
    for (int i = 0; i < 32; ++i) {
        norm_sq += out_vec[i] * out_vec[i];
    }
    float inv_norm = 1.0f / (std::sqrt(norm_sq) + 1e-5f);
    for (int i = 0; i < 32; ++i) {
        out_vec[i] *= inv_norm;
    }
}

} // namespace

struct vs_anom_model {
    vs_anom_params params;
    bool finalized;
    float calibrated_threshold;

    std::vector<float> raw_vectors; // Contiguous float buffer [N * dim]
    std::unique_ptr<HNSWIndex> index;
};

extern "C" {

void vs_anom_default_params(vs_anom_params* p) {
    if (!p) return;
    p->struct_size = sizeof(vs_anom_params);
    p->patch_size = 16;
    p->patch_stride = 8;
    p->feature_dim = 32;
    p->coreset_ratio = 0.10f;
    p->max_coreset_size = 2500;
    p->hnsw_m = 16;
    p->hnsw_ef_construction = 64;
    p->hnsw_ef_search = 24;
    p->sensitivity = 1.0f;
    p->min_defect_area_px = 20;
}

vs_anom_model* vs_anom_create(const vs_anom_params* p) {
    auto* m = new vs_anom_model();
    if (p) {
        m->params = *p;
    } else {
        vs_anom_default_params(&m->params);
    }
    m->finalized = false;
    m->calibrated_threshold = 0.5f;
    m->index = std::make_unique<HNSWIndex>(
        m->params.feature_dim,
        m->params.hnsw_m,
        m->params.hnsw_ef_construction,
        m->params.hnsw_ef_search);
    return m;
}

void vs_anom_destroy(vs_anom_model* model) {
    delete model;
}

void vs_anom_reset(vs_anom_model* model) {
    if (!model) return;
    model->finalized = false;
    model->calibrated_threshold = 0.5f;
    model->raw_vectors.clear();
    model->index->clear();
}

int32_t vs_anom_teach_image(
    vs_anom_model* model,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride)
{
    if (!model || !src || width <= 0 || height <= 0 || stride < width) {
        return -1;
    }

    int s = model->params.patch_size;
    int step = model->params.patch_stride;
    int dim = model->params.feature_dim;

    if (width < s || height < s) return -1;

    std::vector<float> desc(dim);

    for (int y = 0; y <= height - s; y += step) {
        for (int x = 0; x <= width - s; x += step) {
            extract_patch_descriptor(src, stride, x, y, s, desc.data());
            model->raw_vectors.insert(model->raw_vectors.end(), desc.begin(), desc.end());
        }
    }

    model->finalized = false;
    return 0;
}

int32_t vs_anom_finalize_teach(
    vs_anom_model* model,
    float* out_calibrated_threshold,
    int32_t* out_coreset_vectors)
{
    if (!model) return -1;

    int dim = model->params.feature_dim;
    size_t total_vectors = model->raw_vectors.size() / dim;
    if (total_vectors < 10) {
        return -1; // Need at least 10 vectors
    }

    // Determine target coreset size
    size_t target_m = static_cast<size_t>(total_vectors * model->params.coreset_ratio);
    target_m = std::clamp(target_m, size_t(20), static_cast<size_t>(model->params.max_coreset_size));
    target_m = std::min(target_m, total_vectors);

    // Greedy Minimax Coreset Subsampling
    std::vector<size_t> coreset_indices;
    coreset_indices.reserve(target_m);

    std::vector<float> min_dists(total_vectors, 1e9f);
    size_t first_idx = 0;
    coreset_indices.push_back(first_idx);

    const float* first_vec = model->raw_vectors.data() + first_idx * dim;
    for (size_t i = 0; i < total_vectors; ++i) {
        min_dists[i] = HNSWIndex::l2_sq(first_vec, model->raw_vectors.data() + i * dim, dim);
    }

    for (size_t step = 1; step < target_m; ++step) {
        size_t best_idx = 0;
        float max_d = -1.0f;
        for (size_t i = 0; i < total_vectors; ++i) {
            if (min_dists[i] > max_d) {
                max_d = min_dists[i];
                best_idx = i;
            }
        }

        coreset_indices.push_back(best_idx);
        const float* best_vec = model->raw_vectors.data() + best_idx * dim;

        for (size_t i = 0; i < total_vectors; ++i) {
            float d = HNSWIndex::l2_sq(best_vec, model->raw_vectors.data() + i * dim, dim);
            if (d < min_dists[i]) {
                min_dists[i] = d;
            }
        }
    }

    // Build HNSW index with the selected coreset
    model->index->clear();
    for (size_t idx : coreset_indices) {
        model->index->add_point(model->raw_vectors.data() + idx * dim);
    }

    // Auto-calibrate baseline threshold:
    // Find the maximum distance from any raw training patch to its nearest coreset neighbor (covering radius)
    float max_covering_radius = 0.05f;
    std::vector<int> nns;
    std::vector<float> dists;

    for (size_t i = 0; i < total_vectors; ++i) {
        const float* v = model->raw_vectors.data() + i * dim;
        model->index->search_knn(v, 1, nns, dists);
        if (!dists.empty()) {
            if (dists[0] > max_covering_radius) {
                max_covering_radius = dists[0];
            }
        }
    }

    float sens = std::clamp(model->params.sensitivity, 0.5f, 2.0f);
    model->calibrated_threshold = (max_covering_radius * 1.18f) / sens;
    model->finalized = true;

    if (out_calibrated_threshold) *out_calibrated_threshold = model->calibrated_threshold;
    if (out_coreset_vectors) *out_coreset_vectors = static_cast<int32_t>(coreset_indices.size());

    return 0;
}

int32_t vs_anom_inspect_mono8(
    vs_anom_model* model,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    float* out_score,
    int32_t* out_verdict,
    vs_anom_defect* out_defect,
    uint8_t* heatmap_out,
    int32_t heatmap_stride)
{
    if (!model || !src || !model->finalized || width <= 0 || height <= 0 || stride < width) {
        return -1;
    }

    int s = model->params.patch_size;
    int step = model->params.patch_stride;
    int dim = model->params.feature_dim;

    if (width < s || height < s) return -1;

    if (heatmap_out && heatmap_stride >= width) {
        for (int y = 0; y < height; ++y) {
            std::memset(heatmap_out + y * heatmap_stride, 0, width);
        }
    }

    std::vector<float> patch_scores;
    std::vector<int> patch_xs;
    std::vector<int> patch_ys;
    std::vector<float> desc(dim);

    std::vector<int> nns;
    std::vector<float> dists;

    float peak_score = 0.0f;
    int peak_x = 0, peak_y = 0;

    for (int y = 0; y <= height - s; y += step) {
        for (int x = 0; x <= width - s; x += step) {
            extract_patch_descriptor(src, stride, x, y, s, desc.data());

            // Query nearest neighbor in HNSW memory bank
            model->index->search_knn(desc.data(), 1, nns, dists);
            float d = (dists.empty()) ? 0.0f : dists[0];

            patch_scores.push_back(d);
            patch_xs.push_back(x);
            patch_ys.push_back(y);

            if (d > peak_score) {
                peak_score = d;
                peak_x = x;
                peak_y = y;
            }

            // Fill heatmap
            if (heatmap_out && heatmap_stride >= width) {
                float norm_patch = (d / model->calibrated_threshold) * 128.0f;
                uint8_t h_val = static_cast<uint8_t>(std::clamp(std::round(norm_patch), 0.0f, 255.0f));

                int hy_end = std::min(height, y + step);
                int hx_end = std::min(width, x + step);
                for (int hy = y; hy < hy_end; ++hy) {
                    uint8_t* hrow = heatmap_out + hy * heatmap_stride;
                    for (int hx = x; hx < hx_end; ++hx) {
                        if (h_val > hrow[hx]) hrow[hx] = h_val;
                    }
                }
            }
        }
    }

    if (patch_scores.empty()) return -1;

    // Image-level score: sensitive combination of peak patch score and top-3 average
    std::sort(patch_scores.rbegin(), patch_scores.rend());
    float peak = patch_scores[0];
    int k_top = std::min(3, static_cast<int>(patch_scores.size()));
    float sum_top = 0.0f;
    for (int i = 0; i < k_top; ++i) sum_top += patch_scores[i];
    float top_avg = sum_top / k_top;

    float raw_score = 0.75f * peak + 0.25f * top_avg;
    float normalized_score = raw_score / model->calibrated_threshold;

    int32_t verdict = VS_ANOMALY_PASS;
    if (normalized_score >= 1.0f) {
        verdict = VS_ANOMALY_DEFECT;
    } else if (normalized_score >= 0.88f) {
        verdict = VS_ANOMALY_BORDERLINE; // Fail-safe to NG
    }

    if (out_score) *out_score = normalized_score;
    if (out_verdict) *out_verdict = verdict;

    if (out_defect) {
        out_defect->bbox_x = std::max(0, peak_x - s / 4);
        out_defect->bbox_y = std::max(0, peak_y - s / 4);
        out_defect->bbox_w = std::min(width - out_defect->bbox_x, s + s / 2);
        out_defect->bbox_h = std::min(height - out_defect->bbox_y, s + s / 2);
        out_defect->peak_anomaly_score = normalized_score;
        out_defect->area_px = s * s;
    }

    return 0;
}

int32_t vs_anom_save_model(vs_anom_model* model, const char* filepath) {
    if (!model || !model->finalized || !filepath) return -1;

    std::ofstream out(filepath, std::ios::binary);
    if (!out.is_open()) return -1;

    uint32_t magic = 0x5653414E; // "VSAN"
    out.write(reinterpret_cast<const char*>(&magic), sizeof(magic));
    out.write(reinterpret_cast<const char*>(&model->params), sizeof(model->params));
    out.write(reinterpret_cast<const char*>(&model->calibrated_threshold), sizeof(model->calibrated_threshold));

    uint32_t num_vectors = static_cast<uint32_t>(model->index->size());
    out.write(reinterpret_cast<const char*>(&num_vectors), sizeof(num_vectors));

    for (size_t i = 0; i < num_vectors; ++i) {
        const float* v = model->index->get_vector(static_cast<int>(i));
        out.write(reinterpret_cast<const char*>(v), model->params.feature_dim * sizeof(float));
    }

    return 0;
}

int32_t vs_anom_load_model(const char* filepath, vs_anom_model** out_model) {
    if (!filepath || !out_model) return -1;

    std::ifstream in(filepath, std::ios::binary);
    if (!in.is_open()) return -1;

    uint32_t magic = 0;
    in.read(reinterpret_cast<char*>(&magic), sizeof(magic));
    if (magic != 0x5653414E) return -2;

    vs_anom_params p;
    in.read(reinterpret_cast<char*>(&p), sizeof(p));
    float calib_t = 0.0f;
    in.read(reinterpret_cast<char*>(&calib_t), sizeof(calib_t));

    uint32_t num_vectors = 0;
    in.read(reinterpret_cast<char*>(&num_vectors), sizeof(num_vectors));

    auto* m = vs_anom_create(&p);
    m->calibrated_threshold = calib_t;

    std::vector<float> vec(p.feature_dim);
    for (uint32_t i = 0; i < num_vectors; ++i) {
        in.read(reinterpret_cast<char*>(vec.data()), p.feature_dim * sizeof(float));
        m->index->add_point(vec.data());
    }

    m->finalized = true;
    *out_model = m;
    return 0;
}

} // extern "C"
