// measure_tool.cpp - the Measure tool (vs_measure.h): calipers, line, circle and ellipse fit, corner, gap,
// pin pitch and edge count.
//
// Caliper: the box is averaged across its height into a profile (bilinear samples one pixel apart,
// reaching a little past each end so an edge at the end is seen whole), smoothed and differentiated in
// one pass with a sampled Gaussian derivative, and each peak of the derivative above the threshold is an
// edge. Its sub-pixel place is the centroid of the derivative over the peak's reach, exact for an
// area-sampled step whatever the smoothing (a parabola through the peak is biased by up to 0.09 px on a
// sharp edge along the pixel grid). Lines, circles and ellipses are calipers across the expected edge, fitted
// with RANSAC and then least squares on the points that agree, all in measure space (lens distortion taken out).
// A corner is two lines met; a gap is pair calipers in slices across a box; pins and edge counts are every
// edge of one profile along a box.
#include "vs_measure.h"

#include <opencv2/core.hpp>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <vector>

namespace {

constexpr double kPi = 3.14159265358979323846;

struct Pt {
    double x, y;
};

// results: the caller sets struct_size to the size it was built with (at least this library's), and the
// library fills its own and says so
template <class R> bool resultOk(const R* r) { return r && r->struct_size >= static_cast<int32_t>(sizeof(R)); }
template <class R> void resetResult(R& r)
{
    r = R{};
    r.struct_size = static_cast<int32_t>(sizeof(R));
}

bool hasLens(const vs_meas_calib* c) { return c && c->fx > 0.f && c->fy > 0.f; }
double mmPerPx(const vs_meas_calib* c) { return c && c->mm_per_px > 0.f ? c->mm_per_px : 0.0; }

// the inverse of the Brown-Conrady model by fixed-point iteration (as calibration.cpp and OpenCV's
// undistortPoints), so sizes here agree with the reader's own mm
Pt undistort(const vs_meas_calib* c, Pt p)
{
    if (!hasLens(c)) return p;
    const double x0 = (p.x - c->cx) / c->fx, y0 = (p.y - c->cy) / c->fy;
    double x = x0, y = y0;
    for (int it = 0; it < 8; ++it) {
        const double r2 = x * x + y * y;
        const double radial = 1 + c->k1 * r2 + c->k2 * r2 * r2 + c->k3 * r2 * r2 * r2;
        const double dx = 2 * c->p1 * x * y + c->p2 * (r2 + 2 * x * x), dy = c->p1 * (r2 + 2 * y * y) + 2 * c->p2 * x * y;
        x = (x0 - dx) / radial;
        y = (y0 - dy) / radial;
    }
    return {x * c->fx + c->cx, y * c->fy + c->cy};
}

Pt distort(const vs_meas_calib* c, Pt p)
{
    if (!hasLens(c)) return p;
    const double x = (p.x - c->cx) / c->fx, y = (p.y - c->cy) / c->fy, r2 = x * x + y * y;
    const double radial = 1 + c->k1 * r2 + c->k2 * r2 * r2 + c->k3 * r2 * r2 * r2;
    const double xd = x * radial + 2 * c->p1 * x * y + c->p2 * (r2 + 2 * x * x);
    const double yd = y * radial + c->p1 * (r2 + 2 * y * y) + 2 * c->p2 * x * y;
    return {xd * c->fx + c->cx, yd * c->fy + c->cy};
}

struct Image {
    const uint8_t* px;
    int w, h, stride;
    double at(double x, double y) const
    {
        const int x0 = static_cast<int>(x), y0 = static_cast<int>(y);
        const int x1 = std::min(x0 + 1, w - 1), y1 = std::min(y0 + 1, h - 1);
        const double fx = x - x0, fy = y - y0;
        const uint8_t* r0 = px + static_cast<size_t>(y0) * stride;
        const uint8_t* r1 = px + static_cast<size_t>(y1) * stride;
        return (r0[x0] * (1 - fx) + r0[x1] * fx) * (1 - fy) + (r1[x0] * (1 - fx) + r1[x1] * fx) * fy;
    }
};

struct Edge {
    double pos, contrast; // contrast signed, + = dark to light along the box
};

struct Profile {
    Pt start, u;
    double length = 0;
    std::vector<Edge> edges; // above the threshold, any polarity, strongest first
    double threshold = 0;
};

bool polarityOk(int polarity, double contrast)
{
    return polarity == VS_MEAS_ANY || (polarity == VS_MEAS_DARK_TO_LIGHT ? contrast > 0 : contrast < 0);
}

// Where an edge's response meets a neighbour's (pins, threads), its centroid over the samples of one sign is
// cut unevenly by the sample grid (up to half a pixel off). Instead: the centroid of the linearly interpolated
// response over a window symmetric about the estimate, as wide as the nearer zero crossing (at most R + 1),
// iterated from the parabola's estimate. Positions in box samples (index q - E).
double symmetricCentroid(const std::vector<double>& d, int q, int E, int R, double start)
{
    const int N = static_cast<int>(d.size());
    const bool up = d[static_cast<size_t>(q)] > 0;
    const auto at = [&](double t) { // linear interpolation at box position t
        const double u = t + E;
        const int k = std::clamp(static_cast<int>(std::floor(u)), 0, N - 2);
        const double f = u - k, v = d[static_cast<size_t>(k)] * (1 - f) + d[static_cast<size_t>(k + 1)] * f;
        return up ? v : -v;
    };
    // the zero crossings either side of the peak
    double zl = q - E - (R + 1), zr = q - E + (R + 1);
    for (int k = q; k > std::max(0, q - R - 1); --k)
        if ((d[static_cast<size_t>(k - 1)] > 0) != up || d[static_cast<size_t>(k - 1)] == 0) {
            const double a = std::abs(d[static_cast<size_t>(k)]), b = std::abs(d[static_cast<size_t>(k - 1)]);
            const double side = (d[static_cast<size_t>(k - 1)] > 0) != up ? b : 0;
            zl = k - E - a / (a + side + 1e-12);
            break;
        }
    for (int k = q; k < std::min(N - 1, q + R + 1); ++k)
        if ((d[static_cast<size_t>(k + 1)] > 0) != up || d[static_cast<size_t>(k + 1)] == 0) {
            const double a = std::abs(d[static_cast<size_t>(k)]), b = std::abs(d[static_cast<size_t>(k + 1)]);
            const double side = (d[static_cast<size_t>(k + 1)] > 0) != up ? b : 0;
            zr = k - E + a / (a + side + 1e-12);
            break;
        }
    double c = start;
    for (int it = 0; it < 4; ++it) {
        const double w = std::min(c - zl, zr - c);
        if (!(w > 0.25)) return start;
        constexpr int S = 64;
        double m0 = 0, m1 = 0;
        for (int j = 0; j < S; ++j) {
            const double t = c - w + 2 * w * (j + 0.5) / S, v = std::max(0.0, at(t));
            m0 += v;
            m1 += v * t;
        }
        if (!(m0 > 0)) return start;
        c = m1 / m0;
    }
    return c;
}

// Lanczos-3 interpolation weights about x: w[k] for sample floor(x) - 2 + k, summing to 1
void lanczos3(double x, int& base, double w[6])
{
    base = static_cast<int>(std::floor(x));
    double sum = 0;
    for (int k = 0; k < 6; ++k) {
        const double t = x - (base - 2 + k);
        w[k] = std::abs(t) < 1e-9 ? 1 : std::abs(t) >= 3 ? 0 : 3 * std::sin(kPi * t) * std::sin(kPi * t / 3) / (kPi * kPi * t * t);
        sum += w[k];
    }
    for (int k = 0; k < 6; ++k) w[k] /= sum;
}

// An edge with a neighbour's response on one side only (the first or last edge of a row, either edge of a lone
// narrow pin): the response is lopsided, so even the symmetric centroid leans outwards (0.35 px at a 1 px blur and
// 3.2 px between edges, 0.6 px at 2.2 px). The edges of one profile share one blur, and the edge's free side shows its
// response clean: mirrored about the edge it is the template. The row is drawn again from it, sample by sample (the
// edge and up to three neighbours on the cut side, heights by least squares), and the edge is placed where the
// drawing fits the profile best: Gauss-Newton steps on the misfit, each kept only while it lowers it. The neighbours' tails
// that reach the free side are taken off the template (three rounds). No blur model is assumed (a defocus disk is as
// exact as a Gaussian). A fit over the whole shape is needed: matching only the drawing's centroid left the place
// nearly free, as a template taken about a wrong place leans almost like the profile (rows closer than 2.6 px stayed
// 0.2 px off). A neighbour with another edge beyond it is exact as measured and only drawn; one with nothing beyond
// (the far edge of a lone pin) is a row end itself and is fitted with the edge. An edge whose response does not reach
// halfway to its neighbour does not lean and is left as measured.
// meas: the edge's symmetric centroid; dir: the cut side (-1 left, +1 right). Positions in box samples (index q - E).
// farPeak, farPos: the far end of a lone pin or short row fitted with the edge (its peak sample, else -1), which the
// caller takes for that edge instead of fitting the same row again from its side.
double endEdge(const std::vector<double>& d, int q, int E, int R, double thr, int dir, double meas, int& farPeak, double& farPos)
{
    farPeak = -1;
    const int N = static_cast<int>(d.size()), lo = R, hi = N - R - 1; // d is valid on lo .. hi
    const auto D = [&](int i) { return d[static_cast<size_t>(i)]; };
    // neighbours: the next local peaks of |d| on the cut side above a fifth of the threshold and a tenth of the edge
    // (a noise ripple far past a lone pin is not a neighbour)
    constexpr int kMaxNb = 3;
    const int reach = (kMaxNb + 1) * (R + 1);
    int pk[kMaxNb + 1] = {q}, M = 1; // peak samples, the edge first
    bool beyond = false; // another edge past the last neighbour (looked for one reach further)
    bool fallen = false; // past the edge's own response (a flat top's second sample is not a neighbour)
    for (int i = q + dir; std::abs(i - q) <= reach + R + 1 && i - 1 >= lo && i + 1 <= hi; i += dir) {
        fallen = fallen || (D(i) > 0) != (D(q) > 0) || std::abs(D(i)) < 0.5 * std::abs(D(q));
        if (fallen && std::abs(D(i)) >= std::abs(D(i - dir)) && std::abs(D(i)) > std::abs(D(i + dir)) &&
            std::abs(D(i)) > std::max(0.2 * thr, 0.1 * std::abs(D(q)))) {
            // a gap wider than the response's reach ends the row (the next pin of a pitch row is not drawn: it
            // leans itself, and the last edge before the gap has a free side)
            if (std::abs(i - pk[M - 1]) > 2 * (R + 1)) break;
            if (M == kMaxNb + 1 || std::abs(i - q) > reach) {
                beyond = true;
                break;
            }
            pk[M++] = i;
        }
    }
    if (M == 1) return meas;
    // Lanczos-3 interpolation of f at index position x (0 off the valid range)
    const auto interp = [&](const std::vector<double>& f, double x) {
        if (!(x >= lo && x <= hi)) return 0.0;
        int base;
        double w[6];
        lanczos3(x, base, w);
        double sum = 0;
        for (int k = 0; k < 6; ++k) sum += w[k] * f[static_cast<size_t>(std::clamp(base - 2 + k, lo, hi))];
        return sum;
    };
    // places (p, from the symmetric centroids) and heights (a); edge j is fitted when nothing lies past it
    double p[kMaxNb + 1], a[kMaxNb + 1] = {};
    for (int j = 0; j < M; ++j) p[j] = j == 0 ? meas : symmetricCentroid(d, pk[j], E, R, pk[j] - E);
    // responses that do not reach halfway to the neighbour do not lean (a sharp edge, whose template could not be
    // shifted by a fraction of a pixel anyway)
    const double peak0 = interp(d, p[0] + E);
    if (!(std::abs(peak0) > 0) || !(interp(d, p[0] + E - dir * std::abs(p[1] - p[0]) / 2) / peak0 > 0.05)) return meas;
    // the template's reach: the free side while its response keeps falling (not into another feature), R + 2 .. 2R + 1
    int V = 1;
    for (; V < 2 * R + 1; ++V) {
        const int i = q - dir * V, i1 = i - dir;
        if (i1 < lo || i1 > hi || (D(i1) > 0) != (D(q) > 0) || !(std::abs(D(i1)) < std::abs(D(i)))) break;
    }
    V = std::max(V, R + 2);
    // the fitted samples: the edge's free side to the template's reach, and on the cut side to the last drawn edge's
    // reach, or, with an edge beyond it that is not drawn, to halfway between the last two drawn edges
    const int f0 = pk[0] - dir * V;
    const int f1 = beyond ? (M >= 3 ? (pk[M - 2] + pk[M - 1]) / 2 : pk[M - 1]) : pk[M - 1] + dir * V;
    const int s0 = std::max(lo, std::min(f0, f1)), s1 = std::min(hi, std::max(f0, f1));
    // where the template (and so the tails taken off it) is read: about the edge, which the fit moves 1.5 px at most
    const int r0 = std::max(lo, pk[0] - V - 6), r1 = std::min(hi, pk[0] + V + 6);
    std::vector<double> clean(d.size(), 0.0), nb(d.size(), 0.0), next(d.size(), 0.0);
    // the misfit of the row drawn with the edges at pv: the template from the free side about pv[0], the neighbours'
    // tails taken off it, the heights by least squares; res: the residual of each fitted sample
    const auto misfit = [&](const double* pv, double* res) {
        std::fill(nb.begin() + r0, nb.begin() + r1 + 1, 0.0);
        double ss = 0;
        constexpr int kRounds = 3;
        for (int round = 0; round < kRounds; ++round) {
            for (int i = r0; i <= r1; ++i) clean[static_cast<size_t>(i)] = D(i) - nb[static_cast<size_t>(i)];
            const double a0 = interp(clean, pv[0] + E);
            if (!(std::abs(a0) > 0)) return std::numeric_limits<double>::infinity();
            // edge j's response at sample i is the template at |i - E - pv[j]|, read at pv[0] + E -+ that: on either
            // side of the edge one fraction, so one set of weights per side
            int base[kMaxNb + 1][2];
            double w[kMaxNb + 1][2][6];
            for (int j = 0; j < M; ++j) {
                lanczos3(pv[0] + E + dir * (E + pv[j]), base[j][0], w[j][0]); // samples at or past the edge, index i: - dir * i
                lanczos3(pv[0] + E - dir * (E + pv[j]), base[j][1], w[j][1]); // samples before it: + dir * i
            }
            const auto T = [&](int j, int i) {
                if (std::abs(i - E - pv[j]) > V) return 0.0;
                const int side = i - E >= pv[j] ? 0 : 1, b = side == 0 ? base[j][0] - dir * i : base[j][1] + dir * i;
                if (b < lo || b > hi) return 0.0;
                double v = 0;
                for (int k = 0; k < 6; ++k) v += w[j][side][k] * clean[static_cast<size_t>(std::clamp(b - 2 + k, lo, hi))];
                return v / a0;
            };
            double A[kMaxNb + 1][kMaxNb + 2] = {};
            double B[kMaxNb + 1];
            for (int i = s0; i <= s1; ++i) {
                for (int j = 0; j < M; ++j) B[j] = T(j, i);
                for (int j = 0; j < M; ++j) {
                    for (int k = 0; k < M; ++k) A[j][k] += B[j] * B[k];
                    A[j][M] += B[j] * D(i);
                }
            }
            for (int c = 0; c < M; ++c) { // Gauss-Jordan on the normal equations
                if (!(std::abs(A[c][c]) > 1e-12)) return std::numeric_limits<double>::infinity();
                for (int r = 0; r < M; ++r)
                    if (r != c) {
                        const double f = A[r][c] / A[c][c];
                        for (int k = c; k <= M; ++k) A[r][k] -= f * A[c][k];
                    }
            }
            for (int j = 0; j < M; ++j) a[j] = A[j][M] / A[j][j];
            if (round < kRounds - 1) {
                // the neighbours' tails, taken off the profile for the next round's template
                for (int i = r0; i <= r1; ++i) {
                    double v = 0;
                    for (int j = 1; j < M; ++j) v += a[j] * T(j, i);
                    next[static_cast<size_t>(i)] = v;
                }
                nb.swap(next);
            } else
                for (int i = s0; i <= s1; ++i) {
                    double v = D(i);
                    for (int j = 0; j < M; ++j) v -= a[j] * T(j, i);
                    res[i - s0] = v;
                    ss += v * v;
                }
        }
        return ss;
    };
    // Gauss-Newton steps on the fitted places (the edge, and the far end of a lone pin or short row), the residuals'
    // slopes by differences
    int U[2] = {0, 0}, nu = 1;
    if (!beyond) U[nu++] = M - 1;
    const int ns = s1 - s0 + 1;
    std::vector<double> res(static_cast<size_t>(ns)), trial(static_cast<size_t>(ns)), J(static_cast<size_t>(2 * ns));
    double cur = misfit(p, res.data()); // the misfit at p
    for (int it = 0; it < 8 && std::isfinite(cur); ++it) {
        constexpr double h = 0.005;
        for (int u = 0; u < nu; ++u) {
            p[U[u]] += h;
            misfit(p, J.data() + u * ns);
            p[U[u]] -= h;
            for (int k = 0; k < ns; ++k) J[static_cast<size_t>(u * ns + k)] = (J[static_cast<size_t>(u * ns + k)] - res[static_cast<size_t>(k)]) / h;
        }
        double A[2][2] = {}, b[2] = {};
        for (int u = 0; u < nu; ++u) {
            for (int k = 0; k < ns; ++k) b[u] += J[static_cast<size_t>(u * ns + k)] * res[static_cast<size_t>(k)];
            for (int v = 0; v < nu; ++v)
                for (int k = 0; k < ns; ++k) A[u][v] += J[static_cast<size_t>(u * ns + k)] * J[static_cast<size_t>(v * ns + k)];
        }
        double step[2] = {};
        const double det = nu == 2 ? A[0][0] * A[1][1] - A[0][1] * A[1][0] : A[0][0];
        if (!(det > 0)) return meas;
        if (nu == 2) {
            step[0] = -(A[1][1] * b[0] - A[0][1] * b[1]) / det;
            step[1] = -(A[0][0] * b[1] - A[1][0] * b[0]) / det;
        } else
            step[0] = -b[0] / A[0][0];
        for (int u = 0; u < nu; ++u) {
            if (!std::isfinite(step[u])) return meas;
            step[u] = std::clamp(step[u], -0.5, 0.5);
        }
        // the step is kept only while it lowers the misfit (halved up to four times)
        for (int half = 0; half < 5; ++half) {
            for (int u = 0; u < nu; ++u) p[U[u]] += step[u];
            const double c = misfit(p, trial.data());
            if (c < cur) {
                cur = c;
                res.swap(trial);
                break;
            }
            for (int u = 0; u < nu; ++u) {
                p[U[u]] -= step[u];
                step[u] = half < 4 ? step[u] / 2 : 0;
            }
        }
        if (std::max(std::abs(step[0]), std::abs(step[1])) < 2e-4) break;
    }
    if (!(std::abs(p[0] - meas) <= 1.5)) return meas; // (a 2 px blur on a 4 px pin leans 1.1 px)
    if (!beyond && std::abs(p[M - 1] - (pk[M - 1] - E)) <= 1.0) {
        farPeak = pk[M - 1];
        farPos = p[M - 1];
    }
    return p[0];
}

// 0 ok, -1 bad argument, -2 the box leaves the image. dense: auto threshold for a box full of edges (pins,
// threads), where the edges themselves lift the noise estimate: at most a quarter of the strongest edge.
// boxProfile (may be null): the averaged profile on the box, one sample per pixel from its start.
int profileEdges(const Image& im, const vs_meas_box& b, const vs_meas_caliper_params& p, Profile& out, bool dense = false,
                 std::vector<double>* boxProfile = nullptr)
{
    if (!(b.length >= 3.f) || !(b.height >= 1.f) || !std::isfinite(b.cx) || !std::isfinite(b.cy) || !std::isfinite(b.angle_deg))
        return -1;
    // a box larger than the image is a setup error (and would overflow the casts below)
    if (b.length > 2.f * (im.w + im.h) || b.height > 2.f * (im.w + im.h)) return -1;
    const double a = b.angle_deg * kPi / 180.0;
    const Pt u{std::cos(a), -std::sin(a)}, v{std::sin(a), std::cos(a)};
    const int n = static_cast<int>(std::floor(b.length)) + 1; // samples on the box, one per pixel from its start
    const int m = std::max(1, static_cast<int>(std::lround(b.height)));
    const Pt start{b.cx - u.x * b.length / 2.0, b.cy - u.y * b.length / 2.0};
    const double s0 = -(m - 1) / 2.0;
    // Gaussian derivative c(x) = x G(x), scaled so a sharp step of H grey levels peaks at H
    const double sigma = std::max(0.7, std::clamp(static_cast<double>(p.filter_half_width), 1.0, 20.0) / 2.0);
    const int R = std::max(2, static_cast<int>(std::ceil(3 * sigma)));
    // the profile reaches E samples past each end, so an edge anywhere on the box is seen whole (no blind ends)
    const int E = 2 * R + 2, N = n + 2 * E;
    // the sampled span's four corners must lie inside the image (an edge off the image is not measured)
    for (int ci = 0; ci < 4; ++ci) {
        const double t = (ci & 1) ? n - 1 + E : -E, s = (ci & 2) ? -s0 : s0;
        const double x = start.x + u.x * t + v.x * s, y = start.y + u.y * t + v.y * s;
        if (x < 0 || y < 0 || x > im.w - 1 || y > im.h - 1) return -2;
    }
    // index q of prof and d is box sample q - E
    std::vector<double> prof(static_cast<size_t>(N), 0.0);
    for (int q = 0; q < N; ++q) {
        const int i = q - E;
        double sum = 0;
        for (int j = 0; j < m; ++j) {
            const double s = s0 + j;
            sum += im.at(start.x + u.x * i + v.x * s, start.y + u.y * i + v.y * s);
        }
        prof[static_cast<size_t>(q)] = sum / m;
    }
    std::vector<double> k(static_cast<size_t>(2 * R + 1));
    double half = 0;
    for (int x = -R; x <= R; ++x) {
        k[static_cast<size_t>(x + R)] = x * std::exp(-x * x / (2 * sigma * sigma));
        if (x > 0) half += k[static_cast<size_t>(x + R)];
    }
    for (double& c : k) c /= half;
    std::vector<double> d(static_cast<size_t>(N), 0.0);
    for (int q = R; q < N - R; ++q) {
        double s = 0;
        for (int x = -R; x <= R; ++x) s += k[static_cast<size_t>(x + R)] * prof[static_cast<size_t>(q + x)];
        d[static_cast<size_t>(q)] = s;
    }
    double thr = p.threshold;
    if (!(thr > 0)) {
        // auto: five times the noise of the derivative (median absolute value, robust to the edges), at least 5
        std::vector<double> ad;
        for (int q = E; q < E + n; ++q) ad.push_back(std::abs(d[static_cast<size_t>(q)]));
        double mad = 0;
        if (!ad.empty()) {
            std::nth_element(ad.begin(), ad.begin() + static_cast<long>(ad.size() / 2), ad.end());
            mad = ad[ad.size() / 2];
        }
        thr = std::max(5.0, 5.0 * mad / 0.6745);
        if (dense) {
            double peak = 0;
            for (int q = E; q < E + n; ++q) peak = std::max(peak, std::abs(d[static_cast<size_t>(q)]));
            thr = std::max(5.0, std::min(thr, 0.25 * peak));
        }
    }
    if (boxProfile) boxProfile->assign(prof.begin() + E, prof.begin() + E + n);
    out.start = start;
    out.u = u;
    out.length = b.length;
    out.threshold = thr;
    out.edges.clear();
    // peaks on the box (positions 0 .. length); their neighbours and centroid reach lie in the margin
    int farPeak = -1; // the far end of the last row end fitted (endEdge)
    double farPos = 0;
    for (int q = E; q < E + n; ++q) {
        const int i = q - E;
        const double a0 = d[static_cast<size_t>(q - 1)], b0 = d[static_cast<size_t>(q)], c0 = d[static_cast<size_t>(q + 1)];
        const double av = std::abs(a0), bv = std::abs(b0), cv = std::abs(c0);
        if (bv < thr || bv < av || bv <= cv) continue;
        double delta = 0, peak = bv;
        const bool sameSign = (a0 > 0) == (b0 > 0) && (c0 > 0) == (b0 > 0) && av > 0 && cv > 0;
        if (sameSign) {
            const double la = std::log(av), lb = std::log(bv), lc = std::log(cv), den = la - 2 * lb + lc;
            if (den < 0) {
                delta = std::clamp(0.5 * (la - lc) / den, -0.5, 0.5);
                peak = std::exp(lb - 0.25 * (la - lc) * delta);
            }
        } else {
            const double den = av - 2 * bv + cv;
            if (den < 0) {
                delta = std::clamp(0.5 * (av - cv) / den, -0.5, 0.5);
                peak = bv - 0.25 * (av - cv) * delta;
            }
        }
        // place: the centroid of the derivative over the response's reach (R + 1 either side, up to a sign
        // change). For an area-sampled step it is exact whatever the smoothing (the centroid of the first
        // differences is the edge, and a symmetric filter keeps it); the parabola is the fallback
        double sw = 0, sx = 0;
        int cut = 0; // sides where the response ends in a sign change before its reach: another edge is near
        int sides = 0, freeDir = 0; // the same, also when the crossing falls on a sample (the sample past it tells)
        for (int dir = -1; dir <= 1; dir += 2)
            for (int x = (dir < 0 ? 0 : 1); x <= R + 1; ++x) {
                const int qq = q + dir * x;
                if (qq < R || qq >= N - R) break;
                const double w = d[static_cast<size_t>(qq)];
                if (w == 0 || (w > 0) != (b0 > 0)) {
                    const int q2 = qq + dir;
                    const double w2 = q2 >= R && q2 < N - R ? d[static_cast<size_t>(q2)] : 0;
                    if (w != 0 && std::abs(w) > 0.2 * thr) ++cut; // a neighbour's response, not the noise past a lone edge
                    if ((w != 0 && std::abs(w) > 0.2 * thr) || (w2 != 0 && (w2 > 0) != (b0 > 0) && std::abs(w2) > 0.2 * thr)) ++sides;
                    else freeDir = dir;
                    break;
                }
                sw += std::abs(w);
                sx += std::abs(w) * (qq - E);
                if (x == R + 1) freeDir = dir;
            }
        double pos = sw > 0 ? sx / sw : i + delta;
        // a neighbour's response in reach: the symmetric centroid (exact inside a row of pins or a thread); with
        // a neighbour on one side only (a row's end) the response is lopsided and its lean is taken off
        if (cut > 0) pos = symmetricCentroid(d, q, E, R, i + delta);
        if (sides == 1 && freeDir != 0) {
            if (q == farPeak) pos = farPos; // fitted already, as the far end of the row from its first edge
            else pos = endEdge(d, q, E, R, thr, -freeDir, pos, farPeak, farPos);
        }
        out.edges.push_back({std::abs(pos - i) <= 1.0 ? pos : i + delta, b0 > 0 ? peak : -peak});
    }
    std::sort(out.edges.begin(), out.edges.end(), [](const Edge& x, const Edge& y) { return std::abs(x.contrast) > std::abs(y.contrast); });
    return 0;
}

vs_meas_edge toEdge(const Profile& pr, const Edge& e)
{
    return {static_cast<float>(pr.start.x + pr.u.x * e.pos), static_cast<float>(pr.start.y + pr.u.y * e.pos),
            static_cast<float>(e.pos), static_cast<float>(e.contrast)};
}

int caliper(const Image& im, const vs_meas_box& box, const vs_meas_caliper_params& p, const vs_meas_calib* calib,
            vs_meas_edge* edges, int cap, vs_meas_caliper_result& r)
{
    resetResult(r);
    Profile pr;
    const int rc = profileEdges(im, box, p, pr);
    if (rc < 0) return rc;
    r.threshold = static_cast<float>(pr.threshold);
    r.candidates = static_cast<int>(pr.edges.size());
    for (int i = 0; edges && i < cap && i < r.candidates; ++i) edges[i] = toEdge(pr, pr.edges[static_cast<size_t>(i)]);
    const double centre = pr.length / 2;
    double best = -1, second = -1;
    const Edge* e0 = nullptr;
    const Edge* e1 = nullptr;
    if (p.mode == VS_MEAS_PAIR) {
        for (const Edge& a : pr.edges) {
            if (!polarityOk(p.polarity, a.contrast)) continue;
            for (const Edge& b : pr.edges) {
                if (b.pos <= a.pos || (b.contrast > 0) == (a.contrast > 0)) continue;
                const double strength = (std::abs(a.contrast) + std::abs(b.contrast)) / 2;
                double s = strength;
                if (p.score_by == VS_MEAS_BY_POSITION) s = strength * std::max(0.0, 1 - std::abs((a.pos + b.pos) / 2 - centre) / std::max(1.0, centre));
                else if (p.score_by == VS_MEAS_BY_WIDTH && p.expected_width > 0) {
                    const Pt ma = undistort(calib, {pr.start.x + pr.u.x * a.pos, pr.start.y + pr.u.y * a.pos});
                    const Pt mb = undistort(calib, {pr.start.x + pr.u.x * b.pos, pr.start.y + pr.u.y * b.pos});
                    const double w = std::hypot(mb.x - ma.x, mb.y - ma.y);
                    s = strength * std::max(0.0, 1 - std::abs(w - p.expected_width) / p.expected_width);
                }
                if (s > best) { second = best; best = s; e0 = &a; e1 = &b; }
                else if (s > second) second = s;
            }
        }
    } else {
        for (const Edge& a : pr.edges) {
            if (!polarityOk(p.polarity, a.contrast)) continue;
            double s = std::abs(a.contrast);
            if (p.score_by == VS_MEAS_BY_POSITION) s *= std::max(0.0, 1 - std::abs(a.pos - centre) / std::max(1.0, centre));
            if (s > best) { second = best; best = s; e0 = &a; }
            else if (s > second) second = s;
        }
    }
    if (!e0 || best <= 0) return r.candidates;
    r.found = 1;
    r.ambiguous = second >= 0.9 * best ? 1 : 0;
    r.edge0 = toEdge(pr, *e0);
    const Pt m0 = undistort(calib, {r.edge0.x, r.edge0.y});
    r.mx0 = static_cast<float>(m0.x);
    r.my0 = static_cast<float>(m0.y);
    r.position = r.edge0.pos;
    if (e1) {
        r.edge1 = toEdge(pr, *e1);
        const Pt m1 = undistort(calib, {r.edge1.x, r.edge1.y});
        r.mx1 = static_cast<float>(m1.x);
        r.my1 = static_cast<float>(m1.y);
        r.position = (r.edge0.pos + r.edge1.pos) / 2;
        r.width_px = static_cast<float>(std::hypot(m1.x - m0.x, m1.y - m0.y));
        r.width_mm = static_cast<float>(r.width_px * mmPerPx(calib));
    }
    return r.candidates;
}

// deterministic sampling, so the same image always gives the same fit
struct Lcg {
    uint64_t s = 0x9E3779B97F4A7C15ull;
    int next(int n)
    {
        s = s * 6364136223846793005ull + 1442695040888963407ull;
        return static_cast<int>((s >> 33) % static_cast<uint64_t>(n));
    }
};

// total least squares: centroid and unit direction
bool fitLineTls(const std::vector<Pt>& pts, const std::vector<int>& idx, Pt& c, Pt& dir)
{
    if (idx.size() < 2) return false;
    double mx = 0, my = 0;
    for (int i : idx) { mx += pts[static_cast<size_t>(i)].x; my += pts[static_cast<size_t>(i)].y; }
    mx /= idx.size();
    my /= idx.size();
    double sxx = 0, syy = 0, sxy = 0;
    for (int i : idx) {
        const double dx = pts[static_cast<size_t>(i)].x - mx, dy = pts[static_cast<size_t>(i)].y - my;
        sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
    }
    const double th = 0.5 * std::atan2(2 * sxy, sxx - syy);
    c = {mx, my};
    dir = {std::cos(th), std::sin(th)};
    return true;
}

// The tolerance of the last refit: three robust sigmas of the residuals of the first fit (at least
// 0.25 px, at most outlier_px). A caliper only partly over a dent gives a point a pixel off that lies
// inside outlier_px and would pull the fit; the noise of the good points sets how far is too far.
// Lines only: round a small hole the radial calipers' own spread is systematic, and cutting it
// leaves the points lopsided and moves the centre.
double tightTol(std::vector<double> res, double outlier)
{
    if (res.empty()) return outlier;
    for (double& r : res) r = std::abs(r);
    std::nth_element(res.begin(), res.begin() + static_cast<long>(res.size() / 2), res.end());
    return std::min(outlier, std::max(0.25, 3 * 1.4826 * res[res.size() / 2]));
}

double lineDist(Pt c, Pt dir, Pt p) { return (p.x - c.x) * -dir.y + (p.y - c.y) * dir.x; }

std::vector<int> lineInliers(const std::vector<Pt>& pts, Pt c, Pt dir, double tol)
{
    std::vector<int> in;
    for (size_t i = 0; i < pts.size(); ++i)
        if (std::abs(lineDist(c, dir, pts[i])) <= tol) in.push_back(static_cast<int>(i));
    return in;
}

struct Circle {
    double a, b, r;
};

bool circle3(Pt p, Pt q, Pt s, Circle& c)
{
    const double d = 2 * (p.x * (q.y - s.y) + q.x * (s.y - p.y) + s.x * (p.y - q.y));
    if (std::abs(d) < 1e-9) return false;
    const double p2 = p.x * p.x + p.y * p.y, q2 = q.x * q.x + q.y * q.y, s2 = s.x * s.x + s.y * s.y;
    c.a = (p2 * (q.y - s.y) + q2 * (s.y - p.y) + s2 * (p.y - q.y)) / d;
    c.b = (p2 * (s.x - q.x) + q2 * (p.x - s.x) + s2 * (q.x - p.x)) / d;
    c.r = std::hypot(p.x - c.a, p.y - c.b);
    return std::isfinite(c.r);
}

// algebraic seed (Kasa), then geometric least squares (Gauss-Newton on the distances to the rim)
bool fitCircleLs(const std::vector<Pt>& pts, const std::vector<int>& idx, Circle& c)
{
    if (idx.size() < 3) return false;
    cv::Matx33d A = cv::Matx33d::zeros();
    cv::Vec3d B(0, 0, 0);
    for (int i : idx) {
        const Pt& p = pts[static_cast<size_t>(i)];
        const cv::Vec3d row(p.x, p.y, 1);
        const double z = -(p.x * p.x + p.y * p.y);
        A += row * row.t();
        B += row * z;
    }
    cv::Vec3d s;
    if (!cv::solve(A, B, s, cv::DECOMP_SVD)) return false;
    c.a = -s[0] / 2;
    c.b = -s[1] / 2;
    const double r2 = c.a * c.a + c.b * c.b - s[2];
    if (!(r2 > 0)) return false;
    c.r = std::sqrt(r2);
    for (int it = 0; it < 20; ++it) {
        cv::Matx33d J = cv::Matx33d::zeros();
        cv::Vec3d g(0, 0, 0);
        for (int i : idx) {
            const Pt& p = pts[static_cast<size_t>(i)];
            const double dx = p.x - c.a, dy = p.y - c.b, d = std::max(1e-9, std::hypot(dx, dy));
            const cv::Vec3d jr(-dx / d, -dy / d, -1);
            const double res = d - c.r;
            J += jr * jr.t();
            g += jr * res;
        }
        cv::Vec3d step;
        if (!cv::solve(J, -g, step, cv::DECOMP_SVD)) break;
        c.a += step[0]; c.b += step[1]; c.r += step[2];
        if (std::abs(step[0]) + std::abs(step[1]) + std::abs(step[2]) < 1e-7) break;
    }
    return std::isfinite(c.a) && std::isfinite(c.b) && c.r > 0;
}

std::vector<int> circleInliers(const std::vector<Pt>& pts, const Circle& c, double tol)
{
    std::vector<int> in;
    for (size_t i = 0; i < pts.size(); ++i)
        if (std::abs(std::hypot(pts[i].x - c.a, pts[i].y - c.b) - c.r) <= tol) in.push_back(static_cast<int>(i));
    return in;
}

bool caliperParamsOk(const vs_meas_caliper_params* p)
{
    return p && p->struct_size >= static_cast<int32_t>(sizeof(vs_meas_caliper_params)) && p->polarity >= 0 && p->polarity <= 2;
}

} // namespace

