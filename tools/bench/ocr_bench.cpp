// ocr_bench - read rate, check (OCV) rate and time of the OCR / OCV tool over a folder of images.
//
//   ocr_bench <folder> [truth.tsv] --model ocrnet.onnx --charset ocr_charset.txt
//             [--roi x,y,w,h] [--char-h px] [--print auto|solid|dot|inkjet] [--dot-size px] [--join 0.5-3] [--polarity auto|dark|light]
//             [--min-quality q] [--budget ms] [--runs n] [--mutate] [--clock-from-name]
//             [--expect "pattern|pattern|..."] [--dump dir] [--find-only] [--read-only] [--csv out.csv] [--limit n]
//             [--layout "2 2 2-4 | 6"] [--layout-truth] [--tight]
//
// truth.tsv: one image per row, tab separated: file, then the text of each line top to bottom.
//   Lines starting with # are comments. Spaces are not checked.
// --mutate         also check every image against its truth with one character changed: must fail
//                  (the summary gives the verification margin of passing prints and of the changed texts)
// --expect         check against these patterns instead of the truth (fields such as {TIME:HH:mm+-2})
// --clock-from-name  reference clock from file names like 20261023_154234728.bmp
// --dump dir       write the straightened line images (dir/<image>_L<n>.png) for training data
// --find-only      find and dump lines without a model (no reading)
// --read-only      read only, do not check against the truth: character accuracy, and the score of
//                  each wrong read (a wrong read at or above --min-quality would show as OK)
// --agree off|auto|shift  Read text safety (vs_ocr_set_agreement; default auto): a second reader must read the
//                  same text. --second-model m.onnx loads a second AI model (same charset) as that reader;
//                  --teach-font <folder>=<truth.tsv>=<n> teaches the classical font from the first n images
//                  there (every truth line), so the taught font is the second reader
// --layout         the block's text-line layout (vs_ocr_set_layout); --layout-truth: each image's own,
//                  from its truth lines (their groups apart by spaces). With --read-only, lines read under
//                  the layout that pass but differ from the truth are counted: wrong reads accepted
// --tight          dot print spaced tight (vs_ocr_set_tight_spacing)

#include "vs_ocr.h"
#include "../../engine/src/ocr/ocr.hpp"

#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <map>
#include <random>
#include <sstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace {

std::vector<std::string> split(const std::string& s, char sep)
{
    std::vector<std::string> out;
    std::string cur;
    for (char c : s) {
        if (c == sep) { out.push_back(cur); cur.clear(); }
        else if (c != '\r') cur.push_back(c);
    }
    out.push_back(cur);
    return out;
}

std::u32string noSpace(const std::string& s)
{
    std::u32string u = ocr::fromUtf8(s);
    u.erase(std::remove(u.begin(), u.end(), U' '), u.end());
    return u;
}

int editDistance(const std::u32string& a, const std::u32string& b)
{
    std::vector<int> d(b.size() + 1);
    for (size_t j = 0; j <= b.size(); ++j) d[j] = int(j);
    for (size_t i = 1; i <= a.size(); ++i) {
        int prev = d[0];
        d[0] = int(i);
        for (size_t j = 1; j <= b.size(); ++j) {
            const int cur = d[j];
            d[j] = std::min({d[j] + 1, d[j - 1] + 1, prev + (a[i - 1] == b[j - 1] ? 0 : 1)});
            prev = cur;
        }
    }
    return d[b.size()];
}

std::string escapePattern(const std::string& s)
{
    std::string o;
    for (char c : s) {
        if (c == '?' || c == '{' || c == '\\') o.push_back('\\');
        o.push_back(c);
    }
    return o;
}

std::time_t clockFromName(const std::string& name)
{
    int Y, M, D, h, m, s;
    if (std::sscanf(name.c_str(), "%4d%2d%2d_%2d%2d%2d", &Y, &M, &D, &h, &m, &s) != 6) return 0;
    std::tm tm{};
    tm.tm_year = Y - 1900; tm.tm_mon = M - 1; tm.tm_mday = D; tm.tm_hour = h; tm.tm_min = m; tm.tm_sec = s;
    tm.tm_isdst = -1;
    return std::mktime(&tm);
}

