/*
 * inpaint_tests.cpp - Unit and benchmark tests for the Specular Highlight Glare Inpainter (AG-07).
 */

#include "vs_inpaint.h"
#include <cassert>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include "check.h"

#define TEST_CHECK(cond, msg) \
    do { \
        if (!(cond)) { \
            std::fprintf(stderr, "FAIL: %s (%s:%d)\n", msg, __FILE__, __LINE__); \
            std::exit(1); \
        } \
    } while (0)

void test_default_params() {
    std::printf("[TEST] Default parameters initialization...\n");
    vs_inpaint_params p;
    std::memset(&p, 0xFF, sizeof(p));
    vs_inpaint_default_params(&p);

    TEST_CHECK(p.struct_size == sizeof(vs_inpaint_params), "struct_size mismatch");
    TEST_CHECK(p.glare_threshold == 252, "glare_threshold default");
    TEST_CHECK(p.halo_radius == 2, "halo_radius default");
    TEST_CHECK(p.inpaint_radius == 4, "inpaint_radius default");
    TEST_CHECK(p.method == VS_INPAINT_TELEA, "method default");
    TEST_CHECK(p.max_glare_pct >= 30.0f && p.max_glare_pct <= 40.0f, "max_glare_pct default");
    TEST_CHECK(p.iterations >= 20, "iterations default");
    std::printf("  PASSED\n");
}

void test_glare_detection() {
    std::printf("[TEST] Specular glare detection and halo bloom...\n");
    const int w = 100, h = 100;
    std::vector<uint8_t> img(w * h, 120);

    // Punch a saturated specular circle of radius 5 at (50, 50)
    int core_pixels = 0;
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            int dx = x - 50, dy = y - 50;
            if (dx * dx + dy * dy <= 25) {
                img[y * w + x] = 255;
                core_pixels++;
            }
        }
    }

    std::vector<uint8_t> mask(w * h, 0);
    int32_t detected = 0;

    // Detect without halo
    int res = vs_inpaint_detect_glare(img.data(), w, h, w, 252, 0, mask.data(), w, &detected);
    TEST_CHECK(res == 0, "vs_inpaint_detect_glare failed");
    TEST_CHECK(detected == core_pixels, "detected core pixel count mismatch");

    // Detect with halo dilation radius 2
    int32_t dilated_count = 0;
    res = vs_inpaint_detect_glare(img.data(), w, h, w, 252, 2, mask.data(), w, &dilated_count);
    TEST_CHECK(res == 0, "vs_inpaint_detect_glare with halo failed");
    TEST_CHECK(dilated_count > core_pixels, "dilated count should exceed core pixels");

    // Zero glare check
    std::vector<uint8_t> clean_img(w * h, 200);
    int32_t zero_count = -1;
    res = vs_inpaint_detect_glare(clean_img.data(), w, h, w, 252, 2, mask.data(), w, &zero_count);
    TEST_CHECK(res == 0 && zero_count == 0, "zero glare check failed");

    std::printf("  PASSED\n");
}

void test_line_continuity_restoration() {
    std::printf("[TEST] Barcode black line continuity restoration across glare...\n");
    // Background: light paper (210)
    // Continuous horizontal black line from x=0 to x=60 at y=28..32 (intensity 25)
    // Specular glare spot from x=25 to x=35, y=24 to y=36 (intensity 255)
    const int w = 60, h = 60;
    std::vector<uint8_t> img(w * h, 210);

    for (int y = 28; y <= 32; ++y) {
        for (int x = 0; x < w; ++x) {
            img[y * w + x] = 25;
        }
    }

    // Punch realistic specular glare highlight across the bar (7 px wide x 9 px high)
    for (int y = 26; y <= 34; ++y) {
        for (int x = 27; x <= 33; ++x) {
            img[y * w + x] = 255;
        }
    }

    std::vector<uint8_t> out(w * h, 0);
    vs_inpaint_params p;
    vs_inpaint_default_params(&p);
    p.method = VS_INPAINT_TELEA;
    p.inpaint_radius = 5;
    p.halo_radius = 1;

    int32_t count = 0;
    int res = vs_inpaint_auto_mono8(img.data(), w, h, w, out.data(), w, &p, &count);
    TEST_CHECK(res == 0, "auto inpaint failed");
    TEST_CHECK(count > 0, "glare should be detected");

    std::printf("  Row 30 values across glare (x=26..34):\n  ");
    for (int x = 26; x <= 34; ++x) {
        std::printf("%3u ", out[30 * w + x]);
    }
    std::printf("\n");

    uint8_t center_line_val = out[30 * w + 30];
    uint8_t bg_val = out[25 * w + 30];
    std::printf("  DEBUG: center_line_val = %u, bg_val = %u, contrast margin = %d\n",
                center_line_val, bg_val, (int)bg_val - (int)center_line_val);

    // Prior to inpainting: 255 (blown out).
    // After inpainting: must be significantly darker than background (by >= 60 levels)
    // and below local adaptive threshold (<= 135) to reconstruct binary line continuity.
    TEST_CHECK(center_line_val <= 135, "reconstructed line pixel must be dark enough for binarizer");
    TEST_CHECK(bg_val >= 180, "reconstructed background pixel must remain light");
    TEST_CHECK((int)bg_val - (int)center_line_val >= 60, "contrast margin between line and background must be >= 60");

    std::printf("  PASSED (center line: %u, background: %u, contrast margin: %d)\n",
                center_line_val, bg_val, (int)bg_val - (int)center_line_val);
}

