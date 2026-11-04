// adversarial - writes the bench's adversarial batch: synthetic cases on which the reader must
// give no read, or only the codes that are printed (study 04b, 5.0.4; wave 0 of chapter 06).
// Nothing here comes from customer images: every case is drawn from a fixed seed, so the batch
// is the same on every machine and run.
//
// Usage: adversarial <out-folder>     writes <out>/*.png, <out>/operator.csv (file,format,expected,note)
//                                     and <out>/codes.csv (file,sha256,text: the printed codes)
//        adversarial --self-test      checks the SHA-256 against the FIPS 180-4 examples
//
// Per format (QR from qrcodegen, Data Matrix from zint, not from the engine's own encoders), a
// 6 x 4 case of product A's serial run read with the count set to 24, as on the line:
//   00, 01  clean cases: the carried reader learns product A (encoding, serial run)
//   02  serial-run gap trap: one slot carries a serial outside the run (SN..960 where the run
//       expects SN..160) under a large blot; filling the gap from the run is a wrong read
//   03  one module off: 1-4 modules flipped in each code's codewords (error correction fixes them)
//   04  codewords off: each code takes 1 .. (D-1)/2 of the D codewords in which it differs from
//       a one-digit neighbour that is not on the case; it stays closer to its own text
//   05  beyond capacity: t+1 .. t+4 random codeword errors; the decoders must refuse, never
//       miscorrect
//   06, 07  another product's codes (product B, same layout), clean and under blots
//   08  near-duplicates: serials one digit apart, out of order, light damage
//   09  damage: scratches, blots, heavy blur, low contrast, a code cut by the image edge
//   10  no code: noise texture
//   11  no code: symbols with real finders, timing and format information and random codewords
//   12  no code: printed text and lines
//   13  no code: checkerboards and halftone dots at the module size

#include "codereader.h"
#include "qrcodegen.hpp"
#include "reader.hpp"
#include "sha256.hpp"

#include <ZXing/Barcode.h>
#include <ZXing/CreateBarcode.h>
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <functional>
#include <string>
#include <vector>

namespace fs = std::filesystem;
using qrcodegen::QrCode;
using qrcodegen::QrSegment;

namespace {

constexpr int kCols = 6, kRows = 4, kCell = 260; // a 24-code case: 6 x 4 labels
const char* const kQrA = "VSA-2026-L07-SN";        // + 6-digit serial (alphanumeric mode)
const char* const kQrB = "VSB-2026-L03-SN";
const char* const kDmA = "VSA2026L07SN";
const char* const kDmB = "VSB2026L03SN";

struct Symbol {
    cv::Mat mods;          // 1 = dark, no quiet zone
    cr::CodewordMap map;   // codeword cells, for damage by codeword
    int ecc = 0;           // check codewords per block (first block)
};

std::string serialText(bool qr, bool productA, int serial)
{
    char s[8];
    std::snprintf(s, sizeof s, "%06d", serial);
    return std::string(qr ? (productA ? kQrA : kQrB) : (productA ? kDmA : kDmB)) + s;
}

// QR at EC level M; mask < 0 lets qrcodegen choose
Symbol makeQr(const std::string& text, int mask = -1, int* maskOut = nullptr)
{
    const QrCode q = QrCode::encodeSegments(QrSegment::makeSegments(text.c_str()), QrCode::Ecc::MEDIUM, 1, 40, mask, false);
    Symbol s;
    const int n = q.getSize();
    s.mods = cv::Mat(n, n, CV_8U);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) s.mods.at<uint8_t>(y, x) = q.getModule(x, y) ? 1 : 0;
    if (maskOut) *maskOut = q.getMask();
    cr::codewordMap(CR_QR, n, n, "M", s.map);
    s.ecc = s.map.blockEcc.empty() ? 0 : s.map.blockEcc[0];
    return s;
}

Symbol makeDm(const std::string& text)
{
    const auto bc = ZXing::CreateBarcodeFromText(text, ZXing::CreatorOptions(ZXing::BarcodeFormat::DataMatrix));
    const auto iv = bc.symbol();
    Symbol s;
    s.mods = cv::Mat(iv.height(), iv.width(), CV_8U);
    for (int y = 0; y < iv.height(); ++y)
        for (int x = 0; x < iv.width(); ++x) s.mods.at<uint8_t>(y, x) = iv.data(x, y)[0] < 128;
    cr::codewordMap(CR_DATAMATRIX, s.mods.cols, s.mods.rows, "", s.map);
    s.ecc = s.map.blockEcc.empty() ? 0 : s.map.blockEcc[0];
    return s;
}

