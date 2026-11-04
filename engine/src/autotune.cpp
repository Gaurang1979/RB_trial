#include "vs_autotune.h"
#include <cmath>
#include <cstring>
#include <cstdio>
#include <algorithm>
#include <vector>

extern "C" {

void vs_autotune_default_mount_config(vs_mount_config* cfg) {
    if (!cfg) return;
    std::memset(cfg, 0, sizeof(vs_mount_config));
    cfg->struct_size = sizeof(vs_mount_config);
    cfg->min_acceptable_uniformity = 0.65f;
    cfg->min_nominal_sharpness = 5.0f;
}

void vs_autotune_default_dust_config(vs_dust_config* cfg) {
    if (!cfg) return;
    std::memset(cfg, 0, sizeof(vs_dust_config));
    cfg->struct_size = sizeof(vs_dust_config);
    cfg->dust_threshold_dn = 25;
    cfg->min_spot_px = 3;
    cfg->max_spot_px = 60;
    cfg->max_allowed_spots = 4;
}

void vs_autotune_default_applicator_config(vs_applicator_config* cfg) {
    if (!cfg) return;
    std::memset(cfg, 0, sizeof(vs_applicator_config));
    cfg->struct_size = sizeof(vs_applicator_config);
    cfg->px_per_mm = 10.0f;
    cfg->screw_pitch_mm_per_turn = 0.5f;
    cfg->max_allowed_eccentricity_mm = 1.20f;
}

int vs_autotune_check_mount_tilt(
    const uint8_t* img,
    int w,
    int h,
    int stride,
    const vs_mount_config* cfg,
    vs_mount_diagnostic* diag)
{
    if (!img || !diag) return VS_AUTOTUNE_ERR_NULL_PTR;
    if (w < 32 || h < 32) return VS_AUTOTUNE_ERR_INVALID_DIM;

    vs_mount_config conf;
    if (cfg) conf = *cfg;
    else vs_autotune_default_mount_config(&conf);

    std::memset(diag, 0, sizeof(vs_mount_diagnostic));
    diag->struct_size = sizeof(vs_mount_diagnostic);

    // Compute zone boundaries
    int zw = w / 3;
    int zh = h / 3;

    float min_s = 1e9f;
    float max_s = -1e9f;
    float sum_s = 0.0f;

    for (int zy = 0; zy < 3; ++zy) {
        int y_start = zy * zh;
        int y_end = (zy == 2) ? (h - 1) : ((zy + 1) * zh);

        for (int zx = 0; zx < 3; ++zx) {
            int x_start = zx * zw;
            int x_end = (zx == 2) ? (w - 1) : ((zx + 1) * zw);

            int zone_idx = zy * 3 + zx;
            double zone_grad = 0.0;
            int count = 0;

            // Interior gradient calculation in this quadrant
            int y0 = std::max(1, y_start);
            int y1 = std::min(h - 2, y_end - 1);
            int x0 = std::max(1, x_start);
            int x1 = std::min(w - 2, x_end - 1);

            for (int y = y0; y <= y1; ++y) {
                const uint8_t* row_prev = img + (y - 1) * stride;
                const uint8_t* row_curr = img + y * stride;
                const uint8_t* row_next = img + (y + 1) * stride;

                for (int x = x0; x <= x1; ++x) {
                    int gx = std::abs((int)row_curr[x + 1] - (int)row_curr[x - 1]);
                    int gy = std::abs((int)row_next[x] - (int)row_prev[x]);
                    zone_grad += (gx + gy);
                    count++;
                }
            }

            float s = (count > 0) ? (float)(zone_grad / count) : 0.0f;
            diag->zone_sharpness[zone_idx] = s;
            if (s < min_s) min_s = s;
            if (s > max_s) max_s = s;
            sum_s += s;
        }
    }

    diag->min_sharpness = min_s;
    diag->max_sharpness = max_s;
    diag->mean_sharpness = sum_s / 9.0f;
    diag->uniformity_ratio = (max_s > 1e-4f) ? (min_s / max_s) : 1.0f;

    // Check if mount tilt (Scheimpflug error) is present
    if (diag->uniformity_ratio < conf.min_acceptable_uniformity && diag->max_sharpness >= conf.min_nominal_sharpness) {
        diag->is_mount_tilted = 1;

        // Determine primary tilt axis
        float top_row = (diag->zone_sharpness[0] + diag->zone_sharpness[1] + diag->zone_sharpness[2]) / 3.0f;
        float bot_row = (diag->zone_sharpness[6] + diag->zone_sharpness[7] + diag->zone_sharpness[8]) / 3.0f;
        float lft_col = (diag->zone_sharpness[0] + diag->zone_sharpness[3] + diag->zone_sharpness[6]) / 3.0f;
        float rgt_col = (diag->zone_sharpness[2] + diag->zone_sharpness[5] + diag->zone_sharpness[8]) / 3.0f;

        const char* v_dir = (top_row < bot_row * 0.75f) ? "Top Blur" : ((bot_row < top_row * 0.75f) ? "Bottom Blur" : "");
        const char* h_dir = (lft_col < rgt_col * 0.75f) ? "Left Blur" : ((rgt_col < lft_col * 0.75f) ? "Right Blur" : "");

        if (v_dir[0] && h_dir[0]) {
            snprintf(diag->tilt_direction, sizeof(diag->tilt_direction), "Tilt: %s / %s", v_dir, h_dir);
            snprintf(diag->advice, sizeof(diag->advice), "Scheimpflug tilt: adjust pitch and yaw mount screws");
        } else if (v_dir[0]) {
            snprintf(diag->tilt_direction, sizeof(diag->tilt_direction), "Pitch Tilt: %s", v_dir);
            snprintf(diag->advice, sizeof(diag->advice), "Adjust vertical camera tilt pitch bracket");
        } else if (h_dir[0]) {
            snprintf(diag->tilt_direction, sizeof(diag->tilt_direction), "Yaw Tilt: %s", h_dir);
            snprintf(diag->advice, sizeof(diag->advice), "Adjust horizontal camera yaw bracket");
        } else {
            snprintf(diag->tilt_direction, sizeof(diag->tilt_direction), "Corner Defocus");
            snprintf(diag->advice, sizeof(diag->advice), "Lens field curvature or peripheral defocus detected");
        }
    } else if (diag->mean_sharpness < conf.min_nominal_sharpness) {
        diag->is_mount_tilted = 0;
        snprintf(diag->tilt_direction, sizeof(diag->tilt_direction), "Uniform Low Contrast");
        snprintf(diag->advice, sizeof(diag->advice), "Overall lens focus blur or low contrast illumination");
    } else {
        diag->is_mount_tilted = 0;
        snprintf(diag->tilt_direction, sizeof(diag->tilt_direction), "Optimal Flat Plane");
        snprintf(diag->advice, sizeof(diag->advice), "Camera mount alignment verified (MTF focus uniform)");
    }

    return VS_AUTOTUNE_OK;
}

int vs_autotune_check_lens_dust(
    const uint8_t* background_or_min_img,
    int w,
    int h,
    int stride,
    const vs_dust_config* cfg,
    vs_dust_diagnostic* diag)
{
    if (!background_or_min_img || !diag) return VS_AUTOTUNE_ERR_NULL_PTR;
    if (w < 16 || h < 16) return VS_AUTOTUNE_ERR_INVALID_DIM;

    vs_dust_config conf;
    if (cfg) conf = *cfg;
    else vs_autotune_default_dust_config(&conf);

    std::memset(diag, 0, sizeof(vs_dust_diagnostic));
    diag->struct_size = sizeof(vs_dust_diagnostic);

    // Compute global background average intensity
    uint64_t sum_dn = 0;
    int step = 2; // Fast subsampled mean
    int count = 0;
    for (int y = 0; y < h; y += step) {
        const uint8_t* row = background_or_min_img + y * stride;
        for (int x = 0; x < w; x += step) {
            sum_dn += row[x];
            count++;
        }
    }
    int mean_bg = (count > 0) ? (int)(sum_dn / count) : 128;
    int dark_thresh = std::max(0, mean_bg - conf.dust_threshold_dn);

    // Simple connected component labeling for stationary dust spots
    std::vector<uint8_t> mask(w * h, 0);
    int total_dust_px = 0;

    for (int y = 1; y < h - 1; ++y) {
        const uint8_t* row = background_or_min_img + y * stride;
        for (int x = 1; x < w - 1; ++x) {
            if (row[x] < dark_thresh) {
                mask[y * w + x] = 1;
                total_dust_px++;
            }
        }
    }

    // Flood-fill connected blobs
    int spots_found = 0;
    std::vector<uint8_t> visited(w * h, 0);

    for (int y = 1; y < h - 1; ++y) {
        for (int x = 1; x < w - 1; ++x) {
            if (mask[y * w + x] && !visited[y * w + x]) {
                // BFS flood fill
                std::vector<int> qx, qy;
                qx.push_back(x);
                qy.push_back(y);
                visited[y * w + x] = 1;

                int blob_area = 0;
                size_t head = 0;

                while (head < qx.size()) {
                    int cx = qx[head];
                    int cy = qy[head++];
                    blob_area++;

                    // 4-neighborhood
                    const int dx[4] = {1, -1, 0, 0};
                    const int dy[4] = {0, 0, 1, -1};

                    for (int i = 0; i < 4; ++i) {
                        int nx = cx + dx[i];
                        int ny = cy + dy[i];
                        if (nx >= 0 && nx < w && ny >= 0 && ny < h) {
                            int idx = ny * w + nx;
                            if (mask[idx] && !visited[idx]) {
                                visited[idx] = 1;
                                qx.push_back(nx);
                                qy.push_back(ny);
                            }
                        }
                    }
                }

                if (blob_area >= conf.min_spot_px && blob_area <= conf.max_spot_px) {
                    spots_found++;
                }
            }
        }
    }

    diag->dust_spots_found = spots_found;
    diag->dust_coverage_pct = (float)(total_dust_px * 100.0) / (float)(w * h);
    diag->is_lens_contaminated = (spots_found > conf.max_allowed_spots) ? 1 : 0;

    if (diag->is_lens_contaminated) {
        snprintf(diag->advice, sizeof(diag->advice),
                 "Lens hygiene warning: %d dust spots detected (clean front element with optical wipe)",
                 spots_found);
    } else {
        snprintf(diag->advice, sizeof(diag->advice),
                 "Lens optic clean (%d spots within allowable limit)", spots_found);
    }

    return VS_AUTOTUNE_OK;
}

int vs_autotune_strobe_minus_ambient(
    const uint8_t* strobe_img,
    const uint8_t* ambient_img,
    uint8_t* clean_dst,
    int w,
    int h,
    int stride,
    float* out_ambient_leakage_pct)
{
    if (!strobe_img || !ambient_img || !clean_dst) return VS_AUTOTUNE_ERR_NULL_PTR;
    if (w <= 0 || h <= 0) return VS_AUTOTUNE_ERR_INVALID_DIM;

    uint64_t sum_strobe = 0;
    uint64_t sum_ambient = 0;

    for (int y = 0; y < h; ++y) {
        const uint8_t* s_row = strobe_img + y * stride;
        const uint8_t* a_row = ambient_img + y * stride;
        uint8_t* d_row = clean_dst + y * stride;

        for (int x = 0; x < w; ++x) {
            int s_val = s_row[x];
            int a_val = a_row[x];
            sum_strobe += s_val;
            sum_ambient += a_val;

            int diff = s_val - a_val;
            d_row[x] = (uint8_t)std::clamp(diff, 0, 255);
        }
    }

    if (out_ambient_leakage_pct) {
        *out_ambient_leakage_pct = (sum_strobe > 0) ? (float)(sum_ambient * 100.0 / sum_strobe) : 0.0f;
    }

    return VS_AUTOTUNE_OK;
}

int vs_autotune_analyze_lighting(
    const uint8_t* img,
    int w,
    int h,
    int stride,
    float target_mean_dn,
    vs_lighting_diagnostic* diag)
{
    if (!img || !diag) return VS_AUTOTUNE_ERR_NULL_PTR;
    if (w < 16 || h < 16) return VS_AUTOTUNE_ERR_INVALID_DIM;

    std::memset(diag, 0, sizeof(vs_lighting_diagnostic));
    diag->struct_size = sizeof(vs_lighting_diagnostic);

    uint64_t sum = 0;
    uint8_t min_v = 255;
    uint8_t max_v = 0;

    int box_w = w / 4;
    int box_h = h / 4;
    uint64_t sum_center = 0;
    int count_center = 0;
    uint64_t sum_corners = 0;
    int count_corners = 0;

    int cx0 = w / 2 - box_w / 2;
    int cx1 = cx0 + box_w;
    int cy0 = h / 2 - box_h / 2;
    int cy1 = cy0 + box_h;

    for (int y = 0; y < h; ++y) {
        const uint8_t* row = img + y * stride;
        bool in_v_corner = (y < box_h || y >= h - box_h);
        bool in_v_center = (y >= cy0 && y < cy1);

        for (int x = 0; x < w; ++x) {
            uint8_t v = row[x];
            sum += v;
            if (v < min_v) min_v = v;
            if (v > max_v) max_v = v;

            bool in_h_corner = (x < box_w || x >= w - box_w);
            bool in_h_center = (x >= cx0 && x < cx1);

            if (in_v_corner && in_h_corner) {
                sum_corners += v;
                count_corners++;
            }
            if (in_v_center && in_h_center) {
                sum_center += v;
                count_center++;
            }
        }
    }

    float mean_val = (float)sum / (float)(w * h);
    float mean_center = (count_center > 0) ? (float)sum_center / count_center : mean_val;
    float mean_corners = (count_corners > 0) ? (float)sum_corners / count_corners : mean_val;

    diag->mean_luminance = mean_val;
    diag->min_luminance = min_v;
    diag->max_luminance = max_v;
    diag->corner_to_center_ratio = (mean_center > 1e-4f) ? (mean_corners / mean_center) : 1.0f;

    // Day/Night and solar ingress indicator
    diag->is_daytime = (mean_val > 145.0f || diag->corner_to_center_ratio < 0.60f) ? 1 : 0;

    // Target exposure trim suggestion
    float target = (target_mean_dn > 10.0f && target_mean_dn < 240.0f) ? target_mean_dn : 128.0f;
    diag->suggested_exposure_trim = std::clamp(target / (mean_val + 1e-4f), 0.50f, 2.0f);

    if (diag->is_daytime) {
        snprintf(diag->lighting_advice, sizeof(diag->lighting_advice),
                 "Solar ingress detected (L=%.1f DN). Strobe overdrive trim: %.2fx",
                 mean_val, diag->suggested_exposure_trim);
    } else {
        snprintf(diag->lighting_advice, sizeof(diag->lighting_advice),
                 "Controlled lighting stable (L=%.1f DN, Uniformity=%.2f)",
                 mean_val, diag->corner_to_center_ratio);
    }

    return VS_AUTOTUNE_OK;
}

int vs_autotune_applicator_guidance(
    float cap_cx,
    float cap_cy,
    float cap_radius,
    float mark_cx,
    float mark_cy,
    float mark_radius,
    const vs_applicator_config* cfg,
    vs_applicator_advisory* advisory)
{
    (void)cap_radius;
    (void)mark_radius;
    if (!advisory) return VS_AUTOTUNE_ERR_NULL_PTR;

    vs_applicator_config conf;
    if (cfg) conf = *cfg;
    else vs_autotune_default_applicator_config(&conf);

    std::memset(advisory, 0, sizeof(vs_applicator_advisory));
    advisory->struct_size = sizeof(vs_applicator_advisory);

    float ppm = (conf.px_per_mm > 0.1f) ? conf.px_per_mm : 10.0f;
    float pitch = (conf.screw_pitch_mm_per_turn > 0.01f) ? conf.screw_pitch_mm_per_turn : 0.5f;

    float dx_px = mark_cx - cap_cx;
    float dy_px = mark_cy - cap_cy;

    float dx_mm = dx_px / ppm;
    float dy_mm = dy_px / ppm;
    float ecc_mm = std::sqrt(dx_mm * dx_mm + dy_mm * dy_mm);

    advisory->delta_x_mm = dx_mm;
    advisory->delta_y_mm = dy_mm;
    advisory->total_eccentricity_mm = ecc_mm;

    // Screw adjustment calculations
    // If mark is to the right (+dx), applicator must move left (-dx), requiring screw adjustment
    advisory->x_screw_turns = -dx_mm / pitch;
    advisory->y_screw_turns = -dy_mm / pitch;

    float tol_mm = (conf.max_allowed_eccentricity_mm > 0.1f) ? conf.max_allowed_eccentricity_mm : 1.20f;
    advisory->is_drift_critical = (ecc_mm >= tol_mm * 0.70f) ? 1 : 0;

    // Cpk estimation assuming normal distribution with 3-sigma at 60% of tolerance
    float est_sigma = (tol_mm * 0.60f) / 3.0f;
    advisory->cpk_estimate = (est_sigma > 1e-4f) ? ((tol_mm - ecc_mm) / (3.0f * est_sigma)) : 1.0f;

    if (advisory->is_drift_critical) {
        const char* h_cmd = (dx_mm > 0.10f) ? "CCW" : ((dx_mm < -0.10f) ? "CW" : "");
        const char* v_cmd = (dy_mm > 0.10f) ? "CCW" : ((dy_mm < -0.10f) ? "CW" : "");

        if (std::abs(dx_mm) > std::abs(dy_mm)) {
            snprintf(advisory->advisory_pill, sizeof(advisory->advisory_pill),
                     "Applicator drift: Turn Screw X by %.2f turns %s (Offset %.2fmm)",
                     std::abs(advisory->x_screw_turns), h_cmd, ecc_mm);
        } else {
            snprintf(advisory->advisory_pill, sizeof(advisory->advisory_pill),
                     "Applicator drift: Turn Screw Y by %.2f turns %s (Offset %.2fmm)",
                     std::abs(advisory->y_screw_turns), v_cmd, ecc_mm);
        }
    } else {
        snprintf(advisory->advisory_pill, sizeof(advisory->advisory_pill),
                 "Applicator centered (Eccentricity %.2fmm, Cpk=%.2f)",
                 ecc_mm, advisory->cpk_estimate);
    }

    return VS_AUTOTUNE_OK;
}

} // extern "C"