extern "C" {

void vs_meas_default_caliper(vs_meas_caliper_params* p)
{
    if (!p) return;
    *p = vs_meas_caliper_params{};
    p->struct_size = sizeof(vs_meas_caliper_params);
    p->mode = VS_MEAS_SINGLE;
    p->polarity = VS_MEAS_ANY;
    p->filter_half_width = 2.f;
    p->score_by = VS_MEAS_BY_CONTRAST;
}

int32_t vs_meas_caliper(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, const vs_meas_box* box,
                        const vs_meas_caliper_params* p, const vs_meas_calib* calib, vs_meas_edge* edges, int32_t cap,
                        vs_meas_caliper_result* out)
{
    if (!pixels || width < 2 || height < 2 || stride < width || !box || !caliperParamsOk(p) || !resultOk(out) || cap < 0) return -1;
    return caliper(Image{pixels, width, height, stride}, *box, *p, calib, edges, cap, *out);
}

void vs_meas_default_line(vs_meas_line_params* p)
{
    if (!p) return;
    *p = vs_meas_line_params{};
    p->struct_size = sizeof(vs_meas_line_params);
    p->calipers = 10;
    p->search_width = 20.f;
    p->outlier_px = 1.5f;
    p->fit = VS_MEAS_FIT_RANSAC;
    vs_meas_default_caliper(&p->caliper);
}

} // extern "C"

