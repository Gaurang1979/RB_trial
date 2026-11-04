// decode.cpp - the decode pipeline.
//
// 1. Locate candidate regions on a shrunken image (locate.cpp).
// 2. Decode candidates in parallel, best first. Each crop gets a short chain of
//    image fixes (stretch, upscale, contrast, sharpen, threshold) and stops at
//    the first fix that reads it.
// 3. Stop as soon as the operator's expected count is reached; then decode the code-sized regions
//    left, so an extra pack in the case is seen (more codes than expected is NG).
// 4. If still short and time remains, run whole-image passes as a fallback.
//
// 1D-only jobs (the parent code on the side camera) skip the locator: a 2 MP
// image with one code is read directly.

#include "reader.hpp"
#include "zxformats.hpp"
#include "pool.hpp"

#include <ZXing/ReadBarcode.h>
#ifdef CR_HAVE_DMTX
#include <dmtx.h>
#endif
#include <opencv2/core/utility.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cstdlib>
#include <cctype>
#include <atomic>
#include <cmath>
#include <chrono>
#include <map>
#include <memory>
#include <condition_variable>
#include <mutex>
#include <set>
#include <thread>
#include <vector>

namespace cr {

namespace {

using Clock = std::chrono::steady_clock;

double msSince(Clock::time_point t0)
{
    return std::chrono::duration<double, std::milli>(Clock::now() - t0).count();
}

bool is2D(int f) { return (f & (CR_QR | CR_DATAMATRIX)) != 0; }
// the types zxing reads (an empty zxing format set would read every type)
constexpr int kZxTypes = CR_QR | CR_DATAMATRIX | CR_ALL_LINEAR | CR_MICRO_QR | CR_RMQR | CR_PDF417 | CR_AZTEC;

// Linear stretch so the 1st..99.5th percentile fills 0..255.
cv::Mat stretch(const cv::Mat& c)
{
    int hist[256] = {};
    for (int y = 0; y < c.rows; ++y) {
        const uint8_t* row = c.ptr<uint8_t>(y);
        for (int x = 0; x < c.cols; ++x) hist[row[x]]++;
    }
    const int64_t n = static_cast<int64_t>(c.total());
    int lo = 0, hi = 255;
    int64_t acc = 0;
    for (int v = 0; v < 256; ++v) { acc += hist[v]; if (acc >= n / 100) { lo = v; break; } }
    acc = 0;
    for (int v = 255; v >= 0; --v) { acc += hist[v]; if (acc >= n / 200) { hi = v; break; } }
    if (hi - lo < 4) hi = lo + 4;
    cv::Mat out;
    c.convertTo(out, CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
    return out;
}

// Uneven light inside one code (bright left half, shadowed right half, a film edge across it):
// divide by the local paper level, so paper is equally white everywhere. The paper level is
// a morphological closing (removes the dark modules) at about two modules, then smoothed.
// codeSide: the finder's size of the code area (0 = unknown); the window is about two modules
cv::Mat flatten(const cv::Mat& crop, float codeSide = 0.f)
{
    int k = codeSide > 0 ? static_cast<int>(codeSide / 12.f) : std::min(crop.cols, crop.rows) / 30;
    k = std::clamp(k, 5, 41) | 1;
    cv::Mat f, bg;
    crop.convertTo(f, CV_32F, 1.0, 1.0);
    cv::morphologyEx(f, bg, cv::MORPH_CLOSE, cv::getStructuringElement(cv::MORPH_ELLIPSE, {k, k}));
    cv::GaussianBlur(bg, bg, cv::Size(), k / 2.0);
    cv::Mat r = f / bg;
    // 1st..99th percentile of the ratio -> 0..255
    std::vector<float> v(r.begin<float>(), r.end<float>());
    const size_t lo = v.size() / 100, hi = v.size() * 99 / 100;
    std::nth_element(v.begin(), v.begin() + lo, v.end());
    const float vlo = v[lo];
    std::nth_element(v.begin(), v.begin() + hi, v.end());
    const float vhi = std::max(v[hi], vlo + 1e-3f);
    cv::Mat out;
    r.convertTo(out, CV_8U, 255.0 / (vhi - vlo), -vlo * 255.0 / (vhi - vlo));
    return out;
}

} // namespace

// Glare on part of a code (film or a hologram label bent by a wrinkle reflects the light): the
// dark modules under the reflection turn light grey while the rest of the code stays black and
// white, with a sharp edge between, which a paper-level division (flatten) smooths over. Here
// each small area is stretched between its own darkest and brightest level (a window of about
// two and a half modules, which nearly always holds both kinds; the levels smoothed over about a
// module), so the code under the glare is as black and white as the rest. A light smoothing first
// keeps the noise from being stretched with the faint modules; a blank or saturated area is not
// stretched beyond 20x (its range floored at a twentieth of the crop's range).
// Tried on codes half under a synthetic reflection band: alone it reads about as many as CLAHE,
// but different ones (together 39 of 40 where CLAHE reads 36), so it is a step of its own,
// learned and pruned like the others.
cv::Mat deglare(const cv::Mat& crop, float codeSide)
{
    int k = codeSide > 0 ? static_cast<int>(codeSide / 10.f) : std::min(crop.cols, crop.rows) / 24;
    k = std::clamp(k, 5, 61) | 1;
    cv::Mat f, hi, lo;
    crop.convertTo(f, CV_32F);
    cv::GaussianBlur(f, f, cv::Size(), 0.6);
    const cv::Mat ker = cv::getStructuringElement(cv::MORPH_ELLIPSE, {k, k});
    cv::dilate(f, hi, ker);
    cv::erode(f, lo, ker);
    cv::GaussianBlur(hi, hi, cv::Size(), k / 2.0);
    cv::GaussianBlur(lo, lo, cv::Size(), k / 2.0);
    std::vector<float> v(f.begin<float>(), f.end<float>());
    const size_t a = v.size() / 100, b = v.size() * 99 / 100;
    std::nth_element(v.begin(), v.begin() + a, v.end());
    const float p1 = v[a];
    std::nth_element(v.begin(), v.begin() + b, v.end());
    const float floorRange = std::max(8.f, 0.05f * (v[b] - p1));
    cv::Mat range = cv::max(hi - lo, floorRange), out;
    cv::Mat r = (f - lo) / range;
    r.convertTo(out, CV_8U, 255.0);
    return out;
}

namespace {

// Defocus (labels bent over caps or bottles, out of the depth of field): Richardson-Lucy
// deconvolution with a Gaussian blur of the given sigma, on the brightness-stretched crop.
// Wiener deconvolution with a Gaussian of sigma (pixels): one forward and one inverse transform.
// The crop is mirrored at its borders first, so the transform does not ring on its edges.
cv::Mat wiener(const cv::Mat& f, double sigma, double k)
{
    const int pad = static_cast<int>(std::ceil(3 * sigma)) + 2;
    cv::Mat ext;
    cv::copyMakeBorder(f, ext, pad, pad, pad, pad, cv::BORDER_REFLECT);
    const int w = cv::getOptimalDFTSize(ext.cols), h = cv::getOptimalDFTSize(ext.rows);
    cv::copyMakeBorder(ext, ext, 0, h - ext.rows, 0, w - ext.cols, cv::BORDER_REFLECT);
    cv::Mat spec;
    cv::dft(ext, spec, cv::DFT_COMPLEX_OUTPUT);
    // the Gaussian's transform is real and even: exp(-2 pi^2 sigma^2 (u^2 + v^2)), u in cycles/px
    const double c = 2.0 * CV_PI * CV_PI * sigma * sigma;
    for (int y = 0; y < h; ++y) {
        const double v = (y <= h / 2 ? y : y - h) / static_cast<double>(h);
        cv::Vec2f* row = spec.ptr<cv::Vec2f>(y);
        for (int x = 0; x < w; ++x) {
            const double u = (x <= w / 2 ? x : x - w) / static_cast<double>(w);
            const double H = std::exp(-c * (u * u + v * v));
            const float g = static_cast<float>(H / (H * H + k));
            row[x] *= g;
        }
    }
    cv::Mat out;
    cv::dft(spec, out, cv::DFT_INVERSE | cv::DFT_SCALE | cv::DFT_REAL_OUTPUT);
    return out(cv::Rect(pad, pad, f.cols, f.rows)).clone();
}

// Wiener start, then 3 Richardson-Lucy iterations (15 iterations from the blurred image before:
// 8 deblur-dependent images 2026-10-27, deblur calls 7 ms instead of 11-13 ms, at the line
// targets 261-262 of 264 codes instead of 254-259; noise constant 0.01-0.05 and 1-6 iterations
// within noise). CR_DEBLUR=rl15 is the old way, for A/B. fromBlurred: the old way for this call;
// the deblur steps use it as a second try, since the two read different codes (dark caps,
// 2026-10-30: 5 of 47 crate caps read only from the blurred start, 2 of 48 cans only from Wiener).
cv::Mat deblur(const cv::Mat& crop, double sigma, bool fromBlurred = false)
{
    cv::Mat f;
    stretch(crop).convertTo(f, CV_32F, 1.0 / 255, 1e-3);
    cv::Mat est, conv, ratio;
    static const bool old = std::getenv("CR_DEBLUR") && std::string(std::getenv("CR_DEBLUR")) == "rl15";
    int iterations = 3;
    if (old || fromBlurred) {
        est = f.clone();
        iterations = 15;
    } else {
        est = cv::max(wiener(f, sigma, 0.02), 1e-3);
    }
    for (int i = 0; i < iterations; ++i) {
        cv::GaussianBlur(est, conv, cv::Size(), sigma);
        cv::divide(f, conv + 1e-6, ratio);
        cv::GaussianBlur(ratio, ratio, cv::Size(), sigma);
        est = est.mul(ratio);
    }
    cv::Mat out;
    est.convertTo(out, CV_8U, 255.0);
    return out;
}

// Quick quality look at one region, to pick the right fix first instead of trying them all.
struct RegionLook {
    float contrast = 0;  // 5th..95th percentile spread, grey levels
    float sharpness = 0; // 95th percentile gradient of the normalised crop; ~1 / edge width (px)
};

RegionLook lookAt(const cv::Mat& crop)
{
    RegionLook r;
    cv::Mat c = crop;
    if (c.cols > 400) cv::resize(crop, c, cv::Size(), 400.0 / crop.cols, 400.0 / crop.cols, cv::INTER_AREA);
    int hist[256] = {};
    for (int y = 0; y < c.rows; ++y) {
        const uint8_t* row = c.ptr<uint8_t>(y);
        for (int x = 0; x < c.cols; ++x) hist[row[x]]++;
    }
    const int64_t n = static_cast<int64_t>(c.total());
    int lo = 0, hi = 255;
    int64_t acc = 0;
    for (int v = 0; v < 256; ++v) { acc += hist[v]; if (acc > n / 20) { lo = v; break; } }
    acc = 0;
    for (int v = 255; v >= 0; --v) { acc += hist[v]; if (acc > n / 20) { hi = v; break; } }
    r.contrast = static_cast<float>(hi - lo);
    const float scale = static_cast<float>(crop.cols) / c.cols; // gradients shrink when we downscale
    cv::Mat f, gx, gy, g;
    c.convertTo(f, CV_32F, 1.0 / std::max(1, hi - lo), -lo / static_cast<double>(std::max(1, hi - lo)));
    cv::Sobel(f, gx, CV_32F, 1, 0);
    cv::Sobel(f, gy, CV_32F, 0, 1);
    cv::magnitude(gx, gy, g);
    std::vector<float> v(g.begin<float>(), g.end<float>());
    const size_t k = v.size() * 95 / 100;
    std::nth_element(v.begin(), v.begin() + k, v.end());
    r.sharpness = v.empty() ? 0.f : v[k] / 4.f / scale;
    return r;
}

// Dot-printed modules (inkjet on caps: each dark module a round dot with gaps around it), print
// voids and specks, as NI Vision's cell filters treat them: the dots melted into solid modules.
// Three variants, m = module size in pixels (synthetic dot-print scenes, ml/experiments/dots.py:
// none reads alone everywhere, together 8-12 of 12 Data Matrix and 5-12 of 12 QR at dot sizes
// 0.5-0.8 where the unfiltered crop reads none): 0 a blur of a third of a module (the dots run
// together), 1 the dark grown by 0.7 module (gaps closed), 2 grown then shrunk by 1.2 modules (gaps
// and voids closed, the module size kept)
cv::Mat dotFilter(const cv::Mat& crop, float m, int variant)
{
    const auto odd = [](float x) { return std::max(3, static_cast<int>(std::lround(x))) | 1; };
    cv::Mat s = stretch(crop), out;
    if (variant == 0) {
        cv::GaussianBlur(s, out, cv::Size(), std::max(0.8, 0.35 * m));
    } else if (variant == 1) {
        const int k = odd(0.7f * m);
        cv::erode(s, out, cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(k, k)));
    } else {
        const int k = odd(1.2f * m);
        const cv::Mat e = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(k, k));
        cv::erode(s, out, e);
        cv::dilate(out, out, e);
    }
    // small modules: a 2x upscale, as the other steps give them
    if (m < 4.f) cv::resize(out, out, cv::Size(), 2.0, 2.0, cv::INTER_CUBIC);
    return out;
}

// The deblur width of a deblur step. An unread code is blurrier than the read ones the blur was
// measured on: the first deblur takes a little more than the measured width, the strong one about
// twice it. deblurPx 0 = not measured: the fixed widths.
double deblurSigma(int step, float deblurPx)
{
    if (step == CR_STEP_DEBLUR_STRONG) return deblurPx > 0.f ? std::clamp(2.0 * deblurPx, 1.3, 6.0) : 2.5;
    return deblurPx > 0.f ? std::clamp(1.2 * deblurPx, 0.8, 4.0) : 1.5;
}

// deblurPx: the lens blur at this region in crop pixels (measured on read codes, psf.cpp); 0 =
// not measured, the fixed widths then apply
cv::Mat applyStep(const cv::Mat& crop, int step, int maxUpscale, float codeSide = 0.f, float deblurPx = 0.f)
{
    const double up = std::min(2, std::max(1, maxUpscale));
    cv::Mat out;
    switch (step) {
    case CR_STEP_PLAIN:
        return stretch(crop);
    case CR_STEP_UPSCALE:
        cv::resize(stretch(crop), out, cv::Size(), up, up, cv::INTER_CUBIC);
        return out;
    case CR_STEP_CLAHE: {
        auto clahe = cv::createCLAHE(3.0, cv::Size(4, 4));
        clahe->apply(crop, out);
        return out;
    }
    case CR_STEP_SHARPEN: {
        cv::Mat u, b;
        cv::resize(stretch(crop), u, cv::Size(), up, up, cv::INTER_CUBIC);
        cv::GaussianBlur(u, b, cv::Size(), 2.0);
        cv::addWeighted(u, 1.8, b, -0.8, 0, out);
        return out;
    }
    case CR_STEP_BINARY: {
        cv::Mat u;
        cv::resize(stretch(crop), u, cv::Size(), up, up, cv::INTER_CUBIC);
        cv::adaptiveThreshold(u, out, 255, cv::ADAPTIVE_THRESH_MEAN_C, cv::THRESH_BINARY, 31, 0);
        return out;
    }
    case CR_STEP_FLAT:
        cv::resize(flatten(crop, codeSide), out, cv::Size(), up, up, cv::INTER_CUBIC);
        return out;
    case CR_STEP_GLARE:
        cv::resize(deglare(crop, codeSide), out, cv::Size(), up, up, cv::INTER_CUBIC);
        return out;
    case CR_STEP_UPSCALE4:
        cv::resize(stretch(crop), out, cv::Size(), 4.0, 4.0, cv::INTER_LANCZOS4);
        return out;
    case CR_STEP_DEBLUR:
    case CR_STEP_DEBLUR_STRONG:
        return deblur(crop, deblurSigma(step, deblurPx));
    case CR_STEP_DOTS: {
        // the first variant here; the step loop tries the other two (dotFilter)
        return dotFilter(crop, codeSide > 0.f ? codeSide / 18.f : std::min(crop.cols, crop.rows) / 45.f, 0);
    }
    case CR_STEP_FLAT_ADAPT: {
        cv::Mat u;
        cv::resize(flatten(crop, codeSide), u, cv::Size(), up, up, cv::INTER_CUBIC);
        cv::adaptiveThreshold(u, out, 255, cv::ADAPTIVE_THRESH_GAUSSIAN_C, cv::THRESH_BINARY, 41, 5);
        return out;
    }
    default:
        return crop;
    }
}

ZXing::Barcodes read(const cv::Mat& img, const ZXing::ReaderOptions& opts)
{
    ZXing::ImageView view(img.data, img.cols, img.rows, ZXing::ImageFormat::Lum, static_cast<int>(img.step));
    return ZXing::ReadBarcodes(view, opts);
}

} // namespace

#ifdef CR_HAVE_DMTX
// libdmtx: slower than zxing-cpp but stronger on blurred / damaged Data Matrix. Used only on
// regions zxing could not read, with a time limit per region.
// symId: "]d2" when the symbol starts with FNC1 (GS1), else "]d1". Every FNC1 comes out as GS
// (ASCII 29), as from zxing; the leading one is taken off the text.
bool readDmtx(const cv::Mat& img, int timeoutMs, std::string& text, cv::Point2f q[4], std::string* symId)
{
    cv::Mat c = img.isContinuous() ? img : img.clone();
    DmtxImage* dimg = dmtxImageCreate(c.data, c.cols, c.rows, DmtxPack8bppK);
    if (!dimg) return false;
    dmtxImageSetProp(dimg, DmtxPropImageFlip, DmtxFlipNone);
    DmtxDecode* dec = dmtxDecodeCreate(dimg, 1);
    bool ok = false;
    if (dec) {
        dmtxDecodeSetProp(dec, DmtxPropFnc1, 29);
        DmtxTime timeout = dmtxTimeAdd(dmtxTimeNow(), timeoutMs);
        DmtxRegion* reg = dmtxRegionFindNext(dec, &timeout);
        if (reg) {
            DmtxMessage* msg = dmtxDecodeMatrixRegion(dec, reg, DmtxUndefined);
            if (msg) {
                text.assign(reinterpret_cast<const char*>(msg->output), msg->outputIdx);
                const bool gs1 = !text.empty() && text.front() == '\x1d';
                if (gs1) text.erase(0, 1);
                if (symId) *symId = gs1 ? "]d2" : "]d1";
                const double corners[4][2] = {{0, 1}, {1, 1}, {1, 0}, {0, 0}};
                for (int i = 0; i < 4; ++i) {
                    DmtxVector2 v = {corners[i][0], corners[i][1]};
                    dmtxMatrix3VMultiplyBy(&v, reg->fit2raw);
                    // libdmtx counts y up from the bottom row (DmtxFlipNone): image rows count down
                    q[i] = cv::Point2f(static_cast<float>(v.X), static_cast<float>(c.rows - 1 - v.Y));
                }
                ok = !text.empty();
                dmtxMessageDestroy(&msg);
            }
            dmtxRegionDestroy(&reg);
        }
        dmtxDecodeDestroy(&dec);
    }
    dmtxImageDestroy(&dimg);
    return ok;
}
#endif

