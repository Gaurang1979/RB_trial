// ocr_tests - unit tests of the OCR / OCV tool internals (no test framework, like engine_tests).
//
//   patterns     expected-line patterns: literals, wildcards, {TIME} windows, {DATE} offsets, escapes
//   check        the OCV score from known per-column probabilities: right text passes, one wrong
//                character fails with status WRONG, a missing character fails with status MISSING
//   margin       the verification margin: clear print far from failing, a look-alike close, a misprint
//                below 0; reported only (the pass rule is unchanged)
//   format       reading under a format: allowed characters chosen, weak ones and extra marks still fail
//   lines        the line finder on drawn text: rotated, slanted, light on dark, dot matrix
//   model        with ml/models/ocrnet.onnx present: read and check drawn lines end to end
//   pitch        fixed-pitch cells for dot print (CIJ) on a made-up ink profile
//   font         classical OCR from a taught font, solid and dot print (dots missing too)
//   print        print quality learned from good parts: broken, faint, out-of-place and extra print
//   shift        Allow shift: the line's place learned, within / beyond / unlearned; empty expected lines
//   dots/inkjet  dot size measured, dot print read with dot size and join strength, inkjet grain
//   layout       text-line layout: parsed, learned, a split line joined, lines that do not fit fail with the
//                reason; dot print spaced tight read with tight spacing on
//   agreement    Read text safety: line confidence, a second reader must read the same text

#include "ocr.hpp"
#include "vs_ocr.h"

#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <cstddef>
#include <cstdlib>

#include <chrono>
#include <cmath>
#include <algorithm>
#include <cstdio>
#include <fstream>
#include <array>
#include <map>
#include <random>
#include <string>
#include <vector>
#include <limits>

#include "check.h"

// The tool's time budget is a limit of the optimized build. In a Debug build each OCR tool made here gets
// vs_test_ms() of its budget, so the reads behind the correctness checks finish (Req 17.11 AUD-13).
static vs_ocr* testOcrCreate(const vs_ocr_params* p)
{
    if (!p) return vs_ocr_create(p);
    vs_ocr_params q = *p;
    q.time_budget_ms = vs_test_ms(q.time_budget_ms);
    return vs_ocr_create(&q);
}
#define vs_ocr_create(p) testOcrCreate(p)

