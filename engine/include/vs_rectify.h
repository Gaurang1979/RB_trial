/*
 * vs_rectify.h - C API of the Conveyor Rolling-Shutter Rectification Tool (AG-06):
 * Compensates for rolling-shutter geometric distortion (shear, compression, elongation)
 * caused by relative conveyor motion during row-by-row exposure on CMOS sensors (e.g. Sony IMX183).
 *
 * Theory:
 * A rolling-shutter sensor exposes row by row with inter-row readout period t_row (microseconds).
 * When an inspected object moves across the camera with velocity v (mm/s), each row y experiences
 * an exposure time offset relative to reference row y_ref:
 *     Δt(y) = (y - y_ref) * t_row
 * The corresponding lateral and vertical physical shifts are:
 *     Δx(y) = v_x * Δt(y) / mm_per_px = k_x * (y - y_ref)
 *     Δy(y) = v_y * Δt(y) / mm_per_px = k_y * (y - y_ref)
 *
 * For standard horizontal conveyors (motion along x, angle = 0°):
 *     k_x = (v_line_mm_s * 1e-6 * row_time_us) / mm_per_px
 *     k_y = 0
 * Skewed vertical lines (e.g., QR finder patterns becoming parallelograms) are unwarped back to
 * true orthogonality via 1D sub-pixel row-shift interpolation in < 5 ms on CPU for 20 MP frames.
 *
 * Threading: All functions are stateless and re-entrant. Multiple threads may rectify different
 * images or ROIs concurrently.
 */
#ifndef VS_RECTIFY_H
#define VS_RECTIFY_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_RECTIFY_BUILD
#    define VS_RECT_API __declspec(dllexport)
#  else
#    define VS_RECT_API __declspec(dllimport)
#  endif
#else
#  define VS_RECT_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct vs_rect_params {
    int32_t struct_size;          /* sizeof(vs_rect_params); initialized by vs_rect_default_params */
    float   line_speed_mm_s;      /* Conveyor speed in mm/s (from PLC encoder). 0 = stationary */
    float   mm_per_px;            /* Optical scale in mm/pixel (from calibration). e.g. 0.08 */
    float   row_time_us;          /* Sensor row readout time in microseconds. IMX183 = ~22.5 us */
    float   motion_angle_deg;     /* Motion direction in image plane: 0 = +X (left-to-right),
                                     90 = +Y (top-to-bottom), 180 = -X, 270 = -Y */
    float   ref_y;                /* Reference row where shift = 0. Default: height / 2.0f */
    uint8_t fill_value;           /* Background fill color for boundary pixels. Default: 0 */
    uint8_t interpolation;        /* 0 = Nearest neighbor, 1 = Bilinear (recommended) */
    uint8_t pad[2];
} vs_rect_params;

typedef struct vs_rect_stats {
    float max_shift_px;           /* Maximum pixel displacement between top and bottom rows */
    float shear_angle_deg;        /* Induced tilt angle in degrees */
    float time_ms;                /* Execution time of last rectification pass in milliseconds */
} vs_rect_stats;

/* Initialize default parameters (IMX183 defaults, 0 line speed) */
VS_RECT_API void vs_rect_default_params(vs_rect_params* p);

/* Calculate dimensionless shear coefficients kx and ky from physical parameters.
 * Returns 0 on success, negative on invalid arguments (e.g. mm_per_px <= 0). */
VS_RECT_API int32_t vs_rect_calculate_shear(const vs_rect_params* p, float* kx, float* ky);

/* Rectify an 8-bit monochrome image buffer.
 * If dst == src, operation executes in-place (allocation-free).
 * If dst != src, dst buffer must have stride >= width and capacity >= stride * height.
 * Returns 0 on success, negative error code on failure. */
VS_RECT_API int32_t vs_rect_apply_mono8(const vs_rect_params* p,
                                       const uint8_t* src,
                                       uint8_t* dst,
                                       int32_t width,
                                       int32_t height,
                                       int32_t stride,
                                       vs_rect_stats* stats);

/* Rectify an ROI sub-region within an image.
 * Rectifies only the region [roi_x, roi_y, roi_w, roi_h] directly into dst_roi.
 * Enables sub-millisecond execution for local inspection tools. */
VS_RECT_API int32_t vs_rect_apply_roi_mono8(const vs_rect_params* p,
                                           const uint8_t* src,
                                           int32_t src_width,
                                           int32_t src_height,
                                           int32_t src_stride,
                                           int32_t roi_x,
                                           int32_t roi_y,
                                           int32_t roi_w,
                                           int32_t roi_h,
                                           uint8_t* dst_roi,
                                           int32_t dst_stride,
                                           vs_rect_stats* stats);

/* Transform a single 2D point from distorted image space to rectified world space */
VS_RECT_API int32_t vs_rect_distorted_to_rectified(const vs_rect_params* p,
                                                   float dist_x,
                                                   float dist_y,
                                                   float* rect_x,
                                                   float* rect_y);

/* Transform a single 2D point from rectified space back to distorted image space */
VS_RECT_API int32_t vs_rect_rectified_to_distorted(const vs_rect_params* p,
                                                   float rect_x,
                                                   float rect_y,
                                                   float* dist_x,
                                                   float* dist_y);

#ifdef __cplusplus
}
#endif

#endif /* VS_RECTIFY_H */
