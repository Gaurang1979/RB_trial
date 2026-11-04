// grade_dpm.cpp - direct part mark (DPM) quality of Data Matrix and QR codes after ISO/IEC TR 29158
// (AIM DPM-1-2006): codes dot-peened, laser-etched or ink-marked on metal and plastic parts, where
// the ISO/IEC 15415 measures (symbol contrast against a white reference, reflectance margin)
// grade a readable mark F.
//
// What changes against 15415 (quality.cpp):
//  - the image is graded as the camera sees it under the line's lighting (90 = diffuse on-axis,
//    30Q / 30T / 30S = 30 degrees from four, two or one side, D = dome), with the light cells'
//    mean (ML) between 70 % and 86 % of full scale; ML is reported so the exposure can be set;
//  - cells are sampled through a synthetic aperture of 50 % or 80 % of the cell (X), as a circle
//    at the cell centre; the overall grade comes from one of them or the better of the two;
//  - dot-peen marks: the gaps between the dots of one dark row are closed (a grey closing of the
//    mark sized from the widest gap found along a solid fixed-pattern row) before sampling;
//  - Cell contrast CC = (ML - MD) / ML on the cell centres; Cell modulation from each cell's
//    distance to the global threshold (ML + MD) / 2, graded against the error correction as the
//    15415 modulation; Minimum reflectance from the image-based reflectance calibration:
//    Rtarget = Rcal x (SRcal / SRtarget) x (MLtarget / MLcal), where a calibration symbol of known
//    light reflectance Rcal was imaged at system response SRcal (exposure x gain) with mean light
//    MLcal, and the part at SRtarget with mean light MLtarget;
//  - Fixed pattern damage per segment (Data Matrix: the two L sides, the two clock tracks and the
//    quiet zone; QR: each finder with its separator and the two timing patterns), and Distributed
//    damage, the mean of the segment grades (a symbol with many slightly damaged segments grades
//    lower than its worst segment alone says);
//  - Axial and grid non-uniformity and unused error correction as 15415 (gradeCode).
// Marks may be light on dark (laser on anodised parts, peened dots under 30-degree light): the
// polarity is taken from the cells, and "light" and "dark" below are the image's.
//
// Like quality.cpp these are process-control grades from camera grey levels: Minimum reflectance
// needs the calibration. Cell modulation is graded per codeword as 15415 does: a codeword takes
// the lowest grade of its modules (the real codeword placement and blocks, codewords.cpp), and at
// each grade level the codewords under it are errors against their block's error correction. Sizes
// without a codeword table (DMRE) fall back to one wrong cell = one codeword against the whole
// symbol's correction.
//
// Grades: 4 = A, 3 = B, 2 = C, 1 = D, 0 = F, -1 = not measured.

#include "reader.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <numeric>
#include <string>