namespace {

// Collects unique codes from all threads.
class Collector {
public:
    explicit Collector(const cr_params& p)
        : expected_(p.expected_count), formats_(p.format), minLength_(p.min_length), maxLength_(p.max_length), checkDigit_(p.check_digit) {}
    // zxing reads, and those that show light-on-dark print (read inverted on the image as it was,
    // or read normally on the flipped image): the print's polarity
    std::atomic<int> zxReads{0}, invertedReads{0};
    std::atomic<bool> flipped{false};

    // offset/scale map crop coordinates back to the full image. True when the code is new;
    // centreOut gets its centre (full image).
    bool add(const ZXing::Barcode& b, int step, cv::Point2f offset, float scale, cv::Point2f* centreOut = nullptr)
    {
        const auto& pos = b.position();
        cv::Point2f q[4];
        for (int i = 0; i < 4; ++i)
            q[i] = cv::Point2f(offset.x + pos[i].x / scale, offset.y + pos[i].y / scale);
        Code c;
        c.text = mainText(b, &c.addon);
        c.format = fromZx(b.format());
        c.step = step;
        c.lightOnDark = b.isInverted() != flipped.load() ? 1 : 0;
        // the sampled module grid, kept for quality grading
        const auto sym = b.symbol();
        if (sym.data() && sym.width() > 0 && sym.height() > 0) {
            c.modW = sym.width();
            c.modH = sym.height();
            c.modules.resize(static_cast<size_t>(c.modW) * c.modH);
            for (int y = 0; y < c.modH; ++y)
                for (int x = 0; x < c.modW; ++x)
                    c.modules[static_cast<size_t>(y) * c.modW + x] = *sym.data(x, y) == 0 ? 1 : 0; // 1 = dark
        }
        if (const std::string u = b.extra("UEC"); !u.empty()) c.uec = std::strtof(u.c_str(), nullptr);
        c.ecLevel = b.ecLevel();
        c.symbologyId = b.symbologyIdentifier();
        c.gs1 = b.contentType() == ZXing::ContentType::GS1 ? 1 : 0;
        zxReads++;
        if (b.isInverted() != flipped.load()) invertedReads++;
        if (centreOut) *centreOut = (q[0] + q[2]) * 0.5f;
        if (c.format == CR_PDF417 && !pdf417Holds(c.text, q, c.uec, b.isInverted() != flipped.load())) return false;
        return addCode(std::move(c), q);
    }

    // the image the reads' corners refer to (less the ROI offset), as the camera gave it: zxing's
    // PDF417 reads are cross-checked on it (pdf417.cpp)
    void setSource(const cv::Mat& gray, cv::Point2f offset)
    {
        source_ = gray;
        sourceOffset_ = offset;
    }
    // zxing corrects a PDF417 to the last check codeword and tries up to 100 guesses for unclear
    // ones: a read that used check codewords is read again (pdf417ReadHolds); one that does not
    // hold is never reported. Texts once refused are refused without the work.
    bool pdf417Holds(const std::string& text, const cv::Point2f q[4], float uec, bool lightOnDark)
    {
        if (uec >= 0.999f) return true;
        {
            std::lock_guard<std::mutex> lock(mu_);
            if (codes_.count(text)) return true;
            if (refusedPdf417_.count(text)) return false;
        }
        cv::Point2f local[4];
        for (int i = 0; i < 4; ++i) local[i] = q[i] - sourceOffset_;
        if (!source_.empty() && pdf417ReadHolds(source_, local, text, uec, lightOnDark)) return true;
        std::lock_guard<std::mutex> lock(mu_);
        refusedPdf417_.insert(text);
        if (std::getenv("CR_PROF")) std::fprintf(stderr, "pdf417 zxing read refused (UEC %.2f)\n", static_cast<double>(uec));
        return false;
    }

    // zxing decodes an EAN / UPC character from its bar and space widths, which print gain throws: a
    // label with 2 px of gain at 6 px a module read as another number whose check digit holds. Each
    // EAN / UPC read is read again on the image after the reference decode (eanReadHolds); one that
    // does not hold is never reported (line safety: a wrong number is worse than none)
    bool eanHolds(const Code& c, const cv::Point2f q[4])
    {
        if (!(c.format & (CR_EAN13 | CR_UPCA | CR_EAN8 | CR_UPCE)) || source_.empty()) return true;
        {
            std::lock_guard<std::mutex> lock(mu_);
            if (codes_.count(c.text)) return true;
        }
        Code local = c;
        for (int i = 0; i < 4; ++i) {
            local.x[i] = q[i].x - sourceOffset_.x;
            local.y[i] = q[i].y - sourceOffset_.y;
        }
        if (eanReadHolds(source_, local)) return true;
        refusedEan++;
        if (std::getenv("CR_PROF")) std::fprintf(stderr, "ean/upc read %s (step %d) refused: not read again on the image\n", c.text.c_str(), c.step);
        return false;
    }
    std::atomic<int> refusedEan{0};

    // zxing reads an EAN-2 / EAN-5 add-on by its bar widths as it reads the main symbol, and checks
    // it by parity alone (EAN-2 a value modulo 4): each add-on is read again on the image after the
    // reference decode, past the main symbol that read c's text (eanReadHolds). One that does not hold
    // is reported as none (a job that requires the add-on gives NG), never as read. An add-on already
    // confirmed for this text is not read again
    bool addonHolds(const Code& c, const cv::Point2f q[4])
    {
        if (c.addon.empty()) return true;
        {
            std::lock_guard<std::mutex> lock(mu_);
            if (confirmedAddon_.count(c.text + '\x1f' + c.addon)) return true;
        }
        int holds = 0;
        if (!source_.empty()) {
            Code local = c;
            for (int i = 0; i < 4; ++i) {
                local.x[i] = q[i].x - sourceOffset_.x;
                local.y[i] = q[i].y - sourceOffset_.y;
            }
            eanReadHolds(source_, local, &holds);
        }
        if (holds) {
            std::lock_guard<std::mutex> lock(mu_);
            confirmedAddon_.insert(c.text + '\x1f' + c.addon);
            return true;
        }
        refusedAddon++;
        if (std::getenv("CR_PROF")) std::fprintf(stderr, "ean/upc add-on %s of %s (step %d) refused: not read again on the image\n", c.addon.c_str(), c.text.c_str(), c.step);
        return false;
    }
    std::atomic<int> refusedAddon{0};

    // the job's type and 1D rules on a copy of a read's text: false = addCode would drop it.
    // `text` becomes the text addCode would keep (a stripped check digit)
    bool accepts(int format, std::string& text)
    {
        if (format == 0 || !(format & formats_)) return false;
        int reject = kRejectNone;
        if (applyLinearRules(format, text, minLength_, maxLength_, checkDigit_, &reject)) return true;
        (reject == kRejectLength ? rejectedLength : rejectedCheckDigit)++;
        return false;
    }
    // 1D reads the job's rules dropped (cr_stats)
    std::atomic<int> rejectedLength{0}, rejectedCheckDigit{0};

    // which read of a code its grade comes from, lower first: the raw whole-image pass (the image
    // as captured), then other reads with a module grid and the decoder's UEC, then a grid alone,
    // then none (the engine's own decoders, graded on their own sampling)
    static int gradeRank(const Code& c)
    {
        if (c.modules.empty()) return 3;
        if (c.uec < 0.f) return 2;
        return c.step == CR_STEP_FULL ? 0 : 1;
    }

    // a code from any decoder: corners in full-image coordinates. True when it is new.
    bool addCode(Code c, const cv::Point2f q[4])
    {
        // a type the job does not read (a Micro QR seen by the QR decoder) or a 1D read that
        // breaks the job's length or check-digit rule is never reported
        if (!accepts(c.format, c.text)) return false;
        if (!eanHolds(c, q)) return false;
        if (!addonHolds(c, q)) c.addon.clear();
        std::lock_guard<std::mutex> lock(mu_);
        auto it = codes_.find(c.text);
        if (it != codes_.end()) {
            // a read that also caught the EAN/UPC add-on (the first may have missed it); two labels with
            // the same main text but different add-ons (issue, price) report none, never one of them
            Code& o = it->second;
            if (!c.addon.empty() && !o.addonMixed) {
                if (o.addon.empty()) o.addon = c.addon;
                else if (o.addon != c.addon) { o.addon.clear(); o.addonMixed = true; }
            }
            // the same code found again: a copy when it is away from every place it was seen, by
            // the rule the regions use (0.6 code sides: codes on a case sit one side apart, so the
            // copy on the next pack or the label's second print is one; a re-read of a symbol lands
            // on its own place). Every place is kept, so a copy read again is not counted twice
            // A 1D read's corners are the band of scan rows that decoded, which moves along tall
            // bars: there a copy must be 1.5 long sides away
            const cv::Point2f ctr = (q[0] + q[2]) * 0.5f;
            float side = 0.f, longSide = 0.f;
            for (int k = 0; k < 4; ++k) {
                const float e = std::hypot(it->second.x[(k + 1) % 4] - it->second.x[k], it->second.y[(k + 1) % 4] - it->second.y[k]);
                side += e / 4.f;
                longSide = std::max(longSide, e);
            }
            const float apart = (it->second.format & CR_ALL_LINEAR) ? 1.5f * longSide : 0.6f * side;
            auto& seen = places_[it->first];
            bool known = false;
            for (const auto& s : seen) known = known || cv::norm(ctr - s) < apart;
            if (!known) {
                seen.push_back(ctr);
                it->second.copies++;
            }
            // the same symbol read again (at its first place, not a copy's): its grading data (module
            // grid, the decoder's UEC, EC level and the corners they belong to) from the read nearest
            // the image as captured, never from whichever thread added first. The raw and brightened whole-image jobs run at once, and
            // the brightened one can see a codeword more wrong (Micro QR M4-L: UEC 0.33 or 0.00 by
            // thread order). Between reads of the same standing, the lower UEC (order-free). Only of
            // the same type: a 1D and a 2D code carrying the same text keep their own grids.
            if (c.format == o.format && !seen.empty() && cv::norm(ctr - seen.front()) < apart && (gradeRank(c) < gradeRank(o) || (gradeRank(c) == gradeRank(o) && c.uec >= 0.f && o.uec >= 0.f && c.uec < o.uec))) {
                o.modules = std::move(c.modules);
                o.modW = c.modW;
                o.modH = c.modH;
                o.uec = c.uec;
                o.ecLevel = c.ecLevel;
                for (int i = 0; i < 4; ++i) {
                    o.x[i] = q[i].x;
                    o.y[i] = q[i].y;
                }
            }
            // the identifier from the read that knows it; two that disagree on GS1 report the
            // one without FNC1, so a copy printed without it is never hidden by one printed with it
            if (it->second.lightOnDark < 0) it->second.lightOnDark = c.lightOnDark; // the polarity from a read that knows it
            if (!c.symbologyId.empty() && (it->second.symbologyId.empty() || (c.gs1 == 0 && it->second.gs1 == 1))) {
                it->second.symbologyId = c.symbologyId;
                it->second.gs1 = c.gs1;
            }
            return false;
        }
        for (int i = 0; i < 4; ++i) {
            c.x[i] = q[i].x;
            c.y[i] = q[i].y;
        }
        places_[c.text] = {(q[0] + q[2]) * 0.5f};
        std::string key = c.text;
        codes_.emplace(std::move(key), std::move(c));
        if (expected_ > 0 && static_cast<int>(codes_.size()) >= expected_) done_ = true;
        return true;
    }

    bool done() const { return done_.load(); }

    int count()
    {
        std::lock_guard<std::mutex> lock(mu_);
        return static_cast<int>(codes_.size());
    }

    std::vector<Code> take()
    {
        std::lock_guard<std::mutex> lock(mu_);
        std::vector<Code> out;
        out.reserve(codes_.size());
        for (auto& kv : codes_) out.push_back(kv.second);
        return out;
    }

    // median side (pixels) of the 2D codes read so far in this image, and the QR module count:
    // every code in a case has the same size and type, so this is the best guess for one that did
    // not read. 0 = none.
    std::pair<float, int> medianQr()
    {
        std::lock_guard<std::mutex> lock(mu_);
        std::vector<float> sides;
        std::vector<int> mods;
        for (const auto& kv : codes_) {
            const auto& c = kv.second;
            if (c.format != CR_QR && c.format != CR_DATAMATRIX) continue;
            float s = 0.f;
            for (int k = 0; k < 4; ++k) s += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
            sides.push_back(s / 4.f);
            if (c.format == CR_QR && c.modW >= 21) mods.push_back(c.modW);
        }
        if (sides.empty()) return {0.f, 0};
        std::nth_element(sides.begin(), sides.begin() + sides.size() / 2, sides.end());
        int m = 0;
        if (!mods.empty()) {
            std::nth_element(mods.begin(), mods.begin() + mods.size() / 2, mods.end());
            m = mods[mods.size() / 2];
        }
        return {sides[sides.size() / 2], m};
    }

    // texts of the codes of one format read so far (the content the product's codes share comes
    // from them)
    std::vector<std::string> texts(int format)
    {
        std::lock_guard<std::mutex> lock(mu_);
        std::vector<std::string> out;
        for (const auto& kv : codes_)
            if (kv.second.format == format) out.push_back(kv.first);
        return out;
    }
    std::vector<std::string> qrTexts() { return texts(CR_QR); }

    // median side (pixels) and module count of the Data Matrix codes read so far; 0 = none
    std::pair<float, int> medianDm()
    {
        std::lock_guard<std::mutex> lock(mu_);
        std::vector<float> sides;
        std::vector<int> mods;
        for (const auto& kv : codes_) {
            const auto& c = kv.second;
            if (c.format != CR_DATAMATRIX || c.modW < 10 || c.modW != c.modH) continue;
            float s = 0.f;
            for (int k = 0; k < 4; ++k) s += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
            sides.push_back(s / 4.f);
            mods.push_back(c.modW);
        }
        if (sides.empty()) return {0.f, 0};
        std::nth_element(sides.begin(), sides.begin() + sides.size() / 2, sides.end());
        std::nth_element(mods.begin(), mods.begin() + mods.size() / 2, mods.end());
        return {sides[sides.size() / 2], mods[mods.size() / 2]};
    }

    bool has(const std::string& text)
    {
        std::lock_guard<std::mutex> lock(mu_);
        return codes_.count(text) > 0;
    }

    // true when a decoded code is the one region r (full-image coordinates) was proposed for:
    // its centre within 0.6 code sides of the region's centre (codes on a case can sit one side
    // apart, so anywhere inside the enlarged box is too loose); inside r when the side is unknown
    bool covers(const cv::Rect& r, float side = 0.f)
    {
        std::lock_guard<std::mutex> lock(mu_);
        const cv::Point2f rc(r.x + r.width / 2.f, r.y + r.height / 2.f);
        for (const auto& kv : codes_) {
            const auto& c = kv.second;
            const cv::Point2f mid((c.x[0] + c.x[2]) / 2.f, (c.y[0] + c.y[2]) / 2.f);
            if (side > 0.f ? cv::norm(mid - rc) < 0.6f * side : r.contains(cv::Point(static_cast<int>(mid.x), static_cast<int>(mid.y)))) return true;
        }
        return false;
    }

private:
    int expected_;
    int formats_, minLength_, maxLength_, checkDigit_;
    std::mutex mu_;
    std::map<std::string, Code> codes_;
    std::map<std::string, std::vector<cv::Point2f>> places_; // centres where each code was seen (copies)
    std::set<std::string> refusedPdf417_;
    std::set<std::string> confirmedAddon_; // main text, 0x1f, add-on: read again on the image (addonHolds)
    cv::Mat source_;
    cv::Point2f sourceOffset_;
    std::atomic<bool> done_{false};
};

// the same rule as Collector::covers, on a finished code list
bool containsCode(const cv::Rect& r, const std::vector<Code>& codes, float side = 0.f)
{
    const cv::Point2f rc(r.x + r.width / 2.f, r.y + r.height / 2.f);
    for (const auto& c : codes) {
        const cv::Point2f mid((c.x[0] + c.x[2]) / 2.f, (c.y[0] + c.y[2]) / 2.f);
        if (side > 0.f ? cv::norm(mid - rc) < 0.6f * side : r.contains(cv::Point(static_cast<int>(mid.x), static_cast<int>(mid.y)))) return true;
    }
    return false;
}

} // namespace

// The printer's QR encoding: learned once per reader from a decoded code, else from this image.
// An encoding loaded from a saved state is a hint: it is used until the first decoded code that
// yields an encoding confirms or replaces it (the printer may have changed between shifts).
static QrEncoding learnedEncoding(const std::vector<Code>& codes, StepStats* learned)
{
    const auto stored = [&]() {
        QrEncoding e;
        e.format = learned->encFormat.load();
        e.kind = learned->encKind.load();
        e.split = learned->encSplit.load();
        e.ecl = learned->encEcl.load();
        e.modules = learned->encModules.load();
        return e;
    };
    if (learned && learned->encKind.load() >= 0 && (learned->encConfirmed.load() || codes.empty())) return stored();
    for (const auto& c : codes) {
        const QrEncoding e = c.format == CR_DATAMATRIX ? learnDmEncoding(c) : learnQrEncoding(c);
        if (!e.valid()) continue;
        if (learned) {
            learned->encFormat.store(e.format);
            learned->encSplit.store(e.split);
            learned->encEcl.store(e.ecl);
            learned->encModules.store(e.modules);
            learned->encKind.store(e.kind);
            learned->encConfirmed.store(1);
        }
        return e;
    }
    if (learned && learned->encKind.load() >= 0) return stored();
    return {};
}

