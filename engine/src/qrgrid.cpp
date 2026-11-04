// qrgrid.cpp - QR module-grid helpers shared by the grid fit (gridfit.cpp) and candidate
// matching (match.cpp): own finder-pattern search, the code square from three finders, and
// the mean brightness of every module under a known grid.

#include "reader.hpp"

#include <array>

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>

namespace cr {

cv::Mat homography4(const cv::Point2f s[4], const cv::Point2f d[4]); // quality.cpp

cv::Mat moduleMeans(const cv::Mat& img32, const cv::Point2f quad[4], int n)
{
    const cv::Point2f src[4] = {{0, 0}, {float(n), 0}, {float(n), float(n)}, {0, float(n)}};
    const cv::Mat H = homography4(src, quad);
    constexpr int sub = 3; // 3 x 3 samples in the central 60% of each module
    std::vector<cv::Point2f> pts, out;
    pts.reserve(static_cast<size_t>(n) * n * sub * sub);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x)
            for (int j = 0; j < sub; ++j)
                for (int i = 0; i < sub; ++i)
                    pts.emplace_back(x + 0.2f + 0.6f * (i + 0.5f) / sub, y + 0.2f + 0.6f * (j + 0.5f) / sub);
    cv::perspectiveTransform(pts, out, H);
    cv::Mat mapX(1, static_cast<int>(out.size()), CV_32F), mapY(1, static_cast<int>(out.size()), CV_32F);
    for (size_t k = 0; k < out.size(); ++k) {
        mapX.at<float>(0, static_cast<int>(k)) = out[k].x;
        mapY.at<float>(0, static_cast<int>(k)) = out[k].y;
    }
    cv::Mat vals;
    cv::remap(img32, vals, mapX, mapY, cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    cv::Mat means(n, n, CV_32F);
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            float s = 0;
            for (int k = 0; k < sub * sub; ++k) s += vals.at<float>(0, (y * n + x) * sub * sub + k);
            means.at<float>(y, x) = s / (sub * sub);
        }
    return means;
}

// QR fixed patterns for an n x n symbol: 1 dark, 0 light, -1 data
cv::Mat qrFixed(int n)
{
    cv::Mat f(n, n, CV_8S, cv::Scalar(-1));
    const int corners[3][2] = {{0, 0}, {n - 7, 0}, {0, n - 7}};
    for (const auto& c : corners)
        for (int y = -1; y <= 7; ++y)
            for (int x = -1; x <= 7; ++x) {
                const int xx = c[0] + x, yy = c[1] + y;
                if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
                const int d = std::max(std::abs(x - 3), std::abs(y - 3));
                f.at<int8_t>(yy, xx) = static_cast<int8_t>((d == 2 || d == 4) ? 0 : 1);
            }
    for (int i = 8; i < n - 8; ++i) {
        f.at<int8_t>(6, i) = static_cast<int8_t>(i % 2 == 0 ? 1 : 0);
        f.at<int8_t>(i, 6) = static_cast<int8_t>(i % 2 == 0 ? 1 : 0);
    }
    return f;
}

