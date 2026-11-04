// dpm_tests - direct part mark grading after ISO/IEC TR 29158 (grade_dpm.cpp) on synthetic marks:
// laser-etched (solid cells, dark on light and light on dark), dot-peened (round dots with gaps),
// low contrast, a damaged L side, the reflectance calibration, and the C API; the codeword map
// (codewords.cpp) against real encoders, and cell modulation graded per codeword and block.
// Run: build/dpm_tests (exit code 0 = all passed).

#include "codereader.h"
#include "reader.hpp"

#include "qrcodegen.hpp"

#ifdef CR_HAVE_DMTX
#include <dmtx.h>
#endif

#include <opencv2/imgproc.hpp>

#include <array>
#include <cmath>
#include <cstdio>
#include <random>
#include <string>

namespace {

int failures = 0;

void check(bool ok, const std::string& what)
{
    std::printf("%s %s\n", ok ? "  ok  " : "  FAIL", what.c_str());
    if (!ok) ++failures;
}

enum class Mark { Laser, Dots };

struct Drawn {
    cv::Mat img;
    cr::Code code;
};

// A symbol from its grid (1 = mark), px pixels a cell, `quiet` cells of background round it. Laser:
// solid square cells; dots: a round dot of `dot` x the cell per mark cell. Camera blur and noise.
Drawn draw(const std::vector<uint8_t>& m, int w, int format, int px, int markGrey, int backGrey, Mark kind, float dot = 0.75f,
           int quiet = 3, unsigned seed = 29158)
{
    Drawn d;
    const int side = (w + 2 * quiet) * px;
    cv::Mat img(side, side, CV_32F, cv::Scalar(backGrey));
    for (int y = 0; y < w; ++y)
        for (int x = 0; x < w; ++x) {
            if (!m[static_cast<size_t>(y) * w + x]) continue;
            const float x0 = static_cast<float>((quiet + x) * px), y0 = static_cast<float>((quiet + y) * px);
            if (kind == Mark::Laser)
                cv::rectangle(img, cv::Point2f(x0, y0), cv::Point2f(x0 + px - 0.01f, y0 + px - 0.01f), cv::Scalar(markGrey), -1);
            else
                cv::circle(img, cv::Point(static_cast<int>(x0 + px / 2.f), static_cast<int>(y0 + px / 2.f)),
                           static_cast<int>(std::lround(dot * px / 2.f)), cv::Scalar(markGrey), -1, cv::LINE_AA);
        }
    cv::GaussianBlur(img, img, cv::Size(), 0.8);
    cv::Mat noise(img.size(), CV_32F);
    cv::RNG rng(seed);
    rng.fill(noise, cv::RNG::NORMAL, 0, 3);
    img += noise;
    img.convertTo(d.img, CV_8U);
    const float a = static_cast<float>(quiet * px), b = a + static_cast<float>(w * px);
    const float xs[4] = {a, b, b, a}, ys[4] = {a, a, b, b};
    for (int k = 0; k < 4; ++k) { d.code.x[k] = xs[k]; d.code.y[k] = ys[k]; }
    d.code.format = format;
    d.code.modules = m;
    d.code.modW = d.code.modH = w;
    return d;
}

void print(const char* what, const cr::DpmQuality& q)
{
    std::printf("     %s: ML %.0f%% CC %.2f(%d) CM %.2f(%d) MR %.1f(%d) FPD %.2f(%d) DD %.2f AN %d GN %d UEC %d inv %d close %.0f -> %d "
                "(50%%: %d, 80%%: %d)\n",
                what, q.meanLightPct, q.cellContrast, q.gradeCC, q.cellModulation, q.gradeCM, q.minReflectance, q.gradeMR,
                q.fixedPatternDamage, q.gradeFPD, q.distributedDamage, q.gradeAN, q.gradeGN, q.gradeUEC, q.inverted ? 1 : 0, q.closingPx,
                q.overall, q.overall50, q.overall80);
}

void testDataMatrix()
{
    using namespace cr;
    std::vector<uint8_t> dm;
    check(encodeDm("PN 4711-0815 SN 000123", 22, dm), "encode a 22 x 22 Data Matrix");
    const int w = 22, px = 10;
    DpmSettings s;
    s.enabled = true;

    // laser-etched, dark marks on a bright part, mean light about 78 % of full scale
    const Drawn laser = draw(dm, w, CR_DATAMATRIX, px, 45, 200, Mark::Laser);
    const DpmQuality q = gradeDpm(laser.img, laser.code, s);
    print("laser dark on light", q);
    check(q.measured && q.overall == 4 && q.gradeCC == 4 && q.gradeCM == 4 && q.gradeFPD == 4 && q.gradeDecode == 4,
          "laser-etched Data Matrix grades A");
    check(!q.inverted && q.meanLightOk && q.aperturePct == 80 && q.closingPx == 0.f && q.distributedDamage == 4.f,
          "dark marks, mean light in 70..86 %, 80 % aperture, no dot closing on solid cells");
    check(q.gradeMR == -1 && q.minReflectance < 0 && !q.calibrated, "without the reflectance calibration minimum reflectance is not measured");

    // the same mark light on a dark part (laser on anodised aluminium)
    const Drawn light = draw(dm, w, CR_DATAMATRIX, px, 205, 50, Mark::Laser);
    const cv::Mat before = light.img.clone();
    const DpmQuality qi = gradeDpm(light.img, light.code, s);
    check(cv::countNonZero(before != light.img) == 0, "grading never writes into the caller's image");
    print("laser light on dark", qi);
    check(qi.measured && qi.inverted && qi.overall == 4 && qi.meanLightPct > 70.f, "light marks on a dark part: polarity found, grades A");

    // dot peen: round dots of 0.7 cell, gaps between them along the L sides
    const Drawn dots = draw(dm, w, CR_DATAMATRIX, 12, 60, 190, Mark::Dots, 0.7f);
    const DpmQuality qd = gradeDpm(dots.img, dots.code, s);
    DpmSettings open = s;
    open.closing = -1;
    const DpmQuality qo = gradeDpm(dots.img, dots.code, open);
    print("dot peen, closed", qd);
    print("dot peen, not closed", qo);
    check(qd.measured && qd.closingPx > 0.f && qo.closingPx == 0.f, "dot peen: the gaps between dots are found and closed");
    // closing darkens the light cells next to the dots a little: the grade must hold, not rise
    check(qd.overall >= 3 && qd.overall >= qo.overall && std::abs(qd.cellContrast - qo.cellContrast) < 0.05f && qd.gradeFPD >= 3,
          "dot peen: closed, it grades A or B, no lower than unclosed");

    // low contrast (a shallow etch): CC (ML - MD) / ML under 0.20 grades D or F
    const Drawn faint = draw(dm, w, CR_DATAMATRIX, px, 168, 200, Mark::Laser);
    const DpmQuality qf = gradeDpm(faint.img, faint.code, s);
    print("faint", qf);
    check(qf.measured && qf.gradeCC <= 1 && qf.overall <= 1, "a faint mark grades D or F on cell contrast");

    // damaged L side: a scratch takes 5 cells of the left column out
    Drawn hurt = draw(dm, w, CR_DATAMATRIX, px, 45, 200, Mark::Laser);
    cv::rectangle(hurt.img, cv::Rect(3 * px, (3 + 6) * px, px, 5 * px), cv::Scalar(200), -1);
    const DpmQuality qh = gradeDpm(hurt.img, hurt.code, s);
    print("scratched L", qh);
    check(qh.measured && qh.gradeFPD <= 1 && qh.distributedDamage < 4.f && qh.fixedPatternDamage > 0.f && qh.overall <= 1,
          "5 of 22 L-side cells lost: fixed pattern damage D or F, distributed damage under A");

    // the image-based reflectance calibration: Rtarget = Rcal x SRcal / SRtarget x ML / MLcal
    DpmSettings cal = s;
    cal.rCal = 80.f;
    cal.mlCal = q.meanLight;
    cal.srCal = 1.f;
    cal.srTarget = 4.f; // the part needed 4 x the exposure to reach the same mean light
    const DpmQuality qc = gradeDpm(laser.img, laser.code, cal);
    cal.srTarget = 10.f;
    const DpmQuality qc10 = gradeDpm(laser.img, laser.code, cal);
    cal.srTarget = 0.f; // the same exposure as the calibration
    const DpmQuality qc1 = gradeDpm(laser.img, laser.code, cal);
    std::printf("     minimum reflectance: SR x4 %.1f%% (%d), x10 %.1f%% (%d), x1 %.1f%% (%d)\n", qc.minReflectance, qc.gradeMR,
                qc10.minReflectance, qc10.gradeMR, qc1.minReflectance, qc1.gradeMR);
    check(qc.calibrated && std::abs(qc.minReflectance - 20.f) < 0.5f && qc.gradeMR >= 3, "a dark part at 4 x exposure: Rtarget 20 %");
    check(std::abs(qc10.minReflectance - 8.f) < 0.3f && qc10.gradeMR == 1 && qc10.overall <= 1, "at 10 x exposure: Rtarget 8 %, D, and the overall with it");
    check(std::abs(qc1.minReflectance - 80.f) < 1.f && qc1.gradeMR == 4, "no system response given: the calibration's own");

    // over-exposed: mean light over 86 % is flagged for a new exposure
    const Drawn bright = draw(dm, w, CR_DATAMATRIX, px, 60, 245, Mark::Laser);
    const DpmQuality qb = gradeDpm(bright.img, bright.code, s);
    check(qb.measured && !qb.meanLightOk && qb.meanLightPct > 86.f, "mean light over 86 % of full scale is flagged");

    // apertures: either as set, or the better of the two
    DpmSettings a50 = s, best = s;
    a50.aperture = 50;
    best.aperture = 0;
    const DpmQuality q50 = gradeDpm(dots.img, dots.code, a50), qbest = gradeDpm(dots.img, dots.code, best);
    check(q50.aperturePct == 50 && qbest.overall == std::max(qbest.overall50, qbest.overall80), "the 50 % aperture as set; 0 takes the better grade");

    // not graded: a code without a grid, a 1D code, and cells under 1.5 px
    Code none = laser.code;
    none.modules.clear();
    check(!gradeDpm(laser.img, none, s).measured, "no cell grid: not graded");
    Code tiny = laser.code;
    for (int k = 0; k < 4; ++k) { tiny.x[k] = 30 + (tiny.x[k] - 30) / 8.f; tiny.y[k] = 30 + (tiny.y[k] - 30) / 8.f; }
    check(!gradeDpm(laser.img, tiny, s).measured, "cells under 1.5 px: not graded");
    Code aztec = laser.code;
    aztec.format = CR_AZTEC;
    check(!gradeDpm(laser.img, aztec, s).measured, "Aztec: not a TR 29158 symbology here, not graded");
}

void testQr()
{
    using namespace cr;
    const qrcodegen::QrCode code = qrcodegen::QrCode::encodeText("PART 4711-0815", qrcodegen::QrCode::Ecc::MEDIUM);
    const int w = code.getSize();
    std::vector<uint8_t> m(static_cast<size_t>(w) * w);
    for (int y = 0; y < w; ++y)
        for (int x = 0; x < w; ++x) m[static_cast<size_t>(y) * w + x] = code.getModule(x, y) ? 1 : 0;
    DpmSettings s;
    s.enabled = true;
    Drawn d = draw(m, w, CR_QR, 8, 50, 195, Mark::Laser, 0.f, 4);
    d.code.ecLevel = "M";
    const DpmQuality q = gradeDpm(d.img, d.code, s);
    print("QR laser", q);
    check(q.measured && q.overall == 4 && q.gradeFPD == 4, "laser-etched QR grades A");
    // three cells of the top-left finder's ring gone
    Drawn hurt = draw(m, w, CR_QR, 8, 50, 195, Mark::Laser, 0.f, 4);
    hurt.code.ecLevel = "M";
    cv::rectangle(hurt.img, cv::Rect(4 * 8, 4 * 8, 3 * 8, 8), cv::Scalar(195), -1);
    const DpmQuality qh = gradeDpm(hurt.img, hurt.code, s);
    print("QR finder hit", qh);
    check(qh.measured && qh.gradeFPD <= 1 && qh.distributedDamage < 4.f, "three finder cells lost: fixed pattern damage D or F");
}

void testCApi()
{
    cr_dpm_params p;
    cr_default_dpm_params(&p);
    check(p.struct_size == static_cast<int32_t>(sizeof(cr_dpm_params)) && p.enabled == 0 && p.aperture == 80 && p.lighting == CR_DPM_LIGHT_90,
          "cr_default_dpm_params: off, 80 % aperture, 90 lighting");
    cr_params rp;
    cr_default_params(&rp);
    rp.format = CR_DATAMATRIX;
    rp.time_budget_ms = 2000;
    cr_reader* r = cr_create(&rp);
    check(r != nullptr, "reader created");
    cr_dpm_params bad = p;
    bad.aperture = 60;
    check(cr_set_dpm(r, &bad) == -1, "an aperture other than 0, 50 or 80 is refused");
    bad = p;
    bad.struct_size = 0;
    check(cr_set_dpm(r, &bad) == -1, "struct_size not set is refused");
    bad = p;
    bad.r_cal = 120.f;
    check(cr_set_dpm(r, &bad) == -1, "a reflectance over 100 % is refused");

    std::vector<uint8_t> dm;
    cr::encodeDm("PN 4711-0815 SN 000123", 22, dm);
    const Drawn d = draw(dm, 22, CR_DATAMATRIX, 10, 45, 200, Mark::Laser, 0.f, 6);
    // grading off: a code read, measured 0
    const int n0 = cr_decode(r, d.img.data, d.img.cols, d.img.rows, static_cast<int32_t>(d.img.step));
    cr_dpm out{};
    out.struct_size = sizeof(cr_dpm);
    check(n0 == 1 && cr_get_dpm(r, 0, &out) == 0 && out.measured == 0 && out.overall == -1, "DPM grading off: not measured");
    p.enabled = 1;
    p.lighting = CR_DPM_LIGHT_30Q;
    p.aperture = 0;
    check(cr_set_dpm(r, &p) == 0, "cr_set_dpm accepts the defaults with grading on");
    const int n = cr_decode(r, d.img.data, d.img.cols, d.img.rows, static_cast<int32_t>(d.img.step));
    out = cr_dpm{};
    out.struct_size = sizeof(cr_dpm);
    check(n == 1 && cr_get_dpm(r, 0, &out) == 0 && out.measured == 1 && out.overall == 4 && out.lighting == CR_DPM_LIGHT_30Q &&
              (out.aperture == 50 || out.aperture == 80) && out.grade_mr == -1,
          "a decoded Data Matrix graded A through cr_get_dpm, its lighting recorded");
    // an older caller's smaller struct: only its bytes are written
    struct Old { int32_t struct_size, measured, calibrated; int32_t guard; } old{12, 0, 0, 0x5A5A5A5A};
    check(cr_get_dpm(r, 0, reinterpret_cast<cr_dpm*>(&old)) == 0 && old.measured == 1 && old.guard == 0x5A5A5A5A,
          "a smaller cr_dpm gets only the bytes its struct_size covers");
    check(cr_get_dpm(r, 1, &out) == -1, "a bad index is refused");
    cr_destroy(r);
}

// Reed-Solomon syndromes of one block (codewords high degree first) over GF(256) with this field
// polynomial, roots alpha^first .. alpha^(first + ecc - 1): all zero for a valid block
bool rsValid(const std::vector<int>& block, int ecc, int poly, int first)
{
    std::array<int, 512> exp{};
    std::array<int, 256> log{};
    for (int i = 0, x = 1; i < 255; ++i) {
        exp[static_cast<size_t>(i)] = exp[static_cast<size_t>(i + 255)] = x;
        log[static_cast<size_t>(x)] = i;
        x <<= 1;
        if (x & 0x100) x ^= poly;
    }
    for (int j = 0; j < ecc; ++j) {
        const int root = exp[static_cast<size_t>((first + j) % 255)];
        int y = 0;
        for (int v : block) y = (y ? exp[static_cast<size_t>(log[static_cast<size_t>(y)] + log[static_cast<size_t>(root)])] : 0) ^ v;
        if (y) return false;
    }
    return true;
}

// the codewords as the map reads them from a grid (1 = dark), sorted into their blocks; every
// block must be a valid Reed-Solomon word
bool mapReadsValidBlocks(const std::vector<uint8_t>& grid, const cr::CodewordMap& m, int poly, int first, std::vector<int>* stream = nullptr)
{
    std::vector<std::vector<int>> blocks(m.blockEcc.size());
    std::vector<std::vector<int>> checks(m.blockEcc.size());
    for (size_t k = 0; k < m.cells.size(); ++k) {
        int v = 0;
        for (int i : m.cells[k]) v = v << 1 | grid[static_cast<size_t>(i)];
        if (stream) stream->push_back(v);
        const auto b = static_cast<size_t>(m.block[k]);
        (static_cast<int>(blocks[b].size()) < m.blockData[b] ? blocks[b] : checks[b]).push_back(v);
    }
    for (size_t b = 0; b < blocks.size(); ++b) {
        if (static_cast<int>(checks[b].size()) != m.blockEcc[b]) return false;
        blocks[b].insert(blocks[b].end(), checks[b].begin(), checks[b].end());
        if (!rsValid(blocks[b], m.blockEcc[b], poly, first)) return false;
    }
    return true;
}

void testCodewordMap()
{
    using namespace cr;
    using qrcodegen::QrCode;
    // QR: every version and EC level; the data modules unmasked (mask 0: (x + y) even)
    int qrOk = 0, qrN = 0;
    const QrCode::Ecc ecls[4] = {QrCode::Ecc::LOW, QrCode::Ecc::MEDIUM, QrCode::Ecc::QUARTILE, QrCode::Ecc::HIGH};
    for (int v = 1; v <= 40; ++v)
        for (int e = 0; e < 4; ++e) {
            ++qrN;
            const QrCode q = QrCode::encodeSegments(qrcodegen::QrSegment::makeSegments("VS 29158"), ecls[e], v, v, 0, false);
            const int n = q.getSize();
            std::vector<uint8_t> g(static_cast<size_t>(n) * n);
            for (int y = 0; y < n; ++y)
                for (int x = 0; x < n; ++x) g[static_cast<size_t>(y) * n + x] = (q.getModule(x, y) ? 1 : 0) ^ ((x + y) % 2 == 0 ? 1 : 0);
            CodewordMap m;
            if (codewordMap(CR_QR, n, n, std::string(1, "LMQH"[e]), m) && mapReadsValidBlocks(g, m, 0x11D, 0)) ++qrOk;
            else std::printf("     QR version %d level %c: codeword map wrong\n", v, "LMQH"[e]);
        }
    check(qrOk == qrN, "QR versions 1-40 at L, M, Q, H: every block read through the map is a valid RS word (" + std::to_string(qrOk) +
                           "/" + std::to_string(qrN) + ")");

    // Data Matrix: the engine's own encoder (square, one region)
    int dmOk = 0, dmN = 0;
    for (int n : {10, 12, 14, 16, 18, 20, 22, 24, 26}) {
        ++dmN;
        std::vector<uint8_t> g;
        CodewordMap m;
        if (encodeDm("29158", n, g) && codewordMap(CR_DATAMATRIX, n, n, "", m) && mapReadsValidBlocks(g, m, 0x12D, 1)) ++dmOk;
        else std::printf("     Data Matrix %d x %d: codeword map wrong\n", n, n);
    }
    check(dmOk == dmN, "Data Matrix 10 x 10 .. 26 x 26 (engine encoder): blocks valid through the map");
#ifdef CR_HAVE_DMTX
    // libdmtx: every ECC 200 size, multi-region, interleaved and rectangular; the codewords read
    // through the map must equal libdmtx's own, in order
    const int dims[30][2] = {{10, 10}, {12, 12}, {14, 14}, {16, 16}, {18, 18}, {20, 20}, {22, 22}, {24, 24}, {26, 26}, {32, 32},
                             {36, 36}, {40, 40}, {44, 44}, {48, 48}, {52, 52}, {64, 64}, {72, 72}, {80, 80}, {88, 88}, {96, 96},
                             {104, 104}, {120, 120}, {132, 132}, {144, 144}, {8, 18}, {8, 32}, {12, 26}, {12, 36}, {16, 36}, {16, 48}};
    int lxOk = 0;
    for (int idx = 0; idx < 30; ++idx) {
        const int h = dims[idx][0], w = dims[idx][1];
        DmtxEncode* enc = dmtxEncodeCreate();
        dmtxEncodeSetProp(enc, DmtxPropSizeRequest, idx);
        unsigned char text[] = "29158";
        bool ok = dmtxEncodeDataMatrix(enc, 5, text) == DmtxPass;
        std::vector<uint8_t> g(static_cast<size_t>(w) * h);
        for (int r = 0; ok && r < h; ++r)
            for (int c = 0; c < w; ++c) // libdmtx counts rows from the bottom
                g[static_cast<size_t>(h - 1 - r) * w + c] = (dmtxSymbolModuleStatus(enc->message, idx, r, c) & DmtxModuleOnRGB) ? 1 : 0;
        CodewordMap m;
        std::vector<int> stream;
        ok = ok && codewordMap(CR_DATAMATRIX, w, h, "", m) && mapReadsValidBlocks(g, m, 0x12D, 1, &stream) &&
             stream.size() == enc->message->codeSize;
        for (size_t k = 0; ok && k < stream.size(); ++k) ok = stream[k] == enc->message->code[k];
        if (ok) ++lxOk;
        else std::printf("     Data Matrix %d x %d: codeword map differs from libdmtx\n", h, w);
        dmtxEncodeDestroy(&enc);
    }
    check(lxOk == 30, "Data Matrix, all 30 ECC 200 sizes (libdmtx): the map reads libdmtx's codewords in order, blocks valid (" +
                          std::to_string(lxOk) + "/30)");
#endif
    CodewordMap m;
    check(!codewordMap(CR_DATAMATRIX, 48, 8, "", m) && !codewordMap(CR_QR, 25, 25, "", m) && !codewordMap(CR_AZTEC, 15, 15, "", m),
          "no map for a DMRE size, a QR of unknown EC level, or another symbology");
}

// Cell modulation per codeword: the modules of chosen codewords drawn on the wrong side
void flip(Drawn& d, const std::vector<int>& cells, int w, int px, int quiet, int markGrey, int backGrey)
{
    for (int i : cells) {
        const int x = i % w, y = i / w;
        const bool mark = d.code.modules[static_cast<size_t>(i)] != 0;
        cv::rectangle(d.img, cv::Rect((quiet + x) * px, (quiet + y) * px, px, px), cv::Scalar(mark ? backGrey : markGrey), -1);
    }
}

void testCellModulationPerCodeword()
{
    using namespace cr;
    DpmSettings s;
    s.enabled = true;
    // Data Matrix 22 x 22: one block, 20 check codewords, 10 errors correctable
    std::vector<uint8_t> dm;
    encodeDm("PN 4711-0815 SN 000123", 22, dm);
    CodewordMap m;
    codewordMap(CR_DATAMATRIX, 22, 22, "", m);
    const int px = 10, quiet = 3;
    // all 8 modules of one data codeword wrong: one error, UEC 1 - 2/20 = 0.90, A
    Drawn one = draw(dm, 22, CR_DATAMATRIX, px, 45, 200, Mark::Laser);
    flip(one, m.cells[5], 22, px, quiet, 45, 200);
    const DpmQuality q1 = gradeDpm(one.img, one.code, s);
    print("DM 8 cells in codeword 5", q1);
    check(q1.measured && q1.perCodeword && q1.gradeCM == 4, "Data Matrix: 8 wrong cells all in one codeword are one error: cell modulation A");
    // one module in each of 8 codewords: 8 errors, UEC 1 - 16/20 = 0.20, F
    Drawn eight = draw(dm, 22, CR_DATAMATRIX, px, 45, 200, Mark::Laser);
    std::vector<int> spread;
    for (int k = 0; k < 8; ++k) spread.push_back(m.cells[static_cast<size_t>(4 * k + 1)][3]);
    flip(eight, spread, 22, px, quiet, 45, 200);
    const DpmQuality q8 = gradeDpm(eight.img, eight.code, s);
    print("DM 1 cell in each of 8 codewords", q8);
    check(q8.measured && q8.gradeCM == 0, "Data Matrix: 8 wrong cells in 8 codewords are 8 errors of 10: cell modulation F");
    // 4 codewords: UEC 1 - 8/20 = 0.60, B
    Drawn four = draw(dm, 22, CR_DATAMATRIX, px, 45, 200, Mark::Laser);
    flip(four, {spread.begin(), spread.begin() + 4}, 22, px, quiet, 45, 200);
    const DpmQuality q4 = gradeDpm(four.img, four.code, s);
    check(q4.measured && q4.gradeCM == 3, "Data Matrix: 4 codewords wrong, UEC 0.60: cell modulation B (" + std::to_string(q4.gradeCM) + ")");

    // QR version 5-Q: 4 blocks of 18 check codewords (9 errors each); 5 wrong codewords in one
    // block grade C (UEC 1 - 10/18 = 0.44), the same 5 over the blocks A (worst 1 - 4/18 = 0.78)
    const qrcodegen::QrCode code = qrcodegen::QrCode::encodeSegments(qrcodegen::QrSegment::makeSegments("PART 4711-0815 SN 000123"),
                                                                    qrcodegen::QrCode::Ecc::QUARTILE, 5, 5, -1, false);
    const int w = code.getSize(), qpx = 8, qq = 4;
    std::vector<uint8_t> g(static_cast<size_t>(w) * w);
    for (int y = 0; y < w; ++y)
        for (int x = 0; x < w; ++x) g[static_cast<size_t>(y) * w + x] = code.getModule(x, y) ? 1 : 0;
    CodewordMap qm;
    check(codewordMap(CR_QR, w, w, "Q", qm) && qm.blockEcc.size() == 4, "QR 5-Q: four blocks");
    std::vector<int> same, over;
    for (size_t k = 0; k < qm.cells.size(); ++k) {
        if (qm.block[k] == 2 && same.size() < 5 * 2) { same.push_back(qm.cells[k][0]); same.push_back(qm.cells[k][7]); }
        if (over.size() < 5 * 2 && k >= 8) { over.push_back(qm.cells[k][0]); over.push_back(qm.cells[k][7]); }
    }
    auto gradeQr = [&](const std::vector<int>& cells, const char* what) {
        Drawn d = draw(g, w, CR_QR, qpx, 50, 195, Mark::Laser, 0.f, qq);
        d.code.ecLevel = "Q";
        flip(d, cells, w, qpx, qq, 50, 195);
        const DpmQuality q = gradeDpm(d.img, d.code, s);
        print(what, q);
        return q;
    };
    const DpmQuality qs = gradeQr(same, "QR 5 codewords in block 2"), qo = gradeQr(over, "QR 5 codewords over the blocks");
    check(qs.measured && qs.perCodeword && qs.gradeCM == 2, "QR: 5 wrong codewords in one block of 9 correctable: cell modulation C");
    check(qo.measured && qo.gradeCM == 4, "QR: the same 5 spread over four blocks: cell modulation A");
    // EC level unknown: the old approximation, not per codeword
    Drawn u = draw(g, w, CR_QR, qpx, 50, 195, Mark::Laser, 0.f, qq);
    check(!gradeDpm(u.img, u.code, s).perCodeword, "QR of unknown EC level: graded without the codeword table");
}

} // namespace

int main()
{
    testDataMatrix();
    testQr();
    testCApi();
    testCodewordMap();
    testCellModulationPerCodeword();
    std::printf("%s\n", failures ? "FAILED" : "all passed");
    return failures ? 1 : 0;
}
