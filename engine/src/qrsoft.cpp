// qrsoft.cpp - QR soft decoding (versions 1-6) from module means, with Reed-Solomon erasures.
//
// The QR counterpart of softDecodeDm (dmmatch.cpp), after Cognex's "virtual perimeter" idea:
//   - finders, timing and alignment are known, not read;
//   - EC level and mask come from a soft match of the format modules against the references;
//   - data modules are read in placement order, unmasked, each with a confidence (distance of its
//     mean from the dark / light split); codewords are de-interleaved into the RS blocks;
//   - the least certain codewords of a block become erasures; two check symbols stay in reserve
//     (2e + f <= ecc - 2) against miscorrection;
//   - the corrected codewords are redrawn into a clean symbol that must decode (zxing).
// On noisy synthetic codes it reads 60/60 where hard decisions read 44/60 (noise sd 40), never
// wrongly, and nothing from random grids; on matched shrink-wrap codes it verifies 15 of 23 at
// the matched corners where hard decisions verify 9.

#include "reader.hpp"
#include "zxformats.hpp"

#include "qrcodegen.hpp"

#include <ZXing/ReadBarcode.h>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <chrono>
#include <map>
#include <mutex>

namespace cr {

namespace {

using qrcodegen::QrCode;
using qrcodegen::QrSegment;

constexpr int kMaxVersion = 6;
// per EC level (L, M, Q, H) and version: error correction codewords per block, number of blocks
constexpr int kEccPerBlock[4][kMaxVersion + 1] = {
    {-1, 7, 10, 15, 20, 26, 18}, {-1, 10, 16, 26, 18, 24, 16}, {-1, 13, 22, 18, 26, 18, 24}, {-1, 17, 28, 22, 16, 22, 28}};
constexpr int kBlocks[4][kMaxVersion + 1] = {
    {-1, 1, 1, 1, 1, 1, 2}, {-1, 1, 1, 1, 2, 2, 4}, {-1, 1, 1, 2, 2, 4, 4}, {-1, 1, 1, 2, 4, 4, 4}};
constexpr int kRawCodewords[kMaxVersion + 1] = {0, 26, 44, 70, 100, 134, 172};
constexpr int kAlign[kMaxVersion + 1] = {0, 0, 18, 22, 26, 30, 34}; // second alignment coordinate (first is 6)

QrCode::Ecc ecc(int e)
{
    switch (e) {
    case 0: return QrCode::Ecc::LOW;
    case 2: return QrCode::Ecc::QUARTILE;
    case 3: return QrCode::Ecc::HIGH;
    default: return QrCode::Ecc::MEDIUM;
    }
}

// the mask inverts module (x, y) when this is true
bool masked(int mask, int x, int y)
{
    switch (mask) {
    case 0: return (x + y) % 2 == 0;
    case 1: return y % 2 == 0;
    case 2: return x % 3 == 0;
    case 3: return (x + y) % 3 == 0;
    case 4: return (x / 3 + y / 2) % 2 == 0;
    case 5: return x * y % 2 + x * y % 3 == 0;
    case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
    default: return ((x + y) % 2 + x * y % 3) % 2 == 0;
    }
}

// modules that are not data: finders + separators + format areas, timing, alignment
std::vector<uint8_t> functionModules(int version)
{
    const int n = 17 + 4 * version;
    std::vector<uint8_t> f(static_cast<size_t>(n) * n, 0);
    auto set = [&](int y, int x) { f[static_cast<size_t>(y) * n + x] = 1; };
    for (int y = 0; y < 9; ++y)
        for (int x = 0; x < 9; ++x) set(y, x);
    for (int y = 0; y < 9; ++y)
        for (int x = n - 8; x < n; ++x) set(y, x);
    for (int y = n - 8; y < n; ++y)
        for (int x = 0; x < 9; ++x) set(y, x);
    for (int i = 0; i < n; ++i) {
        set(6, i);
        set(i, 6);
    }
    if (version >= 2) {
        const int a = kAlign[version];
        for (int y = a - 2; y <= a + 2; ++y)
            for (int x = a - 2; x <= a + 2; ++x) set(y, x);
    }
    return f;
}

// data module positions (y * n + x) in codeword-bit order: two-column zigzag from the bottom right
std::vector<int> placementOrder(int version, const std::vector<uint8_t>& fn)
{
    const int n = 17 + 4 * version;
    std::vector<int> out;
    for (int right = n - 1; right >= 1; right -= 2) {
        if (right == 6) right = 5;
        for (int vert = 0; vert < n; ++vert)
            for (int j = 0; j < 2; ++j) {
                const int x = right - j;
                const bool upward = ((right + 1) & 2) == 0;
                const int y = upward ? n - 1 - vert : vert;
                if (!fn[static_cast<size_t>(y) * n + x]) out.push_back(y * n + x);
            }
    }
    return out;
}

// a reference symbol of this version, EC level and mask: its function modules and format bits
const std::vector<uint8_t>& reference(int version, int e, int mask)
{
    static std::mutex mu;
    static std::map<int, std::vector<uint8_t>> cache;
    const int key = (version * 4 + e) * 8 + mask;
    std::lock_guard<std::mutex> lock(mu);
    auto it = cache.find(key);
    if (it != cache.end()) return it->second;
    const QrCode q = QrCode::encodeSegments({QrSegment::makeBytes({'0'})}, ecc(e), version, version, mask, false);
    const int n = q.getSize();
    std::vector<uint8_t> m(static_cast<size_t>(n) * n);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) m[static_cast<size_t>(y) * n + x] = q.getModule(x, y) ? 1 : 0;
    return cache[key] = std::move(m);
}

std::string decodeQrBits(const std::vector<uint8_t>& dark, int n)
{
    constexpr int cell = 6, qz = 4;
    cv::Mat img((n + 2 * qz) * cell, (n + 2 * qz) * cell, CV_8U, cv::Scalar(255));
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x)
            if (dark[static_cast<size_t>(y) * n + x]) img(cv::Rect((x + qz) * cell, (y + qz) * cell, cell, cell)).setTo(0);
    ZXing::ReaderOptions o;
    o.setFormats(ZXing::BarcodeFormat::QRCode);
    o.setIsPure(true);
    o.setTryRotate(false);
    o.setTryDownscale(false);
    o.setBinarizer(ZXing::Binarizer::BoolCast);
    o.setTextMode(ZXing::TextMode::Plain);
    const auto r = ZXing::ReadBarcode(ZXing::ImageView(img.data, img.cols, img.rows, ZXing::ImageFormat::Lum), o);
    return ownGridText(r);
}

} // namespace

