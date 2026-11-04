// fuzz_codereader.cpp - the code reader's C API (codereader.h) against bad input: settings with
// broken struct_size, NaN and extreme values; images of odd sizes, strides and contents, some with
// a drawn (and damaged) QR so the decode passes past the finder run; several frames; calibration;
// DPM settings; mutated learned-state blobs; every getter with any index and struct_size.
#include "codereader.h"
#include "fuzz_common.hpp"
#include "qrcodegen.hpp"

#include <algorithm>

namespace {

// Settings: defaults, each field now and then replaced by a nasty value; struct_size now and then
// wrong (an old struct, untagged, odd, too small or anything).
cr_params makeParams(fuzz::Input& in)
{
    cr_params p;
    cr_default_params(&p);
    static const int32_t formats[] = {CR_QR, CR_DATAMATRIX, CR_QR | CR_DATAMATRIX, CR_GS1_128, CR_EAN13, CR_ITF,
                                      CR_PHARMACODE, CR_PHARMACODE | CR_PHARMACODE_REV, CR_DOTCODE, CR_PDF417,
                                      CR_AZTEC, CR_MICRO_QR, CR_ALL_LINEAR, CR_ANY, 0, 0xFFFF, 0x40000};
    p.format = in.chance(80) ? in.pick(formats) : in.nastyInt();
    p.expected_count = in.field(in.range(0, 96));
    // the time budget stays short, so a case is quick under the sanitizers; now and then anything
    p.time_budget_ms = in.chance(5) ? in.nastyInt() : in.range(1, 120);
    if (p.time_budget_ms > 2000) p.time_budget_ms = 2000;
    p.threads = in.chance(90) ? in.range(0, 4) : in.nastyInt();
    p.locator_scale = in.field(4);
    p.max_upscale = in.field(2);
    p.gamma = in.fieldf(0.5f);
    p.locator_density = in.fieldf(0.12f);
    p.full_pass = in.field(1);
    if (in.chance(30)) {
        p.roi_x = in.nastyInt();
        p.roi_y = in.nastyInt();
        p.roi_w = in.nastyInt();
        p.roi_h = in.nastyInt();
    }
    p.locator_mode = in.field(CR_LOCATE_CLASSICAL);
    p.ai_threshold = in.fieldf(0.4f);
    p.enhancer_mode = in.field(CR_ENHANCE_LAST);
    p.grade_quality = in.field(in.range(0, 1));
    p.match_serials = in.field(1);
    p.hint_modules = in.field(0);
    p.hint_code_side_px = in.fieldf(0.f);
    p.hint_symbols_per_code = in.fieldf(0.f);
    p.min_length = in.field(0);
    p.max_length = in.field(0);
    p.check_digit = in.field(CR_CHECK_AS_READ);
    p.pharma_direction = in.field(CR_PHARMA_ACROSS);
    if (in.chance(10)) {
        static const int32_t sizes[] = {0, 4, 25 * 4, 26 * 4, 27 * 4, CR_PARAMS_TAG, CR_PARAMS_TAG | 25 * 4,
                                        CR_PARAMS_TAG | 24 * 4, CR_PARAMS_TAG | 0xFFFF, CR_PARAMS_TAG | 0xFFFC,
                                        CR_PARAMS_TAG | 103, -1};
        p.struct_size = in.chance(70) ? in.pick(sizes) : in.i32();
    }
    return p;
}

// A QR drawn into the image at any size and place (partly off the image too), then damaged, so the
// decode reaches the grid fit, quality grading, serial matching and the diagnosis of misses.
void drawQr(fuzz::Input& in, fuzz::Image& img)
{
    if (img.width < 8 || img.height < 8) return;
    char text[24];
    std::snprintf(text, sizeof text, "%08u%04u", in.u32() % 100000000u, in.u32() % 10000u);
    const auto qr = qrcodegen::QrCode::encodeText(text, qrcodegen::QrCode::Ecc::LOW);
    const int n = qr.getSize();
    const int module = in.range(1, std::max(1, std::min(img.width, img.height) / (n + 2)));
    const int ox = in.range(-module * 4, img.width - 1), oy = in.range(-module * 4, img.height - 1);
    const uint8_t dark = static_cast<uint8_t>(in.range(0, 120)), light = static_cast<uint8_t>(in.range(130, 255));
    const int flips = in.range(0, 40);
    for (int my = -2; my < n + 2; ++my)
        for (int mx = -2; mx < n + 2; ++mx) {
            bool on = qr.getModule(mx, my); // false outside the symbol: quiet zone
            for (int y = 0; y < module; ++y)
                for (int x = 0; x < module; ++x) {
                    const int px = ox + (mx + 2) * module + x, py = oy + (my + 2) * module + y;
                    if (px >= 0 && py >= 0 && px < img.width && py < img.height) img.at(px, py) = on ? dark : light;
                }
        }
    for (int k = 0; k < flips; ++k) { // damage: specks anywhere in the symbol
        const int px = ox + in.range(0, (n + 4) * module), py = oy + in.range(0, (n + 4) * module);
        if (px >= 0 && py >= 0 && px < img.width && py < img.height) img.at(px, py) ^= 0xFF;
    }
}

// Every getter, with good, edge and bad indexes and struct sizes. A NULL out must be refused too.
void readBack(fuzz::Input& in, cr_reader* r)
{
    const int32_t n = cr_code_count(r);
    for (int k = 0; k < 4; ++k) {
        const int32_t i = in.chance(70) ? in.range(-1, n) : in.nastyInt();
        cr_code c{};
        if (cr_get_code(r, i, &c) == 0 && c.text) (void)std::strlen(c.text);
        cr_quality q{};
        cr_get_quality(r, i, &q);
        // a caller's struct may be older (smaller) or newer (larger) than ours: only struct_size bytes are written
        struct { cr_code_ext e; uint8_t tail[64]; } ext{};
        ext.e.struct_size = in.chance(70) ? static_cast<int32_t>(sizeof(cr_code_ext)) : in.range(-8, static_cast<int>(sizeof ext));
        if (cr_get_code_ext(r, i, &ext.e) == 0 && ext.e.struct_size >= static_cast<int32_t>(sizeof(cr_code_ext))) {
            if (ext.e.addon) (void)std::strlen(ext.e.addon);
            for (int s = -1; s <= ext.e.scan_count; ++s) {
                cr_scan sc{};
                sc.struct_size = in.chance(80) ? static_cast<int32_t>(sizeof sc) : in.range(-4, static_cast<int>(sizeof sc));
                cr_get_scan(r, i, s, &sc);
            }
        }
        cr_scan sc{};
        sc.struct_size = static_cast<int32_t>(sizeof sc);
        cr_get_scan(r, i, in.nastyInt(), &sc);
        cr_dpm d{};
        d.struct_size = in.chance(70) ? static_cast<int32_t>(sizeof d) : in.range(-4, static_cast<int>(sizeof d));
        cr_get_dpm(r, i, &d);
        cr_get_code(r, i, nullptr);
        cr_get_quality(r, i, nullptr);
        cr_get_code_ext(r, i, nullptr);
        cr_get_dpm(r, i, nullptr);
    }
    const int32_t regions = cr_region_count(r);
    for (int32_t i = -1; i <= std::min(regions, 64); ++i) {
        cr_region g{};
        cr_get_region(r, i, &g);
    }
    const int32_t misses = cr_miss_count(r);
    for (int32_t i = -1; i <= std::min(misses, 64); ++i) {
        cr_miss m{};
        cr_get_miss(r, i, &m);
        cr_miss_info mi{};
        cr_get_miss_info(r, i, &mi);
    }
    cr_get_miss(r, in.nastyInt(), nullptr);
    cr_stats st{};
    cr_get_stats(r, &st);
    cr_get_stats(r, nullptr);
    cr_stats_ext se{};
    se.struct_size = in.chance(70) ? static_cast<int32_t>(sizeof se) : in.range(-4, static_cast<int>(sizeof se));
    cr_get_stats_ext(r, &se);
    int32_t zr[16], zu[16];
    cr_get_zone_stats(r, zr, zu);
    (void)std::strlen(cr_last_error(r));
}

void onFound(void* user, const cr_region* regions, int32_t count)
{
    auto* sum = static_cast<int64_t*>(user);
    for (int32_t i = 0; i < count; ++i) *sum += regions[i].x + regions[i].w; // touch every region
}

// learned state: the reader's own blob, mutated, or junk; then decoding must still work
void learnedState(fuzz::Input& in, cr_reader* r)
{
    const int32_t need = cr_get_learned(r, nullptr, 0);
    std::string blob;
    if (need > 0) {
        // cap below, at and above the need: a short cap must write nothing
        std::vector<char> buf(static_cast<size_t>(need) + 8, '\x7f');
        const int32_t cap = in.chance(70) ? need : in.range(0, need + 8);
        if (cr_get_learned(r, buf.data(), cap) == need && cap >= need) blob.assign(buf.data());
    }
    static const char* seeds[] = {
        "codereader-learned 1\nsteps 24 zones 4\ncases 5\n",
        "codereader-learned 1\nsteps 24 zones 4\ncases 10\nwins 1 2 3\ntries 4 5 6\nqr 25\ndm 18 40\nslots 4 6\nfullpass 30\n"
        "psf 1.2 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1\nenc 1 0 1 25 2\nzonewins 3 1 1\nzonetries 15 1 1\nzoneread 1 2\nzoneunread 3 4\n",
        "codereader-learned 2\n", "codereader-learned\n", "", "\n\n\n", "codereader-learned 1"};
    std::string text = blob.empty() || in.chance(30) ? std::string(in.pick(seeds)) : blob;
    if (in.chance(80)) text = in.mutate(text);
    cr_set_learned(r, text.c_str());
    if (in.chance(10)) cr_set_learned(r, nullptr);
}

void calibration(fuzz::Input& in, cr_reader* r)
{
    switch (in.byte() % 6) {
    case 0: {
        fuzz::Image w = fuzz::makeImage(in, 640);
        cr_set_flat_field(r, w.data(), w.width, w.height, w.stride);
        break;
    }
    case 1: cr_set_flat_field(r, nullptr, 0, 0, 0); break;
    case 2: cr_set_reflectance(r, in.nastyFloat(), in.nastyFloat(), in.nastyFloat(), in.nastyFloat()); break;
    case 3: cr_set_reflectance(r, in.fieldf(30.f), in.fieldf(200.f), in.fieldf(5.f), in.fieldf(90.f)); break;
    case 4: {
        cr_lens l{in.fieldf(3000.f), in.fieldf(3000.f), in.fieldf(320.f), in.fieldf(240.f), in.fieldf(0.f), in.fieldf(0.f),
                  in.fieldf(0.f), in.fieldf(0.f), in.fieldf(0.f), in.fieldf(0.1f), in.fieldf(-1.f)};
        cr_set_lens(r, in.chance(90) ? &l : nullptr);
        break;
    }
    default: {
        fuzz::Image c = fuzz::makeImage(in, 640);
        float d = 0, l = 0;
        cr_measure_card(r, c.data(), c.width, c.height, c.stride, in.chance(90) ? &d : nullptr, &l);
        break;
    }
    }
}

void dpm(fuzz::Input& in, cr_reader* r)
{
    cr_dpm_params d;
    cr_default_dpm_params(&d);
    d.enabled = in.field(1);
    d.lighting = in.field(CR_DPM_LIGHT_90);
    static const int32_t apertures[] = {0, 50, 80};
    d.aperture = in.chance(80) ? in.pick(apertures) : in.nastyInt();
    d.wavelength_nm = in.field(660);
    d.closing = in.field(in.range(-1, 6));
    d.r_cal = in.fieldf(0.f);
    d.ml_cal = in.fieldf(0.f);
    d.sr_cal = in.fieldf(0.f);
    d.sr_target = in.fieldf(0.f);
    if (in.chance(15)) d.struct_size = in.range(-4, static_cast<int>(sizeof d) + 16);
    // a caller struct larger than ours: the extra bytes must not be read past the caller's object
    struct { cr_dpm_params p; uint8_t tail[16]; } big{};
    big.p = d;
    cr_set_dpm(r, in.chance(95) ? &big.p : nullptr);
}

} // namespace

