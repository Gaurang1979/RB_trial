/*
 * codereader.h - C API of the aggregation code reader engine.
 *
 * One reader handle = one job (for example "child codes on the 20 MP top
 * camera" or "parent code on the 2 MP side camera"). Create it once with the
 * operator's settings, then call cr_decode for every image. The API is plain C
 * so the .NET wrapper (dotnet/CodeReader) can call it with P/Invoke.
 *
 * Threading: one handle must not be used from two threads at once. Use one
 * handle per camera. cr_decode itself uses several threads internally.
 */
#ifndef CODEREADER_H
#define CODEREADER_H

#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
#  ifdef CODEREADER_BUILD
#    define CR_API __declspec(dllexport)
#  else
#    define CR_API __declspec(dllimport)
#  endif
#else
#  define CR_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Code types, as bit flags. A job usually reads one type; several may be combined (slower). A code
   of a type that is not enabled is never reported, even when its decoder shares a family with an
   enabled one (a Micro QR or rMQR in a QR job). A UPC-A symbol is the EAN-13 with a leading 0: an
   EAN-13 job reports it as EAN-13 (13 digits), a UPC-A job as UPC-A. Flags only of unknown types
   read nothing. */
enum cr_format {
    CR_QR = 1,
    CR_DATAMATRIX = 2,
    CR_GS1_128 = 4,         /* Code 128, including GS1-128 / SSCC on cases */
    CR_EAN13 = 8,           /* EAN-13 (and ISBN) */
    CR_EAN8 = 0x10,
    CR_UPCA = 0x20,
    CR_UPCE = 0x40,
    CR_CODE39 = 0x80,       /* standard and full ASCII (the text as printed: no Code 32 / PZN reading) */
    CR_CODE93 = 0x100,
    CR_ITF = 0x200,         /* Interleaved 2 of 5, ITF-14 */
    CR_CODABAR = 0x400,
    CR_DATABAR = 0x800,     /* GS1 DataBar: omnidirectional, truncated, stacked, limited, expanded */
    CR_MICRO_QR = 0x1000,   /* M1-M4 */
    CR_PDF417 = 0x2000,     /* PDF417, compact and MicroPDF417 */
    CR_AZTEC = 0x4000,      /* compact and full-range */
    /* Pharmacode (Laetus one-track): 2-16 bars, values 3-131070. It has no check digit: a read is
       reported only with a bar-count lock (min_length and max_length both set: they count bars for this type) and a
       clear quiet zone on both sides. The job states the reading direction (cr_params.pharma_direction):
       bars upright, read across the image (the default), or bars lying flat, read down it, each within
       75 degrees. Standard reading across: the bar on the right of the image weighs 1 (narrow) or 2
       (wide), each bar to its left twice as much. _REV reads the same symbol the other way (the bar
       on the left weighs least), for a code that passes the camera turned round. Read down, the
       bottom bar weighs least (standard) or the top one (_REV). With both flags set, the standard
       reading is reported. Bars alone cannot tell a code of
       one bar width under heavy ink spread from its all-wide twin, nor a pack turned round: check
       the value read against the expected one. */
    CR_PHARMACODE = 0x8000,
    CR_PHARMACODE_REV = 0x10000,
    CR_DOTCODE = 0x20000,   /* AIM DotCode (ISS Rev 4.0), GS1 and plain data; Reed-Solomon checked */
    CR_RMQR = 0x40000,      /* rectangular Micro QR (ISO/IEC 23941), read by zxing-cpp; not graded yet */
    CR_ALL_LINEAR = 0x0FFC, /* every 1D type above (GS1-128 to DataBar); Pharmacode has rules of its own */
    CR_ANY = 0x7FFFF        /* testing and teaching only: slower, reads every type. An older caller's
                               "any" (0xFFFF, 0x3FFFF) is read as this */
};

/* Check digit of Code 39 (modulo 43), ITF (GS1 modulo 10) and Codabar (modulo 16, the character before
   the stop character); other types check their own or have none. */
enum cr_check {
    CR_CHECK_AS_READ = 0,   /* no extra check: the text as printed */
    CR_CHECK_VERIFY = 1,    /* the last character must be a correct check digit, else the read is dropped */
    CR_CHECK_STRIP = 2      /* as VERIFY, and the check digit is removed from the text */
};

/* Which way a Pharmacode's bars are read (cr_params.pharma_direction). It has no start or stop, so
   the other direction reads another valid value: the job states the direction, the engine never
   guesses it, and a code lying the other way is not read. */
enum cr_pharma_direction {
    CR_PHARMA_ACROSS = 0,   /* bars upright: read across the image (standard: the right bar weighs least) */
    CR_PHARMA_DOWN = 1      /* bars lying flat, the code turned a quarter turn: read down the image (standard:
                               the bottom bar weighs least, a code turned clockwise; _REV the top bar) */
};

