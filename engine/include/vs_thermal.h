/*
 * vs_thermal.h - C API of the Industrial Thermal Heat Seal & Packaging Inspection Engine (Tool 8.4, Sun Teknovation eSaksham).
 *
 * Inspects thermal signatures for non-destructive, sub-surface packaging integrity:
 *   1. Bottle Induction Cap Sealing:
 *      Checks heated aluminum foil liner inside plastic closure for missing foil,
 *      incomplete heat ring (partial crescent), crooked/tilted caps, and overheated/scorched liners.
 *   2. Flexible Pouch & Tray Heat Seal:
 *      Inspects heat-sealed seam across pouches, IV bags, and thermoformed blister trays
 *      for cold channel leaks (pinholes, voids), seal necking/thinning, and trapped product contamination.
 *   3. Hot Melt Adhesive (Glue) Carton Sealing:
 *      Inspects hot melt glue beads on corrugated/folding carton flaps for missing glue beads (clogged nozzles),
 *      broken/intermittent beads, glue position, and cold joints.
 *
 * Supports both 8-bit calibrated thermal frames and 16-bit raw radiometric temperature arrays (0.01 deg C / cK).
 * Includes Ironbow/Rainbow thermal pseudo-color palette synthesis for operator HMI displays.
 *
 * Safety Rule: Any cold channel, missing foil, or broken glue bead triggers strict NG with exact defect diagnostics.
 * Threading: All inspect functions are stateless and thread-safe across multiple thermal cameras.
 */
#ifndef VS_THERMAL_H
#define VS_THERMAL_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_THERMAL_BUILD
#    define VS_THERM_API __declspec(dllexport)
#  else
#    define VS_THERM_API __declspec(dllimport)
#  endif
#else
#  define VS_THERM_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Verdict status codes */
enum vs_thermal_verdict {
    VS_THERMAL_PASS = 0,                    /* Hermetic, uniform seal / continuous hot glue */
    VS_THERMAL_NG_MISSING_FOIL = 1,         /* Bottle: No induction foil present (ambient cold circle) */
    VS_THERMAL_NG_INCOMPLETE_SEAL = 2,      /* Bottle: Broken ring or cold sector (leak hazard) */
    VS_THERMAL_NG_OVERHEATED = 3,           /* Bottle/Pouch: Excessive temperature (scorched liner) */
    VS_THERMAL_NG_UNDERHEATED = 4,          /* Bottle/Pouch: Insufficient heat (cold bond / peel risk) */
    VS_THERMAL_NG_CROOKED_SEAL = 5,         /* Bottle: Lopsided heat distribution (tilted/loose cap) */
    VS_THERMAL_NG_SEAM_VOID = 6,            /* Pouch: Cold channel / pinhole leak through seal */
    VS_THERMAL_NG_SEAM_TOO_NARROW = 7,      /* Pouch: Seal width below minimum safe margin */
    VS_THERMAL_NG_CONTAMINATION = 8,        /* Pouch: Product trapped in seal zone (cold notch) */
    VS_THERMAL_NG_MISSING_BEAD = 9,         /* Hot Glue: One or more glue beads missing (clogged gun) */
    VS_THERMAL_NG_BROKEN_BEAD = 10,         /* Hot Glue: Intermittent bead with gap exceeding limit */
    VS_THERMAL_NG_COLD_GLUE = 11,           /* Hot Glue: Glue cooled before compression (cold joint) */
    VS_THERMAL_NG_MISPLACED_BEAD = 12,      /* Hot Glue: Glue bead shifted outside flap bonding zone */
    VS_THERMAL_NG_BLISTER_BARRIER_BREACH = 13, /* Blister: Moisture barrier breached around tablet cavity */
    VS_THERMAL_NG_JAW_TILT = 14,            /* Machine tooling: Sealing jaw or platen mechanically tilted */
    VS_THERMAL_ERROR = 99                   /* Dimension mismatch, null pointer, or invalid config */
};

