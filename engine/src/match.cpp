// match.cpp - reads a QR code too faint to decode by matching it against the codes it can be.
//
// Liquor holograms (and many pharma lines) are printed from a roll in serial order, so the codes
// of one case form a run: the codes not yet read are among the gaps of that run. Telling which of
// ~50 known patterns lies under a faint code needs far less signal than decoding it from scratch
// (every module votes; error correction is not needed). Safeguards against a wrong read:
//   - the printer's QR encoding (segments, error correction level) is learned from codes this
//     reader decoded, and must reproduce them exactly;
//   - only the data area is compared (finders and timing are the same in every candidate);
//   - a match must score high AND clearly beat every other candidate;
//   - codes already read are never candidates, and every match is reported as CR_STEP_MATCH.

#include "reader.hpp"

#include "qrcodegen.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <array>
#include <chrono>
#include <cmath>
#include <map>
#include <mutex>

namespace cr {

namespace {

using qrcodegen::QrCode;
using qrcodegen::QrSegment;

// accept a match only above this score (correlation over the data modules) and this lead over
// the second best; on the samples true codes scored 0.81-0.97, wrong candidates at most 0.68
constexpr float kMinScore = 0.75f;
constexpr float kMinLead = 0.15f;
constexpr float kPsfFrom = 0.45f; // a leader below this is not a blurred code but noise

QrCode::Ecc toEcc(int ecl)
{
    switch (ecl) {
    case 0: return QrCode::Ecc::LOW;
    case 2: return QrCode::Ecc::QUARTILE;
    case 3: return QrCode::Ecc::HIGH;
    default: return QrCode::Ecc::MEDIUM;
    }
}

int eclFromString(const std::string& s)
{
    if (s.empty()) return -1;
    switch (s[0]) {
    case 'L': return 0;
    case 'M': return 1;
    case 'Q': return 2;
    case 'H': return 3;
    default: return -1;
    }
}

// segments for one encoding variant: 0 byte, 1 alphanumeric, 2 numeric, 3 alnum prefix + numeric,
// 4 byte prefix + numeric (prefix = the first `split` characters)
bool segments(const std::string& t, int kind, int split, std::vector<QrSegment>& out)
{
    out.clear();
    const auto bytes = [](const std::string& s) { return std::vector<std::uint8_t>(s.begin(), s.end()); };
    switch (kind) {
    case 0: out.push_back(QrSegment::makeBytes(bytes(t))); return true;
    case 1:
        if (!QrSegment::isAlphanumeric(t.c_str())) return false;
        out.push_back(QrSegment::makeAlphanumeric(t.c_str()));
        return true;
    case 2:
        if (!QrSegment::isNumeric(t.c_str())) return false;
        out.push_back(QrSegment::makeNumeric(t.c_str()));
        return true;
    case 3:
    case 4: {
        if (split <= 0 || split >= static_cast<int>(t.size())) return false;
        const std::string a = t.substr(0, static_cast<size_t>(split)), b = t.substr(static_cast<size_t>(split));
        if (!QrSegment::isNumeric(b.c_str())) return false;
        if (kind == 3) {
            if (!QrSegment::isAlphanumeric(a.c_str())) return false;
            out.push_back(QrSegment::makeAlphanumeric(a.c_str()));
        } else {
            out.push_back(QrSegment::makeBytes(bytes(a)));
        }
        out.push_back(QrSegment::makeNumeric(b.c_str()));
        return true;
    }
    default: return false;
    }
}

} // namespace

bool encodeQr(const std::string& text, const QrEncoding& e, int mask, std::vector<uint8_t>& dark)
{
    if (e.format == CR_DATAMATRIX) return encodeDm(text, e.modules, dark, e.kind == 1);
    std::vector<QrSegment> segs;
    if (!segments(text, e.kind, e.split, segs)) return false;
    const int version = (e.modules - 17) / 4;
    try {
        const QrCode q = QrCode::encodeSegments(segs, toEcc(e.ecl), version, version, mask, false);
        const int n = q.getSize();
        dark.assign(static_cast<size_t>(n) * n, 0);
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x) dark[static_cast<size_t>(y) * n + x] = q.getModule(x, y) ? 1 : 0;
        return true;
    } catch (const std::exception&) {
        return false; // does not fit this version, or a bad mask
    }
}

