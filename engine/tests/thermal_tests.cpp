/*
 * thermal_tests.cpp - Unit and benchmark tests for the Thermal Packaging Inspection Engine.
 */
#include "vs_thermal.h"

#include "check.h"
#include <cmath>
#include <cstring>
#include <iostream>
#include <vector>
#include <chrono>

static void test_default_params() {
    std::cout << "[TEST] vs_thermal default parameters... " << std::flush;
    vs_thermal_bottle_params bp{};
    vs_thermal_default_bottle_params(&bp);
    VS_CHECK(bp.struct_size == sizeof(vs_thermal_bottle_params));
    VS_CHECK(bp.min_seal_temp_c >= 50.0f && bp.min_seal_temp_c <= 75.0f);
    VS_CHECK(bp.max_seal_temp_c >= 90.0f);
    VS_CHECK(bp.max_cold_gap_deg >= 10.0f);

    vs_thermal_pouch_params pp{};
    vs_thermal_default_pouch_params(&pp);
    VS_CHECK(pp.struct_size == sizeof(vs_thermal_pouch_params));
    VS_CHECK(pp.min_seal_temp_c >= 55.0f);
    VS_CHECK(pp.min_seal_width_px >= 5.0f);

    vs_thermal_glue_params gp{};
    vs_thermal_default_glue_params(&gp);
    VS_CHECK(gp.struct_size == sizeof(vs_thermal_glue_params));
    VS_CHECK(gp.expected_bead_count == 2);
    VS_CHECK(gp.min_glue_temp_c >= 60.0f);
    std::cout << "PASS\n";
}

// ---------------------------------------------------------------------------
// Bottle Induction Cap Sealing Tests
// ---------------------------------------------------------------------------

static void test_bottle_good_seal() {
    std::cout << "[TEST] Bottle induction good seal... " << std::flush;
    const int w = 160, h = 160;
    const float cx = 80.0f, cy = 80.0f;
    std::vector<uint8_t> img(w * h, 10); // Ambient 24 C

    // Draw uniform induction heat ring at 78 C (mapped to pixel value 148 on 20..120 C scale)
    // 78 C = 20 + (p / 255) * 100 -> p = 148
    const uint8_t seal_val = 148;
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            float dist = std::hypot(x - cx, y - cy);
            if (dist >= 35.0f && dist <= 55.0f) {
                img[y * w + x] = seal_val;
            }
        }
    }

    vs_thermal_bottle_params p{};
    vs_thermal_default_bottle_params(&p);
    p.expected_cx = cx;
    p.expected_cy = cy;

    vs_thermal_bottle_result res{};
    res.struct_size = sizeof(vs_thermal_bottle_result);

    int rc = vs_thermal_inspect_bottle_seal(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_PASS);
    VS_CHECK(res.cold_gap_deg == 0.0f);
    VS_CHECK(res.seal_integrity_pct == 100.0f);
    VS_CHECK(res.mean_seal_temp_c >= 75.0f && res.mean_seal_temp_c <= 82.0f);
    std::cout << "PASS (Mean Temp: " << res.mean_seal_temp_c << " C, Gap: " << res.cold_gap_deg << " deg)\n";
}

static void test_bottle_missing_foil() {
    std::cout << "[TEST] Bottle missing foil (cold top)... " << std::flush;
    const int w = 160, h = 160;
    std::vector<uint8_t> img(w * h, 10); // Completely cold ambient (24 C)

    vs_thermal_bottle_params p{};
    vs_thermal_default_bottle_params(&p);
    p.expected_cx = 80.0f;
    p.expected_cy = 80.0f;

    vs_thermal_bottle_result res{};
    res.struct_size = sizeof(vs_thermal_bottle_result);

    int rc = vs_thermal_inspect_bottle_seal(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_NG_MISSING_FOIL);
    VS_CHECK(res.seal_integrity_pct == 0.0f);
    std::cout << "PASS (Verdict: NG_MISSING_FOIL)\n";
}