namespace cr {

namespace {

int gradeHigh(float v, float a, float b, float c, float d) // larger is better
{
    return v >= a ? 4 : v >= b ? 3 : v >= c ? 2 : v >= d ? 1 : 0;
}

int gradeLow(float v, float a, float b, float c, float d) // smaller is better
{
    return v <= a ? 4 : v <= b ? 3 : v <= c ? 2 : v <= d ? 1 : 0;
}

// The mean grey level in a circle of diameter `diam` px at p (the synthetic aperture); bilinear at
// p when the circle is under a pixel. -1 when p is outside the image.
float aperture(const cv::Mat& g, cv::Point2f p, float diam)
{
    if (p.x < 0 || p.y < 0 || p.x > g.cols - 1 || p.y > g.rows - 1) return -1.f;
    const float r = diam / 2.f;
    if (r < 0.75f) {
        const int x0 = std::min(g.cols - 2, static_cast<int>(p.x)), y0 = std::min(g.rows - 2, static_cast<int>(p.y));
        if (x0 < 0 || y0 < 0) return g.at<uint8_t>(static_cast<int>(p.y), static_cast<int>(p.x));
        const float fx = p.x - x0, fy = p.y - y0;
        return (1 - fy) * ((1 - fx) * g.at<uint8_t>(y0, x0) + fx * g.at<uint8_t>(y0, x0 + 1))
             + fy * ((1 - fx) * g.at<uint8_t>(y0 + 1, x0) + fx * g.at<uint8_t>(y0 + 1, x0 + 1));
    }
    const int x0 = std::max(0, static_cast<int>(std::floor(p.x - r))), x1 = std::min(g.cols - 1, static_cast<int>(std::ceil(p.x + r)));
    const int y0 = std::max(0, static_cast<int>(std::floor(p.y - r))), y1 = std::min(g.rows - 1, static_cast<int>(std::ceil(p.y + r)));
    double s = 0;
    int n = 0;
    for (int y = y0; y <= y1; ++y)
        for (int x = x0; x <= x1; ++x) {
            const float dx = x - p.x, dy = y - p.y;
            if (dx * dx + dy * dy > r * r) continue;
            s += g.at<uint8_t>(y, x);
            ++n;
        }
    return n ? static_cast<float>(s / n) : -1.f;
}

// One fixed-pattern segment: its cells (index into the grid, or -1 - k for quiet-zone cell k)
// and what each must be (true = mark).
struct Segment {
    const char* name;
    std::vector<int> cells;
    std::vector<bool> mark;
    bool byCount = false; // QR finders: graded by the number of damaged cells (ISO/IEC 18004)
};

std::vector<Segment> segments(int format, int w, int h)
{
    std::vector<Segment> s;
    auto at = [&](int x, int y) { return y * w + x; };
    if (format == CR_DATAMATRIX) {
        Segment l1{"L1", {}, {}}, l2{"L2", {}, {}}, ct{"clock top", {}, {}}, cr_{"clock right", {}, {}};
        for (int y = 0; y < h; ++y) { l1.cells.push_back(at(0, y)); l1.mark.push_back(true); }
        for (int x = 0; x < w; ++x) { l2.cells.push_back(at(x, h - 1)); l2.mark.push_back(true); }
        for (int x = 0; x < w; ++x) { ct.cells.push_back(at(x, 0)); ct.mark.push_back(x % 2 == 0); }
        for (int y = 0; y < h; ++y) { cr_.cells.push_back(at(w - 1, y)); cr_.mark.push_back((h - 1 - y) % 2 == 0); }
        s = {l1, l2, ct, cr_};
    }
    else if (format == CR_QR) {
        auto finder = [&](const char* name, int x0, int y0) {
            Segment f{name, {}, {}, true};
            for (int y = -1; y <= 7; ++y)
                for (int x = -1; x <= 7; ++x) {
                    if (x0 + x < 0 || y0 + y < 0 || x0 + x >= w || y0 + y >= h) continue;
                    const int d = std::max(std::abs(x - 3), std::abs(y - 3));
                    f.cells.push_back(at(x0 + x, y0 + y));
                    f.mark.push_back(!(d == 4 || d == 2));
                }
            s.push_back(f);
        };
        finder("finder top left", 0, 0);
        finder("finder top right", w - 7, 0);
        finder("finder bottom left", 0, h - 7);
        Segment th{"timing row", {}, {}}, tv{"timing column", {}, {}};
        for (int i = 8; i < w - 8; ++i) {
            th.cells.push_back(at(i, 6)); th.mark.push_back(i % 2 == 0);
            tv.cells.push_back(at(6, i)); tv.mark.push_back(i % 2 == 0);
        }
        s.push_back(th);
        s.push_back(tv);
    }
    return s;
}

// A segment's grade from its damaged cells: QR finders by count (0 A, 1 B, 2 C, 3 D), the others
// by share (ISO/IEC 16022 L sides and clock tracks: 0 % A, 9 % B, 13 % C, 17 % D)
int segmentGrade(const Segment& s, int damaged)
{
    if (s.byCount) return damaged <= 0 ? 4 : damaged == 1 ? 3 : damaged == 2 ? 2 : damaged == 3 ? 1 : 0;
    return gradeLow(static_cast<float>(damaged) / std::max<size_t>(1, s.cells.size()), 0.f, 0.09f, 0.13f, 0.17f);
}

// ISO/IEC 15415 modulation against the error correction: for each level g, the codewords graded
// under g are errors in their block, the notional UEC at g = the lowest over the blocks of
// 1 - 2 x errors / (check codewords - misdecode protection) caps that level's grade, and the best
// level wins.
int gradeCodewords(const std::vector<int>& cwGrades, const CodewordMap& m)
{
    int best = 0;
    for (int g = 1; g <= 4; ++g) {
        std::vector<int> errors(m.blockEcc.size(), 0);
        for (size_t k = 0; k < cwGrades.size(); ++k)
            if (cwGrades[k] < g) ++errors[static_cast<size_t>(m.block[k])];
        float uec = 1.f;
        for (size_t b = 0; b < errors.size(); ++b) {
            const int cap = std::max(1, m.blockEcc[b] - m.blockP[b]);
            uec = std::min(uec, 1.f - 2.f * errors[b] / cap);
        }
        best = std::max(best, std::min(g, gradeHigh(std::max(0.f, uec), 0.62f, 0.50f, 0.37f, 0.25f)));
    }
    return best;
}

// Without a codeword table: each cell under g is taken to damage one codeword, against the whole
// symbol's error correction share.
int gradeAgainstEc(const std::vector<int>& cellGrades, float codewords, float ec)
{
    int best = 0;
    for (int g = 1; g <= 4; ++g) {
        const float under = static_cast<float>(std::count_if(cellGrades.begin(), cellGrades.end(), [g](int v) { return v < g; }));
        const float uec = std::clamp(1.f - (under / codewords) / (ec / 2.f), 0.f, 1.f);
        best = std::max(best, std::min(g, gradeHigh(uec, 0.62f, 0.50f, 0.37f, 0.25f)));
    }
    return best;
}

struct ApertureResult {
    bool ok = false;
    float ml = 0, md = 0, cc = 0, cm = 0, fpd = 0, dd = 0, mr = -1;
    int gCC = -1, gCM = -1, gMR = -1, gFPD = -1, overall = -1;
};

} // namespace

// The widest light gap (px) between the dots of a row that must be solid mark: the Data Matrix
// bottom L side, or the QR top-left finder's top row; 0 when the row is solid (printed, laser).
static float dotGapPx(const cv::Mat& gray, const Code& c, const cv::Mat& H, float pitch, bool lightMarks, float gt)
{
    cv::Point2f a, b;
    if (c.format == CR_DATAMATRIX) { a = {0.5f, c.modH - 0.5f}; b = {c.modW - 0.5f, c.modH - 0.5f}; }
    else { a = {0.5f, 0.5f}; b = {6.5f, 0.5f}; }
    std::vector<cv::Point2f> in{a, b}, out;
    cv::perspectiveTransform(in, out, H);
    const float len = static_cast<float>(cv::norm(out[1] - out[0]));
    const int n = static_cast<int>(len * 2) + 1; // half-pixel steps
    float run = 0, widest = 0;
    for (int k = 0; k < n; ++k) {
        const cv::Point2f p = out[0] + (out[1] - out[0]) * (static_cast<float>(k) / std::max(1, n - 1));
        const float v = aperture(gray, p, 0.f);
        if (v < 0) continue;
        const bool gap = lightMarks ? v < gt : v > gt;
        run = gap ? run + 0.5f : 0.f;
        widest = std::max(widest, run);
    }
    // a gap of a cell or more is a missing dot (damage), not the spacing of the dots
    return widest >= pitch * 0.9f ? 0.f : widest;
}

DpmQuality gradeDpm(const cv::Mat& gray, const Code& c, const DpmSettings& s, const Calibration* cal)
{
    DpmQuality q;
    q.lighting = s.lighting;
    if ((c.format != CR_DATAMATRIX && c.format != CR_QR) || c.modules.empty() || c.modW < 8 || c.modH < 8) return q;
    const int w = c.modW, h = c.modH;
    if (c.modules.size() != static_cast<size_t>(w) * h) return q;

    const cv::Point2f src[4] = {{0.f, 0.f}, {static_cast<float>(w), 0.f}, {static_cast<float>(w), static_cast<float>(h)},
                                {0.f, static_cast<float>(h)}};
    const cv::Point2f dst[4] = {{c.x[0], c.y[0]}, {c.x[1], c.y[1]}, {c.x[2], c.y[2]}, {c.x[3], c.y[3]}};
    const cv::Mat H = homography4(src, dst);
    const float pitch = static_cast<float>((cv::norm(dst[1] - dst[0]) + cv::norm(dst[2] - dst[3])) / (2.0 * w)
                                           + (cv::norm(dst[3] - dst[0]) + cv::norm(dst[2] - dst[1])) / (2.0 * h)) / 2.f;
    if (pitch < 1.5f) return q; // under 1.5 px a cell: no aperture to speak of
    q.cellPx = pitch;

    std::vector<cv::Point2f> centres, pts;
    centres.reserve(static_cast<size_t>(w) * h);
    for (int y = 0; y < h; ++y)
        for (int x = 0; x < w; ++x) centres.emplace_back(x + 0.5f, y + 0.5f);
    cv::perspectiveTransform(centres, pts, H);
    // the quiet zone ring (Data Matrix, 1 cell): must be background
    std::vector<cv::Point2f> qzIn, qzPts;
    if (c.format == CR_DATAMATRIX)
        for (int y = -1; y <= h; ++y)
            for (int x = -1; x <= w; ++x)
                if (x < 0 || y < 0 || x >= w || y >= h) qzIn.emplace_back(x + 0.5f, y + 0.5f);
    if (!qzIn.empty()) cv::perspectiveTransform(qzIn, qzPts, H);

    // polarity: the decoder's dark cells (modules 1) are the mark; light marks on a dark part read
    // inverted. A first look through the 80 % aperture.
    double sumMark = 0, sumBack = 0;
    int nMark = 0, nBack = 0;
    for (size_t i = 0; i < pts.size(); ++i) {
        const float v = aperture(gray, pts[i], 0.8f * pitch);
        if (v < 0) return q; // partly outside the image
        (c.modules[i] ? sumMark : sumBack) += v;
        (c.modules[i] ? nMark : nBack) += 1;
    }
    if (!nMark || !nBack) return q;
    q.inverted = sumMark / nMark > sumBack / nBack;
    const float gt0 = static_cast<float>((sumMark / nMark + sumBack / nBack) / 2.0);

    // dot peen: close the gaps between the dots of the mark (the mark is dark: a grey opening; light:
    // a grey closing), with a disc just wider than the widest gap along a solid row
    cv::Mat img = gray;
    if (s.closing >= 0) {
        const float gap = s.closing > 0 ? static_cast<float>(s.closing) : dotGapPx(gray, c, H, pitch, q.inverted, gt0);
        if (gap > 0.f) {
            const int k = static_cast<int>(std::ceil(gap)) | 1;
            const cv::Mat disc = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(k, k));
            float bx0 = dst[0].x, by0 = dst[0].y, bx1 = bx0, by1 = by0;
            for (const auto& d : dst) { bx0 = std::min(bx0, d.x); by0 = std::min(by0, d.y); bx1 = std::max(bx1, d.x); by1 = std::max(by1, d.y); }
            cv::Rect box(static_cast<int>(bx0), static_cast<int>(by0), static_cast<int>(bx1 - bx0) + 1, static_cast<int>(by1 - by0) + 1);
            const int margin = static_cast<int>(3 * pitch) + k;
            box = cv::Rect(box.x - margin, box.y - margin, box.width + 2 * margin, box.height + 2 * margin) & cv::Rect(0, 0, gray.cols, gray.rows);
            img = gray.clone();
            cv::Mat roi = img(box);
            cv::morphologyEx(roi, roi, q.inverted ? cv::MORPH_CLOSE : cv::MORPH_OPEN, disc);
            q.closingPx = static_cast<float>(k);
        }
    }

