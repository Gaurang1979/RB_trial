/*
 * vs_drift.h - C API of the Optical Drift & Out-Of-Distribution (OOD) Detector (AG-12, AS-13):
 * Monitors optical baseline stability (LED strobe intensity, lens cleanliness, dust accumulation,
 * focus, contrast) using Mahalanobis distance in optical feature space over moving production windows.
 *
 * Theory:
 * Rather than waiting for parts to fail inspection (false rejects due to dust or LED fading),
 * vs_drift models the joint Gaussian distribution of baseline optical features:
 *     x = [mean_lum, contrast, sharpness, uniformity, corner_sharp_0..3, saturation, dark_floor]
 * At teach time, the baseline mean μ and covariance matrix Σ are computed.
 * For each production frame, the Mahalanobis distance:
 *     D_M(x) = sqrt((x - μ)^T Σ^-1 (x - μ))
 * measures departure from the golden optical envelope.
 * A sliding-window average D_bar over W parts (default: 50..200 parts) eliminates part-to-part jitter.
 * When D_bar exceeds warning thresholds, directional diagnostic vectors identify the root physical cause:
 *   - Sharpness drop with stable luminance -> Lens dust, contamination, or vibration defocus
 *   - Luminance drop with uniform distribution -> LED strobe fading or power degradation
 *   - Luminance increase / flare -> Strobe trigger timing drift or ambient light intrusion
 *
 * Safety Rule: The drift detector issues early warnings only; it never alters inspection thresholds
 * or overrides tool OK/NG verdicts.
 *
 * Threading: Multiple trackers can operate concurrently on different camera channels.
 */
#ifndef VS_DRIFT_H
#define VS_DRIFT_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_DRIFT_BUILD
#    define VS_DRIFT_API __declspec(dllexport)
#  else
#    define VS_DRIFT_API __declspec(dllimport)
#  endif
#else
#  define VS_DRIFT_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Drift diagnostic status codes */
enum vs_drift_status {
    VS_DRIFT_STATUS_OK = 0,             /* Baseline optical performance within healthy tolerances */
    VS_DRIFT_WARN_LENS_DIRT = 1,        /* Lens contamination, dust, grease, or defocus detected */
    VS_DRIFT_WARN_LIGHTING_DROP = 2,    /* LED strobe fading or illumination drop detected */
    VS_DRIFT_WARN_OVEREXPOSURE = 3,     /* Lighting flare, ambient leak, or overexposure detected */
    VS_DRIFT_WARN_OOD = 4               /* General out-of-distribution optical shift */
};

/* 10-dimensional optical feature vector */
typedef struct vs_drift_features {
    float mean_brightness;              /* Overall image luminance (0..255) */
    float contrast_std;                 /* Standard deviation of luminance (dynamic range) */
    float sharpness;                    /* High-frequency gradient energy / sharpness metric */
    float uniformity;                   /* Ratio of corner-to-center illumination (0..1) */
    float corner_sharpness[4];          /* Regional sharpness in TL, TR, BL, BR corners */
    float saturated_ratio;              /* Fraction of pixels >= 250 (glare/overexposure) */
    float dark_floor;                   /* 5th percentile black level */
} vs_drift_features;

typedef struct vs_drift_params {
    int32_t struct_size;                /* sizeof(vs_drift_params); initialized by vs_drift_default_params */
    int32_t window_size;                /* Moving window size in parts for trend filtering (default: 50, range: 10..500) */
    float   warn_distance_threshold;    /* Mahalanobis distance warning threshold (default: 5.0f for 10-dim feature space) */
    float   dirt_sharpness_sigma;       /* Standard deviations of sharpness drop to trigger lens dirt warning (default: 2.0f) */
    float   light_drop_sigma;           /* Standard deviations of luminance drop to trigger LED fading warning (default: 2.0f) */
    int32_t grid_step;                  /* Fast pixel subsampling stride for 20 MP frames (default: 4, range: 1..16) */
} vs_drift_params;

/* Opaque tracker handle */
typedef struct vs_drift_tracker vs_drift_tracker;

/* Initialize params with plant defaults */
VS_DRIFT_API void vs_drift_default_params(vs_drift_params* p);

/*
 * Extract optical feature vector from mono8 image.
 * Uses grid_step subsampling for sub-millisecond execution on 20 MP frames.
 * Returns 0 on success, negative error code on failure.
 */
VS_DRIFT_API int32_t vs_drift_extract_features(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t grid_step,
    vs_drift_features* out_feat);

/*
 * Create an optical drift tracker instance.
 */
VS_DRIFT_API vs_drift_tracker* vs_drift_create(const vs_drift_params* p);

/*
 * Destroy a drift tracker instance.
 */
VS_DRIFT_API void vs_drift_destroy(vs_drift_tracker* tracker);

/*
 * Reset tracker state and baseline model.
 */
VS_DRIFT_API void vs_drift_reset(vs_drift_tracker* tracker);

/*
 * Feed a teach sample to establish the golden optical baseline distribution.
 * Call 20..100 times during job teach or golden batch run.
 */
VS_DRIFT_API int32_t vs_drift_teach_sample(
    vs_drift_tracker* tracker,
    const vs_drift_features* feat);

/*
 * Finalize the baseline distribution model: computes mean vector μ, covariance Σ,
 * and inverts Σ to obtain the precision matrix.
 * Returns 0 on success, -1 if fewer than 5 teach samples were provided.
 */
VS_DRIFT_API int32_t vs_drift_finalize_teach(vs_drift_tracker* tracker);

/*
 * Update the tracker with an incoming production inspection sample.
 * Computes instantaneous Mahalanobis distance and updates moving window trend.
 * out_dist: receives the moving window smoothed Mahalanobis distance.
 * out_status: receives vs_drift_status code.
 * out_diagnostic: receives human-readable diagnostic message (can be NULL).
 * diag_cap: capacity of out_diagnostic buffer in bytes.
 * Returns 0 on success, negative error code on failure.
 */
VS_DRIFT_API int32_t vs_drift_update(
    vs_drift_tracker* tracker,
    const vs_drift_features* feat,
    float* out_dist,
    int32_t* out_status,
    char* out_diagnostic,
    int32_t diag_cap);

/*
 * End-to-end convenience method: extracts features from image and updates tracker.
 * Thread-safe with respect to other trackers.
 */
VS_DRIFT_API int32_t vs_drift_inspect_mono8(
    vs_drift_tracker* tracker,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    float* out_dist,
    int32_t* out_status,
    char* out_diagnostic,
    int32_t diag_cap);

#ifdef __cplusplus
}
#endif

#endif /* VS_DRIFT_H */
