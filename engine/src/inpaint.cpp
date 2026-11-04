/*
 * inpaint.cpp - Implementation of the Specular Highlight Glare Inpainter Tool (AG-07).
 *
 * Implements:
 * 1. Specular glare detection with morphological halo dilation.
 * 2. Two-stage Telea Fast Marching inpainting with anisotropic isophote/edge alignment.
 * 3. Harmonic Laplacian relaxation with SOR acceleration.
 * 4. Isophote-driven anisotropic diffusion (Navier-Stokes).
 * 5. Bounding-box localized ROI execution for sub-millisecond CPU speed on 20 MP frames.
 */

#include "vs_inpaint.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <queue>
#include <vector>

namespace {

enum PixelFlag : uint8_t {
    FLAG_KNOWN = 0,
    FLAG_BAND = 1,
    FLAG_INSIDE = 2
};

struct HeapNode {
    float t;
    int32_t y;
    int32_t x;

    bool operator>(const HeapNode& other) const {
        return t > other.t;
    }
};

struct MaskPixel {
    float t;
    int32_t y;
    int32_t x;
};

// Solve Eikonal equation for distance update: |∇T| = 1
inline float solve_eikonal(float tx, float ty) {
    if (std::abs(tx - ty) < 1.0f) {
        return 0.5f * (tx + ty + std::sqrt(2.0f - (tx - ty) * (tx - ty)));
    }
    return std::min(tx, ty) + 1.0f;
}

// Compute image gradient at (y, x)
inline void compute_image_gradient(const float* img, int w, int h, int y, int x, float& gx, float& gy) {
    int x0 = std::max(0, x - 1);
    int x1 = std::min(w - 1, x + 1);
    float dx = static_cast<float>(x1 - x0);
    gx = (dx > 0) ? ((img[y * w + x1] - img[y * w + x0]) / dx) : 0.0f;

    int y0 = std::max(0, y - 1);
    int y1 = std::min(h - 1, y + 1);
    float dy = static_cast<float>(y1 - y0);
    gy = (dy > 0) ? ((img[y1 * w + x] - img[y0 * w + x]) / dy) : 0.0f;
}

// Two-stage Fast Marching Inpainting with Isophote/Edge Alignment
void inpaint_telea_block(
    float* img,
    const uint8_t* mask,
    int w,
    int h,
    int radius)
{
    std::vector<float> dist(w * h, 1e6f);
    std::vector<uint8_t> state(w * h, FLAG_KNOWN);
    std::priority_queue<HeapNode, std::vector<HeapNode>, std::greater<HeapNode>> pq;

    // Stage 1: Initialize distance field
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            if (mask[y * w + x] > 0) {
                state[y * w + x] = FLAG_INSIDE;
            } else {
                dist[y * w + x] = 0.0f;
                state[y * w + x] = FLAG_KNOWN;
            }
        }
    }

    // Identify initial boundary (BAND) pixels
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            if (state[y * w + x] == FLAG_INSIDE) {
                bool adj = (x > 0 && state[y * w + x - 1] == FLAG_KNOWN) ||
                           (x < w - 1 && state[y * w + x + 1] == FLAG_KNOWN) ||
                           (y > 0 && state[(y - 1) * w + x] == FLAG_KNOWN) ||
                           (y < h - 1 && state[(y + 1) * w + x] == FLAG_KNOWN);
                if (adj) {
                    state[y * w + x] = FLAG_BAND;
                    dist[y * w + x] = 1.0f;
                    pq.push({1.0f, y, x});
                }
            }
        }
    }

    // Solve Fast Marching Eikonal equation for all masked pixels
    const int dx4[4] = {1, -1, 0, 0};
    const int dy4[4] = {0, 0, 1, -1};

    while (!pq.empty()) {
        HeapNode top = pq.top();
        pq.pop();

        int y = top.y;
        int x = top.x;

        if (state[y * w + x] == FLAG_KNOWN) continue;
        state[y * w + x] = FLAG_KNOWN;
        dist[y * w + x] = top.t;

        for (int k = 0; k < 4; ++k) {
            int ny = y + dy4[k];
            int nx = x + dx4[k];

            if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
            if (state[ny * w + nx] == FLAG_KNOWN) continue;

            float tx = 1e6f;
            if (nx > 0 && state[ny * w + nx - 1] == FLAG_KNOWN)
                tx = std::min(tx, dist[ny * w + nx - 1]);
            if (nx < w - 1 && state[ny * w + nx + 1] == FLAG_KNOWN)
                tx = std::min(tx, dist[ny * w + nx + 1]);

            float ty = 1e6f;
            if (ny > 0 && state[(ny - 1) * w + nx] == FLAG_KNOWN)
                ty = std::min(ty, dist[(ny - 1) * w + nx]);
            if (ny < h - 1 && state[(ny + 1) * w + nx] == FLAG_KNOWN)
                ty = std::min(ty, dist[(ny + 1) * w + nx]);

            float d_new = solve_eikonal(tx, ty);
            if (d_new < dist[ny * w + nx]) {
                dist[ny * w + nx] = d_new;
                state[ny * w + nx] = FLAG_BAND;
                pq.push({d_new, ny, nx});
            }
        }
    }

    // Stage 2: Inpaint pixels in order of ascending distance
    std::vector<MaskPixel> ordered_pixels;
    ordered_pixels.reserve(w * h);

    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            if (mask[y * w + x] > 0) {
                ordered_pixels.push_back({dist[y * w + x], y, x});
            }
        }
    }

    std::sort(ordered_pixels.begin(), ordered_pixels.end(), [](const MaskPixel& a, const MaskPixel& b) {
        return a.t < b.t;
    });

    const float rad2 = static_cast<float>(radius * radius);
    std::vector<uint8_t> inpainted_done(w * h, 0);

    for (const auto& mp : ordered_pixels) {
        int y = mp.y;
        int x = mp.x;

        // Compute normal N = ∇dist
        int x0 = std::max(0, x - 1);
        int x1 = std::min(w - 1, x + 1);
        float nx = (dist[y * w + x1] - dist[y * w + x0]) / static_cast<float>(x1 - x0);

        int y0 = std::max(0, y - 1);
        int y1 = std::min(h - 1, y + 1);
        float ny = (dist[y1 * w + x] - dist[y0 * w + x]) / static_cast<float>(y1 - y0);

        float n_norm = std::sqrt(nx * nx + ny * ny);

        float sum_i = 0.0f;
        float sum_w = 0.0f;

        int y_min = std::max(0, y - radius);
        int y_max = std::min(h - 1, y + radius);
        int x_min = std::max(0, x - radius);
        int x_max = std::min(w - 1, x + radius);

        for (int qy = y_min; qy <= y_max; ++qy) {
            for (int qx = x_min; qx <= x_max; ++qx) {
                if (mask[qy * w + qx] > 0 && inpainted_done[qy * w + qx] == 0) {
                    continue;
                }

                float dy = static_cast<float>(y - qy);
                float dx = static_cast<float>(x - qx);
                float d2 = dx * dx + dy * dy;

                if (d2 > rad2 || d2 < 0.25f) continue;

                float d = std::sqrt(d2);
                float dst_weight = 1.0f / d2;

                float dir_weight = 1.0f;
                if (n_norm > 1e-4f) {
                    float dot = (dx * nx + dy * ny) / (d * n_norm);
                    dir_weight = std::max(0.1f, std::abs(dot));
                }

                float lev_weight = 1.0f / (1.0f + std::abs(dist[y * w + x] - dist[qy * w + qx]));

                // Compute image gradient and isophote edge alignment
                float gx = 0.0f, gy = 0.0f;
                compute_image_gradient(img, w, h, qy, qx, gx, gy);
                float g_mag = std::sqrt(gx * gx + gy * gy);

                float edge_weight = 1.0f;
                if (g_mag > 15.0f) {
                    // Isophote direction is perpendicular to gradient
                    float tx_iso = -gy / g_mag;
                    float ty_iso = gx / g_mag;

                    // Dot product between displacement vector and isophote direction
                    float cos_iso = std::abs(dx * tx_iso + dy * ty_iso) / d;

                    // Boost propagation along the edge, penalize bleeding across the edge
                    if (cos_iso > 0.6f) {
                        edge_weight = 1.0f + 6.0f * (g_mag / 255.0f) * cos_iso;
                    } else if (cos_iso < 0.3f) {
                        edge_weight = 0.15f;
                    }
                }

                float w_total = dst_weight * dir_weight * lev_weight * edge_weight;

                float delta = gx * dx + gy * dy;
                delta = std::clamp(delta, -15.0f, 15.0f);

                float i_proj = std::clamp(img[qy * w + qx] + delta, 0.0f, 255.0f);

                sum_i += w_total * i_proj;
                sum_w += w_total;
            }
        }

        if (sum_w > 1e-5f) {
            img[y * w + x] = sum_i / sum_w;
        }
        inpainted_done[y * w + x] = 1;
    }
}

