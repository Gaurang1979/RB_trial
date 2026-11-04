// format.cpp - reading a line under its format (Read text mode, tool specification 7.4).
//
// A format ("LOT {A:1}{D:5}", "EXP {|JAN|FEB|...}{D:2}") is compiled into an automaton of character
// positions, each allowing a set of characters (a digit, a letter, one fixed character ...). The
// recogniser's per-column probabilities are then decoded along the best path the automaton allows (CTC
// Viterbi over (position, character) nodes), so an O can only be read where a letter may stand.
//
// The constraint chooses; it never vouches. A character's score is its own probability in its columns,
// not renormalised over the allowed set: a printed O where a digit belongs reads as a 0 with the 0's low
// probability, and the line fails. A mark the format has no room for (an extra character) fails the line;
// so do two equal characters with no gap seen between them (one wide 0 must not read as 00).

#include "ocr.hpp"

#include "vs_ocr.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace ocr {
namespace {

constexpr float kNegInf = -std::numeric_limits<float>::infinity();
constexpr int kMaxCount = 64;          // most characters one field may take
constexpr size_t kMaxPositions = 1024;
constexpr double kMaxCells = 16e6;     // columns x nodes kept for the path back

enum Kind { kLiteral = 0, kDigit = 1, kLetter = 2, kLetterOrDigit = 3, kAny = 4 };

bool isLetter(char32_t c)
{
    if ((c >= U'A' && c <= U'Z') || (c >= U'a' && c <= U'z')) return true;
    if (c < 0xC0 || c == 0xD7 || c == 0xF7 || c >= 0x2000) return false;   // ASCII marks, x and / signs, symbols
    return !(c >= 0x966 && c <= 0x96F);                                       // Devanagari digits
}

bool isDigit(char32_t c) { return c >= U'0' && c <= U'9'; }

// the same letter in the other case (Latin), or 0
char32_t otherCase(char32_t c)
{
    if (c >= U'A' && c <= U'Z') return c + 32;
    if (c >= U'a' && c <= U'z') return c - 32;
    if (c >= 0xC0 && c <= 0xDE && c != 0xD7) return c + 32;
    if (c >= 0xE0 && c <= 0xFE && c != 0xF7) return c - 32;
    if (c >= 0x100 && c <= 0x17F && c != 0x130 && c != 0x131 && c != 0x138 && c != 0x149 && c != 0x17F)
        return (c >= 0x139 && c <= 0x148) || c >= 0x179 ? ((c & 1) ? c + 1 : c - 1) : (c ^ 1);
    return 0;
}

// one case for both (O and o are the same character to a check that ignores case)
char32_t fold(char32_t c)
{
    const char32_t o = otherCase(c);
    return o ? std::min(c, o) : c;
}

// classes of the charset a position allows; {-1} when none (that character then scores 0)
std::vector<int> classesOf(const FormatPos& p, const Charset& cs)
{
    std::vector<int> out;
    if (p.kind == kLiteral) {
        for (char32_t c : {p.ch, otherCase(p.ch)})
            if (c) { const int i = cs.find(c); if (i > 0 && std::find(out.begin(), out.end(), i) == out.end()) out.push_back(i); }
    } else {
        for (size_t i = 1; i < cs.chars.size(); ++i) {
            const char32_t c = cs.chars[i];
            const bool ok = p.kind == kDigit ? isDigit(c)
                          : p.kind == kLetter ? isLetter(c)
                          : p.kind == kLetterOrDigit ? isDigit(c) || isLetter(c)
                          : c != U' ';
            if (ok) out.push_back(int(i));
        }
    }
    if (out.empty()) out.push_back(-1);
    return out;
}

} // namespace

