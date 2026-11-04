/*
 * vs_blob.h - C API of the Blob tool ("Count / spot", TOOL-SPECIFICATIONS.md 4.1 and 4.4): thresholds a
 * region, finds the connected regions (blobs) and measures each, then filters and sorts them.
 *
 * Per blob: area (pixels), centroid, perimeter, circularity 4 pi A / P^2 and compactness P^2 / (4 pi A),
 * the same-moment ellipse (axes, angle, elongation = major / minor axis), the upright box, the smallest
 * turned box, the number of holes, the mean grey level and whether it touches the region's edge.
 *
 * Method: threshold (fixed, Otsu, band, or local mean in a window), optional open / close 3x3 and hole
 * filling, connected components (4- or 8-connected, OpenCV's run-based labelling), then on the blobs that
 * pass the cheap filters (edge, area): the outer boundary and its holes (background regions not reached from
 * outside, the background connected the other way round from the blobs: 4 for 8-connected blobs, 8 for
 * 4-connected ones, so a gap only a diagonal wide is no hole of a 4-connected blob), the perimeter as the boundary
 * chain through the edge pixels with Vossepoel-Smeulders weights (0.980 straight, 1.406 diagonal, -0.091
 * per corner) plus pi for the half pixel to the true edge, central moments for the ellipse. The blobs
 * are measured in parallel on OpenCV's thread pool.
 *
 * Coordinates are image pixels (not region pixels); angles are degrees counter-clockwise on the screen.
 * Line safety: a run that cannot finish (time budget, or more blobs than can be measured) sets
 * vs_blob_result.truncated; the caller must then treat the part as not checked (NG), never as passed.
 *
 * Threading: one handle must not be used from two threads at once.
 */
#ifndef VS_BLOB_H
#define VS_BLOB_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_BLOB_BUILD
#    define VS_BLOB_API __declspec(dllexport)
#  else
#    define VS_BLOB_API __declspec(dllimport)
#  endif
#else
#  define VS_BLOB_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct vs_blob vs_blob;

enum {
    VS_BLOB_THRESH_AUTO = 0,     /* Otsu on the region */
    VS_BLOB_THRESH_FIXED = 1,    /* threshold */
    VS_BLOB_THRESH_BAND = 2,     /* threshold <= grey <= threshold_high is a blob (polarity not used) */
    VS_BLOB_THRESH_LOCAL = 3     /* darker (lighter) than the mean of a local_window square by local_offset */
};
enum { VS_BLOB_DARK_ON_LIGHT = 0, VS_BLOB_LIGHT_ON_DARK = 1 };
enum { VS_BLOB_MORPH_NONE = 0, VS_BLOB_MORPH_OPEN3 = 1, VS_BLOB_MORPH_CLOSE3 = 2 };
enum {
    VS_BLOB_SORT_NONE = 0,       /* label order: top to bottom by first row */
    VS_BLOB_SORT_AREA = 1, VS_BLOB_SORT_X = 2, VS_BLOB_SORT_Y = 3, VS_BLOB_SORT_PERIMETER = 4,
    VS_BLOB_SORT_CIRCULARITY = 5, VS_BLOB_SORT_ELONGATION = 6, VS_BLOB_SORT_MEAN_GREY = 7
};
/* why a blob was left out (vs_blob_get_rejected) */
enum {
    VS_BLOB_REJ_EDGE = 1,        /* touches the region's edge and exclude_edge is on */
    VS_BLOB_REJ_AREA_LOW = 2, VS_BLOB_REJ_AREA_HIGH = 3,
    VS_BLOB_REJ_CIRC_LOW = 4, VS_BLOB_REJ_CIRC_HIGH = 5,
    VS_BLOB_REJ_ELONG_LOW = 6, VS_BLOB_REJ_ELONG_HIGH = 7,
    VS_BLOB_REJ_KEEP_FIRST = 8   /* past keep_first after sorting */
};

