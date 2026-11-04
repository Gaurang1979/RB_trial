/*
 * rectify.cpp - Implementation of Conveyor Rolling-Shutter Rectification Tool (AG-06)
 */
#include "vs_rectify.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <vector>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

namespace {

inline double deg2rad(double deg) {
    return deg * (M_PI / 180.0);
}

inline double rad2deg(double rad) {
    return rad * (180.0 / M_PI);
}

} // namespace

void vs_rect_default_params(vs_rect_params* p) {
    if (!p) return;
    std::memset(p, 0, sizeof(vs_rect_params));
    p->struct_size = sizeof(vs_rect_params);
    p->line_speed_mm_s = 0.0f;       /* stationary by default */
    p->mm_per_px = 0.08f;            /* typical 20 MP case aggregation scale */
    p->row_time_us = 22.5f;          /* Sony IMX183 standard row readout */
    p->motion_angle_deg = 0.0f;      /* +X direction (standard conveyor) */
    p->ref_y = -1.0f;                /* -1 indicates auto-center (height / 2) */
    p->fill_value = 0;               /* black background fill */
    p->interpolation = 1;            /* Bilinear interpolation */
}

int32_t vs_rect_calculate_shear(const vs_rect_params* p, float* kx, float* ky) {
    if (!p || p->struct_size < (int32_t)sizeof(vs_rect_params) || !kx || !ky) {
        return -1;
    }
    if (p->mm_per_px <= 1e-7f || p->row_time_us <= 0.0f) {
        return -2;
    }

    const double angle_rad = deg2rad(p->motion_angle_deg);
    const double speed_px_per_s = (double)p->line_speed_mm_s / (double)p->mm_per_px;
    const double row_time_s = (double)p->row_time_us * 1e-6;
    const double speed_px_per_row = speed_px_per_s * row_time_s;

    *kx = (float)(speed_px_per_row * std::cos(angle_rad));
    *ky = (float)(speed_px_per_row * std::sin(angle_rad));
    return 0;
}

int32_t vs_rect_distorted_to_rectified(const vs_rect_params* p,
                                       float dist_x,
                                       float dist_y,
                                       float* rect_x,
                                       float* rect_y) {
    if (!p || !rect_x || !rect_y) return -1;
    float kx = 0.0f, ky = 0.0f;
    int32_t rc = vs_rect_calculate_shear(p, &kx, &ky);
    if (rc != 0) return rc;

    const float ref_y = (p->ref_y >= 0.0f) ? p->ref_y : 0.0f;
    const float dy = dist_y - ref_y;
    *rect_x = dist_x - kx * dy;
    *rect_y = dist_y - ky * dy;
    return 0;
}

int32_t vs_rect_rectified_to_distorted(const vs_rect_params* p,
                                       float rect_x,
                                       float rect_y,
                                       float* dist_x,
                                       float* dist_y) {
    if (!p || !dist_x || !dist_y) return -1;
    float kx = 0.0f, ky = 0.0f;
    int32_t rc = vs_rect_calculate_shear(p, &kx, &ky);
    if (rc != 0) return rc;

    const float ref_y = (p->ref_y >= 0.0f) ? p->ref_y : 0.0f;
    if (std::abs(1.0f - ky) < 1e-6f) return -3;
    const float dy_sol = (rect_y - ky * ref_y) / (1.0f - ky);
    *dist_y = dy_sol;
    *dist_x = rect_x + kx * (dy_sol - ref_y);
    return 0;
}

