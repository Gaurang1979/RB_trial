/*
 * vs_anomaly.h - C API of the In-Memory PatchCore Anomaly Detection Engine (AG-02, Tool 10):
 * Unsupervised surface defect detection (scratches, dents, foreign objects, torn labels, voids)
 * trained strictly from good golden images (zero defect samples required).
 *
 * Theory:
 * PatchCore (Roth et al., CVPR 2022) extracts localized patch feature representations across
 * normal inspection parts. A greedy minimax coreset selection retains an optimal, compact
 * manifold coverage M. Feature vectors are indexed in an in-memory Hierarchical Navigable
 * Small World (HNSW) graph, enabling O(log M) nearest-neighbor queries on CPU without GPU overhead.
 *
 * During inspection:
 *   1. Image is tiled into overlapping spatial patches of size patch_size x patch_size.
 *   2. For each patch, the nearest neighbor distance in M is queried from the HNSW index.
 *   3. A continuous 2D anomaly heatmap (0..255) is synthesized via spatial interpolation.
 *   4. Image-level score is calculated via neighborhood-aware top-k patch re-weighting.
 *   5. If score > anomaly_threshold, verdict = VS_ANOMALY_NG with defect bounding box.
 *
 * Safety Rule: High-confidence anomaly scores trigger NG line safety alerts with diagnostic heatmaps.
 * In doubt (borderline anomaly score near threshold), fails safe to NG.
 *
 * Threading: Multiple models can inspect separate cameras concurrently.
 */
#ifndef VS_ANOMALY_H
#define VS_ANOMALY_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_ANOMALY_BUILD
#    define VS_ANOM_API __declspec(dllexport)
#  else
#    define VS_ANOM_API __declspec(dllimport)
#  endif
#else
#  define VS_ANOM_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Verdict status */
enum vs_anomaly_verdict {
    VS_ANOMALY_PASS = 0,         /* Normal product, within golden baseline manifold */
    VS_ANOMALY_DEFECT = 1,       /* Defect detected (scratch, contamination, void, dent) */
    VS_ANOMALY_BORDERLINE = 2    /* Ambiguous part near threshold; fails safe to NG */
};

typedef struct vs_anom_params {
    int32_t struct_size;          /* sizeof(vs_anom_params); initialized by vs_anom_default_params */
    int32_t patch_size;           /* Patch window diameter in pixels (default: 16, range: 8..64) */
    int32_t patch_stride;         /* Grid stride between patch centers (default: 8, range: 4..32) */
    int32_t feature_dim;          /* Dimension of feature descriptor (default: 32) */
    float   coreset_ratio;        /* Fraction of teach vectors retained in coreset (default: 0.10f, 10%) */
    int32_t max_coreset_size;     /* Hard cap on coreset vectors in memory (default: 3000) */
    int32_t hnsw_m;               /* Number of bidirectional links per HNSW node (default: 16) */
    int32_t hnsw_ef_construction; /* Dynamic candidate list during HNSW construction (default: 64) */
    int32_t hnsw_ef_search;       /* Dynamic candidate list during HNSW search (default: 24) */
    float   sensitivity;          /* Anomaly detection sensitivity multiplier (default: 1.0f, range: 0.5..2.0) */
    int32_t min_defect_area_px;   /* Minimum defect blob area in pixels to trigger NG (default: 20) */
} vs_anom_params;

typedef struct vs_anom_defect {
    int32_t bbox_x;
    int32_t bbox_y;
    int32_t bbox_w;
    int32_t bbox_h;
    float   peak_anomaly_score;
    int32_t area_px;
} vs_anom_defect;

/* Opaque anomaly model handle */
typedef struct vs_anom_model vs_anom_model;

/* Initialize parameters with industrial defaults */
VS_ANOM_API void vs_anom_default_params(vs_anom_params* p);

/*
 * Create an anomaly model instance.
 */
VS_ANOM_API vs_anom_model* vs_anom_create(const vs_anom_params* p);

/*
 * Destroy model instance and free memory bank.
 */
VS_ANOM_API void vs_anom_destroy(vs_anom_model* model);

/*
 * Clear all taught baseline data.
 */
VS_ANOM_API void vs_anom_reset(vs_anom_model* model);

/*
 * Feed a golden good training image (mono8).
 * Can be called 10 to 50 times during job setup.
 * Returns 0 on success, negative error code on failure.
 */
VS_ANOM_API int32_t vs_anom_teach_image(
    vs_anom_model* model,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride);

/*
 * Finalize teach: runs minimax greedy Coreset subsampling, constructs the in-memory
 * HNSW graph index, and auto-calibrates the baseline anomaly threshold.
 * out_calibrated_threshold: receives the baseline decision threshold.
 * out_coreset_vectors: receives the final number of indexed vectors.
 * Returns 0 on success, negative error code on failure.
 */
VS_ANOM_API int32_t vs_anom_finalize_teach(
    vs_anom_model* model,
    float* out_calibrated_threshold,
    int32_t* out_coreset_vectors);

/*
 * Inspect an image for surface anomalies (mono8).
 * model: trained model instance
 * src: inspected image
 * width, height, stride: image dimensions
 * out_score: receives global normalized anomaly score (1.0 = threshold boundary)
 * out_verdict: receives vs_anomaly_verdict (VS_ANOMALY_PASS, DEFECT, or BORDERLINE)
 * out_defect: receives bounding box of worst defect (can be NULL)
 * heatmap_out: optional 8-bit heatmap buffer (width x height, stride heatmap_stride, can be NULL)
 * Returns 0 on success, negative error code on failure.
 */
VS_ANOM_API int32_t vs_anom_inspect_mono8(
    vs_anom_model* model,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    float* out_score,
    int32_t* out_verdict,
    vs_anom_defect* out_defect,
    uint8_t* heatmap_out,
    int32_t heatmap_stride);

/*
 * Save trained anomaly model and HNSW memory bank to disk.
 */
VS_ANOM_API int32_t vs_anom_save_model(vs_anom_model* model, const char* filepath);

/*
 * Load trained anomaly model from disk.
 */
VS_ANOM_API int32_t vs_anom_load_model(const char* filepath, vs_anom_model** out_model);

#ifdef __cplusplus
}
#endif

#endif /* VS_ANOMALY_H */