// ---- own finder-pattern detection for tiny, noisy QR (OpenCV's detectors need clean edges)
// Normalised correlation with a 7x7-module finder template (dark ring, light ring, dark centre,
// light margin) at the known module size, over rotations 0-80 degrees.
// Faster variant for candidate matching: 6 angles, and the local contrast normalised once over the
// template's square (the same for every angle) instead of per rotated mask.
cv::Mat finderResponseFast(const cv::Mat& img32, float m)
{
    const int s = static_cast<int>(std::ceil(9 * m * 1.5f)) | 1;
    const float c = s / 2.f;
    cv::Mat s1, s2;
    cv::boxFilter(img32, s1, CV_32F, cv::Size(s, s), cv::Point(-1, -1), true, cv::BORDER_REPLICATE);
    cv::boxFilter(img32.mul(img32), s2, CV_32F, cv::Size(s, s), cv::Point(-1, -1), true, cv::BORDER_REPLICATE);
    cv::Mat sd;
    cv::sqrt(cv::max(s2 - s1.mul(s1), 1e-3), sd);
    // matchTemplate's output is offset by half the template: crop the normaliser to match
    const int h = s / 2;
    const cv::Mat sdv = sd(cv::Rect(h, h, img32.cols - s + 1, img32.rows - s + 1));
    cv::Mat best;
    for (int ang = 0; ang < 90; ang += 15) {
        cv::Mat t(s, s, CV_32F, cv::Scalar(0)), mk(s, s, CV_32F, cv::Scalar(0));
        const float a = static_cast<float>(ang * CV_PI / 180.0), ca = std::cos(a), sa = std::sin(a);
        double sum = 0;
        int cnt = 0;
        for (int y = 0; y < s; ++y)
            for (int x = 0; x < s; ++x) {
                const float xx = x - c + 0.5f, yy = y - c + 0.5f;
                const float u = (xx * ca + yy * sa) / m, v = (-xx * sa + yy * ca) / m;
                const float d = std::max(std::abs(u), std::abs(v));
                if (d > 4.5f) continue;
                const float val = d <= 1.5f ? 0.f : d <= 2.5f ? 1.f : d <= 3.5f ? 0.f : 1.f;
                t.at<float>(y, x) = val;
                mk.at<float>(y, x) = 1.f;
                sum += val;
                ++cnt;
            }
        const float mean = static_cast<float>(sum / cnt);
        cv::Mat num;
        cv::matchTemplate(img32, (t - mean).mul(mk) / static_cast<float>(cnt), num, cv::TM_CCORR);
        cv::Mat r = num / (sdv + 1e-3);
        best = best.empty() ? r : cv::max(best, r);
    }
    return best;
}

cv::Mat finderResponse(const cv::Mat& img32, float m)
{
    cv::Mat best;
    const int s = static_cast<int>(std::ceil(9 * m * 1.5f)) | 1;
    const float c = s / 2.f;
    for (int ang = 0; ang < 90; ang += 10) {
        cv::Mat t(s, s, CV_32F, cv::Scalar(0)), mk(s, s, CV_32F, cv::Scalar(0));
        const float a = static_cast<float>(ang * CV_PI / 180.0), ca = std::cos(a), sa = std::sin(a);
        double sum = 0;
        int cnt = 0;
        for (int y = 0; y < s; ++y)
            for (int x = 0; x < s; ++x) {
                const float xx = x - c + 0.5f, yy = y - c + 0.5f;
                const float u = (xx * ca + yy * sa) / m, v = (-xx * sa + yy * ca) / m;
                const float d = std::max(std::abs(u), std::abs(v));
                if (d > 4.5f) continue;
                const float val = d <= 1.5f ? 0.f : d <= 2.5f ? 1.f : d <= 3.5f ? 0.f : 1.f;
                t.at<float>(y, x) = val;
                mk.at<float>(y, x) = 1.f;
                sum += val;
                ++cnt;
            }
        const float mean = static_cast<float>(sum / cnt);
        cv::Mat tz = (t - mean).mul(mk);
        cv::Mat num, s1, s2, img2 = img32.mul(img32);
        cv::matchTemplate(img32, tz, num, cv::TM_CCORR);
        cv::matchTemplate(img32, mk, s1, cv::TM_CCORR);
        cv::matchTemplate(img2, mk, s2, cv::TM_CCORR);
        cv::Mat var = s2 - s1.mul(s1) / cnt, sd;
        cv::sqrt(cv::max(var, 1e-3), sd);
        cv::Mat r = num / (sd + 1e-3);
        best = best.empty() ? r : cv::max(best, r);
    }
    return best;
}