namespace {

// the soft decode proper: v dark low, t the dark / light split, spread the unit of confidence
std::string softDecodeQrCore(const cv::Mat& v, double t, double spread, int version, int eclHint);

} // namespace

std::string softDecodeQrEvidence(const cv::Mat& evidence, int version)
{
    if (version < 1 || version > kMaxVersion) return {};
    const int n = 17 + 4 * version;
    if (evidence.rows != n || evidence.cols != n) return {};
    cv::Mat v;
    evidence.convertTo(v, CV_32F, -1.0); // dark low, split at zero, confidence = |evidence|
    return softDecodeQrCore(v, 0.0, 1.0, version, -1);
}

std::string softDecodeQr(const cv::Mat& means32, int version, int eclHint)
{
    if (version < 1 || version > kMaxVersion) return {};
    const int n = 17 + 4 * version;
    if (means32.rows != n || means32.cols != n) return {};
    cv::Mat v;
    means32.convertTo(v, CV_32F);
    // the dark / light split: Otsu classes, midway between their means
    cv::Mat v8, scratch;
    cv::normalize(v, v8, 0, 255, cv::NORM_MINMAX, CV_8U);
    double mn, mx;
    cv::minMaxLoc(v, &mn, &mx);
    const double cut = mn + cv::threshold(v8, scratch, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU) / 255.0 * (mx - mn);
    double sD = 0, sL = 0;
    int nD = 0, nL = 0;
    for (int i = 0; i < n * n; ++i) {
        const float m = v.at<float>(i / n, i % n);
        if (m <= cut) { sD += m; ++nD; } else { sL += m; ++nL; }
    }
    if (!nD || !nL) return {};
    const double t = (sD / nD + sL / nL) / 2;
    cv::Scalar mean, sd;
    cv::meanStdDev(v, mean, sd);
    return softDecodeQrCore(v, t, sd[0] + 1e-6, version, eclHint);
}

