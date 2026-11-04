// locate.cpp - finds regions that look like 2D codes.
//
// A QR or Data Matrix code is a dense patch of short edges running in both
// directions. Text, box edges and glare give edges mostly in one direction or
// spread thinly, so "edge density in both directions" separates codes well,
// even on dark images, because edges are measured relative to local brightness.

#include "reader.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>

namespace cr {

namespace {

struct Candidate {
    cv::Rect rect;
    float score;
};

} // namespace

std::vector<cv::Rect> locate(const cv::Mat& gray, const cr_params& p, float* labelBrightness)
{
    const int S = std::max(1, p.locator_scale);
    cv::Mat small;
    cv::resize(gray, small, cv::Size(), 1.0 / S, 1.0 / S, cv::INTER_AREA);

    cv::Mat s;
    small.convertTo(s, CV_32F);

    if (labelBrightness) {
        // brightness of the brightest 5%: labels are the brightest thing in the case
        int hist[256] = {};
        for (int y = 0; y < small.rows; ++y) {
            const uint8_t* row = small.ptr<uint8_t>(y);
            for (int x = 0; x < small.cols; ++x) hist[row[x]]++;
        }
        const int64_t target = static_cast<int64_t>(small.total()) * 95 / 100;
        int64_t acc = 0;
        int v = 0;
        for (; v < 256; ++v) {
            acc += hist[v];
            if (acc >= target) break;
        }
        *labelBrightness = static_cast<float>(std::min(v, 255));
    }

    cv::Mat mean, gx, gy;
    cv::blur(s, mean, cv::Size(31, 31));
    cv::Sobel(s, gx, CV_32F, 1, 0);
    cv::Sobel(s, gy, CV_32F, 0, 1);
    gx = cv::abs(gx);
    gy = cv::abs(gy);

    // edge strength relative to local brightness, so dark images work too
    cv::Mat rel = (gx + gy) / (mean + 4.0f);

    // keep the strongest 10% of edges (threshold from a sampled histogram)
    std::vector<float> sample;
    sample.reserve(rel.total() / 16 + 1);
    for (int y = 0; y < rel.rows; y += 4) {
        const float* row = rel.ptr<float>(y);
        for (int x = 0; x < rel.cols; x += 4) sample.push_back(row[x]);
    }
    const size_t k = sample.size() * 9 / 10;
    std::nth_element(sample.begin(), sample.begin() + k, sample.end());
    const float edgeThresh = sample.empty() ? 0.f : sample[k];

    cv::Mat strong = rel > edgeThresh;
    cv::Mat horiz = (gx > gy) & strong;
    cv::Mat vert = (gy >= gx) & strong;

    cv::Mat ex, ey;
    horiz.convertTo(ex, CV_32F, 1.0 / 255);
    vert.convertTo(ey, CV_32F, 1.0 / 255);
    cv::blur(ex, ex, cv::Size(9, 9));
    cv::blur(ey, ey, cv::Size(9, 9));
    cv::Mat density = cv::min(ex, ey);

    cv::Mat mask = density > p.locator_density;
    cv::morphologyEx(mask, mask, cv::MORPH_CLOSE, cv::getStructuringElement(cv::MORPH_RECT, {5, 5}));
    cv::morphologyEx(mask, mask, cv::MORPH_OPEN, cv::getStructuringElement(cv::MORPH_RECT, {3, 3}));

    cv::Mat labels, stats, centroids;
    const int n = cv::connectedComponentsWithStats(mask, labels, stats, centroids, 8, CV_32S);

    std::vector<Candidate> cands;
    cands.reserve(n);
    for (int i = 1; i < n; ++i) {
        const int x = stats.at<int>(i, cv::CC_STAT_LEFT);
        const int y = stats.at<int>(i, cv::CC_STAT_TOP);
        const int w = stats.at<int>(i, cv::CC_STAT_WIDTH);
        const int h = stats.at<int>(i, cv::CC_STAT_HEIGHT);
        const int a = stats.at<int>(i, cv::CC_STAT_AREA);
        if (a < 40 || w > small.cols / 4 || h > small.rows / 4) continue;

        // score = mean density over the component, favouring square-ish blobs
        const float d = static_cast<float>(cv::mean(density(cv::Rect(x, y, w, h)), mask(cv::Rect(x, y, w, h)))[0]);
        const float aspect = static_cast<float>(std::min(w, h)) / std::max(w, h);
        const int pad = std::max(w, h) / 3 + 4;
        cv::Rect r((x - pad) * S, (y - pad) * S, (w + 2 * pad) * S, (h + 2 * pad) * S);
        r &= cv::Rect(0, 0, gray.cols, gray.rows);
        if (r.area() > 0) cands.push_back({r, d * (0.5f + 0.5f * aspect)});
    }

    std::sort(cands.begin(), cands.end(), [](const Candidate& a, const Candidate& b) { return a.score > b.score; });

    // drop a weaker region whose centre falls inside a stronger one already kept
    std::vector<cv::Rect> out;
    out.reserve(cands.size());
    for (const auto& c : cands) {
        const cv::Point mid(c.rect.x + c.rect.width / 2, c.rect.y + c.rect.height / 2);
        bool inside = false;
        for (const auto& k : out) {
            if (k.contains(mid)) {
                inside = true;
                break;
            }
        }
        if (!inside) out.push_back(c.rect);
    }
    return out;
}

} // namespace cr
