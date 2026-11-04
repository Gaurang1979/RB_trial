#include "vs_deform.h"
#include <cmath>
#include <cstring>
#include <algorithm>
#include <vector>
#include <cassert>

namespace {

inline float clamp_f(float v, float mn, float mx) {
    return (v < mn) ? mn : ((v > mx) ? mx : v);
}

inline uint8_t sample_bilinear(const uint8_t* img, int w, int h, int stride, float x, float y) {
    if (x < 0.0f) x = 0.0f;
    if (y < 0.0f) y = 0.0f;
    if (x > (float)(w - 1)) x = (float)(w - 1);
    if (y > (float)(h - 1)) y = (float)(h - 1);

    int x0 = (int)x;
    int y0 = (int)y;
    int x1 = (x0 < w - 1) ? x0 + 1 : x0;
    int y1 = (y0 < h - 1) ? y0 + 1 : y0;

    float fx = x - (float)x0;
    float fy = y - (float)y0;

    float p00 = img[y0 * stride + x0];
    float p10 = img[y0 * stride + x1];
    float p01 = img[y1 * stride + x0];
    float p11 = img[y1 * stride + x1];

    float top = p00 + fx * (p10 - p00);
    float bot = p01 + fx * (p11 - p01);
    float val = top + fy * (bot - top);

    return (uint8_t)std::min(255.0f, std::max(0.0f, val + 0.5f));
}

// Downsample image by factor of 2 with area averaging
void downsample2x(const uint8_t* src, int sw, int sh, int sstride,
                  uint8_t* dst, int dw, int dh, int dstride) {
    for (int y = 0; y < dh; ++y) {
        int sy0 = y * 2;
        int sy1 = std::min(sy0 + 1, sh - 1);
        for (int x = 0; x < dw; ++x) {
            int sx0 = x * 2;
            int sx1 = std::min(sx0 + 1, sw - 1);

            int sum = src[sy0 * sstride + sx0] +
                      src[sy0 * sstride + sx1] +
                      src[sy1 * sstride + sx0] +
                      src[sy1 * sstride + sx1];
            dst[y * dstride + x] = (uint8_t)((sum + 2) >> 2);
        }
    }
}

// Bilinear interpolation of displacement field from mesh nodes
inline void interpolate_mesh(
    const float* mesh_dx, const float* mesh_dy,
    int mesh_cols, int mesh_rows,
    float x, float y, float img_w, float img_h,
    float* out_dx, float* out_dy)
{
    float uf = (img_w > 1.0f) ? (x * (float)(mesh_cols - 1) / (img_w - 1.0f)) : 0.0f;
    float vf = (img_h > 1.0f) ? (y * (float)(mesh_rows - 1) / (img_h - 1.0f)) : 0.0f;

    uf = clamp_f(uf, 0.0f, (float)(mesh_cols - 1));
    vf = clamp_f(vf, 0.0f, (float)(mesh_rows - 1));

    int c = std::min((int)uf, mesh_cols - 2);
    int r = std::min((int)vf, mesh_rows - 2);

    float alpha = uf - (float)c;
    float beta = vf - (float)r;

    int idx00 = r * mesh_cols + c;
    int idx10 = idx00 + 1;
    int idx01 = idx00 + mesh_cols;
    int idx11 = idx01 + 1;

    float w00 = (1.0f - alpha) * (1.0f - beta);
    float w10 = alpha * (1.0f - beta);
    float w01 = (1.0f - alpha) * beta;
    float w11 = alpha * beta;

    *out_dx = w00 * mesh_dx[idx00] + w10 * mesh_dx[idx10] + w01 * mesh_dx[idx01] + w11 * mesh_dx[idx11];
    *out_dy = w00 * mesh_dy[idx00] + w10 * mesh_dy[idx10] + w01 * mesh_dy[idx01] + w11 * mesh_dy[idx11];
}

} // namespace