int fuzzOne(fuzz::Input& in)
{
    // calls on a NULL reader, now and then: every entry point must refuse it
    if (in.chance(5)) {
        cr_destroy(nullptr);
        cr_set_params(nullptr, nullptr);
        cr_decode(nullptr, nullptr, 0, 0, 0);
        cr_decode_frames(nullptr, nullptr, 0, 0, 0, 0);
        cr_load_model(nullptr, nullptr);
        cr_load_enhancer(nullptr, nullptr);
        cr_set_found_callback(nullptr, nullptr, nullptr);
        cr_get_learned(nullptr, nullptr, 0);
        cr_set_learned(nullptr, "x");
        cr_set_dpm(nullptr, nullptr);
        cr_set_flat_field(nullptr, nullptr, 0, 0, 0);
        cr_set_reflectance(nullptr, 0, 0, 0, 0);
        cr_set_lens(nullptr, nullptr);
        cr_measure_card(nullptr, nullptr, 0, 0, 0, nullptr, nullptr);
        cr_default_params(nullptr);
        cr_default_dpm_params(nullptr);
        readBack(in, nullptr);
        cr_lens out{};
        cr_calibrate_chessboard(nullptr, 0, 0, 0, 0, 0, 0, 0.f, &out);
        (void)cr_version();
    }

    cr_params p = makeParams(in);
    cr_reader* r = cr_create(in.chance(3) ? nullptr : &p);
    if (!r) return 0;
    int64_t foundSum = 0;
    if (in.chance(30)) cr_set_found_callback(r, onFound, &foundSum);
    if (in.chance(3)) cr_load_model(r, in.chance(50) ? "/nonexistent/model.onnx" : "");
    if (in.chance(3)) cr_load_enhancer(r, "/dev/null");

    const int steps = in.range(1, 4);
    for (int s = 0; s < steps; ++s) {
        switch (in.byte() % 10) {
        case 0: {
            cr_params q = makeParams(in);
            cr_set_params(r, in.chance(95) ? &q : nullptr);
            break;
        }
        case 1: learnedState(in, r); break;
        case 2: calibration(in, r); break;
        case 3: dpm(in, r); break;
        case 4: { // several frames of one case
            const int frames = in.chance(80) ? in.range(1, 5) : in.nastyInt();
            int32_t w, h, stride;
            fuzz::imageSize(in, w, h, stride, 640);
            if (fuzz::promisedBytes(w, h, stride) > (size_t(256) << 20)) stride = w; // as makeImage
            const size_t bytes = fuzz::promisedBytes(w, h, stride);
            std::vector<fuzz::Image> imgs(static_cast<size_t>(std::clamp(frames, 0, 8)));
            std::vector<const uint8_t*> ptrs;
            for (auto& im : imgs) {
                im.width = w; im.height = h; im.stride = stride;
                if (bytes) {
                    im.buf.resize(bytes);
                    fuzz::fillImage(in, im);
                    if (in.chance(50)) drawQr(in, im);
                }
                ptrs.push_back(in.chance(97) ? im.data() : nullptr);
            }
            if (ptrs.empty()) ptrs.push_back(nullptr);
            cr_decode_frames(r, in.chance(97) ? ptrs.data() : nullptr, frames, w, h, stride);
            readBack(in, r);
            break;
        }
        case 5: { // chessboard calibration on junk
            fuzz::Image im = fuzz::makeImage(in, 320);
            const uint8_t* views[2] = {im.data(), in.chance(50) ? im.data() : nullptr};
            cr_lens out{};
            cr_calibrate_chessboard(views, in.range(0, 2), im.width, im.height, im.stride, in.field(7), in.field(5),
                                    in.fieldf(10.f), in.chance(95) ? &out : nullptr);
            break;
        }
        default: {
            fuzz::Image im = fuzz::makeImage(in, 1024);
            if (im.valid && in.chance(60)) {
                const int codes = in.range(1, 4);
                for (int k = 0; k < codes; ++k) drawQr(in, im);
            }
            fuzz::dumpImage(im, "decode");
            cr_decode(r, im.data(), im.width, im.height, im.stride);
            readBack(in, r);
            break;
        }
        }
    }
    readBack(in, r);
    cr_destroy(r);
    return 0;
}
