/*
 * polar.cpp - Implementation of the Polarimetric Reflection Decoupler Engine.
 */
#include "vs_polar.h"

#include <cmath>
#include <cstring>
#include <vector>
#include <algorithm>

namespace {

const float PI = 3.141592653589793f;

// Angular difference on [0..180) degrees circle
inline float angular_diff_deg(float a1, float a2) {
    float diff = std::abs(a1 - a2);
    if (diff > 90.0f) diff = 180.0f - diff;
    return diff;
}

} // namespace

extern "C" {

void vs_polar_default_params(vs_polar_params* p) {
    if (!p) return;
    std::memset(p, 0, sizeof(vs_polar_params));
    p->struct_size = sizeof(vs_polar_params);
    p->mosaic_layout = VS_POLAR_MOSAIC_SONY_POLARSENS;
    p->glare_suppress_alpha = 1.0f;
    p->specular_gain = 2.0f;
    p->dolp_defect_thresh = 0.45f;
    p->stress_gradient_thresh = 35.0f;
}

int32_t vs_polar_process_channels(
    const uint8_t* i0,
    const uint8_t* i45,
    const uint8_t* i90,
    const uint8_t* i135,
    int32_t width,
    int32_t height,
    int32_t stride,
    const vs_polar_params* params,
    uint8_t* diffuse_out,
    int32_t diffuse_stride,
    uint8_t* specular_out,
    int32_t specular_stride,
    uint8_t* dolp_out,
    int32_t dolp_stride,
    uint8_t* aolp_out,
    int32_t aolp_stride,
    vs_polar_stats* stats_out)
{
    if (!i0 || !i45 || !i90 || !i135 || width <= 0 || height <= 0 || stride < width) return -1;

    vs_polar_params p;
    if (params && params->struct_size == sizeof(vs_polar_params)) {
        p = *params;
    } else {
        vs_polar_default_params(&p);
    }

    double sum_dolp = 0.0;
    float max_dolp = 0.0f;
    int32_t glare_count = 0;
    float max_stress_grad = 0.0f;

    // Buffer for AoLP in degrees to compute stress gradients if requested
    std::vector<float> aolp_grid;
    if (stats_out || aolp_out) {
        aolp_grid.resize(static_cast<size_t>(width) * height);
    }

    const float alpha = p.glare_suppress_alpha;
    const float gain = p.specular_gain;

    for (int32_t y = 0; y < height; ++y) {
        const uint8_t* r0 = i0 + y * stride;
        const uint8_t* r45 = i45 + y * stride;
        const uint8_t* r90 = i90 + y * stride;
        const uint8_t* r135 = i135 + y * stride;

        uint8_t* row_diffuse = diffuse_out ? (diffuse_out + y * diffuse_stride) : nullptr;
        uint8_t* row_specular = specular_out ? (specular_out + y * specular_stride) : nullptr;
        uint8_t* row_dolp = dolp_out ? (dolp_out + y * dolp_stride) : nullptr;
        uint8_t* row_aolp = aolp_out ? (aolp_out + y * aolp_stride) : nullptr;
        float* row_aolp_deg = aolp_grid.empty() ? nullptr : (aolp_grid.data() + y * width);

        for (int32_t x = 0; x < width; ++x) {
            float v0 = static_cast<float>(r0[x]);
            float v45 = static_cast<float>(r45[x]);
            float v90 = static_cast<float>(r90[x]);
            float v135 = static_cast<float>(r135[x]);

            // Stokes parameters normalized to 8-bit intensity range
            float s0 = 0.25f * (v0 + v45 + v90 + v135);
            float s1 = v0 - v90;
            float s2 = v45 - v135;

            // Degree of Linear Polarization (DoLP) in [0..1]
            float lin_pol = std::sqrt(s1 * s1 + s2 * s2);
            float dolp = (s0 > 1e-4f) ? std::min(1.0f, lin_pol / (2.0f * s0)) : 0.0f;

            sum_dolp += dolp;
            if (dolp > max_dolp) max_dolp = dolp;

            // Angle of Linear Polarization (AoLP) [0..180 deg)
            float aolp_rad = 0.5f * std::atan2(s2, s1);
            if (aolp_rad < 0.0f) aolp_rad += PI;
            float aolp_deg = aolp_rad * (180.0f / PI);

            if (row_aolp_deg) row_aolp_deg[x] = aolp_deg;

            // 1. Diffuse Image: subtract polarized specular glare
            if (row_diffuse) {
                float val_diffuse = s0 * (1.0f - alpha * dolp);
                row_diffuse[x] = static_cast<uint8_t>(std::clamp(std::round(val_diffuse), 0.0f, 255.0f));
            }

            // 2. Specular Saliency Map: highlight polarized glare & scratches
            if (row_specular) {
                float val_specular = s0 * dolp * gain;
                row_specular[x] = static_cast<uint8_t>(std::clamp(std::round(val_specular), 0.0f, 255.0f));
            }

            // 3. DoLP Output Map
            if (row_dolp) {
                row_dolp[x] = static_cast<uint8_t>(std::clamp(std::round(dolp * 255.0f), 0.0f, 255.0f));
            }

            // 4. AoLP Output Map
            if (row_aolp) {
                float mapped = (aolp_deg / 180.0f) * 255.0f;
                row_aolp[x] = static_cast<uint8_t>(std::clamp(std::round(mapped), 0.0f, 255.0f));
            }

            // Track eliminated glare pixels
            if (dolp >= p.dolp_defect_thresh && s0 > 150.0f) {
                ++glare_count;
            }
        }
    }

    // 5. Stress Birefringence Gradient Inspection
    if (!aolp_grid.empty() && stats_out) {
        for (int32_t y = 1; y < height - 1; ++y) {
            for (int32_t x = 1; x < width - 1; ++x) {
                float a_curr = aolp_grid[y * width + x];
                float a_r = aolp_grid[y * width + (x + 1)];
                float a_b = aolp_grid[(y + 1) * width + x];

                float d_x = angular_diff_deg(a_r, a_curr);
                float d_y = angular_diff_deg(a_b, a_curr);
                float grad = std::sqrt(d_x * d_x + d_y * d_y);

                if (grad > max_stress_grad) max_stress_grad = grad;
            }
        }
    }

    if (stats_out) {
        stats_out->struct_size = sizeof(vs_polar_stats);
        stats_out->mean_dolp = static_cast<float>(sum_dolp / (static_cast<double>(width) * height));
        stats_out->max_dolp = max_dolp;
        stats_out->glare_pixel_count = glare_count;
        stats_out->max_stress_gradient = max_stress_grad;
        stats_out->is_stress_defective = (max_stress_grad >= p.stress_gradient_thresh) ? 1 : 0;
    }

    return 0;
}

int32_t vs_polar_process_mosaic(
    const uint8_t* raw_mosaic,
    int32_t width,
    int32_t height,
    int32_t stride,
    const vs_polar_params* params,
    uint8_t* diffuse_out,
    int32_t diffuse_stride,
    uint8_t* specular_out,
    int32_t specular_stride,
    uint8_t* dolp_out,
    int32_t dolp_stride,
    uint8_t* aolp_out,
    int32_t aolp_stride,
    vs_polar_stats* stats_out)
{
    if (!raw_mosaic || width < 2 || height < 2 || stride < width) return -1;

    int32_t out_w = width / 2;
    int32_t out_h = height / 2;

    std::vector<uint8_t> ch_i0(out_w * out_h);
    std::vector<uint8_t> ch_i45(out_w * out_h);
    std::vector<uint8_t> ch_i90(out_w * out_h);
    std::vector<uint8_t> ch_i135(out_w * out_h);

    // Demux Sony Polarsens 2x2 pattern:
    // (2y, 2x): 90 deg
    // (2y, 2x+1): 45 deg
    // (2y+1, 2x): 135 deg
    // (2y+1, 2x+1): 0 deg
    for (int32_t y = 0; y < out_h; ++y) {
        const uint8_t* row0 = raw_mosaic + (2 * y) * stride;
        const uint8_t* row1 = raw_mosaic + (2 * y + 1) * stride;

        uint8_t* d0 = ch_i0.data() + y * out_w;
        uint8_t* d45 = ch_i45.data() + y * out_w;
        uint8_t* d90 = ch_i90.data() + y * out_w;
        uint8_t* d135 = ch_i135.data() + y * out_w;

        for (int32_t x = 0; x < out_w; ++x) {
            d90[x] = row0[2 * x];
            d45[x] = row0[2 * x + 1];
            d135[x] = row1[2 * x];
            d0[x] = row1[2 * x + 1];
        }
    }

    return vs_polar_process_channels(
        ch_i0.data(),
        ch_i45.data(),
        ch_i90.data(),
        ch_i135.data(),
        out_w,
        out_h,
        out_w,
        params,
        diffuse_out,
        diffuse_stride,
        specular_out,
        specular_stride,
        dolp_out,
        dolp_stride,
        aolp_out,
        aolp_stride,
        stats_out);
}

} // extern "C"
