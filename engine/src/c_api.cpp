// c_api.cpp - the plain C API in codereader.h, wrapping the C++ pipeline.

#include "codereader.h"
#include "reader.hpp"
#include "pool.hpp"
#include "integrity.hpp"

#include <opencv2/core/utility.hpp>

#include <algorithm>
#include <exception>
#include <new>
#include <string>
#include <cmath>
#include <cstdlib>
#include <cstddef>
#include <cstring>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

struct cr_reader {
    cr_params params;
    cv::dnn::Net net;
    bool hasModel = false;
    std::vector<cv::dnn::Net> enhancers; // one per decode thread
    // a persistent worker pool for the parallel passes (CR_POOL=1): no gain measured on macOS, where
    // threads are cheap to create; kept for an A/B on the Windows line PC, where they are not
    std::unique_ptr<cr::WorkerPool> pool;
    cr::StepStats steps;                 // learned order of image fixes for this reader
    cr::Calibration calib;               // the grades' optics (flat field, reflectance, lens)
    cr::Result result;
    cr::DpmSettings dpm;                 // ISO/IEC TR 29158 grading (cr_set_dpm)
    std::vector<cr::DpmQuality> dpmResult; // one per code when DPM grading is on
    std::string error;
    cr_found_fn onFound = nullptr;
    void* onFoundUser = nullptr;
};

