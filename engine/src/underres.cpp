// underres.cpp - discrete solver for under-resolved QR codes (about 1.3 - 2.6 px per module).
//
// Our own form of the process in Cognex US11151346 (prototype: ml/experiments/underres.py):
//   - each pixel is the light level minus the contrast times the blurred area of the dark modules
//     it overlaps, I_p = L(x, y) - C * sum_k A_pk d_k with d_k in {0, 1}; A_pk from 3 x 3
//     sub-samples per pixel through the code's homography and a Gaussian blur on top; the quiet
//     zone is light; L a plane, C one contrast, both by least squares;
//   - the corners are refined (Levenberg-Marquardt) on the pixels the function modules explain,
//     then the corners and the blur on all pixels with the modules from least squares;
//   - known modules are fixed: finders, separators, timing, the format once found, and the
//     codewords every code already read in the case shares (qrKnownModules), these only when the
//     solved modules agree with them (they assume how the printer encodes);
//   - the other modules start from least squares, then are solved as binary: for each unknown
//     module every pattern of its 3 x 3 neighbourhood (up to 512) is tested against the pixels the
//     neighbourhood covers and the best kept, working inward from the known modules and sweeping
//     until nothing changes;
//   - a module's confidence is the smallest rise in squared error when it is flipped, over the
//     neighbourhoods that contain it (its eight neighbours' tests must all agree);
//   - the least certain codewords become Reed-Solomon erasures with two check symbols in reserve,
//     and the corrected symbol must parse (decodeQrSolved, qrsoft.cpp).
// Synthetic version 1-M codes (contrast 20, noise sd 2.5, corners off by up to 0.5 px): 24 of 24 at
// 1.5 - 2 px per module where module sampling reads 10, never wrongly; random images give nothing.
// On the TN excise image (1.9 px per module) it reads the four codes no other step reads (one new
// serial, three second labels of serials read elsewhere). 60 - 200 ms a code.
// Patent: US11151346 is in force in the US, DE, CN and JP to about 2034, no Indian member found;
// build with -DCR_UNDERRES=OFF for a reader that leaves this step out (docs/PATENTS.md).

#include "reader.hpp"

#include <opencv2/core.hpp>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <numeric>
#include <tuple>

namespace cr {

#ifdef CR_NO_UNDERRES
std::string underresDecodeQr(const cv::Mat&, const cv::Point2f[4], int, const std::vector<std::string>*, std::chrono::steady_clock::time_point)
{
    return {};
}
#else

namespace {

using Clock = std::chrono::steady_clock;
constexpr int kSub = 3; // sub-samples per pixel side

// format module positions (both copies; the dark module stays known)
std::vector<int> formatPositions(int n)
{
    std::vector<int> f;
    for (int x = 0; x <= 8; ++x)
        if (x != 6) f.push_back(8 * n + x);
    for (int y = 0; y <= 7; ++y)
        if (y != 6) f.push_back(y * n + 8);
    for (int x = n - 8; x < n; ++x) f.push_back(8 * n + x);
    for (int y = n - 7; y < n; ++y) f.push_back(y * n + 8);
    return f;
}

class Solver {
public:
    Solver(const cv::Mat& gray, int n) : g_(gray), n_(n), nn_(n * n) {}

    // the fixed pixel set: centres within two modules of the code
    bool setPixels(const cv::Point2f quad[4])
    {
        setQuad(quad);
        const cv::Mat Hi = H_.inv();
        std::vector<cv::Point2f> c(4), m;
        const float qz = 2.f;
        const std::vector<cv::Point2f> src = {{-qz, -qz}, {n_ + qz, -qz}, {n_ + qz, n_ + qz}, {-qz, n_ + qz}};
        cv::perspectiveTransform(src, c, H_);
        float x0 = 1e9f, y0 = 1e9f, x1 = -1e9f, y1 = -1e9f;
        for (const auto& p : c) {
            x0 = std::min(x0, p.x); y0 = std::min(y0, p.y); x1 = std::max(x1, p.x); y1 = std::max(y1, p.y);
        }
        const cv::Rect box = cv::Rect(cv::Point(static_cast<int>(std::floor(x0)) - 1, static_cast<int>(std::floor(y0)) - 1),
                                      cv::Point(static_cast<int>(std::ceil(x1)) + 2, static_cast<int>(std::ceil(y1)) + 2)) &
                             cv::Rect(0, 0, g_.cols, g_.rows);
        if (box.area() < 16 || box.area() > 200 * 200) return false;
        std::vector<cv::Point2f> pts;
        for (int y = box.y; y < box.y + box.height; ++y)
            for (int x = box.x; x < box.x + box.width; ++x) pts.emplace_back(static_cast<float>(x), static_cast<float>(y));
        cv::perspectiveTransform(pts, m, Hi);
        at_.clear();
        v_.clear();
        for (size_t i = 0; i < pts.size(); ++i)
            if (m[i].x > -qz && m[i].x < n_ + qz && m[i].y > -qz && m[i].y < n_ + qz) {
                at_.emplace_back(static_cast<int>(pts[i].x), static_cast<int>(pts[i].y));
                v_.push_back(static_cast<float>(g_.at<uint8_t>(static_cast<int>(pts[i].y), static_cast<int>(pts[i].x))));
            }
        box_ = box;
        cx_ = box.x + box.width * 0.5f;
        cy_ = box.y + box.height * 0.5f;
        return at_.size() >= 16;
    }