QrEncoding learnQrEncoding(const Code& c)
{
    QrEncoding none;
    if (c.format != CR_QR || c.modW < 21 || c.modW != c.modH || c.modules.empty()) return none;
    const int ecl = eclFromString(c.ecLevel);
    if (ecl < 0) return none;
    // the decoder's module grid is the sampled one (error correction fixed a few misread modules),
    // so the printer's encoding is the closest fit, if it is close: wrong encodings agree on about
    // 75% of the modules, the right one on 98-99%
    const size_t total = c.modules.size();
    QrEncoding best;
    size_t bestSame = 0;
    std::vector<uint8_t> dark;
    for (int kind = 0; kind <= 4; ++kind) {
        const int lastSplit = kind >= 3 ? static_cast<int>(c.text.size()) - 1 : 0;
        for (int split = kind >= 3 ? 1 : 0; split <= lastSplit; ++split) {
            QrEncoding e;
            e.kind = kind;
            e.split = split;
            e.ecl = ecl;
            e.modules = c.modW;
            for (int mask = 0; mask < 8; ++mask) {
                if (!encodeQr(c.text, e, mask, dark) || dark.size() != total) break;
                size_t same = 0;
                for (size_t k = 0; k < total; ++k) same += dark[k] == c.modules[k];
                if (same > bestSame) {
                    bestSame = same;
                    best = e;
                }
            }
        }
    }
    return bestSame * 100 >= total * 95 ? best : none;
}

namespace {

// data area of an n x n QR: everything but finders + separators + format info and timing
cv::Mat dataArea(int n)
{
    cv::Mat m(n, n, CV_8U, cv::Scalar(1));
    m(cv::Rect(0, 0, 9, 9)).setTo(0);
    m(cv::Rect(n - 8, 0, 8, 9)).setTo(0);
    m(cv::Rect(0, n - 8, 9, 8)).setTo(0);
    m.row(6).setTo(0);
    m.col(6).setTo(0);
    return m;
}

// correlation of the (dark-positive, standardised) module means with a 0/1 pattern on a mask
float corr(const cv::Mat& z, const uint8_t* pat, const cv::Mat& use)
{
    const int n = z.rows;
    double sz = 0, sp = 0, szz = 0, spp = 0, szp = 0;
    int cnt = 0;
    for (int y = 0; y < n; ++y)
        for (int x = 0; x < n; ++x) {
            if (!use.at<uint8_t>(y, x)) continue;
            const double a = z.at<float>(y, x), b = pat[y * n + x];
            sz += a; sp += b; szz += a * a; spp += b * b; szp += a * b;
            ++cnt;
        }
    if (cnt < 2) return 0.f;
    const double cov = szp - sz * sp / cnt, va = szz - sz * sz / cnt, vb = spp - sp * sp / cnt;
    return va <= 0 || vb <= 0 ? 0.f : static_cast<float>(cov / std::sqrt(va * vb));
}

} // namespace

// Candidate patterns, encoded once per image and mask (shared by the decode threads).
struct MatchCandidates::Cache {
    std::mutex mu;
    std::map<int, std::vector<std::vector<uint8_t>>> byMask; // mask -> pattern of each candidate
};

MatchCandidates::MatchCandidates() : cache_(std::make_shared<Cache>()) {}

const std::vector<std::vector<uint8_t>>& MatchCandidates::patterns(int mask) const
{
    std::lock_guard<std::mutex> lock(cache_->mu);
    auto it = cache_->byMask.find(mask);
    if (it != cache_->byMask.end()) return it->second;
    auto& v = cache_->byMask[mask];
    v.resize(texts.size());
    for (size_t i = 0; i < texts.size(); ++i)
        if (!encodeQr(texts[i], encoding, mask, v[i])) v[i].clear();
    return v;
}

