/*
 * thermal.cpp - Implementation of the Industrial Thermal Packaging Inspection Engine.
 */
#include "vs_thermal.h"

#include <cmath>
#include <cstring>
#include <chrono>
#include <vector>
#include <algorithm>

namespace {

inline float get_temp_c(
    const void* src,
    int32_t x,
    int32_t y,
    int32_t stride,
    int32_t format,
    float t_min_calib,
    float t_max_calib)
{
    if (format == VS_THERMAL_MONO8) {
        const uint8_t* p = static_cast<const uint8_t*>(src) + y * stride;
        float norm = static_cast<float>(p[x]) / 255.0f;
        return t_min_calib + norm * (t_max_calib - t_min_calib);
    } else { // VS_THERMAL_RADIOMETRIC16
        const uint16_t* p = reinterpret_cast<const uint16_t*>(
            static_cast<const uint8_t*>(src) + y * stride);
        // 1 LSB = 0.01 deg C
        return static_cast<float>(p[x]) * 0.01f;
    }
}

// Generate Ironbow RGB palette (256 entries, BGR layout)
void generate_ironbow_lut(uint8_t bgr_lut[256][3]) {
    for (int i = 0; i < 256; ++i) {
        float t = i / 255.0f;
        float r = 0.0f, g = 0.0f, b = 0.0f;
        if (t < 0.2f) {
            float f = t / 0.2f;
            r = f * 0.3f;
            g = 0.0f;
            b = f * 0.5f;
        } else if (t < 0.45f) {
            float f = (t - 0.2f) / 0.25f;
            r = 0.3f + f * 0.55f;
            g = 0.0f;
            b = 0.5f - f * 0.3f;
        } else if (t < 0.75f) {
            float f = (t - 0.45f) / 0.30f;
            r = 0.85f + f * 0.15f;
            g = f * 0.65f;
            b = 0.2f * (1.0f - f);
        } else if (t < 0.92f) {
            float f = (t - 0.75f) / 0.17f;
            r = 1.0f;
            g = 0.65f + f * 0.30f;
            b = f * 0.3f;
        } else {
            float f = (t - 0.92f) / 0.08f;
            r = 1.0f;
            g = 0.95f + f * 0.05f;
            b = 0.3f + f * 0.7f;
        }
        bgr_lut[i][0] = static_cast<uint8_t>(std::clamp(b * 255.0f, 0.0f, 255.0f)); // B
        bgr_lut[i][1] = static_cast<uint8_t>(std::clamp(g * 255.0f, 0.0f, 255.0f)); // G
        bgr_lut[i][2] = static_cast<uint8_t>(std::clamp(r * 255.0f, 0.0f, 255.0f)); // R
    }
}

// Generate Rainbow RGB palette (256 entries, BGR layout)
void generate_rainbow_lut(uint8_t bgr_lut[256][3]) {
    for (int i = 0; i < 256; ++i) {
        float t = i / 255.0f;
        float r = std::clamp(1.5f - std::abs(t * 4.0f - 3.0f), 0.0f, 1.0f);
        float g = std::clamp(1.5f - std::abs(t * 4.0f - 2.0f), 0.0f, 1.0f);
        float b = std::clamp(1.5f - std::abs(t * 4.0f - 1.0f), 0.0f, 1.0f);
        bgr_lut[i][0] = static_cast<uint8_t>(b * 255.0f);
        bgr_lut[i][1] = static_cast<uint8_t>(g * 255.0f);
        bgr_lut[i][2] = static_cast<uint8_t>(r * 255.0f);
    }
}

} // namespace

