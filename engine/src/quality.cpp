// quality.cpp - print / image quality of each decoded 2D matrix code (QR, Micro QR, Data Matrix,
// Aztec), after ISO/IEC 15415 (2D print quality), ISO/IEC 29158 (direct part marks) and the
// symbology standards (ISO/IEC 18004, 16022, 24778). PDF417 is graded by rows (grade_pdf417.cpp).
//
// DotCode (AIM ISS DotCode Rev 4.0) is graded by the same ISO/IEC 15415 matrix method on the dot grid
// its decoder fitted (dotcode.cpp), with what the symbology changes: it has no finder or clock, so no
// fixed pattern damage grade; its grid non-uniformity is how far each printed dot's centre lies from
// its place on that grid (no timing pattern to follow); unused error correction from its own Reed-
// Solomon blocks, the two spare check words kept as 15415's misdecode protection; quiet zone 3 X.
//
// rMQR (ISO/IEC 23941) is graded by ISO/IEC 15415 with its own fixed patterns: the finder and its
// separator top left, the finder sub-pattern bottom right, the corner patterns top right and bottom
// left, the alignment patterns on the top and bottom edges with the vertical timing patterns between
// them, and the timing patterns along the four edges; quiet zone 2 X.
//
// The module grid comes from the decoder (zxing symbol()) and the code's corners; every module
// centre is sampled in the image through a homography. Grey levels are camera values, not
// calibrated reflectance, so these are process-control grades with the ISO thresholds, not
// certified verification (that needs a calibrated verifier with ISO lighting).
//
// Grades: 4 = A, 3 = B, 2 = C, 1 = D, 0 = F, -1 = not measured.

#include "reader.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <numeric>

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

float percentile(std::vector<float> v, float q)
{
    if (v.empty()) return 0.f;
    const size_t k = std::min(v.size() - 1, static_cast<size_t>(q * (v.size() - 1)));
    std::nth_element(v.begin(), v.begin() + static_cast<long>(k), v.end());
    return v[k];
}

// The decoder's unused error correction after ISO/IEC 15415: 1 - 2t / (d - p) for t errors
// corrected in a block of d error correction codewords, p of them misdecode protection that may
// not be used for correction; 0 when 2t > d - p. zxing reports 1 - 2t / d of its worst block,
// ignoring p (it corrects 3 errors in a QR 1-L where ISO allows 2), printed cut to two decimals;
// t is recovered from that. p is not 0 only in single-block symbols: QR (ISO/IEC 18004 table 9)
// 1-L/M/Q/H, 2-L and 3-L, Micro QR M2-L/M, M3-L and M4-L; Data Matrix (ISO/IEC 16022) where d is
// odd: 10 x 10, 12 x 12, 8 x 18 and 8 x 32. Elsewhere the decoder's value as it is.
float isoUec(int format, int w, int h, const std::string& ecLevel, float zx)
{
    if (zx < 0.f) return -1.f;
    const char l = ecLevel.empty() ? '?' : ecLevel[0];
    int d = 0, p = 0;
    if (format == CR_QR && w == h) {
        const int v = (w - 17) / 4;
        if (v == 1) { d = l == 'L' ? 7 : l == 'M' ? 10 : l == 'Q' ? 13 : 17; p = l == 'L' ? 3 : l == 'M' ? 2 : 1; }
        else if (v == 2 && l == 'L') { d = 10; p = 2; }
        else if (v == 3 && l == 'L') { d = 15; p = 1; }
    }
    else if (format == CR_MICRO_QR && w == h) {
        if (w == 13) { d = l == 'L' ? 5 : 6; p = l == 'L' ? 3 : 2; }
        else if (w == 15 && l == 'L') { d = 6; p = 2; }
        else if (w == 17 && l == 'L') { d = 8; p = 2; }
    }
    else if (format == CR_DATAMATRIX) {
        const int a = std::min(w, h), b = std::max(w, h);
        d = a == 10 && b == 10 ? 5 : a == 12 && b == 12 ? 7 : a == 8 && b == 18 ? 7 : a == 8 && b == 32 ? 11 : 0;
        p = d ? 1 : 0;
    }
    if (!d) return std::clamp(zx, 0.f, 1.f);
    const int t = static_cast<int>(std::lround((1.f - std::clamp(zx, 0.f, 1.f)) * d / 2.f));
    return 2 * t > d - p ? 0.f : 1.f - 2.f * t / static_cast<float>(d - p);
}