typedef struct vs_blob_params {
    int32_t struct_size;      /* sizeof(vs_blob_params); set by vs_blob_default_params */
    int32_t roi_x, roi_y, roi_w, roi_h; /* region; roi_w = 0 means the whole image */
    int32_t threshold_mode;   /* VS_BLOB_THRESH_*, default AUTO */
    int32_t polarity;         /* VS_BLOB_DARK_ON_LIGHT (default) or VS_BLOB_LIGHT_ON_DARK */
    int32_t threshold;        /* FIXED: level 0..255 (dark: grey <= level is a blob); BAND: low end */
    int32_t threshold_high;   /* BAND: high end */
    int32_t local_window;     /* LOCAL: window side in px, odd, >= 3 (default 31) */
    int32_t local_offset;     /* LOCAL: grey levels past the local mean (default 10) */
    int32_t min_contrast;     /* AUTO: the level stays at least this far (grey levels) from the background
                                 class's mean, so Otsu never splits the noise of a clean region; a small spot
                                 that much darker (lighter) is still found (default 15) */
    int32_t connectivity;     /* 4 or 8 (default 8) */
    int32_t fill_holes;       /* 1 = holes are part of the blob (area includes them); default 0 */
    int32_t exclude_edge;     /* 1 = blobs touching the region's edge are left out (default 1) */
    int32_t morphology;       /* VS_BLOB_MORPH_*, default NONE */
    float   min_area, max_area;               /* px; default 10 and 0 (0 = no upper limit) */
    float   min_circularity, max_circularity; /* 0..1; 0 = no limit */
    float   min_elongation, max_elongation;   /* >= 1; 0 = no limit */
    int32_t sort_by;          /* VS_BLOB_SORT_*, default NONE */
    int32_t sort_descending;  /* 1 = largest first */
    int32_t keep_first;       /* keep only the first N after sorting; 0 = all */
    int32_t time_budget_ms;   /* default 50; past it the run stops with truncated = 1 */
} vs_blob_params;

typedef struct vs_blob_info {
    float   area;             /* pixels */
    float   cx, cy;           /* centroid */
    float   perimeter;        /* outer boundary, px */
    float   circularity;      /* 4 pi A / P^2, 0..1 (1 = disc) */
    float   compactness;      /* P^2 / (4 pi A), >= 1 */
    float   major_axis, minor_axis; /* same-moment ellipse, full axis lengths in px */
    float   elongation;       /* major / minor axis, >= 1 */
    float   angle_deg;        /* major axis, counter-clockwise from the x axis, -90..90 */
    int32_t box_x, box_y, box_w, box_h;       /* upright bounding box */
    float   rbox_cx, rbox_cy, rbox_w, rbox_h, rbox_angle_deg; /* smallest turned box (counter-clockwise) */
    int32_t holes;
    float   mean_grey;
    int32_t touches_edge;
    int32_t reject_reason;    /* 0 for kept blobs, VS_BLOB_REJ_* for rejected ones. Blobs rejected for edge
                                 or area carry only area, centroid, box and touches_edge */
} vs_blob_info;

typedef struct vs_blob_result {
    int32_t count;            /* blobs kept */
    int32_t rejected;         /* blobs left out by a filter (all of them; vs_blob_get_rejected keeps 1000) */
    float   total_area;       /* of the kept blobs */
    int32_t threshold_used;   /* the level applied (AUTO, FIXED; low end for BAND; -1 for LOCAL) */
    int32_t flat;             /* AUTO: no clear two-level contrast, so the level was set min_contrast from the
                                 background instead of Otsu's */
    float   region_mean;      /* mean grey of the region (a black or white frame shows here) */
    int32_t truncated;        /* 1 = the run did not finish (time budget or too many blobs): not checked */
    float   time_ms;
    int32_t edge_count;       /* blobs left out only for touching the region's edge (they pass the area
                                 filter): a caller with an upper count limit must count them too */
} vs_blob_result;

VS_BLOB_API void        vs_blob_default_params(vs_blob_params* p);
VS_BLOB_API vs_blob*    vs_blob_create(const vs_blob_params* p);
VS_BLOB_API void        vs_blob_destroy(vs_blob* t);
VS_BLOB_API int32_t     vs_blob_set_params(vs_blob* t, const vs_blob_params* p);

/* Runs on an 8-bit grey image. Returns 0 (also when truncated), < 0 on bad input or an error. */
VS_BLOB_API int32_t     vs_blob_run(vs_blob* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride);
VS_BLOB_API void        vs_blob_get_result(const vs_blob* t, vs_blob_result* out);
/* The kept blobs in sort order: copies up to cap, returns the number kept. */
VS_BLOB_API int32_t     vs_blob_get_blobs(const vs_blob* t, vs_blob_info* out, int32_t cap);
/* The rejected blobs (the first 1000), largest first: copies up to cap, returns how many are held. */
VS_BLOB_API int32_t     vs_blob_get_rejected(const vs_blob* t, vs_blob_info* out, int32_t cap);
VS_BLOB_API const char* vs_blob_last_error(const vs_blob* t);