bool compileFormat(const std::string& utf8, Format& out, std::string& err)
{
    out = Format{};
    out.source = utf8;
    const std::u32string s = fromUtf8(utf8);
    std::vector<int> prevLast;
    bool started = false;
    auto newPos = [&](int kind, char32_t ch) {
        out.pos.push_back(FormatPos{kind, ch});
        out.follow.emplace_back();
        return int(out.pos.size() - 1);
    };
    auto link = [&](const std::vector<int>& from, const std::vector<int>& to) {
        for (int a : from)
            for (int b : to) out.follow[size_t(a)].push_back(b);
    };
    auto attach = [&](const std::vector<int>& firsts, const std::vector<int>& lasts) {
        if (!started) { out.first = firsts; started = true; }
        else link(prevLast, firsts);
        prevLast = lasts;
    };
    auto literal = [&](char32_t c) { const int p = newPos(kLiteral, c); attach({p}, {p}); };

    for (size_t i = 0; i < s.size(); ++i) {
        const char32_t c = s[i];
        if (c == U'\\' && i + 1 < s.size()) { literal(s[++i]); continue; }
        if (c == U' ' || c == U'\t') continue;                      // spacing is never checked
        if (c != U'{') { literal(c); continue; }
        // a field: up to the first } not escaped
        std::u32string body;
        size_t j = i + 1;
        for (; j < s.size() && s[j] != U'}'; ++j) {
            if (s[j] == U'\\' && j + 1 < s.size()) body.push_back(s[j++]);
            body.push_back(s[j]);
        }
        if (j >= s.size()) { err = "missing } in format"; return false; }
        i = j;
        if (!body.empty() && body[0] == U'|') {                     // {|JAN|FEB|...}: one of these words
            std::vector<std::u32string> words(1);
            for (size_t k = 1; k < body.size(); ++k) {
                if (body[k] == U'\\' && k + 1 < body.size()) { words.back().push_back(body[++k]); continue; }
                if (body[k] == U'|') { words.emplace_back(); continue; }
                if (body[k] != U' ') words.back().push_back(body[k]);
            }
            std::vector<int> firsts, lasts;
            for (const auto& w : words) {
                if (w.empty()) { err = "an empty word in {" + toUtf8(body) + "}"; return false; }
                int prev = -1;
                for (char32_t wc : w) {
                    const int p = newPos(kLiteral, wc);
                    if (prev < 0) firsts.push_back(p);
                    else out.follow[size_t(prev)].push_back(p);
                    prev = p;
                }
                lasts.push_back(prev);
            }
            attach(firsts, lasts);
        } else {                                                    // {D:5} {A:1-3} {X} {?:2}
            const std::string b = toUtf8(body);
            const char k0 = b.empty() ? 0 : b[0];
            const int kind = k0 == 'D' ? kDigit : k0 == 'A' ? kLetter : k0 == 'X' ? kLetterOrDigit : k0 == '?' ? kAny : -1;
            int lo = 1, hi = -1;                                    // hi -1 = one or more
            bool ok = kind >= 0;
            if (ok && b.size() > 1) {
                ok = b[1] == ':' && b.size() > 2;
                size_t at = 2;
                auto num = [&](int& v) {
                    const size_t from = at;
                    v = 0;
                    while (at < b.size() && b[at] >= '0' && b[at] <= '9' && at - from < 3) v = v * 10 + (b[at++] - '0');
                    return at > from;
                };
                if (ok) ok = num(lo);
                hi = lo;
                if (ok && at < b.size()) ok = b[at++] == '-' && num(hi) && at == b.size();
                if (ok) ok = lo >= 1 && hi >= lo && hi <= kMaxCount;
            }
            if (!ok) { err = "{" + b + "} is not a format field: use {D:5}, {A:1-3}, {X}, {?:2} or {|WORD|WORD}"; return false; }
            std::vector<int> chain;
            for (int n = 0; n < lo; ++n) {
                const int p = newPos(kind, 0);
                if (!chain.empty()) out.follow[size_t(chain.back())].push_back(p);
                chain.push_back(p);
            }
            std::vector<int> lasts{chain.back()};
            if (hi < 0) out.follow[size_t(chain.back())].push_back(chain.back());   // repeats
            for (int n = lo; n < hi; ++n) {
                const int p = newPos(kind, 0);
                out.follow[size_t(lasts.back())].push_back(p);
                lasts.push_back(p);
            }
            attach({chain.front()}, lasts);
        }
        if (out.pos.size() > kMaxPositions) { err = "format too long"; return false; }
    }
    if (out.pos.empty()) { err = "format has no characters"; return false; }
    if (out.pos.size() > kMaxPositions) { err = "format too long"; return false; }
    out.last.assign(out.pos.size(), 0);
    for (int p : prevLast) out.last[size_t(p)] = 1;
    return true;
}

