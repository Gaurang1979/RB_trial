/*
 * vs_inpaint.h - C API of the Specular Highlight Glare Inpainter Tool (AG-07):
 * Detects specular highlight glare (pixel saturation from plastic shrink-wrap reflections,
 * glossy carton laminates, hologram foils, or bright LEDs) and reconstructs obliterated
 * Data Matrix clock tracks, QR finder patterns, 1D barcode bars, and OCR characters using
 * gradient-preserving anisotropic diffusion and Fast Marching inpainting.
 *
 * Theory:
 * Specular reflections cause CMOS sensor saturation where all pixels in the reflection core clip
 * to 255 (or >= glare_threshold). This obliterates underlying contrast, causing decode failures
 * (CR_MISS_NO_FINDER, CR_MISS_GLARE) or grid-fitting failures on Data Matrix and QR codes.
 *
 * Rather than naive contrast stretching or flat blurring (which creates invalid smudges),
 * vs_inpaint computes the boundary normals N(p) and intensity gradients ∇I(q) along the periphery
 * of the glare spot. Using Telea's Fast Marching Method or Navier-Stokes isophote transport:
 *     I(p) = Σ w(p, q) * [I(q) + ∇I(q) · (p - q)] / Σ w(p, q)
 * The first-order Taylor expansion [I(q) + ∇I(q) · (p - q)] projects the orientation and edge
 * continuity of entering bars and module borders straight across the saturated hole.
 *
 * Threading: All functions are stateless and re-entrant. Multiple threads can inpaint separate
 * frames or ROIs concurrently.
 */
#ifndef VS_INPAINT_H
#define VS_INPAINT_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_INPAINT_BUILD
#    define VS_INP_API __declspec(dllexport)
#  else
#    define VS_INP_API __declspec(dllimport)
#  endif
#else
#  define VS_INP_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Inpainting method selection */
enum vs_inpaint_method {
    VS_INPAINT_TELEA = 0,          /* Fast Marching gradient-preserving inpainting (default, best for barcodes) */
    VS_INPAINT_NAVIER_STOKES = 1,   /* Isophote-driven fluid flow diffusion (smooth gradients) */
    VS_INPAINT_HARMONIC = 2        /* Multi-grid Laplacian harmonic relaxation (ultra-fast for small spots) */
};

typedef struct vs_inpaint_params {
    int32_t struct_size;          /* sizeof(vs_inpaint_params); initialized by vs_inpaint_default_params */
    uint8_t glare_threshold;      /* Pixel level >= threshold considered specular glare core (default: 252) */
    int32_t halo_radius;          /* Morphological dilation radius in pixels around glare core to include
                                     the saturated bloom transition zone (default: 2, range: 0..10) */
    int32_t inpaint_radius;       /* Neighborhood radius in pixels for boundary gradient sampling (default: 4, range: 1..15) */
    int32_t method;               /* Inpainting algorithm: VS_INPAINT_TELEA, VS_INPAINT_NAVIER_STOKES, or VS_INPAINT_HARMONIC */
    float   max_glare_pct;        /* Max allowable glare area in percent (0..100). If glare exceeds this,
                                     the image is deemed unsalvageable and left unmodified (default: 35.0f) */
    int32_t iterations;           /* Iteration count for Navier-Stokes or harmonic relaxation (default: 25) */
} vs_inpaint_params;

/* Initialize params with industrial defaults */
VS_INP_API void vs_inpaint_default_params(vs_inpaint_params* p);

/*
 * Detect specular highlight glare mask.
 * Generates an 8-bit binary mask (0 = valid pixel, 255 = glare to inpaint) by thresholding src >= threshold
 * and dilating by halo_radius.
 * Returns the number of glare pixels detected (0 if no glare).
 */
VS_INP_API int32_t vs_inpaint_detect_glare(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t src_stride,
    uint8_t glare_threshold,
    int32_t halo_radius,
    uint8_t* mask_out,
    int32_t mask_stride,
    int32_t* glare_pixel_count);

/*
 * Apply inpainting to mono8 image with an explicit binary mask.
 * src: input 8-bit grayscale image
 * mask: 8-bit binary mask (pixels > 0 are inpainted; 0 are kept unchanged)
 * dst: output 8-bit grayscale image (can be identical to src for in-place inpainting)
 * p: inpainting parameters (or NULL for defaults)
 * Returns 0 on success, negative error code on invalid parameters or failure.
 */
VS_INP_API int32_t vs_inpaint_apply_mono8(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t src_stride,
    const uint8_t* mask,
    int32_t mask_stride,
    uint8_t* dst,
    int32_t dst_stride,
    const vs_inpaint_params* p);

/*
 * Automatic glare detection and inpainting on a full mono8 image.
 * If no glare is detected (glare_pixel_count == 0), dst is populated with a direct copy of src.
 * If glare is detected and within max_glare_pct, glare regions are inpainted.
 * out_glare_count: receives the total number of inpainted pixels (can be NULL).
 * Returns 0 on success, negative error code on failure.
 */
VS_INP_API int32_t vs_inpaint_auto_mono8(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t src_stride,
    uint8_t* dst,
    int32_t dst_stride,
    const vs_inpaint_params* p,
    int32_t* out_glare_count);

/*
 * Automatic glare detection and inpainting restricted to a Region of Interest (ROI).
 * Useful for localized candidate code boxes (e.g. from the AI locator or edge candidate detector)
 * to run in < 1 ms without touching the rest of a 20 MP frame.
 * roi_x, roi_y, roi_w, roi_h: crop rectangle within src.
 * dst must have dimensions roi_w x roi_h with stride dst_stride.
 * Returns 0 on success, negative error code on failure.
 */
VS_INP_API int32_t vs_inpaint_roi_mono8(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t src_stride,
    int32_t roi_x,
    int32_t roi_y,
    int32_t roi_w,
    int32_t roi_h,
    uint8_t* dst,
    int32_t dst_stride,
    const vs_inpaint_params* p,
    int32_t* out_glare_count);

#ifdef __cplusplus
}
#endif

#endif /* VS_INPAINT_H */
