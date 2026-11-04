/*
 * vs_measure.h - C API of the Measure tool ("Check size"): calipers that find edges to a fraction of a
 * pixel, a straight edge fitted from calipers along it, a hole or boss (circle or ellipse) fitted from
 * calipers round it, a corner where two edges meet, widths along a gap, the pitch of a row of pins and
 * the edges along a line (docs/vision-system/TOOL-SPECIFICATIONS.md, tool 2: 2.1-2.5, 2.7-2.9; the
 * geometry of 2.6 between found features is closed-form and lives with the caller).
 *
 * Geometry. Angles are degrees counter-clockwise on the screen, as vs_locate.h, so a box placed on the
 * teach image follows the part by turning its angle with the Locate result. A box at angle a runs along
 *     u = (cos a, -sin a)          (a = 0: left to right)
 * and its height lies along v = (sin a, cos a) (a = 0: top to bottom). Positions along a box count from
 * its start, centre - u * length / 2.
 *
 * Calibration (optional, vs_meas_calib, the same model as cr_lens in codereader.h): measuring is done on
 * undistorted points ("measure space", pixels with the lens distortion taken out; image pixels when there
 * is no lens model), so a lens does not bend a straight edge or bias a distance. Sizes in mm are measure-
 * space lengths times mm_per_px. Points meant for drawing (x, y) stay in image pixels.
 *
 * Sub-pixel edges: the box is averaged across its height into a profile (bilinear samples, one per
 * pixel), smoothed and differentiated with a Gaussian derivative, and each peak is placed at the centroid
 * of the derivative round it (exact for an area-sampled step). The profile is sampled a few pixels past
 * each end of the box (2 * ceil(1.5 * filter_half_width) + 2, at least 6), so an edge at the box's end is
 * seen whole; that margin must lie inside the image too.
 *
 * Structs carry struct_size so they can grow without breaking a caller built against an older header.
 * Params: set by the vs_meas_default_* functions. Results: the caller sets struct_size to sizeof(the
 * result) before the call (smaller than this library's result is refused, -1); on return it holds the
 * size the library filled, so a caller built against a newer, larger header can tell which fields are set.
 *
 * All functions are stateless and may run on several threads at once. They return 0 (or a count) on
 * success, a negative value on bad arguments: -1 bad argument, -2 the box leaves the image.
 */
#ifndef VS_MEASURE_H
#define VS_MEASURE_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_MEASURE_BUILD
#    define VS_MEAS_API __declspec(dllexport)
#  else
#    define VS_MEAS_API __declspec(dllimport)
#  endif
#else
#  define VS_MEAS_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum vs_meas_polarity { VS_MEAS_ANY = 0, VS_MEAS_DARK_TO_LIGHT = 1, VS_MEAS_LIGHT_TO_DARK = 2 };
enum vs_meas_mode { VS_MEAS_SINGLE = 0, VS_MEAS_PAIR = 1 };
enum vs_meas_score { VS_MEAS_BY_CONTRAST = 0, VS_MEAS_BY_POSITION = 1, VS_MEAS_BY_WIDTH = 2 };

/* lens and scale; all zero = image pixels, no mm */
typedef struct vs_meas_calib {
    float fx, fy, cx, cy, k1, k2, p1, p2, k3; /* fx 0 = no lens model */
    float mm_per_px;                          /* 0 = no mm */
} vs_meas_calib;

/* a rotated box: centre, length along u, height along v, angle (see above) */
typedef struct vs_meas_box {
    float cx, cy, length, height, angle_deg;
} vs_meas_box;