namespace {

bool imageOk(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride)
{
    return pixels && width >= 2 && height >= 2 && stride >= width;
}

int lineImpl(const Image& im, const vs_meas_line_params* p, const vs_meas_calib* calib, float* points_xy, int32_t cap,
             vs_meas_line_result* out)
{
    if (!p || p->struct_size < static_cast<int32_t>(sizeof(vs_meas_line_params)) || cap < 0 || p->calipers < 3 ||
        p->calipers > 200 || !(p->search_width >= 3.f) || !(p->outlier_px > 0.f) || !caliperParamsOk(&p->caliper))
        return -1;
    resetResult(*out);
    const double L = std::hypot(p->x1 - p->x0, p->y1 - p->y0);
    if (!(L >= 2.0)) return -1;
    const Pt d{(p->x1 - p->x0) / L, (p->y1 - p->y0) / L};
    const double spacing = L / p->calipers;
    const double hgt = p->caliper_height > 0.f ? p->caliper_height : std::clamp(spacing, 1.0, 10.0);
    vs_meas_caliper_params cp = p->caliper;
    cp.mode = VS_MEAS_SINGLE;
    std::vector<Pt> pts;
    std::vector<double> contrast;
    out->calipers = p->calipers;
    for (int i = 0; i < p->calipers; ++i) {
        const double t = spacing * (i + 0.5);
        // searches along v of the line's direction: v = (-dy, dx); its box angle a has (cos a, -sin a) = v
        const vs_meas_box b{static_cast<float>(p->x0 + d.x * t), static_cast<float>(p->y0 + d.y * t), p->search_width,
                            static_cast<float>(hgt), static_cast<float>(std::atan2(-d.x, -d.y) * 180.0 / kPi)};
        vs_meas_caliper_result r;
        // a caliper whose choice is in doubt gives no point, so it counts against the share used
        const bool ok = caliper(im, b, cp, calib, nullptr, 0, r) >= 0 && r.found && !r.ambiguous;
        if (points_xy && i < cap) {
            points_xy[2 * i] = ok ? r.edge0.x : std::numeric_limits<float>::quiet_NaN();
            points_xy[2 * i + 1] = ok ? r.edge0.y : std::numeric_limits<float>::quiet_NaN();
        }
        if (!ok) continue;
        pts.push_back({r.mx0, r.my0});
        contrast.push_back(std::abs(r.edge0.contrast));
    }
    out->points_found = static_cast<int>(pts.size());
    if (pts.size() < 2) return 0;
    std::vector<int> in;
    Pt c, dir;
    if (p->fit == VS_MEAS_FIT_RANSAC) {
        const int n = static_cast<int>(pts.size());
        size_t bestN = 0;
        double bestErr = 1e300;
        Lcg rng;
        const bool all = n * (n - 1) / 2 <= 2000;
        const int tries = all ? n * (n - 1) / 2 : 2000;
        int i = 0, j = 1;
        for (int k = 0; k < tries; ++k) {
            int a, bb;
            if (all) {
                a = i; bb = j;
                if (++j == n) { ++i; j = i + 1; }
            } else {
                a = rng.next(n); bb = rng.next(n);
                if (a == bb) continue;
            }
            const Pt pa = pts[static_cast<size_t>(a)], pb = pts[static_cast<size_t>(bb)];
            const double l = std::hypot(pb.x - pa.x, pb.y - pa.y);
            if (l < 1e-6) continue;
            const Pt dd{(pb.x - pa.x) / l, (pb.y - pa.y) / l};
            const std::vector<int> cand = lineInliers(pts, pa, dd, p->outlier_px);
            double err = 0;
            for (int q : cand) err += std::abs(lineDist(pa, dd, pts[static_cast<size_t>(q)]));
            if (cand.size() > bestN || (cand.size() == bestN && err < bestErr)) { bestN = cand.size(); bestErr = err; in = cand; }
        }
        if (!fitLineTls(pts, in, c, dir)) return 0;
        in = lineInliers(pts, c, dir, p->outlier_px);
        if (!fitLineTls(pts, in, c, dir)) return 0;
        std::vector<double> res;
        for (int q : in) res.push_back(lineDist(c, dir, pts[static_cast<size_t>(q)]));
        in = lineInliers(pts, c, dir, tightTol(res, p->outlier_px));
        if (!fitLineTls(pts, in, c, dir)) return 0;
    } else {
        for (size_t q = 0; q < pts.size(); ++q) in.push_back(static_cast<int>(q));
        if (!fitLineTls(pts, in, c, dir)) return 0;
    }
    const Pt m0 = undistort(calib, {p->x0, p->y0}), m1 = undistort(calib, {p->x1, p->y1});
    if (dir.x * (m1.x - m0.x) + dir.y * (m1.y - m0.y) < 0) dir = {-dir.x, -dir.y};
    const auto project = [&](Pt q) {
        const double t = (q.x - c.x) * dir.x + (q.y - c.y) * dir.y;
        return Pt{c.x + dir.x * t, c.y + dir.y * t};
    };
    const Pt f0 = project(m0), f1 = project(m1), i0 = distort(calib, f0), i1 = distort(calib, f1);
    // straightness over every point found, not only the inliers: a dent or bow left out of the fit
    // must still show (line safety)
    double worst = 0, ss = 0, mc = 0;
    for (const Pt& q : pts) worst = std::max(worst, std::abs(lineDist(c, dir, q)));
    for (int q : in) {
        const double e = std::abs(lineDist(c, dir, pts[static_cast<size_t>(q)]));
        ss += e * e;
        mc += contrast[static_cast<size_t>(q)];
    }
    out->found = in.size() >= 2 ? 1 : 0;
    out->points_used = static_cast<int>(in.size());
    out->x0 = static_cast<float>(i0.x); out->y0 = static_cast<float>(i0.y);
    out->x1 = static_cast<float>(i1.x); out->y1 = static_cast<float>(i1.y);
    out->mx0 = static_cast<float>(f0.x); out->my0 = static_cast<float>(f0.y);
    out->mx1 = static_cast<float>(f1.x); out->my1 = static_cast<float>(f1.y);
    out->angle_deg = static_cast<float>(std::atan2(-dir.y, dir.x) * 180.0 / kPi);
    out->straightness_px = static_cast<float>(worst);
    out->rms_px = static_cast<float>(std::sqrt(ss / in.size()));
    out->mean_contrast = static_cast<float>(mc / in.size());
    return 0;
}

} // namespace