// The codes not yet read, when the codes read form a serial run (one text prefix, a numeric
// suffix of one width, a span of a few times the case count): the gaps in the run, and a few
// past either end for the codes still missing. Empty when nothing is missing or there is no run.
static std::vector<std::string> serialRunGaps(const std::vector<Code>& codes, int expected, int format)
{
    std::map<std::pair<std::string, size_t>, std::vector<long long>> groups;
    for (const auto& c : codes) {
        if (c.format != format) continue;
        size_t k = c.text.size();
        while (k > 0 && std::isdigit(static_cast<unsigned char>(c.text[k - 1]))) --k;
        const size_t width = c.text.size() - k;
        if (width == 0 || width > 18) continue;
        groups[{c.text.substr(0, k), width}].push_back(std::stoll(c.text.substr(k)));
    }
    const std::vector<long long>* run = nullptr;
    std::pair<std::string, size_t> key;
    for (const auto& kv : groups)
        if (!run || kv.second.size() > run->size()) {
            run = &kv.second;
            key = kv.first;
        }
    if (!run || run->size() < 3 || expected <= 0) return {};
    const long long missing = expected - static_cast<long long>(codes.size());
    if (missing <= 0) return {};
    const auto [mn, mx] = std::minmax_element(run->begin(), run->end());
    const long long span = *mx - *mn;
    if (span > 4LL * expected + 20) return {}; // random serials: no run to complete
    const std::set<long long> have(run->begin(), run->end());
    long long maxValue = 1;
    for (size_t i = 0; i < key.second && maxValue < 1000000000000000000LL / 10; ++i) maxValue *= 10;
    std::vector<std::string> out;
    for (long long v = std::max(0LL, *mn - missing - 1); v <= std::min(maxValue - 1, *mx + missing + 1); ++v) {
        if (have.count(v)) continue;
        std::string digits = std::to_string(v);
        if (digits.size() < key.second) digits.insert(0, key.second - digits.size(), '0');
        out.push_back(key.first + digits);
    }
    return out;
}

// centres and median side of decoded codes (the slot map's fixture)
static void centresOf(const std::vector<Code>& codes, std::vector<cv::Point2f>& centres, float* side)
{
    std::vector<float> sides;
    for (const auto& c : codes) {
        centres.emplace_back((c.x[0] + c.x[2]) / 2.f, (c.y[0] + c.y[2]) / 2.f);
        float sd = 0.f;
        for (int k = 0; k < 4; ++k) sd += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
        sides.push_back(sd / 4.f);
    }
    if (side) {
        std::sort(sides.begin(), sides.end());
        *side = sides.empty() ? 0.f : sides[sides.size() / 2];
    }
}

std::vector<size_t> pickMisses(const std::vector<cv::Rect>& boxes, const std::vector<float>& sides,
                               const std::vector<float>& scores, const std::vector<Code>& codes, int missing,
                               float symbolsPerCode, float sideLo, float sideHi)
{
    if (missing <= 0 || boxes.empty()) return {};
    auto at = [](const std::vector<float>& v, size_t i) { return i < v.size() ? v[i] : 0.f; };
    auto centre = [](const cv::Rect& r) { return cv::Point2f(r.x + r.width / 2.f, r.y + r.height / 2.f); };
    // the product's code size (the learned range of the AI's sides, with the finder's margin);
    // unknown: every region counts as the right size
    auto rightSize = [&](size_t i) {
        const float s = at(sides, i);
        return sideLo <= 0.f || (s > sideLo * 0.75f && s < sideHi * 1.35f);
    };
    // the codes' spacing: half the median distance from a read code to the next is still that
    // code's item (its label flag, a second copy, the label edge), not a code of its own. Codes
    // sit at least one pitch apart, a missing one where a code should be.
    std::vector<cv::Point2f> read;
    for (const auto& c : codes) read.emplace_back((c.x[0] + c.x[2]) / 2.f, (c.y[0] + c.y[2]) / 2.f);
    float nearRead = 0.f;
    if (read.size() >= 4) {
        std::vector<float> nn;
        for (size_t a = 0; a < read.size(); ++a) {
            float best = 1e9f;
            for (size_t b = 0; b < read.size(); ++b)
                if (a != b) best = std::min(best, static_cast<float>(cv::norm(read[a] - read[b])));
            nn.push_back(best);
        }
        std::nth_element(nn.begin(), nn.begin() + static_cast<long>(nn.size() / 2), nn.end());
        nearRead = 0.5f * nn[nn.size() / 2];
    }
    // the right size first, then the AI's confidence
    std::vector<size_t> order(boxes.size());
    for (size_t i = 0; i < order.size(); ++i) order[i] = i;
    std::stable_sort(order.begin(), order.end(), [&](size_t a, size_t b) {
        const bool ra = rightSize(a), rb = rightSize(b);
        return ra != rb ? ra : at(scores, a) > at(scores, b);
    });
    const int want = static_cast<int>(std::ceil(missing * std::max(1.f, symbolsPerCode)));
    const size_t cap = static_cast<size_t>(3 * want + 2);
    std::vector<size_t> out;
    for (size_t i : order) {
        if (out.size() >= cap) break;
        // a region of the wrong size only while too few of the right size are listed
        if (!rightSize(i) && static_cast<int>(out.size()) >= want) break;
        const cv::Point2f c = centre(boxes[i]);
        bool drop = false;
        for (const auto& q : read)
            if (cv::norm(c - q) < nearRead) { drop = true; break; }
        // the same code twice: half the smaller box inside a region already listed
        for (size_t k = 0; k < out.size() && !drop; ++k) {
            const cv::Rect& o = boxes[out[k]];
            drop = (o & boxes[i]).area() * 2 >= std::min(o.area(), boxes[i].area());
        }
        if (!drop) out.push_back(i);
    }
    return out;
}

// the second look is not a public step, but its wins are learned like one (pass order)
constexpr int kStepSecondLook = 19;

