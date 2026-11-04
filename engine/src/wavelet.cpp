/*
 * wavelet.cpp - Implementation of the Wavelet Homomorphic Scratch & Micro-Defect Filter Engine.
 */
#include "vs_wavelet.h"

#include <cmath>
#include <cstring>
#include <vector>
#include <algorithm>
#include <numeric>

namespace {

struct LogLut {
    float to_log[256];
    uint8_t to_linear[1024];

    LogLut() {
        const float max_val = std::log(256.0f);
        for (int i = 0; i < 256; ++i) {
            to_log[i] = std::log(static_cast<float>(i) + 1.0f) * (255.0f / max_val);
        }
        for (int i = 0; i < 1024; ++i) {
            float norm = static_cast<float>(i) * (max_val / 255.0f);
            float exp_val = std::exp(norm) - 1.0f;
            to_linear[i] = static_cast<uint8_t>(std::clamp(std::round(exp_val), 0.0f, 255.0f));
        }
    }
};

const LogLut g_log_lut;

} // namespace

struct vs_wavelet_filter {
    vs_wavelet_params params{};
    // Reusable working buffers to ensure ZERO allocations in real-time loop
    mutable std::vector<float> img_in;
    mutable std::vector<float> low_h;
    mutable std::vector<float> high_h;
    mutable std::vector<float> LL;
    mutable std::vector<float> LH;
    mutable std::vector<float> HL;
    mutable std::vector<float> HH;
    mutable std::vector<float> residual;
    mutable std::vector<float> current_LL;

    void ensure_capacity(size_t n) const {
        if (img_in.size() < n) {
            img_in.resize(n);
            low_h.resize(n);
            high_h.resize(n);
            LL.resize(n);
            LH.resize(n);
            HL.resize(n);
            HH.resize(n);
            residual.resize(n);
            current_LL.resize(n);
        }
    }
};