namespace {

std::string softDecodeQrCore(const cv::Mat& v, double t, double spread, int version, int eclHint)
{
    const int n = 17 + 4 * version;
    // EC level and mask: soft match of the format modules (both copies)
    std::vector<int> fmtPos;
    for (int x = 0; x <= 8; ++x)
        if (x != 6) fmtPos.push_back(8 * n + x);
    for (int y = 0; y <= 7; ++y)
        if (y != 6) fmtPos.push_back(y * n + 8);
    for (int x = n - 8; x < n; ++x) fmtPos.push_back(8 * n + x);
    for (int y = n - 7; y < n; ++y) fmtPos.push_back(y * n + 8);
    std::vector<std::tuple<double, int, int>> cands; // score, ecl, mask
    for (int e = 0; e < 4; ++e) {
        if (eclHint >= 0 && e != eclHint) continue;
        for (int m = 0; m < 8; ++m) {
            const auto& ref = reference(version, e, m);
            double sc = 0;
            for (int pos : fmtPos) sc += (t - v.at<float>(pos / n, pos % n)) * (ref[static_cast<size_t>(pos)] ? 1 : -1);
            cands.emplace_back(sc, e, m);
        }
    }
    std::sort(cands.begin(), cands.end(), [](const auto& a, const auto& b) { return std::get<0>(a) > std::get<0>(b); });

    static const std::vector<uint8_t> fnCache[kMaxVersion + 1] = {
        {}, functionModules(1), functionModules(2), functionModules(3), functionModules(4), functionModules(5), functionModules(6)};
    const auto& fn = fnCache[version];
    const std::vector<int> order = placementOrder(version, fn);
    const int total = kRawCodewords[version];

    for (size_t ci = 0; ci < std::min<size_t>(3, cands.size()); ++ci) {
        const int e = std::get<1>(cands[ci]), mask = std::get<2>(cands[ci]);
        const int nb = kBlocks[e][version], el = kEccPerBlock[e][version];
        const int nShort = nb - total % nb, shortLen = total / nb;
        // read the codewords in interleaved order, with the confidence of their weakest bit
        std::vector<int> cw(static_cast<size_t>(total), 0);
        std::vector<double> conf(static_cast<size_t>(total), 1e9);
        for (int k = 0; k < total * 8 && k < static_cast<int>(order.size()); ++k) {
            const int pos = order[static_cast<size_t>(k)];
            const int y = pos / n, x = pos % n;
            const float m = v.at<float>(y, x);
            const bool bit = (m < t) != masked(mask, x, y);
            cw[static_cast<size_t>(k / 8)] |= (bit ? 1 : 0) << (7 - k % 8);
            conf[static_cast<size_t>(k / 8)] = std::min(conf[static_cast<size_t>(k / 8)], std::abs(m - t) / spread);
        }
        // de-interleave: data codewords round-robin over the blocks (short blocks first), then ecc
        std::vector<std::vector<int>> blocks(static_cast<size_t>(nb));
        std::vector<std::vector<double>> bconf(static_cast<size_t>(nb));
        std::vector<std::vector<int>> where(static_cast<size_t>(nb)); // interleaved index of each block codeword
        const int maxData = shortLen - el + (nShort < nb ? 1 : 0);
        int idx = 0;
        for (int k = 0; k < maxData; ++k)
            for (int b = 0; b < nb; ++b) {
                const int dlen = shortLen - el + (b >= nShort ? 1 : 0);
                if (k >= dlen) continue;
                blocks[static_cast<size_t>(b)].push_back(cw[static_cast<size_t>(idx)]);
                bconf[static_cast<size_t>(b)].push_back(conf[static_cast<size_t>(idx)]);
                where[static_cast<size_t>(b)].push_back(idx++);
            }
        for (int k = 0; k < el; ++k)
            for (int b = 0; b < nb; ++b) {
                blocks[static_cast<size_t>(b)].push_back(cw[static_cast<size_t>(idx)]);
                bconf[static_cast<size_t>(b)].push_back(conf[static_cast<size_t>(idx)]);
                where[static_cast<size_t>(b)].push_back(idx++);
            }
        // RS per block, least certain codewords as erasures, two check symbols in reserve
        bool ok = true;
        std::vector<int> fixed(static_cast<size_t>(total), 0);
        for (int b = 0; b < nb && ok; ++b) {
            auto& blk = blocks[static_cast<size_t>(b)];
            std::vector<std::pair<double, int>> sure;
            for (size_t i = 0; i < blk.size(); ++i) sure.emplace_back(bconf[static_cast<size_t>(b)][i], static_cast<int>(i));
            std::sort(sure.begin(), sure.end());
            // codewords with a bit of no evidence at all (masked in every frame) are erased from the start
            int none = 0;
            while (none < static_cast<int>(sure.size()) && sure[static_cast<size_t>(none)].first <= 1e-9) ++none;
            bool done = false;
            for (int f = none; f <= el - 2 && !done; ++f) {
                std::vector<int> er;
                for (int i = 0; i < f; ++i) er.push_back(sure[static_cast<size_t>(i)].second);
                std::vector<int> c = blk;
                int errs = 0;
                if (!rsDecodeQr(c, el, er, &errs) || 2 * errs + f > el - 2) continue;
                for (size_t i = 0; i < c.size(); ++i) fixed[static_cast<size_t>(where[static_cast<size_t>(b)][i])] = c[i];
                done = true;
            }
            ok = done;
        }
        if (!ok) continue;
        // redraw: reference function modules and format, corrected data with the mask
        std::vector<uint8_t> sym = reference(version, e, mask);
        for (size_t k = 0; k < order.size(); ++k) {
            const int pos = order[k];
            const int bit = static_cast<int>(k) < total * 8 ? (fixed[k / 8] >> (7 - k % 8)) & 1 : 0;
            sym[static_cast<size_t>(pos)] = static_cast<uint8_t>((bit != 0) != masked(mask, pos % n, pos / n));
        }
        const std::string text = decodeQrBits(sym, n);
        if (!text.empty()) return text;
    }
    return {};
}

} // namespace

// ---- known-content decoding ------------------------------------------------------------------
// Codes of one product in one case share all their content but the serial (GTIN, batch, dates).
// The codes already read give that shared content; for an unread code the codewords that hold
// only shared bits are known exactly. They serve three ways:
//   - anchors: their modules (with the function patterns, and the check codewords of a block
//     whose data is all known) are a large set of modules with known values, so the grid is
//     registered on them (smooth K x K displacement field, coordinate search);
//   - training data: a small linear classifier on the pixels around each module, trained on the
//     anchors, decides the other modules (it learns this label's ink spread and blur, which a
//     global threshold cannot: single light modules between dark ones come out almost dark);
//   - Reed-Solomon: known codewords are fixed, so correction is needed for the serial part only;
//     the least certain other codewords become erasures, two check symbols stay in reserve, and
//     the known codewords must survive correction unchanged.
// The redrawn symbol must decode (zxing). Tried on the owner's cans image: the four unread cans
// (labels with heavy ink spread) read with known-content decoding, both copies on a label giving
// the same serial, which matches the label's printed text.

cv::Mat homography4(const cv::Point2f s[4], const cv::Point2f d[4]); // quality.cpp