Result decode(const cv::Mat& fullGray, const cr_params& p, const Models& models, StepStats* learned)
{
    const auto t0 = Clock::now();
    Result res;
    res.stats.expected = p.expected_count;

    cv::Mat gray = fullGray;
    cv::Point2f roiOffset(0, 0);
    if (p.roi_w > 0 && p.roi_h > 0) {
        const cv::Rect roi = cv::Rect(p.roi_x, p.roi_y, p.roi_w, p.roi_h) & cv::Rect(0, 0, fullGray.cols, fullGray.rows);
        if (roi.area() > 0) {
            gray = fullGray(roi);
            roiOffset = cv::Point2f(static_cast<float>(roi.x), static_cast<float>(roi.y));
        }
    }

    // no known type (only unknown flags were set): read nothing; an empty zxing set would read every type
    if ((p.format & (kZxTypes | CR_PHARMACODE | CR_PHARMACODE_REV | CR_DOTCODE)) == 0) {
        res.stats.total_ms = static_cast<float>(msSince(t0));
        return res;
    }
    const ZXing::BarcodeFormats formats = toZx(p.format);
    Collector found(p);
    found.setSource(gray, roiOffset);
    // light-on-dark print (laser-marked caps, white ink on dark labels): the image is inverted once,
    // on a copy, so the locator and every decoder see dark modules on light (the strong decoders
    // assume it: on synthetic 2 px light-on-dark codes they read 2-4 of 18 where dark-on-light reads
    // 18, and the locator proposes three times the regions). The polarity is learned from zxing's
    // reads; a product's first case flips after the region pass when most reads came out inverted
    static const bool noPolarity = std::getenv("CR_NO_POLARITY") != nullptr; // A/B
    bool invertedImage = false;
    auto flipImage = [&]() {
        cv::Mat flipped;
        cv::bitwise_not(gray, flipped);
        gray = flipped;
        invertedImage = !invertedImage;
        found.flipped.store(invertedImage);
        if (std::getenv("CR_POLARITY")) std::fprintf(stderr, "polarity: image flipped (%d of %d zxing reads light on dark)\n", found.invertedReads.load(), found.zxReads.load());
    };
    if (!noPolarity && learned && learned->polarity.load() == 2 && is2D(p.format)) flipImage();

    // crops: one symbol each, no rotation search beyond what the decoder does itself
    ZXing::ReaderOptions cropOpts;
    cropOpts.setFormats(formats);
    cropOpts.setTryRotate(true);
    cropOpts.setTryDownscale(true);
    cropOpts.setTryHarder(true);
    cropOpts.setTextMode(ZXing::TextMode::Plain); // raw text: GS1 separators stay ASCII 29
    cropOpts.setEanAddOnSymbol(ZXing::EanAddOnSymbol::Read); // EAN/UPC add-ons, reported apart (Code::addon)
    // a region crop holds the product's symbols per label (a cans label carries its QR twice) and
    // perhaps a neighbour; zxing's default keeps searching for 255 symbols after the last read
    {
        const float spc = learned ? learned->symbolsPerCode.load() : 0.f;
        const int perCrop = spc > 0.f ? static_cast<int>(std::ceil(spc)) + 1 : 3;
        cropOpts.setMaxNumberOfSymbols(static_cast<uint8_t>(std::clamp(perCrop, 2, 8)));
    }
    // a second binarizer for lighting-flattened images: one global threshold suits them, where the
    // default local one sees film edges as module edges
    // a region of unknown size (a merged classical region) may hold several codes: no cap there
    ZXing::ReaderOptions cropOptsAll = cropOpts;
    cropOptsAll.setMaxNumberOfSymbols(255);
    ZXing::ReaderOptions globalOpts = cropOpts;
    globalOpts.setBinarizer(ZXing::Binarizer::GlobalHistogram);
    // already binary images (fixed levels below): no binarizer of the decoder's own
    ZXing::ReaderOptions boolOpts = cropOpts;
    boolOpts.setBinarizer(ZXing::Binarizer::BoolCast);
    // After deblurring, bled print (dark modules spread into the light ones, worst where the lens
    // blurs the case corners) defeats the decoders' own thresholds: a few fixed levels between the
    // dark and light ink, on a 2x upscale, read it (crate caps: 3 of 6 more).
    // modulePx > 0 (module size in img pixels): then a local level as well, the mean over 3
    // modules. A label on a shiny can rim is lit unevenly, so no one level fits all of it (24 and
    // 48 QR crops, 2026-10-31: 3 blurred labels read this way after a Wiener deblur, none by the
    // fixed levels and none by the local level without the deblur).
    auto readLevels = [&](const cv::Mat& img, float modulePx = 0.f) {
        cv::Mat u;
        cv::resize(img, u, cv::Size(), 2.0, 2.0, cv::INTER_CUBIC);
        std::vector<uint8_t> v(u.begin<uint8_t>(), u.end<uint8_t>());
        std::nth_element(v.begin(), v.begin() + v.size() / 20, v.end());
        const double lo = v[v.size() / 20];
        std::nth_element(v.begin(), v.begin() + v.size() * 19 / 20, v.end());
        const double hi = v[v.size() * 19 / 20];
        for (double level : {0.4, 0.5, 0.3}) {
            cv::Mat bw;
            cv::threshold(u, bw, lo + (hi - lo) * level, 255, cv::THRESH_BINARY);
            auto codes = read(bw, boolOpts);
            if (!codes.empty()) return std::make_pair(codes, 2.0f);
        }
        if (modulePx > 0.f) {
            cv::Mat bw;
            cv::adaptiveThreshold(u, bw, 255, cv::ADAPTIVE_THRESH_MEAN_C, cv::THRESH_BINARY,
                                  std::max(5, static_cast<int>(std::lround(6.f * modulePx))) | 1, 0);
            auto codes = read(bw, boolOpts);
            if (!codes.empty()) return std::make_pair(codes, 2.0f);
        }
        return std::make_pair(ZXing::Barcodes{}, 2.0f);
    };

    // ---- FIND: every code in the case first, then decode only those regions.
    // The case's priors decide how hard to look: N codes expected (99.9% of cases hold exactly N),
    // all the same type and size. Too few found -> look harder; regions of the wrong size are not
    // codes and are dropped, so no time is spent on background.
    std::vector<cv::Rect> cands;
    std::vector<float> candSide;
    std::vector<float> candScore; // the AI's confidence (mean logit); 0 for classical regions
    size_t aiCount = 0; // the first aiCount regions came from the AI locator
    if (is2D(p.format)) {
        cv::dnn::Net* net = models.locator;
        int mode = p.locator_mode;
        if (mode == CR_LOCATE_AUTO) mode = net ? CR_LOCATE_AI_CLASSICAL : CR_LOCATE_CLASSICAL;
        if (!net) mode = CR_LOCATE_CLASSICAL;
        // expected count from the product master; an implausible value means "unknown"
        const int N = p.expected_count > 500 ? 0 : p.expected_count;
        // the extra search passes may use at most 30% of the time budget: decoding needs the rest
        auto searchTime = [&]() { return msSince(t0) < p.time_budget_ms * 0.3; };
        // learned size range of this product's codes, with a margin
        const float lo = learned ? learned->codeMin.load() : 0.f, hi = learned ? learned->codeMax.load() : 0.f;
        auto sizeOk = [&](float side) { return lo <= 0.f || (side > lo * 0.75f && side < hi * 1.35f); };
        std::vector<Found> areas;
        // main pass: keep every region; one smaller than this product's codes is a fragment (glare
        // or film hides part of the code), so its crop grows to the full code size around it.
        // Extra search passes: regions of the wrong size are background and are dropped.
        std::vector<Found> grownAreas; // tried after every other region
        auto addFound = [&](std::vector<Found> more, bool mainPass) {
            for (auto& f : more) {
                if (!mainPass && !sizeOk(f.side)) continue;
                if (mainPass && lo > 0.f && f.side < lo * 0.75f) {
                    // keep the tight crop (it may read as it is) and add one grown to full code size
                    Found grown = f;
                    const int half = static_cast<int>(hi * 0.9f);
                    const cv::Point c(static_cast<int>(f.rotated.center.x), static_cast<int>(f.rotated.center.y));
                    grown.box = (f.box | cv::Rect(c.x - half, c.y - half, 2 * half, 2 * half)) & cv::Rect(0, 0, gray.cols, gray.rows);
                    grown.side = lo;
                    areas.push_back(f);
                    grownAreas.push_back(grown);
                    continue;
                }
                // the same code found twice: centres within 0.6 code sides (codes on a case can sit
                // one side apart, so "inside the other's box" merged neighbours)
                bool dup = false;
                for (const auto& g : areas)
                    if (cv::norm(g.rotated.center - f.rotated.center) < 0.6f * std::max(1.f, std::min(g.side, f.side))) { dup = true; break; }
                if (!dup) areas.push_back(f);
            }
        };
        // areas that can be codes of this case: about the size of the confident ones (a wrinkle or
        // a speck found among them is far smaller). Counting every area let a wrinkled 40-code case
        // reach 40 with four specks, so the finer look that finds the missed codes ran only in the
        // search-harder pass, 200+ ms later
        static const bool countAll = std::getenv("CR_COUNT_ALL") != nullptr; // A/B: the old count
        const auto plausible = [&]() {
            if (countAll) return static_cast<int>(areas.size());
            std::vector<float> sides;
            for (const auto& a : areas)
                if (a.score > 1.f && a.side > 0.f) sides.push_back(a.side);
            if (sides.size() < 3) return static_cast<int>(areas.size());
            std::nth_element(sides.begin(), sides.begin() + sides.size() / 2, sides.end());
            const float med = sides[sides.size() / 2];
            int n = 0;
            for (const auto& a : areas) n += a.side <= 0.f || (a.side > 0.6f * med && a.side < 1.8f * med);
            return n;
        };
        if (mode == CR_LOCATE_AI || mode == CR_LOCATE_AI_CLASSICAL) {
            addFound(locateAI(*net, gray, p.ai_threshold, &res.stats.label_brightness, 8), true);
            // fewer than N: look harder - a lower threshold, then twice the resolution
            if (N > 0 && plausible() < N && searchTime())
                addFound(locateAI(*net, gray, std::min(p.ai_threshold, 0.15f), nullptr, 8), false);
            if (N > 0 && plausible() < N && searchTime())
                addFound(locateAI(*net, gray, p.ai_threshold, nullptr, 4), false);
            std::stable_sort(areas.begin(), areas.end(), [](const Found& a, const Found& b) { return a.score > b.score; });
            // enough found: the N x symbols-per-code most confident plus a margin; the rest is
            // background. Nothing is cut before the product's symbols-per-code is learned.
            const float spc = learned ? learned->symbolsPerCode.load() : 0.f;
            if (N > 0 && spc > 0.f) {
                const int want = static_cast<int>(std::ceil(N * spc));
                size_t keep = static_cast<size_t>(want + std::max(3, want / 8));
                // ... but a region the locator is confident about is never dropped: on a wrinkled
                // case the wrinkles can outscore a real code
                while (keep < areas.size() && areas[keep].score > 1.f) ++keep;
                if (areas.size() > keep) areas.resize(keep);
            }
            std::stable_sort(grownAreas.begin(), grownAreas.end(), [](const Found& a, const Found& b) { return a.score > b.score; });
            areas.insert(areas.end(), grownAreas.begin(), grownAreas.end());
        }
        for (const auto& f : areas) {
            cands.push_back(f.box);
            candSide.push_back(f.side);
            candScore.push_back(f.score);
        }
        aiCount = cands.size();
        // classical regions only when the AI found fewer than N (or there is no AI model)
        const bool needMore = N <= 0 ? mode != CR_LOCATE_AI : static_cast<int>(aiCount) < N;
        if ((mode == CR_LOCATE_CLASSICAL || mode == CR_LOCATE_AI_CLASSICAL) && needMore && (aiCount == 0 || searchTime())) {
            const size_t cap = aiCount == 0 || N <= 0 ? SIZE_MAX : static_cast<size_t>(std::max(10, 4 * (N - static_cast<int>(aiCount))));
            size_t added = 0;
            for (const auto& r : locate(gray, p, &res.stats.label_brightness)) {
                if (added >= cap) break;
                const cv::Point c(r.x + r.width / 2, r.y + r.height / 2);
                bool dup = false;
                for (size_t k = 0; k < aiCount; ++k) {
                    const cv::Point2f kc(cands[k].x + cands[k].width / 2.f, cands[k].y + cands[k].height / 2.f);
                    if (candSide[k] > 0.f ? cv::norm(kc - cv::Point2f(c)) < 0.6f * candSide[k] : cands[k].contains(c)) { dup = true; break; }
                }
                if (dup) continue;
                cands.push_back(r);
                candSide.push_back(0.f);
                candScore.push_back(0.f);
                ++added;
            }
        }
    }
    // which regions came from the AI locator (the search-harder pass adds more later)
    std::vector<char> isAi(cands.size(), 0);
    for (size_t i = 0; i < aiCount && i < cands.size(); ++i) isAi[i] = 1;
    size_t slotFrom = SIZE_MAX; // the first region placed by the slot lattice, not by a locator (CR_CANDS)
    res.stats.candidates = static_cast<int>(cands.size());
    res.stats.locate_ms = static_cast<float>(msSince(t0));
    if (models.onFound) {
        std::vector<FoundRegion> all;
        all.reserve(cands.size());
        for (size_t i = 0; i < cands.size(); ++i)
            all.push_back({cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y)), i < aiCount, false});
        try {
            models.onFound(all);
        } catch (...) {
            // the live view must never stop a decode
        }
    }

    const auto t1 = Clock::now();
    int nThreads = p.threads > 0 ? p.threads : std::max(1u, std::thread::hardware_concurrency());
    if (models.pool) nThreads = std::min(nThreads, models.pool->size());
    // a parallel pass: the same callable on every worker (with its index), waited for. On the
    // reader's persistent pool when there is one; else threads per pass (tests, tools)
    auto runAll = [&](const std::function<void(int)>& fn) {
        if (models.pool) {
            models.pool->runAll(nThreads, fn);
            return;
        }
        std::vector<std::thread> threads;
        try {
            for (int t = 0; t < nThreads; ++t) threads.emplace_back(fn, t);
        } catch (...) { // out of threads: join those started (a joinable thread destroyed ends the process)
            for (auto& th : threads) th.join();
            throw;
        }
        for (auto& th : threads) th.join();
    };
    std::atomic<size_t> next{0};
    std::vector<char> cropRead(cands.size(), 0);
    std::vector<char> blurredRegion(cands.size(), 0);
    std::vector<char> planDone(cands.size(), 0); // the first pass ran every fix of its plan on it
    // per-pass profile (CR_PROF=1): elapsed since the pass before, codes read so far
    const bool prof = std::getenv("CR_PROF") != nullptr;
    auto profLast = Clock::now();
    auto profMark = [&](const char* pass) {
        if (!prof) return;
        const auto now = Clock::now();
        std::fprintf(stderr, "prof %-12s %6.0f ms  read %3d  regions %zu\n", pass,
                     std::chrono::duration<double, std::milli>(now - profLast).count(), found.count(), cands.size());
        profLast = now;
    };
    if (prof) std::fprintf(stderr, "prof %-12s %6.0f ms  regions %zu\n", "locate", msSince(t0), cands.size());
    // From here the passes run on every core themselves: OpenCV fanning out inside each worker
    // only costs (measured: 18% of the CPU per case for nothing). The locator's forward pass
    // before this point keeps OpenCV's threads (16 ms against 29 single-threaded).
    // OpenCV runs a parallel_for_ on the calling thread alone while another one is open anywhere
    // in the process, so a helper thread holds an empty one-item region open while the passes run
    // (counted: two readers decoding at once share it; the last to leave closes it). Never
    // cv::setNumThreads here: with the TBB backend it re-creates the thread arena, and a thread
    // inside cv::parallel_for_ at that moment (a text tool on the other camera of the job)
    // crashed the whole process (SIGBUS, two-camera job, 2026-10-30).
    struct CvSerialGuard {
        struct Holder {
            std::mutex m;
            std::condition_variable cv;
            int depth = 0;
            bool open = false;
            Holder()
            {
                std::thread([this] {
                    std::unique_lock<std::mutex> lock(m);
                    for (;;) {
                        cv.wait(lock, [&] { return depth > 0; });
                        lock.unlock();
                        cv::parallel_for_(cv::Range(0, 1), [&](const cv::Range&) {
                            std::unique_lock<std::mutex> inside(m);
                            open = true;
                            cv.notify_all();
                            cv.wait(inside, [&] { return depth == 0; });
                            open = false;
                        });
                        lock.lock();
                    }
                }).detach();
            }
        };
        // never destroyed: its thread waits for the next decode until the process ends
        static Holder& holder() { static Holder* h = new Holder(); return *h; }
        CvSerialGuard()
        {
            auto& h = holder();
            std::unique_lock<std::mutex> lock(h.m);
            if (h.depth++ == 0) h.cv.notify_all();
            h.cv.wait(lock, [&] { return h.open; });
        }
        ~CvSerialGuard()
        {
            auto& h = holder();
            std::lock_guard<std::mutex> lock(h.m);
            if (--h.depth == 0) h.cv.notify_all();
        }
    } cvSerialGuard;
    // per step: thread time spent and calls (CR_PROF=1), over the per-region passes
    std::array<std::atomic<long long>, StepStats::kMax> stepUs{};
    std::array<std::atomic<int>, StepStats::kMax> stepCalls{}, stepWins{};
    auto profStep = [&](int step, Clock::time_point from, bool win) {
        if (!prof || step < 0 || step >= StepStats::kMax) return;
        stepUs[static_cast<size_t>(step)] += std::chrono::duration_cast<std::chrono::microseconds>(Clock::now() - from).count();
        stepCalls[static_cast<size_t>(step)]++;
        if (win) stepWins[static_cast<size_t>(step)]++;
    };
    // a code at c (full image) is region i's own code when it lies within 0.6 code sides of the
    // region's centre; codes on a case sit one side apart, so a neighbour inside an enlarged crop
    // must not close the region. A region of unknown size owns any code.
    auto nearRegion = [&](size_t i, cv::Point2f c) {
        const float side = i < candSide.size() ? candSide[i] : 0.f;
        if (side <= 0.f) return true;
        const cv::Point2f mid(roiOffset.x + cands[i].x + cands[i].width / 2.f, roiOffset.y + cands[i].y + cands[i].height / 2.f);
        return cv::norm(c - mid) < 0.6f * side;
    };
    // flattened lighting comes early: uneven light inside a code is the commonest failure on
    // the sample lines (film, foil edges, shadows)
    // glare correction last: on the sample lines the earlier steps read what it reads, so it
    // runs only after they fail; on a line where it wins, the learned order moves it forward
    // the dot filters last as well: solid print never needs them; a dot-printed product moves them up
    static const int kDefault[] = {CR_STEP_PLAIN, CR_STEP_FLAT, CR_STEP_UPSCALE, CR_STEP_CLAHE, CR_STEP_DEBLUR,
                                   CR_STEP_SHARPEN, CR_STEP_BINARY, CR_STEP_FLAT_ADAPT, CR_STEP_DEBLUR_STRONG, CR_STEP_GLARE, CR_STEP_DOTS};
    // learned order: plain stays first (cheapest); the rest by how often each read a code that
    // the steps before it could not, smoothed so an untried step still gets its turn
    std::vector<int> kSteps(std::begin(kDefault), std::end(kDefault));
    static const bool noDots = std::getenv("CR_NO_DOTS") != nullptr; // A/B
    if (noDots) kSteps.erase(std::remove(kSteps.begin(), kSteps.end(), CR_STEP_DOTS), kSteps.end());
    // a product whose Data Matrix size is known (earlier cases): the lattice decoder becomes a step
    // like the others, so it moves to the front on lines where it reads what the others cannot
    // the first case of a product: what calibration measured seeds what later cases learn, so the
    // lattice runs on the first Data Matrix case and the grid sizes are known from the start
    if (learned) {
        if ((p.format & CR_DATAMATRIX) && learned->dmModules.load() == 0 && p.hint_modules >= 10 && p.hint_modules <= 26 && p.hint_modules % 2 == 0 && p.hint_code_side_px > 0.f) {
            learned->dmModules.store(p.hint_modules);
            learned->dmSide.store(p.hint_code_side_px);
        }
        if ((p.format & CR_QR) && learned->qrModules.load() == 0 && p.hint_modules >= 21 && (p.hint_modules - 21) % 4 == 0) learned->qrModules.store(p.hint_modules);
        if (learned->symbolsPerCode.load() <= 0.f && p.hint_symbols_per_code > 0.f) learned->symbolsPerCode.store(p.hint_symbols_per_code);
    }
    const int dmN = learned ? learned->dmModules.load() : 0;
    const float dmSideLearned = learned ? learned->dmSide.load() : 0.f;
    if ((p.format & CR_DATAMATRIX) && dmN >= 10 && dmSideLearned > 0.f) kSteps.push_back(CR_STEP_LATTICE);
    if (learned) {
        auto score = [&](int s) {
            const double w = learned->wins[static_cast<size_t>(s)].load(), t = learned->tries[static_cast<size_t>(s)].load();
            return (w + 1.0) / (t + 2.0);
        };
        std::stable_sort(kSteps.begin() + 1, kSteps.end(), [&](int a, int b) { return score(a) > score(b); });
    }
    // steps that (almost) never read on this product are skipped, as a trained HALCON model
    // searches only what the samples needed; every 20th case tries them all again, so a change on
    // the line (new labels, other light) is noticed
    const bool probe = !learned || learned->cases.fetch_add(1) % 20 == 0;
    auto useless = [&](int s) {
        if (probe || !learned || s == CR_STEP_PLAIN) return false;
        const uint32_t t = learned->tries[static_cast<size_t>(s)].load(), w = learned->wins[static_cast<size_t>(s)].load();
        return t >= 60 && w * 100 < t;
    };
    // per zone: once a zone has enough history, its own order and its own pruning
    constexpr int kZ = StepStats::kZones;
    auto zoneOf = [&](const cv::Rect& r) {
        const float cx = roiOffset.x + r.x + r.width / 2.f, cy = roiOffset.y + r.y + r.height / 2.f;
        const int zx = std::clamp(static_cast<int>(cx * kZ / fullGray.cols), 0, kZ - 1);
        const int zy = std::clamp(static_cast<int>(cy * kZ / fullGray.rows), 0, kZ - 1);
        return zy * kZ + zx;
    };
    std::vector<std::vector<int>> zoneSteps(static_cast<size_t>(kZ * kZ), kSteps);
    std::vector<char> zoneKnown(static_cast<size_t>(kZ * kZ), 0);
    if (learned)
        for (int z = 0; z < kZ * kZ; ++z) {
            const auto& zt = learned->zoneTries[static_cast<size_t>(z)];
            const auto& zw = learned->zoneWins[static_cast<size_t>(z)];
            uint32_t total = 0;
            for (int s : kSteps) total += zt[static_cast<size_t>(s)].load();
            if (total < 30) continue;
            zoneKnown[static_cast<size_t>(z)] = 1;
            auto zscore = [&](int s) { return (zw[static_cast<size_t>(s)].load() + 1.0) / (zt[static_cast<size_t>(s)].load() + 2.0); };
            auto& ks = zoneSteps[static_cast<size_t>(z)];
            std::stable_sort(ks.begin() + 1, ks.end(), [&](int a, int b) { return zscore(a) > zscore(b); });
        }
    auto uselessIn = [&](int s, int z) {
        if (probe || !learned || s == CR_STEP_PLAIN) return false;
        if (zoneKnown[static_cast<size_t>(z)]) {
            const uint32_t t = learned->zoneTries[static_cast<size_t>(z)][static_cast<size_t>(s)].load();
            const uint32_t w = learned->zoneWins[static_cast<size_t>(z)][static_cast<size_t>(s)].load();
            if (t >= 20) return w * 100 < t; // the zone decides: a fix useless elsewhere may be the one here
        }
        return useless(s);
    };
    auto countTry = [&](int s, int z, bool win) {
        if (!learned) return;
        learned->tries[static_cast<size_t>(s)]++;
        learned->zoneTries[static_cast<size_t>(z)][static_cast<size_t>(s)]++;
        if (win) {
            learned->wins[static_cast<size_t>(s)]++;
            learned->zoneWins[static_cast<size_t>(z)][static_cast<size_t>(s)]++;
        }
    };
    bool latticeFirst = false;
    if (learned && kSteps.size() > 1 && kSteps[1] == CR_STEP_LATTICE &&
        learned->tries[static_cast<size_t>(CR_STEP_LATTICE)].load() >= 10)
        latticeFirst = true;

    const bool haveEnhancer = models.enhancers && !models.enhancers->empty() && p.enhancer_mode != CR_ENHANCE_OFF;
    const bool inlineEnhance = haveEnhancer && (p.enhancer_mode == CR_ENHANCE_LAST || p.enhancer_mode == CR_ENHANCE_EARLY);
    // run the enhancer on one region; true when it read a code
    // a cv::dnn::Net is not thread safe: there is one per core (cr_load_enhancer), and with more decode
    // threads than nets (cr_params.threads over the core count) two threads share one, so each net is
    // used under its own lock (uncontended while every thread has its own net)
    std::unique_ptr<std::mutex[]> enhancerLocks(haveEnhancer ? new std::mutex[models.enhancers->size()] : nullptr);
    auto tryEnhance = [&](size_t i, const cv::Mat& crop, int threadIndex) {
        const size_t k = static_cast<size_t>(threadIndex) % models.enhancers->size();
        cv::Mat clean;
        {
            std::lock_guard<std::mutex> lock(enhancerLocks[k]);
            clean = enhanceAI((*models.enhancers)[k], crop);
        }
        const auto codes = read(clean, cropOpts);
        const cv::Point2f off(roiOffset.x + cands[i].x, roiOffset.y + cands[i].y);
        bool mine = false;
        for (const auto& b : codes) {
            cv::Point2f ctr;
            found.add(b, CR_STEP_AI_ENHANCE, off, 1.f, &ctr);
            mine = mine || nearRegion(i, ctr);
        }
        return mine;
    };
    // libdmtx on the deblurred region (defocused caps); AI regions only, so false regions from
    // the classical locator never spend this time
    // libdmtx corrects up to its symbols' full capacity, and a 10 x 10 symbol has 5 check codewords:
    // dot texture next to a real code once read as "Y62" (synthetic dot-print scenes, main
    // 2026-10-29). A libdmtx read must look like the case's Data Matrix codes once two are known
    // (length, digits) and be at least 60% of their size (learned, else the codes read so far)
    auto dmtxPlausible = [&](const std::string& text, const cv::Point2f q[4]) {
        const std::vector<std::string> known = found.texts(CR_DATAMATRIX);
        if (known.size() >= 2 && !plausibleAmong(text, known)) return false;
        float ref = dmSideLearned;
        if (ref <= 0.f) {
            std::vector<float> sides;
            for (const auto& c : found.take())
                if (c.format == CR_DATAMATRIX) {
                    float sd = 0.f;
                    for (int k = 0; k < 4; ++k) sd += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
                    sides.push_back(sd / 4.f);
                }
            if (sides.size() >= 2) {
                std::nth_element(sides.begin(), sides.begin() + static_cast<long>(sides.size() / 2), sides.end());
                ref = sides[sides.size() / 2];
            }
        }
        float side = 0.f;
        for (int k = 0; k < 4; ++k) side += static_cast<float>(cv::norm(q[(k + 1) % 4] - q[k])) / 4.f;
        return ref <= 0.f || side >= 0.6f * ref;
    };
    auto tryDmtx = [&](size_t i, const cv::Mat& crop) {
#ifdef CR_HAVE_DMTX
        if (!(p.format & CR_DATAMATRIX) || !isAi[i] || found.done()) return false;
        const int left = p.time_budget_ms - static_cast<int>(msSince(t0));
        if (left <= 10) return false;
        std::string text;
        cv::Point2f q[4];
        std::string symId;
        if (!readDmtx(deblur(crop, 2.0), std::min(left, 60), text, q, &symId) || !dmtxPlausible(text, q)) return false;
        Code c;
        c.text = text;
        c.symbologyId = symId;
        c.gs1 = symId == "]d2" ? 1 : 0;
        c.format = CR_DATAMATRIX;
        c.step = CR_STEP_DMTX;
        for (auto& pt : q) pt += cv::Point2f(roiOffset.x + cands[i].x, roiOffset.y + cands[i].y);
        found.addCode(std::move(c), q);
        return true;
#else
        (void)i;
        (void)crop;
        return false;
#endif
    };
    // libdmtx is slow (up to 60-80 ms a region) and reads few codes on most lines: a learned step
    // like the others, so it is skipped where it never wins (re-checked every 20th case)
    auto learnedDmtx = [&](size_t i, const cv::Mat& crop, int zone) {
        if (!(p.format & CR_DATAMATRIX) || !isAi[i]) return false;
        const bool ok = tryDmtx(i, crop);
        countTry(CR_STEP_DMTX, zone, ok);
        return ok;
    };
    // the first pass may spend 45% of the budget: the passes after it (known content, lattice,
    // match, second look, grid fit) read what the image fixes cannot, and on a tight budget they
    // must get their turn, or their wins are never learned for the next case
    double passLimit = p.time_budget_ms * 0.45;
    auto worker = [&](int threadIndex) {
        for (;;) {
            if (found.done() || msSince(t0) > passLimit) return;
            const size_t i = next.fetch_add(1);
            if (i >= cands.size()) return;
            // a region around a code that is already read: nothing new to find
            if (found.covers(cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y)), i < candSide.size() ? candSide[i] : 0.f)) {
                cropRead[i] = 1;
                continue;
            }
            const cv::Mat crop = gray(cands[i]);
            // pick the fixes this region needs first (calibrated on the sample lines:
            // read codes measure sharpness 0.26-0.74, missed blurred caps about 0.20)
            // Heavy fixes (deblur, enhancer, libdmtx) only where the AI sees a code: the classical
            // locator's extra regions are mostly background, which also looks blurred and dull.
            const bool aiRegion = isAi[i] || aiCount == 0;
            const RegionLook look = lookAt(crop);
            // the lens blurs the case corners: far from the image centre, deblur early as well
            const cv::Point2f mid(roiOffset.x + cands[i].x + cands[i].width / 2.f, roiOffset.y + cands[i].y + cands[i].height / 2.f);
            const float off = std::hypot(mid.x - fullGray.cols / 2.f, mid.y - fullGray.rows / 2.f) /
                              std::hypot(fullGray.cols / 2.f, fullGray.rows / 2.f);
            const bool blurred = aiRegion && (look.sharpness < 0.32f || (off > 0.55f && look.sharpness < 0.45f));
            blurredRegion[i] = blurred ? 1 : 0;
            const bool dull = aiRegion && look.contrast < 20.f;
            std::vector<int> plan;
            plan.push_back(CR_STEP_PLAIN);
            // the step that reads most on this product goes right after the plain read when it is
            // the lattice decoder (cap labels: the others mostly fail there and cost the time)
            if (latticeFirst && aiRegion) plan.push_back(CR_STEP_LATTICE);
            const int zone = zoneOf(cands[i]);
            // a zone with history follows what worked there; otherwise the region's look decides
            if (!zoneKnown[static_cast<size_t>(zone)]) {
                if (blurred && look.sharpness < 0.24f) { plan.push_back(CR_STEP_DEBLUR_STRONG); plan.push_back(CR_STEP_DEBLUR); }
                else if (blurred) { plan.push_back(CR_STEP_DEBLUR); plan.push_back(CR_STEP_DEBLUR_STRONG); }
                if (dull) { plan.push_back(CR_STEP_FLAT); plan.push_back(CR_STEP_CLAHE); }
            }
            // a small region means small modules (shrink-wrapped cases: about 2.3 px): 4x Lanczos
            const bool small = aiRegion && std::min(crop.cols, crop.rows) < 260;
            if (small && !zoneKnown[static_cast<size_t>(zone)]) plan.push_back(CR_STEP_UPSCALE4);
            for (int s : zoneSteps[static_cast<size_t>(zone)]) {
                const bool heavy = s == CR_STEP_DEBLUR || s == CR_STEP_DEBLUR_STRONG || s == CR_STEP_UPSCALE4;
                if ((!heavy || aiRegion) && std::find(plan.begin(), plan.end(), s) == plan.end()) plan.push_back(s);
            }
            plan.erase(std::remove_if(plan.begin(), plan.end(), [&](int s) { return uselessIn(s, zone); }), plan.end());
            bool dmtxTried = false;
            for (int step : plan) {
                if (found.done() || msSince(t0) > passLimit) return;
                if (step == CR_STEP_LATTICE) {
                    if (!aiRegion) continue;
                    const cv::Point mid(cands[i].x + cands[i].width / 2, cands[i].y + cands[i].height / 2);
                    // at least 2 code sides each way: the AI box can sit a whole side off the code
                    // (a cap's label), and the lattice re-centres on the code it finds inside
                    const int r = static_cast<int>(std::max(std::max(cands[i].width, cands[i].height) * 0.75f, dmSideLearned * 2.f));
                    const cv::Rect big = cv::Rect(mid.x - r, mid.y - r, 2 * r, 2 * r) & cv::Rect(0, 0, gray.cols, gray.rows);
                    const int left = p.time_budget_ms - static_cast<int>(msSince(t0));
                    std::string text;
                    cv::Point2f q[4];
                    const std::vector<std::string> knownDm = found.texts(CR_DATAMATRIX);
                    const auto tLat = Clock::now();
                    OwnReadScope ids;
                    const bool got = left > 30 && latticeDm(gray(big), dmSideLearned, dmN, std::min(left, std::max(80, p.time_budget_ms / 12)), text, q, &knownDm);
                    profStep(step, tLat, got);
                    countTry(step, zone, got);
                    if (got) {
                        Code c;
                        c.text = text;
                        c.format = CR_DATAMATRIX;
                        c.step = CR_STEP_LATTICE;
                        c.modW = c.modH = dmN;
                        ids.apply(c);
                        for (auto& pt : q) pt += cv::Point2f(roiOffset.x + big.x, roiOffset.y + big.y);
                        found.addCode(std::move(c), q);
                        if (nearRegion(i, (q[0] + q[2]) * 0.5f)) cropRead[i] = 1;
                        break;
                    }
                    continue;
                }
                if ((step == CR_STEP_UPSCALE || step == CR_STEP_SHARPEN) && p.max_upscale < 2) continue;
                const auto tStep = Clock::now();
                // modules of 3.5 px or more need no 2x upscale (a quarter of the work on a large
                // label crop); the module size comes from the codes read, else the learned count
                const float cside = i < candSide.size() ? candSide[i] : 0.f;
                const int modsNow = found.medianQr().second > 0 ? found.medianQr().second : learned ? learned->qrModules.load() : 0;
                // the threshold steps (binary, sharpen) keep their 2x: their result changes with it
                const bool lightingStep = step == CR_STEP_FLAT || step == CR_STEP_FLAT_ADAPT || step == CR_STEP_UPSCALE;
                const int maxUp = lightingStep && cside > 0.f && modsNow > 0 && cside / modsNow >= 3.5f ? 1 : p.max_upscale;
                // the deblur width from the lens blur learned in this zone (modules) and this
                // region's module size; before any measurement the fixed widths apply. A region of
                // unknown size (the classical locator's) takes the side of the codes read in this
                // image: every code of a case has one size (24 QR, 2026-10-31: the unread codes
                // there were all such regions, so they had no module size for the measured width
                // or the local level of readLevels).
                // QR only: on the Data Matrix caps the same widths and the local level read 4
                // wrong texts (48 DM, 2026-10-31), so Data Matrix keeps the region's own size.
                float deblurPx = 0.f;
                const bool qrOnly = !(p.format & CR_DATAMATRIX);
                const float codeSide = cside > 0.f || !qrOnly ? cside : found.medianQr().first;
                const float modPx = codeSide > 0.f && modsNow > 0 ? codeSide / static_cast<float>(modsNow) : 0.f;
                const float levelPx = qrOnly ? modPx : 0.f; // the local level of readLevels
                static const bool fixedDeblur = std::getenv("CR_DEBLUR_FIXED") != nullptr; // A/B
                if (learned && !fixedDeblur && (step == CR_STEP_DEBLUR || step == CR_STEP_DEBLUR_STRONG) && modPx > 0.f) {
                    const float z = learned->psfSigma[static_cast<size_t>(zone)].load(), all = learned->psfSigmaAll.load();
                    const float mod = z > 0.f ? z : all;
                    if (mod > 0.f) deblurPx = mod * modPx;
                }
                cv::Mat img;
                ZXing::Barcodes codes;
                if (step == CR_STEP_DOTS) {
                    // three cell-filter variants at the module size: the region's side over the
                    // product's modules (learned, else typical); img is the one that read
                    const int n = (p.format & CR_DATAMATRIX) ? (dmN > 0 ? dmN : 16) : (modsNow > 0 ? modsNow : 21);
                    const float m = cside > 0.f ? cside / static_cast<float>(n) : std::min(crop.cols, crop.rows) / 2.5f / static_cast<float>(n);
                    for (int v = 0; codes.empty() && v < 3; ++v) {
                        img = dotFilter(crop, m, v);
                        codes = read(img, cside > 0.f ? cropOpts : cropOptsAll);
                    }
                } else {
                    img = applyStep(crop, step, maxUp, cside, deblurPx);
                    codes = read(img, cside > 0.f ? cropOpts : cropOptsAll);
                }
                if (codes.empty() && (step == CR_STEP_FLAT || step == CR_STEP_GLARE)) codes = read(img, globalOpts);
                float extra = 1.f; // the fixed levels read a 2x upscale of img
                if (codes.empty() && (step == CR_STEP_DEBLUR || step == CR_STEP_DEBLUR_STRONG)) {
                    auto lv = readLevels(img, levelPx);
                    codes = std::move(lv.first);
                    extra = lv.second;
                    // second try for Data Matrix: the deblur from the blurred start, which reads
                    // other codes than the Wiener start (about 12 ms), while the pass has time for
                    // it. Not for QR: there the time is worth more to the later passes (24 QR at
                    // the line targets lost one code on each of 3 images with it, 2026-10-30)
                    if (codes.empty() && p.format == CR_DATAMATRIX && msSince(t0) + 15 < passLimit) {
                        img = deblur(crop, deblurSigma(step, deblurPx), true);
                        codes = read(img, cside > 0.f ? cropOpts : cropOptsAll);
                        if (codes.empty()) {
                            lv = readLevels(img);
                            codes = std::move(lv.first);
                            extra = lv.second;
                        }
                    }
                }
                profStep(step, tStep, !codes.empty());
                if (isAi[i]) countTry(step, zone, !codes.empty());
                if (!codes.empty()) {
                    const float scale = static_cast<float>(img.cols) / crop.cols * extra;
                    const cv::Point2f off(roiOffset.x + cands[i].x, roiOffset.y + cands[i].y);
                    // the region is read when its own code is: one near the region's centre (a
                    // neighbour inside the crop, new or not, does not close it: codes sit one side
                    // apart). A region of unknown size counts any code.
                    const cv::Point2f mid(off.x + cands[i].width / 2.f, off.y + cands[i].height / 2.f);
                    const float side = i < candSide.size() ? candSide[i] : 0.f;
                    bool mine = false;
                    for (const auto& b : codes) {
                        cv::Point2f ctr;
                        found.add(b, step, off, scale, &ctr);
                        mine = mine || side <= 0.f || cv::norm(ctr - mid) < 0.6f * side;
                    }
                    if (mine) {
                        cropRead[i] = 1;
                        break;
                    }
                    continue;
                }
                if (step == CR_STEP_PLAIN && inlineEnhance && (p.enhancer_mode == CR_ENHANCE_EARLY || dull) &&
                    tryEnhance(i, crop, threadIndex)) {
                    cropRead[i] = 1;
                    break;
                }
                // a blurred Data Matrix: libdmtx right after the deblur steps, not at the very end
                if (blurred && step == CR_STEP_DEBLUR && !dmtxTried) {
                    dmtxTried = true;
                    if (!uselessIn(CR_STEP_DMTX, zone) && learnedDmtx(i, crop, zone)) {
                        cropRead[i] = 1;
                        break;
                    }
                }
            }
            planDone[i] = 1;
            if (!cropRead[i] && aiRegion && inlineEnhance && p.enhancer_mode == CR_ENHANCE_LAST && !found.done() &&
                msSince(t0) < p.time_budget_ms && tryEnhance(i, crop, threadIndex))
                cropRead[i] = 1;
            if (!cropRead[i] && !dmtxTried && !uselessIn(CR_STEP_DMTX, zone) && learnedDmtx(i, crop, zone)) cropRead[i] = 1;
        }
    };

    runAll(worker);
