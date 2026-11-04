// fuzz_ocr.cpp - the OCR / OCV tool's C API (vs_ocr.h) against bad input: settings with broken
// struct_size, NaN and extreme values; images of odd sizes, strides and contents, some with drawn
// text lines; expected-line patterns and formats from their grammar, broken on purpose; any clock;
// taught fonts and learned print references, saved and mutated; every getter with any index, size
// and buffer capacity. The line recogniser is loaded in some cases (OCR_MODEL_DIR).
#include "vs_ocr.h"
#include "fuzz_common.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>

#ifndef OCR_MODEL_DIR
#define OCR_MODEL_DIR "../ml/models"
#endif

namespace {

vs_ocr_params makeParams(fuzz::Input& in)
{
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    if (in.chance(30)) {
        p.roi_x = in.nastyInt();
        p.roi_y = in.nastyInt();
        p.roi_w = in.nastyInt();
        p.roi_h = in.nastyInt();
    }
    p.print = in.field(in.range(0, 3));
    p.polarity = in.field(in.range(0, 2));
    p.char_height_px = in.field(in.chance(50) ? 0 : in.range(6, 60));
    p.max_lines = in.field(8);
    p.min_quality = in.fieldf(60.f);
    p.max_skew_deg = in.fieldf(15.f);
    p.time_budget_ms = in.chance(5) ? in.nastyInt() : in.range(1, 80);
    if (p.time_budget_ms > 2000) p.time_budget_ms = 2000;
    p.reader = in.field(in.range(0, 1));
    p.allow_shift_px = in.fieldf(0.f);
    p.dot_size_px = in.fieldf(0.f);
    p.join_strength = in.fieldf(0.f);
    if (in.chance(10)) p.struct_size = in.chance(50) ? in.range(-4, static_cast<int>(sizeof p) + 8) : in.i32();
    if (std::getenv("FUZZ_DUMP"))
        std::fprintf(stderr, "params: size %d roi %d %d %d %d print %d polarity %d height %d lines %d minq %g skew %g budget %d reader %d shift %g dot %g join %g\n",
                     p.struct_size, p.roi_x, p.roi_y, p.roi_w, p.roi_h, p.print, p.polarity, p.char_height_px, p.max_lines,
                     p.min_quality, p.max_skew_deg, p.time_budget_ms, p.reader, p.allow_shift_px, p.dot_size_px, p.join_strength);
    return p;
}

// text the tool may read: digits, capitals, a date and price, as on a pack
std::string printedText(fuzz::Input& in)
{
    static const char* texts[] = {"BATCH A1234", "MFD 10/26", "EXP 09/28", "MRP 125.00", "0123456789", "LOT 77Q", "X",
                                  "88:88", "ABCDEFGHIJKLMNOPQRSTUVWXYZ"};
    return in.chance(80) ? in.pick(texts) : in.text(20);
}

// one to four lines of text drawn into the image (solid or dotted, either polarity, turned a little)
void drawText(fuzz::Input& in, fuzz::Image& img)
{
    if (img.width < 16 || img.height < 8) return;
    cv::Mat m(img.height, img.width, CV_8U, img.buf.data(), static_cast<size_t>(img.stride));
    if (std::getenv("FUZZ_DUMP")) std::fprintf(stderr, "drawText\n");
    const bool dark = in.chance(70);
    if (in.chance(80)) m.setTo(dark ? 220 : 30);
    const int lines = in.range(1, 4);
    const double scale = 0.3 + in.range(0, 30) / 10.0;
    for (int k = 0; k < lines; ++k) {
        const std::string s = printedText(in);
        std::string ascii;
        for (char c : s) ascii.push_back(c >= 32 && c < 127 ? c : '?');
        const cv::Point at(in.range(-10, img.width / 2), in.range(0, img.height));
        cv::putText(m, ascii, at, cv::FONT_HERSHEY_SIMPLEX, scale, cv::Scalar(dark ? 20 : 230), in.range(1, 3));
    }
    if (in.chance(30)) { // dot print: holes through the strokes
        for (int y = 0; y < m.rows; y += 3)
            for (int x = 0; x < m.cols; x += 3) m.at<uint8_t>(y, x) = dark ? 220 : 30;
    }
    if (in.chance(20)) {
        const double a = in.range(-20, 20) * CV_PI / 180.0, c = std::cos(a), s = std::sin(a);
        const double cx = m.cols / 2.0, cy = m.rows / 2.0;
        const cv::Matx23d rot(c, s, cx - c * cx - s * cy, -s, c, cy + s * cx - c * cy);
        cv::Mat out;
        cv::warpAffine(m, out, rot, m.size(), cv::INTER_LINEAR, cv::BORDER_REPLICATE);
        out.copyTo(m);
    }
}

// an expected-line pattern from its grammar: plain text, ?, escapes, {TIME:..} and {DATE:..} fields with
// offsets and tolerances (also huge and broken ones), or bytes
std::string pattern(fuzz::Input& in)
{
    static const char* parts[] = {"BATCH ", "?", "\\?", "\\{", "\\\\", "{TIME:HH:mm+-5}", "{TIME:HH:mm}", "{DATE:DD/MM/YY}",
                                  "{DATE:MM/YY+730d}", "{DATE:MMM YYYY-30d+-2}", "{DATE:DD.MM.YYYY+1d\xC2\xB1" "1}",
                                  "{DATE:}", "{TIME}", "{BATCH}", "{", "}", "{DATE:YY+99999999999999999999d}",
                                  "{TIME:HH+-99999999999}", "{DATE:DD+-2147483647}", "{DATE:YYYY+9223372036854775807d}",
                                  "{DATE:YYYY-9223372036854775807d+-1}", "{TIME:ss+-1000}", "\xE2\x82\xB9 0.23", "\xFF\xFE",
                                  "\xC2", "\xF4\x90\x80\x80", " ", "0", "MRP"};
    std::string s;
    const int n = in.range(0, 6);
    for (int k = 0; k < n; ++k) s += in.chance(85) ? in.pick(parts) : in.text(12);
    return in.chance(20) ? in.mutate(s) : s;
}

// a format from its grammar: {D:5} {D:4-6} {A:..} {X:..} {?:..} {|A|B} and escapes, broken now and then
std::string format(fuzz::Input& in)
{
    static const char* parts[] = {"{D:5}", "{D:4-6}", "{D}", "{A:3}", "{X:2-8}", "{?:1-64}", "{|JAN|FEB|MAR}", "{|}",
                                  "{|A||B}", "\\{", "\\|", "\\\\", " ", "BATCH", "/", "{D:0}", "{D:65}", "{D:6-4}",
                                  "{D:99999999999}", "{D:-1}", "{A:1-}", "{Q:3}", "{D:5", "}", "{", "{?:64}{?:64}{?:64}",
                                  "\xE2\x82\xB9", "\xFF", "{|\xC2}"};
    std::string s;
    const int n = in.range(0, 6);
    for (int k = 0; k < n; ++k) s += in.chance(85) ? in.pick(parts) : in.text(12);
    return in.chance(20) ? in.mutate(s) : s;
}

void readBack(fuzz::Input& in, vs_ocr* t)
{
    const int32_t lines = vs_ocr_line_count(t);
    for (int32_t i = -1; i <= std::min(lines, 40); ++i) {
        vs_ocr_line l{};
        if (vs_ocr_get_line(t, i, &l) == 0) {
            if (l.text) (void)std::strlen(l.text);
            if (l.expected) (void)std::strlen(l.expected);
        }
        struct { vs_ocr_line_ext e; uint8_t tail[32]; } ext{};
        static const int32_t sizes[] = {static_cast<int32_t>(sizeof(vs_ocr_line_ext)),
                                        static_cast<int32_t>(offsetof(vs_ocr_line_ext, cut_short)),
                                        static_cast<int32_t>(offsetof(vs_ocr_line_ext, margin)), 0, 4, -1, 1 << 20};
        ext.e.struct_size = in.pick(sizes);
        vs_ocr_get_line_ext(t, i, &ext.e);
        // line image: cap below, at and above the need; a short cap must write nothing
        int32_t w = 0;
        const int32_t need = vs_ocr_get_line_image(t, i, nullptr, 0, &w);
        if (need > 0 && need < (64 << 20)) {
            const int32_t cap = in.chance(70) ? need : in.range(0, need + 4);
            std::vector<uint8_t> buf(static_cast<size_t>(std::max(cap, 1)));
            vs_ocr_get_line_image(t, i, buf.data(), cap, in.chance(80) ? &w : nullptr);
        }
    }
    const int32_t chars = vs_ocr_char_count(t);
    for (int32_t i = -1; i <= std::min(chars, 200); ++i) {
        vs_ocr_char c{};
        vs_ocr_get_char(t, i, &c);
        struct { vs_ocr_char_quality q; uint8_t tail[32]; } cq{};
        static const int32_t sizes[] = {static_cast<int32_t>(sizeof(vs_ocr_char_quality)),
                                        static_cast<int32_t>(offsetof(vs_ocr_char_quality, margin)), 0, 4, -1, 1 << 20};
        cq.q.struct_size = in.pick(sizes);
        vs_ocr_get_char_quality(t, i, &cq.q);
    }
    vs_ocr_get_char(t, in.nastyInt(), nullptr);
    vs_ocr_get_line(t, in.nastyInt(), nullptr);
    vs_ocr_get_line_ext(t, 0, nullptr);
    vs_ocr_get_char_quality(t, 0, nullptr);
    vs_ocr_get_line_image(t, in.nastyInt(), nullptr, in.nastyInt(), nullptr);
    vs_ocr_stats st{};
    vs_ocr_get_stats(t, &st);
    vs_ocr_get_stats(t, nullptr);
    (void)std::strlen(vs_ocr_last_error(t));
}

// a saved text (font or print reference), mutated, loaded back
template <typename Get, typename Set>
void blob(fuzz::Input& in, vs_ocr* t, Get get, Set set, const char* const* seeds, size_t nSeeds)
{
    std::string saved;
    const int32_t need = get(t, nullptr, 0);
    if (need > 0 && need < (16 << 20)) {
        std::vector<char> buf(static_cast<size_t>(need) + 4, '\x7f');
        const int32_t cap = in.chance(70) ? need : in.range(0, need + 4);
        if (get(t, buf.data(), cap) == need && cap >= need) saved.assign(buf.data());
    }
    std::string text = saved.empty() || in.chance(30) ? std::string(seeds[in.byte() % nSeeds]) : saved;
    if (in.chance(80)) text = in.mutate(text);
    set(t, in.chance(97) ? text.c_str() : nullptr);
}

} // namespace

