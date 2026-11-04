// c_api_ocr.cpp - the plain C API in vs_ocr.h, wrapping the OCR / OCV pipeline.

#include "vs_ocr.h"
#include "ocr.hpp"

#include <algorithm>
#include <chrono>
#include <climits>
#include <cstddef>
#include <cstring>
#include <cmath>
#include <exception>
#include <map>
#include <new>
#include <string>
#include <vector>

namespace {

using Clock = std::chrono::steady_clock;

double msSince(Clock::time_point t0)
{
    return std::chrono::duration<double, std::milli>(Clock::now() - t0).count();
}

vs_ocr_params sanitize(vs_ocr_params p)
{
    p.struct_size = int32_t(sizeof(vs_ocr_params));
    if (p.print < VS_PRINT_AUTO || p.print > VS_PRINT_INKJET) p.print = VS_PRINT_AUTO;
    if (p.polarity < VS_POLARITY_AUTO || p.polarity > VS_POLARITY_LIGHT_ON_DARK) p.polarity = VS_POLARITY_AUTO;
    if (p.char_height_px < 0) p.char_height_px = 0;
    p.max_lines = std::clamp(p.max_lines <= 0 ? 8 : p.max_lines, 1, 32);
    // NaN, which std::clamp passes through, takes the default; for the pass limit the strictest (a
    // broken setting never loosens a check)
    p.min_quality = std::isnan(p.min_quality) ? 99.f : std::clamp(p.min_quality, 1.f, 99.f);
    p.max_skew_deg = std::isnan(p.max_skew_deg) ? 15.f : std::clamp(p.max_skew_deg, 0.f, 30.f);
    if (p.time_budget_ms <= 0) p.time_budget_ms = 100;
    if (p.roi_w < 0 || p.roi_h < 0) p.roi_w = p.roi_h = 0;
    if (p.reader != VS_READER_FONT) p.reader = VS_READER_AI;
    // NaN and negative values switch a setting off; join strength 0 = normal
    p.allow_shift_px = p.allow_shift_px > 0 ? std::min(p.allow_shift_px, 10000.f) : 0.f;
    p.dot_size_px = p.dot_size_px > 0 ? std::min(p.dot_size_px, 200.f) : 0.f;
    p.join_strength = p.join_strength > 0 ? std::clamp(p.join_strength, 0.5f, 3.f) : 0.f;
    return p;
}

// One found line after reading (and checking).
struct LineOut {
    ocr::Line line;
    cv::Mat logp;
    std::vector<ocr::ReadChar> read;
    std::vector<ocr::FontCut> cuts;  // taught font: each character's columns and fit
    int expected = -1;              // index into the expected list, -1 = not checked
    ocr::CheckResult check;
    int format = -1;                // the format it was read with (Read text mode), -1 = none
    ocr::FormatRead fread;
    int layout = -1;                // the layout line it was matched to (Read text mode), -1 = none
    // Read text: the free read's score (scoreRead), the second readers' verdict (vs_agree_state), the readers
    // that read it again (bits 1 << vs_second_reader), the line confidence, and the read characters a second
    // reader read otherwise (one per read entry)
    bool scored = false;
    float quality = 0;
    int agreeState = VS_AGREE_NOT_CHECKED;
    int readers = 0;
    float lineConf = 100.f;
    std::vector<char> unsure;
};

struct Pass {
    std::vector<LineOut> lines;
    std::vector<int> missing;       // expected lines with no line found
    std::vector<int> missingFormats; // formats with no line left
    int passed = 0;
    int read = 0;                   // Read text, no format: lines with something read
    int unread = 0;                 // lines found but left unread when the time budget ran out
    float quality = 0;
    std::string layoutWhy;          // the first way the lines do not fit the layout; "" = they do (or none set)
};

} // namespace

struct vs_ocr {
    vs_ocr_params params{};
    ocr::Recognizer rec;
    ocr::Recognizer rec2;                    // the second AI model (vs_ocr_load_second_model), a second reader
    int agree = VS_AGREE_AUTO;               // vs_agree
    ocr::Font font;
    ocr::PrintRef print;                     // print quality learned from good parts
    std::map<int, std::string> expected;    // line index -> pattern
    std::map<int, ocr::Format> formats;     // Read text mode: format index -> compiled format
    ocr::Layout layout;                     // the block's layout (vs_ocr_set_layout); no lines = none
    std::vector<ocr::Format> layoutFormats; // per layout line: any characters, as many as it allows
    bool tight = false;                     // dot print spaced tight (vs_ocr_set_tight_spacing)
    std::time_t clock = 0;
    // results of the last run
    std::vector<vs_ocr_line> lines;
    std::vector<std::string> texts, expTexts;
    std::vector<vs_ocr_char> chars;
    std::vector<vs_ocr_char_quality> quals; // one per chars entry
    std::vector<vs_ocr_line_ext> exts;      // one per lines entry
    std::vector<int32_t> lineLayouts;       // one per lines entry: its layout line, -1 = none
    std::string layoutWhy;                  // vs_ocr_layout_reason
    std::vector<std::u32string> layoutTexts; // the lines of a run that passed, top to bottom (vs_ocr_learn_layout)
    std::vector<std::pair<int, ocr::CheckResult>> checked;   // checked lines (line number, result), for learning
    std::vector<cv::Mat> images;
    vs_ocr_stats stats{};
    std::string error;
};