typedef struct vs_meas_caliper_params {
    int32_t struct_size;       /* sizeof(vs_meas_caliper_params); set by vs_meas_default_caliper */
    int32_t mode;              /* vs_meas_mode: one edge, or a pair (the first edge's polarity, then the opposite) */
    int32_t polarity;          /* vs_meas_polarity of the (first) edge, along u */
    float   threshold;         /* lowest edge contrast in grey levels; 0 = auto (from the profile's noise, at least 5) */
    float   filter_half_width; /* px, 1..20 (default 2): the smoothing; larger for noisy or soft edges */
    float   expected_width;    /* pair mode, measure-space px: the pair nearest it wins with VS_MEAS_BY_WIDTH; 0 = none */
    int32_t score_by;          /* vs_meas_score (default by contrast) */
} vs_meas_caliper_params;

/* One edge. contrast: grey levels, signed (+ dark to light along u); a sharp step of H grey levels gives H. */
typedef struct vs_meas_edge {
    float x, y;                /* on the box's centre line, image pixels */
    float pos;                 /* along the box from its start, image pixels */
    float contrast;
} vs_meas_edge;

typedef struct vs_meas_caliper_result {
    int32_t struct_size;       /* sizeof(vs_meas_caliper_result), set by the caller (see above) */
    int32_t found;             /* 1 = an edge (pair) of the polarity at or above the threshold */
    int32_t ambiguous;         /* 1 = another candidate nearly as good (at least 90 % of the score): the choice is in doubt */
    int32_t candidates;        /* edges at or above the threshold, any polarity */
    float   threshold;         /* the threshold used (auto resolved) */
    vs_meas_edge edge0, edge1; /* the edge, or the pair (edge1 only in pair mode) */
    float   position;          /* edge0 (single) or the pair's middle, along the box, image pixels */
    float   width_px;          /* pair: distance between the two edges, measure-space px */
    float   width_mm;          /* width_px * mm_per_px; 0 without a scale */
    float   mx0, my0, mx1, my1;/* edge0 and edge1 in measure space */
} vs_meas_caliper_result;

VS_MEAS_API void    vs_meas_default_caliper(vs_meas_caliper_params* p);

/* Finds the edge (pair) in box on an 8-bit grey image. edges (may be NULL): every candidate, up to cap,
   strongest first. Returns the number of candidates, or < 0. calib may be NULL. */
VS_MEAS_API int32_t vs_meas_caliper(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                    const vs_meas_box* box, const vs_meas_caliper_params* p, const vs_meas_calib* calib,
                                    vs_meas_edge* edges, int32_t cap, vs_meas_caliper_result* out);

enum vs_meas_fit { VS_MEAS_FIT_LEAST_SQUARES = 0, VS_MEAS_FIT_RANSAC = 1 };

typedef struct vs_meas_line_params {
    int32_t struct_size;       /* sizeof(vs_meas_line_params); set by vs_meas_default_line */
    float   x0, y0, x1, y1;    /* the expected edge, image pixels; calipers search across it along v of the
                                  direction (x0,y0) -> (x1,y1) (left to right: top to bottom) */
    int32_t calipers;          /* 3..200 (default 10), spread evenly along the line */
    float   search_width;      /* px, each caliper's length across the line (default 20) */
    float   caliper_height;    /* px, each caliper's width along the line; 0 = the spacing, at most 10 */
    float   outlier_px;        /* measure-space px: points farther from the line are not used (default 1.5); with RANSAC the last refit
                                  also leaves out points beyond 3 robust sigmas of the others (at least 0.25 px) */
    int32_t fit;               /* vs_meas_fit (default RANSAC, then least squares on the inliers) */
    vs_meas_caliper_params caliper; /* polarity, threshold, filter per caliper (mode is single) */
} vs_meas_line_params;

typedef struct vs_meas_line_result {
    int32_t struct_size;       /* sizeof(vs_meas_line_result), set by the caller */
    int32_t found;             /* 1 = at least two points used */
    int32_t calipers, points_found, points_used;
    float   x0, y0, x1, y1;    /* the fitted line between the ends of the expected one, image pixels */
    float   mx0, my0, mx1, my1;/* the same in measure space */
    float   angle_deg;         /* direction x0 -> x1 in measure space, -180..180, counter-clockwise */
    float   straightness_px;   /* largest distance of any point found from the line (outliers too), measure-space px */
    float   rms_px;            /* rms distance of the used points */
    float   mean_contrast;     /* of the used points, grey levels (absolute) */
} vs_meas_line_result;