int fuzzOne(fuzz::Input& in)
{
    if (in.chance(5)) {
        vs_ocr_destroy(nullptr);
        vs_ocr_set_params(nullptr, nullptr);
        vs_ocr_load_model(nullptr, nullptr, nullptr);
        vs_ocr_set_expected(nullptr, 0, nullptr);
        vs_ocr_clear_expected(nullptr);
        vs_ocr_set_clock(nullptr, 0);
        vs_ocr_set_format(nullptr, 0, "x");
        vs_ocr_clear_formats(nullptr);
        vs_ocr_teach_font(nullptr, nullptr, 0, 0, 0, 0, nullptr);
        vs_ocr_teach_char(nullptr, nullptr, 0, 0, 0, 0, 0, nullptr);
        vs_ocr_clear_font(nullptr);
        vs_ocr_get_font(nullptr, nullptr, 0);
        vs_ocr_set_font(nullptr, nullptr);
        vs_ocr_learn_print(nullptr);
        vs_ocr_clear_print(nullptr);
        vs_ocr_get_print(nullptr, nullptr, 0);
        vs_ocr_set_print(nullptr, nullptr);
        vs_ocr_measure_dots(nullptr, nullptr, 0, 0, 0, nullptr);
        vs_ocr_run(nullptr, nullptr, 0, 0, 0);
        vs_ocr_default_params(nullptr);
        (void)vs_ocr_font_glyphs(nullptr);
        (void)vs_ocr_print_parts(nullptr);
        readBack(in, nullptr);
        (void)vs_ocr_version();
    }

    vs_ocr_params p = makeParams(in);
    vs_ocr* t = vs_ocr_create(in.chance(3) ? nullptr : &p);
    if (!t) return 0;
    if (in.chance(15))
        vs_ocr_load_model(t, OCR_MODEL_DIR "/ocrnet.onnx", OCR_MODEL_DIR "/ocr_charset.txt");
    else if (in.chance(3))
        vs_ocr_load_model(t, "/nonexistent.onnx", in.chance(50) ? "/dev/null" : "");

    static const char* fontSeeds[] = {"", "VSFONT1\n", "VSFONT1\n65 12 00ff\n", "VSFONT1\n65 12 00 3 3 ff00ff00ff00ff00ff\n"};
    static const char* printSeeds[] = {"", "VSPRINT1 10 4\nI 65 10 500 25000\nC 65 10 900 81000\nP 65_66 9 90 900\nX 0 10 50 250\nY 0 10 50 250\n",
                                       "VSPRINT1 2147483647 4\n", "VSPRINT1 3 4\nI 65 nan 1 1\n"};
    const int steps = in.range(1, 6);
    for (int s = 0; s < steps; ++s) {
        switch (in.byte() % 12) {
        case 0: {
            vs_ocr_params q = makeParams(in);
            vs_ocr_set_params(t, in.chance(95) ? &q : nullptr);
            break;
        }
        case 1: {
            const std::string pat = pattern(in);
            vs_ocr_set_expected(t, in.chance(85) ? in.range(0, 3) : in.nastyInt(), in.chance(95) ? pat.c_str() : nullptr);
            break;
        }
        case 2: {
            const std::string f = format(in);
            vs_ocr_set_format(t, in.chance(85) ? in.range(0, 3) : in.nastyInt(), in.chance(95) ? f.c_str() : nullptr);
            break;
        }
        case 3: {
            // any clock: the pattern's dates and times are formatted from it
            static const int64_t clocks[] = {0, 1, -1, 1793000000, std::numeric_limits<int64_t>::max(),
                                             std::numeric_limits<int64_t>::min(), 253402300799, -62135596800,
                                             int64_t(1) << 55, -(int64_t(1) << 55)};
            vs_ocr_set_clock(t, in.chance(70) ? in.pick(clocks) : static_cast<int64_t>(in.u32()) << in.range(0, 32));
            break;
        }
        case 4: case 5: {
            fuzz::Image im = fuzz::makeImage(in, 640);
            if (im.valid && in.chance(70)) drawText(in, im);
            const std::string text = printedText(in);
            fuzz::dumpImage(im, "teach");
            vs_ocr_teach_font(t, im.data(), im.width, im.height, im.stride, in.chance(85) ? in.range(0, 2) : in.nastyInt(),
                              in.chance(95) ? text.c_str() : nullptr);
            if (in.chance(50)) {
                const std::string ch = in.chance(80) ? std::string(1, static_cast<char>('A' + in.range(0, 25))) : in.text(5);
                vs_ocr_teach_char(t, im.data(), im.width, im.height, im.stride, in.fieldf(static_cast<float>(in.range(0, 640))),
                                  in.fieldf(static_cast<float>(in.range(0, 480))), in.chance(95) ? ch.c_str() : nullptr);
            }
            break;
        }
        case 6: blob(in, t, vs_ocr_get_font, vs_ocr_set_font, fontSeeds, 4); break;
        case 7: blob(in, t, vs_ocr_get_print, vs_ocr_set_print, printSeeds, 4); break;
        case 8: {
            fuzz::Image im = fuzz::makeImage(in, 640);
            if (im.valid && in.chance(70)) drawText(in, im);
            float pitch = 0;
            vs_ocr_measure_dots(t, im.data(), im.width, im.height, im.stride, in.chance(80) ? &pitch : nullptr);
            break;
        }
        case 9:
            vs_ocr_learn_print(t);
            if (in.chance(10)) vs_ocr_clear_print(t);
            if (in.chance(10)) vs_ocr_clear_font(t);
            if (in.chance(10)) vs_ocr_clear_expected(t);
            if (in.chance(10)) vs_ocr_clear_formats(t);
            break;
        default: {
            fuzz::Image im = fuzz::makeImage(in, 1024);
            if (im.valid && in.chance(70)) drawText(in, im);
            fuzz::dumpImage(im, "run");
            vs_ocr_run(t, im.data(), im.width, im.height, im.stride);
            readBack(in, t);
            if (in.chance(30)) vs_ocr_learn_print(t);
            break;
        }
        }
    }
    readBack(in, t);
    vs_ocr_destroy(t);
    return 0;
}