namespace {

void corners(const cv::Matx23d& M, double u0, double u1, float* x, float* y)
{
    const double uv[4][2] = {{u0, 0}, {u1, 0}, {u1, double(ocr::kLineH)}, {u0, double(ocr::kLineH)}};
    for (int i = 0; i < 4; ++i) {
        const cv::Vec2d X = M * cv::Vec3d(uv[i][0], uv[i][1], 1);
        x[i] = float(X[0]);
        y[i] = float(X[1]);
    }
}

// A character's box from its matrix rows t0..t1: the taught font knows the columns it cut the
// character from (its rows hold one column each), the AI only the rows
void charBox(const LineOut& lo, int t0, int t1, float* x, float* y)
{
    int c0 = INT32_MAX, c1 = -1;
    for (const auto& c : lo.cuts)
        if (c.t >= t0 && c.t <= t1) { c0 = std::min(c0, c.cols.start); c1 = std::max(c1, c.cols.end); }
    if (c1 > c0) corners(lo.line.toImage, double(c0) - 1, double(c1) + 1, x, y);
    else corners(lo.line.toImage, double(t0 * ocr::kStride) - 2, double((t1 + 1) * ocr::kStride) + 2, x, y);
}

// how well the taught font's closest character fits what is printed in rows t0..t1; 1 = no cut there
float fitAt(const LineOut& lo, int t0, int t1)
{
    float f = 1.f;
    for (const auto& c : lo.cuts)
        if (c.t >= t0 && c.t <= t1) f = std::min(f, c.fit);
    return f;
}

bool useFont(const vs_ocr* t) { return t->params.reader == VS_READER_FONT; }
const ocr::Charset& charsetOf(const vs_ocr* t) { return useFont(t) ? t->font.charset() : t->rec.charset(); }

// where to look, from the handle's settings; false (t->error set) when the region is off the image
bool findParams(vs_ocr* t, const cv::Mat& gray, ocr::FindParams& fp, double& locateMs)
{
    const vs_ocr_params& P = t->params;
    fp.roi = P.roi_w > 0 ? cv::Rect(P.roi_x, P.roi_y, P.roi_w, P.roi_h) & cv::Rect(0, 0, gray.cols, gray.rows)
                         : cv::Rect(0, 0, gray.cols, gray.rows);
    if (fp.roi.width < 8 || fp.roi.height < 8) { t->error = "region outside the image"; return false; }
    fp.print = P.print;
    fp.polarity = P.polarity;
    fp.maxLines = P.max_lines;
    fp.maxSkewDeg = P.max_skew_deg;
    fp.dotSize = P.dot_size_px;
    fp.join = P.join_strength > 0 ? P.join_strength : 1.f;
    fp.layout = t->layout.lines.empty() ? nullptr : &t->layout;
    fp.tight = t->tight;
    t->font.setTight(t->tight);
    // as the Locate tool: the line finder stops at 10 times the time budget, at least a second
    fp.deadline = Clock::now() + std::chrono::milliseconds(std::max(1000LL, 10LL * P.time_budget_ms));
    const auto te = Clock::now();
    fp.charHeight = P.char_height_px > 0 ? float(P.char_height_px) : ocr::estimateCharHeight(gray, fp);
    locateMs += msSince(te);
    // the taught font melts dot print by about half a dot (line image px: a line is about 1.36 capitals high)
    float melt = 1.7f;
    if (P.dot_size_px > 0 && fp.charHeight > 0)
        melt = std::clamp(0.55f * P.dot_size_px * float(ocr::kLineH) / (1.36f * fp.charHeight), 0.8f, 4.f);
    t->font.setMelt(melt * fp.join);
    return true;
}

// A line that may go on past its ends (ocr::Line::cutShort) reads as a whole line of its own: the
// text it shows may be only part of the print. Never trusted: scored below min quality, so it fails
float cutShortQuality(float q, float minQ) { return std::min(q, std::max(0.f, minQ - 1.f)); }

// A line the layout joined (ocr::Line::pieces): true when every piece holds a character the line was
// read or checked with (an extra mark does not count). A piece holding none is print beside the line
// (a smear, a ghost, another field) that nothing read: the line then fails as one cut short would
bool piecesHeld(const ocr::Line& line, const std::vector<ocr::CheckChar>& chars)
{
    for (const auto& [u0, u1] : line.pieces) {
        bool held = false;
        for (const auto& c : chars) {
            if (c.t0 < 0 || c.status == VS_CHAR_EXTRA) continue;
            const float u = 0.5f * float((c.t0 + c.t1 + 1) * ocr::kStride);
            held = held || (u >= u0 - 2 * ocr::kStride && u <= u1 + 2 * ocr::kStride);
        }
        if (!held) return false;
    }
    return true;
}

// ---- Read text safety (vs_agree): a second reader must read the same text

std::u32string bare(const std::u32string& s)
{
    std::u32string o;
    for (char32_t c : s) if (c != U' ') o.push_back(c);
    return o;
}

// the characters of a that b does not have at their place: changed, extra, or beside one b has more
std::vector<char> differing(const std::u32string& a, const std::u32string& b)
{
    const size_t n = a.size(), m = b.size();
    std::vector<std::vector<int>> d(n + 1, std::vector<int>(m + 1, 0));
    for (size_t i = 0; i <= n; ++i) d[i][0] = int(i);
    for (size_t j = 0; j <= m; ++j) d[0][j] = int(j);
    for (size_t i = 1; i <= n; ++i)
        for (size_t j = 1; j <= m; ++j)
            d[i][j] = std::min({d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)});
    std::vector<char> out(n, 0);
    size_t i = n, j = m;
    while (i > 0 || j > 0) {
        if (i > 0 && j > 0 && d[i][j] == d[i - 1][j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)) {
            if (a[i - 1] != b[j - 1]) out[i - 1] = 1;
            --i; --j;
        } else if (i > 0 && d[i][j] == d[i - 1][j] + 1) {
            out[i - 1] = 1;
            --i;
        } else {                                    // b has a character more here: both neighbours are in doubt
            if (i > 0) out[i - 1] = 1;
            if (i < n) out[i] = 1;
            --j;
        }
    }
    return out;
}

struct SecondRead {
    int reader = 0;                 // vs_second_reader
    cv::Mat logp;
    const ocr::Charset* cs = nullptr;
    bool ai = false;                // its scores are calibrated probabilities: the line confidence applies
};

// The line read again by every second reader the mode asks for (vs_agree). late: the time budget ran out
// before a reader could read it
std::vector<SecondRead> readAgain(vs_ocr* t, const LineOut& lo, bool dots, Clock::time_point deadline, bool& late)
{
    std::vector<SecondRead> out;
    late = false;
    auto ai = [&](ocr::Recognizer& r, int reader, const cv::Mat& img) {
        if (Clock::now() > deadline) { late = true; return; }
        out.push_back({reader, r.run(img), &r.charset(), true});
    };
    auto font = [&](int reader, const cv::Mat& img) {
        if (Clock::now() > deadline) { late = true; return; }
        out.push_back({reader, t->font.run(img, dots, deadline), &t->font.charset(), false});
    };
    if (t->agree == VS_AGREE_AUTO) {
        if (t->rec2.ready()) ai(t->rec2, VS_SECOND_MODEL, lo.line.image);
        if (!useFont(t) && t->font.ready()) font(VS_SECOND_FONT, lo.line.image);
        if (useFont(t) && t->rec.ready()) ai(t->rec, VS_SECOND_AI, lo.line.image);
    }
    if (out.empty() && !late) {
        const cv::Mat moved = ocr::shiftedLine(lo.line.image);
        if (useFont(t)) font(VS_SECOND_SHIFT, moved);
        else ai(t->rec, VS_SECOND_SHIFT, moved);
    }
    return out;
}

bool readSomething(const std::vector<ocr::ReadChar>& read)
{
    return std::any_of(read.begin(), read.end(), [](const ocr::ReadChar& rc) { return rc.c != U' '; });
}

// text and lowest character score (0..100) of a free read; 0 when it read nothing but spaces
float readScore(const std::vector<ocr::ReadChar>& read, std::u32string& text)
{
    float q = 100.f;
    bool any = false;
    text.clear();
    for (const auto& rc : read) {
        text.push_back(rc.c);
        if (rc.c == U' ') continue;
        q = std::min(q, 100.f * rc.conf);
        any = true;
    }
    return any ? q : 0.f;
}

// Read text, no format: the line's score is its lowest character confidence, at most the line confidence (AI
// reader), then (when it passes so far) the second readers': each must read the same text at or above
// min_quality, or the line scores below it. Sets lo.quality, lineConf, agreeState, readers and unsure
void scoreRead(vs_ocr* t, LineOut& lo, bool dots, Clock::time_point deadline)
{
    const float minQ = t->params.min_quality;
    lo.scored = true;
    lo.unsure.assign(lo.read.size(), 0);
    std::u32string text;
    float q = readScore(lo.read, text);
    if (q <= 0.f) { lo.quality = 0.f; return; }
    if (!useFont(t)) {
        lo.lineConf = ocr::lineConfidence(ocr::linePosterior(lo.logp, t->rec.charset(), text));
        q = std::min(q, lo.lineConf);
    }
    if (lo.line.cutShort) q = cutShortQuality(q, minQ);
    if (q >= minQ && t->agree != VS_AGREE_OFF) {
        bool late = false;
        const auto again = readAgain(t, lo, dots, deadline, late);
        const std::u32string mine = bare(text);
        lo.agreeState = late || again.empty() ? VS_AGREE_DIFFERENT : VS_AGREE_SAME;
        for (const auto& s : again) {
            lo.readers |= 1 << s.reader;
            std::u32string theirs;
            float q2 = readScore(ocr::greedyRead(s.logp, *s.cs), theirs);
            if (s.ai) q2 = std::min(q2, ocr::lineConfidence(ocr::linePosterior(s.logp, *s.cs, theirs)));
            if (bare(theirs) == mine) { q = std::min(q, q2); continue; }
            lo.agreeState = VS_AGREE_DIFFERENT;
            const std::vector<char> diff = differing(mine, bare(theirs));
            for (size_t i = 0, k = 0; i < lo.read.size(); ++i)
                if (lo.read[i].c != U' ') { if (diff[k]) lo.unsure[i] = 1; ++k; }
        }
        if (lo.agreeState == VS_AGREE_DIFFERENT) q = cutShortQuality(q, minQ);
    }
    lo.quality = q;
}

