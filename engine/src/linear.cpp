// linear.cpp - 1D case codes at any angle, and SSCCs past the point where their bar edges are gone.
//
// zxing scans image rows (and a 90 degree turn), so a label tilted 25-45 degrees is not read at
// all, and it measures bar edges, so defocus over about 0.6 modules or motion over about 5 px
// leaves it nothing to measure. Here (prototypes in ml/experiments: band1d.py, gen1d.py):
//  1. bands: the gradient structure tensor; bars give strong gradients all in one direction. The
//     largest coherent blobs are the codes, their gradient direction the scan direction.
//  2. a profile across the bars: many scan lines along the band, fused by the median (scratches,
//     spots and noise drop out), drawn as a clean upright row for zxing.
//  3. an SSCC's layout is fixed (start C, FNC1, 10 digit pairs, check, stop = 156 modules): its
//     geometry and blur come from the parts every SSCC shares, by matched filters; each digit
//     pair is the value whose rendering through that blur fits the pixels best. The read stands
//     only when those per-position best values already satisfy both the mod-103 check and the
//     GS1 check digit: the checks confirm, never choose (letting them change even one symbol gave
//     wrong reads at the blur limit; two checks over 18 digits leave many valid numbers).
#include "reader.hpp"
#include "zxformats.hpp"

#include <ZXing/ReadBarcode.h>

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <numeric>
#include <set>
#include <tuple>