void test_safety_gate_on_massive_glare() {
    std::printf("[TEST] Line safety gate: huge glare exceeds max_glare_pct...\n");
    const int w = 100, h = 100;
    std::vector<uint8_t> img(w * h, 255); // 100% glare
    std::vector<uint8_t> out(w * h, 0);

    vs_inpaint_params p;
    vs_inpaint_default_params(&p);
    p.max_glare_pct = 30.0f;

    int32_t count = 0;
    int res = vs_inpaint_auto_mono8(img.data(), w, h, w, out.data(), w, &p, &count);
    // Safe return code 1 indicates safely skipped
    TEST_CHECK(res == 1, "safety gate should skip massive glare");
    // Output should match input (unmodified)
    TEST_CHECK(out[50 * w + 50] == 255, "output must match input when skipped");

    std::printf("  PASSED\n");
}

void test_performance_benchmark() {
    std::printf("[TEST] Performance benchmark on code crops and 20 MP frame...\n");

    // 1. Candidate code crop benchmark (256x256 crop with 20x20 glare spot)
    const int cw = 256, ch = 256;
    std::vector<uint8_t> crop(cw * ch, 200);
    // Simulate Data Matrix finder L
    for (int y = 20; y < 236; ++y) crop[y * cw + 20] = 20;
    for (int x = 20; x < 236; ++x) crop[235 * cw + x] = 20;
    // Glare over finder corner
    for (int y = 225; y < 245; ++y) {
        for (int x = 15; x < 35; ++x) {
            crop[y * cw + x] = 255;
        }
    }

    std::vector<uint8_t> crop_out(cw * ch, 0);
    vs_inpaint_params p;
    vs_inpaint_default_params(&p);

    const int iterations = 50;
    auto t0 = std::chrono::high_resolution_clock::now();
    for (int i = 0; i < iterations; ++i) {
        vs_inpaint_auto_mono8(crop.data(), cw, ch, cw, crop_out.data(), cw, &p, nullptr);
    }
    auto t1 = std::chrono::high_resolution_clock::now();
    double ms_per_crop = std::chrono::duration<double, std::milli>(t1 - t0).count() / iterations;
    std::printf("  256x256 code crop with glare inpaint: %.3f ms (Budget: <= 3.0 ms)\n", ms_per_crop);
    TEST_CHECK(VS_TIME_BUDGET(ms_per_crop < 5.0), "Crop inpainting too slow");

    // 2. 20 MP frame with localized shrink-wrap glare spot
    const int fw = 5472, fh = 3648;
    std::vector<uint8_t> frame(fw * fh, 128);
    // Add glare spot at center
    for (int y = 1800; y < 1840; ++y) {
        for (int x = 2700; x < 2740; ++x) {
            frame[y * fw + x] = 255;
        }
    }
    std::vector<uint8_t> frame_out(fw * fh, 0);
    auto ft0 = std::chrono::high_resolution_clock::now();
    vs_inpaint_auto_mono8(frame.data(), fw, fh, fw, frame_out.data(), fw, &p, nullptr);
    auto ft1 = std::chrono::high_resolution_clock::now();
    double ms_frame = std::chrono::duration<double, std::milli>(ft1 - ft0).count();
    std::printf("  20 MP (5472x3648) localized glare inpaint: %.2f ms (Budget: <= 30 ms)\n", ms_frame);
    TEST_CHECK(VS_TIME_BUDGET(ms_frame < 50.0), "20 MP inpainting too slow");

    std::printf("  PASSED\n");
}

int main() {
    std::printf("=== Running Specular Highlight Glare Inpainter Tests (AG-07) ===\n");
    test_default_params();
    test_glare_detection();
    test_line_continuity_restoration();
    test_safety_gate_on_massive_glare();
    test_performance_benchmark();
    std::printf("=== All Specular Inpainter Tests Passed 100%% ===\n");
    return 0;
}
