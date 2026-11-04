// grade1d.cpp - 1D print quality after ISO/IEC 15416 (linear symbols): ten scan reflectance
// profiles across the bars, each graded on Rmin, symbol contrast, minimum edge contrast,
// modulation, defects, decodability (per symbology, decodability.cpp), decode and quiet zones; a scan's grade is its
// worst parameter, the symbol's grade the mean of its scans. GS1 DataBar: the single-row variants
// (Omnidirectional, Truncated, Limited, Expanded), each scan's variant from its own decode. The
// stacked ones (Stacked, Stacked Omnidirectional, Expanded Stacked) row by row (ISO/IEC 24724): the
// rows found between their separator patterns, ten scans inside each row clear of its separators,
// each scan decoded together with the other rows (no row decodes alone), a row's grade the mean of
// its scans and the symbol's the lowest row's.
//
// Pharmacode (Laetus one-track) has no print quality standard of its own: graded here the same way, as
// verifiers grade it, with its own decoder (pharmacode.cpp) as each scan's reference decode, its
// decodability from the bar + gap pitches (decodability.cpp), X its unit (the narrow bar) from the
// symbol's length and value, and its decoder's quiet zones: 4 gaps (8 X) of light on both sides.
//
// The profiles come from the band of bars around the code (linear.cpp), sampled through a
// measuring aperture of 0.8 X (the X-dimension from the code itself). With a reflectance
// calibration (calibration.cpp) the values are reflectance %, as a verifier's; without, grey as a
// share of full scale (process control, not certified verification).
#include "reader.hpp"
#include "zxformats.hpp"

#include <ZXing/ReadBarcode.h>

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <numeric>
#include <cstdio>
#include <cstdlib>