// Micro QR M1 (11 x 11): its two check codewords only detect (ISO/IEC 18004: d = p = 2), and
// zxing reports UEC 0 even for a clean M1, so its own check on the decoder's sampled grid: 1 when
// the check codewords agree with the data, 0 when they do not (a codeword was wrong), -1 when the
// format information cannot be read. modules: 1 = dark, row by row.
float microQrM1Uec(const std::vector<uint8_t>& modules)
{
    constexpr int n = 11;
    if (modules.size() != static_cast<size_t>(n * n)) return -1.f;
    auto dark = [&](int x, int y) { return modules[static_cast<size_t>(y) * n + x] != 0; };
    // format information: row 8 x = 1..8, then column 8 y = 7..1, first bit the highest; masked
    // with 0x4445; BCH (15, 5), generator 0x537; the nearest valid word within 3 bits
    int f = 0;
    for (int x = 1; x <= 8; ++x) f = f << 1 | dark(x, 8);
    for (int y = 7; y >= 1; --y) f = f << 1 | dark(8, y);
    int best = -1, bestDist = 4;
    for (int data = 0; data < 32; ++data) {
        int r = data << 10;
        for (int i = 14; i >= 10; --i)
            if (r >> i & 1) r ^= 0x537 << (i - 10);
        const int word = (data << 10 | r) ^ 0x4445;
        int dist = 0;
        for (int v = word ^ f; v; v &= v - 1) ++dist;
        if (dist < bestDist) { bestDist = dist; best = data; }
    }
    if (best < 0 || best >> 2 != 0) return -1.f; // symbol number 0 is M1
    const int mask = best & 3;
    auto masked = [&](int x, int y) { // i = row, j = column
        switch (mask) {
        case 0: return y % 2 == 0;
        case 1: return (y / 2 + x / 3) % 2 == 0;
        case 2: return ((x * y) % 2 + (x * y) % 3) % 2 == 0;
        default: return ((x + y) % 2 + (x * y) % 3) % 2 == 0;
        }
    };
    // 36 data bits upward and downward in two-module columns from the bottom right, skipping the
    // finder, separator and format area (x, y <= 8) and the timing row and column 0
    std::vector<int> bits;
    bool up = true;
    for (int right = n - 1; right >= 1; right -= 2, up = !up)
        for (int k = 0; k < n; ++k) {
            const int y = up ? n - 1 - k : k;
            for (int j = 0; j < 2; ++j) {
                const int x = right - j;
                if ((x <= 8 && y <= 8) || x == 0 || y == 0) continue;
                bits.push_back(dark(x, y) != masked(x, y));
            }
        }
    if (bits.size() != 36) return -1.f;
    // codewords: two of 8 bits, one of 4 (the high half of a byte), then the two check codewords
    int cw[5] = {};
    size_t b = 0;
    for (int i = 0; i < 5; ++i) {
        const int len = i == 2 ? 4 : 8;
        for (int k = 0; k < len; ++k) cw[i] = cw[i] << 1 | bits[b++];
        if (len == 4) cw[i] <<= 4;
    }
    // Reed-Solomon over GF(256) (0x11D), generator (x - 1)(x - 2): both syndromes 0 when intact
    int exp[512], lg[256] = {};
    for (int i = 0, x = 1; i < 255; ++i, x = x << 1 ^ (x & 0x80 ? 0x11D : 0)) { exp[i] = exp[i + 255] = x; lg[x] = i; }
    auto mul = [&](int a, int c) { return a && c ? exp[lg[a] + lg[c]] : 0; };
    for (int alpha : {1, 2}) {
        int s = 0;
        for (int v : cw) s = mul(s, alpha) ^ v;
        if (s) return 0.f;
    }
    return 1.f;
}

// average grey level in a small square around p (about a third of a module)
float sampleAt(const cv::Mat& g, cv::Point2f p, float r)
{
    const int x0 = std::max(0, static_cast<int>(p.x - r)), x1 = std::min(g.cols - 1, static_cast<int>(p.x + r));
    const int y0 = std::max(0, static_cast<int>(p.y - r)), y1 = std::min(g.rows - 1, static_cast<int>(p.y + r));
    if (x1 < x0 || y1 < y0) return -1.f;
    double s = 0;
    int n = 0;
    for (int y = y0; y <= y1; ++y)
        for (int x = x0; x <= x1; ++x) {
            s += g.at<uint8_t>(y, x);
            ++n;
        }
    return n ? static_cast<float>(s / n) : -1.f;
}

// Aztec (ISO/IEC 24778): the bullseye's centre and the ring of its orientation marks (5 compact, 7
// full-range). A full-range symbol has a light ring 5 and a dark ring 6 round the compact bullseye;
// at 15 modules it is compact, from 31 full-range; 19, 23 and 27 can be either, so the sampled
// modules tell.
struct AztecShape {
    int c = 0, ring = 5;
    bool full = false;
};
AztecShape aztecShape(int w, const std::vector<uint8_t>& modules)
{
    AztecShape a;
    a.c = w / 2;
    if (w >= 31) a.full = true;
    else if (w > 15 && modules.size() == static_cast<size_t>(w) * w) {
        int n5 = 0, light5 = 0, n6 = 0, dark6 = 0;
        for (int y = 0; y < w; ++y)
            for (int x = 0; x < w; ++x) {
                const int d = std::max(std::abs(x - a.c), std::abs(y - a.c));
                const bool dark = modules[static_cast<size_t>(y) * w + x] != 0;
                if (d == 5) { ++n5; light5 += !dark; }
                if (d == 6) { ++n6; dark6 += dark; }
            }
        a.full = light5 >= 0.9f * n5 && dark6 >= 0.9f * n6;
    }
    a.ring = a.full ? 7 : 5;
    return a;
}