/* How code regions are found. */
enum cr_locator {
    CR_LOCATE_AUTO = 0,        /* AI then classical when a model is loaded, else classical */
    CR_LOCATE_CLASSICAL = 1,   /* texture density only */
    CR_LOCATE_AI = 2,          /* AI only (fastest; needs a model) */
    CR_LOCATE_AI_CLASSICAL = 3 /* AI regions first, classical regions as fallback */
};

/* Where the AI enhancer (cr_load_enhancer) runs. The tuning agent picks this per product. */
enum cr_enhance {
    CR_ENHANCE_LAST = 0,        /* per region, after the cheap fixes failed */
    CR_ENHANCE_EARLY = 1,       /* per region, right after the plain attempt (dark, blurred lines) */
    CR_ENHANCE_SECOND_PASS = 2, /* only after every region had the cheap fixes (mostly easy lines) */
    CR_ENHANCE_OFF = 3
};

/* Which processing step read a code. Fed back to the learning service. */
enum cr_step {
    CR_STEP_PLAIN = 0,     /* crop with brightness stretch */
    CR_STEP_UPSCALE = 1,   /* crop upscaled 2x */
    CR_STEP_CLAHE = 2,     /* local contrast */
    CR_STEP_SHARPEN = 3,   /* unsharp mask on upscaled crop */
    CR_STEP_BINARY = 4,    /* adaptive threshold */
    CR_STEP_AI_ENHANCE = 5, /* AI enhancer (cr_load_enhancer), last resort per region */
    CR_STEP_FLAT = 6,      /* lighting flattened (divide by local paper level), upscaled 2x */
    CR_STEP_FLAT_ADAPT = 7, /* flattened, upscaled, local adaptive threshold */
    CR_STEP_DMTX = 8,      /* libdmtx on the deblurred region (Data Matrix, AI regions only) */
    CR_STEP_DEBLUR = 12,   /* Richardson-Lucy deblur, sigma 1.5 (defocus) */
    CR_STEP_DEBLUR_STRONG = 13, /* Richardson-Lucy deblur, sigma 2.5 */
    CR_STEP_UPSCALE4 = 14, /* Lanczos 4x: very small modules (about 2-3 px) */
    CR_STEP_GRIDFIT = 15,  /* model-based QR: known grid on the finder patterns, module averaging */
    CR_STEP_LATTICE = 17,  /* Data Matrix read inside-out: grid from the module texture, no finder needed */
    CR_STEP_MATCH = 16,    /* faint QR matched to a gap in the case's serial run (cr_params.match_serials) */
    CR_STEP_KNOWN = 18,    /* QR read with the content it shares with the case's codes already read (all but the serial) */
    CR_STEP_GLARE = 19,    /* glare corrected: each small area stretched between its own darkest and brightest level */
    CR_STEP_FRAMES = 20,   /* QR read from several frames of the case fused (cr_decode_frames) */
    CR_STEP_LINEAR = 21,   /* 1D code at any angle: scan lines along its bars fused, read by zxing */
    CR_STEP_LINEAR_SSCC = 22, /* SSCC whose bar edges are gone: digits fitted through the blur, confirmed by both check digits */
    CR_STEP_DOTS = 23,     /* dot-printed modules (inkjet on caps), voids and specks: dots melted into solid modules (cell filters) */
    CR_STEP_PHARMACODE = 24, /* Pharmacode: bar widths on scan lines across its bars (pharmacode.cpp) */
    CR_STEP_DOTCODE = 25,  /* DotCode: dot lattice fitted, dots sampled, Reed-Solomon corrected (dotcode.cpp) */
    CR_STEP_FULL = 10,     /* whole-image pass (fallback) */
    CR_STEP_FULL_BRIGHT = 11
};

/*
 * Structs that grow. New fields only ever go at the end of cr_params, cr_code_ext and cr_scan, and
 * each starts with struct_size, so a program built against an older header keeps working with a
 * newer engine and the other way round: the engine reads (cr_params) or writes (cr_code_ext,
 * cr_scan) only the bytes the caller's struct_size covers, and gives the fields it covers not, or
 * does not know, their defaults. cr_code, cr_quality, cr_miss_info, cr_region and cr_stats are
 * fixed: what a code gains goes into cr_code_ext.
 *
 * cr_params.struct_size carries CR_PARAMS_TAG in its upper half, so a struct from before the field
 * existed (whose first field was the format, at most 0x3FFFF) is refused, never read as settings.
 */
#define CR_PARAMS_TAG 0x43520000 /* "CR" */
#define CR_PARAMS_SIZE ((int32_t)(CR_PARAMS_TAG | sizeof(cr_params)))