extern "C" {

void vs_deform_default_params(vs_deform_params* params) {
    if (!params) return;
    params->struct_size = sizeof(vs_deform_params);
    params->pyramid_levels = 3;
    params->max_iterations_per_level = 15;
    params->smoothness_weight = 0.35f;
    params->convergence_epsilon = 0.005f;
    params->rupture_strain_threshold = 0.35f;
    params->crumple_bending_threshold = 40.0f;
    params->min_alignment_score = 0.70f;
}

int vs_deform_analyze_strain(
    const float* mesh_dx,
    const float* mesh_dy,
    int mesh_cols,
    int mesh_rows,
    float cell_w,
    float cell_h,
    const vs_deform_params* params,
    vs_deform_stats* stats)
{
    if (!mesh_dx || !mesh_dy || !stats) return VS_DEFORM_ERR_NULL_PTR;
    if (mesh_cols < 2 || mesh_rows < 2) return VS_DEFORM_ERR_INVALID_MESH;
    if (cell_w <= 0.0f || cell_h <= 0.0f) return VS_DEFORM_ERR_INVALID_DIM;

    vs_deform_params p;
    if (params) {
        p = *params;
    } else {
        vs_deform_default_params(&p);
    }

    int total_nodes = mesh_cols * mesh_rows;
    float sum_disp = 0.0f;
    float max_disp = 0.0f;

    for (int i = 0; i < total_nodes; ++i) {
        float d = std::sqrt(mesh_dx[i] * mesh_dx[i] + mesh_dy[i] * mesh_dy[i]);
        sum_disp += d;
        if (d > max_disp) max_disp = d;
    }

    stats->mean_displacement_px = sum_disp / (float)total_nodes;
    stats->max_displacement_px = max_disp;

    float max_stretch = 0.0f;
    float max_comp = 0.0f;

    // Analyze 2D Jacobian transformation det(J) for each grid cell
    for (int r = 0; r < mesh_rows - 1; ++r) {
        for (int c = 0; c < mesh_cols - 1; ++c) {
            int idx00 = r * mesh_cols + c;
            int idx10 = idx00 + 1;
            int idx01 = idx00 + mesh_cols;
            int idx11 = idx01 + 1;

            float ux0 = mesh_dx[idx00], uy0 = mesh_dy[idx00];
            float ux1 = mesh_dx[idx10], uy1 = mesh_dy[idx10];
            float ux2 = mesh_dx[idx01], uy2 = mesh_dy[idx01];
            float ux3 = mesh_dx[idx11], uy3 = mesh_dy[idx11];

            // Finite difference approximations of displacement derivatives
            float dudx = (ux1 - ux0 + ux3 - ux2) / (2.0f * cell_w);
            float dvdx = (uy1 - uy0 + uy3 - uy2) / (2.0f * cell_w);
            float dudy = (ux2 - ux0 + ux3 - ux1) / (2.0f * cell_h);
            float dvdy = (uy2 - uy0 + uy3 - uy1) / (2.0f * cell_h);

            // Area Jacobian determinant: det(I + \nabla d)
            float detJ = (1.0f + dudx) * (1.0f + dvdy) - (dudy * dvdx);

            float stretch = detJ - 1.0f;
            float comp = 1.0f - detJ;

            if (stretch > max_stretch) max_stretch = stretch;
            if (comp > max_comp) max_comp = comp;
        }
    }

    stats->max_area_stretch = max_stretch;
    stats->max_area_compression = max_comp;

    // Discrete thin-plate bending energy normalized by cell area: \iint \|\nabla^2 d\|^2 dx dy
    float cell_area = cell_w * cell_h;
    if (cell_area < 1.0f) cell_area = 1.0f;

    float bending_sum = 0.0f;
    for (int r = 1; r < mesh_rows - 1; ++r) {
        for (int c = 1; c < mesh_cols - 1; ++c) {
            int idx = r * mesh_cols + c;
            float lap_x = 4.0f * mesh_dx[idx] - mesh_dx[idx - 1] - mesh_dx[idx + 1]
                          - mesh_dx[idx - mesh_cols] - mesh_dx[idx + mesh_cols];
            float lap_y = 4.0f * mesh_dy[idx] - mesh_dy[idx - 1] - mesh_dy[idx + 1]
                          - mesh_dy[idx - mesh_cols] - mesh_dy[idx + mesh_cols];

            bending_sum += (lap_x * lap_x + lap_y * lap_y) / cell_area;
        }
    }

    stats->total_bending_energy = bending_sum;
    stats->is_film_ruptured = (max_stretch > p.rupture_strain_threshold) ? 1 : 0;
    stats->is_excessive_crumple = (bending_sum > p.crumple_bending_threshold) ? 1 : 0;

    return VS_DEFORM_OK;
}

int vs_deform_unwarp(
    const uint8_t* test_img,
    int test_w,
    int test_h,
    int test_stride,
    const float* mesh_dx,
    const float* mesh_dy,
    int mesh_cols,
    int mesh_rows,
    uint8_t* unwarped_out,
    int out_w,
    int out_h,
    int out_stride)
{
    if (!test_img || !mesh_dx || !mesh_dy || !unwarped_out) return VS_DEFORM_ERR_NULL_PTR;
    if (test_w <= 0 || test_h <= 0 || out_w <= 0 || out_h <= 0) return VS_DEFORM_ERR_INVALID_DIM;
    if (mesh_cols < 2 || mesh_rows < 2) return VS_DEFORM_ERR_INVALID_MESH;

    for (int y = 0; y < out_h; ++y) {
        for (int x = 0; x < out_w; ++x) {
            float dx = 0.0f, dy = 0.0f;
            interpolate_mesh(mesh_dx, mesh_dy, mesh_cols, mesh_rows,
                             (float)x, (float)y, (float)out_w, (float)out_h,
                             &dx, &dy);

            float sx = (float)x + dx;
            float sy = (float)y + dy;

            unwarped_out[y * out_stride + x] = sample_bilinear(test_img, test_w, test_h, test_stride, sx, sy);
        }
    }

    return VS_DEFORM_OK;
}

int vs_deform_estimate(
    const uint8_t* test_img,
    int test_w,
    int test_h,
    int test_stride,
    const uint8_t* ref_img,
    int ref_w,
    int ref_h,
    int ref_stride,
    const vs_deform_params* params,
    float* mesh_dx,
    float* mesh_dy,
    int mesh_cols,
    int mesh_rows,
    vs_deform_stats* stats)
{
    if (!test_img || !ref_img || !mesh_dx || !mesh_dy || !stats) return VS_DEFORM_ERR_NULL_PTR;
    if (test_w <= 0 || test_h <= 0 || ref_w <= 0 || ref_h <= 0) return VS_DEFORM_ERR_INVALID_DIM;
    if (mesh_cols < 2 || mesh_rows < 2) return VS_DEFORM_ERR_INVALID_MESH;

    vs_deform_params p;
    if (params) {
        p = *params;
    } else {
        vs_deform_default_params(&p);
    }

    std::memset(stats, 0, sizeof(vs_deform_stats));
    stats->struct_size = sizeof(vs_deform_stats);

    int total_nodes = mesh_cols * mesh_rows;
    std::fill(mesh_dx, mesh_dx + total_nodes, 0.0f);
    std::fill(mesh_dy, mesh_dy + total_nodes, 0.0f);

    int levels = std::max(1, std::min(p.pyramid_levels, 4));

    // Construct Gaussian multi-resolution pyramids
    struct PyramidLevel {
        std::vector<uint8_t> test;
        std::vector<uint8_t> ref;
        int tw, th;
        int rw, rh;
    };

    std::vector<PyramidLevel> pyr(levels);

    // Level 0 (finest)
    pyr[0].tw = test_w; pyr[0].th = test_h;
    pyr[0].test.resize(test_w * test_h);
    for (int y = 0; y < test_h; ++y) {
        std::memcpy(&pyr[0].test[y * test_w], test_img + y * test_stride, test_w);
    }

    pyr[0].rw = ref_w; pyr[0].rh = ref_h;
    pyr[0].ref.resize(ref_w * ref_h);
    for (int y = 0; y < ref_h; ++y) {
        std::memcpy(&pyr[0].ref[y * ref_w], ref_img + y * ref_stride, ref_w);
    }

    // Coarse levels
    for (int l = 1; l < levels; ++l) {
        pyr[l].tw = std::max(pyr[l - 1].tw / 2, 4);
        pyr[l].th = std::max(pyr[l - 1].th / 2, 4);
        pyr[l].test.resize(pyr[l].tw * pyr[l].th);
        downsample2x(pyr[l - 1].test.data(), pyr[l - 1].tw, pyr[l - 1].th, pyr[l - 1].tw,
                     pyr[l].test.data(), pyr[l].tw, pyr[l].th, pyr[l].tw);

        pyr[l].rw = std::max(pyr[l - 1].rw / 2, 4);
        pyr[l].rh = std::max(pyr[l - 1].rh / 2, 4);
        pyr[l].ref.resize(pyr[l].rw * pyr[l].rh);
        downsample2x(pyr[l - 1].ref.data(), pyr[l - 1].rw, pyr[l - 1].rh, pyr[l - 1].rw,
                     pyr[l].ref.data(), pyr[l].rw, pyr[l].rh, pyr[l].rw);
    }

    // Coarse-to-fine iterative optimization
    for (int l = levels - 1; l >= 0; --l) {
        int cur_tw = pyr[l].tw;
        int cur_th = pyr[l].th;
        int cur_rw = pyr[l].rw;
        int cur_rh = pyr[l].rh;
        const uint8_t* cur_test = pyr[l].test.data();
        const uint8_t* cur_ref = pyr[l].ref.data();

        float scale_x = (float)cur_rw / (float)ref_w;
        float scale_y = (float)cur_rh / (float)ref_h;

        // Scale mesh displacements to current pyramid level
        for (int i = 0; i < total_nodes; ++i) {
            mesh_dx[i] *= (l == levels - 1) ? 1.0f : 2.0f;
            mesh_dy[i] *= (l == levels - 1) ? 1.0f : 2.0f;
        }

        float cell_w_curr = (cur_rw > 1) ? ((float)(cur_rw - 1) / (float)(mesh_cols - 1)) : 1.0f;
        float cell_h_curr = (cur_rh > 1) ? ((float)(cur_rh - 1) / (float)(mesh_rows - 1)) : 1.0f;

        // Buffers for Gauss-Newton gradient and Hessian accumulation
        std::vector<float> grad_u(total_nodes, 0.0f);
        std::vector<float> grad_v(total_nodes, 0.0f);
        std::vector<float> hess_u(total_nodes, 0.0f);
        std::vector<float> hess_v(total_nodes, 0.0f);

        for (int iter = 0; iter < p.max_iterations_per_level; ++iter) {
            std::fill(grad_u.begin(), grad_u.end(), 0.0f);
            std::fill(grad_v.begin(), grad_v.end(), 0.0f);
            std::fill(hess_u.begin(), hess_u.end(), 0.0f);
            std::fill(hess_v.begin(), hess_v.end(), 0.0f);

            // Accumulate image gradients and residuals
            // Subsample pixels for speed on CPU (step = 2)
            int step = (l == 0) ? 2 : 1;
            for (int y = 1; y < cur_rh - 1; y += step) {
                for (int x = 1; x < cur_rw - 1; x += step) {
                    float dx = 0.0f, dy = 0.0f;
                    interpolate_mesh(mesh_dx, mesh_dy, mesh_cols, mesh_rows,
                                     (float)x, (float)y, (float)cur_rw, (float)cur_rh,
                                     &dx, &dy);

                    float sx = (float)x + dx;
                    float sy = (float)y + dy;

                    if (sx < 1.0f || sx >= (float)(cur_tw - 2) ||
                        sy < 1.0f || sy >= (float)(cur_th - 2)) {
                        continue;
                    }

                    // Sample deformed test image and spatial gradients
                    float I_warped = sample_bilinear(cur_test, cur_tw, cur_th, cur_tw, sx, sy);
                    float I_ref = (float)cur_ref[y * cur_rw + x];
                    float diff = I_warped - I_ref;

                    float Ix = (sample_bilinear(cur_test, cur_tw, cur_th, cur_tw, sx + 1.0f, sy) -
                                sample_bilinear(cur_test, cur_tw, cur_th, cur_tw, sx - 1.0f, sy)) * 0.5f;
                    float Iy = (sample_bilinear(cur_test, cur_tw, cur_th, cur_tw, sx, sy + 1.0f) -
                                sample_bilinear(cur_test, cur_tw, cur_th, cur_tw, sx, sy - 1.0f)) * 0.5f;

                    // Distribute gradient to surrounding 4 control nodes
                    float uf = (float)x / cell_w_curr;
                    float vf = (float)y / cell_h_curr;
                    int c0 = std::min((int)uf, mesh_cols - 2);
                    int r0 = std::min((int)vf, mesh_rows - 2);
                    float a = uf - (float)c0;
                    float b = vf - (float)r0;

                    int nodes[4] = {
                        r0 * mesh_cols + c0,
                        r0 * mesh_cols + c0 + 1,
                        (r0 + 1) * mesh_cols + c0,
                        (r0 + 1) * mesh_cols + c0 + 1
                    };
                    float weights[4] = {
                        (1.0f - a) * (1.0f - b),
                        a * (1.0f - b),
                        (1.0f - a) * b,
                        a * b
                    };

                    for (int k = 0; k < 4; ++k) {
                        int nid = nodes[k];
                        float w = weights[k];
                        grad_u[nid] += w * diff * Ix;
                        grad_v[nid] += w * diff * Iy;
                        hess_u[nid] += w * (Ix * Ix + 1.0f);
                        hess_v[nid] += w * (Iy * Iy + 1.0f);
                    }
                }
            }

            // Gauss-Seidel relaxation with elastic Laplacian regularization
            float max_delta = 0.0f;
            float lambda = p.smoothness_weight * 50.0f;

            for (int r = 0; r < mesh_rows; ++r) {
                for (int c = 0; c < mesh_cols; ++c) {
                    int nid = r * mesh_cols + c;

                    // Compute discrete Laplacian
                    float lap_u = 0.0f;
                    float lap_v = 0.0f;
                    int neighbors = 0;

                    if (c > 0) { lap_u += mesh_dx[nid - 1]; lap_v += mesh_dy[nid - 1]; neighbors++; }
                    if (c < mesh_cols - 1) { lap_u += mesh_dx[nid + 1]; lap_v += mesh_dy[nid + 1]; neighbors++; }
                    if (r > 0) { lap_u += mesh_dx[nid - mesh_cols]; lap_v += mesh_dy[nid - mesh_cols]; neighbors++; }
                    if (r < mesh_rows - 1) { lap_u += mesh_dx[nid + mesh_cols]; lap_v += mesh_dy[nid + mesh_cols]; neighbors++; }

                    float cur_u = mesh_dx[nid];
                    float cur_v = mesh_dy[nid];

                    float reg_u = lambda * ((float)neighbors * cur_u - lap_u);
                    float reg_v = lambda * ((float)neighbors * cur_v - lap_v);

                    float denom_u = hess_u[nid] + lambda * (float)neighbors + 1e-4f;
                    float denom_v = hess_v[nid] + lambda * (float)neighbors + 1e-4f;

                    float delta_u = (grad_u[nid] + reg_u) / denom_u;
                    float delta_v = (grad_v[nid] + reg_v) / denom_v;

                    // Damped Gauss-Newton update
                    mesh_dx[nid] -= 0.65f * delta_u;
                    mesh_dy[nid] -= 0.65f * delta_v;

                    float d_mag = std::sqrt(delta_u * delta_u + delta_v * delta_v);
                    if (d_mag > max_delta) max_delta = d_mag;
                }
            }

            if (max_delta < p.convergence_epsilon) break;
        }
    }

    // Compute mechanical strain diagnostics
    float cell_w = (ref_w > 1) ? ((float)(ref_w - 1) / (float)(mesh_cols - 1)) : 1.0f;
    float cell_h = (ref_h > 1) ? ((float)(ref_h - 1) / (float)(mesh_rows - 1)) : 1.0f;
    vs_deform_analyze_strain(mesh_dx, mesh_dy, mesh_cols, mesh_rows, cell_w, cell_h, &p, stats);

    // Compute final gradient alignment score post-unwarping
    std::vector<uint8_t> unwarped(ref_w * ref_h);
    vs_deform_unwarp(test_img, test_w, test_h, test_stride,
                     mesh_dx, mesh_dy, mesh_cols, mesh_rows,
                     unwarped.data(), ref_w, ref_h, ref_w);

    double dot_prod = 0.0;
    double norm_u = 0.0;
    double norm_r = 0.0;

    for (int y = 1; y < ref_h - 1; ++y) {
        for (int x = 1; x < ref_w - 1; ++x) {
            int idx = y * ref_w + x;
            float gx_u = (float)(unwarped[idx + 1] - unwarped[idx - 1]) * 0.5f;
            float gy_u = (float)(unwarped[idx + ref_w] - unwarped[idx - ref_w]) * 0.5f;

            float gx_r = (float)(ref_img[y * ref_stride + (x + 1)] - ref_img[y * ref_stride + (x - 1)]) * 0.5f;
            float gy_r = (float)(ref_img[(y + 1) * ref_stride + x] - ref_img[(y - 1) * ref_stride + x]) * 0.5f;

            dot_prod += (gx_u * gx_r + gy_u * gy_r);
            norm_u += (gx_u * gx_u + gy_u * gy_u);
            norm_r += (gx_r * gx_r + gy_r * gy_r);
        }
    }

    float align_score = 0.0f;
    if (norm_u > 1e-3 && norm_r > 1e-3) {
        align_score = (float)(dot_prod / (std::sqrt(norm_u) * std::sqrt(norm_r)));
    }
    align_score = clamp_f(align_score, 0.0f, 1.0f);
    stats->final_alignment_score = align_score;

    stats->is_match_ok = (stats->final_alignment_score >= p.min_alignment_score &&
                          !stats->is_film_ruptured &&
                          !stats->is_excessive_crumple) ? 1 : 0;

    return VS_DEFORM_OK;
}

} // extern "C"