int32_t vs_rect_apply_mono8(const vs_rect_params* p,
                           const uint8_t* src,
                           uint8_t* dst,
                           int32_t width,
                           int32_t height,
                           int32_t stride,
                           vs_rect_stats* stats) {
    if (!p || !src || !dst || width <= 0 || height <= 0 || stride < width) {
        return -1;
    }

    const auto t_start = std::chrono::high_resolution_clock::now();

    float kx = 0.0f, ky = 0.0f;
    int32_t rc = vs_rect_calculate_shear(p, &kx, &ky);
    if (rc != 0) return rc;

    const float ref_y = (p->ref_y >= 0.0f) ? p->ref_y : ((float)height / 2.0f);
    const float max_row_diff = (float)std::max(std::abs(0.0f - ref_y), std::abs((float)height - 1.0f - ref_y));
    const float max_shift = std::sqrt(kx * kx + ky * ky) * max_row_diff;
    const float shear_angle = (float)rad2deg(std::atan2(kx, 1.0f));

    if (stats) {
        stats->max_shift_px = max_shift;
        stats->shear_angle_deg = shear_angle;
    }

    // Zero motion fast-path: simple copy if out-of-place, no-op if in-place
    if (std::abs(kx) < 1e-7f && std::abs(ky) < 1e-7f) {
        if (dst != src) {
            for (int32_t y = 0; y < height; ++y) {
                std::memcpy(dst + (size_t)y * stride, src + (size_t)y * stride, (size_t)width);
            }
        }
        if (stats) {
            const auto t_end = std::chrono::high_resolution_clock::now();
            stats->time_ms = std::chrono::duration<float, std::milli>(t_end - t_start).count();
        }
        return 0;
    }

    const uint8_t fill = p->fill_value;
    const bool bilinear = (p->interpolation != 0);

    // Fast-path: Horizontal conveyor motion (ky == 0)
    // Only X is shifted row-by-row. Memory locality is purely 1D along scanlines.
    if (std::abs(ky) < 1e-7f) {
        std::vector<uint8_t> row_buffer;
        if (dst == src) {
            row_buffer.resize(width);
        }

        for (int32_t y = 0; y < height; ++y) {
            const uint8_t* src_row = src + (size_t)y * stride;
            uint8_t* dst_row = (dst == src) ? row_buffer.data() : (dst + (size_t)y * stride);

            const float shift_x = kx * ((float)y - ref_y);

            if (!bilinear) {
                const int32_t ishift = (int32_t)std::round(shift_x);
                for (int32_t x = 0; x < width; ++x) {
                    const int32_t sx = x + ishift;
                    if (sx >= 0 && sx < width) {
                        dst_row[x] = src_row[sx];
                    } else {
                        dst_row[x] = fill;
                    }
                }
            } else {
                const float fx = shift_x;
                const int32_t ix0 = (int32_t)std::floor(fx);
                const float frac = fx - (float)ix0;
                const int32_t w_frac = (int32_t)(frac * 256.0f + 0.5f);
                const int32_t w_inv  = 256 - w_frac;

                for (int32_t x = 0; x < width; ++x) {
                    const int32_t sx0 = x + ix0;
                    const int32_t sx1 = sx0 + 1;

                    const bool in0 = (sx0 >= 0 && sx0 < width);
                    const bool in1 = (sx1 >= 0 && sx1 < width);

                    if (in0 && in1) {
                        dst_row[x] = (uint8_t)((src_row[sx0] * w_inv + src_row[sx1] * w_frac + 128) >> 8);
                    } else if (in0) {
                        dst_row[x] = (uint8_t)((src_row[sx0] * w_inv + fill * w_frac + 128) >> 8);
                    } else if (in1) {
                        dst_row[x] = (uint8_t)((fill * w_inv + src_row[sx1] * w_frac + 128) >> 8);
                    } else {
                        dst_row[x] = fill;
                    }
                }
            }

            if (dst == src) {
                std::memcpy(dst + (size_t)y * stride, row_buffer.data(), (size_t)width);
            }
        }
    } else {
        // General 2D motion (arbitrary conveyor orientation)
        std::vector<uint8_t> temp_frame;
        const uint8_t* read_ptr = src;
        if (dst == src) {
            temp_frame.assign(src, src + (size_t)stride * height);
            read_ptr = temp_frame.data();
        }

        for (int32_t y = 0; y < height; ++y) {
            uint8_t* dst_row = dst + (size_t)y * stride;
            const float dy = (float)y - ref_y;
            const float shift_x = kx * dy;
            const float shift_y = ky * dy;
            const float sy = (float)y + shift_y;

            if (!bilinear) {
                const int32_t isy = (int32_t)std::round(sy);
                if (isy < 0 || isy >= height) {
                    std::memset(dst_row, fill, width);
                    continue;
                }
                const uint8_t* src_row = read_ptr + (size_t)isy * stride;
                const int32_t isx = (int32_t)std::round(shift_x);
                for (int32_t x = 0; x < width; ++x) {
                    const int32_t sx = x + isx;
                    dst_row[x] = (sx >= 0 && sx < width) ? src_row[sx] : fill;
                }
            } else {
                const int32_t iy0 = (int32_t)std::floor(sy);
                const int32_t iy1 = iy0 + 1;
                const float frac_y = sy - (float)iy0;
                const float fx = shift_x;
                const int32_t ix0 = (int32_t)std::floor(fx);
                const float frac_x = fx - (float)ix0;

                const bool valid_y0 = (iy0 >= 0 && iy0 < height);
                const bool valid_y1 = (iy1 >= 0 && iy1 < height);

                const uint8_t* r0 = valid_y0 ? (read_ptr + (size_t)iy0 * stride) : nullptr;
                const uint8_t* r1 = valid_y1 ? (read_ptr + (size_t)iy1 * stride) : nullptr;

                for (int32_t x = 0; x < width; ++x) {
                    const int32_t sx0 = x + ix0;
                    const int32_t sx1 = sx0 + 1;

                    const float p00 = (r0 && sx0 >= 0 && sx0 < width) ? (float)r0[sx0] : (float)fill;
                    const float p10 = (r0 && sx1 >= 0 && sx1 < width) ? (float)r1[sx1] : (float)fill;
                    const float p01 = (r1 && sx0 >= 0 && sx0 < width) ? (float)r1[sx0] : (float)fill;
                    const float p11 = (r1 && sx1 >= 0 && sx1 < width) ? (float)r1[sx1] : (float)fill;

                    const float top = p00 + frac_x * (p10 - p00);
                    const float bot = p01 + frac_x * (p11 - p01);
                    const float val = top + frac_y * (bot - top);

                    dst_row[x] = (uint8_t)std::clamp(std::round(val), 0.0f, 255.0f);
                }
            }
        }
    }

    if (stats) {
        const auto t_end = std::chrono::high_resolution_clock::now();
        stats->time_ms = std::chrono::duration<float, std::milli>(t_end - t_start).count();
    }
    return 0;
}