namespace {

// data codewords of text as label printers encode GS1-style content: digit runs in numeric mode,
// other runs in byte mode; terminator and pad bytes as the standard says (versions 1-9)
bool dataCodewords(const std::string& text, int version, int e, std::vector<int>& out)
{
    const int total = kRawCodewords[version], nd = total - kBlocks[e][version] * kEccPerBlock[e][version];
    std::vector<uint8_t> bits;
    auto put = [&](int v, int n) {
        for (int i = n - 1; i >= 0; --i) bits.push_back(static_cast<uint8_t>((v >> i) & 1));
    };
    for (size_t i = 0; i < text.size();) {
        const bool digit = std::isdigit(static_cast<unsigned char>(text[i])) != 0;
        size_t j = i;
        while (j < text.size() && (std::isdigit(static_cast<unsigned char>(text[j])) != 0) == digit) ++j;
        const int len = static_cast<int>(j - i);
        if (digit) {
            if (len >= 1024) return false;
            put(1, 4);
            put(len, 10);
            for (size_t k = i; k < j; k += 3) {
                const size_t m = std::min<size_t>(3, j - k);
                put(std::stoi(text.substr(k, m)), m == 3 ? 10 : m == 2 ? 7 : 4);
            }
        } else {
            if (len >= 256) return false;
            put(4, 4);
            put(len, 8);
            for (size_t k = i; k < j; ++k) put(static_cast<unsigned char>(text[k]), 8);
        }
        i = j;
    }
    const size_t cap = static_cast<size_t>(nd) * 8;
    if (bits.size() > cap) return false;
    for (int k = 0; k < 4 && bits.size() < cap; ++k) bits.push_back(0);
    while (bits.size() % 8) bits.push_back(0);
    for (int pad = 0; bits.size() < cap; ++pad) put(pad % 2 ? 0x11 : 0xEC, 8);
    out.assign(static_cast<size_t>(nd), 0);
    for (size_t k = 0; k < cap; ++k) out[k / 8] |= bits[k] << (7 - k % 8);
    return true;
}

// QR check codewords of one block: generator roots alpha^0 .. alpha^(el-1), GF(256)/0x11D
std::vector<int> qrEcc(const std::vector<int>& data, int el)
{
    static const auto tables = [] {
        std::pair<std::array<int, 512>, std::array<int, 256>> t{};
        int x = 1;
        for (int i = 0; i < 255; ++i) {
            t.first[static_cast<size_t>(i)] = x;
            t.second[static_cast<size_t>(x)] = i;
            x <<= 1;
            if (x & 0x100) x ^= 0x11D;
        }
        for (int i = 255; i < 512; ++i) t.first[static_cast<size_t>(i)] = t.first[static_cast<size_t>(i - 255)];
        return t;
    }();
    const auto& ex = tables.first;
    const auto& lg = tables.second;
    auto mul = [&](int a, int b) { return a && b ? ex[static_cast<size_t>(lg[static_cast<size_t>(a)] + lg[static_cast<size_t>(b)])] : 0; };
    std::vector<int> gen = {1}; // high to low degree
    for (int i = 0; i < el; ++i) {
        std::vector<int> next(gen.size() + 1, 0);
        for (size_t j = 0; j < gen.size(); ++j) {
            next[j] ^= gen[j];
            next[j + 1] ^= mul(gen[j], ex[static_cast<size_t>(i)]);
        }
        gen = next;
    }
    std::vector<int> rem(static_cast<size_t>(el), 0);
    for (int d : data) {
        const int f = d ^ rem[0];
        rem.erase(rem.begin());
        rem.push_back(0);
        for (int j = 0; j < el; ++j) rem[static_cast<size_t>(j)] ^= mul(gen[static_cast<size_t>(j + 1)], f);
    }
    return rem;
}

// block layout: interleaved codeword index of each block's codewords (data first, then check)
std::vector<std::vector<int>> blockIndex(int version, int e, std::vector<int>* dataLen)
{
    const int total = kRawCodewords[version], nb = kBlocks[e][version], el = kEccPerBlock[e][version];
    const int nShort = nb - total % nb, shortLen = total / nb;
    std::vector<std::vector<int>> where(static_cast<size_t>(nb));
    if (dataLen) dataLen->assign(static_cast<size_t>(nb), 0);
    const int maxData = shortLen - el + (nShort < nb ? 1 : 0);
    int idx = 0;
    for (int k = 0; k < maxData; ++k)
        for (int b = 0; b < nb; ++b)
            if (k < shortLen - el + (b >= nShort ? 1 : 0)) {
                where[static_cast<size_t>(b)].push_back(idx++);
                if (dataLen) (*dataLen)[static_cast<size_t>(b)]++;
            }
    for (int k = 0; k < el; ++k)
        for (int b = 0; b < nb; ++b) where[static_cast<size_t>(b)].push_back(idx++);
    return where;
}

// values at module (x + ox, y + oy) for every module, through a K x K displacement net
cv::Mat sampleGrid(const cv::Mat& img32, const cv::Mat& H, int n, const std::vector<cv::Point2f>& net, int K, float ox, float oy)
{
    std::vector<cv::Point2f> pts, out;
    pts.reserve(static_cast<size_t>(n) * n);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            const float px = x + 0.5f, py = y + 0.5f;
            const float u = std::clamp(px / n * (K - 1), 0.f, K - 1 - 1e-4f), v = std::clamp(py / n * (K - 1), 0.f, K - 1 - 1e-4f);
            const int i0 = static_cast<int>(u), j0 = static_cast<int>(v);
            const float fu = u - i0, fv = v - j0;
            const auto at = [&](int j, int i) { return net[static_cast<size_t>(j * K + i)]; };
            const cv::Point2f d = at(j0, i0) * (1 - fu) * (1 - fv) + at(j0, i0 + 1) * fu * (1 - fv) + at(j0 + 1, i0) * (1 - fu) * fv +
                                  at(j0 + 1, i0 + 1) * fu * fv;
            pts.emplace_back(px + d.x + ox, py + d.y + oy);
        }
    cv::perspectiveTransform(pts, out, H);
    cv::Mat mapX(n, n, CV_32F), mapY(n, n, CV_32F);
    for (size_t k = 0; k < out.size(); ++k) {
        mapX.at<float>(static_cast<int>(k)) = out[k].x;
        mapY.at<float>(static_cast<int>(k)) = out[k].y;
    }
    cv::Mat vals;
    cv::remap(img32, vals, mapX, mapY, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    return vals;
}

// how well module values follow the known ones (truth: 1 dark, 0 light, -1 unknown): -correlation
float anchorScore(const cv::Mat& v, const std::vector<int8_t>& truth)
{
    double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
    int cnt = 0;
    for (size_t k = 0; k < truth.size(); ++k) {
        if (truth[k] < 0) continue;
        const double a = v.at<float>(static_cast<int>(k)), b = truth[k];
        sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b;
        ++cnt;
    }
    if (cnt < 2) return -1.f;
    const double va = saa - sa * sa / cnt, vb = sbb - sb * sb / cnt;
    return static_cast<float>(-(sab - sa * sb / cnt) / std::sqrt(std::max(1e-9, va * vb)));
}

} // namespace