VS_MEAS_API void    vs_meas_default_line(vs_meas_line_params* p);
/* points (may be NULL): each caliper's edge in image pixels, NaN where none; up to cap. */
VS_MEAS_API int32_t vs_meas_line(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                 const vs_meas_line_params* p, const vs_meas_calib* calib,
                                 float* points_xy, int32_t cap, vs_meas_line_result* out);

typedef struct vs_meas_circle_params {
    int32_t struct_size;       /* sizeof(vs_meas_circle_params); set by vs_meas_default_circle */
    float   cx, cy;            /* the expected centre, image pixels */
    float   r_inner, r_outer;  /* the annulus searched, px (0 <= r_inner < r_outer) */
    float   start_deg, span_deg; /* the arc searched, counter-clockwise from start (default 0, 360) */
    int32_t calipers;          /* 8..360 (default 36) */
    int32_t inward;            /* 0 = calipers search outward (polarity seen from the centre out), 1 = inward */
    float   caliper_height;    /* px; 0 = half the spacing at the mid radius, 1..10 */
    float   outlier_px;        /* measure-space px, default 1.5 */
    int32_t fit;               /* vs_meas_fit (default RANSAC, then geometric least squares) */
    vs_meas_caliper_params caliper;
} vs_meas_circle_params;

typedef struct vs_meas_circle_result {
    int32_t struct_size;       /* sizeof(vs_meas_circle_result), set by the caller */
    int32_t found;             /* 1 = at least three points used */
    int32_t calipers, points_found, points_used;
    float   cx, cy;            /* centre, image pixels */
    float   mcx, mcy;          /* centre in measure space */
    float   radius_px;         /* measure-space px */
    float   roundness_px;      /* largest minus smallest distance of any point found from the centre (outliers too) */
    float   rms_px;
    float   mean_contrast;
} vs_meas_circle_result;

VS_MEAS_API void    vs_meas_default_circle(vs_meas_circle_params* p);
VS_MEAS_API int32_t vs_meas_circle(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                   const vs_meas_circle_params* p, const vs_meas_calib* calib,
                                   float* points_xy, int32_t cap, vs_meas_circle_result* out);

/* Find ellipse (spec 2.4): an oval hole or a circle seen at a slant. The search is a circle's: radial calipers
   across the ring r_inner..r_outer round (cx, cy), so the ring must hold the whole rim (r_inner below half the
   minor axis, r_outer above half the major). Fit: RANSAC on five-point direct ellipse fits (Fitzgibbon), then
   geometric least squares on the true distances to the rim. */
typedef struct vs_meas_ellipse_result {
    int32_t struct_size;       /* sizeof(vs_meas_ellipse_result), set by the caller */
    int32_t found;             /* 1 = at least six points used */
    int32_t calipers, points_found, points_used;
    float   cx, cy;            /* centre, image pixels */
    float   mcx, mcy;          /* centre in measure space */
    float   major_px, minor_px;/* full axis lengths (the largest and smallest diameter), measure-space px */
    float   angle_deg;         /* direction of the major axis, counter-clockwise on the screen, -90..90 */
    float   fit_error_px;      /* largest distance of any point found from the ellipse (outliers too) */
    float   roundness_px;      /* largest minus smallest signed distance of any point found from the ellipse (outliers
                                  too), as a circle's roundness: a dent or a bulge left out of the fit still shows */
    float   rms_px;            /* rms distance of the used points */
    float   mean_contrast;
} vs_meas_ellipse_result;

VS_MEAS_API int32_t vs_meas_ellipse(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                    const vs_meas_circle_params* p, const vs_meas_calib* calib,
                                    float* points_xy, int32_t cap, vs_meas_ellipse_result* out);

