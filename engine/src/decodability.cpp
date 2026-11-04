// decodability.cpp - ISO/IEC 15416 decodability of one scan's bars and spaces, per symbology, and the
// X-dimension from the element widths.
//
// Decodability is the share of the reference decoder's tolerance a scan leaves unused: 1 = every
// element exactly on its nominal width, 0 = an element on a decision threshold (one more step and it
// reads as another character). Two families:
//   n-width codes (Code 128, Code 93, EAN / UPC, DataBar): each character's edge-to-similar-edge
//     distances in modules against the decision points halfway between whole modules; EAN / UPC
//     characters 1, 2, 7 and 8, whose edge-to-similar-edge distances are the same in pairs (1 and 7,
//     2 and 8), also by their two bars' total width against the threshold between the pair's
//     (ISO/IEC 15420 reference decode: a 1 or 2 has its bars a module narrower in set A, wider in sets
//     B and C);
//   two-width codes (Code 39, Codabar, ITF): each character's narrow and wide elements against the
//     threshold between them, as the room left between the widest narrow and the narrowest wide one
//     over the room between their means (verifier-style; the standards' reference decodes use the
//     same threshold);
//   Pharmacode (no standard of its own; its reference decoder is the engine's, pharmacode.cpp): each
//     bar's bar + gap pitch, edge to similar edge as that decoder measures it, narrow + gap 3 units and
//     wide + gap 5, against its tolerance of 15% of the pitch; the unit from the symbol's length and
//     the bars the decoded value names. A code of one width also by each bar's width over its pitch
//     against the decoder's narrow / wide limits (0.44, 0.52), which ink spread moves.
// A scan whose element count does not fit the symbology gives -1 (not measured): it then fails on
// decode, never passes on decodability.

#include "reader.hpp"

#include <algorithm>
#include <array>
#include <initializer_list>
#include <cctype>
#include <string>
#include <cmath>
#include <numeric>