double pct(std::vector<double> v, double q)
{
    if (v.empty()) return 0;
    std::sort(v.begin(), v.end());
    return v[std::min(v.size() - 1, size_t(q * double(v.size() - 1) + 0.5))];
}

// the lowest verification margin over the checked lines of the last run (-99 when none)
double lowestMargin(const vs_ocr* t)
{
    double m = 99;
    bool any = false;
    for (int i = 0, n = vs_ocr_line_count(t); i < n; ++i) {
        vs_ocr_line L;
        vs_ocr_line_ext x{};
        x.struct_size = int32_t(sizeof x);
        if (vs_ocr_get_line(t, i, &L) != 0 || !L.checked || L.format >= 0 || vs_ocr_get_line_ext(t, i, &x) != 0) continue;
        m = std::min(m, double(x.margin));
        any = true;
    }
    return any ? m : -99;
}

} // namespace

int main(int argc, char** argv)
{
    if (argc < 2) {
        std::fprintf(stderr, "usage: ocr_bench <folder> [truth.tsv] --model m.onnx --charset c.txt [options]\n");
        return 2;
    }
    std::string folder = argv[1], truthPath, model, charset, dump, expectArg, csvPath, model2, teachSpec;
    int agree = VS_AGREE_AUTO;
    vs_ocr_params P;
    vs_ocr_default_params(&P);
    int runs = 1, limit = 0;
    bool mutate = false, clockName = false, findOnly = false, readOnly = false, layoutTruth = false, tight = false;
    std::string layoutArg;
    for (int i = 2; i < argc; ++i) {
        const std::string a = argv[i];
        auto next = [&]() -> std::string { return i + 1 < argc ? argv[++i] : ""; };
        if (a == "--model") model = next();
        else if (a == "--charset") charset = next();
        else if (a == "--roi") { auto v = split(next(), ','); if (v.size() == 4) { P.roi_x = std::stoi(v[0]); P.roi_y = std::stoi(v[1]); P.roi_w = std::stoi(v[2]); P.roi_h = std::stoi(v[3]); } }
        else if (a == "--char-h") P.char_height_px = std::stoi(next());
        else if (a == "--print") { auto v = next(); P.print = v == "solid" ? VS_PRINT_SOLID : v == "dot" ? VS_PRINT_DOT : v == "inkjet" ? VS_PRINT_INKJET : VS_PRINT_AUTO; }
        else if (a == "--dot-size") P.dot_size_px = std::stof(next());
        else if (a == "--join") P.join_strength = std::stof(next());
        else if (a == "--polarity") { auto v = next(); P.polarity = v == "dark" ? VS_POLARITY_DARK_ON_LIGHT : v == "light" ? VS_POLARITY_LIGHT_ON_DARK : VS_POLARITY_AUTO; }
        else if (a == "--min-quality") P.min_quality = std::stof(next());
        else if (a == "--budget") P.time_budget_ms = std::stoi(next());
        else if (a == "--runs") runs = std::max(1, std::stoi(next()));
        else if (a == "--limit") limit = std::stoi(next());
        else if (a == "--mutate") mutate = true;
        else if (a == "--clock-from-name") clockName = true;
        else if (a == "--expect") expectArg = next();
        else if (a == "--dump") dump = next();
        else if (a == "--find-only") findOnly = true;
        else if (a == "--read-only") readOnly = true;
        else if (a == "--csv") csvPath = next();
        else if (a == "--agree") { auto v = next(); agree = v == "off" ? VS_AGREE_OFF : v == "shift" ? VS_AGREE_SHIFT : VS_AGREE_AUTO; }
        else if (a == "--second-model") model2 = next();
        else if (a == "--teach-font") teachSpec = next();
        else if (a == "--layout") layoutArg = next();
        else if (a == "--layout-truth") layoutTruth = true;
        else if (a == "--tight") tight = true;
        else if (a.rfind("--", 0) != 0 && truthPath.empty()) truthPath = a;
        else { std::fprintf(stderr, "unknown option %s\n", a.c_str()); return 2; }
    }

    std::map<std::string, std::vector<std::string>> truth;
    if (!truthPath.empty()) {
        std::ifstream f(truthPath);
        std::string line;
        while (std::getline(f, line)) {
            if (line.empty() || line[0] == '#') continue;
            auto v = split(line, '\t');
            if (v.size() < 2) continue;
            truth[v[0]] = std::vector<std::string>(v.begin() + 1, v.end());
        }
    }
    std::vector<fs::path> files;
    for (const auto& e : fs::directory_iterator(folder)) {
        const auto ext = e.path().extension().string();
        if (ext == ".bmp" || ext == ".png" || ext == ".jpg" || ext == ".BMP")
            if (truth.empty() || truth.count(e.path().filename().string())) files.push_back(e.path());
    }
    std::sort(files.begin(), files.end());
    if (limit > 0 && int(files.size()) > limit) files.resize(size_t(limit));
    if (!dump.empty()) fs::create_directories(dump);

    if (findOnly) {
        double tot = 0;
        long linesAsTruth = 0;
        for (const auto& f : files) {
            cv::Mat g = cv::imread(f.string(), cv::IMREAD_GRAYSCALE);
            if (g.empty()) continue;
            ocr::FindParams fp;
            fp.roi = P.roi_w > 0 ? cv::Rect(P.roi_x, P.roi_y, P.roi_w, P.roi_h) & cv::Rect(0, 0, g.cols, g.rows) : cv::Rect(0, 0, g.cols, g.rows);
            fp.print = P.print; fp.polarity = P.polarity; fp.maxLines = P.max_lines; fp.maxSkewDeg = P.max_skew_deg;
            fp.dotSize = P.dot_size_px; fp.join = P.join_strength > 0 ? P.join_strength : 1.f; fp.tight = tight;
            ocr::Layout lay;
            std::string lerr;
            const auto tr = truth.count(f.filename().string()) ? truth[f.filename().string()] : std::vector<std::string>{};
            std::vector<std::u32string> tl;
            for (const auto& x : tr) tl.push_back(ocr::fromUtf8(x));
            if (!layoutArg.empty() || (layoutTruth && !tl.empty()))
                if (ocr::parseLayout(layoutTruth ? ocr::layoutOf(tl) : layoutArg, lay, lerr)) fp.layout = &lay;
            const auto t0 = std::chrono::steady_clock::now();
            fp.charHeight = P.char_height_px > 0 ? float(P.char_height_px) : ocr::estimateCharHeight(g, fp);
            auto lines = ocr::findLines(g, fp, P.print == VS_PRINT_DOT);
            const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
            tot += ms;
            const std::string vsTruth = tr.empty() ? "" : " (truth " + std::to_string(tr.size()) + ")";
            std::printf("%s  char-h %.1f  lines %zu%s  %.1f ms", f.filename().string().c_str(), fp.charHeight, lines.size(), vsTruth.c_str(), ms);
            linesAsTruth += !tr.empty() && lines.size() == tr.size();
            for (size_t i = 0; i < lines.size(); ++i) {
                std::printf("  [%dx%d a%.1f s%.1f h%.0f]", lines[i].image.cols, lines[i].image.rows, lines[i].angleDeg, lines[i].slantDeg, lines[i].charHeight);
                if (!dump.empty())
                    cv::imwrite((fs::path(dump) / (f.stem().string() + "_L" + std::to_string(i) + ".png")).string(), lines[i].image);
            }
            std::printf("\n");
            if (!dump.empty()) {   // overlay: every line found, on the region
                cv::Mat ov;
                cv::cvtColor(g, ov, cv::COLOR_GRAY2BGR);
                cv::rectangle(ov, fp.roi, {255, 160, 0}, 2);
                for (size_t i = 0; i < lines.size(); ++i) {
                    std::vector<cv::Point> q;
                    for (const auto& c : lines[i].corners) q.emplace_back(int(c.x), int(c.y));
                    cv::polylines(ov, q, true, lines[i].lightOnDark ? cv::Scalar(0, 220, 255) : cv::Scalar(0, 200, 0), 2);
                    cv::putText(ov, std::to_string(i), q[0] + cv::Point(-22, 18), cv::FONT_HERSHEY_SIMPLEX, 0.7, {0, 0, 255}, 2);
                }
                cv::Mat crop = ov(fp.roi).clone();
                cv::imwrite((fs::path(dump) / (f.stem().string() + "_overlay.jpg")).string(), crop);
            }
        }
        std::printf("images %zu  mean %.1f ms\n", files.size(), files.empty() ? 0 : tot / double(files.size()));
        if (!truth.empty()) std::printf("as many lines as the truth: %ld / %zu\n", linesAsTruth, files.size());
        return 0;
    }

    if (model.empty() || charset.empty()) { std::fprintf(stderr, "--model and --charset are needed\n"); return 2; }
    vs_ocr* t = vs_ocr_create(&P);
    if (vs_ocr_load_model(t, model.c_str(), charset.c_str()) != 0) {
        std::fprintf(stderr, "model: %s\n", vs_ocr_last_error(t));
        return 1;
    }
    vs_ocr_set_agreement(t, agree);
    if (!model2.empty() && vs_ocr_load_second_model(t, model2.c_str(), charset.c_str()) != 0) {
        std::fprintf(stderr, "second model: %s\n", vs_ocr_last_error(t));
        return 1;
    }
    if (!teachSpec.empty()) {
        const auto v = split(teachSpec, '=');
        if (v.size() != 3) { std::fprintf(stderr, "--teach-font <folder>=<truth.tsv>=<n>\n"); return 2; }
        std::ifstream f(v[1]);
        std::string line;
        int images = 0, taught = 0, refused = 0;
        while (images < std::stoi(v[2]) && std::getline(f, line)) {
            if (line.empty() || line[0] == '#') continue;
            const auto row = split(line, '\t');
            cv::Mat g = cv::imread((fs::path(v[0]) / row[0]).string(), cv::IMREAD_GRAYSCALE);
            if (g.empty() || row.size() < 2) continue;
            ++images;
            for (size_t i = 1; i < row.size(); ++i) {
                const int n = vs_ocr_teach_font(t, g.data, g.cols, g.rows, int32_t(g.step), int32_t(i - 1), row[i].c_str());
                if (n > 0) ++taught; else ++refused;
            }
        }
        std::printf("taught font: %d lines from %d images (%d refused), %d characters\n", taught, images, refused, vs_ocr_font_glyphs(t));
    }
    std::vector<std::string> expectPats = expectArg.empty() ? std::vector<std::string>{} : split(expectArg, '|');
    vs_ocr_set_tight_spacing(t, tight ? 1 : 0);
    if (!layoutArg.empty() && vs_ocr_set_layout(t, layoutArg.c_str()) != 0) {
        std::fprintf(stderr, "layout: %s\n", vs_ocr_last_error(t));
        return 2;
    }
    long layoutNg = 0, layoutWrongOk = 0, layoutLinesOk = 0;

    std::FILE* csv = csvPath.empty() ? nullptr : std::fopen(csvPath.c_str(), "w");
    if (csv) std::fprintf(csv, "file,pass,lines,ms,char_errors,chars,read\n");
    std::mt19937 rng(7);
    const char32_t pool[] = U"0123456789ABCDEFGHJKLMNPRSTUVWXYZ";
    std::vector<double> times;
    long chars = 0, charErr = 0, linesOk = 0, linesTot = 0, ocvPass = 0, ocvTot = 0, mutFail = 0, mutTot = 0, rightHi = 0;
    std::vector<double> wrongQ;   // line score of each truth line read wrong (closest line read)
    std::vector<double> passMargin, mutMargin;   // lowest line margin of each passing print, each changed-text run
    for (const auto& f : files) {
        cv::Mat g = cv::imread(f.string(), cv::IMREAD_GRAYSCALE);
        if (g.empty()) continue;
        const std::string name = f.filename().string();
        const auto tr = truth.count(name) ? truth[name] : std::vector<std::string>{};
        vs_ocr_set_clock(t, clockName ? clockFromName(name) : 0);
        // expected lines: --expect patterns, else the truth itself (exact text)
        vs_ocr_clear_expected(t);
        std::vector<std::string> exp = expectPats;
        if (exp.empty()) for (const auto& s : tr) exp.push_back(escapePattern(s));
        if (readOnly) exp.clear();
        for (size_t i = 0; i < exp.size(); ++i) vs_ocr_set_expected(t, int32_t(i), exp[i].c_str());
        if (layoutTruth) {
            std::vector<std::u32string> tl;
            for (const auto& x : tr) tl.push_back(ocr::fromUtf8(x));
            vs_ocr_set_layout(t, tl.empty() ? nullptr : ocr::layoutOf(tl).c_str());
        }

        double best = 1e9;
        for (int r = 0; r < runs; ++r) {
            if (vs_ocr_run(t, g.data, g.cols, g.rows, int32_t(g.step)) != 0) { std::fprintf(stderr, "%s: %s\n", name.c_str(), vs_ocr_last_error(t)); break; }
            vs_ocr_stats st;
            vs_ocr_get_stats(t, &st);
            best = std::min(best, double(st.total_ms));
        }
        times.push_back(best);
        vs_ocr_stats st;
        vs_ocr_get_stats(t, &st);
        if (st.pass && !exp.empty()) passMargin.push_back(lowestMargin(t));
        const int n = vs_ocr_line_count(t);
        std::string readAll;
        int errs = 0, cnt = 0;
        std::printf("%s  %s  %.1f ms", name.c_str(), exp.empty() && !vs_ocr_get_layout(t, nullptr, 0) ? "read" : (st.pass ? "OK" : "NG"), best);
        if (*vs_ocr_layout_reason(t)) { std::printf("  [layout: %s]", vs_ocr_layout_reason(t)); ++layoutNg; }
        for (int i = 0; i < n; ++i) {
            vs_ocr_line L;
            vs_ocr_get_line(t, i, &L);
            std::printf("  | %s%s (%.0f)", L.text, L.checked && !L.pass ? " !" : "", L.quality);
            readAll += std::string(i ? " | " : "") + L.text;
            if (!dump.empty()) {
                int w = 0;
                const int need = vs_ocr_get_line_image(t, i, nullptr, 0, &w);
                if (need > 0) {
                    cv::Mat im(ocr::kLineH, w, CV_8U);
                    vs_ocr_get_line_image(t, i, im.data, need, &w);
                    cv::imwrite((fs::path(dump) / (f.stem().string() + "_L" + std::to_string(i) + ".png")).string(), im);
                }
            }
        }
        std::printf("\n");
        if (!tr.empty()) {
            // character accuracy of the free read: each truth line against the closest line read
            // (other print in the region, such as label text, is read too and ignored here)
            // the score of a wrong read: the line's quality when it was read, not checked (--read-only)
            std::vector<std::u32string> got;
            std::vector<double> gotQ;
            std::vector<int> gotOk;   // read under a layout or format, and passed
            for (int i = 0; i < n; ++i) {
                vs_ocr_line L;
                vs_ocr_get_line(t, i, &L);
                if (!L.text || !*L.text) continue;
                got.push_back(noSpace(L.text));
                gotQ.push_back(L.checked ? -1.0 : double(L.quality));
                gotOk.push_back(readOnly && L.checked && L.pass);
            }
            for (size_t i = 0; i < tr.size(); ++i) {
                const std::u32string a = noSpace(tr[i]);
                int e = int(a.size());
                double q = -1;
                int ok = 0;
                for (size_t k = 0; k < got.size(); ++k) {
                    const int ek = editDistance(a, got[k]);
                    if (ek < e) { e = ek; q = gotQ[k]; ok = gotOk[k]; }
                }
                if (ok && e > 0) {
                    ++layoutWrongOk;
                    std::printf("  WRONG READ ACCEPTED: %s\n", tr[i].c_str());
                }
                layoutLinesOk += ok && e == 0;
                if (e > 0 && q >= 0) wrongQ.push_back(q);
                if (e == 0 && q >= P.min_quality) ++rightHi;
                errs += e;
                cnt += int(a.size());
                linesOk += e == 0;
                ++linesTot;
            }
            chars += cnt;
            charErr += errs;
            ++ocvTot;
            ocvPass += st.pass;
            if (mutate && !readOnly) {
                // one character changed in one line: must be NG
                std::uniform_int_distribution<size_t> pickLine(0, tr.size() - 1);
                const size_t li = pickLine(rng);
                std::u32string u = ocr::fromUtf8(tr[li]);
                std::vector<size_t> pos;
                for (size_t k = 0; k < u.size(); ++k) if (u[k] != U' ') pos.push_back(k);
                if (!pos.empty()) {
                    const size_t k = pos[std::uniform_int_distribution<size_t>(0, pos.size() - 1)(rng)];
                    char32_t c;
                    do c = pool[std::uniform_int_distribution<size_t>(0, 32)(rng)]; while (c == u[k]);
                    u[k] = c;
                    vs_ocr_set_expected(t, int32_t(li), escapePattern(ocr::toUtf8(u)).c_str());
                    vs_ocr_run(t, g.data, g.cols, g.rows, int32_t(g.step));
                    vs_ocr_stats ms;
                    vs_ocr_get_stats(t, &ms);
                    ++mutTot;
                    mutMargin.push_back(lowestMargin(t));
                    if (ms.pass) {
                        ++mutFail;
                        std::printf("  FALSE ACCEPT: %s\n", ocr::toUtf8(u).c_str());
                    }
                }
            }
        }
        if (csv) std::fprintf(csv, "%s,%d,%d,%.2f,%d,%d,\"%s\"\n", name.c_str(), st.pass, n, best, errs, cnt, readAll.c_str());
    }
    if (csv) std::fclose(csv);
    std::printf("\nimages %zu  time median %.1f  p95 %.1f  max %.1f ms\n", times.size(), pct(times, 0.5), pct(times, 0.95), pct(times, 1.0));
    if (chars) {
        std::printf("free read: character accuracy %.2f%% (%ld errors / %ld), lines exact %ld / %ld\n",
                    100.0 * double(chars - charErr) / double(chars), charErr, chars, linesOk, linesTot);
        if (!readOnly) std::printf("check (OCV) against truth: pass %ld / %ld\n", ocvPass, ocvTot);
        else std::printf("read text: images passing %ld / %ld, lines read right at or above min quality %ld / %ld\n",
                         ocvPass, ocvTot, rightHi, linesTot);
        if (mutate) std::printf("one character changed: false accepts %ld / %ld\n", mutFail, mutTot);
        // verification margin (vs_ocr_line_ext.margin, nats): how far the passing prints are from being read as
        // another text, and how close the changed texts came
        if (!passMargin.empty())
            std::printf("margin of passing prints (lowest line, nats): min %.2f  5%% %.2f  median %.2f\n",
                        pct(passMargin, 0.0), pct(passMargin, 0.05), pct(passMargin, 0.5));
        if (!mutMargin.empty())
            std::printf("margin with one character changed: max %.2f  95%% %.2f\n", pct(mutMargin, 1.0), pct(mutMargin, 0.95));
        if (!layoutArg.empty() || layoutTruth)
            std::printf("layout: images not fitting %ld; read-only lines passed and right %ld, passed but wrong %ld\n",
                        layoutNg, layoutLinesOk, layoutWrongOk);
        if (!wrongQ.empty()) {
            const long hi = long(std::count_if(wrongQ.begin(), wrongQ.end(), [&](double q) { return q >= P.min_quality; }));
            std::printf("wrong reads: score median %.0f, max %.0f, at or above min quality %.0f: %ld / %zu\n",
                        pct(wrongQ, 0.5), pct(wrongQ, 1.0), double(P.min_quality), hi, wrongQ.size());
        }
    }
    vs_ocr_destroy(t);
    return 0;
}