static void test_bottle_incomplete_seal() {
    std::cout << "[TEST] Bottle incomplete seal (cold gap crescent)... " << std::flush;
    const int w = 160, h = 160;
    const float cx = 80.0f, cy = 80.0f;
    std::vector<uint8_t> img(w * h, 10);

    // Draw ring with a 40 degree gap at angle 45..85 deg
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            float dist = std::hypot(x - cx, y - cy);
            if (dist >= 35.0f && dist <= 55.0f) {
                float angle = std::atan2(y - cy, x - cx) * (180.0f / 3.141592653589793f);
                if (angle < 0.0f) angle += 360.0f;

                // Cold notch between 45 and 85 deg
                if (!(angle >= 45.0f && angle <= 85.0f)) {
                    img[y * w + x] = 148; // 78 C
                }
            }
        }
    }

    vs_thermal_bottle_params p{};
    vs_thermal_default_bottle_params(&p);
    p.expected_cx = cx;
    p.expected_cy = cy;
    p.max_cold_gap_deg = 15.0f; // Limit 15 deg

    vs_thermal_bottle_result res{};
    res.struct_size = sizeof(vs_thermal_bottle_result);

    int rc = vs_thermal_inspect_bottle_seal(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_NG_INCOMPLETE_SEAL);
    VS_CHECK(res.cold_gap_deg >= 35.0f && res.cold_gap_deg <= 45.0f);
    std::cout << "PASS (Cold Gap: " << res.cold_gap_deg << " deg)\n";
}

static void test_bottle_overheated() {
    std::cout << "[TEST] Bottle overheated seal (scorched liner)... " << std::flush;
    const int w = 160, h = 160;
    const float cx = 80.0f, cy = 80.0f;
    std::vector<uint8_t> img(w * h, 10);

    // Ring at 112 C (pixel val 234)
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            float dist = std::hypot(x - cx, y - cy);
            if (dist >= 35.0f && dist <= 55.0f) {
                img[y * w + x] = 234;
            }
        }
    }

    vs_thermal_bottle_params p{};
    vs_thermal_default_bottle_params(&p);
    p.expected_cx = cx;
    p.expected_cy = cy;
    p.max_seal_temp_c = 95.0f;

    vs_thermal_bottle_result res{};
    res.struct_size = sizeof(vs_thermal_bottle_result);

    int rc = vs_thermal_inspect_bottle_seal(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_NG_OVERHEATED);
    std::cout << "PASS (Peak Temp: " << res.peak_seal_temp_c << " C > 95 C)\n";
}

// ---------------------------------------------------------------------------
// Pouch & Tray Heat Seal Seam Tests
// ---------------------------------------------------------------------------

static void test_pouch_good_seam() {
    std::cout << "[TEST] Pouch good continuous seal seam... " << std::flush;
    const int w = 200, h = 60;
    std::vector<uint8_t> img(w * h, 10);

    // Continuous horizontal seam across y=20..35 (16 px width) at 85 C (val 118 on 20..160 C scale)
    for (int y = 20; y <= 35; ++y) {
        for (int x = 0; x < w; ++x) {
            img[y * w + x] = 118;
        }
    }

    vs_thermal_pouch_params p{};
    vs_thermal_default_pouch_params(&p);
    p.seam_roi_w = w;
    p.seam_roi_h = h;
    p.min_seal_width_px = 10.0f;

    vs_thermal_pouch_result res{};
    res.struct_size = sizeof(vs_thermal_pouch_result);

    int rc = vs_thermal_inspect_pouch_seam(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_PASS);
    VS_CHECK(res.void_count == 0);
    VS_CHECK(res.mean_seam_width_px >= 15.0f);
    VS_CHECK(res.contamination_found == 0);
    std::cout << "PASS (Width: " << res.mean_seam_width_px << " px)\n";
}