/* Find corner (spec 2.5): two straight edges fitted (2.2) and where they meet. */
typedef struct vs_meas_corner_params {
    int32_t struct_size;       /* sizeof(vs_meas_corner_params); set by vs_meas_default_corner */
    vs_meas_line_params line1, line2;
} vs_meas_corner_params;

typedef struct vs_meas_corner_result {
    int32_t struct_size;       /* sizeof(vs_meas_corner_result), set by the caller */
    int32_t found;             /* 1 = both lines found and they meet */
    int32_t parallel;          /* 1 = both lines found but within 0.5 degrees of parallel: no corner */
    float   x, y;              /* the corner, image pixels */
    float   mx, my;            /* the corner in measure space */
    float   angle_deg;         /* 0..180: between the two edges as they leave the corner (each towards the middle
                                  of its fitted line), measure space */
    vs_meas_line_result line1, line2; /* their struct_size is set by the library */
} vs_meas_corner_result;

VS_MEAS_API void    vs_meas_default_corner(vs_meas_corner_params* p);
VS_MEAS_API int32_t vs_meas_corner(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                   const vs_meas_corner_params* p, const vs_meas_calib* calib, vs_meas_corner_result* out);

/* Edge width / gap (spec 2.7): the box is cut across its height into `samples` slices, and each slice is a pair
   caliper (2.1) along the box's length: a strip, slot or seam running along v is measured at each slice. */
typedef struct vs_meas_gap_params {
    int32_t struct_size;       /* sizeof(vs_meas_gap_params); set by vs_meas_default_gap */
    vs_meas_box box;
    int32_t samples;           /* 1..200 (default 20); each slice is box.height / samples high */
    vs_meas_caliper_params caliper; /* polarity of the first edge (a dark gap on bright: light to dark), threshold,
                                       filter, score; mode is always pair */
} vs_meas_gap_params;

typedef struct vs_meas_gap_result {
    int32_t struct_size;       /* sizeof(vs_meas_gap_result), set by the caller */
    int32_t samples;
    int32_t found;             /* slices with a width; a slice with no pair, two pairs nearly as good, or an edge between
                                  its pair (a burr, a partial bridge) has none */
    float   min_px, max_px, mean_px; /* over the slices found, measure-space px */
    int32_t min_index, max_index;    /* the slice of the narrowest and of the widest, 0 = nearest the box's top */
    float   min_x, min_y, max_x, max_y; /* the middle of that pair, image pixels */
} vs_meas_gap_result;

VS_MEAS_API void    vs_meas_default_gap(vs_meas_gap_params* p);
/* widths (may be NULL): each slice's width in measure-space px, NaN where none; up to cap. */
VS_MEAS_API int32_t vs_meas_gap(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                const vs_meas_gap_params* p, const vs_meas_calib* calib,
                                float* widths, int32_t cap, vs_meas_gap_result* out);

/* Pin pitch (spec 2.8): every edge along the box (its whole height averaged), paired in order into pins: a pin is
   an edge of the leading polarity followed by the next edge, of the opposite one. */
typedef struct vs_meas_pitch_params {
    int32_t struct_size;       /* sizeof(vs_meas_pitch_params); set by vs_meas_default_pitch */
    vs_meas_box box;           /* along the row, starting and ending on the background */
    int32_t polarity;          /* the leading edge of each pin along the box: dark to light (default) = bright pins on dark;
                                  any = the polarity of the first edge */
    float   threshold;         /* grey levels; 0 = auto: from the noise, at least 5, at most a quarter of the strongest edge */
    float   filter_half_width; /* px, 1..20 (default 1, see vs_meas_edges) */
    float   min_width;         /* measure-space px: a narrower pair is not a pin (counted unpaired); default 1 */
} vs_meas_pitch_params;

