// quality.cpp - print quality of a checked line (OCV, tool spec 7.3 and 7.5).
//
// The recogniser says whether a character is the right one; it does not say whether it is printed
// well: a faint character, or one with a third of its dots missing, is often still read with
// confidence. So each aligned character is also measured in the straightened line image:
//   contrast  grey levels between its ink and the paper around it, in the camera image
//   ink       ink pixels in its cell (missing strokes or dots lower it, a smear raises it)
//   pitch     distance from the previous character's ink centre (a printer that slips or jumps)
//   position  where the line's first character sits in the region (Allow shift: a printer head or
//             label that moved), learned as the mean over good parts
// Limits come from good parts (mean +- k sigma, tool spec 1). A measure scores 100 at the learned
// mean and min_quality at its limit, so one rule (every character >= min_quality) covers the
// recogniser and the print. Until enough good parts are learned only the recogniser decides.

#include "ocr.hpp"
#include "vs_ocr.h"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <sstream>

namespace ocr {
namespace {

// Spread floors: a print repeated almost exactly (or drawn) must not give limits thinner than the
// measure's own noise.
constexpr double kInkFloor = 0.06, kInkFloorPx = 2;          // of the mean, and pixels
constexpr double kContrastFloor = 0.05, kContrastFloorGrey = 3;
constexpr double kPitchFloor = 0.03, kPitchFloorPx = 1;
constexpr double kGapPitch = 1.5;     // a learned distance this many typical pitches is a word gap

double sdFloor(const Stat& st, double frac, double abs) { return std::max({st.sd(), frac * st.mean(), abs}); }

// 100 at the mean, minQ at mean - k sd (dev = how far it is on the bad side), 0 below
float scoreOf(double dev, double band, float minQ)
{
    if (dev <= 0 || band <= 0) return 100.f;
    return float(std::clamp(100.0 - (100.0 - minQ) * dev / band, 0.0, 100.0));
}

double median(std::vector<double> v)
{
    if (v.empty()) return 0;
    std::nth_element(v.begin(), v.begin() + std::ptrdiff_t(v.size() / 2), v.end());
    return v[v.size() / 2];
}

} // namespace

double Stat::sd() const
{
    if (n < 2) return 0;
    const double m = s / n;
    return std::sqrt(std::max(0.0, (ss - n * m * m) / (n - 1)));
}

std::string PrintRef::pitchKey(int line, size_t index, size_t length)
{
    return std::to_string(line) + "/" + std::to_string(index) + "/" + std::to_string(length);
}

void PrintRef::add(const CheckResult& cr, int line)
{
    for (size_t k = 0; k < cr.chars.size(); ++k) {
        const CheckChar& c = cr.chars[k];
        if (!c.measured) continue;
        ink[c.expected].add(c.ink);
        contrast[c.expected].add(c.contrast);
        if (c.expected != 0) contrast[0].add(c.contrast);
        if (k > 0 && cr.chars[k - 1].measured) pitch[pitchKey(line, k, cr.chars.size())].add(c.pitch);
    }
    if (cr.posKnown) {
        posX[line].add(cr.posX);
        posY[line].add(cr.posY);
    }
}

std::string PrintRef::save() const
{
    std::ostringstream o;
    char b[160];
    std::snprintf(b, sizeof b, "VSPRINT1 %d %.9g\n", parts, k);
    o << b;
    auto put = [&](const char* tag, const std::string& key, const Stat& st) {
        std::snprintf(b, sizeof b, "%s %s %.9g %.17g %.17g\n", tag, key.c_str(), st.n, st.s, st.ss);
        o << b;
    };
    for (const auto& [c, st] : ink) put("I", std::to_string(uint32_t(c)), st);
    for (const auto& [c, st] : contrast) put("C", std::to_string(uint32_t(c)), st);
    for (const auto& [key, st] : pitch) put("P", key, st);
    for (const auto& [line, st] : posX) put("X", std::to_string(line), st);
    for (const auto& [line, st] : posY) put("Y", std::to_string(line), st);
    return o.str();
}

bool PrintRef::load(const std::string& text, std::string& err)
{
    PrintRef r;
    std::istringstream in(text);
    std::string head;
    if (!(in >> head >> r.parts >> r.k) || head != "VSPRINT1" || r.parts < 0 || !(r.k > 0 && r.k <= 10)) {
        err = "not a print quality reference";
        return false;
    }
    std::string tag, key;
    Stat st;
    while (in >> tag) {
        if (!(in >> key >> st.n >> st.s >> st.ss) || st.n < 0 || !std::isfinite(st.s) || !std::isfinite(st.ss)) {
            err = "damaged print quality reference";
            return false;
        }
        if (tag == "P") { r.pitch[key] = st; continue; }
        char* end = nullptr;
        const unsigned long c = std::strtoul(key.c_str(), &end, 10);
        if (tag == "X" || tag == "Y") {
            if (!end || *end || c > 63 || st.n < 1) { err = "damaged print quality reference"; return false; }
            (tag == "X" ? r.posX : r.posY)[int(c)] = st;
            continue;
        }
        if (!end || *end || c > 0x10FFFF || (tag != "I" && tag != "C")) { err = "damaged print quality reference"; return false; }
        (tag == "I" ? r.ink : r.contrast)[char32_t(c)] = st;
    }
    *this = std::move(r);
    return true;
}

void scorePrint(const Line& line, const std::vector<ReadChar>& read, CheckResult& cr, const PrintRef& ref,
                int lineNo, float minQ)
{
    const cv::Mat& img = line.image;
    const size_t n = cr.chars.size();
    cr.passPrint = cr.pass;
    if (img.empty() || n == 0) return;

    // cells: from midway to the previous aligned character to midway to the next, at most 0.6 of
    // the typical pitch either side (a word gap is not part of a character)
    std::vector<int> idx;
    std::vector<double> mid;
    for (size_t k = 0; k < n; ++k) {
        CheckChar& c = cr.chars[k];
        c.recognition = c.score;
        if (c.t0 < 0) continue;
        idx.push_back(int(k));
        mid.push_back(0.5 * (c.t0 + c.t1 + 1) * kStride);
    }
    std::vector<double> gaps;
    for (size_t i = 1; i < mid.size(); ++i) gaps.push_back(mid[i] - mid[i - 1]);
    const double pitchTyp = gaps.empty() ? 0.5 * kLineH : std::max(4.0, median(gaps));
    const double half = 0.6 * pitchTyp;

    cv::Mat bin;
    cv::threshold(img, bin, 0, 255, cv::THRESH_BINARY_INV | cv::THRESH_OTSU);   // ink = 255
    const double toGrey = (line.rawHi - line.rawLo) / 255.0;
    double firstEdge = 0, lastEdge = img.cols;
    for (size_t i = 0; i < idx.size(); ++i) {
        CheckChar& c = cr.chars[size_t(idx[i])];
        const double l = i == 0 ? mid[i] - half : std::max(mid[i] - half, 0.5 * (mid[i - 1] + mid[i]));
        const double r = i + 1 == idx.size() ? mid[i] + half : std::min(mid[i] + half, 0.5 * (mid[i] + mid[i + 1]));
        if (i == 0) firstEdge = l;
        if (i + 1 == idx.size()) lastEdge = r;
        const int x0 = std::clamp(int(std::floor(l)), 0, img.cols), x1 = std::clamp(int(std::ceil(r)), 0, img.cols);
        double inkN = 0, inkSum = 0, paperN = 0, paperSum = 0, cx = 0;
        for (int y = 0; y < img.rows; ++y) {
            const uint8_t* g = img.ptr<uint8_t>(y);
            const uint8_t* m = bin.ptr<uint8_t>(y);
            for (int x = x0; x < x1; ++x) {
                if (m[x]) { inkN += 1; inkSum += g[x]; cx += x; }
                else { paperN += 1; paperSum += g[x]; }
            }
        }
        c.measured = true;
        c.ink = float(inkN);
        c.contrast = inkN >= 3 && paperN >= 3 ? float((paperSum / paperN - inkSum / inkN) * toGrey) : 0.f;
        c.centre = float(inkN > 0 ? cx / inkN : mid[i]);
    }
    for (size_t k = 1; k < n; ++k)
        if (cr.chars[k].measured && cr.chars[k - 1].measured) cr.chars[k].pitch = cr.chars[k].centre - cr.chars[k - 1].centre;

    // against the learned limits
    if (ref.active()) {
        const double band = ref.k;
        for (size_t k = 0; k < n; ++k) {
            CheckChar& c = cr.chars[k];
            if (!c.measured) continue;
            if (auto it = ref.ink.find(c.expected); it != ref.ink.end() && it->second.n >= PrintRef::kMinSamples && c.expected != 0) {
                const double m = it->second.mean(), sd = sdFloor(it->second, kInkFloor, kInkFloorPx);
                c.qInk = std::min(scoreOf(m - c.ink, band * sd, minQ), scoreOf(c.ink - m, band * sd, minQ));
                c.learned = true;
            }
            auto ct = ref.contrast.find(c.expected);
            if (ct == ref.contrast.end() || ct->second.n < PrintRef::kMinSamples) ct = ref.contrast.find(0);
            if (ct != ref.contrast.end() && ct->second.n >= PrintRef::kMinSamples) {
                const double m = ct->second.mean(), sd = sdFloor(ct->second, kContrastFloor, kContrastFloorGrey);
                c.qContrast = scoreOf(m - c.contrast, band * sd, minQ);
                c.learned = true;
            }
            // not across a word gap: where the alignment puts a gap's blank columns moves the next cell, so
            // that distance varies more than the print does (a good part failed on it 1 time in 11000 characters)
            if (k > 0 && cr.chars[k - 1].measured)
                if (auto pt = ref.pitch.find(PrintRef::pitchKey(lineNo, k, n));
                    pt != ref.pitch.end() && pt->second.n >= PrintRef::kMinSamples && pt->second.mean() < kGapPitch * pitchTyp) {
                    const double m = pt->second.mean(), sd = sdFloor(pt->second, kPitchFloor, kPitchFloorPx);
                    c.qPitch = scoreOf(std::abs(c.pitch - m), band * sd, minQ);
                    c.learned = true;
                }
        }
    }

    // the character's score is its lowest; the reason is the first failing one in cause order: a
    // character out of place or faint also loses ink in its cell, and missing ink makes it unclear
    float q = 100.f;
    for (CheckChar& c : cr.chars) {
        const auto inkRef = ref.ink.find(c.expected);
        const bool thin = inkRef != ref.ink.end() && c.ink < inkRef->second.mean();
        c.score = std::min({c.recognition, c.qContrast, c.qInk, c.qPitch});
        c.reason = c.score >= minQ ? VS_PRINT_OK
                 : c.qPitch < minQ ? VS_PRINT_POSITION
                 : c.qContrast < minQ ? VS_PRINT_FAINT
                 : c.qInk < minQ ? (thin ? VS_PRINT_BROKEN : VS_PRINT_SMEARED)
                 : VS_PRINT_UNCLEAR;
        if (c.status == VS_CHAR_OK && c.score < minQ) c.status = VS_CHAR_UNSURE;   // the right character, printed poorly
        q = std::min(q, c.score);
    }

    // print beyond the expected text: a character read with confidence outside the first and last cells
    cr.extras.clear();
    if (!idx.empty())
        for (const ReadChar& rc : read) {
            if (rc.c == U' ' || 100.f * rc.conf < minQ) continue;
            const double x = 0.5 * (rc.t0 + rc.t1 + 1) * kStride;
            if (x < firstEdge || x > lastEdge) cr.extras.push_back(rc);
        }
    cr.quality = n ? q : 0.f;
    cr.pass = cr.pass && q >= minQ && cr.extras.empty();
    cr.passPrint = cr.pass;
}

void scoreShift(const Line& line, CheckResult& cr, const PrintRef& ref, int lineNo, cv::Point2f roiOrigin, float allowPx)
{
    cr.posKnown = false;
    cr.shiftState = VS_SHIFT_OFF;
    cr.shift = -1;
    cr.shiftX = cr.shiftY = 0;
    for (const CheckChar& c : cr.chars) {
        if (!c.measured) continue;
        const cv::Vec2d X = line.toImage * cv::Vec3d(c.centre, 0.5 * kLineH, 1);
        cr.posX = float(X[0]) - roiOrigin.x;
        cr.posY = float(X[1]) - roiOrigin.y;
        cr.posKnown = true;
        break;
    }
    if (!(allowPx > 0)) return;
    const auto ix = ref.posX.find(lineNo), iy = ref.posY.find(lineNo);
    if (ix == ref.posX.end() || iy == ref.posY.end() || ix->second.n < 1 || !cr.posKnown) {
        // Allow shift is set: a line whose place is not known (none learned, or nothing measured) is NOT OK
        cr.shiftState = VS_SHIFT_UNLEARNED;
        cr.pass = false;
        return;
    }
    cr.shiftX = cr.posX - float(ix->second.mean());
    cr.shiftY = cr.posY - float(iy->second.mean());
    cr.shift = std::hypot(cr.shiftX, cr.shiftY);
    cr.shiftState = cr.shift <= allowPx ? VS_SHIFT_OK : VS_SHIFT_BEYOND;
    if (cr.shiftState == VS_SHIFT_BEYOND) cr.pass = false;
}

} // namespace ocr
