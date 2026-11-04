// diagnose.cpp - why an unread code did not read, in one word for the operator.
//
// After the expired Cognex verifier patents US9552506 / US10061946 (grade a mark after a failed
// decode: sharpness = min(1, edge strength / (light - dark)) from the strongest 10% of edges and
// the 10% lightest / darkest pixels, a finder score, the quiet zone; results shown on the image)
// and US8027802. It runs after decoding, only on the misses of short cases, within a small time
// cap, so the decode itself is unchanged. The thresholds are camera grey levels on the sample
// lines, not calibrated reflectance: a line hint, not a verifier grade.

#include "reader.hpp"

#include <opencv2/core/utility.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <vector>

namespace cr {

namespace {

using Clock = std::chrono::steady_clock;

// Thresholds of the reasons, from the synthetic crops in engine_tests and the 1960 read codes of
// the 55 sample images (sharpness p1 0.39, contrast p1 13 grey levels, texture p1 0.14, module
// p1 1.95 px; nothing saturated).
constexpr float kGlareShare = 0.10f;      // saturated share of the code area
constexpr float kLowContrast = 25.f;      // light - dark, grey levels, when nothing read in the image
constexpr float kMinContrast = 8.f;       // ... and never above this when codes did read
constexpr float kContrastVsRead = 0.45f;  // ... else this share of the read codes' median contrast
constexpr float kMinTexture = 0.06f;      // edge share of the code area
constexpr float kTooSmallPx = 2.0f;       // px per module
constexpr float kBlurSharpness = 0.35f;   // when nothing read in the image
constexpr float kSharpnessVsRead = 0.7f;  // ... else this share of the read codes' median sharpness
constexpr float kEvidenceModuleLow = 0.6f;  // code evidence: measured module size against the product's
constexpr float kEvidenceModuleHigh = 1.6f;
constexpr float kCodeTexture = 0.14f;      // edge share of a read code's area (p1 of the samples)
constexpr float kNoTextureBlur = 0.7f;    // no code texture left: blurred only when this much softer still

float median(std::vector<float> v)
{
    if (v.empty()) return 0.f;
    std::nth_element(v.begin(), v.begin() + static_cast<long>(v.size() / 2), v.end());
    return v[v.size() / 2];
}

// Means of the brightest and darkest 10% of the pixels under mask, and the share at 250+.
void levels(const cv::Mat& g, const cv::Mat& mask, float& light, float& dark, float& saturated)
{
    int hist[256] = {};
    int64_t n = 0;
    for (int y = 0; y < g.rows; ++y) {
        const uint8_t* row = g.ptr<uint8_t>(y);
        const uint8_t* mk = mask.ptr<uint8_t>(y);
        for (int x = 0; x < g.cols; ++x)
            if (mk[x]) {
                hist[row[x]]++;
                ++n;
            }
    }
    light = dark = saturated = 0.f;
    if (n == 0) return;
    const int64_t tenth = std::max<int64_t>(1, n / 10);
    int64_t got = 0;
    double sum = 0;
    for (int v = 0; v < 256 && got < tenth; ++v) {
        const int64_t take = std::min<int64_t>(hist[v], tenth - got);
        sum += static_cast<double>(take) * v;
        got += take;
    }
    dark = static_cast<float>(sum / static_cast<double>(got));
    got = 0;
    sum = 0;
    for (int v = 255; v >= 0 && got < tenth; --v) {
        const int64_t take = std::min<int64_t>(hist[v], tenth - got);
        sum += static_cast<double>(take) * v;
        got += take;
    }
    light = static_cast<float>(sum / static_cast<double>(got));
    int64_t sat = 0;
    for (int v = 250; v < 256; ++v) sat += hist[v];
    saturated = static_cast<float>(sat) / static_cast<float>(n);
}

// Sobel magnitude / 4: a step edge of height d measures d at the edge.
cv::Mat edgeMagnitude(const cv::Mat& g)
{
    cv::Mat gx, gy, mag;
    cv::Sobel(g, gx, CV_32F, 1, 0);
    cv::Sobel(g, gy, CV_32F, 0, 1);
    cv::magnitude(gx, gy, mag);
    return mag * 0.25f;
}

// Mean of the strongest 10% of the edge values under mask.
float strongestEdges(const cv::Mat& mag, const cv::Mat& mask)
{
    std::vector<float> v;
    v.reserve(static_cast<size_t>(cv::countNonZero(mask)));
    for (int y = 0; y < mag.rows; ++y) {
        const float* row = mag.ptr<float>(y);
        const uint8_t* mk = mask.ptr<uint8_t>(y);
        for (int x = 0; x < mag.cols; ++x)
            if (mk[x]) v.push_back(row[x]);
    }
    if (v.empty()) return 0.f;
    const size_t k = v.size() - std::max<size_t>(1, v.size() / 10);
    std::nth_element(v.begin(), v.begin() + static_cast<long>(k), v.end());
    double s = 0;
    for (size_t i = k; i < v.size(); ++i) s += v[i];
    return static_cast<float>(s / static_cast<double>(v.size() - k));
}

// Module size from the binarised texture: random modules change colour at about every second
// module boundary, so area / (horizontal + vertical transitions) ~ one module.
float modulesFromTransitions(const cv::Mat& bin, const cv::Mat& mask)
{
    int64_t t = 0, a = 0;
    for (int y = 0; y + 1 < bin.rows; ++y) {
        const uint8_t* r0 = bin.ptr<uint8_t>(y);
        const uint8_t* r1 = bin.ptr<uint8_t>(y + 1);
        const uint8_t* mk = mask.ptr<uint8_t>(y);
        for (int x = 0; x + 1 < bin.cols; ++x) {
            if (!mk[x]) continue;
            ++a;
            t += (r0[x] != r0[x + 1]) + (r0[x] != r1[x]);
        }
    }
    return t > 0 ? static_cast<float>(a) / static_cast<float>(t) : 0.f;
}

// One line of the binarised image (1 = dark): centres of dark-light-dark-light-dark runs in the
// finder ratio 1:1:3:1:1, for a finder of about m px per module. Any line through a finder's
// centre crosses its rings in this ratio, whatever the rotation.
void finderRuns(const std::vector<uint8_t>& line, float m, std::vector<float>& centres)
{
    std::vector<int> start, len;
    for (int i = 0; i < static_cast<int>(line.size());) {
        int j = i;
        while (j < static_cast<int>(line.size()) && line[static_cast<size_t>(j)] == line[static_cast<size_t>(i)]) ++j;
        start.push_back(i);
        len.push_back(j - i);
        i = j;
    }
    for (size_t k = 0; k + 4 < len.size(); ++k) {
        if (!line[static_cast<size_t>(start[k])]) continue; // starts dark
        const float total = static_cast<float>(len[k] + len[k + 1] + len[k + 2] + len[k + 3] + len[k + 4]);
        const float u = total / 7.f;
        if (u < 0.6f * m || u > 2.f * m) continue; // 1.41 m on the diagonal
        bool ok = std::abs(len[k + 2] - 3 * u) < 1.5f * u;
        for (size_t q : {k, k + 1, k + 3, k + 4}) ok = ok && std::abs(len[q] - u) < 0.75f * u;
        if (ok) centres.push_back(start[k + 2] + len[k + 2] / 2.f);
    }
}

// QR: three finder patterns in the code area (binarised, module m px, n modules across or 0):
// row hits confirmed by a column hit, clustered, and three clusters at a right angle with equal
// legs (n - 7 modules when n is known).
bool hasQrFinders(const cv::Mat& bin, float m, int n)
{
    std::vector<cv::Point2f> rowHits;
    std::vector<uint8_t> line;
    std::vector<float> c;
    for (int y = 0; y < bin.rows; ++y) {
        line.assign(bin.ptr<uint8_t>(y), bin.ptr<uint8_t>(y) + bin.cols);
        for (auto& v : line) v = v ? 1 : 0;
        c.clear();
        finderRuns(line, m, c);
        for (float x : c) rowHits.emplace_back(x, static_cast<float>(y));
    }
    if (rowHits.empty()) return false;
    std::vector<cv::Point2f> colHits;
    line.resize(static_cast<size_t>(bin.rows));
    for (int x = 0; x < bin.cols; ++x) {
        for (int y = 0; y < bin.rows; ++y) line[static_cast<size_t>(y)] = bin.at<uint8_t>(y, x) ? 1 : 0;
        c.clear();
        finderRuns(line, m, c);
        for (float y : c) colHits.emplace_back(static_cast<float>(x), y);
    }
    // a finder centre: a row hit with a column hit within a module; clustered within 3 modules
    cv::Mat colMap(bin.size(), CV_8U, cv::Scalar(0));
    const int reach = std::max(1, static_cast<int>(m));
    for (const auto& q : colHits) {
        const cv::Rect r(static_cast<int>(q.x) - reach, static_cast<int>(q.y) - reach, 2 * reach + 1, 2 * reach + 1);
        colMap(r & cv::Rect(0, 0, bin.cols, bin.rows)).setTo(1);
    }
    std::vector<cv::Point2f> sum;
    std::vector<int> count;
    for (const auto& r : rowHits) {
        if (!colMap.at<uint8_t>(static_cast<int>(r.y), std::min(bin.cols - 1, static_cast<int>(r.x)))) continue;
        size_t k = 0;
        for (; k < sum.size(); ++k)
            if (cv::norm(sum[k] / static_cast<float>(count[k]) - r) < 3.f * m) break;
        if (k == sum.size()) {
            sum.push_back(r);
            count.push_back(1);
        } else {
            sum[k] += r;
            ++count[k];
        }
    }
    std::vector<cv::Point2f> pk;
    for (size_t k = 0; k < sum.size(); ++k)
        if (count[k] >= 2) pk.push_back(sum[k] / static_cast<float>(count[k]));
    const float L = n >= 21 ? (n - 7) * m : 0.f;
    for (size_t a = 0; a < pk.size(); ++a)
        for (size_t b = 0; b < pk.size(); ++b)
            for (size_t cI = b + 1; cI < pk.size(); ++cI) {
                if (a == b || a == cI) continue;
                const cv::Point2f ab = pk[b] - pk[a], ac = pk[cI] - pk[a];
                const float la = static_cast<float>(cv::norm(ab)), lc = static_cast<float>(cv::norm(ac));
                if (la < 10 * m || lc < 10 * m || std::max(la, lc) > 1.25f * std::min(la, lc)) continue;
                if (L > 0.f && (la < 0.7f * L || la > 1.3f * L)) continue;
                if (std::abs(ab.dot(ac)) / (la * lc) > 0.25f) continue;
                return true;
            }
    return false;
}

// Data Matrix: two adjacent sides of the code rectangle solid in one colour (the L).
bool hasDmL(const cv::Mat& bin, const cv::RotatedRect& rr, float m)
{
    cv::Point2f c[4];
    rr.points(c);
    const cv::Point2f mid = rr.center;
    float solid[4];
    for (int sIdx = 0; sIdx < 4; ++sIdx) {
        const cv::Point2f a = c[sIdx], b = c[(sIdx + 1) % 4];
        const float len = static_cast<float>(cv::norm(b - a));
        int n = 0, dark = 0;
        for (float t = 0.1f; t <= 0.9f; t += 0.5f * m / std::max(len, 1.f)) {
            cv::Point2f p = a + (b - a) * t;
            p += (mid - p) * (0.5f * m / std::max(1.f, static_cast<float>(cv::norm(mid - p)))); // half a module in
            const int x = static_cast<int>(p.x), y = static_cast<int>(p.y);
            if (x < 0 || y < 0 || x >= bin.cols || y >= bin.rows) continue;
            ++n;
            dark += bin.at<uint8_t>(y, x) ? 1 : 0;
        }
        solid[sIdx] = n ? static_cast<float>(dark) / n : 0.5f;
    }
    for (int sIdx = 0; sIdx < 4; ++sIdx) {
        const float a = solid[sIdx], b = solid[(sIdx + 1) % 4];
        if ((a > 0.85f && b > 0.85f) || (a < 0.15f && b < 0.15f)) return true;
    }
    return false;
}

// The smallest rectangle around the points, any rotation (3 degree steps; OpenCV 5 moved
// minAreaRect to a module the engine does not link).
cv::RotatedRect smallestRect(const std::vector<cv::Point>& pts)
{
    float bestArea = 1e30f;
    cv::RotatedRect best;
    for (int deg = 0; deg < 90; deg += 3) {
        const float a = static_cast<float>(deg * CV_PI / 180.0), ca = std::cos(a), sa = std::sin(a);
        float u0 = 1e30f, u1 = -1e30f, v0 = 1e30f, v1 = -1e30f;
        for (const auto& p : pts) {
            const float u = p.x * ca + p.y * sa, v = -p.x * sa + p.y * ca;
            u0 = std::min(u0, u);
            u1 = std::max(u1, u);
            v0 = std::min(v0, v);
            v1 = std::max(v1, v);
        }
        const float area = (u1 - u0 + 1) * (v1 - v0 + 1);
        if (area < bestArea) {
            bestArea = area;
            const float uc = (u0 + u1) / 2, vc = (v0 + v1) / 2;
            best = cv::RotatedRect(cv::Point2f(uc * ca - vc * sa, uc * sa + vc * ca), cv::Size2f(u1 - u0 + 1, v1 - v0 + 1), static_cast<float>(deg));
        }
    }
    return best;
}

} // namespace

MissContext missContext(const Result& res, const cr_params& p, const StepStats* learned)
{
    MissContext ctx;
    ctx.format = p.format == CR_DATAMATRIX ? CR_DATAMATRIX : CR_QR;
    std::vector<float> mods, px;
    int dm = 0;
    for (const auto& c : res.codes) {
        // QR and Data Matrix only: a DotCode's dot grid is no guide to their module size
        if (c.modW < 10 || !(c.format & (CR_QR | CR_DATAMATRIX))) continue;
        float side = 0.f;
        for (int k = 0; k < 4; ++k) side += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
        mods.push_back(static_cast<float>(c.modW));
        px.push_back(side / 4.f / static_cast<float>(c.modW));
        dm += c.format == CR_DATAMATRIX ? 1 : 0;
    }
    if (!mods.empty()) {
        if (p.format == CR_ANY) ctx.format = 2 * dm > static_cast<int>(mods.size()) ? CR_DATAMATRIX : CR_QR;
        ctx.modules = static_cast<int>(median(mods));
        ctx.modulePx = median(px);
        return ctx;
    }
    if (learned) {
        if (ctx.format == CR_DATAMATRIX && learned->dmModules.load() > 0 && learned->dmSide.load() > 0.f) {
            ctx.modules = learned->dmModules.load();
            ctx.modulePx = learned->dmSide.load() / static_cast<float>(ctx.modules);
            return ctx;
        }
        const float lo = learned->codeMin.load(), hi = learned->codeMax.load();
        if (ctx.format == CR_QR && learned->qrModules.load() >= 21 && lo > 0.f && hi > 0.f) {
            ctx.modules = learned->qrModules.load();
            ctx.modulePx = std::sqrt(lo * hi) / static_cast<float>(ctx.modules);
            return ctx;
        }
    }
    if (p.hint_modules > 0 && p.hint_code_side_px > 0.f) {
        ctx.modules = p.hint_modules;
        ctx.modulePx = p.hint_code_side_px / static_cast<float>(p.hint_modules);
    }
    return ctx;
}

cr_miss_info diagnoseMiss(const cv::Mat& gray, const cv::Rect& box, float codeSide, const MissContext& ctx, float* measuredModule)
{
    if (measuredModule) *measuredModule = 0.f;
    cr_miss_info out{};
    out.x = box.x;
    out.y = box.y;
    out.w = box.width;
    out.h = box.height;
    out.finder = -1;
    out.quiet_zone = -1;
    out.learned_module_px = ctx.modulePx;
    out.ref_sharpness = ctx.refSharpness;
    out.ref_contrast = ctx.refContrast;
    const cv::Rect img(0, 0, gray.cols, gray.rows);

    // where the code should be: the AI's code side around the box centre, else the whole box. A
    // region much smaller than the product's codes is a fragment the AI saw of one (glare, a bend
    // or film hid the rest): it is measured over the product's code size, and its module size is
    // the product's, not its own side over the modules.
    const float productSide = ctx.modulePx > 0.f && ctx.modules > 0 ? ctx.modulePx * ctx.modules / ctx.aiToCode : 0.f;
    float side = codeSide > 0.f ? codeSide : static_cast<float>(std::min(box.width, box.height));
    // The module size: the product's when known (codes of one product are one size; the AI side
    // follows the label more than the code), else the AI side over the modules, else the texture.
    float modulePx = ctx.modulePx;
    if (productSide > 0.f && (codeSide <= 0.f || codeSide < 0.6f * productSide))
        side = codeSide > 0.f ? productSide : std::min(side, 1.2f * productSide);
    else if (modulePx <= 0.f && ctx.modules > 0 && codeSide > 0.f)
        modulePx = codeSide * ctx.aiToCode / static_cast<float>(ctx.modules);
    const cv::Point2f centre(box.x + box.width / 2.f, box.y + box.height / 2.f);
    const cv::Rect expect(static_cast<int>(centre.x - side / 2.f), static_cast<int>(centre.y - side / 2.f),
                          static_cast<int>(side), static_cast<int>(side));
    const cv::Rect area = (box | expect) & img;
    if (area.width < 8 || area.height < 8) {
        out.measured = 1;
        out.reason = CR_MISS_CUT_OFF;
        return out;
    }
    const cv::Mat g = gray(area);
    const cv::Rect inImage = expect & img;
    const float lost = 1.f - static_cast<float>(inImage.area()) / std::max(1.f, static_cast<float>(expect.area()));
    cv::Mat mask(g.size(), CV_8U, cv::Scalar(0));
    {
        const cv::Rect local = (expect - area.tl()) & cv::Rect(0, 0, g.cols, g.rows);
        if (local.area() > 0) mask(local).setTo(255);
        else mask.setTo(255);
    }

    // a first look at the levels, for the edge threshold of the code texture
    float light, dark, sat;
    levels(g, mask, light, dark, sat);
    cv::Mat smooth;
    cv::GaussianBlur(g, smooth, cv::Size(3, 3), 0.8);
    const cv::Mat magS = edgeMagnitude(smooth);
    // an edge: well above the pixel noise (Immerkaer's estimate, robust to the code's own
    // structure) and a tenth of the contrast, so a blurred code still shows its texture
    float noise;
    {
        const cv::Mat k = (cv::Mat_<float>(3, 3) << 1, -2, 1, -2, 4, -2, 1, -2, 1);
        cv::Mat lap;
        cv::filter2D(g, lap, CV_32F, k);
        noise = static_cast<float>(std::sqrt(CV_PI / 2.0) / 6.0 * cv::mean(cv::abs(lap))[0]);
    }
    const cv::Mat edges = magS > std::max({6.f, 2.f * noise, 0.1f * (light - dark)});
    {
        cv::Mat inside;
        cv::bitwise_and(edges, mask, inside);
        out.texture = static_cast<float>(cv::countNonZero(inside)) / std::max(1, cv::countNonZero(mask));
    }

    // the code's footprint: edges closed over about two modules, the largest blob in the region
    // (the code need not sit at the region's centre: the AI box can follow the label)
    const float mGuess = modulePx > 0.f ? modulePx : std::max(2.f, side / 25.f);
    cv::RotatedRect foot;
    bool haveFoot = false;
    {
        const int k = std::max(3, static_cast<int>(std::lround(2.f * mGuess)) | 1);
        cv::Mat closed, lab, stats, cents;
        cv::morphologyEx(edges, closed, cv::MORPH_CLOSE, cv::getStructuringElement(cv::MORPH_RECT, cv::Size(k, k)));
        const int n = cv::connectedComponentsWithStats(closed, lab, stats, cents, 8, CV_32S);
        int best = 0, bestArea = 0;
        for (int i = 1; i < n; ++i)
            if (stats.at<int>(i, cv::CC_STAT_AREA) > bestArea) {
                bestArea = stats.at<int>(i, cv::CC_STAT_AREA);
                best = i;
            }
        if (best > 0 && bestArea > 0.25f * side * side * ctx.aiToCode * ctx.aiToCode) {
            std::vector<std::vector<cv::Point>> contours;
            cv::findContours(cv::Mat(lab == best), contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
            std::vector<cv::Point> pts;
            for (const auto& c : contours) pts.insert(pts.end(), c.begin(), c.end());
            foot = smallestRect(pts);
            haveFoot = true;
            mask.setTo(0);
            cv::Point2f q[4];
            foot.points(q);
            std::vector<cv::Point> poly;
            for (const auto& p : q) poly.emplace_back(static_cast<int>(std::lround(p.x)), static_cast<int>(std::lround(p.y)));
            cv::fillConvexPoly(mask, poly, cv::Scalar(255));
        }
    }

    // the measures on the code area (US9552506)
    levels(g, mask, light, dark, sat);
    out.light = light;
    out.dark = dark;
    out.contrast = light - dark;
    out.saturated = sat;
    const float edge = strongestEdges(edgeMagnitude(g), mask);
    out.sharpness = out.contrast > 1.f ? std::min(1.f, edge / out.contrast) : 0.f;

    // module size: the AI side over the product's modules, else from the texture
    // binarised against the local mean over a few modules (uneven light), 255 = dark
    cv::Mat bin;
    {
        const int block = std::max(5, static_cast<int>(std::lround(6.f * mGuess)) | 1);
        cv::adaptiveThreshold(smooth, bin, 255, cv::ADAPTIVE_THRESH_MEAN_C, cv::THRESH_BINARY_INV, block, 0.1 * (light - dark));
    }
    if (modulePx > 0.f) out.module_px = modulePx;
    else if (haveFoot) out.module_px = modulesFromTransitions(bin, mask);
    if (measuredModule && haveFoot) {
        // against the local mean with no contrast offset, so a blurred or faint code keeps its transitions
        cv::Mat bin0;
        const int block = std::max(5, static_cast<int>(std::lround(6.f * mGuess)) | 1);
        cv::adaptiveThreshold(smooth, bin0, 255, cv::ADAPTIVE_THRESH_MEAN_C, cv::THRESH_BINARY_INV, block, 0.0);
        *measuredModule = modulesFromTransitions(bin0, mask);
    }
    const float m = out.module_px > 0.f ? out.module_px : mGuess;

    // cut off: the code runs off the image (its expected square, or its footprint at the border)
    bool cut = lost > 0.15f;
    if (!cut && haveFoot) {
        cv::Point2f q[4];
        foot.points(q);
        for (const auto& p : q) {
            const float fx = p.x + area.x, fy = p.y + area.y;
            if (fx < 1.f || fy < 1.f || fx > gray.cols - 2.f || fy > gray.rows - 2.f) cut = true;
        }
    }

    // finder and quiet zone, on the footprint
    if (haveFoot && m >= 1.2f) {
        if (ctx.format == CR_DATAMATRIX) out.finder = hasDmL(bin, foot, m) ? 1 : 0;
        else {
out.finder = hasQrFinders(bin, m, ctx.modules) ? 1 : 0;
        }
        // ring from half a module to two modules (QR) / one and a half (DM) outside the footprint
        const float qz = ctx.format == CR_DATAMATRIX ? 1.5f : 2.f;
        cv::Mat outer(g.size(), CV_8U, cv::Scalar(0)), inner(g.size(), CV_8U, cv::Scalar(0));
        auto fillRect = [](cv::Mat& dst, cv::RotatedRect r) {
            cv::Point2f q[4];
            r.points(q);
            std::vector<cv::Point> poly;
            for (const auto& p : q) poly.emplace_back(static_cast<int>(std::lround(p.x)), static_cast<int>(std::lround(p.y)));
            cv::fillConvexPoly(dst, poly, cv::Scalar(255));
        };
        fillRect(outer, cv::RotatedRect(foot.center, foot.size + cv::Size2f(2 * qz * m, 2 * qz * m), foot.angle));
        fillRect(inner, cv::RotatedRect(foot.center, foot.size + cv::Size2f(m, m), foot.angle));
        cv::Mat ring;
        cv::bitwise_and(outer, ~inner, ring);
        const int nRing = cv::countNonZero(ring);
        if (nRing > 0) {
            cv::Mat darkRing;
            cv::bitwise_and(bin, ring, darkRing);
            out.quiet_zone = static_cast<float>(cv::countNonZero(darkRing)) / static_cast<float>(nRing) < 0.2f ? 1 : 0;
        }
    }

    // the reason: the first that holds. Contrast and sharpness against this image's read codes
    // when there are any (a dark line reads at 15 grey levels; read codes measure at least 0.46 of
    // the image's median contrast and 0.64 of its median sharpness on the samples).
    const float lowContrast = ctx.refContrast > 0.f ? std::max(kMinContrast, kContrastVsRead * ctx.refContrast) : kLowContrast;
    const float blurred = ctx.refSharpness > 0.f ? kSharpnessVsRead * ctx.refSharpness : kBlurSharpness;
    if (cut) out.reason = CR_MISS_CUT_OFF;
    else if (out.saturated >= kGlareShare) out.reason = CR_MISS_GLARE;
    else if (out.contrast < lowContrast) out.reason = CR_MISS_LOW_CONTRAST;
    else if (out.texture < kMinTexture) // no code texture: very soft edges = blurred away, else covered
        out.reason = out.sharpness < kNoTextureBlur * blurred ? CR_MISS_BLUR : CR_MISS_COVERED;
    else if (out.module_px > 0.f && out.module_px < kTooSmallPx) out.reason = CR_MISS_TOO_SMALL;
    else if (out.sharpness < blurred) out.reason = CR_MISS_BLUR;
    else if (out.finder == 0) out.reason = CR_MISS_NO_FINDER;
    else out.reason = CR_MISS_UNKNOWN;
    out.measured = 1;
    return out;
}

bool missHasEvidence(const cr_miss_info& d, const MissContext& ctx, float measuredModule)
{
    if (d.finder == 1) return true;
    if (d.texture < kMinTexture || measuredModule <= 0.f) return false;
    if (ctx.modulePx <= 0.f) return true;
    if (measuredModule < kEvidenceModuleLow * ctx.modulePx) return false; // grain finer than a module: wrap or noise
    // the module size from the transitions holds for a sharp code; a blurred code has few left, so
    // a dense texture (read codes: 0.14 and up) counts too
    return measuredModule <= kEvidenceModuleHigh * ctx.modulePx || d.texture >= kCodeTexture;
}

// Leaves out of the miss list the regions whose code was read in another region (a read code's
// centre within half the region's code side of the region's centre), and the LOW_CONTRAST / BLUR
// ones without code evidence (background). A region not measured (time cap) stays.
static void dropNonCodes(Result& res, const MissContext& ctx, const std::vector<float>& measuredModule)
{
    std::vector<cv::Point2f> readAt;
    for (const Code& c : res.codes) {
        cv::Point2f m(0.f, 0.f);
        for (int k = 0; k < 4; ++k) m += cv::Point2f(c.x[k], c.y[k]) * 0.25f;
        readAt.push_back(m);
    }
    const bool sides = res.missSide.size() == res.misses.size();
    const bool slots = res.missSlot.size() == res.misses.size();
    size_t keep = 0;
    for (size_t i = 0; i < res.misses.size(); ++i) {
        const cv::Rect& b = res.misses[i];
        const cr_miss_info& d = res.missInfo[i];
        const float side = sides && res.missSide[i] > 0.f ? res.missSide[i] : static_cast<float>(std::min(b.width, b.height));
        const cv::Point2f at(b.x + b.width / 2.f, b.y + b.height / 2.f);
        bool drop = false;
        for (const cv::Point2f& m : readAt)
            if (std::hypot(m.x - at.x, m.y - at.y) <= 0.5f * side) drop = true;
        if (!drop && d.measured && (d.reason == CR_MISS_LOW_CONTRAST || d.reason == CR_MISS_BLUR) && !missHasEvidence(d, ctx, measuredModule[i]))
            drop = true;
        if (drop) continue;
        if (keep != i) {
            res.misses[keep] = res.misses[i];
            res.missInfo[keep] = res.missInfo[i];
            if (sides) res.missSide[keep] = res.missSide[i];
            if (slots) res.missSlot[keep] = res.missSlot[i];
        }
        ++keep;
    }
    res.misses.resize(keep);
    res.missInfo.resize(keep);
    if (sides) res.missSide.resize(keep);
    if (slots) res.missSlot.resize(keep);
}

void diagnoseMisses(const cv::Mat& gray, Result& res, const MissContext& ctxIn, double budgetMs)
{
    const auto t0 = Clock::now();
    auto ms = [&]() { return std::chrono::duration<double, std::milli>(Clock::now() - t0).count(); };
    const bool dump = std::getenv("CR_DIAG_ALL") != nullptr; // calibration: every read code too, with times
    MissContext ctx = ctxIn;
    if (res.aiToCode > 0.f) ctx.aiToCode = res.aiToCode;
    // the reference: the same measures on a few of this image's read codes
    {
        MissContext own = ctx;
        own.aiToCode = 1.f;
        std::vector<float> sh, co;
        const size_t step = std::max<size_t>(1, res.codes.size() / 6);
        for (size_t i = 0; i < res.codes.size() && (dump || sh.size() < 6); i += dump ? 1 : step) {
            const Code& c = res.codes[i];
            float sd = 0.f, x0 = 1e9f, y0 = 1e9f, x1 = -1e9f, y1 = -1e9f;
            for (int k = 0; k < 4; ++k) {
                sd += std::hypot(c.x[(k + 1) % 4] - c.x[k], c.y[(k + 1) % 4] - c.y[k]);
                x0 = std::min(x0, c.x[k]);
                y0 = std::min(y0, c.y[k]);
                x1 = std::max(x1, c.x[k]);
                y1 = std::max(y1, c.y[k]);
            }
            sd /= 4.f;
            const float pad = c.modW > 0 ? 3.f * sd / static_cast<float>(c.modW) : 0.1f * sd;
            const cv::Rect box(static_cast<int>(x0 - pad), static_cast<int>(y0 - pad), static_cast<int>(x1 - x0 + 2 * pad),
                               static_cast<int>(y1 - y0 + 2 * pad));
            const auto tc = Clock::now();
            const cr_miss_info d = diagnoseMiss(gray, box, sd, own);
            if (dump)
                std::fprintf(stderr, "diag read %d %d %dx%d reason %d sharp %.2f contrast %.1f light %.0f sat %.3f texture %.3f module %.2f finder %d qz %d %.2f ms\n",
                             box.x, box.y, box.width, box.height, d.reason, d.sharpness, d.contrast, d.light, d.saturated, d.texture,
                             d.module_px, d.finder, d.quiet_zone, std::chrono::duration<double, std::milli>(Clock::now() - tc).count());
            if (d.contrast > 0.f) {
                sh.push_back(d.sharpness);
                co.push_back(d.contrast);
            }
        }
        ctx.refSharpness = median(sh);
        ctx.refContrast = median(co);
    }
    // the misses in parallel (a case can leave dozens of regions unread), each started only while
    // the cap lasts; the rest stay measured = 0, UNKNOWN
    res.missInfo.assign(res.misses.size(), cr_miss_info{});
    std::vector<float> measuredModule(res.misses.size(), 0.f);
    const bool sides = res.missSide.size() == res.misses.size();
    std::atomic<size_t> next{0};
    cv::parallel_for_(cv::Range(0, static_cast<int>(std::min<size_t>(res.misses.size(), 16))), [&](const cv::Range& workers) {
        for (int w = workers.start; w < workers.end; ++w)
            for (;;) {
                const size_t i = next.fetch_add(1);
                if (i >= res.misses.size()) return;
                const cv::Rect& b = res.misses[i];
                cr_miss_info& d = res.missInfo[i];
                if (!dump && ms() > budgetMs) {
                    d.x = b.x;
                    d.y = b.y;
                    d.w = b.width;
                    d.h = b.height;
                    d.finder = d.quiet_zone = -1;
                    d.learned_module_px = ctx.modulePx;
                    d.ref_sharpness = ctx.refSharpness;
                    d.ref_contrast = ctx.refContrast;
                    continue;
                }
                d = diagnoseMiss(gray, b, sides ? res.missSide[i] : 0.f, ctx, &measuredModule[i]);
            }
    });
    if (dump)
        for (const auto& d : res.missInfo)
            std::fprintf(stderr, "diag miss %d %d %dx%d reason %d sharp %.2f contrast %.1f light %.0f sat %.3f texture %.3f module %.2f finder %d qz %d 0 ms\n",
                         d.x, d.y, d.w, d.h, d.reason, d.sharpness, d.contrast, d.light, d.saturated, d.texture, d.module_px, d.finder, d.quiet_zone);
    if (dump) std::fprintf(stderr, "diag total %.1f ms, ai to code %.2f, module %.2f px, ref sharp %.2f contrast %.1f\n", ms(), ctx.aiToCode, ctx.modulePx, ctx.refSharpness, ctx.refContrast);
    // calibration dump: the lines above are the list before this; the list itself is what is kept
    dropNonCodes(res, ctx, measuredModule);
}

} // namespace cr