// three finder peaks forming a right isosceles triangle with legs (n - 7) modules -> 4 corners
bool quadFromFinders(const cv::Mat& resp, float m, int n, int off, cv::Point2f quad[4])
{
    std::vector<cv::Point3f> pk; // x, y, score
    cv::Mat b = resp.clone();
    for (int k = 0; k < 8; ++k) {
        double mv;
        cv::Point ml;
        cv::minMaxLoc(b, nullptr, &mv, nullptr, &ml);
        pk.emplace_back(static_cast<float>(ml.x + off), static_cast<float>(ml.y + off), static_cast<float>(mv));
        cv::circle(b, ml, static_cast<int>(5 * m), cv::Scalar(-1e9), -1);
    }
    const float L = (n - 7) * m;
    float bestScore = -1e9f;
    bool found = false;
    const size_t K = std::min<size_t>(6, pk.size());
    for (size_t a = 0; a < K; ++a)
        for (size_t bI = 0; bI < K; ++bI)
            for (size_t cI = 0; cI < K; ++cI) {
                if (a == bI || a == cI || bI == cI) continue;
                const cv::Point2f A(pk[a].x, pk[a].y), B(pk[bI].x, pk[bI].y), C(pk[cI].x, pk[cI].y);
                const cv::Point2f ab = B - A, ac = C - A;
                const float la = static_cast<float>(cv::norm(ab)), lc = static_cast<float>(cv::norm(ac));
                if (la < 0.7f * L || la > 1.3f * L || lc < 0.7f * L || lc > 1.3f * L) continue;
                const float cosv = std::abs(ab.dot(ac)) / (la * lc);
                if (cosv > 0.25f || ab.x * ac.y - ab.y * ac.x < 0) continue;
                const float sc = pk[a].z + pk[bI].z + pk[cI].z - 2 * cosv - std::abs(la - lc) / L;
                if (sc > bestScore) {
                    bestScore = sc;
                    const cv::Point2f ex = ab * (1.f / (n - 7)), ey = ac * (1.f / (n - 7));
                    const cv::Point2f tl = A - ex * 3.5f - ey * 3.5f;
                    quad[0] = tl;
                    quad[1] = tl + ex * static_cast<float>(n);
                    quad[2] = tl + ex * static_cast<float>(n) + ey * static_cast<float>(n);
                    quad[3] = tl + ey * static_cast<float>(n);
                    found = true;
                }
            }
    return found;
}

std::vector<std::array<cv::Point2f, 4>> quadsFromFinders(const cv::Mat& resp, float m, int n, int off, size_t maxQuads)
{
    std::vector<cv::Point3f> pk; // x, y, score
    cv::Mat b = resp.clone();
    for (int k = 0; k < 12; ++k) {
        double mv;
        cv::Point ml;
        cv::minMaxLoc(b, nullptr, &mv, nullptr, &ml);
        if (mv <= 0) break;
        pk.emplace_back(static_cast<float>(ml.x + off), static_cast<float>(ml.y + off), static_cast<float>(mv));
        cv::circle(b, ml, static_cast<int>(5 * m), cv::Scalar(-1e9), -1);
    }
    const float L = (n - 7) * m;
    std::vector<std::pair<float, std::array<cv::Point2f, 4>>> all;
    std::vector<cv::Point2f> corner; // the corner finder of each, to keep one quad per code
    for (size_t a = 0; a < pk.size(); ++a)
        for (size_t bI = 0; bI < pk.size(); ++bI)
            for (size_t cI = 0; cI < pk.size(); ++cI) {
                if (a == bI || a == cI || bI == cI) continue;
                const cv::Point2f A(pk[a].x, pk[a].y), B(pk[bI].x, pk[bI].y), C(pk[cI].x, pk[cI].y);
                const cv::Point2f ab = B - A, ac = C - A;
                const float la = static_cast<float>(cv::norm(ab)), lc = static_cast<float>(cv::norm(ac));
                if (la < 0.7f * L || la > 1.3f * L || lc < 0.7f * L || lc > 1.3f * L) continue;
                const float cosv = std::abs(ab.dot(ac)) / (la * lc);
                if (cosv > 0.25f || ab.x * ac.y - ab.y * ac.x < 0) continue;
                const float sc = pk[a].z + pk[bI].z + pk[cI].z - 2 * cosv - std::abs(la - lc) / L;
                const cv::Point2f ex = ab * (1.f / (n - 7)), ey = ac * (1.f / (n - 7));
                const cv::Point2f tl = A - ex * 3.5f - ey * 3.5f;
                all.push_back({sc, {tl, tl + ex * static_cast<float>(n), tl + (ex + ey) * static_cast<float>(n), tl + ey * static_cast<float>(n)}});
            }
    std::sort(all.begin(), all.end(), [](const auto& x, const auto& y) { return x.first > y.first; });
    std::vector<std::array<cv::Point2f, 4>> out;
    std::vector<cv::Point2f> centres;
    for (const auto& q : all) {
        const cv::Point2f c = (q.second[0] + q.second[2]) * 0.5f;
        bool near = false;
        for (const auto& o : centres) near = near || cv::norm(c - o) < L * 0.5f;
        if (near) continue;
        centres.push_back(c);
        out.push_back(q.second);
        if (out.size() >= maxQuads) break;
    }
    return out;
}

} // namespace cr
