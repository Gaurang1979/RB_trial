/*
 * drift.cpp - Implementation of the Optical Drift & Out-Of-Distribution (OOD) Detector (AG-12, AS-13).
 *
 * Implements:
 * 1. Fast grid-sampled optical feature extraction on mono8 images.
 * 2. Regularized Mahalanobis baseline distribution modeling (mean, covariance, matrix inversion).
 * 3. Moving-window trend tracking with directional physical diagnostics.
 * 4. Human-readable diagnostic messaging for plant operators and dashboard condition monitoring.
 */

#include "vs_drift.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <vector>

namespace {

constexpr int D = 10; // Dimension of optical feature vector

// Convert vs_drift_features struct to flat float array of length 10
void feat_to_array(const vs_drift_features* f, float arr[D]) {
    arr[0] = f->mean_brightness;
    arr[1] = f->contrast_std;
    arr[2] = f->sharpness;
    arr[3] = f->uniformity;
    arr[4] = f->corner_sharpness[0];
    arr[5] = f->corner_sharpness[1];
    arr[6] = f->corner_sharpness[2];
    arr[7] = f->corner_sharpness[3];
    arr[8] = f->saturated_ratio;
    arr[9] = f->dark_floor;
}

// Invert 10x10 symmetric positive-definite matrix using Gauss-Jordan elimination with partial pivoting
bool invert_matrix(const float A[D][D], float Inv[D][D]) {
    float mat[D][2 * D];
    for (int i = 0; i < D; ++i) {
        for (int j = 0; j < D; ++j) {
            mat[i][j] = A[i][j];
            mat[i][j + D] = (i == j) ? 1.0f : 0.0f;
        }
    }

    for (int i = 0; i < D; ++i) {
        // Find pivot
        int pivot = i;
        float max_val = std::abs(mat[i][i]);
        for (int r = i + 1; r < D; ++r) {
            float val = std::abs(mat[r][i]);
            if (val > max_val) {
                max_val = val;
                pivot = r;
            }
        }

        if (max_val < 1e-7f) {
            return false; // Singular matrix
        }

        // Swap rows
        if (pivot != i) {
            for (int c = 0; c < 2 * D; ++c) {
                std::swap(mat[i][c], mat[pivot][c]);
            }
        }

        // Scale pivot row
        float div = mat[i][i];
        for (int c = 0; c < 2 * D; ++c) {
            mat[i][c] /= div;
        }

        // Eliminate column
        for (int r = 0; r < D; ++r) {
            if (r != i) {
                float factor = mat[r][i];
                for (int c = 0; c < 2 * D; ++c) {
                    mat[r][c] -= factor * mat[i][c];
                }
            }
        }
    }

    // Extract inverse
    for (int i = 0; i < D; ++i) {
        for (int j = 0; j < D; ++j) {
            Inv[i][j] = mat[i][j + D];
        }
    }
    return true;
}

} // namespace

struct vs_drift_tracker {
    vs_drift_params params;
    bool finalized;

    // Teach baseline data
    std::vector<vs_drift_features> teach_samples;
    float mean[D];
    float std_dev[D];
    float cov_inv[D][D];

    // Moving window trend tracking
    std::deque<float> window_dist;
    std::deque<float> window_brightness;
    std::deque<float> window_sharpness;
};