profMark("steps");
    // a product's first case: most codes read so far came out light on dark, so the strong passes
    // that follow get the flipped image
    if (!noPolarity && !invertedImage && is2D(p.format) && (!learned || learned->polarity.load() == 0) &&
        found.invertedReads.load() >= 2 && found.invertedReads.load() * 2 > found.zxReads.load())
        flipImage();



    // Data Matrix inside-out (lattice): the codes whose finder or quiet zone is broken, decoded
    // from the module grid the texture shows. A real decode, so before candidate matching.
    if ((p.format & CR_DATAMATRIX) && !found.done() && msSince(t0) < p.time_budget_ms) {
        const auto readNow = found.take();
        std::vector<int> mods;
        std::vector<float> sides;
        for (const auto& c : readNow)
            if (c.format == CR_DATAMATRIX && c.modW >= 10 && c.modW == c.modH) {
                mods.push_back(c.modW);
                float s = 0.f;
                for (int k = 0; k < 4; ++k) s += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
                sides.push_back(s / 4.f);
            }
        int n = 0;
        float side = 0.f;
        if (!mods.empty()) {
            std::nth_element(mods.begin(), mods.begin() + mods.size() / 2, mods.end());
            std::nth_element(sides.begin(), sides.begin() + sides.size() / 2, sides.end());
            n = mods[mods.size() / 2];
            side = sides[sides.size() / 2];
        }
        if (n > 0 && side > 0.f) {
            const std::vector<std::string> knownDm = found.texts(CR_DATAMATRIX);
            std::atomic<size_t> nextL{0};
            auto latticer = [&]() {
                for (;;) {
                    if (found.done() || msSince(t0) > p.time_budget_ms) return;
                    const size_t i = nextL.fetch_add(1);
                    if (i >= cands.size()) return;
                    // AI regions only, unless there is no AI locator: then the classical regions
                    // are the code regions (as aiRegion in the step plan)
                    if (!isAi[i] && aiCount > 0) continue;
                    if (cropRead[i]) continue;
                    if (found.covers(cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y)), i < candSide.size() ? candSide[i] : 0.f)) {
                        cropRead[i] = 1;
                        continue;
                    }
                    const cv::Point mid(cands[i].x + cands[i].width / 2, cands[i].y + cands[i].height / 2);
                    const int r = static_cast<int>(std::max(std::max(cands[i].width, cands[i].height) * 0.75f, side * 2.f));
                    const cv::Rect big = cv::Rect(mid.x - r, mid.y - r, 2 * r, 2 * r) & cv::Rect(0, 0, gray.cols, gray.rows);
                    const int left = p.time_budget_ms - static_cast<int>(msSince(t0));
                    // one region may take a twelfth of the budget (80 ms at least): with short budgets
                    // the passes after this one still get their turn
                    const int cap = std::max(80, p.time_budget_ms / 12);
                    if (left < 60) return; // too little time to start
                    std::string text;
                    cv::Point2f q[4];
                    const auto tLat = Clock::now();
                    OwnReadScope ids;
                    const bool lg = latticeDm(gray(big), side, n, std::min(left, cap), text, q, &knownDm);
                    profStep(CR_STEP_LATTICE, tLat, lg);
                    if (!lg) continue;
                    Code c;
                    c.text = text;
                    c.format = CR_DATAMATRIX;
                    c.step = CR_STEP_LATTICE;
                    c.modW = c.modH = n;
                    ids.apply(c);
                    for (auto& pt : q) pt += cv::Point2f(roiOffset.x + big.x, roiOffset.y + big.y);
                    found.addCode(std::move(c), q);
                    if (nearRegion(i, (q[0] + q[2]) * 0.5f)) cropRead[i] = 1;
                }
            };
            runAll([&](int) { latticer(); });