static void test_pouch_leak_void() {
    std::cout << "[TEST] Pouch seam void (cold channel / leak)... " << std::flush;
    const int w = 200, h = 60;
    std::vector<uint8_t> img(w * h, 10);

    for (int y = 20; y <= 35; ++y) {
        for (int x = 0; x < w; ++x) {
            // Cold void between x=100 and x=108
            if (!(x >= 100 && x <= 108)) {
                img[y * w + x] = 118;
            }
        }
    }

    vs_thermal_pouch_params p{};
    vs_thermal_default_pouch_params(&p);
    p.seam_roi_w = w;
    p.seam_roi_h = h;
    p.max_void_length_px = 4.0f;

    vs_thermal_pouch_result res{};
    res.struct_size = sizeof(vs_thermal_pouch_result);

    int rc = vs_thermal_inspect_pouch_seam(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_NG_SEAM_VOID);
    VS_CHECK(res.void_count >= 1);
    VS_CHECK(res.worst_void_length_px >= 8.0f);
    std::cout << "PASS (Void Length: " << res.worst_void_length_px << " px at x=" << res.worst_void_x << ")\n";
}

// ---------------------------------------------------------------------------
// Hot Melt Glue Carton Flap Tests
// ---------------------------------------------------------------------------

static void test_glue_good_beads() {
    std::cout << "[TEST] Hot melt glue good 2-bead dispense... " << std::flush;
    const int w = 150, h = 80;
    std::vector<uint8_t> img(w * h, 10);

    // Two parallel beads at y=25 and y=55, length 120 px, temp 130 C (val 175 on 20..180 C)
    for (int x = 15; x < 135; ++x) {
        for (int dy = -2; dy <= 2; ++dy) {
            img[(25 + dy) * w + x] = 175;
            img[(55 + dy) * w + x] = 175;
        }
    }

    vs_thermal_glue_params p{};
    vs_thermal_default_glue_params(&p);
    // ROI wider than the beads (120 px beads in a 150 px ROI): the cold ends are not gaps.
    p.roi_w = w;
    p.roi_h = h;
    p.expected_bead_count = 2;

    vs_thermal_glue_result res{};
    res.struct_size = sizeof(vs_thermal_glue_result);

    int rc = vs_thermal_inspect_hot_glue(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_PASS);
    VS_CHECK(res.detected_bead_count == 2);
    VS_CHECK(res.worst_gap_px == 0.0f);
    VS_CHECK(res.bonding_confidence >= 95.0f);
    std::cout << "PASS (Beads: " << res.detected_bead_count << ", Confidence: " << res.bonding_confidence << "%)\n";
}

static void test_glue_missing_bead() {
    std::cout << "[TEST] Hot melt glue missing bead (clogged nozzle)... " << std::flush;
    const int w = 150, h = 80;
    std::vector<uint8_t> img(w * h, 10);

    // Only 1 bead at y=25
    for (int x = 15; x < 135; ++x) {
        for (int dy = -2; dy <= 2; ++dy) {
            img[(25 + dy) * w + x] = 175;
        }
    }

    vs_thermal_glue_params p{};
    vs_thermal_default_glue_params(&p);
    p.roi_w = w;
    p.roi_h = h;
    p.expected_bead_count = 2;

    vs_thermal_glue_result res{};
    res.struct_size = sizeof(vs_thermal_glue_result);

    int rc = vs_thermal_inspect_hot_glue(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_NG_MISSING_BEAD);
    VS_CHECK(res.detected_bead_count == 1);
    std::cout << "PASS (Found: " << res.detected_bead_count << " of 2 expected)\n";
}

static void test_glue_broken_bead() {
    std::cout << "[TEST] Hot melt glue broken bead (20 px break)... " << std::flush;
    const int w = 150, h = 80;
    std::vector<uint8_t> img(w * h, 10);

    // Two beads from x=15 to 135; the bead at y=55 has a cold break at x=60..79
    for (int x = 15; x < 135; ++x) {
        for (int dy = -2; dy <= 2; ++dy) {
            img[(25 + dy) * w + x] = 175;
            if (x < 60 || x >= 80) img[(55 + dy) * w + x] = 175;
        }
    }

    vs_thermal_glue_params p{};
    vs_thermal_default_glue_params(&p);
    p.roi_w = w;
    p.roi_h = h;
    p.expected_bead_count = 2;

    vs_thermal_glue_result res{};
    res.struct_size = sizeof(vs_thermal_glue_result);

    int rc = vs_thermal_inspect_hot_glue(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_NG_BROKEN_BEAD);
    VS_CHECK(res.worst_gap_px == 20.0f);
    std::cout << "PASS (Worst gap: " << res.worst_gap_px << " px)\n";
}