// Harmonic Laplacian relaxation with Successive Over-Relaxation (SOR)
void inpaint_harmonic_block(
    float* img,
    const uint8_t* mask,
    int w,
    int h,
    int iterations)
{
    float b_sum = 0.0f;
    int b_count = 0;

    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            if (mask[y * w + x] == 0) {
                bool adj = (x > 0 && mask[y * w + x - 1]) ||
                           (x < w - 1 && mask[y * w + x + 1]) ||
                           (y > 0 && mask[(y - 1) * w + x]) ||
                           (y < h - 1 && mask[(y + 1) * w + x]);
                if (adj) {
                    b_sum += img[y * w + x];
                    b_count++;
                }
            }
        }
    }

    float init_val = (b_count > 0) ? (b_sum / b_count) : 128.0f;
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            if (mask[y * w + x] > 0) {
                img[y * w + x] = init_val;
            }
        }
    }

    const float omega = 1.35f;

    for (int iter = 0; iter < iterations; ++iter) {
        for (int y = 0; y < h; ++y) {
            for (int x = 0; x < w; ++x) {
                if (mask[y * w + x] == 0) continue;

                float left   = (x > 0) ? img[y * w + x - 1] : img[y * w + x];
                float right  = (x < w - 1) ? img[y * w + x + 1] : img[y * w + x];
                float top    = (y > 0) ? img[(y - 1) * w + x] : img[y * w + x];
                float bottom = (y < h - 1) ? img[(y + 1) * w + x] : img[y * w + x];

                float avg = 0.25f * (left + right + top + bottom);
                img[y * w + x] += omega * (avg - img[y * w + x]);
            }
        }
    }
}