extern "C" {

int32_t vs_meas_line(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, const vs_meas_line_params* p,
                     const vs_meas_calib* calib, float* points_xy, int32_t cap, vs_meas_line_result* out)
{
    if (!imageOk(pixels, width, height, stride) || !resultOk(out)) return -1;
    return lineImpl(Image{pixels, width, height, stride}, p, calib, points_xy, cap, out);
}

void vs_meas_default_circle(vs_meas_circle_params* p)
{
    if (!p) return;
    *p = vs_meas_circle_params{};
    p->struct_size = sizeof(vs_meas_circle_params);
    p->span_deg = 360.f;
    p->calipers = 36;
    p->outlier_px = 1.5f;
    p->fit = VS_MEAS_FIT_RANSAC;
    vs_meas_default_caliper(&p->caliper);
}

} // extern "C"

namespace {

bool ringParamsOk(const vs_meas_circle_params* p, int32_t cap)
{
    return p && p->struct_size >= static_cast<int32_t>(sizeof(vs_meas_circle_params)) && cap >= 0 && p->calipers >= 8 &&
           p->calipers <= 360 && p->r_inner >= 0.f && p->r_outer - p->r_inner >= 3.f && p->span_deg > 0.f && p->span_deg <= 360.f &&
           p->outlier_px > 0.f && caliperParamsOk(&p->caliper);
}

// radial calipers across the ring: the points found (measure space) and their contrasts
void ringPoints(const Image& im, const vs_meas_circle_params* p, const vs_meas_calib* calib, float* points_xy, int32_t cap,
                std::vector<Pt>& pts, std::vector<double>& contrast)
{
    const double rmid = (p->r_inner + p->r_outer) / 2.0, span = p->span_deg * kPi / 180.0;
    const double spacing = span * rmid / p->calipers;
    const double hgt = p->caliper_height > 0.f ? p->caliper_height : std::clamp(spacing / 2, 1.0, 10.0);
    vs_meas_caliper_params cp = p->caliper;
    cp.mode = VS_MEAS_SINGLE;
    const bool full = p->span_deg >= 360.f;
    for (int i = 0; i < p->calipers; ++i) {
        const double phi = p->start_deg * kPi / 180.0 + span * (full ? i : i + 0.5) / p->calipers;
        const Pt w{std::cos(phi), -std::sin(phi)}; // outward, counter-clockwise on the screen
        const Pt u = p->inward ? Pt{-w.x, -w.y} : w;
        const vs_meas_box b{static_cast<float>(p->cx + w.x * rmid), static_cast<float>(p->cy + w.y * rmid), p->r_outer - p->r_inner,
                            static_cast<float>(hgt), static_cast<float>(std::atan2(-u.y, u.x) * 180.0 / kPi)};
        vs_meas_caliper_result r;
        // a caliper whose choice is in doubt gives no point, so it counts against the share used
        const bool ok = caliper(im, b, cp, calib, nullptr, 0, r) >= 0 && r.found && !r.ambiguous;
        if (points_xy && i < cap) {
            points_xy[2 * i] = ok ? r.edge0.x : std::numeric_limits<float>::quiet_NaN();
            points_xy[2 * i + 1] = ok ? r.edge0.y : std::numeric_limits<float>::quiet_NaN();
        }
        if (!ok) continue;
        pts.push_back({r.mx0, r.my0});
        contrast.push_back(std::abs(r.edge0.contrast));
    }
}

} // namespace

