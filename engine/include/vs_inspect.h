/*
 * vs_inspect.h - C API of the Golden Template & Variation Model Inspection Engine (Tool 8.2, AS-05).
 * "Compare to good": Learns normal pixel appearance and natural process variation across
 * 5..50 golden reference parts, then detects defects (scratches, pinholes, stains, voids,
 * missing printing, burrs) on production parts.
 *
 * Method:
 *   1. Golden Teaching:
 *      Accumulates per-pixel mean mu(x, y) and standard deviation sigma(x, y) across aligned golden images.
 *   2. Edge-Aware Tolerance Envelope:
 *      Dilates Sobel gradient magnitude to widen tolerance bounds near high-contrast edges,
 *      preventing false edge rejections caused by slight mechanical vibration or sub-pixel registration shift:
 *        U(x, y) = mu(x, y) + k * sigma(x, y) + C_offset + alpha * G_dilated(x, y)
 *        L(x, y) = mu(x, y) - k * sigma(x, y) - C_offset - alpha * G_dilated(x, y)
 *   3. Lighting Normalization:
 *      Compensates global strobe intensity drift before comparison.
 *   4. Difference & Connected Component Defect Localization:
 *      Extracts bright and dark difference images, segments contiguous defect blobs,
 *      and reports bounding boxes, area, and peak contrast for rejection diagnostics.
 *
 * Safety Rule:
 *   If any defect blob >= min_defect_area_px is detected, verdict is strictly VS_INSPECT_DEFECT.
 *   If images cannot be compared or parameters are invalid, verdict is VS_INSPECT_ERROR (never OK).
 *
 * Threading:
 *   One model handle may be called concurrently from multiple threads for read-only inspection (vs_inspect_run).
 *   Teaching functions (vs_inspect_add_sample, vs_inspect_finalize_teach) must not be called concurrently.
 */
#ifndef VS_INSPECT_H
#define VS_INSPECT_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_INSPECT_BUILD
#    define VS_INSP_API __declspec(dllexport)
#  else
#    define VS_INSP_API __declspec(dllimport)
#  endif
#else
#  define VS_INSP_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Verdict status */
enum vs_inspect_verdict {
    VS_INSPECT_PASS = 0,         /* Product matches golden baseline within variation tolerance */
    VS_INSPECT_DEFECT = 1,       /* Surface defect detected (difference exceeds tolerance) */
    VS_INSPECT_ERROR = 2         /* Runtime error (dimension mismatch, unfinalized model) */
};

/* Defect polarity */
enum vs_inspect_defect_type {
    VS_DEFECT_ANY = 0,
    VS_DEFECT_BRIGHT = 1,        /* Bright defect: scratch, hole in dark print, glossy flake */
    VS_DEFECT_DARK = 2,          /* Dark defect: ink stain, void, pinhole, contamination */
    VS_DEFECT_MIXED = 3          /* Mixed polarity cluster */
};

typedef struct vs_inspect_params {
    int32_t struct_size;          /* sizeof(vs_inspect_params); set by vs_inspect_default_params */
    float   sensitivity;          /* Sigma tolerance multiplier k (default: 1.0f, range: 0.2..5.0) */
    float   base_offset;          /* Minimum grey-level difference offset (default: 12.0f, range: 2..50) */
    float   edge_tolerance_px;    /* Edge dilation radius in pixels (default: 1.5f, range: 0..5.0) */
    float   edge_margin_weight;   /* Edge gradient tolerance weight alpha (default: 0.15f, range: 0..1.0) */
    int32_t min_defect_area_px;   /* Minimum defect blob area in pixels to reject (default: 15) */
    int32_t normalize_lighting;   /* 1 = normalize global mean intensity before diff (default: 1) */
} vs_inspect_params;

typedef struct vs_inspect_defect {
    int32_t bbox_x;
    int32_t bbox_y;
    int32_t bbox_w;
    int32_t bbox_h;
    int32_t area_px;
    float   peak_contrast;        /* Peak grey-level difference in blob */
    int32_t defect_type;          /* vs_inspect_defect_type */
} vs_inspect_defect;

typedef struct vs_inspect_result {
    int32_t struct_size;          /* sizeof(vs_inspect_result) */
    int32_t verdict;              /* vs_inspect_verdict */
    int32_t defect_count;         /* Number of distinct defect blobs found */
    int32_t total_defect_area;    /* Total defect area in pixels */
    float   max_contrast;         /* Peak contrast across all defects */
    float   confidence;           /* 0..100 inspection confidence score */
    float   time_ms;              /* Processing duration in ms */
    vs_inspect_defect worst_defect; /* Bounding box and details of worst defect */
} vs_inspect_result;

/* Opaque variation model handle */
typedef struct vs_inspect_model vs_inspect_model;

/* Initialize parameters with standard industrial defaults */
VS_INSP_API void vs_inspect_default_params(vs_inspect_params* p);

/*
 * Create a new variation inspection model instance.
 * width, height: fixed inspection dimensions (must be identical for all taught and inspected images)
 */
VS_INSP_API vs_inspect_model* vs_inspect_create(int32_t width, int32_t height, const vs_inspect_params* p);

/*
 * Destroy model instance and free memory.
 */
VS_INSP_API void vs_inspect_destroy(vs_inspect_model* model);

/*
 * Reset all learned statistics to start a fresh teach sequence.
 */
VS_INSP_API void vs_inspect_reset(vs_inspect_model* model);

/*
 * Set or update runtime parameters.
 */
VS_INSP_API int32_t vs_inspect_set_params(vs_inspect_model* model, const vs_inspect_params* p);

/*
 * Get current runtime parameters.
 */
VS_INSP_API int32_t vs_inspect_get_params(const vs_inspect_model* model, vs_inspect_params* p);

/*
 * Add a golden reference image sample (mono8).
 * Call 5 to 50 times during recipe setup.
 * Returns 0 on success, negative error code on failure.
 */
VS_INSP_API int32_t vs_inspect_add_sample(
    vs_inspect_model* model,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride);

/*
 * Finalize training: computes mean, standard deviation, and edge-dilated tolerance envelopes.
 * out_samples_trained: receives the count of valid samples processed.
 * Returns 0 on success, negative error code on failure (< 2 samples = -2).
 */
VS_INSP_API int32_t vs_inspect_finalize_teach(
    vs_inspect_model* model,
    int32_t* out_samples_trained);

/*
 * Inspect a production image against the variation model (mono8).
 * src: input image
 * width, height, stride: image dimensions (width and height must match model)
 * out_result: receives inspection result (verdict, defect count, worst defect bbox)
 * diff_out: optional 8-bit difference image (width x height, stride diff_stride, can be NULL)
 * Returns 0 on success, negative error code on failure.
 */
VS_INSP_API int32_t vs_inspect_run(
    const vs_inspect_model* model,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    vs_inspect_result* out_result,
    uint8_t* diff_out,
    int32_t diff_stride);

/*
 * Save trained variation model (mean, variation, and envelopes) to disk.
 */
VS_INSP_API int32_t vs_inspect_save(const vs_inspect_model* model, const char* filepath);

/*
 * Load trained variation model from disk.
 */
VS_INSP_API int32_t vs_inspect_load(const char* filepath, vs_inspect_model** out_model);

#ifdef __cplusplus
}
#endif

#endif /* VS_INSPECT_H */