extern "C" {

void vs_drift_default_params(vs_drift_params* p) {
    if (!p) return;
    p->struct_size = sizeof(vs_drift_params);
    p->window_size = 50;
    p->warn_distance_threshold = 5.0f;
    p->dirt_sharpness_sigma = 2.0f;
    p->light_drop_sigma = 2.0f;
    p->grid_step = 4;
}

int32_t vs_drift_extract_features(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    int32_t grid_step,
    vs_drift_features* out)
{
    if (!src || !out || width <= 0 || height <= 0 || stride < width) {
        return -1;
    }

    grid_step = std::clamp(grid_step, 1, 16);

    uint32_t hist[256];
    std::memset(hist, 0, sizeof(hist));

    double sum = 0.0;
    double sum_sq = 0.0;
    uint32_t sample_count = 0;
    uint32_t sat_count = 0;

    double grad_sum = 0.0;
    uint32_t grad_count = 0;

    // 5-zone metrics: 4 corners + center (matching AS-13)
    // Corner zones: 1/3 of width & height
    int w_third = width / 3;
    int h_third = height / 3;
    int cx0 = width / 4, cx1 = 3 * width / 4;
    int cy0 = height / 4, cy1 = 3 * height / 4;

    double corner_grad_sum[4] = {0, 0, 0, 0};
    uint32_t corner_grad_count[4] = {0, 0, 0, 0};
    double corner_lum_sum = 0.0;
    uint32_t corner_lum_count = 0;
    double center_lum_sum = 0.0;
    uint32_t center_lum_count = 0;

    for (int y = 1; y < height - 1; y += grid_step) {
        const uint8_t* r_prev = src + (y - 1) * stride;
        const uint8_t* r_curr = src + y * stride;
        const uint8_t* r_next = src + (y + 1) * stride;

        for (int x = 1; x < width - 1; x += grid_step) {
            uint8_t val = r_curr[x];
            hist[val]++;
            sum += val;
            sum_sq += static_cast<double>(val) * val;
            sample_count++;

            if (val >= 250) sat_count++;

            // Spatial zone accumulation
            bool is_corner = false;
            int corner_idx = -1;
            if (y < h_third) {
                if (x < w_third) corner_idx = 0;        // TL
                else if (x >= 2 * w_third) corner_idx = 1; // TR
            } else if (y >= 2 * h_third) {
                if (x < w_third) corner_idx = 2;        // BL
                else if (x >= 2 * w_third) corner_idx = 3; // BR
            }

            if (corner_idx >= 0) {
                corner_lum_sum += val;
                corner_lum_count++;
                is_corner = true;
            } else if (x >= cx0 && x < cx1 && y >= cy0 && y < cy1) {
                center_lum_sum += val;
                center_lum_count++;
            }

            // High-frequency gradient (Sobel magnitude)
            int gx = r_curr[x + 1] - r_curr[x - 1];
            int gy = r_next[x] - r_prev[x];
            double g_mag = std::abs(gx) + std::abs(gy);

            grad_sum += g_mag;
            grad_count++;

            if (corner_idx >= 0) {
                corner_grad_sum[corner_idx] += g_mag;
                corner_grad_count[corner_idx]++;
            }
        }
    }

    if (sample_count == 0) return -1;

    double mean_lum = sum / sample_count;
    double var_lum = std::max(0.0, (sum_sq / sample_count) - (mean_lum * mean_lum));
    double std_lum = std::sqrt(var_lum);

    out->mean_brightness = static_cast<float>(mean_lum);
    out->contrast_std = static_cast<float>(std_lum);
    out->sharpness = (grad_count > 0) ? static_cast<float>(grad_sum / grad_count) : 0.0f;

    // Corner sharpness
    for (int i = 0; i < 4; ++i) {
        out->corner_sharpness[i] = (corner_grad_count[i] > 0)
            ? static_cast<float>(corner_grad_sum[i] / corner_grad_count[i])
            : out->sharpness;
    }

    // Uniformity: ratio of corner luminance to center luminance
    double corner_avg = (corner_lum_count > 0) ? (corner_lum_sum / corner_lum_count) : mean_lum;
    double center_avg = (center_lum_count > 0) ? (center_lum_sum / center_lum_count) : mean_lum;
    out->uniformity = static_cast<float>(corner_avg / (center_avg + 1e-4));

    out->saturated_ratio = static_cast<float>(static_cast<double>(sat_count) / sample_count);

    // Dark floor: 5th percentile from histogram
    uint32_t p5_target = sample_count / 20;
    uint32_t cumulative = 0;
    uint8_t floor_val = 0;
    for (int i = 0; i < 256; ++i) {
        cumulative += hist[i];
        if (cumulative >= p5_target) {
            floor_val = static_cast<uint8_t>(i);
            break;
        }
    }
    out->dark_floor = static_cast<float>(floor_val);

    return 0;
}

vs_drift_tracker* vs_drift_create(const vs_drift_params* p) {
    auto* t = new vs_drift_tracker();
    if (p) {
        t->params = *p;
    } else {
        vs_drift_default_params(&t->params);
    }
    vs_drift_reset(t);
    return t;
}

void vs_drift_destroy(vs_drift_tracker* tracker) {
    delete tracker;
}

void vs_drift_reset(vs_drift_tracker* tracker) {
    if (!tracker) return;
    tracker->finalized = false;
    tracker->teach_samples.clear();
    tracker->window_dist.clear();
    tracker->window_brightness.clear();
    tracker->window_sharpness.clear();
    std::memset(tracker->mean, 0, sizeof(tracker->mean));
    std::memset(tracker->std_dev, 0, sizeof(tracker->std_dev));
    std::memset(tracker->cov_inv, 0, sizeof(tracker->cov_inv));
}

int32_t vs_drift_teach_sample(vs_drift_tracker* tracker, const vs_drift_features* feat) {
    if (!tracker || !feat) return -1;
    tracker->teach_samples.push_back(*feat);
    tracker->finalized = false;
    return 0;
}

int32_t vs_drift_finalize_teach(vs_drift_tracker* tracker) {
    if (!tracker || tracker->teach_samples.size() < 5) {
        return -1; // Require at least 5 baseline samples
    }

    size_t n = tracker->teach_samples.size();

    // 1. Compute baseline mean vector μ
    std::memset(tracker->mean, 0, sizeof(tracker->mean));
    std::vector<std::vector<float>> data(n, std::vector<float>(D));

    for (size_t i = 0; i < n; ++i) {
        feat_to_array(&tracker->teach_samples[i], data[i].data());
        for (int j = 0; j < D; ++j) {
            tracker->mean[j] += data[i][j];
        }
    }
    for (int j = 0; j < D; ++j) {
        tracker->mean[j] /= static_cast<float>(n);
    }

    // 2. Compute sample covariance matrix Σ
    float cov[D][D];
    std::memset(cov, 0, sizeof(cov));

    for (size_t i = 0; i < n; ++i) {
        for (int j = 0; j < D; ++j) {
            float dj = data[i][j] - tracker->mean[j];
            for (int k = 0; k < D; ++k) {
                float dk = data[i][k] - tracker->mean[k];
                cov[j][k] += dj * dk;
            }
        }
    }

    float inv_n1 = 1.0f / static_cast<float>(n - 1);
    for (int j = 0; j < D; ++j) {
        for (int k = 0; k < D; ++k) {
            cov[j][k] *= inv_n1;
        }
        tracker->std_dev[j] = std::sqrt(std::max(1e-6f, cov[j][j]));
        // Tikhonov diagonal regularization for conditioning
        cov[j][j] += 1e-3f * (tracker->std_dev[j] * tracker->std_dev[j] + 1e-4f);
    }

    // 3. Invert covariance matrix
    if (!invert_matrix(cov, tracker->cov_inv)) {
        return -2; // Inversion failed
    }

    tracker->finalized = true;
    tracker->window_dist.clear();
    tracker->window_brightness.clear();
    tracker->window_sharpness.clear();
    return 0;
}

int32_t vs_drift_update(
    vs_drift_tracker* tracker,
    const vs_drift_features* feat,
    float* out_dist,
    int32_t* out_status,
    char* out_diagnostic,
    int32_t diag_cap)
{
    if (!tracker || !feat || !tracker->finalized) {
        return -1;
    }

    float arr[D];
    feat_to_array(feat, arr);

    // Compute instantaneous Mahalanobis distance D_M(x)
    float diff[D];
    for (int j = 0; j < D; ++j) {
        diff[j] = arr[j] - tracker->mean[j];
    }

    float dm2 = 0.0f;
    for (int j = 0; j < D; ++j) {
        float row_sum = 0.0f;
        for (int k = 0; k < D; ++k) {
            row_sum += tracker->cov_inv[j][k] * diff[k];
        }
        dm2 += diff[j] * row_sum;
    }

    float dm = std::sqrt(std::max(0.0f, dm2));

    // Update moving window
    tracker->window_dist.push_back(dm);
    tracker->window_brightness.push_back(arr[0]);
    tracker->window_sharpness.push_back(arr[2]);

    int w_size = std::max(5, tracker->params.window_size);
    while (static_cast<int>(tracker->window_dist.size()) > w_size) {
        tracker->window_dist.pop_front();
        tracker->window_brightness.pop_front();
        tracker->window_sharpness.pop_front();
    }

    // Compute moving window average distance
    float avg_dist = 0.0f;
    for (float d_val : tracker->window_dist) avg_dist += d_val;
    avg_dist /= tracker->window_dist.size();

    // Directional shifts
    float avg_bright = 0.0f;
    for (float b_val : tracker->window_brightness) avg_bright += b_val;
    avg_bright /= tracker->window_brightness.size();

    float avg_sharp = 0.0f;
    for (float s_val : tracker->window_sharpness) avg_sharp += s_val;
    avg_sharp /= tracker->window_sharpness.size();

    float z_bright = (avg_bright - tracker->mean[0]) / std::max(1e-4f, tracker->std_dev[0]);
    float z_sharp = (avg_sharp - tracker->mean[2]) / std::max(1e-4f, tracker->std_dev[2]);

    int32_t status = VS_DRIFT_STATUS_OK;
    char diag_buf[256];

    // Only diagnose if we have accumulated at least 5 parts
    if (tracker->window_dist.size() >= 5 && avg_dist >= tracker->params.warn_distance_threshold) {
        if (z_sharp <= -tracker->params.dirt_sharpness_sigma && z_bright > -tracker->params.light_drop_sigma) {
            status = VS_DRIFT_WARN_LENS_DIRT;
            std::snprintf(diag_buf, sizeof(diag_buf),
                "OPTICAL DRIFT: Lens contamination / defocus detected (sharpness: %.1f sigma, DM: %.1f)",
                z_sharp, avg_dist);
        } else if (z_bright <= -tracker->params.light_drop_sigma) {
            status = VS_DRIFT_WARN_LIGHTING_DROP;
            std::snprintf(diag_buf, sizeof(diag_buf),
                "OPTICAL DRIFT: LED strobe fading / illumination drop detected (brightness: %.1f sigma, DM: %.1f)",
                z_bright, avg_dist);
        } else if (z_bright >= tracker->params.light_drop_sigma) {
            status = VS_DRIFT_WARN_OVEREXPOSURE;
            std::snprintf(diag_buf, sizeof(diag_buf),
                "OPTICAL DRIFT: Lighting flare / overexposure detected (brightness: +%.1f sigma, DM: %.1f)",
                z_bright, avg_dist);
        } else {
            status = VS_DRIFT_WARN_OOD;
            std::snprintf(diag_buf, sizeof(diag_buf),
                "OPTICAL DRIFT: Unrecognized optical shift detected (DM: %.1f)", avg_dist);
        }
    } else {
        status = VS_DRIFT_STATUS_OK;
        std::snprintf(diag_buf, sizeof(diag_buf),
            "Optical baseline healthy (DM: %.2f)", avg_dist);
    }

    if (out_dist) *out_dist = avg_dist;
    if (out_status) *out_status = status;
    if (out_diagnostic && diag_cap > 0) {
        std::strncpy(out_diagnostic, diag_buf, diag_cap - 1);
        out_diagnostic[diag_cap - 1] = '\0';
    }

    return 0;
}

int32_t vs_drift_inspect_mono8(
    vs_drift_tracker* tracker,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    float* out_dist,
    int32_t* out_status,
    char* out_diagnostic,
    int32_t diag_cap)
{
    if (!tracker || !src) return -1;
    vs_drift_features feat;
    int32_t res = vs_drift_extract_features(src, width, height, stride, tracker->params.grid_step, &feat);
    if (res != 0) return res;
    return vs_drift_update(tracker, &feat, out_dist, out_status, out_diagnostic, diag_cap);
}

} // extern "C"