profMark("lattice");
        }
    }

    // candidate matching, early: cheap, and on shrink-wrapped cases it reads most of what the
    // plain steps could not (the gaps in the case's serial run, encoded the printer's way)
    const auto medianRead = found.medianQr();
    const float readSide = medianRead.first;
    const int readMods = medianRead.second;
    MatchCandidates mc;
    std::mutex claimMu; // two regions never claim the same candidate
    if (p.match_serials && (p.format & (CR_QR | CR_DATAMATRIX)) && !found.done() && msSince(t0) < p.time_budget_ms) {
        const auto readNow = found.take();
        mc.encoding = learnedEncoding(readNow, learned);
        if (mc.encoding.valid()) mc.texts = serialRunGaps(readNow, p.expected_count, mc.encoding.format);
    }
    std::atomic<size_t> nextM{0};
    auto matcher = [&]() {
            for (;;) {
                if (found.done() || msSince(t0) > p.time_budget_ms) return;
                const size_t i = nextM.fetch_add(1);
                if (i >= cands.size()) return;
                if (!isAi[i]) continue;
                if (cropRead[i]) continue;
                if (found.covers(cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y)), i < candSide.size() ? candSide[i] : 0.f)) {
                    cropRead[i] = 1;
                    continue;
                }
                // the region plus a third of a code around it, at most 2.5 codes across: the
                // finder search is the costly part and grows with the crop
                const int margin = static_cast<int>(readSide * 0.35f), cap = static_cast<int>(readSide * 2.5f);
                cv::Rect big(cands[i].x - margin, cands[i].y - margin, cands[i].width + 2 * margin, cands[i].height + 2 * margin);
                if (big.width > cap || big.height > cap) {
                    const cv::Point mid(cands[i].x + cands[i].width / 2, cands[i].y + cands[i].height / 2);
                    big = cv::Rect(mid.x - cap / 2, mid.y - cap / 2, cap, cap);
                }
                big &= cv::Rect(0, 0, gray.cols, gray.rows);
                const int left = p.time_budget_ms - static_cast<int>(msSince(t0));
                std::string text;
                cv::Point2f q[4];
                const bool dm = mc.encoding.format == CR_DATAMATRIX;
                float mscore = 0.f;
                // the lens blur of this zone (measured on read codes), else of the image, else the
                // typical value: the matcher re-scores a near miss through it
                float psfMod = kDefaultPsfMod;
                if (learned) {
                    const float z = learned->psfSigma[static_cast<size_t>(zoneOf(cands[i]))].load(), all = learned->psfSigmaAll.load();
                    psfMod = z > 0.f ? z : all > 0.f ? all : kDefaultPsfMod;
                }
                // a pattern match alone does not tell FNC1; the verifying decode below does
                OwnReadScope ids;
                bool matched = dm ? matchDm(gray(big), readSide, mc, std::min(left, 150), text, q)
                                  : matchQr(gray(big), readSide, mc, std::min(left, 150), text, q, &mscore, psfMod);
                // a defocused code: the sharp pattern correlates weakly with the blurred image;
                // a Richardson-Lucy deblur of the crop lifts a near miss over the threshold
                for (const double sigma : {1.5, 2.5}) {
                    if (matched || dm || !(blurredRegion[i] || mscore > 0.5f)) break;
                    const int now = p.time_budget_ms - static_cast<int>(msSince(t0));
                    if (now < 30) break;
                    matched = matchQr(deblur(gray(big), sigma), readSide, mc, std::min(now, 150), text, q, &mscore);
                }
                if (!matched) continue;
                // check the match with a real decode at the corners it found: the same text makes it
                // a decode (Reed-Solomon verified); a different text means the match was wrong
                // (a Data Matrix too: its identifier and FNC1 come from that decode of its own modules;
                // a match it does not confirm keeps none, and a GS1 check fails it)
                int step = CR_STEP_MATCH;
                {
                    const std::string real = dm ? decodeDmAt(gray(big), q, mc.encoding.modules) : decodeQrAt(gray(big), q, mc.encoding.modules);
                    if (!real.empty() && real != text) continue;
                    if (real == text) step = CR_STEP_GRIDFIT;
                }
                std::lock_guard<std::mutex> lock(claimMu);
                if (found.has(text)) continue;
                Code c;
                c.text = text;
                c.format = mc.encoding.format;
                c.step = step;
                c.modW = c.modH = mc.encoding.modules;
                ids.apply(c);
                for (auto& pt : q) pt += cv::Point2f(roiOffset.x + big.x, roiOffset.y + big.y);
                found.addCode(std::move(c), q);
                if (nearRegion(i, (q[0] + q[2]) * 0.5f)) cropRead[i] = 1;
            }
    };
    if (!mc.texts.empty() && readSide > 0.f) {
        runAll([&](int) { matcher(); });
profMark("match");

    }

    // the second look (cheap: an enlarged crop with the lighting fixes) and the known-content
    // pass (reads what no image fix can, but costs more): the one that has won on this product
    // goes first, else the cheap one
    auto runSecondLook = [&]() {
        // second look: glare can hide part of a code, so the AI marks only the rest and the crop
        // comes out off-centre. An enlarged crop with the lighting fixes, after the cheaper passes.
        std::atomic<size_t> nextS{0};
        auto secondLook = [&]() {
            for (;;) {
                if (found.done() || msSince(t0) > p.time_budget_ms) return;
                const size_t i = nextS.fetch_add(1);
                if (i >= cands.size()) return;
                    if (!isAi[i]) continue;
                if (cropRead[i]) continue;
                if (found.covers(cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y)), i < candSide.size() ? candSide[i] : 0.f)) {
                    cropRead[i] = 1;
                    continue;
                }
                const bool blurred = blurredRegion[i] != 0;
                {
                    const cv::Rect big = cv::Rect(cands[i].x - cands[i].width * 2 / 5, cands[i].y - cands[i].height * 2 / 5,
                                                  cands[i].width * 9 / 5, cands[i].height * 9 / 5) &
                                         cv::Rect(0, 0, gray.cols, gray.rows);
                    const cv::Mat bigCrop = gray(big);
                    const float side = i < candSide.size() ? candSide[i] : 0.f;
                    std::vector<int> second = {CR_STEP_PLAIN, CR_STEP_FLAT, CR_STEP_FLAT_ADAPT};
                    if (blurred) second.insert(second.begin() + 1, {CR_STEP_DEBLUR_STRONG, CR_STEP_DEBLUR});
                    for (int step : second) {
                        if (found.done() || msSince(t0) > p.time_budget_ms) break;
                        const cv::Mat img = applyStep(bigCrop, step, p.max_upscale, side);
                        auto codes = read(img, cropOpts);
                        if (codes.empty() && step != CR_STEP_PLAIN) codes = read(img, globalOpts);
                        float extra = 1.f;
                        if (codes.empty() && (step == CR_STEP_DEBLUR || step == CR_STEP_DEBLUR_STRONG)) {
                            auto lv = readLevels(img);
                            codes = std::move(lv.first);
                            extra = lv.second;
                        }
                        if (codes.empty()) continue;
                        const float scale = static_cast<float>(img.cols) / bigCrop.cols * extra;
                        const cv::Point2f off(roiOffset.x + big.x, roiOffset.y + big.y);
                        // read when the region's own code is (near its centre); a neighbour in the
                        // enlarged crop does not close it
                        const cv::Point2f mid(roiOffset.x + cands[i].x + cands[i].width / 2.f, roiOffset.y + cands[i].y + cands[i].height / 2.f);
                        bool mine = false;
                        for (const auto& b : codes) {
                            cv::Point2f ctr;
                            found.add(b, step, off, scale, &ctr);
                            mine = mine || side <= 0.f || cv::norm(ctr - mid) < 0.6f * side;
                        }
                        if (!mine) continue;
                        cropRead[i] = 1;
                        break;
                    }
                    countTry(kStepSecondLook, zoneOf(cands[i]), cropRead[i] != 0);
    #ifdef CR_HAVE_DMTX
                    // a blurred Data Matrix whose crop was off-centre: libdmtx on the enlarged crop
                    if (!cropRead[i] && blurred && (p.format & CR_DATAMATRIX) && !found.done() && !uselessIn(CR_STEP_DMTX, zoneOf(cands[i]))) {
                        const int left = p.time_budget_ms - static_cast<int>(msSince(t0));
                        std::string text;
                        cv::Point2f q[4];
                        std::string symId;
                        if (left > 10 && readDmtx(deblur(bigCrop, 2.0), std::min(left, 80), text, q, &symId) && dmtxPlausible(text, q)) {
                            Code c;
                            c.text = text;
                            c.symbologyId = symId;
                            c.gs1 = symId == "]d2" ? 1 : 0;
                            c.format = CR_DATAMATRIX;
                            c.step = CR_STEP_DMTX;
                            for (auto& pt : q) pt += cv::Point2f(roiOffset.x + big.x, roiOffset.y + big.y);
                            found.addCode(std::move(c), q);
                            if (nearRegion(i, (q[0] + q[2]) * 0.5f)) cropRead[i] = 1;
                        }
                    }
    #endif
                }
            }
        };
        if (!found.done() && msSince(t0) < p.time_budget_ms) {
            runAll([&](int) { secondLook(); });
    profMark("second-look");
        }
    };
    auto runKnown = [&]() {
        // known content, after the serial-run match (20x cheaper, and it reads the gaps on serial-run
        // products): once codes of the product are read, an unread QR differs from them in
        // its serial only. Cheap (tens of ms a region) and it reads labels no other step can (heavy
        // ink spread), so it runs before the slower passes. Only that step here; the full grid fit
        // comes last.
        if ((p.format & CR_QR) && !found.done() && msSince(t0) < p.time_budget_ms) {
            const std::vector<std::string> knownTexts = found.qrTexts();
            const auto medianNow = found.medianQr();
            // learned like a step: where it never reads (other products), it is skipped
            if (knownTexts.size() >= 2 && medianNow.second >= 21 && !useless(CR_STEP_KNOWN)) {
                std::atomic<size_t> nextK{0};
                auto knower = [&]() {
                    for (;;) {
                        if (found.done() || msSince(t0) > p.time_budget_ms) return;
                        const size_t i = nextK.fetch_add(1);
                        if (i >= cands.size()) return;
                        if (!isAi[i] || cropRead[i]) continue;
                        const cv::Rect full = cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y));
                        if (found.covers(full, i < candSide.size() ? candSide[i] : 0.f)) continue;
                        const cv::Rect big = cv::Rect(cands[i].x - cands[i].width * 2 / 5, cands[i].y - cands[i].height * 2 / 5,
                                                      cands[i].width * 9 / 5, cands[i].height * 9 / 5) &
                                             cv::Rect(0, 0, gray.cols, gray.rows);
                        const int left = p.time_budget_ms - static_cast<int>(msSince(t0));
                        if (left < 30) return;
                        std::vector<std::string> texts;
                        std::vector<std::array<cv::Point2f, 4>> quads;
                        const auto tK = Clock::now();
                        OwnReadScope ids;
                        const bool got = knownFitQR(gray(big), medianNow.second, medianNow.first, knownTexts, std::min(left, 200), texts, quads) > 0;
                        profStep(CR_STEP_KNOWN, tK, got);
                        countTry(CR_STEP_KNOWN, zoneOf(cands[i]), got);
                        if (!got) continue;
                        bool mine = false;
                        for (size_t k = 0; k < texts.size(); ++k) {
                            Code c;
                            c.text = texts[k];
                            c.format = CR_QR;
                            c.step = CR_STEP_KNOWN;
                            ids.apply(c);
                            for (auto& pt : quads[k]) pt += cv::Point2f(roiOffset.x + big.x, roiOffset.y + big.y);
                            found.addCode(std::move(c), quads[k].data());
                            mine = mine || nearRegion(i, (quads[k][0] + quads[k][2]) * 0.5f);
                        }
                        if (mine) cropRead[i] = 1;
                    }
                };
                runAll([&](int) { knower(); });
    profMark("known");
            }
        }
    };
    {
        // learned win rates (smoothed); a new product ties, and the known pass goes first then
        auto rate = [&](int step) {
            if (!learned) return 0.5;
            const double w = learned->wins[static_cast<size_t>(step)].load(), t = learned->tries[static_cast<size_t>(step)].load();
            return (w + 1.0) / (t + 2.0);
        };
        if (rate(CR_STEP_KNOWN) >= rate(kStepSecondLook)) {
            runKnown();
            runSecondLook();
        } else {
            runSecondLook();
            runKnown();
        }
    }




    // CR_ENHANCE_SECOND_PASS: the enhancer on regions still unread, best first, while time
    // remains, after every region had the cheap fixes.
    if (haveEnhancer && p.enhancer_mode == CR_ENHANCE_SECOND_PASS && !found.done() && msSince(t0) < p.time_budget_ms) {
        std::atomic<size_t> nextE{0};
        auto enhancer = [&](int threadIndex) {
            for (;;) {
                if (found.done() || msSince(t0) > p.time_budget_ms) return;
                const size_t i = nextE.fetch_add(1);
                if (i >= cands.size()) return;
                if (cropRead[i]) continue;
                const cv::Rect full = cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y));
                if (found.covers(full, i < candSide.size() ? candSide[i] : 0.f)) {
                    cropRead[i] = 1;
                    continue;
                }
                if (tryEnhance(i, gray(cands[i]), threadIndex)) cropRead[i] = 1;
            }
        };
        runAll(enhancer);