// rMQR (ISO/IEC 23941): the columns of the alignment patterns and vertical timing patterns for each
// symbol width; empty for a width no rMQR has (then the symbol is not graded)
std::vector<int> rmqrAlignment(int w, bool& known)
{
    known = true;
    switch (w) {
    case 27: return {};
    case 43: return {21};
    case 59: return {19, 39};
    case 77: return {25, 51};
    case 99: return {23, 49, 75};
    case 139: return {27, 55, 83, 111};
    default: known = false; return {};
    }
}

bool rmqrSize(int w, int h)
{
    bool known = false;
    rmqrAlignment(w, known);
    return known && h >= 7 && h <= 17 && h % 2 == 1;
}

// The fixed patterns: 1 = must be dark, 0 = must be light, -1 = data (not fixed).
std::vector<int8_t> fixedPattern(int format, int w, int h, const std::vector<uint8_t>& modules)
{
    std::vector<int8_t> f(static_cast<size_t>(w) * h, -1);
    auto set = [&](int x, int y, int v) {
        if (x >= 0 && y >= 0 && x < w && y < h) f[static_cast<size_t>(y) * w + x] = static_cast<int8_t>(v);
    };
    // a 7x7 finder (dark ring, light ring, dark 3x3) at x0, y0 with its light separator
    auto finder = [&](int x0, int y0) {
        for (int y = -1; y <= 7; ++y)
            for (int x = -1; x <= 7; ++x) {
                const int ax = std::abs(x - 3), ay = std::abs(y - 3), d = std::max(ax, ay);
                set(x0 + x, y0 + y, d == 4 ? 0 : (d == 2 ? 0 : 1)); // ring 3 dark, 2 light, 0-1 dark, 4 separator
            }
    };
    if (format == CR_QR && w == h && w >= 21) {
        // three finder patterns (7x7 rings) with their light separators, and the timing patterns
        finder(0, 0);
        finder(w - 7, 0);
        finder(0, h - 7);
        for (int i = 8; i < w - 8; ++i) {
            set(i, 6, i % 2 == 0 ? 1 : 0);
            set(6, i, i % 2 == 0 ? 1 : 0);
        }
    }
    else if (format == CR_MICRO_QR && w == h && w >= 11 && w <= 17) {
        // ISO/IEC 18004 Micro QR: one finder top left with its separator, the timing patterns along
        // the top row and the left column
        finder(0, 0);
        for (int i = 8; i < w; ++i) {
            set(i, 0, i % 2 == 0 ? 1 : 0);
            set(0, i, i % 2 == 0 ? 1 : 0);
        }
    }
    else if (format == CR_DATAMATRIX && w >= 10 && h >= 8) {
        // L finder: left column and bottom row dark; clock track: top row and right column alternate
        for (int y = 0; y < h; ++y) set(0, y, 1);
        for (int x = 0; x < w; ++x) set(x, h - 1, 1);
        for (int x = 0; x < w; ++x) set(x, 0, x % 2 == 0 ? 1 : 0);
        for (int y = 0; y < h; ++y) set(w - 1, y, (h - 1 - y) % 2 == 0 ? 1 : 0);
    }
    else if (format == CR_AZTEC && w == h && w >= 15 && w % 2 == 1) {
        // ISO/IEC 24778: the bullseye (dark rings at even distances from the centre), the
        // orientation marks at the corners of the mode message ring (top left 3 dark, top right 2,
        // bottom right 1, bottom left none), and on full-range symbols the reference grid: every
        // 16th row and column from the centre, dark where the offset from the centre is even
        const AztecShape a = aztecShape(w, modules);
        const int c = a.c, s = a.ring;
        if (a.full)
            for (int j = 0; j <= c; j += 16)
                for (int k = 0; k < w; ++k) {
                    const int v = (k - c) % 2 == 0 ? 1 : 0;
                    for (int line : {c - j, c + j})
                        for (const auto& [x, y] : {std::pair{line, k}, std::pair{k, line}})
                            if (std::max(std::abs(x - c), std::abs(y - c)) > s) set(x, y, v);
                }
        for (int y = c - s + 1; y <= c + s - 1; ++y)
            for (int x = c - s + 1; x <= c + s - 1; ++x) set(x, y, std::max(std::abs(x - c), std::abs(y - c)) % 2 == 0 ? 1 : 0);
        const int marks[12][3] = {{-s, -s, 1}, {-s + 1, -s, 1}, {-s, -s + 1, 1}, // top left
                                  {s, -s, 1}, {s, -s + 1, 1}, {s - 1, -s, 0},    // top right
                                  {s, s - 1, 1}, {s, s, 0}, {s - 1, s, 0},       // bottom right
                                  {-s, s, 0}, {-s + 1, s, 0}, {-s, s - 1, 0}};   // bottom left
        for (const auto& m : marks) set(c + m[0], c + m[1], m[2]);
    }
    else if (format == CR_RMQR && rmqrSize(w, h)) {
        // ISO/IEC 23941: timing patterns along the four edges, dark where the position is even
        for (int x = 0; x < w; ++x) { set(x, 0, x % 2 == 0); set(x, h - 1, x % 2 == 0); }
        for (int y = 0; y < h; ++y) { set(0, y, y % 2 == 0); set(w - 1, y, y % 2 == 0); }
        finder(0, 0); // with its separator right of it and, from 9 rows, under it
        // corner patterns: top right always; bottom left from 9 rows (the bottom row's second
        // module), its upper part from 11 (at 9 the separator lies there)
        set(w - 2, 0, 1); set(w - 1, 1, 1); set(w - 2, 1, 0);
        if (h >= 9) set(1, h - 1, 1);
        if (h >= 11) { set(0, h - 2, 1); set(1, h - 2, 0); }
        // the finder sub-pattern bottom right: a dark ring, a light ring, a dark centre (5 x 5)
        for (int y = 0; y < 5; ++y)
            for (int x = 0; x < 5; ++x) set(w - 5 + x, h - 5 + y, std::max(std::abs(x - 2), std::abs(y - 2)) != 1);
        // alignment patterns (3 x 3, light centre) on the top and bottom edges, the vertical
        // timing pattern between them
        bool known = false;
        for (const int a : rmqrAlignment(w, known)) {
            for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx) {
                    set(a + dx, 1 + dy, dx != 0 || dy != 0);
                    set(a + dx, h - 2 + dy, dx != 0 || dy != 0);
                }
            for (int y = 3; y <= h - 4; ++y) set(a, y, y % 2 == 0);
        }
    }
    return f;
}

} // namespace