extern "C" {

int32_t vs_meas_circle(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, const vs_meas_circle_params* p,
                       const vs_meas_calib* calib, float* points_xy, int32_t cap, vs_meas_circle_result* out)
{
    if (!imageOk(pixels, width, height, stride) || !resultOk(out) || !ringParamsOk(p, cap)) return -1;
    resetResult(*out);
    std::vector<Pt> pts;
    std::vector<double> contrast;
    out->calipers = p->calipers;
    ringPoints(Image{pixels, width, height, stride}, p, calib, points_xy, cap, pts, contrast);
    out->points_found = static_cast<int>(pts.size());
    if (pts.size() < 3) return 0;
    std::vector<int> in;
    Circle c{};
    if (p->fit == VS_MEAS_FIT_RANSAC) {
        const int n = static_cast<int>(pts.size());
        size_t bestN = 0;
        double bestErr = 1e300;
        Lcg rng;
        const long triples = static_cast<long>(n) * (n - 1) * (n - 2) / 6;
        const bool all = triples <= 3000;
        const int tries = all ? static_cast<int>(triples) : 3000;
        int a = 0, bb = 1, cc = 2;
        for (int k = 0; k < tries; ++k) {
            int i0, i1, i2;
            if (all) {
                i0 = a; i1 = bb; i2 = cc;
                if (++cc == n) { if (++bb == n - 1) { ++a; bb = a + 1; } cc = bb + 1; }
            } else {
                i0 = rng.next(n); i1 = rng.next(n); i2 = rng.next(n);
                if (i0 == i1 || i1 == i2 || i0 == i2) continue;
            }
            Circle t;
            if (!circle3(pts[static_cast<size_t>(i0)], pts[static_cast<size_t>(i1)], pts[static_cast<size_t>(i2)], t)) continue;
            const std::vector<int> cand = circleInliers(pts, t, p->outlier_px);
            double err = 0;
            for (int q : cand) err += std::abs(std::hypot(pts[static_cast<size_t>(q)].x - t.a, pts[static_cast<size_t>(q)].y - t.b) - t.r);
            if (cand.size() > bestN || (cand.size() == bestN && err < bestErr)) { bestN = cand.size(); bestErr = err; in = cand; }
        }
        if (!fitCircleLs(pts, in, c)) return 0;
        in = circleInliers(pts, c, p->outlier_px);
        if (!fitCircleLs(pts, in, c)) return 0;
    } else {
        for (size_t q = 0; q < pts.size(); ++q) in.push_back(static_cast<int>(q));
        if (!fitCircleLs(pts, in, c)) return 0;
    }
    // roundness over every point found, not only the inliers (as straightness)
    double lo = 1e300, hi = -1e300, ss = 0, mc = 0;
    for (const Pt& q : pts) {
        const double rr = std::hypot(q.x - c.a, q.y - c.b);
        lo = std::min(lo, rr);
        hi = std::max(hi, rr);
    }
    for (int q : in) {
        const double rr = std::hypot(pts[static_cast<size_t>(q)].x - c.a, pts[static_cast<size_t>(q)].y - c.b);
        ss += (rr - c.r) * (rr - c.r);
        mc += contrast[static_cast<size_t>(q)];
    }
    const Pt ic = distort(calib, {c.a, c.b});
    out->found = in.size() >= 3 ? 1 : 0;
    out->points_used = static_cast<int>(in.size());
    out->cx = static_cast<float>(ic.x); out->cy = static_cast<float>(ic.y);
    out->mcx = static_cast<float>(c.a); out->mcy = static_cast<float>(c.b);
    out->radius_px = static_cast<float>(c.r);
    out->roundness_px = static_cast<float>(hi - lo);
    out->rms_px = static_cast<float>(std::sqrt(ss / in.size()));
    out->mean_contrast = static_cast<float>(mc / in.size());
    return 0;
}

} // extern "C"