    // UEC, axial and grid non-uniformity as 15415, on the image with dark marks
    // (a new buffer when inverted: img may be the caller's pixels, never written)
    cv::Mat darkMarks;
    if (q.inverted) cv::bitwise_not(img, darkMarks);
    else darkMarks = img;
    const Quality base = gradeCode(darkMarks, c, cal);
    if (!base.measured) return q;
    q.axialNonUniformity = base.axialNonUniformity;
    q.gridNonUniformity = base.gridNonUniformity;
    q.unusedErrorCorrection = base.unusedErrorCorrection;
    q.gradeAN = base.gradeAN;
    q.gradeGN = base.gradeGN;
    q.gradeUEC = base.gradeUEC;

    // error correction share, as quality.cpp: QR by its level, Data Matrix about 30 %
    float ec = 0.3f;
    if (c.format == CR_QR) {
        const std::string& l = c.ecLevel;
        ec = l.find('L') != std::string::npos ? 0.07f : l.find('M') != std::string::npos ? 0.15f
           : l.find('Q') != std::string::npos ? 0.25f : 0.30f;
    }
    const auto segs = segments(c.format, w, h);
    int fixedN = 0;
    std::vector<bool> fixed(pts.size(), false);
    for (const auto& sg : segs)
        for (int i : sg.cells) if (!fixed[static_cast<size_t>(i)]) { fixed[static_cast<size_t>(i)] = true; ++fixedN; }
    const float codewords = std::max(1.f, static_cast<float>(pts.size() - static_cast<size_t>(fixedN)) / 8.f);
    CodewordMap cwMap;
    const bool perCodeword = codewordMap(c.format, w, h, c.ecLevel, cwMap);
    q.perCodeword = perCodeword;