namespace {

// The latched anti-tamper state turned into a cr_decode return and a clear error, or 0 when clean.
// Run once per decode: cheap latched-state read every call, the full check on the cadence. On a
// positive, latched detection the result is cleared and the case is NG (line safety, section 12).
int32_t tamperGate(cr_reader* r)
{
    if (cr::detail::autoChecks() && cr::integrityDueThisDecode()) cr::runIntegrityChecks();
    const cr::TamperReason t = cr::tamperState();
    if (t == cr::TamperReason::None) return 0;
    r->error = (t == cr::TamperReason::Debugger)
                   ? "tampering detected: a debugger or instrumentation is attached (NG)"
                   : "tampering detected: the engine code was patched in memory (NG)";
    r->result = cr::Result{};
    r->dpmResult.clear();
    return CR_DECODE_TAMPER;
}

// why each miss did not read: after decoding, so the decode time is unchanged; a small cap
constexpr double kDiagnoseMs = 20.0;

void diagnose(cr_reader* r, const cv::Mat& gray)
{
    if (r->result.misses.empty() && !std::getenv("CR_DIAG_ALL")) return; // CR_DIAG_ALL: calibration dump of every image
    cr::diagnoseMisses(gray, r->result, cr::missContext(r->result, r->params, &r->steps), kDiagnoseMs);
    for (size_t i = 0; i < r->result.missInfo.size() && i < r->result.missSlot.size(); ++i) {
        r->result.missInfo[i].slot_row = r->result.missSlot[i].x;
        r->result.missInfo[i].slot_col = r->result.missSlot[i].y;
    }
}

// DPM grades of the Data Matrix and QR codes read (cr_set_dpm), on the image as the camera gave it
void gradeDpm(cr_reader* r, const cv::Mat& gray)
{
    r->dpmResult.clear();
    if (!r->dpm.enabled) return;
    r->dpmResult.reserve(r->result.codes.size());
    for (const auto& c : r->result.codes) r->dpmResult.push_back(cr::gradeDpm(gray, c, r->dpm, &r->calib));
}

// The size of cr_params when struct_size came in; a caller's struct is never smaller.
constexpr uint32_t kParamsSizeV1 = 25 * 4;

// The caller's settings over the defaults: only the bytes its struct_size covers. False when p is
// not a tagged cr_params (a struct from before struct_size, or not set by cr_default_params).
bool readParams(const cr_params* p, cr_params& out)
{
    cr_default_params(&out);
    if (!p) return true;
    const uint32_t tagged = static_cast<uint32_t>(p->struct_size);
    const uint32_t size = tagged & 0xFFFFu;
    if ((tagged & 0xFFFF0000u) != static_cast<uint32_t>(CR_PARAMS_TAG) || size < kParamsSizeV1 || size % 4 != 0) return false;
    std::memcpy(&out, p, std::min<size_t>(size, sizeof(cr_params)));
    out.struct_size = CR_PARAMS_SIZE;
    return true;
}

// A grown output struct: only the bytes the caller's struct_size covers (it is at least 4).
template <typename T>
void writeSized(T* out, const T& value)
{
    std::memcpy(out, &value, std::min(static_cast<size_t>(out->struct_size), sizeof(T)));
}

constexpr int32_t kMaxThreads = 64;

cr_params sanitize(cr_params p)
{
    if (p.time_budget_ms <= 0) p.time_budget_ms = 200;
    // 0 or negative = all cores; more than kMaxThreads is a wrong setting (a thread per pass each)
    p.threads = std::clamp(p.threads, 0, kMaxThreads);
    p.locator_scale = std::clamp(p.locator_scale, 1, 8);
    p.max_upscale = std::clamp(p.max_upscale, 1, 3);
    // NaN, which std::clamp passes through, takes the default
    p.gamma = std::isnan(p.gamma) ? 0.5f : std::clamp(p.gamma, 0.3f, 1.5f);
    p.locator_density = std::isnan(p.locator_density) ? 0.12f : std::clamp(p.locator_density, 0.02f, 0.6f);
    p.expected_count = std::clamp(p.expected_count, 0, 4096); // a case holds at most a few hundred codes
    // 0 = the default type; unknown bits dropped, and a job with only unknown bits reads nothing (NG)
    if (p.format == 0) p.format = CR_QR;
    // an older caller's "any": 0xFFFF (before Pharmacode and DotCode) and 0x3FFFF (before rMQR)
    else if (p.format == 0xFFFF || p.format == 0x3FFFF) p.format = CR_ANY;
    else if (p.format != CR_ANY)
        p.format &= CR_QR | CR_DATAMATRIX | CR_ALL_LINEAR | CR_MICRO_QR | CR_PDF417 | CR_AZTEC | CR_PHARMACODE |
                    CR_PHARMACODE_REV | CR_DOTCODE | CR_RMQR;
    p.min_length = std::clamp(p.min_length, 0, 4096);
    p.max_length = std::clamp(p.max_length, 0, 4096);
    if (p.check_digit < CR_CHECK_AS_READ || p.check_digit > CR_CHECK_STRIP) p.check_digit = CR_CHECK_AS_READ;
    // an unknown direction reads no Pharmacode (NG): another direction would read another value
    if (p.pharma_direction != CR_PHARMA_ACROSS && p.pharma_direction != CR_PHARMA_DOWN) p.pharma_direction = -1;
    if (p.locator_mode < CR_LOCATE_AUTO || p.locator_mode > CR_LOCATE_AI_CLASSICAL) p.locator_mode = CR_LOCATE_AUTO;
    p.ai_threshold = !(p.ai_threshold > 0.f) ? 0.4f : std::clamp(p.ai_threshold, 0.05f, 0.95f);
    if (p.enhancer_mode < CR_ENHANCE_LAST || p.enhancer_mode > CR_ENHANCE_OFF) p.enhancer_mode = CR_ENHANCE_LAST;
    p.match_serials = p.match_serials ? 1 : 0;
    p.hint_modules = p.hint_modules >= 10 && p.hint_modules <= 177 ? p.hint_modules : 0;
    p.hint_code_side_px = std::isfinite(p.hint_code_side_px) && p.hint_code_side_px > 0.f && p.hint_code_side_px < 1e5f ? p.hint_code_side_px : 0.f;
    p.hint_symbols_per_code = std::isnan(p.hint_symbols_per_code) ? 0.f : std::clamp(p.hint_symbols_per_code, 0.f, 8.f);
    return p;
}

} // namespace

