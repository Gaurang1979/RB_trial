// ai_locator.cpp - deep-learning code locator (CodeNet, ml/model.py) run with OpenCV DNN.
//
// The network sees the image at 1/8 of full resolution, stretched to 0..1 exactly as in
// training, and returns a code-probability map at 1/16. Connected areas above the threshold
// become candidate regions for the decoder, strongest first.

#include "reader.hpp"

#include <opencv2/dnn.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>

namespace cr {

namespace {

constexpr int kInputScale = 8;   // network input = full resolution / 8
constexpr int kOutputScale = 16; // network output = full resolution / 16

// 1st..99.5th percentile -> 0..1, as model.stretch() in training. hiQ: the top percentile
// (99.99 for high-dynamic-range scenes: bright labels on a very dark case).
cv::Mat stretch01(const cv::Mat& u8, double hiQ = 99.5)
{
    int hist[256] = {};
    for (int y = 0; y < u8.rows; ++y) {
        const uint8_t* row = u8.ptr<uint8_t>(y);
        for (int x = 0; x < u8.cols; ++x) hist[row[x]]++;
    }
    const double n = static_cast<double>(u8.total());
    auto pct = [&](double q) {
        // linear interpolation between ranks, like numpy.percentile
        const double rank = q / 100.0 * (n - 1);
        int64_t acc = 0;
        for (int v = 0; v < 256; ++v) {
            acc += hist[v];
            if (acc > rank) return static_cast<double>(v);
        }
        return 255.0;
    };
    double lo = pct(1.0), hi = pct(hiQ);
    if (hi - lo < 4) hi = lo + 4;
    cv::Mat f;
    u8.convertTo(f, CV_32F, 1.0 / (hi - lo), -lo / (hi - lo));
    cv::min(cv::max(f, 0.0), 1.0, f);
    return f;
}

} // namespace

cv::Mat enhanceAI(cv::dnn::Net& net, const cv::Mat& crop)
{
    // the network halves the size twice: pad to a multiple of 4 (edge pixels)
    const int H = (crop.rows + 3) / 4 * 4, W = (crop.cols + 3) / 4 * 4;
    cv::Mat padded;
    cv::copyMakeBorder(crop, padded, 0, H - crop.rows, 0, W - crop.cols, cv::BORDER_REPLICATE);
    const cv::Mat input = stretch01(padded);
    const int sz[] = {1, 1, H, W};
    cv::Mat blob(4, sz, CV_32F, const_cast<float*>(input.ptr<float>()));
    net.setInput(blob);
    cv::Mat out = net.forward();
    const cv::Mat logits(out.size[2], out.size[3], CV_32F, out.ptr<float>());
    cv::Mat bw = logits(cv::Rect(0, 0, crop.cols, crop.rows)) > 0.f; // paper = 255, ink = 0
    return bw;
}

std::vector<Found> locateAI(cv::dnn::Net& net, const cv::Mat& gray, float threshold, float* labelBrightness, int inputScale)
{
    // inputScale 8: normal pass (codes of 6-45 px at that scale); 4: fine pass for small codes
    const int outScale = inputScale * 2; // the network halves once more
    cv::Mat small;
    cv::resize(gray, small, cv::Size(), 1.0 / inputScale, 1.0 / inputScale, cv::INTER_AREA);
    if (labelBrightness) {
        std::vector<uint8_t> v(small.begin<uint8_t>(), small.end<uint8_t>());
        std::nth_element(v.begin(), v.begin() + v.size() * 95 / 100, v.end());
        *labelBrightness = v.empty() ? 0.f : v[v.size() * 95 / 100];
    }

    // High dynamic range (a few labels far brighter than everything else): the usual stretch clips
    // the labels to white and their codes vanish, so also run with the top set by the labels and
    // keep the stronger response of the two.
    int hist[256] = {};
    for (int y = 0; y < small.rows; ++y) {
        const uint8_t* row = small.ptr<uint8_t>(y);
        for (int x = 0; x < small.cols; ++x) hist[row[x]]++;
    }
    auto pctOf = [&](double q) {
        const double rank = q / 100.0 * (small.total() - 1);
        int64_t acc = 0;
        for (int v = 0; v < 256; ++v) {
            acc += hist[v];
            if (acc > rank) return v;
        }
        return 255;
    };
    const bool hdr = pctOf(99.99) > 3 * std::max(4, pctOf(99.5));
    auto run = [&](double hiQ) {
        const cv::Mat input = stretch01(small, hiQ);
        const int sz[] = {1, 1, input.rows, input.cols};
        cv::Mat blob(4, sz, CV_32F, const_cast<float*>(input.ptr<float>()));
        net.setInput(blob);
        cv::Mat o = net.forward(); // 1 x 1 x h x w logits
        return cv::Mat(o.size[2], o.size[3], CV_32F, o.ptr<float>()).clone();
    };
    // one pass per stretch; regions of the HDR pass are added only where the normal pass found
    // nothing (merging the two maps would join neighbouring codes and shift their crops)
    std::vector<Found> found;
    auto extract = [&](const cv::Mat& logits) {
        // sigmoid(x) > t  <=>  x > log(t / (1 - t))
        const float t = std::clamp(threshold, 0.01f, 0.99f);
        const float logitT = std::log(t / (1.f - t));
        cv::Mat mask = logits > logitT;

        cv::Mat labels, stats, centroids;
        const int n = cv::connectedComponentsWithStats(mask, labels, stats, centroids, 8, CV_32S);

        const cv::Rect full(0, 0, gray.cols, gray.rows);
        for (int i = 1; i < n; ++i) {
            const int a = stats.at<int>(i, cv::CC_STAT_AREA);
            if (a < 2) continue;
            // a region bigger than a quarter of the image is not one code: a bad model output
            // must not swallow the time budget
            if (stats.at<int>(i, cv::CC_STAT_WIDTH) > logits.cols / 4 || stats.at<int>(i, cv::CC_STAT_HEIGHT) > logits.rows / 4)
                continue;
            const cv::Rect b(stats.at<int>(i, cv::CC_STAT_LEFT), stats.at<int>(i, cv::CC_STAT_TOP),
                             stats.at<int>(i, cv::CC_STAT_WIDTH), stats.at<int>(i, cv::CC_STAT_HEIGHT));
            Found f;
            f.score = static_cast<float>(cv::mean(logits(b), labels(b) == i)[0]);
            // the code area: a filled square-ish blob, so its side is sqrt(area) and its angle comes
            // from the second moments (OpenCV 5 moved minAreaRect out of imgproc)
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            int cnt = 0;
            for (int y = b.y; y < b.y + b.height; ++y)
                for (int x = b.x; x < b.x + b.width; ++x)
                    if (labels.at<int>(y, x) == i) {
                        sx += x; sy += y; sxx += double(x) * x; syy += double(y) * y; sxy += double(x) * y; ++cnt;
                    }
            const double mx = sx / cnt, my = sy / cnt;
            const double cxx = sxx / cnt - mx * mx, cyy = syy / cnt - my * my, cxy = sxy / cnt - mx * my;
            const float angle = static_cast<float>(0.5 * std::atan2(2 * cxy, cxx - cyy) * 180.0 / CV_PI);
            f.side = static_cast<float>(std::sqrt(static_cast<double>(cnt)) * outScale);
            f.rotated = cv::RotatedRect(cv::Point2f(static_cast<float>((mx + 0.5) * outScale), static_cast<float>((my + 0.5) * outScale)),
                                        cv::Size2f(f.side, f.side), angle);
            // crop with a margin for the quiet zone and the coarse mask: a crop that cuts a finder
            // or the L pattern cannot be read
            const int pad = std::max(b.width, b.height) * outScale / 2 + 2 * outScale;
            cv::Rect r(b.x * outScale - pad, b.y * outScale - pad, b.width * outScale + 2 * pad, b.height * outScale + 2 * pad);
            f.box = r & full;
            if (f.box.area() > 0) found.push_back(f);
        }
    };
    extract(run(99.5));
    if (hdr) {
        const size_t before = found.size();
        std::vector<Found> normal(found.begin(), found.end());
        found.clear();
        extract(run(99.99));
        std::vector<Found> extra;
        for (const auto& f : found) {
            const cv::Point c(static_cast<int>(f.rotated.center.x), static_cast<int>(f.rotated.center.y));
            bool dup = false;
            for (size_t k = 0; k < before; ++k)
                if (normal[k].box.contains(c)) { dup = true; break; }
            if (!dup) extra.push_back(f);
        }
        found = std::move(normal);
        found.insert(found.end(), extra.begin(), extra.end());
    }
    std::sort(found.begin(), found.end(), [](const Found& a, const Found& b) { return a.score > b.score; });
    return found;
}

} // namespace cr