    void setQuad(const cv::Point2f quad[4])
    {
        std::copy(quad, quad + 4, q_);
        const cv::Point2f src[4] = {{0, 0}, {float(n_), 0}, {float(n_), float(n_)}, {0, float(n_)}};
        H_ = homography4(src, q_);
    }

    // A for the current quad and blur: per pixel (module, area); per module (pixel, area)
    int builds = 0;
    void build()
    {
        ++builds;
        const int r = std::max(1, static_cast<int>(std::ceil(2.5f * sigma_)));
        const cv::Rect ex(box_.x - r, box_.y - r, box_.width + 2 * r, box_.height + 2 * r);
        // box coverage of every pixel of the expanded box: up to 12 modules each
        constexpr int kCap = 12;
        const size_t np = static_cast<size_t>(ex.area());
        bk_.assign(np * kCap, -1);
        bw_.assign(np * kCap, 0.f);
        std::vector<cv::Point2f> pts;
        pts.reserve(np * kSub * kSub);
        for (int y = ex.y; y < ex.y + ex.height; ++y)
            for (int x = ex.x; x < ex.x + ex.width; ++x)
                for (int j = 0; j < kSub; ++j)
                    for (int i = 0; i < kSub; ++i)
                        pts.emplace_back(x - 0.5f + (i + 0.5f) / kSub, y - 0.5f + (j + 0.5f) / kSub);
        std::vector<cv::Point2f> m;
        cv::perspectiveTransform(pts, m, H_.inv());
        const float w1 = 1.f / (kSub * kSub);
        for (size_t p = 0; p < np; ++p)
            for (int s = 0; s < kSub * kSub; ++s) {
                const cv::Point2f& u = m[p * kSub * kSub + static_cast<size_t>(s)];
                if (!(u.x >= 0 && u.x < n_ && u.y >= 0 && u.y < n_)) continue; // light
                const int k = static_cast<int>(u.y) * n_ + static_cast<int>(u.x);
                for (int j = 0; j < kCap; ++j) {
                    int& kk = bk_[p * kCap + static_cast<size_t>(j)];
                    if (kk == k || kk < 0) {
                        kk = k;
                        bw_[p * kCap + static_cast<size_t>(j)] += w1;
                        break;
                    }
                }
            }
        // Gaussian blur of the coverage
        std::vector<float> gk(static_cast<size_t>((2 * r + 1) * (2 * r + 1)));
        float gs = 0;
        for (int dy = -r; dy <= r; ++dy)
            for (int dx = -r; dx <= r; ++dx) {
                const float g = sigma_ > 0.05f ? std::exp(-(dx * dx + dy * dy) / (2 * sigma_ * sigma_)) : (dx == 0 && dy == 0 ? 1.f : 0.f);
                gk[static_cast<size_t>((dy + r) * (2 * r + 1) + dx + r)] = g;
                gs += g;
            }
        for (auto& g : gk) g /= gs;
        rs_.assign(1, 0);
        rc_.clear();
        rv_.clear();
        std::vector<float> acc(static_cast<size_t>(nn_), 0.f);
        std::vector<int> touched;
        for (const auto& pt : at_) {
            touched.clear();
            for (int dy = -r; dy <= r; ++dy)
                for (int dx = -r; dx <= r; ++dx) {
                    const size_t q = static_cast<size_t>((pt.y + dy - ex.y) * ex.width + (pt.x + dx - ex.x));
                    const float g = gk[static_cast<size_t>((dy + r) * (2 * r + 1) + dx + r)];
                    for (int j = 0; j < kCap; ++j) {
                        const int k = bk_[q * kCap + static_cast<size_t>(j)];
                        if (k < 0) break;
                        if (acc[static_cast<size_t>(k)] == 0.f) touched.push_back(k);
                        acc[static_cast<size_t>(k)] += g * bw_[q * kCap + static_cast<size_t>(j)];
                    }
                }
            for (int k : touched) {
                if (acc[static_cast<size_t>(k)] > 1e-3f) {
                    rc_.push_back(k);
                    rv_.push_back(acc[static_cast<size_t>(k)]);
                }
                acc[static_cast<size_t>(k)] = 0.f;
            }
            rs_.push_back(static_cast<int>(rc_.size()));
        }
        // columns
        cs_.assign(static_cast<size_t>(nn_) + 1, 0);
        for (int k : rc_) cs_[static_cast<size_t>(k) + 1]++;
        for (int k = 0; k < nn_; ++k) cs_[static_cast<size_t>(k) + 1] += cs_[static_cast<size_t>(k)];
        cr_.assign(rc_.size(), 0);
        cw_.assign(rc_.size(), 0.f);
        std::vector<int> fill(cs_.begin(), cs_.end() - 1);
        for (size_t p = 0; p + 1 < rs_.size(); ++p)
            for (int e = rs_[p]; e < rs_[p + 1]; ++e) {
                const int k = rc_[static_cast<size_t>(e)];
                const int at = fill[static_cast<size_t>(k)]++;
                cr_[static_cast<size_t>(at)] = static_cast<int>(p);
                cw_[static_cast<size_t>(at)] = rv_[static_cast<size_t>(e)];
            }
    }