    const bool haveCal = s.rCal > 0.f && s.mlCal > 0.f && s.srCal > 0.f;
    auto measure = [&](float apPct) {
        ApertureResult a;
        const float diam = apPct / 100.f * pitch;
        std::vector<float> R(pts.size()), Q(qzPts.size());
        for (size_t i = 0; i < pts.size(); ++i) {
            R[i] = aperture(img, pts[i], diam);
            if (R[i] < 0) return a;
            if (cal) R[i] = flatCorrect(*cal, pts[i], R[i]);
        }
        for (size_t i = 0; i < qzPts.size(); ++i) {
            Q[i] = aperture(img, qzPts[i], diam);
            if (Q[i] >= 0 && cal) Q[i] = flatCorrect(*cal, qzPts[i], Q[i]);
        }
        // light cells: background unless inverted. ML, MD: the means of the light and dark cells
        double sl = 0, sd = 0;
        int nl = 0, nd = 0;
        for (size_t i = 0; i < R.size(); ++i) {
            const bool light = (c.modules[i] != 0) == q.inverted;
            (light ? sl : sd) += R[i];
            (light ? nl : nd) += 1;
        }
        a.ml = static_cast<float>(sl / nl);
        a.md = static_cast<float>(sd / nd);
        const float gt = (a.ml + a.md) / 2.f, half = std::max(1.f, (a.ml - a.md) / 2.f);
        a.cc = std::max(0.f, (a.ml - a.md) / std::max(1.f, a.ml));
        // each cell's modulation: its distance from the threshold over the half range, signed so
        // a cell on the wrong side is negative, and its grade (0.50 A, 0.40 B, 0.30 C, 0.20 D)
        auto cellMod = [&](float v, bool light) { return (v - gt) / half * (light ? 1.f : -1.f); };
        auto cellGrade = [](float m) { return m < 0 ? 0 : gradeHigh(m, 0.50f, 0.40f, 0.30f, 0.20f); };
        std::vector<int> grades(R.size()), dataGrades;
        std::vector<float> mods;
        for (size_t i = 0; i < R.size(); ++i) {
            const float m = cellMod(R[i], (c.modules[i] != 0) == q.inverted);
            mods.push_back(std::max(0.f, m));
            grades[i] = cellGrade(m);
            if (!fixed[i]) dataGrades.push_back(grades[i]);
        }
        std::nth_element(mods.begin(), mods.begin() + static_cast<long>(mods.size() / 20), mods.end());
        a.cm = mods[mods.size() / 20]; // 5th percentile, as the reported value
        if (perCodeword) {
            std::vector<int> cwGrades;
            for (const auto& cells : cwMap.cells) {
                int g = 4;
                for (int i : cells) g = std::min(g, grades[static_cast<size_t>(i)]);
                cwGrades.push_back(g);
            }
            a.gCM = gradeCodewords(cwGrades, cwMap);
        }
        else a.gCM = gradeAgainstEc(dataGrades, codewords, ec);
        // fixed patterns, against what they must be (not what was decoded); each segment's grade
        // at each level as for cell modulation, the best level winning
        std::vector<int> segGrades;
        auto segGrade = [&](const Segment& sg, auto valueOf) {
            std::vector<int> g;
            for (size_t k = 0; k < sg.cells.size(); ++k) {
                const float v = valueOf(k);
                if (v < 0) continue;
                const bool light = sg.mark[k] == q.inverted;
                g.push_back(cellGrade(cellMod(v, light)));
            }
            int best = 0;
            for (int lv = 1; lv <= 4; ++lv) {
                const int under = static_cast<int>(std::count_if(g.begin(), g.end(), [lv](int v) { return v < lv; }));
                best = std::max(best, std::min(lv, segmentGrade(sg, under)));
            }
            return best;
        };
        int damaged = 0, fixedCells = 0;
        for (const auto& sg : segs) {
            segGrades.push_back(segGrade(sg, [&](size_t k) { return R[static_cast<size_t>(sg.cells[k])]; }));
            for (size_t k = 0; k < sg.cells.size(); ++k) {
                ++fixedCells;
                if (cellMod(R[static_cast<size_t>(sg.cells[k])], sg.mark[k] == q.inverted) < 0) ++damaged;
            }
        }
        if (!qzPts.empty()) {
            Segment qz{"quiet zone", std::vector<int>(qzPts.size(), 0), std::vector<bool>(qzPts.size(), false)};
            segGrades.push_back(segGrade(qz, [&](size_t k) { return Q[k]; }));
        }
        a.fpd = fixedCells ? static_cast<float>(damaged) / fixedCells : 0.f;
        a.dd = segGrades.empty() ? 4.f : static_cast<float>(std::accumulate(segGrades.begin(), segGrades.end(), 0)) / segGrades.size();
        a.gFPD = std::min(segGrades.empty() ? 4 : *std::min_element(segGrades.begin(), segGrades.end()), static_cast<int>(std::floor(a.dd)));
        a.gCC = gradeHigh(a.cc, 0.30f, 0.25f, 0.20f, 0.15f);
        if (haveCal) {
            // Rtarget = Rcal x (SRcal / SRtarget) x (MLtarget / MLcal); SRtarget unset = SRcal
            const float srT = s.srTarget > 0.f ? s.srTarget : s.srCal;
            a.mr = s.rCal * (s.srCal / srT) * (a.ml / s.mlCal);
            a.gMR = gradeHigh(a.mr, 20.f, 15.f, 10.f, 5.f);
        }
        int overall = 4; // decode: it was read
        for (int g : {a.gCC, a.gCM, a.gMR, a.gFPD, q.gradeAN, q.gradeGN, q.gradeUEC})
            if (g >= 0) overall = std::min(overall, g);
        a.overall = overall;
        a.ok = true;
        return a;
    };
    const ApertureResult a50 = measure(50.f), a80 = measure(80.f);
    if (!a50.ok || !a80.ok) return q;
    q.overall50 = a50.overall;
    q.overall80 = a80.overall;
    // the aperture the grades come from: as set, or the better of the two (80 % on a tie)
    const bool use50 = s.aperture == 50 || (s.aperture == 0 && a50.overall > a80.overall);
    const ApertureResult& a = use50 ? a50 : a80;
    q.aperturePct = use50 ? 50 : 80;
    q.meanLight = a.ml;
    q.meanDark = a.md;
    q.meanLightPct = a.ml / 255.f * 100.f;
    q.meanLightOk = q.meanLightPct >= 70.f && q.meanLightPct <= 86.f;
    q.cellContrast = a.cc;
    q.cellModulation = a.cm;
    q.minReflectance = a.mr;
    q.fixedPatternDamage = a.fpd;
    q.distributedDamage = a.dd;
    q.gradeCC = a.gCC;
    q.gradeCM = a.gCM;
    q.gradeMR = a.gMR;
    q.gradeFPD = a.gFPD;
    q.gradeDecode = 4;
    q.calibrated = haveCal;
    q.overall = a.overall;
    q.measured = true;
    return q;
}

} // namespace cr