static void test_glue_short_bead() {
    std::cout << "[TEST] Hot melt glue short bead (stopped early)... " << std::flush;
    const int w = 150, h = 80;
    std::vector<uint8_t> img(w * h, 10);

    // Bead at y=25 is full length; bead at y=55 stops after 30 px (under the 40 px minimum)
    for (int x = 15; x < 135; ++x) {
        for (int dy = -2; dy <= 2; ++dy) {
            img[(25 + dy) * w + x] = 175;
            if (x < 45) img[(55 + dy) * w + x] = 175;
        }
    }

    vs_thermal_glue_params p{};
    vs_thermal_default_glue_params(&p);
    p.roi_w = w;
    p.roi_h = h;
    p.expected_bead_count = 2;

    vs_thermal_glue_result res{};
    res.struct_size = sizeof(vs_thermal_glue_result);

    int rc = vs_thermal_inspect_hot_glue(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_NG_BROKEN_BEAD);
    VS_CHECK(res.worst_gap_px == 0.0f);
    VS_CHECK(res.min_bead_length_px == 30.0f);
    std::cout << "PASS (Shortest bead: " << res.min_bead_length_px << " px)\n";
}

// ---------------------------------------------------------------------------
// 16-Bit Radiometric Temperature Input Test
// ---------------------------------------------------------------------------

static void test_radiometric16_bottle() {
    std::cout << "[TEST] 16-bit radiometric bottle inspection... " << std::flush;
    const int w = 100, h = 100;
    const float cx = 50.0f, cy = 50.0f;
    std::vector<uint16_t> rad16(w * h, 2500); // 25.00 C ambient (2500 in 0.01 C)

    // Ring at 78.50 C (7850)
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            float dist = std::hypot(x - cx, y - cy);
            if (dist >= 25.0f && dist <= 35.0f) {
                rad16[y * w + x] = 7850;
            }
        }
    }

    vs_thermal_bottle_params p{};
    vs_thermal_default_bottle_params(&p);
    p.expected_cx = cx;
    p.expected_cy = cy;
    p.inner_radius_px = 25.0f;
    p.outer_radius_px = 35.0f;

    vs_thermal_bottle_result res{};
    res.struct_size = sizeof(vs_thermal_bottle_result);

    int rc = vs_thermal_inspect_bottle_seal(rad16.data(), w, h, w * sizeof(uint16_t), VS_THERMAL_RADIOMETRIC16, &p, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_PASS);
    VS_CHECK(std::abs(res.mean_seal_temp_c - 78.50f) < 0.5f);
    std::cout << "PASS (Radiometric 16-bit Temp: " << res.mean_seal_temp_c << " C)\n";
}

// ---------------------------------------------------------------------------
// Thermal Palette Generation Test
// ---------------------------------------------------------------------------

static void test_palette_synthesis() {
    std::cout << "[TEST] Thermal Ironbow palette synthesis... " << std::flush;
    const int w = 50, h = 50;
    std::vector<uint8_t> thermal_in(w * h);
    for (int i = 0; i < w * h; ++i) {
        thermal_in[i] = static_cast<uint8_t>((i * 255) / (w * h));
    }

    std::vector<uint8_t> bgr_out(w * h * 3, 0);
    int rc = vs_thermal_apply_palette(thermal_in.data(), w, h, w, VS_THERMAL_MONO8, 20.0f, 100.0f, VS_PALETTE_IRONBOW, bgr_out.data(), w * 3);
    VS_CHECK(rc == 0);

    // Pixel 0 (coldest) should be deep dark violet/blue
    VS_CHECK(bgr_out[0] < 50 && bgr_out[1] == 0);
    // Last pixel (hottest) should be bright white/yellow
    int last_idx = (w * h - 1) * 3;
    VS_CHECK(bgr_out[last_idx + 2] > 200 && bgr_out[last_idx + 1] > 200);
    std::cout << "PASS\n";
}

// ---------------------------------------------------------------------------
// Industrial 640x512 Performance Benchmark
// ---------------------------------------------------------------------------

