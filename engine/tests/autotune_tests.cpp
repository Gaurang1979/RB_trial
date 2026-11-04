#include "vs_autotune.h"
#include "check.h"
#include <cstdio>
#include <vector>
#include <cmath>
#include <cstring>

static void test_default_configs() {
    printf("[vs_autotune] Running test_default_configs...\n");
    vs_mount_config m_cfg;
    vs_autotune_default_mount_config(&m_cfg);
    VS_CHECK(m_cfg.struct_size == sizeof(vs_mount_config));
    VS_CHECK(m_cfg.min_acceptable_uniformity > 0.5f);

    vs_dust_config d_cfg;
    vs_autotune_default_dust_config(&d_cfg);
    VS_CHECK(d_cfg.struct_size == sizeof(vs_dust_config));
    VS_CHECK(d_cfg.max_allowed_spots >= 1);

    vs_applicator_config a_cfg;
    vs_autotune_default_applicator_config(&a_cfg);
    VS_CHECK(a_cfg.struct_size == sizeof(vs_applicator_config));
    VS_CHECK(a_cfg.px_per_mm > 1.0f);
    printf("  -> PASS\n");
}

static void test_mount_tilt_uniform() {
    printf("[vs_autotune] Running test_mount_tilt_uniform...\n");
    const int w = 90;
    const int h = 90;
    std::vector<uint8_t> img(w * h, 128);

    // Create uniform checkerboard pattern across all 9 zones
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            if ((x / 3 + y / 3) % 2 == 0) {
                img[y * w + x] = 200;
            } else {
                img[y * w + x] = 50;
            }
        }
    }

    vs_mount_diagnostic diag;
    int rc = vs_autotune_check_mount_tilt(img.data(), w, h, w, nullptr, &diag);
    VS_CHECK(rc == VS_AUTOTUNE_OK);
    VS_CHECK(diag.is_mount_tilted == 0);
    VS_CHECK(diag.uniformity_ratio >= 0.80f);
    VS_CHECK(diag.mean_sharpness > 20.0f);
    VS_CHECK(strstr(diag.tilt_direction, "Optimal") != nullptr);
    printf("  -> PASS (uniformity=%.2f, mean_sharpness=%.1f)\n", diag.uniformity_ratio, diag.mean_sharpness);
}

static void test_mount_tilt_scheimpflug() {
    printf("[vs_autotune] Running test_mount_tilt_scheimpflug...\n");
    const int w = 90;
    const int h = 90;
    std::vector<uint8_t> img(w * h, 128);

    // Top half is sharp checkerboard; Bottom half is blurred / flat (tilted mount)
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            if (y < 30) {
                // Top third: high contrast edges
                img[y * w + x] = ((x / 3) % 2 == 0) ? 220 : 40;
            } else if (y < 60) {
                // Middle third: medium contrast
                img[y * w + x] = ((x / 6) % 2 == 0) ? 160 : 100;
            } else {
                // Bottom third: completely blurred out flat gradient
                img[y * w + x] = (uint8_t)(120 + (x % 5));
            }
        }
    }

    vs_mount_diagnostic diag;
    int rc = vs_autotune_check_mount_tilt(img.data(), w, h, w, nullptr, &diag);
    VS_CHECK(rc == VS_AUTOTUNE_OK);
    VS_CHECK(diag.is_mount_tilted == 1);
    VS_CHECK(diag.uniformity_ratio < 0.50f);
    VS_CHECK(strstr(diag.tilt_direction, "Bottom Blur") != nullptr);
    VS_CHECK(diag.advice[0] != '\0');
    printf("  -> PASS (Detected: %s, uniformity=%.2f)\n", diag.tilt_direction, diag.uniformity_ratio);
}

static void test_lens_dust_clean_vs_contaminated() {
    printf("[vs_autotune] Running test_lens_dust_clean_vs_contaminated...\n");
    const int w = 100;
    const int h = 100;
    std::vector<uint8_t> clean_bg(w * h, 200);

    vs_dust_diagnostic diag;
    int rc = vs_autotune_check_lens_dust(clean_bg.data(), w, h, w, nullptr, &diag);
    VS_CHECK(rc == VS_AUTOTUNE_OK);
    VS_CHECK(diag.dust_spots_found == 0);
    VS_CHECK(diag.is_lens_contaminated == 0);

    // Add 8 synthetic dark dust spots of area ~8 px each
    std::vector<uint8_t> dirty_bg = clean_bg;
    const int spot_coords[8][2] = {
        {15, 15}, {30, 20}, {70, 25}, {20, 80},
        {50, 50}, {80, 80}, {85, 40}, {40, 75}
    };

    for (int s = 0; s < 8; ++s) {
        int sx = spot_coords[s][0];
        int sy = spot_coords[s][1];
        for (int dy = -1; dy <= 1; ++dy) {
            for (int dx = -1; dx <= 1; ++dx) {
                dirty_bg[(sy + dy) * w + (sx + dx)] = 50; // Dark dust spot
            }
        }
    }

    vs_dust_config d_cfg;
    vs_autotune_default_dust_config(&d_cfg);
    d_cfg.max_allowed_spots = 4;

    rc = vs_autotune_check_lens_dust(dirty_bg.data(), w, h, w, &d_cfg, &diag);
    VS_CHECK(rc == VS_AUTOTUNE_OK);
    VS_CHECK(diag.dust_spots_found >= 7);
    VS_CHECK(diag.is_lens_contaminated == 1);
    VS_CHECK(strstr(diag.advice, "Lens hygiene warning") != nullptr);
    printf("  -> PASS (Found %d spots, contaminated=%d)\n", diag.dust_spots_found, diag.is_lens_contaminated);
}

