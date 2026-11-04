#ifndef VS_AUTOTUNE_H
#define VS_AUTOTUNE_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(__CYGWIN__)
  #ifdef VS_AUTOTUNE_BUILD
    #define VS_AUTOTUNE_API __declspec(dllexport)
  #else
    #define VS_AUTOTUNE_API __declspec(dllimport)
  #endif
#else
  #define VS_AUTOTUNE_API __attribute__((visibility("default")))
#endif

#define VS_AUTOTUNE_OK 0
#define VS_AUTOTUNE_ERR_NULL_PTR -1
#define VS_AUTOTUNE_ERR_INVALID_DIM -2
#define VS_AUTOTUNE_ERR_INVALID_PARAM -3

// 9 Spatial Quadrants for MTF focus analysis
// 0: Top-Left,   1: Top-Center,    2: Top-Right
// 3: Mid-Left,   4: Mid-Center,    5: Mid-Right
// 6: Bot-Left,   7: Bot-Center,    8: Bot-Right
typedef struct {
    int struct_size;
    float zone_sharpness[9];
    float max_sharpness;
    float min_sharpness;
    float mean_sharpness;
    float uniformity_ratio;      // min / (max + 1e-5), 1.0 = perfectly flat, <0.60 = tilted
    int is_mount_tilted;         // 1 if tilted beyond threshold, 0 if OK
    char tilt_direction[64];     // e.g. "Top-Left Blur / Bottom-Right Sharp"
    char advice[128];            // e.g. "Adjust mount pitch: raise top bracket"
} vs_mount_diagnostic;

typedef struct {
    int struct_size;
    int dust_spots_found;
    float dust_coverage_pct;
    int is_lens_contaminated;    // 1 if spots exceed limit, 0 if clean
    char advice[128];            // e.g. "Clean front optic: 5 stationary dust spots detected"
} vs_dust_diagnostic;

typedef struct {
    int struct_size;
    float mean_luminance;
    float min_luminance;
    float max_luminance;
    float corner_to_center_ratio;// Vignetting / spatial uniformity
    int is_daytime;              // 1 if high ambient sunlight ingress, 0 if night/controlled
    float suggested_exposure_trim;// Multiplier e.g. 0.95 or 1.05 to reach nominal target
    char lighting_advice[128];   // e.g. "Solar ingress: Strobe overdrive boosted 120%"
} vs_lighting_diagnostic;

typedef struct {
    int struct_size;
    float delta_x_mm;
    float delta_y_mm;
    float total_eccentricity_mm; // sqrt(dx^2 + dy^2)
    float cpk_estimate;
    float x_screw_turns;         // Positive = clockwise, negative = counter-clockwise
    float y_screw_turns;
    int is_drift_critical;       // 1 if >75% of tolerance, 0 if nominal
    char advisory_pill[128];     // Plain-language operator instruction
} vs_applicator_advisory;

// Parameters for mount tilt check
typedef struct {
    int struct_size;
    float min_acceptable_uniformity; // Default 0.65 (65% uniformity across 9 zones)
    float min_nominal_sharpness;     // Default 5.0
} vs_mount_config;

// Parameters for dust check
typedef struct {
    int struct_size;
    uint8_t dust_threshold_dn;       // Pixel intensity drop below background (default 30)
    int min_spot_px;                 // Minimum dust spot area (default 3 px)
    int max_spot_px;                 // Maximum dust spot area (default 50 px)
    int max_allowed_spots;           // Threshold before warning (default 4)
} vs_dust_config;

// Parameters for hologram / cap applicator
typedef struct {
    int struct_size;
    float px_per_mm;                 // Optical calibration (e.g. 10.0 px/mm)
    float screw_pitch_mm_per_turn;   // Lead screw pitch (e.g. 0.5 mm/turn)
    float max_allowed_eccentricity_mm; // Max allowable offset before reject (e.g. 1.2 mm)
} vs_applicator_config;

VS_AUTOTUNE_API void vs_autotune_default_mount_config(vs_mount_config* cfg);
VS_AUTOTUNE_API void vs_autotune_default_dust_config(vs_dust_config* cfg);
VS_AUTOTUNE_API void vs_autotune_default_applicator_config(vs_applicator_config* cfg);

// 1. 9-Quadrant MTF Focus & Mount Tilt Diagnostic
VS_AUTOTUNE_API int vs_autotune_check_mount_tilt(
    const uint8_t* img,
    int w,
    int h,
    int stride,
    const vs_mount_config* cfg,
    vs_mount_diagnostic* diag);

// 2. Lens Dust & Smudge Contamination Diagnostic
VS_AUTOTUNE_API int vs_autotune_check_lens_dust(
    const uint8_t* background_or_min_img,
    int w,
    int h,
    int stride,
    const vs_dust_config* cfg,
    vs_dust_diagnostic* diag);

// 3. Strobe-Minus-Ambient Decoupling (Sunlight & Ceiling Light Cancellation)
VS_AUTOTUNE_API int vs_autotune_strobe_minus_ambient(
    const uint8_t* strobe_img,
    const uint8_t* ambient_img,
    uint8_t* clean_dst,
    int w,
    int h,
    int stride,
    float* out_ambient_leakage_pct);

// 4. Ambient Lighting & Day/Night Circadian Analyzer
VS_AUTOTUNE_API int vs_autotune_analyze_lighting(
    const uint8_t* img,
    int w,
    int h,
    int stride,
    float target_mean_dn,
    vs_lighting_diagnostic* diag);

// 5. Upstream Tooling / Hologram Applicator Closed-Loop Advisory
VS_AUTOTUNE_API int vs_autotune_applicator_guidance(
    float cap_cx,
    float cap_cy,
    float cap_radius,
    float mark_cx,
    float mark_cy,
    float mark_radius,
    const vs_applicator_config* cfg,
    vs_applicator_advisory* advisory);

#ifdef __cplusplus
}
#endif

#endif // VS_AUTOTUNE_H