typedef struct vs_meas_pin {
    float x, y;                /* centre, on the box's centre line, image pixels */
    float pos;                 /* centre from the box's start, measure-space px */
    float width_px;            /* measure-space px */
    float contrast;            /* the weaker of its two edges, grey levels (absolute) */
} vs_meas_pin;

typedef struct vs_meas_pitch_result {
    int32_t struct_size;       /* sizeof(vs_meas_pitch_result), set by the caller */
    int32_t count;             /* pins found */
    int32_t unpaired;          /* edges that are part of no pin: a double edge, a pin cut by the box, dirt */
    float   threshold;         /* the threshold used */
    float   mean_pitch_px;     /* (last centre - first) / (count - 1), measure-space px; 0 below two pins */
    float   min_pitch_px, max_pitch_px; /* between neighbours */
} vs_meas_pitch_result;

VS_MEAS_API void    vs_meas_default_pitch(vs_meas_pitch_params* p);
/* pins (may be NULL): in order along the box, up to cap. Returns the count, or < 0. */
VS_MEAS_API int32_t vs_meas_pitch(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                  const vs_meas_pitch_params* p, const vs_meas_calib* calib,
                                  vs_meas_pin* pins, int32_t cap, vs_meas_pitch_result* out);

/* Count edges / thread (spec 2.9): every edge of the polarity along the box, in order, and their spacing.
   Edges closer than the filter's reach (pins, threads) are placed by a centroid over a window symmetric about the
   edge, exact inside a row. The first and last edge of a row (and either edge of a lone narrow pin), whose
   response a neighbour overlaps on one side only, would lean outwards on a soft image (0.35 px at a 1 px blur and
   3.2 px between edges, 0.6 px at 2.2 px); the row is drawn again from the edge's own free side and the edge placed
   where the drawing fits the profile best (within 0.1 px down to 2.4 px between edges at a 1 px blur, 0.11 px at
   2.2 px, and on a 4 px pin at a 1.5 px blur, measure_tests). pitch and edges default to filter_half_width 1 so that responses overlap
   little. */
typedef struct vs_meas_edges_params {
    int32_t struct_size;       /* sizeof(vs_meas_edges_params); set by vs_meas_default_edges */
    vs_meas_box box;
    int32_t polarity;          /* vs_meas_polarity (default any: every crest gives two edges) */
    float   threshold;         /* grey levels; 0 = auto as vs_meas_pitch_params */
    float   filter_half_width; /* px, 1..20 (default 1, see above) */
} vs_meas_edges_params;

typedef struct vs_meas_edges_result {
    int32_t struct_size;       /* sizeof(vs_meas_edges_result), set by the caller */
    int32_t count;             /* edges of the polarity found */
    float   threshold;
    float   mean_spacing_px;   /* (last - first) / (count - 1), measure-space px; 0 below two edges */
    float   min_spacing_px, max_spacing_px;
    float   period_px;         /* the profile's repeat distance from its autocorrelation (a whole thread pitch, also with
                                  polarity any), measure-space px; 0 = no clear repeat */
} vs_meas_edges_result;

VS_MEAS_API void    vs_meas_default_edges(vs_meas_edges_params* p);
/* edges (may be NULL): in order along the box (pos in image pixels as vs_meas_caliper), up to cap. Returns the count, or < 0. */
VS_MEAS_API int32_t vs_meas_edges(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                  const vs_meas_edges_params* p, const vs_meas_calib* calib,
                                  vs_meas_edge* edges, int32_t cap, vs_meas_edges_result* out);

/* An image point in measure space (undistorted; unchanged without a lens model), and back. */
VS_MEAS_API void    vs_meas_to_measure(const vs_meas_calib* calib, float x, float y, float* mx, float* my);
VS_MEAS_API void    vs_meas_to_image(const vs_meas_calib* calib, float mx, float my, float* x, float* y);

#ifdef __cplusplus
}
#endif

#endif