    // levels (plane L, contrast C) for module values d; residual I - model into r; returns SSE.
    // weight: pixels to count (null = all)
    double fit(const std::vector<float>& d, std::vector<float>& r, const std::vector<uint8_t>* weight = nullptr)
    {
        const size_t np = at_.size();
        std::vector<float> s(np, 0.f);
        for (size_t p = 0; p < np; ++p)
            for (int e = rs_[p]; e < rs_[p + 1]; ++e) s[p] += rv_[static_cast<size_t>(e)] * d[static_cast<size_t>(rc_[static_cast<size_t>(e)])];
        double M[4][4] = {}, b[4] = {};
        for (size_t p = 0; p < np; ++p) {
            if (weight && !(*weight)[p]) continue;
            const double x[4] = {1, (at_[p].x - cx_) / 20.0, (at_[p].y - cy_) / 20.0, -s[p]};
            for (int i = 0; i < 4; ++i) {
                b[i] += x[i] * v_[p];
                for (int j = 0; j < 4; ++j) M[i][j] += x[i] * x[j];
            }
        }
        for (int i = 0; i < 4; ++i) M[i][i] += 1e-6;
        cv::Mat th;
        if (!cv::solve(cv::Mat(4, 4, CV_64F, M), cv::Mat(4, 1, CV_64F, b), th, cv::DECOMP_CHOLESKY)) th = cv::Mat::zeros(4, 1, CV_64F);
        for (int i = 0; i < 4; ++i) th_[i] = th.at<double>(i);
        r.assign(np, 0.f);
        double sse = 0;
        for (size_t p = 0; p < np; ++p) {
            const double model = th_[0] + th_[1] * (at_[p].x - cx_) / 20.0 + th_[2] * (at_[p].y - cy_) / 20.0 - th_[3] * s[p];
            r[p] = static_cast<float>(v_[p] - model);
            if (!weight || (*weight)[p]) sse += static_cast<double>(r[p]) * r[p];
        }
        return sse;
    }

    // pixels mostly explained by known modules (unknown area below 0.15)
    std::vector<uint8_t> knownPixels(const std::vector<int8_t>& known) const
    {
        std::vector<uint8_t> w(at_.size(), 0);
        for (size_t p = 0; p < at_.size(); ++p) {
            float u = 0;
            for (int e = rs_[p]; e < rs_[p + 1]; ++e)
                if (known[static_cast<size_t>(rc_[static_cast<size_t>(e)])] < 0) u += rv_[static_cast<size_t>(e)];
            w[p] = u < 0.15f;
        }
        return w;
    }