/* Job settings. Call cr_default_params first, then change what you need. */
typedef struct cr_params {
    int32_t struct_size;      /* CR_PARAMS_SIZE, set by cr_default_params; keep it */
    int32_t format;           /* cr_format flags: the types to read */
    int32_t expected_count;   /* codes per case from the operator; 0 = unknown */
    int32_t time_budget_ms;   /* stop and report after this long (default 200) */
    int32_t threads;          /* 0 = all CPU cores */
    int32_t locator_scale;    /* image shrink factor for locating codes (default 4) */
    int32_t max_upscale;      /* largest crop upscale tried, 1..3 (default 2) */
    float   gamma;            /* brightness curve for whole-image pass, 0.3..1.5 (default 0.5) */
    float   locator_density;  /* texture density threshold, 0.05..0.4 (default 0.12) */
    int32_t full_pass;        /* 1 = run whole-image passes when short (default 1) */
    int32_t roi_x, roi_y, roi_w, roi_h; /* region of interest; roi_w = 0 means whole image */
    int32_t locator_mode;     /* cr_locator; AI modes need cr_load_model (default CR_LOCATE_AUTO) */
    float   ai_threshold;     /* AI locator probability threshold, 0.05..0.95 (default 0.4) */
    int32_t enhancer_mode;    /* cr_enhance: where the AI enhancer runs (default CR_ENHANCE_LAST) */
    int32_t grade_quality;    /* 1 = grade every decoded code (cr_get_quality); about 1 ms per code */
    int32_t match_serials;    /* 1 = a QR too faint to decode may be matched to a gap in the serial run of
                                 the codes read in this case (CR_STEP_MATCH); 0 = off */
    /* What calibration measured about the product's codes, so a new reader starts where a learned
       one would (its first case reads like the later ones). 0 = unknown. */
    int32_t hint_modules;     /* modules across one code: QR 21..177 (21 + 4 x version), Data Matrix 10..26 */
    float   hint_code_side_px;    /* one code's side in image pixels */
    float   hint_symbols_per_code;/* symbols the finder marks per code, e.g. 2 when a label carries its code twice */
    /* 1D rules (CR_ALL_LINEAR types only; 2D codes carry their own length and error correction). A
       read that breaks a rule is dropped. Types without a fixed length (ITF above all, whose partial
       reads are valid shorter codes) are safe from partial reads only with a length lock or a check
       digit: Vision Studio refuses an ITF tool with neither. */
    int32_t min_length;       /* fewest characters as printed (check digit included); not for EAN/UPC; 0 = any.
                                 Pharmacode: fewest bars (2-16); a Pharmacode job needs both min_length and max_length */
    int32_t max_length;       /* most characters as printed (Pharmacode: most bars); 0 = any */
    int32_t check_digit;      /* cr_check for Code 39, ITF and Codabar (default CR_CHECK_AS_READ) */
    int32_t pharma_direction; /* cr_pharma_direction (default CR_PHARMA_ACROSS); any other value reads no Pharmacode */
} cr_params;

/*
 * Print / image quality of one decoded code, after ISO/IEC 15415 and 29158 (process control:
 * camera grey levels, not calibrated reflectance). Grades: 4 = A ... 0 = F, -1 = not measured.
 */
typedef struct cr_quality {
    int32_t measured;
    float symbol_contrast;          /* %, (light - dark) / light */
    float modulation;               /* 0..1 */
    float reflectance_margin;       /* 0..1 */
    float fixed_pattern_damage;     /* share of finder / timing / L / clock modules wrong; -1 DotCode (has none) */
    float axial_nonuniformity;
    float grid_nonuniformity;       /* modules; -1 not measured */
    float unused_error_correction;  /* 0..1 (the decoder's, after ISO 15415; M1 its check codewords; else approximated); -1 not measured */
    float print_growth;
    float quiet_zone_dark;          /* share of dark samples in the quiet zone; -1 not measured */
    float module_size_px;
    float lighting_uniformity;      /* 1 = even light over the code */
    float saturated;                /* share of modules in glare */
    int32_t grade_sc, grade_mod, grade_rm, grade_fpd, grade_an, grade_gn, grade_uec, grade_qz;
    int32_t overall;
    /* with a calibration (cr_set_flat_field, cr_set_reflectance, cr_set_lens) */
    int32_t calibrated;             /* 1 = reflectance-calibrated: contrasts are reflectance %, as a verifier's */
    float   x_dim_mm;               /* module (X-dimension) in mm; 0 = no scale set */
    /* 1D codes, after ISO/IEC 15416: means over 10 scan lines (symbol_contrast and modulation above too).
       Pharmacode is graded here too (linear = 1), its own decoder the reference decode; DotCode as a
       matrix code on its dot grid, with no fixed pattern (grade_fpd -1) */
    int32_t linear;                 /* 1 = graded as a 1D symbol */
    float   edge_contrast_min;      /* ECmin, % */
    float   defects;                /* ERNmax / SC */
    float   decodability;           /* reference decode margin of its type, 0..1 (ISO 15416); -1 not measured */
    float   r_min, r_max;           /* % */
    int32_t scans, decoded_scans;
    int32_t grade_ec, grade_defects, grade_decodability;
} cr_quality;