extern "C" {

void cr_default_params(cr_params* p)
{
    if (!p) return;
    *p = cr_params{};
    p->struct_size = CR_PARAMS_SIZE;
    p->format = CR_QR;
    p->expected_count = 0;
    p->time_budget_ms = 200;
    p->threads = 0;
    p->locator_scale = 4;
    p->max_upscale = 2;
    p->gamma = 0.5f;
    p->locator_density = 0.12f;
    p->full_pass = 1;
    p->locator_mode = CR_LOCATE_AUTO;
    p->ai_threshold = 0.4f;
    p->enhancer_mode = CR_ENHANCE_LAST;
    p->match_serials = 1;
    p->hint_modules = 0;
    p->hint_code_side_px = 0.f;
    p->hint_symbols_per_code = 0.f;
    p->min_length = 0;
    p->max_length = 0;
    p->check_digit = CR_CHECK_AS_READ;
    p->pharma_direction = CR_PHARMA_ACROSS;
}

static_assert(sizeof(cr_params) == 26 * 4 && offsetof(cr_params, struct_size) == 0, "cr_params changed: update the .NET Native.Params and ml/engine.py mirrors");

cr_reader* cr_create(const cr_params* p)
{
    // OpenCV's own thread count (diagnostic, CR_CV_THREADS=n): the decode already runs its passes
    // on every core, so OpenCV fanning out inside each worker can oversubscribe; 1 makes every
    // OpenCV call single-threaded, including the locator's forward pass. Applied once, at the first
    // reader: cv::setNumThreads rebuilds OpenCV's TBB arena, and a reader created while another
    // camera decodes must never rebuild it under that decode (the PR #148 crash)
    static std::once_flag cvThreads;
    std::call_once(cvThreads, [] {
        if (const char* cvt = std::getenv("CR_CV_THREADS")) cv::setNumThreads(std::max(1, std::atoi(cvt)));
    });
    cr_params given;
    if (!readParams(p, given)) return nullptr;
    auto* r = new (std::nothrow) cr_reader();
    if (!r) return nullptr;
    try {
        if (std::getenv("CR_POOL")) r->pool = std::make_unique<cr::WorkerPool>(static_cast<int>(std::max(1u, std::thread::hardware_concurrency())));
    } catch (const std::exception&) {
        delete r;
        return nullptr;
    }
    r->params = sanitize(given);
    // Record the baseline of the critical code region once a live reader exists, so later decodes
    // can tell if that code is patched in memory (anti-tamper, requirements section 12).
    cr::integrityBaseline();
    return r;
}

void cr_destroy(cr_reader* r) { delete r; }

int32_t cr_set_params(cr_reader* r, const cr_params* p)
{
    if (!r || !p) return -1;
    cr_params given;
    if (!readParams(p, given)) {
        r->error = "cr_params.struct_size is not set: call cr_default_params first";
        return -1;
    }
    r->params = sanitize(given);
    return 0;
}

int32_t cr_load_model(cr_reader* r, const char* path)
{
    if (!r || !path) return -1;
    try {
        r->net = cv::dnn::readNetFromONNX(path);
        r->hasModel = !r->net.empty();
        if (!r->hasModel) {
            r->error = "model is empty";
            return -1;
        }
        r->error.clear();
        return 0;
    } catch (const std::exception& e) {
        r->error = e.what();
    }
    r->hasModel = false;
    return -1;
}

int32_t cr_load_enhancer(cr_reader* r, const char* path)
{
    if (!r || !path) return -1;
    try {
        const unsigned n = std::max(1u, std::thread::hardware_concurrency());
        std::vector<cv::dnn::Net> nets;
        for (unsigned i = 0; i < n; ++i) {
            nets.push_back(cv::dnn::readNetFromONNX(path));
            nets.back().enableWinograd(false);
        }
        if (nets.front().empty()) {
            r->error = "enhancer model is empty";
            return -1;
        }
        r->enhancers = std::move(nets);
        r->error.clear();
        return 0;
    } catch (const std::exception& e) {
        r->error = e.what();
    }
    r->enhancers.clear();
    return -1;
}

int32_t cr_load_model_buffer(cr_reader* r, const uint8_t* data, size_t size)
{
    if (!r || !data || size == 0) {
        if (r) r->error = "empty model buffer";
        if (r) r->hasModel = false;
        return -1;
    }
    try {
        r->net = cv::dnn::readNetFromONNX(reinterpret_cast<const char*>(data), size);
        r->hasModel = !r->net.empty();
        if (!r->hasModel) {
            r->error = "model is empty";
            return -1;
        }
        r->error.clear();
        return 0;
    } catch (const std::exception& e) {
        r->error = e.what();
    }
    r->hasModel = false;
    return -1;
}

int32_t cr_load_enhancer_buffer(cr_reader* r, const uint8_t* data, size_t size)
{
    if (!r || !data || size == 0) {
        if (r) r->error = "empty enhancer buffer";
        return -1;
    }
    try {
        const unsigned n = std::max(1u, std::thread::hardware_concurrency());
        std::vector<cv::dnn::Net> nets;
        for (unsigned i = 0; i < n; ++i) {
            nets.push_back(cv::dnn::readNetFromONNX(reinterpret_cast<const char*>(data), size));
            nets.back().enableWinograd(false);
        }
        if (nets.front().empty()) {
            r->error = "enhancer model is empty";
            return -1;
        }
        r->enhancers = std::move(nets);
        r->error.clear();
        return 0;
    } catch (const std::exception& e) {
        r->error = e.what();
    }
    r->enhancers.clear();
    return -1;
}

void cr_set_found_callback(cr_reader* r, cr_found_fn fn, void* user)
{
    if (!r) return;
    r->onFound = fn;
    r->onFoundUser = user;
}

void cr_get_zone_stats(const cr_reader* r, int32_t read[16], int32_t unread[16])
{
    for (int z = 0; z < 16; ++z) {
        read[z] = r ? static_cast<int32_t>(r->steps.zoneRead[static_cast<size_t>(z)].load()) : 0;
        unread[z] = r ? static_cast<int32_t>(r->steps.zoneUnread[static_cast<size_t>(z)].load()) : 0;
    }
}

int32_t cr_decode(cr_reader* r, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride)
{
    if (!r) return -1;
    if (!pixels || width <= 0 || height <= 0 || stride < width) {
        r->error = "bad image arguments";
        return -1;
    }
    if (const int32_t t = tamperGate(r)) return t;
    try {
        const cv::Mat gray(height, width, CV_8UC1, const_cast<uint8_t*>(pixels), static_cast<size_t>(stride));
        cr::Models models;
        if (r->onFound) {
            models.onFound = [r](const std::vector<cr::FoundRegion>& all) {
                std::vector<cr_region> out;
                out.reserve(all.size());
                for (const auto& g : all) out.push_back({g.box.x, g.box.y, g.box.width, g.box.height, g.fromAI ? 1 : 0, 0});
                r->onFound(r->onFoundUser, out.data(), static_cast<int32_t>(out.size()));
            };
        }
        models.locator = r->hasModel ? &r->net : nullptr;
        models.enhancers = &r->enhancers;
        models.pool = r->pool.get();
        models.calibration = &r->calib;
        r->result = cr::decode(gray, r->params, models, &r->steps);
        diagnose(r, gray);
        gradeDpm(r, gray);
        r->error.clear();
        return static_cast<int32_t>(r->result.codes.size());
    } catch (const std::exception& e) {
        r->error = e.what();
    } catch (...) {
        r->error = "unknown error";
    }
    r->result = cr::Result{};
    r->dpmResult.clear();
    return -2;
}

int32_t cr_decode_frames(cr_reader* r, const uint8_t* const* pixels, int32_t frames, int32_t width, int32_t height, int32_t stride)
{
    if (!r) return -1;
    if (!pixels || frames <= 0 || frames > 8 || width <= 0 || height <= 0 || stride < width) {
        r->error = "bad image arguments";
        return -1;
    }
    if (const int32_t t = tamperGate(r)) return t;
    try {
        std::vector<cv::Mat> mats;
        for (int32_t k = 0; k < frames; ++k) {
            if (!pixels[k]) {
                r->error = "bad image arguments";
                return -1;
            }
            mats.emplace_back(height, width, CV_8UC1, const_cast<uint8_t*>(pixels[k]), static_cast<size_t>(stride));
        }
        cr::Models models;
        models.locator = r->hasModel ? &r->net : nullptr;
        models.enhancers = &r->enhancers;
        models.pool = r->pool.get();
        models.calibration = &r->calib;
        r->result = cr::decodeFrames(mats, r->params, models, &r->steps);
        diagnose(r, mats.front());
        gradeDpm(r, mats.front());
        r->error.clear();
        return static_cast<int32_t>(r->result.codes.size());
    } catch (const std::exception& e) {
        r->error = e.what();
    } catch (...) {
        r->error = "unknown error";
    }
    r->result = cr::Result{};
    r->dpmResult.clear();
    return -2;
}

int32_t cr_code_count(const cr_reader* r) { return r ? static_cast<int32_t>(r->result.codes.size()) : 0; }

int32_t cr_get_code(const cr_reader* r, int32_t index, cr_code* out)
{
    if (!r || !out || index < 0 || index >= static_cast<int32_t>(r->result.codes.size())) return -1;
    const auto& c = r->result.codes[static_cast<size_t>(index)];
    out->text = c.text.c_str();
    out->format = c.format;
    out->step = c.step;
    for (int i = 0; i < 4; ++i) {
        out->x[i] = c.x[i];
        out->y[i] = c.y[i];
    }
    out->copies = c.copies;
    return 0;
}

int32_t cr_get_quality(const cr_reader* r, int32_t index, cr_quality* out)
{
    if (!r || !out || index < 0 || index >= static_cast<int32_t>(r->result.quality.size())) return -1;
    const auto& q = r->result.quality[static_cast<size_t>(index)];
    *out = cr_quality{q.measured ? 1 : 0, q.symbolContrast, q.modulation, q.reflectanceMargin, q.fixedPatternDamage,
                      q.axialNonUniformity, q.gridNonUniformity, q.unusedErrorCorrection, q.printGrowth, q.quietZoneDark,
                      q.moduleSizePx, q.lightingUniformity, q.saturated, q.gradeSC, q.gradeMOD, q.gradeRM, q.gradeFPD,
                      q.gradeAN, q.gradeGN, q.gradeUEC, q.gradeQZ, q.overall,
                      q.calibrated ? 1 : 0, q.xDimMm, q.linear ? 1 : 0, q.edgeContrastMin, q.defects, q.decodability,
                      q.rMin, q.rMax, q.scans, q.decodedScans, q.gradeEC, q.gradeDefects, q.gradeDecodability};
    return 0;
}

int32_t cr_get_code_ext(const cr_reader* r, int32_t index, cr_code_ext* out)
{
    if (!r || !out || index < 0 || index >= static_cast<int32_t>(r->result.codes.size()) || out->struct_size < 4) return -1;
    const auto& c = r->result.codes[static_cast<size_t>(index)];
    cr_code_ext e{};
    e.struct_size = out->struct_size;
    std::strncpy(e.symbology_id, c.symbologyId.size() < sizeof(e.symbology_id) ? c.symbologyId.c_str() : "", sizeof(e.symbology_id) - 1);
    e.gs1 = c.gs1;
    e.grade_rmin = -1;
    e.codeword_yield = -1.f;
    e.grade_codeword_yield = -1;
    if (static_cast<size_t>(index) < r->result.quality.size()) {
        const auto& q = r->result.quality[static_cast<size_t>(index)];
        e.grade_rmin = q.gradeRmin;
        e.scan_count = static_cast<int32_t>(q.scanLines.size());
        if (q.measured && q.gradeCY >= 0) {
            e.codeword_yield = q.codewordYield;
            e.grade_codeword_yield = q.gradeCY;
        }
    }
    const bool matrix = c.format == CR_QR || c.format == CR_MICRO_QR || c.format == CR_RMQR || c.format == CR_DATAMATRIX ||
                        c.format == CR_AZTEC || c.format == CR_DOTCODE;
    if (matrix && c.modW > 0 && c.modH > 0 && c.modules.size() == static_cast<size_t>(c.modW) * c.modH) {
        e.grid_rows = c.modH;
        e.grid_cols = c.modW;
    }
    e.angle = c.angle;
    e.check_digit_ok = c.checkDigitOk;
    e.quiet_zone_ok = c.quietZoneOk;
    e.addon = c.addon.c_str();
    writeSized(out, e);
    return 0;
}

int32_t cr_get_scan(const cr_reader* r, int32_t index, int32_t scan, cr_scan* out)
{
    if (!r || !out || index < 0 || index >= static_cast<int32_t>(r->result.quality.size()) || out->struct_size < 4) return -1;
    const auto& lines = r->result.quality[static_cast<size_t>(index)].scanLines;
    if (scan < 0 || scan >= static_cast<int32_t>(lines.size())) return -1;
    cr_scan s = lines[static_cast<size_t>(scan)];
    s.struct_size = out->struct_size;
    writeSized(out, s);
    return 0;
}

int32_t cr_region_count(const cr_reader* r) { return r ? static_cast<int32_t>(r->result.regions.size()) : 0; }

int32_t cr_get_region(const cr_reader* r, int32_t index, cr_region* out)
{
    if (!r || !out || index < 0 || index >= static_cast<int32_t>(r->result.regions.size())) return -1;
    const auto& g = r->result.regions[static_cast<size_t>(index)];
    *out = cr_region{g.box.x, g.box.y, g.box.width, g.box.height, g.fromAI ? 1 : 0, g.read ? 1 : 0};
    return 0;
}

int32_t cr_miss_count(const cr_reader* r) { return r ? static_cast<int32_t>(r->result.misses.size()) : 0; }

int32_t cr_get_miss(const cr_reader* r, int32_t index, cr_miss* out)
{
    if (!r || !out || index < 0 || index >= static_cast<int32_t>(r->result.misses.size())) return -1;
    const auto& m = r->result.misses[static_cast<size_t>(index)];
    *out = cr_miss{m.x, m.y, m.width, m.height};
    return 0;
}

int32_t cr_get_miss_info(const cr_reader* r, int32_t index, cr_miss_info* out)
{
    if (!r || !out || index < 0 || index >= static_cast<int32_t>(r->result.misses.size())) return -1;
    const size_t i = static_cast<size_t>(index);
    if (i < r->result.missInfo.size()) {
        *out = r->result.missInfo[i];
        return 0;
    }
    const auto& m = r->result.misses[i]; // not diagnosed
    *out = cr_miss_info{};
    out->x = m.x;
    out->y = m.y;
    out->w = m.width;
    out->h = m.height;
    out->finder = out->quiet_zone = -1;
    return 0;
}

static_assert(sizeof(cr_miss_info) == 20 * 4, "cr_miss_info changed: update the .NET Native.MissInfo and ml/engine.py mirrors");

void cr_get_stats(const cr_reader* r, cr_stats* out)
{
    if (!out) return;
    *out = r ? r->result.stats : cr_stats{};
}

int32_t cr_get_stats_ext(const cr_reader* r, cr_stats_ext* out)
{
    if (!out || out->struct_size < 4) return -1;
    cr_stats_ext e{};
    e.struct_size = out->struct_size;
    if (r) {
        e.rejected_length = r->result.rejectedLength;
        e.rejected_check_digit = r->result.rejectedCheckDigit;
        e.copies_over = r->result.copiesOver;
        e.extra_unchecked = r->result.extraUnchecked;
        e.refused_ean = r->result.refusedEan;
        e.refused_addon = r->result.refusedAddon;
    }
    writeSized(out, e);
    return 0;
}

const char* cr_last_error(const cr_reader* r) { return r ? r->error.c_str() : "null reader"; }

const char* cr_version(void) { return "0.1.0"; }

int32_t cr_integrity_check(void) { return static_cast<int32_t>(cr::runIntegrityChecks()); }
int32_t cr_integrity_state(void) { return static_cast<int32_t>(cr::tamperState()); }

static_assert(sizeof(cr_code_ext) == 8 * 4 + sizeof(void*) + 4 * 4 && offsetof(cr_code_ext, addon) == 8 * 4 &&
                  offsetof(cr_code_ext, codeword_yield) == 8 * 4 + sizeof(void*),
              "cr_code_ext changed: grow it only at its end; update the .NET Native.CodeExt mirror");
static_assert(sizeof(cr_stats_ext) == 7 * 4, "cr_stats_ext changed: grow it only at its end; update the .NET Native.StatsExt mirror");
static_assert(sizeof(cr_scan) == 13 * 4, "cr_scan changed: grow it only at its end; update the .NET Native.Scan mirror");
static_assert(sizeof(cr_quality) == 35 * 4, "cr_quality changed: update the .NET Native.Quality and ml/engine.py mirrors");

int32_t cr_set_flat_field(cr_reader* r, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride)
{
    if (!r) return -1;
    if (!pixels) { r->calib.flatGain.release(); return 0; }
    if (width <= 0 || height <= 0 || stride < width) return -1;
    try {
        const cv::Mat white(height, width, CV_8U, const_cast<uint8_t*>(pixels), static_cast<size_t>(stride));
        if (!cr::setFlatField(r->calib, white)) {
            r->error = "flat field: not a white card under working light (too dark or too uneven)";
            return -1;
        }
        return 0;
    } catch (const std::exception& e) {
        r->error = e.what();
        return -1;
    }
}

int32_t cr_set_reflectance(cr_reader* r, float grey_dark, float grey_light, float refl_dark, float refl_light)
{
    if (!r) return -1;
    if (refl_light <= 0.f) { r->calib.reflectance = false; return 0; }
    if (!(grey_light > grey_dark + 10.f) || !(refl_light > refl_dark) || refl_dark < 0.f || refl_light > 100.f) {
        r->error = "reflectance: the light patch must read brighter than the dark one, reflectances 0..100%";
        return -1;
    }
    r->calib.reflectance = true;
    r->calib.greyDark = grey_dark;
    r->calib.greyLight = grey_light;
    r->calib.reflDark = refl_dark;
    r->calib.reflLight = refl_light;
    return 0;
}

int32_t cr_measure_card(cr_reader* r, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, float* grey_dark, float* grey_light)
{
    if (!r || !pixels || width <= 0 || height <= 0 || stride < width || !grey_dark || !grey_light) return -1;
    // the middle 60% of the picture, flat-fielded; the patches as the 10th and 90th percentiles
    try {
        std::vector<float> v;
        const int64_t y1 = int64_t(height) * 4 / 5, x1 = int64_t(width) * 4 / 5;
        for (int64_t y = height / 5; y < y1; y += 2)
            for (int64_t x = width / 5; x < x1; x += 2)
                v.push_back(cr::flatCorrect(r->calib, cv::Point2f(static_cast<float>(x), static_cast<float>(y)), pixels[static_cast<size_t>(y) * stride + x]));
        if (v.size() < 100) return -1;
        std::sort(v.begin(), v.end());
        *grey_dark = v[v.size() / 10];
        *grey_light = v[v.size() * 9 / 10];
        if (*grey_light < *grey_dark + 10.f) {
            r->error = "card: no dark and light patch in the middle of the picture";
            return -1;
        }
        return 0;
    } catch (const std::exception& e) {
        r->error = e.what();
        return -1;
    }
}

int32_t cr_set_lens(cr_reader* r, const cr_lens* l)
{
    if (!r) return -1;
    if (!l) { r->calib.fx = 0; r->calib.mmPerPx = 0; return 0; }
    // a distortion model over the calibration limit (or with no error measured) is never used: the
    // lens is cleared, so no size in mm comes from it (line safety)
    if (l->fx != 0.f && !(std::isfinite(l->rms_px) && l->rms_px >= 0.f && l->rms_px <= cr::kMaxLensRmsPx)) {
        r->calib.fx = 0;
        r->calib.mmPerPx = 0;
        r->error = "lens model reprojects worse than the calibration limit (0.5 px): calibrate again";
        return -1;
    }
    auto& c = r->calib;
    c.fx = l->fx; c.fy = l->fy; c.cx = l->cx; c.cy = l->cy;
    c.k1 = l->k1; c.k2 = l->k2; c.p1 = l->p1; c.p2 = l->p2; c.k3 = l->k3;
    c.mmPerPx = std::isfinite(l->mm_per_px) && l->mm_per_px > 0.f ? l->mm_per_px : 0.f;
    return 0;
}

int32_t cr_calibrate_chessboard(const uint8_t* const* pixels, int32_t count, int32_t width, int32_t height, int32_t stride,
                                int32_t cols, int32_t rows, float square_mm, cr_lens* out)
{
    // a chessboard has a few dozen corners each way: more is a wrong argument (and billions of board points)
    if (!pixels || count <= 0 || width <= 0 || height <= 0 || stride < width || cols < 3 || rows < 3 || cols > 64 || rows > 64 ||
        !(square_mm > 0.f) || !std::isfinite(square_mm) || !out)
        return -1;
    try { // OpenCV refuses some images (too small for its thresholds) by throwing: none found
        std::vector<cv::Mat> views;
        for (int i = 0; i < count; ++i)
            if (pixels[i]) views.emplace_back(height, width, CV_8U, const_cast<uint8_t*>(pixels[i]), static_cast<size_t>(stride));
        cr::Calibration c;
        float rms = 0.f;
        const int used = cr::calibrateChessboard(views, cols, rows, square_mm, c, &rms);
        if (used <= 0) return used;
        *out = cr_lens{c.fx, c.fy, c.cx, c.cy, c.k1, c.k2, c.p1, c.p2, c.k3, c.mmPerPx, rms};
        return used;
    } catch (const std::exception&) {
        return -1;
    }
}

int32_t cr_get_learned(const cr_reader* r, char* buf, int32_t cap)
{
    if (!r) return -1;
    try {
        const std::string text = cr::learnedToText(r->steps);
        const int32_t need = static_cast<int32_t>(text.size()) + 1;
        if (buf && cap >= need) std::memcpy(buf, text.c_str(), static_cast<size_t>(need));
        return need;
    } catch (const std::exception&) {
        return -1;
    }
}

int32_t cr_set_learned(cr_reader* r, const char* text)
{
    if (!r) return -1;
    if (!text) {
        r->error = "learned state is null";
        return -1;
    }
    std::string err;
    try {
        if (!cr::learnedFromText(r->steps, text, &err)) {
            r->error = "learned state rejected: " + err;
            return -1;
        }
    } catch (const std::exception& e) {
        r->error = std::string("learned state rejected: ") + e.what();
        return -1;
    }
    return 0;
}

void cr_default_dpm_params(cr_dpm_params* p)
{
    if (!p) return;
    const cr::DpmSettings d;
    *p = cr_dpm_params{static_cast<int32_t>(sizeof(cr_dpm_params)), 0, d.lighting, d.aperture, d.wavelengthNm, d.closing,
                       d.rCal, d.mlCal, d.srCal, d.srTarget};
}

int32_t cr_set_dpm(cr_reader* r, const cr_dpm_params* p)
{
    if (!r) return -1;
    if (!p || p->struct_size < 8) {
        r->error = "cr_dpm_params.struct_size is not set: call cr_default_dpm_params first";
        return -1;
    }
    // the caller's fields over the defaults: only the bytes its struct_size covers
    cr_dpm_params v;
    cr_default_dpm_params(&v);
    std::memcpy(&v, p, std::min(static_cast<size_t>(p->struct_size), sizeof(cr_dpm_params)));
    if (v.aperture != 0 && v.aperture != 50 && v.aperture != 80) {
        r->error = "DPM aperture must be 50, 80 or 0 (the better of the two)";
        return -1;
    }
    if (v.lighting < CR_DPM_LIGHT_90 || v.lighting > CR_DPM_LIGHT_D) {
        r->error = "DPM lighting must be a cr_dpm_lighting value";
        return -1;
    }
    if (v.closing > 64) { // a disc wider than a few cells closes the whole mark
        r->error = "DPM closing must be -1 (off), 0 (sized from the mark) or a disc of 1..64 px";
        return -1;
    }
    if (!std::isfinite(v.r_cal) || !std::isfinite(v.ml_cal) || !std::isfinite(v.sr_cal) || !std::isfinite(v.sr_target) ||
        v.r_cal < 0.f || v.r_cal > 100.f || v.ml_cal < 0.f || v.sr_cal < 0.f || v.sr_target < 0.f) {
        r->error = "DPM calibration: reflectance 0..100 %, mean light and system responses not negative";
        return -1;
    }
    cr::DpmSettings& d = r->dpm;
    d.enabled = v.enabled != 0;
    d.lighting = v.lighting;
    d.aperture = v.aperture;
    d.wavelengthNm = v.wavelength_nm;
    d.closing = v.closing;
    d.rCal = v.r_cal;
    d.mlCal = v.ml_cal;
    d.srCal = v.sr_cal;
    d.srTarget = v.sr_target;
    return 0;
}

int32_t cr_get_dpm(const cr_reader* r, int32_t index, cr_dpm* out)
{
    if (!r || !out || index < 0 || index >= static_cast<int32_t>(r->result.codes.size()) || out->struct_size < 4) return -1;
    cr_dpm e{};
    e.struct_size = out->struct_size;
    e.aperture = 0;
    e.min_reflectance = -1.f;
    e.grid_nonuniformity = -1.f;
    e.unused_error_correction = -1.f;
    e.grade_decode = e.grade_cc = e.grade_cm = e.grade_mr = e.grade_fpd = e.grade_an = e.grade_gn = e.grade_uec = -1;
    e.overall = e.overall_50 = e.overall_80 = -1;
    if (static_cast<size_t>(index) < r->dpmResult.size()) {
        const auto& q = r->dpmResult[static_cast<size_t>(index)];
        e.lighting = q.lighting;
        if (q.measured) {
            e.measured = 1;
            e.calibrated = q.calibrated ? 1 : 0;
            e.inverted = q.inverted ? 1 : 0;
            e.aperture = q.aperturePct;
            e.cell_px = q.cellPx;
            e.closing_px = q.closingPx;
            e.mean_light = q.meanLight;
            e.mean_dark = q.meanDark;
            e.mean_light_pct = q.meanLightPct;
            e.mean_light_ok = q.meanLightOk ? 1 : 0;
            e.cell_contrast = q.cellContrast;
            e.cell_modulation = q.cellModulation;
            e.min_reflectance = q.minReflectance;
            e.fixed_pattern_damage = q.fixedPatternDamage;
            e.distributed_damage = q.distributedDamage;
            e.axial_nonuniformity = q.axialNonUniformity;
            e.grid_nonuniformity = q.gridNonUniformity;
            e.unused_error_correction = q.unusedErrorCorrection;
            e.grade_decode = q.gradeDecode;
            e.grade_cc = q.gradeCC;
            e.grade_cm = q.gradeCM;
            e.grade_mr = q.gradeMR;
            e.grade_fpd = q.gradeFPD;
            e.grade_an = q.gradeAN;
            e.grade_gn = q.gradeGN;
            e.grade_uec = q.gradeUEC;
            e.overall = q.overall;
            e.overall_50 = q.overall50;
            e.overall_80 = q.overall80;
        }
    }
    writeSized(out, e);
    return 0;
}

static_assert(sizeof(cr_dpm_params) == 10 * 4, "cr_dpm_params changed: update the .NET Native.DpmParams mirror");
static_assert(sizeof(cr_dpm) == 31 * 4, "cr_dpm changed: update the .NET Native.Dpm mirror");

} // extern "C"