    // corners and blur by coordinate search on the squared error, module values fixed
    // corners and blur by Levenberg-Marquardt on the residuals (numeric Jacobian), module values
    // fixed; onlyKnown: the pixels the known modules explain, corners only (the finders alone
    // would pull the blur wide)
    void refineGeometry(const std::vector<float>& d, const std::vector<int8_t>& known, bool onlyKnown, int iters,
                        Clock::time_point deadline)
    {
        const int np = onlyKnown ? 8 : 9;
        const size_t nr = at_.size();
        std::vector<float> r0, r1;
        build();
        const std::vector<uint8_t> w = onlyKnown ? knownPixels(known) : std::vector<uint8_t>(nr, 1);
        const auto eval = [&](const float p[9], std::vector<float>& r) {
            cv::Point2f q[4];
            for (int k = 0; k < 4; ++k) q[k] = cv::Point2f(p[2 * k], p[2 * k + 1]);
            setQuad(q);
            sigma_ = p[8];
            build();
            const double sse = fit(d, r, &w);
            for (size_t i = 0; i < nr; ++i)
                if (!w[i]) r[i] = 0.f;
            return sse;
        };
        float p[9];
        for (int k = 0; k < 4; ++k) {
            p[2 * k] = q_[k].x;
            p[2 * k + 1] = q_[k].y;
        }
        p[8] = sigma_;
        double sse0 = eval(p, r0), lambda = 1e-3;
        std::vector<float> J(nr * 9);
        for (int it = 0; it < iters && Clock::now() < deadline; ++it) {
            const float h[9] = {0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f, 0.03f};
            for (int j = 0; j < np; ++j) {
                float pj[9];
                std::copy(p, p + 9, pj);
                pj[j] += h[j];
                eval(pj, r1);
                for (size_t i = 0; i < nr; ++i) J[i * 9 + static_cast<size_t>(j)] = (r1[i] - r0[i]) / h[j];
            }
            double JtJ[9][9] = {}, Jtr[9] = {};
            for (size_t i = 0; i < nr; ++i)
                for (int a = 0; a < np; ++a) {
                    Jtr[a] += J[i * 9 + static_cast<size_t>(a)] * r0[i];
                    for (int b = 0; b < np; ++b) JtJ[a][b] += J[i * 9 + static_cast<size_t>(a)] * J[i * 9 + static_cast<size_t>(b)];
                }
            bool better = false;
            for (int tries = 0; tries < 3 && !better; ++tries) {
                cv::Mat A(np, np, CV_64F), bv(np, 1, CV_64F), delta;
                for (int a = 0; a < np; ++a) {
                    bv.at<double>(a) = -Jtr[a];
                    for (int b = 0; b < np; ++b) A.at<double>(a, b) = JtJ[a][b] + (a == b ? lambda * (JtJ[a][a] + 1e-6) : 0.0);
                }
                if (!cv::solve(A, bv, delta, cv::DECOMP_CHOLESKY)) break;
                float pn[9];
                std::copy(p, p + 9, pn);
                for (int a = 0; a < np; ++a) pn[a] += static_cast<float>(std::clamp(delta.at<double>(a), -1.0, 1.0));
                pn[8] = std::clamp(pn[8], 0.1f, 1.5f);
                const double sse = eval(pn, r1);
                if (sse < sse0) {
                    better = true;
                    const double gain = (sse0 - sse) / sse0;
                    std::copy(pn, pn + 9, p);
                    std::swap(r0, r1);
                    sse0 = sse;
                    lambda = std::max(1e-6, lambda / 3);
                    if (gain < 1e-3) it = iters;
                } else
                    lambda *= 8;
            }
            if (!better) break;
        }
        cv::Point2f q[4];
        for (int k = 0; k < 4; ++k) q[k] = cv::Point2f(p[2 * k], p[2 * k + 1]);
        setQuad(q);
        sigma_ = p[8];
        build();
    }