/* Color palettes for operator thermal display */
enum vs_thermal_palette {
    VS_PALETTE_IRONBOW = 0,   /* Standard FLIR Ironbow: Black -> Violet -> Orange -> Yellow -> White */
    VS_PALETTE_HOT_IRON = 1,  /* Deep Red -> Orange -> Yellow -> White */
    VS_PALETTE_RAINBOW = 2,   /* Blue -> Cyan -> Green -> Yellow -> Red */
    VS_PALETTE_FIRE = 3       /* Black -> Red -> Orange -> Yellow */
};

/* Data format of thermal input buffer */
enum vs_thermal_format {
    VS_THERMAL_MONO8 = 0,         /* 8-bit normalized thermal image (0..255) with min/max temp scale */
    VS_THERMAL_RADIOMETRIC16 = 1  /* 16-bit direct temperature: value in 0.01 deg C (e.g. 7520 = 75.20 C) */
};

/* ========================================================================= */
/* 1. Bottle Induction Cap Sealing Parameters & Results                      */
/* ========================================================================= */

typedef struct vs_thermal_bottle_params {
    int32_t struct_size;          /* sizeof(vs_thermal_bottle_params) */
    float   expected_cx;          /* Expected bottle center X (px); <= 0 for auto-centroid */
    float   expected_cy;          /* Expected bottle center Y (px); <= 0 for auto-centroid */
    float   inner_radius_px;      /* Inner radius of bottle rim annulus (px) */
    float   outer_radius_px;      /* Outer radius of bottle rim annulus (px) */
    float   min_seal_temp_c;      /* Minimum acceptable seal temperature (deg C, default: 60.0f) */
    float   max_seal_temp_c;      /* Maximum acceptable seal temperature (deg C, default: 95.0f) */
    float   missing_foil_temp_c;  /* Threshold below which foil is considered missing (deg C, default: 42.0f) */
    float   max_cold_gap_deg;     /* Maximum allowed angular gap of cold seal (deg, default: 15.0f) */
    float   max_tilt_temp_delta;  /* Max allowed temperature difference across 180 deg (deg C, default: 18.0f) */
    float   t_min_calib;          /* Calibration min temp for MONO8 format (deg C, default: 20.0f) */
    float   t_max_calib;          /* Calibration max temp for MONO8 format (deg C, default: 120.0f) */
} vs_thermal_bottle_params;

typedef struct vs_thermal_bottle_result {
    int32_t struct_size;          /* sizeof(vs_thermal_bottle_result) */
    int32_t verdict;              /* vs_thermal_verdict */
    float   detected_cx;          /* Measured center X of heat ring */
    float   detected_cy;          /* Measured center Y of heat ring */
    float   mean_seal_temp_c;     /* Mean temperature along the heat ring */
    float   peak_seal_temp_c;     /* Maximum temperature recorded */
    float   min_seal_temp_c;      /* Minimum temperature recorded along ring */
    float   cold_gap_deg;         /* Angular span of worst cold gap (0 = continuous) */
    float   cold_gap_angle_deg;   /* Starting angle of worst cold gap (0..360 deg) */
    float   tilt_delta_c;         /* Left-to-right or top-to-bottom temperature gradient */
    float   seal_integrity_pct;   /* 0..100% sealing completeness score */
    float   time_ms;              /* Inspection duration */
} vs_thermal_bottle_result;

/* ========================================================================= */
/* 2. Pouch & Tray Heat Seal Seam Parameters & Results                       */
/* ========================================================================= */

typedef struct vs_thermal_pouch_params {
    int32_t struct_size;          /* sizeof(vs_thermal_pouch_params) */
    int32_t seam_roi_x;           /* Bounding box of seal seam ROI */
    int32_t seam_roi_y;
    int32_t seam_roi_w;
    int32_t seam_roi_h;
    int32_t is_vertical;          /* 1 = vertical seam along Y; 0 = horizontal seam along X */
    float   min_seal_temp_c;      /* Minimum bond temperature threshold (deg C, default: 65.0f) */
    float   min_seal_width_px;    /* Minimum acceptable seal width across seam (px, default: 10.0f) */
    float   max_void_length_px;   /* Maximum tolerated cold notch / void length (px, default: 4.0f) */
    float   contamination_drop_c; /* Temperature drop signaling trapped product (deg C, default: 12.0f) */
    float   t_min_calib;          /* Calibration min temp for MONO8 format (deg C, default: 20.0f) */
    float   t_max_calib;          /* Calibration max temp for MONO8 format (deg C, default: 160.0f) */
} vs_thermal_pouch_params;