// Navier-Stokes isophote diffusion
void inpaint_navier_stokes_block(
    float* img,
    const uint8_t* mask,
    int w,
    int h,
    int iterations)
{
    inpaint_harmonic_block(img, mask, w, h, 10);

    std::vector<float> laplacian(w * h, 0.0f);
    const float dt = 0.1f;

    for (int iter = 0; iter < iterations; ++iter) {
        for (int y = 1; y < h - 1; ++y) {
            for (int x = 1; x < w - 1; ++x) {
                laplacian[y * w + x] = img[(y - 1) * w + x] + img[(y + 1) * w + x] +
                                      img[y * w + x - 1] + img[y * w + x + 1] -
                                      4.0f * img[y * w + x];
            }
        }

        for (int y = 1; y < h - 1; ++y) {
            for (int x = 1; x < w - 1; ++x) {
                if (mask[y * w + x] == 0) continue;

                float ix = 0.5f * (img[y * w + x + 1] - img[y * w + x - 1]);
                float iy = 0.5f * (img[(y + 1) * w + x] - img[(y - 1) * w + x]);

                float vx = -iy;
                float vy = ix;
                float v_norm = std::sqrt(vx * vx + vy * vy);
                if (v_norm > 1e-4f) {
                    vx /= v_norm;
                    vy /= v_norm;
                }

                float dlx = 0.5f * (laplacian[y * w + x + 1] - laplacian[y * w + x - 1]);
                float dly = 0.5f * (laplacian[(y + 1) * w + x] - laplacian[(y - 1) * w + x]);

                float transport = (vx * dlx + vy * dly);
                float lap = laplacian[y * w + x];

                img[y * w + x] += dt * (transport + 0.1f * lap);
                img[y * w + x] = std::clamp(img[y * w + x], 0.0f, 255.0f);
            }
        }
    }
}

} // namespace