    // continuous darkness of the unknown modules: ridge least squares by conjugate gradients
    std::vector<float> leastSquares(const std::vector<int8_t>& known, float reg = 0.3f)
    {
        std::vector<float> d(static_cast<size_t>(nn_));
        for (int k = 0; k < nn_; ++k) d[static_cast<size_t>(k)] = known[static_cast<size_t>(k)] >= 0 ? known[static_cast<size_t>(k)] : 0.5f;
        std::vector<float> r;
        const size_t np = at_.size();
        for (int round = 0; round < 2; ++round) {
            fit(d, r);
            const double c = std::max(1e-6, th_[3]);
            // r = I - model, model = L - c A d; target for the unknown part: A_U x = A_U d_U - r / c
            std::vector<double> t(np), x(static_cast<size_t>(nn_), 0.0), b(static_cast<size_t>(nn_), 0.0);
            for (size_t p = 0; p < np; ++p) {
                double su = 0;
                for (int e = rs_[p]; e < rs_[p + 1]; ++e) {
                    const int k = rc_[static_cast<size_t>(e)];
                    if (known[static_cast<size_t>(k)] < 0) su += rv_[static_cast<size_t>(e)] * d[static_cast<size_t>(k)];
                }
                t[p] = su - r[p] / c;
            }
            auto apply = [&](const std::vector<double>& in, std::vector<double>& out) { // (A_U' A_U + reg) in
                std::vector<double> y(np, 0.0);
                for (size_t p = 0; p < np; ++p)
                    for (int e = rs_[p]; e < rs_[p + 1]; ++e) {
                        const int k = rc_[static_cast<size_t>(e)];
                        if (known[static_cast<size_t>(k)] < 0) y[p] += rv_[static_cast<size_t>(e)] * in[static_cast<size_t>(k)];
                    }
                out.assign(static_cast<size_t>(nn_), 0.0);
                for (int k = 0; k < nn_; ++k) {
                    if (known[static_cast<size_t>(k)] >= 0) continue;
                    double a = reg * in[static_cast<size_t>(k)];
                    for (int e = cs_[static_cast<size_t>(k)]; e < cs_[static_cast<size_t>(k) + 1]; ++e)
                        a += cw_[static_cast<size_t>(e)] * y[static_cast<size_t>(cr_[static_cast<size_t>(e)])];
                    out[static_cast<size_t>(k)] = a;
                }
            };
            for (int k = 0; k < nn_; ++k) {
                if (known[static_cast<size_t>(k)] >= 0) continue;
                double a = reg * 0.5;
                for (int e = cs_[static_cast<size_t>(k)]; e < cs_[static_cast<size_t>(k) + 1]; ++e)
                    a += cw_[static_cast<size_t>(e)] * t[static_cast<size_t>(cr_[static_cast<size_t>(e)])];
                b[static_cast<size_t>(k)] = a;
                x[static_cast<size_t>(k)] = 0.5;
            }
            std::vector<double> Ax, res(static_cast<size_t>(nn_)), dir, Ad;
            apply(x, Ax);
            for (int k = 0; k < nn_; ++k) res[static_cast<size_t>(k)] = b[static_cast<size_t>(k)] - Ax[static_cast<size_t>(k)];
            dir = res;
            double rr = std::inner_product(res.begin(), res.end(), res.begin(), 0.0);
            for (int it = 0; it < 60 && rr > 1e-10; ++it) {
                apply(dir, Ad);
                const double a = rr / std::max(1e-30, std::inner_product(dir.begin(), dir.end(), Ad.begin(), 0.0));
                for (int k = 0; k < nn_; ++k) {
                    x[static_cast<size_t>(k)] += a * dir[static_cast<size_t>(k)];
                    res[static_cast<size_t>(k)] -= a * Ad[static_cast<size_t>(k)];
                }
                const double rr2 = std::inner_product(res.begin(), res.end(), res.begin(), 0.0);
                for (int k = 0; k < nn_; ++k) dir[static_cast<size_t>(k)] = res[static_cast<size_t>(k)] + rr2 / rr * dir[static_cast<size_t>(k)];
                rr = rr2;
            }
            for (int k = 0; k < nn_; ++k)
                if (known[static_cast<size_t>(k)] < 0) d[static_cast<size_t>(k)] = static_cast<float>(std::clamp(x[static_cast<size_t>(k)], 0.0, 1.0));
        }
        return d;
    }

