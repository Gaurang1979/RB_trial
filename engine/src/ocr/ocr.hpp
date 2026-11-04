// ocr.hpp - internals of the OCR / OCV tool (vs_ocr.h).
//
// The pipeline for one image:
//   findLines   region -> text lines, each with its rotation, slant and a straightened 32 px high image
//   Recognizer  straightened line -> per-column character probabilities (CTC, ocrnet.onnx)
//   greedyRead  probabilities -> free read with a confidence per character
//   checkLine   probabilities + expected pattern -> per-character scores (CTC forced alignment)
//   scorePrint  checked line -> print quality per character (contrast, ink, pitch) against limits
//               learned from good parts, and extra print beyond the expected text
#pragma once

#include <opencv2/core.hpp>
#include <opencv2/dnn.hpp>

#include <chrono>
#include <cstdint>
#include <ctime>
#include <map>
#include <string>
#include <unordered_map>
#include <vector>

namespace ocr {

constexpr int kLineH = 32;    // height of a straightened line, the recogniser's input height
constexpr int kStride = 4;    // recogniser output: one column of probabilities per 4 input columns
constexpr int kPrintInkjet = 3;   // vs_print VS_PRINT_INKJET
constexpr float kMaxMargin = 99;  // verification margins are clamped to +-this (a wildcard: +, not aligned: -)

// ---- Text-line layout prior (layout.cpp): the block's lines top to bottom, each as its groups of
// characters apart by word gaps ("2 2 2-4"), lines apart by "|" ("6 | 4 2").
struct LayoutLine {
    std::vector<std::pair<int, int>> groups;  // characters per group: least, most
    int minChars() const;
    int maxChars() const;
    std::string text() const;                 // "2 2 2-4"
};
struct Layout {
    std::string source;                       // normalised: "2 2 2-4 | 6"
    std::vector<LayoutLine> lines;
};
bool parseLayout(const std::string& src, Layout& out, std::string& err);
// the layout of these lines' text (groups apart by spaces), exact counts: learned at Teach
std::string layoutOf(const std::vector<std::u32string>& lines);
// "" when a line's text (a space where a word gap was seen) fits its layout, else why not
std::string layoutMismatch(const std::u32string& text, const LayoutLine& l);
// a format (format.cpp) of any characters, as many per group as the layout allows: {?:2} {?:2} {?:2-4}
std::string layoutFormat(const LayoutLine& l);

struct FindParams {
    cv::Rect roi;             // already clipped to the image
    int print = 0;            // vs_print
    int polarity = 0;         // vs_polarity
    float charHeight = 0;     // capital height in image px; 0 = estimate
    int maxLines = 8;
    float maxSkewDeg = 15;
    float dotSize = 0;        // dot diameter in image px; 0 = from the character height
    float join = 1;           // join strength: closing size of dots (and grain on inkjet) times this
    // the block's layout (nullptr = none): pieces of one line that a wide gap split are joined when
    // the layout lists fewer lines than were found and the joined line fits one of its lines
    const Layout* layout = nullptr;
    // dot print spaced tight (the gap between characters no wider than a gap between dots of one
    // character): dots are joined along the line only within a character's dot gap
    bool tight = false;
    // the line finder gives up (finds no line, so a check fails) after this: a region of noise, or a
    // character height set far too small, makes every speck a letter to follow
    std::chrono::steady_clock::time_point deadline = std::chrono::steady_clock::time_point::max();
};

struct Line {
    cv::Mat image;            // kLineH rows, width a multiple of kStride, text dark on light, 8-bit
    cv::Matx23d toImage;      // (u, v, 1) in the line image -> (x, y) in the full image
    cv::Point2f corners[4];   // TL, TR, BR, BL in the full image
    float angleDeg = 0, slantDeg = 0, charHeight = 0;
    bool lightOnDark = false;
    float rawLo = 0, rawHi = 255;   // grey levels the line image was stretched from (1st and 99th percentile)
    // the text may go on past the line's ends: letters in line with it just beyond a gap too wide to
    // join, or a letter cut by the region's edge. Its read is never trusted (scored below min quality)
    bool cutShort = false;
    // a line the layout joined (FindParams::layout): each piece's columns in the line image. Every piece
    // must hold characters of the line's read or check, else the line fails as a line cut short does:
    // print joined in beside the line (a smear, a ghost) is never let through unread
    std::vector<std::pair<float, float>> pieces;
};

// Text lines in the region, top to bottom. dots = join dot-matrix dots before finding lines.
std::vector<Line> findLines(const cv::Mat& gray, const FindParams& p, bool dots);
// Capital height estimate (px) when the job did not set one; 0 = no text found.
float estimateCharHeight(const cv::Mat& gray, const FindParams& p);
// Dot print in the region (tool spec 7.5): median dot diameter in image px (pitch = median distance to
// the nearest dot); 0 when the region shows no dot print (err says why).
float measureDots(const cv::Mat& gray, const FindParams& p, float& pitch, std::string& err);

// Character list of the recogniser: index 0 = CTC blank, 1.. = characters.
struct Charset {
    std::vector<char32_t> chars;                 // chars[0] = 0 (blank)
    std::unordered_map<char32_t, int> index;
    bool load(const std::string& path, std::string& err);
    int find(char32_t c) const { auto it = index.find(c); return it == index.end() ? -1 : it->second; }
};

class Recognizer {
public:
    bool load(const std::string& onnx, const std::string& charset, std::string& err);
    // Load from the decrypted ONNX bytes already in memory (the model never has to exist as a
    // plaintext file; the charset is still a path, it is not a secret). Empty buffer -> false.
    bool loadBuffer(const uint8_t* onnx, size_t onnxSize, const std::string& charset, std::string& err);
    bool ready() const { return ready_; }
    const Charset& charset() const { return cs_; }
    // Log probabilities, T x C (CV_32F), T = width / kStride.
    cv::Mat run(const cv::Mat& line);
private:
    // Validate a freshly built net against the charset and keep it (common to both loads).
    bool finishLoad(cv::dnn::Net net, const std::string& charset, std::string& err);
    cv::dnn::Net net_;
    Charset cs_;
    bool ready_ = false;
};

// ---- Classical OCR from a taught font (font.cpp): the same T x C log probabilities as Recognizer,
// with a charset of blank, space and the taught characters.
// Columns of a line image holding ink, as runs; wantCount > 0 (teaching) cuts them into exactly that
// many characters, or returns none when they do not fit it. profOut gets the ink per column.
std::vector<cv::Range> segmentLine(const cv::Mat& line, bool dots, float typicalWidth, int wantCount, std::vector<int>* profOut = nullptr);
// a run cut into k pieces at the weakest columns near its even split points
std::vector<cv::Range> cutRun(const std::vector<int>& prof, const cv::Range& r, int k);
// CIJ prints every character in a cell of one width (the pitch): the cells covering the line's ink,
// empty ones included, from the ink per column and the taught character width; none when the line
// shows no steady pitch (then characters are cut by their strokes). pitchOut gets the pitch found.
std::vector<cv::Range> pitchCells(const std::vector<int>& prof, float width, float* pitchOut = nullptr);

// A character the taught font read (Font::run): where it was cut and how well it fits.
struct FontCut {
    int t = 0;                // its row in the log-probability matrix
    cv::Range cols;           // its columns in the line image
    float fit = 0;            // 0..1: how well the closest taught character fits (0 = like none taught)
};

class Font {
public:
    Font() { rebuild(); }
    void clear();
    // adds samples from one straightened line and the text printed on it (spaces ignored): the number
    // of characters taught, or -1 when the line does not cut into that many (err says so)
    int teach(const cv::Mat& line, const std::u32string& text, bool dots, std::string& err);
    // adds one sample: the character c printed in columns `cols` of the line ("Teach this character");
    // 1, or -1 when it clearly looks like another taught character (err says so)
    int teachCell(const cv::Mat& line, const cv::Range& cols, char32_t c, bool dots, std::string& err);
    // gives up (a blank line: nothing read, so a check fails) when the deadline passes; cuts gets each
    // character read (spaces left out), left to right
    cv::Mat run(const cv::Mat& line, bool dots,
                std::chrono::steady_clock::time_point deadline = std::chrono::steady_clock::time_point::max(),
                std::vector<FontCut>* cuts = nullptr) const;
    const Charset& charset() const { return cs_; }
    bool ready() const { return !samples_.empty(); }
    int glyphs() const;                       // distinct characters taught
    int samples() const { return int(samples_.size()); }
    std::string save() const;                 // text: a header line, then one line per sample
    bool load(const std::string& blob, std::string& err);
    cv::Mat prepare(const cv::Mat& line, bool dots) const;      // dot print melted into strokes
    // how far dot print is melted (Gaussian sigma, line image px; 1.7 by default): from the dot size and
    // join strength (vs_ocr_params)
    void setMelt(float sigma) { melt_ = sigma; }
    // dot print spaced tight (FindParams::tight): the pitch is found on dots not joined along the line,
    // and print is melted less along it
    void setTight(bool tight) { tight_ = tight; }
    static std::vector<cv::Mat> sheared(const cv::Mat& line);   // the line at a few small shears
    std::vector<float> scores(const std::vector<cv::Mat>& lines, const cv::Range& r) const; // best correlation per class
private:
    struct Sample {
        char32_t c = 0;
        float width = 0;                      // columns in the line image
        std::vector<uint8_t> raw;             // the cell as taught (saved)
        std::vector<float> cell;              // zero mean, unit length
        // dot print cut on its pitch: each dot place of the character's cell, 0..255 = no dot..a whole
        // dot (dotRows x dotCols, row by row; saved); empty when taught otherwise
        int dotRows = 0, dotCols = 0;
        std::vector<uint8_t> dots;
    };
    void rebuild();
    Sample sampleOf(const cv::Mat& line, const cv::Range& cols, char32_t c) const;
    // dot print: how far each taught character's dots are from those read in a cell (log-likelihood,
    // 0 = every dot as taught; lower = dots missing or extra), per class; NaN where none was taught on dots
    std::vector<float> dotLikelihood(const std::vector<uint8_t>& read, int rows, int cols) const;
    // lowers the scores (each, per class) of the characters whose dots fit those read clearly worse
    // than the best-scoring character's; when another character's dots fit clearly better, the two tie
    void dotCheck(std::vector<float>& each, const std::vector<uint8_t>& read, int rows, int cols) const;
    int dotCols() const;                      // dot columns per pitch the font was taught with (0: none)
    // the taught character a new sample clearly looks like instead of its own; 0 = none
    char32_t looksLike(const Sample& s) const;
    // the same on dot print, by the dots: a character whose taught dots fit the sample's clearly
    // better than its own class's (an I print typed as l); 0 = none
    char32_t dotsLookLike(const Sample& s) const;
    void keep(std::vector<Sample> fresh);
    std::vector<Sample> samples_;
    Charset cs_;
    float width_ = 0;                         // median taught width
    // per class: the dots of all its samples taught on dot print together (a dot missing from one
    // teaching print is there in another), rows x cols; empty where none was
    struct DotTemplate { int rows = 0, cols = 0; std::vector<uint8_t> on; };
    std::vector<DotTemplate> dotT_;
    float wide_ = 0;                          // a wide character's taught width (4 in 5 are no wider): the pitch's scale
    float good_ = 0.9f;                       // correlation that scores full (rebuild)
    float melt_ = 1.7f;                       // prepare: blur of dot print, line image px
    bool tight_ = false;                      // setTight
};

struct ReadChar {
    char32_t c = 0;
    float conf = 0;           // 0..1
    int t0 = 0, t1 = 0;       // first and last column (output steps)
};
std::vector<ReadChar> greedyRead(const cv::Mat& logp, const Charset& cs);

// ---- Read text safety (vs_ocr_set_agreement): how sure the reader is of the whole line, and a second reader.
// P(text | line) summed over every CTC alignment, spaces counted as blanks (they are never checked): a dropped
// "." or an extra letter lowers it, which the lowest character confidence never shows. 0 when the charset
// lacks a character of the text or the text is empty.
float linePosterior(const cv::Mat& logp, const Charset& cs, const std::u32string& text);
// Line confidence 0..100 from the posterior: 100 * P^(1 / kLineGamma). Gamma fitted on the synthetic
// validation sets (eval_ocr --make --seed 901, --fit-gamma, second reader the shifted re-read) as the most
// lenient at which no wrong read the print decides scores >= 60; tested on --seed 900 and the real Harpic lines
constexpr float kLineGamma = 0.9f;
float lineConfidence(float posterior);
// The line moved right by half a recogniser step (2 px), padded with its own background, width a multiple of
// kStride: the same reader on it is the second read when no independent second reader is there
cv::Mat shiftedLine(const cv::Mat& line);

// Expected-line pattern after expansion: one or more candidate strings (TIME windows, DATE ranges).
// A code point of 0 marks a wildcard (any one character). Spaces are dropped: they are never checked.
struct Pattern {
    std::string source;
    std::vector<std::u32string> candidates;
};
bool expandPattern(const std::string& utf8, std::time_t clock, Pattern& out, std::string& err);

struct CheckChar {
    char32_t expected = 0;    // 0 = wildcard
    char32_t found = 0;
    float score = 0;          // 0..100
    int status = 0;           // vs_char_status
    int t0 = -1, t1 = -1;     // columns it was aligned to
    // verification margin (tool spec 7.3.3), nats: log P(expected) - log P(best rival character or nothing)
    // at its columns; reported only, the pass rule does not use it
    float margin = 0;
    // print quality (scorePrint); score above is the lowest of recognition and these
    float recognition = 0;    // the recogniser's score 0..100
    bool measured = false;    // false: not aligned, nothing measured
    float contrast = 0;       // grey levels between ink and paper in the camera image
    float ink = 0;            // ink pixels in its cell of the line image
    float centre = 0;         // ink centre, line image columns
    float pitch = 0;          // distance to the previous character's centre; 0 for the first
    float qContrast = 100, qInk = 100, qPitch = 100;   // 0..100 against the learned limits; 100 = not learned
    int reason = 0;           // vs_print_reason of the lowest score
    bool learned = false;     // a learned limit applied
};
struct CheckResult {
    std::u32string expected;  // the candidate that matched best
    std::vector<CheckChar> chars;
    std::vector<ReadChar> extras;   // read beyond the ends of the expected text (scorePrint)
    float quality = 0;        // lowest character score
    float margin = kMaxMargin;  // lowest character margin
    bool pass = false;
    // position (scoreShift): the first measured character's ink centre, image px from the region's corner
    bool posKnown = false;
    float posX = 0, posY = 0;
    int shiftState = 0;       // vs_shift_state
    float shift = -1, shiftX = 0, shiftY = 0;
    bool passPrint = false;   // pass before the shift check (a part failing only on an unlearned position is learnable)
};
CheckResult checkLine(const cv::Mat& logp, const Charset& cs, const Pattern& pat,
                      const std::vector<ReadChar>& read, float minQuality);

// ---- Reading under a format (format.cpp, Read text mode): vs_ocr_set_format syntax.
struct FormatPos {
    int kind = 0;             // 0 one fixed character (letters in either case), 1 digit, 2 letter, 3 letter or digit, 4 any
    char32_t ch = 0;          // kind 0
};
struct Format {
    std::string source;
    std::vector<FormatPos> pos;               // character positions
    std::vector<std::vector<int>> follow;     // positions that may come after each
    std::vector<int> first;                   // positions that may start the line
    std::vector<char> last;                   // 1 = may end the line
};
bool compileFormat(const std::string& utf8, Format& out, std::string& err);

struct FormatRead {
    std::u32string text;      // the line read under the format (a space where a word gap was seen)
    std::vector<CheckChar> chars;  // expected = the character read under the format, found = the free read there;
                                   // marks the format has no room for are VS_CHAR_EXTRA
    float quality = 0;        // lowest character score
    bool fits = false;        // the line has columns enough for the format
    bool pass = false;        // fits, no extra mark, every character at or above minQuality (never below 50)
    bool freeFits = false;    // the free read is already these characters (case ignored)
};
FormatRead readFormat(const cv::Mat& logp, const Charset& cs, const Format& f, const std::vector<ReadChar>& read,
                      float minQuality);

// Print quality learned from good parts: mean and spread of each measure (tool spec 1, auto-tolerance:
// limits at mean +- k sigma). Kept with the job as text.
struct Stat {
    double n = 0, s = 0, ss = 0;
    void add(double x) { n += 1; s += x; ss += x * x; }
    double mean() const { return n > 0 ? s / n : 0; }
    double sd() const;
};
class PrintRef {
public:
    static constexpr int kMinParts = 10;      // limits apply from this many good parts (tool spec 1: 10-30)
    static constexpr int kMinSamples = 5;     // and this many samples of the measure
    double k = 4;
    int parts = 0;
    std::map<char32_t, Stat> ink, contrast;   // per character; contrast[0] = every character
    std::map<std::string, Stat> pitch;        // "line/index/length" -> distance to the previous character
    std::map<int, Stat> posX, posY;           // expected line -> where its first character sits (scoreShift)
    bool active() const { return parts >= kMinParts; }
    void clear() { *this = PrintRef(); }
    void add(const CheckResult& cr, int line);
    std::string save() const;
    bool load(const std::string& text, std::string& err);
    static std::string pitchKey(int line, size_t index, size_t length);
};
// Measures every aligned character of a checked line and scores it against ref (when learned); adds
// extra print beyond the expected text; updates the scores, statuses, quality and pass.
void scorePrint(const Line& line, const std::vector<ReadChar>& read, CheckResult& cr, const PrintRef& ref,
                int lineNo, float minQuality);
// Where the line sits (Allow shift, tool spec 7.3): measures the first character's position from the
// region's corner (roiOrigin) and, with allowPx > 0, fails the line farther than allowPx from the learned
// position, or with no position learned for lineNo. Call after scorePrint.
void scoreShift(const Line& line, CheckResult& cr, const PrintRef& ref, int lineNo, cv::Point2f roiOrigin, float allowPx);

std::u32string fromUtf8(const std::string& s);
std::string toUtf8(const std::u32string& s);

} // namespace ocr