// Homography from 4 point pairs (the same as getPerspectiveTransform, which OpenCV 5 moved out
// of imgproc): solve the 8x8 linear system with core only.
cv::Mat homography4(const cv::Point2f s[4], const cv::Point2f d[4])
{
    cv::Mat A(8, 8, CV_64F, cv::Scalar(0)), b(8, 1, CV_64F);
    for (int i = 0; i < 4; ++i) {
        const double x = s[i].x, y = s[i].y, u = d[i].x, v = d[i].y;
        double* r0 = A.ptr<double>(2 * i);
        double* r1 = A.ptr<double>(2 * i + 1);
        r0[0] = x; r0[1] = y; r0[2] = 1; r0[6] = -x * u; r0[7] = -y * u;
        r1[3] = x; r1[4] = y; r1[5] = 1; r1[6] = -x * v; r1[7] = -y * v;
        b.at<double>(2 * i) = u;
        b.at<double>(2 * i + 1) = v;
    }
    cv::Mat hvec;
    cv::solve(A, b, hvec, cv::DECOMP_SVD);
    cv::Mat H(3, 3, CV_64F);
    for (int i = 0; i < 8; ++i) H.at<double>(i / 3, i % 3) = hvec.at<double>(i);
    H.at<double>(2, 2) = 1.0;
    return H;
}