    // binary modules by 3 x 3 neighbourhood pattern tests; conf = rise in squared error on a flip
    // (in noise variances), over the neighbourhoods containing the module
    void solve(std::vector<float>& d, const std::vector<int8_t>& known, std::vector<float>& conf, Clock::time_point deadline)
    {
        for (int k = 0; k < nn_; ++k)
            d[static_cast<size_t>(k)] = known[static_cast<size_t>(k)] >= 0 ? known[static_cast<size_t>(k)] : (d[static_cast<size_t>(k)] > 0.5f ? 1.f : 0.f);
        std::vector<int> unk;
        std::vector<std::pair<int, int>> byKnown; // (-known neighbours, module): inward from the known
        for (int k = 0; k < nn_; ++k) {
            if (known[static_cast<size_t>(k)] >= 0) continue;
            const int y = k / n_, x = k % n_;
            int kn = 0;
            for (int yy = y - 1; yy <= y + 1; ++yy)
                for (int xx = x - 1; xx <= x + 1; ++xx)
                    kn += yy < 0 || xx < 0 || yy >= n_ || xx >= n_ || known[static_cast<size_t>(yy * n_ + xx)] >= 0;
            byKnown.emplace_back(-kn, k);
        }
        std::stable_sort(byKnown.begin(), byKnown.end());
        for (const auto& b : byKnown) unk.push_back(b.second);
        std::vector<float> r;
        fit(d, r);
        std::vector<int> stamp(at_.size(), -1), rowAt(at_.size(), 0), rows;
        std::vector<float> Ab, R, e(512), cover;
        int stampNo = 0;
        // the squared error of every pattern of the block around m (residual r, current d)
        auto patterns = [&](int m, std::vector<int>& blk) {
            blk.clear();
            const int y = m / n_, x = m % n_;
            for (int yy = y - 1; yy <= y + 1; ++yy)
                for (int xx = x - 1; xx <= x + 1; ++xx)
                    if (yy >= 0 && xx >= 0 && yy < n_ && xx < n_ && known[static_cast<size_t>(yy * n_ + xx)] < 0) blk.push_back(yy * n_ + xx);
            // the pixels the neighbourhood covers by a tenth or more (the fringe says little)
            rows.clear();
            cover.clear();
            ++stampNo;
            for (int k : blk)
                for (int q = cs_[static_cast<size_t>(k)]; q < cs_[static_cast<size_t>(k) + 1]; ++q) {
                    const int p = cr_[static_cast<size_t>(q)];
                    if (stamp[static_cast<size_t>(p)] != stampNo) {
                        stamp[static_cast<size_t>(p)] = stampNo;
                        rowAt[static_cast<size_t>(p)] = static_cast<int>(rows.size());
                        rows.push_back(p);
                        cover.push_back(0.f);
                    }
                    cover[static_cast<size_t>(rowAt[static_cast<size_t>(p)])] += cw_[static_cast<size_t>(q)];
                }
            size_t kept = 0;
            for (size_t i = 0; i < rows.size(); ++i)
                if (cover[i] >= 0.1f) {
                    rowAt[static_cast<size_t>(rows[i])] = static_cast<int>(kept);
                    rows[kept++] = rows[i];
                } else
                    stamp[static_cast<size_t>(rows[i])] = -1;
            rows.resize(kept);
            const size_t nr = rows.size(), nb = blk.size();
            Ab.assign(nr * nb, 0.f);
            for (size_t j = 0; j < nb; ++j) {
                const int k = blk[j];
                for (int q = cs_[static_cast<size_t>(k)]; q < cs_[static_cast<size_t>(k) + 1]; ++q) {
                    const int p = cr_[static_cast<size_t>(q)];
                    if (stamp[static_cast<size_t>(p)] == stampNo) Ab[static_cast<size_t>(rowAt[static_cast<size_t>(p)]) * nb + j] = cw_[static_cast<size_t>(q)];
                }
            }
            const float c = static_cast<float>(th_[3]);
            // start at the all-light pattern, then walk the Gray code
            R.assign(nr, 0.f);
            for (size_t i = 0; i < nr; ++i) {
                float v = r[static_cast<size_t>(rows[i])];
                for (size_t j = 0; j < nb; ++j) v -= c * Ab[i * nb + j] * d[static_cast<size_t>(blk[j])];
                R[i] = v;
            }
            int pat = 0;
            const int count = 1 << nb;
            for (int g = 0; g < count; ++g) {
                if (g > 0) {
                    int j = 0;
                    while (!((g >> j) & 1)) ++j; // the bit the Gray code flips
                    pat ^= 1 << j;
                    const float sg = (pat >> j) & 1 ? c : -c;
                    for (size_t i = 0; i < nr; ++i) R[i] += sg * Ab[i * nb + static_cast<size_t>(j)];
                }
                float s = 0;
                for (size_t i = 0; i < nr; ++i) s += R[i] * R[i];
                e[static_cast<size_t>(pat)] = s;
            }
        };
        std::vector<int> blk;
        // a block is tested again only when a module within two of its centre changed
        std::vector<uint8_t> dirty(static_cast<size_t>(nn_), 1);
        for (int sweep = 0; sweep < 8 && Clock::now() < deadline; ++sweep) {
            int changed = 0;
            for (int m : unk) {
                if (!dirty[static_cast<size_t>(m)]) continue;
                dirty[static_cast<size_t>(m)] = 0;
                patterns(m, blk);
                const int nb = static_cast<int>(blk.size());
                int cur = 0;
                for (int j = 0; j < nb; ++j) cur |= (d[static_cast<size_t>(blk[static_cast<size_t>(j)])] > 0.5f ? 1 : 0) << j;
                int best = cur;
                for (int pt = 0; pt < (1 << nb); ++pt)
                    if (e[static_cast<size_t>(pt)] < e[static_cast<size_t>(best)] - 1e-4f) best = pt;
                if (best == cur) continue;
                const float c = static_cast<float>(th_[3]);
                for (int j = 0; j < nb; ++j) {
                    const float nv = static_cast<float>((best >> j) & 1), dv = nv - d[static_cast<size_t>(blk[static_cast<size_t>(j)])];
                    if (dv == 0.f) continue;
                    const int k = blk[static_cast<size_t>(j)];
                    for (int q = cs_[static_cast<size_t>(k)]; q < cs_[static_cast<size_t>(k) + 1]; ++q)
                        r[static_cast<size_t>(cr_[static_cast<size_t>(q)])] += c * dv * cw_[static_cast<size_t>(q)];
                    d[static_cast<size_t>(k)] = nv;
                    const int ky = k / n_, kx = k % n_;
                    for (int yy = std::max(0, ky - 2); yy <= std::min(n_ - 1, ky + 2); ++yy)
                        for (int xx = std::max(0, kx - 2); xx <= std::min(n_ - 1, kx + 2); ++xx) dirty[static_cast<size_t>(yy * n_ + xx)] = 1;
                }
                ++changed;
            }
            fit(d, r);
            if (!changed) break;
        }
        double var = 0;
        for (float v : r) var += static_cast<double>(v) * v;
        var = std::max(1e-6, var / std::max<size_t>(1, r.size()));
        var_ = var;
        conf.assign(static_cast<size_t>(nn_), 1e9f);
        for (int m : unk) {
            patterns(m, blk);
            const int nb = static_cast<int>(blk.size());
            int cur = 0;
            for (int j = 0; j < nb; ++j) cur |= (d[static_cast<size_t>(blk[static_cast<size_t>(j)])] > 0.5f ? 1 : 0) << j;
            for (int j = 0; j < nb; ++j) {
                float mn = 1e30f;
                for (int pt = 0; pt < (1 << nb); ++pt)
                    if (((pt ^ cur) >> j) & 1) mn = std::min(mn, e[static_cast<size_t>(pt)]);
                float& cf = conf[static_cast<size_t>(blk[static_cast<size_t>(j)])];
                cf = std::min(cf, static_cast<float>((mn - e[static_cast<size_t>(cur)]) / (2 * var)));
            }
        }
    }