/*
 * Verifier-style calibration of the grades (optional; without it grades are camera-relative):
 * cr_set_flat_field: a picture of a plain white card under the line's lighting, at the image size
 *   decoded later (NULL clears); -1 when it is not a white card under working light.
 * cr_set_reflectance: the grey levels the camera reads on a card's dark and light patches (after
 *   the flat field) and their certified reflectance in % (refl_light <= 0 clears).
 * cr_set_lens: lens distortion and mm per pixel at the case's top; cr_calibrate_chessboard measures
 *   them from pictures of a chessboard lying there (cols x rows inner corners, square side in mm):
 *   returns the views used, -1 none found, -2 built without OpenCV's calib module, -3 three or more
 *   views whose lens model reprojects worse than 0.5 px (take the views again; nothing is written).
 *   Fewer than three views give the scale only (rms_px -1).
 *   cr_set_lens returns -1 and clears the lens (no distortion model, no scale) for a distortion model
 *   (fx not 0) whose rms_px is not 0..0.5 px, so a stored poor model is never used (cr_last_error).
 */
typedef struct cr_lens {
    float fx, fy, cx, cy, k1, k2, p1, p2, k3; /* fx 0 = no distortion model */
    float mm_per_px;                          /* 0 = unknown */
    float rms_px;                             /* calibration reprojection error; -1 = scale only (fewer than 3 views) */
} cr_lens;

/* One decoded code. Strings stay valid until the next cr_decode on the handle. */
typedef struct cr_code {
    const char* text;      /* decoded content; GS1 FNC1 sent as ASCII 29 (GS) */
    int32_t format;        /* cr_format value */
    int32_t step;          /* cr_step that read it */
    float   x[4], y[4];    /* corners in full-image pixels */
    int32_t copies;        /* same text seen this many times in the image */
} cr_code;

/*
 * More about one decoded code (cr_get_code_ext; same index as cr_get_code). Set struct_size to
 * sizeof(cr_code_ext) before the call.
 */
typedef struct cr_code_ext {
    int32_t struct_size;
    char    symbology_id[4];  /* AIM symbology identifier, NUL-terminated: "]Q3" QR with FNC1 first
                                 (GS1), "]d2" GS1 Data Matrix, "]C1" GS1-128, "]e0" DataBar ...;
                                 "" = not known (a code read by one of the engine's own decoders
                                 rather than zxing: lattice, grid fit, known content, frames) */
    int32_t gs1;              /* 1 = GS1 symbol (FNC1 first, or DataBar), 0 = not, -1 = not known */
    /* 1D grades (cr_quality.linear): ISO/IEC 15416 grades each scan line on Rmin and its quiet zones
       pass / fail; here as the symbol's grade, the mean of its scans' grades as for the overall
       grade (cr_quality.grade_qz carries the quiet zones). -1 = not graded. */
    int32_t grade_rmin;
    int32_t scan_count;       /* scan lines graded (cr_get_scan), 0 = none */
    /* Code Reader spec 5.2 outputs (added 2026-10-30) */
    float   angle;            /* reading direction, degrees clockwise from left-to-right (0..360; 180 = upside
                                 down): from corner 0 to corner 1, for every type */
    int32_t check_digit_ok;   /* 1 = check digit verified (EAN/UPC, Code 128, Code 93, DataBar always; Code 39,
                                 ITF and Codabar when check_digit is VERIFY or STRIP), -1 = not checked */
    int32_t quiet_zone_ok;    /* 1D except DataBar: 1 = clear for the type's quiet zone (10 X; EAN-13 11 X before
                                 and 7 X after, EAN-8 7 X, UPC-A 9 X, UPC-E 9 X / 7 X, 5 X after an add-on),
                                 0 = a mark in it or it runs off the image; -1 = not checked (2D, DataBar,
                                 too small to measure) */
    const char* addon;        /* EAN/UPC 2- or 5-digit add-on ("" = none; cr_code.text holds the main symbol
                                 only); valid until the next cr_decode */
    /* 2D grades (added 2026-10-30). PDF417 is graded in the 1D fields of cr_quality (linear = 1) after
       ISO/IEC 15415's multi-row method; its codeword yield is here: codewords validly decoded / codewords
       the row scans crossed (0..1), graded A >= 0.71, B >= 0.64, C >= 0.57, D >= 0.50 and counted in
       cr_quality.overall. -1 = not measured (every type but PDF417). */
    float   codeword_yield;
    int32_t grade_codeword_yield;
    /* the module grid the code was graded on (QR, Micro QR, Data Matrix, Aztec, DotCode: its dot grid): modules down and
       across, between cr_code's corners (the symbol's outer corners); 0 = not known (1D, PDF417, a
       code the engine's own decoders read without a grid) */
    int32_t grid_rows, grid_cols;
} cr_code_ext;

/* One 1D scan line of a graded code (cr_get_scan), after ISO/IEC 15416. Reflectances in % (camera
   grey / 255 without a reflectance calibration). Set struct_size to sizeof(cr_scan) before the call. */
