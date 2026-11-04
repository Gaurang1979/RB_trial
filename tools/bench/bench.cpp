// bench - runs the engine over a folder of images and reports read rate and time.
//
// Usage: bench <image-folder> [truth.csv] [--runs N] [--budget MS|auto] [--threads N] [--csv out.csv]
// --budget auto: the line's time target for the case size (12: 100 ms, 24: 275, 45/48: 500, 96: 600).
//
// truth.csv rows: file,format,expected   (format: qr | dm | code128 | ean13 | ean8 | upca | upce | code39 | code93 | itf | codabar | databar
//                                     | microqr | rmqr | pdf417 | aztec | pharmacode | pharmacode_rev | dotcode)
// Images not in truth.csv are read as QR with an unknown count.
// --texts codes.csv: the true texts per image, by SHA-256 (truth/README.md). Every read whose text
// is not among its image's true texts is a wrong read: counted, listed and written to the csv.

#include "codereader.h"
#include "sha256.hpp"

#include <opencv2/imgcodecs.hpp>

#include <algorithm>
#ifndef _WIN32
#include <sys/resource.h>
#else
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif
#include <cctype>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <sstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace {

struct Truth {
    int format = CR_QR;
    int expected = 0;
};

int parseFormat(const std::string& s)
{
    if (s == "dm" || s == "datamatrix") return CR_DATAMATRIX;
    if (s == "any") return CR_ANY; // mixed types (locator_recall.py --via)
    if (s == "code128" || s == "gs1-128") return CR_GS1_128;
    if (s == "ean13") return CR_EAN13;
    if (s == "ean8") return CR_EAN8;
    if (s == "upca") return CR_UPCA;
    if (s == "upce") return CR_UPCE;
    if (s == "code39") return CR_CODE39;
    if (s == "code93") return CR_CODE93;
    if (s == "itf") return CR_ITF;
    if (s == "codabar") return CR_CODABAR;
    if (s == "databar") return CR_DATABAR;
    if (s == "microqr") return CR_MICRO_QR;
    if (s == "rmqr") return CR_RMQR;
    if (s == "pdf417") return CR_PDF417;
    if (s == "aztec") return CR_AZTEC;
    if (s == "pharmacode") return CR_PHARMACODE;
    if (s == "pharmacode_rev") return CR_PHARMACODE_REV;
    if (s == "dotcode") return CR_DOTCODE;
    return CR_QR;
}

std::map<std::string, Truth> loadTruth(const std::string& path)
{
    std::map<std::string, Truth> out;
    std::ifstream in(path);
    std::string line;
    while (std::getline(in, line)) {
        if (line.empty() || line[0] == '#') continue;
        std::stringstream ss(line);
        std::string file, fmt, exp;
        if (!std::getline(ss, file, ',') || !std::getline(ss, fmt, ',') || !std::getline(ss, exp, ',')) continue;
        if (file == "file") continue; // header
        Truth t;
        t.format = parseFormat(fmt);
        t.expected = std::atoi(exp.c_str());
        out[file] = t;
    }
    return out;
}

// truth/codes.csv: file,sha256[,text] - the SHA-256 (hex) of each true text of each image
std::map<std::string, std::set<std::string>> loadTexts(const std::string& path)
{
    std::map<std::string, std::set<std::string>> out;
    std::ifstream in(path);
    if (!in) {
        std::fprintf(stderr, "cannot read %s\n", path.c_str());
        std::exit(2);
    }
    std::string line;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty() || line[0] == '#') continue;
        std::stringstream ss(line);
        std::string file, hash;
        if (!std::getline(ss, file, ',') || !std::getline(ss, hash, ',') || file == "file") continue;
        std::transform(hash.begin(), hash.end(), hash.begin(), ::tolower);
        out[file].insert(hash);
    }
    return out;
}