int32_t vs_rect_apply_roi_mono8(const vs_rect_params* p,
                               const uint8_t* src,
                               int32_t src_width,
                               int32_t src_height,
                               int32_t src_stride,
                               int32_t roi_x,
                               int32_t roi_y,
                               int32_t roi_w,
                               int32_t roi_h,
                               uint8_t* dst_roi,
                               int32_t dst_stride,
                               vs_rect_stats* stats) {
    if (!p || !src || !dst_roi || src_width <= 0 || src_height <= 0 ||
        roi_w <= 0 || roi_h <= 0 || roi_x < 0 || roi_y < 0 ||
        roi_x + roi_w > src_width || roi_y + roi_h > src_height ||
        src_stride < src_width || dst_stride < roi_w) {
        return -1;
    }

    const auto t_start = std::chrono::high_resolution_clock::now();

    float kx = 0.0f, ky = 0.0f;
    int32_t rc = vs_rect_calculate_shear(p, &kx, &ky);
    if (rc != 0) return rc;

    const float ref_y = (p->ref_y >= 0.0f) ? p->ref_y : ((float)src_height / 2.0f);
    const uint8_t fill = p->fill_value;
    const bool bilinear = (p->interpolation != 0);

    if (std::abs(ky) < 1e-7f) {
        for (int32_t ry = 0; ry < roi_h; ++ry) {
            const int32_t global_y = roi_y + ry;
            uint8_t* dst_row = dst_roi + (size_t)ry * dst_stride;
            const uint8_t* src_row = src + (size_t)global_y * src_stride;

            const float shift_x = kx * ((float)global_y - ref_y);

            if (!bilinear) {
                const int32_t ishift = (int32_t)std::round(shift_x);
                for (int32_t rx = 0; rx < roi_w; ++rx) {
                    const int32_t sx = roi_x + rx + ishift;
                    dst_row[rx] = (sx >= 0 && sx < src_width) ? src_row[sx] : fill;
                }
            } else {
                const float fx = shift_x;
                const int32_t ix0 = (int32_t)std::floor(fx);
                const float frac = fx - (float)ix0;
                const int32_t w_frac = (int32_t)(frac * 256.0f + 0.5f);
                const int32_t w_inv  = 256 - w_frac;

                for (int32_t rx = 0; rx < roi_w; ++rx) {
                    const int32_t sx0 = roi_x + rx + ix0;
                    const int32_t sx1 = sx0 + 1;

                    const bool in0 = (sx0 >= 0 && sx0 < src_width);
                    const bool in1 = (sx1 >= 0 && sx1 < src_width);

                    if (in0 && in1) {
                        dst_row[rx] = (uint8_t)((src_row[sx0] * w_inv + src_row[sx1] * w_frac + 128) >> 8);
                    } else if (in0) {
                        dst_row[rx] = (uint8_t)((src_row[sx0] * w_inv + fill * w_frac + 128) >> 8);
                    } else if (in1) {
                        dst_row[rx] = (uint8_t)((fill * w_inv + src_row[sx1] * w_frac + 128) >> 8);
                    } else {
                        dst_row[rx] = fill;
                    }
                }
            }
        }
    } else {
        for (int32_t ry = 0; ry < roi_h; ++ry) {
            const int32_t global_y = roi_y + ry;
            uint8_t* dst_row = dst_roi + (size_t)ry * dst_stride;

            const float dy = (float)global_y - ref_y;
            const float shift_x = kx * dy;
            const float shift_y = ky * dy;

            const float sy = (float)global_y + shift_y;

            if (!bilinear) {
                const int32_t isy = (int32_t)std::round(sy);
                if (isy < 0 || isy >= src_height) {
                    std::memset(dst_row, fill, roi_w);
                    continue;
                }
                const uint8_t* src_row = src + (size_t)isy * src_stride;
                for (int32_t rx = 0; rx < roi_w; ++rx) {
                    const int32_t global_x = roi_x + rx;
                    const int32_t isx = (int32_t)std::round((float)global_x + shift_x);
                    dst_row[rx] = (isx >= 0 && isx < src_width) ? src_row[isx] : fill;
                }
            } else {
                const int32_t iy0 = (int32_t)std::floor(sy);
                const int32_t iy1 = iy0 + 1;
                const float frac_y = sy - (float)iy0;
                const float fx = shift_x;
                const int32_t ix0 = (int32_t)std::floor(fx);
                const float frac_x = fx - (float)ix0;

                const bool valid_y0 = (iy0 >= 0 && iy0 < src_height);
                const bool valid_y1 = (iy1 >= 0 && iy1 < src_height);

                const uint8_t* r0 = valid_y0 ? (src + (size_t)iy0 * src_stride) : nullptr;
                const uint8_t* r1 = valid_y1 ? (src + (size_t)iy1 * src_stride) : nullptr;

                for (int32_t rx = 0; rx < roi_w; ++rx) {
                    const int32_t global_x = roi_x + rx;
                    const float sx = (float)global_x + shift_x;
                    const int32_t ix0 = (int32_t)std::floor(sx);
                    const int32_t ix1 = ix0 + 1;
                    const float frac_x = sx - (float)ix0;

                    const float p00 = (r0 && ix0 >= 0 && ix0 < src_width) ? (float)r0[ix0] : (float)fill;
                    const float p10 = (r0 && ix1 >= 0 && ix1 < src_width) ? (float)r1[ix1] : (float)fill;
                    const float p01 = (r1 && ix0 >= 0 && ix0 < src_width) ? (float)r1[ix0] : (float)fill;
                    const float p11 = (r1 && ix1 >= 0 && ix1 < src_width) ? (float)r1[ix1] : (float)fill;

                    const float top = p00 + frac_x * (p10 - p00);
                    const float bot = p01 + frac_x * (p11 - p01);
                    const float val = top + frac_y * (bot - top);

                    dst_row[rx] = (uint8_t)std::clamp(std::round(val), 0.0f, 255.0f);
                }
            }
        }
    }

    if (stats) {
        const auto t_end = std::chrono::high_resolution_clock::now();
        stats->time_ms = std::chrono::duration<float, std::milli>(t_end - t_start).count();
    }
    return 0;
}