namespace {

int failures = 0;

void check(bool ok, const std::string& what)
{
    std::printf("%s  %s\n", ok ? "ok  " : "FAIL", what.c_str());
    if (!ok) ++failures;
}

std::time_t at(int y, int mo, int d, int h, int mi)
{
    std::tm tm{};
    tm.tm_year = y - 1900; tm.tm_mon = mo - 1; tm.tm_mday = d; tm.tm_hour = h; tm.tm_min = mi; tm.tm_isdst = -1;
    return std::mktime(&tm);
}

void testPatterns()
{
    ocr::Pattern p;
    std::string err;
    check(ocr::expandPattern("\xE2\x82\xB9 46.00, HWN937", 0, p, err) && p.candidates.size() == 1 &&
          p.candidates[0] == U"₹46.00,HWN937", "literal pattern, spaces dropped");
    check(ocr::expandPattern("A?C", 0, p, err) && p.candidates[0] == std::u32string(U"A") + char32_t(0) + U"C", "? is a wildcard");
    check(ocr::expandPattern("A\\?C", 0, p, err) && p.candidates[0] == U"A?C", "\\? is a literal ?");
    check(ocr::expandPattern("# 09/28 {TIME:HH:mm+-2}", at(2026, 10, 23, 16, 59), p, err) && p.candidates.size() == 5 &&
          p.candidates.front() == U"#09/2816:57" && p.candidates.back() == U"#09/2817:01", "TIME window of 5 minutes across the hour");
    check(ocr::expandPattern("EXP {DATE:MM/YY+730d}", at(2026, 10, 23, 12, 0), p, err) && p.candidates.size() == 1 &&
          p.candidates[0] == U"EXP10/28", "DATE with an offset in days");
    check(ocr::expandPattern("{DATE:DD MMM YYYY}", at(2026, 1, 5, 12, 0), p, err) && p.candidates[0] == U"05JAN2026", "month names");
    check(!ocr::expandPattern("{BATCH}", 0, p, err), "an unfilled field is refused");
    check(!ocr::expandPattern("{TIME:HH:mm", 0, p, err), "a missing } is refused");
}

// log-probabilities for a text: each character for `w` columns, a blank column between characters
cv::Mat fakeLogits(const std::u32string& text, const ocr::Charset& cs, int w = 3, float confident = 0.95f)
{
    std::vector<int> cols;
    cols.push_back(0);
    for (char32_t c : text) {
        for (int k = 0; k < w; ++k) cols.push_back(cs.find(c));
        cols.push_back(0);
    }
    const int C = int(cs.chars.size());
    cv::Mat lp(int(cols.size()), C, CV_32F);
    const float rest = std::log((1.f - confident) / float(C - 1));
    for (int t = 0; t < lp.rows; ++t) {
        for (int c = 0; c < C; ++c) lp.at<float>(t, c) = rest;
        lp.at<float>(t, cols[size_t(t)]) = std::log(confident);
    }
    return lp;
}

ocr::Charset testCharset()
{
    ocr::Charset cs;
    cs.chars.assign(1, 0);
    for (char32_t c : std::u32string(U"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.,:/#₹¥")) {
        cs.index[c] = int(cs.chars.size());
        cs.chars.push_back(c);
    }
    return cs;
}

void testCheck()
{
    const ocr::Charset cs = testCharset();
    std::string err;
    ocr::Pattern pat;
    ocr::expandPattern("\xE2\x82\xB9 46.00, HWN937", 0, pat, err);

    cv::Mat good = fakeLogits(U"₹46.00,HWN937", cs);
    auto read = ocr::greedyRead(good, cs);
    auto r = ocr::checkLine(good, cs, pat, read, 60);
    check(r.pass && r.quality > 90, "the printed text passes");

    cv::Mat other = fakeLogits(U"₹46.00,HWN881", cs);
    r = ocr::checkLine(other, cs, pat, ocr::greedyRead(other, cs), 60);
    int wrong = 0;
    for (const auto& c : r.chars) wrong += c.status == VS_CHAR_WRONG;
    check(!r.pass && wrong == 3, "another batch fails with 3 wrong characters");

    cv::Mat missing = fakeLogits(U"₹46.00,HWN93", cs);
    r = ocr::checkLine(missing, cs, pat, ocr::greedyRead(missing, cs), 60);
    check(!r.pass && r.chars.back().status == VS_CHAR_MISSING, "a missing last character fails as missing");

    ocr::Pattern wild;
    ocr::expandPattern("HWN9??", 0, wild, err);
    r = ocr::checkLine(good, cs, wild, ocr::greedyRead(good, cs), 60);
    check(!r.pass, "wildcards still need the fixed text (line is longer)");
    cv::Mat batch = fakeLogits(U"HWN937", cs);
    r = ocr::checkLine(batch, cs, wild, ocr::greedyRead(batch, cs), 60);
    check(r.pass, "wildcards accept any printed character");

    ocr::Pattern times;
    ocr::expandPattern("{TIME:HH:mm+-3}", at(2026, 10, 23, 15, 41), times, err);
    cv::Mat t1 = fakeLogits(U"15:39", cs);
    r = ocr::checkLine(t1, cs, times, ocr::greedyRead(t1, cs), 60);
    check(r.pass && r.expected == U"15:39", "a time 2 minutes earlier passes and is reported");
    cv::Mat t2 = fakeLogits(U"15:30", cs);
    r = ocr::checkLine(t2, cs, times, ocr::greedyRead(t2, cs), 60);
    check(!r.pass, "a time 11 minutes earlier fails");

    // repeated characters need a blank between them: "88" must not pass as "8"
    ocr::Pattern eight;
    ocr::expandPattern("881", 0, eight, err);
    cv::Mat one8 = fakeLogits(U"81", cs);
    r = ocr::checkLine(one8, cs, eight, ocr::greedyRead(one8, cs), 60);
    check(!r.pass, "a double letter printed once fails");
}

// columns of the k-th character (0-based) of a fakeLogits text with width w: rows 1 + k * (w + 1) ...
void setChar(cv::Mat& lp, const ocr::Charset& cs, size_t k, int w, const std::vector<std::pair<char32_t, float>>& probs)
{
    const int C = lp.cols;
    float used = 0;
    for (const auto& [c, p] : probs) used += p;
    const float rest = std::log((1.f - used) / float(C - int(probs.size())));
    for (int t = 1 + int(k) * (w + 1); t < 1 + int(k) * (w + 1) + w; ++t) {
        for (int c = 0; c < C; ++c) lp.at<float>(t, c) = rest;
        for (const auto& [c, p] : probs) lp.at<float>(t, cs.find(c)) = std::log(p);
    }
}

// the verification margin (tool spec 7.3.3): reported per character and per line, never changes pass
void testMargin()
{
    const ocr::Charset cs = testCharset();
    std::string err;
    ocr::Pattern pat;
    ocr::expandPattern("LOT 8A3", 0, pat, err);

    cv::Mat good = fakeLogits(U"LOT8A3", cs);
    auto r = ocr::checkLine(good, cs, pat, ocr::greedyRead(good, cs), 60);
    float lowest = 99;
    for (const auto& c : r.chars) lowest = std::min(lowest, c.margin);
    check(r.pass && r.margin > 5 && std::fabs(r.margin - lowest) < 1e-4f, "clear print: margins of several nats, line = lowest");

    // the 8 at 0.55 with a B at 0.40: fails the score; the margin says a look-alike is close
    cv::Mat close = good.clone();
    setChar(close, cs, 3, 3, {{U'8', 0.55f}, {U'B', 0.40f}});
    r = ocr::checkLine(close, cs, pat, ocr::greedyRead(close, cs), 60);
    check(!r.pass && std::fabs(r.chars[3].margin - std::log(0.55f / 0.40f)) < 0.01f && r.margin == r.chars[3].margin,
          "a look-alike close behind: margin ln(0.55 / 0.40)");

    // a thin 8 at 0.55 with every rival far behind: still fails (unchanged rule), margin large
    cv::Mat thin = good.clone();
    setChar(thin, cs, 3, 3, {{U'8', 0.55f}});
    r = ocr::checkLine(thin, cs, pat, ocr::greedyRead(thin, cs), 60);
    check(!r.pass && r.chars[3].margin > 2, "a faint 8 with no rival: large margin, still fails on its score");

    // a B printed where 8 is expected: the rival wins, margin below 0
    cv::Mat wrong = fakeLogits(U"LOTBA3", cs);
    r = ocr::checkLine(wrong, cs, pat, ocr::greedyRead(wrong, cs), 60);
    check(!r.pass && r.chars[3].margin < -2, "a misprint: margin below 0");

    // a look-alike printed one column to the side still counts as the rival
    cv::Mat side = good.clone();
    setChar(side, cs, 3, 3, {{U'8', 0.7f}});
    side.at<float>(1 + 3 * 4 + 3, cs.find(U'B')) = std::log(0.6f);   // the blank column after the 8
    r = ocr::checkLine(side, cs, pat, ocr::greedyRead(side, cs), 60);
    check(r.chars[3].margin < 0.2f, "a rival one column beside the character counts");

    // a neighbour aligned right next to it (no blank column between) is not its rival
    cv::Mat touch = good.clone();
    for (int c = 0; c < touch.cols; ++c) touch.at<float>(1 + 3 * 4 + 3, c) = touch.at<float>(1 + 4 * 4, c);   // A from the blank column on
    r = ocr::checkLine(touch, cs, pat, ocr::greedyRead(touch, cs), 60);
    check(r.pass && r.chars[3].t1 + 1 == r.chars[4].t0 && r.chars[3].margin > 5 && r.chars[4].margin > 5,
          "touching characters: each neighbour is not the other's rival");

    // the space read in the gap at a word break is no rival (spaces are never checked)
    ocr::Charset spaced = cs;
    spaced.index[U' '] = int(spaced.chars.size());
    spaced.chars.push_back(U' ');
    cv::Mat gap(good.rows, good.cols + 1, CV_32F, cv::Scalar(std::log(0.05f / float(good.cols))));
    good.copyTo(gap(cv::Rect(0, 0, good.cols, good.rows)));
    gap.at<float>(1 + 2 * 4 + 3, good.cols) = std::log(0.9f);   // a space after "LOT"
    r = ocr::checkLine(gap, spaced, pat, ocr::greedyRead(gap, spaced), 60);
    check(r.pass && r.chars[2].margin > 5, "a space at the word break is not the rival of the letter before it");

    // wildcards are never the lowest; a character the charset lacks is the lowest
    ocr::Pattern wild;
    ocr::expandPattern("LOT ?A3", 0, wild, err);
    r = ocr::checkLine(good, cs, wild, ocr::greedyRead(good, cs), 60);
    check(r.pass && r.chars[3].margin == ocr::kMaxMargin && r.margin < ocr::kMaxMargin, "a wildcard's margin is not judged");
    ocr::Pattern odd;
    ocr::expandPattern("LOT 8A\xC3\x9F", 0, odd, err);   // sharp s: not in the charset
    r = ocr::checkLine(good, cs, odd, ocr::greedyRead(good, cs), 60);
    check(!r.pass && r.margin == -ocr::kMaxMargin, "a character outside the charset: margin -99");
}

ocr::FormatRead fmtRead(const std::string& format, const cv::Mat& lp, const ocr::Charset& cs, float minQ = 60)
{
    ocr::Format f;
    std::string err;
    if (!ocr::compileFormat(format, f, err)) { std::printf("     format %s: %s\n", format.c_str(), err.c_str()); return {}; }
    return ocr::readFormat(lp, cs, f, ocr::greedyRead(lp, cs), minQ);
}

int countStatus(const ocr::FormatRead& r, int status)
{
    int n = 0;
    for (const auto& c : r.chars) n += c.status == status;
    return n;
}

// reading under a format (format.cpp): the format picks among allowed characters, never vouches for one
void testFormat()
{
    ocr::Format f;
    std::string err;
    for (const char* bad : {"", "  ", "{D:0}", "{D:3-2}", "{D:65}", "{Q:2}", "{D:5", "{|JAN||FEB}", "{D:x}"})
        check(!ocr::compileFormat(bad, f, err), std::string("format \"") + bad + "\" is refused: " + err);
    check(ocr::compileFormat("LOT {A:1}{D:4-6} \\{\\|{|JAN|FEB}{X}{?:2}", f, err), "a format with every field compiles");

    const ocr::Charset cs = testCharset();
    cv::Mat lot = fakeLogits(U"LOTA12345", cs);
    auto r = fmtRead("LOT {A:1}{D:5}", lot, cs);
    check(r.pass && r.text == U"LOTA12345" && r.quality > 90, "a lot fitting its format passes");

    cv::Mat oForZero = fakeLogits(U"LOTA1O345", cs);
    r = fmtRead("LOT {A:1}{D:5}", oForZero, cs);
    check(!r.pass && r.text == U"LOTA10345" && countStatus(r, VS_CHAR_WRONG) == 1 && r.chars[5].found == U'O' && r.chars[5].score < 10,
          "an O printed where a digit belongs reads as 0 with the 0's low score: NOT OK");

    // the recogniser unsure between O and 0: the allowed 0 is chosen, but with its own 40, not 100
    cv::Mat unsure = fakeLogits(U"LOTA10345", cs);
    setChar(unsure, cs, 5, 3, {{U'O', 0.55f}, {U'0', 0.40f}});
    r = fmtRead("LOT {A:1}{D:5}", unsure, cs);
    check(!r.pass && r.text == U"LOTA10345" && std::abs(r.chars[5].score - 40.f) < 1, "a weak allowed character is never forced to a pass");
    check(!r.freeFits, "... and the free read (an O) does not fit the format either");
    r = fmtRead("LOT {A:1}{D:5}", unsure, cs, 35);
    check(!r.pass, "... nor with a minimum quality of 35: under a format a character needs 50 or more");
    // a clear 0 the free read would take as O by a hair still needs its own score
    setChar(unsure, cs, 5, 3, {{U'0', 0.70f}, {U'O', 0.25f}});
    check(fmtRead("LOT {A:1}{D:5}", unsure, cs).pass, "a clear allowed character passes");

    cv::Mat extra = fakeLogits(U"LOTA123456", cs);
    r = fmtRead("LOT {A:1}{D:5}", extra, cs);
    check(!r.pass && countStatus(r, VS_CHAR_EXTRA) == 1, "a sixth digit has no room in {D:5}: NOT OK as extra");
    cv::Mat shortLot = fakeLogits(U"LOTA1234", cs);
    check(!fmtRead("LOT {A:1}{D:5}", shortLot, cs).pass, "a missing digit: NOT OK");
    check(fmtRead("LOT {A:1}{D:4-6}", shortLot, cs).pass && fmtRead("LOT {A:1}{D}", extra, cs).pass, "count ranges and one-or-more");

    // one wide 0 must not read as 00; two zeros with a gap do
    cv::Mat wide = fakeLogits(U"10", cs, 6);
    r = fmtRead("{D:3}", wide, cs);
    check(!r.pass, "one wide 0 is not read as 00 (" + ocr::toUtf8(r.text) + ", " + std::to_string(int(r.quality)) + ")");
    cv::Mat two = fakeLogits(U"100", cs);
    check(fmtRead("{D:3}", two, cs).pass, "two zeros with a gap read as 00");

    cv::Mat mar = fakeLogits(U"MAR26", cs), may = fakeLogits(U"MAY26", cs);
    check(fmtRead("{|JAN|FEB|MAR}{D:2}", mar, cs).pass, "a month name of the list passes");
    r = fmtRead("{|JAN|FEB|MAR}{D:2}", may, cs);
    check(!r.pass && r.chars[2].status == VS_CHAR_WRONG, "a month name not in the list: NOT OK at the wrong letter");
    cv::Mat day = fakeLogits(U"32/10", cs);
    check(!fmtRead("{|01|02|03|10|20|30|31}/{|01|10|11|12}", day, cs).pass, "day 32 has no place in a day list");

    cv::Mat faint = fakeLogits(U"LOTA12345", cs, 3, 0.5f);
    r = fmtRead("LOT {A:1}{D:5}", faint, cs);
    check(!r.pass && r.freeFits, "faint print under a format: NOT OK, yet still its line (the free read fits), so never skipped");
    check(!fmtRead("{D:5}", cv::Mat(), cs).fits, "no columns: nothing fits");

    // letters in either case, and the word gap kept in the text
    ocr::Charset ws = testCharset();
    for (char32_t c : std::u32string(U" abot")) { ws.index[c] = int(ws.chars.size()); ws.chars.push_back(c); }
    cv::Mat spaced = fakeLogits(U"Lot A12345", ws);
    r = fmtRead("LOT {A:1}{D:5}", spaced, ws);
    check(r.pass && r.text == U"Lot A12345", "lower-case print fits an upper-case format, the gap stays: " + ocr::toUtf8(r.text));
    // one wide O must not read as Oo (the same letter in the other case) either
    r = fmtRead("{A:2}", fakeLogits(U"O", ws, 6), ws);
    check(!r.pass, "one wide O is not read as Oo (" + ocr::toUtf8(r.text) + ", " + std::to_string(int(r.quality)) + ")");
    check(fmtRead("{A:2}", fakeLogits(U"Oo", ws), ws).pass, "O and o with a gap read as two letters");
    // a character the reader does not know fails, never passes
    check(!fmtRead("LOT-{D:5}", lot, cs).pass, "a format character missing from the charset: NOT OK");
}

// A text line drawn with OpenCV's stroke font, as a camera would see it.
cv::Mat drawText(const std::vector<std::string>& lines, double angleDeg, double slantDeg, bool lightOnDark, bool dots,
                 int capPx = 30)
{
    const double scale = capPx / 22.0;     // FONT_HERSHEY_SIMPLEX capital ~22 px at scale 1
    cv::Mat img(420, 900, CV_8U, cv::Scalar(lightOnDark ? 60 : 200));
    cv::Mat ink(img.size(), CV_8U, cv::Scalar(0));
    for (size_t i = 0; i < lines.size(); ++i)
        cv::putText(ink, lines[i], {120, 150 + int(i) * int(capPx * 1.9)}, cv::FONT_HERSHEY_SIMPLEX, scale, 255,
                    std::max(1, capPx / 10), cv::LINE_AA);
    if (dots) {   // keep ink only on a dot grid, drop 15% of the dots
        cv::Mat mask(ink.size(), CV_8U, cv::Scalar(0));
        std::mt19937 rng(3);
        const int p = std::max(3, capPx / 6);
        for (int y = 0; y < ink.rows; y += p)
            for (int x = 0; x < ink.cols; x += p)
                if (ink.at<uint8_t>(y, x) > 100 && rng() % 100 >= 15) cv::circle(mask, {x, y}, p / 2, 255, -1, cv::LINE_AA);
        ink = mask;
    }
    const double t = std::tan(slantDeg * CV_PI / 180.0);
    cv::Mat sh = (cv::Mat_<double>(2, 3) << 1, -t, t * 200, 0, 1, 0);
    cv::warpAffine(ink, ink, sh, ink.size());
    // rotation about the centre, counter-clockwise for positive angles (as cv::getRotationMatrix2D)
    const double a = angleDeg * CV_PI / 180.0, ca = std::cos(a), sa = std::sin(a);
    cv::Mat R = (cv::Mat_<double>(2, 3) << ca, sa, (1 - ca) * 450 - sa * 210, -sa, ca, sa * 450 + (1 - ca) * 210);
    cv::warpAffine(ink, ink, R, ink.size());
    cv::Mat f;
    img.convertTo(f, CV_32F);
    cv::Mat i32;
    ink.convertTo(i32, CV_32F, (lightOnDark ? 40.0 : -120.0) / 255.0);
    f += i32;
    cv::Mat noise(f.size(), CV_32F);
    cv::randn(noise, 0, 2.0);
    f += noise;
    f.convertTo(img, CV_8U);
    return img;
}

void testLines()
{
    struct Case { const char* name; double angle, slant; bool light, dots; };
    const Case cases[] = {
        {"straight dark text", 0, 0, false, false},
        {"rotated 8 degrees", 8, 0, false, false},
        {"slanted 12 degrees", 0, 12, false, false},
        {"light on dark (laser on a patch)", -4, 0, true, false},
        {"dot matrix with missing dots", 3, 6, false, true},
    };
    for (const auto& c : cases) {
        cv::Mat img = drawText({"BATCH HWN937", "EXP 09/28 16:59", "MRP 46.00"}, c.angle, c.slant, c.light, c.dots);
        ocr::FindParams fp;
        fp.roi = cv::Rect(0, 0, img.cols, img.rows);
        fp.charHeight = 30;
        auto lines = ocr::findLines(img, fp, c.dots);
        if (const char* dbg = std::getenv("OCR_TEST_DUMP")) cv::imwrite(std::string(dbg) + "/" + c.name + ".png", img);
        bool ok = lines.size() == 3;
        for (const auto& l : lines) {
            ok = ok && std::abs(l.angleDeg - (-c.angle)) < 2.0;   // image y down: a counter-clockwise turn reads negative
            ok = ok && l.lightOnDark == c.light;
        }
        std::string got = std::to_string(lines.size()) + " lines:";
        for (const auto& l : lines) {
            char b[96];
            std::snprintf(b, sizeof b, " [%dpx a%.1f s%.1f %s]", l.image.cols, l.angleDeg, l.slantDeg, l.lightOnDark ? "light" : "dark");
            got += b;
        }
        check(ok, std::string("line finder: ") + c.name + " (" + got + ")");
    }
    // lines of mostly diagonal letters: V W X and the arms of Y lean both ways in pairs, / and Z one
    // way. Their strokes are straightened as easily as a slant: the estimate must follow the print's
    // slant (none, or a real one) at the default search range, not the diagonals
    struct Slant { const char* text; double angle, slant; bool alone; };
    const Slant diag[] = {
        {"VWXYZ/ VWXYZ/", 0, 0, false}, {"VWXYZ/ VWXYZ/", 4, 0, false}, {"VWXYZ/ VWXYZ/", 0, 10, false},
        {"VWXYZ/ VWXYZ/", 0, -8, false}, {"W/V/X/Y/Z/", 0, 0, false}, {"VAVAV WAXY", 0, 0, false},
        {"VAVAV WAXY", -3, 12, false}, {"//// 77 ZZ", 0, 0, false},
        // alone in the region, no other line to follow: upright diagonals stay upright, and the arms
        // of V A W either side of a real slant find it
        {"VWXYZ/ VWXYZ/", 0, 0, true}, {"//// 77 ZZ", 3, 0, true}, {"VAVAV WAXY", 0, 0, true},
        {"VAVAV WAXY", 0, 12, true}, {"VAVAV WAXY", 0, -10, true}, {"U V W X Y Z / . - :", 0, 0, true},
    };
    for (const auto& d : diag) {
        std::vector<std::string> text{d.text};
        if (!d.alone) text.push_back("BATCH HWN937");
        cv::Mat img = drawText(text, d.angle, d.slant, false, false);
        ocr::FindParams fp;
        fp.roi = cv::Rect(0, 0, img.cols, img.rows);
        fp.charHeight = 30;
        const auto lines = ocr::findLines(img, fp, false);
        const bool ok = lines.size() == text.size() && std::abs(lines[0].slantDeg - d.slant) < 3.0;
        char b[160];
        std::snprintf(b, sizeof b, "slant of \"%s\"%s drawn at %.0f (turned %.0f): %.1f", d.text, d.alone ? " alone" : "",
                      d.slant, d.angle, lines.empty() ? 0.0 : double(lines[0].slantDeg));
        check(ok, b);
    }
    // auto letter height
    cv::Mat img = drawText({"LOT A12345"}, 0, 0, false, false, 40);
    ocr::FindParams fp;
    fp.roi = cv::Rect(0, 0, img.cols, img.rows);
    const float h = ocr::estimateCharHeight(img, fp);
    check(h > 28 && h < 60, "letter height estimate " + std::to_string(h) + " for 40 px capitals");
}

void testModel()
{
    const std::string dir = OCR_MODEL_DIR;
    const std::string onnx = dir + "/ocrnet.onnx", cs = dir + "/ocr_charset.txt";
    if (!std::ifstream(onnx)) {
        std::printf("skip  model tests: %s not found (train it with ml/ocr/train_ocr.py)\n", onnx.c_str());
        return;
    }
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.char_height_px = 30;
    p.time_budget_ms = 500;
    vs_ocr* t = vs_ocr_create(&p);
    check(vs_ocr_load_model(t, onnx.c_str(), cs.c_str()) == 0, "model loads");
    cv::Mat img = drawText({"BATCH HWN937", "EXP 09/28"}, 4, 5, false, false);
    vs_ocr_set_expected(t, 0, "BATCH HWN937");
    vs_ocr_set_expected(t, 1, "EXP 09/28");
    vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_stats st;
    vs_ocr_get_stats(t, &st);
    std::string read;
    for (int i = 0; i < vs_ocr_line_count(t); ++i) { vs_ocr_line l; vs_ocr_get_line(t, i, &l); read += std::string(" | ") + l.text; }
    check(st.pass == 1, "drawn lines check OK end to end (" + read + ", " + std::to_string(st.total_ms) + " ms)");
    {
        // the margin through the C API, and callers built before it was added (shorter structs)
        vs_ocr_line_ext ext{};
        ext.struct_size = int32_t(sizeof ext);
        vs_ocr_char_quality q{};
        q.struct_size = int32_t(sizeof q);
        vs_ocr_line l0;
        vs_ocr_get_line(t, 0, &l0);
        const bool got = vs_ocr_get_line_ext(t, 0, &ext) == 0 && vs_ocr_get_char_quality(t, l0.first_char, &q) == 0;
        check(got && ext.margin > 1 && q.margin >= ext.margin, "line and character margins of a good print (line " +
              std::to_string(ext.margin) + " nats)");
        vs_ocr_line_ext oldExt{};
        oldExt.struct_size = int32_t(offsetof(vs_ocr_line_ext, margin));
        oldExt.margin = 12345;
        vs_ocr_char_quality oldQ{};
        oldQ.struct_size = int32_t(offsetof(vs_ocr_char_quality, margin));
        oldQ.margin = 12345;
        check(vs_ocr_get_line_ext(t, 0, &oldExt) == 0 && oldExt.margin == 12345 && oldExt.shift_px == ext.shift_px &&
              vs_ocr_get_char_quality(t, l0.first_char, &oldQ) == 0 && oldQ.margin == 12345 && oldQ.recognition == q.recognition,
              "structs from before margin: filled up to it, nothing past it");
    }
    vs_ocr_set_expected(t, 0, "BATCH HWN938");
    vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_get_stats(t, &st);
    check(st.pass == 0, "one changed character is NOT OK");
    vs_ocr_destroy(t);
}

// A print defect on one character of drawDots: more dots missing, moved along the line, lighter ink.
struct DotDefect {
    double drop = 0;          // extra share of its dots missing
    int dx = 0;               // px along the line
    double ink = 1;           // 1 = full ink
};
using DotDefects = std::map<std::pair<int, int>, DotDefect>;   // (line, character index) -> defect

// CIJ print: every character from a 5 x 7 dot matrix (as the printer does), a dot pitch of `pitch`
// px, `drop` of the dots missing at random (or exactly `dropEach` dots of every character), the block
// rotated by angleDeg; `defects` changes single characters; characters `advance` pitches apart (6: one empty dot
// column between two characters, less: spaced tight)
cv::Mat drawDots(const std::vector<std::string>& lines, double angleDeg, int pitch, double drop, unsigned seed, int dropEach = 0,
                 const DotDefects& defects = {}, double advance = 6.0)
{
    static const std::map<char, std::array<const char*, 7>> font = {
        {'0', {"01110", "10001", "10011", "10101", "11001", "10001", "01110"}},
        {'1', {"00100", "01100", "00100", "00100", "00100", "00100", "01110"}},
        {'2', {"01110", "10001", "00001", "00010", "00100", "01000", "11111"}},
        {'3', {"11111", "00010", "00100", "00010", "00001", "10001", "01110"}},
        {'4', {"00010", "00110", "01010", "10010", "11111", "00010", "00010"}},
        {'5', {"11111", "10000", "11110", "00001", "00001", "10001", "01110"}},
        {'6', {"00110", "01000", "10000", "11110", "10001", "10001", "01110"}},
        {'7', {"11111", "00001", "00010", "00100", "01000", "01000", "01000"}},
        {'8', {"01110", "10001", "10001", "01110", "10001", "10001", "01110"}},
        {'9', {"01110", "10001", "10001", "01111", "00001", "00010", "01100"}},
        {'A', {"01110", "10001", "10001", "10001", "11111", "10001", "10001"}},
        {'B', {"11110", "10001", "10001", "11110", "10001", "10001", "11110"}},
        {'E', {"11111", "10000", "10000", "11110", "10000", "10000", "11111"}},
        {'L', {"10000", "10000", "10000", "10000", "10000", "10000", "11111"}},
        {'O', {"01110", "10001", "10001", "10001", "10001", "10001", "01110"}},
        {'P', {"11110", "10001", "10001", "11110", "10000", "10000", "10000"}},
        {'T', {"11111", "00100", "00100", "00100", "00100", "00100", "00100"}},
        {'X', {"10001", "10001", "01010", "00100", "01010", "10001", "10001"}},
        {'I', {"01110", "00100", "00100", "00100", "00100", "00100", "01110"}},
        {'l', {"01100", "00100", "00100", "00100", "00100", "00100", "01110"}},
        {'/', {"00000", "00001", "00010", "00100", "01000", "10000", "00000"}},
    };
    cv::Mat ink(420, 900, CV_8U, cv::Scalar(0));
    std::mt19937 rng(seed);
    std::uniform_real_distribution<double> u(0, 1);
    for (size_t i = 0; i < lines.size(); ++i) {
        int x = 120;
        const int y0 = 120 + int(i) * 11 * pitch;
        for (size_t ci = 0; ci < lines[i].size(); ++ci) {
            const auto it = font.find(lines[i][ci]);
            const auto d = defects.find({int(i), int(ci)});
            const DotDefect def = d == defects.end() ? DotDefect{} : d->second;
            if (it != font.end()) {
                std::vector<cv::Point> dots;
                for (int r = 0; r < 7; ++r)
                    for (int k = 0; k < 5; ++k)
                        if (it->second[size_t(r)][k] == '1' && u(rng) >= drop + def.drop)
                            dots.emplace_back(x + def.dx + k * pitch, y0 + r * pitch);
                if (dropEach > 0) {
                    std::shuffle(dots.begin(), dots.end(), rng);
                    dots.resize(dots.size() - std::min(dots.size(), size_t(dropEach)));
                }
                for (const auto& p : dots) cv::circle(ink, p, std::max(1, pitch * 2 / 5), int(255 * def.ink), -1, cv::LINE_AA);
            }
            x += int(std::lround(advance * pitch));
        }
    }
    const double a = angleDeg * CV_PI / 180.0, ca = std::cos(a), sa = std::sin(a);
    cv::Mat R = (cv::Mat_<double>(2, 3) << ca, sa, (1 - ca) * 450 - sa * 210, -sa, ca, sa * 450 + (1 - ca) * 210);
    cv::warpAffine(ink, ink, R, ink.size());
    cv::Mat f(ink.size(), CV_32F, cv::Scalar(200));
    cv::Mat i32;
    ink.convertTo(i32, CV_32F, -120.0 / 255.0);
    f += i32;
    cv::Mat noise(f.size(), CV_32F);
    cv::theRNG().state = seed;   // the same noise for a seed, whatever was drawn before
    cv::randn(noise, 0, 2.0);
    f += noise;
    cv::Mat img;
    f.convertTo(img, CV_8U);
    return img;
}

// fixed-pitch cells (font.cpp) on a made-up ink profile: characters 17 columns wide at a pitch of
// 21.3, dot columns missing inside some of them, one empty cell for a word gap
void testPitch()
{
    const float pitch = 21.3f;
    std::vector<int> prof(260, 0);
    std::mt19937 rng(7);
    std::vector<cv::Range> chars;
    for (int c = 0; c < 10; ++c) {
        if (c == 3) continue;
        const int s = 12 + int(std::lround(float(c) * pitch));
        chars.emplace_back(s, s + 17);
        for (int x = s; x < s + 17; ++x) prof[size_t(x)] = 6 + int(rng() % 8);
        if (c % 2 == 0) {
            const int h = s + 4 + int(rng() % 8);
            for (int x = h; x < h + 4; ++x) prof[size_t(x)] = 0;
        }
    }
    float found = 0;
    const std::vector<cv::Range> cells = ocr::pitchCells(prof, 18.f, &found);
    int inOne = 0;
    for (const auto& ch : chars)
        for (const auto& c : cells) if (c.start <= ch.start && c.end >= ch.end) ++inOne;
    check(cells.size() == 10 && std::abs(found - pitch) < 0.02f * pitch && inOne == int(chars.size()),
          "pitch " + std::to_string(found) + " for 21.3 (within 2%): " + std::to_string(cells.size()) + " cells, " +
          std::to_string(inOne) + " of 9 characters inside one cell, gaps in characters ignored");
    std::vector<int> touching(260, 0);
    for (int x = 12; x < 240; ++x) touching[size_t(x)] = 8;
    check(ocr::pitchCells(touching, 18.f).empty(), "no gaps between characters: no pitch, cut by strokes");
}

// classical OCR from a taught font (font.cpp)
std::string readAll(vs_ocr* t)
{
    std::string read;
    for (int i = 0; i < vs_ocr_line_count(t); ++i) {
        vs_ocr_line l;
        vs_ocr_get_line(t, i, &l);
        read += std::string(i ? " | " : "") + l.text;
    }
    return read;
}

std::string noSpaces(std::string s)
{
    s.erase(std::remove(s.begin(), s.end(), ' '), s.end());
    return s;
}

// each checked line's quality and its characters below OK
std::string checkDetail(vs_ocr* t)
{
    std::string qs;
    for (int i = 0; i < vs_ocr_line_count(t); ++i) {
        vs_ocr_line l;
        vs_ocr_get_line(t, i, &l);
        qs += std::string(i ? ", " : "") + l.expected + " " + std::to_string(int(l.quality));
        for (int c = l.first_char; c < l.first_char + l.char_count; ++c) {
            vs_ocr_char ch;
            vs_ocr_get_char(t, c, &ch);
            if (ch.status != VS_CHAR_OK) qs += " [" + std::string(1, char(ch.codepoint)) + "=" + std::to_string(int(ch.score)) + "]";
        }
    }
    return qs;
}

// teaches lines of drawn print; false when a line does not teach
bool teach(vs_ocr* t, const std::vector<std::string>& lines, bool dots)
{
    const cv::Mat img = drawText(lines, 0, 0, false, dots);
    bool ok = true;
    for (size_t i = 0; i < lines.size(); ++i) {
        const int n = vs_ocr_teach_font(t, img.data, img.cols, img.rows, int32_t(img.step), int32_t(i), lines[i].c_str());
        if (n != int(noSpaces(lines[i]).size())) {
            std::printf("     teach \"%s\": %d (%s)\n", lines[i].c_str(), n, vs_ocr_last_error(t));
            ok = false;
        }
    }
    return ok;
}

void testFont()
{
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.char_height_px = 30;
    p.time_budget_ms = 500;
    p.reader = VS_READER_FONT;
    p.print = VS_PRINT_SOLID;
    vs_ocr* t = vs_ocr_create(&p);
    const cv::Mat probe = drawText({"LOT A12345"}, 0, 0, false, false);
    check(vs_ocr_run(t, probe.data, probe.cols, probe.rows, int32_t(probe.step)) < 0, "font reader without a font refuses to run");
    for (const char* bad : {"LOT A1234", "LOT A123456", "LOTT A1234"}) {
        const int wrong = vs_ocr_teach_font(t, probe.data, probe.cols, probe.rows, int32_t(probe.step), 0, bad);
        check(wrong < 0 && vs_ocr_font_glyphs(t) == 0,
              std::string("teaching \"") + bad + "\" on a LOT A12345 print is refused: " + vs_ocr_last_error(t));
    }
    // taught from spaced print (the drawing font kerns letters into each other, which no column cut
    // separates); read below from the font's normal, tighter print
    check(teach(t, {"0 1 2 3 4 5 6 7 8 9", "A B C D E F G H I J", "K L M N O P Q R S T"}, false) &&
          teach(t, {"U V W X Y Z / . - :"}, false), "font taught from 4 lines of print");
    check(vs_ocr_font_glyphs(t) == 40, "40 characters taught (" + std::to_string(vs_ocr_font_glyphs(t)) + ")");

    // read other text, rotated and slanted (the line finder straightens it). The drawing font's O and
    // 0 are nearly the same shape: the reader rightly scores either one low (tested below), so the
    // text read here has none
    const cv::Mat img = drawText({"LST A12345", "EXP 19/28"}, 4, 5, false, false);
    vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_stats st;
    vs_ocr_get_stats(t, &st);
    const std::string read = readAll(t);
    check(noSpaces(read) == "LSTA12345|EXP19/28", "free read with the taught font: " + read + " (" + std::to_string(st.total_ms) + " ms)");
    // the drawing font is proportional: its word gap adds only about half a character to the pitch
    check(read == "LST A12345 | EXP 19/28", "the word gaps read as spaces, and nothing else does: " + read);
    vs_ocr_set_expected(t, 0, "LST A12345");
    vs_ocr_set_expected(t, 1, "EXP 19/28");
    vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_get_stats(t, &st);
    check(st.pass == 1, "check OK with the taught font (" + checkDetail(t) + ")");
    vs_ocr_set_expected(t, 0, "LST A12346");
    vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_get_stats(t, &st);
    check(st.pass == 0, "one changed character is NOT OK with the taught font");
    // the same print read under formats (Read text mode), with the taught font's probabilities
    vs_ocr_clear_expected(t);
    check(vs_ocr_set_format(t, 0, "LST {A:1}{D:5}") == 0 && vs_ocr_set_format(t, 1, "EXP {D:2}/{D:2}") == 0 &&
          vs_ocr_set_format(t, 2, "{D:5") < 0, "formats set; a broken one is refused");
    vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_get_stats(t, &st);
    vs_ocr_line fl0{}, fl1{};
    vs_ocr_get_line(t, 0, &fl0);
    vs_ocr_get_line(t, 1, &fl1);
    check(st.pass == 1 && fl0.format == 0 && fl1.format == 1 && fl0.checked == 1 && noSpaces(fl1.text) == "EXP19/28",
          "taught font under formats: OK (" + checkDetail(t) + ")");
    vs_ocr_set_format(t, 0, "LST {D:6}");
    vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_get_stats(t, &st);
    check(st.pass == 0, "a letter where the format wants a digit is NOT OK with the taught font (" + checkDetail(t) + ")");
    vs_ocr_clear_formats(t);
    // O and 0 swapped, on five noise patterns: never OK (the two are too alike in this font to tell)
    int swapsOk = 0;
    for (int k = 0; k < 5; ++k) {
        cv::randn(cv::Mat(1, 1, CV_32F), 0, 1); // move OpenCV's noise generator on
        const cv::Mat a = drawText({"LOT B0X 10"}, 2, 3, false, false), b = drawText({"LOT BOX 1O"}, 2, 3, false, false);
        vs_ocr_clear_expected(t);
        vs_ocr_set_expected(t, 0, "LOT BOX 10");
        vs_ocr_run(t, a.data, a.cols, a.rows, int32_t(a.step));
        vs_ocr_get_stats(t, &st);
        swapsOk += st.pass;
        vs_ocr_run(t, b.data, b.cols, b.rows, int32_t(b.step));
        vs_ocr_get_stats(t, &st);
        swapsOk += st.pass;
    }
    check(swapsOk == 0, "O printed for 0 and 0 for O are never OK (10 tries, " + std::to_string(swapsOk) + " OK)");
    // a character never taught that looks like two taught ones side by side (m and r n): never OK
    {
        vs_ocr* tm = vs_ocr_create(&p);
        teach(tm, {"r n L O T A", "1 2 3 4 5"}, false);
        int ok = 0;
        for (int k = 0; k < 5; ++k) {
            const cv::Mat m = drawText({"LOT mA"}, 0, 0, false, false);
            vs_ocr_set_expected(tm, 0, "LOT rnA");
            vs_ocr_run(tm, m.data, m.cols, m.rows, int32_t(m.step));
            vs_ocr_get_stats(tm, &st);
            ok += st.pass;
        }
        check(ok == 0, "an m (never taught) is not taken for r n (5 tries, " + std::to_string(ok) + " OK)");
        vs_ocr_destroy(tm);
    }
    // texture, not text (a barcode in the region): the cycle time stays within the budget, nothing passes
    {
        cv::Mat bars(420, 900, CV_8U, cv::Scalar(200));
        for (int x = 100; x < 800; x += 7) cv::rectangle(bars, cv::Rect(x, 150, 3, 40), cv::Scalar(60), -1);
        vs_ocr_set_expected(t, 0, "LOT A12345");
        const auto t0 = std::chrono::steady_clock::now();
        vs_ocr_run(t, bars.data, bars.cols, bars.rows, int32_t(bars.step));
        const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
        vs_ocr_get_stats(t, &st);
        check(st.pass == 0 && VS_TIME_BUDGET(ms < 1000), "bar texture: not OK, done in " + std::to_string(int(ms)) + " ms (budget 500)");
        vs_ocr_clear_expected(t);
    }
    // narrow characters close together: every one is read (none dropped at the line's end), and
    // digits at a fixed pitch get no spaces between them
    vs_ocr_clear_expected(t);
    const cv::Mat ones = drawText({"1111111111111", "11:11:11"}, 0, 0, false, false);
    vs_ocr_run(t, ones.data, ones.cols, ones.rows, int32_t(ones.step));
    const std::string onesRead = readAll(t);
    check(onesRead == "1111111111111 | 11:11:11", "thirteen narrow 1s and a time read in full, no spaces added: " + onesRead);
    vs_ocr_clear_expected(t);

    // save and load: the same read on a new handle
    const int32_t need = vs_ocr_get_font(t, nullptr, 0);
    std::string blob(size_t(need), '\0');
    vs_ocr_get_font(t, blob.data(), need);
    vs_ocr* t2 = vs_ocr_create(&p);
    check(vs_ocr_set_font(t2, blob.c_str()) == 0 && vs_ocr_font_glyphs(t2) == 40, "font saved and loaded");
    vs_ocr_run(t2, img.data, img.cols, img.rows, int32_t(img.step));
    check(readAll(t2) == read, "loaded font reads the same");
    check(vs_ocr_set_font(t2, "VSFONT1\n65 10 zz\n") < 0 && vs_ocr_set_font(t2, "not a font") < 0, "a damaged font is refused");
    vs_ocr_destroy(t2);

    // a character never taught: the check fails, it is not skipped
    vs_ocr* t3 = vs_ocr_create(&p);
    teach(t3, {"0 1 2 3 4", "A B C D E F G H I J", "K L M N O P Q R S T"}, false);   // no 5 to 9
    vs_ocr_set_expected(t3, 0, "LOT A12345");
    vs_ocr_set_expected(t3, 1, "EXP 09/28");
    vs_ocr_run(t3, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_get_stats(t3, &st);
    check(st.pass == 0, "a character missing from the font makes the check NOT OK");
    vs_ocr_destroy(t3);
    vs_ocr_destroy(t);

    // dot-matrix (CIJ) print: taught from one print, checked on another with other dots missing
    p.print = VS_PRINT_DOT;
    p.char_height_px = 7 * 6;
    p.time_budget_ms = 5000;   // these checks are of cutting and scoring, not speed (a busy test machine)
    vs_ocr* td = vs_ocr_create(&p);
    const std::vector<std::string> taughtText = {"0123456789", "ABELOPTX/"};
    bool taught = true;
    for (const unsigned seed : {11u, 12u}) {   // two prints, as an engineer teaches a dot font
        const cv::Mat timg = drawDots(taughtText, 0, 6, 0.08, seed);
        for (int i = 0; i < 2; ++i) {
            const int n = vs_ocr_teach_font(td, timg.data, timg.cols, timg.rows, int32_t(timg.step), i, taughtText[size_t(i)].c_str());
            if (n != int(taughtText[size_t(i)].size())) { taught = false; std::printf("     teach dots %d: %d (%s)\n", i, n, vs_ocr_last_error(td)); }
        }
    }
    // clean dot print, rotated: read and passed
    const cv::Mat dimg = drawDots({"LOT A12345", "EXP 09/28"}, 3, 6, 0.0, 29);
    vs_ocr_run(td, dimg.data, dimg.cols, dimg.rows, int32_t(dimg.step));
    const std::string dread = readAll(td);
    vs_ocr_set_expected(td, 0, "LOT A12345");
    vs_ocr_set_expected(td, 1, "EXP 09/28");
    vs_ocr_run(td, dimg.data, dimg.cols, dimg.rows, int32_t(dimg.step));
    vs_ocr_get_stats(td, &st);
    check(taught && st.pass == 1 && noSpaces(dread) == "LOTA12345|EXP09/28",
          "dot-matrix print taught and a clean print read and checked OK (read: " + dread + "; " + checkDetail(td) + ")");
    // missing dots lower the characters' scores (a nozzle problem is a print defect); with one dot
    // in ten missing (a stress level) the wrong text is still never OK
    const cv::Mat worn = drawDots({"LOT A12345", "EXP 09/28"}, 3, 6, 0.10, 29);
    vs_ocr_set_expected(td, 1, "EXP 09/29");
    vs_ocr_run(td, dimg.data, dimg.cols, dimg.rows, int32_t(dimg.step));
    vs_ocr_get_stats(td, &st);
    const int changedOk = st.pass;
    vs_ocr_run(td, worn.data, worn.cols, worn.rows, int32_t(worn.step));
    vs_ocr_get_stats(td, &st);
    check(changedOk == 0 && st.pass == 0, "dot-matrix print: one changed character is NOT OK (clean, and 10% dots missing)");
    const cv::Mat oFor0 = drawDots({"LOT A12345", "EXP O9/28"}, 3, 6, 0.10, 29);
    vs_ocr_set_expected(td, 1, "EXP 09/28");
    vs_ocr_run(td, oFor0.data, oFor0.cols, oFor0.rows, int32_t(oFor0.step));
    vs_ocr_get_stats(td, &st);
    check(st.pass == 0, "dot-matrix print: a letter O printed where a zero belongs is NOT OK");
    // CIJ print with 1 to 3 dots missing from every character (a blocked nozzle, a rough surface):
    // cut on the print's pitch, every character is found, none split in two and none lost; most
    // pass with a dot missing, and a damaged character that cannot be told apart scores low. A wrong
    // character, printed or expected, is never OK however many dots are missing
    for (int k = 1; k <= 3; ++k) {
        int whole = 0, passed = 0, wrongOk = 0;
        for (unsigned seed = 100; seed < 110; ++seed) {
            const cv::Mat damaged = drawDots({"LOT A12345", "EXP 09/28"}, 2, 6, 0.0, seed, k);
            const auto run = [&](const cv::Mat& im, const char* l0, const char* l1) {
                vs_ocr_set_expected(td, 0, l0);
                vs_ocr_set_expected(td, 1, l1);
                vs_ocr_run(td, im.data, im.cols, im.rows, int32_t(im.step));
                vs_ocr_get_stats(td, &st);
                return st.pass;
            };
            passed += run(damaged, "LOT A12345", "EXP 09/28");
            if (noSpaces(readAll(td)).size() == std::string("LOTA12345|EXP09/28").size()) ++whole;
            wrongOk += run(damaged, "LOT A12345", "EXP 09/29") + run(damaged, "LOT A12346", "EXP 09/28");
            const cv::Mat changed = drawDots({"LOT A12345", "EXP 08/28"}, 2, 6, 0.0, seed, k);
            wrongOk += run(changed, "LOT A12345", "EXP 09/28");
        }
        check(whole == 10 && passed >= (k == 1 ? 7 : k == 2 ? 2 : 0) && wrongOk == 0,
              "dot-matrix print, " + std::to_string(k) + " dot(s) missing from every character: all characters found in " +
              std::to_string(whole) + " of 10 prints, " + std::to_string(passed) + " OK, a wrong character OK " +
              std::to_string(wrongOk) + " of 30 times");
    }
    vs_ocr_destroy(td);

    // narrow characters (I, l, 1: three dot columns) among wide ones (T, 7, L, 4: five) at one pitch,
    // dots missing from every character. Each character is compared at a few extents near its ink and
    // keeps its best, which eases the width penalty: a narrow character must still not pass for a
    // wide one, nor l for 1 or I (one or two dots apart), however many dots are missing. Taught on the
    // pitch grid from prints with dots missing. Clean, most lines pass (l is one dot short of I, so it
    // scores just above the line); with a dot missing, I, l and 1 can no longer be told apart and score
    // low, as they must (an I without its top right dot is an l)
    vs_ocr* tn = vs_ocr_create(&p);
    const std::vector<std::string> narrowText = {"I1lT7L4I", "1lI4L7T1"};
    bool narrowTaught = true;
    for (const unsigned seed : {21u, 22u}) {
        const cv::Mat timg = drawDots(narrowText, 0, 6, 0.08, seed);
        for (int i = 0; i < 2; ++i)
            if (vs_ocr_teach_font(tn, timg.data, timg.cols, timg.rows, int32_t(timg.step), i, narrowText[size_t(i)].c_str()) != 8) {
                narrowTaught = false;
            }
    }
    check(narrowTaught, "narrow and wide dot characters taught on the pitch grid from prints with 8% of the dots missing");
    for (int k = 0; k <= 2; ++k) {
        int whole = 0, passed = 0, wrongOk = 0;
        for (unsigned seed = 400; seed < 410; ++seed) {
            const auto run = [&](const std::vector<std::string>& printed, const char* l0, const char* l1) {
                const cv::Mat im = drawDots(printed, 2, 6, 0.0, seed, k);
                vs_ocr_set_expected(tn, 0, l0);
                vs_ocr_set_expected(tn, 1, l1);
                vs_ocr_run(tn, im.data, im.cols, im.rows, int32_t(im.step));
                vs_ocr_get_stats(tn, &st);
                return st.pass;
            };
            passed += run({"T1lI 7L", "I4l1 TI"}, "T1lI 7L", "I4l1 TI");
            if (noSpaces(readAll(tn)).size() == std::string("T1lI7L|I4l1TI").size()) ++whole;
            // one character swapped in the text expected, or in the print: narrow for wide, l for 1, I for 1
            wrongOk += run({"T1lI 7L", "I4l1 TI"}, "TllI 7L", "I4l1 TI") + run({"T1lI 7L", "I4l1 TI"}, "T1lI 7L", "I4l1 TT") +
                       run({"T1lI 7L", "I4l1 TI"}, "T1l1 7L", "I4l1 TI") + run({"T1lI 7L", "I4l1 TI"}, "T1lI 7L", "I4lI TI") +
                       run({"T1lI 1L", "I4l1 TI"}, "T1lI 7L", "I4l1 TI") + run({"T1lI 7L", "I4ll TI"}, "T1lI 7L", "I4l1 TI") +
                       run({"T1lI 7L", "I4l1 TT"}, "T1lI 7L", "I4l1 TI");
        }
        check(narrowTaught && whole >= (k == 0 ? 10 : 7) && passed >= (k == 0 ? 7 : 0) && wrongOk == 0,
              "narrow and wide dot characters, " + std::to_string(k) + " dot(s) missing from each: read whole in " + std::to_string(whole) +
              " of 10, " + std::to_string(passed) + " OK, a wrong character OK " + std::to_string(wrongOk) + " of 70 times");
    }
    // an I typed as l when teaching (the strokes are too close to tell): refused by the dots, so the l's
    // dots never take in an I's
    {
        const cv::Mat timg = drawDots({"TI7L"}, 0, 6, 0.0, 23);
        const int n = vs_ocr_teach_font(tn, timg.data, timg.cols, timg.rows, int32_t(timg.step), 0, "Tl7L");
        check(n < 0, std::string("an I typed as l when teaching dot print is refused: ") + vs_ocr_last_error(tn));
    }
    vs_ocr_destroy(tn);

    // a character never taught, one dot short of a taught one (an l, the font has I): with dots missing
    // it is as likely a damaged I as an l, so the dots must not lift it to a pass as I (review of PR #91;
    // before the fix the dots passed it 5 times in 80 with one dot missing, at a score of 100)
    vs_ocr* tu = vs_ocr_create(&p);
    for (const unsigned seed : {21u, 22u}) {
        const std::vector<std::string> noL = {"I1T7L4I1", "1I4L7T1I"};
        const cv::Mat timg = drawDots(noL, 0, 6, 0.08, seed);
        for (int i = 0; i < 2; ++i) vs_ocr_teach_font(tu, timg.data, timg.cols, timg.rows, int32_t(timg.step), i, noL[size_t(i)].c_str());
    }
    for (int k = 1; k <= 2; ++k) {
        int lForI = 0;
        for (unsigned seed = 500; seed < 520; ++seed) {
            const cv::Mat im = drawDots({"T1lI 7L", "I4l1 TI"}, 2, 6, 0.0, seed, k);
            vs_ocr_set_expected(tu, 0, "T1II 7L");
            vs_ocr_set_expected(tu, 1, "I4I1 TI");
            vs_ocr_run(tu, im.data, im.cols, im.rows, int32_t(im.step));
            vs_ocr_get_stats(tu, &st);
            lForI += st.pass;
        }
        // with one dot missing the comparison of strokes alone (as on main) passes it about 1 time in
        // 20: the dots leave that as it is (pending: an owner's choice between missing dots tolerated
        // and characters never taught refused)
        check(lForI <= (k == 1 ? 2 : 0), "an l never taught, printed where I belongs, " + std::to_string(k) +
                                             " dot(s) missing from each character: OK " + std::to_string(lForI) + " of 20 times");
    }
    vs_ocr_destroy(tu);
}

// scorePrint on a made-up line: five 8 px characters at a 20 px pitch, learned from 10 parts that move
// up to 1 px; the line moved on 7 px before the 4th character is NOT OK as position, a good one OK
void testPrintPosition()
{
    auto make = [](const std::vector<int>& centres, ocr::Line& line, ocr::CheckResult& cr) {
        line = ocr::Line{};
        line.image = cv::Mat(ocr::kLineH, 160, CV_8U, cv::Scalar(230));
        cr = ocr::CheckResult{};
        cr.expected = U"ABCDE";
        cr.pass = true;
        for (size_t k = 0; k < centres.size(); ++k) {
            cv::rectangle(line.image, cv::Rect(centres[k] - 4, 6, 8, 20), cv::Scalar(30), -1);
            ocr::CheckChar c;
            c.expected = cr.expected[k];
            c.found = c.expected;
            c.score = 95;
            c.t0 = (centres[k] - 4) / ocr::kStride;
            c.t1 = (centres[k] + 3) / ocr::kStride;
            cr.chars.push_back(c);
        }
    };
    ocr::PrintRef ref;
    ocr::Line line;
    ocr::CheckResult cr;
    for (int part = 0; part < 10; ++part) {
        const int j = part % 3 - 1;    // -1, 0, 1 px
        make({20, 40 + j, 60, 80 - j, 100}, line, cr);
        ocr::scorePrint(line, {}, cr, ref, 0, 60);
        ref.add(cr, 0);
        ++ref.parts;
    }
    make({20, 40, 60, 80, 100}, line, cr);
    ocr::scorePrint(line, {}, cr, ref, 0, 60);
    check(cr.pass && cr.chars[3].learned, "a good line scores OK against the learned pitch");
    make({20, 40, 60, 87, 107}, line, cr);
    ocr::scorePrint(line, {}, cr, ref, 0, 60);
    check(!cr.pass && cr.chars[3].reason == VS_PRINT_POSITION && cr.chars[3].qPitch < 60 && cr.chars[4].reason == VS_PRINT_OK,
          "the line moved on 7 px before one character: NOT OK, position, that character only (pitch score " +
          std::to_string(int(cr.chars[3].qPitch)) + ")");
}

// print quality learned from good parts (quality.cpp): dot print taught, 10 good parts learned, then
// a print defect on one character is NOT OK with its reason, while good prints stay OK
struct CharOut { vs_ocr_char c; vs_ocr_char_quality q; };
std::vector<CharOut> charsOf(vs_ocr* t)
{
    std::vector<CharOut> out;
    for (int i = 0; i < vs_ocr_char_count(t); ++i) {
        CharOut o{};
        vs_ocr_get_char(t, i, &o.c);
        o.q.struct_size = int32_t(sizeof(vs_ocr_char_quality));
        vs_ocr_get_char_quality(t, i, &o.q);
        out.push_back(o);
    }
    return out;
}

void testPrintQuality()
{
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.reader = VS_READER_FONT;
    p.print = VS_PRINT_DOT;
    p.char_height_px = 7 * 6;
    p.time_budget_ms = 500;
    vs_ocr* t = vs_ocr_create(&p);
    const std::vector<std::string> taughtText = {"0123456789", "ABELOPTX/"};
    for (const unsigned seed : {11u, 12u}) {
        const cv::Mat timg = drawDots(taughtText, 0, 6, 0.08, seed);
        for (int i = 0; i < 2; ++i) vs_ocr_teach_font(t, timg.data, timg.cols, timg.rows, int32_t(timg.step), i, taughtText[size_t(i)].c_str());
    }
    const std::vector<std::string> text = {"LOT A12345", "EXP 09/28"};
    vs_ocr_set_expected(t, 0, text[0].c_str());
    vs_ocr_set_expected(t, 1, text[1].c_str());
    vs_ocr_stats st;
    auto run = [&](const cv::Mat& img) {
        vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
        vs_ocr_get_stats(t, &st);
        if (std::getenv("OCR_TEST_PRINT")) {
            std::printf("     run pass %d: %s\n", st.pass, checkDetail(t).c_str());
            for (const auto& o : charsOf(t))
                std::printf("       %c/%c s%.0f st%d r%d rec%.0f c%.0f(%.0f) i%.0f(%.0f) p%.1f(%.0f)\n", char(o.c.codepoint ? o.c.codepoint : '_'),
                            char(o.c.found ? o.c.found : '_'), o.c.score, o.c.status, o.q.reason, o.q.recognition, o.q.contrast,
                            o.q.contrast_score, o.q.ink, o.q.ink_score, o.q.pitch, o.q.pitch_score);
        }
        return st.pass == 1;
    };
    // what the good parts vary in: a slight turn and camera noise (the font taught from two prints
    // misreads some with dots missing at random, which is the reader's business, not this test's)
    auto good = [&](unsigned seed, const DotDefects& d = {}) {
        return drawDots(text, 1.0 + 0.4 * std::sin(double(seed)), 6, 0.0, seed, 0, d);
    };

    check(vs_ocr_learn_print(t) < 0, "nothing learned before a run");
    const cv::Mat bad = drawDots({"LOT A12345", "EXP 09/29"}, 1, 6, 0.0, 5);
    run(bad);
    check(st.pass == 0 && vs_ocr_learn_print(t) < 0, "a part that is NOT OK is never learned");
    // before learning, contrast, ink and pitch are measured but not scored
    run(good(40));
    auto cs = charsOf(t);
    bool measured = !cs.empty(), unscored = true;
    for (const auto& o : cs) {
        measured = measured && o.q.ink > 0 && o.q.contrast > 20;
        unscored = unscored && !o.q.learned && o.q.ink_score == 100 && o.q.contrast_score == 100;
    }
    check(st.pass == 1 && measured && unscored, "measured, not scored, before learning");

    int parts = 0, learnFails = 0;
    for (unsigned seed = 100; seed < 110; ++seed) {
        if (run(good(seed))) parts = vs_ocr_learn_print(t);
        else ++learnFails;
    }
    check(learnFails == 0 && parts == 10 && vs_ocr_print_parts(t) == 10, "10 good parts learned (" + std::to_string(parts) + ")");

    int goodOk = 0;
    for (unsigned seed = 200; seed < 210; ++seed) goodOk += run(good(seed));
    cs = charsOf(t);
    check(goodOk == 10 && !cs.empty() && cs[0].q.learned == 1, "10 other good parts are OK after learning (" + std::to_string(goodOk) + " OK)");

    // one defect at a time on the 3 of A12345 (line 0, index 7 counting the space): NOT OK, that
    // character, that reason
    // reason < 0: only NOT OK is required (the reader may reject the part before the print measures do)
    auto defect = [&](const DotDefects& d, int reason, const std::string& what) {
        const bool ok = run(good(300, d));
        cs = charsOf(t);
        const CharOut* worst = nullptr;
        for (const auto& o : cs)
            if (o.c.status != VS_CHAR_OK && (!worst || o.c.score < worst->c.score)) worst = &o;
        const bool right = reason < 0 || (worst && worst->c.codepoint == '3' && worst->q.reason == reason);
        char b[200];
        if (worst)
            std::snprintf(b, sizeof b, " (%c status %d score %.0f, reason %d, contrast %.0f/%.0f ink %.0f/%.0f pitch %.1f/%.0f)", char(worst->c.codepoint), worst->c.status,
                          worst->c.score, worst->q.reason, worst->q.contrast, worst->q.contrast_score, worst->q.ink, worst->q.ink_score,
                          worst->q.pitch, worst->q.pitch_score);
        check(!ok && right, what + (worst ? b : " (no character failed)"));
    };
    defect({{{0, 7}, {0.35, 0, 1}}}, VS_PRINT_BROKEN, "a third of the dots of one character missing: NOT OK, broken");
    defect({{{0, 7}, {0, 0, 0.45}}}, VS_PRINT_FAINT, "one character printed faint: NOT OK, faint");
    // the printer slips two dot pitches before the 3: the rest of the line moves on. The fixed-pitch dot
    // reader (PR #80) cuts this line on one grid and misreads it first, so only NOT OK is checked here;
    // the position score itself is checked on the measures below
    defect({{{0, 7}, {0, 12, 1}}, {{0, 8}, {0, 12, 1}}, {{0, 9}, {0, 12, 1}}}, -1,
           "the print slips two dot pitches before one character: NOT OK");

    // print beyond the expected text
    run(drawDots({"LOT A123457", "EXP 09/28"}, 1, 6, 0.0, 301));
    cs = charsOf(t);
    int extras = 0;
    for (const auto& o : cs) extras += o.c.status == VS_CHAR_EXTRA && o.c.codepoint == 0 && o.c.found == '7';
    check(st.pass == 0 && extras == 1, "a character printed after the expected text: NOT OK, extra 7");
    run(drawDots({"8LOT A12345", "EXP 09/28"}, 1, 6, 0.0, 302));
    check(st.pass == 0, "a character printed before the expected text: NOT OK");

    // kept with the job
    const int32_t need = vs_ocr_get_print(t, nullptr, 0);
    std::string blob(size_t(need), '\0');
    vs_ocr_get_print(t, blob.data(), need);
    vs_ocr* t2 = vs_ocr_create(&p);
    vs_ocr_set_font(t2, [&] { std::string f(size_t(vs_ocr_get_font(t, nullptr, 0)), '\0'); vs_ocr_get_font(t, f.data(), int32_t(f.size())); return f; }().c_str());
    vs_ocr_set_expected(t2, 0, text[0].c_str());
    vs_ocr_set_expected(t2, 1, text[1].c_str());
    check(vs_ocr_set_print(t2, blob.c_str()) == 0 && vs_ocr_print_parts(t2) == 10, "print reference saved and loaded");
    const cv::Mat faint = good(300, {{{0, 7}, {0, 0, 0.45}}});
    vs_ocr_run(t2, faint.data, faint.cols, faint.rows, int32_t(faint.step));
    vs_ocr_get_stats(t2, &st);
    check(st.pass == 0, "the loaded reference fails the faint character too");
    check(vs_ocr_set_print(t2, "VSPRINT1 3 4\nI x 1 2 3\n") < 0 && vs_ocr_set_print(t2, "nonsense") < 0 && vs_ocr_print_parts(t2) == 10,
          "a damaged reference is refused and the old one kept");
    vs_ocr_char_quality q{};
    check(vs_ocr_get_char_quality(t2, 0, &q) < 0, "a quality struct without struct_size is refused");
    vs_ocr_clear_print(t2);
    check(vs_ocr_print_parts(t2) == 0, "reference cleared");
    vs_ocr_destroy(t2);
    vs_ocr_destroy(t);
}

// the character with this code point in the last run, and its box centre; false when none
bool findChar(vs_ocr* t, char cp, vs_ocr_char& out, float& cx, float& cy)
{
    for (int i = 0; i < vs_ocr_char_count(t); ++i) {
        vs_ocr_get_char(t, i, &out);
        if (out.codepoint != cp) continue;
        cx = (out.x[0] + out.x[1] + out.x[2] + out.x[3]) / 4;
        cy = (out.y[0] + out.y[1] + out.y[2] + out.y[3]) / 4;
        return true;
    }
    return false;
}

// classical OCR teaching: "Teach this character" from a run image, characters never taught named as
// such, character boxes over the whole character, and a line confirmed from the font's own read
void testFontTeaching()
{
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.char_height_px = 30;
    p.time_budget_ms = 500;
    p.reader = VS_READER_FONT;
    p.print = VS_PRINT_SOLID;
    vs_ocr* t = vs_ocr_create(&p);
    const cv::Mat img = drawText({"LST A12345"}, 4, 5, false, false);
    check(vs_ocr_teach_char(t, img.data, img.cols, img.rows, int32_t(img.step), 300, 150, "5") < 0,
          std::string("teach this character with no font yet is refused: ") + vs_ocr_last_error(t));
    teach(t, {"0 1 2 3 4", "A B C D E F G H I J", "K L M N O P Q R S T"}, false);   // no 5 to 9
    const int before = vs_ocr_font_glyphs(t);

    // a check of a 5 never taught: NOT OK, and the 5 is named as not taught
    vs_ocr_set_expected(t, 0, "LST A12345");
    vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_stats st;
    vs_ocr_get_stats(t, &st);
    vs_ocr_char five{};
    float fx = 0, fy = 0;
    const bool got5 = findChar(t, '5', five, fx, fy);
    check(st.pass == 0 && got5 && five.status == VS_CHAR_UNKNOWN,
          "a 5 never taught: NOT OK, status unknown (" + std::to_string(got5 ? five.status : -1) + ")");
    // the font's character boxes cover the character (about its width), not one matrix column
    vs_ocr_char a{};
    float ax = 0, ay = 0;
    findChar(t, 'A', a, ax, ay);
    const float boxW = std::hypot(a.x[1] - a.x[0], a.y[1] - a.y[0]);
    check(boxW > 15 && boxW < 45, "the A's box spans the A (" + std::to_string(int(boxW)) + " px wide, letters 30 px high)");

    // refused: a typed character the print clearly is not, nothing printed there, more than one character
    vs_ocr_char four{};
    float qx = 0, qy = 0;
    findChar(t, '4', four, qx, qy);
    check(vs_ocr_teach_char(t, img.data, img.cols, img.rows, int32_t(img.step), qx, qy, "A") < 0 && vs_ocr_font_glyphs(t) == before,
          std::string("the 4 taught as an A is refused: ") + vs_ocr_last_error(t));
    check(vs_ocr_teach_char(t, img.data, img.cols, img.rows, int32_t(img.step), 20, 20, "5") < 0,
          std::string("teaching where nothing is printed is refused: ") + vs_ocr_last_error(t));
    check(vs_ocr_teach_char(t, img.data, img.cols, img.rows, int32_t(img.step), fx, fy, "55") < 0, "two characters typed are refused");

    // taught from the failed image: the same check is now OK, and the font kept it
    const int n = vs_ocr_teach_char(t, img.data, img.cols, img.rows, int32_t(img.step), fx, fy, "5");
    vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_get_stats(t, &st);
    check(n == 1 && vs_ocr_font_glyphs(t) == before + 1 && st.pass == 1,
          std::string("the 5 taught from the NOT OK image (") + vs_ocr_last_error(t) + "): now OK (" + checkDetail(t) + ")");

    // reading print in a font of only digits and some letters: a W, like no taught character, is
    // named unknown, not only unsure
    vs_ocr_clear_expected(t);
    const cv::Mat w = drawText({"LST W12"}, 0, 0, false, false);
    vs_ocr_run(t, w.data, w.cols, w.rows, int32_t(w.step));
    std::string statuses;
    for (int i = 0; i < vs_ocr_char_count(t); ++i) {
        vs_ocr_char c;
        vs_ocr_get_char(t, i, &c);
        statuses += std::to_string(c.status);
    }
    check(statuses.size() == 6 && statuses[3] == '0' + VS_CHAR_UNKNOWN,
          "a W read with a font without it is named unknown (" + readAll(t) + ", statuses " + statuses + ")");

    // teaching a line confirmed from the font's read: on a print with the new characters 6 7 8 9 the
    // read is unsure of the new ones and sure of A and 1. A new character can still read as a taught
    // one with confidence (this drawing font's 8 as B), so the whole line is shown to be confirmed
    const cv::Mat more = drawText({"A 6 7 8 9 1"}, 0, 0, false, false);
    vs_ocr_run(t, more.data, more.cols, more.rows, int32_t(more.step));
    std::string asked;
    for (int i = 0; i < vs_ocr_char_count(t); ++i) {
        vs_ocr_char c;
        vs_ocr_get_char(t, i, &c);
        asked += c.status == VS_CHAR_OK ? char(c.codepoint) : '?';
    }
    const bool unsureNew = asked.size() == 6 && asked[0] == 'A' && asked[5] == '1' && asked[1] == '?' && asked[2] == '?' && asked[4] == '?';
    const int taught = vs_ocr_teach_font(t, more.data, more.cols, more.rows, int32_t(more.step), 0, "A67891");
    check(unsureNew && taught == 6 && vs_ocr_font_glyphs(t) == before + 5,
          "the new 6 7 9 are unsure, A and 1 sure (" + asked + "); the confirmed line teaches them (" + std::to_string(taught) + ")");
    vs_ocr_destroy(t);
}

// the line's shift state from vs_ocr_get_line_ext
vs_ocr_line_ext extOf(vs_ocr* t, int line)
{
    vs_ocr_line_ext e{};
    e.struct_size = int32_t(sizeof(vs_ocr_line_ext));
    vs_ocr_get_line_ext(t, line, &e);
    return e;
}

cv::Mat moved(const cv::Mat& img, double dx, double dy)
{
    cv::Mat out;
    const cv::Mat M = (cv::Mat_<double>(2, 3) << 1, 0, dx, 0, 1, dy);
    cv::warpAffine(img, out, M, img.size(), cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    return out;
}

// Allow shift (tool spec 7.3): the line's place is learned from a good part; within the allowed shift
// OK, beyond it NOT OK, nothing learned NOT OK; an empty expected line never passes
void testShift()
{
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.char_height_px = 30;
    p.time_budget_ms = 3000;   // the checks here are not about time (a loaded test PC)
    p.reader = VS_READER_FONT;
    p.print = VS_PRINT_SOLID;
    p.max_skew_deg = 8;
    p.allow_shift_px = 6;
    vs_ocr* t = vs_ocr_create(&p);
    check(t != nullptr, "params with allow shift accepted");
    teach(t, {"0 1 2 3 4 5", "A L O T"}, false);
    vs_ocr_set_expected(t, 0, "LOT A12345");
    const cv::Mat img = drawText({"LOT A12345"}, 0, 0, false, false);
    vs_ocr_stats st;
    auto run = [&](const cv::Mat& m) {
        vs_ocr_run(t, m.data, m.cols, m.rows, int32_t(m.step));
        vs_ocr_get_stats(t, &st);
        return st.pass == 1;
    };
    const bool first = run(img);
    check(!first && extOf(t, 0).shift_state == VS_SHIFT_UNLEARNED, "allow shift with no place learned: NOT OK, unlearned");
    check(vs_ocr_learn_print(t) == 1, std::string("a part failing only on the unlearned place is learned: ") + vs_ocr_last_error(t));
    check(run(img) && extOf(t, 0).shift_state == VS_SHIFT_OK && extOf(t, 0).shift_px < 1,
          "the same print: OK, shift " + std::to_string(extOf(t, 0).shift_px));
    check(run(moved(img, 3, 2)) && extOf(t, 0).shift_state == VS_SHIFT_OK, "moved 3, 2 px (allowed 6): OK");
    const bool far = run(moved(img, 12, 0));
    const vs_ocr_line_ext e = extOf(t, 0);
    check(!far && e.shift_state == VS_SHIFT_BEYOND && std::abs(e.shift_x - 12) < 2 && std::abs(e.shift_y) < 2,
          "moved 12 px: NOT OK, beyond (" + std::to_string(e.shift_x) + ", " + std::to_string(e.shift_y) + ")");
    check(vs_ocr_learn_print(t) < 0, "a part beyond the allowed shift is never learned");
    // kept with the job
    std::string blob(size_t(vs_ocr_get_print(t, nullptr, 0)), '\0');
    vs_ocr_get_print(t, blob.data(), int32_t(blob.size()));
    vs_ocr* t2 = vs_ocr_create(&p);
    check(vs_ocr_set_print(t2, blob.c_str()) == 0 && blob.find("\nX 0 ") != std::string::npos, "the learned place is saved and loaded");
    vs_ocr_destroy(t2);
    // off: the moved print is OK again
    p.allow_shift_px = 0;
    vs_ocr_set_params(t, &p);
    check(run(moved(img, 12, 0)) && extOf(t, 0).shift_state == VS_SHIFT_OFF, "allow shift 0: the moved print is OK, not checked");
    vs_ocr_line_ext bad{};
    check(vs_ocr_get_line_ext(t, 0, &bad) < 0, "a line ext struct without struct_size is refused");
    // an empty expected line (an empty value from the PLC) never passes
    vs_ocr_set_expected(t, 0, "");
    check(!run(img), "an empty expected line: NOT OK");
    vs_ocr_set_expected(t, 0, "   ");
    check(!run(img), "an expected line of spaces only: NOT OK");
    vs_ocr_destroy(t);
}

// Dot print (tool spec 7.5): the dot size and pitch measured, solid print refused; dot print read with
// the learned dot size and with a weak and a strong join; inkjet grain read with the Inkjet print type
void testDotsAndInkjet()
{
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.reader = VS_READER_FONT;
    p.print = VS_PRINT_DOT;
    p.char_height_px = 7 * 6;
    p.time_budget_ms = 3000;   // the checks here are not about time (a loaded test PC)
    vs_ocr* t = vs_ocr_create(&p);
    const cv::Mat dots = drawDots({"LOT A12345"}, 1, 6, 0.0, 21);
    float pitch = 0;
    const float d = vs_ocr_measure_dots(t, dots.data, dots.cols, dots.rows, int32_t(dots.step), &pitch);
    check(d >= 3 && d <= 6.5 && pitch >= 5 && pitch <= 7, "dots measured: size " + std::to_string(d) + " px, pitch " + std::to_string(pitch) + " px (drawn 5, 6)");
    const cv::Mat solid = drawText({"LOT A12345"}, 0, 0, false, false, 42);
    check(vs_ocr_measure_dots(t, solid.data, solid.cols, solid.rows, int32_t(solid.step), nullptr) < 0,
          std::string("solid print has no dots: ") + vs_ocr_last_error(t));

    for (const float join : {0.f, 0.7f, 1.2f}) {   // much more blurs O into 0: the cost of a strong join
        p.dot_size_px = d;
        p.join_strength = join;
        vs_ocr* f = vs_ocr_create(&p);
        const std::vector<std::string> taughtText = {"0123456789", "ABELOPTX/"};
        for (const unsigned seed : {11u, 12u}) {
            const cv::Mat timg = drawDots(taughtText, 0, 6, 0.08, seed);
            for (int i = 0; i < 2; ++i) vs_ocr_teach_font(f, timg.data, timg.cols, timg.rows, int32_t(timg.step), i, taughtText[size_t(i)].c_str());
        }
        vs_ocr_set_expected(f, 0, "LOT A12345");
        int ok = 0;
        vs_ocr_stats st;
        for (unsigned seed = 30; seed < 35; ++seed) {
            const cv::Mat img = drawDots({"LOT A12345"}, 1, 6, 0.05, seed);
            vs_ocr_run(f, img.data, img.cols, img.rows, int32_t(img.step));
            vs_ocr_get_stats(f, &st);
            ok += st.pass;
            if (!st.pass && std::getenv("OCR_TEST_PRINT")) std::printf("     join %.1f %s: %s\n", join, readAll(f).c_str(), checkDetail(f).c_str());
        }
        check(ok >= 4, "dot print with the learned dot size, join " + std::to_string(join).substr(0, 3) + ": " + std::to_string(ok) + " of 5 OK");
        vs_ocr_set_expected(f, 0, "LOT A12346");
        const cv::Mat img = drawDots({"LOT A12345"}, 1, 6, 0.05, 30);
        vs_ocr_run(f, img.data, img.cols, img.rows, int32_t(img.step));
        vs_ocr_get_stats(f, &st);
        check(st.pass == 0, "and a wrong character is NOT OK");
        vs_ocr_destroy(f);
    }
    vs_ocr_destroy(t);

    // inkjet: strokes made of grain (40 % of the ink pixels missing at random). Inkjet fills the grain; on
    // light grain (30 %) Solid reads better (Inkjet then 1 of 5): the print type is the engineer's or
    // auto-tune's choice, as the spec says
    auto grainy = [](const std::string& text, unsigned seed) {
        cv::Mat img = drawText({text}, 0, 0, false, false);
        std::mt19937 rng(seed);
        for (int y = 0; y < img.rows; ++y)
            for (int x = 0; x < img.cols; ++x) {
                uint8_t& v = img.at<uint8_t>(y, x);
                if (v < 150 && rng() % 100 < 40) v = uint8_t(195 + rng() % 10);
            }
        return img;
    };
    // the same grainy print read as Solid and as Inkjet (letters unlike the digits: O and 0 are the
    // reader's business, not the grain's)
    int okOf[2] = {0, 0};
    for (const int print : {VS_PRINT_SOLID, VS_PRINT_INKJET}) {
    vs_ocr_default_params(&p);
    p.reader = VS_READER_FONT;
    p.print = print;
    p.char_height_px = 30;
    p.time_budget_ms = 3000;   // the checks here are not about time (a loaded test PC)
    p.max_skew_deg = 8;
    vs_ocr* k = vs_ocr_create(&p);
    const std::vector<std::string> lines = {"0 1 2 3 4 5", "A E L T"};
    const cv::Mat timg = [&] {
        cv::Mat a = grainy(lines[0], 1), b = grainy(lines[1], 2);
        cv::Mat both = a.clone();
        b(cv::Rect(0, 100, b.cols, 100)).copyTo(both(cv::Rect(0, 200, b.cols, 100)));
        return both;
    }();
    int taught = 0;
    for (int i = 0; i < 2; ++i) taught += vs_ocr_teach_font(k, timg.data, timg.cols, timg.rows, int32_t(timg.step), i, lines[size_t(i)].c_str()) > 0;
    vs_ocr_set_expected(k, 0, "LET A12345");
    int& ok = okOf[print == VS_PRINT_INKJET];
    vs_ocr_stats st;
    for (unsigned seed = 40; seed < 45; ++seed) {
        const cv::Mat img = grainy("LET A12345", seed);
        vs_ocr_run(k, img.data, img.cols, img.rows, int32_t(img.step));
        vs_ocr_get_stats(k, &st);
        ok += st.pass;
        if (!st.pass && std::getenv("OCR_TEST_PRINT")) std::printf("     inkjet %s: %s\n", readAll(k).c_str(), checkDetail(k).c_str());
    }
    check(taught == 2, "taught from grainy print");
    vs_ocr_destroy(k);
    }
    check(okOf[1] >= 4 && okOf[1] >= okOf[0], "inkjet grain: " + std::to_string(okOf[1]) + " of 5 OK as Inkjet, " +
          std::to_string(okOf[0]) + " as Solid");
}

// A line cut short (the region's edge through its last letter) fails, and vs_ocr_get_line_ext says why
// (cut_short), so the operator is told the text may go on past the line; a struct of the size before
// cut_short was added is still served, without it
void testCutShort()
{
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.char_height_px = 30;
    p.time_budget_ms = 3000;   // not about time (a loaded test PC)
    p.reader = VS_READER_FONT;
    p.print = VS_PRINT_SOLID;
    p.max_skew_deg = 8;
    vs_ocr* t = vs_ocr_create(&p);
    teach(t, {"0 1 2 3 4 5", "A L O T"}, false);
    const cv::Mat img = drawText({"LOT A12345"}, 0, 0, false, false);
    vs_ocr_stats st;
    auto run = [&](const char* expected) {
        vs_ocr_set_expected(t, 0, expected);
        vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
        vs_ocr_get_stats(t, &st);
        return st.pass == 1;
    };
    check(run("LOT A12345") && extOf(t, 0).cut_short == 0, "the whole line in the region: OK, not cut short");
    // the region ends in the middle of the last 5: what is left reads LOT A1234 and a part of a letter
    int base = 0;
    const cv::Size sz = cv::getTextSize("LOT A12345", cv::FONT_HERSHEY_SIMPLEX, 30 / 22.0, 3, &base);
    p.roi_x = 60;
    p.roi_y = 60;
    p.roi_w = 120 + sz.width - 9 - p.roi_x;
    p.roi_h = 200;
    vs_ocr_set_params(t, &p);
    const bool cut = run("LOT A1234");
    check(!cut && st.lines >= 1 && extOf(t, 0).cut_short == 1,
          "the region's edge through the last letter: NOT OK, cut short (" + std::to_string(extOf(t, 0).cut_short) + ")");
    vs_ocr_line_ext old{};
    old.struct_size = int32_t(offsetof(vs_ocr_line_ext, cut_short));
    old.cut_short = 77;
    check(vs_ocr_get_line_ext(t, 0, &old) == 0 && old.struct_size == int32_t(offsetof(vs_ocr_line_ext, cut_short))
          && old.cut_short == 77,
          "a line ext struct of the older size is served, up to pos_y only");
    vs_ocr_line_ext odd{};
    odd.struct_size = int32_t(sizeof(vs_ocr_line_ext)) + 4;
    check(vs_ocr_get_line_ext(t, 0, &odd) < 0, "a line ext struct of an unknown size is refused");
    vs_ocr_destroy(t);
}

// Read text safety (vs_agree): the line confidence (CTC posterior of the whole read) sees a dropped character the
// lowest character confidence never shows; a second reader must read the same text, or the line is NOT OK
void testAgreement()
{
    const ocr::Charset cs = testCharset();
    // "AB" clearly, then a column where "." has 0.35 and the blank 0.65: the greedy read drops the "."
    cv::Mat lp = fakeLogits(U"AB", cs);
    const int C = int(cs.chars.size());
    cv::Mat dot(1, C, CV_32F, cv::Scalar(std::log(1e-4f)));
    dot.at<float>(0, 0) = std::log(0.65f);
    dot.at<float>(0, cs.find(U'.')) = std::log(0.35f);
    lp.push_back(dot);
    const auto read = ocr::greedyRead(lp, cs);
    float lowest = 100;
    std::u32string text;
    for (const auto& rc : read) { lowest = std::min(lowest, 100 * rc.conf); text.push_back(rc.c); }
    // against the same line without that column: the "." takes 0.35 of the line's probability from "AB"
    const float sure = ocr::linePosterior(fakeLogits(U"AB", cs), cs, U"AB");
    const float pAB = ocr::linePosterior(lp, cs, U"AB") / sure, pDot = ocr::linePosterior(lp, cs, U"AB.") / sure;
    check(text == U"AB" && lowest > 90 && pAB > 0.6f && pAB < 0.7f && pDot > 0.3f && pDot < 0.4f,
          "a dropped \".\" (0.35 in one column): characters " + std::to_string(lowest) + ", line posterior x "
          + std::to_string(pAB) + " (\".\" " + std::to_string(pDot) + ")");
    check(std::abs(ocr::linePosterior(lp, cs, U"A B") - ocr::linePosterior(lp, cs, U"AB")) < 1e-6f, "spaces are not compared (counted as blanks)");
    check(ocr::linePosterior(lp, cs, U"A?") == 0.f && ocr::linePosterior(lp, cs, U"") == 0.f,
          "a character the charset lacks, or no text, has posterior 0");
    check(std::abs(ocr::lineConfidence(1.f) - 100.f) < 1e-3f && ocr::lineConfidence(ocr::linePosterior(lp, cs, U"AB")) < 60.f,
          "line confidence 100 for a sure line, below 60 for the dropped \".\" ("
          + std::to_string(ocr::lineConfidence(ocr::linePosterior(lp, cs, U"AB"))) + ")");
    const cv::Mat line(ocr::kLineH, 37, CV_8U, cv::Scalar(222));
    const cv::Mat sh = ocr::shiftedLine(line);
    check(sh.cols % ocr::kStride == 0 && sh.cols >= 39 && sh.at<uchar>(5, 0) == 222, "the shifted line: width a multiple of 4, padded with its background");

    const std::string dir = OCR_MODEL_DIR;
    const std::string onnx = dir + "/ocrnet.onnx", csf = dir + "/ocr_charset.txt";
    if (!std::ifstream(onnx)) { std::printf("skip  agreement model tests: %s not found\n", onnx.c_str()); return; }
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.char_height_px = 30;
    p.time_budget_ms = 3000;   // not about time (a loaded test PC)
    p.print = VS_PRINT_SOLID;
    vs_ocr* t = vs_ocr_create(&p);
    check(vs_ocr_load_model(t, onnx.c_str(), csf.c_str()) == 0, "model loads");
    const cv::Mat img = drawText({"LOT A12345"}, 0, 0, false, false);
    vs_ocr_stats st;
    vs_ocr_line l{};
    auto run = [&] {
        vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
        vs_ocr_get_stats(t, &st);
        l = vs_ocr_line{};
        vs_ocr_get_line(t, 0, &l);
        return st.pass == 1 && l.pass == 1;
    };
    const bool clean = run();
    vs_ocr_line_ext e = extOf(t, 0);
    check(clean && e.agreement == VS_AGREE_SAME && e.second_readers == (1 << VS_SECOND_SHIFT) && e.line_confidence >= 60
          && l.quality <= e.line_confidence,
          std::string("clean print, no second reader: the shifted re-read agrees, OK (") + l.text + ", "
          + std::to_string(l.quality) + ", line " + std::to_string(e.line_confidence) + ")");
    check(vs_ocr_load_second_model(t, onnx.c_str(), csf.c_str()) == 0 && run()
          && extOf(t, 0).second_readers == (1 << VS_SECOND_MODEL), "a second model loaded: it reads the line again, OK");
    vs_ocr_load_second_model(t, nullptr, nullptr);

    // a taught font that learned the 5 as a 6 reads otherwise: the AI's read is NOT OK, the 5 unsure
    const int n = vs_ocr_teach_font(t, img.data, img.cols, img.rows, int32_t(img.step), 0, "LOT A12346");
    const bool differ = !run();
    e = extOf(t, 0);
    int unsure = -1;
    for (int i = l.first_char; i < l.first_char + l.char_count; ++i) {
        vs_ocr_char c{};
        vs_ocr_get_char(t, i, &c);
        if (c.status == VS_CHAR_UNSURE) unsure = unsure == -1 ? c.found : -2;
    }
    check(n == 9 && differ && e.agreement == VS_AGREE_DIFFERENT && (e.second_readers & (1 << VS_SECOND_FONT)) && l.quality < p.min_quality
          && unsure == '5',
          "the taught font reads 6 where the AI reads 5: NOT OK, the 5 unsure (" + std::to_string(unsure) + ", "
          + std::to_string(l.quality) + ")");
    vs_ocr_set_format(t, 0, "LOT {A:1}{D:5}");
    const bool fmt = run();
    check(!fmt && l.format == 0 && extOf(t, 0).agreement == VS_AGREE_DIFFERENT, "the same under a format: NOT OK");
    vs_ocr_clear_formats(t);
    check(vs_ocr_set_agreement(t, VS_AGREE_OFF) == 0 && run() && extOf(t, 0).agreement == VS_AGREE_NOT_CHECKED,
          "agreement off: the AI alone decides, OK");
    check(vs_ocr_set_agreement(t, VS_AGREE_SHIFT) == 0 && run() && extOf(t, 0).second_readers == (1 << VS_SECOND_SHIFT),
          "shifted re-read only: the taught font is not asked, OK");
    check(vs_ocr_set_agreement(t, 7) < 0, "an unknown mode is refused");
    vs_ocr_set_agreement(t, VS_AGREE_AUTO);
    // the font reads first, the AI model second: both read LOT A12346 / A12345
    p.reader = VS_READER_FONT;
    vs_ocr_set_params(t, &p);
    check(!run() && (extOf(t, 0).second_readers & (1 << VS_SECOND_AI)) && extOf(t, 0).agreement == VS_AGREE_DIFFERENT,
          "the font reads first, the AI second: they differ, NOT OK");
    // Check text is not affected: the expected text is checked as before
    p.reader = VS_READER_AI;
    vs_ocr_set_params(t, &p);
    vs_ocr_set_expected(t, 0, "LOT A12345");
    check(run() && extOf(t, 0).agreement == VS_AGREE_NOT_CHECKED, "Check text: no second reader, OK");
    vs_ocr_clear_expected(t);
    vs_ocr_line_ext old{};
    old.struct_size = int32_t(offsetof(vs_ocr_line_ext, agreement));
    old.agreement = 77;
    run();
    check(vs_ocr_get_line_ext(t, 0, &old) == 0 && old.struct_size == int32_t(offsetof(vs_ocr_line_ext, agreement))
          && old.agreement == 77, "a line ext struct of the size before agreement is served, up to cut_short");
    vs_ocr_destroy(t);
}

// Text-line layout prior (layout.cpp, tool study 7.4.6): parsed and normalised, learned from lines, a read
// fits it or says why not; the line finder joins the two pieces of one line a wide gap split; Read text
// lines are read with as many characters as the layout allows, and a block that does not fit fails
void testLayout()
{
    ocr::Layout L;
    std::string err;
    check(ocr::parseLayout(" 2 2  2-4|6 ", L, err) && L.lines.size() == 2 && L.source == "2 2 2-4 | 6"
          && L.lines[0].minChars() == 6 && L.lines[0].maxChars() == 8, "layout parsed: " + L.source);
    for (const char* bad : {"", "2 x", "4-2", "2 || 3", "65", "0", "2-", "|"})
        check(!ocr::parseLayout(bad, L, err), std::string("layout \"") + bad + "\" refused: " + err);
    check(ocr::layoutOf({U"LOT A12345", U"", U"EXP  09/28 16:59"}) == "3 6 | 3 5 5", "layout learned from lines: " +
          ocr::layoutOf({U"LOT A12345", U"", U"EXP  09/28 16:59"}));
    ocr::parseLayout("2 2 2-4", L, err);
    const auto& l0 = L.lines[0];
    check(ocr::layoutMismatch(U"AB CD EFG", l0).empty() && ocr::layoutMismatch(U"ABCDEFG", l0).empty()
          && ocr::layoutMismatch(U"AB CDEFGH", l0).empty(),
          "a read fits: every group, or the count when a word gap was not seen");
    check(!ocr::layoutMismatch(U"AB CD E", l0).empty(), "too few characters: " + ocr::layoutMismatch(U"AB CD E", l0));
    check(!ocr::layoutMismatch(U"ABC D EFG", l0).empty(), "a group too long: " + ocr::layoutMismatch(U"ABC D EFG", l0));
    check(!ocr::layoutMismatch(U"A B CD EF", l0).empty(), "a word gap too many: " + ocr::layoutMismatch(U"A B CD EF", l0));
    check(ocr::layoutFormat(l0) == "{?:2} {?:2} {?:2-4}", "read as a format: " + ocr::layoutFormat(l0));

    // one line printed with a gap far wider than a word gap: two pieces without the layout; one line with it
    const cv::Mat wide = drawText({"LOT            A12345", "EXP 09/28"}, 0, 0, false, false);
    ocr::FindParams fp;
    fp.roi = cv::Rect(0, 0, wide.cols, wide.rows);
    fp.charHeight = 30;
    const auto apart = ocr::findLines(wide, fp, false);
    ocr::parseLayout("3 6 | 3 5", L, err);
    fp.layout = &L;
    const auto joined = ocr::findLines(wide, fp, false);
    check(apart.size() == 3 && joined.size() == 2 && !joined[0].cutShort && !joined[1].cutShort,
          "a line split by a wide gap: " + std::to_string(apart.size()) + " lines, with the layout " + std::to_string(joined.size()) +
          (joined.empty() ? "" : joined[0].cutShort ? " (cut short)" : " (whole)"));
    // the layout lists as many lines as are found: nothing is joined (two lines apart on one band stay apart)
    ocr::parseLayout("3 | 6 | 3 5", L, err);
    check(ocr::findLines(wide, fp, false).size() == 3, "as many lines as the layout lists: none joined");

    // end to end, classical OCR: Read text with the layout
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.char_height_px = 30;
    p.time_budget_ms = 3000;   // not about time (a loaded test PC)
    p.reader = VS_READER_FONT;
    p.print = VS_PRINT_SOLID;
    p.max_skew_deg = 8;
    vs_ocr* t = vs_ocr_create(&p);
    teach(t, {"0 1 2 3 4 5 9", "A E L O P T X /"}, false);
    vs_ocr_stats st;
    auto run = [&](const cv::Mat& img, const char* layout) {
        check(vs_ocr_set_layout(t, layout) == 0, std::string("layout set: ") + (layout ? layout : "none") + " " + vs_ocr_last_error(t));
        vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
        vs_ocr_get_stats(t, &st);
        return st.pass == 1;
    };
    const cv::Mat good = drawText({"LOT A12345", "EXP 09/25"}, 0, 0, false, false);
    check(run(good, "3 6 | 3 5") && *vs_ocr_layout_reason(t) == 0 && vs_ocr_line_layout(t, 0) == 0 && vs_ocr_line_layout(t, 1) == 1,
          "the block fits its layout: OK (" + readAll(t) + ")");
    vs_ocr_line first{};
    vs_ocr_get_line(t, 0, &first);
    check(first.checked == 1 && first.pass == 1 && std::string(first.expected) == "{?:3} {?:6}", std::string("a line read under its layout line: ") + first.expected);
    char buf[64];
    check(vs_ocr_get_layout(t, buf, sizeof buf) == 10 && std::string(buf) == "3 6 | 3 5", std::string("layout kept: ") + buf);
    check(!run(good, "3 7 | 3 5") && std::string(vs_ocr_layout_reason(t)).find("layout line 1") != std::string::npos,
          std::string("a character too few for the layout: NOT OK, ") + vs_ocr_layout_reason(t));
    check(!run(good, "3 6 | 3 5 | 4") && std::string(vs_ocr_layout_reason(t)).find("not found") != std::string::npos,
          std::string("a layout line not printed: NOT OK, ") + vs_ocr_layout_reason(t));
    check(!run(good, "4 5 | 3 5") && *vs_ocr_layout_reason(t) != 0, std::string("groups not as laid out: NOT OK, ") + vs_ocr_layout_reason(t));
    const cv::Mat split = drawText({"LOT            A12345", "EXP 09/25"}, 0, 0, false, false);
    run(split, nullptr);
    const int freeLines = vs_ocr_line_count(t);
    const bool withLayout = run(split, "3 6 | 3 5");
    check(freeLines == 3 && withLayout && vs_ocr_line_count(t) == 2 && extOf(t, 0).cut_short == 0,
          "a line split by a wide gap: " + std::to_string(freeLines) + " lines without the layout (each piece read as a line); one, whole and OK, with it (" +
          readAll(t) + ")");
    // learned at Teach from a good part
    run(good, nullptr);
    check(vs_ocr_learn_layout(t) == 2 && vs_ocr_get_layout(t, buf, sizeof buf) > 0 && std::string(buf) == "3 6 | 3 5",
          std::string("layout learned from a good part: ") + buf);
    const cv::Mat blank(300, 600, CV_8U, cv::Scalar(200));
    vs_ocr_run(t, blank.data, blank.cols, blank.rows, int32_t(blank.step));
    check(vs_ocr_learn_layout(t) < 0 && vs_ocr_get_layout(t, buf, sizeof buf) > 0 && std::string(buf) == "3 6 | 3 5",
          "nothing learned from a part that failed; the layout is kept");
    // Check text: the expected text must fit its layout line
    vs_ocr_set_expected(t, 0, "LOT A12345");
    vs_ocr_set_expected(t, 1, "EXP 09/25");
    check(run(good, "3 6 | 3 5"), "Check text fitting its layout: OK");
    check(!run(good, "3 5 | 3 5") && *vs_ocr_layout_reason(t) != 0, std::string("Check text whose expected text does not fit: NOT OK, ") + vs_ocr_layout_reason(t));
    check(!run(good, "3 6 | 3 5 | 2"), std::string("Check text with a layout line not found: NOT OK, ") + vs_ocr_layout_reason(t));
    // print beside a checked line, joined in by the layout but holding no character of it (a smear, a ghost
    // of letters the font does not know): NOT OK, as the line cut short is without the layout
    const cv::Mat ghost = drawText({"LOT A12345            MWM", "EXP 09/25"}, 0, 0, false, false);
    check(!run(ghost, "3 6 | 3 5"), "Check text: unread print joined in beside the line: NOT OK (" + readAll(t) + ")");
    check(vs_ocr_learn_print(t) < 0, "and such a part is never learned as a good part");
    vs_ocr_clear_expected(t);
    check(!run(ghost, "3 6 | 3 5"), std::string("Read text: the same: NOT OK, ") + vs_ocr_layout_reason(t));
    check(vs_ocr_set_layout(t, "2 x") < 0 && vs_ocr_set_layout(t, "") == 0 && vs_ocr_get_layout(t, buf, sizeof buf) == 1,
          "a layout that does not parse is refused; an empty one clears it");
    vs_ocr_destroy(t);
}

// Dot print spaced tight (vs_ocr_set_tight_spacing): characters closer than one empty dot column, so joining
// dots joins characters. With tight spacing on, the taught font finds the pitch on dots not joined along the
// line and still reads every character; a wrong character is still NOT OK
void testTightDots()
{
    int okOf[2] = {0, 0};
    for (const int tight : {0, 1}) {
        vs_ocr_params p;
        vs_ocr_default_params(&p);
        p.reader = VS_READER_FONT;
        p.print = VS_PRINT_DOT;
        p.char_height_px = 7 * 6;
        p.time_budget_ms = 3000;   // not about time (a loaded test PC)
        vs_ocr* f = vs_ocr_create(&p);
        vs_ocr_set_tight_spacing(f, tight);
        const std::vector<std::string> taughtText = {"0123456789", "ABELOPTX/"};
        for (const unsigned seed : {11u, 12u}) {
            const cv::Mat timg = drawDots(taughtText, 0, 6, 0.08, seed, 0, {}, 5.4);
            for (int i = 0; i < 2; ++i) vs_ocr_teach_font(f, timg.data, timg.cols, timg.rows, int32_t(timg.step), i, taughtText[size_t(i)].c_str());
        }
        vs_ocr_set_expected(f, 0, "LOT A12345");
        vs_ocr_stats st;
        for (unsigned seed = 30; seed < 35; ++seed) {
            const cv::Mat img = drawDots({"LOT A12345"}, 1, 6, 0.05, seed, 0, {}, 5.4);
            vs_ocr_run(f, img.data, img.cols, img.rows, int32_t(img.step));
            vs_ocr_get_stats(f, &st);
            okOf[tight] += st.pass;
            if (!st.pass && std::getenv("OCR_TEST_PRINT")) std::printf("     tight %d: %s %s\n", tight, readAll(f).c_str(), checkDetail(f).c_str());
        }
        if (tight) {
            vs_ocr_set_expected(f, 0, "LOT A12346");
            const cv::Mat img = drawDots({"LOT A12345"}, 1, 6, 0.05, 30, 0, {}, 5.4);
            vs_ocr_run(f, img.data, img.cols, img.rows, int32_t(img.step));
            vs_ocr_get_stats(f, &st);
            check(st.pass == 0 && vs_ocr_get_tight_spacing(f) == 1, "tight dot print: a wrong character is NOT OK");
        }
        vs_ocr_destroy(f);
    }
    check(okOf[1] >= 4 && okOf[1] >= okOf[0], "dot print spaced tight: " + std::to_string(okOf[1]) + " of 5 OK with tight spacing, " +
          std::to_string(okOf[0]) + " without");
}


// Bad input through the C API, found by the fuzzers (tests/fuzz, scripts/fuzz-engine.sh, PR #174). Each
// used to crash, hang or run into undefined behaviour (seen under the sanitizer build, engine/build-asan).
void testBadApiInput()
{
    const auto since = [](std::chrono::steady_clock::time_point t0) {
        return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count();
    };
    const std::string dir = OCR_MODEL_DIR, onnx = dir + "/ocrnet.onnx", cs = dir + "/ocr_charset.txt";
    const bool model = bool(std::ifstream(onnx));
    if (!model) std::printf("skip  bad input runs: %s not found\n", onnx.c_str());
    const auto withModel = [&](vs_ocr* h) { if (model) vs_ocr_load_model(h, onnx.c_str(), cs.c_str()); return h; };
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    vs_ocr* t = withModel(vs_ocr_create(&p));
    // date and time fields: offsets and tolerances overflowed (days * 86400), a tolerance of 10^11
    // minutes was 10^11 alternatives, and std::stol's out_of_range left the C API
    const auto t0 = std::chrono::steady_clock::now();
    check(vs_ocr_set_expected(t, 0, "{DATE:YYYY+9223372036854775807d}") < 0, "date offset 2^63 days: refused");
    check(vs_ocr_set_expected(t, 0, "{DATE:YY+99999999999999999999d}") < 0, "date offset of 20 digits: refused");
    check(vs_ocr_set_expected(t, 0, "{TIME:HH:mm+-99999999999}") < 0 && since(t0) < 1000, "time tolerance 10^11 minutes: refused at once");
    check(vs_ocr_set_expected(t, 0, "{DATE:DD/MM/YY+-367}") < 0, "date tolerance 367 days: refused");
    check(vs_ocr_set_expected(t, 0, "{DATE:DD/MM/YY+36525d+-366}") == 0, "date offset 100 years, tolerance a year: accepted");
    check(vs_ocr_set_expected(t, 0, "{TIME:HH:mm+-1440}") == 0, "time tolerance a day: accepted");
    // a reference clock beyond any date: the check runs and fails, it does not overflow
    vs_ocr_set_clock(t, std::numeric_limits<int64_t>::max());
    cv::Mat img(120, 400, CV_8U, cv::Scalar(220));
    cv::putText(img, "12/26", {20, 80}, cv::FONT_HERSHEY_SIMPLEX, 1.5, cv::Scalar(20), 3);
    if (model) check(vs_ocr_run(t, img.data, img.cols, img.rows, int(img.step)) < 0, "clock 2^63 s: the run reports an error (NG)");
    vs_ocr_set_clock(t, 0);
    // line + 1 in an error message overflowed
    check(vs_ocr_teach_font(t, img.data, img.cols, img.rows, int(img.step), std::numeric_limits<int32_t>::max(), "12/26") < 0,
          "teach line INT_MAX: refused");
    vs_ocr_destroy(t);

    // dot print with the largest dot size and join on small letters: a closing disc of 1441 px over the
    // whole image (42 s before the radius was held to twice the letter height)
    vs_ocr_default_params(&p);
    p.print = VS_PRINT_DOT;
    p.dot_size_px = 200.f;
    p.join_strength = 3.f;
    p.char_height_px = 10;
    t = vs_ocr_create(&p);
    cv::Mat noise(1200, 1600, CV_8U);
    cv::randu(noise, 0, 256);
    auto t1 = std::chrono::steady_clock::now();
    vs_ocr_teach_font(t, noise.data, noise.cols, noise.rows, int(noise.step), 0, "12/26");
    check(VS_TIME_BUDGET(since(t1) < 5000), "dot size 200 px, join 3, letters 10 px high: the line finder ends (" + std::to_string(since(t1)) + " ms)");
    vs_ocr_destroy(t);

    // noise turned a little (smoothed into specks of a letter's size), read as dot print with 8 px
    // letters: every speck a letter to follow over a 15 MP image (minutes, found by the fuzzer). The line
    // finder now stops at 10 times the time budget, at least a second, and finds no line
    vs_ocr_default_params(&p);
    p.print = VS_PRINT_DOT;
    p.char_height_px = 8;
    p.time_budget_ms = 100;
    t = vs_ocr_create(&p);
    cv::Mat big(3648, 4000, CV_8U), turned;
    cv::randu(big, 0, 256);
    const double a = 7 * CV_PI / 180.0, ca = std::cos(a), sa = std::sin(a), cx = big.cols / 2.0, cy = big.rows / 2.0;
    cv::warpAffine(big, turned, cv::Matx23d(ca, sa, cx - ca * cx - sa * cy, -sa, ca, cy + sa * cx - ca * cy), big.size(),
                   cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    t1 = std::chrono::steady_clock::now();
    const int32_t taught = vs_ocr_teach_font(t, turned.data, turned.cols, turned.rows, int(turned.step), 0, "12/26");
    check(VS_TIME_BUDGET(since(t1) < 5000) && taught < 0, "turned noise, dot print, letters 8 px: the line finder stops, nothing taught (" +
                                          std::to_string(since(t1)) + " ms)");
    vs_ocr_destroy(t);

    // NaN settings: std::clamp passes NaN; the pass limit takes the strictest value, never a looser one
    vs_ocr_default_params(&p);
    p.min_quality = p.max_skew_deg = std::numeric_limits<float>::quiet_NaN();
    t = withModel(vs_ocr_create(&p));
    if (model) check(vs_ocr_run(t, img.data, img.cols, img.rows, int(img.step)) == 0, "NaN min quality and skew: runs");
    vs_ocr_destroy(t);
}

} // namespace

// Capital height estimate (no height in the job) on embossed print, where a letter shows only as light
// and dark edges of its strokes, on a ground with uneven light; and on dot print the job names, where
// the estimate is the dot-centre height of the printer's matrix, not one dot higher
void testHeightEstimate()
{
    for (const int cap : {24, 34}) {
        cv::Mat ink8(420, 900, CV_8U, cv::Scalar(0)), ink;
        const double scale = cap / 22.0;
        const char* lines[] = {"LOT A12345", "EXP 09/28"};
        for (int i = 0; i < 2; ++i)   // a letter at a time, a little apart as stamped
            for (int k = 0; lines[i][k]; ++k)
                cv::putText(ink8, std::string(1, lines[i][k]), {120 + int(k * cap * 0.95), 150 + int(i * cap * 1.9)},
                            cv::FONT_HERSHEY_SIMPLEX, scale, 255, std::max(2, cap / 8), cv::LINE_AA);
        ink8.convertTo(ink, CV_32F, 1.0 / 255);
        cv::GaussianBlur(ink, ink, {0, 0}, 1.5);
        cv::Mat gx, gy;
        cv::Sobel(ink, gx, CV_32F, 1, 0);
        cv::Sobel(ink, gy, CV_32F, 0, 1);
        cv::Mat f = 0.6 * gx + 0.8 * gy;   // lit from above and a little from the side
        double lo, hi;
        cv::minMaxLoc(cv::abs(f), &lo, &hi);
        f = f * (25.0 / hi);
        for (int x = 0; x < f.cols; ++x) f.col(x) += 120.0 + 30.0 * x / f.cols;
        cv::Mat noise(f.size(), CV_32F);
        cv::theRNG().state = 7;
        cv::randn(noise, 0, 2.0);
        f += noise;
        cv::Mat img;
        f.convertTo(img, CV_8U);
        ocr::FindParams fp;
        fp.roi = cv::Rect(0, 0, img.cols, img.rows);
        const float h = ocr::estimateCharHeight(img, fp);
        check(h > 0.8f * cap && h < 1.25f * cap, "embossed letter height estimate " + std::to_string(h) + " for " + std::to_string(cap) + " px capitals");
    }
    // 5 x 7 dots at a pitch of 6 px: 36 px from the top to the bottom dot centre
    cv::Mat img = drawDots({"LOT 12345", "EXP 09/28"}, 0, 6, 0.1, 5);
    ocr::FindParams fp;
    fp.roi = cv::Rect(0, 0, img.cols, img.rows);
    fp.print = VS_PRINT_DOT;
    const float h = ocr::estimateCharHeight(img, fp);
    check(h > 0.85f * 36 && h < 1.12f * 36, "dot print letter height estimate " + std::to_string(h) + " for 36 px between dot centres");
}

// The ends of a line (real laser print on the Harpic line, 2026-10-30): light print in a dark box with
// the label's bright margin just past the line's end, a faint letter past the end, a line bowed round
// a bottle. Drawn as drawText draws light print: ground 60, ink +40.
void testLineEnds()
{
    const int cap = 30;
    const double scale = cap / 22.0;
    const int thick = std::max(1, cap / 10);
    const std::string text = "EXP 09/28 16:59";
    int base = 0;
    const int tw = cv::getTextSize(text, cv::FONT_HERSHEY_SIMPLEX, scale, thick, &base).width;
    // ink (0..255) to a camera image: dark box ground 60, light print, noise
    auto camera = [&](const cv::Mat& ink, float inkLevel, int marginX) {
        cv::Mat f(ink.size(), CV_32F, cv::Scalar(60));
        cv::Mat i32;
        ink.convertTo(i32, CV_32F, inkLevel / 255.0);
        f += i32;
        if (marginX > 0) {   // the label's bright margin past the box's edge, then the dark bottle
            f(cv::Rect(marginX, 0, std::min(40, f.cols - marginX), f.rows)).setTo(150);
            if (marginX + 40 < f.cols) f(cv::Rect(marginX + 40, 0, f.cols - marginX - 40, f.rows)).setTo(20);
        }
        cv::GaussianBlur(f, f, {0, 0}, 2.0);   // the camera's focus: the edge is a soft ramp
        cv::Mat noise(f.size(), CV_32F);
        cv::randn(noise, 0, 2.0);
        f += noise;
        cv::Mat img;
        f.convertTo(img, CV_8U);
        return img;
    };
    auto find = [&](const cv::Mat& img) {
        ocr::FindParams fp;
        fp.roi = cv::Rect(0, 0, img.cols, img.rows);
        fp.charHeight = float(cap);
        return ocr::findLines(img, fp, false);
    };
    cv::Mat ink(420, 900, CV_8U, cv::Scalar(0));
    cv::putText(ink, text, {120, 150}, cv::FONT_HERSHEY_SIMPLEX, scale, 255, thick, cv::LINE_AA);
    int inkEnd = 0;   // the last letter's last column
    for (int x = 0; x < ink.cols; ++x) if (cv::countNonZero(ink.col(x)) > 0) inkEnd = x;
    // the box edge about a capital past the end: a region border, not more print
    for (const double gap : {1.0, 1.2}) {
        const auto lines = find(camera(ink, 40, inkEnd + int(gap * cap)));
        check(lines.size() == 1 && !lines[0].cutShort,
              "a line ending " + std::to_string(gap).substr(0, 3) + " capitals before the box's edge is whole (" +
                  std::to_string(lines.size()) + " lines" + (lines.size() == 1 && lines[0].cutShort ? ", cut short)" : ")"));
    }
    // the edge right after the last letter: a next letter could be under it
    {
        const auto lines = find(camera(ink, 40, inkEnd + int(0.2 * cap)));
        check(lines.size() == 1 && lines[0].cutShort, "a line running into the box's edge is cut short");
    }
    // a faint letter after the end (a quarter of the print's contrast): held in the line's image, or the
    // line is cut short; never dropped from a line that reads as whole
    {
        const int w0 = int(find(camera(ink, 40, 0)).at(0).image.cols);
        cv::Mat faint(ink.size(), CV_8U, cv::Scalar(0));
        const int adv = cv::getTextSize("0", cv::FONT_HERSHEY_SIMPLEX, scale, thick, &base).width;
        cv::putText(faint, "8", {120 + tw + adv / 3, 150}, cv::FONT_HERSHEY_SIMPLEX, scale, 255, thick, cv::LINE_AA);
        cv::Mat both = ink + faint / 4;
        const auto lines = find(camera(both, 40, 0));
        const bool held = lines.size() == 1 && (lines[0].cutShort || lines[0].image.cols >= w0 + 8);
        check(held, "a faint letter past the end is held in the line image (" + std::to_string(w0) + " -> " +
                        (lines.empty() ? std::string("none") : std::to_string(lines[0].image.cols)) + " px) or the line is cut short");
    }
    // bowed round a bottle: the last letters 0.3 capitals higher than the first. The band holds them:
    // no ink in the top or bottom two rows of the line image's right quarter
    {
        cv::Mat bowed(ink.size(), CV_8U, cv::Scalar(0));
        int x = 120;
        for (char c : text) {
            const std::string one(1, c);
            const double u = double(x - 120) / tw;
            cv::putText(bowed, one, {x, 150 - int(std::lround(0.3 * cap * u * u))}, cv::FONT_HERSHEY_SIMPLEX, scale, 255, thick, cv::LINE_AA);
            x += cv::getTextSize(one, cv::FONT_HERSHEY_SIMPLEX, scale, thick, &base).width;
        }
        const auto lines = find(camera(bowed, 40, 0));
        bool clear = lines.size() == 1;
        if (clear) {
            const cv::Mat& li = lines[0].image;
            const cv::Rect q(li.cols * 3 / 4, 0, li.cols - li.cols * 3 / 4, li.rows);
            double lo = 255;
            for (int r : {0, 1, li.rows - 2, li.rows - 1}) {
                double m;
                cv::minMaxLoc(li(q).row(r), &m);
                lo = std::min(lo, m);
            }
            clear = lo > 110;   // text dark on light: no stroke reaches the edge rows
        }
        check(clear, "a bowed line keeps its end letters inside its band");
    }
}

// ocrnet is encrypted at rest (AES-256-GCM, content key from the licence; LICENSING.md 7.4): the host
// decrypts it into memory and loads from the buffer, so no plaintext model is on the shipped disk.
// Check the buffer load reads the same drawn lines the file load does, and that a bad/empty buffer is
// refused without leaving a model loaded (line safety: the tool stays NG, never reads without the model).
void testModelFromBuffer()
{
    const std::string dir = OCR_MODEL_DIR;
    const std::string onnx = dir + "/ocrnet.onnx", cs = dir + "/ocr_charset.txt";
    // Negative cases run even without a model on disk.
    {
        vs_ocr_params p;
        vs_ocr_default_params(&p);
        vs_ocr* t = vs_ocr_create(&p);
        check(vs_ocr_load_model_buffer(t, nullptr, 0, cs.c_str()) == -1, "vs_ocr_load_model_buffer rejects a null buffer");
        const uint8_t junk[8] = {1, 2, 3, 4, 5, 6, 7, 8};
        check(vs_ocr_load_model_buffer(t, junk, sizeof junk, cs.c_str()) < 0, "vs_ocr_load_model_buffer rejects non-ONNX bytes");
        check(vs_ocr_load_second_model_buffer(t, nullptr, 0, cs.c_str()) == 0, "a null second-model buffer unloads it (0)");
        vs_ocr_destroy(t);
    }
    std::ifstream mf(onnx, std::ios::binary);
    if (!mf) {
        std::printf("skip  load-from-buffer model tests: %s not found\n", onnx.c_str());
        return;
    }
    std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(mf)), std::istreambuf_iterator<char>());
    vs_ocr_params p;
    vs_ocr_default_params(&p);
    p.char_height_px = 30;
    p.time_budget_ms = 500;
    vs_ocr* t = vs_ocr_create(&p);
    check(vs_ocr_load_model_buffer(t, bytes.data(), bytes.size(), cs.c_str()) == 0, "ocrnet loads from a buffer");
    cv::Mat img = drawText({"BATCH HWN937", "EXP 09/28"}, 4, 5, false, false);
    vs_ocr_set_expected(t, 0, "BATCH HWN937");
    vs_ocr_set_expected(t, 1, "EXP 09/28");
    vs_ocr_run(t, img.data, img.cols, img.rows, int32_t(img.step));
    vs_ocr_stats st;
    vs_ocr_get_stats(t, &st);
    check(st.pass == 1, "a buffer-loaded ocrnet checks the drawn lines OK, same as the file load");

    // A truncated buffer must fail and must not leave a usable model behind.
    std::vector<uint8_t> trunc(bytes.begin(), bytes.begin() + bytes.size() / 2);
    vs_ocr* t2 = vs_ocr_create(&p);
    check(vs_ocr_load_model_buffer(t2, trunc.data(), trunc.size(), cs.c_str()) < 0, "a truncated ocrnet buffer is refused");
    vs_ocr_destroy(t2);
    vs_ocr_destroy(t);
}

int main()
{
    if (const char* only = std::getenv("OCR_TEST_ONLY"); only && std::string(only) == "layout") {
        testLayout();
        testTightDots();
        std::printf("\n%s (%d failed)\n", failures ? "FAILED" : "all passed", failures);
        return failures ? 1 : 0;
    }
    if (const char* only = std::getenv("OCR_TEST_ONLY"); only && std::string(only) == "shift") {
        testShift();
        testDotsAndInkjet();
        testCutShort();
        std::printf("\n%s (%d failed)\n", failures ? "FAILED" : "all passed", failures);
        return failures ? 1 : 0;
    }
    testPatterns();
    testCheck();
    testFormat();
    testLines();
    testModel();
    testModelFromBuffer();
    testPitch();
    testFont();
    // both draw camera noise from OpenCV's shared generator and were tuned right after testFont:
    // each starts from that same state (their results depend on the noise drawn)
    const uint64_t noise = cv::theRNG().state;
    if (const char* seed = std::getenv("OCR_TEST_SEED")) cv::theRNG().state = std::strtoull(seed, nullptr, 10);
    testPrintQuality();
    testPrintPosition();
    cv::theRNG().state = noise;
    testFontTeaching();
    testShift();
    testDotsAndInkjet();
    testCutShort();
    testAgreement();
    testHeightEstimate();
    testLineEnds();
    testMargin();
    testLayout();
    testTightDots();
    testBadApiInput();
    std::printf("\n%s (%d failed)\n", failures ? "FAILED" : "all passed", failures);
    return failures ? 1 : 0;
}
