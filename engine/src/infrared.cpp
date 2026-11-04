/*
 * infrared.cpp - Implementation of the Infrared Vision Inspection Engine.
 */
#include "vs_infrared.h"

#include <cmath>
#include <cstring>
#include <vector>
#include <algorithm>
#include <numeric>

extern "C" {

/* -------------------------------------------------------------------------- */
/* 1. See-Through Liquid Fill Level & Foam Rejection                          */
/* -------------------------------------------------------------------------- */

void vs_ir_default_fill_params(vs_ir_fill_params* p) {
    if (!p) return;
    std::memset(p, 0, sizeof(vs_ir_fill_params));
    p->struct_size = sizeof(vs_ir_fill_params);
    p->roi_x = 0;
    p->roi_y = 0;
    p->roi_w = 0;
    p->roi_h = 0;
    p->datum_y = 0.0f;
    p->px_per_mm = 10.0f;
    p->target_fill_mm = 50.0f;
    p->tol_underfill_mm = 2.0f;
    p->tol_overfill_mm = 2.0f;
    p->min_meniscus_contrast = 15.0f;
    p->ignore_foam = 1;
}

int32_t vs_ir_measure_fill_level(
    const uint8_t* nir_image,
    int32_t width,
    int32_t height,
    int32_t stride,
    const vs_ir_fill_params* params,
    vs_ir_fill_result* result)
{
    if (!nir_image || !params || !result || width <= 0 || height <= 0 || stride < width) return -1;

    int32_t rx = std::clamp(params->roi_x, 0, width - 1);
    int32_t ry = std::clamp(params->roi_y, 0, height - 1);
    int32_t rw = std::clamp(params->roi_w, 1, width - rx);
    int32_t rh = std::clamp(params->roi_h, 3, height - ry);

    std::memset(result, 0, sizeof(vs_ir_fill_result));
    result->struct_size = sizeof(vs_ir_fill_result);

    // 1. Compute horizontal average intensity profile P(y) across ROI
    std::vector<float> profile(rh);
    for (int32_t y = 0; y < rh; ++y) {
        const uint8_t* row = nir_image + (ry + y) * stride + rx;
        uint32_t sum = 0;
        for (int32_t x = 0; x < rw; ++x) {
            sum += row[x];
        }
        profile[y] = static_cast<float>(sum) / rw;
    }

    // 2. Compute 1D derivative D(y) to find meniscus edge
    // Liquid meniscus forms a sharp transition (air above is bright, liquid below absorbs or refracts)
    std::vector<float> deriv(rh, 0.0f);
    float max_deriv = 0.0f;
    int32_t best_y = -1;

    // Foam detection: track any secondary transition above the liquid
    float first_foam_y = -1.0f;

    for (int32_t y = 1; y < rh - 1; ++y) {
        // Central difference derivative
        float d = std::abs(profile[y + 1] - profile[y - 1]) * 0.5f;
        deriv[y] = d;

        if (d > max_deriv) {
            max_deriv = d;
            best_y = y;
        }

        // Detect foam onset (milder gradient preceding liquid step)
        if (params->ignore_foam && d > params->min_meniscus_contrast * 0.4f && first_foam_y < 0.0f) {
            first_foam_y = static_cast<float>(ry + y);
        }
    }

    if (best_y < 1 || max_deriv < params->min_meniscus_contrast) {
        // No clear meniscus detected (e.g. empty or occluded)
        result->is_underfill = 1;
        result->is_ok = 0;
        return 0;
    }

    // Sub-pixel parabolic refinement around peak derivative
    float y_sub = static_cast<float>(best_y);
    float d_left = deriv[best_y - 1];
    float d_mid = deriv[best_y];
    float d_right = deriv[best_y + 1];
    float denom = (d_left - 2.0f * d_mid + d_right);
    if (std::abs(denom) > 1e-4f) {
        float offset = 0.5f * (d_left - d_right) / denom;
        y_sub += std::clamp(offset, -0.5f, 0.5f);
    }

    float meniscus_y = ry + y_sub;
    result->meniscus_y_px = meniscus_y;
    result->meniscus_contrast = max_deriv;

    // Convert pixel position to physical fill height in mm
    float scale = (params->px_per_mm > 0.001f) ? params->px_per_mm : 1.0f;
    if (params->datum_y >= meniscus_y) {
        // Datum is at bottle bottom, fill height increases upward
        result->fill_height_mm = (params->datum_y - meniscus_y) / scale;
    } else {
        // Datum is at bottle mouth / top, fill distance is downward
        result->fill_height_mm = (meniscus_y - params->datum_y) / scale;
    }

    if (first_foam_y > 0.0f && first_foam_y < meniscus_y) {
        result->foam_thickness_mm = (meniscus_y - first_foam_y) / scale;
    }

    // Check tolerances
    float target = params->target_fill_mm;
    float tol_under = params->tol_underfill_mm;
    float tol_over = params->tol_overfill_mm;

    result->is_underfill = (result->fill_height_mm < (target - tol_under)) ? 1 : 0;
    result->is_overfill = (result->fill_height_mm > (target + tol_over)) ? 1 : 0;
    result->is_ok = (!result->is_underfill && !result->is_overfill) ? 1 : 0;

    return 0;
}

/* -------------------------------------------------------------------------- */
/* 2. Metameric Normalized Difference Ratio (NDMI) & Security Inks            */
/* -------------------------------------------------------------------------- */

void vs_ir_default_metameric_params(vs_ir_metameric_params* p) {
    if (!p) return;
    std::memset(p, 0, sizeof(vs_ir_metameric_params));
    p->struct_size = sizeof(vs_ir_metameric_params);
    p->dye_ndmi_thresh = 0.25f;
    p->carbon_ndmi_max = 0.15f;
    p->min_security_area_px = 50;
}

int32_t vs_ir_verify_metameric(
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
    vs_ir_metameric_result* result)
{
    if (!vis_image || !nir_image || !params || !result || width <= 0 || height <= 0) return -1;

    std::memset(result, 0, sizeof(vs_ir_metameric_result));
    result->struct_size = sizeof(vs_ir_metameric_result);

    int32_t carbon_pixels = 0;
    int32_t dye_pixels = 0;
    double sum_security_ndmi = 0.0;

    for (int32_t y = 0; y < height; ++y) {
        const uint8_t* row_vis = vis_image + y * vis_stride;
        const uint8_t* row_nir = nir_image + y * nir_stride;
        uint8_t* row_ndmi = ndmi_out ? (ndmi_out + y * ndmi_stride) : nullptr;
        uint8_t* row_mask = mask_out ? (mask_out + y * mask_stride) : nullptr;

        for (int32_t x = 0; x < width; ++x) {
            float v = static_cast<float>(row_vis[x]);
            float n = static_cast<float>(row_nir[x]);

            // NDMI = (NIR - VIS) / (NIR + VIS + 1.0)
            float ndmi = (n - v) / (n + v + 1.0f);

            if (row_ndmi) {
                // Map [-1.0 .. 1.0] to [0 .. 255]
                float mapped = (ndmi + 1.0f) * 127.5f;
                row_ndmi[x] = static_cast<uint8_t>(std::clamp(std::round(mapped), 0.0f, 255.0f));
            }

            // Region classification:
            // Printed ink is dark in VIS (v < 130)
            bool is_print = (v < 130.0f);
            bool is_carbon = is_print && (ndmi <= params->carbon_ndmi_max);
            bool is_dye = is_print && (ndmi >= params->dye_ndmi_thresh);

            if (is_carbon) {
                ++carbon_pixels;
                sum_security_ndmi += ndmi;
                if (row_mask) row_mask[x] = 255;
            } else {
                if (row_mask) row_mask[x] = 0;
            }

            if (is_dye) {
                ++dye_pixels;
            }
        }
    }

    result->verified_ink_pixels = carbon_pixels;
    result->transparent_dye_pixels = dye_pixels;
    result->mean_security_ndmi = (carbon_pixels > 0) ? static_cast<float>(sum_security_ndmi / carbon_pixels) : 0.0f;
    result->is_authentic = (carbon_pixels >= params->min_security_area_px) ? 1 : 0;

    return 0;
}

/* -------------------------------------------------------------------------- */
/* 3. Moisture Barrier Breach & Seam Liquid Ingress (SWIR 1450nm)             */
/* -------------------------------------------------------------------------- */

void vs_ir_default_moisture_params(vs_ir_moisture_params* p) {
    if (!p) return;
    std::memset(p, 0, sizeof(vs_ir_moisture_params));
    p->struct_size = sizeof(vs_ir_moisture_params);
    p->absorption_thresh = 40.0f;
    p->min_nominal_barrier_px = 30.0f;
    p->min_safe_barrier_px = 15.0f;
}

int32_t vs_ir_detect_moisture_seam(
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
    vs_ir_moisture_result* result)
{
    if (!swir_image || !params || !result || width <= 0 || height <= 0) return -1;

    int32_t rx = std::clamp(seam_roi_x, 0, width - 1);
    int32_t ry = std::clamp(seam_roi_y, 0, height - 1);
    int32_t rw = std::clamp(seam_roi_w, 1, width - rx);
    int32_t rh = std::clamp(seam_roi_h, 1, height - ry);

    std::memset(result, 0, sizeof(vs_ir_moisture_result));
    result->struct_size = sizeof(vs_ir_moisture_result);

    // Compute nominal dry background intensity inside seam
    double total_sum = 0.0;
    for (int32_t y = 0; y < rh; ++y) {
        const uint8_t* row = swir_image + (ry + y) * stride + rx;
        for (int32_t x = 0; x < rw; ++x) {
            total_sum += row[x];
        }
    }
    float mean_val = static_cast<float>(total_sum / (static_cast<double>(rw) * rh));
    float water_cutoff = mean_val - params->absorption_thresh;

    int32_t moisture_count = 0;
    float min_barrier = static_cast<float>(rh); // initial max barrier

    // Assume seam runs horizontally along X, barrier thickness is along Y
    // (If seam is vertical, rw and rh can be transposed or inspected symmetrically)
    for (int32_t x = 0; x < rw; ++x) {
        int32_t dry_count = 0;

        for (int32_t y = 0; y < rh; ++y) {
            uint8_t pixel = swir_image[(ry + y) * stride + (rx + x)];
            bool is_wet = (pixel < water_cutoff);

            if (is_wet) {
                ++moisture_count;
                if (mask_out) mask_out[(ry + y) * mask_stride + (rx + x)] = 255;
            } else {
                ++dry_count;
                if (mask_out) mask_out[(ry + y) * mask_stride + (rx + x)] = 0;
            }
        }

        if (dry_count < min_barrier) {
            min_barrier = static_cast<float>(dry_count);
        }
    }

    result->min_remaining_barrier_px = min_barrier;
    result->moisture_pixel_count = moisture_count;
    result->has_complete_breach = (min_barrier <= 0.5f) ? 1 : 0;
    result->is_seal_ok = (!result->has_complete_breach && min_barrier >= params->min_safe_barrier_px) ? 1 : 0;

    return 0;
}

/* -------------------------------------------------------------------------- */
/* 4. Solar Cell Electroluminescence (EL) & Silicon Micro-Crack Extractor     */
/* -------------------------------------------------------------------------- */

void vs_ir_default_crack_params(vs_ir_crack_params* p) {
    if (!p) return;
    std::memset(p, 0, sizeof(vs_ir_crack_params));
    p->struct_size = sizeof(vs_ir_crack_params);
    p->hessian_sigma = 1.5f;
    p->crack_response_thresh = 12.0f;
    p->min_crack_length_px = 8;
}

int32_t vs_ir_extract_cracks(
    const uint8_t* el_image,
    int32_t width,
    int32_t height,
    int32_t stride,
    const vs_ir_crack_params* params,
    uint8_t* crack_mask_out,
    int32_t mask_stride,
    vs_ir_crack_result* result)
{
    if (!el_image || !params || !result || width < 4 || height < 4) return -1;

    std::memset(result, 0, sizeof(vs_ir_crack_result));
    result->struct_size = sizeof(vs_ir_crack_result);

    int32_t crack_pixels = 0;
    float max_resp = 0.0f;
    const float thresh = params->crack_response_thresh;

    // Fast 2D Hessian ridge detector for dark curvilinear cracks
    for (int32_t y = 1; y < height - 1; ++y) {
        const uint8_t* row_p = el_image + (y - 1) * stride;
        const uint8_t* row_c = el_image + y * stride;
        const uint8_t* row_n = el_image + (y + 1) * stride;
        uint8_t* row_mask = crack_mask_out ? (crack_mask_out + y * mask_stride) : nullptr;

        for (int32_t x = 1; x < width - 1; ++x) {
            float c = static_cast<float>(row_c[x]);
            float l = static_cast<float>(row_c[x - 1]);
            float r = static_cast<float>(row_c[x + 1]);
            float t = static_cast<float>(row_p[x]);
            float b = static_cast<float>(row_n[x]);

            float tr = static_cast<float>(row_p[x + 1]);
            float tl = static_cast<float>(row_p[x - 1]);
            float br = static_cast<float>(row_n[x + 1]);
            float bl = static_cast<float>(row_n[x - 1]);

            // Discrete Hessian second derivatives
            // For a dark crack, the cross-section is a valley: d2I/dx2 > 0
            float dxx = l - 2.0f * c + r;
            float dyy = t - 2.0f * c + b;
            float dxy = 0.25f * (br - bl - tr + tl);

            // Eigenvalues of 2x2 symmetric matrix:
            // trace = dxx + dyy
            // diff = dxx - dyy
            // lambda_max = 0.5 * (trace + sqrt(diff^2 + 4 * dxy^2))
            float trace = dxx + dyy;
            float diff = dxx - dyy;
            float disc = std::sqrt(diff * diff + 4.0f * dxy * dxy);
            float lambda_max = 0.5f * (trace + disc);

            if (lambda_max > max_resp) max_resp = lambda_max;

            bool is_crack = (lambda_max >= thresh);
            if (is_crack) {
                ++crack_pixels;
                if (row_mask) row_mask[x] = 255;
            } else {
                if (row_mask) row_mask[x] = 0;
            }
        }
    }

    result->crack_pixel_count = crack_pixels;
    result->max_crack_contrast = max_resp;
    result->is_cracked = (crack_pixels >= params->min_crack_length_px) ? 1 : 0;

    return 0;
}

} // extern "C"