profMark("enhancer");
    }

    // fallback: whole-image passes (raw and brightened) when still short
    const bool short_ = p.expected_count <= 0 || found.count() < p.expected_count;
    // its cost is learned per reader (the whole image, ms), first guessed per megapixel: 7 ms, or
    // 20 ms with Data Matrix on (measured 12-23 ms on 20 MP cases of caps and cans, 2026-10-31)
    const float fullGuess = static_cast<float>(gray.total()) * ((p.format & CR_DATAMATRIX) ? 20e-6f : 7e-6f);
    const float fullCost = learned && learned->fullPassMs.load() > 0.f ? learned->fullPassMs.load() : fullGuess;
    // the region path serves QR and Data Matrix; every other type enabled needs this pass
    if ((p.full_pass || (p.format & kZxTypes & ~(CR_QR | CR_DATAMATRIX))) && (p.format & kZxTypes) && short_ &&
        msSince(t0) < p.time_budget_ms) {
        ZXing::ReaderOptions fullOpts;
        fullOpts.setFormats(formats);
        fullOpts.setTryRotate(true);
        fullOpts.setTryDownscale(true);
        fullOpts.setTryHarder(true);
        // the case's count plus a margin for second copies: zxing's default searches on for 255
        if (p.expected_count > 0) fullOpts.setMaxNumberOfSymbols(static_cast<uint8_t>(std::min(255, p.expected_count * 2 + 4)));
        fullOpts.setTextMode(ZXing::TextMode::Plain);
        fullOpts.setEanAddOnSymbol(ZXing::EanAddOnSymbol::Read);

        cv::Mat bright;
        cv::Mat lut(1, 256, CV_8U);
        for (int i = 0; i < 256; ++i) lut.at<uint8_t>(i) = cv::saturate_cast<uint8_t>(std::pow(i / 255.0, p.gamma) * 255.0);
        cv::LUT(stretch(gray), lut, bright);

        // the whole-image decoders correct up to their full capacity with no reserve: once enough
        // codes of the case are known (five, or half the case), a read must look like them (length,
        // digits); fewer would reject random alphanumeric serials by chance
        const int plausibleFrom = std::max(5, p.expected_count > 0 ? p.expected_count / 2 : 5);
        // compared as the job keeps it (a stripped check digit), with the codes already kept
        auto addFull = [&](const ZXing::Barcode& b, int step, cv::Point2f offset) {
            std::string kept = mainText(b, nullptr);
            if (!found.accepts(fromZx(b.format()), kept)) return;
            const std::vector<std::string> known = found.texts(fromZx(b.format()));
            if (static_cast<int>(known.size()) >= plausibleFrom && !plausibleAmong(kept, known)) return;
            found.add(b, step, offset, 1.f);
        };

        // A zxing read cannot stop part-way, and on a large image it can take most of a case's
        // budget (470 ms on a 20 MP case of cans, which it did not help). So a large image is read
        // as 2 x 2 tiles that overlap by more than a code, each tile once raw and once brightened:
        // a job starts only when its cost fits in the time left, and the tiles holding unread
        // regions go first (2026-10-31: 48 DM cans 708 -> under 500 ms).
        float maxSide = 0.f;
        for (float sd : candSide) maxSide = std::max(maxSide, sd);
        const int n = gray.total() > 6000000 ? 2 : 1;
        const int ov = n > 1 ? std::max(static_cast<int>(2.5f * maxSide), std::min(gray.cols, gray.rows) / 12) : 0;
        struct Tile { cv::Rect r; int unread; };
        std::vector<Tile> tiles;
        for (int ty = 0; ty < n; ++ty)
            for (int tx = 0; tx < n; ++tx) {
                const int x0 = std::max(0, tx * gray.cols / n - ov / 2), x1 = std::min(gray.cols, (tx + 1) * gray.cols / n + ov / 2);
                const int y0 = std::max(0, ty * gray.rows / n - ov / 2), y1 = std::min(gray.rows, (ty + 1) * gray.rows / n + ov / 2);
                tiles.push_back({cv::Rect(x0, y0, x1 - x0, y1 - y0), 0});
            }
        for (size_t i = 0; i < cands.size(); ++i) {
            if (cropRead[i]) continue;
            const cv::Rect full = cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y));
            if (found.covers(full, i < candSide.size() ? candSide[i] : 0.f)) continue;
            const cv::Point c(cands[i].x + cands[i].width / 2, cands[i].y + cands[i].height / 2);
            for (auto& t : tiles) if (t.r.contains(c)) t.unread++;
        }
        std::stable_sort(tiles.begin(), tiles.end(), [](const Tile& a, const Tile& b) { return a.unread > b.unread; });
        struct Job { size_t tile; bool brightened; };
        std::vector<Job> jobs;
        for (size_t t = 0; t < tiles.size(); ++t) {
            // the image as captured first, so a code read there is graded on it: a worker that starts
            // late on a busy machine finds the code read and skips its job, and with the brightened
            // job first that was the raw one (Micro QR UEC 0.00 instead of 0.33). The brightened
            // image, on the other worker at the same time, reads the dark codes
            jobs.push_back({t, false});
            jobs.push_back({t, true});
        }
        // a job's cost: the whole image's by its share of the pixels, then what this case's jobs took
        std::mutex costMu;
        float jobCost = 0.f, jobMsSum = 0.f;
        int jobsDone = 0;
        auto costOf = [&](const cv::Rect& r) {
            std::lock_guard<std::mutex> lock(costMu);
            const float share = static_cast<float>(r.area()) / static_cast<float>(gray.total());
            return jobsDone > 0 ? std::max(jobCost, fullCost * share) : fullCost * share;
        };
        std::atomic<size_t> nextJob{0};
        std::atomic<bool> stop{false};
        auto worker = [&] {
            for (;;) {
                if (stop || found.done()) return;
                const size_t j = nextJob.fetch_add(1);
                if (j >= jobs.size()) return;
                const cv::Rect r = tiles[jobs[j].tile].r;
                if (msSince(t0) + 0.8f * costOf(r) > p.time_budget_ms) { stop = true; return; }
                const auto tJob = Clock::now();
                const cv::Point2f off(roiOffset.x + r.x, roiOffset.y + r.y);
                const int step = jobs[j].brightened ? CR_STEP_FULL_BRIGHT : CR_STEP_FULL;
                for (const auto& b : read((jobs[j].brightened ? bright : gray)(r), fullOpts)) addFull(b, step, off);
                const float ms = static_cast<float>(std::chrono::duration<double, std::milli>(Clock::now() - tJob).count());
                std::lock_guard<std::mutex> lock(costMu);
                jobMsSum += ms;
                ++jobsDone;
                jobCost = std::max(jobCost, ms);
            }
        };
        std::thread a(worker), b(worker);
        a.join();
        b.join();
        if (learned && jobsDone > 0) {
            // back to the whole image: the jobs' mean over their mean share of the pixels
            float area = 0.f;
            for (const auto& t : tiles) area += static_cast<float>(t.r.area());
            const float ms = jobMsSum / jobsDone * static_cast<float>(gray.total()) / (area / tiles.size());
            const float old = learned->fullPassMs.load();
            learned->fullPassMs.store(old <= 0.f ? ms : 0.7f * old + 0.3f * ms);
        }
    }


    // 1D codes the passes above missed: a label at an angle zxing does not scan, or bars blurred past
    // their edges (linear.cpp)
    {
        const int N = p.expected_count > 0 ? p.expected_count : 1;
        if ((p.format & CR_ALL_LINEAR) && found.count() < N && !found.done() && msSince(t0) < p.time_budget_ms) {
            const auto by = t0 + std::chrono::milliseconds(p.time_budget_ms);
            const auto accept = [&](int format, std::string text) { return found.accepts(format, text); };
            for (auto& r : readLinear(gray, p.format & CR_ALL_LINEAR, N - found.count(), by, accept)) {
                Code c;
                c.text = r.text;
                c.addon = r.addon;
                c.symbologyId = r.symbologyId;
                c.gs1 = r.gs1;
                c.format = r.format;
                c.step = r.step;
                c.lightOnDark = r.lightOnDark < 0 ? -1 : (r.lightOnDark == 1) != invertedImage ? 1 : 0;
                for (auto& q : r.quad) q += roiOffset;
                found.addCode(std::move(c), r.quad);
            }
        }
profMark("linear");
    }

    // PDF417 the whole-image pass missed: a scratch along the bars breaks a codeword in every row
    // (pdf417.cpp)
    {
        const int N = p.expected_count > 0 ? p.expected_count : 1;
        static const bool off = std::getenv("CR_NO_PDF417_FUSE") != nullptr; // A/B
        if (!off && (p.format & CR_PDF417) && found.count() < N && !found.done() && msSince(t0) < p.time_budget_ms) {
            const auto by = t0 + std::chrono::milliseconds(p.time_budget_ms);
            const auto accept = [&](int format, std::string text) { return found.accepts(format, text); };
            for (auto& r : readPdf417Fused(gray, N - found.count(), by, accept)) {
                Code c;
                c.text = r.text;
                c.format = r.format;
                c.step = r.step;
                for (auto& q : r.quad) q += roiOffset;
                found.addCode(std::move(c), r.quad);
            }
        }
profMark("pdf417");
    }

    // Micro QR the whole-image pass missed: blurred or slanted past zxing's edge sampling (microqr.cpp)
    {
        const int N = p.expected_count > 0 ? p.expected_count : 1;
        static const bool off = std::getenv("CR_NO_MICROQR_FIT") != nullptr; // A/B
        if (!off && (p.format & CR_MICRO_QR) && found.count() < N && !found.done() && msSince(t0) < p.time_budget_ms) {
            const auto by = t0 + std::chrono::milliseconds(p.time_budget_ms);
            const auto accept = [&](int format, std::string text) { return found.accepts(format, text); };
            for (auto& r : readMicroQrFit(gray, N - found.count(), by, accept)) {
                Code c;
                c.text = r.text;
                c.format = r.format;
                c.step = r.step;
                for (auto& q : r.quad) q += roiOffset;
                found.addCode(std::move(c), r.quad);
            }
        }
profMark("microqr");
    }

    // Pharmacode and DotCode: decoders of the engine's own (pharmacode.cpp, dotcode.cpp)
    {
        const int N = p.expected_count > 0 ? p.expected_count : 1;
        const auto by = t0 + std::chrono::milliseconds(p.time_budget_ms);
        const auto accept = [&](int format, std::string text) { return found.accepts(format, text); };
        const auto keep = [&](std::vector<LinearRead> reads) {
            for (auto& r : reads) {
                Code c;
                c.text = r.text;
                c.symbologyId = r.symbologyId;
                c.gs1 = r.gs1;
                c.format = r.format;
                c.step = r.step;
                c.modules = std::move(r.modules); // DotCode's dot grid, for grading
                c.modW = r.modW;
                c.modH = r.modH;
                c.uec = r.uec;
                for (auto& q : r.quad) q += roiOffset;
                found.addCode(std::move(c), r.quad);
            }
        };
        if ((p.format & (CR_PHARMACODE | CR_PHARMACODE_REV)) && found.count() < N && !found.done() && msSince(t0) < p.time_budget_ms) {
            // no check digit: without a closed bar-count lock (both bounds; with only a top, a missing
            // end bar reads as a shorter code) a Pharmacode is read only while teaching (CR_ANY)
            if ((p.min_length >= 2 && p.max_length >= p.min_length) || p.format == CR_ANY)
                keep(readPharmacode(gray, p.format, p.pharma_direction, p.min_length, p.max_length, N - found.count(), by, accept));
        }
        if ((p.format & CR_DOTCODE) && found.count() < N && !found.done() && msSince(t0) < p.time_budget_ms)
            keep(readDotCode(gray, N - found.count(), by, accept));
profMark("pharmacode/dotcode");
    }

    // Search harder when still short (owner's rule): fewer than N codes READ, although the finder may have
    // proposed N regions (film wrinkles or a hand fill the count, while labels under clear tape are
    // missed). The AI again at a lower threshold and twice the resolution, outside the regions
    // already tried; new regions of the product's size get the same steps and the passes after.
    {
        const int N = p.expected_count > 500 ? 0 : p.expected_count;
        cv::dnn::Net* net = models.locator;
        if (net && N > 0 && is2D(p.format) && found.count() < N && !found.done() && msSince(t0) < p.time_budget_ms * 0.7) {
            const float lo = learned ? learned->codeMin.load() : 0.f, hi = learned ? learned->codeMax.load() : 0.f;
            std::vector<float> readSides;
            for (const auto& c : found.take()) {
                float sd = 0.f;
                for (int k = 0; k < 4; ++k) sd += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
                readSides.push_back(sd / 4.f);
            }
            std::sort(readSides.begin(), readSides.end());
            const float readSide = readSides.empty() ? 0.f : readSides[readSides.size() / 2];
            const size_t first = cands.size();
            for (int scale : {4, 8}) {
                if (msSince(t0) > p.time_budget_ms * 0.7) break;
                for (const auto& f : locateAI(*net, gray, std::min(p.ai_threshold, 0.15f), nullptr, scale)) {
                    const cv::Point c(static_cast<int>(f.rotated.center.x), static_cast<int>(f.rotated.center.y));
                    bool tried = false;
                    for (size_t k = 0; k < cands.size(); ++k) {
                        const cv::Point2f kc(cands[k].x + cands[k].width / 2.f, cands[k].y + cands[k].height / 2.f);
                        const float ks = k < candSide.size() ? candSide[k] : 0.f;
                        if (ks > 0.f ? cv::norm(kc - cv::Point2f(c)) < 0.6f * ks : cands[k].contains(c)) { tried = true; break; }
                    }
                    if (tried) continue;
                    // the product's size: learned range, else about the size of the codes read now.
                    // The AI's side of a defocused code is far too small (a blob): the box counts too
                    const float fs = std::max(f.side, std::max(f.box.width, f.box.height) * 0.5f);
                    if (lo > 0.f ? !(fs > lo * 0.6f && f.side < hi * 1.5f)
                                 : readSide > 0.f && !(fs > readSide * 0.5f && f.side < readSide * 2.5f))
                        continue;
                    // a crop of at least one and a half codes around it: the AI box may be tight
                    const int half = static_cast<int>(std::max<float>(std::max(f.box.width, f.box.height), readSide * 1.5f) / 2);
                    cands.push_back(cv::Rect(c.x - half, c.y - half, 2 * half, 2 * half) & cv::Rect(0, 0, gray.cols, gray.rows));
                    candSide.push_back(f.side > 0.f ? f.side : readSide);
                    candScore.push_back(f.score);
                    isAi.push_back(1);
                }
            }
            // the case's empty slots no region covers: the read codes are the fixture (slots.cpp),
            // so a code the locator missed still gets the strong passes
            static const bool noSlots = std::getenv("CR_NO_SLOTS") != nullptr; // A/B
            slotFrom = cands.size();
            if (!noSlots && learned && msSince(t0) < p.time_budget_ms * 0.7) {
                std::vector<cv::Point2f> centres;
                float side = 0.f;
                centresOf(found.take(), centres, &side);
                for (auto& c : centres) c -= roiOffset;
                const SlotGrid sg = fitSlots(centres, learned->slotA.load(), learned->slotB.load(), &gray, side, p.expected_count);
                for (const auto& [si, sj] : emptySlots(sg, centres)) {
                    const cv::Point2f c = slotCentreNear(sg, si, sj, centres);
                    bool tried = false;
                    for (size_t k = 0; k < cands.size() && !tried; ++k) {
                        const cv::Point2f kc(cands[k].x + cands[k].width / 2.f, cands[k].y + cands[k].height / 2.f);
                        tried = cv::norm(kc - c) < 0.4f * sg.pitch;
                    }
                    if (tried) continue;
                    const int half = static_cast<int>(std::max(side * 0.75f, 0.45f * sg.pitch));
                    const cv::Rect r = cv::Rect(static_cast<int>(c.x) - half, static_cast<int>(c.y) - half, 2 * half, 2 * half) & cv::Rect(0, 0, gray.cols, gray.rows);
                    if (r.width < half || r.height < half) continue;
                    cands.push_back(r);
                    candSide.push_back(side);
                    candScore.push_back(0.5f);
                    isAi.push_back(1);
                }
            }
            if (cands.size() > first) {
                cropRead.resize(cands.size(), 0);
                blurredRegion.resize(cands.size(), 0);
                next.store(first);
                passLimit = p.time_budget_ms;
                runAll(worker);
profMark("recall-steps");
                // and the serial-run match on the new regions the steps could not read, with the
                // gaps as they are now (a faint code whose own region comes only from this pass)
                if (p.match_serials && mc.encoding.valid() && readSide > 0.f && !found.done() && msSince(t0) < p.time_budget_ms) {
                    // a fresh candidate set: the pattern cache is sized by the first text list
                    const QrEncoding enc = mc.encoding;
                    mc = MatchCandidates();
                    mc.encoding = enc;
                    mc.texts = serialRunGaps(found.take(), p.expected_count, enc.format);
                    if (!mc.texts.empty()) {
                        nextM.store(first);
                        runAll([&](int) { matcher(); });
profMark("recall-match");
                    }
                }
                // and the Data Matrix lattice on the new regions the steps could not read (a
                // defocused cap: the AI box is small and comes only from this pass)
                if (p.format & CR_DATAMATRIX) {
                    const auto medDm = found.medianDm();
                    const std::vector<std::string> knownDm = found.texts(CR_DATAMATRIX);
                    for (size_t i = first; i < cands.size() && medDm.second >= 10; ++i) {
                        if (cropRead[i] || found.done()) continue;
                        const int left = p.time_budget_ms - static_cast<int>(msSince(t0));
                        if (left < 60) break;
                        const cv::Point mid(cands[i].x + cands[i].width / 2, cands[i].y + cands[i].height / 2);
                        const int r = static_cast<int>(std::max(std::max(cands[i].width, cands[i].height) * 0.75f, medDm.first * 2.f));
                        const cv::Rect big = cv::Rect(mid.x - r, mid.y - r, 2 * r, 2 * r) & cv::Rect(0, 0, gray.cols, gray.rows);
                        std::string text;
                        cv::Point2f q[4];
                        OwnReadScope ids;
                        if (!latticeDm(gray(big), medDm.first, medDm.second, std::min(left, std::max(80, p.time_budget_ms / 12)), text, q, &knownDm)) continue;
                        Code c;
                        c.text = text;
                        c.format = CR_DATAMATRIX;
                        c.step = CR_STEP_LATTICE;
                        c.modW = c.modH = medDm.second;
                        ids.apply(c);
                        for (auto& pt : q) pt += cv::Point2f(roiOffset.x + big.x, roiOffset.y + big.y);
                        found.addCode(std::move(c), q);
                        if (nearRegion(i, (q[0] + q[2]) * 0.5f)) cropRead[i] = 1;
                    }
                }
                // and the model-based QR step on the new regions the steps could not read
                const int mods = learned ? learned->qrModules.load() : 0;
                const std::vector<std::string> knownTexts = found.qrTexts();
                // at most a quarter of the budget for all of them: the lower threshold proposes
                // many wrinkles, and a fit on each costs up to 400 ms
                const double recallFitBy = msSince(t0) + p.time_budget_ms * 0.25;
                for (size_t i = first; i < cands.size() && (p.format & CR_QR); ++i) {
                    if (cropRead[i] || found.done()) continue;
                    const int left = static_cast<int>(std::min<double>(p.time_budget_ms, recallFitBy) - msSince(t0));
                    if (left < 60) break;
                    std::string text;
                    cv::Point2f q[4];
                    OwnReadScope ids;
                    if (!gridFitQR(gray(cands[i]), mods, readSide, std::min(left, 400), text, q, &knownTexts)) continue;
                    Code c;
                    c.text = text;
                    c.format = CR_QR;
                    c.step = CR_STEP_GRIDFIT;
                    ids.apply(c);
                    for (auto& pt : q) pt += cv::Point2f(roiOffset.x + cands[i].x, roiOffset.y + cands[i].y);
                    found.addCode(std::move(c), q);
                    if (nearRegion(i, (q[0] + q[2]) * 0.5f)) cropRead[i] = 1;
                }
            }
        }
    }

    // tiny, noisy QR: the product's known grid laid on the finder patterns. The slowest fix, so it
    // runs last, after every region had the cheaper ones, after the whole-image passes and after
    // the search-harder pass (whose regions it also gets).
    if ((p.format & CR_QR) && !found.done() && msSince(t0) < p.time_budget_ms) {
        std::atomic<size_t> nextG{0};
        // size priors: the codes read in this image first (all codes in a case are one size)
        const auto medianNow = found.medianQr();
        const float sideNow = medianNow.first > 0.f ? medianNow.first : readSide;
        const int mods = medianNow.second > 0 ? medianNow.second : readMods > 0 ? readMods : learned ? learned->qrModules.load() : 0;
        const std::vector<std::string> knownTexts = found.qrTexts();
        auto fitter = [&]() {
            for (;;) {
                if (found.done() || msSince(t0) > p.time_budget_ms) return;
                const size_t i = nextG.fetch_add(1);
                if (i >= cands.size()) return;
                if (!isAi[i]) continue;
                if (cropRead[i]) continue;
                const cv::Rect full = cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y));
                if (found.covers(full, i < candSide.size() ? candSide[i] : 0.f)) {
                    cropRead[i] = 1;
                    continue;
                }
                const cv::Rect big = cv::Rect(cands[i].x - cands[i].width * 2 / 5, cands[i].y - cands[i].height * 2 / 5,
                                              cands[i].width * 9 / 5, cands[i].height * 9 / 5) &
                                     cv::Rect(0, 0, gray.cols, gray.rows);
                const int left = p.time_budget_ms - static_cast<int>(msSince(t0));
                if (left < 100) return; // one fit cannot stop part-way: too little time to start
                std::string text;
                cv::Point2f q[4];
                const float side = sideNow > 0.f ? sideNow : i < candSide.size() ? candSide[i] : 0.f;
                // a tight crop first (two code sides each way around the region centre): on a label box far
                // larger than the code, the enlarged crop holds neighbours, and the fit stops at
                // the first code it reads
                bool got = false;
                cv::Rect used = big;
                OwnReadScope ids;
                if (side > 0.f && std::max(big.width, big.height) > side * 3.f) {
                    const int hs = static_cast<int>(side * 2.f);
                    const cv::Point mid(cands[i].x + cands[i].width / 2, cands[i].y + cands[i].height / 2);
                    const cv::Rect tight = cv::Rect(mid.x - hs, mid.y - hs, 2 * hs, 2 * hs) & cv::Rect(0, 0, gray.cols, gray.rows);
                    if (tight.width > side && tight.height > side && gridFitQR(gray(tight), mods, side, std::min(left, 400), text, q, &knownTexts)) {
                        got = true;
                        used = tight;
                    }
                }
                // the time left is measured again: the tight attempt may have spent it
                const int left2 = p.time_budget_ms - static_cast<int>(msSince(t0));
                if (!got && left2 >= 60) got = gridFitQR(gray(big), mods, side, std::min(left2, 400), text, q, &knownTexts);
                if (got) {
                    Code c;
                    c.text = text;
                    c.format = CR_QR;
                    c.step = CR_STEP_GRIDFIT;
                    ids.apply(c);
                    for (auto& pt : q) pt += cv::Point2f(roiOffset.x + used.x, roiOffset.y + used.y);
                    found.addCode(std::move(c), q);
                    if (nearRegion(i, (q[0] + q[2]) * 0.5f)) cropRead[i] = 1;
                }
            }
        };
        runAll([&](int) { fitter(); });
profMark("gridfit");
    }

    // An extra pack (line safety: more codes than expected is NG). Every pass above stops at the
    // expected count, so an extra code was read only when its region came before the last right
    // one. With the count read, the code-sized regions still unread are decoded as well, with the
    // fixes that read this case's codes (an extra pack is printed and lit as the others): a new
    // text puts the count over, a copy of a code in the case counts as a copy (copiesOver below).
    // Regions the first pass already ran its whole plan on are not tried again.
    if (p.expected_count > 0 && found.count() == p.expected_count && !cands.empty()) {
        const auto readNow = found.take();
        std::array<int, StepStats::kMax> wins{};
        for (const auto& c : readNow)
            if (c.step >= 0 && c.step < StepStats::kMax) wins[static_cast<size_t>(c.step)]++;
        static const int kCropSteps[] = {CR_STEP_FLAT, CR_STEP_UPSCALE, CR_STEP_CLAHE, CR_STEP_DEBLUR, CR_STEP_SHARPEN, CR_STEP_BINARY,
                                         CR_STEP_FLAT_ADAPT, CR_STEP_DEBLUR_STRONG, CR_STEP_GLARE, CR_STEP_UPSCALE4};
        std::vector<int> more;
        for (int s : kCropSteps)
            if (wins[static_cast<size_t>(s)] > 0) more.push_back(s);
        std::stable_sort(more.begin(), more.end(), [&](int a, int b) { return wins[static_cast<size_t>(a)] > wins[static_cast<size_t>(b)]; });
        std::vector<int> steps = {CR_STEP_PLAIN};
        for (int s : more)
            if (steps.size() < 3) steps.push_back(s);
        // code-sized: an AI region near the side of the AI regions that read, a classical one (no
        // side) about as large as the codes read; far smaller or larger is background
        std::vector<float> aiSides;
        for (size_t i = 0; i < cands.size() && i < candSide.size(); ++i)
            if (cropRead[i] && candSide[i] > 0.f) aiSides.push_back(candSide[i]);
        float aiSide = 0.f;
        if (!aiSides.empty()) {
            std::nth_element(aiSides.begin(), aiSides.begin() + static_cast<long>(aiSides.size() / 2), aiSides.end());
            aiSide = aiSides[aiSides.size() / 2];
        }
        const float codeSide = found.medianQr().first;
        std::vector<size_t> left;
        for (size_t i = 0; i < cands.size(); ++i) {
            if (cropRead[i] || (i < planDone.size() && planDone[i])) continue;
            const float cs = i < candSide.size() ? candSide[i] : 0.f;
            if (found.covers(cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y)), cs)) continue;
            const bool sized = cs > 0.f ? aiSide <= 0.f || (cs > 0.6f * aiSide && cs < 1.8f * aiSide)
                                        : codeSide <= 0.f || (std::min(cands[i].width, cands[i].height) > 0.6f * codeSide &&
                                                              std::max(cands[i].width, cands[i].height) < 4.f * codeSide);
            if (sized) left.push_back(i);
        }
        std::atomic<size_t> nextX{0};
        std::atomic<int> unchecked{0};
        runAll([&](int) {
            for (;;) {
                const size_t k = nextX.fetch_add(1);
                if (k >= left.size()) return;
                if (msSince(t0) > p.time_budget_ms) {
                    unchecked++;
                    continue;
                }
                const size_t i = left[k];
                const cv::Mat crop = gray(cands[i]);
                const float cs = i < candSide.size() ? candSide[i] : 0.f;
                const cv::Point2f off(roiOffset.x + cands[i].x, roiOffset.y + cands[i].y);
                for (int step : steps) {
                    if (step != steps.front() && msSince(t0) > p.time_budget_ms) {
                        unchecked++; // not every fix was tried
                        break;
                    }
                    const cv::Mat img = applyStep(crop, step, p.max_upscale, cs);
                    const auto codes = read(img, cs > 0.f ? cropOpts : cropOptsAll);
                    if (codes.empty()) continue;
                    const float scale = static_cast<float>(img.cols) / crop.cols;
                    bool mine = false;
                    for (const auto& b : codes) {
                        cv::Point2f ctr;
                        found.add(b, step, off, scale, &ctr);
                        mine = mine || nearRegion(i, ctr);
                    }
                    if (mine) {
                        cropRead[i] = 1;
                        break;
                    }
                }
            }
        });
        res.extraUnchecked = unchecked.load();
        if (prof) std::fprintf(stderr, "prof extra check: %zu regions, %zu fixes, %d not checked\n", left.size(), steps.size(), res.extraUnchecked);