namespace cr {

namespace {

// Code 128 bar/space widths of values 0..105 (bar first); the stop is 13 modules
constexpr std::array<std::array<uint8_t, 6>, 106> kC128 = {{
    {2,1,2,2,2,2},{2,2,2,1,2,2},{2,2,2,2,2,1},{1,2,1,2,2,3},{1,2,1,3,2,2},{1,3,1,2,2,2},{1,2,2,2,1,3},{1,2,2,3,1,2},
    {1,3,2,2,1,2},{2,2,1,2,1,3},{2,2,1,3,1,2},{2,3,1,2,1,2},{1,1,2,2,3,2},{1,2,2,1,3,2},{1,2,2,2,3,1},{1,1,3,2,2,2},
    {1,2,3,1,2,2},{1,2,3,2,2,1},{2,2,3,2,1,1},{2,2,1,1,3,2},{2,2,1,2,3,1},{2,1,3,2,1,2},{2,2,3,1,1,2},{3,1,2,1,3,1},
    {3,1,1,2,2,2},{3,2,1,1,2,2},{3,2,1,2,2,1},{3,1,2,2,1,2},{3,2,2,1,1,2},{3,2,2,2,1,1},{2,1,2,1,2,3},{2,1,2,3,2,1},
    {2,3,2,1,2,1},{1,1,1,3,2,3},{1,3,1,1,2,3},{1,3,1,3,2,1},{1,1,2,3,1,3},{1,3,2,1,1,3},{1,3,2,3,1,1},{2,1,1,3,1,3},
    {2,3,1,1,1,3},{2,3,1,3,1,1},{1,1,2,1,3,3},{1,1,2,3,3,1},{1,3,2,1,3,1},{1,1,3,1,2,3},{1,1,3,3,2,1},{1,3,3,1,2,1},
    {3,1,3,1,2,1},{2,1,1,3,3,1},{2,3,1,1,3,1},{2,1,3,1,1,3},{2,1,3,3,1,1},{2,1,3,1,3,1},{3,1,1,1,2,3},{3,1,1,3,2,1},
    {3,3,1,1,2,1},{3,1,2,1,1,3},{3,1,2,3,1,1},{3,3,2,1,1,1},{3,1,4,1,1,1},{2,2,1,4,1,1},{4,3,1,1,1,1},{1,1,1,2,2,4},
    {1,1,1,4,2,2},{1,2,1,1,2,4},{1,2,1,4,2,1},{1,4,1,1,2,2},{1,4,1,2,2,1},{1,1,2,2,1,4},{1,1,2,4,1,2},{1,2,2,1,1,4},
    {1,2,2,4,1,1},{1,4,2,1,1,2},{1,4,2,2,1,1},{2,4,1,2,1,1},{2,2,1,1,1,4},{4,1,3,1,1,1},{2,4,1,1,1,2},{1,3,4,1,1,1},
    {1,1,1,2,4,2},{1,2,1,1,4,2},{1,2,1,2,4,1},{1,1,4,2,1,2},{1,2,4,1,1,2},{1,2,4,2,1,1},{4,1,1,2,1,2},{4,2,1,1,1,2},
    {4,2,1,2,1,1},{2,1,2,1,4,1},{2,1,4,1,2,1},{4,1,2,1,2,1},{1,1,1,1,4,3},{1,1,1,3,4,1},{1,3,1,1,4,1},{1,1,4,1,1,3},
    {1,1,4,3,1,1},{4,1,1,1,1,3},{4,1,1,3,1,1},{1,1,3,1,4,1},{1,1,4,1,3,1},{3,1,1,1,4,1},{4,1,1,1,3,1},{2,1,1,4,1,2},
    {2,1,1,2,1,4},{2,1,1,2,3,2}}};
constexpr std::array<uint8_t, 7> kStop = {2, 3, 3, 1, 1, 1, 2};
constexpr int kStartC = 105, kFnc1 = 102, kSsccModules = 156;
constexpr int kSub = 4; // profile samples per pixel
constexpr float kSigmas[] = {0.5f, 1.0f, 1.6f, 2.3f, 3.0f, 3.8f, 4.8f};  // px; the last is the grid's edge
constexpr float kMotions[] = {0.f, 3.f, 6.f, 9.f, 12.f, 16.f};

void appendModules(std::vector<float>& m, int value)
{
    const auto& w = kC128[static_cast<size_t>(value)];
    for (int i = 0; i < 6; ++i) m.insert(m.end(), w[static_cast<size_t>(i)], i % 2 == 0 ? 1.f : 0.f);
}

void appendStop(std::vector<float>& m)
{
    for (int i = 0; i < 7; ++i) m.insert(m.end(), kStop[static_cast<size_t>(i)], i % 2 == 0 ? 1.f : 0.f);
}

// PSF sampled every profile sample: a Gaussian of sigma (samples) through a box of `motion` samples
std::vector<float> psfKernel(float sigma, float motion)
{
    const int r = static_cast<int>(std::ceil(3 * sigma + motion / 2 + 1));
    std::vector<float> g(static_cast<size_t>(2 * r + 1));
    for (int i = -r; i <= r; ++i) g[static_cast<size_t>(i + r)] = std::exp(-0.5f * (i / std::max(sigma, 1e-3f)) * (i / std::max(sigma, 1e-3f)));
    if (motion > 1.f) {
        std::vector<float> b(g.size(), 0.f);
        for (int i = -r; i <= r; ++i) {
            float s = 0.f;
            for (int k = -r; k <= r; ++k)
                if (std::abs(i - k) <= motion / 2 && k + r >= 0 && k + r < static_cast<int>(g.size())) s += g[static_cast<size_t>(k + r)];
            b[static_cast<size_t>(i + r)] = s;
        }
        g = b;
    }
    const float sum = std::accumulate(g.begin(), g.end(), 0.f);
    for (auto& v : g) v /= sum;
    return g;
}

// darkness of a module string starting at x0 (samples), m samples per module, over samples
// [a, b), blurred by kern; rendered over the window widened by the kernel radius
std::vector<float> render(const std::vector<float>& mods, double x0, double m, int a, int b, const std::vector<float>& kern)
{
    const int r = static_cast<int>(kern.size()) / 2;
    std::vector<float> d(static_cast<size_t>(b - a + 2 * r));
    for (int i = 0; i < static_cast<int>(d.size()); ++i) {
        const double x = a - r + i;
        const long k = static_cast<long>(std::floor((x - x0) / m));
        d[static_cast<size_t>(i)] = k >= 0 && k < static_cast<long>(mods.size()) ? mods[static_cast<size_t>(k)] : 0.f;
    }
    std::vector<float> out(static_cast<size_t>(b - a), 0.f);
    for (int i = 0; i < b - a; ++i) {
        float s = 0.f;
        for (size_t k = 0; k < kern.size(); ++k) s += d[static_cast<size_t>(i) + k] * kern[k];
        out[static_cast<size_t>(i)] = s;
    }
    return out;
}

// obs ~ a + slope * model by least squares: residual sum of squares, and the slope (bars darker: < 0)
double fitLevels(const std::vector<float>& model, const float* obs, double* slope)
{
    const size_t n = model.size();
    double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
    for (size_t i = 0; i < n; ++i) {
        const double x = model[i], y = obs[i];
        sx += x; sy += y; sxx += x * x; sxy += x * y; syy += y * y;
    }
    const double vx = sxx - sx * sx / static_cast<double>(n);
    if (n < 4 || vx <= 1e-9) { *slope = 0; return 1e30; }
    const double b = (sxy - sx * sy / static_cast<double>(n)) / vx, a = (sy - b * sx) / static_cast<double>(n);
    *slope = b;
    return std::max(0.0, syy - 2 * b * sxy - 2 * a * sy + b * b * sxx + 2 * a * b * sx + a * a * static_cast<double>(n));
}

char gs1CheckOk(const std::string& body18)
{
    int s = 0;
    for (int i = 0; i < 17; ++i) s += (body18[static_cast<size_t>(i)] - '0') * (i % 2 == 0 ? 3 : 1);
    return (s + (body18[17] - '0')) % 10 == 0;
}

} // namespace

std::vector<uint8_t> ssccModules(const std::string& digits)
{
    if (digits.size() != 20 || digits.find_first_not_of("0123456789") != std::string::npos) return {};
    std::vector<int> vals = {kStartC, kFnc1};
    for (size_t i = 0; i < 20; i += 2) vals.push_back((digits[i] - '0') * 10 + (digits[i + 1] - '0'));
    int sum = vals[0];
    for (size_t i = 1; i < vals.size(); ++i) sum += static_cast<int>(i) * vals[i];
    vals.push_back(sum % 103);
    std::vector<float> m;
    for (const int v : vals) appendModules(m, v);
    appendStop(m);
    return std::vector<uint8_t>(m.begin(), m.end());
}

std::vector<LinearBand> findLinearBandsAt(const cv::Mat& gray, double f, int maxBands)
{
    std::vector<LinearBand> out;
    {
        cv::Mat s;
        if (f != 1.0) cv::resize(gray, s, cv::Size(), f, f, cv::INTER_AREA);
        else s = gray;
        cv::Mat g, gx, gy;
        s.convertTo(g, CV_32F);
        cv::Sobel(g, gx, CV_32F, 1, 0, 3);
        cv::Sobel(g, gy, CV_32F, 0, 1, 3);
        const double sig = f < 1.0 ? 3.5 : 3.0;
        cv::Mat jxx, jyy, jxy;
        cv::GaussianBlur(gx.mul(gx), jxx, cv::Size(), sig);
        cv::GaussianBlur(gy.mul(gy), jyy, cv::Size(), sig);
        cv::GaussianBlur(gx.mul(gy), jxy, cv::Size(), sig);
        cv::Mat tr = jxx + jyy, diff = jxx - jyy, det;
        cv::sqrt(diff.mul(diff) + 4 * jxy.mul(jxy), det);
        cv::Mat coh = det / (tr + 1e-6), energy;
        cv::sqrt(tr, energy);
        std::vector<float> ev(energy.begin<float>(), energy.end<float>());
        const size_t k99 = ev.size() * 99 / 100;
        std::nth_element(ev.begin(), ev.begin() + static_cast<long>(k99), ev.end());
        const float thr = std::max(ev[k99] * 0.25f, 1e-3f);
        cv::Mat mask = (coh > 0.6) & (energy > thr);
        cv::morphologyEx(mask, mask, cv::MORPH_CLOSE, cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(5, 5)));
        cv::Mat lab, stats, cents;
        const int n = cv::connectedComponentsWithStats(mask, lab, stats, cents);
        std::vector<int> order;
        for (int i = 1; i < n; ++i)
            if (stats.at<int>(i, cv::CC_STAT_AREA) >= 200 * f * f) order.push_back(i);
        std::sort(order.begin(), order.end(), [&](int a, int b) { return stats.at<int>(a, cv::CC_STAT_AREA) > stats.at<int>(b, cv::CC_STAT_AREA); });
        if (order.size() > static_cast<size_t>(maxBands)) order.resize(static_cast<size_t>(maxBands));
        for (const int id : order) {
            double sxx = 0, syy = 0, sxy = 0;
            std::vector<cv::Point2f> pts;
            const cv::Rect bb(stats.at<int>(id, cv::CC_STAT_LEFT), stats.at<int>(id, cv::CC_STAT_TOP), stats.at<int>(id, cv::CC_STAT_WIDTH), stats.at<int>(id, cv::CC_STAT_HEIGHT));
            for (int y = bb.y; y < bb.y + bb.height; ++y)
                for (int x = bb.x; x < bb.x + bb.width; ++x)
                    if (lab.at<int>(y, x) == id) {
                        sxx += jxx.at<float>(y, x); syy += jyy.at<float>(y, x); sxy += jxy.at<float>(y, x);
                        pts.emplace_back(static_cast<float>(x / f), static_cast<float>(y / f));
                    }
            const double theta = 0.5 * std::atan2(2 * sxy, sxx - syy);
            const cv::Point2f d(static_cast<float>(std::cos(theta)), static_cast<float>(std::sin(theta))), e(-d.y, d.x);
            cv::Point2f c(0, 0);
            for (const auto& p : pts) c += p;
            c *= 1.f / static_cast<float>(pts.size());
            std::vector<float> u, v;
            for (const auto& p : pts) { u.push_back((p - c).dot(d)); v.push_back((p - c).dot(e)); }
            const auto pct = [](std::vector<float> a, double q) {
                const size_t k = std::min(a.size() - 1, static_cast<size_t>(q * static_cast<double>(a.size() - 1)));
                std::nth_element(a.begin(), a.begin() + static_cast<long>(k), a.end());
                return a[k];
            };
            const float u0 = pct(u, 0.005), u1 = pct(u, 0.995), v0 = pct(v, 0.03), v1 = pct(v, 0.97);
            LinearBand b;
            b.centre = c + d * ((u0 + u1) / 2) + e * ((v0 + v1) / 2);
            b.dir = d;
            b.length = u1 - u0;
            b.height = v1 - v0;
            // a band of bars is long across them and at least a few times its own module tall
            if (b.length < 40.f || b.height < 8.f) continue;
            bool dup = false;
            for (const auto& o : out) dup = dup || cv::norm(o.centre - b.centre) < 0.3f * std::max(o.length, b.length);
            if (!dup) out.push_back(b);
        }
    }
    return out;
}