bool matchQr(const cv::Mat& crop, float codeSide, const MatchCandidates& mc, int budgetMs, std::string& text,
             cv::Point2f quadOut[4], float* scoreOut, float psfSigmaMod)
{
    const int n = mc.encoding.modules;
    if (mc.texts.empty() || n < 21 || codeSide <= 0 || budgetMs <= 0) return false;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(budgetMs);

    // stretch, slight blur against noise, scale to about 3 px per module
    std::vector<uint8_t> v(crop.begin<uint8_t>(), crop.end<uint8_t>());
    std::nth_element(v.begin(), v.begin() + v.size() / 100, v.end());
    const double lo = v[v.size() / 100];
    std::nth_element(v.begin(), v.begin() + v.size() * 995 / 1000, v.end());
    const double hi = std::max(lo + 4, static_cast<double>(v[v.size() * 995 / 1000]));
    cv::Mat s, b, u;
    crop.convertTo(s, CV_32F, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
    cv::GaussianBlur(s, b, cv::Size(), 0.6);
    const double f = std::clamp(3.2 * n / codeSide, 1.0, 6.0); // about 3.2 px per module
    cv::resize(b, u, cv::Size(), f, f, cv::INTER_CUBIC);
    const float m0 = static_cast<float>(codeSide * f / n);

    static thread_local std::map<int, cv::Mat> dataMasks, fixedMasks;
    if (!dataMasks.count(n)) {
        dataMasks[n] = dataArea(n);
        fixedMasks[n] = qrFixed(n) >= 0;
    }
    const cv::Mat& use = dataMasks[n];
    const cv::Mat& fixedUse = fixedMasks[n];
    // reference for orientation (finders, timing) and the format bits of each mask
    const auto& ref = mc.patterns(0);
    if (ref.empty() || ref[0].empty()) return false;
    std::array<std::vector<uint8_t>, 8> fmtRef; // format bits depend only on the mask (and EC level)
    for (int mk = 0; mk < 8; ++mk)
        if (!encodeQr(mc.texts[0], mc.encoding, mk, fmtRef[static_cast<size_t>(mk)])) return false;
    cv::Mat fmtUse(n, n, CV_8U, cv::Scalar(0));
    for (int c : {0, 1, 2, 3, 4, 5, 7, 8}) fmtUse.at<uint8_t>(8, c) = 1;
    for (int r : {0, 1, 2, 3, 4, 5, 7}) fmtUse.at<uint8_t>(r, 8) = 1;
    for (int i = 0; i < 7; ++i) fmtUse.at<uint8_t>(n - 1 - i, 8) = 1;
    for (int i = 0; i < 8; ++i) fmtUse.at<uint8_t>(8, n - 8 + i) = 1;

    std::vector<float> best(mc.texts.size(), -2.f);
    std::vector<cv::Point2f> bestQuad(mc.texts.size() * 4);
    std::vector<int> bestMask(mc.texts.size(), 0);
    bool any = false;
    for (float m : {m0, m0 * 0.9f, m0 * 1.1f}) {
        if (any || std::chrono::steady_clock::now() >= deadline) break; // other sizes only if needed
        const cv::Mat resp = finderResponseFast(u, m);
        const int off = (static_cast<int>(std::ceil(9 * m * 1.5f)) | 1) / 2;
        cv::Point2f q0[4];
        if (!quadFromFinders(resp, m, n, off, q0)) continue;
        // the fourth corner has no finder: search around it
        for (int dy = -2; dy <= 2; ++dy)
            for (int dx = -2; dx <= 2; ++dx) {
                cv::Point2f q[4] = {q0[0], q0[1], q0[2] + cv::Point2f(dx * m * 0.5f, dy * m * 0.5f), q0[3]};
                cv::Mat mm = moduleMeans(u, q, n);
                cv::Scalar mean, sd;
                cv::meanStdDev(mm, mean, sd);
                cv::Mat z = (mean[0] - mm) / (sd[0] + 1e-6); // dark -> positive
                // orientation: the rotation whose finders and timing fit (codes are never mirrored)
                int rot = 0;
                float rotBest = -2.f;
                cv::Mat zr = z;
                for (int k = 0; k < 4; ++k) {
                    cv::Mat t = z.clone();
                    for (int j = 0; j < k; ++j) cv::rotate(t, t, cv::ROTATE_90_COUNTERCLOCKWISE);
                    const float sc = corr(t, ref[0].data(), fixedUse);
                    if (sc > rotBest) { rotBest = sc; rot = k; zr = t; }
                }
                if (rotBest < 0.5f) continue; // not a QR grid
                // mask: from the format bits (both copies)
                int mask = 0;
                float maskBest = -2.f;
                for (int mk = 0; mk < 8; ++mk) {
                    const float sc = corr(zr, fmtRef[static_cast<size_t>(mk)].data(), fmtUse);
                    if (sc > maskBest) { maskBest = sc; mask = mk; }
                }
                const auto& pats = mc.patterns(mask);
                // corners in the code's own orientation (TL, TR, BR, BL) for reporting
                cv::Point2f oq[4];
                // (turning the grid k times counter-clockwise puts quad corner k at the code's top left)
                for (int k = 0; k < 4; ++k) oq[k] = q[(k + rot) % 4];
                for (size_t i = 0; i < pats.size(); ++i) {
                    if (pats[i].size() != static_cast<size_t>(n) * n) continue;
                    const float sc = corr(zr, pats[i].data(), use);
                    if (sc > best[i]) {
                        best[i] = sc;
                        bestMask[i] = mask;
                        for (int k = 0; k < 4; ++k) bestQuad[i * 4 + static_cast<size_t>(k)] = oq[k];
                        any = true;
                    }
                }
            }
    }
    if (!any) return false;
    size_t top = 0;
    float second = -2.f;
    for (size_t i = 1; i < best.size(); ++i)
        if (best[i] > best[top]) top = i;
    for (size_t i = 0; i < best.size(); ++i)
        if (i != top) second = std::max(second, best[i]);
    if (scoreOut) *scoreOut = best[top];
    if (best[top] < kMinScore || best[top] - second < kMinLead) {
        // a defocused code: its sampled modules lose the pattern, so the leader falls short of the
        // rule. Every candidate is rendered through the lens blur and scored on the pixels of its
        // data modules instead. Blur scores run higher for impostors than sampled ones (synthetic
        // true-absent trials: up to 0.79), so the floor alone does not protect: the blur leader
        // must also be the sampling leader, and its lead over every other candidate must pass
        if (psfSigmaMod <= 0.f || best[top] < kPsfFrom || std::chrono::steady_clock::now() >= deadline) return false;
        // the image blur at the working scale, with the 0.6 px smoothing applied above
        const float sigmaPx = psfSigmaMod * codeSide / static_cast<float>(n);
        const float sigmaU = static_cast<float>(f) * std::sqrt(sigmaPx * sigmaPx + 0.36f);
        size_t ptop = top;
        float pbest = -2.f, psecond = -2.f;
        for (size_t i = 0; i < best.size(); ++i) {
            if (best[i] <= -2.f) continue;
            const auto& pat = mc.patterns(bestMask[i])[i];
            if (pat.size() != static_cast<size_t>(n) * n) continue;
            const float sc = psfScore(u, &bestQuad[i * 4], pat.data(), n, use, kPrintGrowth, sigmaU);
            if (sc > pbest) { psecond = pbest; pbest = sc; ptop = i; }
            else psecond = std::max(psecond, sc);
        }
        if (ptop != top || pbest < kMinScore || pbest - psecond < kMinLead) return false;
    }
    text = mc.texts[top];
    for (int k = 0; k < 4; ++k) quadOut[k] = bestQuad[top * 4 + static_cast<size_t>(k)] * static_cast<float>(1.0 / f);
    return true;
}

} // namespace cr