namespace cr {

namespace {

// n-width: characters of `elems` elements and `modules` modules each, starting at edge `first`,
// `count` of them; worst margin to a decision point as a share of the half module, or -1. width:
// the character's width when given (> 0), else its first to its closing edge
float nWidth(const std::vector<float>& e, size_t first, int elems, int modules, int count, float worst, float width = 0.f)
{
    for (int c = 0; c < count; ++c) {
        const size_t k = first + size_t(c) * size_t(elems);
        if (k + size_t(elems) >= e.size()) return -1.f;
        const float S = width > 0.f ? width : e[k + size_t(elems)] - e[k];
        if (S <= 0) return -1.f;
        for (int j = 0; j + 2 <= elems; ++j) {
            const float m = (e[k + size_t(j) + 2] - e[k + size_t(j)]) * float(modules) / S;
            worst = std::min(worst, (0.5f - std::abs(m - std::round(m))) / 0.5f);
        }
    }
    return worst;
}

// two-width: one character's elements, `wide` of them wide. The decision threshold is halfway
// between the narrow and wide means; the room the closest element leaves to it, over half the
// distance between the means: 1 = every element on its mean, 0 = an element on the threshold
float twoWidth(std::vector<float> w, int wide)
{
    if (w.size() < 3 || wide <= 0 || size_t(wide) >= w.size()) return -1.f;
    std::sort(w.begin(), w.end());
    const size_t split = w.size() - size_t(wide);
    const float nMax = w[split - 1], wMin = w[split];
    const float nMean = std::accumulate(w.begin(), w.begin() + long(split), 0.f) / float(split);
    const float wMean = std::accumulate(w.begin() + long(split), w.end(), 0.f) / float(w.size() - split);
    if (wMean <= nMean) return 0.f;
    const float T = 0.5f * (nMean + wMean);
    return std::clamp(std::min(T - nMax, wMin - T) / (0.5f * (wMean - nMean)), 0.f, 1.f);
}

// ISO/IEC 15420 reference decode of one EAN / UPC character of 4 elements from edge k: its edge-to-
// similar-edge distances T1, T2 in modules (print gain moves both edges of each alike), the digit
// whose (T1, T2) they are; 1 / 7 and 2 / 8 by their two bars' total width (eanBars). set: 'A', 'B'
// (left half, starts with a space) or 'C' (right half, starts with a bar); gain: the print gain on
// each bar in modules (from the guards), taken off the bars' total; -1 = no digit, also when the bars'
// total lies within half a module of the threshold (a 1 and a 7 could flip together with a 7 and a 1
// of the same weight, the check digit still holding)
int eanChar(const std::vector<float>& e, size_t k, bool startsWithBar, float gain, char* set)
{
    // (T1, T2) of set A's digits 0-9 (L patterns 3211 2221 2122 1411 1132 1231 1114 1312 1213 3112);
    // set C has the same, set B the patterns reversed
    static const int kA[10][2] = {{5, 3}, {4, 4}, {3, 3}, {5, 5}, {2, 4}, {3, 5}, {2, 2}, {4, 4}, {3, 3}, {4, 2}};
    static const int kB[10][2] = {{2, 3}, {3, 4}, {4, 3}, {2, 5}, {5, 4}, {4, 5}, {5, 2}, {3, 4}, {4, 3}, {3, 2}};
    const float S = e[k + 4] - e[k];
    if (S <= 0) return -1;
    const float m1 = (e[k + 2] - e[k]) * 7.f / S, m2 = (e[k + 3] - e[k + 1]) * 7.f / S;
    const int t1 = static_cast<int>(std::lround(m1)), t2 = static_cast<int>(std::lround(m2));
    const bool b = !startsWithBar && (t1 + t2) % 2 != 0;
    *set = startsWithBar ? 'C' : b ? 'B' : 'A';
    const int(*tab)[2] = b ? kB : kA;
    int digit = -1;
    for (int d = 0; d < 10; ++d)
        if (tab[d][0] == t1 && tab[d][1] == t2) { digit = d; break; }
    if (digit < 0) return -1;
    if (digit == 1 || digit == 2) {
        const size_t bar = startsWithBar ? k : k + 1;
        const float bars = ((e[bar + 1] - e[bar]) + (e[bar + 3] - e[bar + 2])) * 7.f / S - 2.f * gain;
        const bool setA = *set == 'A';
        const float thr = setA ? 4.f : 3.f;
        if (std::abs(bars - thr) < 0.5f) return -1;
        // a 1 or 2: bars under the threshold in set A (3 of 4), over it in B and C (4 of 3)
        const bool narrow = bars < thr;
        if (narrow != setA) digit += 6;
    }
    return digit;
}

// a guard of `n` one-module elements from edge k: each element 0.5 to 1.5 modules (m: the module)
bool eanGuard(const std::vector<float>& e, size_t k, int n, float m)
{
    for (int i = 0; i < n; ++i) {
        const float w = (e[k + size_t(i) + 1] - e[k + size_t(i)]) / m;
        if (w < 0.25f || w > 2.f) return false; // print gain moves one element's width, not its pair's
    }
    for (int i = 0; i + 2 <= n; ++i) {
        const float t = (e[k + size_t(i) + 2] - e[k + size_t(i)]) / m;
        if (t < 1.5f || t > 2.5f) return false;
    }
    return true;
}

char eanCheck(const std::string& body)
{
    int sum = 0;
    for (size_t i = 0; i < body.size(); ++i) sum += (body[body.size() - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
    return static_cast<char>('0' + (10 - sum % 10) % 10);
}

// UPC-E (number system, 6 digits, check digit) as the UPC-A it stands for (zxing's convertUPCEtoUPCA)
std::string upceToUpca(const std::string& t)
{
    const std::string d = t.substr(1, 6);
    std::string a = t.substr(0, 1);
    const char last = d[5];
    if (last <= '2') a += d.substr(0, 2) + last + "0000" + d.substr(2, 3);
    else if (last == '3') a += d.substr(0, 3) + "00000" + d.substr(3, 2);
    else if (last == '4') a += d.substr(0, 4) + "00000" + d[4];
    else a += d.substr(0, 5) + "0000" + last;
    return a + t.back();
}

// EAN / UPC characters 1, 2, 7 and 8 (ISO/IEC 15420): `count` characters of 4 elements from edge
// `first`, starting with a space (sets A and B, left half and UPC-E) or a bar (set C, right half).
// Their edge-to-similar-edge distances T1, T2 are the same for 1 and 7, and for 2 and 8: (4, 4) and
// (3, 3) in sets A and C, (3, 4) and (4, 3) in set B (A's T1 + T2 is even, B's odd). The reference
// decode tells them apart by the two bars' total width in modules: set A 3 (1, 2) or 5 (7, 8),
// threshold 4; sets B and C 4 (1, 2) or 2 (7, 8), threshold 3. The margin left to the threshold,
// over the nominal's distance to it (1 module): 1 = on the nominal, 0 = on the threshold. `digits`:
// the printed digit of each character when the decoded text gives it; a bar total on the other
// digit's side of the threshold then counts 0 (the reference decode reads another digit)
float eanBars(const std::vector<float>& e, size_t first, int count, bool startsWithBar, const std::string& digits, float worst)
{
    for (int c = 0; c < count; ++c) {
        const size_t k = first + size_t(c) * 4;
        if (k + 4 >= e.size()) return -1.f;
        const float S = e[k + 4] - e[k];
        if (S <= 0) return -1.f;
        const int t1 = int(std::lround((e[k + 2] - e[k]) * 7.f / S)), t2 = int(std::lround((e[k + 3] - e[k + 1]) * 7.f / S));
        const bool setB = !startsWithBar && (t1 + t2) % 2 != 0;
        const bool pair = setB ? (t1 == 3 && t2 == 4) || (t1 == 4 && t2 == 3) : (t1 == t2 && (t1 == 3 || t1 == 4));
        if (!pair) continue;
        const size_t b = startsWithBar ? k : k + 1; // the character's two bars: elements b and b + 2
        const float bars = ((e[b + 1] - e[b]) + (e[b + 3] - e[b + 2])) * 7.f / S;
        const bool setA = !startsWithBar && !setB;
        const float thr = setA ? 4.f : 3.f;
        float margin = std::min(1.f, std::abs(bars - thr));
        if (size_t(c) < digits.size()) {
            const char d = digits[size_t(c)];
            if (d == '1' || d == '2' || d == '7' || d == '8') {
                // a 1 or 2: bars under the threshold in set A, over it in B and C
                const bool narrowSide = bars < thr;
                if (narrowSide != ((d == '1' || d == '2') == setA)) margin = 0.f;
            }
        }
        worst = std::min(worst, margin);
    }
    return worst;
}

// the printed digits of the encoded characters, from the decoded text when it fits the type:
// EAN-13 the 12 after the first (the first sets the left half's parity), UPC-A and EAN-8 all,
// UPC-E the 6 between the number system and the check digit; empty when it does not fit
std::string eanDigits(int format, const std::string& text)
{
    const size_t n = format == CR_EAN13 ? 13 : format == CR_UPCA ? 12 : format == CR_EAN8 || format == CR_UPCE ? 8 : 0;
    if (text.size() != n || !std::all_of(text.begin(), text.end(), [](char ch) { return ch >= '0' && ch <= '9'; })) return {};
    if (format == CR_EAN13) return text.substr(1);
    if (format == CR_UPCE) return text.substr(1, 6);
    return text;
}

// DataBar: one data character of 8 or 14 elements (an even count: its width runs between similar
// edges) and `modules` modules from edge `first`
float dataBarChar(const std::vector<float>& e, size_t first, int elems, int modules, float worst)
{
    return worst < 0 ? worst : nWidth(e, first, elems, modules, 1, worst);
}

// DataBar: a finder of 5 elements, 15 modules, from edge `first`. Its own width runs from a space's
// leading edge to a bar's (dissimilar edges: ink spread changes it), so it is 15 modules of the
// characters' beside it: `chars` of (first edge, elements, modules)
float dataBarFinder(const std::vector<float>& e, size_t first, std::initializer_list<std::array<size_t, 3>> chars, float worst)
{
    if (worst < 0) return worst;
    float px = 0.f, mods = 0.f;
    for (const auto& [k, n, m] : chars) {
        if (k + n >= e.size()) return -1.f;
        px += e[k + n] - e[k];
        mods += float(m);
    }
    return mods > 0 ? nWidth(e, first, 5, 15, 1, worst, px * 15.f / mods) : -1.f;
}

std::vector<float> widths(const std::vector<float>& e, size_t from, size_t n)
{
    std::vector<float> w;
    for (size_t i = from; i < from + n && i + 1 < e.size(); ++i) w.push_back(e[i + 1] - e[i]);
    return w;
}

} // namespace

std::vector<bool> pharmacodeBars(int format, const std::string& text)
{
    std::vector<bool> b;
    if (text.empty() || text.size() > 6 || !std::all_of(text.begin(), text.end(), [](char c) { return c >= '0' && c <= '9'; })) return b;
    int v = std::stoi(text);
    if (v < 3 || v > 131070 || !(format & (CR_PHARMACODE | CR_PHARMACODE_REV))) return b;
    // lowest weight first: 2^k narrow, 2 x 2^k wide
    while (v > 0) {
        const bool wide = v % 2 == 0;
        b.push_back(wide);
        v = (v - (wide ? 2 : 1)) / 2;
    }
    // standard reading: the lowest weight on the right
    if (format & CR_PHARMACODE) std::reverse(b.begin(), b.end());
    return b;
}

namespace {

// a Pharmacode's bar + gap pitch per bar, edge to similar edge (2 x bars edges, from the first
// bar's leading edge), as pharmacode.cpp measures it
std::vector<float> pharmaPitches(const std::vector<float>& e)
{
    const size_t n = e.size() / 2;
    std::vector<float> p(n);
    for (size_t i = 0; i < n; ++i) {
        float s = 0.f;
        int k = 0;
        if (i + 1 < n) { s += e[2 * i + 2] - e[2 * i]; ++k; }
        if (i > 0) { s += e[2 * i + 1] - e[2 * i - 1]; ++k; }
        p[i] = k ? s / static_cast<float>(k) : 0.f;
    }
    return p;
}

} // namespace

float pharmacodeModule(const std::vector<float>& e, int format, const std::string& text)
{
    const auto bars = pharmacodeBars(format, text);
    if (bars.size() < 2 || e.size() != 2 * bars.size()) return 0.f;
    float units = 0.f;
    for (size_t i = 0; i < bars.size(); ++i) units += (bars[i] ? 3.f : 1.f) + (i + 1 < bars.size() ? 2.f : 0.f);
    // first leading to last trailing edge: print gain lengthens it by one spread only
    return (e.back() - e.front()) / units;
}

float decodability1d(const std::vector<float>& e, int format, const std::string& text)
{
    const size_t n = e.size(); // edges; elements = n - 1, from the first bar's leading edge
    if (format & (CR_PHARMACODE | CR_PHARMACODE_REV)) {
        const auto bars = pharmacodeBars(format, text);
        if (bars.size() < 2 || n != 2 * bars.size()) return -1.f;
        const auto p = pharmaPitches(e);
        const float u = pharmacodeModule(e, format, text);
        if (u <= 0.f) return -1.f;
        float worst = 1.f;
        for (size_t i = 0; i < bars.size(); ++i) {
            const float m = (bars[i] ? 5.f : 3.f) * u;
            worst = std::min(worst, 1.f - std::abs(p[i] - m) / (0.15f * m));
        }
        // a code of one width only (all narrow or all wide) has no second class for its pitches: the
        // decoder tells narrow from wide by each bar's width over its pitch (narrow 1/3 up to 0.44,
        // wide 3/5 down to 0.52, between them no read), which ink spread moves; its margin too
        if (std::all_of(bars.begin(), bars.end(), [&](bool b) { return b == bars[0]; }))
            for (size_t i = 0; i < bars.size(); ++i) {
                const float ratio = (e[2 * i + 1] - e[2 * i]) / p[i];
                worst = std::min(worst, bars[0] ? (ratio - 0.52f) / (0.6f - 0.52f) : (0.44f - ratio) / (0.44f - 1.f / 3.f));
            }
        return std::max(0.f, worst);
    }
    if (n < 6) return -1.f;
    switch (format) {
    case CR_GS1_128: {
        // characters of 6 elements, 11 modules; the stop is 13 modules, 7 elements
        if ((n - 1 - 7) % 6 != 0) return -1.f;
        return nWidth(e, 0, 6, 11, int((n - 1 - 7) / 6) + 1, 1.f);
    }
    case CR_CODE93: {
        // characters of 6 elements, 9 modules, and a terminating bar
        if ((n - 2) % 6 != 0) return -1.f;
        return nWidth(e, 0, 6, 9, int((n - 2) / 6), 1.f);
    }
    case CR_EAN13:
    case CR_UPCA:
    case CR_EAN8: {
        // 3 + h x 4 + 5 + h x 4 + 3 elements, h = 6 (EAN-13, UPC-A) or 4 (EAN-8)
        const int h = format == CR_EAN8 ? 4 : 6;
        if (n != size_t(3 + 8 * h + 5 + 3 + 1)) return -1.f;
        const std::string d = eanDigits(format, text);
        const size_t right = size_t(3 + 4 * h + 5);
        float v = nWidth(e, right, 4, 7, h, nWidth(e, 3, 4, 7, h, 1.f));
        if (v < 0) return v;
        v = eanBars(e, 3, h, false, d.empty() ? d : d.substr(0, size_t(h)), v);
        return v < 0 ? v : eanBars(e, right, h, true, d.empty() ? d : d.substr(size_t(h)), v);
    }
    case CR_UPCE: { // 3 + 6 x 4 + 6, sets A and B
        if (n != 34) return -1.f;
        const float v = nWidth(e, 3, 4, 7, 6, 1.f);
        return v < 0 ? v : eanBars(e, 3, 6, false, eanDigits(format, text), v);
    }
    case CR_CODE39: {
        // characters of 9 elements (3 wide) and a narrow gap between them
        if (n % 10 != 0) return -1.f;
        float worst = 1.f;
        for (size_t k = 0; k + 9 < n; k += 10) {
            const float v = twoWidth(widths(e, k, 9), 3);
            if (v < 0) return -1.f;
            worst = std::min(worst, v);
        }
        return worst;
    }
    case CR_CODABAR: {
        // characters of 7 elements and a gap between them; 0-9 - $ have 2 wide elements, : / . + and
        // the start / stop characters 3. The wide count comes from the decoded text (with or without
        // its start and stop characters), never from the widths themselves
        if (n % 8 != 0) return -1.f;
        const size_t chars = n / 8;
        std::string all = text;
        if (all.size() + 2 == chars) all = "A" + all + "A";
        if (all.size() != chars) return -1.f;
        float worst = 1.f;
        for (size_t k = 0, c = 0; k + 7 < n; k += 8, ++c) {
            const bool two = std::isdigit(static_cast<unsigned char>(all[c])) || all[c] == '-' || all[c] == '$';
            const float v = twoWidth(widths(e, k, 7), two ? 2 : 3);
            if (v < 0) return -1.f;
            worst = std::min(worst, v);
        }
        return worst;
    }
    case CR_ITF: {
        // start (4 narrow), pairs of 10 interleaved elements (each digit's 5 bars or 5 spaces, 2 wide),
        // stop (wide bar, narrow space, narrow bar)
        if (n < 4 + 10 + 3 + 1 || (n - 1 - 4 - 3) % 10 != 0) return -1.f;
        float worst = 1.f;
        for (size_t k = 4; k + 10 <= n - 1 - 3; k += 10) {
            const std::vector<float> w = widths(e, k, 10);
            std::vector<float> bars, spaces;
            for (size_t i = 0; i < w.size(); ++i) (i % 2 == 0 ? bars : spaces).push_back(w[i]);
            const float vb = twoWidth(bars, 2), vs = twoWidth(spaces, 2);
            if (vb < 0 || vs < 0) return -1.f;
            worst = std::min({worst, vb, vs});
        }
        return worst;
    }
    default:
        return -1.f; // DataBar: decodabilityDataBar
    }
}

std::string eanReferenceDecode(const std::vector<float>& e, int format)
{
    const int h = format == CR_EAN8 ? 4 : format == CR_UPCE ? 6 : (format & (CR_EAN13 | CR_UPCA)) ? 6 : 0;
    if (!h) return {};
    const bool upce = format == CR_UPCE;
    const size_t n = upce ? 34 : size_t(3 + 8 * h + 5 + 3 + 1);
    if (e.size() != n) return {};
    const float m = (e.back() - e.front()) / (upce ? 51.f : static_cast<float>(3 + 14 * h + 5 + 3));
    if (m <= 0 || !eanGuard(e, 0, 3, m)) return {};
    // print gain on each bar, in modules: the guards' one-module bars against their spaces
    float barSum = 0.f, spaceSum = 0.f;
    int nb = 0, ns = 0;
    const auto guard = [&](size_t k, int count, bool startsWithBar) {
        for (int i = 0; i < count; ++i) {
            const float w = e[k + size_t(i) + 1] - e[k + size_t(i)];
            if ((i % 2 == 0) == startsWithBar) { barSum += w; ++nb; }
            else { spaceSum += w; ++ns; }
        }
    };
    guard(0, 3, true);
    guard(upce ? 27 : size_t(3 + 4 * h), upce ? 6 : 5, false); // the middle guard, or UPC-E's end guard: a space first
    if (!upce) guard(n - 4, 3, true);
    const float gain = 0.5f * (barSum / nb - spaceSum / ns) / m;
    std::string digits, sets;
    const auto chars = [&](size_t first, int count, bool startsWithBar) {
        for (int c = 0; c < count; ++c) {
            char set = 0;
            const int d = eanChar(e, first + size_t(c) * 4, startsWithBar, gain, &set);
            if (d < 0) return false;
            digits += static_cast<char>('0' + d);
            sets += set;
        }
        return true;
    };
    if (!chars(3, h, false)) return {};
    if (upce) {
        // end guard: 6 one-module elements; the number system (0 or 1) and the check digit from the
        // sets' pattern, the check digit then checked on the UPC-A the code stands for
        if (!eanGuard(e, 3 + 4 * 6, 6, m)) return {};
        static const char* kPar[10] = {"BBBAAA", "BBABAA", "BBAABA", "BBAAAB", "BABBAA", "BAABBA", "BAAABB", "BABABA", "BABAAB", "BAABAB"};
        for (int ns = 0; ns < 2; ++ns)
            for (int ck = 0; ck < 10; ++ck) {
                std::string par = kPar[ck];
                if (ns) for (char& ch : par) ch = ch == 'A' ? 'B' : 'A';
                if (par != sets) continue;
                const std::string t = std::string(1, char('0' + ns)) + digits + char('0' + ck);
                const std::string a = upceToUpca(t);
                return eanCheck(a.substr(0, 11)) == t.back() ? t : std::string();
            }
        return {};
    }
    const size_t mid = size_t(3 + 4 * h), right = mid + 5;
    if (!eanGuard(e, mid, 5, m) || !eanGuard(e, n - 4, 3, m) || !chars(right, h, true)) return {};
    std::string text;
    if (h == 4) {
        if (sets.compare(0, 4, "AAAA") != 0) return {}; // EAN-8's left half: set A only
        text = digits;
    } else {
        // the first digit from the left half's sets
        static const char* kFirst[10] = {"AAAAAA", "AABABB", "AABBAB", "AABBBA", "ABAABB", "ABBAAB", "ABBBAA", "ABABAB", "ABABBA", "ABBABA"};
        int first = -1;
        for (int d = 0; d < 10; ++d)
            if (sets.compare(0, 6, kFirst[d]) == 0) first = d;
        if (first < 0) return {};
        text = std::string(1, char('0' + first)) + digits;
        if (format == CR_UPCA) {
            if (first != 0) return {};
            text = digits;
        }
    }
    return eanCheck(text.substr(0, text.size() - 1)) == text.back() ? text : std::string();
}

std::string eanGtin(int format, const std::string& text)
{
    if (!std::all_of(text.begin(), text.end(), [](char ch) { return ch >= '0' && ch <= '9'; })) return text;
    if (format == CR_UPCE && text.size() == 8) return "0" + upceToUpca(text);
    if ((format == CR_UPCA || format == CR_UPCE) && text.size() == 12) return "0" + text;
    return text;
}

std::string eanAddonReferenceDecode(const std::vector<float>& e, int digits)
{
    // guard 1011 (bar, space, two-module bar), then the digits in sets A / B (4 elements from a
    // space), a 01 separator (space, bar) between two: 20 modules for EAN-2, 47 for EAN-5
    if ((digits != 2 && digits != 5) || e.size() != size_t(6 * digits + 2)) return {};
    const float m = (e.back() - e.front()) / (digits == 2 ? 20.f : 47.f);
    if (m <= 0) return {};
    const float w0 = (e[1] - e[0]) / m, w1 = (e[2] - e[1]) / m, w2 = (e[3] - e[2]) / m;
    if (w0 < 0.25f || w0 > 2.f || w1 < 0.25f || w1 > 2.f || w2 < 1.f || w2 > 3.f) return {};
    if ((e[2] - e[0]) / m < 1.5f || (e[2] - e[0]) / m > 2.5f || (e[3] - e[1]) / m < 2.5f || (e[3] - e[1]) / m > 3.5f) return {};
    // print gain on each bar, in modules: the guard's and separators' one-module bars against their spaces
    float barSum = w0, spaceSum = w1;
    int nb = 1, ns = 1;
    for (int d = 0; d + 1 < digits; ++d) {
        const size_t k = size_t(7 + 6 * d);
        if (!eanGuard(e, k, 2, m)) return {};
        spaceSum += (e[k + 1] - e[k]) / m; ++ns;
        barSum += (e[k + 2] - e[k + 1]) / m; ++nb;
    }
    const float gain = 0.5f * (barSum / nb - spaceSum / ns);
    std::string text, sets;
    for (int d = 0; d < digits; ++d) {
        char set = 0;
        const int v = eanChar(e, size_t(3 + 6 * d), false, gain, &set);
        if (v < 0) return {};
        text += static_cast<char>('0' + v);
        sets += set;
    }
    if (digits == 2) {
        static const char* kPar2[4] = {"AA", "AB", "BA", "BB"};
        return sets == kPar2[std::stoi(text) % 4] ? text : std::string();
    }
    static const char* kPar5[10] = {"BBAAA", "BABAA", "BAABA", "BAAAB", "ABBAA", "AABBA", "AAABB", "ABABA", "ABAAB", "AABAB"};
    int sum = 0;
    for (int d = 0; d < 5; ++d) sum += (text[size_t(d)] - '0') * (d % 2 == 0 ? 3 : 9);
    return sets == kPar5[sum % 10] ? text : std::string();
}

float decodabilityDataBar(const std::vector<float>& edges, DataBarKind kind, bool mirrored)
{
    // edges from the left guard's bar (its light module is outside the first edge) to the right
    // guard's bar: guard bar, the characters and finders, guard space and bar. Each data character
    // and finder decoded on its own from its edge-to-similar-edge distances (ISO/IEC 24724), the
    // guards not (their elements are fixed single modules, no character)
    std::vector<float> e = edges;
    if (mirrored)
        for (size_t i = 0; i < e.size(); ++i) e[i] = edges.front() + edges.back() - edges[edges.size() - 1 - i];
    const size_t n = e.size();
    switch (kind) {
    case DataBarKind::StackedTop: {
        // 1 + 8 (16 modules) + 5 (finder, 15) + 8 (15) + 1 elements: the left half of an Omnidirectional
        if (n != 24) return -1.f;
        float v = dataBarChar(e, 1, 8, 16, 1.f);
        v = dataBarFinder(e, 9, {{1, 8, 16}, {14, 8, 15}}, v);
        return dataBarChar(e, 14, 8, 15, v);
    }
    case DataBarKind::StackedBottom: {
        // 2 + 8 (15) + 5 (finder, 15) + 8 (16) + 2 elements: the right half, behind a guard bar and space
        if (n != 26) return -1.f;
        float v = dataBarChar(e, 2, 8, 15, 1.f);
        v = dataBarFinder(e, 10, {{2, 8, 15}, {15, 8, 16}}, v);
        return dataBarChar(e, 15, 8, 16, v);
    }
    case DataBarKind::Omni: {
        // 1 + 8 (16 modules) + 5 (finder, 15) + 8 (15) + 8 (15) + 5 (finder, 15) + 8 (16) + 2 elements
        if (n != 46) return -1.f;
        float v = dataBarChar(e, 1, 8, 16, 1.f);
        v = dataBarFinder(e, 9, {{1, 8, 16}, {14, 8, 15}}, v);
        v = dataBarChar(e, 14, 8, 15, v);
        v = dataBarChar(e, 22, 8, 15, v);
        v = dataBarFinder(e, 30, {{22, 8, 15}, {35, 8, 16}}, v);
        return dataBarChar(e, 35, 8, 16, v);
    }
    case DataBarKind::Limited: {
        // 1 + 14 (26 modules) + 14 (check character, 18) + 14 (26) + 2 elements
        if (n != 46) return -1.f;
        float v = dataBarChar(e, 1, 14, 26, 1.f);
        v = dataBarChar(e, 15, 14, 18, v);
        return dataBarChar(e, 29, 14, 26, v);
    }
    case DataBarKind::Expanded: {
        // 1 + pairs of (character 17 modules, finder 15, character 17), 8 + 5 + 8 elements, the last
        // pair possibly without its second character, + 2
        if (n < 1 + 13 + 2 + 1) return -1.f;
        const size_t inner = n - 1 - 3; // elements between the guards
        if (inner % 21 != 0 && inner % 21 != 13) return -1.f;
        float v = 1.f;
        for (size_t k = 1; k < 1 + inner; k += 21) {
            v = dataBarChar(e, k, 8, 17, v);
            if (k + 13 < 1 + inner) {
                v = dataBarFinder(e, k + 8, {{k, 8, 17}, {k + 13, 8, 17}}, v);
                v = dataBarChar(e, k + 13, 8, 17, v);
            } else {
                v = dataBarFinder(e, k + 8, {{k, 8, 17}}, v);
            }
        }
        return v;
    }
    }
    return -1.f;
}

float moduleFromEdges(const std::vector<float>& all)
{
    // the narrowest elements are one module (n-width codes) or the narrow width (two-width). Print
    // gain widens bars and narrows spaces by the same amount, so X is the mean of the narrow bars'
    // and the narrow spaces' medians; only the symbol's own edges count (a run of edges with no gap
    // over 8 narrow widths, around the middle: not the label's or the carton's edge)
    std::vector<float> w;
    for (size_t i = 1; i < all.size(); ++i) if (all[i] > all[i - 1]) w.push_back(all[i] - all[i - 1]);
    if (w.size() < 6) return 0.f;
    std::sort(w.begin(), w.end());
    const float w0 = w[w.size() * 15 / 100];
    size_t mid = all.size() / 2, lo = mid, hi = mid;
    while (lo > 0 && all[lo] - all[lo - 1] <= 8.f * w0) --lo;
    while (hi + 1 < all.size() && all[hi + 1] - all[hi] <= 8.f * w0) ++hi;
    if (hi - lo < 6) return 0.f;
    // from the symbol's first edge (a bar's leading edge): even elements are bars, odd ones spaces
    std::vector<float> bars, spaces;
    for (size_t i = lo; i < hi; ++i) ((i - lo) % 2 == 0 ? bars : spaces).push_back(all[i + 1] - all[i]);
    const auto narrowMedian = [](std::vector<float> v) {
        std::sort(v.begin(), v.end());
        const float p15 = v[v.size() * 15 / 100];
        std::vector<float> nar;
        for (float x : v) if (x <= 1.4f * p15) nar.push_back(x);
        return nar[nar.size() / 2];
    };
    if (bars.size() < 3 || spaces.size() < 3) return 0.f;
    return 0.5f * (narrowMedian(bars) + narrowMedian(spaces));
}

} // namespace cr