std::vector<LinearBand> findLinearBands(const cv::Mat& gray, int maxBands)
{
    auto out = findLinearBandsAt(gray, 0.5, maxBands);
    if (out.empty()) out = findLinearBandsAt(gray, 1.0, maxBands); // the finer scale only when the coarse one finds nothing
    return out;
}

std::vector<float> linearProfile(const cv::Mat& gray, const LinearBand& b, int lines)
{
    const cv::Point2f e(-b.dir.y, b.dir.x);
    const float half = b.length / 2 + 0.1f * b.length + 10.f; // the quiet zones
    const int n = static_cast<int>(2 * half * kSub);
    cv::Mat mx(lines, n, CV_32F), my(lines, n, CV_32F);
    for (int l = 0; l < lines; ++l) {
        const float off = (lines > 1 ? -0.3f + 0.6f * l / (lines - 1) : 0.f) * b.height;
        for (int i = 0; i < n; ++i) {
            const float t = -half + static_cast<float>(i) / kSub;
            mx.at<float>(l, i) = b.centre.x + t * b.dir.x + off * e.x;
            my.at<float>(l, i) = b.centre.y + t * b.dir.y + off * e.y;
        }
    }
    cv::Mat g, rows;
    gray.convertTo(g, CV_32F);
    cv::remap(g, rows, mx, my, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    std::vector<float> p(static_cast<size_t>(n)), col(static_cast<size_t>(lines));
    for (int i = 0; i < n; ++i) {
        for (int l = 0; l < lines; ++l) col[static_cast<size_t>(l)] = rows.at<float>(l, i);
        std::nth_element(col.begin(), col.begin() + lines / 2, col.end());
        p[static_cast<size_t>(i)] = col[static_cast<size_t>(lines / 2)];
    }
    return p;
}

cv::Mat profileImage(const std::vector<float>& p, int height)
{
    std::vector<float> s = p;
    std::sort(s.begin(), s.end());
    const float lo = s[s.size() / 100], hi = s[s.size() * 99 / 100];
    cv::Mat row(1, static_cast<int>(p.size()), CV_8U);
    for (size_t i = 0; i < p.size(); ++i)
        row.at<uint8_t>(0, static_cast<int>(i)) = cv::saturate_cast<uint8_t>((p[i] - lo) * 255.f / std::max(4.f, hi - lo));
    cv::Mat img;
    cv::repeat(row, height, 1, img);
    return img;
}

std::string decodeSsccProfile(const std::vector<float>& p, float mPrior, std::string* why)
{
    const auto fail = [&](const char* w) { if (why) *why = w; return std::string(); };
    const int n = static_cast<int>(p.size());
    if (n < 64) return fail("profile too short");
    const cv::Mat prof(1, n, CV_32F, const_cast<float*>(p.data()));
    std::vector<float> head = {0.f, 0.f}, tail;
    appendModules(head, kStartC);
    appendModules(head, kFnc1);
    appendStop(tail);
    tail.push_back(0.f);
    tail.push_back(0.f);
    // geometry: the start + FNC1 and the stop templates, 143 modules apart, by matched filters
    double best = -1e9, x0 = 0, m = 0;
    float sig = 0, mot = 0;
    const double mGuess = mPrior / 1.17; // the band reaches past the code by about 17%
    // coarse to fine: module size in 2% steps over every blur (4% drifts 6 modules over the code
    // and misses the peak), then 0.5% steps over the blurs next to the coarse best
    std::vector<std::tuple<double, float, float>> grid;
    for (int mi = 0; mi <= 20; ++mi)
        for (const float s : kSigmas)
            for (const float mo : kMotions) grid.emplace_back(mGuess * (0.8 + 0.02 * mi), s, mo);
    for (int pass = 0; pass < 2; ++pass) {
        if (pass == 1) {
            const double mc = m;
            const float sc = sig, mc2 = mot;
            grid.clear();
            for (int mi = -3; mi <= 3; ++mi)
                for (const float s : kSigmas)
                    for (const float mo : kMotions)
                        if (std::abs(s - sc) <= 0.9f && std::abs(mo - mc2) <= 3.5f) grid.emplace_back(mc * (1 + 0.005 * mi), s, mo);
        }
        for (const auto& [mm, s, mo] : grid) {
            if (mm < 2.0) continue;
            {
                const auto k = psfKernel(s * kSub, mo * kSub);
                const int lh = static_cast<int>(head.size() * mm), lt = static_cast<int>(tail.size() * mm);
                if (lh >= n || lt >= n) continue;
                const std::vector<float> rh = render(head, 0, mm, 0, lh, k), rt = render(tail, 0, mm, 0, lt, k);
                cv::Mat th(1, lh, CV_32F), tt(1, lt, CV_32F);
                for (int i = 0; i < lh; ++i) th.at<float>(0, i) = 1.f - rh[static_cast<size_t>(i)];
                for (int i = 0; i < lt; ++i) tt.at<float>(0, i) = 1.f - rt[static_cast<size_t>(i)];
                cv::Mat ch, ct;
                cv::matchTemplate(prof, th, ch, cv::TM_CCOEFF_NORMED);
                cv::matchTemplate(prof, tt, ct, cv::TM_CCOEFF_NORMED);
                const int shift = static_cast<int>(std::lround(145 * mm)); // 2 quiet + 143 modules
                const int cnt = std::min(ch.cols, ct.cols - shift);
                for (int x = 0; x < cnt; ++x) {
                    const double sc = ch.at<float>(0, x) + ct.at<float>(0, x + shift);
                    if (sc > best) { best = sc; x0 = x + 2 * mm; m = mm; sig = s; mot = mo; }
                }
            }
        }
    }
    if (best < 1.6) return fail("the fixed parts do not match");
    if (sig >= kSigmas[std::size(kSigmas) - 1] || mot >= kMotions[std::size(kMotions) - 1]) return fail("blur beyond the model");
    const auto k = psfKernel(sig * kSub, mot * kSub);
    double noise = 0;
    for (int i = 1; i < n; ++i) noise += (p[static_cast<size_t>(i)] - p[static_cast<size_t>(i - 1)]) * (p[static_cast<size_t>(i)] - p[static_cast<size_t>(i - 1)]);
    noise = noise / (n - 1) / 2 + 1e-3;
    // each position's best value: the symbol always ends with a space and the next starts with a bar
    std::array<int, 11> val{};
    for (int j = 0; j < 11; ++j) {
        const double sx = x0 + (22 + 11 * j) * m;
        const int a = static_cast<int>(std::ceil(sx - m)), b = static_cast<int>(std::ceil(sx + 12 * m));
        if (a < 0 || b > n || b - a < 8) return fail("code outside the profile");
        double bestCost = 1e30;
        int bestV = -1;
        for (int v = 0; v < (j == 10 ? 103 : 100); ++v) {
            std::vector<float> mods = {0.f};
            appendModules(mods, v);
            mods.push_back(1.f);
            double slope = 0;
            const double c = fitLevels(render(mods, sx - m, m, a, b, k), p.data() + a, &slope) / (2 * noise);
            if (slope < 0 && c < bestCost) { bestCost = c; bestV = v; }
        }
        if (bestV < 0) return fail("a symbol fits no value");
        val[static_cast<size_t>(j)] = bestV;
    }
    if (val[0] != 0) return fail("not an SSCC (AI 00)");
    int sum = kStartC + kFnc1;
    for (int j = 0; j < 10; ++j) sum += (j + 2) * val[static_cast<size_t>(j)];
    if (sum % 103 != val[10]) return fail("the check symbol does not confirm the digits");
    std::string text;
    for (int j = 0; j < 10; ++j) {
        text += static_cast<char>('0' + val[static_cast<size_t>(j)] / 10);
        text += static_cast<char>('0' + val[static_cast<size_t>(j)] % 10);
    }
    if (!gs1CheckOk(text.substr(2))) return fail("the GS1 check digit does not confirm the digits");
    return text;
}

namespace {

// the type's quiet zones in modules (ISO/IEC 15417, 15420, 16388, 16390, 15438 and the AIM Code 93
// spec): before the symbol, after it
std::pair<float, float> quietZones(int format, bool addon)
{
    float before = 10.f, after = 10.f;
    if (format & CR_EAN13) { before = 11.f; after = 7.f; }
    else if (format & CR_EAN8) before = after = 7.f;
    else if (format & CR_UPCA) before = after = 9.f;
    else if (format & CR_UPCE) { before = 9.f; after = 7.f; }
    // the corners then span the add-on (7-12 modules after the main symbol), whose own zone is 5
    if (addon) after = 5.f;
    return {before, after};
}

// a 1D code's scan axis from a read's corners, as the image has it. Passes give corners a module or
// so apart at the ends, and some span the bars' whole height while others a strip of a few rows; so
// the corners only say roughly where the code lies: the axis runs from the start corners' middle to
// the end's, moved to the middle of the bars as found in the image; lo, hi: the bars' extent across
// it (from the corners' middle row). False when the code has too few pixels to tell a module
struct BarsAxis {
    cv::Mat g; // the image: 8-bit as the camera gives it (sampled in place, no copy), else float
    cv::Point2f s, e, u, v;
    float len = 0.f, lo = 0.f, hi = 0.f;
    bool found = false; // the extent was measured
    float px(int y, int x) const { return g.type() == CV_8U ? static_cast<float>(g.at<uint8_t>(y, x)) : g.at<float>(y, x); }
    bool sample(cv::Point2f a, float& out) const
    {
        if (a.x < 0 || a.y < 0 || a.x > g.cols - 1.001f || a.y > g.rows - 1.001f) return false;
        const int x0 = static_cast<int>(a.x), y0 = static_cast<int>(a.y);
        const float fx = a.x - x0, fy = a.y - y0;
        out = (1 - fy) * ((1 - fx) * px(y0, x0) + fx * px(y0, x0 + 1)) + fy * ((1 - fx) * px(y0 + 1, x0) + fx * px(y0 + 1, x0 + 1));
        return true;
    }
};

bool barsAxis(const cv::Mat& gray, const Code& c, BarsAxis& a)
{
    const cv::Point2f q0(c.x[0], c.y[0]), q1(c.x[1], c.y[1]), q2(c.x[2], c.y[2]), q3(c.x[3], c.y[3]);
    a.s = (q0 + q3) * 0.5f;
    a.e = (q1 + q2) * 0.5f;
    a.len = static_cast<float>(cv::norm(a.e - a.s));
    if (a.len < 24.f) return false; // too few pixels to tell a module
    const float len = a.len;
    a.u = (a.e - a.s) / len;
    a.v = cv::Point2f(-a.u.y, a.u.x);
    if (gray.type() == CV_8U) a.g = gray;
    else gray.convertTo(a.g, CV_32F);
    // the bars' extent across the scan direction: the rows (2 px apart, sampled every 2 px) whose
    // profile over the middle 80% of the symbol still matches the corners' middle row; 3 rows in a row
    // that do not end it
    const int m = static_cast<int>(0.4f * len);
    const auto rowAt = [&](float o, std::vector<float>& r) {
        r.resize(static_cast<size_t>(m));
        for (int i = 0; i < m; ++i)
            if (!a.sample(a.s + a.u * (0.1f * len + 2.f * i) + a.v * o, r[static_cast<size_t>(i)])) return false;
        return true;
    };
    const auto corr = [](const std::vector<float>& x, const std::vector<float>& y) {
        double mx = 0, my = 0;
        for (size_t i = 0; i < x.size(); ++i) { mx += x[i]; my += y[i]; }
        mx /= x.size(); my /= y.size();
        double xy = 0, xx = 0, yy = 0;
        for (size_t i = 0; i < x.size(); ++i) {
            xy += (x[i] - mx) * (y[i] - my); xx += (x[i] - mx) * (x[i] - mx); yy += (y[i] - my) * (y[i] - my);
        }
        return xx > 0 && yy > 0 ? xy / std::sqrt(xx * yy) : 0.0;
    };
    std::vector<float> ref, row;
    if (rowAt(0.f, ref)) {
        for (const int dir : {-1, +1}) {
            int miss = 0;
            for (int o = 2 * dir; std::abs(o) <= static_cast<int>(len) && miss < 3; o += 2 * dir) {
                if (!rowAt(static_cast<float>(o), row) || corr(ref, row) < 0.6) { ++miss; continue; }
                miss = 0;
                (dir < 0 ? a.lo : a.hi) = static_cast<float>(o);
            }
        }
        a.s += a.v * (0.5f * (a.lo + a.hi));
        a.e += a.v * (0.5f * (a.lo + a.hi));
        a.found = true;
    }
    return true;
}

} // namespace

int quietZoneOk(const cv::Mat& gray, const Code& c)
{
    // DataBar needs no quiet zone; 2D and stacked types carry their own measures (cr_quality)
    if (!(c.format & CR_ALL_LINEAR) || (c.format & CR_DATABAR) || gray.empty()) return -1;
    // The verdict must follow the pixels, not the pass that read the code (line safety: the same image
    // gives the same OK / NG). Passes give corners a module or so apart at the ends, and some span the
    // bars' whole height while others a strip of a few rows; so the corners only say roughly where the
    // code lies: the scan lines go through the middle of the bars as found in the image, and each zone
    // is measured from the end bar's own edge.
    BarsAxis ax;
    if (!barsAxis(gray, c, ax)) return -1;
    const float len = ax.len;
    const cv::Point2f s = ax.s, u = ax.u, v = ax.v;
    const auto sample = [&](cv::Point2f p, float& out) { return ax.sample(p, out); };
    float spread = 0.02f * len; // between the scan lines
    if (ax.found) spread = std::clamp(0.1f * (ax.hi - ax.lo), 1.f, spread); // no wider apart than before: a mark a few modules tall still shows on 3 lines
    // the median of 5 scan lines through the middle 40% of the bars, 2 samples per pixel, reaching 60%
    // of the length past both ends (over 14 modules for the shortest symbols); off the image = unknown
    constexpr float kStep = 0.5f;
    const float ext = 0.6f * len;
    const int n = static_cast<int>((len + 2 * ext) / kStep);
    std::vector<float> prof(static_cast<size_t>(n));
    std::vector<uint8_t> inside(static_cast<size_t>(n));
    for (int i = 0; i < n; ++i) {
        const cv::Point2f p = s + u * (-ext + i * kStep);
        std::array<float, 5> col{};
        bool in = true;
        for (int k = 0; k < 5 && in; ++k) in = sample(p + v * ((k - 2) * spread), col[static_cast<size_t>(k)]);
        inside[static_cast<size_t>(i)] = in;
        if (in) {
            std::nth_element(col.begin(), col.begin() + 2, col.end());
            prof[static_cast<size_t>(i)] = col[2];
        }
    }
    const auto at = [&](float t) { return std::clamp(static_cast<int>(std::lround((t + ext) / kStep)), 0, n - 1); };
    // levels from the symbol itself: threshold midway between its bars and spaces
    std::vector<float> sym;
    for (int i = at(0.f); i <= at(len); ++i)
        if (inside[static_cast<size_t>(i)]) sym.push_back(prof[static_cast<size_t>(i)]);
    if (sym.size() < 20) return 0; // the symbol itself is off the image
    std::sort(sym.begin(), sym.end());
    const float dark = sym[sym.size() / 10], light = sym[sym.size() * 9 / 10], thr = 0.5f * (dark + light);
    if (light - dark < 10.f) return -1;
    // the element edges inside the symbol give the module (X)
    std::vector<float> edges;
    for (int i = at(0.01f * len) + 1; i <= at(0.99f * len); ++i) {
        const float a = prof[static_cast<size_t>(i - 1)], b = prof[static_cast<size_t>(i)];
        if ((a < thr) != (b < thr) && inside[static_cast<size_t>(i)] && inside[static_cast<size_t>(i - 1)])
            edges.push_back(-ext + (i - 1 + (thr - a) / (b - a)) * kStep);
    }
    const float X = moduleFromEdges(edges);
    if (X <= 0.f) return -1;
    // the bars' side of the threshold (dark bars, or light ones on a dark print): from the decoder that
    // read the code. Never from the zone alone: a code with no quiet zone on a darker carton would
    // take the carton for its spaces. Not known: the end bars at the corners and the ground 1.5 to 4 X
    // past both ends must agree, else the zone is in doubt
    bool darkBars = c.lightOnDark != 1;
    if (c.lightOnDark < 0) {
        std::vector<float> back;
        for (const auto& [t0, t1] : {std::pair{-4.f * X, -1.5f * X}, std::pair{len + 1.5f * X, len + 4.f * X}})
            for (int i = at(t0); i <= at(t1); ++i)
                if (inside[static_cast<size_t>(i)]) back.push_back(prof[static_cast<size_t>(i)]);
        if (back.empty()) return 0; // both zones off the image
        std::nth_element(back.begin(), back.begin() + back.size() / 2, back.end());
        darkBars = prof[static_cast<size_t>(at(0.5f * X))] + prof[static_cast<size_t>(at(len - 0.5f * X))] < 2 * thr;
        if (darkBars != (back[back.size() / 2] >= thr)) return 0;
    }
    const auto isBar = [&](int i) { return (prof[static_cast<size_t>(i)] < thr) == darkBars; };
    const auto tOf = [&](float i) { return -ext + i * kStep; };
    // the bars' outer edges (bar to space, going away from the symbol) within `reach` X of the corner
    const auto outerEdges = [&](float tCorner, int dir, float reach) {
        std::vector<float> out;
        for (int i = at(tCorner - dir * reach * X), last = at(tCorner + dir * reach * X); i != last; i += dir) {
            const size_t a = static_cast<size_t>(i), b = static_cast<size_t>(i + dir);
            if (inside[a] && inside[b] && isBar(i) && !isBar(i + dir)) out.push_back(tOf(static_cast<float>(i) + 0.5f * dir));
        }
        return out;
    };
    const auto [before, after] = quietZones(c.format, !c.addon.empty());
    // the zone from half a module past the end bar's outer edge: a mark of half a module or more on the
    // bars' side, or the zone off the image, fails
    const auto zoneClear = [&](float edge, int dir, float modules) {
        const int from = at(edge + dir * 0.5f * X), to = at(edge + dir * modules * X);
        if ((to - from) * dir < 1 || std::abs(tOf(static_cast<float>(to)) - edge) < (modules - 0.6f) * X) return false; // past the profile
        const int minRun = std::max(1, static_cast<int>(std::ceil(0.5f * X / kStep)));
        int run = 0;
        for (int k = from; k != to + dir; k += dir) {
            if (!inside[static_cast<size_t>(k)]) return false;
            run = isBar(k) ? run + 1 : 0;
            if (run >= minRun) return false;
        }
        return true;
    };
    // The ends are the start and end edges with a bar count the type can have between them, whichever
    // pass gave the corners (a corner a module short of a guard bar wider than its module, or of a
    // start or stop pattern whose second bar is 2 X in, sits between two bar edges). EAN / UPC: a
    // fixed count. The others: a count in steps of their characters' bars (Code 39 5 a character,
    // Codabar 4, ITF 5 a digit pair and 4 in its start and stop, Code 93 3 and its termination bar,
    // Code 128 3 and 4 in its stop); within 1.5 X of the corners the candidate ends change the count
    // by 2 bars at most, never by a whole step. Every such pair must have clear zones: a second pair
    // only exists with a mark beside an end, and then one of its zones holds a bar (or, for a mark
    // touching the end bar, the end bar is wider than it is printed)
    struct BarCount { int step, rest, least; };
    const BarCount count = !c.addon.empty()                     ? BarCount{0, 0, 0}
                           : (c.format & (CR_EAN13 | CR_UPCA)) ? BarCount{0, 30, 30}
                           : (c.format & CR_EAN8)              ? BarCount{0, 22, 22}
                           : (c.format & CR_UPCE)              ? BarCount{0, 17, 17}
                           : (c.format & CR_CODE39)            ? BarCount{5, 0, 15}
                           : (c.format & CR_CODABAR)           ? BarCount{4, 0, 12}
                           : (c.format & CR_ITF)               ? BarCount{5, 4, 9}
                           : (c.format & CR_CODE93)            ? BarCount{3, 1, 16}
                           : (c.format & CR_GS1_128)           ? BarCount{3, 1, 13}
                                                               : BarCount{0, 0, 0};
    const bool fixedCount = count.step == 0;
    const auto countFits = [&](int bars) {
        return count.least > 0 && bars >= count.least && (fixedCount ? bars == count.rest : bars % count.step == count.rest);
    };
    if (count.least > 0) {
        // within 1.5 X of the corners: a mark touching the end bar and over 1.5 X wide is not taken for
        // the bar (and a narrower one widens the end bar)
        const std::vector<float> starts = outerEdges(0.f, -1, 1.5f), ends = outerEdges(len, +1, 1.5f);
        // bars between two edges: runs on the bars' side, a run or gap under 0.25 X taken as noise
        const int minRun = std::max(1, static_cast<int>(std::lround(0.25f * X / kStep)));
        const auto barsBetween = [&](float t0, float t1, std::vector<int>* widths = nullptr) {
            int bars = 0, barRun = 0, gapRun = minRun;
            bool counted = false;
            const auto close = [&] {
                if (counted && widths) widths->push_back(barRun);
            };
            for (int i = at(t0); i <= at(t1); ++i) {
                if (isBar(i)) {
                    ++barRun;
                    if (barRun >= minRun && !counted) { ++bars; counted = true; }
                    gapRun = 0;
                } else if (++gapRun >= minRun) {
                    if (gapRun == minRun) close();
                    barRun = 0;
                    counted = false;
                }
            }
            close();
            return bars;
        };
        // a run of samples on one side of the threshold, from sample i going against dir
        const auto runEnd = [&](int i, bool bar, int dir) {
            while (i - dir >= 0 && i - dir < n && inside[static_cast<size_t>(i)] && isBar(i) == bar) i -= dir;
            return i;
        };
        // EAN / UPC: the guard's end bar no wider than its other bar (both one module): a mark touching
        // the end bar widens it
        const auto guardEven = [&](float edge, int dir) {
            const int i0 = at(edge - dir * 0.5f * kStep), i1 = runEnd(i0, true, dir), i2 = runEnd(i1, false, dir), i3 = runEnd(i2, true, dir);
            return static_cast<float>(std::abs(i1 - i0) - std::abs(i3 - i2)) * kStep < 0.5f * X;
        };
        // each end's first three elements (bar, space, bar from the end edge inward) as the type's start
        // or stop pattern prints them, after print gain (bars wider by g, spaces narrower: g from the
        // symbol's narrow bars against X, which print gain does not change): k modules within half a
        // module, kWide a two-width code's wide element (over 1.5 X, under 3.5 X). A mark touching the
        // end bar widens it; a mark past the end bar taken for it (the symbol a bar short, as when blur
        // closes a space) has the zone's light for its space and the end bar for its next bar, neither
        // as printed. Empty: not known (Codabar without its start / stop letters)
        constexpr int kWide = -1;
        using Pattern = std::vector<int>;
        const auto patternOk = [&](float edge, int dir, const Pattern& pat, float g) {
            int i = at(edge - dir * 0.5f * kStep);
            bool bar = true;
            for (const int k : pat) {
                const int j = runEnd(i, bar, dir);
                const float w = static_cast<float>(std::abs(j - i)) * kStep + (bar ? -g : g);
                if (k == kWide ? (w <= 1.5f * X || w >= 3.5f * X) : std::abs(w - static_cast<float>(k) * X) >= 0.5f * X) return false;
                i = j;
                bar = !bar;
            }
            return true;
        };
        const auto codabarEnd = [&](char letter, bool start) -> Pattern {
            const char L = static_cast<char>(std::toupper(static_cast<unsigned char>(letter)));
            if (start) return L == 'A' ? Pattern{1, 1, kWide} : L == 'B' ? Pattern{1, kWide, 1} : L == 'C' || L == 'D' ? Pattern{1, 1, 1} : Pattern{};
            return L == 'A' ? Pattern{1, kWide, 1} : L == 'B' ? Pattern{kWide, 1, 1} : L == 'C' ? Pattern{kWide, kWide, 1} : L == 'D' ? Pattern{1, kWide, kWide} : Pattern{};
        };
        std::pair<Pattern, Pattern> endPatterns{{1, 1, 1}, {1, 1, 1}}; // EAN / UPC guards, ITF's start, Code 93's start
        if (c.format & CR_CODE39) endPatterns = {{1, kWide, 1}, {1, 1, kWide}};
        else if (c.format & CR_ITF) endPatterns.second = {1, 1, kWide};
        else if (c.format & CR_CODE93) endPatterns.second = {1, 1, 4}; // its termination bar, then the stop's last space and bar
        else if (c.format & CR_GS1_128) endPatterns = {{2, 1, 1}, {2, 1, 1}};
        else if (c.format & CR_CODABAR) endPatterns = {c.text.empty() ? Pattern{} : codabarEnd(c.text.front(), true),
                                                       c.text.empty() ? Pattern{} : codabarEnd(c.text.back(), false)};
        bool found = false, clear = true;
        for (const float l : starts)
            for (const float r : ends) {
                std::vector<int> widths;
                if (!countFits(barsBetween(l, r, &widths))) continue;
                found = true;
                // the narrow bar: the symbol's narrowest bars (a fifth of them or more are narrow in every
                // one of these types)
                std::sort(widths.begin(), widths.end());
                const float g = static_cast<float>(widths[widths.size() / 5]) * kStep - X;
                // an end known only by its count: the one edge within reach of its corner
                const bool startKnown = !endPatterns.first.empty() || starts.size() == 1, endKnown = !endPatterns.second.empty() || ends.size() == 1;
                const bool ends1 = startKnown && endKnown && (!fixedCount || (guardEven(l, -1) && guardEven(r, +1))) &&
                                   patternOk(l, -1, endPatterns.first, g) && patternOk(r, +1, endPatterns.second, g);
                clear = clear && ends1 && zoneClear(l, -1, before) && zoneClear(r, +1, after);
            }
        if (found) return clear ? 1 : 0;
    }
    // types with no bar count here (an EAN / UPC add-on), and symbols whose bars do not count up (as
    // when blur closes a narrow space): the one bar edge within 1.5 X of each corner. The bar before
    // the end bar ends 2 X or more inside it, so a corner up to half a module off finds that edge
    // alone; two edges there (a mark just past the end bar, or a corner a module off, where a mark's
    // edge and the symbol's could not be told apart) leave the end in doubt, and the zone fails
    const std::vector<float> starts = outerEdges(0.f, -1, 1.5f), ends = outerEdges(len, +1, 1.5f);
    if (starts.size() != 1 || ends.size() != 1) return 0;
    return zoneClear(starts[0], -1, before) && zoneClear(ends[0], +1, after) ? 1 : 0;
}

bool eanReadHolds(const cv::Mat& gray, const Code& c, int* addonHolds)
{
    if (addonHolds) *addonHolds = 0;
    const int format = c.format & (CR_EAN13 | CR_UPCA | CR_EAN8 | CR_UPCE);
    if (!format || gray.empty()) return true; // not an EAN / UPC
    BarsAxis ax;
    if (!barsAxis(gray, c, ax)) return false;
    const size_t nEdges = format == CR_EAN8 ? 44 : format == CR_UPCE ? 34 : 60;
    const float modules = format == CR_EAN8 ? 67.f : format == CR_UPCE ? 51.f : 95.f;
    // the add-on, when asked: its digits after the main symbol (the corners are the main symbol's)
    const int addonDigits = addonHolds ? static_cast<int>(c.addon.size()) : 0;
    const bool wantAddon = addonDigits == 2 || addonDigits == 5;
    // scan lines across the bars' height (the middle 80% of it), each the median of 3 lines 1 px apart,
    // 2 samples per pixel, a third of the length past both ends (a corner a module or more off); with
    // an add-on asked for, also its gap and length past both ends (the corners may run either way)
    constexpr float kStep = 0.5f;
    const float ext = 0.3f * ax.len + (wantAddon ? ax.len * (14.f + (addonDigits == 2 ? 21.f : 48.f)) / modules : 0.f);
    const float h = ax.found ? ax.hi - ax.lo : 0.f;
    const int n = static_cast<int>((ax.len + 2 * ext) / kStep);
    std::set<std::string> texts, addons;
    bool same = false, addonSame = false;
    for (int line = 0; line < 9; ++line) {
        const float off = (static_cast<float>(line) / 8.f - 0.5f) * 0.8f * h;
        // the line clipped to the image: the samples inside it around the symbol
        std::vector<float> prof(static_cast<size_t>(n));
        std::vector<uint8_t> inside(static_cast<size_t>(n));
        for (int i = 0; i < n; ++i) {
            std::array<float, 3> col{};
            bool in = true;
            for (int k = 0; k < 3 && in; ++k)
                in = ax.sample(ax.s + ax.u * (-ext + i * kStep) + ax.v * (off + static_cast<float>(k - 1)), col[static_cast<size_t>(k)]);
            inside[static_cast<size_t>(i)] = in;
            if (!in) continue;
            std::sort(col.begin(), col.end());
            prof[static_cast<size_t>(i)] = col[1];
        }
        const int sym0 = static_cast<int>(ext / kStep), sym1 = std::min(n - 1, static_cast<int>((ext + ax.len) / kStep));
        if (!std::all_of(inside.begin() + sym0, inside.begin() + sym1 + 1, [](uint8_t v) { return v != 0; })) continue; // the symbol leaves the image
        int from = sym0, to = sym1;
        while (from > 0 && inside[static_cast<size_t>(from - 1)]) --from;
        while (to + 1 < n && inside[static_cast<size_t>(to + 1)]) ++to;
        std::vector<float> sym(prof.begin() + sym0, prof.begin() + sym1 + 1);
        std::sort(sym.begin(), sym.end());
        const float thr = 0.5f * (sym[sym.size() / 10] + sym[sym.size() * 9 / 10]);
        // both polarities when the read did not say which; both directions (a read's corners may run
        // either way)
        for (const bool darkBars : {true, false}) {
            if ((c.lightOnDark == 1 && darkBars) || (c.lightOnDark == 0 && !darkBars)) continue;
            std::vector<float> edges;   // all, at the threshold
            std::vector<bool> intoBar; // the edge starts a bar going forward
            for (int i = from + 1; i <= to; ++i) {
                const float a = prof[static_cast<size_t>(i - 1)], b = prof[static_cast<size_t>(i)];
                if ((a < thr) != (b < thr)) {
                    edges.push_back((static_cast<float>(i - 1) + (thr - a) / (b - a)) * kStep);
                    intoBar.push_back((b < thr) == darkBars);
                }
            }
            for (const bool forward : {true, false}) {
                std::vector<float> e = edges;
                std::vector<bool> into = intoBar;
                if (!forward) {
                    std::reverse(e.begin(), e.end());
                    for (float& t : e) t = -t;
                    std::reverse(into.begin(), into.end());
                    into.flip(); // going the other way a bar's trailing edge starts it
                }
                for (size_t i = 0; i + nEdges <= e.size(); ++i) {
                    if (!into[i]) continue;
                    const std::string t = eanReferenceDecode(std::vector<float>(e.begin() + static_cast<long>(i), e.begin() + static_cast<long>(i + nEdges)), format);
                    if (t.empty()) continue;
                    if (eanGtin(format, t) != eanGtin(format, c.text)) {
                        texts.insert(t);
                        continue;
                    }
                    same = true;
                    if (!wantAddon) continue;
                    // the add-on's guard starts 7 to 12 modules past the main symbol's last bar (a
                    // module either way for print gain and blur)
                    const size_t last = i + nEdges - 1;
                    const float m = (e[last] - e[i]) / modules;
                    const size_t na = static_cast<size_t>(6 * addonDigits + 2);
                    for (size_t j = last + 1; j + na <= e.size() && e[j] - e[last] <= 13.f * m; ++j) {
                        if (!into[j] || e[j] - e[last] < 6.f * m) continue;
                        const std::string a = eanAddonReferenceDecode(std::vector<float>(e.begin() + static_cast<long>(j), e.begin() + static_cast<long>(j + na)), addonDigits);
                        if (a.empty()) continue;
                        if (a == c.addon) addonSame = true;
                        else addons.insert(a);
                    }
                }
            }
        }
    }
    const bool holds = same && texts.empty();
    if (addonHolds) *addonHolds = holds && addonSame && addons.empty() ? 1 : 0;
    return holds;
}

void finishLinear(const cv::Mat& gray, std::vector<Code>& codes, int checkDigit)
{
    for (Code& c : codes) {
        float a = static_cast<float>(std::atan2(c.y[1] - c.y[0], c.x[1] - c.x[0]) * 180.0 / CV_PI);
        if (a < 0.f) a += 360.f;
        c.angle = a;
        c.checkDigitOk = (c.format & CR_ALL_LINEAR) ? checkDigitState(c.format, checkDigit) : -1;
        c.quietZoneOk = quietZoneOk(gray, c);
        if (c.format & (CR_PHARMACODE | CR_PHARMACODE_REV)) c.quietZoneOk = 1; // its decoder reads none without 4 gaps clear
    }
}

std::vector<LinearRead> readLinear(const cv::Mat& gray, int formats, int maxCodes, std::chrono::steady_clock::time_point deadline,
                                   const std::function<bool(int, std::string)>& accept)
{
    std::vector<LinearRead> out;
    ZXing::ReaderOptions o;
    formats &= CR_ALL_LINEAR;
    if (!formats) return out;
    o.setFormats(toZx(formats));
    o.setTextMode(ZXing::TextMode::Plain); // as every other pass: GS1 separators stay ASCII 29
    o.setTryRotate(false);
    o.setTryHarder(true);
    o.setMaxNumberOfSymbols(1);
    o.setEanAddOnSymbol(ZXing::EanAddOnSymbol::Read); // reported apart (LinearRead::addon)
    for (const auto& b : findLinearBands(gray, std::max(3, 2 * maxCodes))) {
        if (static_cast<int>(out.size()) >= maxCodes || std::chrono::steady_clock::now() >= deadline) break;
        const std::vector<float> p = linearProfile(gray, b, 24);
        LinearRead r;
        const cv::Point2f e(-b.dir.y, b.dir.x);
        const cv::Point2f du = b.dir * (b.length / 2), dv = e * (b.height / 2);
        r.quad[0] = b.centre - du - dv; r.quad[1] = b.centre + du - dv; r.quad[2] = b.centre + du + dv; r.quad[3] = b.centre - du + dv;
        const auto zx = ZXing::ReadBarcodes(ZXing::ImageView(profileImage(p, 24).data, static_cast<int>(p.size()), 24, ZXing::ImageFormat::Lum), o);
        if (!zx.empty() && zx[0].isValid() && (fromZx(zx[0].format()) & formats)) {
            r.text = mainText(zx[0], &r.addon);
            r.symbologyId = zx[0].symbologyIdentifier();
            r.lightOnDark = zx[0].isInverted() ? 1 : 0;
            r.gs1 = zx[0].contentType() == ZXing::ContentType::GS1 ? 1 : 0;
            // read right to left on the profile: corner 0 at the symbol's start, as zxing's own reads
            if (std::abs(zx[0].orientation()) > 90) {
                std::swap(r.quad[0], r.quad[1]);
                std::swap(r.quad[2], r.quad[3]);
            }
            r.format = fromZx(zx[0].format());
            r.step = CR_STEP_LINEAR;
        } else if (formats & CR_GS1_128) {
            const float mPrior = b.length * kSub / kSsccModules;
            std::vector<float> rev(p.rbegin(), p.rend());
            std::string t = decodeSsccProfile(p, mPrior, nullptr);
            if (t.empty()) t = decodeSsccProfile(rev, mPrior, nullptr);
            if (t.empty()) continue;
            r.text = t;
            // the fit found Start C and FNC1 in their places, and the check symbol, which counts the
            // FNC1, confirmed them: a GS1-128
            r.symbologyId = "]C1";
            r.gs1 = 1;
            r.format = CR_GS1_128;
            r.step = CR_STEP_LINEAR_SSCC;
        } else {
            continue;
        }
        if (accept && !accept(r.format, r.text)) continue;
        // an EAN / UPC read that does not hold on the image (Collector::eanHolds would drop it) does not
        // use up maxCodes either
        if (r.format & (CR_EAN13 | CR_UPCA | CR_EAN8 | CR_UPCE)) {
            Code c;
            c.format = r.format;
            c.text = r.text;
            c.lightOnDark = r.lightOnDark;
            for (int i = 0; i < 4; ++i) {
                c.x[i] = r.quad[i].x;
                c.y[i] = r.quad[i].y;
            }
            if (!eanReadHolds(gray, c)) continue;
        }
        bool dup = false;
        for (const auto& o2 : out) dup = dup || o2.text == r.text;
        if (!dup) out.push_back(r);
    }
    return out;
}

} // namespace cr