Symbol makeSymbol(bool qr, const std::string& text) { return qr ? makeQr(text) : makeDm(text); }

int getCodeword(const Symbol& s, size_t k)
{
    int v = 0;
    for (int cell : s.map.cells[k]) v = (v << 1) | s.mods.at<uint8_t>(cell / s.mods.cols, cell % s.mods.cols);
    return v;
}

void setCodeword(Symbol& s, size_t k, int v)
{
    const auto& cells = s.map.cells[k];
    for (size_t b = 0; b < cells.size(); ++b)
        s.mods.at<uint8_t>(cells[b] / s.mods.cols, cells[b] % s.mods.cols) = (v >> (cells.size() - 1 - b)) & 1;
}

// the scene: a grey carton with labels; each label is drawn, damaged and placed by itself
struct Scene {
    cv::Mat img = cv::Mat(kRows * kCell + 80, kCols * kCell + 80, CV_32F, cv::Scalar(105));
    std::vector<std::string> texts; // the codes printed on it
};

// a label: modules X px each, 3-module quiet zone, on a light patch
cv::Mat drawLabel(const cv::Mat& mods, float X, float light = 215, float dark = 38)
{
    const int q = 3;
    const int w = static_cast<int>(std::lround((mods.cols + 2 * q) * X)), h = static_cast<int>(std::lround((mods.rows + 2 * q) * X));
    cv::Mat lab(h, w, CV_32F, cv::Scalar(light));
    for (int y = 0; y < mods.rows; ++y)
        for (int x = 0; x < mods.cols; ++x)
            if (mods.at<uint8_t>(y, x))
                cv::rectangle(lab, cv::Point2f((x + q) * X, (y + q) * X), cv::Point2f((x + q + 1) * X - 0.01f, (y + q + 1) * X - 0.01f),
                              cv::Scalar(dark), -1);
    return lab;
}

// a label onto the carton at slot i, turned by a few degrees
void place(Scene& sc, const cv::Mat& lab, int slot, cv::RNG& rng, double maxAngle = 4)
{
    const int col = slot % kCols, row = slot / kCols;
    const cv::Point2f centre(40 + col * kCell + kCell / 2.f + rng.uniform(-8.f, 8.f), 40 + row * kCell + kCell / 2.f + rng.uniform(-8.f, 8.f));
    // turn about the label's centre and move it to the slot (getRotationMatrix2D left imgproc in OpenCV 5)
    const double a = rng.uniform(-maxAngle, maxAngle) * CV_PI / 180, c = std::cos(a), s = std::sin(a);
    const double cx = lab.cols / 2.0, cy = lab.rows / 2.0;
    const cv::Mat M = (cv::Mat_<double>(2, 3) << c, s, centre.x - c * cx - s * cy, -s, c, centre.y + s * cx - c * cy);
    cv::Mat warped, mask;
    cv::warpAffine(lab, warped, M, sc.img.size(), cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(0));
    cv::warpAffine(cv::Mat(lab.size(), CV_32F, cv::Scalar(1)), mask, M, sc.img.size(), cv::INTER_LINEAR, cv::BORDER_CONSTANT, cv::Scalar(0));
    sc.img = sc.img.mul(1 - mask) + warped.mul(mask);
}

// damage drawn on a label (label pixels)
void blot(cv::Mat& lab, double areaFraction, cv::RNG& rng)
{
    const double r = std::sqrt(areaFraction * lab.rows * lab.cols / CV_PI);
    const cv::Point c(rng.uniform(lab.cols / 3, 2 * lab.cols / 3), rng.uniform(lab.rows / 3, 2 * lab.rows / 3));
    const double grey = rng.uniform(0, 2) ? rng.uniform(170, 230) : rng.uniform(25, 60); // light glare or dark smear
    cv::ellipse(lab, c, cv::Size(static_cast<int>(r * 1.2), static_cast<int>(r / 1.2)), rng.uniform(0, 180), 0, 360, cv::Scalar(grey), -1);
}

void scratch(cv::Mat& lab, int n, cv::RNG& rng)
{
    for (int i = 0; i < n; ++i)
        cv::line(lab, cv::Point(rng.uniform(0, lab.cols), 0), cv::Point(rng.uniform(0, lab.cols), lab.rows - 1),
                 cv::Scalar(rng.uniform(0, 2) ? 225 : 40), rng.uniform(2, 6));
}