    double contrast() const { return th_[3]; }
    double noise() const { return std::sqrt(var_); }
    const cv::Point2f* quad() const { return q_; }
    void setSigma(float s) { sigma_ = s; }
    float sigma() const { return sigma_; }

private:
    const cv::Mat& g_;
    int n_, nn_;
    cv::Point2f q_[4];
    cv::Mat H_;
    float sigma_ = 0.6f;
    std::vector<cv::Point> at_;
    std::vector<float> v_;
    cv::Rect box_;
    float cx_ = 0, cy_ = 0;
    std::vector<int> bk_;
    std::vector<float> bw_;
    std::vector<int> rs_, rc_, cs_, cr_;
    std::vector<float> rv_, cw_;
    double th_[4] = {};
    double var_ = 1;
};

} // namespace

std::string underresDecodeQr(const cv::Mat& gray, const cv::Point2f quadIn[4], int n, const std::vector<std::string>* texts,
                             Clock::time_point deadline)
{
    const int version = (n - 17) / 4;
    if (version < 1 || version > 6 || 17 + 4 * version != n || gray.type() != CV_8U) return {};
    const auto late = [&]() { return Clock::now() >= deadline; };
    const std::vector<int> fmtPos = formatPositions(n);
    std::vector<uint8_t> isFmt(static_cast<size_t>(n) * n, 0);
    for (int p : fmtPos) isFmt[static_cast<size_t>(p)] = 1;
    // known before the format is read: function modules
    std::vector<int8_t> known0 = qrKnownModules(version, 1, 0, nullptr, nullptr);
    for (int p : fmtPos) known0[static_cast<size_t>(p)] = -1;
    // a region crop can cut the code or its quiet zone: look past it into the whole image
    cv::Mat view = gray;
    cv::Point2f quadV[4];
    {
        cv::Size whole;
        cv::Point ofs;
        gray.locateROI(whole, ofs);
        const int m = 24;
        view.adjustROI(m, m, m, m);
        cv::Point ofs2;
        view.locateROI(whole, ofs2);
        for (int k = 0; k < 4; ++k) quadV[k] = quadIn[k] + cv::Point2f(static_cast<float>(ofs.x - ofs2.x), static_cast<float>(ofs.y - ofs2.y));
    }
    Solver S(view, n);
    if (!S.setPixels(quadV)) return {};
    // orientation: of the four rotations, the one the function modules explain best (finders and
    // timing are symmetric about the diagonal, so a mirrored order cannot be told apart here: the
    // corners are taken clockwise on the image, as a printed code is seen)
    cv::Point2f quadCw[4];
    std::copy(quadV, quadV + 4, quadCw);
    {
        const cv::Point2f a = quadV[1] - quadV[0], b = quadV[3] - quadV[0];
        if (a.x * b.y - a.y * b.x < 0) std::swap(quadCw[1], quadCw[3]);
    }
    {
        std::vector<float> d0(known0.size()), r;
        for (size_t k = 0; k < known0.size(); ++k) d0[k] = known0[k] >= 0 ? known0[k] : 0.5f;
        double best = 1e300;
        cv::Point2f bestQ[4];
        for (int o = 0; o < 4; ++o) {
            cv::Point2f q[4];
            for (int k = 0; k < 4; ++k) q[k] = quadCw[(k + o) % 4];
            S.setQuad(q);
            S.build();
            const auto w = S.knownPixels(known0);
            const double sse = S.fit(d0, r, &w);
            if (S.contrast() > 0 && sse < best) {
                best = sse;
                std::copy(q, q + 4, bestQ);
            }
        }
        if (best >= 1e300) return {};
        S.setQuad(bestQ);
    }
    // CR_UPROF=1: one line per call on stderr (where it started, how far it got, in what time)
    const bool prof = std::getenv("CR_UPROF") != nullptr;
    const auto t0 = Clock::now();
    const auto ms = [&]() { return std::chrono::duration<double, std::milli>(Clock::now() - t0).count(); };
    std::string plog;
    struct ProfOut {
        bool on;
        const std::string* log;
        ~ProfOut()
        {
            if (on) std::fprintf(stderr, "underres%s\n", log->c_str());
        }
    } pout{prof, &plog};
    const auto note = [&](const char* what) {
        if (!prof) return;
        char buf[160];
        std::snprintf(buf, sizeof buf, " %s@%.0f", what, ms());
        plog += buf;
    };
    if (prof) {
        cv::Size whole;
        cv::Point ofs;
        view.locateROI(whole, ofs);
        char buf[96];
        std::snprintf(buf, sizeof buf, " at %.0f,%.0f n %d:", quadV[0].x + ofs.x, quadV[0].y + ofs.y, n);
        plog += buf;
    }
    std::vector<float> d(known0.size()), conf;
    for (size_t k = 0; k < known0.size(); ++k) d[k] = known0[k] >= 0 ? known0[k] : 0.5f;
    S.refineGeometry(d, known0, true, 2, deadline);
    note("corners");
    if (late()) return {};
    d = S.leastSquares(known0);
    S.refineGeometry(d, known0, false, 4, deadline);
    note("geometry");
    if (late()) return {};
    d = S.leastSquares(known0);
    S.solve(d, known0, conf, deadline);
    // a code has contrast well above the noise; anything else is not worth the rest
    if (prof) {
        char buf[96];
        std::snprintf(buf, sizeof buf, " solved@%.0f (%d builds, contrast %.1f noise %.2f blur %.2f)", ms(), S.builds, S.contrast(), S.noise(), S.sigma());
        plog += buf;
    }
    if (late() || S.contrast() < 1.5 * S.noise()) return {};

    // EC level and mask: the solved format modules weighted by their confidence
    std::vector<std::tuple<double, int, int>> fmts;
    for (int e = 0; e < 4; ++e)
        for (int m = 0; m < 8; ++m) {
            const auto ref = qrKnownModules(version, e, m, nullptr, nullptr);
            double sc = 0;
            for (int p : fmtPos) {
                const double w = std::clamp(static_cast<double>(conf[static_cast<size_t>(p)]), 0.0, 20.0);
                sc += ((d[static_cast<size_t>(p)] > 0.5f) == (ref[static_cast<size_t>(p)] != 0) ? w : -w);
            }
            fmts.emplace_back(sc, e, m);
        }
    std::sort(fmts.begin(), fmts.end(), [](const auto& a, const auto& b) { return std::get<0>(a) > std::get<0>(b); });
    for (size_t fi = 0; fi < 2 && !late(); ++fi) {
        const int e = std::get<1>(fmts[fi]), m = std::get<2>(fmts[fi]);
        // the format known, then (texts) the content shared with the case's codes as well; the
        // geometry and the solved modules carry over
        for (int withTexts = 0; withTexts < (texts && texts->size() >= 2 ? 2 : 1) && !late(); ++withTexts) {
            std::vector<int> knownCw;
            const auto known = qrKnownModules(version, e, m, withTexts ? texts : nullptr, &knownCw);
            std::vector<float> d2 = d;
            if (withTexts) {
                // the shared content assumes how the printer encodes (digit runs numeric): when the
                // solved modules disagree with it, it is another encoding and would only mislead
                int same = 0, all = 0;
                for (size_t k = 0; k < known.size(); ++k)
                    if (known[k] >= 0 && known0[k] < 0 && !isFmt[k]) {
                        ++all;
                        same += (d[k] > 0.5f) == (known[k] != 0);
                    }
                if (prof) plog += " shared " + std::to_string(same) + "/" + std::to_string(all);
                if (all == 0 || same * 10 < all * 9) break;
            }
            S.solve(d2, known, conf, deadline);
            std::vector<uint8_t> dark(d2.size());
            for (size_t k = 0; k < d2.size(); ++k) dark[k] = d2[k] > 0.5f;
            const std::string text = decodeQrSolved(dark, conf, version, e, m, knownCw, withTexts ? texts : nullptr);
            if (prof) plog += " format " + std::to_string(e) + "/" + std::to_string(m) + " -> " + (text.empty() ? "-" : text);
            if (!text.empty()) return text;
            if (fi == 0 && !withTexts) d = d2; // the format's own solution for the next try
        }
    }
    return {};
}

#endif

} // namespace cr