extern "C" {

void vs_wavelet_default_params(vs_wavelet_params* p) {
    if (!p) return;
    std::memset(p, 0, sizeof(vs_wavelet_params));
    p->struct_size = sizeof(vs_wavelet_params);
    p->levels = 2;
    p->texture_mode = VS_TEXTURE_AUTO;
    p->suppression_strength = 0.85f;
    p->scratch_gain = 3.0f;
    p->homomorphic_enable = 1;
    p->defect_contrast_thresh = 35.0f;
    p->min_defect_area_px = 5;
}

vs_wavelet_filter* vs_wavelet_create(const vs_wavelet_params* p) {
    auto* filter = new (std::nothrow) vs_wavelet_filter();
    if (!filter) return nullptr;

    if (p && p->struct_size == sizeof(vs_wavelet_params)) {
        filter->params = *p;
    } else {
        vs_wavelet_default_params(&filter->params);
    }

    filter->params.levels = std::clamp(filter->params.levels, 1, 3);
    filter->params.suppression_strength = std::clamp(filter->params.suppression_strength, 0.0f, 1.0f);
    filter->params.scratch_gain = std::max(0.1f, filter->params.scratch_gain);
    filter->params.defect_contrast_thresh = std::max(1.0f, filter->params.defect_contrast_thresh);
    filter->params.min_defect_area_px = std::max(1, filter->params.min_defect_area_px);

    // Pre-allocate for standard 1024x1024 frame
    filter->ensure_capacity(1024 * 1024);

    return filter;
}

void vs_wavelet_destroy(vs_wavelet_filter* filter) {
    delete filter;
}

int32_t vs_wavelet_process(
    const vs_wavelet_filter* filter,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    uint8_t* residual_out,
    int32_t residual_stride,
    uint8_t* mask_out,
    int32_t mask_stride,
    vs_wavelet_stats* stats_out)
{
    if (!filter || !src || width < 4 || height < 4 || stride < width) return -1;

    const vs_wavelet_params& p = filter->params;
    const size_t num_pixels = static_cast<size_t>(width) * height;

    filter->ensure_capacity(num_pixels);

    float* img_in = filter->img_in.data();
    float* low_h = filter->low_h.data();
    float* high_h = filter->high_h.data();
    float* LL = filter->LL.data();
    float* LH = filter->LH.data();
    float* HL = filter->HL.data();
    float* HH = filter->HH.data();
    float* residual = filter->residual.data();
    float* current_LL = filter->current_LL.data();

    std::fill(residual, residual + num_pixels, 0.0f);

    // 1. Homomorphic transform (optional log mapping)
    if (p.homomorphic_enable) {
        for (int32_t y = 0; y < height; ++y) {
            const uint8_t* row_src = src + y * stride;
            float* row_dst = img_in + y * width;
            for (int32_t x = 0; x < width; ++x) {
                row_dst[x] = g_log_lut.to_log[row_src[x]];
            }
        }
    } else {
        for (int32_t y = 0; y < height; ++y) {
            const uint8_t* row_src = src + y * stride;
            float* row_dst = img_in + y * width;
            for (int32_t x = 0; x < width; ++x) {
                row_dst[x] = static_cast<float>(row_src[x]);
            }
        }
    }

    std::memcpy(current_LL, img_in, num_pixels * sizeof(float));

    // 2. 2D Stationary Wavelet Decomposition (SWT / à trous)
    double energy_lh = 0.0;
    double energy_hl = 0.0;
    double energy_hh = 0.0;

    int32_t detected_orient = p.texture_mode;

    for (int level = 0; level < p.levels; ++level) {
        const int32_t step = 1 << level; // 1, 2, 4...

        // Horizontal filtering (Cache-friendly row-major)
        for (int32_t y = 0; y < height; ++y) {
            const float* row = current_LL + y * width;
            float* row_low = low_h + y * width;
            float* row_high = high_h + y * width;

            for (int32_t x = 0; x < width; ++x) {
                int32_t x_prev = (x >= step) ? (x - step) : 0;
                int32_t x_next = (x + step < width) ? (x + step) : (width - 1);

                float p_val = row[x_prev];
                float c_val = row[x];
                float n_val = row[x_next];

                row_low[x] = 0.25f * p_val + 0.5f * c_val + 0.25f * n_val;
                row_high[x] = -0.25f * p_val + 0.5f * c_val - 0.25f * n_val;
            }
        }

        // Vertical filtering (Cache-friendly row-major)
        for (int32_t y = 0; y < height; ++y) {
            int32_t y_prev = (y >= step) ? (y - step) : 0;
            int32_t y_next = (y + step < height) ? (y + step) : (height - 1);

            const float* row_low_p = low_h + y_prev * width;
            const float* row_low_c = low_h + y * width;
            const float* row_low_n = low_h + y_next * width;

            const float* row_high_p = high_h + y_prev * width;
            const float* row_high_c = high_h + y * width;
            const float* row_high_n = high_h + y_next * width;

            float* row_LL = LL + y * width;
            float* row_HL = HL + y * width;
            float* row_LH = LH + y * width;
            float* row_HH = HH + y * width;

            for (int32_t x = 0; x < width; ++x) {
                float low_p = row_low_p[x];
                float low_c = row_low_c[x];
                float low_n = row_low_n[x];

                float high_p = row_high_p[x];
                float high_c = row_high_c[x];
                float high_n = row_high_n[x];

                // LL: low-low (approximation)
                row_LL[x] = 0.25f * low_p + 0.5f * low_c + 0.25f * low_n;
                // HL: low-high (vertical edge / horizontal lines)
                row_HL[x] = -0.25f * low_p + 0.5f * low_c - 0.25f * low_n;
                // LH: high-low (horizontal edge / vertical lines)
                row_LH[x] = 0.25f * high_p + 0.5f * high_c + 0.25f * high_n;
                // HH: high-high (diagonal details)
                row_HH[x] = -0.25f * high_p + 0.5f * high_c - 0.25f * high_n;

                if (level == 0) {
                    energy_lh += row_LH[x] * row_LH[x];
                    energy_hl += row_HL[x] * row_HL[x];
                    energy_hh += row_HH[x] * row_HH[x];
                }
            }
        }

        // Texture orientation classification
        if (level == 0 && p.texture_mode == VS_TEXTURE_AUTO) {
            double total_e = energy_lh + energy_hl + energy_hh + 1e-6;
            double ratio_hl = energy_hl / total_e;
            double ratio_lh = energy_lh / total_e;

            if (ratio_hl > 0.45 && ratio_hl > 1.8 * ratio_lh) {
                detected_orient = VS_TEXTURE_HORIZONTAL;
            } else if (ratio_lh > 0.45 && ratio_lh > 1.8 * ratio_hl) {
                detected_orient = VS_TEXTURE_VERTICAL;
            } else if (ratio_hl > 0.25 && ratio_lh > 0.25) {
                detected_orient = VS_TEXTURE_KNURLED_GRID;
            } else {
                detected_orient = VS_TEXTURE_DIAGONAL;
            }
        }

        // 3. Directional Texture Shrinkage
        float suppress_lh = 0.0f;
        float suppress_hl = 0.0f;
        float suppress_hh = 0.0f;

        switch (detected_orient) {
            case VS_TEXTURE_HORIZONTAL:
                suppress_hl = p.suppression_strength;
                suppress_hh = p.suppression_strength * 0.5f;
                suppress_lh = 0.0f;
                break;
            case VS_TEXTURE_VERTICAL:
                suppress_lh = p.suppression_strength;
                suppress_hh = p.suppression_strength * 0.5f;
                suppress_hl = 0.0f;
                break;
            case VS_TEXTURE_KNURLED_GRID:
                suppress_lh = p.suppression_strength * 0.95f;
                suppress_hl = p.suppression_strength * 0.95f;
                suppress_hh = p.suppression_strength * 0.6f;
                break;
            case VS_TEXTURE_DIAGONAL:
            default:
                suppress_hh = p.suppression_strength;
                suppress_lh = p.suppression_strength * 0.4f;
                suppress_hl = p.suppression_strength * 0.4f;
                break;
        }

        // In multi-scale representation:
        // Total high-frequency detail = (current_LL - LL)
        // Periodic texture component to subtract = (suppress_lh * LH + suppress_hl * HL + suppress_hh * HH)
        // Defect detail = (current_LL - LL) - PeriodicTexture
        for (size_t i = 0; i < num_pixels; ++i) {
            float total_detail = current_LL[i] - LL[i];
            float periodic_texture = (LH[i] * suppress_lh) + (HL[i] * suppress_hl) + (HH[i] * suppress_hh);
            float defect_signal = total_detail - periodic_texture;

            residual[i] += defect_signal;
        }

        std::memcpy(current_LL, LL, num_pixels * sizeof(float));
    }

    // 4. Output Mapping and Defect Statistics
    int32_t defect_count = 0;
    float max_contrast = 0.0f;
    const float gain = p.scratch_gain;
    const float thresh = p.defect_contrast_thresh;

    for (int32_t y = 0; y < height; ++y) {
        uint8_t* row_res = residual_out ? (residual_out + y * residual_stride) : nullptr;
        uint8_t* row_mask = mask_out ? (mask_out + y * mask_stride) : nullptr;
        const float* row_diff = residual + y * width;

        for (int32_t x = 0; x < width; ++x) {
            float val = row_diff[x] * gain;
            float abs_val = std::abs(val);
            if (abs_val > max_contrast) max_contrast = abs_val;

            float pixel_val = 128.0f + val;
            uint8_t out_byte = static_cast<uint8_t>(std::clamp(std::round(pixel_val), 0.0f, 255.0f));

            if (row_res) {
                row_res[x] = out_byte;
            }

            bool is_defect = abs_val >= thresh;
            if (is_defect) {
                ++defect_count;
                if (row_mask) row_mask[x] = 255;
            } else {
                if (row_mask) row_mask[x] = 0;
            }
        }
    }

    if (stats_out) {
        stats_out->struct_size = sizeof(vs_wavelet_stats);
        stats_out->defect_pixel_count = defect_count;
        stats_out->max_scratch_contrast = max_contrast;
        stats_out->texture_energy_suppressed = p.suppression_strength * 100.0f;
        stats_out->detected_orientation = detected_orient;
        stats_out->is_defective = (defect_count >= p.min_defect_area_px) ? 1 : 0;
    }

    return 0;
}

} // extern "C"