typedef struct cr_scan {
    int32_t struct_size;
    float   r_min, r_max, symbol_contrast, edge_contrast_min, modulation, defects;
    float   decodability;     /* -1 = not measured */
    int32_t decoded;          /* 1 = this scan line alone decodes to the code's text */
    int32_t quiet_zone_ok;    /* 1 = both quiet zones wide and light enough */
    int32_t grade_rmin;       /* 4 = Rmin <= 0.5 Rmax, else 0 */
    int32_t grade_quiet_zone; /* 4 or 0 */
    int32_t grade;            /* the scan's grade: its worst parameter, 4 = A .. 0 = F */
} cr_scan;

/*
 * Direct part mark grading after ISO/IEC TR 29158 (AIM DPM), for Data Matrix and QR marked on the
 * part (dot peen, laser, ink jet). Off until cr_set_dpm enables it; then every decoded Data Matrix
 * and QR is graded (cr_get_dpm), as well as any cr_quality grading. Grades 4 = A .. 0 = F, -1 =
 * not measured. Process control from camera grey levels, like cr_quality.
 *
 * Image-based reflectance calibration (Minimum reflectance): image a calibration symbol of known
 * light reflectance r_cal (%) with grading on and the exposure set so its mean_light_pct is 70..86;
 * keep its mean_light as ml_cal and the system response (exposure x gain, any unit) as sr_cal. For
 * each part give sr_target, the response the part was imaged at (0 = the same as sr_cal). Without
 * r_cal, ml_cal and sr_cal Minimum reflectance is not measured.
 */
typedef enum cr_dpm_lighting {
    CR_DPM_LIGHT_90 = 0,  /* diffuse on-axis ("90") */
    CR_DPM_LIGHT_30Q = 1, /* 30 degrees from four sides */
    CR_DPM_LIGHT_30T = 2, /* 30 degrees from two sides */
    CR_DPM_LIGHT_30S = 3, /* 30 degrees from one side */
    CR_DPM_LIGHT_D = 4    /* diffuse off-axis (dome) */
} cr_dpm_lighting;

/* DPM settings (cr_default_dpm_params, then change what you need). Grows at its end. */
typedef struct cr_dpm_params {
    int32_t struct_size;   /* sizeof(cr_dpm_params), set by cr_default_dpm_params */
    int32_t enabled;       /* 1 = grade Data Matrix and QR after ISO/IEC TR 29158 */
    int32_t lighting;      /* cr_dpm_lighting, recorded with the grade */
    int32_t aperture;      /* synthetic aperture, % of the cell: 80 (default), 50, or 0 = the better of the two */
    int32_t wavelength_nm; /* recorded with the grade; 0 = not recorded */
    int32_t closing;       /* dot peen: gaps between dots closed before grading; 0 = sized from the widest
                              gap along a solid fixed-pattern row (none on solid marks), -1 = off, > 0 = disc px */
    float   r_cal, ml_cal, sr_cal; /* reflectance calibration (above); 0 = none */
    float   sr_target;     /* the part's system response; 0 = sr_cal */
} cr_dpm_params;

/* The DPM grade of one decoded code (cr_get_dpm; same index as cr_get_code). Grows at its end. */
typedef struct cr_dpm {
    int32_t struct_size;
    int32_t measured;            /* 0 = not graded (not Data Matrix / QR, partly outside, cells under 1.5 px) */
    int32_t calibrated;          /* 1 = minimum reflectance measured (reflectance calibration set) */
    int32_t inverted;            /* 1 = light marks on a dark part */
    int32_t lighting;            /* cr_dpm_lighting as set */
    int32_t aperture;            /* % of the cell the grades below come from (50 or 80) */
    float   cell_px;             /* cell size in pixels */
    float   closing_px;          /* dot-peen closing disc used; 0 = none */
    float   mean_light, mean_dark; /* ML, MD: mean grey of the light and dark cells */
    float   mean_light_pct;      /* ML as % of full scale; ISO/IEC TR 29158 wants 70..86 */
    int32_t mean_light_ok;       /* 1 = ML within 70..86 %: else adjust the exposure and grade again */
    float   cell_contrast;       /* (ML - MD) / ML, 0..1 */
    float   cell_modulation;     /* 5th percentile of the cells' modulation, 0..1 */
    float   min_reflectance;     /* %, -1 = not measured */
    float   fixed_pattern_damage;/* share of fixed-pattern cells on the wrong side */
    float   distributed_damage;  /* mean fixed-pattern segment grade, 0..4 */
    float   axial_nonuniformity, grid_nonuniformity, unused_error_correction; /* as cr_quality */
    int32_t grade_decode, grade_cc, grade_cm, grade_mr, grade_fpd, grade_an, grade_gn, grade_uec;
    int32_t overall;             /* lowest of the grades above, at `aperture` */
    int32_t overall_50, overall_80; /* the overall grade at each aperture */
} cr_dpm;