namespace cr {

namespace {

int gradeHigh(float v, float a, float b, float c, float d) { return v >= a ? 4 : v >= b ? 3 : v >= c ? 2 : v >= d ? 1 : 0; }
int gradeLow(float v, float a, float b, float c, float d) { return v <= a ? 4 : v <= b ? 3 : v <= c ? 2 : v <= d ? 1 : 0; }

struct Scan {
    float rmin = 0, rmax = 0, sc = 0, ecmin = 0, mod = 0, defects = 0, decodability = -1;
    bool decoded = false, quietOk = false;
    int grade = 0, gradeRmin = 0;
    // DataBar: decodability read forward and mirrored (the orientation is chosen for the whole row,
    // never per scan); Limited: the light before the first and after the last edge, in X (its 5 light
    // guard modules are on the right of the symbol as read)
    float dbFwd = -1, dbRev = -1, lightBeforeX = 0, lightAfterX = 0;
    bool limited = false;
    // a stacked row's scan at its global threshold, light outside the symbol, aligned with the other
    // rows' scans: decoded with them
    cv::Mat bits;
};

// element edges of a profile at the global threshold (sub-sample by linear interpolation)
std::vector<float> edgesOf(const std::vector<float>& r, float gt)
{
    std::vector<float> e;
    for (size_t i = 1; i < r.size(); ++i)
        // a sample exactly on the threshold counts on the light side, so a crossing is never missed
        if ((r[i - 1] < gt) != (r[i] < gt)) e.push_back(static_cast<float>(i - 1) + (gt - r[i - 1]) / (r[i] - r[i - 1]));
    return e;
}

// a scan's grade: its worst parameter
void finishScan(Scan& s)
{
    const int gRmin = s.gradeRmin = s.rmin <= 0.5f * s.rmax ? 4 : 0;
    const int gSC = gradeHigh(s.sc, 70, 55, 40, 20);
    const int gEC = s.ecmin >= 15.f ? 4 : 0;
    const int gMOD = gradeHigh(s.mod, 0.70f, 0.60f, 0.50f, 0.40f);
    const int gDef = gradeLow(s.defects, 0.15f, 0.20f, 0.25f, 0.30f);
    // a scan whose elements do not fit its type (a speck in the quiet zone, a void across a bar) or
    // that did not read the code grades F here
    const int gDec = s.decodability >= 0 ? gradeHigh(s.decodability, 0.62f, 0.50f, 0.37f, 0.25f) : 0;
    s.grade = std::min({gRmin, gSC, gEC, gMOD, gDef, gDec, s.decoded ? 4 : 0, s.quietOk ? 4 : 0});
    if (std::getenv("CR_GRADE1D")) std::fprintf(stderr, "scan rmin %.0f rmax %.0f sc %.0f ec %.0f mod %.2f def %.2f dec %.2f decoded %d quiet %d -> %d %d %d %d %d %d\n",
        s.rmin, s.rmax, s.sc, s.ecmin, s.mod, s.defects, s.decodability, int(s.decoded), int(s.quietOk), gRmin, gSC, gEC, gMOD, gDef, gDec);
}

// DataBar: one orientation for the whole row, the one its scans read better in (a defect in one scan
// never turns that scan around); then each scan's grade
void finishDataBar(std::vector<Scan>& scans)
{
    float fwd = 0.f, rev = 0.f;
    for (const auto& s : scans) { fwd += std::max(0.f, s.dbFwd); rev += std::max(0.f, s.dbRev); }
    const bool mirrored = rev > fwd;
    for (auto& s : scans) {
        s.decodability = mirrored ? s.dbRev : s.dbFwd;
        if (s.limited) s.quietOk = s.quietOk && (mirrored ? s.lightBeforeX : s.lightAfterX) >= 5.f;
        finishScan(s);
    }
}

// stackedRow: a row of a stacked DataBar, decoded later with the other rows (Scan::bits); its
// decodability by the row's element count (24 edges a top row, 26 a bottom row, else Expanded).
// in0, in1: the samples inside the image; a quiet zone reaching past them is measured only to the
// image's edge (beyond it the edge pixels repeat), so one the edge cuts short fails (line safety)
Scan gradeScan(const std::vector<float>& r, size_t in0, size_t in1, float xSamples, const std::string& text, int format, bool stackedRow = false)
{
    Scan s;
    if (r.size() < 32) return s;
    // the symbol: the run of edges around the scan's middle with no gap wider than 6 X (no element
    // of these codes is; a quiet zone is 10 X); DataBar 10 X (its finders hold 8 X elements, it needs
    // no quiet zone beyond its guards). The scan line may run on past the label onto a darker
    // carton: that edge is not the symbol's
    // Pharmacode: 4.5 X (its widest element is a 3 X wide bar; its quiet zone 8 X)
    const bool pharma = (format & (CR_PHARMACODE | CR_PHARMACODE_REV)) != 0;
    const float gapX = format == CR_DATABAR ? 10.f : pharma ? 4.5f : 6.f;
    const auto symbolEdges = [&](const std::vector<float>& all, size_t& before, size_t& after) {
        std::vector<float> e;
        if (all.empty()) return e;
        const float mid = static_cast<float>(r.size()) / 2.f;
        size_t i0 = 0;
        for (size_t k = 0; k < all.size(); ++k) if (std::abs(all[k] - mid) < std::abs(all[i0] - mid)) i0 = k;
        size_t lo = i0, hi = i0;
        while (lo > 0 && all[lo] - all[lo - 1] <= gapX * xSamples) --lo;
        while (hi + 1 < all.size() && all[hi + 1] - all[hi] <= gapX * xSamples) ++hi;
        before = lo > 0 ? static_cast<size_t>(std::ceil(all[lo - 1])) : 0;
        after = hi + 1 < all.size() ? static_cast<size_t>(std::floor(all[hi + 1])) + 1 : r.size();
        return std::vector<float>(all.begin() + static_cast<long>(lo), all.begin() + static_cast<long>(hi) + 1);
    };
    size_t q0 = 0, q1 = r.size();
    {
        const auto [mn, mx] = std::minmax_element(r.begin(), r.end());
        symbolEdges(edgesOf(r, (*mn + *mx) / 2.f), q0, q1);
    }
    // reflectances over the symbol and its quiet zones only
    s.rmin = *std::min_element(r.begin() + static_cast<long>(q0), r.begin() + static_cast<long>(q1));
    s.rmax = *std::max_element(r.begin() + static_cast<long>(q0), r.begin() + static_cast<long>(q1));
    s.sc = s.rmax - s.rmin;
    const float gt = (s.rmax + s.rmin) / 2.f;
    size_t before = 0, after = r.size();
    const std::vector<float> e = symbolEdges(edgesOf(r, gt), before, after);
    if (e.size() < 4 || s.sc <= 0) return s;
    // the code's extent: first to last edge; the quiet zones outside, to the next edge or the scan's end
    const size_t a = static_cast<size_t>(std::floor(e.front())), b = static_cast<size_t>(std::ceil(e.back()));
    // quiet zones in X, left and right: EAN / UPC have their own (ISO/IEC 15420), DataBar none beyond
    // its guards' light module (ISO/IEC 24724; Limited's right guard holds 5, checked after the
    // decode names the variant), the others 10 X
    const auto [qzL, qzR] = format == CR_EAN13 ? std::pair{11.f, 7.f} : format == CR_EAN8 ? std::pair{7.f, 7.f}
                            : format == CR_UPCA ? std::pair{9.f, 9.f} : format == CR_UPCE ? std::pair{9.f, 7.f}
                            : format == CR_DATABAR ? std::pair{1.f, 1.f} : pharma ? std::pair{8.f, 8.f} : std::pair{10.f, 10.f};
    const float qBefore = static_cast<float>(std::max(before, in0)), qAfter = static_cast<float>(std::min(after, in1));
    s.quietOk = e.front() - qBefore >= qzL * xSamples && qAfter - e.back() >= qzR * xSamples &&
                r[a > 0 ? a - 1 : 0] > gt; // the symbol starts with a bar after a light quiet zone
    // elements: between consecutive edges, plus the quiet zones as the outer spaces
    std::vector<std::pair<size_t, size_t>> el;
    el.emplace_back(before, a);
    for (size_t k = 0; k + 1 < e.size(); ++k) el.emplace_back(static_cast<size_t>(std::ceil(e[k])), static_cast<size_t>(std::floor(e[k + 1])) + 1);
    el.emplace_back(b, after);
    std::vector<float> ext; // each element's extreme: a space its brightest, a bar its darkest
    float ern = 0.f;
    for (size_t k = 0; k < el.size(); ++k) {
        const auto [x0, x1] = el[k];
        if (x1 <= x0) { ext.push_back(gt); continue; }
        const bool bar = k % 2 == 1; // the profile starts in the light quiet zone
        float lo = 1e9f, hi = -1e9f;
        for (size_t i = x0; i < x1 && i < r.size(); ++i) { lo = std::min(lo, r[i]); hi = std::max(hi, r[i]); }
        ext.push_back(bar ? lo : hi);
        // element reflectance non-uniformity (ISO 15416): a valley inside a space with higher
        // profile on both sides of it (a void in the paper's white), or a peak inside a bar with
        // lower profile on both sides (a void in the ink). The slopes at an element's edges are
        // not defects: they are monotonic, so they hold no such valley or peak.
        if (x1 > x0 + 2) {
            const size_t m = std::min(x1, r.size()) - x0;
            std::vector<float> left(m), right(m);
            for (size_t i = 0; i < m; ++i) left[i] = i ? (bar ? std::min(left[i - 1], r[x0 + i]) : std::max(left[i - 1], r[x0 + i])) : r[x0];
            for (size_t i = m; i-- > 0;) right[i] = i + 1 < m ? (bar ? std::min(right[i + 1], r[x0 + i]) : std::max(right[i + 1], r[x0 + i])) : r[x0 + i];
            for (size_t i = 1; i + 1 < m; ++i) {
                const float v = bar ? r[x0 + i] - std::max(left[i - 1], right[i + 1]) : std::min(left[i - 1], right[i + 1]) - r[x0 + i];
                ern = std::max(ern, v);
            }
        }
    }
    s.ecmin = 1e9f;
    for (size_t k = 0; k + 1 < ext.size(); ++k) s.ecmin = std::min(s.ecmin, std::abs(ext[k] - ext[k + 1]));
    s.mod = s.ecmin / s.sc;
    s.defects = ern / s.sc;
    s.lightBeforeX = (e.front() - qBefore) / xSamples;
    s.lightAfterX = (qAfter - e.back()) / xSamples;
    if (stackedRow) {
        const DataBarKind k = e.size() == 24 ? DataBarKind::StackedTop : e.size() == 26 ? DataBarKind::StackedBottom : DataBarKind::Expanded;
        s.dbFwd = decodabilityDataBar(e, k);
        s.dbRev = decodabilityDataBar(e, k, true);
        cv::Mat full(1, static_cast<int>(r.size()), CV_8U, cv::Scalar(255));
        for (size_t i = before; i < after; ++i) full.at<uint8_t>(0, static_cast<int>(i)) = r[i] < gt ? 0 : 255;
        s.bits = full;
        return s;
    }
    if (pharma) {
        // decode with the engine's own decoder, locked to the bar count the value names, in the
        // direction the code was read (the profile runs from corner 0 to corner 1, as the decoder's):
        // the symbol and its quiet zones scaled to full range, with light margins
        const int n = static_cast<int>(pharmacodeBars(format, text).size());
        const size_t pad = static_cast<size_t>(std::lround(12.f * xSamples));
        std::vector<float> row(after - before + 2 * pad, 255.f);
        for (size_t i = before; i < after; ++i) row[i - before + pad] = std::clamp((r[i] - s.rmin) * 255.f / s.sc, 0.f, 255.f);
        const int v = n >= 2 ? decodePharmacodeProfile(row, static_cast<float>(pad), static_cast<float>(pad + after - before), n, n,
                                                       format == CR_PHARMACODE_REV, nullptr, nullptr) : -1;
        s.decoded = v >= 0 && std::to_string(v) == text;
        s.decodability = decodability1d(e, format, text);
        finishScan(s);
        return s;
    }
    // decode: this scan line's symbol and its quiet zones alone (not the carton beyond the label),
    // with light margins, at about 6 samples per module, as a clean row for zxing. The quiet zones
    // are graded above; here only whether the bars read back to the code's text
    const int pad = static_cast<int>(std::lround(12.f * xSamples));
    cv::Mat row(1, static_cast<int>(after - before) + 2 * pad, CV_8U, cv::Scalar(255));
    for (size_t i = before; i < after; ++i)
        row.at<uint8_t>(0, static_cast<int>(i - before) + pad) = cv::saturate_cast<uint8_t>((r[i] - s.rmin) * 255.f / s.sc);
    if (xSamples > 6.f) cv::resize(row, row, cv::Size(std::max(32, static_cast<int>(std::lround(static_cast<float>(row.cols) * 6.f / xSamples))), 1), 0, 0, cv::INTER_AREA);
    // at the global threshold, as the standard decodes a scan (its edges are the ones graded above):
    // grey edge pixels left to zxing's own binarizer move an edge by a fraction of a module, which
    // DataBar's character widths do not tolerate
    cv::threshold(row, row, 127, 255, cv::THRESH_BINARY);
    cv::Mat img;
    cv::repeat(row, 16, 1, img);
    ZXing::ReaderOptions o;
    o.setFormats(toZx(format));
    o.setTextMode(ZXing::TextMode::Plain);
    o.setTryRotate(false);
    const auto z = ZXing::ReadBarcodes(ZXing::ImageView(img.data, img.cols, img.rows, ZXing::ImageFormat::Lum), o);
    // the text the reader kept may have had its check digit stripped (Code 39, ITF, Codabar)
    s.decoded = !z.empty() && (fromZx(z[0].format()) & format) && readMatchesKept(format, z[0].text(), text);
    if (format != CR_DATABAR) {
        s.decodability = decodability1d(e, format, text);
        finishScan(s);
    } else if (s.decoded) {
        // the variant this scan read (Omnidirectional and a two-pair Expanded have the same element
        // count); a scan that did not read the code has no variant and is not measured. Graded in
        // finishDataBar, once the symbol's orientation is known
        const auto f = z[0].format();
        using F = ZXing::BarcodeFormat;
        const auto both = [&](DataBarKind k) { s.dbFwd = decodabilityDataBar(e, k); s.dbRev = decodabilityDataBar(e, k, true); };
        if (f == F::DataBarOmni) both(DataBarKind::Omni);
        else if (f == F::DataBarExp) both(DataBarKind::Expanded);
        else if (f == F::DataBarLtd) { both(DataBarKind::Limited); s.limited = true; }
    }
    return s;
}

// the rows of a stacked symbol about the band, as offsets across it (from, to) in image pixels, in order.
// Lines 0.5 px apart along the bars, each at its own threshold: a row is a run of lines that agree
// with its middle line on 90% of their samples (a void or a speck changes a few), at least 2.5 X
// tall; the separator patterns between rows (1 to 3 modules of other patterns) are not rows.
// reach: how far across the bars from the band's middle the code's outline goes (the band of bars may
// hold one row only where a separator pattern splits it)
std::vector<std::pair<float, float>> stackedRows(const cv::Mat& g32, const LinearBand& b, float X, float reach)
{
    const cv::Point2f e(-b.dir.y, b.dir.x);
    const float half = b.length / 2 + 2.f * X, hv = std::max(b.height / 2, reach) + 4.f * X;
    const int n = static_cast<int>(2 * half), m = static_cast<int>(4 * hv);
    if (n < 16 || m < 4) return {};
    cv::Mat mx(m, n, CV_32F), my(m, n, CV_32F), img;
    for (int j = 0; j < m; ++j)
        for (int i = 0; i < n; ++i) {
            const float t = -half + static_cast<float>(i), v = -hv + 0.5f * static_cast<float>(j);
            mx.at<float>(j, i) = b.centre.x + t * b.dir.x + v * e.x;
            my.at<float>(j, i) = b.centre.y + t * b.dir.y + v * e.y;
        }
    cv::remap(g32, img, mx, my, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    std::vector<std::vector<uint8_t>> bits(static_cast<size_t>(m));
    std::vector<float> contrast(static_cast<size_t>(m));
    for (int j = 0; j < m; ++j) {
        double lo = 0, hi = 0;
        cv::minMaxLoc(img.row(j), &lo, &hi);
        contrast[static_cast<size_t>(j)] = static_cast<float>(hi - lo);
        for (int i = 0; i < n; ++i) bits[static_cast<size_t>(j)].push_back(img.at<float>(j, i) < (lo + hi) / 2 ? 1 : 0);
    }
    const auto agree = [&](int a, int c) {
        int same = 0;
        for (int i = 0; i < n; ++i) same += bits[static_cast<size_t>(a)][static_cast<size_t>(i)] == bits[static_cast<size_t>(c)][static_cast<size_t>(i)];
        return static_cast<float>(same) / static_cast<float>(n);
    };
    const float maxContrast = *std::max_element(contrast.begin(), contrast.end());
    std::vector<std::pair<float, float>> rows;
    int done = -1;
    for (int i = 0; i < m;) {
        int j = i;
        while (j + 1 < m && agree(j + 1, i) >= 0.9f) ++j;
        if (0.5f * static_cast<float>(j - i) >= 2.5f * X) {
            // again from the run's middle line, so a first line on a row's blurred edge does not set it
            const int mid = (i + j) / 2;
            int lo = mid, hi = mid;
            while (lo - 1 > done && agree(lo - 1, mid) >= 0.9f) --lo;
            while (hi + 1 < m && agree(hi + 1, mid) >= 0.9f) ++hi;
            // a row holds bars: not the even paper above or below the symbol
            if (contrast[static_cast<size_t>(mid)] >= 0.5f * maxContrast) rows.emplace_back(-hv + 0.5f * static_cast<float>(lo), -hv + 0.5f * static_cast<float>(hi));
            done = hi;
            i = hi + 1;
        } else {
            i = j + 1;
        }
    }
    return rows;
}

} // namespace

Quality gradeLinear(const cv::Mat& gray, const Code& c, const Calibration* cal)
{
    Quality q;
    const bool pharma = (c.format & (CR_PHARMACODE | CR_PHARMACODE_REV)) != 0;
    if (!(c.format & CR_ALL_LINEAR) && !pharma) return q;
    const cv::Point2f centre((c.x[0] + c.x[1] + c.x[2] + c.x[3]) / 4.f, (c.y[0] + c.y[1] + c.y[2] + c.y[3]) / 4.f);
    float len = 0.f;
    for (int k = 0; k < 4; ++k) len = std::max(len, static_cast<float>(std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k])));
    // the band of bars around the code: a crop of about twice its length
    const int half = static_cast<int>(std::max(60.f, len * 1.1f));
    const cv::Rect roi = cv::Rect(static_cast<int>(centre.x) - half, static_cast<int>(centre.y) - half, 2 * half, 2 * half) & cv::Rect(0, 0, gray.cols, gray.rows);
    if (roi.area() <= 0) return q;
    constexpr int kSub = 4;
    cv::Mat g32;
    gray.convertTo(g32, CV_32F);
    // the middle scan line of a band, 0.75 of its length either side, and its edges at the line's
    // mid-grey (lo, hi out: the line's darkest and lightest)
    const auto middleEdges = [&](const LinearBand& b, std::vector<float>& r0, double* lo, double* hi) {
        const float ext0 = b.length * 0.75f;
        const int n0 = static_cast<int>(2 * ext0 * kSub);
        r0.clear();
        if (n0 < 8) return std::vector<float>{};
        cv::Mat mx(1, n0, CV_32F), my(1, n0, CV_32F), line;
        for (int i = 0; i < n0; ++i) {
            const float t = -ext0 + static_cast<float>(i) / kSub;
            mx.at<float>(0, i) = b.centre.x + t * b.dir.x;
            my.at<float>(0, i) = b.centre.y + t * b.dir.y;
        }
        cv::remap(g32, line, mx, my, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
        cv::minMaxLoc(line, lo, hi);
        r0.assign(line.begin<float>(), line.end<float>());
        return edgesOf(r0, static_cast<float>((*lo + *hi) / 2));
    };
    // a band's X from the narrow bars and spaces of its middle scan line (print gain does not change
    // it); Pharmacode: half its median element (its gaps are 2 units, its bars 1 or 3) as a first
    // unit. 0 when the line crosses no bars (a label's or a stacked symbol's row edges make bands too)
    const auto barsX = [&](const LinearBand& b) {
        std::vector<float> r0;
        double lo = 0, hi = 0;
        const auto all = middleEdges(b, r0, &lo, &hi);
        if (!pharma) return std::max(0.f, moduleFromEdges(all) / kSub);
        if (all.size() < 4) return 0.f;
        std::vector<float> w;
        for (size_t k = 1; k < all.size(); ++k) w.push_back(all[k] - all[k - 1]);
        std::nth_element(w.begin(), w.begin() + static_cast<long>(w.size() / 2), w.end());
        return w[w.size() / 2] / 2.f / kSub;
    };
    // the band with bars nearest the code (in image pixels) and its X
    const auto nearest = [&](std::vector<LinearBand> bands, float* Xo) {
        for (auto& b : bands) b.centre += cv::Point2f(static_cast<float>(roi.x), static_cast<float>(roi.y));
        std::sort(bands.begin(), bands.end(), [&](const LinearBand& x, const LinearBand& y) { return cv::norm(x.centre - centre) < cv::norm(y.centre - centre); });
        for (const auto& b : bands)
            if ((*Xo = barsX(b)) > 0.f) return b;
        *Xo = 0.f;
        return LinearBand{};
    };
    const auto bands = findLinearBands(gray(roi), 3);
    float X = 0.f;
    LinearBand b;
    if (!bands.empty()) b = nearest(bands, &X);
    // nothing with bars at half or full size: a large X can leave only the label's edges or a stacked
    // symbol's row edges as bands there; looked for again at a quarter and an eighth. Still nothing:
    // not graded (never a grade made up without bars)
    for (const double f : {0.25, 0.125}) {
        if (X > 0.f) break;
        const auto coarse = findLinearBandsAt(gray(roi), f, 3);
        if (!coarse.empty()) b = nearest(coarse, &X);
    }
    if (X <= 0.f) return q;
    // a large X: wide bars tens of px across leave the band in pieces at these scales (a piece of a
    // row scanned is no grade of the symbol). The band grows along its middle scan line while bars go
    // on, every gap between edges at most 9 X (DataBar's widest element is 8 modules; a quiet zone
    // is wider), at the threshold of the piece's own bars; never shorter than the piece. Not for
    // Pharmacode, whose run of bars is measured below
    if (!pharma && X > 5.f) {
        const float reachT = static_cast<float>(std::hypot(roi.width, roi.height));
        const int nl = static_cast<int>(2 * reachT * kSub);
        cv::Mat mx(1, nl, CV_32F), my(1, nl, CV_32F), line;
        for (int i = 0; i < nl; ++i) {
            const float t = -reachT + static_cast<float>(i) / kSub;
            mx.at<float>(0, i) = b.centre.x + t * b.dir.x;
            my.at<float>(0, i) = b.centre.y + t * b.dir.y;
        }
        cv::remap(g32, line, mx, my, cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(-1));
        std::vector<float> r(line.begin<float>(), line.end<float>());
        // the piece's samples set the threshold; outside the image (-1) ends the bars
        const int p0 = static_cast<int>((reachT - b.length / 2) * kSub), p1 = static_cast<int>((reachT + b.length / 2) * kSub);
        float lo = 1e9f, hi = -1e9f;
        for (int i = std::max(0, p0); i < std::min(nl, p1); ++i)
            if (r[static_cast<size_t>(i)] >= 0.f) { lo = std::min(lo, r[static_cast<size_t>(i)]); hi = std::max(hi, r[static_cast<size_t>(i)]); }
        for (auto& v : r)
            if (v < 0.f) v = hi;
        const auto edges = edgesOf(r, (lo + hi) / 2);
        if (hi > lo && edges.size() >= 4) {
            const float mid = reachT * kSub, gap = 9.f * X * kSub;
            // the edge nearest the piece's middle, then outwards over gaps of bars and spaces
            size_t k = 0;
            for (size_t i = 1; i < edges.size(); ++i)
                if (std::abs(edges[i] - mid) < std::abs(edges[k] - mid)) k = i;
            size_t a = k, z = k;
            while (a > 0 && edges[a] - edges[a - 1] <= gap) --a;
            while (z + 1 < edges.size() && edges[z + 1] - edges[z] <= gap) ++z;
            const float t0 = edges[a] / kSub - reachT, t1 = edges[z] / kSub - reachT, span = t1 - t0 + 2.f * X;
            if (span > b.length) {
                b.centre += b.dir * ((t0 + t1) / 2);
                b.length = span;
            }
        }
    }
    // Pharmacode: in the direction its decoder read it (its value hangs on the direction), corner 0 to
    // corner 1 of the code, whatever reading direction the job set
    if (pharma && b.dir.dot(cv::Point2f(c.x[1] - c.x[0], c.y[1] - c.y[0])) < 0) b.dir = -b.dir;
    std::vector<float> pharmaRun; // Pharmacode: its bars' edges on the middle line, px from the band's centre
    if (pharma) {
        // the run of edges round the middle with no element over 4.5 units, from a bar's leading edge,
        // then the unit from its length and the bars its value names
        std::vector<float> r0;
        double lo = 0, hi = 0;
        const auto all = middleEdges(b, r0, &lo, &hi);
        if (all.size() < 4) return q;
        const float ext0 = b.length * 0.75f, u0 = X * kSub;
        const float mid = static_cast<float>(r0.size()) / 2.f;
        size_t i0 = 0;
        for (size_t k = 0; k < all.size(); ++k) if (std::abs(all[k] - mid) < std::abs(all[i0] - mid)) i0 = k;
        size_t a = i0, z = i0;
        while (a > 0 && all[a] - all[a - 1] <= 4.5f * u0) --a;
        while (z + 1 < all.size() && all[z + 1] - all[z] <= 4.5f * u0) ++z;
        const float thr = static_cast<float>((lo + hi) / 2);
        if (r0[static_cast<size_t>(std::max(0.f, std::floor(all[a])))] < thr) ++a; // starts on a bar's trailing edge
        if (z > a && (z - a) % 2 == 0) --z;                                          // ends on a leading edge
        pharmaRun.assign(all.begin() + static_cast<long>(a), all.begin() + static_cast<long>(z) + 1);
        for (auto& t : pharmaRun) t = -ext0 + t / kSub; // px along the band from its centre
        const float u = z > a ? pharmacodeModule(pharmaRun, c.format, c.text) : 0.f;
        if (u > 0.f) X = u;
    }
    const cv::Point2f e(-b.dir.y, b.dir.x);
    // a scan line `off` px across the bars from the band's middle, through a 0.8 X aperture, with the
    // quiet zones too; every scan line of the symbol the same length and aligned along the bars
    const float extent = b.length / 2 + 0.1f * b.length + 12.f * X;
    const int n = static_cast<int>(2 * extent * kSub);
    // in0, in1 (out): the samples inside the image
    const auto profile = [&](float off, float Xs, size_t* in0 = nullptr, size_t* in1 = nullptr) {
        cv::Mat mx(1, n, CV_32F), my(1, n, CV_32F);
        for (int i = 0; i < n; ++i) {
            const float t = -extent + static_cast<float>(i) / kSub;
            mx.at<float>(0, i) = b.centre.x + t * b.dir.x + off * e.x;
            my.at<float>(0, i) = b.centre.y + t * b.dir.y + off * e.y;
        }
        // the aperture: a disc average, applied to the image once per scan line's neighbourhood
        cv::Mat line;
        cv::remap(g32, line, mx, my, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
        std::vector<float> r(static_cast<size_t>(n));
        const int k = std::max(1, static_cast<int>(std::lround(0.8f * Xs * kSub)));
        for (int i = 0; i < n; ++i) {
            double s = 0;
            int cnt = 0;
            for (int d = -k / 2; d <= k / 2; ++d)
                if (i + d >= 0 && i + d < n) { s += line.at<float>(0, i + d); ++cnt; }
            const cv::Point2f p(mx.at<float>(0, i), my.at<float>(0, i));
            const float grey = static_cast<float>(s / std::max(1, cnt));
            r[static_cast<size_t>(i)] = cal ? toReflectance(*cal, p, grey) : grey / 255.f * 100.f;
        }
        const auto inside = [&](int i) {
            const float x = mx.at<float>(0, i), y = my.at<float>(0, i);
            return x >= 0.f && y >= 0.f && x <= static_cast<float>(g32.cols - 1) && y <= static_cast<float>(g32.rows - 1);
        };
        int lo = 0, hi = n;
        while (lo < n && !inside(lo)) ++lo;
        while (hi > lo && !inside(hi - 1)) --hi;
        if (in0) *in0 = static_cast<size_t>(lo);
        if (in1) *in1 = static_cast<size_t>(hi);
        return r;
    };
    // ten scan lines across the bars, from 10% to 90% of the bar height
    float v0 = -0.5f * b.height, v1 = 0.5f * b.height;
    if (pharma && pharmaRun.size() >= 4 && pharmaRun.size() % 2 == 0) {
        // Pharmacode: the band finder's height over a few sparse bars is rough; the bars' own extent
        // across, the median over its bars of the dark run through each bar's middle
        const float reach = 2.f * std::max(b.height, b.length);
        const int m = static_cast<int>(8 * reach);
        std::vector<float> tops, bottoms;
        for (size_t k = 0; k + 1 < pharmaRun.size(); k += 2) {
            const float t = (pharmaRun[k] + pharmaRun[k + 1]) / 2;
            cv::Mat mx(1, m, CV_32F), my(1, m, CV_32F), line;
            for (int j = 0; j < m; ++j) {
                const float v = -reach + 0.25f * static_cast<float>(j);
                mx.at<float>(0, j) = b.centre.x + t * b.dir.x + v * e.x;
                my.at<float>(0, j) = b.centre.y + t * b.dir.y + v * e.y;
            }
            cv::remap(g32, line, mx, my, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
            double lo = 0, hi = 0;
            cv::minMaxLoc(line, &lo, &hi);
            const float thr = static_cast<float>((lo + hi) / 2);
            int j0 = m / 2, j1 = m / 2;
            if (line.at<float>(0, j0) >= thr) continue;
            while (j0 > 0 && line.at<float>(0, j0 - 1) < thr) --j0;
            while (j1 + 1 < m && line.at<float>(0, j1 + 1) < thr) ++j1;
            tops.push_back(-reach + 0.25f * static_cast<float>(j0));
            bottoms.push_back(-reach + 0.25f * static_cast<float>(j1));
        }
        if (!tops.empty()) {
            std::nth_element(tops.begin(), tops.begin() + static_cast<long>(tops.size() / 2), tops.end());
            std::nth_element(bottoms.begin(), bottoms.begin() + static_cast<long>(bottoms.size() / 2), bottoms.end());
            if (bottoms[bottoms.size() / 2] - tops[tops.size() / 2] >= 2.f * X) { v0 = tops[tops.size() / 2]; v1 = bottoms[bottoms.size() / 2]; }
        }
    }
    std::vector<std::vector<Scan>> rows(1);
    std::vector<float> rowX{X};
    for (int l = 0; l < 10; ++l) {
        size_t in0 = 0, in1 = 0;
        const auto r = profile(v0 + (0.1f + 0.8f * l / 9.f) * (v1 - v0), X, &in0, &in1);
        rows[0].push_back(gradeScan(r, in0, in1, X * kSub, c.text, c.format));
    }
    if (c.format == CR_DATABAR) finishDataBar(rows[0]);
    // DataBar: a stacked variant's row reads as no single-row variant in any scan: graded row by row
    if (c.format == CR_DATABAR && std::none_of(rows[0].begin(), rows[0].end(), [](const Scan& s) { return s.decodability >= 0; })) {
        rows.clear();
        rowX.clear();
        float reach = 0.f;
        for (int k = 0; k < 4; ++k) reach = std::max(reach, std::abs((cv::Point2f(c.x[k], c.y[k]) - b.centre).dot(e)));
        for (const auto& [v0, v1] : stackedRows(g32, b, X, reach)) {
            // the row's own X from its middle line; ten scans from 10% to 90% of its height, clear of
            // the separator patterns above and below it
            const auto mid = profile((v0 + v1) / 2, X);
            const auto [lo, hi] = std::minmax_element(mid.begin(), mid.end());
            const float Xr = moduleFromEdges(edgesOf(mid, (*lo + *hi) / 2)) / kSub;
            if (Xr <= 0.f) continue;
            std::vector<Scan> row;
            for (int l = 0; l < 10; ++l) {
                size_t in0 = 0, in1 = 0;
                const auto r = profile(v0 + (0.1f + 0.8f * l / 9.f) * (v1 - v0), Xr, &in0, &in1);
                row.push_back(gradeScan(r, in0, in1, Xr * kSub, c.text, c.format, true));
            }
            // a row holds a DataBar row's elements in most of its scans
            if (std::count_if(row.begin(), row.end(), [](const Scan& s) { return std::max(s.dbFwd, s.dbRev) >= 0; }) < 5) continue;
            rows.push_back(std::move(row));
            rowX.push_back(Xr);
        }
        // fewer than two rows is no stacked symbol: not graded (no grade rather than a made-up F; a
        // lowest grade set on it then fails the code)
        if (rows.size() < 2) return q;
        // no row decodes alone: each scan with the other rows' best scans (their highest
        // decodability), as one symbol of rows. A row that never reads leaves every scan of the
        // others undecoded too: the symbol does not decode, it grades F
        std::vector<size_t> best(rows.size(), 0);
        for (size_t k = 0; k < rows.size(); ++k)
            for (size_t l = 0; l < rows[k].size(); ++l)
                if (std::max(rows[k][l].dbFwd, rows[k][l].dbRev) > std::max(rows[k][best[k]].dbFwd, rows[k][best[k]].dbRev)) best[k] = l;
        ZXing::ReaderOptions o;
        o.setFormats(toZx(CR_DATABAR));
        o.setTextMode(ZXing::TextMode::Plain);
        o.setTryRotate(true);
        // every row at the same scale, about 6 samples per module, so the rows stay aligned
        for (auto& row : rows)
            for (auto& s : row) {
                if (X * kSub > 6.f) cv::resize(s.bits, s.bits, cv::Size(std::max(32, static_cast<int>(std::lround(static_cast<float>(n) * 6.f / (X * kSub)))), 1), 0, 0, cv::INTER_AREA);
                cv::threshold(s.bits, s.bits, 127, 255, cv::THRESH_BINARY);
            }
        for (size_t k = 0; k < rows.size(); ++k)
            for (auto& s : rows[k]) {
                const int w = s.bits.cols;
                cv::Mat img(static_cast<int>(rows.size()) * 12 + 16, w, CV_8U, cv::Scalar(255));
                bool same = true;
                for (size_t j = 0; j < rows.size(); ++j) {
                    const cv::Mat& bits = j == k ? s.bits : rows[j][best[j]].bits;
                    same = same && bits.cols == w;
                    if (!same) break;
                    for (int y = 0; y < 12; ++y) bits.copyTo(img.row(8 + static_cast<int>(j) * 12 + y));
                }
                const auto z = same ? ZXing::ReadBarcodes(ZXing::ImageView(img.data, img.cols, img.rows, ZXing::ImageFormat::Lum), o) : ZXing::Barcodes{};
                s.decoded = !z.empty() && (fromZx(z[0].format()) & CR_DATABAR) && readMatchesKept(CR_DATABAR, z[0].text(), c.text);
            }
        for (auto& row : rows) finishDataBar(row);
    }
    // a grade as the mean of a row's scans' grades, as ISO/IEC 15416 forms the symbol's
    const auto meanGrade = [](const std::vector<Scan>& row, auto f) {
        float avg = 0;
        for (const auto& s : row) avg += static_cast<float>(f(s));
        avg /= static_cast<float>(row.size());
        return avg >= 3.5f ? 4 : avg >= 2.5f ? 3 : avg >= 1.5f ? 2 : avg >= 0.5f ? 1 : 0;
    };
    // the symbol's grade: its row's (a stacked DataBar: the lowest row's, ISO/IEC 24724), its
    // parameters that row's means
    size_t worst = 0;
    for (size_t k = 1; k < rows.size(); ++k)
        if (meanGrade(rows[k], [](const Scan& s) { return s.grade; }) < meanGrade(rows[worst], [](const Scan& s) { return s.grade; })) worst = k;
    const auto& scans = rows[worst];
    const auto mean = [&](auto f) { float s = 0; for (const auto& x : scans) s += f(x); return s / scans.size(); };
    q.symbolContrast = mean([](const Scan& s) { return s.sc; });
    q.modulation = mean([](const Scan& s) { return s.mod; });
    q.edgeContrastMin = mean([](const Scan& s) { return s.ecmin; });
    q.defects = mean([](const Scan& s) { return s.defects; });
    // decodability: the mean over the scans, a scan that could not measure it counting 0
    q.decodability = mean([](const Scan& s) { return std::max(0.f, s.decodability); });
    q.rMin = mean([](const Scan& s) { return s.rmin; });
    q.rMax = mean([](const Scan& s) { return s.rmax; });
    q.moduleSizePx = std::accumulate(rowX.begin(), rowX.end(), 0.f) / static_cast<float>(rowX.size());
    q.xDimMm = cal && cal->mmPerPx > 0.f ? q.moduleSizePx * cal->mmPerPx : 0.f;
    q.calibrated = cal && cal->reflectance;
    q.gradeSC = gradeHigh(q.symbolContrast, 70, 55, 40, 20);
    q.gradeMOD = gradeHigh(q.modulation, 0.70f, 0.60f, 0.50f, 0.40f);
    q.gradeEC = q.edgeContrastMin >= 15.f ? 4 : 0;
    q.gradeDefects = gradeLow(q.defects, 0.15f, 0.20f, 0.25f, 0.30f);
    q.gradeDecodability = q.decodability < 0 ? -1 : gradeHigh(q.decodability, 0.62f, 0.50f, 0.37f, 0.25f);
    q.overall = meanGrade(scans, [](const Scan& s) { return s.grade; });
    // Rmin and the quiet zones are graded per scan only: their means name them when they pull the
    // overall grade down (the lowest row's)
    q.gradeRmin = q.gradeQZ = 4;
    for (const auto& row : rows) {
        q.gradeRmin = std::min(q.gradeRmin, meanGrade(row, [](const Scan& s) { return s.gradeRmin; }));
        q.gradeQZ = std::min(q.gradeQZ, meanGrade(row, [](const Scan& s) { return s.quietOk ? 4 : 0; }));
    }
    // every scan line, row by row (ten per row)
    for (const auto& row : rows)
        for (const auto& s : row) {
            cr_scan out{};
            out.r_min = s.rmin; out.r_max = s.rmax; out.symbol_contrast = s.sc; out.edge_contrast_min = s.ecmin;
            out.modulation = s.mod; out.defects = s.defects; out.decodability = s.decodability;
            out.decoded = s.decoded ? 1 : 0;
            out.quiet_zone_ok = s.quietOk ? 1 : 0;
            out.grade_rmin = s.gradeRmin;
            out.grade_quiet_zone = s.quietOk ? 4 : 0;
            out.grade = s.grade;
            q.scanLines.push_back(out);
            ++q.scans;
            q.decodedScans += s.decoded ? 1 : 0;
        }
    q.linear = true;
    q.measured = true;
    return q;
}

} // namespace cr