// Read text under format f: the read is at most the line confidence (AI reader), and (when it passes so far)
// every second reader must read the same text under the format and pass, or the line fails
void agreeFormat(vs_ocr* t, LineOut& lo, const ocr::Format& f, bool dots, Clock::time_point deadline)
{
    const float minQ = t->params.min_quality;
    ocr::FormatRead& fr = lo.fread;
    if (!useFont(t) && !fr.text.empty()) {
        lo.lineConf = ocr::lineConfidence(ocr::linePosterior(lo.logp, t->rec.charset(), fr.text));
        fr.quality = std::min(fr.quality, lo.lineConf);
        if (fr.quality < minQ) fr.pass = false;
    }
    if (!fr.pass || t->agree == VS_AGREE_OFF) return;
    bool late = false;
    const auto again = readAgain(t, lo, dots, deadline, late);
    const std::u32string mine = bare(fr.text);
    lo.agreeState = late || again.empty() ? VS_AGREE_DIFFERENT : VS_AGREE_SAME;
    for (const auto& s : again) {
        lo.readers |= 1 << s.reader;
        ocr::FormatRead fr2 = ocr::readFormat(s.logp, *s.cs, f, ocr::greedyRead(s.logp, *s.cs), minQ);
        if (s.ai && !fr2.text.empty())
            fr2.quality = std::min(fr2.quality, ocr::lineConfidence(ocr::linePosterior(s.logp, *s.cs, fr2.text)));
        if (bare(fr2.text) == mine && fr2.pass && fr2.quality >= minQ) { fr.quality = std::min(fr.quality, fr2.quality); continue; }
        if (bare(fr2.text) == mine) { fr.quality = std::min(fr.quality, fr2.quality); fr.pass = false; continue; }
        lo.agreeState = VS_AGREE_DIFFERENT;
        // the characters read under the format, as compared (extra marks and spaces left out)
        std::u32string shown;
        std::vector<size_t> at;
        for (size_t i = 0; i < fr.chars.size(); ++i)
            if (fr.chars[i].status != VS_CHAR_EXTRA && fr.chars[i].expected != U' ' && fr.chars[i].expected != 0) {
                shown.push_back(fr.chars[i].expected);
                at.push_back(i);
            }
        const std::vector<char> diff = differing(shown == mine ? mine : shown, bare(fr2.text));
        for (size_t k = 0; k < at.size(); ++k)
            if (shown != mine || diff[k]) {
                auto& c = fr.chars[at[k]];
                if (c.status == VS_CHAR_OK) c.status = VS_CHAR_UNSURE;
                c.score = std::min(c.score, std::max(0.f, minQ - 1.f));
            }
    }
    if (lo.agreeState == VS_AGREE_DIFFERENT) { fr.pass = false; fr.quality = cutShortQuality(fr.quality, minQ); }
}

// Read text mode with formats: each format takes the first unused line, top to bottom, that is its line:
// its free read already fits (pass or not: a weak print of the right line is never skipped for a later
// stray line that happens to fit), or it passes under the format. Else the unused line it reads best, as
// failed.
void readFormats(vs_ocr* t, Pass& ps, bool dots, Clock::time_point t0)
{
    const int n = int(ps.lines.size());
    if (!t->formats.empty()) {
        std::vector<char> used(size_t(n), 0);
        ps.quality = 100.f;
        for (const auto& [idx, f] : t->formats) {
            if (msSince(t0) > t->params.time_budget_ms) { ps.missingFormats.push_back(idx); ps.quality = 0; continue; }
            int take = -1, best = -1;
            ocr::FormatRead takeR, bestR;
            for (int j = 0; j < n && take < 0; ++j) {
                if (used[size_t(j)]) continue;
                const auto& lo = ps.lines[size_t(j)];
                ocr::FormatRead fr = ocr::readFormat(lo.logp, charsetOf(t), f, lo.read, t->params.min_quality);
                if (fr.pass || fr.freeFits) { take = j; takeR = std::move(fr); }
                else if (fr.fits && (best < 0 || fr.quality > bestR.quality)) { best = j; bestR = std::move(fr); }
            }
            if (take < 0) { take = best; takeR = std::move(bestR); }
            if (take < 0) { ps.missingFormats.push_back(idx); ps.quality = 0; continue; }
            auto& lo = ps.lines[size_t(take)];
            used[size_t(take)] = 1;
            lo.format = idx;
            lo.fread = std::move(takeR);
            if (lo.line.cutShort || !piecesHeld(lo.line, lo.fread.chars)) {
                lo.fread.pass = false;
                lo.fread.quality = cutShortQuality(lo.fread.quality, t->params.min_quality);
            }
            agreeFormat(t, lo, f, dots, t0 + std::chrono::milliseconds(t->params.time_budget_ms));
            ps.passed += lo.fread.pass ? 1 : 0;
            ps.quality = std::min(ps.quality, lo.fread.quality);
        }
        return;
    }
}

// Read text mode with a layout: the layout's lines are matched to the found lines in order (most of them
// fitting their line's character count). A matched line with no format is read with as many characters
// as its layout line allows (as under a format of any characters); with a format, the format's read must
// fit it too. A layout line with no line found, or a line that does not fit its layout line, fails with
// the reason (Pass::layoutWhy). Other lines stay free reads.
void readLayout(vs_ocr* t, Pass& ps, bool dots, Clock::time_point t0)
{
    const auto& lay = t->layout.lines;
    const int m = int(lay.size()), n = int(ps.lines.size());
    if (m == 0) return;
    const float minQ = t->params.min_quality;
    if (t->formats.empty()) ps.quality = 100.f;
    auto textOf = [](const LineOut& lo) {
        if (lo.format >= 0) return lo.fread.text;
        std::u32string s;
        for (const auto& rc : lo.read) s.push_back(rc.c);
        return s;
    };
    auto count = [](const std::u32string& s) { return int(std::count_if(s.begin(), s.end(), [](char32_t c) { return c != U' '; })); };
    // ordered matching: dp over (layout line i, found line j); a pair scores 1, and 1 more as its read's
    // count comes near the layout line's (a full 1 inside its range)
    std::vector<std::vector<float>> dp(size_t(m + 1), std::vector<float>(size_t(n + 1), 0.f));
    std::vector<std::vector<int8_t>> how(size_t(m + 1), std::vector<int8_t>(size_t(n + 1), 0));
    for (int i = 1; i <= m; ++i) how[size_t(i)][0] = 2;
    for (int j = 1; j <= n; ++j) how[0][size_t(j)] = 1;
    for (int i = 1; i <= m; ++i)
        for (int j = 1; j <= n; ++j) {
            const auto& L = lay[size_t(i - 1)];
            const LineOut& lo = ps.lines[size_t(j - 1)];
            const int k = count(textOf(lo));
            const int off = k < L.minChars() ? L.minChars() - k : k > L.maxChars() ? k - L.maxChars() : 0;
            const float fit = k == 0 ? -1.f : 1.f - std::min(1.f, float(off) / float(std::max(1, L.maxChars())));
            float best = dp[size_t(i)][size_t(j - 1)];
            int8_t h = 1;
            if (dp[size_t(i - 1)][size_t(j)] > best) { best = dp[size_t(i - 1)][size_t(j)]; h = 2; }
            const float sc = dp[size_t(i - 1)][size_t(j - 1)] + 1.f + fit;
            if (fit >= 0 && sc > best) { best = sc; h = 3; }
            dp[size_t(i)][size_t(j)] = best;
            how[size_t(i)][size_t(j)] = h;
        }
    std::vector<int> missing;
    for (int i = m, j = n; i > 0 || j > 0;) {
        const int8_t h = how[size_t(i)][size_t(j)];
        if (h == 3) { ps.lines[size_t(j - 1)].layout = i - 1; --i; --j; }
        else if (h == 2) { missing.push_back(i - 1); --i; }
        else --j;
    }
    int passed = 0;
    for (auto& lo : ps.lines) {
        if (lo.layout < 0) continue;
        const auto& L = lay[size_t(lo.layout)];
        if (lo.format < 0) {
            lo.fread = ocr::readFormat(lo.logp, charsetOf(t), t->layoutFormats[size_t(lo.layout)], lo.read, minQ);
            if (lo.line.cutShort) { lo.fread.pass = false; lo.fread.quality = cutShortQuality(lo.fread.quality, minQ); }
            // read under its layout line as under a format: the second readers must read the same
            agreeFormat(t, lo, t->layoutFormats[size_t(lo.layout)], dots, t0 + std::chrono::milliseconds(t->params.time_budget_ms));
        }
        if (!piecesHeld(lo.line, lo.fread.chars)) {
            if (lo.format >= 0 && lo.fread.pass) --ps.passed;
            lo.fread.pass = false;
            lo.fread.quality = cutShortQuality(lo.fread.quality, minQ);
            if (ps.layoutWhy.empty())
                ps.layoutWhy = "layout line " + std::to_string(lo.layout + 1) + " (" + L.text() + "): print joined into the line holds no character of it";
        }
        // the read must fit: under a format of any characters it always has the count, the groups may not.
        // Forced to the count, a read with a character too few or too many fails on that character's score;
        // the free read says why
        std::string why = ocr::layoutMismatch(lo.fread.text, L);
        if (why.empty() && !lo.fread.pass && lo.format < 0 && !lo.line.cutShort) why = ocr::layoutMismatch(textOf(lo), L);
        if (!why.empty()) {
            if (lo.format >= 0 && lo.fread.pass) --ps.passed;
            lo.fread.pass = false;
            lo.fread.quality = cutShortQuality(lo.fread.quality, minQ);
            if (ps.layoutWhy.empty()) ps.layoutWhy = "layout line " + std::to_string(lo.layout + 1) + " (" + L.text() + "): " + why;
        } else if (!lo.fread.fits && ps.layoutWhy.empty()) {
            ps.layoutWhy = "layout line " + std::to_string(lo.layout + 1) + " (" + L.text() + "): too few columns for its characters";
        }
        if (lo.format < 0) {
            passed += lo.fread.pass ? 1 : 0;
            ps.quality = std::min(ps.quality, lo.fread.quality);
            // with formats, the formats count what passed: a layout line read below min quality says so here
            if (!lo.fread.pass && !t->formats.empty() && ps.layoutWhy.empty())
                ps.layoutWhy = "layout line " + std::to_string(lo.layout + 1) + " (" + L.text() + "): \"" + ocr::toUtf8(lo.fread.text) +
                               "\" is not read clearly";
        }
    }
    if (!missing.empty()) {
        std::reverse(missing.begin(), missing.end());
        if (ps.layoutWhy.empty())
            ps.layoutWhy = "layout line " + std::to_string(missing[0] + 1) + " (" + lay[size_t(missing[0])].text() + ") not found: " +
                           std::to_string(n) + " lines found, the layout has " + std::to_string(m);
        ps.quality = 0;
    }
    // with formats, the formats count what passed; else the layout's lines do
    if (t->formats.empty()) ps.passed = missing.empty() ? passed : std::min(passed, m - 1);
}

