// reader.hpp - internal C++ interface of the decode pipeline.
#pragma once

#include "codereader.h"

#include <opencv2/core.hpp>
#include <opencv2/dnn.hpp>

#include <array>
#include <atomic>
#include <functional>
#include <memory>
#include <chrono>
#include <string>
#include <vector>

namespace cr {

struct Code {
    std::string text;
    int format = 0;
    int step = 0;
    float x[4] = {}, y[4] = {};
    int copies = 1;
    // sampled module grid from the decoder (1 = dark), for quality grading; empty if unknown
    std::vector<uint8_t> modules;
    int modW = 0, modH = 0;
    float uec = -1.f; // the decoder's unused error correction, 0..1 (zxing); -1 = not known
    std::string ecLevel;
    // AIM symbology identifier ("]Q3", "]d2", "]C1" ...) and GS1 (1 FNC1 first or DataBar, 0 not,
    // -1 not known) from the decoder that read it. The engine's own decoders report the one their
    // final zxing decode of the module grid gave (OwnReadScope); empty / -1 where none decoded the
    // text (a known text matched as a pattern): unknown, which a GS1 check fails
    std::string symbologyId;
    int gs1 = -1;
    // 1D outputs (spec 5.2): the EAN/UPC add-on (text holds the main symbol), set by finishLinear
    std::string addon;
    bool addonMixed = false; // reads of this text disagreed on the add-on: reported as none
    float angle = 0;
    int checkDigitOk = -1, quietZoneOk = -1;
    // 1D print polarity from the decoder that read it (zxing reads inverted print): 1 light bars on a
    // dark ground, 0 dark bars, -1 not known; the quiet-zone check takes the bars' side from it
    int lightOnDark = -1;
};

// The AIM identifier and GS1 flag of the engine's own decoders (lattice, grid fit, soft and known-content
// decodes, frames): each ends by decoding a clean module grid with zxing, which reports them
// (zxformats.cpp). A scope open on this thread around one decoder call collects them by text; apply()
// gives a code the ones seen for its text, and leaves it unknown when that text was not decoded in the
// scope or was decoded with two identifiers. Scopes nest: a decode is noted in every open one.
class OwnReadScope {
public:
    OwnReadScope();
    ~OwnReadScope();
    OwnReadScope(const OwnReadScope&) = delete;
    OwnReadScope& operator=(const OwnReadScope&) = delete;
    void apply(Code& c) const;
    // a decoded grid's text with its identifier, in every scope open on this thread
    static void note(const std::string& text, const std::string& symId, int gs1);

private:
    struct Seen {
        std::string symId;
        int gs1 = -1;
    };
    std::vector<std::pair<std::string, Seen>> seen_;
    OwnReadScope* outer_;
};

// Print / image quality of one code (quality.cpp). Grades: 4 A .. 0 F, -1 not measured.
struct Quality {
    bool measured = false;
    float symbolContrast = 0;      // % (light - dark) / light
    float modulation = 0;          // 5th percentile module distance from threshold, 0..1
    float reflectanceMargin = 0;   // same, wrong-side modules counted as 0
    float fixedPatternDamage = 0;  // share of finder / timing / L / clock modules wrong
    float axialNonUniformity = 0;  // |x pitch - y pitch| / pitch
    float gridNonUniformity = -1;  // worst timing-module offset, in modules
    float unusedErrorCorrection = 0; // 1 = no correction used (approximation for QR, Data Matrix); -1 not measured
    float printGrowth = 0;         // + dark modules too big, - too small
    float quietZoneDark = -1;      // share of dark samples in the quiet zone ring
    float moduleSizePx = 0;
    float lightingUniformity = 0;  // 10th / 90th percentile of light modules (1 = even)
    float saturated = 0;           // share of modules at 250+ (glare)
    int gradeSC = -1, gradeMOD = -1, gradeRM = -1, gradeFPD = -1, gradeAN = -1, gradeGN = -1, gradeUEC = -1, gradeQZ = -1;
    int overall = -1;
    // with a calibration (calibration.cpp): the X-dimension in mm; reflectance values in %
    float xDimMm = 0;
    bool calibrated = false;          // reflectance-calibrated (else camera-relative)
    // 1D (ISO/IEC 15416, grade1d.cpp): the means over the scan lines
    bool linear = false;
    float edgeContrastMin = 0, defects = 0, decodability = -1, rMin = 0, rMax = 0;
    // multi-row (PDF417, ISO/IEC 15415, grade_pdf417.cpp): validly decoded codewords / codewords crossed
    float codewordYield = -1;
    int gradeCY = -1;
    int scans = 0, decodedScans = 0;
    int gradeEC = -1, gradeDefects = -1, gradeDecodability = -1;
    int gradeRmin = -1;               // 1D: the mean of the scans' Rmin grades (gradeQZ: their quiet zones)
    std::vector<cr_scan> scanLines;   // 1D: each scan line, struct_size unset
};

// ---- What the reader knows about its optics (calibration.cpp), for verifier-style grades.
struct Calibration {
    cv::Mat flatGain;          // CV_32F, an eighth of the image: brings each point to the white card's level
    cv::Size flatSize;         // the image size the flat field was taken at
    bool reflectance = false;  // two-point card: grey (after the flat field) -> reflectance %
    float greyDark = 0, greyLight = 0, reflDark = 0, reflLight = 0;
    float mmPerPx = 0;         // at the case's top; 0 = unknown
    float fx = 0, fy = 0, cx = 0, cy = 0, k1 = 0, k2 = 0, p1 = 0, p2 = 0, k3 = 0; // lens; fx 0 = none
};
// false when the picture is not a white card under working light (then nothing changes)
bool setFlatField(Calibration& c, const cv::Mat& white);
float flatCorrect(const Calibration& c, cv::Point2f p, float grey);
// reflectance % at image point p; camera-relative (grey / 255) without a reflectance calibration
float toReflectance(const Calibration& c, cv::Point2f p, float grey);
cv::Point2f undistort(const Calibration& c, cv::Point2f p);
float lengthMm(const Calibration& c, cv::Point2f a, cv::Point2f b);
// lens and scale from chessboard pictures (cols x rows inner corners, square side in mm) at the
// case's top; returns the views used, -1 none found, -2 built without OpenCV's calib module
int calibrateChessboard(const std::vector<cv::Mat>& images, int cols, int rows, float squareMm, Calibration& c, float* rmsPx);
// the calibration limit: a lens model that reprojects worse is refused, both when it is measured
// (calibrateChessboard -3) and when a stored one is set (cr_set_lens -1)
constexpr float kMaxLensRmsPx = 0.5f;

Quality gradeCode(const cv::Mat& gray, const Code& c, const Calibration* cal = nullptr);
// 1D print quality after ISO/IEC 15416 (grade1d.cpp)
Quality gradeLinear(const cv::Mat& gray, const Code& c, const Calibration* cal = nullptr);
// PDF417 print quality after ISO/IEC 15415 (multi-row) and ISO/IEC 15438 (grade_pdf417.cpp)
Quality gradePdf417(const cv::Mat& gray, const Code& c, const Calibration* cal = nullptr);

// Direct part mark quality after ISO/IEC TR 29158 (grade_dpm.cpp), Data Matrix and QR only.
struct DpmSettings {
    bool enabled = false;
    int lighting = CR_DPM_LIGHT_90; // cr_dpm_lighting: recorded with the grade
    int aperture = 80;              // % of the cell: 50, 80, or 0 = the better of the two
    int wavelengthNm = 0;           // recorded with the grade; 0 = not recorded
    int closing = 0;                // dot-peen gap closing: 0 = sized from the gaps, -1 off, > 0 disc px
    // image-based reflectance calibration: the calibration symbol's light reflectance %, its mean
    // light (grey) and the system response (exposure x gain) it was imaged at; the part's response
    float rCal = 0, mlCal = 0, srCal = 0, srTarget = 0;
};
struct DpmQuality {
    bool measured = false, calibrated = false, inverted = false;
    bool perCodeword = false; // cell modulation graded per codeword (codeword table known)
    int lighting = 0, aperturePct = 0;
    float cellPx = 0, closingPx = 0;
    float meanLight = 0, meanDark = 0, meanLightPct = 0; // ML, MD (grey), ML as % of full scale
    bool meanLightOk = false;                            // ML within 70..86 % of full scale
    float cellContrast = 0, cellModulation = 0, minReflectance = -1, fixedPatternDamage = 0, distributedDamage = 0;
    float axialNonUniformity = 0, gridNonUniformity = -1, unusedErrorCorrection = -1;
    int gradeDecode = -1, gradeCC = -1, gradeCM = -1, gradeMR = -1, gradeFPD = -1, gradeAN = -1, gradeGN = -1, gradeUEC = -1;
    int overall = -1, overall50 = -1, overall80 = -1;
};
DpmQuality gradeDpm(const cv::Mat& gray, const Code& c, const DpmSettings& s, const Calibration* cal = nullptr);

// Where each codeword of a Data Matrix (ECC 200) or QR lies in its module grid, and its
// Reed-Solomon block (codewords.cpp), for grading per codeword. Codewords in transmission order
// (data, then check codewords, interleaved as the symbol stores them).
struct CodewordMap {
    std::vector<std::vector<int>> cells;          // per codeword: its 8 modules, y * w + x, bit 7 first
    std::vector<int> block;                       // per codeword: its block
    std::vector<int> blockData, blockEcc, blockP; // per block: data and check codewords, misdecode protection
};
// false for other formats and sizes (DMRE), or a QR whose EC level (L, M, Q, H) is not known
bool codewordMap(int format, int w, int h, const std::string& ecLevel, CodewordMap& m);

// 4-point homography (quality.cpp; OpenCV 5 moved getPerspectiveTransform out of imgproc)
cv::Mat homography4(const cv::Point2f s[4], const cv::Point2f d[4]);

// Model-based QR read for tiny noisy codes (gridfit.cpp): known module count (0 = try 21/25/29).
// quad: the code's corners in crop pixels.
// A real decode of an n x n QR at known corners (TL, TR, BR, BL in crop pixels); empty if none.
std::string decodeQrAt(const cv::Mat& crop, const cv::Point2f quad[4], int n);
// codeSide: the finder's code-area size in crop pixels (0 = unknown), for the own finder search.
// budgetMs: time this call may take; it gives up when it runs out.
bool gridFitQR(const cv::Mat& crop, int modules, float codeSide, int budgetMs, std::string& text, cv::Point2f quad[4],
               const std::vector<std::string>* known = nullptr);
// The n x n QR code in crop (codeSide pixels across): own finder search at half scale; the best
// three-finder group's corners in crop pixels, any orientation. False when none is plausible.
bool locateQrQuad(const cv::Mat& crop, int n, float codeSide, cv::Point2f quad[4]);
// Multi-frame reading (multiframe.cpp). frameShift: the translation of b relative to a (pixels;
// content at x in a is at x + shift in b), from phase correlation at a quarter scale.
cv::Point2d frameShift(const cv::Mat& a, const cv::Mat& b);
// One code seen in several crops (the same region of each frame, already aligned to a pixel):
// each crop's grid located (or, where a reflection hides a finder, the reference grid moved to
// where it correlates best), oriented like the reference (the first crop located). Each frame
// gives per-module evidence: distance from its own local threshold over the distance to the class
// mean on that side, zero for modules with clipped pixels; frames are weighted by their quality on
// the code (inverse variance of that evidence). The fused evidence is soft-decoded (weak modules
// as erasures), then known-content decoded. quad0: the code's corners in crop *quadFrom (the
// reference). Empty when it cannot read.
std::string decodeQrFrames(const std::vector<cv::Mat>& crops, int n, float codeSide, const std::vector<std::string>& known,
                           int budgetMs, cv::Point2f quad0[4] = nullptr, int* quadFrom = nullptr);
// Multi-frame super-resolution (multiframe.cpp; after Scanbuy US8150163, expired): the crops
// (the same region of each frame, aligned to about a pixel) registered to a small part of a pixel
// against the first, their levels matched to it, clipped pixels left out, and the region drawn on
// a grid s times finer by iterative back-projection (each crop modelled as the fine image moved by
// its shift and averaged over s x s blocks). CV_32F in the first crop's levels; empty when fewer
// than two crops register. shifts: each used crop's shift against the first.
cv::Mat superResolve(const std::vector<cv::Mat>& crops, int s, std::vector<cv::Point2d>* shifts = nullptr);
// A QR code too small for the module sampling (below about 2.6 px per module) read from the crops'
// super-resolved region at about 4 px per module: grid fit, then the soft and known-content decode.
// quad0: the code's corners in the first crop. Empty when it cannot read.
std::string superResolveQrFrames(const std::vector<cv::Mat>& crops, int n, float codeSide, const std::vector<std::string>& known,
                                 int budgetMs, cv::Point2f quad0[4] = nullptr);
// Known-content reading of the QR codes in crop (n x n modules, codeSide pixels): own finder
// search at about 4 px per module, every plausible code tried with knownDecodeQr. Each code read
// is added to texts / quads (crop pixels). For the early pass: no OpenCV detectors, no fallbacks.
int knownFitQR(const cv::Mat& crop, int n, float codeSide, const std::vector<std::string>& known, int budgetMs,
               std::vector<std::string>& texts, std::vector<std::array<cv::Point2f, 4>>& quads);

// A region the finder proposed, and what became of it.
struct FoundRegion {
    cv::Rect box;
    bool fromAI = false;
    bool read = false;
};

struct Result {
    std::vector<Code> codes;
    std::vector<Quality> quality; // one per code when grading is on
    std::vector<cv::Rect> misses;
    std::vector<float> missSide;        // the AI's code side of each miss (0 = unknown), same order
    std::vector<cr_miss_info> missInfo; // why each miss did not read (diagnose.cpp), same order
    std::vector<cv::Point> missSlot;    // the slot (row, column from 1; 0 = none) of each miss, same order
    float aiToCode = 0.f;               // decoded code side / AI side on this image's read regions; 0 = unknown
    std::vector<FoundRegion> regions; // everything the finder proposed (live view: yellow)
    cr_stats stats{};
    int rejectedLength = 0, rejectedCheckDigit = 0; // 1D reads the job's rules dropped (cr_stats_ext)
    int copiesOver = 0;     // codes seen more often than the product's label carries (cr_stats_ext)
    int extraUnchecked = 0; // code-sized regions left undecoded once the count was read (cr_stats_ext)
    int refusedEan = 0, refusedAddon = 0; // EAN / UPC reads and add-ons not read again on the image (cr_stats_ext)
};

// ---- Decode-fail diagnostics (diagnose.cpp), after the expired US9552506 / US8027802.
struct StepStats;
// What is known about the product's codes when a miss is diagnosed.
struct MissContext {
    int format = CR_QR;
    int modules = 0;            // modules across the product's codes; 0 = unknown
    float modulePx = 0.f;       // the product's module size in pixels; 0 = unknown
    float aiToCode = 1.f;       // decoded code side / AI side, from this image's read regions
    float refSharpness = 0.f;   // the same measures on this image's read codes (0 = none read)
    float refContrast = 0.f;
};
// One miss: box in full-image pixels, codeSide = the AI's code side (0 = unknown).
// measuredModule (optional): the module size from the region's own texture, 0 = not measured.
cr_miss_info diagnoseMiss(const cv::Mat& gray, const cv::Rect& box, float codeSide, const MissContext& ctx, float* measuredModule = nullptr);
// Code evidence of a diagnosed miss: a finder pattern, or code texture whose measured module size
// is within 0.6..1.6 of the product's (any, when none is known). Background has neither.
bool missHasEvidence(const cr_miss_info& d, const MissContext& ctx, float measuredModule);
// Every miss of a result (res.missInfo), within budgetMs: the rest stay measured = 0, UNKNOWN.
// Regions whose code was read in another region, and LOW_CONTRAST / BLUR regions without code
// evidence, are dropped from the list (diagnostics only: nothing read or decided changes).
void diagnoseMisses(const cv::Mat& gray, Result& res, const MissContext& ctx, double budgetMs);
// The context from this image's decoded codes, else the reader's learned state, else the hints.
MissContext missContext(const Result& res, const cr_params& p, const StepStats* learned);

// Locator: proposes code regions in full-image coordinates, best first.
std::vector<cv::Rect> locate(const cv::Mat& gray, const cr_params& p, float* labelBrightness);

// A code area found by the AI locator.
struct Found {
    cv::Rect box;              // crop to decode (code plus margin), full-image pixels
    cv::RotatedRect rotated;   // the code area itself (coarse), full-image pixels
    float side = 0;            // longer side of the code area, pixels
    float score = 0;           // mean logit: confidence
};

// AI locator (ai_locator.cpp): CodeNet probability map -> code areas, most confident first.
// inputScale 8 = normal pass; 4 = finer pass (2x resolution) for small codes.
std::vector<Found> locateAI(cv::dnn::Net& net, const cv::Mat& gray, float threshold, float* labelBrightness,
                            int inputScale = 8);

// AI enhancer (ai_locator.cpp): dark / blurred / glare crop -> clean black-and-white image.
cv::Mat enhanceAI(cv::dnn::Net& net, const cv::Mat& crop);

// AI models of one reader. Nets are not thread safe: one enhancer per decode thread.
class WorkerPool; // pool.hpp: the reader's persistent worker threads

struct Models {
    cv::dnn::Net* locator = nullptr;
    std::vector<cv::dnn::Net>* enhancers = nullptr;
    WorkerPool* pool = nullptr; // the parallel passes run on it; null = threads per pass
    const Calibration* calibration = nullptr; // the grades' optics (cr_set_flat_field and friends)
    // told every found region once finding is done, before decoding (live view: yellow)
    std::function<void(const std::vector<FoundRegion>&)> onFound;
};

// What each image fix has achieved on this reader's recent cases. The next case tries the
// fixes that read codes most often first, so every product line settles on its own order.
struct StepStats {
    static constexpr int kMax = 24;
    std::array<std::atomic<uint32_t>, kMax> wins{};
    std::array<std::atomic<uint32_t>, kMax> tries{};
    std::atomic<uint32_t> cases{0}; // images decoded by this reader
    // Learned from decoded cases of the running product (one case = one code size and type):
    // the code-area size range (AI side, pixels; 0 = not learned yet) and how many symbols carry
    // each code (2 on can labels that print the same QR twice).
    std::atomic<float> codeMin{0.f}, codeMax{0.f};
    std::atomic<float> symbolsPerCode{0.f};
    std::atomic<int> qrModules{0}; // QR size of this product's codes (21 = version 1), 0 = unknown
    // the printer's QR encoding, learned from a decoded code (candidate matching); kind -1 = unknown
    std::atomic<int> encKind{-1}, encSplit{0}, encEcl{1}, encModules{0}, encFormat{CR_QR};
    std::atomic<int> encConfirmed{0}; // 0: the encoding came from a saved state and no code has confirmed it yet
    std::atomic<float> fullPassMs{0.f}; // cost of the whole-image passes on this line (ms)
    // the product's Data Matrix size (lattice step): modules across and side in pixels; 0 = unknown
    std::atomic<int> dmModules{0};
    std::atomic<float> dmSide{0.f};
    // the lens blur (psf.cpp), sigma in modules, measured on read codes: per zone and overall;
    // 0 = not measured yet
    static constexpr int kZones = 4;
    // the case's slot lattice (slots.cpp): rows x columns learned from complete cases, either way
    // round; 0 = not learned yet
    std::atomic<int> slotA{0}, slotB{0};
    // the print's polarity: 0 unknown, 1 dark on light, 2 light on dark (laser-marked caps, white
    // ink on dark labels: the image is inverted once so every decoder sees dark modules)
    std::atomic<int> polarity{0};
    std::array<std::atomic<float>, kZones * kZones> psfSigma{};
    std::atomic<float> psfSigmaAll{0.f};
    // The same per zone of the image (4 x 4): a lens corner or a glare spot needs other fixes than
    // the centre, and a failing zone gets its own order; plus how many AI regions read per zone.
    std::array<std::array<std::atomic<uint32_t>, kMax>, kZones * kZones> zoneWins{}, zoneTries{};
    std::array<std::atomic<uint32_t>, kZones * kZones> zoneRead{}, zoneUnread{};
};

// ---- QR module grid (qrgrid.cpp). quad = code corners TL, TR, BR, BL; n = modules across.
// Mean brightness of each module (3 x 3 samples in its centre), n x n CV_32F.
cv::Mat moduleMeans(const cv::Mat& img32, const cv::Point2f quad[4], int n);
// QR fixed patterns of an n x n symbol: 1 dark, 0 light, -1 data.
cv::Mat qrFixed(int n);
// Finder-pattern likeness at every position for module size m (normalised correlation, 0-80 deg).
cv::Mat finderResponse(const cv::Mat& img32, float m);
// Faster, slightly coarser (15 degree steps) version for candidate matching.
cv::Mat finderResponseFast(const cv::Mat& img32, float m);
// Three finder peaks forming the QR right angle -> the code's 4 corners. off = template half size.
bool quadFromFinders(const cv::Mat& resp, float m, int n, int off, cv::Point2f quad[4]);
// Every plausible code (three finders at a right angle, n x n at module m), best first, one per
// code position: a crop can hold several codes (two copies on a label).
std::vector<std::array<cv::Point2f, 4>> quadsFromFinders(const cv::Mat& resp, float m, int n, int off, size_t maxQuads);

// ---- candidate matching (match.cpp: QR, dmmatch.cpp: Data Matrix)
// How a printer encodes a product's codes. QR: kind 0 byte, 1 alphanumeric, 2 numeric,
// 3 alphanumeric prefix + numeric, 4 byte prefix + numeric (prefix = first `split` characters).
// Data Matrix: kind 0 ASCII encodation, kind 1 GS1 (ASCII with FNC1 first and every GS as FNC1),
// square symbol of `modules`.
struct QrEncoding {
    int format = CR_QR;
    int kind = -1, split = 0, ecl = 1, modules = 0; // ecl 0 L, 1 M, 2 Q, 3 H
    bool valid() const { return kind >= 0 && modules >= (format == CR_DATAMATRIX ? 10 : 21); }
};
// The encoding that reproduces a decoded QR code's modules (kind -1 if none does).
QrEncoding learnQrEncoding(const Code& c);
// The same for a decoded Data Matrix (ASCII encodation, plain or GS1).
QrEncoding learnDmEncoding(const Code& c);
// The modules of `text` under an encoding and mask (n x n row-major, 1 = dark); the mask is
// ignored for Data Matrix.
bool encodeQr(const std::string& text, const QrEncoding& e, int mask, std::vector<uint8_t>& dark);
// An ECC200 Data Matrix of n x n modules, ASCII encodation (10 <= n <= 26); gs1: FNC1 first and
// every GS (ASCII 29) of the text as FNC1, the way a GS1 printer encodes the decoded text.
bool encodeDm(const std::string& text, int n, std::vector<uint8_t>& dark, bool gs1 = false);
// The codes an unread region can be (one image), with their patterns cached per mask.
class MatchCandidates {
public:
    MatchCandidates();
    QrEncoding encoding;
    std::vector<std::string> texts;
    const std::vector<std::vector<uint8_t>>& patterns(int mask) const;
private:
    struct Cache;
    std::shared_ptr<Cache> cache_;
};
// Finds the code in crop (codeSide = code size in crop pixels) and returns the candidate it
// matches, when the match is strong and clearly ahead of the rest. quad in crop pixels.
// psfSigmaMod > 0: when the leader by module sampling misses the rule, every candidate is
// re-scored through the lens blur (psf.cpp, sigma in modules), which keeps the pattern of a
// defocused code; accepted only when the same candidate leads both ways and the blur scores
// pass the rule over all candidates.
bool matchQr(const cv::Mat& crop, float codeSide, const MatchCandidates& mc, int budgetMs, std::string& text,
             cv::Point2f quad[4], float* score = nullptr, float psfSigmaMod = 0.f);
// Reed-Solomon decoding of Data Matrix codewords (GF(256)/0x12D, roots alpha^1..alpha^nEcc) with
// erasures (known-bad positions): corrects e errors and f erasures while 2e + f <= nEcc.
bool rsDecodeDm(std::vector<int>& codewords, int nEcc, const std::vector<int>& erasures, int* errors = nullptr);
// The same for QR (GF(256)/0x11D, roots alpha^0 .. alpha^(nEcc-1)).
bool rsDecodeQr(std::vector<int>& codewords, int nEcc, const std::vector<int>& erasures, int* errors = nullptr);
// Soft Data Matrix decoding from n x n module means (dark = low, canonical orientation): the L
// finder and clocks are not read (they are known), the least certain codewords become erasures,
// two check symbols stay in reserve against miscorrection, and the corrected symbol must parse.
std::string softDecodeDm(const cv::Mat& means, int n, int* erasuresUsed = nullptr);
// The same for a symbol seen in part: seen (CV_8U, n x n) is nonzero where the module was seen;
// codewords with a module not seen are erasures first. Four check symbols stay in reserve, the
// corrected symbol must agree with the seen modules, and with two or more known texts the read
// must look like them (plausibleAmong). Empty when it cannot read.
std::string maskedSoftDecodeDm(const cv::Mat& means, const cv::Mat& seen, int n, const std::vector<std::string>* known = nullptr,
                               int* erasuresUsed = nullptr);
// Hard-decision decoding of an n x n module matrix (CV_8U, 1 = dark, canonical orientation) with
// two check symbols in reserve against a false correction. Empty when it cannot read.
std::string hardDecodeDm(const cv::Mat& dark, int n);
// True when text looks like the product's other codes (same length, digits where they all have
// digits): the plausibility rule of the known-content readers.
bool plausibleAmong(const std::string& text, const std::vector<std::string>& texts);
// Known-content Data Matrix decoding from n x n module means (canonical orientation): data
// codewords equal in every code already read (texts) are fixed, a classifier trained on the known
// modules decides the rest, Reed-Solomon corrects the unknown part. Empty when it cannot read.
std::string knownSoftDecodeDm(const cv::Mat& means, int n, const std::vector<std::string>& texts);
// Soft QR decoding (versions 1-6) from n x n module means (dark = low, canonical orientation):
// known function patterns, EC level and mask from the format modules (eclHint -1 = any), least
// certain codewords as RS erasures with two check symbols in reserve, corrected symbol must parse.
std::string softDecodeQr(const cv::Mat& means, int version, int eclHint = -1);
// The same from fused evidence (multiframe.cpp): n x n, positive = dark, magnitude = confidence;
// modules of zero evidence (masked in every frame) make their codewords erasures from the start.
std::string softDecodeQrEvidence(const cv::Mat& evidence, int version);
// Glare correction (decode.cpp, step CR_STEP_GLARE): each small area of the crop stretched between
// its own darkest and brightest level (about three modules of a code of codeSide pixels; 0 =
// unknown), so a code half under a reflection comes out as black and white as the other half.
cv::Mat deglare(const cv::Mat& crop, float codeSide);
// Known-content QR decoding (versions 1-6) of the code at quad (corners of the n x n grid in
// img32, any corner order): codewords shared by every code already read (texts) are known; the
// grid is registered on the known modules, a pixel classifier trained on them reads the rest, and
// Reed-Solomon corrects only the unknown part, known codewords fixed. Empty when it cannot read.
std::string knownDecodeQr(const cv::Mat& img32, const cv::Point2f quad[4], int n, const std::vector<std::string>& texts,
                          std::chrono::steady_clock::time_point deadline);
// Known modules of a QR (versions 1-6) at an EC level and mask: function patterns and format
// (1 dark, 0 light), and with texts (two or more) the modules of the data codewords every text
// shares, plus the check codewords of a block whose data is all shared; -1 elsewhere. knownCw:
// interleaved codeword index -> known value, or -1.
std::vector<int8_t> qrKnownModules(int version, int e, int mask, const std::vector<std::string>* texts, std::vector<int>* knownCw);
// QR text from solved modules (n x n, 1 = dark, canonical orientation) with a confidence each:
// known codewords fixed and kept, the least certain others erased, two check symbols in reserve,
// the corrected symbol redrawn and parsed; with texts the result must look like them.
std::string decodeQrSolved(const std::vector<uint8_t>& dark, const std::vector<float>& conf, int version, int e, int mask,
                           const std::vector<int>& knownCw, const std::vector<std::string>* texts);
// Under-resolved QR (underres.cpp, versions 1-6, about 1.3 - 2.5 px per module): the pixels of
// gray (8-bit, original resolution) modelled as blurred area mixes of the modules under the quad
// (TL, TR, BR, BL of the n x n grid in gray pixels, any corner order); the modules solved as
// binary on 3 x 3 neighbourhoods, then Reed-Solomon. texts: codes read in the case (or null).
std::string underresDecodeQr(const cv::Mat& gray, const cv::Point2f quad[4], int n, const std::vector<std::string>* texts,
                             std::chrono::steady_clock::time_point deadline);
// Decodes a clean n x n Data Matrix module matrix (CV_8U, 1 = dark, canonical orientation).
std::string decodeDmBits(const cv::Mat& dark);

// Data Matrix squares in an 8-bit image: dense dark blocks on bright label paper about `side`
// across (n modules), label borders removed (dmmatch.cpp).
std::vector<cv::RotatedRect> dmCodeSquares(const cv::Mat& img8, float side, int n);
// Inside-out Data Matrix reading (lattice.cpp, after Cognex's PowerGrid idea): no finder, clock
// or quiet zone needed. The module pitch and grid angle come from the texture (the periodicity of
// the module edges), the lattice of module centres is sampled, and every n x n window whose L
// finder fits is decoded; Reed-Solomon rejects wrong windows. codeSide in crop pixels.
bool latticeDm(const cv::Mat& crop, float codeSide, int n, int budgetMs, std::string& text, cv::Point2f quad[4],
               const std::vector<std::string>* known = nullptr);

// The same for Data Matrix (the code square is found as a dark block on the label).
bool matchDm(const cv::Mat& crop, float codeSide, const MatchCandidates& mc, int budgetMs, std::string& text,
             cv::Point2f quad[4], float* score = nullptr);
// A real decode of an n x n Data Matrix at a known position (the corners matchDm found, top left
// first in the canonical orientation): module means, a half-module shift each way and 3 % in size,
// soft decoding with check symbols in reserve. The final grid decode notes the symbol's own identifier (]d1 or
// ]d2) in the open OwnReadScopes. Empty when it cannot read.
std::string decodeDmAt(const cv::Mat& crop, const cv::Point2f quad[4], int n);

// The miss list of a short case: of the regions that did not read (full-image boxes, the AI's
// code side and confidence, 0 for classical regions), the ones most likely to be the codes still
// missing, best first, as indices into boxes. Drops the same box found twice (a grown fragment
// over its tight crop), regions beside a read code (its label flag or a second copy, label edges)
// and, once enough regions of the product's code size are listed, everything else.
// sideLo / sideHi: the product's code size range (AI side), 0 = unknown.
std::vector<size_t> pickMisses(const std::vector<cv::Rect>& boxes, const std::vector<float>& sides,
                               const std::vector<float>& scores, const std::vector<Code>& codes, int missing,
                               float symbolsPerCode, float sideLo, float sideHi);

// Pipeline: locate, decode crops in parallel, fall back to whole-image passes.
Result decode(const cv::Mat& gray, const cr_params& p, const Models& models, StepStats* learned = nullptr);

// ---- The lens and printer as a kernel (psf.cpp). A pattern (n x n, 1 = dark; quad = corners TL,
// TR, BR, BL in img pixels) rendered through print growth (modules) and a Gaussian (pixels).
constexpr float kPrintGrowth = 0.1f;   // measured 0.10 modules on both sample products
constexpr float kDefaultPsfMod = 0.35f; // sigma in modules before any measurement (0.21-0.55 seen)
// Correlation of the rendering with the image pixels on the modules in `use` (n x n CV_8U, empty =
// all), -2 when the box is too small. Dark modules are low pixels: a match scores positive.
float psfScore(const cv::Mat& img, const cv::Point2f quad[4], const uint8_t* pat, int n, const cv::Mat& use, float growth, float sigmaPx);
// The blur of a read code: the sigma (modules) whose rendering fits the pixels best; -1 when no
// blur fits better than the sharp pattern (wrong pattern or corners).
float measurePsfSigma(const cv::Mat& img, const cv::Point2f quad[4], const uint8_t* pat, int n, float growth);

// ---- The case's slot map (slots.cpp): the read codes as the fixture.
struct SlotGrid {
    bool valid = false;         // the codes sit on a lattice (median 0.15, worst 0.5 pitch off)
    cv::Point2f origin, a, b;   // node (i, j) at origin + i a + j b
    int i0 = 0, ni = 0, j0 = 0, nj = 0; // the case's nodes
    float pitch = 0, offMedian = 0, offMax = 0;
};
// the lattice of the read code centres; learnedA x learnedB the product's slots (0 = unknown),
// gray and codeSide to choose the side a short row or column grows on
// expected: the case's code count, which gives its size before one is learned (0 = unknown)
SlotGrid fitSlots(const std::vector<cv::Point2f>& centres, int learnedA, int learnedB, const cv::Mat* gray, float codeSide, int expected = 0);
cv::Point2f slotCentre(const SlotGrid& g, int i, int j);
// the same, corrected by the offsets of the nearest read codes from their nodes (perspective)
cv::Point2f slotCentreNear(const SlotGrid& g, int i, int j, const std::vector<cv::Point2f>& read);
// the node nearest p; true when it is one of the case's nodes; off in pitches
bool slotOf(const SlotGrid& g, cv::Point2f p, int* i, int* j, float* off);
// row and column for the operator: from 1 at the top left
void slotRowCol(const SlotGrid& g, int i, int j, int* row, int* col);
// the case's nodes no read code sits on
std::vector<std::pair<int, int>> emptySlots(const SlotGrid& g, const std::vector<cv::Point2f>& read);

// ---- ISO 15416 decodability per symbology (decodability.cpp). edges: one scan's element edges from
// the first bar's leading edge to the last bar's trailing edge. 0..1, -1 = not measured (the element
// count does not fit the symbology; DataBar: decodabilityDataBar)
// text: the decoded text (Codabar's wide counts come from its characters; EAN / UPC's digits tell
// which side of the bar-width threshold characters 1, 2, 7 and 8 must lie on)
float decodability1d(const std::vector<float>& edges, int format, const std::string& text = {});
// EAN-13, UPC-A, EAN-8 and UPC-E after the ISO/IEC 15420 reference decode: each character from its
// edge-to-similar-edge distances (print gain moves both edges of a pair alike), 1 / 7 and 2 / 8 by
// their bars' width, guards checked, the text as zxing gives it when its check digit holds; empty
// when the edges are not such a symbol read from its start (edges as decodability1d's)
std::string eanReferenceDecode(const std::vector<float>& edges, int format);
// an EAN / UPC text as its GTIN: UPC-A and UPC-E (expanded) as 13 digits, as zxing-cpp 3 gives
// them; EAN-13 and EAN-8 as they are
std::string eanGtin(int format, const std::string& text);
// an EAN-2 / EAN-5 add-on after the ISO/IEC 15420 reference decode: edges from its guard's first bar
// to its last bar (6 x digits + 2 of them), each digit from its edge-to-similar-edge distances, the
// guard and separators checked, the digits' sets matching their parity (EAN-2: the value modulo 4;
// EAN-5: the weighted sum's check); empty when the edges are not such an add-on
std::string eanAddonReferenceDecode(const std::vector<float>& edges, int digits);
// a Pharmacode's bars left to right as its decoder reads the image (true = wide) from its value and
// direction (CR_PHARMACODE or CR_PHARMACODE_REV); empty when text is not a value 3..131070
std::vector<bool> pharmacodeBars(int format, const std::string& text);
// a Pharmacode's unit (narrow bar, half a gap) from one scan's symbol edges and its value; 0 when
// the edges are not its bars
float pharmacodeModule(const std::vector<float>& edges, int format, const std::string& text);
// GS1 DataBar after ISO/IEC 24724's reference decode; the variant from the scan's own decode (the
// element counts of Omnidirectional and a two-pair Expanded are the same). A stacked symbol's rows:
// Stacked and Stacked Omnidirectional's top row (left guard, character, finder, character, right
// guard bar) and bottom row (guard bar and space, character, finder, character, guard), Expanded
// Stacked's rows as Expanded. mirrored: the edges read from the other end (a symbol upside down, or
// an Expanded Stacked row printed right to left); the caller picks the orientation for the whole row
enum class DataBarKind { Omni, Limited, Expanded, StackedTop, StackedBottom }; // Omni: Omnidirectional and Truncated
float decodabilityDataBar(const std::vector<float>& edges, DataBarKind kind, bool mirrored = false);
// the X-dimension (narrow element) from a scan's edges: the mean of the narrow bars' and narrow spaces'
// medians, so print gain does not change it; 0 = too few
float moduleFromEdges(const std::vector<float>& edges);

// ---- 1D rules of the job (rules1d.cpp)
// Code 39 modulo 43 over the text, its last character the check; false for text outside the
// 43-character set (full ASCII cannot be checked from the text)
bool code39CheckOk(const std::string& text);
// GS1 modulo 10 (ITF-14, GTIN, SSCC): the last digit checks the others; false for a non-digit
bool gs1Mod10Ok(const std::string& digits);
// cr_params.min_length / max_length / check_digit on one read of `format` (a cr_format flag):
// false = drop the read. Length = characters as printed (check digit included; not checked for
// EAN/UPC). CR_CHECK_STRIP removes the check digit from text. 2D types always pass.
// Codabar modulo 16 over the whole text (start and stop characters included), the character before
// the stop character the check; false for fewer than one data character or an unknown character
bool codabarCheckOk(const std::string& text);
// why a read was dropped (applyLinearRules)
enum LinearReject { kRejectNone = 0, kRejectLength = 1, kRejectCheckDigit = 2 };
bool applyLinearRules(int format, std::string& text, int minLength, int maxLength, int checkDigit, int* reject = nullptr);
// a re-read of a code is the text the job kept: equal, or the kept text is the re-read with a correct
// check digit stripped (Code 39 and ITF: the last character; Codabar: the one before the stop)
bool readMatchesKept(int format, const std::string& read, const std::string& kept);
// cr_code.check_digit_ok of a reported read of `format` under the job's check_digit setting
int checkDigitState(int format, int checkDigit);

// ---- 1D codes at any angle (linear.cpp)
struct LinearBand {
    cv::Point2f centre, dir; // dir: across the bars (the scan direction), unit length
    float length = 0, height = 0;
};
#ifdef CR_HAVE_DMTX
// libdmtx on a region (decode.cpp): text with every FNC1 as GS (ASCII 29) but the leading one;
// symId "]d2" when the symbol starts with FNC1 (GS1), else "]d1"
bool readDmtx(const cv::Mat& img, int timeoutMs, std::string& text, cv::Point2f q[4], std::string* symId = nullptr);
#endif
struct LinearRead {
    std::string text, addon;
    std::string symbologyId; // zxing's; "]C1" for the fitted SSCC (FNC1 checked by the fit)
    int gs1 = -1;
    int format = 0, step = 0;
    int lightOnDark = -1; // zxing read the profile inverted (1) or not (0); -1 = not zxing
    cv::Point2f quad[4];
    // DotCode: the grid it decoded (1 = dot, row by row in the symbol's own orientation, between the
    // quad's corners) and its unused error correction, for grading; empty / -1 otherwise
    std::vector<uint8_t> modules;
    int modW = 0, modH = 0;
    float uec = -1.f;
};
// the modules (1 = bar) of a GS1-128 SSCC: FNC1, code set C, the 20 digits ("00" + 18), check, stop
std::vector<uint8_t> ssccModules(const std::string& digits);
// bands of parallel bars at any angle (gradient structure tensor), largest first
std::vector<LinearBand> findLinearBands(const cv::Mat& gray, int maxBands);
// the same at one scale f (gray resized by f; the bands in gray's pixels). The tensor's window and the
// closing are a few px at that scale, so bars much wider (a large X dimension) leave the band in pieces
// or show only the label's edges; gradeLinear looks again at a quarter and an eighth, then grows a
// piece along its bars
std::vector<LinearBand> findLinearBandsAt(const cv::Mat& gray, double f, int maxBands);
// the median of `lines` scan lines across the band, 4 samples per pixel, with the quiet zones
std::vector<float> linearProfile(const cv::Mat& gray, const LinearBand& b, int lines);
// an SSCC from a profile (mPrior: samples per module from the band's length); empty when the
// per-position best digits are not confirmed by both check digits (why says which test failed)
std::string decodeSsccProfile(const std::vector<float>& profile, float mPrior, std::string* why);
// every 1D code found this way, up to maxCodes, in image pixels; `accept` (when set) is the job's
// type and 1D rules on a copy of the text, so a read the job would drop does not use up maxCodes
// cr_code.quiet_zone_ok of a reported code (corners in gray's pixels, 0 -> 1 the reading direction)
int quietZoneOk(const cv::Mat& gray, const Code& c);
// an EAN / UPC read (corners in gray's pixels) holds on the image: scan lines across its bars read
// it again after the ISO/IEC 15420 reference decode (edge to similar edge: print gain does not
// change it), at least one gives its text and none another. A zxing read whose bar widths are
// thrown by print gain can give another number with a valid check digit (a misread, line safety).
// True for other types. addonHolds (when given and c has an add-on): 1 when the EAN-2 / EAN-5 add-on
// is read again the same way, 7 to 12 modules past a scan line's main symbol that read c's text, on
// at least one line and as another add-on on none; else 0
bool eanReadHolds(const cv::Mat& gray, const Code& c, int* addonHolds = nullptr);
// the 1D outputs of every reported code: angle, check_digit_ok, quiet_zone_ok
void finishLinear(const cv::Mat& gray, std::vector<Code>& codes, int checkDigit);
std::vector<LinearRead> readLinear(const cv::Mat& gray, int formats, int maxCodes, std::chrono::steady_clock::time_point deadline,
                                   const std::function<bool(int, std::string)>& accept = {});

// ---- PDF417 with damaged rows (pdf417.cpp): codewords fused over each row's scan lines, damaged
// module columns masked, unclear codewords erased, Reed-Solomon over GF(929) decides; the text
// from zxing on the corrected symbol redrawn clean. Same contract as readLinear (CR_STEP_LINEAR).
std::vector<LinearRead> readPdf417Fused(const cv::Mat& gray, int maxCodes, std::chrono::steady_clock::time_point deadline,
                                        const std::function<bool(int, std::string)>& accept = {});
// a zxing PDF417 read cross-checked (pdf417.cpp): the symbol at zxing's corners read again with
// 2 check codewords to spare must give the same text; when it cannot be read so, zxing's unused
// error correction (uec, -1 unknown) must leave 2 check codewords. False: no read (a miscorrection
// is NG, never a pass). gray as the camera gave it; lightOnDark: the print is light on dark.
bool pdf417ReadHolds(const cv::Mat& gray, const cv::Point2f quad[4], const std::string& text, float uec, bool lightOnDark = false);
// Reed-Solomon over GF(929) (PDF417): cw = data then nCheck check codewords, erasures = positions
// whose value is unknown. Corrects in place; false when it cannot or would use more than
// nCheck - reserve check codewords (2 x errors + erasures), leaving cw untouched
bool rs929Correct(std::vector<int>& cw, const std::vector<int>& erasures, int nCheck, int reserve, int* errors = nullptr);
// the 17 modules (bit 16 first, 1 = bar) of a PDF417 codeword in cluster 0, 1, 2 (= 0, 3, 6); -1 if none
int pdf417Pattern(int cluster, int value);

// ---- Micro QR grid fit (microqr.cpp): blurred or slanted Micro QR (M2-M4) read through its module
// grid, fitted on the finder, separator and timing patterns, the blur undone in module space;
// zxing on the redrawn code decides. Same contract as readLinear (CR_STEP_GRIDFIT).
std::vector<LinearRead> readMicroQrFit(const cv::Mat& gray, int maxCodes, std::chrono::steady_clock::time_point deadline,
                                       const std::function<bool(int, std::string)>& accept = {});

// ---- Pharmacode (pharmacode.cpp): Laetus one-track, 2-16 bars, values 3-131070
// the value of the bars in profile samples [codeFrom, codeTo) (4 samples per pixel, -1 = off the
// image), or -1 when the bars are not a clean Pharmacode with clear quiet zones inside the bar-count
// lock (0 = no bound); reverse = the bar on the left weighs least. why says which test failed
int decodePharmacodeProfile(const std::vector<float>& profile, float codeFrom, float codeTo, int minBars, int maxBars,
                            bool reverse, int* bars, std::string* why);
// Pharmacodes in the image (formats: CR_PHARMACODE and/or CR_PHARMACODE_REV), up to maxCodes, read
// in `direction` (cr_pharma_direction: across or down the image; any other value reads none)
std::vector<LinearRead> readPharmacode(const cv::Mat& gray, int formats, int direction, int minBars, int maxBars, int maxCodes,
                                       std::chrono::steady_clock::time_point deadline,
                                       const std::function<bool(int, std::string)>& accept = {});

// ---- DotCode (dotcode.cpp)
// stream position -> grid index (row-major, top row first) of a w x h symbol
std::vector<int> dotcodeOrder(int w, int h);
// the 9-dot pattern of a codeword value (Annex C), -1 outside 0..112
int dotcodePattern(int value);
// data codewords of a w x h symbol (fixed by its size: the encoder pads to the last that fits)
int dotcodeDataLength(int w, int h);
// data codewords (unmasked) to text after Annex F; false for a malformed message, a Structured
// Append part or a reader-programming symbol. gs1 = the message is GS1 (FNC1 sent as ASCII 29)
bool decodeDotCodeMessage(const std::vector<int>& codewords, std::string* text, bool* gs1);
// a w x h grid of dots (1 = printed, row-major) to text; corrected = codewords Reed-Solomon fixed;
// uec = unused error correction after ISO/IEC 15415, of the worst block: 1 - (2 e + f) / (checks -
// spare), the spare check words kept for misdecode protection as 15415's p
bool decodeDotCodeGrid(const std::vector<uint8_t>& dots, int w, int h, std::string* text, int* corrected, bool* gs1 = nullptr,
                       float* uec = nullptr);
// the grid of a symbol carrying these data codewords (padded to the size), for tests; empty when
// they do not fit
std::vector<uint8_t> dotcodeSymbol(std::vector<int> data, int w, int h, int mask);
// DotCodes in the image, up to maxCodes
std::vector<LinearRead> readDotCode(const cv::Mat& gray, int maxCodes, std::chrono::steady_clock::time_point deadline,
                                    const std::function<bool(int, std::string)>& accept = {});

// ---- Learned state in and out of a text blob (learned.cpp), one product's, saved between shifts.
// Only what is safe to carry (step tables, zone map, sizes, whole-pass cost, encoding as a hint).
std::string learnedToText(const StepStats& s);
// Rejects a blob of another version or engine layout, leaving s untouched; error says why.
bool learnedFromText(StepStats& s, const std::string& text, std::string* error);


// Every frame decoded and the reads united; regions unread in every frame fused and read.
Result decodeFrames(const std::vector<cv::Mat>& frames, const cr_params& p, const Models& models, StepStats* learned);

} // namespace cr