/*
 * A code that did not decode (short cases only): one region per likely unread code, most likely
 * first. Regions beside a read code (its label flag or second copy), the same region twice and,
 * once enough code-sized regions are listed, the rest of the background are left out.
 */
typedef struct cr_miss {
    int32_t x, y, w, h;
} cr_miss;

/*
 * Why a region did not decode (cr_get_miss_info), after the expired verifier patents US9552506 /
 * US8027802: measured on the image after decoding, only for misses of short cases. The reason is
 * the first of these that holds, in this order.
 */
enum cr_miss_reason {
    CR_MISS_UNKNOWN = 0,      /* nothing measured stands out (or not diagnosed: time cap) */
    CR_MISS_CUT_OFF = 1,      /* the code runs off the image edge */
    CR_MISS_GLARE = 2,        /* too much of the code is saturated (250+) */
    CR_MISS_LOW_CONTRAST = 3, /* light and dark too close: light level, print, wrong exposure */
    CR_MISS_COVERED = 4,      /* no code texture where the finder saw a code: covered, folded, blank */
    CR_MISS_TOO_SMALL = 5,    /* under about 1.8 px per module: camera too far or lens too wide */
    CR_MISS_BLUR = 6,         /* edges soft against the contrast: focus, motion, bent label */
    CR_MISS_NO_FINDER = 7     /* code texture, but no QR finder patterns / Data Matrix L */
};

/* The diagnosis of one miss (same index as cr_get_miss). */
typedef struct cr_miss_info {
    int32_t x, y, w, h;       /* same box as cr_get_miss */
    int32_t reason;           /* cr_miss_reason */
    int32_t measured;         /* 1 = diagnosed; 0 = the time cap ran out first (reason UNKNOWN) */
    float   sharpness;        /* 0..1: mean of the strongest 10% edges / (light - dark); sharp print ~0.5+ */
    float   contrast;         /* light - dark, grey levels */
    float   light, dark;      /* means of the brightest / darkest 10% of the code area */
    float   saturated;        /* share of the code area at 250+ (glare) */
    float   texture;          /* share of the code area on an edge (a code ~0.2-0.6, blank ~0) */
    float   module_px;        /* estimated module size here, pixels; 0 = unknown */
    float   learned_module_px;/* the product's module size (decoded codes, learned or calibrated); 0 = unknown */
    int32_t finder;           /* 1 = finder found (QR: three finder patterns, DM: the L), 0 = not, -1 = not checked */
    int32_t quiet_zone;       /* 1 = clear, 0 = dark marks in it, -1 = not checked */
    float   ref_sharpness;    /* the same measures on this image's read codes, for comparison; 0 = none read */
    float   ref_contrast;
    int32_t slot_row, slot_col; /* the case's slot (rows down, columns across, from 1 at the top left)
                                   when its codes sit on a lattice; 0 = no slot map */
} cr_miss_info;

/* A region the finder proposed (live view: yellow while searching, then green / red). */
typedef struct cr_region {
    int32_t x, y, w, h;
    int32_t from_ai; /* 1 = AI finder, 0 = classical fallback */
    int32_t read;    /* 1 = a code was decoded in it */
} cr_region;

/* Timings and counts for the whole image. */
typedef struct cr_stats {
    int32_t codes;          /* unique codes decoded */
    int32_t expected;       /* copy of expected_count */
    int32_t candidates;     /* regions the locator proposed */
    int32_t misses;         /* candidate regions left unread */
    float   locate_ms;
    float   decode_ms;
    float   total_ms;
    int32_t timed_out;      /* 1 = time budget ran out before all were read */
    float   label_brightness; /* 0..255, brightness of the brightest 5% (labels) */
    /* 1D reads dropped by the job's rules (each decoder attempt counts; > 0 on a short image says why) */
} cr_stats;

/* More counts for the whole image (cr_get_stats_ext). Set struct_size to sizeof(cr_stats_ext). */
typedef struct cr_stats_ext {
    int32_t struct_size;
    /* 1D reads dropped by the job's rules (each decoder attempt counts; > 0 on a short image says why) */
    int32_t rejected_length;      /* outside min_length..max_length: most often a partial read */
    int32_t rejected_check_digit; /* a wrong check digit (check_digit VERIFY or STRIP) */
    /* An extra pack in the case (more codes than expected is NG, line safety). With the expected
       count read, the code-sized regions left are decoded as well, so an extra pack's code is read
       also when the count was reached before it. */
    int32_t copies_over;          /* codes seen more times in the image than the product's label carries
                                     (hint_symbols_per_code, else learned): an extra pack carrying a copy
                                     of a code already in the case. > 0 = NG */
    int32_t extra_unchecked;      /* code-sized regions left undecoded after the expected count was read
                                     (the time budget ran out): an extra pack there would not be seen */
    /* Reads the engine's own checks did not confirm on the image (line safety: a wrong number is worse
       than none). Each decoder attempt counts; > 0 on a short image says why a code was not reported. */
    int32_t refused_ean;          /* EAN / UPC reads the ISO/IEC 15420 reference decode did not read again: never reported */
    int32_t refused_addon;        /* EAN-2 / EAN-5 add-ons it did not read again after the main symbol: reported as none */
} cr_stats_ext;