// Check text: match the expected lines to found lines in order (best total score) and check them.
void checkExpected(vs_ocr* t, Pass& ps, const ocr::FindParams& fp, const std::vector<std::pair<int, ocr::Pattern>>& pats)
{
    const int m = int(pats.size()), n = int(ps.lines.size());
    std::vector<std::vector<ocr::CheckResult>> res(static_cast<size_t>(m), std::vector<ocr::CheckResult>(static_cast<size_t>(n)));
    for (int i = 0; i < m; ++i)
        for (int j = 0; j < n; ++j)
        {
            const LineOut& lo = ps.lines[size_t(j)];
            ocr::CheckResult& cr = res[size_t(i)][size_t(j)];
            cr = ocr::checkLine(lo.logp, charsetOf(t), pats[size_t(i)].second, lo.read, t->params.min_quality);
            ocr::scorePrint(lo.line, lo.read, cr, t->print, pats[size_t(i)].first, t->params.min_quality);
            ocr::scoreShift(lo.line, cr, t->print, pats[size_t(i)].first, cv::Point2f(float(fp.roi.x), float(fp.roi.y)),
                            t->params.allow_shift_px);
            // cut short, or print joined in that nothing read: never a good part to learn from either
            if (lo.line.cutShort || !piecesHeld(lo.line, cr.chars)) {
                cr.pass = cr.passPrint = false;
                cr.quality = cutShortQuality(cr.quality, t->params.min_quality);
            }
        }
    // ordered matching: dp over (expected i, found j)
    std::vector<std::vector<float>> dp(size_t(m + 1), std::vector<float>(size_t(n + 1), 0.f));
    std::vector<std::vector<int8_t>> how(size_t(m + 1), std::vector<int8_t>(size_t(n + 1), 0));
    for (int i = 1; i <= m; ++i) { dp[size_t(i)][0] = 0; how[size_t(i)][0] = 2; }
    for (int j = 1; j <= n; ++j) how[0][size_t(j)] = 1;
    for (int i = 1; i <= m; ++i)
        for (int j = 1; j <= n; ++j) {
            float best = dp[size_t(i)][size_t(j - 1)];
            int8_t h = 1;                                                     // found line j not expected
            if (dp[size_t(i - 1)][size_t(j)] > best) { best = dp[size_t(i - 1)][size_t(j)]; h = 2; } // expected i missing
            const float s = dp[size_t(i - 1)][size_t(j - 1)] + 1.f + std::max(0.f, res[size_t(i - 1)][size_t(j - 1)].quality);
            if (s > best) { best = s; h = 3; }
            dp[size_t(i)][size_t(j)] = best;
            how[size_t(i)][size_t(j)] = h;
        }
    int i = m, j = n;
    while (i > 0 || j > 0) {
        const int8_t h = how[size_t(i)][size_t(j)];
        if (h == 3) {
            auto& lo = ps.lines[size_t(j - 1)];
            lo.expected = i - 1;
            lo.check = res[size_t(i - 1)][size_t(j - 1)];
            --i; --j;
        } else if (h == 2) { ps.missing.push_back(i - 1); --i; }
        else --j;
    }
    std::reverse(ps.missing.begin(), ps.missing.end());
    ps.quality = 100.f;
    for (const auto& lo : ps.lines)
        if (lo.expected >= 0) {
            ps.passed += lo.check.pass ? 1 : 0;
            ps.quality = std::min(ps.quality, lo.check.quality);
        }
    if (!ps.missing.empty()) ps.quality = 0;
}

