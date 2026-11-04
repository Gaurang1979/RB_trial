/*
 * vs_polar.h - C API of the Polarimetric Reflection Decoupler Engine (Tool D, Roadmap Priority 7).
 *
 * Designed for micro-polarizer array sensors (Sony Polarsens IMX250MZR / IMX264MZR)
 * with on-chip linear wire grids at 0, 45, 90, 135 degrees.
 *
 * Key Capabilities:
 *   1. 100% Specular Glare Removal on Blister Packs, Glossy Bottles & Shrink Sleeves:
 *      Separates unpolarized diffuse reflectance (printed text, barcodes) from polarized specular glare.
 *   2. Photoelastic Stress Birefringence Inspection:
 *      Extracts Angle of Linear Polarization (AoLP) and Degree of Linear Polarization (DoLP)
 *      to reveal residual mechanical molding stress in transparent PET bottle preforms and glass vials.
 *   3. Transparent Film & Clear Tape Seam Detection:
 *      Clear tape, protective liners, and transparent adhesive seams light up in DoLP contrast.
 *
 * Thread Safety: Thread-safe, reentrant, zero heap allocations on the inspection loop.
 * Line Safety Rule: Fail-safe to NG on detected stress concentration or unreadable barcode under glare.
 */
#ifndef VS_POLAR_H
#define VS_POLAR_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_POLAR_BUILD
#    define VS_POLAR_API __declspec(dllexport)
#  else
#    define VS_POLAR_API __declspec(dllimport)
#  endif
#else
#  define VS_POLAR_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Polarizer mosaic arrangement in raw 2x2 sensor pixels */
typedef enum vs_polar_mosaic {
    VS_POLAR_MOSAIC_SONY_POLARSENS = 0 /* TopLeft=90, TopRight=45, BottomLeft=135, BottomRight=0 */
} vs_polar_mosaic;

typedef struct vs_polar_params {
    int32_t struct_size;          /* sizeof(vs_polar_params) */
    int32_t mosaic_layout;        /* vs_polar_mosaic enum (default: 0) */
    float   glare_suppress_alpha; /* Aggressiveness of glare suppression (0.0..1.5, default: 1.0f) */
    float   specular_gain;        /* Gain multiplier for specular saliency map (default: 2.0f) */
    float   dolp_defect_thresh;   /* DoLP threshold to flag scratch/specular defect (default: 0.45f) */
    float   stress_gradient_thresh;/* AoLP angular gradient threshold for stress cracks (default: 35.0f) */
} vs_polar_params;

typedef struct vs_polar_stats {
    int32_t struct_size;          /* sizeof(vs_polar_stats) */
    float   mean_dolp;            /* Average degree of linear polarization across image (0..1) */
    float   max_dolp;             /* Maximum degree of linear polarization observed */
    int32_t glare_pixel_count;    /* Number of pixels where glare was successfully eliminated */
    float   max_stress_gradient;  /* Peak mechanical stress gradient detected */
    int32_t is_stress_defective;  /* 1 = excessive residual molding stress / brittle point detected */
} vs_polar_stats;

/* Initialize parameters with standard industrial defaults */
VS_POLAR_API void vs_polar_default_params(vs_polar_params* p);

/*
 * Process from 4 pre-demuxed linear polarization channels (I0, I45, I90, I135).
 * All input images must be mono8 of size width x height.
 * diffuse_out: receives 8-bit diffuse image (glare completely removed) (can be NULL)
 * specular_out: receives 8-bit specular image (scratches / glare highlighted) (can be NULL)
 * dolp_out: receives 8-bit DoLP map: DoLP * 255.0 (can be NULL)
 * aolp_out: receives 8-bit AoLP map: AoLP_deg / 180.0 * 255.0 (can be NULL)
 * stats_out: receives polarization statistics
 * Returns 0 on success, negative error code on failure.
 */
VS_POLAR_API int32_t vs_polar_process_channels(
    const uint8_t* i0,
    const uint8_t* i45,
    const uint8_t* i90,
    const uint8_t* i135,
    int32_t width,
    int32_t height,
    int32_t stride,
    const vs_polar_params* params,
    uint8_t* diffuse_out,
    int32_t diffuse_stride,
    uint8_t* specular_out,
    int32_t specular_stride,
    uint8_t* dolp_out,
    int32_t dolp_stride,
    uint8_t* aolp_out,
    int32_t aolp_stride,
    vs_polar_stats* stats_out);

/*
 * Process directly from raw micro-polarizer mosaic camera frame (e.g. Sony Polarsens 2x2 pattern).
 * The output images will have half resolution (width/2, height/2).
 */
VS_POLAR_API int32_t vs_polar_process_mosaic(
    const uint8_t* raw_mosaic,
    int32_t width,
    int32_t height,
    int32_t stride,
    const vs_polar_params* params,
    uint8_t* diffuse_out,
    int32_t diffuse_stride,
    uint8_t* specular_out,
    int32_t specular_stride,
    uint8_t* dolp_out,
    int32_t dolp_stride,
    uint8_t* aolp_out,
    int32_t aolp_stride,
    vs_polar_stats* stats_out);

#ifdef __cplusplus
}
#endif

#endif /* VS_POLAR_H */