static void test_performance_benchmark() {
    std::cout << "[TEST] 640x512 industrial thermal benchmark... " << std::flush;
    const int w = 640, h = 512;
    std::vector<uint8_t> img(w * h, 10);

    // Bottle ring at center
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            float dist = std::hypot(x - 320, y - 256);
            if (dist >= 60.0f && dist <= 100.0f) {
                img[y * w + x] = 150;
            }
        }
    }

    vs_thermal_bottle_params p{};
    vs_thermal_default_bottle_params(&p);
    p.expected_cx = 320.0f;
    p.expected_cy = 256.0f;
    p.inner_radius_px = 60.0f;
    p.outer_radius_px = 100.0f;

    vs_thermal_bottle_result res{};
    res.struct_size = sizeof(vs_thermal_bottle_result);

    // Warm-up
    vs_thermal_inspect_bottle_seal(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);

    double total_ms = 0.0;
    const int iters = 20;
    for (int i = 0; i < iters; ++i) {
        vs_thermal_inspect_bottle_seal(img.data(), w, h, w, VS_THERMAL_MONO8, &p, &res);
        total_ms += res.time_ms;
    }

    double avg_ms = total_ms / iters;
    std::cout << "PASS (Average 640x512 inspection: " << avg_ms << " ms; budget <= 10.0 ms)\n";
    VS_CHECK(VS_TIME_BUDGET(avg_ms < 10.0));
}

// ---------------------------------------------------------------------------
// Medicine Blister Pack & Tooling Diagnostic Tests
// ---------------------------------------------------------------------------

static void test_blister_good_seal() {
    std::cout << "[TEST] Blister pack good moisture barrier... " << std::flush;
    const int w = 240, h = 140;
    // Entire card heated to 80 C (val 153 on 20..120 C scale)
    std::vector<uint8_t> card(w * h, 153);

    vs_thermal_blister_params bp{};
    vs_thermal_default_blister_params(&bp);
    bp.card_roi_w = w;
    bp.card_roi_h = h;
    bp.rows = 2;
    bp.cols = 3;
    bp.pocket_w_px = 30.0f;
    bp.pocket_h_px = 20.0f;
    bp.pitch_x_px = 70.0f;
    bp.pitch_y_px = 50.0f;
    bp.min_barrier_width_px = 8.0f;

    // Cut out 6 cold pockets (ambient 25 C = val 12)
    const float margin_x = (w - (3 * 70.0f)) * 0.5f;
    const float margin_y = (h - (2 * 50.0f)) * 0.5f;

    for (int r = 0; r < 2; ++r) {
        for (int c = 0; c < 3; ++c) {
            int pc_x = static_cast<int>(margin_x + c * 70.0f + 35.0f);
            int pc_y = static_cast<int>(margin_y + r * 50.0f + 25.0f);

            for (int dy = -10; dy <= 10; ++dy) {
                for (int dx = -15; dx <= 15; ++dx) {
                    card[(pc_y + dy) * w + (pc_x + dx)] = 12;
                }
            }
        }
    }

    vs_thermal_blister_result res{};
    res.struct_size = sizeof(vs_thermal_blister_result);

    int rc = vs_thermal_inspect_blister(card.data(), w, h, w, VS_THERMAL_MONO8, &bp, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_PASS);
    VS_CHECK(res.compromised_cavity_idx == -1);
    VS_CHECK(res.min_barrier_width_px >= 8.0f);
    VS_CHECK(std::strcmp(res.advice.fault_code, "OK_NORMAL") == 0);
    std::cout << "PASS (Min Barrier Width: " << res.min_barrier_width_px << " px)\n";
}

