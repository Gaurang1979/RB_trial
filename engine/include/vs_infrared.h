/*
 * vs_infrared.h - C API of the Infrared Vision Inspection Engine (Tool 9, No-IR-Filter CMOS & SWIR/NIR).
 *
 * Industrial Applications:
 *   1. See-Through Fill Level & Meniscus Peak Detection:
 *      Inspects through amber glass and dark PET bottles with 850/940 nm NIR backlighting.
 *      Filters out surface foam to extract true liquid-gas meniscus.
 *   2. Metameric Security Ink & Metamerism Verification:
 *      Dual-spectrum (VIS vs NIR) comparison to verify anti-counterfeit carbon-black ink
 *      and read barcodes hidden under opaque decorative colored dyes.
 *   3. Moisture Barrier Breach & Seam Liquid Ingress:
 *      Inspects flexible pouch seams and blister flanges for trapped liquid drops via SWIR (1450 nm) water absorption.
 *   4. Sub-Surface Micro-Crack & Solar Cell Electroluminescence (EL) Fracture Extractor:
 *      Multi-scale Hessian eigenvalue filtering to isolate dendritic micro-cracks in silicon wafers and PV cells.
 *
 * Thread Safety: Thread-safe, reentrant, zero heap allocations on the inspection loop.
 * Line Safety Rule: Fail-safe to NG on detected leak, crack, or underfill.
 */
#ifndef VS_INFRARED_H
#define VS_INFRARED_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef VS_INFRARED_BUILD
#    define VS_IR_API __declspec(dllexport)
#  else
#    define VS_IR_API __declspec(dllimport)
#  endif
#else
#  define VS_IR_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* -------------------------------------------------------------------------- */
/* 1. See-Through Liquid Fill Level & Foam Rejection                          */
/* -------------------------------------------------------------------------- */

typedef struct vs_ir_fill_params {
    int32_t struct_size;          /* sizeof(vs_ir_fill_params) */
    int32_t roi_x;                /* Inspection region covering neck & liquid top */
    int32_t roi_y;
    int32_t roi_w;
    int32_t roi_h;
    float   datum_y;              /* Reference datum line (e.g. bottle mouth or shoulder in image Y) */
    float   px_per_mm;            /* Spatial calibration factor (pixels per millimeter) */
    float   target_fill_mm;       /* Nominal target fill height from datum (mm) */
    float   tol_underfill_mm;     /* Maximum allowed underfill drop (mm, positive) */
    float   tol_overfill_mm;      /* Maximum allowed overfill rise (mm, positive) */
    float   min_meniscus_contrast;/* Minimum edge derivative contrast (default: 15.0f) */
    int32_t ignore_foam;          /* 1 = use curvature derivative to reject foam layer (default: 1) */
} vs_ir_fill_params;

typedef struct vs_ir_fill_result {
    int32_t struct_size;          /* sizeof(vs_ir_fill_result) */
    float   meniscus_y_px;        /* Detected liquid meniscus vertical coordinate (sub-pixel) */
    float   fill_height_mm;       /* Measured fill height from datum (mm) */
    float   meniscus_contrast;    /* Peak edge derivative contrast across meniscus */
    float   foam_thickness_mm;    /* Estimated foam thickness above liquid (mm) */
    int32_t is_underfill;         /* 1 if fill_height_mm < target - tol_underfill */
    int32_t is_overfill;          /* 1 if fill_height_mm > target + tol_overfill */
    int32_t is_ok;                /* 1 if within tolerance, 0 if NG */
} vs_ir_fill_result;

VS_IR_API void vs_ir_default_fill_params(vs_ir_fill_params* p);

VS_IR_API int32_t vs_ir_measure_fill_level(
    const uint8_t* nir_image,
    int32_t width,
    int32_t height,
    int32_t stride,
    const vs_ir_fill_params* params,
    vs_ir_fill_result* result);

/* -------------------------------------------------------------------------- */
/* 2. Metameric Normalized Difference Ratio (NDMI) & Security Inks            */
/* -------------------------------------------------------------------------- */

typedef struct vs_ir_metameric_params {
    int32_t struct_size;          /* sizeof(vs_ir_metameric_params) */
    float   dye_ndmi_thresh;      /* NDMI threshold where dye becomes transparent (default: 0.25f) */
    float   carbon_ndmi_max;      /* Maximum NDMI for genuine carbon black ink (default: 0.15f) */
    float   min_security_area_px; /* Minimum required pixels of verified security ink */
} vs_ir_metameric_params;

