// font.cpp - classical OCR from a taught font (tool specification 7.2): no AI model.
//
// A straightened line (findLines, 32 px high, dark on light) is cut into characters by the columns
// that hold ink; each character is compared with every taught sample by normalised correlation of a
// small cell (the line height is fixed, so widths and positions compare across images). The result
// is written as the same T x C log-probability matrix the AI recogniser gives, so the free read, the
// expected-text check (CTC alignment), the TIME / DATE fields and the character boxes work unchanged.
//
// Confidence: a character that matches its best sample well (correlation 0.9+) and no other character
// nearly as well reads at ~100; a poor match, or one as close to two characters (O and 0), reads low,
// so a check at min_quality fails it. A character never taught still appears in the read (as the
// closest one, with a low score), so a missing font character can never pass as a shorter text.

#include "ocr.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <functional>
#include <limits>
#include <map>
#include <numeric>
#include <sstream>

namespace ocr {

namespace {

using Clock = std::chrono::steady_clock;
constexpr int kCellW = 16, kCellH = kLineH; // a character's cell: its columns stretched to 16
constexpr int kCell = kCellW * kCellH;
constexpr float kGood = 0.90f, kPoor = 0.55f; // correlation for a full score / for none
constexpr float kMargin = 0.10f;      // lead over the next best character for a full score
constexpr float kWidthWeight = 0.6f;  // correlation lost per unit of |log(width / taught width)|
constexpr char kMagic[] = "VSFONT1";

// ink of a line image (255 = ink). joinDots: dot print as printed, each row's dots joined to their
// neighbours a dot apart (so a lone dot, as in the arms of a T, is no speck), never across the gap
// of a dot column between two characters
cv::Mat inkOf(const cv::Mat& line, bool joinDots = false)
{
    cv::Mat ink;
    // a nearly blank line: Otsu would split the paper noise; no ink there
    cv::Scalar mean, sd;
    cv::meanStdDev(line, mean, sd);
    if (sd[0] < 8.0) return cv::Mat::zeros(line.size(), CV_8U);
    cv::threshold(line, ink, 0, 255, cv::THRESH_BINARY_INV | cv::THRESH_OTSU);
    if (joinDots) cv::morphologyEx(ink, ink, cv::MORPH_CLOSE, cv::getStructuringElement(cv::MORPH_RECT, cv::Size(3, 1)));
    // specks (dust, a stray dot) are not characters
    cv::Mat labels, stats, centroids;
    const int n = cv::connectedComponentsWithStats(ink, labels, stats, centroids, 8);
    for (int i = 1; i < n; ++i)
        if (stats.at<int>(i, cv::CC_STAT_AREA) < 6) ink.setTo(0, labels == i);
    return ink;
}

std::vector<int> profileOf(const cv::Mat& ink)
{
    std::vector<int> prof(size_t(ink.cols), 0);
    for (int x = 0; x < ink.cols; ++x) prof[size_t(x)] = cv::countNonZero(ink.col(x));
    return prof;
}

} // namespace

std::vector<cv::Range> cutRun(const std::vector<int>& prof, const cv::Range& r, int k)
{
    std::vector<cv::Range> out;
    int start = r.start;
    for (int j = 1; j < k; ++j) {
        const float ideal = float(r.start) + float(r.size()) * float(j) / float(k);
        const int half = std::max(1, int(float(r.size()) / (3.f * float(k))));
        const int lo = std::max(start + 2, int(ideal) - half), hi = std::min(r.end - 2, int(ideal) + half);
        int at = std::clamp(int(std::lround(ideal)), start + 1, r.end - 1);
        for (int x = lo; x <= hi; ++x)
            if (prof[size_t(x)] < prof[size_t(at)] || (prof[size_t(x)] == prof[size_t(at)] && std::abs(x - ideal) < std::abs(at - ideal))) at = x;
        out.emplace_back(start, at);
        start = at;
    }
    out.emplace_back(start, r.end);
    return out;
}

std::vector<cv::Range> pitchCells(const std::vector<int>& prof, float width, float* pitchOut)
{
    const int n = int(prof.size());
    int a = 0, b = n;
    while (a < n && prof[size_t(a)] == 0) ++a;
    while (b > a && prof[size_t(b - 1)] == 0) --b;
    if (width <= 0 || float(b - a) < 3.f * width) return {};   // two characters or fewer show no pitch

    // ink per column, capped at a typical column's (one heavy column does not decide), and at an edge
    // the less of its two columns, so a gap one column wide is found at any phase
    std::vector<int> inked;
    for (int x = a; x < b; ++x) if (prof[size_t(x)] > 0) inked.push_back(prof[size_t(x)]);
    std::nth_element(inked.begin(), inked.begin() + long(inked.size() / 2), inked.end());
    const float typical = float(inked[inked.size() / 2]);
    std::vector<float> pf(static_cast<size_t>(n));
    for (int x = 0; x < n; ++x) pf[size_t(x)] = std::min(float(prof[size_t(x)]), typical) / typical;
    const auto at = [&](float x) {
        const int i0 = std::clamp(int(x), 0, n - 1), i1 = std::min(i0 + 1, n - 1);
        return std::min(pf[size_t(i0)], pf[size_t(i1)]);
    };
    // nearest ink column left and right of each column: how far an edge clears the characters
    std::vector<int> inkL(static_cast<size_t>(n)), inkR(static_cast<size_t>(n));
    for (int x = 0, last = -1000; x < n; ++x) inkL[size_t(x)] = last = prof[size_t(x)] > 0 ? x : last;
    for (int x = n - 1, next = n + 1000; x >= 0; --x) inkR[size_t(x)] = next = prof[size_t(x)] > 0 ? x : next;

    // the cell edges fall in the gaps between characters. Every edge inside the line is evidence: one
    // that clears the ink on both sides by a little adds up to 1, one in a character costs 3. The
    // grid (pitch 0.9 to 1.9 taught widths: one character and its gap, not a dot, not two characters)
    // that puts the most edges in gaps wins: a wider pitch that finds a gap by chance here and there
    // has fewer of them, and the winner lies in the middle of the gaps all along the line (a pitch a
    // little off would drift into the characters by the line's end)
    const float room = std::max(1.f, 0.08f * width);
    const auto evidence = [&](float p, float phase) {
        float e = 0;
        for (float x = phase; x < float(b) - 1; x += p) {   // phase too: an edge in the line's first character
            const int i0 = std::clamp(int(x), 0, n - 1);
            const float left = x - float(inkL[size_t(i0)]) - 0.5f, right = float(inkR[size_t(std::min(n - 1, i0 + 1))]) - x - 0.5f;
            e += std::clamp(std::min(left, right), 0.f, room) / room - 3.f * at(x);
        }
        return e;
    };
    float pitch = 0, phase = 0, most = 0;
    for (float p = 0.9f * width; p <= 1.9f * width; p += 0.1f)
        for (float f = 0; f < p; f += 0.25f) {
            const float e = evidence(p, float(a) + f);
            if (e > most) { most = e; pitch = p; phase = float(a) + f; }
        }
    if (pitch <= 0) return {};

    // a steady pitch puts every inner edge in a gap: little ink there against a character's column.
    // Characters that touch (no gap) or text that is not on one pitch: none
    // (one edge through a character, as a grid laid on text of another pitch does, is enough)
    float edgeInk = 0, worst = 0;
    int edges = 0;
    for (float x = phase; x < float(b) - 1; x += pitch, ++edges) {
        edgeInk += at(x);
        worst = std::max(worst, at(x));
    }
    if (edges < 2 || edgeInk / float(edges) > 0.1f || worst > 0.5f) return {};

    std::vector<cv::Range> cells;
    const int k0 = int(std::floor((float(a) - phase) / pitch));
    for (int k = k0; phase + float(k) * pitch < float(b); ++k) {
        const int s = std::clamp(int(std::lround(phase + float(k) * pitch)), 0, n);
        const int e = std::clamp(int(std::lround(phase + float(k + 1) * pitch)), 0, n);
        if (e > s) cells.emplace_back(s, e);
    }
    if (pitchOut) *pitchOut = pitch;
    return cells;
}

std::vector<cv::Range> segmentLine(const cv::Mat& line, bool dots, float typicalWidth, int wantCount, std::vector<int>* profOut)
{
    (void)dots; // dot print arrives melted (Font::prepare)
    const cv::Mat ink = inkOf(line);
    std::vector<int> prof = profileOf(ink);
    std::vector<cv::Range> segs;
    for (int x = 0; x < ink.cols;) {
        if (prof[size_t(x)] == 0) { ++x; continue; }
        int e = x;
        while (e < ink.cols && prof[size_t(e)] > 0) ++e;
        const int mass = std::accumulate(prof.begin() + x, prof.begin() + e, 0);
        if (mass >= 8) segs.emplace_back(x, e);
        x = e;
    }
    if (profOut) *profOut = prof;
    if (wantCount <= 0 || segs.empty()) return segs; // reading: Font::run splits touching characters

    // teaching: the typed text says how many characters there are. A sliver (a broken stroke) joins
    // its nearer neighbour; each run then gets as many characters as its width holds at the usual
    // width (the font's, else the line's mean), cut at the weakest columns near even split points.
    // A piece left far wider than usual means the text typed has a character too few: refused, so
    // a text typed wrong never teaches wrong characters
    const auto total = [&]() { int t = 0; for (const auto& r : segs) t += r.size(); return t; };
    float ref = typicalWidth > 0 ? typicalWidth : float(total()) / float(wantCount);
    while (int(segs.size()) > wantCount && segs.size() >= 2) {
        auto ni = std::min_element(segs.begin(), segs.end(), [](const cv::Range& a, const cv::Range& b) { return a.size() < b.size(); });
        if (ni->size() >= 0.35f * ref) return {};
        const size_t i = size_t(ni - segs.begin());
        const bool left = i > 0 && (i + 1 == segs.size() || segs[i].start - segs[i - 1].end <= segs[i + 1].start - segs[i].end);
        const size_t a = left ? i - 1 : i;
        segs[a] = cv::Range(segs[a].start, segs[a + 1].end);
        segs.erase(segs.begin() + long(a) + 1);
    }
    if (typicalWidth <= 0) ref = float(total()) / float(wantCount);
    // a run is only cut when it is clearly wider than one character: the font's width, or the line's
    // median run when most characters stand apart. Cutting a normal character in two would take a
    // text typed with a character too many
    std::vector<int> natural;
    for (const auto& r : segs) natural.push_back(r.size());
    std::sort(natural.begin(), natural.end());
    const float single = typicalWidth > 0 ? typicalWidth : natural.size() >= 3 ? float(natural[natural.size() / 2]) : 0.f;
    std::vector<int> k(segs.size());
    int sum = 0;
    for (size_t i = 0; i < segs.size(); ++i) sum += k[i] = std::max(1, int(std::lround(float(segs[i].size()) / ref)));
    while (sum != wantCount) {
        // the run whose pieces are widest gets one more; narrowest one fewer
        size_t pick = segs.size();
        for (size_t i = 0; i < segs.size(); ++i) {
            if (sum > wantCount && k[i] <= 1) continue;
            const float per = float(segs[i].size()) / float(k[i]);
            if (pick == segs.size()) { pick = i; continue; }
            const float perPick = float(segs[pick].size()) / float(k[pick]);
            if (sum < wantCount ? per > perPick : per < perPick) pick = i;
        }
        if (pick == segs.size()) return {};
        k[pick] += sum < wantCount ? 1 : -1;
        sum += sum < wantCount ? 1 : -1;
    }
    std::vector<cv::Range> out;
    for (size_t i = 0; i < segs.size(); ++i) {
        if (k[i] > 1 && single > 0 && float(segs[i].size()) <= 1.45f * single) return {};
        for (const auto& r : cutRun(prof, segs[i], k[i])) out.push_back(r);
    }
    for (const auto& r : out)
        if (r.size() > 1.7f * ref || r.size() < 2) return {};
    return out;
}

namespace {

// the character's cell: its own columns (ink = 255 - grey) stretched to a fixed width, at the line's
// height (so a comma and an apostrophe differ); its width is kept apart (Sample::width)
std::vector<uint8_t> rawCell(const cv::Mat& line, const cv::Range& r)
{
    const cv::Mat glyph = 255 - line.colRange(r);
    cv::Mat cell;
    cv::resize(glyph, cell, cv::Size(kCellW, kCellH), 0, 0, glyph.cols > kCellW ? cv::INTER_AREA : cv::INTER_LINEAR);
    // a slight blur: a stroke a pixel off still correlates
    cv::GaussianBlur(cell, cell, cv::Size(3, 3), 0.6);
    return std::vector<uint8_t>(cell.begin<uint8_t>(), cell.end<uint8_t>());
}

// zero mean, unit length: the correlation of two cells is then their dot product
std::vector<float> normalised(const std::vector<uint8_t>& raw)
{
    std::vector<float> v(raw.begin(), raw.end());
    const float mean = std::accumulate(v.begin(), v.end(), 0.f) / float(v.size());
    double n2 = 0;
    for (float& x : v) { x -= mean; n2 += double(x) * x; }
    const float n = float(std::sqrt(n2));
    for (float& x : v) x = n > 1e-3f ? x / n : 0.f;
    return v;
}

constexpr uint8_t kTaughtDot = 90;   // a taught dot place holds a dot from 0.35 of a whole dot

// a cell's character (dot print cut on its pitch): the melted ink near its own printed dots, so ink
// melted across from the next character stays out; empty when the cell holds no ink of its own
cv::Range cellInk(const cv::Range& cell, const std::vector<int>& printed, const std::vector<int>& melted)
{
    constexpr int kMelt = 3;   // how far the melt spreads a dot (Font::prepare)
    int s = cell.end, e = cell.start;
    for (int x = cell.start; x < cell.end; ++x) {
        bool own = false;
        for (int d = std::max(cell.start, x - kMelt); d < std::min(cell.end, x + kMelt + 1) && !own; ++d) own = printed[size_t(d)] > 0;
        if (own && melted[size_t(x)] > 0) { s = std::min(s, x); e = x + 1; }
    }
    return e > s ? cv::Range(s, e) : cv::Range();
}

// The dot lattice of CIJ print on a line as printed (before the melt): dot columns dx apart, a whole
// number of them to the pitch (a character's dots and the gap), dot rows dy apart from the line's top
// row of dots to its bottom one. None (cols 0) when the dots do not stand on a clear lattice (print
// too blurred or too small to tell the dots apart): the characters are then compared as strokes only
struct Lattice {
    float x0 = 0, dx = 0, y0 = 0, dy = 0;
    float shear = 0;             // dot columns lean: x moves this much per row of pixels below y0
    int rows = 0, cols = 0;      // dot rows of the line; dot columns per cell
    float paper = 0, ink = 0;    // grey ink (255 - grey) of the paper and of a whole dot
    bool ok() const { return cols > 0; }
};

// how clearly a profile peaks every p from phase on over [a, b): its values there against half way
// between, -1..1
float combContrast(const std::vector<float>& f, int a, int b, float p, float phase)
{
    const auto val = [&](float x) {
        const int i = std::clamp(int(x), 0, int(f.size()) - 1), j = std::min(i + 1, int(f.size()) - 1);
        const float t = std::clamp(x - float(i), 0.f, 1.f);
        return f[size_t(i)] * (1 - t) + f[size_t(j)] * t;
    };
    float on = 0, off = 0;
    for (float x = float(a) + phase; x + 0.5f * p < float(b); x += p) { on += val(x); off += val(x + 0.5f * p); }
    return on + off > 1e-3f ? (on - off) / (on + off) : 0.f;
}

// the best phase of a comb of period p, and its contrast
std::pair<float, float> bestComb(const std::vector<float>& f, int a, int b, float p)
{
    float best = -2.f, at = 0;
    for (float ph = 0; ph < p; ph += 0.1f) {
        const float c = combContrast(f, a, b, p, ph);
        if (c > best) { best = c; at = ph; }
    }
    return {at, best};
}

Lattice latticeOf(const cv::Mat& lineIn, float pitch, int taughtCols)
{
    Lattice L;
    const cv::Mat mask = inkOf(lineIn);
    cv::Mat g;
    cv::subtract(cv::Scalar(255), lineIn, g);
    g.convertTo(g, CV_32F);
    int a = g.cols, b = 0, top = g.rows, bottom = -1;
    for (int y = 0; y < mask.rows; ++y)
        for (int x = 0; x < mask.cols; ++x)
            if (mask.at<uint8_t>(y, x)) { a = std::min(a, x); b = std::max(b, x + 1); top = std::min(top, y); bottom = std::max(bottom, y); }
    if (b - a < 8 || bottom - top < 6) return L;
    std::vector<float> colP(size_t(g.cols), 0.f), rowP(size_t(g.rows), 0.f);
    for (int y = top; y <= bottom; ++y)
        for (int x = a; x < b; ++x) { colP[size_t(x)] += g.at<float>(y, x); rowP[size_t(y)] += g.at<float>(y, x); }
    // the paper's own grey is no peak
    const float colMin = *std::min_element(colP.begin() + a, colP.begin() + b), rowMin = *std::min_element(rowP.begin() + top, rowP.begin() + bottom + 1);
    for (auto& v : colP) v = std::max(0.f, v - colMin);
    for (auto& v : rowP) v = std::max(0.f, v - rowMin);

    // rows first: the shortest period about as clear as the best, with 4 rows of dots or more (a comb
    // of two or three teeth fits anything)
    constexpr float kMinContrast = 0.25f, kMinDot = 2.f;
    std::vector<std::pair<float, std::pair<float, float>>> byP;
    float bestR = -1.f;
    for (float p = kMinDot; p <= float(bottom - top + 1) / 4.f; p += 0.05f) {
        byP.push_back({p, bestComb(rowP, top, bottom + 1, p)});
        bestR = std::max(bestR, byP.back().second.second);
    }
    if (bestR < kMinContrast) return L;
    for (const auto& r : byP)
        if (r.second.second >= 0.85f * bestR) { L.dy = r.first; L.y0 = float(top) + r.second.first; break; }

    // columns: the pitch holds 4 to 10 dot columns: as many as the font was taught with (rows of dots
    // that do not show them, below, refuse the lattice: the dots are then not compared). Else (a first
    // line taught) from the print; narrow characters (I, l, 1) fill few columns, so they peak less
    // clearly than the rows: of the counts that peak clearly enough, the one whose columns are spaced
    // most like the rows (a CIJ dot is about as wide as high)
    std::vector<std::pair<int, std::pair<float, float>>> byCols;
    float bestC = -1.f;
    for (int n = 4; n <= 10; ++n) {
        if (pitch / float(n) < kMinDot) break;
        byCols.push_back({n, bestComb(colP, a, b, pitch / float(n))});
        bestC = std::max(bestC, byCols.back().second.second);
    }
    if (bestC < kMinContrast) return L;
    L.cols = 0;
    for (const auto& c : byCols)
        if (taughtCols > 0 ? c.first == taughtCols : c.second.second >= 0.6f * bestC && (L.cols == 0 || std::abs(pitch / float(c.first) - L.dy) < std::abs(L.dx - L.dy))) {
            L.cols = c.first;
            L.dx = pitch / float(c.first);
            L.x0 = float(a) + c.second.first;
        }
    if (L.cols == 0) return L;
    // the first row of dots at the ink's top (its dots' centres lie a little below)
    while (L.y0 - L.dy >= float(top) - 0.25f * L.dy) L.y0 -= L.dy;
    while (L.y0 < float(top) - 0.25f * L.dy) L.y0 += L.dy;
    L.rows = int(std::floor((float(bottom) + 0.25f * L.dy - L.y0) / L.dy)) + 1;
    if (L.rows < 3 || L.rows > 16) { L.cols = 0; return L; }
    // the line finder straightens the slant of solid print; dot columns may still lean a little (a
    // dot or more over the character's height), and the pitch found on a line of narrow characters
    // may be a few per cent off (a dot column off by the line's end). So the dot columns' spacing is
    // refined on the rows of dots, each row finding its own phase, and the lean is the straight line
    // through the phases; at least half the rows must show the columns (else a wrong count of columns,
    // or no dot print)
    std::vector<std::vector<float>> rps;
    for (int i = 0; i < L.rows; ++i) {
        const float yc = L.y0 + float(i) * L.dy;
        std::vector<float> rp(size_t(g.cols), 0.f);
        for (int y = std::max(0, int(std::lround(yc - L.dy / 3))); y <= std::min(g.rows - 1, int(std::lround(yc + L.dy / 3))); ++y)
            for (int x = a; x < b; ++x) rp[size_t(x)] += g.at<float>(y, x);
        rps.push_back(std::move(rp));
    }
    std::vector<std::pair<float, float>> phases(size_t(L.rows));   // per row: phase from a, contrast
    const auto rowsFit = [&](float dx, std::vector<std::pair<float, float>>& ph) {
        float sum = 0;
        for (int i = 0; i < L.rows; ++i) { ph[size_t(i)] = bestComb(rps[size_t(i)], a, b, dx); sum += ph[size_t(i)].second; }
        return sum;
    };
    float bestFit = -1e9f, dx0 = L.dx;
    std::vector<std::pair<float, float>> ph(size_t(L.rows));
    for (float f = 0.94f; f <= 1.0601f; f += 0.005f)
        if (const float fit = rowsFit(dx0 * f, ph); fit > bestFit) { bestFit = fit; L.dx = dx0 * f; phases = ph; }
    std::vector<float> ys, offs;
    float ref = -1.f;
    for (int i = 0; i < L.rows; ++i) {
        if (phases[size_t(i)].second <= kMinContrast) continue;
        float o = phases[size_t(i)].first;
        if (ref < 0) ref = o;
        while (o - ref > 0.5f * L.dx) o -= L.dx;   // the phase nearest the first row's: a lean, not a jump of a column
        while (ref - o > 0.5f * L.dx) o += L.dx;
        ys.push_back(float(i) * L.dy);
        offs.push_back(o);
    }
    if (ys.size() < 3 || 2 * ys.size() < size_t(L.rows)) { L.cols = 0; return L; }
    {
        float my = 0, mo = 0;
        for (size_t k = 0; k < ys.size(); ++k) { my += ys[k]; mo += offs[k]; }
        my /= float(ys.size());
        mo /= float(ys.size());
        float sxy = 0, sxx = 0;
        for (size_t k = 0; k < ys.size(); ++k) { sxy += (ys[k] - my) * (offs[k] - mo); sxx += (ys[k] - my) * (ys[k] - my); }
        if (sxx > 0) L.shear = std::clamp(sxy / sxx, -0.15f, 0.15f);
        L.x0 = float(a) + mo - L.shear * my;
    }

    // a whole dot's grey ink (most dot places of a line hold one) and the paper's
    std::vector<float> paper;
    for (int y = 0; y < g.rows; ++y)
        for (int x = 0; x < g.cols; ++x) if (!mask.at<uint8_t>(y, x)) paper.push_back(g.at<float>(y, x));
    std::vector<float> on;
    for (int i = 0; i < L.rows; ++i)
        for (float x = L.x0 + L.shear * float(i) * L.dy; x < float(b); x += L.dx) {
            const int xi = int(std::lround(x)), yi = int(std::lround(L.y0 + float(i) * L.dy));
            if (xi >= 0 && xi < g.cols && yi >= 0 && yi < g.rows && mask.at<uint8_t>(yi, xi)) on.push_back(g.at<float>(yi, xi));
        }
    if (paper.empty() || on.size() < 8) { L.cols = 0; return L; }
    std::nth_element(paper.begin(), paper.begin() + long(paper.size() / 2), paper.end());
    std::nth_element(on.begin(), on.begin() + long(on.size() * 3 / 4), on.end());
    L.paper = paper[paper.size() / 2];
    L.ink = on[on.size() * 3 / 4];
    if (L.ink - L.paper < 20.f) L.cols = 0;
    return L;
}

// the dot places of one cell, row by row: 0 = paper, 255 = a whole dot
std::vector<uint8_t> dotsOf(const cv::Mat& lineIn, const Lattice& L, const cv::Range& cell)
{
    std::vector<uint8_t> out(size_t(L.rows * L.cols), 0);
    const int j0 = int(std::ceil((float(cell.start) - L.x0) / L.dx - 0.25f));   // the cell's first dot column
    for (int i = 0; i < L.rows; ++i)
        for (int j = 0; j < L.cols; ++j) {
            const float x = L.x0 + L.shear * float(i) * L.dy + float(j0 + j) * L.dx, y = L.y0 + float(i) * L.dy;
            // the grey ink at the place itself (between pixels: from the four round it)
            const int xi = int(std::floor(x)), yi = int(std::floor(y));
            const float fx = x - float(xi), fy = y - float(yi);
            const auto inkAt = [&](int px, int py) {
                return px >= 0 && px < lineIn.cols && py >= 0 && py < lineIn.rows ? 255.f - float(lineIn.at<uint8_t>(py, px)) : 0.f;
            };
            const float v = (1 - fy) * ((1 - fx) * inkAt(xi, yi) + fx * inkAt(xi + 1, yi)) + fy * ((1 - fx) * inkAt(xi, yi + 1) + fx * inkAt(xi + 1, yi + 1));
            out[size_t(i * L.cols + j)] = uint8_t(std::clamp((v - L.paper) / (L.ink - L.paper), 0.f, 1.f) * 255.f + 0.5f);
        }
    return out;
}

} // namespace

void Font::clear()
{
    samples_.clear();
    rebuild();
}

void Font::rebuild()
{
    cs_.chars.assign(1, 0);
    cs_.index.clear();
    auto add = [&](char32_t c) {
        if (cs_.find(c) >= 0) return;
        cs_.index[c] = int(cs_.chars.size());
        cs_.chars.push_back(c);
    };
    add(U' ');
    for (const auto& s : samples_) add(s.c);
    // each class's dots: its first sample's, then each other sample's added at the shift (a dot column
    // or row either way) where they agree best
    dotT_.assign(cs_.chars.size(), DotTemplate{});
    for (const auto& s : samples_) {
        if (s.dots.empty()) continue;
        DotTemplate& t = dotT_[size_t(cs_.find(s.c))];
        const auto on = [&](int i, int j) { return i >= 0 && i < s.dotRows && j >= 0 && j < s.dotCols && s.dots[size_t(i * s.dotCols + j)] >= kTaughtDot; };
        if (t.on.empty()) {
            t.rows = s.dotRows;
            t.cols = s.dotCols;
            t.on.resize(s.dots.size());
            for (int i = 0; i < t.rows; ++i) for (int j = 0; j < t.cols; ++j) t.on[size_t(i * t.cols + j)] = on(i, j);
            continue;
        }
        int bestR = 0, bestC = 0, most = -1;
        for (int sr = -1; sr <= 1; ++sr)
            for (int sc = -1; sc <= 1; ++sc) {
                int agree = 0;
                for (int i = 0; i < t.rows; ++i)
                    for (int j = 0; j < t.cols; ++j) agree += t.on[size_t(i * t.cols + j)] && on(i + sr, j + sc);
                if (agree > most) { most = agree; bestR = sr; bestC = sc; }
            }
        for (int i = 0; i < t.rows; ++i)
            for (int j = 0; j < t.cols; ++j) if (on(i + bestR, j + bestC)) t.on[size_t(i * t.cols + j)] = 1;
    }
    std::vector<float> w;
    for (const auto& s : samples_) w.push_back(s.width);
    good_ = kGood;
    if (w.empty()) { width_ = wide_ = 0; return; }
    std::nth_element(w.begin(), w.begin() + long(w.size() / 2), w.end());
    width_ = w[w.size() / 2];
    // dot print's pitch is a wide character and its gap: a font of many narrow characters (I, l, 1)
    // has a narrow median, and a pitch looked for from it would be one narrow character and its gap
    std::nth_element(w.begin(), w.begin() + long(w.size() * 4 / 5), w.end());
    wide_ = w[w.size() * 4 / 5];
    // how well two prints of one character match each other (taught from several prints): a read
    // that matches as well scores full. Dot print with missing dots, or a rough surface, matches
    // itself less than clean solid print, so its bar is lower (never under 0.75)
    std::vector<float> self;
    for (size_t i = 0; i < samples_.size(); ++i) {
        float best = -1.f;
        for (size_t j = 0; j < samples_.size(); ++j)
            if (j != i && samples_[j].c == samples_[i].c)
                best = std::max(best, std::inner_product(samples_[i].cell.begin(), samples_[i].cell.end(), samples_[j].cell.begin(), 0.f));
        if (best > -1.f) self.push_back(best);
    }
    if (self.size() >= 4) {
        std::nth_element(self.begin(), self.begin() + long(self.size() / 2), self.end());
        good_ = std::clamp(self[self.size() / 2] - 0.02f, 0.75f, kGood);
    }
}

int Font::glyphs() const
{
    return std::max(0, int(cs_.chars.size()) - 2); // less the blank and the space
}

Font::Sample Font::sampleOf(const cv::Mat& line, const cv::Range& cols, char32_t c) const
{
    Sample s;
    s.c = c;
    s.raw = rawCell(line, cols);
    s.cell = normalised(s.raw);
    s.width = float(cols.size());
    return s;
}

std::vector<float> Font::dotLikelihood(const std::vector<uint8_t>& read, int rows, int cols) const
{
    // a dot taught but not read is missing (a blocked nozzle: common), a dot read where none was
    // taught is extra ink (rare): each counts its log-odds, pm = 0.15 and pe = 0.02. The character's
    // dots (all its samples' together) and the read ones are aligned at the shift (a dot column or row
    // either way) that fits best
    constexpr float kMissing = 1.88f, kExtra = 3.75f;   // log(0.98 / 0.15), log(0.85 / 0.02)
    std::vector<float> ll(cs_.chars.size(), std::numeric_limits<float>::quiet_NaN());
    for (size_t c = 2; c < dotT_.size(); ++c) {
        const DotTemplate& s = dotT_[c];
        if (s.on.empty()) continue;
        float best = -1e9f;
        for (int sr = -1; sr <= 1; ++sr)
            for (int sc = -1; sc <= 1; ++sc) {
                float cost = 0;
                // every place of either grid: the taught one at (i, j), the read one at (i + sr, j + sc)
                for (int i = std::min(0, -sr); i < std::max(s.rows, rows - sr); ++i)
                    for (int j = std::min(0, -sc); j < std::max(s.cols, cols - sc); ++j) {
                        const bool inT = i >= 0 && i < s.rows && j >= 0 && j < s.cols;
                        const bool inR = i + sr >= 0 && i + sr < rows && j + sc >= 0 && j + sc < cols;
                        const float t = inT && s.on[size_t(i * s.cols + j)] ? 1.f : 0.f;
                        // ink spilt from the next dot (up to about 0.3 of a dot) is no dot, 0.6 is one
                        const float r = inR ? std::clamp((float(read[size_t((i + sr) * cols + j + sc)]) / 255.f - 0.3f) / 0.3f, 0.f, 1.f) : 0.f;
                        cost += kMissing * t * (1 - r) + kExtra * (1 - t) * r;
                    }
                best = std::max(best, -cost);
            }
        ll[c] = best;
    }
    return ll;
}

int Font::dotCols() const
{
    std::vector<int> n;
    for (const auto& s : samples_) if (!s.dots.empty()) n.push_back(s.dotCols);
    if (n.empty()) return 0;
    std::nth_element(n.begin(), n.begin() + long(n.size() / 2), n.end());
    return n[n.size() / 2];
}

void Font::dotCheck(std::vector<float>& each, const std::vector<uint8_t>& read, int rows, int cols) const
{
    // after the melt 8 and B, 0 and O are close as strokes; their dots differ (B has three more in its
    // left column). The comparison of strokes still decides which character it is and how well it fits
    // (so missing dots still lower the score); the dots never raise a score:
    // - every taught dot of the chosen character read, and no ink beyond them: its lead over a
    //   character whose dots fit worse widens (a character never taught a dot away from it would fit
    //   as well only with ink the chosen one has not);
    // - a taught dot of it missing: nothing changes. A character never taught, one dot short of the
    //   chosen one (an l for an I), is then as likely as a damaged print of it;
    // - another character's dots fit clearly better, both fitting fairly: no character scores above
    //   the two tied, so the cell scores low (NG rather than a guess); but when the strokes are about as
    //   close to both and every dot of the other is read, the dots choose it, at its own stroke score;
    // - dots that fit no character well (a lattice off, several dots missing): nothing changes
    constexpr float kWeight = 0.033f;   // per unit of log-likelihood: 3 (a dot that should be there and is not, and a little more) is the full margin
    constexpr float kWhole = 1.f, kTie = 1.f, kFit = 6.f;
    const std::vector<float> ll = dotLikelihood(read, rows, cols);
    size_t a = 2;
    for (size_t c = 3; c < each.size(); ++c) if (each[c] > each[a]) a = c;
    if (std::isnan(ll[a])) return;
    size_t b = a;
    for (size_t c = 2; c < ll.size(); ++c) if (!std::isnan(ll[c]) && ll[c] > ll[b]) b = c;
    if (ll[b] > ll[a] + kTie) {
        if (ll[b] < -kFit) return;
        const float m = std::min(each[a], each[b]);
        for (size_t c = 2; c < each.size(); ++c) each[c] = std::min(each[c], m);
        // strokes about as close to both (l and I melted), every dot of the other read: the dots choose
        // it, at its own stroke score (nothing above it), and its lead widens as below
        if (ll[b] < -kWhole || m < each[a] - kMargin) return;
        a = b;
    }
    if (ll[a] < -kWhole) return;
    for (size_t c = 2; c < each.size(); ++c)
        if (c != a && !std::isnan(ll[c])) each[c] -= kWeight * std::max(0.f, ll[a] - ll[c]);
}

char32_t Font::dotsLookLike(const Sample& s) const
{
    // its own class's dots taught before: another class fits better by a dot of ink its own lacks
    // (a dot of its own missing is a damaged print, taught as it is). A class not taught before: one
    // whose dots it has exactly
    if (s.dots.empty()) return 0;
    const std::vector<float> ll = dotLikelihood(s.dots, s.dotRows, s.dotCols);
    const int own = cs_.find(s.c);
    const float ownLL = own >= 0 ? ll[size_t(own)] : std::numeric_limits<float>::quiet_NaN();
    size_t best = 0;
    for (size_t c = 2; c < ll.size(); ++c)
        if (int(c) != own && !std::isnan(ll[c]) && (best == 0 || ll[c] > ll[best])) best = c;
    if (best == 0) return 0;
    if (std::isnan(ownLL) ? ll[best] > -1.f : ll[best] > ownLL + 3.f) return cs_.chars[best];
    return 0;
}

char32_t Font::looksLike(const Sample& s) const
{
    // a character taught before that now looks clearly like another one: the text is typed wrong
    // (a first sample may look like another character: O and 0, I and 1 can be close)
    float own = -1.f, other = -1.f;
    char32_t otherC = 0;
    for (const auto& o : samples_) {
        const float ncc = std::inner_product(o.cell.begin(), o.cell.end(), s.cell.begin(), 0.f) -
                          kWidthWeight * std::abs(std::log(s.width / std::max(1.f, o.width)));
        if (o.c == s.c) own = std::max(own, ncc);
        else if (ncc > other) { other = ncc; otherC = o.c; }
    }
    // and a character not taught before that matches another one nearly perfectly
    if ((own > -1.f && other > kGood && other > own + 0.15f) || (own == -1.f && other > 0.95f)) return otherC;
    return 0;
}

void Font::keep(std::vector<Sample> fresh)
{
    for (auto& s : fresh) {
        // the same character taught again from a near-identical print adds nothing but time. On dot
        // print the dots decide too: melted, a print with a dot missing looks near-identical, and a
        // character kept only from such a print would look like another (an I without its top right dot
        // is an l)
        const auto sameDots = [](const Sample& x, const Sample& y) {
            if (x.dots.size() != y.dots.size() || x.dotCols != y.dotCols) return x.dots.empty() && y.dots.empty();
            for (size_t i = 0; i < x.dots.size(); ++i) if ((x.dots[i] >= kTaughtDot) != (y.dots[i] >= kTaughtDot)) return false;
            return true;
        };
        bool same = false;
        for (const auto& o : samples_)
            if (o.c == s.c && std::inner_product(o.cell.begin(), o.cell.end(), s.cell.begin(), 0.f) > 0.98f && sameDots(o, s)) { same = true; break; }
        if (!same) samples_.push_back(std::move(s));
    }
    rebuild();
}

int Font::teach(const cv::Mat& lineIn, const std::u32string& text, bool dots, std::string& err)
{
    const cv::Mat line = prepare(lineIn, dots);
    std::u32string chars;
    for (char32_t c : text) if (c != U' ' && c != U'\t') chars.push_back(c);
    if (chars.empty()) { err = "type the text printed on the line"; return -1; }
    // dot print is cut on its pitch, as it is read (Font::run): one character per cell holding ink,
    // so a character with dots missing (fallen apart, or an edge column of dots lost) still teaches
    // as one; each keeps its dots for the comparison dot by dot. A first line, with no font width yet,
    // takes the width from the typed text (its ink over its characters; a space widens the guess, the
    // pitch search allows for it). When the cells holding ink are not as many as the characters typed,
    // the line is cut by its strokes as before
    std::vector<cv::Range> segs, segCells;
    Lattice lat;
    if (dots) {
        const std::vector<int> printed = profileOf(inkOf(lineIn, !tight_)), melted = profileOf(inkOf(line));
        int a = 0, b = int(printed.size());
        while (a < b && printed[size_t(a)] == 0) ++a;
        while (b > a && printed[size_t(b - 1)] == 0) --b;
        float pitch = 0;
        const float w = wide_ > 0 ? wide_ : 0.85f * float(b - a) / float(chars.size());
        for (const auto& cell : pitchCells(printed, w, &pitch)) {
            const cv::Range r = cellInk(cell, printed, melted);
            if (r.size() > 0) { segs.push_back(r); segCells.push_back(cell); }
        }
        if (segs.size() == chars.size()) lat = latticeOf(lineIn, pitch, dotCols());
        else segs.clear(), segCells.clear();
    }
    if (segs.empty()) segs = segmentLine(line, dots, width_, int(chars.size()));
    if (segs.size() != chars.size() && ready()) {
        // the text confirmed from the font's own read (only the unsure characters typed) has as many
        // characters as the read cut the line into: that cut is taken when the even cut does not fit
        std::vector<FontCut> cuts;
        run(lineIn, dots, Clock::time_point::max(), &cuts);
        if (cuts.size() == chars.size()) {
            segs.clear();
            for (const auto& c : cuts) segs.push_back(c.cols);
        }
    }
    if (segs.size() != chars.size()) {
        err = segs.empty() ? "the characters on the line do not match the text typed (check every character)"
                           : "found " + std::to_string(segs.size()) + " characters on the line, the text has " + std::to_string(chars.size());
        return -1;
    }
    std::vector<Sample> fresh;
    for (size_t i = 0; i < segs.size(); ++i) {
        Sample s = sampleOf(line, segs[i], chars[i]);
        if (lat.ok()) {
            s.dotRows = lat.rows;
            s.dotCols = lat.cols;
            s.dots = dotsOf(lineIn, lat, segCells[i]);
        }
        if (const char32_t otherC = looksLike(s) ? looksLike(s) : dotsLookLike(s)) {
            err = "character " + std::to_string(i + 1) + " looks like " + toUtf8(std::u32string(1, otherC)) + ", not " +
                  toUtf8(std::u32string(1, s.c)) + " (check the text typed)";
            return -1;
        }
        // the same character twice in this text must look alike (a text shifted by a character does not)
        for (const auto& f : fresh)
            if (f.c == s.c && std::inner_product(f.cell.begin(), f.cell.end(), s.cell.begin(), 0.f) < 0.7f) {
                err = "the two " + toUtf8(std::u32string(1, s.c)) + " in the text do not look alike (check the text typed)";
                return -1;
            }
        fresh.push_back(std::move(s));
    }
    keep(std::move(fresh));
    return int(chars.size());
}

int Font::teachCell(const cv::Mat& lineIn, const cv::Range& cols, char32_t c, bool dots, std::string& err)
{
    if (c == 0 || c == U' ' || c == U'\t') { err = "type the character printed there"; return -1; }
    if (cols.start < 0 || cols.end > lineIn.cols || cols.size() < 2) { err = "no character at that place"; return -1; }
    const cv::Mat line = prepare(lineIn, dots);
    Sample s = sampleOf(line, cols, c);
    if (dots) {   // on a pitch, with its dots, as taught from a whole line
        float pitch = 0;
        for (const auto& cell : pitchCells(profileOf(inkOf(lineIn, !tight_)), wide_, &pitch))
            if (cell.start <= (cols.start + cols.end) / 2 && (cols.start + cols.end) / 2 < cell.end) {
                const Lattice lat = latticeOf(lineIn, pitch, dotCols());
                if (lat.ok()) { s.dotRows = lat.rows; s.dotCols = lat.cols; s.dots = dotsOf(lineIn, lat, cell); }
            }
    }
    // a print that clearly matches another taught character is not taught as this one: a wrong
    // character typed, or a misprint taught as good, would let wrong text pass from then on
    if (const char32_t otherC = looksLike(s) ? looksLike(s) : dotsLookLike(s)) {
        err = "it looks like " + toUtf8(std::u32string(1, otherC)) + ", not " + toUtf8(std::u32string(1, c)) +
              " (check the character typed)";
        return -1;
    }
    std::vector<Sample> fresh;
    fresh.push_back(std::move(s));
    keep(std::move(fresh));
    return 1;
}

cv::Mat Font::prepare(const cv::Mat& line, bool dots) const
{
    if (!dots) return line;
    // dot print: blurred by about half a dot pitch and stretched back, so the dots melt into strokes
    // (missing dots too) before the characters are cut and compared, as the line finder does
    // spaced tight, the gap between two characters is about a dot gap: melted less along the line, so
    // two characters do not run into one
    cv::Mat m;
    cv::GaussianBlur(line, m, cv::Size(0, 0), tight_ ? 0.6 * melt_ : melt_, melt_);
    cv::normalize(m, m, 0, 255, cv::NORM_MINMAX);
    return m;
}

std::vector<cv::Mat> Font::sheared(const cv::Mat& line)
{
    // the line finder's slant estimate is a few degrees off at times, on the taught print and on the
    // read one; diagonal strokes (X, /, 7) then move by pixels: each character is compared at these
    // shears about the line's middle row and the best kept
    std::vector<cv::Mat> out;
    for (const float deg : {0.f, -2.f, 2.f, -4.f, 4.f}) {
        if (deg == 0.f) { out.push_back(line); continue; }
        const double t = std::tan(double(deg) * CV_PI / 180.0);
        const cv::Mat M = (cv::Mat_<double>(2, 3) << 1, t, -t * kLineH / 2.0, 0, 1, 0);
        cv::Mat w;
        cv::warpAffine(line, w, M, line.size(), cv::INTER_LINEAR, cv::BORDER_REPLICATE);
        out.push_back(w);
    }
    return out;
}

std::vector<float> Font::scores(const std::vector<cv::Mat>& lines, const cv::Range& r) const
{
    // best correlation per character; a width unlike the taught one counts against it (O and 0,
    // l and I differ mostly in width)
    const float w = float(r.size());
    std::vector<float> best(cs_.chars.size(), -1.f);
    for (const cv::Mat& line : lines) {
        const std::vector<float> cell = normalised(rawCell(line, r));
        for (const auto& s : samples_) {
            float ncc = std::inner_product(s.cell.begin(), s.cell.end(), cell.begin(), 0.f);
            ncc -= kWidthWeight * std::abs(std::log(w / std::max(1.f, s.width)));
            float& b = best[size_t(cs_.find(s.c))];
            b = std::max(b, ncc);
        }
    }
    return best;
}

cv::Mat Font::run(const cv::Mat& lineIn, bool dots, Clock::time_point deadline, std::vector<FontCut>* cuts) const
{
    if (cuts) cuts->clear();
    const cv::Mat line = prepare(lineIn, dots);
    const int C = int(cs_.chars.size());
    const int T = std::max(1, line.cols / kStride);
    const auto blankLine = [&](int rows) {
        cv::Mat lp(rows, C, CV_32F, cv::Scalar(std::log(1e-4f / float(std::max(1, C - 1)))));
        for (int t = 0; t < rows; ++t) lp.at<float>(t, 0) = std::log(1.f - 1e-4f);
        return lp;
    };
    // nothing read: no cuts either
    const auto giveUp = [&]() { if (cuts) cuts->clear(); return blankLine(T); };
    if (samples_.empty() || C < 3) return giveUp();

    std::vector<int> prof;
    std::vector<cv::Range> runs = segmentLine(line, dots, width_, 0, &prof);
    // texture, not text (a barcode, knurling): far more pieces than characters fit; nothing is read
    // (the line then fails), rather than spending the cycle on it
    if (width_ > 0 && float(runs.size()) > 2.f * float(line.cols) / width_ + 8.f) return giveUp();
    const std::vector<cv::Mat> views = sheared(line);
    const auto late = [&]() { return Clock::now() > deadline; };
    // scores per range, computed once
    std::map<std::pair<int, int>, std::vector<float>> cache;
    const auto sc = [&](const cv::Range& r) -> const std::vector<float>& {
        auto it = cache.find({r.start, r.end});
        if (it == cache.end()) it = cache.emplace(std::make_pair(r.start, r.end), scores(views, r)).first;
        return it->second;
    };
    const auto topScore = [](const std::vector<float>& b) { return *std::max_element(b.begin() + 2, b.end()); };

    // dot print (CIJ) on a steady pitch: one character per cell, however many of its dots are missing
    // (it may fall apart into fragments, or narrow when an edge column of dots is gone). The pitch is
    // found on the dots as printed: melted, the gap of one dot column between two characters often
    // closes. A character's columns are the melted ink near its own printed dots, so ink melted across
    // from the next character stays out. An empty cell is a space (or nothing); ink in a cell always
    // reads as a character, so stray ink fails a check. Without a steady pitch the runs are joined
    // and cut as below
    std::vector<cv::Range> segs;
    float pitch = 0;
    std::vector<int> printed;
    const std::vector<cv::Range> cells = dots ? pitchCells(printed = profileOf(inkOf(lineIn, !tight_)), wide_, &pitch) : std::vector<cv::Range>{};
    if (!cells.empty()) {
        const int reach = int(std::lround(pitch / 6.f)) + 1;   // a dot column: a 5-dot character in a 6-dot cell
        const Lattice lat = latticeOf(lineIn, pitch, dotCols());
        for (const auto& cell : cells) {
            if (late()) return blankLine(T);
            const cv::Range own = cellInk(cell, printed, prof);
            if (own.size() == 0) continue;   // any ink of its own reads, however little: a character never vanishes
            const int s = own.start, e = own.end;
            // the cell's ink at every shear; the edges moved by a column, or out to the taught width
            // when the ink is narrower (an edge column of dots lost), at the shear that fits best
            cv::Range best(s, e);
            std::vector<float> each(cs_.chars.size(), -1.f);
            float bestTop = -1.f;
            size_t view = 0;
            for (size_t v = 0; v < views.size(); ++v) {
                const std::vector<float> b = scores({views[v]}, best);
                for (size_t c = 0; c < each.size(); ++c) each[c] = std::max(each[c], b[c]);
                if (topScore(b) > bestTop) { bestTop = topScore(b); view = v; }
            }
            // each character keeps its best over these, so the margin to the next best stays fair
            const cv::Range ink = best;
            std::vector<int> moves{-1, 0, 1};
            if (float(e - s) < width_) moves.push_back(reach);
            for (const int dl : moves)
                for (const int dr : moves) {
                    const cv::Range r(std::max(cell.start, s - dl), std::min(cell.end, e + dr));
                    if (r.size() < 2 || r == ink || (dl == reach && dr < 0) || (dr == reach && dl < 0)) continue;
                    const std::vector<float> b = scores({views[view]}, r);
                    for (size_t c = 0; c < each.size(); ++c) each[c] = std::max(each[c], b[c]);
                    if (topScore(b) > bestTop) { bestTop = topScore(b); best = r; }
                }
            if (lat.ok()) dotCheck(each, dotsOf(lineIn, lat, cell), lat.rows, lat.cols);
            cache[{best.start, best.end}] = each;   // the character's scores, as the put below reads them
            segs.push_back(best);
        }
        runs.clear();   // nothing left for the stroke cutting below
    }

    // a character broken in two (missing dots, a diagonal of dots, a gap in a stroke): a narrow
    // fragment that is no good character itself joins the neighbour it fits best, when that
    // neighbour matches the font about as well with it; a fragment that fits nowhere stays, to read
    // as a poor character (stray ink must fail a check, not vanish)
    if (width_ > 0) {
        for (bool joined = true; joined;) {
            joined = false;
            if (late()) return giveUp();
            float bestGain = -1e9f;
            size_t bestA = 0;
            for (size_t i = 0; i < runs.size(); ++i) {
                if (float(runs[i].size()) >= 0.6f * width_ || topScore(sc(runs[i])) >= 0.8f) continue;
                for (const int side : {-1, 1}) {
                    if ((side < 0 && i == 0) || (side > 0 && i + 1 >= runs.size())) continue;
                    const size_t n = size_t(int(i) + side), a = std::min(i, n);
                    const cv::Range both(runs[a].start, runs[a + 1].end);
                    if (runs[a + 1].start - runs[a].end > 0.35f * width_ || float(both.size()) > 1.5f * width_) continue;
                    const float gain = topScore(sc(both)) - topScore(sc(runs[n]));
                    if (gain > bestGain) { bestGain = gain; bestA = a; }
                }
            }
            if (bestGain >= -0.05f) {
                runs[bestA] = cv::Range(runs[bestA].start, runs[bestA + 1].end);
                runs.erase(runs.begin() + long(bestA) + 1);
                joined = true;
            }
        }
    }

    // touching characters: a run clearly wider than a taught character is tried as 2 to 4 characters.
    // A cut must go through a thin bridge (a letter's own stem, as the middle of an m, is not one),
    // every piece must match a character clearly better than any other, and the pieces together
    // clearly better than the run as one
    for (const auto& r : runs) {
        if (late()) return giveUp();
        std::vector<cv::Range> bestCut{r};
        float bestMean = topScore(sc(r));
        if (width_ > 0 && float(r.size()) > 1.45f * width_) {
            std::vector<int> inked;
            for (int x = r.start; x < r.end; ++x) if (prof[size_t(x)] > 0) inked.push_back(prof[size_t(x)]);
            std::nth_element(inked.begin(), inked.begin() + long(inked.size() / 2), inked.end());
            const float typicalInk = inked.empty() ? 0.f : float(inked[inked.size() / 2]);
            const int kMax = std::min(4, int(float(r.size()) / (0.45f * width_)));
            for (int k = 2; k <= kMax; ++k) {
                const std::vector<cv::Range> cut = cutRun(prof, r, k);
                bool ok = true;
                float mean = 0;
                for (size_t i = 0; i < cut.size() && ok; ++i) {
                    if (i > 0 && float(prof[size_t(cut[i].start)]) > 0.4f * typicalInk) ok = false;
                    const std::vector<float>& b = sc(cut[i]);
                    std::vector<float> top2(b.begin() + 2, b.end());
                    std::partial_sort(top2.begin(), top2.begin() + std::min<long>(2, long(top2.size())), top2.end(), std::greater<float>());
                    if (top2.size() >= 2 && top2[0] - top2[1] < kMargin / 2) ok = false;
                    if (top2[0] <= 0.7f) ok = false;
                    mean += top2[0] / float(k);
                }
                if (ok && mean > bestMean + 0.06f) { bestMean = mean; bestCut = cut; }
            }
        }
        segs.insert(segs.end(), bestCut.begin(), bestCut.end());
    }

    // one column per character at its centre; CTC needs a blank only between two equal characters,
    // so a column taken pushes the next one right, and the matrix grows past the line's end rather
    // than any character being left out
    struct Put { int t; int c; std::vector<float> row; };
    std::vector<Put> puts;
    int prevT = -1, prevC = -1;
    // the line's own pitch and gap between characters (medians: word gaps are the few). A word gap
    // in a proportional font adds only about half a character, less than one taught width would
    // ask; a line spaced throughout (or too short to tell) keeps the bar of the taught width
    float pitchRef = 1.35f * width_, gapRef = 0.2f * width_;
    if (segs.size() >= 4) {
        std::vector<float> centres, gaps;
        for (size_t k = 1; k < segs.size(); ++k) {
            centres.push_back(0.5f * float(segs[k].start + segs[k].end - segs[k - 1].start - segs[k - 1].end));
            gaps.push_back(float(std::max(0, segs[k].start - segs[k - 1].end)));
        }
        std::nth_element(centres.begin(), centres.begin() + long(centres.size() / 2), centres.end());
        std::nth_element(gaps.begin(), gaps.begin() + long(gaps.size() / 2), gaps.end());
        pitchRef = std::min(pitchRef, centres[centres.size() / 2]);
        gapRef = std::min(gapRef, gaps[gaps.size() / 2]);
    }
    for (size_t k = 0; k < segs.size(); ++k) {
        const cv::Range& r = segs[k];
        // a word gap before it: a space (the check ignores spaces; the free read keeps them). Both the
        // centres and the blank between must be clearly wider than the line's own, so narrow
        // characters at a fixed pitch (1 1) and a wide pair (M W) are not taken for words
        if (k > 0 && width_ > 0) {
            const float centres = 0.5f * float(r.start + r.end - segs[k - 1].start - segs[k - 1].end);
            const int ts = ((segs[k - 1].end + r.start) / 2) / kStride;
            if (centres > pitchRef + 0.35f * width_ && float(r.start - segs[k - 1].end) > gapRef + 0.3f * width_ &&
                ts > prevT && ts < (r.start + r.end) / 2 / kStride) {
                std::vector<float> row(size_t(C), 0.05f / float(C - 1));
                row[1] = 0.95f;
                puts.push_back({ts, 1, row});
                prevT = ts;
                prevC = 1;
            }
        }
        const std::vector<float>& best = sc(r);
        int top = 2, second = -1;
        for (int c = 3; c < C; ++c) if (best[size_t(c)] > best[size_t(top)]) top = c;
        for (int c = 2; c < C; ++c) if (c != top && (second < 0 || best[size_t(c)] > best[size_t(second)])) second = c;
        const float poor = good_ - (kGood - kPoor);
        const float fit = std::clamp((best[size_t(top)] - poor) / (good_ - poor), 0.f, 1.f);
        // the score is how well the best character fits times how clearly it beats the next best:
        // two characters about as close (O and 0 in some fonts, a worn print) score low, so a check
        // fails instead of passing on noise; the others share what is left, each below the best,
        // so the best is always read and a poor or unknown character never vanishes
        const float margin = second < 0 ? kMargin : best[size_t(top)] - best[size_t(second)];
        const float pTop = std::max(0.02f, fit * std::clamp(margin / kMargin, 0.f, 1.f));
        const float pRest = std::min((1.f - pTop) / float(C - 2), 0.5f * pTop);
        std::vector<float> row(size_t(C), 1e-6f);
        for (int c = 2; c < C; ++c) row[size_t(c)] = c == top ? pTop : pRest;
        int tc = std::max((r.start + r.end) / 2 / kStride, prevT + 1);
        if (top == prevC) tc = std::max(tc, prevT + 2);
        puts.push_back({tc, top, row});
        if (cuts) cuts->push_back({tc, r, fit});
        prevT = tc;
        prevC = top;
    }
    cv::Mat logp = blankLine(std::max(T, prevT + 1));
    for (const auto& p : puts) {
        float* row = logp.ptr<float>(p.t);
        for (int c = 0; c < C; ++c) row[c] = std::log(std::max(p.row[size_t(c)], 1e-6f));
    }
    return logp;
}

std::string Font::save() const
{
    std::ostringstream o;
    o << kMagic << '\n';
    char hex[3];
    for (const auto& s : samples_) {
        o << uint32_t(s.c) << ' ' << s.width << ' ';
        for (uint8_t v : s.raw) { std::snprintf(hex, sizeof hex, "%02x", v); o << hex; }
        // its dots, when taught on dot print's pitch (a font saved before has none; older readers stop
        // at the cell)
        if (!s.dots.empty()) {
            o << ' ' << s.dotRows << ' ' << s.dotCols << ' ';
            for (uint8_t v : s.dots) { std::snprintf(hex, sizeof hex, "%02x", v); o << hex; }
        }
        o << '\n';
    }
    return o.str();
}

bool Font::load(const std::string& blob, std::string& err)
{
    std::istringstream in(blob);
    std::string magic;
    if (!std::getline(in, magic) || magic != kMagic) { err = "not a taught font"; return false; }
    std::vector<Sample> got;
    std::string line;
    while (std::getline(in, line)) {
        if (line.empty()) continue;
        std::istringstream ls(line);
        uint32_t cp = 0;
        float width = 0;
        std::string hex;
        if (!(ls >> cp >> width >> hex) || hex.size() != size_t(2 * kCell) || cp == 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF) || cp == U' ' || !(width > 0 && width < 1000)) {
            err = "taught font is damaged";
            return false;
        }
        Sample s;
        s.c = char32_t(cp);
        s.width = width;
        s.raw.resize(size_t(kCell));
        for (int i = 0; i < kCell; ++i) {
            unsigned v = 0;
            if (std::sscanf(hex.c_str() + 2 * i, "%2x", &v) != 1) { err = "taught font is damaged"; return false; }
            s.raw[size_t(i)] = uint8_t(v);
        }
        int rows = 0, cols = 0;
        std::string dhex;
        if (ls >> rows) {
            if (!(ls >> cols >> dhex) || rows < 1 || rows > 16 || cols < 1 || cols > 16 || dhex.size() != size_t(2 * rows * cols)) {
                err = "taught font is damaged";
                return false;
            }
            s.dotRows = rows;
            s.dotCols = cols;
            s.dots.resize(size_t(rows * cols));
            for (size_t i = 0; i < s.dots.size(); ++i) {
                unsigned v = 0;
                if (std::sscanf(dhex.c_str() + 2 * i, "%2x", &v) != 1) { err = "taught font is damaged"; return false; }
                s.dots[i] = uint8_t(v);
            }
        }
        s.cell = normalised(s.raw);
        got.push_back(std::move(s));
    }
    if (got.empty()) { err = "the taught font has no characters"; return false; }
    samples_ = std::move(got);
    rebuild();
    return true;
}

} // namespace ocr