std::string knownDecodeQr(const cv::Mat& img32, const cv::Point2f quadIn[4], int n, const std::vector<std::string>& texts,
                          std::chrono::steady_clock::time_point deadline)
{
    const int version = (n - 17) / 4;
    if (version < 1 || version > kMaxVersion || 17 + 4 * version != n || texts.size() < 2) return {};
    const auto late = [&]() { return std::chrono::steady_clock::now() >= deadline; };
    static const std::vector<uint8_t> fnCache[kMaxVersion + 1] = {
        {}, functionModules(1), functionModules(2), functionModules(3), functionModules(4), functionModules(5), functionModules(6)};
    const auto& fn = fnCache[version];
    const std::vector<int> order = placementOrder(version, fn);
    const int total = kRawCodewords[version];
    const cv::Point2f src[4] = {{0, 0}, {float(n), 0}, {float(n), float(n)}, {0, float(n)}};

    // orientation: of the 8 corner orders, the one whose finders and timing fit best
    const auto& ref00 = reference(version, 0, 0);
    std::vector<int8_t> fixedTruth(static_cast<size_t>(n) * n, -1);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            const bool finder = (x < 8 && y < 8) || (x >= n - 8 && y < 8) || (x < 8 && y >= n - 8);
            if (finder || x == 6 || y == 6) fixedTruth[static_cast<size_t>(y) * n + x] = static_cast<int8_t>(ref00[static_cast<size_t>(y) * n + x]);
        }
    const int K = 3; // a 3 x 3 net: enough bend for a label, a third of the search of 5 x 5
    std::vector<cv::Point2f> net(static_cast<size_t>(K * K), cv::Point2f(0, 0));
    cv::Point2f quad[4];
    float bestO = -2.f;
    for (int o = 0; o < 8; ++o) {
        cv::Point2f q[4];
        for (int k = 0; k < 4; ++k) q[k] = quadIn[o < 4 ? (k + o) % 4 : (4 - k + o) % 4];
        const float sc = anchorScore(sampleGrid(img32, homography4(src, q), n, net, K, 0, 0), fixedTruth);
        if (sc > bestO) {
            bestO = sc;
            std::copy(q, q + 4, quad);
        }
    }
    const bool kprof = std::getenv("CR_KPROF") != nullptr;
    const auto tk0 = std::chrono::steady_clock::now();
    auto kms = [&]() { return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - tk0).count(); };
    if (bestO < 0.3f) { if (kprof) std::fprintf(stderr, "  kdec orient %.2f: out at %.1f ms\n", bestO, kms()); return {}; }
    const cv::Mat H = homography4(src, quad);
    const cv::Mat v0 = sampleGrid(img32, H, n, net, K, 0, 0);

    // EC level and mask from the format modules
    cv::Scalar mean0 = cv::mean(v0);
    std::vector<int> fmtPos;
    for (int x = 0; x <= 8; ++x)
        if (x != 6) fmtPos.push_back(8 * n + x);
    for (int y = 0; y <= 7; ++y)
        if (y != 6) fmtPos.push_back(y * n + 8);
    for (int x = n - 8; x < n; ++x) fmtPos.push_back(8 * n + x);
    for (int y = n - 7; y < n; ++y) fmtPos.push_back(y * n + 8);
    int e = 1, mask = 0;
    double bestF = -1e18;
    for (int ee = 0; ee < 4; ++ee)
        for (int m = 0; m < 8; ++m) {
            const auto& ref = reference(version, ee, m);
            double sc = 0;
            for (int pos : fmtPos) sc += (mean0[0] - v0.at<float>(pos)) * (ref[static_cast<size_t>(pos)] ? 1 : -1);
            if (sc > bestF) { bestF = sc; e = ee; mask = m; }
        }

    // known data codewords: equal in every code read
    std::vector<int> known, cw;
    std::vector<uint8_t> isKnown;
    int texts0 = 0;
    for (const auto& t : texts) {
        if (!dataCodewords(t, version, e, cw)) continue;
        if (texts0++ == 0) {
            known = cw;
            isKnown.assign(cw.size(), 1);
        } else
            for (size_t k = 0; k < cw.size(); ++k)
                if (cw[k] != known[k]) isKnown[k] = 0;
    }
    if (texts0 < 2) return {};
    const int nKnown = static_cast<int>(std::count(isKnown.begin(), isKnown.end(), 1));
    if (nKnown * 10 < static_cast<int>(known.size()) * 4 || nKnown == static_cast<int>(known.size())) return {};

    // anchors: function modules, known data codewords, check codewords of fully known blocks
    std::vector<int> dataLen;
    const auto where = blockIndex(version, e, &dataLen);
    const int el = kEccPerBlock[e][version];
    std::vector<int> cwKnown(static_cast<size_t>(total), -1); // interleaved index -> known value
    int dataOff = 0;
    for (size_t b = 0; b < where.size(); ++b) {
        std::vector<int> d;
        bool all = true;
        for (int k = 0; k < dataLen[b]; ++k) {
            const size_t g = static_cast<size_t>(dataOff + k);
            if (isKnown[g]) cwKnown[static_cast<size_t>(where[b][static_cast<size_t>(k)])] = known[g];
            else all = false;
            d.push_back(known[g]);
        }
        if (all) {
            const auto ecc = qrEcc(d, el);
            for (int k = 0; k < el; ++k) cwKnown[static_cast<size_t>(where[b][static_cast<size_t>(dataLen[b] + k)])] = ecc[static_cast<size_t>(k)];
        }
        dataOff += dataLen[b];
    }
    const auto& ref = reference(version, e, mask);
    std::vector<int8_t> truth(static_cast<size_t>(n) * n, -1);
    for (size_t p = 0; p < truth.size(); ++p)
        if (fn[p]) truth[p] = static_cast<int8_t>(ref[p]);
    for (int k = 0; k < total * 8 && k < static_cast<int>(order.size()); ++k) {
        const int c = cwKnown[static_cast<size_t>(k / 8)];
        if (c < 0) continue;
        const int pos = order[static_cast<size_t>(k)];
        const bool bit = ((c >> (7 - k % 8)) & 1) != 0;
        truth[static_cast<size_t>(pos)] = static_cast<int8_t>(bit != masked(mask, pos % n, pos / n));
    }

    // register the grid on the anchors
    float best = anchorScore(v0, truth);
    for (float step : {0.3f, 0.15f, 0.08f}) {
        bool improved = true;
        while (improved && !late()) {
            improved = false;
            for (auto& p : net)
                for (int ax = 0; ax < 2; ++ax)
                    for (float s : {step, -step}) {
                        (ax == 0 ? p.x : p.y) += s;
                        const float sc = anchorScore(sampleGrid(img32, H, n, net, K, 0, 0), truth);
                        if (sc > best + 1e-5f) {
                            best = sc;
                            improved = true;
                            break;
                        }
                        (ax == 0 ? p.x : p.y) -= s;
                    }
        }
    }
    if (late()) return {};
    // the anchors must look like this code: below 0.45 it is another label, a wrinkle or
    // background, and the classifier would only cost time
    if (kprof) std::fprintf(stderr, "  kdec orient %.2f registered %.2f at %.1f ms (known %d/%zu)\n", bestO, best, kms(), nKnown, known.size());
    if (best < 0.45f) return {};

    // patch classifier on the anchors, then RS with the known codewords fixed: the cheapest
    // (3 x 3 modules at 2 taps, 36 features) first
    struct Patch { int r, sub; double lambda; };
    for (const Patch pc : {Patch{1, 2, 1.0}, Patch{1, 3, 0.1}, Patch{2, 2, 0.1}}) {
        if (late()) return {};
        const int side = (2 * pc.r + 1) * pc.sub, nf = side * side;
        std::vector<cv::Mat> feats;
        for (int j = 0; j < side; ++j)
            for (int i = 0; i < side; ++i) {
                const float oy = -pc.r - 0.5f + (j + 0.5f) / pc.sub, ox = -pc.r - 0.5f + (i + 0.5f) / pc.sub;
                feats.push_back(sampleGrid(img32, H, n, net, K, ox, oy));
            }
        double mu = 0, sq = 0;
        int cnt = 0;
        for (const auto& f : feats)
            for (size_t p = 0; p < truth.size(); ++p)
                if (truth[p] >= 0) { const double a = f.at<float>(static_cast<int>(p)); mu += a; sq += a * a; ++cnt; }
        mu /= cnt;
        const double sd = std::sqrt(std::max(1e-9, sq / cnt - mu * mu));
        cv::Mat A = cv::Mat::zeros(nf + 1, nf + 1, CV_64F), bv = cv::Mat::zeros(nf + 1, 1, CV_64F);
        std::vector<double> row(static_cast<size_t>(nf + 1));
        for (size_t p = 0; p < truth.size(); ++p) {
            if (truth[p] < 0) continue;
            for (int f = 0; f < nf; ++f) row[static_cast<size_t>(f)] = (feats[static_cast<size_t>(f)].at<float>(static_cast<int>(p)) - mu) / sd;
            row[static_cast<size_t>(nf)] = 1;
            const double t = truth[p] ? 1 : -1;
            for (int a = 0; a <= nf; ++a) {
                bv.at<double>(a) += row[static_cast<size_t>(a)] * t;
                for (int b = 0; b <= nf; ++b) A.at<double>(a, b) += row[static_cast<size_t>(a)] * row[static_cast<size_t>(b)];
            }
        }
        for (int a = 0; a <= nf; ++a) A.at<double>(a, a) += pc.lambda;
        cv::Mat w;
        if (!cv::solve(A, bv, w, cv::DECOMP_CHOLESKY) && !cv::solve(A, bv, w, cv::DECOMP_SVD)) continue;
        std::vector<float> score(truth.size());
        for (size_t p = 0; p < truth.size(); ++p) {
            double s = w.at<double>(nf);
            for (int f = 0; f < nf; ++f) s += w.at<double>(f) * (feats[static_cast<size_t>(f)].at<float>(static_cast<int>(p)) - mu) / sd;
            score[p] = static_cast<float>(s); // > 0 dark
        }
        // codewords in interleaved order with the confidence of their weakest bit
        std::vector<int> rd(static_cast<size_t>(total), 0);
        std::vector<double> conf(static_cast<size_t>(total), 1e9);
        for (int k = 0; k < total * 8 && k < static_cast<int>(order.size()); ++k) {
            const int pos = order[static_cast<size_t>(k)];
            const bool bit = (score[static_cast<size_t>(pos)] > 0) != masked(mask, pos % n, pos / n);
            rd[static_cast<size_t>(k / 8)] |= (bit ? 1 : 0) << (7 - k % 8);
            conf[static_cast<size_t>(k / 8)] = std::min(conf[static_cast<size_t>(k / 8)], static_cast<double>(std::abs(score[static_cast<size_t>(pos)])));
        }
        bool ok = true;
        std::vector<int> fixedCw(static_cast<size_t>(total), 0);
        dataOff = 0;
        for (size_t b = 0; b < where.size() && ok; ++b) {
            const auto& wb = where[b];
            std::vector<int> blk;
            std::vector<std::pair<double, int>> unsure;
            std::vector<int> fixedPos;
            for (size_t i = 0; i < wb.size(); ++i) {
                const int g = wb[i];
                const bool isData = static_cast<int>(i) < dataLen[b];
                if (isData && isKnown[static_cast<size_t>(dataOff) + i]) {
                    blk.push_back(known[static_cast<size_t>(dataOff) + i]);
                    fixedPos.push_back(static_cast<int>(i));
                } else {
                    blk.push_back(rd[static_cast<size_t>(g)]);
                    unsure.emplace_back(conf[static_cast<size_t>(g)], static_cast<int>(i));
                }
            }
            dataOff += dataLen[b];
            std::sort(unsure.begin(), unsure.end());
            bool done = false;
            for (int f = 0; f <= std::min<int>(el - 2, static_cast<int>(unsure.size())) && !done; ++f) {
                std::vector<int> er;
                for (int i = 0; i < f; ++i) er.push_back(unsure[static_cast<size_t>(i)].second);
                std::vector<int> c = blk;
                int errs = 0;
                if (!rsDecodeQr(c, el, er, &errs) || 2 * errs + f > el - 2) continue;
                bool keep = true;
                for (int i : fixedPos) keep = keep && c[static_cast<size_t>(i)] == blk[static_cast<size_t>(i)];
                if (!keep) continue;
                for (size_t i = 0; i < c.size(); ++i) fixedCw[static_cast<size_t>(wb[i])] = c[i];
                done = true;
            }
            ok = done;
        }
        if (!ok) continue;
        std::vector<uint8_t> sym = ref;
        for (size_t k = 0; k < order.size(); ++k) {
            const int pos = order[k];
            const int bit = static_cast<int>(k) < total * 8 ? (fixedCw[k / 8] >> (7 - k % 8)) & 1 : 0;
            sym[static_cast<size_t>(pos)] = static_cast<uint8_t>((bit != 0) != masked(mask, pos % n, pos / n));
        }
        const std::string text = decodeQrBits(sym, n);
        if (kprof) std::fprintf(stderr, "  kdec classifier r%d: %s at %.1f ms\n", pc.r, text.empty() ? "-" : "READ", kms());
        if (!text.empty() && plausibleAmong(text, texts)) return text;
    }
    return {};
}