namespace {

// An ellipse in measure space: centre, semi-axes a (along phi) and b, phi the angle of a from +x towards +y
// (image coordinates, y down).
struct Ell {
    double cx, cy, a, b, phi;
};

// The signed distance of p from the ellipse (+ outside), and its derivatives by (cx, cy, a, b, phi). The nearest
// point on the rim by the trig-free iteration on the first quadrant (Chou's, as 0xfaded's): exact to 1e-6 px in
// 10 rounds for the shapes measured here, a few hundredths in 3 (enough to tell inliers in RANSAC). The derivatives hold the nearest point fixed (it is a minimum).
double ellDist(const Ell& e, Pt p, double* g = nullptr, int rounds = 10)
{
    const double c = std::cos(e.phi), s = std::sin(e.phi);
    const double dx = p.x - e.cx, dy = p.y - e.cy;
    const double qx = c * dx + s * dy, qy = -s * dx + c * dy; // in the ellipse's frame
    const double a = e.a, b = e.b, px = std::abs(qx), py = std::abs(qy);
    double tx = 0.70710678, ty = 0.70710678;
    for (int it = 0; it < rounds; ++it) {
        const double x = a * tx, y = b * ty;
        const double ex = (a * a - b * b) * tx * tx * tx / a, ey = (b * b - a * a) * ty * ty * ty / b;
        const double rx = x - ex, ry = y - ey, qxx = px - ex, qyy = py - ey;
        const double r = std::hypot(rx, ry), q = std::max(1e-12, std::hypot(qxx, qyy));
        tx = std::clamp((qxx * r / q + ex) / a, 0.0, 1.0);
        ty = std::clamp((qyy * r / q + ey) / b, 0.0, 1.0);
        const double t = std::max(1e-12, std::hypot(tx, ty));
        tx /= t;
        ty /= t;
    }
    const double ct = qx < 0 ? -tx : tx, st = qy < 0 ? -ty : ty;
    double nx = ct / a, ny = st / b;
    const double nl = std::max(1e-12, std::hypot(nx, ny));
    nx /= nl;
    ny /= nl;
    const double d = nx * (qx - a * ct) + ny * (qy - b * st);
    if (g) {
        const double wx = c * nx - s * ny, wy = s * nx + c * ny; // the normal in measure space
        g[0] = -wx;
        g[1] = -wy;
        g[2] = -nx * ct;
        g[3] = -ny * st;
        g[4] = -(nx * -b * st + ny * a * ct);
    }
    return d;
}

std::vector<int> ellInliers(const std::vector<Pt>& pts, const Ell& e, double tol, int rounds = 10)
{
    std::vector<int> in;
    for (size_t i = 0; i < pts.size(); ++i)
        if (std::abs(ellDist(e, pts[i], nullptr, rounds)) <= tol) in.push_back(static_cast<int>(i));
    return in;
}

// Whether the whole fitted rim stays in the searched ring (1 px slack), arc or full ring alike: the ring is where
// the oval is expected, and a short arc alone cannot fix an ellipse that explains it from far outside (rim taken
// every degree, in measure space, about the ring's centre undistorted like the points).
bool ellInRing(const Ell& e, const vs_meas_circle_params* p, const vs_meas_calib* calib)
{
    const Pt o = undistort(calib, {p->cx, p->cy});
    const double c = std::cos(e.phi), s = std::sin(e.phi);
    for (int k = 0; k < 360; ++k) {
        const double t = k * kPi / 180;
        const double x = e.a * std::cos(t), y = e.b * std::sin(t);
        const double r = std::hypot(e.cx + c * x - s * y - o.x, e.cy + s * x + c * y - o.y);
        if (r < p->r_inner - 1.0 || r > p->r_outer + 1.0) return false;
    }
    return true;
}

// Direct least-squares ellipse (Fitzgibbon, in Halir and Flusser's stable form), on points centred and scaled
// first for conditioning. Written here on core only: OpenCV 5 moved fitEllipseDirect out of imgproc.
bool ellDirect(const std::vector<Pt>& pts, const std::vector<int>& idx, Ell& e)
{
    if (idx.size() < 5) return false;
    double mx = 0, my = 0;
    for (int i : idx) { mx += pts[static_cast<size_t>(i)].x; my += pts[static_cast<size_t>(i)].y; }
    mx /= idx.size();
    my /= idx.size();
    double sc = 0;
    for (int i : idx) sc += std::hypot(pts[static_cast<size_t>(i)].x - mx, pts[static_cast<size_t>(i)].y - my);
    sc /= idx.size();
    if (!(sc > 1e-9)) return false;
    cv::Matx33d S1 = cv::Matx33d::zeros(), S2 = cv::Matx33d::zeros(), S3 = cv::Matx33d::zeros();
    for (int i : idx) {
        const double x = (pts[static_cast<size_t>(i)].x - mx) / sc, y = (pts[static_cast<size_t>(i)].y - my) / sc;
        const cv::Vec3d d1(x * x, x * y, y * y), d2(x, y, 1);
        S1 += d1 * d1.t();
        S2 += d1 * d2.t();
        S3 += d2 * d2.t();
    }
    cv::Matx33d S3i;
    if (cv::invert(S3, S3i, cv::DECOMP_SVD) == 0) return false;
    const cv::Matx33d T = -(S3i * S2.t());
    const cv::Matx33d M0 = S1 + S2 * T;
    // the constraint 4ac - b^2 = 1: C1^-1 M
    const cv::Matx33d M(M0(2, 0) / 2, M0(2, 1) / 2, M0(2, 2) / 2, -M0(1, 0), -M0(1, 1), -M0(1, 2), M0(0, 0) / 2, M0(0, 1) / 2, M0(0, 2) / 2);
    cv::Mat vals, vecs;
    cv::eigenNonSymmetric(cv::Mat(M), vals, vecs);
    int pick = -1;
    double bestCon = 0;
    for (int r = 0; r < vecs.rows; ++r) {
        const double a = vecs.at<double>(r, 0), b = vecs.at<double>(r, 1), c = vecs.at<double>(r, 2), con = 4 * a * c - b * b;
        if (con > bestCon) { bestCon = con; pick = r; }
    }
    if (pick < 0) return false;
    const cv::Vec3d a1(vecs.at<double>(pick, 0), vecs.at<double>(pick, 1), vecs.at<double>(pick, 2));
    const cv::Vec3d a2 = T * a1;
    const double A = a1[0], B = a1[1], C = a1[2], D = a2[0], E = a2[1], F = a2[2];
    const double den = B * B - 4 * A * C;
    if (!(den < 0)) return false;
    const double x0 = (2 * C * D - B * E) / den, y0 = (2 * A * E - B * D) / den;
    const double f0 = A * x0 * x0 + B * x0 * y0 + C * y0 * y0 + D * x0 + E * y0 + F;
    const double th = 0.5 * std::atan2(B, A - C);
    const auto lam = [&](double t) { const double c = std::cos(t), s = std::sin(t); return A * c * c + B * c * s + C * s * s; };
    const double qa = -f0 / lam(th), qb = -f0 / lam(th + kPi / 2);
    if (!(qa > 0 && qb > 0)) return false;
    e = {mx + x0 * sc, my + y0 * sc, std::sqrt(qa) * sc, std::sqrt(qb) * sc, th};
    return std::isfinite(e.cx) && std::isfinite(e.cy) && e.a > 0.5 && e.b > 0.5 && std::isfinite(e.phi);
}

// geometric least squares on the true distances (Levenberg-Marquardt from the seed in e)
bool ellRefine(const std::vector<Pt>& pts, const std::vector<int>& idx, Ell& e)
{
    if (idx.size() < 6) return false;
    const auto cost = [&](const Ell& t) {
        double ss = 0;
        for (int i : idx) {
            const double d = ellDist(t, pts[static_cast<size_t>(i)]);
            ss += d * d;
        }
        return ss;
    };
    double lambda = 1e-3, cur = cost(e);
    for (int it = 0; it < 30; ++it) {
        cv::Matx<double, 5, 5> J = cv::Matx<double, 5, 5>::zeros();
        cv::Vec<double, 5> gsum = cv::Vec<double, 5>::all(0);
        for (int i : idx) {
            double g[5];
            const double d = ellDist(e, pts[static_cast<size_t>(i)], g);
            for (int r = 0; r < 5; ++r) {
                gsum[r] += g[r] * d;
                for (int k = 0; k < 5; ++k) J(r, k) += g[r] * g[k];
            }
        }
        bool better = false;
        for (int tries = 0; tries < 8 && !better; ++tries) {
            cv::Matx<double, 5, 5> A = J;
            for (int r = 0; r < 5; ++r) A(r, r) += lambda * std::max(1e-9, J(r, r));
            cv::Vec<double, 5> step;
            if (!cv::solve(A, -gsum, step, cv::DECOMP_SVD)) return false;
            const Ell t{e.cx + step[0], e.cy + step[1], e.a + step[2], e.b + step[3], e.phi + step[4]};
            const double c2 = t.a > 0 && t.b > 0 ? cost(t) : 1e300;
            if (c2 < cur) {
                const double gain = cur - c2;
                e = t;
                cur = c2;
                lambda = std::max(1e-9, lambda / 10);
                better = true;
                if (gain < 1e-9 * std::max(1e-6, cur) || std::abs(step[0]) + std::abs(step[1]) + std::abs(step[2]) + std::abs(step[3]) < 1e-6)
                    return true;
            } else
                lambda *= 10;
        }
        if (!better) break;
    }
    return std::isfinite(e.cx) && e.a > 0 && e.b > 0;
}

// The period of a profile from its autocorrelation: the first repeat after the first dip below zero that is
// at least 0.8 of the strongest (a multiple of the period is about as strong), at least 0.3 of the zero lag;
// then refined on its farthest clear multiple, which divides a parabola's bias on a sharp peak by the
// multiple. Image px; 0 = none.
double profilePeriod(const std::vector<double>& prof)
{
    const int n = static_cast<int>(prof.size());
    if (n < 8) return 0;
    double m = 0;
    for (double v : prof) m += v;
    m /= n;
    const int K = n / 2 + 2; // lags up to half the profile: at least two whole repeats
    std::vector<double> r(static_cast<size_t>(K), 0.0);
    for (int k = 0; k < K && k < n; ++k) {
        double s = 0;
        for (int i = 0; i + k < n; ++i) s += (prof[static_cast<size_t>(i)] - m) * (prof[static_cast<size_t>(i + k)] - m);
        r[static_cast<size_t>(k)] = s / (n - k);
    }
    if (!(r[0] > 0)) return 0;
    const auto at = [&](int k) { return r[static_cast<size_t>(k)]; };
    const auto isPeak = [&](int k) { return k > 0 && k < K - 1 && at(k) >= at(k - 1) && at(k) > at(k + 1); };
    const auto refine = [&](int k) {
        const double a = at(k - 1), b = at(k), c = at(k + 1), den = a - 2 * b + c;
        return k + (den < 0 ? std::clamp(0.5 * (a - c) / den, -0.5, 0.5) : 0.0);
    };
    int k0 = 1;
    while (k0 < K - 1 && at(k0) > 0) ++k0;
    double top = 0;
    for (int k = k0; k < K - 1; ++k)
        if (isPeak(k)) top = std::max(top, at(k));
    if (top < 0.3 * r[0]) return 0;
    int first = -1;
    for (int k = k0; k < K - 1 && first < 0; ++k)
        if (isPeak(k) && at(k) >= 0.8 * top) first = k;
    if (first < 0) return 0;
    double period = refine(first);
    for (int mult = 2; mult * period < K - 2; ++mult) {
        const int c = static_cast<int>(std::lround(mult * period));
        int best = -1;
        for (int k = std::max(1, c - 2); k <= std::min(K - 2, c + 2); ++k)
            if (isPeak(k) && (best < 0 || at(k) > at(best))) best = k;
        if (best < 0 || at(best) < 0.3 * r[0]) break;
        period = refine(best) / mult;
    }
    return period;
}

bool boxOk(const vs_meas_box& b) { return std::isfinite(b.cx) && std::isfinite(b.cy) && std::isfinite(b.angle_deg) && b.length >= 3.f && b.height >= 1.f; }

bool polarityValid(int32_t p) { return p >= VS_MEAS_ANY && p <= VS_MEAS_LIGHT_TO_DARK; }

bool filterValid(float f) { return f >= 1.f && f <= 20.f; }

// the edges of a profile in order along the box, and the measure-space start and image-px-to-measure scale
struct BoxEdges {
    Profile pr;
    std::vector<double> prof;
    Pt mStart;
    double scale = 1; // measure px per image px along the box
};

int boxEdges(const Image& im, const vs_meas_box& box, int polarity, float threshold, float filter, const vs_meas_calib* calib, BoxEdges& out)
{
    vs_meas_caliper_params cp;
    vs_meas_default_caliper(&cp);
    cp.polarity = polarity;
    cp.threshold = threshold;
    cp.filter_half_width = filter;
    const int rc = profileEdges(im, box, cp, out.pr, true, &out.prof);
    if (rc < 0) return rc;
    std::sort(out.pr.edges.begin(), out.pr.edges.end(), [](const Edge& a, const Edge& b) { return a.pos < b.pos; });
    out.mStart = undistort(calib, out.pr.start);
    const Pt mEnd = undistort(calib, {out.pr.start.x + out.pr.u.x * box.length, out.pr.start.y + out.pr.u.y * box.length});
    out.scale = std::hypot(mEnd.x - out.mStart.x, mEnd.y - out.mStart.y) / box.length;
    return 0;
}

Pt edgeImage(const Profile& pr, double pos) { return {pr.start.x + pr.u.x * pos, pr.start.y + pr.u.y * pos}; }

} // namespace

