/*
 * photometric.cpp - Implementation of the Computational Photometric Stereo Engine.
 */
#include "vs_photometric.h"

#include <cmath>
#include <cstring>
#include <vector>
#include <algorithm>

namespace {

// 3x3 Matrix Inversion via Adjugate
bool invert_3x3(const float A[3][3], float Inv[3][3]) {
    float det = A[0][0] * (A[1][1] * A[2][2] - A[1][2] * A[2][1]) -
                A[0][1] * (A[1][0] * A[2][2] - A[1][2] * A[2][0]) +
                A[0][2] * (A[1][0] * A[2][1] - A[1][1] * A[2][0]);

    if (std::abs(det) < 1e-7f) return false;
    float inv_det = 1.0f / det;

    Inv[0][0] = (A[1][1] * A[2][2] - A[1][2] * A[2][1]) * inv_det;
    Inv[0][1] = (A[0][2] * A[2][1] - A[0][1] * A[2][2]) * inv_det;
    Inv[0][2] = (A[0][1] * A[1][2] - A[0][2] * A[1][1]) * inv_det;

    Inv[1][0] = (A[1][2] * A[2][0] - A[1][0] * A[2][2]) * inv_det;
    Inv[1][1] = (A[0][0] * A[2][2] - A[0][2] * A[2][0]) * inv_det;
    Inv[1][2] = (A[0][2] * A[1][0] - A[0][0] * A[1][2]) * inv_det;

    Inv[2][0] = (A[1][0] * A[2][1] - A[1][1] * A[2][0]) * inv_det;
    Inv[2][1] = (A[0][1] * A[2][0] - A[0][0] * A[2][1]) * inv_det;
    Inv[2][2] = (A[0][0] * A[1][1] - A[0][1] * A[1][0]) * inv_det;

    return true;
}

} // namespace

struct vs_photo_pipeline {
    vs_photo_params params{};
    int32_t num_lights = 4;
    // S matrix: K x 3
    std::vector<float> light_matrix; // size K * 3
    // Moore-Penrose Pseudo-Inverse P = (S^T S)^(-1) S^T: 3 x K
    std::vector<float> pseudo_inverse; // size 3 * K

    bool initialize() {
        const int K = num_lights;
        if (K < 3) return false;

        light_matrix.resize(K * 3);
        const float deg_to_rad = 3.141592653589793f / 180.0f;

        for (int k = 0; k < K; ++k) {
            float slant = params.lights[k].slant_deg * deg_to_rad;
            float tilt = params.lights[k].tilt_deg * deg_to_rad;

            float sx = std::sin(slant) * std::cos(tilt);
            float sy = std::sin(slant) * std::sin(tilt);
            float sz = std::cos(slant);

            light_matrix[k * 3 + 0] = sx;
            light_matrix[k * 3 + 1] = sy;
            light_matrix[k * 3 + 2] = sz;
        }

        // Compute S^T * S (3 x 3)
        float StS[3][3] = {{0.0f}};
        for (int r = 0; r < 3; ++r) {
            for (int c = 0; c < 3; ++c) {
                float sum = 0.0f;
                for (int k = 0; k < K; ++k) {
                    sum += light_matrix[k * 3 + r] * light_matrix[k * 3 + c];
                }
                StS[r][c] = sum;
            }
        }

        // Invert S^T * S
        float invStS[3][3] = {{0.0f}};
        if (!invert_3x3(StS, invStS)) {
            return false;
        }

        // P = (S^T S)^(-1) * S^T (3 x K)
        pseudo_inverse.resize(3 * K);
        for (int r = 0; r < 3; ++r) {
            for (int k = 0; k < K; ++k) {
                float sum = 0.0f;
                for (int c = 0; c < 3; ++c) {
                    sum += invStS[r][c] * light_matrix[k * 3 + c];
                }
                pseudo_inverse[r * K + k] = sum;
            }
        }

        return true;
    }
};

