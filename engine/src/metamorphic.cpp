#include "vs_metamorphic.h"
#include <cmath>
#include <cstring>
#include <algorithm>
#include <vector>
#include <cstdio>

namespace {

inline float clamp_f(float v, float mn, float mx) {
    return (v < mn) ? mn : ((v > mx) ? mx : v);
}

// Deterministic Pseudo-Random Generator (LCG) for reproducible noise across runs
uint32_t g_seed = 0x12345678;
inline float next_gaussian(float sigma) {
    if (sigma <= 0.0f) return 0.0f;
    // Box-Muller transform using LCG
    g_seed = (1664525u * g_seed + 1013904223u);
    float u1 = ((float)(g_seed & 0xFFFF) + 1.0f) / 65537.0f;
    g_seed = (1664525u * g_seed + 1013904223u);
    float u2 = ((float)(g_seed & 0xFFFF) + 1.0f) / 65537.0f;

    float z0 = std::sqrt(-2.0f * std::log(u1)) * std::cos(2.0f * 3.14159265f * u2);
    return z0 * sigma;
}

inline uint8_t sample_bilinear(const uint8_t* img, int w, int h, int stride, float x, float y, uint8_t border_val) {
    if (x < 0.0f || y < 0.0f || x > (float)(w - 1) || y > (float)(h - 1)) {
        return border_val;
    }

    int x0 = (int)x;
    int y0 = (int)y;
    int x1 = std::min(x0 + 1, w - 1);
    int y1 = std::min(y0 + 1, h - 1);

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

} // namespace

extern "C" {

void vs_metamorphic_default_config(vs_metamorphic_config* config) {
    if (!config) return;
    config->struct_size = sizeof(vs_metamorphic_config);
    config->max_rotation_deg = 3.0f;
    config->rotation_steps = 7;
    config->max_illum_gain_delta = 0.10f;
    config->illum_steps = 5;
    config->max_scale_delta = 0.05f;
    config->scale_steps = 5;
    config->max_noise_sigma = 5.0f;
    config->noise_steps = 4;
    config->max_quiet_zone_px = 8;
    config->max_allowed_jitter_px = 0.75f;
    config->min_acceptable_confidence = 0.60f;
}

int vs_metamorphic_apply_transform(
    const uint8_t* src,
    int src_w,
    int src_h,
    int src_stride,
    const vs_metamorphic_transform* t,
    uint8_t* dst,
    int dst_w,
    int dst_h,
    int dst_stride)
{
    if (!src || !dst || !t) return VS_METAMORPHIC_ERR_NULL_PTR;
    if (src_w <= 0 || src_h <= 0 || dst_w <= 0 || dst_h <= 0) return VS_METAMORPHIC_ERR_INVALID_DIM;

    float rad = t->rot_angle_deg * (3.141592653589793f / 180.0f);
    float cos_t = std::cos(rad);
    float sin_t = std::sin(rad);
    float s = (t->scale > 0.01f) ? t->scale : 1.0f;
    float inv_s = 1.0f / s;

    float src_cx = (float)src_w * 0.5f;
    float src_cy = (float)src_h * 0.5f;
    float dst_cx = (float)dst_w * 0.5f;
    float dst_cy = (float)dst_h * 0.5f;

    // Border value computed from corner pixels of src
    uint8_t border_val = (uint8_t)(((int)src[0] + (int)src[src_w - 1] +
                                    (int)src[(src_h - 1) * src_stride] +
                                    (int)src[(src_h - 1) * src_stride + src_w - 1]) >> 2);

    for (int dy = 0; dy < dst_h; ++dy) {
        float dy_rel = (float)dy - dst_cy - t->shift_y;
        for (int dx = 0; dx < dst_w; ++dx) {
            float dx_rel = (float)dx - dst_cx - t->shift_x;

            // Inverse affine coordinate mapping
            float sx = src_cx + inv_s * (dx_rel * cos_t + dy_rel * sin_t);
            float sy = src_cy + inv_s * (-dx_rel * sin_t + dy_rel * cos_t);

            uint8_t raw = sample_bilinear(src, src_w, src_h, src_stride, sx, sy, border_val);

            // Photometric transform: I' = gamma * I + beta
            float gamma = (t->gain_gamma > 0.0f) ? t->gain_gamma : 1.0f;
            float val = gamma * (float)raw + t->bias_beta;

            // Additive sensor noise
            if (t->noise_sigma > 0.0f) {
                val += next_gaussian(t->noise_sigma);
            }

            dst[dy * dst_stride + dx] = (uint8_t)clamp_f(val + 0.5f, 0.0f, 255.0f);
        }
    }

    return VS_METAMORPHIC_OK;
}

int vs_metamorphic_run_fuzzing_suite(
    const uint8_t* baseline_img,
    int w,
    int h,
    int stride,
    const vs_metamorphic_config* config,
    vs_vision_eval_fn eval_fn,
    void* user_data,
    vs_metamorphic_report* report)
{
    if (!baseline_img || !eval_fn || !report) return VS_METAMORPHIC_ERR_NULL_PTR;
    if (w <= 0 || h <= 0) return VS_METAMORPHIC_ERR_INVALID_DIM;

    vs_metamorphic_config cfg;
    if (config) {
        cfg = *config;
    } else {
        vs_metamorphic_default_config(&cfg);
    }

    std::memset(report, 0, sizeof(vs_metamorphic_report));
    report->struct_size = sizeof(vs_metamorphic_report);
    report->min_observed_confidence = 1.0f;
    report->max_observed_confidence = 0.0f;

    // 1. Evaluate baseline image
    float base_conf = 0.0f, base_x = 0.0f, base_y = 0.0f;
    int base_verdict = 0;
    int rc = eval_fn(baseline_img, w, h, stride, user_data, &base_conf, &base_x, &base_y, &base_verdict);
    if (rc != 0) {
        snprintf(report->failure_reason, sizeof(report->failure_reason), "Baseline evaluation failed with rc=%d", rc);
        return rc;
    }

    report->min_observed_confidence = base_conf;
    report->max_observed_confidence = base_conf;

    float cx = (float)w * 0.5f;
    float cy = (float)h * 0.5f;

    std::vector<uint8_t> buffer(w * h);
    double total_jitter = 0.0;
    int jitter_count = 0;

    auto record_eval = [&](float conf, float pred_x, float pred_y, float exp_x, float exp_y, int verdict, const char* mr_name) {
        report->total_evaluations++;
        if (conf < report->min_observed_confidence) report->min_observed_confidence = conf;
        if (conf > report->max_observed_confidence) report->max_observed_confidence = conf;

        float jx = pred_x - exp_x;
        float jy = pred_y - exp_y;
        float jitter = std::sqrt(jx * jx + jy * jy);

        total_jitter += jitter;
        jitter_count++;
        if (jitter > report->max_positional_jitter_px) {
            report->max_positional_jitter_px = jitter;
        }

        if (verdict != base_verdict) {
            report->verdict_flips++;
            if (report->failure_reason[0] == '\0') {
                snprintf(report->failure_reason, sizeof(report->failure_reason),
                         "Verdict flip under %s (exp=%d, got=%d)", mr_name, base_verdict, verdict);
            }
        }

        if (jitter > cfg.max_allowed_jitter_px && report->failure_reason[0] == '\0') {
            snprintf(report->failure_reason, sizeof(report->failure_reason),
                     "Excessive jitter under %s: %.2f px > %.2f px", mr_name, jitter, cfg.max_allowed_jitter_px);
        }

        if (conf < cfg.min_acceptable_confidence && report->failure_reason[0] == '\0') {
            snprintf(report->failure_reason, sizeof(report->failure_reason),
                     "Confidence drop under %s: %.2f < %.2f", mr_name, conf, cfg.min_acceptable_confidence);
        }
    };

    // --- MR-1: Rotational Equivariance & Invariance (MR_ROT) ---
    int rot_steps = std::max(3, cfg.rotation_steps);
    for (int i = 0; i < rot_steps; ++i) {
        float angle = -cfg.max_rotation_deg + (2.0f * cfg.max_rotation_deg * (float)i) / (float)(rot_steps - 1);
        if (std::abs(angle) < 0.05f) continue; // skip identity

        vs_metamorphic_transform t = {0};
        t.rot_angle_deg = angle;
        t.scale = 1.0f;
        t.gain_gamma = 1.0f;

        vs_metamorphic_apply_transform(baseline_img, w, h, stride, &t, buffer.data(), w, h, w);

        float conf = 0.0f, pred_x = 0.0f, pred_y = 0.0f;
        int verdict = 0;
        eval_fn(buffer.data(), w, h, w, user_data, &conf, &pred_x, &pred_y, &verdict);

        // Expected rotated position
        float rad = angle * (3.141592653589793f / 180.0f);
        float dx = base_x - cx;
        float dy = base_y - cy;
        float exp_x = cx + (dx * std::cos(rad) - dy * std::sin(rad));
        float exp_y = cy + (dx * std::sin(rad) + dy * std::cos(rad));

        record_eval(conf, pred_x, pred_y, exp_x, exp_y, verdict, "MR_ROT");
    }

    // --- MR-2: Photometric Monotonicity (MR_PHOTO) ---
    int illum_steps = std::max(3, cfg.illum_steps);
    for (int i = 0; i < illum_steps; ++i) {
        float gamma = (1.0f - cfg.max_illum_gain_delta) + (2.0f * cfg.max_illum_gain_delta * (float)i) / (float)(illum_steps - 1);
        if (std::abs(gamma - 1.0f) < 0.01f) continue;

        vs_metamorphic_transform t = {0};
        t.scale = 1.0f;
        t.gain_gamma = gamma;
        t.bias_beta = (gamma - 1.0f) * 20.0f;

        vs_metamorphic_apply_transform(baseline_img, w, h, stride, &t, buffer.data(), w, h, w);

        float conf = 0.0f, pred_x = 0.0f, pred_y = 0.0f;
        int verdict = 0;
        eval_fn(buffer.data(), w, h, w, user_data, &conf, &pred_x, &pred_y, &verdict);

        record_eval(conf, pred_x, pred_y, base_x, base_y, verdict, "MR_PHOTO");
    }

    // --- MR-3: Scale Invariance (MR_SCALE) ---
    int scale_steps = std::max(3, cfg.scale_steps);
    for (int i = 0; i < scale_steps; ++i) {
        float s = (1.0f - cfg.max_scale_delta) + (2.0f * cfg.max_scale_delta * (float)i) / (float)(scale_steps - 1);
        if (std::abs(s - 1.0f) < 0.005f) continue;

        vs_metamorphic_transform t = {0};
        t.scale = s;
        t.gain_gamma = 1.0f;

        vs_metamorphic_apply_transform(baseline_img, w, h, stride, &t, buffer.data(), w, h, w);

        float conf = 0.0f, pred_x = 0.0f, pred_y = 0.0f;
        int verdict = 0;
        eval_fn(buffer.data(), w, h, w, user_data, &conf, &pred_x, &pred_y, &verdict);

        float exp_x = cx + s * (base_x - cx);
        float exp_y = cy + s * (base_y - cy);

        record_eval(conf, pred_x, pred_y, exp_x, exp_y, verdict, "MR_SCALE");
    }

    // --- MR-4: Additive Sensor Noise Resilience (MR_NOISE) ---
    int noise_steps = std::max(2, cfg.noise_steps);
    for (int i = 0; i < noise_steps; ++i) {
        float sigma = cfg.max_noise_sigma * ((float)(i + 1) / (float)noise_steps);

        vs_metamorphic_transform t = {0};
        t.scale = 1.0f;
        t.gain_gamma = 1.0f;
        t.noise_sigma = sigma;

        vs_metamorphic_apply_transform(baseline_img, w, h, stride, &t, buffer.data(), w, h, w);

        float conf = 0.0f, pred_x = 0.0f, pred_y = 0.0f;
        int verdict = 0;
        eval_fn(buffer.data(), w, h, w, user_data, &conf, &pred_x, &pred_y, &verdict);

        record_eval(conf, pred_x, pred_y, base_x, base_y, verdict, "MR_NOISE");
    }

    // --- MR-5: Quiet-Zone Monotonicity (MR_QUIET) ---
    if (cfg.max_quiet_zone_px > 0) {
        int pad = cfg.max_quiet_zone_px;
        int pad_w = w + 2 * pad;
        int pad_h = h + 2 * pad;
        std::vector<uint8_t> padded(pad_w * pad_h, baseline_img[0]);

        for (int y = 0; y < h; ++y) {
            std::memcpy(&padded[(y + pad) * pad_w + pad], baseline_img + y * stride, w);
        }

        float conf = 0.0f, pred_x = 0.0f, pred_y = 0.0f;
        int verdict = 0;
        eval_fn(padded.data(), pad_w, pad_h, pad_w, user_data, &conf, &pred_x, &pred_y, &verdict);

        float exp_x = base_x + (float)pad;
        float exp_y = base_y + (float)pad;

        record_eval(conf, pred_x, pred_y, exp_x, exp_y, verdict, "MR_QUIET");
    }

    report->mean_positional_jitter_px = (jitter_count > 0) ? (float)(total_jitter / jitter_count) : 0.0f;
    report->is_robust = (report->verdict_flips == 0 &&
                         report->max_positional_jitter_px <= cfg.max_allowed_jitter_px &&
                         report->min_observed_confidence >= cfg.min_acceptable_confidence) ? 1 : 0;

    return VS_METAMORPHIC_OK;
}

} // extern "C"