Pass runPass(vs_ocr* t, const cv::Mat& gray, const ocr::FindParams& fp, bool dots,
             const std::vector<std::pair<int, ocr::Pattern>>& pats, Clock::time_point t0, double& locateMs)
{
    Pass ps;
    const auto tl = Clock::now();
    std::vector<ocr::Line> found = ocr::findLines(gray, fp, dots);
    locateMs += msSince(tl);
    for (auto& l : found) {
        LineOut lo;
        lo.line = std::move(l);
        lo.logp = useFont(t) ? t->font.run(lo.line.image, dots, t0 + std::chrono::milliseconds(t->params.time_budget_ms), &lo.cuts)
                             : t->rec.run(lo.line.image);
        lo.read = ocr::greedyRead(lo.logp, charsetOf(t));
        ps.lines.push_back(std::move(lo));
        if (msSince(t0) > t->params.time_budget_ms) break;
    }
    ps.unread = int(found.size()) - int(ps.lines.size());
    // the taught font gives up a line when the time budget runs out (a blank read): the last line may not be read
    if (ps.unread == 0 && !ps.lines.empty() && msSince(t0) > t->params.time_budget_ms) ps.unread = 1;
    const int m = int(pats.size());
    if (m == 0) {
        readFormats(t, ps, dots, t0);
        readLayout(t, ps, dots, t0);
        if (t->formats.empty() && t->layout.lines.empty()) {
            // every line read must pass: read clearly, and the same by the second readers
            ps.quality = 100.f;
            for (auto& lo : ps.lines) {
                scoreRead(t, lo, dots, t0 + std::chrono::milliseconds(t->params.time_budget_ms));
                if (!readSomething(lo.read)) continue;
                ++ps.read;
                ps.passed += lo.quality >= t->params.min_quality ? 1 : 0;
                ps.quality = std::min(ps.quality, lo.quality);
            }
            if (ps.read == 0) ps.quality = 0;
        } else {
            // lines no format or layout line took stay free reads (the tool may check them by a pattern of its
            // own): scored as such
            for (auto& lo : ps.lines)
                if (lo.format < 0 && lo.layout < 0) scoreRead(t, lo, dots, t0 + std::chrono::milliseconds(t->params.time_budget_ms));
        }
        return ps;
    }
    if (m > 0 && !t->layout.lines.empty()) {
        // Check text: each expected line is its layout line's (same index); the job's expected text must
        // fit it (an expected line typed with a character too many or too few fails on every part)
        for (const auto& [idx, pat] : pats) {
            if (idx >= int(t->layout.lines.size()) || pat.candidates.empty()) continue;
            const auto& L = t->layout.lines[size_t(idx)];
            const int k = int(pat.candidates[0].size());
            if (ps.layoutWhy.empty() && (k < L.minChars() || k > L.maxChars()))
                ps.layoutWhy = "layout line " + std::to_string(idx + 1) + " (" + L.text() + "): the expected text has " + std::to_string(k) + " characters";
        }
        int found = 0;
        for (const auto& lo : ps.lines) found += lo.read.empty() ? 0 : 1;
        if (ps.layoutWhy.empty() && found < int(t->layout.lines.size()))
            ps.layoutWhy = std::to_string(found) + " lines found, the layout has " + std::to_string(t->layout.lines.size());
    }
    checkExpected(t, ps, fp, pats);
    return ps;
}


} // namespace

