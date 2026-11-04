#ifndef VS_METAMORPHIC_H
#define VS_METAMORPHIC_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(__CYGWIN__)
  #ifdef VS_METAMORPHIC_EXPORTS
    #define VS_METAMORPHIC_API __declspec(dllexport)
  #else
    #define VS_METAMORPHIC_API __declspec(dllimport)
  #endif
#else
  #define VS_METAMORPHIC_API __attribute__((visibility("default")))
#endif

#define VS_METAMORPHIC_OK 0
#define VS_METAMORPHIC_ERR_NULL_PTR -1
#define VS_METAMORPHIC_ERR_INVALID_DIM -2
#define VS_METAMORPHIC_ERR_VIOLATION -3

/**
 * Perturbation parameters applied to an input image.
 */
typedef struct vs_metamorphic_transform {
    float rot_angle_deg;       /**< Rotation angle in degrees (e.g. -3.0 .. +3.0) */
    float scale;               /**< Scale multiplier (e.g. 0.95 .. 1.05) */
    float shift_x;             /**< Translation offset X in pixels */
    float shift_y;             /**< Translation offset Y in pixels */
    float gain_gamma;          /**< Photometric contrast multiplier (e.g. 0.90 .. 1.10) */
    float bias_beta;           /**< Photometric brightness offset (e.g. -15 .. +15) */
    float noise_sigma;         /**< Additive Gaussian sensor noise std dev (e.g. 0.0 .. 6.0) */
    int quiet_zone_pad_px;     /**< Additional uniform background quiet zone padding (e.g. 0 .. 16) */
} vs_metamorphic_transform;

/**
 * Fuzzing sweep configuration across metamorphic relations.
 */
typedef struct vs_metamorphic_config {
    int struct_size;                    /**< sizeof(vs_metamorphic_config) */
    float max_rotation_deg;             /**< Max rotation angle sweep (+/- degrees, default: 3.0f) */
    int rotation_steps;                 /**< Number of rotation evaluation steps (default: 7) */
    float max_illum_gain_delta;         /**< Max photometric gain variation (+/- delta, default: 0.10f = 10%) */
    int illum_steps;                    /**< Number of illumination evaluation steps (default: 5) */
    float max_scale_delta;              /**< Max scale factor variation (+/- delta, default: 0.05f = 5%) */
    int scale_steps;                    /**< Number of scale evaluation steps (default: 5) */
    float max_noise_sigma;              /**< Max Gaussian sensor noise std dev (default: 5.0f) */
    int noise_steps;                    /**< Number of noise evaluation steps (default: 4) */
    int max_quiet_zone_px;              /**< Max quiet-zone padding expansion (default: 8) */
    float max_allowed_jitter_px;        /**< Maximum acceptable sub-pixel coordinate drift (default: 0.75f) */
    float min_acceptable_confidence;    /**< Floor confidence score below which is violation (default: 0.60f) */
} vs_metamorphic_config;

/**
 * Result metrics from metamorphic fuzzing sweep.
 */
typedef struct vs_metamorphic_report {
    int struct_size;                    /**< sizeof(vs_metamorphic_report) */
    int total_evaluations;              /**< Total synthetic variants evaluated */
    int verdict_flips;                  /**< Number of times OK/NG verdict flipped under perturbation */
    float max_positional_jitter_px;     /**< Maximum observed coordinate drift (sub-pixels) */
    float mean_positional_jitter_px;    /**< Mean observed coordinate drift across all steps */
    float min_observed_confidence;      /**< Minimum confidence score seen during fuzzing */
    float max_observed_confidence;      /**< Maximum confidence score seen during fuzzing */
    int is_robust;                      /**< 1 if 0 verdict flips and jitter <= max_allowed_jitter_px */
    char failure_reason[128];           /**< Description of first observed metamorphic violation, if any */
} vs_metamorphic_report;

/**
 * Vision evaluation callback function invoked for each metamorphic perturbation.
 *
 * @param img Perturbed image buffer.
 * @param w Width of perturbed image.
 * @param h Height of perturbed image.
 * @param stride Stride in bytes.
 * @param user_data Caller-provided context or model pointer.
 * @param confidence Output confidence score [0.0..1.0].
 * @param out_x Output detected feature/center X coordinate.
 * @param out_y Output detected feature/center Y coordinate.
 * @param is_ok Output verdict (1 = OK/Pass, 0 = NG/Fail).
 * @return 0 on success, negative error code otherwise.
 */
typedef int (*vs_vision_eval_fn)(
    const uint8_t* img,
    int w,
    int h,
    int stride,
    void* user_data,
    float* confidence,
    float* out_x,
    float* out_y,
    int* is_ok);

/**
 * Populate default configuration for industrial metamorphic fuzzing.
 */
VS_METAMORPHIC_API void vs_metamorphic_default_config(vs_metamorphic_config* config);

/**
 * Synthesize a transformed variant of the input image according to metamorphic parameters.
 * Supports rotation, scaling, translation, photometric gain/bias, Gaussian noise, and quiet-zone expansion.
 */
VS_METAMORPHIC_API int vs_metamorphic_apply_transform(
    const uint8_t* src,
    int src_w,
    int src_h,
    int src_stride,
    const vs_metamorphic_transform* t,
    uint8_t* dst,
    int dst_w,
    int dst_h,
    int dst_stride);

/**
 * Run comprehensive metamorphic robustness fuzzing sweep on a target vision tool/model.
 * Asserts rotational equivariance, photometric monotonicity, scale invariance, noise tolerance,
 * and quiet-zone monotonicity.
 *
 * @param baseline_img Original verified golden/production image.
 * @param w Width of image.
 * @param h Height of image.
 * @param stride Stride in bytes.
 * @param config Fuzzing configuration (or NULL for defaults).
 * @param eval_fn Vision algorithm callback to evaluate.
 * @param user_data User context passed to eval_fn.
 * @param report Output metamorphic stability report.
 * @return 0 on success, negative error code on invalid parameters or failure.
 */
VS_METAMORPHIC_API int vs_metamorphic_run_fuzzing_suite(
    const uint8_t* baseline_img,
    int w,
    int h,
    int stride,
    const vs_metamorphic_config* config,
    vs_vision_eval_fn eval_fn,
    void* user_data,
    vs_metamorphic_report* report);

#ifdef __cplusplus
}
#endif

#endif /* VS_METAMORPHIC_H */
