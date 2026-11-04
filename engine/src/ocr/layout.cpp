// layout.cpp - the text-line layout prior (tool study 7.4.6, INDUSTRY-METHODS rank 15).
//
// A layout gives the block's lines top to bottom, each as its groups of characters: "2 2 2-4" is one
// line of three groups (2, 2 and 2 to 4 characters, apart by word gaps), "6 | 4 2" two lines. The line
// finder uses it to join the pieces of one line that a wide gap split (findLines); each line is then read
// with as many characters as its layout allows and must fit it. A line that does not fit, or a layout
// line with no line found, fails with the reason (vs_ocr_layout_reason).

#include "ocr.hpp"

#include <algorithm>
#include <cctype>
#include <string>

namespace ocr {

namespace {

constexpr int kMaxGroup = 64;     // most characters one group may take (a format field's limit)
constexpr size_t kMaxLines = 32;  // most lines one layout may list (max_lines' limit)
constexpr size_t kMaxGroups = 16; // most groups on one line

std::string range(const std::pair<int, int>& g)
{
    return g.first == g.second ? std::to_string(g.first) : std::to_string(g.first) + "-" + std::to_string(g.second);
}

} // namespace

int LayoutLine::minChars() const
{
    int n = 0;
    for (const auto& g : groups) n += g.first;
    return n;
}

int LayoutLine::maxChars() const
{
    int n = 0;
    for (const auto& g : groups) n += g.second;
    return n;
}

std::string LayoutLine::text() const
{
    std::string s;
    for (const auto& g : groups) s += (s.empty() ? "" : " ") + range(g);
    return s;
}

bool parseLayout(const std::string& src, Layout& out, std::string& err)
{
    out = Layout();
    LayoutLine cur;
    size_t i = 0;
    auto endLine = [&]() {
        if (cur.groups.empty()) { err = "layout: a line with no groups (write each line's groups, e.g. \"2 2 2-4\")"; return false; }
        if (out.lines.size() >= kMaxLines) { err = "layout: more than 32 lines"; return false; }
        out.lines.push_back(std::move(cur));
        cur = LayoutLine();
        return true;
    };
    auto number = [&](int& v) {
        if (i >= src.size() || !std::isdigit(static_cast<unsigned char>(src[i]))) return false;
        v = 0;
        while (i < src.size() && std::isdigit(static_cast<unsigned char>(src[i]))) {
            v = std::min(10 * v + (src[i] - '0'), 1000);
            ++i;
        }
        return true;
    };
    while (i < src.size()) {
        const char c = src[i];
        if (c == ' ' || c == '\t') { ++i; continue; }
        if (c == '|' || c == '\n') {
            ++i;
            if (!endLine()) return false;
            continue;
        }
        int a = 0, b = 0;
        if (!number(a)) {
            err = std::string("layout: '") + c + "' at " + std::to_string(i + 1) + " (only group sizes such as 2 or 2-4, lines apart by |)";
            return false;
        }
        b = a;
        if (i < src.size() && src[i] == '-') {
            ++i;
            if (!number(b)) { err = "layout: a range needs its upper count (2-4)"; return false; }
        }
        if (a < 1 || b < a || b > kMaxGroup) {
            err = "layout: group " + std::to_string(a) + (b != a ? "-" + std::to_string(b) : "") + " (1 to 64 characters, lower count first)";
            return false;
        }
        if (cur.groups.size() >= kMaxGroups) { err = "layout: more than 16 groups on one line"; return false; }
        cur.groups.emplace_back(a, b);
    }
    if (!endLine()) return false;
    for (const auto& l : out.lines) out.source += (out.source.empty() ? "" : " | ") + l.text();
    return true;
}

std::string layoutOf(const std::vector<std::u32string>& lines)
{
    std::string s;
    for (const auto& text : lines) {
        std::string line;
        int run = 0;
        for (size_t k = 0; k <= text.size(); ++k) {
            if (k < text.size() && text[k] != U' ' && text[k] != U'\t') { ++run; continue; }
            if (run > 0) line += (line.empty() ? "" : " ") + std::to_string(run);   // over 64: refused when set
            run = 0;
        }
        if (line.empty()) continue;
        s += (s.empty() ? "" : " | ") + line;
    }
    return s;
}

std::string layoutMismatch(const std::u32string& text, const LayoutLine& l)
{
    std::vector<int> got;
    int run = 0;
    for (size_t k = 0; k <= text.size(); ++k) {
        if (k < text.size() && text[k] != U' ' && text[k] != U'\t') { ++run; continue; }
        if (run > 0) got.push_back(run);
        run = 0;
    }
    int n = 0;
    for (int g : got) n += g;
    std::string read;
    for (int g : got) read += (read.empty() ? "" : " ") + std::to_string(g);
    if (n < l.minChars() || n > l.maxChars())
        return std::to_string(n) + " characters read (" + (read.empty() ? "none" : read) + "), the layout " + l.text() +
               " has " + (l.minChars() == l.maxChars() ? std::to_string(l.minChars()) : std::to_string(l.minChars()) + " to " + std::to_string(l.maxChars()));
    // every word gap seen: each group must fit its own size. Fewer gaps seen than the layout's: a gap
    // the reader did not mark as a space proves nothing, the count above is the check. More gaps than
    // the layout has: a gap where the print has none (or a character split in two)
    if (got.size() > l.groups.size())
        return std::to_string(got.size()) + " groups read (" + read + "), the layout " + l.text() + " has " + std::to_string(l.groups.size());
    if (got.size() == l.groups.size())
        for (size_t g = 0; g < got.size(); ++g)
            if (got[g] < l.groups[g].first || got[g] > l.groups[g].second)
                return "group " + std::to_string(g + 1) + " has " + std::to_string(got[g]) + " characters (" + read + "), the layout " + l.text() +
                       " wants " + range(l.groups[g]);
    return "";
}

std::string layoutFormat(const LayoutLine& l)
{
    std::string f;
    for (const auto& g : l.groups) f += (f.empty() ? "" : " ") + std::string("{?:") + range(g) + "}";
    return f;
}

} // namespace ocr
