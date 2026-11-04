// codewords.cpp - where each codeword of a Data Matrix (ECC 200) or QR symbol lies, and its
// Reed-Solomon block, for grading per codeword (ISO/IEC 15415 modulation, ISO/IEC TR 29158 cell
// modulation): a codeword's grade is the lowest of its modules', and the codewords under each
// grade level are counted as errors against their own block's error correction.
//
// Data Matrix: ISO/IEC 16022 table 7 (square 10 x 10 .. 144 x 144 and the six rectangles; the
// DMRE sizes of ISO/IEC 21471 are not in it), the annex F module placement in the mapping matrix,
// the data regions with their finder and alignment patterns around them, and the interleaving of
// the blocks (codeword i in block i mod B). The unused 2 x 2 corner of some sizes is no codeword.
// QR: ISO/IEC 18004 versions 1 to 40, the error correction tables (as in qrcodegen), the
// two-column placement around finders, separators, format and version information, timing and
// alignment patterns, the interleaving of short and long blocks; the remainder bits are no codeword.
// Both follow the grid as the decoders give it: row 0 at the top, Data Matrix finder L at the left
// and bottom, QR finders top left, top right and bottom left.

#include "reader.hpp"

#include <algorithm>
#include <array>

namespace cr {

namespace {

struct DmTable {
    int rows, cols;  // symbol, modules
    int regRows, regCols; // data region, modules (without its finder and clock)
    int data, ecc, blocks;
};
// ISO/IEC 16022 table 7
constexpr DmTable kDm[] = {
    {10, 10, 8, 8, 3, 5, 1},         {12, 12, 10, 10, 5, 7, 1},       {14, 14, 12, 12, 8, 10, 1},
    {16, 16, 14, 14, 12, 12, 1},     {18, 18, 16, 16, 18, 14, 1},     {20, 20, 18, 18, 22, 18, 1},
    {22, 22, 20, 20, 30, 20, 1},     {24, 24, 22, 22, 36, 24, 1},     {26, 26, 24, 24, 44, 28, 1},
    {32, 32, 14, 14, 62, 36, 1},     {36, 36, 16, 16, 86, 42, 1},     {40, 40, 18, 18, 114, 48, 1},
    {44, 44, 20, 20, 144, 56, 1},    {48, 48, 22, 22, 174, 68, 1},    {52, 52, 24, 24, 204, 84, 2},
    {64, 64, 14, 14, 280, 112, 2},   {72, 72, 16, 16, 368, 144, 4},   {80, 80, 18, 18, 456, 192, 4},
    {88, 88, 20, 20, 576, 224, 4},   {96, 96, 22, 22, 696, 272, 4},   {104, 104, 24, 24, 816, 336, 6},
    {120, 120, 18, 18, 1050, 408, 6}, {132, 132, 20, 20, 1304, 496, 8}, {144, 144, 22, 22, 1558, 620, 10},
    {8, 18, 6, 16, 5, 7, 1},         {8, 32, 6, 14, 10, 11, 1},       {12, 26, 10, 24, 16, 14, 1},
    {12, 36, 10, 16, 22, 18, 1},     {16, 36, 14, 16, 32, 24, 1},     {16, 48, 14, 22, 49, 28, 1},
};

// ISO/IEC 16022 annex F: the mapping-matrix position (r * ncol + c) of each bit of each codeword,
// bit 7 (most significant) first
std::vector<std::array<int, 8>> dmPlacement(int nrow, int ncol)
{
    std::vector<std::array<int, 8>> pos;
    std::vector<uint8_t> used(static_cast<size_t>(nrow) * ncol, 0);
    auto module = [&](int r, int c, std::array<int, 8>& cw, int bit) {
        if (r < 0) {
            r += nrow;
            c += 4 - ((nrow + 4) % 8);
        }
        if (c < 0) {
            c += ncol;
            r += 4 - ((ncol + 4) % 8);
        }
        cw[static_cast<size_t>(bit)] = r * ncol + c;
        used[static_cast<size_t>(r) * ncol + c] = 1;
    };
    auto utah = [&](int r, int c) {
        static const int d[8][2] = {{-2, -2}, {-2, -1}, {-1, -2}, {-1, -1}, {-1, 0}, {0, -2}, {0, -1}, {0, 0}};
        std::array<int, 8> cw{};
        for (int b = 0; b < 8; ++b) module(r + d[b][0], c + d[b][1], cw, b);
        pos.push_back(cw);
    };
    auto corner = [&](const std::array<std::array<int, 2>, 8>& pts) {
        std::array<int, 8> cw{};
        for (int b = 0; b < 8; ++b) module(pts[static_cast<size_t>(b)][0], pts[static_cast<size_t>(b)][1], cw, b);
        pos.push_back(cw);
    };
    auto free = [&](int r, int c) { return !used[static_cast<size_t>(r) * ncol + c]; };
    int r = 4, c = 0;
    do {
        if (r == nrow && c == 0) corner({{{nrow - 1, 0}, {nrow - 1, 1}, {nrow - 1, 2}, {0, ncol - 2}, {0, ncol - 1}, {1, ncol - 1}, {2, ncol - 1}, {3, ncol - 1}}});
        if (r == nrow - 2 && c == 0 && ncol % 4) corner({{{nrow - 3, 0}, {nrow - 2, 0}, {nrow - 1, 0}, {0, ncol - 4}, {0, ncol - 3}, {0, ncol - 2}, {0, ncol - 1}, {1, ncol - 1}}});
        if (r == nrow - 2 && c == 0 && ncol % 8 == 4) corner({{{nrow - 3, 0}, {nrow - 2, 0}, {nrow - 1, 0}, {0, ncol - 2}, {0, ncol - 1}, {1, ncol - 1}, {2, ncol - 1}, {3, ncol - 1}}});
        if (r == nrow + 4 && c == 2 && !(ncol % 8)) corner({{{nrow - 1, 0}, {nrow - 1, ncol - 1}, {0, ncol - 3}, {0, ncol - 2}, {0, ncol - 1}, {1, ncol - 3}, {1, ncol - 2}, {1, ncol - 1}}});
        do { // up and to the right
            if (r < nrow && c >= 0 && free(r, c)) utah(r, c);
            r -= 2;
            c += 2;
        } while (r >= 0 && c < ncol);
        r += 1;
        c += 3;
        do { // down and to the left
            if (r >= 0 && c < ncol && free(r, c)) utah(r, c);
            r += 2;
            c -= 2;
        } while (r < nrow && c >= 0);
        r += 3;
        c += 1;
    } while (r < nrow || c < ncol);
    return pos;
}

bool dmMap(int w, int h, CodewordMap& m)
{
    const DmTable* t = nullptr;
    for (const auto& d : kDm)
        if (d.cols == w && d.rows == h) t = &d;
    if (!t) return false;
    const int nrow = h / (t->regRows + 2) * t->regRows, ncol = w / (t->regCols + 2) * t->regCols;
    const auto pos = dmPlacement(nrow, ncol);
    const int total = t->data + t->ecc;
    if (static_cast<int>(pos.size()) < total) return false;
    const int B = t->blocks;
    m = {};
    for (int k = 0; k < total; ++k) {
        std::vector<int> cells;
        for (int p : pos[static_cast<size_t>(k)]) {
            const int r = p / ncol, c = p % ncol;
            // each data region sits inside its own finder L and clock tracks
            const int y = r / t->regRows * (t->regRows + 2) + 1 + r % t->regRows;
            const int x = c / t->regCols * (t->regCols + 2) + 1 + c % t->regCols;
            cells.push_back(y * w + x);
        }
        m.cells.push_back(std::move(cells));
        m.block.push_back(k < t->data ? k % B : (k - t->data) % B);
    }
    m.blockData.assign(static_cast<size_t>(B), 0);
    for (int k = 0; k < t->data; ++k) ++m.blockData[static_cast<size_t>(k % B)];
    m.blockEcc.assign(static_cast<size_t>(B), t->ecc / B);
    // the small symbols with an odd check count (10 x 10, 12 x 12, 8 x 18, 8 x 32) keep one check
    // codeword for error detection: errors corrected = (ecc - 1) / 2
    m.blockP.assign(static_cast<size_t>(B), (t->ecc / B) % 2);
    return true;
}

// ISO/IEC 18004 table 9, as qrcodegen (QrCode::ECC_CODEWORDS_PER_BLOCK, NUM_ERROR_CORRECTION_BLOCKS)
constexpr int8_t kQrEcc[4][41] = {
    {-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30},
    {-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28},
    {-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30},
    {-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30},
};
constexpr int8_t kQrBlocks[4][41] = {
    {-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25},
    {-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49},
    {-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68},
    {-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81},
};

bool qrMap(int n, const std::string& ecLevel, CodewordMap& m)
{
    if ((n - 17) % 4) return false;
    const int v = (n - 17) / 4;
    if (v < 1 || v > 40) return false;
    int e = -1; // L, M, Q, H
    for (char ch : ecLevel) {
        const char* const lv = "LMQH";
        for (int i = 0; i < 4; ++i)
            if (ch == lv[i]) e = i;
        if (e >= 0) break;
    }
    if (e < 0) return false;

    // function modules: finders with separators and format areas, timing, alignment, version
    std::vector<uint8_t> fn(static_cast<size_t>(n) * n, 0);
    auto set = [&](int x, int y) { fn[static_cast<size_t>(y) * n + x] = 1; };
    for (int y = 0; y < 9; ++y)
        for (int x = 0; x < 9; ++x) {
            set(x, y);
            if (x < 8) set(n - 1 - x, y);
            if (y < 8) set(x, n - 1 - y);
        }
    for (int i = 0; i < n; ++i) {
        set(i, 6);
        set(6, i);
    }
    if (v >= 2) {
        const int num = v / 7 + 2, step = v == 32 ? 26 : (v * 4 + num * 2 + 1) / (num * 2 - 2) * 2;
        std::vector<int> at{6};
        for (int i = 0, p = n - 7; i < num - 1; ++i, p -= step) at.insert(at.begin() + 1, p);
        for (int ay : at)
            for (int ax : at) {
                if ((ax == 6 && ay == 6) || (ax == 6 && ay == n - 7) || (ax == n - 7 && ay == 6)) continue;
                for (int dy = -2; dy <= 2; ++dy)
                    for (int dx = -2; dx <= 2; ++dx) set(ax + dx, ay + dy);
            }
    }
    if (v >= 7)
        for (int i = 0; i < 6; ++i)
            for (int j = n - 11; j < n - 8; ++j) {
                set(j, i);
                set(i, j);
            }

    // placement: two-column zigzag from the bottom right, skipping the vertical timing column
    std::vector<int> order;
    for (int right = n - 1; right >= 1; right -= 2) {
        if (right == 6) right = 5;
        const bool upward = ((right + 1) & 2) == 0;
        for (int vert = 0; vert < n; ++vert)
            for (int j = 0; j < 2; ++j) {
                const int x = right - j, y = upward ? n - 1 - vert : vert;
                if (!fn[static_cast<size_t>(y) * n + x]) order.push_back(y * n + x);
            }
    }
    const int raw = static_cast<int>(order.size()) / 8, B = kQrBlocks[e][v], ecc = kQrEcc[e][v];
    const int shortBlocks = B - raw % B, shortData = raw / B - ecc;
    if (shortData < 1) return false;
    m = {};
    m.blockData.resize(static_cast<size_t>(B));
    for (int b = 0; b < B; ++b) m.blockData[static_cast<size_t>(b)] = shortData + (b >= shortBlocks ? 1 : 0);
    m.blockEcc.assign(static_cast<size_t>(B), ecc);
    // codewords kept for misdecode protection (ISO/IEC 18004 table 9 note): 1-L 3, 1-M and 2-L 2,
    // 1-Q, 1-H and 3-L 1
    const int p = v == 1 ? (e == 0 ? 3 : e == 1 ? 2 : 1) : v == 2 && e == 0 ? 2 : v == 3 && e == 0 ? 1 : 0;
    m.blockP.assign(static_cast<size_t>(B), p);
    // interleaving: data codeword i of each block in turn (long blocks have one more), then the
    // check codewords the same way
    for (int i = 0; i <= shortData; ++i)
        for (int b = 0; b < B; ++b)
            if (i < m.blockData[static_cast<size_t>(b)]) m.block.push_back(b);
    for (int i = 0; i < ecc; ++i)
        for (int b = 0; b < B; ++b) m.block.push_back(b);
    for (int k = 0; k < raw; ++k)
        m.cells.emplace_back(order.begin() + k * 8, order.begin() + k * 8 + 8);
    return static_cast<int>(m.block.size()) == raw;
}

} // namespace

bool codewordMap(int format, int w, int h, const std::string& ecLevel, CodewordMap& m)
{
    if (format == CR_DATAMATRIX) return dmMap(w, h, m);
    if (format == CR_QR && w == h) return qrMap(w, ecLevel, m);
    return false;
}

} // namespace cr