typedef struct cr_reader cr_reader;

/* cr_default_params fills *p and its struct_size. cr_create returns NULL, and cr_set_params -1 (the
   reader keeps its settings), when p->struct_size is not a cr_params one (see CR_PARAMS_TAG). */
CR_API void        cr_default_params(cr_params* p);
CR_API cr_reader*  cr_create(const cr_params* p);
CR_API void        cr_destroy(cr_reader* r);
CR_API int32_t     cr_set_params(cr_reader* r, const cr_params* p);

/*
 * Load the AI code locator (ONNX file from ml/train.py). path is UTF-8.
 * Returns 0, or -1 when the file cannot be loaded (see cr_last_error).
 */
CR_API int32_t     cr_load_model(cr_reader* r, const char* path);

/*
 * Load the AI enhancer (ONNX file from ml/enhance.py). It is tried on a region only after
 * every other image fix failed to read it. Returns 0, or -1 (see cr_last_error).
 */
CR_API int32_t     cr_load_enhancer(cr_reader* r, const char* path);

/*
 * Load the same models from a buffer already in memory (the decrypted ONNX bytes), so the
 * plaintext model never has to exist as a file. The host decrypts the protected model
 * (AES-256-GCM, content key from the licence) into this buffer and passes it here; see
 * docs/vision-system/LICENSING.md section 7.4. `data` must hold `size` bytes of a complete
 * ONNX model. Returns 0, or -1 when the buffer is empty or not a loadable model (cr_last_error);
 * on failure no model is marked loaded, so the line stays NG rather than reading without AI.
 */
CR_API int32_t     cr_load_model_buffer(cr_reader* r, const uint8_t* data, size_t size);
CR_API int32_t     cr_load_enhancer_buffer(cr_reader* r, const uint8_t* data, size_t size);

/*
 * Called once per cr_decode when finding is done and decoding starts, with every region the
 * finder proposed (read = 0). The live view draws them in yellow while the codes decode.
 * Runs on the thread that called cr_decode; the array is valid only during the call.
 * fn = NULL turns it off.
 */
typedef void (*cr_found_fn)(void* user, const cr_region* regions, int32_t count);
CR_API void        cr_set_found_callback(cr_reader* r, cr_found_fn fn, void* user);

/*
 * Decode one 8-bit grey image. stride = bytes per row (>= width).
 * Returns the number of unique codes, or a negative error:
 *   -1 bad argument, -2 internal error, -3 tampering detected (see cr_last_error).
 * A negative return is a fault: the case is NG, never OK (line safety, requirements section 12). -3
 * means the runtime anti-tamper check tripped (a debugger/instrumentation or an in-memory code
 * patch); the state is latched, so every later decode returns -3 and the host should raise a tamper
 * alarm to the line (cr_integrity_state). Decode never crashes or hangs on a fault.
 */
/* Several frames of the same case (2-5, same size, still or slowly moving): every frame is read,
 * the reads are united, and a code unread in every frame is decoded from the frames fused
 * (module evidence summed over the frames; the frames are aligned by their global shift). The
 * result is read like cr_decode's. */
CR_API int32_t     cr_decode_frames(cr_reader* r, const uint8_t* const* pixels, int32_t frames,
                                    int32_t width, int32_t height, int32_t stride);
CR_API int32_t     cr_decode(cr_reader* r, const uint8_t* pixels,
                             int32_t width, int32_t height, int32_t stride);

CR_API int32_t     cr_code_count(const cr_reader* r);
CR_API int32_t     cr_get_code(const cr_reader* r, int32_t index, cr_code* out);
CR_API int32_t     cr_get_quality(const cr_reader* r, int32_t index, cr_quality* out);
/* 0, or -1 for a bad index or an out->struct_size under 4. */
CR_API int32_t     cr_get_code_ext(const cr_reader* r, int32_t index, cr_code_ext* out);
/* Scan line `scan` (0 .. cr_code_ext.scan_count - 1) of graded code `index`: 0, or -1. */
CR_API int32_t     cr_get_scan(const cr_reader* r, int32_t index, int32_t scan, cr_scan* out);
CR_API int32_t     cr_region_count(const cr_reader* r);
CR_API int32_t     cr_get_region(const cr_reader* r, int32_t index, cr_region* out);
CR_API int32_t     cr_miss_count(const cr_reader* r);
CR_API int32_t     cr_get_miss(const cr_reader* r, int32_t index, cr_miss* out);
/* Why miss `index` did not read; 0, or -1 for a bad index. Diagnosed after decoding within a
 * small time cap (about 20 ms per image), so the decode time is unchanged. */