// ---- solved modules (underres.cpp) -------------------------------------------------------------

std::vector<int8_t> qrKnownModules(int version, int e, int mask, const std::vector<std::string>* texts, std::vector<int>* knownCw)
{
    const int n = 17 + 4 * version, total = kRawCodewords[version];
    static const std::vector<uint8_t> fnCache[kMaxVersion + 1] = {
        {}, functionModules(1), functionModules(2), functionModules(3), functionModules(4), functionModules(5), functionModules(6)};
    const auto& fn = fnCache[version];
    const auto& ref = reference(version, e, mask);
    std::vector<int8_t> truth(static_cast<size_t>(n) * n, -1);
    for (size_t p = 0; p < truth.size(); ++p)
        if (fn[p]) truth[p] = static_cast<int8_t>(ref[p]);
    std::vector<int> cwKnown(static_cast<size_t>(total), -1); // interleaved index -> known value
    if (texts && texts->size() >= 2) {
        std::vector<int> known, cw;
        std::vector<uint8_t> isKnown;
        int texts0 = 0;
        for (const auto& t : *texts) {
            if (!dataCodewords(t, version, e, cw)) continue;
            if (texts0++ == 0) {
                known = cw;
                isKnown.assign(cw.size(), 1);
            } else
                for (size_t k = 0; k < cw.size(); ++k)
                    if (cw[k] != known[k]) isKnown[k] = 0;
        }
        if (texts0 >= 2) {
            std::vector<int> dataLen;
            const auto where = blockIndex(version, e, &dataLen);
            const int el = kEccPerBlock[e][version];
            int dataOff = 0;
            for (size_t b = 0; b < where.size(); ++b) {
                std::vector<int> d;
                bool all = true;
                for (int k = 0; k < dataLen[b]; ++k) {
                    const size_t g = static_cast<size_t>(dataOff + k);
                    if (isKnown[g]) cwKnown[static_cast<size_t>(where[b][static_cast<size_t>(k)])] = known[g];
                    else all = false;
                    d.push_back(known[g]);
                }
                if (all) {
                    const auto ecc = qrEcc(d, el);
                    for (int k = 0; k < el; ++k) cwKnown[static_cast<size_t>(where[b][static_cast<size_t>(dataLen[b] + k)])] = ecc[static_cast<size_t>(k)];
                }
                dataOff += dataLen[b];
            }
            const std::vector<int> order = placementOrder(version, fn);
            for (int k = 0; k < total * 8 && k < static_cast<int>(order.size()); ++k) {
                const int c = cwKnown[static_cast<size_t>(k / 8)];
                if (c < 0) continue;
                const int pos = order[static_cast<size_t>(k)];
                truth[static_cast<size_t>(pos)] = static_cast<int8_t>((((c >> (7 - k % 8)) & 1) != 0) != masked(mask, pos % n, pos / n));
            }
        }
    }
    if (knownCw) *knownCw = std::move(cwKnown);
    return truth;
}

