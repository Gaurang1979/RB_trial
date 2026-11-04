/*
 * inspect.cpp - Implementation of the Golden Template & Variation Model Inspection Engine (Tool 8.2, AS-05).
 */
#include "vs_inspect.h"

#include <cmath>
#include <cstring>
#include <chrono>
#include <vector>
#include <algorithm>
#include <fstream>
#include <queue>

namespace {

struct BlobInfo {
    int32_t min_x = 0;
    int32_t min_y = 0;
    int32_t max_x = 0;
    int32_t max_y = 0;
    int32_t area = 0;
    float   peak_contrast = 0.0f;
    int32_t bright_count = 0;
    int32_t dark_count = 0;
};

} // namespace

struct vs_inspect_model {
    int32_t width = 0;
    int32_t height = 0;
    vs_inspect_params params{};
    int32_t sample_count = 0;
    bool is_finalized = false;
    float global_golden_mean = 128.0f;

    // Accumulators during teaching
    std::vector<double> sum;
    std::vector<double> sum_sq;

    // Model statistics
    std::vector<float> mean;
    std::vector<float> std_dev;

    // Computed upper and lower inspection envelopes
    std::vector<uint8_t> upper_bound;
    std::vector<uint8_t> lower_bound;

    void rebuild_envelopes() {
        if (!is_finalized || width <= 0 || height <= 0) return;

        const size_t n_pixels = static_cast<size_t>(width) * height;
        upper_bound.resize(n_pixels);
        lower_bound.resize(n_pixels);

        // 1. Compute horizontal and vertical gradients on the mean image
        std::vector<float> grad_mag(n_pixels, 0.0f);
        for (int32_t y = 1; y < height - 1; ++y) {
            const int32_t y_prev = (y - 1) * width;
            const int32_t y_curr = y * width;
            const int32_t y_next = (y + 1) * width;

            for (int32_t x = 1; x < width - 1; ++x) {
                // Sobel gx
                float gx = (mean[y_prev + x + 1] + 2.0f * mean[y_curr + x + 1] + mean[y_next + x + 1]) -
                           (mean[y_prev + x - 1] + 2.0f * mean[y_curr + x - 1] + mean[y_next + x - 1]);
                // Sobel gy
                float gy = (mean[y_next + x - 1] + 2.0f * mean[y_next + x] + mean[y_next + x + 1]) -
                           (mean[y_prev + x - 1] + 2.0f * mean[y_prev + x] + mean[y_prev + x + 1]);

                grad_mag[y_curr + x] = 0.125f * std::sqrt(gx * gx + gy * gy);
            }
        }

        // 2. Dilate gradient magnitude by edge_tolerance_px
        const int32_t rad = std::clamp(static_cast<int32_t>(std::ceil(params.edge_tolerance_px)), 0, 5);
        std::vector<float> dilated_grad = grad_mag;

        if (rad > 0) {
            for (int32_t y = 0; y < height; ++y) {
                const int32_t y_min = std::max(0, y - rad);
                const int32_t y_max = std::min(height - 1, y + rad);

                for (int32_t x = 0; x < width; ++x) {
                    const int32_t x_min = std::max(0, x - rad);
                    const int32_t x_max = std::min(width - 1, x + rad);

                    float max_val = 0.0f;
                    for (int32_t ny = y_min; ny <= y_max; ++ny) {
                        const int32_t ny_offset = ny * width;
                        for (int32_t nx = x_min; nx <= x_max; ++nx) {
                            max_val = std::max(max_val, grad_mag[ny_offset + nx]);
                        }
                    }
                    dilated_grad[y * width + x] = max_val;
                }
            }
        }

        // 3. Compute envelopes U(x, y) and L(x, y)
        const float k = std::max(0.1f, params.sensitivity);
        const float c_offset = std::max(1.0f, params.base_offset);
        const float alpha = std::max(0.0f, params.edge_margin_weight);
        const float edge_factor = std::max(0.0f, params.edge_tolerance_px) * (alpha > 0.0f ? (alpha * 8.0f) : 1.0f);

        for (size_t i = 0; i < n_pixels; ++i) {
            const float m = mean[i];
            const float s = std_dev[i];
            const float edge_margin = edge_factor * dilated_grad[i];
            const float total_tol = k * s + c_offset + edge_margin;

            const float u = std::round(m + total_tol);
            const float l = std::round(m - total_tol);

            upper_bound[i] = static_cast<uint8_t>(std::clamp(u, 0.0f, 255.0f));
            lower_bound[i] = static_cast<uint8_t>(std::clamp(l, 0.0f, 255.0f));
        }
    }
};