static void test_blister_barrier_breach() {
    std::cout << "[TEST] Blister pack cavity moisture barrier leak... " << std::flush;
    const int w = 240, h = 140;
    std::vector<uint8_t> card(w * h, 153);

    vs_thermal_blister_params bp{};
    vs_thermal_default_blister_params(&bp);
    bp.card_roi_w = w;
    bp.card_roi_h = h;
    bp.rows = 2;
    bp.cols = 3;
    bp.pocket_w_px = 30.0f;
    bp.pocket_h_px = 20.0f;
    bp.pitch_x_px = 70.0f;
    bp.pitch_y_px = 50.0f;
    bp.min_barrier_width_px = 8.0f;

    const float margin_x = (w - (3 * 70.0f)) * 0.5f;
    const float margin_y = (h - (2 * 50.0f)) * 0.5f;

    for (int r = 0; r < 2; ++r) {
        for (int c = 0; c < 3; ++c) {
            int pc_x = static_cast<int>(margin_x + c * 70.0f + 35.0f);
            int pc_y = static_cast<int>(margin_y + r * 50.0f + 25.0f);

            for (int dy = -10; dy <= 10; ++dy) {
                for (int dx = -15; dx <= 15; ++dx) {
                    card[(pc_y + dy) * w + (pc_x + dx)] = 12;
                }
            }
        }
    }

    // Punch cold leak channel across cavity (0, 1) = index 1 upwards to card edge
    int leak_cx = static_cast<int>(margin_x + 1 * 70.0f + 35.0f);
    for (int y = 0; y < 45; ++y) {
        for (int dx = -4; dx <= 4; ++dx) {
            card[y * w + (leak_cx + dx)] = 12; // Cold unsealed channel
        }
    }

    vs_thermal_blister_result res{};
    res.struct_size = sizeof(vs_thermal_blister_result);

    int rc = vs_thermal_inspect_blister(card.data(), w, h, w, VS_THERMAL_MONO8, &bp, &res);
    VS_CHECK(rc == 0);
    VS_CHECK(res.verdict == VS_THERMAL_NG_BLISTER_BARRIER_BREACH);
    VS_CHECK(res.compromised_cavity_idx == 1); // Pocket index 1
    VS_CHECK(res.min_barrier_width_px == 0.0f);
    VS_CHECK(std::strcmp(res.advice.fault_code, "TOOL_BLISTER_POCKET_LEAK") == 0);
    VS_CHECK(std::strstr(res.advice.recommended_action, "Clean sealing drum") != nullptr);
    std::cout << "PASS (Breached Pocket: " << res.compromised_cavity_idx << ", Advice: " << res.advice.recommended_action << ")\n";
}

static void test_tooling_diagnostic_advice() {
    std::cout << "[TEST] Automated machine tooling diagnostics... " << std::flush;

    vs_thermal_tooling_advice adv{};
    vs_thermal_diagnose_tooling(0, VS_THERMAL_NG_CROOKED_SEAL, 24.0f, &adv);
    VS_CHECK(std::strcmp(adv.fault_code, "TOOL_BOTTLE_INDUCT_TILT") == 0);
    VS_CHECK(std::strstr(adv.recommended_action, "guide rails") != nullptr);

    vs_thermal_diagnose_tooling(2, VS_THERMAL_NG_MISSING_BEAD, 0.0f, &adv);
    VS_CHECK(std::strcmp(adv.fault_code, "TOOL_GLUE_NOZZLE_CLOG") == 0);
    VS_CHECK(std::strstr(adv.recommended_action, "purge") != nullptr);

    vs_thermal_diagnose_tooling(3, VS_THERMAL_NG_JAW_TILT, 22.5f, &adv);
    VS_CHECK(std::strcmp(adv.fault_code, "TOOL_PLATEN_TILT_RIGHT") == 0);
    VS_CHECK(std::strstr(adv.recommended_action, "Shim") != nullptr);

    std::cout << "PASS\n";
}

int main() {
    std::cout << "========================================\n";
    std::cout << "Running Thermal Packaging Seal Tests\n";
    std::cout << "========================================\n";

    test_default_params();
    test_bottle_good_seal();
    test_bottle_missing_foil();
    test_bottle_incomplete_seal();
    test_bottle_overheated();
    test_pouch_good_seam();
    test_pouch_leak_void();
    test_glue_good_beads();
    test_glue_missing_bead();
    test_glue_broken_bead();
    test_glue_short_bead();
    test_radiometric16_bottle();
    test_palette_synthesis();
    test_blister_good_seal();
    test_blister_barrier_breach();
    test_tooling_diagnostic_advice();
    test_performance_benchmark();

    std::cout << "\nAll Thermal Packaging Seal tests passed successfully!\n";
    return 0;
}