std::string decodeQrSolved(const std::vector<uint8_t>& dark, const std::vector<float>& conf, int version, int e, int mask,
                           const std::vector<int>& knownCw, const std::vector<std::string>* texts)
{
    const int n = 17 + 4 * version, total = kRawCodewords[version];
    static const std::vector<uint8_t> fnCache[kMaxVersion + 1] = {
        {}, functionModules(1), functionModules(2), functionModules(3), functionModules(4), functionModules(5), functionModules(6)};
    const std::vector<int> order = placementOrder(version, fnCache[version]);
    // codewords in interleaved order with the confidence of their weakest bit
    std::vector<int> rd(static_cast<size_t>(total), 0);
    std::vector<double> cconf(static_cast<size_t>(total), 1e9);
    for (int k = 0; k < total * 8 && k < static_cast<int>(order.size()); ++k) {
        const int pos = order[static_cast<size_t>(k)];
        const bool bit = (dark[static_cast<size_t>(pos)] != 0) != masked(mask, pos % n, pos / n);
        rd[static_cast<size_t>(k / 8)] |= (bit ? 1 : 0) << (7 - k % 8);
        cconf[static_cast<size_t>(k / 8)] = std::min(cconf[static_cast<size_t>(k / 8)], static_cast<double>(conf[static_cast<size_t>(pos)]));
    }
    // RS per block: known codewords fixed and kept, the least certain others erased, two check
    // symbols in reserve
    const int el = kEccPerBlock[e][version];
    const auto where = blockIndex(version, e, nullptr);
    std::vector<int> fixedCw(static_cast<size_t>(total), 0);
    for (const auto& wb : where) {
        std::vector<int> blk, fixedPos;
        std::vector<std::pair<double, int>> unsure;
        for (size_t i = 0; i < wb.size(); ++i) {
            const int g = wb[i], kc = knownCw.empty() ? -1 : knownCw[static_cast<size_t>(g)];
            if (kc >= 0) {
                blk.push_back(kc);
                fixedPos.push_back(static_cast<int>(i));
            } else {
                blk.push_back(rd[static_cast<size_t>(g)]);
                unsure.emplace_back(cconf[static_cast<size_t>(g)], static_cast<int>(i));
            }
        }
        std::sort(unsure.begin(), unsure.end());
        bool done = false;
        for (int f = 0; f <= std::min<int>(el - 2, static_cast<int>(unsure.size())) && !done; ++f) {
            std::vector<int> er;
            for (int i = 0; i < f; ++i) er.push_back(unsure[static_cast<size_t>(i)].second);
            std::vector<int> c = blk;
            int errs = 0;
            if (!rsDecodeQr(c, el, er, &errs) || 2 * errs + f > el - 2) continue;
            bool keep = true;
            for (int i : fixedPos) keep = keep && c[static_cast<size_t>(i)] == blk[static_cast<size_t>(i)];
            if (!keep) continue;
            for (size_t i = 0; i < c.size(); ++i) fixedCw[static_cast<size_t>(wb[i])] = c[i];
            done = true;
        }
        if (!done) return {};
    }
    std::vector<uint8_t> sym = reference(version, e, mask);
    for (size_t k = 0; k < order.size(); ++k) {
        const int pos = order[k];
        const int bit = static_cast<int>(k) < total * 8 ? (fixedCw[k / 8] >> (7 - k % 8)) & 1 : 0;
        sym[static_cast<size_t>(pos)] = static_cast<uint8_t>((bit != 0) != masked(mask, pos % n, pos / n));
    }
    const std::string text = decodeQrBits(sym, n);
    if (!text.empty() && texts && texts->size() >= 2 && !plausibleAmong(text, *texts)) return {};
    return text;
}

} // namespace cr