extern "C" {

void vs_inspect_default_params(vs_inspect_params* p) {
    if (!p) return;
    p->struct_size = sizeof(vs_inspect_params);
    p->sensitivity = 1.0f;
    p->base_offset = 12.0f;
    p->edge_tolerance_px = 1.5f;
    p->edge_margin_weight = 0.15f;
    p->min_defect_area_px = 15;
    p->normalize_lighting = 1;
}

vs_inspect_model* vs_inspect_create(int32_t width, int32_t height, const vs_inspect_params* p) {
    if (width <= 0 || height <= 0) return nullptr;

    auto* m = new (std::nothrow) vs_inspect_model();
    if (!m) return nullptr;

    m->width = width;
    m->height = height;

    if (p && p->struct_size == sizeof(vs_inspect_params)) {
        m->params = *p;
    } else {
        vs_inspect_default_params(&m->params);
    }

    const size_t n_pixels = static_cast<size_t>(width) * height;
    m->sum.assign(n_pixels, 0.0);
    m->sum_sq.assign(n_pixels, 0.0);
    m->sample_count = 0;
    m->is_finalized = false;

    return m;
}

void vs_inspect_destroy(vs_inspect_model* model) {
    delete model;
}

void vs_inspect_reset(vs_inspect_model* model) {
    if (!model) return;
    const size_t n_pixels = static_cast<size_t>(model->width) * model->height;
    model->sum.assign(n_pixels, 0.0);
    model->sum_sq.assign(n_pixels, 0.0);
    model->mean.clear();
    model->std_dev.clear();
    model->upper_bound.clear();
    model->lower_bound.clear();
    model->sample_count = 0;
    model->is_finalized = false;
    model->global_golden_mean = 128.0f;
}

int32_t vs_inspect_set_params(vs_inspect_model* model, const vs_inspect_params* p) {
    if (!model || !p || p->struct_size != sizeof(vs_inspect_params)) return -1;
    model->params = *p;
    if (model->is_finalized) {
        model->rebuild_envelopes();
    }
    return 0;
}

int32_t vs_inspect_get_params(const vs_inspect_model* model, vs_inspect_params* p) {
    if (!model || !p || p->struct_size != sizeof(vs_inspect_params)) return -1;
    *p = model->params;
    return 0;
}

int32_t vs_inspect_add_sample(
    vs_inspect_model* model,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride)
{
    if (!model || !src) return -1;
    if (width != model->width || height != model->height || stride < width) return -2;

    const size_t n_pixels = static_cast<size_t>(width) * height;
    if (model->sum.size() != n_pixels) {
        model->sum.assign(n_pixels, 0.0);
        model->sum_sq.assign(n_pixels, 0.0);
    }

    for (int32_t y = 0; y < height; ++y) {
        const uint8_t* row = src + y * stride;
        const size_t row_offset = static_cast<size_t>(y) * width;
        for (int32_t x = 0; x < width; ++x) {
            const double val = static_cast<double>(row[x]);
            model->sum[row_offset + x] += val;
            model->sum_sq[row_offset + x] += val * val;
        }
    }

    model->sample_count++;
    model->is_finalized = false; // Must be finalized again
    return 0;
}

int32_t vs_inspect_finalize_teach(
    vs_inspect_model* model,
    int32_t* out_samples_trained)
{
    if (!model) return -1;
    if (model->sample_count < 1) return -2; // At least 1 golden sample required (ideal 5..20)

    const size_t n_pixels = static_cast<size_t>(model->width) * model->height;
    model->mean.resize(n_pixels);
    model->std_dev.resize(n_pixels);

    const double n = static_cast<double>(model->sample_count);
    double total_sum = 0.0;

    for (size_t i = 0; i < n_pixels; ++i) {
        const double mean_val = model->sum[i] / n;
        model->mean[i] = static_cast<float>(mean_val);
        total_sum += mean_val;

        if (model->sample_count > 1) {
            double var = (model->sum_sq[i] - (model->sum[i] * model->sum[i]) / n) / (n - 1.0);
            if (var < 0.0) var = 0.0;
            model->std_dev[i] = static_cast<float>(std::sqrt(var));
        } else {
            model->std_dev[i] = 2.0f; // Minimal baseline variance for single golden template
        }
    }

    model->global_golden_mean = static_cast<float>(total_sum / static_cast<double>(n_pixels));
    model->is_finalized = true;
    model->rebuild_envelopes();

    if (out_samples_trained) {
        *out_samples_trained = model->sample_count;
    }
    return 0;
}

int32_t vs_inspect_run(
    const vs_inspect_model* model,
    const uint8_t* src,
    int32_t width,
    int32_t height,
    int32_t stride,
    vs_inspect_result* out_result,
    uint8_t* diff_out,
    int32_t diff_stride)
{
    if (!model || !src || !out_result) return -1;
    if (out_result->struct_size != sizeof(vs_inspect_result)) return -1;
    if (!model->is_finalized) {
        out_result->verdict = VS_INSPECT_ERROR;
        out_result->defect_count = 0;
        return -2;
    }
    if (width != model->width || height != model->height || stride < width) {
        out_result->verdict = VS_INSPECT_ERROR;
        out_result->defect_count = 0;
        return -3;
    }

    auto t_start = std::chrono::steady_clock::now();

    const size_t n_pixels = static_cast<size_t>(width) * height;

    // Optional lighting normalization
    float light_offset = 0.0f;
    if (model->params.normalize_lighting) {
        double current_sum = 0.0;
        // Fast sampled mean
        const int32_t step = std::max(1, width / 64);
        size_t count = 0;
        for (int32_t y = 0; y < height; y += step) {
            const uint8_t* row = src + y * stride;
            for (int32_t x = 0; x < width; x += step) {
                current_sum += row[x];
                count++;
            }
        }
        if (count > 0) {
            const float current_mean = static_cast<float>(current_sum / count);
            light_offset = current_mean - model->global_golden_mean;
        }
    }

    // 1. Calculate difference image and polarity map
    std::vector<uint8_t> diff_map(n_pixels, 0);
    std::vector<int8_t> polarity_map(n_pixels, 0); // +1 = bright, -1 = dark

    const uint8_t* upper = model->upper_bound.data();
    const uint8_t* lower = model->lower_bound.data();

    float global_peak_diff = 0.0f;

    for (int32_t y = 0; y < height; ++y) {
        const uint8_t* row_src = src + y * stride;
        const size_t row_offset = static_cast<size_t>(y) * width;
        uint8_t* row_diff_out = diff_out ? (diff_out + y * diff_stride) : nullptr;

        for (int32_t x = 0; x < width; ++x) {
            const size_t idx = row_offset + x;
            float val = static_cast<float>(row_src[x]) - light_offset;
            int32_t ival = std::clamp(static_cast<int32_t>(std::round(val)), 0, 255);

            uint8_t diff = 0;
            int8_t pol = 0;

            const uint8_t u = upper[idx];
            const uint8_t l = lower[idx];

            if (ival > u) {
                diff = static_cast<uint8_t>(ival - u);
                pol = 1;
            } else if (ival < l) {
                diff = static_cast<uint8_t>(l - ival);
                pol = -1;
            }

            diff_map[idx] = diff;
            polarity_map[idx] = pol;

            if (diff > global_peak_diff) {
                global_peak_diff = diff;
            }

            if (row_diff_out) {
                row_diff_out[x] = diff;
            }
        }
    }

    // 2. Connected component labeling on non-zero diff_map (8-way connectivity)
    std::vector<uint8_t> visited(n_pixels, 0);
    std::vector<BlobInfo> blobs;

    const int32_t dx[8] = {-1, 0, 1, -1, 1, -1, 0, 1};
    const int32_t dy[8] = {-1, -1, -1, 0, 0, 1, 1, 1};

    std::queue<int32_t> q;

    for (int32_t y = 0; y < height; ++y) {
        const size_t row_offset = static_cast<size_t>(y) * width;
        for (int32_t x = 0; x < width; ++x) {
            const size_t idx = row_offset + x;
            if (diff_map[idx] == 0 || visited[idx]) continue;

            // Start new blob
            visited[idx] = 1;
            q.push(static_cast<int32_t>(idx));

            BlobInfo blob{};
            blob.min_x = x;
            blob.max_x = x;
            blob.min_y = y;
            blob.max_y = y;

            while (!q.empty()) {
                const int32_t curr_idx = q.front();
                q.pop();

                const int32_t cx = curr_idx % width;
                const int32_t cy = curr_idx / width;

                blob.area++;
                blob.min_x = std::min(blob.min_x, cx);
                blob.max_x = std::max(blob.max_x, cx);
                blob.min_y = std::min(blob.min_y, cy);
                blob.max_y = std::max(blob.max_y, cy);

                const uint8_t d = diff_map[curr_idx];
                if (d > blob.peak_contrast) {
                    blob.peak_contrast = d;
                }

                if (polarity_map[curr_idx] > 0) {
                    blob.bright_count++;
                } else if (polarity_map[curr_idx] < 0) {
                    blob.dark_count++;
                }

                // Check 8 neighbors
                for (int32_t dir = 0; dir < 8; ++dir) {
                    const int32_t nx = cx + dx[dir];
                    const int32_t ny = cy + dy[dir];

                    if (nx >= 0 && nx < width && ny >= 0 && ny < height) {
                        const size_t n_idx = static_cast<size_t>(ny) * width + nx;
                        if (diff_map[n_idx] > 0 && !visited[n_idx]) {
                            visited[n_idx] = 1;
                            q.push(static_cast<int32_t>(n_idx));
                        }
                    }
                }
            }

            blobs.push_back(blob);
        }
    }

    // 3. Filter defect blobs by min_defect_area_px
    const int32_t min_area = std::max(1, model->params.min_defect_area_px);
    int32_t defect_count = 0;
    int32_t total_defect_area = 0;
    float max_contrast = 0.0f;
    BlobInfo worst_blob{};

    for (const auto& b : blobs) {
        if (b.area >= min_area) {
            defect_count++;
            total_defect_area += b.area;
            max_contrast = std::max(max_contrast, b.peak_contrast);
            if (b.area > worst_blob.area) {
                worst_blob = b;
            }
        }
    }

    // 4. Fill result
    out_result->defect_count = defect_count;
    out_result->total_defect_area = total_defect_area;
    out_result->max_contrast = max_contrast;

    if (defect_count > 0) {
        out_result->verdict = VS_INSPECT_DEFECT;
        out_result->worst_defect.bbox_x = worst_blob.min_x;
        out_result->worst_defect.bbox_y = worst_blob.min_y;
        out_result->worst_defect.bbox_w = worst_blob.max_x - worst_blob.min_x + 1;
        out_result->worst_defect.bbox_h = worst_blob.max_y - worst_blob.min_y + 1;
        out_result->worst_defect.area_px = worst_blob.area;
        out_result->worst_defect.peak_contrast = worst_blob.peak_contrast;

        if (worst_blob.bright_count > 0 && worst_blob.dark_count > 0 &&
            std::min(worst_blob.bright_count, worst_blob.dark_count) >= worst_blob.area * 0.2f) {
            out_result->worst_defect.defect_type = VS_DEFECT_MIXED;
        } else if (worst_blob.bright_count >= worst_blob.dark_count) {
            out_result->worst_defect.defect_type = VS_DEFECT_BRIGHT;
        } else {
            out_result->worst_defect.defect_type = VS_DEFECT_DARK;
        }

        // Confidence in defect (100 = massive clear defect)
        float conf = std::clamp(50.0f + 2.0f * worst_blob.peak_contrast, 50.0f, 100.0f);
        out_result->confidence = conf;
    } else {
        out_result->verdict = VS_INSPECT_PASS;
        std::memset(&out_result->worst_defect, 0, sizeof(vs_inspect_defect));

        // Margin to threshold
        float conf = std::clamp(100.0f - 2.0f * global_peak_diff, 60.0f, 100.0f);
        out_result->confidence = conf;
    }

    auto t_end = std::chrono::steady_clock::now();
    out_result->time_ms = std::chrono::duration<float, std::milli>(t_end - t_start).count();

    return 0;
}

int32_t vs_inspect_save(const vs_inspect_model* model, const char* filepath) {
    if (!model || !filepath || !model->is_finalized) return -1;

    std::ofstream out(filepath, std::ios::binary);
    if (!out.is_open()) return -2;

    const uint32_t magic = 0x5653494D; // 'VSIM'
    const uint32_t version = 1;
    out.write(reinterpret_cast<const char*>(&magic), sizeof(magic));
    out.write(reinterpret_cast<const char*>(&version), sizeof(version));
    out.write(reinterpret_cast<const char*>(&model->width), sizeof(model->width));
    out.write(reinterpret_cast<const char*>(&model->height), sizeof(model->height));
    out.write(reinterpret_cast<const char*>(&model->sample_count), sizeof(model->sample_count));
    out.write(reinterpret_cast<const char*>(&model->global_golden_mean), sizeof(model->global_golden_mean));
    out.write(reinterpret_cast<const char*>(&model->params), sizeof(model->params));

    const size_t n_pixels = static_cast<size_t>(model->width) * model->height;
    out.write(reinterpret_cast<const char*>(model->mean.data()), n_pixels * sizeof(float));
    out.write(reinterpret_cast<const char*>(model->std_dev.data()), n_pixels * sizeof(float));

    if (!out.good()) return -3;
    return 0;
}

int32_t vs_inspect_load(const char* filepath, vs_inspect_model** out_model) {
    if (!filepath || !out_model) return -1;
    *out_model = nullptr;

    std::ifstream in(filepath, std::ios::binary);
    if (!in.is_open()) return -2;

    uint32_t magic = 0, version = 0;
    in.read(reinterpret_cast<char*>(&magic), sizeof(magic));
    in.read(reinterpret_cast<char*>(&version), sizeof(version));

    if (magic != 0x5653494D || version != 1) return -3;

    int32_t width = 0, height = 0, sample_count = 0;
    float global_mean = 0.0f;
    vs_inspect_params params{};

    in.read(reinterpret_cast<char*>(&width), sizeof(width));
    in.read(reinterpret_cast<char*>(&height), sizeof(height));
    in.read(reinterpret_cast<char*>(&sample_count), sizeof(sample_count));
    in.read(reinterpret_cast<char*>(&global_mean), sizeof(global_mean));
    in.read(reinterpret_cast<char*>(&params), sizeof(params));

    if (width <= 0 || height <= 0 || !in.good()) return -4;

    auto* m = vs_inspect_create(width, height, &params);
    if (!m) return -5;

    const size_t n_pixels = static_cast<size_t>(width) * height;
    m->mean.resize(n_pixels);
    m->std_dev.resize(n_pixels);
    in.read(reinterpret_cast<char*>(m->mean.data()), n_pixels * sizeof(float));
    in.read(reinterpret_cast<char*>(m->std_dev.data()), n_pixels * sizeof(float));

    if (!in.good()) {
        vs_inspect_destroy(m);
        return -6;
    }

    m->sample_count = sample_count;
    m->global_golden_mean = global_mean;
    m->is_finalized = true;
    m->rebuild_envelopes();

    *out_model = m;
    return 0;
}

} // extern "C"
