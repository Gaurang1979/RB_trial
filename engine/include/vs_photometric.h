/*
 * vs_photometric.h - C API of the Computational Photometric Stereo Engine (Tool 0, Tool 8.4, Roadmap #1).
 * Keyence LumiTrax(TM) & Cognex SurfaceFX(TM) Alternative.
 *
 * Decouples physical 3D surface shape from 2D surface reflectance (albedo) using
 * 4 directional strobe illumination images (Top, Right, Bottom, Left).
 *
 * What It Buys:
 *   1. Albedo Image (Pure 2D Print):
 *      100% of specular glare highlights, hot spots, and 3D shadows are removed.
 *      Enables near-100% Data Matrix, QR, and OCR decode rates on glossy blister packs,
 *      shrink-wrapped bottles, and curved cans.
 *   2. Shape / Curvature Image (Pure 3D Topographic Relief):
 *      100% of printed ink, colors, barcodes, and decorative graphics are removed.
 *      Exposes debossed lot codes, expiry date punch stamps, scratches, dents,
 *      and seal pinholes with massive contrast on heavily printed packaging.
 *   3. Virtual Lighting Synthesis:
 *      Allows software-controlled virtual directional shading from any angle (0..360 deg)
 *      without physical lighting adjustment.
 *
 * Threading: Thread-safe for concurrent inspection.
 */
#ifndef VS_PHOTOMETRIC_H
#define VS_PHOTOMETRIC_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_PHOTOMETRIC_BUILD
#    define VS_PHOTO_API __declspec(dllexport)
#  else
#    define VS_PHOTO_API __declspec(dllimport)
#  endif
#else
#  define VS_PHOTO_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct vs_photo_light {
    float slant_deg;  /* Slant angle from surface normal (default: 45.0 deg) */
    float tilt_deg;   /* Azimuth angle in image plane (0 = Right, 90 = Top, 180 = Left, 270 = Bottom) */
} vs_photo_light;

typedef struct vs_photo_params {
    int32_t struct_size;          /* sizeof(vs_photo_params) */
    int32_t num_lights;           /* Typically 4 (Top, Right, Bottom, Left) */
    vs_photo_light lights[8];     /* Light orientation vectors */
    float   shape_contrast_gain;  /* Gain multiplier for curvature image (default: 140.0f) */
    float   albedo_gamma;         /* Gamma curve for albedo output (default: 1.0f) */
} vs_photo_params;

/* Opaque photometric pipeline handle */
typedef struct vs_photo_pipeline vs_photo_pipeline;

/* Initialize parameters with standard 4-quadrant ring light defaults */
VS_PHOTO_API void vs_photo_default_params(vs_photo_params* p);

/*
 * Create photometric stereo pipeline instance.
 */
VS_PHOTO_API vs_photo_pipeline* vs_photo_create(const vs_photo_params* p);

/*
 * Destroy pipeline instance and free precomputed pseudo-inverse matrices.
 */
VS_PHOTO_API void vs_photo_destroy(vs_photo_pipeline* pipe);

/*
 * Compute decoupled 2D Albedo (print with glare removed) and 3D Shape Curvature (relief with print removed).
 * input_images: array of num_lights pointers to mono8 images of size width x height (stride)
 * albedo_out: receives 8-bit albedo image (can be NULL)
 * shape_out: receives 8-bit mean curvature shape image (can be NULL)
 * normal_map_out: optional float array of size width x height x 3 storing [nx, ny, nz] (can be NULL)
 * Returns 0 on success, negative error code on failure.
 */
VS_PHOTO_API int32_t vs_photo_compute(
    const vs_photo_pipeline* pipe,
    const uint8_t* const* input_images,
    int32_t width,
    int32_t height,
    int32_t stride,
    uint8_t* albedo_out,
    int32_t albedo_stride,
    uint8_t* shape_out,
    int32_t shape_stride,
    float* normal_map_out,
    int32_t normal_stride);

/*
 * Synthesize virtual directional illumination from arbitrary azimuth and slant.
 * virtual_tilt_deg: 0..360 degrees (0 = right, 90 = top, 180 = left, 270 = bottom)
 * virtual_slant_deg: 0..90 degrees (45 = standard glancing light)
 */
VS_PHOTO_API int32_t vs_photo_synthesize_lighting(
    const uint8_t* albedo,
    int32_t albedo_stride,
    const float* normal_map,
    int32_t normal_stride,
    int32_t width,
    int32_t height,
    float virtual_tilt_deg,
    float virtual_slant_deg,
    uint8_t* shaded_out,
    int32_t shaded_stride);

#ifdef __cplusplus
}
#endif

#endif /* VS_PHOTOMETRIC_H */
