// recognize.cpp - the line recogniser (ocrnet.onnx, a CTC model) and the free read.

#include "ocr.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <fstream>
#include <limits>
#include <sstream>

namespace ocr {

std::u32string fromUtf8(const std::string& s)
{
    std::u32string out;
    for (size_t i = 0; i < s.size();) {
        const unsigned char c = static_cast<unsigned char>(s[i]);
        char32_t cp = 0;
        int n = 0;
        if (c < 0x80) { cp = c; n = 1; }
        else if ((c >> 5) == 0x6) { cp = c & 0x1F; n = 2; }
        else if ((c >> 4) == 0xE) { cp = c & 0x0F; n = 3; }
        else if ((c >> 3) == 0x1E) { cp = c & 0x07; n = 4; }
        else { ++i; continue; }                       // stray byte: skip
        if (i + size_t(n) > s.size()) break;
        for (int k = 1; k < n; ++k) cp = (cp << 6) | (static_cast<unsigned char>(s[i + size_t(k)]) & 0x3F);
        out.push_back(cp);
        i += size_t(n);
    }
    return out;
}

std::string toUtf8(const std::u32string& s)
{
    std::string out;
    for (char32_t c : s) {
        if (c < 0x80) out.push_back(char(c));
        else if (c < 0x800) { out.push_back(char(0xC0 | (c >> 6))); out.push_back(char(0x80 | (c & 0x3F))); }
        else if (c < 0x10000) {
            out.push_back(char(0xE0 | (c >> 12)));
            out.push_back(char(0x80 | ((c >> 6) & 0x3F)));
            out.push_back(char(0x80 | (c & 0x3F)));
        } else {
            out.push_back(char(0xF0 | (c >> 18)));
            out.push_back(char(0x80 | ((c >> 12) & 0x3F)));
            out.push_back(char(0x80 | ((c >> 6) & 0x3F)));
            out.push_back(char(0x80 | (c & 0x3F)));
        }
    }
    return out;
}

bool Charset::load(const std::string& path, std::string& err)
{
    std::ifstream f(path, std::ios::binary);
    if (!f) { err = "cannot open charset " + path; return false; }
    std::stringstream ss;
    ss << f.rdbuf();
    const std::string all = ss.str();
    chars.assign(1, 0);
    index.clear();
    size_t start = 0;
    while (start < all.size()) {
        size_t end = all.find('\n', start);
        if (end == std::string::npos) end = all.size();
        std::string line = all.substr(start, end - start);
        if (!line.empty() && line.back() == '\r') line.pop_back();
        start = end + 1;
        if (line.empty()) continue;
        const std::u32string cp = fromUtf8(line);
        if (cp.size() != 1) { err = "charset line is not one character: " + line; return false; }
        index[cp[0]] = int(chars.size());
        chars.push_back(cp[0]);
    }
    if (chars.size() < 2) { err = "charset is empty"; return false; }
    return true;
}

bool Recognizer::finishLoad(cv::dnn::Net net, const std::string& charset, std::string& err)
{
    ready_ = false;
    if (!cs_.load(charset, err)) return false;
    net_ = std::move(net);
    if (net_.empty()) { err = "cannot load model (empty)"; return false; }
    // check the output size matches the charset on a small input
    cv::Mat probe(kLineH, 64, CV_8U, cv::Scalar(255));
    cv::Mat lp = run(probe);
    if (lp.empty() || lp.cols != int(cs_.chars.size())) {
        ready_ = false;
        net_ = cv::dnn::Net();
        err = "model has " + std::to_string(lp.cols) + " classes, charset " + std::to_string(cs_.chars.size());
        return false;
    }
    ready_ = true;
    return true;
}

bool Recognizer::load(const std::string& onnx, const std::string& charset, std::string& err)
{
    ready_ = false;
    cv::dnn::Net net;
    try {
        net = cv::dnn::readNetFromONNX(onnx);
    } catch (const std::exception& e) {
        err = std::string("cannot load model: ") + e.what();
        return false;
    }
    if (net.empty()) { err = "cannot load model " + onnx; return false; }
    return finishLoad(std::move(net), charset, err);
}

bool Recognizer::loadBuffer(const uint8_t* onnx, size_t onnxSize, const std::string& charset, std::string& err)
{
    ready_ = false;
    if (!onnx || onnxSize == 0) { err = "empty model buffer"; return false; }
    cv::dnn::Net net;
    try {
        net = cv::dnn::readNetFromONNX(reinterpret_cast<const char*>(onnx), onnxSize);
    } catch (const std::exception& e) {
        err = std::string("cannot load model: ") + e.what();
        return false;
    }
    if (net.empty()) { err = "cannot load model (empty buffer)"; return false; }
    return finishLoad(std::move(net), charset, err);
}

cv::Mat Recognizer::run(const cv::Mat& line)
{
    cv::Mat f;
    line.convertTo(f, CV_32F, 1.0 / 255.0);
    const int sz[4] = {1, 1, f.rows, f.cols};
    cv::Mat blob(4, sz, CV_32F, f.data);
    net_.setInput(blob);
    cv::Mat out = net_.forward();                     // 1 x C x 1 x T
    if (out.dims != 4) return {};
    const int C = out.size[1], T = out.size[3];
    cv::Mat ct(C, T, CV_32F, out.ptr<float>());
    cv::Mat lp;
    cv::transpose(ct, lp);                            // T x C
    for (int t = 0; t < T; ++t) {                     // log-softmax per column
        float* r = lp.ptr<float>(t);
        const float mx = *std::max_element(r, r + C);
        double sum = 0;
        for (int c = 0; c < C; ++c) sum += std::exp(double(r[c] - mx));
        const float lse = mx + float(std::log(sum));
        for (int c = 0; c < C; ++c) r[c] -= lse;
    }
    return lp;
}

std::vector<ReadChar> greedyRead(const cv::Mat& logp, const Charset& cs)
{
    std::vector<ReadChar> out;
    int prev = 0;
    for (int t = 0; t < logp.rows; ++t) {
        const float* r = logp.ptr<float>(t);
        const int c = int(std::max_element(r, r + logp.cols) - r);
        const float p = std::exp(r[c]);
        if (c != 0 && c == prev && !out.empty()) {
            out.back().t1 = t;
            out.back().conf = std::max(out.back().conf, p);
        } else if (c != 0) {
            ReadChar rc;
            rc.c = cs.chars[size_t(c)];
            rc.conf = p;
            rc.t0 = rc.t1 = t;
            out.push_back(rc);
        }
        prev = c;
    }
    return out;
}

float linePosterior(const cv::Mat& logp, const Charset& cs, const std::u32string& text)
{
    std::vector<int> lab;
    for (char32_t c : text) {
        if (c == U' ') continue;
        const int k = cs.find(c);
        if (k <= 0) return 0.f;
        lab.push_back(k);
    }
    if (lab.empty() || logp.empty()) return 0.f;
    const int space = cs.find(U' ');
    const int S = 2 * int(lab.size()) + 1, T = logp.rows;
    const double ninf = -std::numeric_limits<double>::infinity();
    auto add = [ninf](double a, double b) {
        if (a == ninf) return b;
        if (b == ninf) return a;
        const double m = std::max(a, b);
        return m + std::log1p(std::exp(-std::abs(a - b)));
    };
    auto emit = [&](int t, int s) {
        const float* r = logp.ptr<float>(t);
        if (s % 2 == 1) return double(r[lab[size_t(s / 2)]]);
        return space > 0 ? add(r[0], r[space]) : double(r[0]);   // blank, or a space
    };
    std::vector<double> a(static_cast<size_t>(S), ninf), n(static_cast<size_t>(S));
    a[0] = emit(0, 0);
    if (S > 1) a[1] = emit(0, 1);
    for (int t = 1; t < T; ++t) {
        for (int s = 0; s < S; ++s) {
            double v = a[size_t(s)];
            if (s >= 1) v = add(v, a[size_t(s - 1)]);
            if (s >= 3 && s % 2 == 1 && lab[size_t(s / 2)] != lab[size_t(s / 2 - 1)]) v = add(v, a[size_t(s - 2)]);
            n[size_t(s)] = v == ninf ? ninf : v + emit(t, s);
        }
        a.swap(n);
    }
    const double lp = add(a[size_t(S - 1)], a[size_t(S - 2)]);
    return lp == ninf ? 0.f : float(std::exp(lp));
}

float lineConfidence(float posterior)
{
    return 100.f * std::pow(std::clamp(posterior, 0.f, 1.f), 1.f / kLineGamma);
}

cv::Mat shiftedLine(const cv::Mat& line)
{
    if (line.empty()) return line;
    const int k = kStride / 2;
    const int right = (kStride - (line.cols + k) % kStride) % kStride;
    cv::Mat edge = line.colRange(0, std::min(2, line.cols)).clone();
    std::vector<uchar> v(edge.begin<uchar>(), edge.end<uchar>());
    std::nth_element(v.begin(), v.begin() + long(v.size() / 2), v.end());
    cv::Mat out;
    cv::copyMakeBorder(line, out, 0, 0, k, right, cv::BORDER_CONSTANT, cv::Scalar(v[v.size() / 2]));
    return out;
}

} // namespace ocr
