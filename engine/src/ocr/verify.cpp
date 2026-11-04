// verify.cpp - check a line against its expected text (OCV).
//
// The expected text is not compared with the free read. Instead the recogniser's per-column
// probabilities are force-aligned to the expected characters (CTC Viterbi): each expected character
// gets the columns where it is most likely printed, and its score is its best probability there. A
// well-printed character scores near 100 even when the free read is unsure (broken CIJ dots, faint
// laser), and a missing or wrong character scores near 0 because no column supports it.
//
// Each character also gets a verification margin (tool spec 7.3.3): how much more likely the expected
// character is than its best rival there, in nats (natural log of the ratio). It is reported, not judged:
// the pass rule is unchanged. A good print far from failing has margins of several nats; a margin near 0
// means another character (or nothing) explains the image almost as well.

#include "ocr.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>
#include <regex>
#include <set>

namespace ocr {
namespace {

constexpr float kNegInf = -std::numeric_limits<float>::infinity();
constexpr size_t kMaxCandidates = 400;

std::string formatTime(const std::string& fmt, std::time_t t)
{
    std::tm tm{};
#ifdef _WIN32
    localtime_s(&tm, &t);
#else
    localtime_r(&t, &tm);
#endif
    static const char* kMon[] = {"JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"};
    auto two = [](int v) { char b[8]; std::snprintf(b, sizeof b, "%02d", v); return std::string(b); };
    std::string out;
    for (size_t i = 0; i < fmt.size();) {
        auto at = [&](const char* tok) { return fmt.compare(i, std::strlen(tok), tok) == 0; };
        if (at("YYYY")) { out += std::to_string(tm.tm_year + 1900); i += 4; }
        else if (at("YY")) { out += two(tm.tm_year % 100); i += 2; }
        else if (at("MMM")) { out += kMon[tm.tm_mon]; i += 3; }
        else if (at("MM")) { out += two(tm.tm_mon + 1); i += 2; }
        else if (at("DD")) { out += two(tm.tm_mday); i += 2; }
        else if (at("HH")) { out += two(tm.tm_hour); i += 2; }
        else if (at("mm")) { out += two(tm.tm_min); i += 2; }
        else if (at("ss")) { out += two(tm.tm_sec); i += 2; }
        else { out.push_back(fmt[i]); ++i; }
    }
    return out;
}

// One {FIELD} -> its alternatives.
bool expandField(const std::string& body, std::time_t clock, std::vector<std::u32string>& alts, std::string& err)
{
    const size_t colon = body.find(':');
    const std::string name = body.substr(0, colon);
    const std::string spec = colon == std::string::npos ? "" : body.substr(colon + 1);
    static const std::regex re(R"(^(.*?)([+-]\d+d)?(?:(?:\+-|\xC2\xB1)(\d+))?$)");
    std::smatch m;
    if (!std::regex_match(spec, m, re)) { err = "bad field {" + body + "}"; return false; }
    const std::string fmt = m[1].str();
    // an offset beyond 100 years, a tolerance beyond a year (dates) or a day (times), or a clock beyond
    // year 30000 is refused: each tolerance step is one alternative, and the sums must not overflow
    constexpr long kMaxOffsetDays = 36525, kMaxTolDays = 366, kMaxTolMinutes = 1440;
    constexpr std::time_t kMaxClock = std::time_t(1) << 40;
    auto number = [](const std::string& s, long limit, long& v) {
        const size_t digits = s.find_first_of("0123456789");
        if (digits == std::string::npos || s.size() - digits > 9) return false; // up to 9 digits fit any long
        v = std::stol(s);
        return v >= -limit && v <= limit;
    };
    long offsetDays = 0, tol = 0;
    const long tolLimit = name == "TIME" ? kMaxTolMinutes : kMaxTolDays;
    if ((m[2].matched && !number(m[2].str().substr(0, size_t(m[2].length() - 1)), kMaxOffsetDays, offsetDays)) ||
        (m[3].matched && !number(m[3].str(), tolLimit, tol))) {
        err = "field {" + body + "}: offset up to " + std::to_string(kMaxOffsetDays) + " days, tolerance up to " +
              std::to_string(tolLimit) + (name == "TIME" ? " minutes" : " days");
        return false;
    }
    if (clock < -kMaxClock || clock > kMaxClock) { err = "the reference clock is out of range"; return false; }
    if (fmt.empty()) { err = "field {" + body + "} has no format"; return false; }
    std::set<std::u32string> seen;
    if (name == "TIME") {
        for (long d = -tol; d <= tol; ++d) seen.insert(fromUtf8(formatTime(fmt, clock + std::time_t(d * 60))));
    } else if (name == "DATE") {
        for (long d = -tol; d <= tol; ++d)
            seen.insert(fromUtf8(formatTime(fmt, clock + std::time_t((offsetDays + d) * 86400))));
    } else {
        err = "unknown field {" + name + "}: fill fixed fields before checking";
        return false;
    }
    alts.assign(seen.begin(), seen.end());
    return true;
}

struct Lev { int op; int ri; };  // op 0 match, 1 substitution, 2 deletion (expected char not read)

// For each expected char: how it lines up with the read text (wildcard 0 matches anything).
std::vector<Lev> alignRead(const std::u32string& exp, const std::u32string& rd)
{
    const size_t n = exp.size(), m = rd.size();
    std::vector<std::vector<int>> d(n + 1, std::vector<int>(m + 1, 0));
    for (size_t i = 0; i <= n; ++i) d[i][0] = int(i);
    for (size_t j = 0; j <= m; ++j) d[0][j] = int(j);
    for (size_t i = 1; i <= n; ++i)
        for (size_t j = 1; j <= m; ++j) {
            const int sub = (exp[i - 1] == 0 || exp[i - 1] == rd[j - 1]) ? 0 : 1;
            d[i][j] = std::min({d[i - 1][j - 1] + sub, d[i - 1][j] + 1, d[i][j - 1] + 1});
        }
    std::vector<Lev> out(n, Lev{2, -1});
    size_t i = n, j = m;
    while (i > 0 && j > 0) {
        const int sub = (exp[i - 1] == 0 || exp[i - 1] == rd[j - 1]) ? 0 : 1;
        if (d[i][j] == d[i - 1][j - 1] + sub) { out[i - 1] = {sub, int(j - 1)}; --i; --j; }
        else if (d[i][j] == d[i - 1][j] + 1) { out[i - 1] = {2, -1}; --i; }
        else --j;
    }
    return out;
}

struct Aligned {
    float logp = kNegInf;
    std::vector<int> t0, t1;   // per label: first and last column, -1 = none
};

// CTC Viterbi alignment of labels (class index, -2 = wildcard, -1 = not in the charset).
Aligned forceAlign(const cv::Mat& lp, const std::vector<float>& anyChar, const std::vector<int>& lab)
{
    Aligned a;
    const int T = lp.rows, L = int(lab.size()), S = 2 * L + 1;
    a.t0.assign(size_t(L), -1);
    a.t1.assign(size_t(L), -1);
    if (L == 0) { a.logp = 0; return a; }
    auto emit = [&](int t, int s) -> float {
        if (s % 2 == 0) return lp.at<float>(t, 0);
        const int c = lab[size_t(s / 2)];
        if (c == -2) return anyChar[size_t(t)];
        if (c < 0) return -13.8f;
        return lp.at<float>(t, c);
    };
    auto canSkip = [&](int s) {            // s odd: may come straight from s - 2 (no blank between)
        if (s < 3) return false;
        const int c = lab[size_t(s / 2)], p = lab[size_t(s / 2 - 1)];
        return !(c >= 1 && c == p);
    };
    std::vector<float> prev(static_cast<size_t>(S), kNegInf), cur(static_cast<size_t>(S));
    std::vector<std::vector<int8_t>> back(size_t(T), std::vector<int8_t>(size_t(S), 0));
    prev[0] = emit(0, 0);
    if (S > 1) prev[1] = emit(0, 1);
    for (int t = 1; t < T; ++t) {
        for (int s = 0; s < S; ++s) {
            float best = prev[size_t(s)];
            int8_t from = 0;
            if (s >= 1 && prev[size_t(s - 1)] > best) { best = prev[size_t(s - 1)]; from = 1; }
            if (s % 2 == 1 && canSkip(s) && prev[size_t(s - 2)] > best) { best = prev[size_t(s - 2)]; from = 2; }
            cur[size_t(s)] = best == kNegInf ? kNegInf : best + emit(t, s);
            back[size_t(t)][size_t(s)] = from;
        }
        std::swap(prev, cur);
    }
    int s = S - 1;
    if (S > 1 && prev[size_t(S - 2)] > prev[size_t(S - 1)]) s = S - 2;
    a.logp = prev[size_t(s)];
    if (a.logp == kNegInf) return a;
    for (int t = T - 1; t >= 0; --t) {
        if (s % 2 == 1) {
            const size_t k = size_t(s / 2);
            if (a.t1[k] < 0) a.t1[k] = t;
            a.t0[k] = t;
        }
        if (t > 0) s -= back[size_t(t)][size_t(s)];
    }
    return a;
}

// log P(expected) - log P(best rival) for label c over columns t0..t1: the expected character's best
// column against any other character within one column of its own (a look-alike read a little to the
// side) and against the blank at that best column (nothing printed there). lo..hi are the columns left
// between its neighbours: a neighbour aligned right next to it (no blank between) is not its rival.
// space (index `space`, -1 = none) is no rival either: spaces are never checked, and the recogniser
// reads one in the gap beside a letter at every word break.
float marginAt(const cv::Mat& lp, int c, int t0, int t1, int lo, int hi, int space)
{
    int tp = t0;
    for (int t = t0; t <= t1; ++t)
        if (lp.at<float>(t, c) > lp.at<float>(tp, c)) tp = t;
    float rival = lp.at<float>(tp, 0);
    for (int t = std::max({0, lo, t0 - 1}); t <= std::min({lp.rows - 1, hi, t1 + 1}); ++t) {
        const float* r = lp.ptr<float>(t);
        for (int k = 1; k < lp.cols; ++k)
            if (k != c && k != space) rival = std::max(rival, r[k]);
    }
    return std::clamp(lp.at<float>(tp, c) - rival, -kMaxMargin, kMaxMargin);
}

// the last column of the aligned character before label k (dir -1; -1 when none), or the first column
// of the one after it (dir +1; INT_MAX when none)
int neighbourEnd(const Aligned& a, size_t k, int dir)
{
    if (dir < 0) {
        for (size_t j = k; j-- > 0;) if (a.t1[j] >= 0) return a.t1[j];
        return -1;
    }
    for (size_t j = k + 1; j < a.t0.size(); ++j) if (a.t0[j] >= 0) return a.t0[j];
    return std::numeric_limits<int>::max();
}

} // namespace

bool expandPattern(const std::string& utf8, std::time_t clock, Pattern& out, std::string& err)
{
    out.source = utf8;
    out.candidates.clear();
    std::vector<std::vector<std::u32string>> parts;   // sequence of alternatives
    std::string lit;
    auto flush = [&]() {
        if (lit.empty()) return;
        std::u32string u = fromUtf8(lit);
        u.erase(std::remove(u.begin(), u.end(), U' '), u.end());
        if (!u.empty()) parts.push_back({u});
        lit.clear();
    };
    for (size_t i = 0; i < utf8.size(); ++i) {
        const char c = utf8[i];
        if (c == '\\' && i + 1 < utf8.size()) { lit.push_back(utf8[++i]); continue; }
        if (c == '?') { flush(); parts.push_back({std::u32string(1, U'\0')}); continue; }
        if (c == '{') {
            const size_t end = utf8.find('}', i);
            if (end == std::string::npos) { err = "missing } in pattern"; return false; }
            flush();
            std::vector<std::u32string> alts;
            if (!expandField(utf8.substr(i + 1, end - i - 1), clock, alts, err)) return false;
            for (auto& a : alts) a.erase(std::remove(a.begin(), a.end(), U' '), a.end());
            parts.push_back(alts);
            i = end;
            continue;
        }
        lit.push_back(c);
    }
    flush();
    out.candidates.assign(1, U"");
    for (const auto& alts : parts) {
        std::vector<std::u32string> next;
        for (const auto& base : out.candidates)
            for (const auto& a : alts) {
                next.push_back(base + a);
                if (next.size() >= kMaxCandidates) break;
            }
        out.candidates.swap(next);
    }
    return true;
}

CheckResult checkLine(const cv::Mat& logp, const Charset& cs, const Pattern& pat,
                      const std::vector<ReadChar>& read, float minQuality)
{
    CheckResult best;
    best.quality = -1;
    best.margin = -kMaxMargin;
    if (logp.empty() || pat.candidates.empty()) return best;
    best.margin = kMaxMargin;
    std::vector<float> anyChar(size_t(logp.rows));
    for (int t = 0; t < logp.rows; ++t) {
        const float* r = logp.ptr<float>(t);
        anyChar[size_t(t)] = *std::max_element(r + 1, r + logp.cols);
    }
    std::u32string rd;
    std::vector<int> rdIdx;
    for (size_t i = 0; i < read.size(); ++i)
        if (read[i].c != U' ') { rd.push_back(read[i].c); rdIdx.push_back(int(i)); }

    float bestLogp = kNegInf;
    Aligned bestA;
    const std::u32string* bestCand = nullptr;
    std::vector<int> bestLab;
    for (const auto& cand : pat.candidates) {
        std::vector<int> lab(cand.size());
        for (size_t i = 0; i < cand.size(); ++i) lab[i] = cand[i] == 0 ? -2 : cs.find(cand[i]);
        Aligned a = forceAlign(logp, anyChar, lab);
        if (!bestCand || a.logp > bestLogp) { bestLogp = a.logp; bestA = a; bestCand = &cand; bestLab = lab; }
    }
    best.expected = *bestCand;
    const auto lev = alignRead(best.expected, rd);
    best.chars.resize(best.expected.size());
    float q = 100.f;
    for (size_t k = 0; k < best.expected.size(); ++k) {
        CheckChar& c = best.chars[k];
        c.expected = best.expected[k];
        c.t0 = bestA.t0.empty() ? -1 : bestA.t0[k];
        c.t1 = bestA.t1.empty() ? -1 : bestA.t1[k];
        float p = 0;
        if (c.t0 >= 0 && bestLab[k] != -1)
            for (int t = c.t0; t <= c.t1; ++t)
                p = std::max(p, std::exp(bestLab[k] == -2 ? anyChar[size_t(t)] : logp.at<float>(t, bestLab[k])));
        c.score = 100.f * p;
        c.margin = bestLab[k] == -2 ? kMaxMargin
                 : c.t0 >= 0 && bestLab[k] > 0 ? marginAt(logp, bestLab[k], c.t0, c.t1, neighbourEnd(bestA, k, -1) + 1,
                                                          neighbourEnd(bestA, k, +1) - 1, cs.find(U' '))
                 : -kMaxMargin;
        best.margin = std::min(best.margin, c.margin);
        const Lev& l = lev[k];
        c.found = l.ri >= 0 ? rd[size_t(l.ri)] : 0;
        if (c.score >= minQuality) { c.status = 0; if (!c.found) c.found = c.expected; }
        else if (l.op == 0) c.status = 1;      // the right character, printed poorly
        else if (l.op == 1) c.status = 2;      // another character
        else c.status = 3;                     // nothing read here
        q = std::min(q, c.score);
    }
    if (best.expected.empty()) best.margin = -kMaxMargin;
    best.quality = best.expected.empty() ? 0.f : q;
    best.pass = !best.expected.empty() && bestLogp != kNegInf && q >= minQuality;
    return best;
}

} // namespace ocr