CR_API int32_t     cr_get_miss_info(const cr_reader* r, int32_t index, cr_miss_info* out);
CR_API void        cr_get_stats(const cr_reader* r, cr_stats* out);
/* 0, or -1 for a bad argument (struct_size under 4) */
CR_API int32_t     cr_get_stats_ext(const cr_reader* r, cr_stats_ext* out);
/*
 * Where on the image this reader loses codes: the image is cut into 4 x 4 zones (row-major, top
 * left first); read[z] and unread[z] count the code regions found in zone z over all images this
 * reader decoded. Each array holds 16 values. Each zone also learns its own order of image fixes.
 */
CR_API void        cr_get_zone_stats(const cr_reader* r, int32_t read[16], int32_t unread[16]);
CR_API const char* cr_last_error(const cr_reader* r);
CR_API const char* cr_version(void);

/* Runtime anti-tamper (requirements section 12; owner 2026-11-01). The engine watches for tampering
 * after it is loaded - a debugger/instrumentation attached, or a critical code region patched in
 * memory - and latches a process-global state that never clears. While tampered, every cr_decode
 * returns CR_DECODE_TAMPER (NG, no codes); it never gives a silent wrong read, a crash or a hang.
 * These checks raise the cost of reverse engineering; they are not unbreakable, and they do not
 * detect a file patched at rest (that is the signed load-time check). */
enum cr_tamper {
    CR_TAMPER_NONE        = 0,
    CR_TAMPER_DEBUGGER    = 1, /* a debugger / tracer is attached to the process */
    CR_TAMPER_CODE_PATCH  = 2  /* a critical code region changed in memory after load */
};
/* cr_decode's return when tampering is latched. */
#define CR_DECODE_TAMPER (-3)

/* Run the runtime anti-tamper checks now and return the latched cr_tamper state (0 = clean). The
 * line software may poll this to raise a tamper alarm even when it is not decoding. Cheap; never
 * throws across the C boundary, never blocks. Process-wide, not per-reader: pass NULL or any reader. */
CR_API int32_t     cr_integrity_check(void);
/* The latched cr_tamper state without re-running the checks (cheaper, for a frequent alarm poll). */
CR_API int32_t     cr_integrity_state(void);
/*
 * What this reader has learned (step order per zone, zone read map, code sizes, pass costs, the
 * printer's encoding), as a text blob to save beside the product and hand to the next reader of
 * the same product, so a shift starts where the last one stopped. Learned state never crosses
 * products: save and load it under one product's key, and drop it when the product, its camera
 * settings or the engine version change.
 * cr_get_learned writes the blob (NUL-terminated) into buf when cap is large enough and returns
 * the size needed including the NUL (call with buf = NULL to size it); -1 on a null reader.
 * cr_set_learned replaces the reader's learned state from a blob; 0 on success, -1 with
 * cr_last_error set when the blob is not one of this engine's (then nothing changes). Step tables
 * are scaled down on load so old history never outweighs a few hundred cases; the zone read map
 * and case count start at zero. The loaded printer encoding is used as a hint until the first
 * decoded code that yields an encoding confirms or replaces it. Lines with keys the engine does
 * not know are ignored, so a caller may add its own (such as a product key) after the first line.
 */
CR_API int32_t     cr_set_flat_field(cr_reader* r, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride);
CR_API int32_t     cr_set_reflectance(cr_reader* r, float grey_dark, float grey_light, float refl_dark, float refl_light);
CR_API int32_t     cr_set_lens(cr_reader* r, const cr_lens* lens);
/* A reflectance card filling the middle of the picture (one dark and one light patch): the grey
 * levels of the two patches after the reader's flat field, for cr_set_reflectance. */
CR_API int32_t     cr_measure_card(cr_reader* r, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                                   float* grey_dark, float* grey_light);
CR_API int32_t     cr_calibrate_chessboard(const uint8_t* const* pixels, int32_t count, int32_t width, int32_t height, int32_t stride,
                                           int32_t cols, int32_t rows, float square_mm, cr_lens* out);
CR_API int32_t     cr_get_learned(const cr_reader* r, char* buf, int32_t cap);
CR_API int32_t     cr_set_learned(cr_reader* r, const char* text);
/* DPM grading (cr_dpm_params): cr_set_dpm returns 0, or -1 for a bad argument (struct_size under 8,
   aperture not 0/50/80, closing above 64, a calibration value negative or not finite) with cr_last_error set; cr_get_dpm 0, or -1 for a bad index or struct_size
   under 4 (grading off: measured 0). */
CR_API void        cr_default_dpm_params(cr_dpm_params* p);
CR_API int32_t     cr_set_dpm(cr_reader* r, const cr_dpm_params* p);
CR_API int32_t     cr_get_dpm(const cr_reader* r, int32_t index, cr_dpm* out);

#ifdef __cplusplus
}
#endif

#endif /* CODEREADER_H */