typedef struct vs_ir_metameric_result {
    int32_t struct_size;          /* sizeof(vs_ir_metameric_result) */
    int32_t verified_ink_pixels;  /* Count of verified carbon-black security ink pixels */
    int32_t transparent_dye_pixels;/* Count of organic dye pixels that disappeared in NIR */
    float   mean_security_ndmi;   /* Mean NDMI over security ink region */
    int32_t is_authentic;         /* 1 = authentic security ink verified, 0 = counterfeit or missing */
} vs_ir_metameric_result;

VS_IR_API void vs_ir_default_metameric_params(vs_ir_metameric_params* p);

/*
 * Compares Visible (VIS) and Near-Infrared (NIR) frames of the same package.
 * ndmi_out: optional 8-bit output normalized difference map: (NDMI + 1.0) * 127.5 (can be NULL)
 * mask_out: optional binary mask (255 = verified carbon security ink, 0 = background/dye) (can be NULL)
 */
VS_IR_API int32_t vs_ir_verify_metameric(
    const uint8_t* vis_image,
    int32_t vis_stride,
    const uint8_t* nir_image,
    int32_t nir_stride,
    int32_t width,
    int32_t height,
    const vs_ir_metameric_params* params,
    uint8_t* ndmi_out,
    int32_t ndmi_stride,
    uint8_t* mask_out,
    int32_t mask_stride,
    vs_ir_metameric_result* result);

/* -------------------------------------------------------------------------- */
/* 3. Moisture Barrier Breach & Seam Liquid Ingress (SWIR 1450nm)             */
/* -------------------------------------------------------------------------- */

typedef struct vs_ir_moisture_params {
    int32_t struct_size;          /* sizeof(vs_ir_moisture_params) */
    float   absorption_thresh;    /* SWIR water absorption contrast threshold (default: 40.0f) */
    float   min_nominal_barrier_px;/* Nominal design barrier width of heat seal (pixels) */
    float   min_safe_barrier_px;  /* Minimum allowed continuous unbreached barrier width (pixels) */
} vs_ir_moisture_params;

typedef struct vs_ir_moisture_result {
    int32_t struct_size;          /* sizeof(vs_ir_moisture_result) */
    float   min_remaining_barrier_px;/* Minimum intact seal barrier remaining across seam */
    int32_t moisture_pixel_count; /* Total pixels of trapped liquid in seal */
    int32_t has_complete_breach;  /* 1 if trapped liquid creates a continuous leak channel across flange */
    int32_t is_seal_ok;           /* 1 if min_remaining_barrier_px >= min_safe_barrier_px */
} vs_ir_moisture_result;

VS_IR_API void vs_ir_default_moisture_params(vs_ir_moisture_params* p);

VS_IR_API int32_t vs_ir_detect_moisture_seam(
    const uint8_t* swir_image,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t seam_roi_x,
    int32_t seam_roi_y,
    int32_t seam_roi_w,
    int32_t seam_roi_h,
    const vs_ir_moisture_params* params,
    uint8_t* mask_out,
    int32_t mask_stride,
    vs_ir_moisture_result* result);

/* -------------------------------------------------------------------------- */
/* 4. Solar Cell Electroluminescence (EL) & Silicon Micro-Crack Extractor     */
/* -------------------------------------------------------------------------- */

typedef struct vs_ir_crack_params {
    int32_t struct_size;          /* sizeof(vs_ir_crack_params) */
    float   hessian_sigma;        /* Gaussian scale for crack width (default: 1.5f) */
    float   crack_response_thresh;/* Minimum dark curvilinear eigenvalue response (default: 12.0f) */
    int32_t min_crack_length_px;  /* Minimum connected crack length to flag defect (default: 8) */
} vs_ir_crack_params;

typedef struct vs_ir_crack_result {
    int32_t struct_size;          /* sizeof(vs_ir_crack_result) */
    int32_t crack_pixel_count;    /* Total count of micro-crack fracture pixels */
    float   max_crack_contrast;   /* Peak dark response along fracture */
    int32_t is_cracked;           /* 1 = cracked / broken busbar (NG), 0 = clean silicon */
} vs_ir_crack_result;

VS_IR_API void vs_ir_default_crack_params(vs_ir_crack_params* p);

VS_IR_API int32_t vs_ir_extract_cracks(
    const uint8_t* el_image,
    int32_t width,
    int32_t height,
    int32_t stride,
    const vs_ir_crack_params* params,
    uint8_t* crack_mask_out,
    int32_t mask_stride,
    vs_ir_crack_result* result);

#ifdef __cplusplus
}
#endif

#endif /* VS_INFRARED_H */