typedef struct vs_thermal_pouch_result {
    int32_t struct_size;          /* sizeof(vs_thermal_pouch_result) */
    int32_t verdict;              /* vs_thermal_verdict */
    float   mean_seam_temp_c;     /* Mean temperature of the seal seam */
    float   mean_seam_width_px;   /* Average seal thickness */
    float   min_seam_width_px;    /* Narrowest seal width recorded */
    int32_t void_count;           /* Number of cold channel voids detected */
    float   worst_void_x;         /* Coordinate of worst leak channel */
    float   worst_void_y;
    float   worst_void_length_px; /* Length of worst leak channel */
    int32_t contamination_found;  /* 1 if trapped liquid/powder detected in seam */
    float   seal_integrity_pct;   /* 0..100% seam score */
    float   time_ms;
} vs_thermal_pouch_result;

/* ========================================================================= */
/* 3. Hot Melt Adhesive (Glue) Carton Flap Parameters & Results              */
/* ========================================================================= */

typedef struct vs_thermal_glue_params {
    int32_t struct_size;          /* sizeof(vs_thermal_glue_params) */
    int32_t roi_x;                /* Flap bonding area ROI */
    int32_t roi_y;
    int32_t roi_w;
    int32_t roi_h;
    int32_t is_vertical;          /* 1 = vertical glue lines; 0 = horizontal glue lines */
    int32_t expected_bead_count;  /* Expected number of parallel glue beads (e.g. 2, 3, or 4) */
    float   min_glue_temp_c;      /* Minimum acceptable glue temperature (deg C, default: 75.0f) */
    float   min_bead_length_px;   /* Minimum required length per bead (px) */
    float   max_bead_gap_px;      /* Maximum allowed gap in a single bead line (px, default: 8.0f) */
    float   t_min_calib;          /* Calibration min temp for MONO8 format (deg C, default: 20.0f) */
    float   t_max_calib;          /* Calibration max temp for MONO8 format (deg C, default: 180.0f) */
} vs_thermal_glue_params;

typedef struct vs_thermal_glue_result {
    int32_t struct_size;          /* sizeof(vs_thermal_glue_result) */
    int32_t verdict;              /* vs_thermal_verdict */
    int32_t detected_bead_count;  /* Number of valid glue beads found */
    float   mean_glue_temp_c;     /* Average glue temperature */
    float   min_bead_length_px;   /* Length of shortest detected bead */
    float   worst_gap_px;         /* Largest gap in any glue line */
    int32_t total_glue_area_px;   /* Total hot glue coverage in pixels */
    float   bonding_confidence;   /* 0..100% confidence */
    float   time_ms;
} vs_thermal_glue_result;

/* ========================================================================= */
/* 4. Actionable Machine Tooling Advice & Blister Pack Inspection            */
/* ========================================================================= */

typedef struct vs_thermal_tooling_advice {
    char    fault_code[32];         /* e.g. "TOOL_JAW_TILT_RIGHT" */
    char    recommended_action[256];/* e.g. "Shim right jaw leveling screw +0.08 mm" */
    float   asymmetry_metric;       /* Quantitative gradient (e.g. 24.5 deg C delta) */
    int32_t severity;               /* 1 = Warning / Tune Soon, 2 = Critical NG / Immediate Stop */
} vs_thermal_tooling_advice;

typedef struct vs_thermal_blister_params {
    int32_t struct_size;          /* sizeof(vs_thermal_blister_params) */
    int32_t card_roi_x;           /* Blister card bounding box */
    int32_t card_roi_y;
    int32_t card_roi_w;
    int32_t card_roi_h;
    int32_t rows;                 /* Cavity rows (e.g. 2) */
    int32_t cols;                 /* Cavity columns (e.g. 5) */
    float   pocket_w_px;          /* Width of individual tablet cavity (px) */
    float   pocket_h_px;          /* Height of individual tablet cavity (px) */
    float   pitch_x_px;           /* Horizontal center-to-center cavity pitch (px) */
    float   pitch_y_px;           /* Vertical center-to-center cavity pitch (px) */
    float   min_barrier_width_px; /* Minimum continuous seal width around each pocket (default: 8.0f) */
    float   min_seal_temp_c;      /* Minimum seal bond temperature threshold (deg C, default: 68.0f) */
    float   t_min_calib;          /* Calibration min temp (deg C, default: 20.0f) */
    float   t_max_calib;          /* Calibration max temp (deg C, default: 120.0f) */
} vs_thermal_blister_params;