FormatRead readFormat(const cv::Mat& logp, const Charset& cs, const Format& f, const std::vector<ReadChar>& read,
                      float minQuality)
{
    FormatRead r;
    const int T = logp.rows, K = int(f.pos.size());
    if (T == 0 || K == 0) return r;
    const int space = cs.find(U' ');

    // nodes: 0 = blank before any character; 1 + k = blank after position k; then (k, class) emitting nodes
    std::vector<std::vector<int>> cls(static_cast<size_t>(K));
    std::vector<int> eoff(static_cast<size_t>(K));
    int N = 1 + K;
    for (int k = 0; k < K; ++k) {
        cls[size_t(k)] = classesOf(f.pos[size_t(k)], cs);
        eoff[size_t(k)] = N;
        N += int(cls[size_t(k)].size());
    }
    if (double(T) * N > kMaxCells) return r;                   // not read: the check fails
    std::vector<std::vector<int>> pred(static_cast<size_t>(K));
    for (int q = 0; q < K; ++q)
        for (int k : f.follow[size_t(q)]) pred[size_t(k)].push_back(q);
    std::vector<char> isFirst(static_cast<size_t>(K), 0);
    for (int k : f.first) isFirst[size_t(k)] = 1;

    std::vector<float> blankish(static_cast<size_t>(T));
    for (int t = 0; t < T; ++t) {
        const float b = logp.at<float>(t, 0);
        blankish[size_t(t)] = space > 0 ? std::max(b, logp.at<float>(t, space)) : b;
    }
    auto emit = [&](int t, int c) { return c < 0 ? -13.8f : logp.at<float>(t, c); };

    std::vector<float> prev(static_cast<size_t>(N), kNegInf), cur(static_cast<size_t>(N));
    std::vector<int32_t> back(size_t(T) * size_t(N), -1);
    prev[0] = blankish[0];
    for (int k = 0; k < K; ++k)
        if (isFirst[size_t(k)])
            for (size_t j = 0; j < cls[size_t(k)].size(); ++j) prev[size_t(eoff[size_t(k)]) + j] = emit(0, cls[size_t(k)][j]);

    // folded character of each emitting node (0 for a missing class)
    std::vector<char32_t> fold_(static_cast<size_t>(N), 0);
    for (int k = 0; k < K; ++k)
        for (size_t j = 0; j < cls[size_t(k)].size(); ++j)
            if (cls[size_t(k)][j] > 0) fold_[size_t(eoff[size_t(k)]) + j] = fold(cs.chars[size_t(cls[size_t(k)][j])]);
    // per position at t - 1: best emitting node, and best of another character (equal characters need a blank between)
    std::vector<int> best1(static_cast<size_t>(K)), best2(static_cast<size_t>(K));
    for (int t = 1; t < T; ++t) {
        int32_t* bk = &back[size_t(t) * size_t(N)];
        for (int k = 0; k < K; ++k) {
            const int n0 = eoff[size_t(k)], n1 = n0 + int(cls[size_t(k)].size());
            int a = n0, b = -1;
            for (int n = n0 + 1; n < n1; ++n) if (prev[size_t(n)] > prev[size_t(a)]) a = n;
            for (int n = n0; n < n1; ++n)
                if ((fold_[size_t(n)] != fold_[size_t(a)] || fold_[size_t(a)] == 0) && n != a && (b < 0 || prev[size_t(n)] > prev[size_t(b)])) b = n;
            best1[size_t(k)] = a;
            best2[size_t(k)] = b;
        }
        cur[0] = prev[0] + blankish[size_t(t)];
        bk[0] = 0;
        for (int k = 0; k < K; ++k) {
            const int B = 1 + k, e = best1[size_t(k)];
            const bool fromE = prev[size_t(e)] > prev[size_t(B)];
            cur[size_t(B)] = (fromE ? prev[size_t(e)] : prev[size_t(B)]) + blankish[size_t(t)];
            bk[B] = fromE ? e : B;
            for (size_t j = 0; j < cls[size_t(k)].size(); ++j) {
                const int n = eoff[size_t(k)] + int(j), c = cls[size_t(k)][j];
                float best = prev[size_t(n)];
                int from = n;
                auto take = [&](int m) { if (m >= 0 && prev[size_t(m)] > best) { best = prev[size_t(m)]; from = m; } };
                if (isFirst[size_t(k)]) take(0);
                for (int q : pred[size_t(k)]) {
                    take(1 + q);
                    // straight from the previous character only when it is another one, case ignored (O
                    // then o is one wide O read twice; a missing class -1 never equals): best2 is the best
                    // of another character than best1's
                    const int a = best1[size_t(q)];
                    take(c >= 0 && a >= 0 && fold_[size_t(a)] == fold_[size_t(n)] ? best2[size_t(q)] : a);
                }
                cur[size_t(n)] = best == kNegInf ? kNegInf : best + emit(t, c);
                bk[n] = from;
            }
        }
        std::swap(prev, cur);
    }
    int end = -1;
    for (int k = 0; k < K; ++k) {
        if (!f.last[size_t(k)]) continue;
        auto consider = [&](int n) { if (prev[size_t(n)] != kNegInf && (end < 0 || prev[size_t(n)] > prev[size_t(end)])) end = n; };
        consider(1 + k);
        for (size_t j = 0; j < cls[size_t(k)].size(); ++j) consider(eoff[size_t(k)] + int(j));
    }
    if (end < 0) return r;                                      // too few columns for the format
    r.fits = true;

    // the path back, then its characters
    std::vector<int> path(static_cast<size_t>(T));
    path[size_t(T - 1)] = end;
    for (int t = T - 1; t > 0; --t) path[size_t(t - 1)] = back[size_t(t) * size_t(N) + size_t(path[size_t(t)])];
    auto classOfNode = [&](int n, int& k) {
        k = int(std::upper_bound(eoff.begin(), eoff.end(), n) - eoff.begin()) - 1;
        return cls[size_t(k)][size_t(n - eoff[size_t(k)])];
    };
    struct Got { int c; int t0, t1; };
    std::vector<Got> got;
    for (int t = 0; t < T; ++t) {
        const int n = path[size_t(t)];
        if (n <= K) continue;                                   // a blank node
        if (t > 0 && path[size_t(t - 1)] == n) { got.back().t1 = t; continue; }
        int k = 0;
        got.push_back({classOfNode(n, k), t, t});
    }

    // each free-read character goes to the format character it overlaps most; none = an extra mark
    std::vector<int> owner(read.size(), -1);
    std::vector<int> owned(got.size(), 0);
    for (size_t i = 0; i < read.size(); ++i) {
        if (read[i].c == U' ') continue;
        int bestOv = 0;
        for (size_t g = 0; g < got.size(); ++g) {
            const int ov = std::min(read[i].t1, got[g].t1) - std::max(read[i].t0, got[g].t0) + 1;
            if (ov > bestOv) { bestOv = ov; owner[i] = int(g); }
        }
    }
    std::vector<int> foundOf(got.size(), -1);
    std::vector<size_t> extras;
    for (size_t i = 0; i < read.size(); ++i) {
        if (read[i].c == U' ') continue;
        const int g = owner[i];
        if (g < 0 || owned[size_t(g)]++ > 0) extras.push_back(i);   // a second character on one place is extra too
        else foundOf[size_t(g)] = int(i);
    }

    // never below 50: above it at most one character can hold a column, so an allowed character that
    // passes is also the recogniser's own first choice there
    minQuality = std::max(minQuality, 50.f);
    float q = 100.f;
    std::vector<CheckChar> chars;
    for (size_t g = 0; g < got.size(); ++g) {
        CheckChar cc;
        cc.t0 = got[g].t0;
        cc.t1 = got[g].t1;
        const int c = got[g].c;
        cc.expected = c > 0 ? cs.chars[size_t(c)] : U'�';
        float p = 0;
        if (c > 0)
            for (int t = cc.t0; t <= cc.t1; ++t) p = std::max(p, std::exp(logp.at<float>(t, c)));
        // equal neighbours need a gap the recogniser saw: else it may be one wide character read twice
        auto gapTo = [&](size_t h) {
            const int a = std::min(got[g].t1, got[h].t1) + 1, b = std::max(got[g].t0, got[h].t0) - 1;
            float gp = 0;
            for (int t = a; t <= b; ++t) gp = std::max(gp, std::exp(blankish[size_t(t)]));
            return gp;
        };
        auto same = [&](size_t h) { return c > 0 && got[h].c > 0 && fold(cs.chars[size_t(got[h].c)]) == fold(cs.chars[size_t(c)]); };
        if (g > 0 && same(g - 1)) p = std::min(p, gapTo(g - 1));
        if (g + 1 < got.size() && same(g + 1)) p = std::min(p, gapTo(g + 1));
        cc.score = 100.f * p;
        cc.found = foundOf[g] >= 0 ? read[size_t(foundOf[g])].c : 0;
        if (cc.score >= minQuality) cc.status = VS_CHAR_OK;
        else cc.status = cc.found == cc.expected ? VS_CHAR_UNSURE : cc.found ? VS_CHAR_WRONG : VS_CHAR_MISSING;
        q = std::min(q, cc.score);
        chars.push_back(cc);
    }
    for (size_t i : extras) {
        CheckChar cc;
        cc.expected = cc.found = read[i].c;
        cc.score = 100.f * read[i].conf;
        cc.status = VS_CHAR_EXTRA;
        cc.t0 = read[i].t0;
        cc.t1 = read[i].t1;
        chars.push_back(cc);
    }
    std::stable_sort(chars.begin(), chars.end(), [](const CheckChar& a, const CheckChar& b) { return a.t0 < b.t0; });

    // the text: the format's characters, with a space where the recogniser saw a word gap
    for (size_t g = 0; g < got.size(); ++g) {
        if (g > 0 && space > 0) {
            bool gap = false;
            for (int t = got[g - 1].t1 + 1; t < got[g].t0 && !gap; ++t) {
                const float* row = logp.ptr<float>(t);
                gap = int(std::max_element(row, row + logp.cols) - row) == space;
            }
            if (gap) r.text.push_back(U' ');
        }
        r.text.push_back(got[g].c > 0 ? cs.chars[size_t(got[g].c)] : U'�');
    }
    r.chars = std::move(chars);
    r.quality = got.empty() ? 0.f : q;
    r.pass = !got.empty() && extras.empty() && q >= minQuality;
    // the free read already fits: the same characters, case ignored
    std::u32string freeText, fmtText;
    for (const auto& rc : read) if (rc.c != U' ') freeText.push_back(fold(rc.c));
    for (const auto& g : got) fmtText.push_back(g.c > 0 ? fold(cs.chars[size_t(g.c)]) : U'\uFFFD');
    r.freeFits = !got.empty() && freeText == fmtText;
    return r;
}

} // namespace ocr