extern "C" {

void vs_thermal_default_bottle_params(vs_thermal_bottle_params* p) {
    if (!p) return;
    p->struct_size = sizeof(vs_thermal_bottle_params);
    p->expected_cx = 0.0f; // Auto-centroid
    p->expected_cy = 0.0f;
    p->inner_radius_px = 35.0f;
    p->outer_radius_px = 55.0f;
    p->min_seal_temp_c = 60.0f;
    p->max_seal_temp_c = 95.0f;
    p->missing_foil_temp_c = 42.0f;
    p->max_cold_gap_deg = 15.0f;
    p->max_tilt_temp_delta = 18.0f;
    p->t_min_calib = 20.0f;
    p->t_max_calib = 120.0f;
}

void vs_thermal_default_pouch_params(vs_thermal_pouch_params* p) {
    if (!p) return;
    p->struct_size = sizeof(vs_thermal_pouch_params);
    p->seam_roi_x = 0;
    p->seam_roi_y = 0;
    p->seam_roi_w = 0; // 0 = entire image
    p->seam_roi_h = 0;
    p->is_vertical = 0; // Horizontal seam
    p->min_seal_temp_c = 65.0f;
    p->min_seal_width_px = 10.0f;
    p->max_void_length_px = 4.0f;
    p->contamination_drop_c = 12.0f;
    p->t_min_calib = 20.0f;
    p->t_max_calib = 160.0f;
}

void vs_thermal_default_glue_params(vs_thermal_glue_params* p) {
    if (!p) return;
    p->struct_size = sizeof(vs_thermal_glue_params);
    p->roi_x = 0;
    p->roi_y = 0;
    p->roi_w = 0;
    p->roi_h = 0;
    p->is_vertical = 0;
    p->expected_bead_count = 2;
    p->min_glue_temp_c = 75.0f;
    p->min_bead_length_px = 40.0f;
    p->max_bead_gap_px = 8.0f;
    p->t_min_calib = 20.0f;
    p->t_max_calib = 180.0f;
}

int32_t vs_thermal_inspect_bottle_seal(
    const void* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t format,
    const vs_thermal_bottle_params* params,
    vs_thermal_bottle_result* out_result)
{
    if (!src || !params || !out_result) return -1;
    if (params->struct_size != 0 && params->struct_size != sizeof(vs_thermal_bottle_params)) return -1;
    if (out_result->struct_size != 0 && out_result->struct_size != sizeof(vs_thermal_bottle_result)) return -1;
    if (width <= 0 || height <= 0 || stride <= 0) return -2;

    auto t_start = std::chrono::steady_clock::now();
    std::memset(out_result, 0, sizeof(vs_thermal_bottle_result));
    out_result->struct_size = sizeof(vs_thermal_bottle_result);

    // 1. Determine center (cx, cy)
    float cx = params->expected_cx;
    float cy = params->expected_cy;

    if (cx <= 0.0f || cy <= 0.0f) {
        // Auto-centroid of thermal mass
        double sum_x = 0.0, sum_y = 0.0, sum_w = 0.0;
        const int step = std::max(1, width / 64);
        for (int32_t y = 0; y < height; y += step) {
            for (int32_t x = 0; x < width; x += step) {
                float t = get_temp_c(src, x, y, stride, format, params->t_min_calib, params->t_max_calib);
                if (t > params->missing_foil_temp_c) {
                    float weight = t - params->missing_foil_temp_c;
                    sum_x += x * weight;
                    sum_y += y * weight;
                    sum_w += weight;
                }
            }
        }
        if (sum_w > 1.0) {
            cx = static_cast<float>(sum_x / sum_w);
            cy = static_cast<float>(sum_y / sum_w);
        } else {
            cx = width * 0.5f;
            cy = height * 0.5f;
        }
    }

    out_result->detected_cx = cx;
    out_result->detected_cy = cy;

    const float r_in = std::max(5.0f, params->inner_radius_px);
    const float r_out = std::max(r_in + 2.0f, params->outer_radius_px);

    // 2. Sample 360 angular radial rays around the ring
    const int num_angles = 360;
    std::vector<float> ray_peak_temp(num_angles, 0.0f);
    std::vector<float> ray_mean_temp(num_angles, 0.0f);

    double total_ring_temp = 0.0;
    float global_peak_t = -100.0f;
    float global_min_t = 1000.0f;

    for (int a = 0; a < num_angles; ++a) {
        float rad = a * (3.141592653589793f / 180.0f);
        float cos_a = std::cos(rad);
        float sin_a = std::sin(rad);

        float max_t = -100.0f;
        float sum_t = 0.0f;
        int count = 0;

        for (float r = r_in; r <= r_out; r += 1.0f) {
            int32_t px = static_cast<int32_t>(std::round(cx + r * cos_a));
            int32_t py = static_cast<int32_t>(std::round(cy + r * sin_a));

            if (px >= 0 && px < width && py >= 0 && py < height) {
                float t = get_temp_c(src, px, py, stride, format, params->t_min_calib, params->t_max_calib);
                if (t > max_t) max_t = t;
                sum_t += t;
                count++;
            }
        }

        float mean_t = (count > 0) ? (sum_t / count) : 0.0f;
        ray_peak_temp[a] = max_t;
        ray_mean_temp[a] = mean_t;

        total_ring_temp += max_t;
        if (max_t > global_peak_t) global_peak_t = max_t;
        if (max_t < global_min_t) global_min_t = max_t;
    }

    float mean_ring_temp = static_cast<float>(total_ring_temp / num_angles);
    out_result->mean_seal_temp_c = mean_ring_temp;
    out_result->peak_seal_temp_c = global_peak_t;
    out_result->min_seal_temp_c = global_min_t;

    // 3. Check for Missing Foil (Entire ring is cold)
    if (mean_ring_temp < params->missing_foil_temp_c) {
        out_result->verdict = VS_THERMAL_NG_MISSING_FOIL;
        out_result->seal_integrity_pct = 0.0f;
        auto t_end = std::chrono::steady_clock::now();
        out_result->time_ms = std::chrono::duration<float, std::milli>(t_end - t_start).count();
        return 0;
    }

    // 4. Check for Overheating (scorched liner risk)
    if (global_peak_t > params->max_seal_temp_c) {
        out_result->verdict = VS_THERMAL_NG_OVERHEATED;
        out_result->seal_integrity_pct = 50.0f;
        auto t_end = std::chrono::steady_clock::now();
        out_result->time_ms = std::chrono::duration<float, std::milli>(t_end - t_start).count();
        return 0;
    }

    // 5. Check for Angular Cold Gaps (incomplete seal / partial crescent)
    int max_gap = 0;
    int current_gap = 0;
    int gap_start = 0;
    int max_gap_start = 0;

    // Double the array to handle wrap-around gap cleanly
    for (int i = 0; i < num_angles * 2; ++i) {
        int a = i % num_angles;
        if (ray_peak_temp[a] < params->min_seal_temp_c) {
            if (current_gap == 0) {
                gap_start = a;
            }
            current_gap++;
            if (current_gap > max_gap) {
                max_gap = current_gap;
                max_gap_start = gap_start;
            }
            if (current_gap >= num_angles) break;
        } else {
            current_gap = 0;
        }
    }

    out_result->cold_gap_deg = static_cast<float>(std::min(max_gap, num_angles));
    out_result->cold_gap_angle_deg = static_cast<float>(max_gap_start);

    // 6. Check for Lopsided Tilt (Cap crookedness)
    double left_temp = 0.0, right_temp = 0.0;
    for (int a = 0; a < 180; ++a) {
        left_temp += ray_peak_temp[a];
        right_temp += ray_peak_temp[(a + 180) % 360];
    }
    float tilt_delta = static_cast<float>(std::abs(left_temp - right_temp) / 180.0);
    out_result->tilt_delta_c = tilt_delta;

    // 7. Calculate overall Seal Integrity Score
    int pass_rays = 0;
    for (int a = 0; a < num_angles; ++a) {
        if (ray_peak_temp[a] >= params->min_seal_temp_c) pass_rays++;
    }
    out_result->seal_integrity_pct = (pass_rays * 100.0f) / num_angles;

    // Decision Logic
    if (out_result->cold_gap_deg > params->max_cold_gap_deg) {
        out_result->verdict = VS_THERMAL_NG_INCOMPLETE_SEAL;
    } else if (tilt_delta > params->max_tilt_temp_delta) {
        out_result->verdict = VS_THERMAL_NG_CROOKED_SEAL;
    } else if (mean_ring_temp < params->min_seal_temp_c) {
        out_result->verdict = VS_THERMAL_NG_UNDERHEATED;
    } else {
        out_result->verdict = VS_THERMAL_PASS;
    }

    auto t_end = std::chrono::steady_clock::now();
    out_result->time_ms = std::chrono::duration<float, std::milli>(t_end - t_start).count();
    return 0;
}

int32_t vs_thermal_inspect_pouch_seam(
    const void* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t format,
    const vs_thermal_pouch_params* params,
    vs_thermal_pouch_result* out_result)
{
    if (!src || !params || !out_result) return -1;
    if (params->struct_size != 0 && params->struct_size != sizeof(vs_thermal_pouch_params)) return -1;
    if (out_result->struct_size != 0 && out_result->struct_size != sizeof(vs_thermal_pouch_result)) return -1;
    if (width <= 0 || height <= 0 || stride <= 0) return -2;

    auto t_start = std::chrono::steady_clock::now();
    std::memset(out_result, 0, sizeof(vs_thermal_pouch_result));
    out_result->struct_size = sizeof(vs_thermal_pouch_result);

    int32_t rx = std::clamp(params->seam_roi_x, 0, width - 1);
    int32_t ry = std::clamp(params->seam_roi_y, 0, height - 1);
    int32_t rw = (params->seam_roi_w > 0) ? std::min(params->seam_roi_w, width - rx) : (width - rx);
    int32_t rh = (params->seam_roi_h > 0) ? std::min(params->seam_roi_h, height - ry) : (height - ry);

    if (rw <= 0 || rh <= 0) return -3;

    const bool is_vert = (params->is_vertical != 0);
    const int length = is_vert ? rh : rw;
    const int cross_span = is_vert ? rw : rh;

    std::vector<float> seam_widths(length, 0.0f);
    std::vector<float> seam_peak_temps(length, 0.0f);

    double total_width = 0.0;
    double total_temp = 0.0;
    float min_w = 1e6f;

    int void_count = 0;
    int current_void_len = 0;
    int max_void_len = 0;
    float worst_void_x = 0.0f, worst_void_y = 0.0f;
    bool contamination = false;

    for (int l = 0; l < length; ++l) {
        int hot_pixels = 0;
        float peak_t = -100.0f;
        float sum_t = 0.0f;

        for (int c = 0; c < cross_span; ++c) {
            int32_t px = is_vert ? (rx + c) : (rx + l);
            int32_t py = is_vert ? (ry + l) : (ry + c);

            float t = get_temp_c(src, px, py, stride, format, params->t_min_calib, params->t_max_calib);
            if (t > peak_t) peak_t = t;
            if (t >= params->min_seal_temp_c) {
                hot_pixels++;
                sum_t += t;
            }
        }

        seam_widths[l] = static_cast<float>(hot_pixels);
        seam_peak_temps[l] = peak_t;

        total_width += hot_pixels;
        total_temp += peak_t;
        if (hot_pixels < min_w) min_w = hot_pixels;

        // Check for complete void (cold channel through seam)
        if (hot_pixels == 0) {
            current_void_len++;
            if (current_void_len > max_void_len) {
                max_void_len = current_void_len;
                worst_void_x = is_vert ? (rx + cross_span * 0.5f) : (rx + l);
                worst_void_y = is_vert ? (ry + l) : (ry + cross_span * 0.5f);
            }
        } else {
            if (current_void_len > 0) {
                void_count++;
                current_void_len = 0;
            }
        }
    }

    if (current_void_len > 0) void_count++;

    float mean_w = static_cast<float>(total_width / length);
    float mean_t = static_cast<float>(total_temp / length);

    out_result->mean_seam_width_px = mean_w;
    out_result->min_seam_width_px = min_w;
    out_result->mean_seam_temp_c = mean_t;
    out_result->void_count = void_count;
    out_result->worst_void_length_px = static_cast<float>(max_void_len);
    out_result->worst_void_x = worst_void_x;
    out_result->worst_void_y = worst_void_y;

    // Check for contamination (localized cold dip where product was trapped)
    for (int l = 1; l < length - 1; ++l) {
        if (seam_widths[l] > 0 &&
            (seam_peak_temps[l] < mean_t - params->contamination_drop_c) &&
            (seam_widths[l] < mean_w * 0.5f)) {
            contamination = true;
            break;
        }
    }
    out_result->contamination_found = contamination ? 1 : 0;

    // Seam completeness percentage
    int valid_cols = 0;
    for (int l = 0; l < length; ++l) {
        if (seam_widths[l] >= params->min_seal_width_px) valid_cols++;
    }
    out_result->seal_integrity_pct = (valid_cols * 100.0f) / length;

    // Decision Logic
    if (max_void_len > params->max_void_length_px) {
        out_result->verdict = VS_THERMAL_NG_SEAM_VOID;
    } else if (min_w < params->min_seal_width_px) {
        out_result->verdict = VS_THERMAL_NG_SEAM_TOO_NARROW;
    } else if (contamination) {
        out_result->verdict = VS_THERMAL_NG_CONTAMINATION;
    } else if (mean_t < params->min_seal_temp_c) {
        out_result->verdict = VS_THERMAL_NG_UNDERHEATED;
    } else {
        out_result->verdict = VS_THERMAL_PASS;
    }

    auto t_end = std::chrono::steady_clock::now();
    out_result->time_ms = std::chrono::duration<float, std::milli>(t_end - t_start).count();
    return 0;
}

int32_t vs_thermal_inspect_hot_glue(
    const void* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t format,
    const vs_thermal_glue_params* params,
    vs_thermal_glue_result* out_result)
{
    if (!src || !params || !out_result) return -1;
    if (params->struct_size != 0 && params->struct_size != sizeof(vs_thermal_glue_params)) return -1;
    if (out_result->struct_size != 0 && out_result->struct_size != sizeof(vs_thermal_glue_result)) return -1;
    if (width <= 0 || height <= 0 || stride <= 0) return -2;

    auto t_start = std::chrono::steady_clock::now();
    std::memset(out_result, 0, sizeof(vs_thermal_glue_result));
    out_result->struct_size = sizeof(vs_thermal_glue_result);

    int32_t rx = std::clamp(params->roi_x, 0, width - 1);
    int32_t ry = std::clamp(params->roi_y, 0, height - 1);
    int32_t rw = (params->roi_w > 0) ? std::min(params->roi_w, width - rx) : (width - rx);
    int32_t rh = (params->roi_h > 0) ? std::min(params->roi_h, height - ry) : (height - ry);

    if (rw <= 0 || rh <= 0) return -3;

    const bool is_vert = (params->is_vertical != 0);
    const int cross_dim = is_vert ? rw : rh;
    const int track_len = is_vert ? rh : rw;

    // Scan cross dimension to find discrete bead tracks
    std::vector<float> cross_profile(cross_dim, 0.0f);
    for (int c = 0; c < cross_dim; ++c) {
        float peak_t = -100.0f;
        for (int l = 0; l < track_len; ++l) {
            int32_t px = is_vert ? (rx + c) : (rx + l);
            int32_t py = is_vert ? (ry + l) : (ry + c);
            float t = get_temp_c(src, px, py, stride, format, params->t_min_calib, params->t_max_calib);
            if (t > peak_t) peak_t = t;
        }
        cross_profile[c] = peak_t;
    }

    // Find bead peak bands in cross dimension
    std::vector<int> bead_centers;
    bool in_bead = false;
    int bead_start = 0;

    for (int c = 0; c < cross_dim; ++c) {
        if (cross_profile[c] >= params->min_glue_temp_c) {
            if (!in_bead) {
                in_bead = true;
                bead_start = c;
            }
        } else {
            if (in_bead) {
                in_bead = false;
                bead_centers.push_back((bead_start + c) / 2);
            }
        }
    }
    if (in_bead) {
        bead_centers.push_back((bead_start + cross_dim) / 2);
    }

    out_result->detected_bead_count = static_cast<int32_t>(bead_centers.size());

    // Inspect each detected bead along its length
    float min_bead_len = 1e6f;
    float worst_gap = 0.0f;
    double total_glue_temp = 0.0;
    int total_glue_pixels = 0;

    for (int bc : bead_centers) {
        int bead_len = 0;
        int current_gap = 0;
        int max_gap_in_bead = 0;

        for (int l = 0; l < track_len; ++l) {
            // Sample a 3-pixel window around bead center
            float max_t = -100.0f;
            for (int dc = -1; dc <= 1; ++dc) {
                int c = std::clamp(bc + dc, 0, cross_dim - 1);
                int32_t px = is_vert ? (rx + c) : (rx + l);
                int32_t py = is_vert ? (ry + l) : (ry + c);
                float t = get_temp_c(src, px, py, stride, format, params->t_min_calib, params->t_max_calib);
                if (t > max_t) max_t = t;
            }

            if (max_t >= params->min_glue_temp_c) {
                // A gap counts only once hot glue follows it: cold after the bead's end is the bead's
                // length (min_bead_length_px), not a break.
                if (current_gap > max_gap_in_bead) max_gap_in_bead = current_gap;
                bead_len++;
                total_glue_temp += max_t;
                total_glue_pixels++;
                current_gap = 0;
            } else {
                if (bead_len > 0) { // Don't count gap before bead starts
                    current_gap++;
                }
            }
        }

        if (bead_len < min_bead_len) min_bead_len = bead_len;
        if (max_gap_in_bead > worst_gap) worst_gap = max_gap_in_bead;
    }

    out_result->min_bead_length_px = (bead_centers.empty()) ? 0.0f : min_bead_len;
    out_result->worst_gap_px = worst_gap;
    out_result->total_glue_area_px = total_glue_pixels;
    out_result->mean_glue_temp_c = (total_glue_pixels > 0) ? static_cast<float>(total_glue_temp / total_glue_pixels) : 0.0f;

    // Decision Logic
    if (out_result->detected_bead_count < params->expected_bead_count) {
        out_result->verdict = VS_THERMAL_NG_MISSING_BEAD;
        out_result->bonding_confidence = 20.0f;
    } else if (worst_gap > params->max_bead_gap_px) {
        out_result->verdict = VS_THERMAL_NG_BROKEN_BEAD;
        out_result->bonding_confidence = 40.0f;
    } else if (out_result->min_bead_length_px < params->min_bead_length_px) {
        out_result->verdict = VS_THERMAL_NG_BROKEN_BEAD;
        out_result->bonding_confidence = 50.0f;
    } else if (out_result->mean_glue_temp_c < params->min_glue_temp_c) {
        out_result->verdict = VS_THERMAL_NG_COLD_GLUE;
        out_result->bonding_confidence = 30.0f;
    } else {
        out_result->verdict = VS_THERMAL_PASS;
        out_result->bonding_confidence = 98.0f;
    }

    auto t_end = std::chrono::steady_clock::now();
    out_result->time_ms = std::chrono::duration<float, std::milli>(t_end - t_start).count();
    return 0;
}

int32_t vs_thermal_apply_palette(
    const void* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t format,
    float t_min_c,
    float t_max_c,
    int32_t palette,
    uint8_t* dst,
    int32_t dst_stride)
{
    if (!src || !dst || width <= 0 || height <= 0 || stride <= 0 || dst_stride <= 0) return -1;
    if (t_max_c <= t_min_c) t_max_c = t_min_c + 1.0f;

    uint8_t bgr_lut[256][3];
    if (palette == VS_PALETTE_RAINBOW) {
        generate_rainbow_lut(bgr_lut);
    } else {
        generate_ironbow_lut(bgr_lut); // Default Ironbow
    }

    const float inv_span = 255.0f / (t_max_c - t_min_c);

    for (int32_t y = 0; y < height; ++y) {
        uint8_t* row_dst = dst + y * dst_stride;

        for (int32_t x = 0; x < width; ++x) {
            float t = get_temp_c(src, x, y, stride, format, t_min_c, t_max_c);
            int32_t idx = std::clamp(static_cast<int32_t>(std::round((t - t_min_c) * inv_span)), 0, 255);

            row_dst[x * 3 + 0] = bgr_lut[idx][0]; // B
            row_dst[x * 3 + 1] = bgr_lut[idx][1]; // G
            row_dst[x * 3 + 2] = bgr_lut[idx][2]; // R
        }
    }

    return 0;
}

void vs_thermal_default_blister_params(vs_thermal_blister_params* p) {
    if (!p) return;
    p->struct_size = sizeof(vs_thermal_blister_params);
    p->card_roi_x = 0;
    p->card_roi_y = 0;
    p->card_roi_w = 0; // Entire image
    p->card_roi_h = 0;
    p->rows = 2;
    p->cols = 5;
    p->pocket_w_px = 24.0f;
    p->pocket_h_px = 16.0f;
    p->pitch_x_px = 36.0f;
    p->pitch_y_px = 28.0f;
    p->min_barrier_width_px = 8.0f;
    p->min_seal_temp_c = 68.0f;
    p->t_min_calib = 20.0f;
    p->t_max_calib = 120.0f;
}

void vs_thermal_diagnose_tooling(
    int32_t packaging_type,
    int32_t verdict,
    float asymmetry_metric,
    vs_thermal_tooling_advice* out_advice)
{
    if (!out_advice) return;
    std::memset(out_advice, 0, sizeof(vs_thermal_tooling_advice));
    out_advice->asymmetry_metric = asymmetry_metric;

    if (verdict == VS_THERMAL_PASS) {
        std::strncpy(out_advice->fault_code, "OK_NORMAL", sizeof(out_advice->fault_code) - 1);
        std::strncpy(out_advice->recommended_action, "Tooling parameters nominal. No adjustment required.", sizeof(out_advice->recommended_action) - 1);
        out_advice->severity = 0;
        return;
    }

    // Diagnostics per packaging type
    switch (packaging_type) {
    case 0: // Bottle induction
        if (verdict == VS_THERMAL_NG_MISSING_FOIL) {
            std::strncpy(out_advice->fault_code, "SUPPLY_FOIL_MISSING", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Check cap feeder bowl / foil liner inserter chute level.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        } else if (verdict == VS_THERMAL_NG_CROOKED_SEAL) {
            std::strncpy(out_advice->fault_code, "TOOL_BOTTLE_INDUCT_TILT", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Shift conveyor guide rails 3.5 mm toward center; verify 3-5 mm coil height above cap.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        } else if (verdict == VS_THERMAL_NG_OVERHEATED) {
            std::strncpy(out_advice->fault_code, "PROCESS_INDUCT_OVERHEAT", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Reduce induction generator power by -6% or check line stoppage interlock.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        } else if (verdict == VS_THERMAL_NG_UNDERHEATED) {
            std::strncpy(out_advice->fault_code, "PROCESS_INDUCT_UNDERHEAT", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Raise induction power potentiometer +6%; ensure chiller water temp < 25 deg C.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        } else {
            std::strncpy(out_advice->fault_code, "TOOL_INDUCTION_GAP", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Check capper spindle torque; uneven seal pressure on bottle lip.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        }
        break;

    case 1: // Pouch seam
        if (verdict == VS_THERMAL_NG_CONTAMINATION) {
            std::strncpy(out_advice->fault_code, "TOOL_FILLER_DRIP", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Adjust filling pump suck-back valve; lower nozzle bottoming height during fill.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        } else if (verdict == VS_THERMAL_NG_SEAM_TOO_NARROW) {
            std::strncpy(out_advice->fault_code, "TOOL_JAW_BOW_PRESSURE", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Check center jaw pivot pin play; reduce pneumatic clamp pressure from 6.0 to 4.8 bar.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        } else {
            std::strncpy(out_advice->fault_code, "TOOL_JAW_SEAM_VOID", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Clean carbonized film buildup on sealing jaw; check silicone rubber backing strip.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        }
        break;

    case 2: // Hot glue
        if (verdict == VS_THERMAL_NG_MISSING_BEAD) {
            std::strncpy(out_advice->fault_code, "TOOL_GLUE_NOZZLE_CLOG", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Execute purge cycle on gun; replace clogged 0.014'' nozzle tip; clean manifold filter.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        } else if (verdict == VS_THERMAL_NG_COLD_GLUE) {
            std::strncpy(out_advice->fault_code, "PROCESS_GLUE_COLD", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Increase heated hose zone temperature +10 deg C; shorten open-time to flap fold plow.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        } else {
            std::strncpy(out_advice->fault_code, "TOOL_GLUE_BEAD_GAP", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Inspect solenoid valve response time; check glue pump pressure regulator +0.4 bar.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        }
        break;

    case 3: // Blister pack
    default:
        if (asymmetry_metric > 18.0f || verdict == VS_THERMAL_NG_JAW_TILT) {
            std::strncpy(out_advice->fault_code, "TOOL_PLATEN_TILT_RIGHT", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Shim right-hand tie rod leveling screw +0.08 mm; verify jaw parallelism with feeler gauge.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        } else {
            std::strncpy(out_advice->fault_code, "TOOL_BLISTER_POCKET_LEAK", sizeof(out_advice->fault_code) - 1);
            std::strncpy(out_advice->recommended_action, "Clean sealing drum pocket with brass wire brush; check cavity knurling for mechanical gouges.", sizeof(out_advice->recommended_action) - 1);
            out_advice->severity = 2;
        }
        break;
    }
}

int32_t vs_thermal_inspect_blister(
    const void* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t format,
    const vs_thermal_blister_params* params,
    vs_thermal_blister_result* out_result)
{
    if (!src || !params || !out_result) return -1;
    if (params->struct_size != 0 && params->struct_size != sizeof(vs_thermal_blister_params)) return -1;
    if (out_result->struct_size != 0 && out_result->struct_size != sizeof(vs_thermal_blister_result)) return -1;
    if (width <= 0 || height <= 0 || stride <= 0) return -2;

    auto t_start = std::chrono::steady_clock::now();
    std::memset(out_result, 0, sizeof(vs_thermal_blister_result));
    out_result->struct_size = sizeof(vs_thermal_blister_result);
    out_result->compromised_cavity_idx = -1;

    int32_t rx = std::clamp(params->card_roi_x, 0, width - 1);
    int32_t ry = std::clamp(params->card_roi_y, 0, height - 1);
    int32_t rw = (params->card_roi_w > 0) ? std::min(params->card_roi_w, width - rx) : (width - rx);
    int32_t rh = (params->card_roi_h > 0) ? std::min(params->card_roi_h, height - ry) : (height - ry);

    const int rows = std::max(1, params->rows);
    const int cols = std::max(1, params->cols);

    const float pw = std::max(5.0f, params->pocket_w_px);
    const float ph = std::max(5.0f, params->pocket_h_px);
    const float pitch_x = (params->pitch_x_px > pw) ? params->pitch_x_px : (pw + 10.0f);
    const float pitch_y = (params->pitch_y_px > ph) ? params->pitch_y_px : (ph + 10.0f);

    const float margin_x = (rw - (cols * pitch_x)) * 0.5f;
    const float margin_y = (rh - (rows * pitch_y)) * 0.5f;

    // 1. Measure left-vs-right platen temperature asymmetry (jaw tilt check)
    double left_temp = 0.0, right_temp = 0.0;
    size_t left_cnt = 0, right_cnt = 0;
    const int mid_x = rx + rw / 2;

    for (int y = ry; y < ry + rh; y += 4) {
        for (int x = rx; x < rx + rw; x += 4) {
            float t = get_temp_c(src, x, y, stride, format, params->t_min_calib, params->t_max_calib);
            if (x < mid_x) {
                left_temp += t;
                left_cnt++;
            } else {
                right_temp += t;
                right_cnt++;
            }
        }
    }

    float mean_left = (left_cnt > 0) ? static_cast<float>(left_temp / left_cnt) : 0.0f;
    float mean_right = (right_cnt > 0) ? static_cast<float>(right_temp / right_cnt) : 0.0f;
    float asymmetry = std::abs(mean_left - mean_right);

    out_result->mean_flange_temp_c = (mean_left + mean_right) * 0.5f;

    // 2. Inspect each cavity pocket perimeter for moisture barrier continuity
    float global_min_barrier = 1e6f;
    int compromised_idx = -1;
    float worst_bx = 0.0f, worst_by = 0.0f;

    const float test_radius_in = std::max(pw, ph) * 0.5f + 1.0f;
    const float test_radius_out = test_radius_in + std::max(8.0f, params->min_barrier_width_px * 1.5f);

    for (int r = 0; r < rows; ++r) {
        for (int c = 0; c < cols; ++c) {
            int k = r * cols + c;

            float pc_x = rx + margin_x + c * pitch_x + pitch_x * 0.5f;
            float pc_y = ry + margin_y + r * pitch_y + pitch_y * 0.5f;

            // Sample 36 rays around cavity perimeter
            float cavity_min_barrier = 1e6f;
            float cav_worst_x = pc_x, cav_worst_y = pc_y;

            for (int a = 0; a < 36; ++a) {
                float rad = a * (3.141592653589793f / 18.0f);
                float cos_a = std::cos(rad);
                float sin_a = std::sin(rad);

                int hot_pixels = 0;
                for (float dist = test_radius_in; dist <= test_radius_out; dist += 1.0f) {
                    int px = static_cast<int>(std::round(pc_x + dist * cos_a));
                    int py = static_cast<int>(std::round(pc_y + dist * sin_a));

                    if (px >= rx && px < rx + rw && py >= ry && py < ry + rh) {
                        float t = get_temp_c(src, px, py, stride, format, params->t_min_calib, params->t_max_calib);
                        if (t >= params->min_seal_temp_c) {
                            hot_pixels++;
                        }
                    }
                }

                float barrier_w = static_cast<float>(hot_pixels);
                if (barrier_w < cavity_min_barrier) {
                    cavity_min_barrier = barrier_w;
                    cav_worst_x = pc_x + test_radius_in * cos_a;
                    cav_worst_y = pc_y + test_radius_in * sin_a;
                }
            }

            if (cavity_min_barrier < global_min_barrier) {
                global_min_barrier = cavity_min_barrier;
                worst_bx = cav_worst_x;
                worst_by = cav_worst_y;
            }

            if (cavity_min_barrier < params->min_barrier_width_px && compromised_idx == -1) {
                compromised_idx = k;
            }
        }
    }

    out_result->min_barrier_width_px = (global_min_barrier < 1e5f) ? global_min_barrier : 0.0f;
    out_result->worst_barrier_x = worst_bx;
    out_result->worst_barrier_y = worst_by;
    out_result->compromised_cavity_idx = compromised_idx;

    // Decision Logic
    if (compromised_idx >= 0) {
        out_result->verdict = VS_THERMAL_NG_BLISTER_BARRIER_BREACH;
    } else if (asymmetry > 18.0f) {
        out_result->verdict = VS_THERMAL_NG_JAW_TILT;
    } else {
        out_result->verdict = VS_THERMAL_PASS;
    }

    // Generate actionable tooling recommendation
    vs_thermal_diagnose_tooling(3, out_result->verdict, asymmetry, &out_result->advice);

    auto t_end = std::chrono::steady_clock::now();
    out_result->time_ms = std::chrono::duration<float, std::milli>(t_end - t_start).count();
    return 0;
}

} // extern "C"