const char* stepName(int s)
{
    switch (s) {
    case CR_STEP_PLAIN: return "plain";
    case CR_STEP_UPSCALE: return "upscale";
    case CR_STEP_CLAHE: return "clahe";
    case CR_STEP_SHARPEN: return "sharpen";
    case CR_STEP_BINARY: return "binary";
    case CR_STEP_AI_ENHANCE: return "ai-enhance";
    case CR_STEP_FLAT: return "flat";
    case CR_STEP_FLAT_ADAPT: return "flat-adapt";
    case CR_STEP_DMTX: return "dmtx";
    case CR_STEP_DEBLUR: return "deblur";
    case CR_STEP_DEBLUR_STRONG: return "deblur2";
    case CR_STEP_UPSCALE4: return "up4";
    case CR_STEP_GRIDFIT: return "gridfit";
    case CR_STEP_MATCH: return "match";
    case CR_STEP_LATTICE: return "lattice";
    case CR_STEP_KNOWN: return "known";
    case CR_STEP_GLARE: return "glare";
    case CR_STEP_FRAMES: return "frames";
    case CR_STEP_LINEAR: return "linear";
    case CR_STEP_LINEAR_SSCC: return "linear-sscc";
    case CR_STEP_DOTS: return "dots";
    case CR_STEP_FULL: return "full";
    case CR_STEP_FULL_BRIGHT: return "full-bright";
    default: return "?";
    }
}

} // namespace

// The line's time target per case (owner, 2026-10-23): 12 codes 100 ms, 24 codes 250-300 ms,
// 45 and 48 codes 500 ms, 96 codes 600 ms.
static int targetMs(int expected)
{
    if (expected <= 0) return 500;
    if (expected <= 12) return 100;
    if (expected <= 24) return 275;
    if (expected <= 48) return 500;
    return 600;
}

