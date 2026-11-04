/*
 * vs_locate.h - C API of the Locate tool ("Find the part"): finds a taught pattern in each image and
 * reports where it moved and how far it turned, so the job's other tools can follow the part.
 *
 * Teach once on a good image (vs_loc_teach: a region holding something distinctive, such as the
 * barcode or a printed patch), or load a stored pattern (vs_loc_set_pattern). Then call vs_loc_run for
 * every image. A point (x, y) of the teach image is at
 *     x' = cx + cos(a) (x - ox) + sin(a) (y - oy)
 *     y' = cy - sin(a) (x - ox) + cos(a) (y - oy)
 * in the new image, where (ox, oy) is the pattern centre at teach time, (cx, cy) the centre found and a
 * the angle found (degrees, counter-clockwise on the screen, as cv::getRotationMatrix2D).
 *
 * Method: normalised grey-level correlation over an image pyramid - a coarse search over the allowed
 * turn at about 64 px pattern size, then a fine search around the best place at a few times that
 * resolution, with a parabola through the peak for sub-pixel position. Grey-level correlation is
 * insensitive to brightness and contrast changes of the whole pattern. Last, the pattern's edge points
 * (taught with their direction) are fitted to the image's edges by least squares, robustly weighted,
 * for a pose good to a few hundredths of a pixel and degree (tests: engine/tests/locate_tests.cpp on
 * the geometry truth set).
 *
 * Threading: one handle must not be used from two threads at once.
 */
#ifndef VS_LOCATE_H
#define VS_LOCATE_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_LOCATE_BUILD
#    define VS_LOC_API __declspec(dllexport)
#  else
#    define VS_LOC_API __declspec(dllimport)
#  endif
#else
#  define VS_LOC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct vs_loc vs_loc;

typedef struct vs_loc_params {
    int32_t struct_size;      /* sizeof(vs_loc_params); set by vs_loc_default_params */
    int32_t search_x, search_y, search_w, search_h; /* where to look; search_w = 0 means the whole image */
    float   max_angle_deg;    /* turn searched either way (default 15, up to 180) */
    float   min_score;        /* 0..1: lowest correlation accepted as found (default 0.6) */
    int32_t time_budget_ms;   /* default 50. The search stops at 10 times this (at least 500 ms): vs_loc_run
                                 returns -1, not found (vs_loc_last_error) */
} vs_loc_params;

typedef struct vs_loc_result {
    int32_t found;            /* 1 = score >= min_score */
    float   score;            /* 0..1 correlation at the best place */
    float   cx, cy;           /* pattern centre in the image, pixels */
    float   angle_deg;        /* turn against the teach image, counter-clockwise */
    float   dx, dy;           /* cx, cy minus the teach centre */
    float   time_ms;
} vs_loc_result;

VS_LOC_API void        vs_loc_default_params(vs_loc_params* p);
VS_LOC_API vs_loc*     vs_loc_create(const vs_loc_params* p);
VS_LOC_API void        vs_loc_destroy(vs_loc* t);
VS_LOC_API int32_t     vs_loc_set_params(vs_loc* t, const vs_loc_params* p);

/* Teach: the pattern is the region (x, y, w, h) of this 8-bit grey image. Returns 0 on success. */
VS_LOC_API int32_t     vs_loc_teach(vs_loc* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                    int32_t x, int32_t y, int32_t w, int32_t h);
/* A stored pattern (8-bit grey, w x h) whose centre was at (origin_x, origin_y) in the teach image. */
VS_LOC_API int32_t     vs_loc_set_pattern(vs_loc* t, const uint8_t* pattern, int32_t w, int32_t h, int32_t stride,
                                          float origin_x, float origin_y);
/* The taught pattern (to store with the job): copies up to cap bytes, returns w * h (or < 0). */
VS_LOC_API int32_t     vs_loc_get_pattern(const vs_loc* t, uint8_t* buf, int32_t cap, int32_t* w, int32_t* h,
                                          float* origin_x, float* origin_y);

VS_LOC_API int32_t     vs_loc_run(vs_loc* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride);
VS_LOC_API void        vs_loc_get_result(const vs_loc* t, vs_loc_result* out);
VS_LOC_API const char* vs_loc_last_error(const vs_loc* t);

#ifdef __cplusplus
}
#endif

#endif