extern "C" {

void vs_photo_default_params(vs_photo_params* p) {
    if (!p) return;
    std::memset(p, 0, sizeof(vs_photo_params));
    p->struct_size = sizeof(vs_photo_params);
    p->num_lights = 4;
    p->shape_contrast_gain = 140.0f;
    p->albedo_gamma = 1.0f;

    // Standard 4-channel ring light: Top, Right, Bottom, Left
    // 0: Top (+Y)
    p->lights[0].slant_deg = 45.0f;
    p->lights[0].tilt_deg = 90.0f;
    // 1: Right (+X)
    p->lights[1].slant_deg = 45.0f;
    p->lights[1].tilt_deg = 0.0f;
    // 2: Bottom (-Y)
    p->lights[2].slant_deg = 45.0f;
    p->lights[2].tilt_deg = 270.0f;
    // 3: Left (-X)
    p->lights[3].slant_deg = 45.0f;
    p->lights[3].tilt_deg = 180.0f;
}

vs_photo_pipeline* vs_photo_create(const vs_photo_params* p) {
    auto* pipe = new (std::nothrow) vs_photo_pipeline();
    if (!pipe) return nullptr;

    if (p && p->struct_size == sizeof(vs_photo_params)) {
        pipe->params = *p;
    } else {
        vs_photo_default_params(&pipe->params);
    }

    pipe->num_lights = std::clamp(pipe->params.num_lights, 3, 8);
    if (!pipe->initialize()) {
        delete pipe;
        return nullptr;
    }

    return pipe;
}

void vs_photo_destroy(vs_photo_pipeline* pipe) {
    delete pipe;
}

int32_t vs_photo_compute(
    const vs_photo_pipeline* pipe,
    const uint8_t* const* input_images,
    int32_t width,
    int32_t height,
    int32_t stride,
    uint8_t* albedo_out,
    int32_t albedo_stride,
    uint8_t* shape_out,
    int32_t shape_stride,
    float* normal_map_out,
    int32_t normal_stride)
{
    if (!pipe || !input_images || width <= 0 || height <= 0 || stride < width) return -1;
    const int K = pipe->num_lights;
    for (int k = 0; k < K; ++k) {
        if (!input_images[k]) return -2;
    }

    const float* P = pipe->pseudo_inverse.data();

    // Intermediate normal maps if curvature shape is requested
    const bool need_normals = (shape_out != nullptr) || (normal_map_out != nullptr);
    std::vector<float> local_normals;
    if (need_normals && !normal_map_out) {
        local_normals.resize(static_cast<size_t>(width) * height * 3);
    }

    float* normal_buf = normal_map_out ? normal_map_out : local_normals.data();
    int32_t norm_stride = normal_map_out ? normal_stride : (width * 3 * sizeof(float));

    // 1. Per-pixel Photometric Reconstruction
    for (int32_t y = 0; y < height; ++y) {
        uint8_t* row_albedo = albedo_out ? (albedo_out + y * albedo_stride) : nullptr;
        float* row_normal = need_normals ? reinterpret_cast<float*>(reinterpret_cast<uint8_t*>(normal_buf) + y * norm_stride) : nullptr;

        for (int32_t x = 0; x < width; ++x) {
            float gx = 0.0f, gy = 0.0f, gz = 0.0f;

            for (int k = 0; k < K; ++k) {
                float intensity = static_cast<float>(input_images[k][y * stride + x]);
                gx += P[0 * K + k] * intensity;
                gy += P[1 * K + k] * intensity;
                gz += P[2 * K + k] * intensity;
            }

            float albedo = std::sqrt(gx * gx + gy * gy + gz * gz);

            if (row_albedo) {
                row_albedo[x] = static_cast<uint8_t>(std::clamp(std::round(albedo), 0.0f, 255.0f));
            }

            if (row_normal) {
                if (albedo > 1.0f) {
                    float inv_albedo = 1.0f / albedo;
                    row_normal[x * 3 + 0] = gx * inv_albedo;
                    row_normal[x * 3 + 1] = gy * inv_albedo;
                    row_normal[x * 3 + 2] = gz * inv_albedo;
                } else {
                    row_normal[x * 3 + 0] = 0.0f;
                    row_normal[x * 3 + 1] = 0.0f;
                    row_normal[x * 3 + 2] = 1.0f;
                }
            }
        }
    }

    // 2. Compute Mean Curvature / Shape Relief Map (H = dnx/dx + dny/dy)
    if (shape_out) {
        const float gain = pipe->params.shape_contrast_gain;

        for (int32_t y = 0; y < height; ++y) {
            uint8_t* row_shape = shape_out + y * shape_stride;

            if (y == 0 || y == height - 1) {
                std::memset(row_shape, 128, width);
                continue;
            }

            const float* norm_prev = reinterpret_cast<const float*>(reinterpret_cast<const uint8_t*>(normal_buf) + (y - 1) * norm_stride);
            const float* norm_curr = reinterpret_cast<const float*>(reinterpret_cast<const uint8_t*>(normal_buf) + y * norm_stride);
            const float* norm_next = reinterpret_cast<const float*>(reinterpret_cast<const uint8_t*>(normal_buf) + (y + 1) * norm_stride);

            row_shape[0] = 128;
            row_shape[width - 1] = 128;

            for (int32_t x = 1; x < width - 1; ++x) {
                // Central difference divergence: dnx/dx + dny/dy
                float dnx_dx = 0.5f * (norm_curr[(x + 1) * 3 + 0] - norm_curr[(x - 1) * 3 + 0]);
                float dny_dy = 0.5f * (norm_next[x * 3 + 1] - norm_prev[x * 3 + 1]);

                float curvature = dnx_dx + dny_dy;
                float shape_val = 128.0f + gain * curvature;

                row_shape[x] = static_cast<uint8_t>(std::clamp(std::round(shape_val), 0.0f, 255.0f));
            }
        }
    }

    return 0;
}

int32_t vs_photo_synthesize_lighting(
    const uint8_t* albedo,
    int32_t albedo_stride,
    const float* normal_map,
    int32_t normal_stride,
    int32_t width,
    int32_t height,
    float virtual_tilt_deg,
    float virtual_slant_deg,
    uint8_t* shaded_out,
    int32_t shaded_stride)
{
    if (!albedo || !normal_map || !shaded_out || width <= 0 || height <= 0) return -1;

    const float deg_to_rad = 3.141592653589793f / 180.0f;
    float slant = virtual_slant_deg * deg_to_rad;
    float tilt = virtual_tilt_deg * deg_to_rad;

    float lx = std::sin(slant) * std::cos(tilt);
    float ly = std::sin(slant) * std::sin(tilt);
    float lz = std::cos(slant);

    for (int32_t y = 0; y < height; ++y) {
        const uint8_t* row_albedo = albedo + y * albedo_stride;
        const float* row_normal = reinterpret_cast<const float*>(reinterpret_cast<const uint8_t*>(normal_map) + y * normal_stride);
        uint8_t* row_shaded = shaded_out + y * shaded_stride;

        for (int32_t x = 0; x < width; ++x) {
            float nx = row_normal[x * 3 + 0];
            float ny = row_normal[x * 3 + 1];
            float nz = row_normal[x * 3 + 2];

            float dot = nx * lx + ny * ly + nz * lz;
            if (dot < 0.0f) dot = 0.0f;

            float val = static_cast<float>(row_albedo[x]) * dot;
            row_shaded[x] = static_cast<uint8_t>(std::clamp(std::round(val), 0.0f, 255.0f));
        }
    }

    return 0;
}

} // extern "C"