typedef struct vs_thermal_blister_result {
    int32_t struct_size;          /* sizeof(vs_thermal_blister_result) */
    int32_t verdict;              /* vs_thermal_verdict */
    int32_t compromised_cavity_idx; /* Index of failed cavity (0-based), or -1 */
    float   min_barrier_width_px; /* Narrowest barrier width recorded across all cavities */
    float   worst_barrier_x;      /* X coordinate of worst moisture barrier leak */
    float   worst_barrier_y;      /* Y coordinate of worst moisture barrier leak */
    float   mean_flange_temp_c;   /* Average temperature of sealing flanges */
    vs_thermal_tooling_advice advice; /* Actionable machine maintenance advice */
    float   time_ms;
} vs_thermal_blister_result;

/* ========================================================================= */
/* API Functions                                                             */
/* ========================================================================= */

/* Initialize parameter structs with standard industrial defaults */
VS_THERM_API void vs_thermal_default_bottle_params(vs_thermal_bottle_params* p);
VS_THERM_API void vs_thermal_default_pouch_params(vs_thermal_pouch_params* p);
VS_THERM_API void vs_thermal_default_glue_params(vs_thermal_glue_params* p);
VS_THERM_API void vs_thermal_default_blister_params(vs_thermal_blister_params* p);

/*
 * Inspect bottle induction heat seal.
 * Supports MONO8 or RADIOMETRIC16.
 * Returns 0 on success, negative error code on invalid parameters.
 */
VS_THERM_API int32_t vs_thermal_inspect_bottle_seal(
    const void* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t format, /* vs_thermal_format */
    const vs_thermal_bottle_params* params,
    vs_thermal_bottle_result* out_result);

/*
 * Inspect flexible pouch or blister tray heat seal seam.
 * Supports MONO8 or RADIOMETRIC16.
 * Returns 0 on success, negative error code on invalid parameters.
 */
VS_THERM_API int32_t vs_thermal_inspect_pouch_seam(
    const void* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t format, /* vs_thermal_format */
    const vs_thermal_pouch_params* params,
    vs_thermal_pouch_result* out_result);

/*
 * Inspect hot melt adhesive (glue) carton flap beads.
 * Supports MONO8 or RADIOMETRIC16.
 * Returns 0 on success, negative error code on invalid parameters.
 */
VS_THERM_API int32_t vs_thermal_inspect_hot_glue(
    const void* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t format, /* vs_thermal_format */
    const vs_thermal_glue_params* params,
    vs_thermal_glue_result* out_result);

/*
 * Inspect medicine blister pack multi-cavity moisture barrier.
 * Supports MONO8 or RADIOMETRIC16.
 * Returns 0 on success, negative error code on invalid parameters.
 */
VS_THERM_API int32_t vs_thermal_inspect_blister(
    const void* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t format, /* vs_thermal_format */
    const vs_thermal_blister_params* params,
    vs_thermal_blister_result* out_result);

/*
 * Translate thermal defect patterns directly into machine tooling adjustments.
 */
VS_THERM_API void vs_thermal_diagnose_tooling(
    int32_t packaging_type, /* 0: Bottle, 1: Pouch, 2: Glue, 3: Blister */
    int32_t verdict,
    float asymmetry_metric,
    vs_thermal_tooling_advice* out_advice);

/*
 * Convert thermal data to a 24-bit BGR pseudo-color visualization (Ironbow, Hot Iron, Rainbow).
 * dst: output buffer of size width x height x 3 (stride dst_stride)
 */
VS_THERM_API int32_t vs_thermal_apply_palette(
    const void* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t format, /* vs_thermal_format */
    float t_min_c,
    float t_max_c,
    int32_t palette, /* vs_thermal_palette */
    uint8_t* dst,
    int32_t dst_stride);

#ifdef __cplusplus
}
#endif

#endif /* VS_THERMAL_H */