static void test_strobe_minus_ambient() {
    printf("[vs_autotune] Running test_strobe_minus_ambient...\n");
    const int w = 80;
    const int h = 80;

    std::vector<uint8_t> ambient(w * h, 60);       // Sunlight/ceiling light background
    std::vector<uint8_t> strobe(w * h, 60);        // Strobe + ambient
    std::vector<uint8_t> clean(w * h, 0);

    // Add feature illuminated by strobe: center square 180 DN above ambient
    for (int y = 20; y < 60; ++y) {
        for (int x = 20; x < 60; ++x) {
            strobe[y * w + x] = 240; // 60 ambient + 180 strobe
        }
    }

    float leakage = 0.0f;
    int rc = vs_autotune_strobe_minus_ambient(strobe.data(), ambient.data(), clean.data(), w, h, w, &leakage);
    VS_CHECK(rc == VS_AUTOTUNE_OK);

    // Ambient background should be cancelled to 0 DN
    VS_CHECK(clean[5 * w + 5] == 0);
    // Center feature should be ~180 DN
    VS_CHECK(clean[30 * w + 30] == 180);
    VS_CHECK(leakage > 0.0f);
    printf("  -> PASS (leakage=%.1f%%, center_val=%d, bg_val=%d)\n", leakage, clean[30 * w + 30], clean[5 * w + 5]);
}

static void test_analyze_lighting() {
    printf("[vs_autotune] Running test_analyze_lighting...\n");
    const int w = 80;
    const int h = 80;
    std::vector<uint8_t> img(w * h, 100);

    vs_lighting_diagnostic diag;
    int rc = vs_autotune_analyze_lighting(img.data(), w, h, w, 128.0f, &diag);
    VS_CHECK(rc == VS_AUTOTUNE_OK);
    VS_CHECK(std::abs(diag.mean_luminance - 100.0f) < 0.1f);
    VS_CHECK(diag.is_daytime == 0);
    VS_CHECK(diag.suggested_exposure_trim > 1.2f); // Needs boost to reach 128 DN

    // Bright washed-out solar frame
    std::fill(img.begin(), img.end(), 180);
    rc = vs_autotune_analyze_lighting(img.data(), w, h, w, 128.0f, &diag);
    VS_CHECK(rc == VS_AUTOTUNE_OK);
    VS_CHECK(diag.is_daytime == 1);
    VS_CHECK(diag.suggested_exposure_trim < 0.8f); // Needs reduction
    printf("  -> PASS (daytime=%d, trim=%.2f)\n", diag.is_daytime, diag.suggested_exposure_trim);
}

static void test_applicator_guidance() {
    printf("[vs_autotune] Running test_applicator_guidance...\n");
    // Outer bottle cap centered at (100, 100)
    float cap_x = 100.0f, cap_y = 100.0f, cap_r = 40.0f;

    // 1. Centered hologram
    vs_applicator_advisory adv;
    vs_applicator_config cfg;
    vs_autotune_default_applicator_config(&cfg); // 10 px/mm, 0.5 mm/turn, 1.2 mm max
    int rc = vs_autotune_applicator_guidance(cap_x, cap_y, cap_r, 100.0f, 100.0f, 10.0f, &cfg, &adv);
    VS_CHECK(rc == VS_AUTOTUNE_OK);
    VS_CHECK(adv.total_eccentricity_mm < 0.01f);
    VS_CHECK(adv.is_drift_critical == 0);
    VS_CHECK(strstr(adv.advisory_pill, "centered") != nullptr);

    // 2. Off-center hologram: shifted +9.0 px in X (+0.90 mm)
    rc = vs_autotune_applicator_guidance(cap_x, cap_y, cap_r, 109.0f, 100.0f, 10.0f, &cfg, &adv);
    VS_CHECK(rc == VS_AUTOTUNE_OK);
    VS_CHECK(std::abs(adv.delta_x_mm - 0.90f) < 0.01f);
    VS_CHECK(std::abs(adv.total_eccentricity_mm - 0.90f) < 0.01f);
    VS_CHECK(adv.is_drift_critical == 1); // 0.90 mm >= 70% of 1.20 mm (0.84 mm)
    // 0.90 mm / 0.5 mm/turn = -1.8 turns CCW
    VS_CHECK(std::abs(adv.x_screw_turns - (-1.80f)) < 0.05f);
    VS_CHECK(strstr(adv.advisory_pill, "Screw X") != nullptr);
    VS_CHECK(strstr(adv.advisory_pill, "CCW") != nullptr);
    printf("  -> PASS (Eccentricity=%.2fmm, Screw X turns=%.2f, Adv='%s')\n",
           adv.total_eccentricity_mm, adv.x_screw_turns, adv.advisory_pill);
}

int main() {
    printf("=========================================\n");
    printf("Running vs_autotune Unit Tests\n");
    printf("=========================================\n");
    test_default_configs();
    test_mount_tilt_uniform();
    test_mount_tilt_scheimpflug();
    test_lens_dust_clean_vs_contaminated();
    test_strobe_minus_ambient();
    test_analyze_lighting();
    test_applicator_guidance();
    printf("All vs_autotune unit tests PASSED successfully!\n");
    return 0;
}