extern "C" {

int32_t vs_meas_ellipse(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, const vs_meas_circle_params* p,
                        const vs_meas_calib* calib, float* points_xy, int32_t cap, vs_meas_ellipse_result* out)
{
    if (!imageOk(pixels, width, height, stride) || !resultOk(out) || !ringParamsOk(p, cap)) return -1;
    resetResult(*out);
    std::vector<Pt> pts;
    std::vector<double> contrast;
    out->calipers = p->calipers;
    ringPoints(Image{pixels, width, height, stride}, p, calib, points_xy, cap, pts, contrast);
    out->points_found = static_cast<int>(pts.size());
    if (pts.size() < 6) return 0;
    std::vector<int> all(pts.size());
    for (size_t q = 0; q < pts.size(); ++q) all[q] = static_cast<int>(q);
    std::vector<int> in;
    Ell e{};
    if (p->fit == VS_MEAS_FIT_RANSAC) {
        const int n = static_cast<int>(pts.size());
        size_t bestN = 0;
        double bestErr = 1e300;
        Lcg rng;
        // adaptive: enough samples for one all-inlier draw with 99.9 % odds at the share agreeing so far (8 .. 400)
        int need = 400;
        for (int k = 0; k < need; ++k) {
            std::vector<int> pick;
            while (pick.size() < 5) {
                const int c = rng.next(n);
                if (std::find(pick.begin(), pick.end(), c) == pick.end()) pick.push_back(c);
            }
            Ell t;
            if (!ellDirect(pts, pick, t)) continue;
            // a sample far from the searched ring is no candidate
            if (t.a > 2 * p->r_outer + 2 || t.b > 2 * p->r_outer + 2) continue;
            const std::vector<int> cand = ellInliers(pts, t, p->outlier_px, 3);
            double err = 0;
            for (int q : cand) err += std::abs(ellDist(t, pts[static_cast<size_t>(q)], nullptr, 3));
            if (cand.size() > bestN || (cand.size() == bestN && err < bestErr)) {
                bestN = cand.size(); bestErr = err; in = cand;
                const double w5 = std::pow(static_cast<double>(bestN) / n, 5);
                need = w5 >= 1 ? 8 : std::clamp(static_cast<int>(std::ceil(std::log(0.001) / std::log(1 - w5))), 8, 400);
            }
        }
        if (!ellDirect(pts, in, e) || !ellRefine(pts, in, e)) return 0;
        in = ellInliers(pts, e, p->outlier_px);
        if (!ellRefine(pts, in, e)) return 0;
    } else {
        in = all;
        if (!ellDirect(pts, in, e) || !ellRefine(pts, in, e)) return 0;
    }
    if (in.size() < 6 || !ellInRing(e, p, calib)) return 0;
    double worst = 0, ss = 0, mc = 0, dlo = 1e300, dhi = -1e300;
    for (const Pt& q : pts) {
        const double d = ellDist(e, q);
        worst = std::max(worst, std::abs(d));
        dlo = std::min(dlo, d);
        dhi = std::max(dhi, d);
    }
    for (int q : in) {
        const double d = ellDist(e, pts[static_cast<size_t>(q)]);
        ss += d * d;
        mc += contrast[static_cast<size_t>(q)];
    }
    // the major axis' direction, counter-clockwise on the screen (y down: minus the image angle), -90..90
    double major = e.a, minor = e.b, phi = e.phi;
    if (minor > major) { std::swap(major, minor); phi += kPi / 2; }
    double deg = -phi * 180.0 / kPi;
    deg = std::fmod(deg, 180.0);
    if (deg > 90) deg -= 180;
    if (deg <= -90) deg += 180;
    const Pt ic = distort(calib, {e.cx, e.cy});
    out->found = 1;
    out->points_used = static_cast<int>(in.size());
    out->cx = static_cast<float>(ic.x); out->cy = static_cast<float>(ic.y);
    out->mcx = static_cast<float>(e.cx); out->mcy = static_cast<float>(e.cy);
    out->major_px = static_cast<float>(2 * major);
    out->minor_px = static_cast<float>(2 * minor);
    out->angle_deg = static_cast<float>(deg);
    out->fit_error_px = static_cast<float>(worst);
    out->roundness_px = static_cast<float>(dhi - dlo);
    out->rms_px = static_cast<float>(std::sqrt(ss / in.size()));
    out->mean_contrast = static_cast<float>(mc / in.size());
    return 0;
}

void vs_meas_default_corner(vs_meas_corner_params* p)
{
    if (!p) return;
    *p = vs_meas_corner_params{};
    p->struct_size = sizeof(vs_meas_corner_params);
    vs_meas_default_line(&p->line1);
    vs_meas_default_line(&p->line2);
}

int32_t vs_meas_corner(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, const vs_meas_corner_params* p,
                       const vs_meas_calib* calib, vs_meas_corner_result* out)
{
    if (!imageOk(pixels, width, height, stride) || !resultOk(out) || !p || p->struct_size < static_cast<int32_t>(sizeof(vs_meas_corner_params)))
        return -1;
    resetResult(*out);
    const Image im{pixels, width, height, stride};
    int rc = lineImpl(im, &p->line1, calib, nullptr, 0, &out->line1);
    if (rc < 0) return rc;
    rc = lineImpl(im, &p->line2, calib, nullptr, 0, &out->line2);
    if (rc < 0) return rc;
    const vs_meas_line_result &a = out->line1, &b = out->line2;
    if (!a.found || !b.found) return 0;
    const Pt da{a.mx1 - a.mx0, a.my1 - a.my0}, db{b.mx1 - b.mx0, b.my1 - b.my0};
    const double la = std::hypot(da.x, da.y), lb = std::hypot(db.x, db.y), cr = da.x * db.y - da.y * db.x;
    if (!(la > 0 && lb > 0)) return 0;
    if (std::abs(cr) / (la * lb) < std::sin(0.5 * kPi / 180.0)) {
        out->parallel = 1;
        return 0;
    }
    // a0 + s da = b0 + t db
    const double s = ((b.mx0 - a.mx0) * db.y - (b.my0 - a.my0) * db.x) / cr;
    const Pt m{a.mx0 + s * da.x, a.my0 + s * da.y};
    // each edge leaves the corner towards the middle of its fitted line (or along it, when the corner is that middle)
    const auto ray = [&](const vs_meas_line_result& l, Pt d, double len) {
        Pt r{(l.mx0 + l.mx1) / 2 - m.x, (l.my0 + l.my1) / 2 - m.y};
        if (std::hypot(r.x, r.y) < 1e-6 * len) r = d;
        return r;
    };
    const Pt ra = ray(a, da, la), rb = ray(b, db, lb);
    const double cosv = (ra.x * rb.x + ra.y * rb.y) / (std::hypot(ra.x, ra.y) * std::hypot(rb.x, rb.y));
    const Pt ic = distort(calib, m);
    out->found = 1;
    out->x = static_cast<float>(ic.x); out->y = static_cast<float>(ic.y);
    out->mx = static_cast<float>(m.x); out->my = static_cast<float>(m.y);
    out->angle_deg = static_cast<float>(std::acos(std::clamp(cosv, -1.0, 1.0)) * 180.0 / kPi);
    return 0;
}

void vs_meas_default_gap(vs_meas_gap_params* p)
{
    if (!p) return;
    *p = vs_meas_gap_params{};
    p->struct_size = sizeof(vs_meas_gap_params);
    p->samples = 20;
    vs_meas_default_caliper(&p->caliper);
    p->caliper.mode = VS_MEAS_PAIR;
}

int32_t vs_meas_gap(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, const vs_meas_gap_params* p,
                    const vs_meas_calib* calib, float* widths, int32_t cap, vs_meas_gap_result* out)
{
    if (!imageOk(pixels, width, height, stride) || !resultOk(out) || !p || p->struct_size < static_cast<int32_t>(sizeof(vs_meas_gap_params)) ||
        cap < 0 || p->samples < 1 || p->samples > 200 || !boxOk(p->box) || !(p->box.height / p->samples >= 1.f) || !caliperParamsOk(&p->caliper))
        return -1;
    resetResult(*out);
    const Image im{pixels, width, height, stride};
    const double a = p->box.angle_deg * kPi / 180.0, vx = std::sin(a), vy = std::cos(a);
    const double h = static_cast<double>(p->box.height) / p->samples;
    vs_meas_caliper_params cp = p->caliper;
    cp.mode = VS_MEAS_PAIR;
    out->samples = p->samples;
    double sum = 0;
    for (int i = 0; i < p->samples; ++i) {
        const double off = -p->box.height / 2.0 + h * (i + 0.5);
        const vs_meas_box b{static_cast<float>(p->box.cx + vx * off), static_cast<float>(p->box.cy + vy * off), p->box.length,
                            static_cast<float>(h), p->box.angle_deg};
        vs_meas_caliper_result r;
        vs_meas_edge cand[64];
        const int rc = caliper(im, b, cp, calib, cand, 64, r);
        if (rc < 0) return rc;
        // a slice whose pair is in doubt gives no width, nor one with any edge between its two (a burr or a
        // partial bridge in the gap: the open width is not the width between the outer edges)
        bool inside = rc > 64;
        for (int k = 0; k < std::min(rc, 64) && r.found; ++k)
            if (cand[k].pos > r.edge0.pos + 0.5f && cand[k].pos < r.edge1.pos - 0.5f) inside = true;
        const bool ok = r.found && !r.ambiguous && r.width_px > 0 && !inside;
        if (widths && i < cap) widths[i] = ok ? r.width_px : std::numeric_limits<float>::quiet_NaN();
        if (!ok) continue;
        const float mx = (r.edge0.x + r.edge1.x) / 2, my = (r.edge0.y + r.edge1.y) / 2;
        if (out->found == 0 || r.width_px < out->min_px) { out->min_px = r.width_px; out->min_index = i; out->min_x = mx; out->min_y = my; }
        if (out->found == 0 || r.width_px > out->max_px) { out->max_px = r.width_px; out->max_index = i; out->max_x = mx; out->max_y = my; }
        sum += r.width_px;
        ++out->found;
    }
    if (out->found > 0) out->mean_px = static_cast<float>(sum / out->found);
    return 0;
}

void vs_meas_default_pitch(vs_meas_pitch_params* p)
{
    if (!p) return;
    *p = vs_meas_pitch_params{};
    p->struct_size = sizeof(vs_meas_pitch_params);
    p->polarity = VS_MEAS_DARK_TO_LIGHT;
    p->filter_half_width = 1.f;
    p->min_width = 1.f;
}

int32_t vs_meas_pitch(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, const vs_meas_pitch_params* p,
                      const vs_meas_calib* calib, vs_meas_pin* pins, int32_t cap, vs_meas_pitch_result* out)
{
    if (!imageOk(pixels, width, height, stride) || !resultOk(out) || !p || p->struct_size < static_cast<int32_t>(sizeof(vs_meas_pitch_params)) ||
        cap < 0 || !boxOk(p->box) || !polarityValid(p->polarity) || !filterValid(p->filter_half_width) || !(p->threshold >= 0.f) ||
        !(p->min_width >= 0.f))
        return -1;
    resetResult(*out);
    BoxEdges be;
    const int rc = boxEdges(Image{pixels, width, height, stride}, p->box, VS_MEAS_ANY, p->threshold, p->filter_half_width, calib, be);
    if (rc < 0) return rc;
    out->threshold = static_cast<float>(be.pr.threshold);
    const std::vector<Edge>& ed = be.pr.edges;
    if (ed.empty()) return 0;
    const bool leadUp = p->polarity == VS_MEAS_ANY ? ed[0].contrast > 0 : p->polarity == VS_MEAS_DARK_TO_LIGHT;
    std::vector<vs_meas_pin> found;
    for (size_t i = 0; i < ed.size();) {
        if ((ed[i].contrast > 0) != leadUp || i + 1 >= ed.size() || (ed[i + 1].contrast > 0) == leadUp) {
            ++out->unpaired;
            ++i;
            continue;
        }
        const Pt m0 = undistort(calib, edgeImage(be.pr, ed[i].pos)), m1 = undistort(calib, edgeImage(be.pr, ed[i + 1].pos));
        const double w = std::hypot(m1.x - m0.x, m1.y - m0.y);
        if (w < p->min_width) {
            out->unpaired += 2;
            i += 2;
            continue;
        }
        const Pt ic = edgeImage(be.pr, (ed[i].pos + ed[i + 1].pos) / 2);
        const Pt mc{(m0.x + m1.x) / 2, (m0.y + m1.y) / 2};
        found.push_back({static_cast<float>(ic.x), static_cast<float>(ic.y),
                         static_cast<float>(std::hypot(mc.x - be.mStart.x, mc.y - be.mStart.y)), static_cast<float>(w),
                         static_cast<float>(std::min(std::abs(ed[i].contrast), std::abs(ed[i + 1].contrast)))});
        i += 2;
    }
    out->count = static_cast<int>(found.size());
    for (int i = 0; pins && i < cap && i < out->count; ++i) pins[i] = found[static_cast<size_t>(i)];
    if (found.size() >= 2) {
        out->mean_pitch_px = (found.back().pos - found.front().pos) / (found.size() - 1);
        out->min_pitch_px = out->max_pitch_px = found[1].pos - found[0].pos;
        for (size_t i = 2; i < found.size(); ++i) {
            const float d = found[i].pos - found[i - 1].pos;
            out->min_pitch_px = std::min(out->min_pitch_px, d);
            out->max_pitch_px = std::max(out->max_pitch_px, d);
        }
    }
    return out->count;
}

void vs_meas_default_edges(vs_meas_edges_params* p)
{
    if (!p) return;
    *p = vs_meas_edges_params{};
    p->struct_size = sizeof(vs_meas_edges_params);
    p->polarity = VS_MEAS_ANY;
    p->filter_half_width = 1.f;
}

int32_t vs_meas_edges(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, const vs_meas_edges_params* p,
                      const vs_meas_calib* calib, vs_meas_edge* edges, int32_t cap, vs_meas_edges_result* out)
{
    if (!imageOk(pixels, width, height, stride) || !resultOk(out) || !p || p->struct_size < static_cast<int32_t>(sizeof(vs_meas_edges_params)) ||
        cap < 0 || !boxOk(p->box) || !polarityValid(p->polarity) || !filterValid(p->filter_half_width) || !(p->threshold >= 0.f))
        return -1;
    resetResult(*out);
    BoxEdges be;
    const int rc = boxEdges(Image{pixels, width, height, stride}, p->box, VS_MEAS_ANY, p->threshold, p->filter_half_width, calib, be);
    if (rc < 0) return rc;
    out->threshold = static_cast<float>(be.pr.threshold);
    std::vector<Pt> m;
    for (const Edge& e : be.pr.edges) {
        if (!polarityOk(p->polarity, e.contrast)) continue;
        if (edges && out->count < cap) edges[out->count] = toEdge(be.pr, e);
        m.push_back(undistort(calib, edgeImage(be.pr, e.pos)));
        ++out->count;
    }
    if (m.size() >= 2) {
        double lo = 1e300, hi = 0;
        for (size_t i = 1; i < m.size(); ++i) {
            const double d = std::hypot(m[i].x - m[i - 1].x, m[i].y - m[i - 1].y);
            lo = std::min(lo, d);
            hi = std::max(hi, d);
        }
        out->min_spacing_px = static_cast<float>(lo);
        out->max_spacing_px = static_cast<float>(hi);
        out->mean_spacing_px = static_cast<float>(std::hypot(m.back().x - m[0].x, m.back().y - m[0].y) / (m.size() - 1));
    }
    out->period_px = static_cast<float>(profilePeriod(be.prof) * be.scale);
    return out->count;
}

void vs_meas_to_measure(const vs_meas_calib* calib, float x, float y, float* mx, float* my)
{
    const Pt m = undistort(calib, {x, y});
    if (mx) *mx = static_cast<float>(m.x);
    if (my) *my = static_cast<float>(m.y);
}

void vs_meas_to_image(const vs_meas_calib* calib, float mx, float my, float* x, float* y)
{
    const Pt i = distort(calib, {mx, my});
    if (x) *x = static_cast<float>(i.x);
    if (y) *y = static_cast<float>(i.y);
}

} // extern "C"