void finish(Scene& sc, cv::RNG& rng, double blur = 0.9, double noise = 3.0)
{
    cv::GaussianBlur(sc.img, sc.img, cv::Size(0, 0), blur);
    cv::Mat n(sc.img.size(), CV_32F);
    rng.fill(n, cv::RNG::NORMAL, 0, noise);
    sc.img += n;
}

bool save(const fs::path& out, const std::string& name, const char* fmt, const char* note, Scene& sc,
          std::ofstream& op, std::ofstream& codes)
{
    cv::Mat img8;
    sc.img.convertTo(img8, CV_8U);
    if (!cv::imwrite((out / name).string(), img8)) return false;
    op << name << ',' << fmt << ",24," << note << '\n';
    std::sort(sc.texts.begin(), sc.texts.end());
    sc.texts.erase(std::unique(sc.texts.begin(), sc.texts.end()), sc.texts.end());
    for (const auto& t : sc.texts) codes << name << ',' << vsbench::sha256Hex(t) << ',' << t << '\n';
    return true;
}

// one format's scenes; X: module size in pixels
bool writeFormat(const fs::path& out, bool qr, std::ofstream& op, std::ofstream& codes)
{
    const char* fmt = qr ? "qr" : "dm";
    const std::string pre = qr ? "q" : "d";
    const float X = qr ? 5.5f : 7.f;
    cv::RNG rng(qr ? 20261031 : 20261101);
    const auto ok = [&](const std::string& stem, const char* note, Scene& sc) {
        return save(out, pre + stem + ".png", fmt, note, sc, op, codes);
    };
    // a case of product A or B, serials first..first+23, with damage per slot
    const auto runCase = [&](bool productA, int first, const std::function<void(Symbol&, cv::Mat&, int)>& damage) {
        Scene sc;
        for (int i = 0; i < kCols * kRows; ++i) {
            const std::string text = serialText(qr, productA, first + i);
            Symbol s = makeSymbol(qr, text);
            cv::Mat lab;
            damage(s, lab, i);
            if (lab.empty()) lab = drawLabel(s.mods, X);
            place(sc, lab, i, rng);
            sc.texts.push_back(text);
        }
        return sc;
    };
    const auto none = [](Symbol&, cv::Mat&, int) {};

    {
        Scene sc = runCase(true, 101, none);
        finish(sc, rng);
        if (!ok("00-clean-a", "clean product A", sc)) return false;
    }
    {
        Scene sc = runCase(true, 125, none);
        finish(sc, rng);
        if (!ok("01-clean-a", "clean product A", sc)) return false;
    }
    {   // the run 149..172 with SN..960 printed where the run has SN..160, both slots under blots
        Scene sc;
        for (int i = 0; i < kCols * kRows; ++i) {
            const int serial = 149 + i == 160 ? 960 : 149 + i;
            const std::string text = serialText(qr, true, serial);
            cv::Mat lab = drawLabel(makeSymbol(qr, text).mods, X);
            if (serial == 960 || serial == 154) blot(lab, 0.30, rng);
            place(sc, lab, i, rng);
            sc.texts.push_back(text);
        }
        finish(sc, rng);
        if (!ok("02-gap-trap", "slot 11 is SN..960, not the run's SN..160", sc)) return false;
    }
    {
        Scene sc = runCase(true, 173, [&](Symbol& s, cv::Mat&, int i) {
            for (int f = 0; f <= i % 4; ++f) {
                const auto& cells = s.map.cells[static_cast<size_t>(rng.uniform(0, static_cast<int>(s.map.cells.size())))];
                const int cell = cells[static_cast<size_t>(rng.uniform(0, static_cast<int>(cells.size())))];
                s.mods.at<uint8_t>(cell / s.mods.cols, cell % s.mods.cols) ^= 1;
            }
        });
        finish(sc, rng);
        if (!ok("03-module-off", "1-4 modules flipped per code", sc)) return false;
    }
    {   // codewords from a neighbour not on the case: the hundreds digit + 1 (SN000197 -> SN000297)
        Scene sc = runCase(true, 197, [&](Symbol& s, cv::Mat&, int i) {
            const int serial = 197 + i;
            const std::string neighbour = serialText(qr, true, serial + 100);
            int mask = -1; // QR: the code's own mask, so only codeword cells differ
            if (qr) makeQr(serialText(qr, true, serial), -1, &mask);
            const Symbol y = qr ? makeQr(neighbour, mask) : makeDm(neighbour);
            if (y.mods.size() != s.mods.size() || y.map.cells.size() != s.map.cells.size()) return;
            std::vector<size_t> diff;
            for (size_t k = 0; k < s.map.cells.size(); ++k)
                if (getCodeword(s, k) != getCodeword(y, k)) diff.push_back(k);
            const int most = (static_cast<int>(diff.size()) - 1) / 2; // strictly closer to the printed text
            const int take = most > 0 ? 1 + i % most : 0;
            for (int k = 0; k < take; ++k) {
                const size_t pick = static_cast<size_t>(rng.uniform(k, static_cast<int>(diff.size())));
                std::swap(diff[static_cast<size_t>(k)], diff[pick]);
                setCodeword(s, diff[static_cast<size_t>(k)], getCodeword(y, diff[static_cast<size_t>(k)]));
            }
        });
        finish(sc, rng);
        if (!ok("04-codewords-off", "codewords of SN+100 (not on the case); fewer than half", sc)) return false;
    }
    {
        Scene sc = runCase(true, 221, [&](Symbol& s, cv::Mat&, int i) {
            const int errors = s.ecc / 2 + 1 + i % 4; // t + 1 .. t + 4
            std::vector<int> order(s.map.cells.size());
            for (size_t k = 0; k < order.size(); ++k) order[k] = static_cast<int>(k);
            for (int k = 0; k < errors && k < static_cast<int>(order.size()); ++k) {
                std::swap(order[static_cast<size_t>(k)], order[static_cast<size_t>(rng.uniform(k, static_cast<int>(order.size())))]);
                const size_t cw = static_cast<size_t>(order[static_cast<size_t>(k)]);
                setCodeword(s, cw, (getCodeword(s, cw) + rng.uniform(1, 256)) % 256);
            }
        });
        finish(sc, rng);
        if (!ok("05-beyond-capacity", "t+1..t+4 random codeword errors", sc)) return false;
    }
    {
        Scene sc = runCase(false, 101, none);
        finish(sc, rng);
        if (!ok("06-other-product", "product B, clean", sc)) return false;
    }
    {
        Scene sc = runCase(false, 125, [&](Symbol& s, cv::Mat& lab, int i) {
            lab = drawLabel(s.mods, X);
            if (i % 2 == 0) blot(lab, 0.20 + 0.05 * (i % 5), rng);
        });
        finish(sc, rng);
        if (!ok("07-other-product-damaged", "product B under blots", sc)) return false;
    }
    {   // serials one digit apart (last digit), out of order, two printed twice
        static const int serials[24] = {301, 311, 321, 331, 341, 351, 361, 371, 381, 391, 302, 312,
                                        322, 332, 342, 352, 362, 372, 382, 392, 303, 303, 313, 313};
        Scene sc;
        for (int i = 0; i < kCols * kRows; ++i) {
            const std::string text = serialText(qr, true, serials[i]);
            cv::Mat lab = drawLabel(makeSymbol(qr, text).mods, X);
            if (i % 3 == 0) blot(lab, 0.12, rng);
            place(sc, lab, i, rng);
            sc.texts.push_back(text);
        }
        finish(sc, rng);
        if (!ok("08-near-duplicates", "serials one digit apart, two printed twice", sc)) return false;
    }
    {
        Scene sc;
        for (int i = 0; i < kCols * kRows - 1; ++i) {
            const std::string text = serialText(qr, true, 401 + i);
            cv::Mat lab = drawLabel(makeSymbol(qr, text).mods, X, i % 6 == 3 ? 130.f : 215.f, i % 6 == 3 ? 95.f : 38.f);
            switch (i % 6) {
            case 0: scratch(lab, 3, rng); break;
            case 1: blot(lab, 0.25, rng); break;
            case 2: cv::GaussianBlur(lab, lab, cv::Size(0, 0), X * 0.6); break;
            case 4: blot(lab, 0.15, rng); scratch(lab, 2, rng); break;
            default: break;
            }
            place(sc, lab, i, rng);
            sc.texts.push_back(text);
        }
        // the last slot: a code cut by the image's right edge (its left half in the frame); it is
        // printed, so it is in the truth if it is read at all
        const std::string cut = serialText(qr, true, 424);
        const cv::Mat lab = drawLabel(makeSymbol(qr, cut).mods, X);
        const int w = lab.cols / 2;
        lab(cv::Rect(0, 0, w, lab.rows)).copyTo(sc.img(cv::Rect(sc.img.cols - w, 40 + (kRows - 1) * kCell + 30, w, lab.rows)));
        sc.texts.push_back(cut);
        finish(sc, rng, 1.0, 4.0);
        if (!ok("09-damage", "scratches; blots; blur; low contrast; a code cut by the edge", sc)) return false;
    }
    {
        Scene sc;
        rng.fill(sc.img, cv::RNG::UNIFORM, 30, 230);
        cv::GaussianBlur(sc.img, sc.img, cv::Size(0, 0), X * 0.8);
        cv::normalize(sc.img, sc.img, 30, 230, cv::NORM_MINMAX);
        finish(sc, rng);
        if (!ok("10-noise", "no code: noise texture", sc)) return false;
    }
    {   // real finders, timing and format information; every codeword random
        Scene sc;
        for (int i = 0; i < kCols * kRows; ++i) {
            Symbol s = makeSymbol(qr, serialText(qr, true, 501 + i));
            for (size_t k = 0; k < s.map.cells.size(); ++k) setCodeword(s, k, rng.uniform(0, 256));
            place(sc, drawLabel(s.mods, X), i, rng);
        }
        finish(sc, rng);
        if (!ok("11-random-codewords", "no code: finders with random codewords", sc)) return false;
    }
    {
        Scene sc;
        cv::Mat ink(sc.img.size(), CV_8U, cv::Scalar(215)); // putText draws on 8-bit images only
        static const char* const words[] = {"BATCH", "MFG", "EXP", "MRP Rs", "LOT", "NET QTY", "0123456789", "VSA-2026-L07"};
        for (int i = 0; i < 60; ++i)
            cv::putText(ink, std::string(words[rng.uniform(0, 8)]) + " " + std::to_string(rng.uniform(0, 99999)),
                        cv::Point(rng.uniform(0, sc.img.cols - 300), rng.uniform(30, sc.img.rows)), rng.uniform(0, 2) ? cv::FONT_HERSHEY_SIMPLEX : cv::FONT_HERSHEY_DUPLEX,
                        rng.uniform(0.8, 2.2), cv::Scalar(35), rng.uniform(1, 4));
        for (int i = 0; i < 25; ++i)
            cv::line(ink, cv::Point(rng.uniform(0, sc.img.cols), rng.uniform(0, sc.img.rows)),
                     cv::Point(rng.uniform(0, sc.img.cols), rng.uniform(0, sc.img.rows)), cv::Scalar(rng.uniform(30, 120)), rng.uniform(1, 8));
        ink.convertTo(sc.img, CV_32F);
        finish(sc, rng);
        if (!ok("12-print", "no code: printed text and lines", sc)) return false;
    }
    {
        Scene sc;
        const int m = static_cast<int>(X);
        for (int y = 0; y < sc.img.rows; ++y)
            for (int x = 0; x < sc.img.cols; ++x) {
                const bool left = x < sc.img.cols / 2;
                const bool dark = left ? ((x / m + y / m) % 2 == 0)
                                       : std::hypot(x % (2 * m) - m, y % (2 * m) - m) < m * (0.3 + 0.6 * y / sc.img.rows);
                sc.img.at<float>(y, x) = dark ? 40.f : 215.f;
            }
        finish(sc, rng);
        if (!ok("13-checker-halftone", "no code: checkerboard and halftone dots", sc)) return false;
    }
    return true;
}