int main(int argc, char** argv)
{
    if (argc < 2) {
        std::fprintf(stderr, "usage: %s <image-folder> [truth.csv] [--runs N] [--budget MS|auto] [--threads N] [--csv out.csv]\n"
                             "       [--model codenet.onnx] [--enhancer enhancer.onnx] [--locator auto|classical|ai|ai+classical]\n"
                             "       [--quality codes.csv]   (grade every decoded code)\n"
                             "       [--codes codes.csv]     (every code read: text, step, centre, SHA-256, in truth)\n"
                             "       [--texts codes.csv]     (true texts by SHA-256, truth/README.md: reads not in it are wrong reads)\n"
                             "       [--misses misses.csv]   (unread regions of short cases: box)\n"
                             "       [--frames N]            (N consecutive files, in name order, are one case: cr_decode_frames)\n"
                             "       [--hint-modules N] [--hint-side PX] [--hint-symbols S]  (what calibration measured: seeds a new reader)\n"
                             "       [--learned DIR]         (a product's learned state saved in DIR as <format>_<count>.learned after each\n"
                             "                                reader and loaded into the next: a shift starting where the last one stopped.\n"
                             "                                Start from an empty DIR with --runs 1: a second run, or --runs N, measures the\n"
                             "                                reader on pictures it learned from. Products of one format and count share a file)\n"
                             "       [--carry]               (one reader per product (format + expected count), carried across its images in\n"
                             "                                name order: learning carries over, as on a line; use with --runs 1, since\n"
                             "                                repeating an image re-learns its own pixels)\n", argv[0]);
        return 2;
    }
    std::string folder = argv[1], truthPath, csvPath, modelPath, enhancerPath, qualityPath, codesPath, missesPath, textsPath;
    int locator = CR_LOCATE_AUTO, enhance = CR_ENHANCE_LAST;
    int runs = 3, budget = 200, threads = 0, framesPerCase = 1;
    int hintModules = 0;
    float hintSide = 0.f, hintSymbols = 0.f; // what calibration would hand a reader (cr_params hints)
    bool carry = false; // one reader carried across the images (its learned state persists), as on a line
    std::string learnedDir; // learned state saved and loaded per product (cr_get_learned / cr_set_learned)
    for (int i = 2; i < argc; ++i) {
        const std::string a = argv[i];
        if (a == "--runs" && i + 1 < argc) runs = std::max(1, std::atoi(argv[++i]));
        else if (a == "--budget" && i + 1 < argc) {
            const std::string v = argv[++i];
            budget = v == "auto" ? -1 : std::atoi(v.c_str()); // auto: the line's target for the case size
        }
        else if (a == "--threads" && i + 1 < argc) threads = std::atoi(argv[++i]);
        else if (a == "--csv" && i + 1 < argc) csvPath = argv[++i];
        else if (a == "--model" && i + 1 < argc) modelPath = argv[++i];
        else if (a == "--enhancer" && i + 1 < argc) enhancerPath = argv[++i];
        else if (a == "--quality" && i + 1 < argc) qualityPath = argv[++i];
        else if (a == "--codes" && i + 1 < argc) codesPath = argv[++i];
        else if (a == "--texts" && i + 1 < argc) textsPath = argv[++i];
        else if (a == "--misses" && i + 1 < argc) missesPath = argv[++i];
        else if (a == "--frames" && i + 1 < argc) framesPerCase = std::clamp(std::atoi(argv[++i]), 1, 8);
        else if (a == "--hint-modules" && i + 1 < argc) hintModules = std::atoi(argv[++i]);
        else if (a == "--hint-side" && i + 1 < argc) hintSide = static_cast<float>(std::atof(argv[++i]));
        else if (a == "--hint-symbols" && i + 1 < argc) hintSymbols = static_cast<float>(std::atof(argv[++i]));
        else if (a == "--carry") carry = true;
        else if (a == "--learned" && i + 1 < argc) learnedDir = argv[++i];
        else if (a == "--enhance" && i + 1 < argc) {
            const std::string m = argv[++i];
            enhance = m == "early" ? CR_ENHANCE_EARLY : m == "second-pass" ? CR_ENHANCE_SECOND_PASS : m == "off" ? CR_ENHANCE_OFF : CR_ENHANCE_LAST;
        }
        else if (a == "--locator" && i + 1 < argc) {
            const std::string m = argv[++i];
            locator = m == "classical" ? CR_LOCATE_CLASSICAL : m == "ai" ? CR_LOCATE_AI : m == "ai+classical" ? CR_LOCATE_AI_CLASSICAL : CR_LOCATE_AUTO;
        }
        else truthPath = a;
    }
    const auto truth = truthPath.empty() ? std::map<std::string, Truth>{} : loadTruth(truthPath);
    const auto texts = textsPath.empty() ? std::map<std::string, std::set<std::string>>{} : loadTexts(textsPath);

    std::vector<fs::path> files;
    for (const auto& e : fs::directory_iterator(folder)) {
        auto ext = e.path().extension().string();
        std::transform(ext.begin(), ext.end(), ext.begin(), ::tolower);
        if (ext == ".bmp" || ext == ".png" || ext == ".jpg" || ext == ".tif" || ext == ".tiff") files.push_back(e.path());
    }
    std::sort(files.begin(), files.end());

    std::ofstream qcsv;
    if (!qualityPath.empty()) {
        qcsv.open(qualityPath);
        qcsv << "file,code,step,module_px,symbol_contrast,modulation,reflectance_margin,fixed_pattern_damage,"
                "axial_nonuniformity,grid_nonuniformity,unused_ec,print_growth,quiet_zone_dark,lighting_uniformity,saturated,"
                "grade_sc,grade_mod,grade_rm,grade_fpd,grade_an,grade_gn,grade_uec,grade_qz,overall\n";
    }
    std::ofstream codesCsv;
    if (!codesPath.empty()) {
        codesCsv.open(codesPath);
        codesCsv << "file,text,step,x,y,sha256,in_truth\n";
    }
    std::ofstream missesCsv;
    if (!missesPath.empty()) {
        missesCsv.open(missesPath);
        missesCsv << "file,x,y,w,h,reason,sharpness,contrast,light,dark,saturated,texture,module_px,learned_module_px,finder,quiet_zone,slot_row,slot_col\n";
    }
    std::ofstream csv;
    if (!csvPath.empty()) {
        csv.open(csvPath);
        csv << "file,format,expected,decoded,candidates,misses,locate_ms,total_ms_median,total_ms_max,label_brightness,cpu_ms_median,wrong\n";
    }

    std::printf("engine %s, %d run(s) per image, budget %d ms, model %s, enhancer %s\n\n", cr_version(), runs, budget,
                modelPath.empty() ? "none" : modelPath.c_str(), enhancerPath.empty() ? "none" : enhancerPath.c_str());
    std::printf("%-36s %5s %8s %6s %5s %7s %9s %9s  %s\n", "image", "fmt", "expected", "read", "rate", "label", "median ms", "max ms", "steps");

    int totalExpected = 0, totalRead = 0, imagesFull = 0, imagesWithTruth = 0;
    int totalWrong = 0, imagesWrong = 0; // --texts: reads whose text is not among the image's true texts
    int overBudget = 0, overBudgetMax = 0; // images whose median / slowest run is over their budget
    std::vector<double> allMax;
    std::vector<double> allMedians, allCpu;

    cr_reader* carried = nullptr;
    int carriedFormat = 0, carriedExpected = -1;
    // --learned: one file per product (format + expected count), as the station keeps one per product
    const auto learnedFile = [&](const cr_params& q) {
        return learnedDir + "/" + std::to_string(q.format) + "_" + std::to_string(q.expected_count) + ".learned";
    };
    const auto loadLearned = [&](cr_reader* r, const cr_params& q) {
        if (learnedDir.empty()) return;
        std::ifstream f(learnedFile(q));
        if (!f) return;
        const std::string text((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
        if (cr_set_learned(r, text.c_str()) != 0) std::fprintf(stderr, "%s: %s\n", learnedFile(q).c_str(), cr_last_error(r));
    };
    const auto saveLearned = [&](cr_reader* r, const cr_params& q) {
        if (learnedDir.empty()) return;
        const int32_t n = cr_get_learned(r, nullptr, 0);
        std::string text(static_cast<size_t>(std::max(n, 1)), '\0');
        if (n <= 0 || cr_get_learned(r, text.data(), n) != n) return;
        text.resize(static_cast<size_t>(n - 1));
        std::ofstream out(learnedFile(q));
        if (!(out << text)) std::fprintf(stderr, "cannot write %s\n", learnedFile(q).c_str());
    };
    cr_params carriedParams{};
    for (size_t fi = 0; fi < files.size(); fi += static_cast<size_t>(framesPerCase)) {
        const auto& path = files[fi];
        const cv::Mat img = cv::imread(path.string(), cv::IMREAD_GRAYSCALE);
        if (img.empty()) {
            std::printf("%-36s could not load\n", path.filename().string().c_str());
            continue;
        }
        // the case's other frames (--frames N): the files that follow, same size
        std::vector<cv::Mat> frames = {img};
        for (size_t k = 1; k < static_cast<size_t>(framesPerCase) && fi + k < files.size(); ++k) {
            const cv::Mat fk = cv::imread(files[fi + k].string(), cv::IMREAD_GRAYSCALE);
            if (!fk.empty() && fk.size() == img.size()) frames.push_back(fk);
        }
        std::vector<const uint8_t*> framePtrs;
        for (const auto& f : frames) framePtrs.push_back(f.data);
        const auto it = truth.find(path.filename().string());
        const Truth t = it != truth.end() ? it->second : Truth{};

        cr_params p;
        cr_default_params(&p);
        p.format = t.format;
        p.expected_count = t.expected;
        p.time_budget_ms = budget > 0 ? budget : targetMs(t.expected);
        p.threads = threads;
        p.locator_mode = locator;
        p.grade_quality = qualityPath.empty() ? 0 : 1;
        p.enhancer_mode = enhance;
        // the hint applies only where it is valid for the case's format (a mixed folder)
        const bool qrHint = hintModules >= 21 && (hintModules - 21) % 4 == 0, dmHint = hintModules >= 10 && hintModules <= 26 && hintModules % 2 == 0;
        p.hint_modules = (p.format & CR_QR) && qrHint ? hintModules : (p.format & CR_DATAMATRIX) && dmHint ? hintModules : 0;
        p.hint_code_side_px = hintSide;
        p.hint_symbols_per_code = hintSymbols;
        // a carried reader is one product's: what it learned must not leak into the next product
        // (pruned steps, sizes, encodings), so it starts afresh when the format or count changes
        if (carry && carried && (p.format != carriedFormat || p.expected_count != carriedExpected)) {
            saveLearned(carried, carriedParams);
            cr_destroy(carried);
            carried = nullptr;
        }
        cr_reader* r = carry && carried ? carried : cr_create(&p);
        if (carry && carried) cr_set_params(r, &p);
        else {
            if (!modelPath.empty() && cr_load_model(r, modelPath.c_str()) != 0) {
                std::fprintf(stderr, "cannot load model %s: %s\n", modelPath.c_str(), cr_last_error(r));
                return 1;
            }
            if (!enhancerPath.empty() && cr_load_enhancer(r, enhancerPath.c_str()) != 0) {
                std::fprintf(stderr, "cannot load enhancer %s: %s\n", enhancerPath.c_str(), cr_last_error(r));
                return 1;
            }
            loadLearned(r, p);
            if (carry) {
                carried = r;
                carriedFormat = p.format;
                carriedExpected = p.expected_count;
                carriedParams = p;
            }
        }

        std::vector<double> times, cpuTimes; // wall ms, and process CPU ms (all threads: what the line PC spends)
        cr_stats st{};
        auto cpuMs = []() {
#ifndef _WIN32
            rusage u{};
            getrusage(RUSAGE_SELF, &u);
            return (u.ru_utime.tv_sec + u.ru_stime.tv_sec) * 1000.0 + (u.ru_utime.tv_usec + u.ru_stime.tv_usec) / 1000.0;
#else
            FILETIME c, e, k, u;
            if (!GetProcessTimes(GetCurrentProcess(), &c, &e, &k, &u)) return 0.0;
            const auto t = [](const FILETIME& f) { return ((static_cast<unsigned long long>(f.dwHighDateTime) << 32) | f.dwLowDateTime) / 10000.0; };
            return t(k) + t(u);
#endif
        };
        // CR_CANDS=1: the engine lists every region on stderr; name the image first (locator_recall.py)
        static const bool listCands = std::getenv("CR_CANDS") != nullptr;
        for (int run = 0; run < runs; ++run) {
            if (listCands) std::fprintf(stderr, "file %s run %d\n", path.filename().string().c_str(), run);
            const double cpu0 = cpuMs();
            const int rc = frames.size() > 1
                               ? cr_decode_frames(r, framePtrs.data(), static_cast<int32_t>(frames.size()), img.cols, img.rows, static_cast<int32_t>(img.step))
                               : cr_decode(r, img.data, img.cols, img.rows, static_cast<int32_t>(img.step));
            if (rc < 0) {
                std::printf("%-36s error: %s\n", path.filename().string().c_str(), cr_last_error(r));
                break;
            }
            cr_get_stats(r, &st);
            times.push_back(st.total_ms);
            cpuTimes.push_back(cpuMs() - cpu0);
        }
        if (times.empty()) {
            if (!carry) { saveLearned(r, p); cr_destroy(r); }
            continue;
        }
        std::sort(times.begin(), times.end());
        std::sort(cpuTimes.begin(), cpuTimes.end());
        const double median = times[times.size() / 2], maxT = times.back(), cpuMedian = cpuTimes[cpuTimes.size() / 2];
        allCpu.push_back(cpuMedian);
        allMedians.push_back(median);
        if (median > p.time_budget_ms) ++overBudget;
        if (maxT > p.time_budget_ms) ++overBudgetMax;
        allMax.push_back(maxT);

        // zone map: where on the image this reader loses codes (4 x 4, over all runs)
        if (std::getenv("CR_ZONES")) {
            int32_t zr[16], zu[16];
            cr_get_zone_stats(r, zr, zu);
            std::printf("   zones unread/found:");
            for (int z = 0; z < 16; ++z) std::printf("%s %d/%d", z % 4 ? "" : " |", zu[z], zr[z] + zu[z]);
            std::printf("\n");
        }
        int stepCount[24] = {};
        int wrong = 0;
        std::string wrongList;
        const auto trueTexts = texts.find(path.filename().string()); // an image not in --texts has none
        for (int i = 0; i < cr_code_count(r); ++i) {
            cr_code c;
            cr_get_code(r, i, &c);
            if (c.step >= 0 && c.step < 24) stepCount[c.step]++;
            const std::string hash = vsbench::sha256Hex(c.text);
            const bool inTruth = trueTexts != texts.end() && trueTexts->second.count(hash) > 0;
            std::string text = c.text;
            for (auto& ch : text) if (ch == ',' || ch == '\x1d' || ch == '\n') ch = ' ';
            if (!textsPath.empty() && !inTruth) {
                ++wrong;
                char where[64];
                std::snprintf(where, sizeof where, " (%s at %.0f,%.0f)", stepName(c.step), (c.x[0] + c.x[2]) / 2, (c.y[0] + c.y[2]) / 2);
                wrongList += "   WRONG READ " + hash.substr(0, 12) + " " + text + where + "\n";
            }
            if (codesCsv.is_open()) {
                codesCsv << path.filename().string() << ',' << text << ',' << stepName(c.step) << ','
                         << (c.x[0] + c.x[2]) / 2 << ',' << (c.y[0] + c.y[2]) / 2 << ',' << hash << ','
                         << (textsPath.empty() ? "" : inTruth ? "1" : "0") << '\n';
            }
        }
        totalWrong += wrong;
        if (wrong) ++imagesWrong;
        if (missesCsv.is_open())
            for (int i = 0; i < cr_miss_count(r); ++i) {
                static const char* const reasons[] = {"UNKNOWN", "CUT_OFF", "GLARE", "LOW_CONTRAST", "COVERED", "TOO_SMALL", "BLUR", "NO_FINDER"};
                cr_miss_info m;
                cr_get_miss_info(r, i, &m);
                missesCsv << path.filename().string() << ',' << m.x << ',' << m.y << ',' << m.w << ',' << m.h << ','
                          << (m.measured ? reasons[m.reason] : "-") << ',' << m.sharpness << ',' << m.contrast << ',' << m.light << ','
                          << m.dark << ',' << m.saturated << ',' << m.texture << ',' << m.module_px << ',' << m.learned_module_px << ','
                          << m.finder << ',' << m.quiet_zone << ',' << m.slot_row << ',' << m.slot_col << '\n';
            }
        std::string steps;
        for (int s = 0; s < 24; ++s)
            if (stepCount[s]) steps += std::string(stepName(s)) + "=" + std::to_string(stepCount[s]) + " ";

        char rate[16] = "-";
        if (t.expected > 0) {
            std::snprintf(rate, sizeof rate, "%3.0f%%", 100.0 * std::min(st.codes, t.expected) / t.expected);
            totalExpected += t.expected;
            totalRead += std::min(st.codes, t.expected);
            imagesWithTruth++;
            if (st.codes >= t.expected) imagesFull++;
        }
        const char* fmt = t.format == CR_DATAMATRIX ? "dm" : t.format == CR_QR ? "qr" : (t.format & CR_ALL_LINEAR) ? "1d" : "2d";
        std::printf("%-36s %5s %8d %6d %5s %7.0f %9.0f %9.0f  %s\n", path.filename().string().substr(0, 36).c_str(), fmt,
                    t.expected, st.codes, rate, st.label_brightness, median, maxT, steps.c_str());
        std::fputs(wrongList.c_str(), stdout);
        if (qcsv.is_open()) {
            int gradeCount[5] = {};
            for (int i = 0; i < cr_code_count(r); ++i) {
                cr_code c;
                cr_quality q;
                cr_get_code(r, i, &c);
                if (cr_get_quality(r, i, &q) != 0 || !q.measured) continue;
                gradeCount[q.overall]++;
                std::string text = c.text;
                for (auto& ch : text) if (ch == ',' || ch == '\x1d' || ch == '\n') ch = ' ';
                qcsv << path.filename().string() << ',' << text << ',' << stepName(c.step) << ',' << q.module_size_px << ','
                     << q.symbol_contrast << ',' << q.modulation << ',' << q.reflectance_margin << ',' << q.fixed_pattern_damage << ','
                     << q.axial_nonuniformity << ',' << q.grid_nonuniformity << ',' << q.unused_error_correction << ','
                     << q.print_growth << ',' << q.quiet_zone_dark << ',' << q.lighting_uniformity << ',' << q.saturated << ','
                     << q.grade_sc << ',' << q.grade_mod << ',' << q.grade_rm << ',' << q.grade_fpd << ',' << q.grade_an << ','
                     << q.grade_gn << ',' << q.grade_uec << ',' << q.grade_qz << ',' << q.overall << '\n';
            }
            std::printf("%-36s quality A %d  B %d  C %d  D %d  F %d\n", "", gradeCount[4], gradeCount[3], gradeCount[2],
                        gradeCount[1], gradeCount[0]);
        }
        if (csv.is_open()) {
            csv << path.filename().string() << ',' << fmt << ',' << t.expected << ',' << st.codes << ',' << st.candidates << ','
                << st.misses << ',' << st.locate_ms << ',' << median << ',' << maxT << ',' << st.label_brightness << ',' << cpuMedian << ','
                << (textsPath.empty() ? "" : std::to_string(wrong)) << '\n';
        }
        if (!carry) { saveLearned(r, p); cr_destroy(r); }
    }
    if (carried) {
        saveLearned(carried, carriedParams);
        cr_destroy(carried);
    }

    if (!allMedians.empty()) {
        std::sort(allMedians.begin(), allMedians.end());
        std::sort(allMax.begin(), allMax.end());
        std::sort(allCpu.begin(), allCpu.end());
        double cpuSum = 0;
        for (double c : allCpu) cpuSum += c;
        std::printf("\n%zu images, median time %.0f ms, p95 of slowest runs %.0f ms, slowest run %.0f ms, %d over budget by median, %d by slowest run; CPU per case median %.0f ms, total %.1f s\n",
                    allMedians.size(), allMedians[allMedians.size() / 2], allMax[allMax.size() * 95 / 100], allMax.back(), overBudget, overBudgetMax,
                    allCpu[allCpu.size() / 2], cpuSum / 1000.0);
    }
    if (imagesWithTruth > 0) {
        std::printf("read rate %d/%d codes (%.1f%%), %d/%d cases complete\n", totalRead, totalExpected,
                    100.0 * totalRead / totalExpected, imagesFull, imagesWithTruth);
    }
    if (!textsPath.empty()) std::printf("wrong reads %d on %d image(s) (texts not in %s)\n", totalWrong, imagesWrong, textsPath.c_str());
    return 0;
}