profMark("extra");
    }

    profMark("end");
    if (prof)
        for (size_t k = 0; k < stepUs.size(); ++k)
            if (stepCalls[k]) std::fprintf(stderr, "prof   step %2zu: %5d calls %4d wins %7.1f ms total %5.1f ms each\n", k, stepCalls[k].load(), stepWins[k].load(), stepUs[k] / 1000.0, stepUs[k] / 1000.0 / stepCalls[k]);
    res.codes = found.take();
    res.stats.codes = static_cast<int>(res.codes.size());
    // copies per code over what the product's label carries: an extra pack with a copy of a code
    // already in the case merges with that code (one text), so the count never sees it. Judged
    // only where the label is known: hint_symbols_per_code from the product, else a product
    // learned over 20 cases or more to carry each code once (1.05 at most). A doubled label's
    // second print is not always read, so its learned average can sit anywhere from 1 to 2, and
    // the case's own copies cannot tell it from an extra pack: then nothing is judged. Matrix
    // codes only (a 1D read's place moves along its bars).
    bool copiesFromHint = false;
    if (p.expected_count > 0 && !res.codes.empty()) {
        int allowed = 0;
        if (p.hint_symbols_per_code > 0.f) {
            allowed = std::max(1, static_cast<int>(std::lround(p.hint_symbols_per_code)));
            copiesFromHint = true;
        } else if (learned && learned->cases.load() >= 20) {
            const float spc = learned->symbolsPerCode.load();
            if (spc > 0.f && spc <= 1.05f) allowed = 1;
        }
        if (allowed > 0)
            for (const auto& c : res.codes)
                if (c.format & (CR_QR | CR_MICRO_QR | CR_DATAMATRIX | CR_AZTEC | CR_DOTCODE)) res.copiesOver += c.copies > allowed ? 1 : 0;
    }
    if (std::getenv("CR_COPIES")) { // diagnostic: copies per case, judged or not (tools/bench)
        int twice = 0, most = 0;
        for (const auto& c : res.codes) {
            twice += c.copies > 1;
            most = std::max(most, c.copies);
        }
        std::fprintf(stderr, "copies: %d codes of %d expected, %d seen twice or more (most %d), copies over %d, learned %.2f over %u cases, extra unchecked %d\n",
                     res.stats.codes, p.expected_count, twice, most, res.copiesOver, learned ? static_cast<double>(learned->symbolsPerCode.load()) : 0.0,
                     learned ? learned->cases.load() : 0u, res.extraUnchecked);
    }
    // the 1D outputs (spec 5.2), on the image as given (corners are full-image pixels)
    finishLinear(fullGray, res.codes, p.check_digit);
    res.rejectedLength = found.rejectedLength.load();
    res.rejectedCheckDigit = found.rejectedCheckDigit.load();
    res.refusedEan = found.refusedEan.load();
    res.refusedAddon = found.refusedAddon.load();
    // per zone: how many AI regions read (the zone map shows where a line loses codes)
    if (learned)
        for (size_t i = 0; i < cands.size(); ++i) {
            if (!isAi[i]) continue;
            const int z = zoneOf(cands[i]);
            const bool ok = cropRead[i] || found.covers(cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y)), i < candSide.size() ? candSide[i] : 0.f);
            (ok ? learned->zoneRead : learned->zoneUnread)[static_cast<size_t>(z)]++;
        }

    // the lens blur, measured on a read code or two when time is left: the printer's pattern of
    // the code rendered through a Gaussian fits the pixels best at the lens's sigma; kept per zone
    // (a corner blurs more than the centre) and overall, as a slow average over cases
    if (learned && (p.format & CR_QR) && !res.codes.empty() && p.time_budget_ms - static_cast<int>(msSince(t0)) >= 25) {
        // the encoding already learned (a read-only look: learning it is the match pass's job),
        // else this case's first QR code gives it for the measurement alone
        QrEncoding enc;
        if (learned->encKind.load() >= 0 && learned->encFormat.load() == CR_QR) {
            enc.format = CR_QR;
            enc.kind = learned->encKind.load();
            enc.split = learned->encSplit.load();
            enc.ecl = learned->encEcl.load();
            enc.modules = learned->encModules.load();
        } else {
            for (const auto& c : res.codes)
                if (c.format == CR_QR && (enc = learnQrEncoding(c)).valid()) break;
        }
        if (enc.valid() && enc.format == CR_QR) {
            int measured = 0;
            const size_t start = learned->cases.load() % res.codes.size(); // other codes each case
            std::vector<uint8_t> dark, bestDark;
            for (size_t k = 0; k < res.codes.size() && measured < 2; ++k) {
                if (p.time_budget_ms - static_cast<int>(msSince(t0)) < 10) break;
                const Code& c = res.codes[(start + k) % res.codes.size()];
                if (c.format != CR_QR || c.modW != enc.modules || c.modW != c.modH || c.modules.empty()) continue;
                // the printed pattern: the mask whose encoding agrees with the decoder's grid
                size_t bestSame = 0;
                for (int mask = 0; mask < 8; ++mask) {
                    if (!encodeQr(c.text, enc, mask, dark) || dark.size() != c.modules.size()) break;
                    size_t same = 0;
                    for (size_t j = 0; j < dark.size(); ++j) same += dark[j] == c.modules[j];
                    if (same > bestSame) { bestSame = same; bestDark = dark; }
                }
                if (bestSame * 100 < c.modules.size() * 95) continue;
                cv::Point2f quad[4];
                for (int j = 0; j < 4; ++j) quad[j] = cv::Point2f(c.x[j], c.y[j]) - roiOffset;
                const float s = measurePsfSigma(gray, quad, bestDark.data(), c.modW, kPrintGrowth);
                if (s < 0.f) continue;
                // under the zone of the code's centre, where the matcher looks a region up
                const cv::Rect r(static_cast<int>((quad[0].x + quad[2].x) * 0.5f), static_cast<int>((quad[0].y + quad[2].y) * 0.5f), 1, 1);
                auto& zone = learned->psfSigma[static_cast<size_t>(zoneOf(r))];
                zone.store(zone.load() > 0.f ? 0.8f * zone.load() + 0.2f * s : s);
                learned->psfSigmaAll.store(learned->psfSigmaAll.load() > 0.f ? 0.9f * learned->psfSigmaAll.load() + 0.1f * s : s);
                ++measured;
            }
        }
    }

    // learn the product's Data Matrix size (modules and side in pixels) for the lattice step
    if (learned) {
        std::vector<int> mods;
        std::vector<float> sides;
        for (const auto& c : res.codes)
            if (c.format == CR_DATAMATRIX && c.modW >= 10 && c.modW == c.modH && c.step != CR_STEP_MATCH) {
                mods.push_back(c.modW);
                float sd = 0.f;
                for (int k = 0; k < 4; ++k) sd += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
                sides.push_back(sd / 4.f);
            }
        if (mods.size() >= 3) {
            std::nth_element(mods.begin(), mods.begin() + mods.size() / 2, mods.end());
            std::nth_element(sides.begin(), sides.begin() + sides.size() / 2, sides.end());
            learned->dmModules.store(mods[mods.size() / 2]);
            learned->dmSide.store(sides[sides.size() / 2]);
        }
    }
    // learn from this case: code-area size range and symbols per code (AI regions that read)
    if (learned) {
        std::vector<float> sides;
        for (size_t i = 0; i < cands.size(); ++i)
            if (isAi[i] && cropRead[i] && candSide[i] > 0) sides.push_back(candSide[i]);
        if (sides.size() >= 3 && !res.codes.empty()) {
            std::sort(sides.begin(), sides.end());
            const float mn = sides[sides.size() / 20], mx = sides[sides.size() - 1 - sides.size() / 20];
            // widen at once, narrow never: a code bigger or smaller than seen so far is still a code
            learned->codeMin.store(learned->codeMin.load() <= 0.f ? mn : std::min(learned->codeMin.load(), mn));
            learned->codeMax.store(std::max(learned->codeMax.load(), mx));
            int copies = 0;
            for (const auto& c : res.codes) {
                copies += c.copies;
                if (c.format == CR_QR && c.modW >= 21) learned->qrModules.store(c.modW);
            }
            const float spc = static_cast<float>(copies) / static_cast<float>(res.codes.size());
            const float old = learned->symbolsPerCode.load();
            // a case with copies over what the product says its label carries (an extra pack)
            // teaches nothing; one judged on the learned value still teaches, or a product whose
            // label changes would stay NG
            if (!(copiesFromHint && res.copiesOver > 0)) learned->symbolsPerCode.store(old <= 0.f ? spc : 0.7f * old + 0.3f * spc);
        }
    }
    for (size_t i = 0; i < cands.size(); ++i) {
        const cv::Rect r = cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y));
        res.regions.push_back({r, isAi[i] != 0, cropRead[i] != 0});
    }
    if (p.grade_quality) {
        res.quality.reserve(res.codes.size());
        // light-on-dark print is graded as read: dark modules dark (the flipped image)
        cv::Mat graded = fullGray;
        if (invertedImage) cv::bitwise_not(fullGray, graded);
        for (const auto& c : res.codes)
            // the matrix codes (QR, Micro QR, rMQR, Data Matrix, Aztec, DotCode) on their module grid; PDF417 by
            // rows; Pharmacode by scans as the 1D codes
            res.quality.push_back((c.format & (CR_ALL_LINEAR | CR_PHARMACODE | CR_PHARMACODE_REV)) ? gradeLinear(graded, c, models.calibration)
                                  : c.format == CR_PDF417 ? gradePdf417(graded, c, models.calibration)
                                  : (c.format & (CR_QR | CR_MICRO_QR | CR_RMQR | CR_DATAMATRIX | CR_AZTEC | CR_DOTCODE)) ? gradeCode(graded, c, models.calibration) : Quality{});
    }
    res.stats.decode_ms = static_cast<float>(msSince(t1));
    res.stats.total_ms = static_cast<float>(msSince(t0));
    res.stats.timed_out = res.stats.total_ms > p.time_budget_ms ? 1 : 0;

    // the product's polarity from this case's zxing reads
    if (learned && found.zxReads.load() >= 2)
        learned->polarity.store(found.invertedReads.load() * 2 > found.zxReads.load() ? 2 : 1);
    // misses: only meaningful when the case is short. The unread codes, not every unread region
    if (p.expected_count > 0 && res.stats.codes < p.expected_count) {
        std::vector<cv::Rect> boxes;
        std::vector<float> sides, scores;
        for (size_t i = 0; i < cands.size(); ++i) {
            if (cropRead[i]) continue;
            const cv::Rect r = cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y));
            const float side = i < candSide.size() ? candSide[i] : 0.f;
            if (containsCode(r, res.codes, side)) continue;
            boxes.push_back(r);
            sides.push_back(side);
            scores.push_back(i < candScore.size() ? candScore[i] : 0.f);
        }
        const float spc = learned ? learned->symbolsPerCode.load() : 0.f;
        const float lo = learned ? learned->codeMin.load() : 0.f, hi = learned ? learned->codeMax.load() : 0.f;
        for (size_t k : pickMisses(boxes, sides, scores, res.codes, p.expected_count - res.stats.codes, spc, lo, hi)) {
            if (static_cast<int>(res.misses.size()) >= 2 * p.expected_count) break;
            res.misses.push_back(boxes[k]);
            res.missSide.push_back(sides[k]); // for the miss diagnosis, same order
        }
    }
    // the case's slots: each miss gets its slot, and an empty slot with no miss gets one, so the
    // operator sees every gap (slots.cpp). A complete case on a lattice teaches the case's size
    {
        static const bool noSlots = std::getenv("CR_NO_SLOTS") != nullptr;
        std::vector<cv::Point2f> centres;
        float side = 0.f;
        centresOf(res.codes, centres, &side);
        const int la = learned ? learned->slotA.load() : 0, lb = learned ? learned->slotB.load() : 0;
        SlotGrid sg;
        if (!noSlots && p.expected_count > 0) {
            for (auto& c : centres) c -= roiOffset;
            sg = fitSlots(centres, la, lb, &gray, side, p.expected_count);
            for (auto& c : centres) c += roiOffset;
            if (sg.valid) sg.origin += roiOffset;
        }
        if (std::getenv("CR_SLOTS")) // diagnostic: the fitted lattice
            std::fprintf(stderr, "slots valid %d nodes %d x %d (from %d,%d) pitch %.0f a (%.1f,%.1f) b (%.1f,%.1f) off median %.2f max %.2f, %zu codes, learned %d x %d\n",
                         int(sg.valid), sg.ni, sg.nj, sg.i0, sg.j0, sg.pitch, sg.a.x, sg.a.y, sg.b.x, sg.b.y, sg.offMedian, sg.offMax, centres.size(), la, lb);
        if (learned && sg.valid && res.stats.codes == p.expected_count && sg.ni * sg.nj == p.expected_count && la == 0) {
            learned->slotA.store(std::min(sg.ni, sg.nj));
            learned->slotB.store(std::max(sg.ni, sg.nj));
        }
        res.missSlot.assign(res.misses.size(), cv::Point(0, 0));
        if (sg.valid && res.stats.codes < p.expected_count) {
            std::vector<char> listed;
            const auto empty = emptySlots(sg, centres);
            listed.assign(empty.size(), 0);
            for (size_t m = 0; m < res.misses.size(); ++m) {
                const cv::Point2f c(res.misses[m].x + res.misses[m].width / 2.f, res.misses[m].y + res.misses[m].height / 2.f);
                int i = 0, j = 0;
                float off = 0;
                if (!slotOf(sg, c, &i, &j, &off) || off > 0.5f) continue;
                for (size_t e = 0; e < empty.size(); ++e)
                    if (empty[e].first == i && empty[e].second == j) {
                        int row = 0, col = 0;
                        slotRowCol(sg, i, j, &row, &col);
                        res.missSlot[m] = cv::Point(row, col);
                        listed[e] = 1;
                    }
            }
            for (size_t e = 0; e < empty.size(); ++e) {
                if (listed[e] || static_cast<int>(res.misses.size()) >= 2 * p.expected_count) continue;
                const cv::Point2f c = slotCentreNear(sg, empty[e].first, empty[e].second, centres);
                const int half = static_cast<int>(std::max(side * 0.65f, 0.4f * sg.pitch));
                const cv::Rect r = cv::Rect(static_cast<int>(c.x) - half, static_cast<int>(c.y) - half, 2 * half, 2 * half) &
                                   cv::Rect(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y), gray.cols, gray.rows);
                if (r.area() <= 0) continue;
                int row = 0, col = 0;
                slotRowCol(sg, empty[e].first, empty[e].second, &row, &col);
                res.misses.push_back(r);
                res.missSide.push_back(side);
                res.missSlot.push_back(cv::Point(row, col));
            }
        }
    }
    // for the miss diagnosis: decoded code side / the AI's side, on the AI regions that read
    if (!res.misses.empty()) {
        std::vector<float> ratio;
        for (size_t i = 0; i < cands.size() && i < candSide.size() && ratio.size() < 15; ++i) {
            if (!isAi[i] || !cropRead[i] || candSide[i] <= 0.f) continue;
            const cv::Rect r = cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y));
            for (const auto& c : res.codes) {
                const cv::Point2f mid((c.x[0] + c.x[2]) / 2.f, (c.y[0] + c.y[2]) / 2.f);
                if (!r.contains(cv::Point(static_cast<int>(mid.x), static_cast<int>(mid.y)))) continue;
                float sd = 0.f;
                for (int k = 0; k < 4; ++k) sd += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
                ratio.push_back(sd / 4.f / candSide[i]);
                break;
            }
        }
        if (ratio.size() >= 3) {
            std::nth_element(ratio.begin(), ratio.begin() + static_cast<long>(ratio.size() / 2), ratio.end());
            res.aiToCode = ratio[ratio.size() / 2];
        }
    }
    res.stats.misses = static_cast<int>(res.misses.size());
    // diagnostic: every region and what became of it (CR_CANDS=1)
    if (std::getenv("CR_CANDS"))
        for (size_t i = 0; i < cands.size(); ++i) {
            const cv::Rect r = cands[i] + cv::Point(static_cast<int>(roiOffset.x), static_cast<int>(roiOffset.y));
            // src: which step placed the region (tools/bench/locator_recall.py)
            const char* src = i < aiCount ? "ai" : i < static_cast<size_t>(res.stats.candidates) ? "classical" : i < slotFrom ? "ai-recall" : "slot";
            std::fprintf(stderr, "cand %zu box %d %d %dx%d ai %d read %d covered %d side %.0f score %.2f src %s\n", i, r.x, r.y, r.width, r.height, int(isAi[i]), int(cropRead[i]),
                         int(containsCode(r, res.codes, i < candSide.size() ? candSide[i] : 0.f)), i < candSide.size() ? candSide[i] : 0.f,
                         i < candScore.size() ? candScore[i] : 0.f, src);
        }
    return res;
}

} // namespace cr