bool selfTest()
{
    struct { const char* in; const char* hex; } v[] = {
        {"", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},
        {"abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"},
        {"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"}};
    bool good = true;
    for (const auto& t : v) {
        const std::string got = vsbench::sha256Hex(t.in);
        if (got != t.hex) {
            std::printf("sha256(\"%s\") = %s, expected %s\n", t.in, got.c_str(), t.hex);
            good = false;
        }
    }
    std::printf("sha256 self-test %s\n", good ? "passed" : "FAILED");
    return good;
}

} // namespace

int main(int argc, char** argv)
{
    if (argc == 2 && std::string(argv[1]) == "--self-test") return selfTest() ? 0 : 1;
    if (argc != 2) {
        std::fprintf(stderr, "usage: %s <out-folder> | --self-test\n", argv[0]);
        return 2;
    }
    const fs::path out = argv[1];
    fs::create_directories(out);
    std::ofstream op(out / "operator.csv"), codes(out / "codes.csv");
    op << "file,format,expected,note\n";
    codes << "file,sha256,text\n";
    if (!writeFormat(out, true, op, codes) || !writeFormat(out, false, op, codes)) {
        std::fprintf(stderr, "cannot write the adversarial images to %s\n", out.string().c_str());
        return 1;
    }
    return 0;
}
