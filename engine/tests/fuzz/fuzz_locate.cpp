// fuzz_locate.cpp - the Locate tool's C API (vs_locate.h) against bad input: settings with broken
// struct_size, NaN and extreme values; teach regions anywhere (negative, overflowing, flat); stored
// patterns of odd sizes, strides and origins; runs on odd images, some holding the pattern moved
// and turned; the pattern read back with any capacity.
#include "vs_locate.h"
#include "fuzz_common.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>

namespace {

vs_loc_params makeParams(fuzz::Input& in)
{
    vs_loc_params p;
    vs_loc_default_params(&p);
    if (in.chance(30)) {
        p.search_x = in.nastyInt();
        p.search_y = in.nastyInt();
        p.search_w = in.nastyInt();
        p.search_h = in.nastyInt();
    }
    p.max_angle_deg = in.fieldf(static_cast<float>(in.range(0, 180)));
    p.min_score = in.fieldf(0.6f);
    p.time_budget_ms = in.chance(5) ? in.nastyInt() : in.range(1, 80);
    if (p.time_budget_ms > 2000) p.time_budget_ms = 2000;
    if (in.chance(10)) p.struct_size = in.chance(50) ? in.range(-4, static_cast<int>(sizeof p) + 8) : in.i32();
    return p;
}

// a patch with detail (rectangles and a cross) drawn into the image, so teach and run get past "flat"
void drawPatch(fuzz::Input& in, fuzz::Image& img)
{
    if (img.width < 20 || img.height < 20) return;
    cv::Mat m(img.height, img.width, CV_8U, img.buf.data(), static_cast<size_t>(img.stride));
    const int side = in.range(16, std::max(16, std::min(img.width, img.height) / 2));
    const cv::Point c(in.range(0, img.width - 1), in.range(0, img.height - 1));
    cv::rectangle(m, cv::Rect(c.x - side / 2, c.y - side / 2, side, side), cv::Scalar(30), cv::FILLED);
    cv::rectangle(m, cv::Rect(c.x - side / 4, c.y - side / 4, side / 2, side / 3), cv::Scalar(220), cv::FILLED);
    cv::line(m, cv::Point(c.x - side / 2, c.y), cv::Point(c.x + side / 2, c.y + side / 5), cv::Scalar(200), 2);
}

} // namespace

int fuzzOne(fuzz::Input& in)
{
    if (in.chance(5)) {
        vs_loc_destroy(nullptr);
        vs_loc_set_params(nullptr, nullptr);
        vs_loc_teach(nullptr, nullptr, 0, 0, 0, 0, 0, 0, 0);
        vs_loc_set_pattern(nullptr, nullptr, 0, 0, 0, 0, 0);
        vs_loc_get_pattern(nullptr, nullptr, 0, nullptr, nullptr, nullptr, nullptr);
        vs_loc_run(nullptr, nullptr, 0, 0, 0);
        vs_loc_get_result(nullptr, nullptr);
        vs_loc_default_params(nullptr);
        vs_loc_result r{};
        vs_loc_get_result(nullptr, &r);
        (void)std::strlen(vs_loc_last_error(nullptr));
    }

    vs_loc_params p = makeParams(in);
    vs_loc* t = vs_loc_create(in.chance(3) ? nullptr : &p);
    if (!t) return 0;
    const int steps = in.range(1, 5);
    for (int s = 0; s < steps; ++s) {
        switch (in.byte() % 6) {
        case 0: {
            vs_loc_params q = makeParams(in);
            vs_loc_set_params(t, in.chance(95) ? &q : nullptr);
            break;
        }
        case 1: { // teach on a region anywhere
            fuzz::Image im = fuzz::makeImage(in, 640);
            if (im.valid && in.chance(70)) drawPatch(in, im);
            int32_t x, y, w, h;
            if (in.chance(60)) {
                x = in.range(0, std::max(0, im.width - 1));
                y = in.range(0, std::max(0, im.height - 1));
                w = in.range(1, std::max(1, im.width));
                h = in.range(1, std::max(1, im.height));
            } else {
                x = in.nastyInt(); y = in.nastyInt(); w = in.nastyInt(); h = in.nastyInt();
            }
            vs_loc_teach(t, im.data(), im.width, im.height, im.stride, x, y, w, h);
            break;
        }
        case 2: { // a stored pattern of any size, stride and origin
            fuzz::Image pat = fuzz::makeImage(in, 300);
            if (pat.valid && in.chance(70)) drawPatch(in, pat);
            vs_loc_set_pattern(t, pat.data(), pat.width, pat.height, pat.stride, in.fieldf(100.f), in.fieldf(100.f));
            break;
        }
        case 3: { // read the pattern back: cap below, at and above the need; a short cap must write nothing
            int32_t w = 0, h = 0;
            float ox = 0, oy = 0;
            const int32_t need = vs_loc_get_pattern(t, nullptr, 0, &w, &h, &ox, &oy);
            if (need > 0 && need < (64 << 20)) {
                const int32_t cap = in.chance(70) ? need : in.range(0, need + 4);
                std::vector<uint8_t> buf(static_cast<size_t>(std::max(cap, 1)));
                vs_loc_get_pattern(t, buf.data(), cap, in.chance(80) ? &w : nullptr, &h, nullptr, &oy);
            }
            vs_loc_get_pattern(t, nullptr, in.nastyInt(), nullptr, nullptr, nullptr, nullptr);
            break;
        }
        default: {
            fuzz::Image im = fuzz::makeImage(in, 1024);
            if (im.valid && in.chance(70)) drawPatch(in, im);
            vs_loc_run(t, im.data(), im.width, im.height, im.stride);
            vs_loc_result r{};
            vs_loc_get_result(t, &r);
            vs_loc_get_result(t, nullptr);
            (void)std::strlen(vs_loc_last_error(t));
            break;
        }
        }
    }
    vs_loc_destroy(t);
    return 0;
}