/*
 * Contour analysis ("Check outline", TOOL-SPECIFICATIONS.md 4.2) of the kept blobs, measured in vs_blob_run
 * (within its time budget) when enabled. Per blob: the outer outline to a fraction of a pixel, its length
 * and enclosed area, convexity (area / convex hull area), the convexity defects (indents: depth, deepest
 * point, width of the hull chord over them) and, when an outline is taught, the deviation from it.
 *
 * Method: the blob's outer boundary chain (pixel centres); each point is moved along the outline's normal to
 * where the smoothed grey crosses the edge level (linear between samples 0.25 px apart, the crossing
 * nearest the pixel's edge). A point without a crossing stays on the pixel edge and does not count in
 * edge_share. Defects: the deepest outline point under each convex hull edge. Deviation: the taught outline
 * is placed on the blob (centroid and axis of the enclosed area, then closest-point ICP, rigid, no scale;
 * 12 start angles when the shape has no clear axis), and the largest closest-point distance either way.
 *
 * Line safety: an outline cut by the region's edge has closed = 0; one that found few edge crossings has a
 * low edge_share. The caller fails both (not checked), never passes them. The time budget is checked inside
 * each outline too (after smoothing, between placements): a run past it is truncated, as in vs_blob_run.
 */
typedef struct vs_blob_contour_params {
    int32_t struct_size;      /* sizeof(vs_blob_contour_params); set by vs_blob_default_contour_params */
    int32_t enabled;          /* 1 = measure the kept blobs' outlines (default 0) */
    float   edge_level;       /* grey level of the edge; < 0 = halfway between the blob's mean grey and the mean of
                                 a 2 px ring round it (default -1) */
    float   smoothing;        /* Gaussian sigma in px on the grey before the edge is placed; 0 = none (default 1) */
    float   min_defect_depth; /* px; shallower indents are not defects (default 1) */
} vs_blob_contour_params;

typedef struct vs_blob_defect {
    float depth;              /* px from the convex hull edge */
    float x, y;               /* deepest point, image px */
    float width;              /* length of the hull edge over the indent, px */
} vs_blob_defect;

typedef struct vs_blob_contour {
    int32_t struct_size;      /* the caller sets sizeof(vs_blob_contour) before vs_blob_get_contour */
    int32_t points;           /* outline points */
    float   length;           /* outline length, px */
    float   area;             /* enclosed by the outline, px */
    float   convexity;        /* area / convex hull area, 0..1 (1 = convex) */
    int32_t defects;          /* indents at least min_defect_depth deep */
    float   max_defect_depth; /* deepest indent, px, also one shallower than min_defect_depth (0 when convex) */
    float   max_defect_x, max_defect_y;
    float   edge_share;       /* share of outline points placed on a grey crossing, 0..1 */
    int32_t closed;           /* 0 = the blob touches the region's edge: the outline is cut there */
    int32_t taught;           /* 1 = compared with the taught outline */
    float   max_deviation;    /* px, largest closest-point distance either way (taught only) */
    float   mean_deviation;   /* px, mean distance of this outline's points from the taught one */
    float   max_deviation_x, max_deviation_y; /* where, image px */
    float   pose_x, pose_y, pose_angle_deg;   /* taught outline's centroid placed here, turned counter-clockwise */
} vs_blob_contour;

VS_BLOB_API void        vs_blob_default_contour_params(vs_blob_contour_params* p);
/* Applies from the next vs_blob_run. Returns -1 on a wrong struct_size. */
VS_BLOB_API int32_t     vs_blob_set_contour_params(vs_blob* t, const vs_blob_contour_params* p);
/* Kept blob `blob` (sort order) of the last run. Returns -1 for a bad index, contour analysis off, or a wrong
   out->struct_size. */
VS_BLOB_API int32_t     vs_blob_get_contour(const vs_blob* t, int32_t blob, vs_blob_contour* out);
/* The outline as x, y pairs (image px): copies up to cap points, returns the number of points (-1 bad index). */
VS_BLOB_API int32_t     vs_blob_get_outline(const vs_blob* t, int32_t blob, float* xy, int32_t cap);
/* The defects, deepest first: copies up to cap, returns how many (-1 bad index). */
VS_BLOB_API int32_t     vs_blob_get_defects(const vs_blob* t, int32_t blob, vs_blob_defect* out, int32_t cap);
/* Teaches kept blob `blob`'s outline of the last run as the reference (stored round its centroid). */
VS_BLOB_API int32_t     vs_blob_teach_outline(vs_blob* t, int32_t blob);
/* Sets the taught outline (x, y pairs in any frame, as vs_blob_get_taught_outline gave them); n = 0 clears it.
   Returns -1 for 1 or 2 points. */
VS_BLOB_API int32_t     vs_blob_set_taught_outline(vs_blob* t, const float* xy, int32_t n);
VS_BLOB_API int32_t     vs_blob_get_taught_outline(const vs_blob* t, float* xy, int32_t cap);

#ifdef __cplusplus
}
#endif

#endif