extern "C" {

void vs_ocr_default_params(vs_ocr_params* p)
{
    if (!p) return;
    *p = vs_ocr_params{};
    p->struct_size = int32_t(sizeof(vs_ocr_params));
    p->print = VS_PRINT_AUTO;
    p->polarity = VS_POLARITY_AUTO;
    p->max_lines = 8;
    p->min_quality = 60.f;
    p->max_skew_deg = 15.f;
    p->time_budget_ms = 100;
}

vs_ocr* vs_ocr_create(const vs_ocr_params* p)
{
    vs_ocr* t = new (std::nothrow) vs_ocr();
    if (!t) return nullptr;
    vs_ocr_params d;
    vs_ocr_default_params(&d);
    t->params = sanitize(p && p->struct_size == int32_t(sizeof(vs_ocr_params)) ? *p : d);
    return t;
}

void vs_ocr_destroy(vs_ocr* t) { delete t; }

int32_t vs_ocr_set_params(vs_ocr* t, const vs_ocr_params* p)
{
    if (!t || !p) return -1;
    if (p->struct_size != int32_t(sizeof(vs_ocr_params))) { t->error = "vs_ocr_params size mismatch"; return -1; }
    t->params = sanitize(*p);
    return 0;
}

int32_t vs_ocr_load_model(vs_ocr* t, const char* onnx_path, const char* charset_path)
{
    if (!t || !onnx_path || !charset_path) return -1;
    try {
        if (!t->rec.load(onnx_path, charset_path, t->error)) return -2;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
    return 0;
}

int32_t vs_ocr_load_model_buffer(vs_ocr* t, const uint8_t* onnx, size_t onnx_size, const char* charset_path)
{
    if (!t || !onnx || onnx_size == 0 || !charset_path) {
        if (t) t->error = "empty model buffer or charset path";
        return -1;
    }
    try {
        if (!t->rec.loadBuffer(onnx, onnx_size, charset_path, t->error)) return -2;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
    return 0;
}

int32_t vs_ocr_set_expected(vs_ocr* t, int32_t line, const char* pattern)
{
    if (!t || line < 0 || line > 63) return -1;
    if (!pattern) { t->expected.erase(line); return 0; }
    try {
        ocr::Pattern p;
        if (!ocr::expandPattern(pattern, std::time(nullptr), p, t->error)) return -1;  // syntax check now
        t->expected[line] = pattern;
        return 0;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
}

void vs_ocr_clear_expected(vs_ocr* t) { if (t) t->expected.clear(); }

int32_t vs_ocr_set_agreement(vs_ocr* t, int32_t mode)
{
    if (!t || mode < VS_AGREE_OFF || mode > VS_AGREE_SHIFT) return -1;
    t->agree = mode;
    return 0;
}

int32_t vs_ocr_load_second_model(vs_ocr* t, const char* onnx_path, const char* charset_path)
{
    if (!t) return -1;
    if (!onnx_path) { t->rec2 = ocr::Recognizer(); return 0; }
    if (!charset_path) return -1;
    try {
        if (!t->rec2.load(onnx_path, charset_path, t->error)) return -2;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
    return 0;
}

int32_t vs_ocr_load_second_model_buffer(vs_ocr* t, const uint8_t* onnx, size_t onnx_size, const char* charset_path)
{
    if (!t) return -1;
    if (!onnx) { t->rec2 = ocr::Recognizer(); return 0; }  // NULL buffer unloads, as the path API does
    if (onnx_size == 0 || !charset_path) {
        t->error = "empty model buffer or charset path";
        return -1;
    }
    try {
        if (!t->rec2.loadBuffer(onnx, onnx_size, charset_path, t->error)) return -2;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
    return 0;
}

void vs_ocr_set_clock(vs_ocr* t, int64_t unix_seconds) { if (t) t->clock = std::time_t(unix_seconds); }

int32_t vs_ocr_set_format(vs_ocr* t, int32_t index, const char* format)
{
    if (!t || index < 0 || index > 63) return -1;
    if (!format) { t->formats.erase(index); return 0; }
    try {
        ocr::Format f;
        if (!ocr::compileFormat(format, f, t->error)) return -1;
        t->formats[index] = std::move(f);
        return 0;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
}

void vs_ocr_clear_formats(vs_ocr* t) { if (t) t->formats.clear(); }

int32_t vs_ocr_set_layout(vs_ocr* t, const char* layout)
{
    if (!t) return -1;
    try {
        if (!layout || !*layout) { t->layout = ocr::Layout(); t->layoutFormats.clear(); return 0; }
        ocr::Layout l;
        if (!ocr::parseLayout(layout, l, t->error)) return -1;
        std::vector<ocr::Format> fs;
        for (const auto& line : l.lines) {
            fs.emplace_back();
            if (!ocr::compileFormat(ocr::layoutFormat(line), fs.back(), t->error)) return -1;
        }
        t->layout = std::move(l);
        t->layoutFormats = std::move(fs);
        return 0;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
}

int32_t vs_ocr_get_layout(const vs_ocr* t, char* buf, int32_t cap)
{
    if (!t) return -1;
    const std::string& s = t->layout.source;
    const int32_t need = int32_t(s.size() + 1);
    if (buf && cap >= need) std::copy(s.c_str(), s.c_str() + need, buf);
    return need;
}

int32_t vs_ocr_learn_layout(vs_ocr* t)
{
    if (!t) return -1;
    if (t->layoutTexts.empty()) { t->error = "the last run did not pass: learn the layout from a good part"; return -1; }
    try {
        const std::string src = ocr::layoutOf(t->layoutTexts);
        if (src.empty()) { t->error = "the last run read no characters: learn the layout from a good part"; return -1; }
        // a group of more characters than a layout allows is refused, and the layout set before is kept
        if (vs_ocr_set_layout(t, src.c_str()) != 0) return -1;
        return int32_t(t->layout.lines.size());
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
}

const char* vs_ocr_layout_reason(const vs_ocr* t) { return t ? t->layoutWhy.c_str() : ""; }

int32_t vs_ocr_line_layout(const vs_ocr* t, int32_t index)
{
    if (!t || index < 0 || index >= int32_t(t->lineLayouts.size())) return -1;
    return t->lineLayouts[size_t(index)];
}

void vs_ocr_set_tight_spacing(vs_ocr* t, int32_t tight) { if (t) t->tight = tight != 0; }

int32_t vs_ocr_get_tight_spacing(const vs_ocr* t) { return t && t->tight ? 1 : 0; }

int32_t vs_ocr_run(vs_ocr* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride)
{
    if (!t) return -1;
    t->lines.clear();
    t->texts.clear();
    t->expTexts.clear();
    t->chars.clear();
    t->quals.clear();
    t->exts.clear();
    t->lineLayouts.clear();
    t->layoutWhy.clear();
    t->layoutTexts.clear();
    t->checked.clear();
    t->images.clear();
    t->stats = vs_ocr_stats{};
    if (!pixels || width <= 0 || height <= 0 || stride < width) { t->error = "bad image"; return -1; }
    if (useFont(t) ? !t->font.ready() : !t->rec.ready()) {
        t->error = useFont(t) ? "no font taught (vs_ocr_teach_font / vs_ocr_set_font)" : "no model loaded (vs_ocr_load_model)";
        return -1;
    }
    try {
        const auto t0 = Clock::now();
        const cv::Mat gray(height, width, CV_8U, const_cast<uint8_t*>(pixels), size_t(stride));
        const vs_ocr_params& P = t->params;
        ocr::FindParams fp;
        double locateMs = 0;
        if (!findParams(t, gray, fp, locateMs)) return -1;

        const std::time_t clock = t->clock ? t->clock : std::time(nullptr);
        std::vector<std::pair<int, ocr::Pattern>> pats;
        for (const auto& [idx, src] : t->expected) {
            ocr::Pattern p;
            if (!ocr::expandPattern(src, clock, p, t->error)) return -1;
            pats.emplace_back(idx, std::move(p));
        }

        Pass best;
        bool dotsUsed = false;
        if (fp.charHeight > 0) {
            const bool tryDotsFirst = P.print == VS_PRINT_DOT;
            best = runPass(t, gray, fp, tryDotsFirst, pats, t0, locateMs);
            dotsUsed = tryDotsFirst;
            const int want = !pats.empty() ? int(pats.size()) : !t->formats.empty() ? int(t->formats.size())
                           : !t->layout.lines.empty() ? int(t->layout.lines.size()) : 1;
            if (P.print == VS_PRINT_AUTO && (best.passed < want || !best.layoutWhy.empty()) && msSince(t0) < 0.5 * P.time_budget_ms) {
                Pass alt = runPass(t, gray, fp, true, pats, t0, locateMs);
                // free reads: never a pass that judged fewer lines (a doubtful line lost or merged by the dot pass)
                const bool fewer = pats.empty() && t->formats.empty() && t->layout.lines.empty()
                                   && alt.read + alt.unread < best.read + best.unread;
                const bool fitsBest = best.layoutWhy.empty(), fitsAlt = alt.layoutWhy.empty();
                if (!fewer && (fitsAlt != fitsBest ? fitsAlt
                    : alt.passed > best.passed || (alt.passed == best.passed && alt.quality > best.quality))) {
                    best = std::move(alt);
                    dotsUsed = true;
                }
            }
        }
        (void)dotsUsed;

        // results
        const float minQ = P.min_quality;
        // fmt: the format the line was read with; lay: its layout line (Read text mode), -1 = none
        auto addLine = [&](const LineOut* lo, int expIdx, const ocr::CheckResult* cr, int fmt = -1, int lay = -1) {
            vs_ocr_line L{};
            L.format = fmt;
            L.first_char = int32_t(t->chars.size());
            std::u32string text;
            if (lo && (fmt >= 0 || lay >= 0)) text = lo->fread.text;   // read under its format or layout
            else if (lo) for (const auto& rc : lo->read) text.push_back(rc.c);
            if (lo) {
                for (int i = 0; i < 4; ++i) { L.x[i] = lo->line.corners[i].x; L.y[i] = lo->line.corners[i].y; }
                L.angle_deg = lo->line.angleDeg;
                L.slant_deg = lo->line.slantDeg;
                L.char_height_px = lo->line.charHeight;
            }
            if (cr) {
                L.checked = 1;
                L.pass = cr->pass ? 1 : 0;
                L.quality = std::max(0.f, cr->quality);
                for (const auto& cc : cr->chars) {
                    vs_ocr_char c{};
                    c.codepoint = int32_t(cc.expected);
                    c.found = int32_t(cc.found);
                    c.score = cc.score;
                    c.status = cc.status;
                    // a failed character the font was never taught: the reason, not a new verdict
                    if (useFont(t) && c.status != VS_CHAR_OK && cc.expected != 0 && t->font.charset().find(cc.expected) < 0)
                        c.status = VS_CHAR_UNKNOWN;
                    if (lo && cc.t0 >= 0) charBox(*lo, cc.t0, cc.t1, c.x, c.y);
                    t->chars.push_back(c);
                    vs_ocr_char_quality qy{};
                    qy.struct_size = int32_t(sizeof(vs_ocr_char_quality));
                    qy.recognition = cc.recognition;
                    qy.contrast = cc.contrast;
                    qy.ink = cc.ink;
                    qy.pitch = cc.pitch;
                    qy.contrast_score = cc.qContrast;
                    qy.ink_score = cc.qInk;
                    qy.pitch_score = cc.qPitch;
                    qy.reason = cc.reason;
                    qy.learned = cc.learned ? 1 : 0;
                    qy.margin = cc.margin;
                    t->quals.push_back(qy);
                }
                for (const auto& rc : cr->extras) {
                    vs_ocr_char c{};
                    c.found = int32_t(rc.c);
                    c.score = 100.f * rc.conf;
                    c.status = VS_CHAR_EXTRA;
                    if (lo) corners(lo->line.toImage, double(rc.t0 * ocr::kStride) - 2, double((rc.t1 + 1) * ocr::kStride) + 2, c.x, c.y);
                    t->chars.push_back(c);
                    t->quals.push_back(vs_ocr_char_quality{int32_t(sizeof(vs_ocr_char_quality))});
                }
            } else if (lo) {
                bool any = false;   // a read of spaces only is no read
                for (size_t i = 0; i < lo->read.size(); ++i) {
                    const auto& rc = lo->read[i];
                    if (rc.c == U' ') continue;
                    vs_ocr_char c{};
                    c.codepoint = c.found = int32_t(rc.c);
                    c.score = 100.f * rc.conf;
                    // a second reader read otherwise here (vs_agree)
                    if (i < lo->unsure.size() && lo->unsure[i]) c.score = std::min(c.score, std::max(0.f, minQ - 1.f));
                    c.status = c.score >= minQ ? VS_CHAR_OK : VS_CHAR_UNSURE;
                    // unsure, and like no taught character at all: most likely one never taught
                    if (c.status == VS_CHAR_UNSURE && useFont(t) && fitAt(*lo, rc.t0, rc.t1) <= 0.f) c.status = VS_CHAR_UNKNOWN;
                    charBox(*lo, rc.t0, rc.t1, c.x, c.y);
                    t->chars.push_back(c);
                    t->quals.push_back(vs_ocr_char_quality{int32_t(sizeof(vs_ocr_char_quality))});
                    any = true;
                }
                // lowest character score, line confidence, cut short and the second readers: scoreRead; a line
                // found but not expected in Check text: its lowest character score
                std::u32string text;
                const float q = lo->scored ? lo->quality : readScore(lo->read, text);
                L.quality = !any ? 0.f : !lo->scored && lo->line.cutShort ? cutShortQuality(q, minQ) : q;
                L.pass = lo->scored && any && L.quality >= minQ ? 1 : 0;
            }
            L.char_count = int32_t(t->chars.size()) - L.first_char;
            vs_ocr_line_ext ext{};
            ext.struct_size = int32_t(sizeof(vs_ocr_line_ext));
            ext.shift_state = VS_SHIFT_OFF;
            ext.shift_px = -1;
            ext.cut_short = lo && lo->line.cutShort ? 1 : 0;
            ext.agreement = lo ? lo->agreeState : VS_AGREE_NOT_CHECKED;
            ext.second_readers = lo ? lo->readers : 0;
            ext.line_confidence = lo ? lo->lineConf : 100.f;
            if (cr && fmt < 0 && lay < 0) {
                ext.shift_state = cr->shiftState;
                ext.shift_px = cr->shift;
                ext.shift_x = cr->shiftX;
                ext.shift_y = cr->shiftY;
                ext.pos_x = cr->posX;
                ext.pos_y = cr->posY;
                ext.margin = cr->margin;
            }
            t->exts.push_back(ext);
            t->texts.push_back(ocr::toUtf8(text));
            t->expTexts.push_back(fmt >= 0 ? t->formats.at(fmt).source
                                  : lay >= 0 ? t->layoutFormats[size_t(lay)].source
                                  : cr ? ocr::toUtf8(cr->expected) : (expIdx >= 0 ? pats[size_t(expIdx)].second.source : ""));
            t->lineLayouts.push_back(lay >= 0 ? lay : expIdx >= 0 && !pats.empty() && pats[size_t(expIdx)].first < int(t->layout.lines.size())
                                                   ? pats[size_t(expIdx)].first : -1);
            t->lines.push_back(L);
            t->images.push_back(lo ? lo->line.image : cv::Mat());
        };
        for (const auto& lo : best.lines) {
            if (lo.format >= 0 || lo.layout >= 0) {
                ocr::CheckResult fr;
                fr.chars = lo.fread.chars;
                fr.quality = lo.fread.quality;
                fr.pass = lo.fread.pass;
                addLine(&lo, -1, &fr, lo.format, lo.layout);
            } else {
                addLine(&lo, lo.expected, lo.expected >= 0 ? &lo.check : nullptr);
            }
        }
        for (int f : best.missingFormats) {
            ocr::CheckResult miss;
            addLine(nullptr, -1, &miss, f);
        }
        for (int e : best.missing) {
            ocr::CheckResult miss;
            miss.expected = pats[size_t(e)].second.candidates.empty() ? U"" : pats[size_t(e)].second.candidates[0];
            miss.margin = -ocr::kMaxMargin;
            for (char32_t c : miss.expected) {
                ocr::CheckChar cc;
                cc.expected = c;
                cc.margin = -ocr::kMaxMargin;
                cc.status = VS_CHAR_MISSING;
                cc.reason = VS_PRINT_UNCLEAR;
                miss.chars.push_back(cc);
            }
            addLine(nullptr, e, &miss);
        }
        for (size_t i = 0; i < t->lines.size(); ++i) {
            t->lines[i].text = t->texts[i].c_str();
            t->lines[i].expected = t->expTexts[i].c_str();
        }
        t->stats.lines = int32_t(best.lines.size());
        t->stats.expected_lines = int32_t(pats.size());
        t->stats.pass = !pats.empty() ? (best.missing.empty() && best.passed == int(pats.size()))
                      : !t->formats.empty() ? (best.missingFormats.empty() && best.passed == int(t->formats.size()))
                      : !t->layout.lines.empty() ? best.passed == int(t->layout.lines.size())
                      : (best.read > 0 && best.passed == best.read && best.unread == 0);
        // the layout: lines that do not fit it fail the part, whatever else passed
        if (!t->layout.lines.empty() && fp.charHeight <= 0) best.layoutWhy = "no text found in the region";
        t->layoutWhy = best.layoutWhy;
        if (!t->layoutWhy.empty()) t->stats.pass = 0;
        // the lines of a part that passed, for vs_ocr_learn_layout: checked lines as expected (grouped as
        // the free read saw the gaps when it read as many characters), read lines as read
        if (t->stats.pass) {
            auto noSpace = [](const std::u32string& s) { std::u32string o; for (char32_t c : s) if (c != U' ') o.push_back(c); return o; };
            for (const auto& lo : best.lines) {
                std::u32string free;
                for (const auto& rc : lo.read) free.push_back(rc.c);
                if (lo.expected >= 0) {
                    const std::u32string& e = lo.check.expected;
                    t->layoutTexts.push_back(noSpace(free).size() == e.size() ? free : e);
                } else if (lo.format >= 0 || lo.layout >= 0) {
                    if (lo.fread.pass) t->layoutTexts.push_back(lo.fread.text);
                } else if (pats.empty() && !noSpace(free).empty() && !lo.line.cutShort) {
                    // every line of the block, those no format takes too: a layout of only the format lines
                    // would let the finder join the others' fields side by side
                    t->layoutTexts.push_back(free);
                }
            }
        }
        // a good part to learn from: every expected line found and OK, or failing only because no position is
        // learned yet for Allow shift (learning that part is how the position is learned)
        bool learnable = !pats.empty() && best.missing.empty() && t->layoutWhy.empty();
        int matched = 0;
        for (const auto& lo : best.lines)
            if (lo.expected >= 0) {
                ++matched;
                learnable = learnable && lo.check.passPrint && lo.check.shiftState != VS_SHIFT_BEYOND;
            }
        if (learnable && matched == int(pats.size()))
            for (const auto& lo : best.lines)
                if (lo.expected >= 0) t->checked.emplace_back(pats[size_t(lo.expected)].first, lo.check);
        t->stats.locate_ms = float(locateMs);
        t->stats.total_ms = float(msSince(t0));
        t->stats.read_ms = t->stats.total_ms - t->stats.locate_ms;
        t->stats.timed_out = t->stats.total_ms > P.time_budget_ms ? 1 : 0;
        return 0;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    } catch (...) {
        t->error = "unknown error";
        return -2;
    }
}

int32_t vs_ocr_teach_font(vs_ocr* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                          int32_t line, const char* text)
{
    if (!t) return -1;
    if (!pixels || width <= 0 || height <= 0 || stride < width) { t->error = "bad image"; return -1; }
    if (!text || line < 0) { t->error = "give the line and the text printed on it"; return -1; }
    try {
        const cv::Mat gray(height, width, CV_8U, const_cast<uint8_t*>(pixels), size_t(stride));
        ocr::FindParams fp;
        double ms = 0;
        if (!findParams(t, gray, fp, ms)) return -1;
        if (fp.charHeight <= 0) { t->error = "no text found in the region"; return -1; }
        const bool dots = t->params.print == VS_PRINT_DOT;
        const std::vector<ocr::Line> found = ocr::findLines(gray, fp, dots);
        if (line >= int32_t(found.size())) {
            t->error = "line " + std::to_string(int64_t(line) + 1) + " not found (" + std::to_string(found.size()) + " lines found)";
            return -1;
        }
        const int n = t->font.teach(found[size_t(line)].image, ocr::fromUtf8(text), dots, t->error);
        return n < 0 ? -1 : int32_t(n);
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    } catch (...) {
        t->error = "unknown error";
        return -2;
    }
}

int32_t vs_ocr_teach_char(vs_ocr* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
                          float x, float y, const char* character)
{
    if (!t) return -1;
    if (!pixels || width <= 0 || height <= 0 || stride < width) { t->error = "bad image"; return -1; }
    if (!t->font.ready()) { t->error = "teach a whole line first"; return -1; }
    try {
        const std::u32string ch = character ? ocr::fromUtf8(character) : std::u32string();
        if (ch.size() != 1 || ch[0] == U' ') { t->error = "type the one character printed there"; return -1; }
        const cv::Mat gray(height, width, CV_8U, const_cast<uint8_t*>(pixels), size_t(stride));
        ocr::FindParams fp;
        double ms = 0;
        if (!findParams(t, gray, fp, ms)) return -1;
        if (fp.charHeight <= 0) { t->error = "no text found in the region"; return -1; }
        // the line holding the point, cut as the font reads it; the character cut there is taught. Dot
        // print is melted as when teaching a line; Find out (AUTO) tries solid print first, as it reads
        bool onLine = false;
        std::vector<bool> passes{t->params.print == VS_PRINT_DOT};
        if (t->params.print == VS_PRINT_AUTO) passes.push_back(true);
        for (const bool dots : passes) {
            for (const auto& line : ocr::findLines(gray, fp, dots)) {
                // the line's (u, v) at the point: inside when 0 <= u < width and 0 <= v <= kLineH
                const cv::Matx22d A(line.toImage(0, 0), line.toImage(0, 1), line.toImage(1, 0), line.toImage(1, 1));
                if (std::abs(cv::determinant(A)) < 1e-9) continue;
                const cv::Vec2d uv = A.inv() * cv::Vec2d(x - line.toImage(0, 2), y - line.toImage(1, 2));
                const double u = uv[0];
                if (u < 0 || u >= line.image.cols || uv[1] < 0 || uv[1] > ocr::kLineH) continue;
                onLine = true;
                std::vector<ocr::FontCut> cuts;
                t->font.run(line.image, dots, Clock::time_point::max(), &cuts);
                const ocr::FontCut* hit = nullptr;
                double gap = 3.0;   // px: a point just beside a character's columns still picks it
                for (const auto& c : cuts) {
                    const double d = u < c.cols.start ? c.cols.start - u : u >= c.cols.end ? u - c.cols.end + 1 : 0.0;
                    if (d <= gap) { gap = d; hit = &c; }
                }
                if (!hit) continue;
                const int n = t->font.teachCell(line.image, hit->cols, ch[0], dots, t->error);
                return n < 0 ? -1 : int32_t(n);
            }
        }
        t->error = onLine ? "no character at that place" : "no text line at that place";
        return -1;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    } catch (...) {
        t->error = "unknown error";
        return -2;
    }
}

void vs_ocr_clear_font(vs_ocr* t)
{
    if (!t) return;
    try { t->font.clear(); } catch (...) {}
}

int32_t vs_ocr_font_glyphs(const vs_ocr* t) { return t ? int32_t(t->font.glyphs()) : 0; }

int32_t vs_ocr_get_font(const vs_ocr* t, char* buf, int32_t cap)
{
    if (!t) return -1;
    try {
        const std::string s = t->font.save();
        const int32_t need = int32_t(s.size() + 1);
        if (buf && cap >= need) std::copy(s.c_str(), s.c_str() + need, buf);
        return need;
    } catch (...) {
        return -2;
    }
}

int32_t vs_ocr_set_font(vs_ocr* t, const char* font)
{
    if (!t || !font) return -1;
    try {
        return t->font.load(font, t->error) ? 0 : -1;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    } catch (...) {
        t->error = "unknown error";
        return -2;
    }
}

int32_t vs_ocr_learn_print(vs_ocr* t)
{
    if (!t) return -1;
    if (t->checked.empty()) { t->error = "the last run did not check text that passed: learn from good parts only"; return -1; }
    try {
        for (const auto& [line, cr] : t->checked) t->print.add(cr, line);
        t->checked.clear();      // each run is one part
        return int32_t(++t->print.parts);
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
}

void vs_ocr_clear_print(vs_ocr* t) { if (t) t->print.clear(); }

int32_t vs_ocr_print_parts(const vs_ocr* t) { return t ? int32_t(t->print.parts) : 0; }

int32_t vs_ocr_get_print(const vs_ocr* t, char* buf, int32_t cap)
{
    if (!t) return -1;
    try {
        const std::string s = t->print.save();
        const int32_t need = int32_t(s.size() + 1);
        if (buf && cap >= need) std::copy(s.c_str(), s.c_str() + need, buf);
        return need;
    } catch (...) {
        return -2;
    }
}

int32_t vs_ocr_set_print(vs_ocr* t, const char* ref)
{
    if (!t || !ref) return -1;
    try {
        return t->print.load(ref, t->error) ? 0 : -1;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    }
}

int32_t vs_ocr_get_char_quality(const vs_ocr* t, int32_t index, vs_ocr_char_quality* out)
{
    // callers built before margin was added pass the shorter struct: copy only what it holds
    constexpr int32_t kBeforeMargin = int32_t(offsetof(vs_ocr_char_quality, margin));
    if (!t || !out || (out->struct_size != int32_t(sizeof(vs_ocr_char_quality)) && out->struct_size != kBeforeMargin)
        || index < 0 || index >= int32_t(t->quals.size()))
        return -1;
    const int32_t size = out->struct_size;
    std::memcpy(out, &t->quals[size_t(index)], size_t(size));
    out->struct_size = size;
    return 0;
}

float vs_ocr_measure_dots(vs_ocr* t, const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, float* pitch_px)
{
    if (!t) return -1;
    if (pitch_px) *pitch_px = 0;
    if (!pixels || width <= 0 || height <= 0 || stride < width) { t->error = "bad image"; return -1; }
    try {
        const cv::Mat gray(height, width, CV_8U, const_cast<uint8_t*>(pixels), size_t(stride));
        ocr::FindParams fp;
        double ms = 0;
        if (!findParams(t, gray, fp, ms)) return -1;
        float pitch = 0;
        const float d = ocr::measureDots(gray, fp, pitch, t->error);
        if (d <= 0) return -1;
        if (pitch_px) *pitch_px = pitch;
        return d;
    } catch (const std::exception& e) {
        t->error = e.what();
        return -2;
    } catch (...) {
        t->error = "unknown error";
        return -2;
    }
}

int32_t vs_ocr_get_line_ext(const vs_ocr* t, int32_t index, vs_ocr_line_ext* out)
{
    // callers built before cut_short or margin was added pass a shorter struct: copy only what it holds
    constexpr int32_t kBeforeCutShort = int32_t(offsetof(vs_ocr_line_ext, cut_short));
    constexpr int32_t kBeforeMargin = int32_t(offsetof(vs_ocr_line_ext, margin));
    constexpr int32_t kBeforeAgreement = int32_t(offsetof(vs_ocr_line_ext, agreement));
    if (!t || !out || (out->struct_size != int32_t(sizeof(vs_ocr_line_ext)) && out->struct_size != kBeforeCutShort
                       && out->struct_size != kBeforeMargin && out->struct_size != kBeforeAgreement)
        || index < 0 || index >= int32_t(t->exts.size()))
        return -1;
    const int32_t size = out->struct_size;
    std::memcpy(out, &t->exts[size_t(index)], size_t(size));
    out->struct_size = size;
    return 0;
}

int32_t vs_ocr_line_count(const vs_ocr* t) { return t ? int32_t(t->lines.size()) : 0; }

int32_t vs_ocr_get_line(const vs_ocr* t, int32_t index, vs_ocr_line* out)
{
    if (!t || !out || index < 0 || index >= int32_t(t->lines.size())) return -1;
    *out = t->lines[size_t(index)];
    return 0;
}

int32_t vs_ocr_char_count(const vs_ocr* t) { return t ? int32_t(t->chars.size()) : 0; }

int32_t vs_ocr_get_char(const vs_ocr* t, int32_t index, vs_ocr_char* out)
{
    if (!t || !out || index < 0 || index >= int32_t(t->chars.size())) return -1;
    *out = t->chars[size_t(index)];
    return 0;
}

void vs_ocr_get_stats(const vs_ocr* t, vs_ocr_stats* out)
{
    if (!t || !out) return;
    *out = t->stats;
}

int32_t vs_ocr_get_line_image(const vs_ocr* t, int32_t index, uint8_t* buf, int32_t cap, int32_t* width)
{
    if (!t || index < 0 || index >= int32_t(t->images.size())) return -1;
    const cv::Mat& m = t->images[size_t(index)];
    if (m.empty()) return -1;
    if (width) *width = m.cols;
    const int32_t need = int32_t(m.total());
    if (buf && cap >= need)
        for (int y = 0; y < m.rows; ++y) std::copy(m.ptr<uint8_t>(y), m.ptr<uint8_t>(y) + m.cols, buf + size_t(y) * size_t(m.cols));
    return need;
}

const char* vs_ocr_last_error(const vs_ocr* t) { return t ? t->error.c_str() : "no handle"; }

const char* vs_ocr_version(void) { return "vs_ocr 0.1.0"; }

} // extern "C"