Quality gradeCode(const cv::Mat& gray, const Code& c, const Calibration* cal)
{
    Quality q;
    const bool dot = c.format == CR_DOTCODE, rmqr = c.format == CR_RMQR;
    const int minSide = dot ? 5 : rmqr ? 7 : 8; // a DotCode may be 5 dots high, an rMQR 7 modules
    if (c.modules.empty() || c.modW < minSide || c.modH < minSide || c.modules.size() != static_cast<size_t>(c.modW) * c.modH) return q;
    // an rMQR of a size the standard has not: its fixed patterns are unknown, so not graded (never A)
    if (rmqr && !rmqrSize(c.modW, c.modH)) return q;
    const int w = c.modW, h = c.modH;

    // module grid -> image: corners are the outer corners of the symbol, in symbol order
    const cv::Point2f src[4] = {{0.f, 0.f}, {static_cast<float>(w), 0.f}, {static_cast<float>(w), static_cast<float>(h)},
                                {0.f, static_cast<float>(h)}};
    const cv::Point2f dst[4] = {{c.x[0], c.y[0]}, {c.x[1], c.y[1]}, {c.x[2], c.y[2]}, {c.x[3], c.y[3]}};
    const cv::Mat H = homography4(src, dst);

    // pitch along each axis from the corner geometry (axial non-uniformity)
    const float xPitch = static_cast<float>((cv::norm(dst[1] - dst[0]) + cv::norm(dst[2] - dst[3])) / (2.0 * w));
    const float yPitch = static_cast<float>((cv::norm(dst[3] - dst[0]) + cv::norm(dst[2] - dst[1])) / (2.0 * h));
    q.moduleSizePx = (xPitch + yPitch) / 2.f;
    q.axialNonUniformity = std::abs(xPitch - yPitch) / std::max(1e-3f, q.moduleSizePx);

    // sample every module centre
    std::vector<cv::Point2f> centres, pts;
    centres.reserve(static_cast<size_t>(w) * h);
    for (int y = 0; y < h; ++y)
        for (int x = 0; x < w; ++x) centres.emplace_back(x + 0.5f, y + 0.5f);
    cv::perspectiveTransform(centres, pts, H);
    // calibrated: the ISO 15415 measuring aperture (0.8 X across) and reflectance %; otherwise a
    // third of a module and grey levels, as before
    const bool refl = cal && cal->reflectance;
    const float r = refl ? std::max(0.5f, 0.4f * q.moduleSizePx) : std::max(0.5f, q.moduleSizePx / 6.f);
    std::vector<float> R(pts.size());
    for (size_t i = 0; i < pts.size(); ++i) R[i] = sampleAt(gray, pts[i], r);
    if (std::any_of(R.begin(), R.end(), [](float v) { return v < 0; })) return q; // partly outside the image
    if (cal) {
        for (size_t i = 0; i < R.size(); ++i) R[i] = refl ? toReflectance(*cal, pts[i], R[i]) * 2.55f : flatCorrect(*cal, pts[i], R[i]);
        if (cal->mmPerPx > 0.f) q.xDimMm = (lengthMm(*cal, dst[0], dst[1]) / w + lengthMm(*cal, dst[0], dst[3]) / h) / 2.f;
        q.calibrated = refl;
    }

    std::vector<float> darkR, lightR;
    for (size_t i = 0; i < R.size(); ++i) (c.modules[i] ? darkR : lightR).push_back(R[i]);
    if (darkR.empty() || lightR.empty()) return q;

    // symbol contrast: light vs dark modules, relative to the light level (uncalibrated)
    const float rMax = percentile(lightR, 0.9f), rMin = percentile(darkR, 0.1f);
    // calibrated: SC = Rmax - Rmin in reflectance % (R holds % x 2.55); else relative to the light level
    q.symbolContrast = refl ? std::max(0.f, (rMax - rMin) / 2.55f) : std::max(0.f, (rMax - rMin) / std::max(1.f, rMax) * 100.f);
    const float gt = (rMax + rMin) / 2.f;
    const float half = std::max(1.f, (rMax - rMin) / 2.f);

    // modulation / reflectance margin: each module's distance from the global threshold, signed
    // so a module on the wrong side of the threshold (vs the decoded value) counts as negative
    std::vector<float> margin(R.size());
    int wrong = 0;
    for (size_t i = 0; i < R.size(); ++i) {
        const float m = (R[i] - gt) / half * (c.modules[i] ? -1.f : 1.f);
        margin[i] = m;
        if (m < 0) ++wrong;
    }
    std::vector<float> absMargin(margin.size());
    std::transform(margin.begin(), margin.end(), absMargin.begin(), [](float m) { return std::abs(m); });
    q.modulation = percentile(absMargin, 0.05f);
    q.reflectanceMargin = std::max(0.f, percentile(margin, 0.05f));

    // fixed pattern damage: finder / timing (QR), L and clock track (Data Matrix), finders,
    // corners, alignment and timing (rMQR)
    const auto fixed = fixedPattern(c.format, w, h, c.modules);
    int fixedN = 0, fixedBad = 0;
    for (size_t i = 0; i < fixed.size(); ++i) {
        if (fixed[i] < 0) continue;
        ++fixedN;
        const bool dark = R[i] < gt;
        if (dark != (fixed[i] == 1)) ++fixedBad;
    }
    // DotCode has no fixed pattern: not measured (-1), never a perfect 0
    q.fixedPatternDamage = dot ? -1.f : fixedN ? static_cast<float>(fixedBad) / fixedN : 0.f;

    // unused error correction: the decoder's own count where it gives one (zxing, from the Reed-
    // Solomon errors it corrected, after ISO/IEC 15415: isoUec); Micro QR M1 from its own check
    // codewords. Otherwise (a read by the engine's own grid decoders) an approximation: wrong
    // modules against the decoder's correction capacity. QR: L 7%, M 15%, Q 25%, H 30% of
    // codewords; Data Matrix about 30%; Aztec its stated share.
    float ec = 0.3f;
    if (c.format == CR_QR || c.format == CR_MICRO_QR || rmqr) { // rMQR: M or H
        const std::string& l = c.ecLevel;
        ec = l.find('L') != std::string::npos ? 0.07f : l.find('M') != std::string::npos ? 0.15f
           : l.find('Q') != std::string::npos ? 0.25f : 0.30f;
    }
    else if (c.format == CR_AZTEC && !c.ecLevel.empty() && c.ecLevel.back() == '%')
        ec = std::clamp(std::strtof(c.ecLevel.c_str(), nullptr) / 100.f, 0.05f, 0.95f);
    const float dataN = static_cast<float>(R.size() - static_cast<size_t>(fixedN));
    const float wrongData = static_cast<float>(wrong - fixedBad);
    // a wrong module damages about one codeword; correctable errors are half the EC codewords
    const float codewords = std::max(1.f, dataN / 8.f);
    q.unusedErrorCorrection = std::clamp(1.f - (std::max(0.f, wrongData) / codewords) / (ec / 2.f), 0.f, 1.f);
    // M1 (11 x 11) has no correction to spare: its check codewords on the decoder's grid, never
    // the decoder's UEC or the approximation; not measured when its format cannot be read
    const bool m1 = c.format == CR_MICRO_QR && w == 11;
    if (m1) q.unusedErrorCorrection = microQrM1Uec(c.modules);
    else if (c.uec >= 0.f) q.unusedErrorCorrection = isoUec(c.format, w, h, c.ecLevel, c.uec);

    // grid non-uniformity (approximation): where the timing / clock pattern's dark modules really
    // are vs where the straight grid puts them, in modules (curvature, wrinkles)
    {
        std::vector<float> dev;
        cv::Mat g32;
        auto locate = [&](cv::Point2f expected, cv::Point2f along) {
            // search +-1 module along the pattern (its neighbours there are light) for the darkest
            // point; a flat dark module (a sharp print) is dark over a run of steps, so the middle
            // of the darkest run counts, the whole run wherever it lies in the window
            constexpr int kN = 21; // steps of 0.1 module
            float v[kN], best = 1e9f;
            int at = -1;
            for (int k = 0; k < kN; ++k) {
                v[k] = sampleAt(gray, expected + along * (-1.f + 0.1f * k), r);
                if (v[k] >= 0 && v[k] < best) { best = v[k]; at = k; }
            }
            if (at < 0) return;
            int lo = at, hi = at;
            while (lo > 0 && v[lo - 1] >= 0 && v[lo - 1] <= best + 1.f) --lo;
            while (hi + 1 < kN && v[hi + 1] >= 0 && v[hi + 1] <= best + 1.f) ++hi;
            dev.push_back(std::abs(-1.f + 0.1f * (lo + hi) / 2.f));
        };
        auto toImg = [&](float x, float y) {
            std::vector<cv::Point2f> in{{x, y}}, out;
            cv::perspectiveTransform(in, out, H);
            return out[0];
        };
        if (c.format == CR_QR && w == h && w >= 21) {
            for (int i = 8; i < w - 8; i += 2) {
                locate(toImg(i + 0.5f, 6.5f), toImg(i + 1.5f, 6.5f) - toImg(i + 0.5f, 6.5f));
                locate(toImg(6.5f, i + 0.5f), toImg(6.5f, i + 1.5f) - toImg(6.5f, i + 0.5f));
            }
        }
        else if (c.format == CR_MICRO_QR) {
            // the timing patterns along the top row and the left column
            for (int i = 8; i < w; i += 2) {
                locate(toImg(i + 0.5f, 0.5f), toImg(i + 1.5f, 0.5f) - toImg(i + 0.5f, 0.5f));
                locate(toImg(0.5f, i + 0.5f), toImg(0.5f, i + 1.5f) - toImg(0.5f, i + 0.5f));
            }
        }
        else if (rmqr) {
            // the dark modules of the timing patterns along the top and bottom edges and of the
            // vertical timing patterns, where both neighbours along the pattern are light: not by
            // the finder, the corner patterns, the sub-pattern or an alignment pattern
            bool known = false;
            const auto align = rmqrAlignment(w, known);
            const auto clear = [&](int x) {
                return std::none_of(align.begin(), align.end(), [&](int a) { return std::abs(x - a) <= 2; });
            };
            for (int x = 8; x <= w - 5; x += 2)
                if (clear(x)) locate(toImg(x + 0.5f, 0.5f), toImg(x + 1.5f, 0.5f) - toImg(x + 0.5f, 0.5f));
            for (int x = h >= 9 ? 4 : 8; x <= w - 7; x += 2)
                if (clear(x)) locate(toImg(x + 0.5f, h - 0.5f), toImg(x + 1.5f, h - 0.5f) - toImg(x + 0.5f, h - 0.5f));
            for (const int a : align)
                for (int y = 4; y <= h - 5; y += 2) locate(toImg(a + 0.5f, y + 0.5f), toImg(a + 0.5f, y + 1.5f) - toImg(a + 0.5f, y + 0.5f));
        }
        else if (c.format == CR_DATAMATRIX) {
            for (int x = 0; x < w; x += 2) locate(toImg(x + 0.5f, 0.5f), toImg(x + 1.5f, 0.5f) - toImg(x + 0.5f, 0.5f));
        }
        else if (c.format == CR_AZTEC && w == h) {
            // the bullseye's dark rings across its centre row and column (not the outermost, whose
            // neighbour is the mode message, data that may be dark), and on full-range symbols the
            // dark modules of the reference grid beyond it
            const AztecShape a = aztecShape(w, c.modules);
            const float cc = a.c + 0.5f;
            for (int d = -(a.ring - 3); d <= a.ring - 3; d += 2) {
                locate(toImg(cc + d, cc), toImg(cc + d + 1, cc) - toImg(cc + d, cc));
                locate(toImg(cc, cc + d), toImg(cc, cc + d + 1) - toImg(cc, cc + d));
            }
            if (a.full)
                for (int j = 0; j <= a.c; j += 16)
                    for (int line : {a.c - j, a.c + j})
                        for (int k = 0; k < w; ++k) {
                            if ((k - a.c) % 2 != 0 || line >= w || std::abs(k - a.c) <= a.ring) continue;
                            const float l = line + 0.5f, kk = k + 0.5f;
                            locate(toImg(l, kk), toImg(l, kk + 1) - toImg(l, kk)); // along a grid column
                            locate(toImg(kk, l), toImg(kk + 1, l) - toImg(kk, l)); // along a grid row
                        }
        }
        else if (dot) {
            // each printed dot's centre: the darkness-weighted mean of the points darker than the
            // threshold within 0.7 pitch, in pitches from its grid place; the circle moved onto that
            // centre and measured again (twice), so a dot far off is not cut off at the circle's edge
            // and measured short. The nearest other dots (diagonal neighbours) lie 1.41 pitches away,
            // their edges past 1 pitch even with heavy print growth; the points beside it on the axes
            // are never printed. A printed dot with nothing dark round its place counts as off by the
            // whole circle (line safety: never as on its place)
            constexpr int kN = 15;
            constexpr float kR = 0.7f;
            const cv::Matx33d Hm(H);
            const auto at = [&](float gx, float gy) { // toImg without its allocations: a large symbol has thousands of dots
                const cv::Vec3d v = Hm * cv::Vec3d(gx, gy, 1.0);
                return cv::Point2f(static_cast<float>(v[0] / v[2]), static_cast<float>(v[1] / v[2]));
            };
            for (int y = 0; y < h; ++y)
                for (int x = 0; x < w; ++x) {
                    if (!c.modules[static_cast<size_t>(y) * w + x]) continue;
                    float cx = 0.f, cy = 0.f; // the centre so far, in pitches from the place
                    bool found = false;
                    for (int pass = 0; pass < 3; ++pass) {
                        float sw = 0.f, sx = 0.f, sy = 0.f;
                        for (int j = 0; j < kN; ++j)
                            for (int i = 0; i < kN; ++i) {
                                const float ox = kR * (-1.f + 2.f * static_cast<float>(i) / (kN - 1)), oy = kR * (-1.f + 2.f * static_cast<float>(j) / (kN - 1));
                                if (ox * ox + oy * oy > kR * kR) continue;
                                const float dx = cx + ox, dy = cy + oy;
                                // bilinear: a box of whole pixels would jitter the centre by its own rounding
                                const cv::Point2f p = at(x + 0.5f + dx, y + 0.5f + dy);
                                const int px = static_cast<int>(std::floor(p.x)), py = static_cast<int>(std::floor(p.y));
                                if (px < 0 || py < 0 || px + 1 >= gray.cols || py + 1 >= gray.rows) continue;
                                const float fx = p.x - static_cast<float>(px), fy = p.y - static_cast<float>(py);
                                float v = (1 - fy) * ((1 - fx) * gray.at<uint8_t>(py, px) + fx * gray.at<uint8_t>(py, px + 1)) +
                                          fy * ((1 - fx) * gray.at<uint8_t>(py + 1, px) + fx * gray.at<uint8_t>(py + 1, px + 1));
                                // in the threshold's own units (R: flat-field corrected, or reflectance x 2.55)
                                if (cal) v = refl ? toReflectance(*cal, p, v) * 2.55f : flatCorrect(*cal, p, v);
                                if (v >= gt) continue;
                                sw += gt - v; sx += (gt - v) * dx; sy += (gt - v) * dy;
                            }
                        if (sw <= 0.f) break;
                        cx = sx / sw;
                        cy = sy / sw;
                        found = true;
                    }
                    dev.push_back(found ? std::hypot(cx, cy) : kR);
                }
        }
        q.gridNonUniformity = dev.empty() ? -1.f : *std::max_element(dev.begin(), dev.end());
    }

    // print growth: dark vs light module size along the timing / clock pattern, from the share
    // of pixels darker than the threshold in dark and light modules (0 = nominal)
    {
        float darkFill = 0, lightFill = 0;
        int nd = 0, nl = 0;
        for (size_t i = 0; i < fixed.size(); ++i) {
            if (fixed[i] < 0) continue;
            const float v = R[i];
            const float fill = std::clamp((gt - v) / half * 0.5f + 0.5f, 0.f, 1.f);
            if (fixed[i] == 1) { darkFill += fill; ++nd; }
            else { lightFill += fill; ++nl; }
        }
        if (nd && nl) q.printGrowth = (darkFill / nd + lightFill / nl) - 1.f;
    }

    // quiet zone: dark modules in a ring as wide as the symbology's quiet zone outside the symbol:
    // 4 modules for QR (ISO/IEC 18004), 2 for Micro QR and rMQR (ISO/IEC 23941), 3 for DotCode, 1
    // for Data Matrix; Aztec needs none (ISO/IEC 24778), so it has no quiet zone grade
    if (c.format != CR_AZTEC) {
        const int ring = c.format == CR_QR ? 4 : c.format == CR_MICRO_QR || rmqr ? 2 : dot ? 3 : 1;
        std::vector<cv::Point2f> rp, ri;
        for (int y = -ring; y < h + ring; ++y)
            for (int x = -ring; x < w + ring; ++x)
                if (x < 0 || y < 0 || x >= w || y >= h) rp.emplace_back(x + 0.5f, y + 0.5f);
        cv::perspectiveTransform(rp, ri, H);
        // a sample off the image is no quiet zone: it counts dark, so a zone the image cuts can
        // never grade clear (line safety, as the PDF417, 1D and DPM graders)
        int bad = 0, n = 0;
        for (const auto& p : ri) {
            float v = sampleAt(gray, p, r);
            // in the threshold's own units (R: flat-field corrected, or reflectance x 2.55), as the
            // modules: raw grey against a calibrated threshold grades a clear margin dark (or a mark clear)
            if (v >= 0 && cal) v = refl ? toReflectance(*cal, p, v) * 2.55f : flatCorrect(*cal, p, v);
            ++n;
            if (v < 0 || v < gt) ++bad;
        }
        q.quietZoneDark = n ? static_cast<float>(bad) / n : -1.f;
    }

    // lighting over the code: light-module level spread (1 = even), and saturated modules (glare)
    q.lightingUniformity = percentile(lightR, 0.1f) / std::max(1.f, percentile(lightR, 0.9f));
    q.saturated = static_cast<float>(std::count_if(R.begin(), R.end(), [](float v) { return v >= 250.f; })) / R.size();

    // grades with the ISO 15415 thresholds (FPD, UEC, GN approximated, see above)
    q.gradeSC = gradeHigh(q.symbolContrast, 70, 55, 40, 20);
    q.gradeMOD = gradeHigh(q.modulation, 0.50f, 0.40f, 0.30f, 0.20f);
    q.gradeRM = gradeHigh(q.reflectanceMargin, 0.50f, 0.40f, 0.30f, 0.20f);
    q.gradeFPD = dot ? -1 : gradeLow(q.fixedPatternDamage, 0.0f, 0.03f, 0.06f, 0.10f);
    q.gradeAN = gradeLow(q.axialNonUniformity, 0.06f, 0.08f, 0.10f, 0.12f);
    q.gradeGN = q.gridNonUniformity < 0 ? -1 : gradeLow(q.gridNonUniformity, 0.38f, 0.50f, 0.63f, 0.75f);
    q.gradeUEC = q.unusedErrorCorrection < 0 ? -1 : gradeHigh(q.unusedErrorCorrection, 0.62f, 0.50f, 0.37f, 0.25f);
    q.gradeQZ = q.quietZoneDark < 0 ? -1 : gradeLow(q.quietZoneDark, 0.0f, 0.05f, 0.10f, 0.20f);
    int overall = 4;
    for (int g : {q.gradeSC, q.gradeMOD, q.gradeRM, q.gradeFPD, q.gradeAN, q.gradeGN, q.gradeUEC, q.gradeQZ})
        if (g >= 0) overall = std::min(overall, g);
    q.overall = overall;
    q.measured = true;
    return q;
}

} // namespace cr