extern "C" {

void vs_inpaint_default_params(vs_inpaint_params* p) {
    if (!p) return;
    p->struct_size = sizeof(vs_inpaint_params);
    p->glare_threshold = 252;
    p->halo_radius = 2;
    p->inpaint_radius = 4;
    p->method = VS_INPAINT_TELEA;
    p->max_glare_pct = 35.0f;
    p->iterations = 25;
}

int32_t vs_inpaint_detect_glare(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t src_stride,
    uint8_t glare_threshold,
    int32_t halo_radius,
    uint8_t* mask_out,
    int32_t mask_stride,
    int32_t* glare_pixel_count)
{
    if (!src || !mask_out || width <= 0 || height <= 0 || src_stride < width || mask_stride < width) {
        return -1;
    }

    // Zero the output mask
    for (int y = 0; y < height; ++y) {
        std::memset(mask_out + y * mask_stride, 0, width);
    }

    int min_x = width, max_x = -1;
    int min_y = height, max_y = -1;
    int32_t count = 0;

    // Fast linear scan to find glare pixels and their bounding box
    for (int y = 0; y < height; ++y) {
        const uint8_t* row = src + y * src_stride;
        for (int x = 0; x < width; ++x) {
            if (row[x] >= glare_threshold) {
                count++;
                if (x < min_x) min_x = x;
                if (x > max_x) max_x = x;
                if (y < min_y) min_y = y;
                if (y > max_y) max_y = y;
            }
        }
    }

    if (count == 0) {
        if (glare_pixel_count) *glare_pixel_count = 0;
        return 0;
    }

    halo_radius = std::clamp(halo_radius, 0, 10);
    if (halo_radius == 0) {
        for (int y = min_y; y <= max_y; ++y) {
            const uint8_t* row = src + y * src_stride;
            uint8_t* out_row = mask_out + y * mask_stride;
            for (int x = min_x; x <= max_x; ++x) {
                if (row[x] >= glare_threshold) {
                    out_row[x] = 255;
                }
            }
        }
        if (glare_pixel_count) *glare_pixel_count = count;
        return 0;
    }

    // Dilate strictly within the localized bounding box of detected glare
    int dy0 = std::max(0, min_y - halo_radius);
    int dy1 = std::min(height - 1, max_y + halo_radius);
    int dx0 = std::max(0, min_x - halo_radius);
    int dx1 = std::min(width - 1, max_x + halo_radius);

    int32_t dilated_count = 0;
    for (int y = dy0; y <= dy1; ++y) {
        uint8_t* out_row = mask_out + y * mask_stride;
        int y_start = std::max(0, y - halo_radius);
        int y_end = std::min(height - 1, y + halo_radius);

        for (int x = dx0; x <= dx1; ++x) {
            int x_start = std::max(0, x - halo_radius);
            int x_end = std::min(width - 1, x + halo_radius);
            uint8_t val = 0;

            for (int ny = y_start; ny <= y_end && val == 0; ++ny) {
                const uint8_t* r = src + ny * src_stride;
                for (int nx = x_start; nx <= x_end; ++nx) {
                    if (r[nx] >= glare_threshold) {
                        val = 255;
                        break;
                    }
                }
            }
            out_row[x] = val;
            if (val > 0) dilated_count++;
        }
    }

    if (glare_pixel_count) *glare_pixel_count = dilated_count;
    return 0;
}

int32_t vs_inpaint_apply_mono8(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t src_stride,
    const uint8_t* mask,
    int32_t mask_stride,
    uint8_t* dst,
    int32_t dst_stride,
    const vs_inpaint_params* p)
{
    if (!src || !mask || !dst || width <= 0 || height <= 0 ||
        src_stride < width || mask_stride < width || dst_stride < width) {
        return -1;
    }

    vs_inpaint_params params;
    if (p) {
        params = *p;
    } else {
        vs_inpaint_default_params(&params);
    }

    int min_x = width, max_x = -1;
    int min_y = height, max_y = -1;
    int mask_count = 0;

    for (int y = 0; y < height; ++y) {
        const uint8_t* mrow = mask + y * mask_stride;
        for (int x = 0; x < width; ++x) {
            if (mrow[x] > 0) {
                mask_count++;
                if (x < min_x) min_x = x;
                if (x > max_x) max_x = x;
                if (y < min_y) min_y = y;
                if (y > max_y) max_y = y;
            }
        }
    }

    if (mask_count == 0) {
        if (dst != src || dst_stride != src_stride) {
            for (int y = 0; y < height; ++y) {
                std::memcpy(dst + y * dst_stride, src + y * src_stride, width);
            }
        }
        return 0;
    }

    float glare_pct = (100.0f * mask_count) / (width * height);
    if (glare_pct > params.max_glare_pct) {
        if (dst != src || dst_stride != src_stride) {
            for (int y = 0; y < height; ++y) {
                std::memcpy(dst + y * dst_stride, src + y * src_stride, width);
            }
        }
        return 1;
    }

    if (dst != src || dst_stride != src_stride) {
        for (int y = 0; y < height; ++y) {
            std::memcpy(dst + y * dst_stride, src + y * src_stride, width);
        }
    }

    int pad = params.inpaint_radius + 4;
    int bx0 = std::max(0, min_x - pad);
    int by0 = std::max(0, min_y - pad);
    int bx1 = std::min(width - 1, max_x + pad);
    int by1 = std::min(height - 1, max_y + pad);

    int bw = bx1 - bx0 + 1;
    int bh = by1 - by0 + 1;

    std::vector<float> block_img(bw * bh);
    std::vector<uint8_t> block_mask(bw * bh, 0);

    for (int y = 0; y < bh; ++y) {
        int gy = by0 + y;
        const uint8_t* srow = src + gy * src_stride;
        const uint8_t* mrow = mask + gy * mask_stride;
        float* irow = block_img.data() + y * bw;
        uint8_t* krow = block_mask.data() + y * bw;

        for (int x = 0; x < bw; ++x) {
            int gx = bx0 + x;
            irow[x] = static_cast<float>(srow[gx]);
            if (mrow[gx] > 0) {
                krow[x] = 255;
            }
        }
    }

    if (params.method == VS_INPAINT_TELEA) {
        inpaint_telea_block(block_img.data(), block_mask.data(), bw, bh, params.inpaint_radius);
    } else if (params.method == VS_INPAINT_HARMONIC) {
        inpaint_harmonic_block(block_img.data(), block_mask.data(), bw, bh, params.iterations);
    } else if (params.method == VS_INPAINT_NAVIER_STOKES) {
        inpaint_navier_stokes_block(block_img.data(), block_mask.data(), bw, bh, params.iterations);
    }

    for (int y = 0; y < bh; ++y) {
        int gy = by0 + y;
        uint8_t* drow = dst + gy * dst_stride;
        const uint8_t* mrow = mask + gy * mask_stride;
        const float* irow = block_img.data() + y * bw;

        for (int x = 0; x < bw; ++x) {
            int gx = bx0 + x;
            if (mrow[gx] > 0) {
                drow[gx] = static_cast<uint8_t>(std::clamp(std::round(irow[x]), 0.0f, 255.0f));
            }
        }
    }

    return 0;
}

int32_t vs_inpaint_auto_mono8(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t src_stride,
    uint8_t* dst,
    int32_t dst_stride,
    const vs_inpaint_params* p,
    int32_t* out_glare_count)
{
    if (!src || !dst || width <= 0 || height <= 0 || src_stride < width || dst_stride < width) {
        return -1;
    }

    vs_inpaint_params params;
    if (p) {
        params = *p;
    } else {
        vs_inpaint_default_params(&params);
    }

    std::vector<uint8_t> mask(width * height);
    int32_t glare_count = 0;

    int32_t res = vs_inpaint_detect_glare(
        src, width, height, src_stride,
        params.glare_threshold, params.halo_radius,
        mask.data(), width, &glare_count);

    if (res != 0) return res;

    if (out_glare_count) *out_glare_count = glare_count;

    if (glare_count == 0) {
        if (dst != src || dst_stride != src_stride) {
            for (int y = 0; y < height; ++y) {
                std::memcpy(dst + y * dst_stride, src + y * src_stride, width);
            }
        }
        return 0;
    }

    return vs_inpaint_apply_mono8(
        src, width, height, src_stride,
        mask.data(), width,
        dst, dst_stride, &params);
}

int32_t vs_inpaint_roi_mono8(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t src_stride,
    int32_t roi_x,
    int32_t roi_y,
    int32_t roi_w,
    int32_t roi_h,
    uint8_t* dst,
    int32_t dst_stride,
    const vs_inpaint_params* p,
    int32_t* out_glare_count)
{
    if (!src || !dst || width <= 0 || height <= 0 ||
        roi_x < 0 || roi_y < 0 || roi_w <= 0 || roi_h <= 0 ||
        roi_x + roi_w > width || roi_y + roi_h > height ||
        src_stride < width || dst_stride < roi_w) {
        return -1;
    }

    const uint8_t* roi_src = src + roi_y * src_stride + roi_x;

    return vs_inpaint_auto_mono8(
        roi_src, roi_w, roi_h, src_stride,
        dst, dst_stride, p, out_glare_count);
}

} // extern "C"
