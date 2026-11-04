/*
 * vs_wavelet.h - C API of the Wavelet Homomorphic Scratch & Micro-Defect Filter Engine (Tool 8, Roadmap Priority 3).
 *
 * Eliminates periodic background textures (brushed metal, knurled caps, foil knurling, machined grain)
 * to expose subtle hairline scratches, cracks, and pinholes.
 *
 * Key Capabilities:
 *   1. Homomorphic Illumination Invariance:
 *      Separates multiplicative lighting gradients from high-frequency surface reflectance via log transform.
 *   2. Stationary Wavelet Decomposition (SWT / à trous):
 *      Shift-invariant multi-scale decomposition eliminating boundary ringing and decimation aliasing.
 *   3. Directional Texture Suppression:
 *      Attenuates directional machining lines / knurling grid via adaptive soft threshold shrinkage (VisuShrink/BayesShrink).
 *   4. Zero Dynamic Heap Allocation:
 *      Deterministic, real-time CPU inspection under 10 ms for 1024x1024 frames.
 *
 * Line Safety Rule: Fail-safe to NG on detected scratch or anomalous defect cluster.
 */
#ifndef VS_WAVELET_H
#define VS_WAVELET_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_WAVELET_BUILD
#    define VS_WAVELET_API __declspec(dllexport)
#  else
#    define VS_WAVELET_API __declspec(dllimport)
#  endif
#else
#  define VS_WAVELET_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Background texture orientation mode */
typedef enum vs_texture_orientation {
    VS_TEXTURE_AUTO         = 0,  /* Automatically detects dominant texture angle from sub-bands */
    VS_TEXTURE_HORIZONTAL   = 1,  /* Brushed metal along horizontal (X) axis */
    VS_TEXTURE_VERTICAL     = 2,  /* Brushed metal along vertical (Y) axis */
    VS_TEXTURE_DIAGONAL     = 3,  /* 45-degree cross-hatch texture */
    VS_TEXTURE_KNURLED_GRID = 4   /* Knurled packaging seal / cap grid (horizontal + vertical) */
} vs_texture_orientation;

typedef struct vs_wavelet_params {
    int32_t struct_size;            /* sizeof(vs_wavelet_params) */
    int32_t levels;                 /* Decomposition levels (1..3, default: 2) */
    int32_t texture_mode;           /* vs_texture_orientation enum (default: VS_TEXTURE_AUTO) */
    float   suppression_strength;   /* Texture shrinkage aggressiveness (0.0f..1.0f, default: 0.85f) */
    float   scratch_gain;           /* Output contrast amplification for defects (default: 3.0f) */
    int32_t homomorphic_enable;     /* 1 = enable log transform, 0 = linear (default: 1) */
    float   defect_contrast_thresh; /* Gray-level deviation from neutral 128 to flag defect (default: 35.0f) */
    int32_t min_defect_area_px;     /* Minimum connected defect pixels to trigger NG (default: 5) */
} vs_wavelet_params;

typedef struct vs_wavelet_stats {
    int32_t struct_size;            /* sizeof(vs_wavelet_stats) */
    int32_t defect_pixel_count;     /* Count of anomalous defect pixels exceeding threshold */
    float   max_scratch_contrast;   /* Peak absolute contrast deviation from neutral background */
    float   texture_energy_suppressed; /* Percentage of periodic texture energy eliminated (0..100%) */
    int32_t detected_orientation;   /* Detected texture mode if AUTO was selected */
    int32_t is_defective;           /* 0 = OK (clean surface), 1 = NG (scratch / pinhole detected) */
} vs_wavelet_stats;

/* Opaque filter handle */
typedef struct vs_wavelet_filter vs_wavelet_filter;

/* Initialize parameters with standard industrial defaults */
VS_WAVELET_API void vs_wavelet_default_params(vs_wavelet_params* p);

/*
 * Create wavelet filter instance.
 */
VS_WAVELET_API vs_wavelet_filter* vs_wavelet_create(const vs_wavelet_params* p);

/*
 * Destroy wavelet filter instance.
 */
VS_WAVELET_API void vs_wavelet_destroy(vs_wavelet_filter* filter);

/*
 * Process single-channel 8-bit image to suppress periodic background and isolate defects.
 * src: input image pointer
 * width, height, stride: image dimensions
 * residual_out: optional output 8-bit image where background is flat 128 and defects are high-contrast (can be NULL)
 * residual_stride: stride for residual_out
 * mask_out: optional binary mask (255 = defect, 0 = OK background) (can be NULL)
 * mask_stride: stride for mask_out
 * stats_out: receives defect statistics and NG verdict
 *
 * Returns 0 on success, negative error code on failure.
 */
VS_WAVELET_API int32_t vs_wavelet_process(
    const vs_wavelet_filter* filter,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    uint8_t* residual_out,
    int32_t residual_stride,
    uint8_t* mask_out,
    int32_t mask_stride,
    vs_wavelet_stats* stats_out);

#ifdef __cplusplus
}
#endif

#endif /* VS_WAVELET_H */
