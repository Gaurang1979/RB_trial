// slots.cpp - the case's slot map (fixturing, as NI Vision Builder and DALSA Sherlock do it with a
// pattern match on the case; here the read codes themselves are the fixture).
//
// Bottles in a crate, caps in a tray and cartons in a case sit on a lattice: on the 30 rigid-case
// sample images every read code lies within 0.04-0.1 pitch of its node (worst 0.46), and every
// unread code at the line targets fell on an empty node. Shrink-wrapped cans do not (0.2-0.37
// pitch off, nodes 20-80% filled): the fit says so and the slot map stays off for them.
//
// From the read codes' centres: the two pitch vectors (nearest-neighbour differences, the two
// densest directions), an affine lattice by least squares, each code's node, and the empty nodes
// of the case. The case's size (rows x columns) is learned from complete cases; until then the
// nodes between the read codes are the case.
#include "reader.hpp"

#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cmath>
#include <map>

namespace cr {

namespace {

constexpr float kMaxMedianOff = 0.15f; // pitches: a code this far from its node on average is not on a lattice
constexpr float kMaxOff = 0.5f;        // ... and none this far
constexpr int kMinCodes = 6;

float angleDist(float a, float b) // directions mod 180 degrees, radians
{
    float d = std::fmod(std::abs(a - b), static_cast<float>(CV_PI));
    return std::min(d, static_cast<float>(CV_PI) - d);
}

// Lagrange-Gauss reduction: the shortest basis of the same lattice
void reduce(SlotGrid& g)
{
    for (int it = 0; it < 20; ++it) {
        if (g.a.dot(g.a) > g.b.dot(g.b)) std::swap(g.a, g.b);
        const float mu = std::round(g.a.dot(g.b) / std::max(1e-6f, g.a.dot(g.a)));
        if (mu == 0.f) return;
        g.b -= g.a * mu;
    }
}

// lattice indices of p: solve p - o = i a + j b
cv::Point2f indexOf(const SlotGrid& g, cv::Point2f p)
{
    const cv::Point2f d = p - g.origin;
    const float det = g.a.x * g.b.y - g.a.y * g.b.x;
    if (std::abs(det) < 1e-6f) return {0, 0};
    return {(d.x * g.b.y - d.y * g.b.x) / det, (g.a.x * d.y - g.a.y * d.x) / det};
}

} // namespace

cv::Point2f slotCentre(const SlotGrid& g, int i, int j)
{
    return g.origin + g.a * static_cast<float>(i) + g.b * static_cast<float>(j);
}

bool slotOf(const SlotGrid& g, cv::Point2f p, int* i, int* j, float* off)
{
    if (!g.valid) return false;
    const cv::Point2f k = indexOf(g, p);
    const int ri = static_cast<int>(std::lround(k.x)), rj = static_cast<int>(std::lround(k.y));
    if (i) *i = ri;
    if (j) *j = rj;
    if (off) *off = static_cast<float>(cv::norm(p - slotCentre(g, ri, rj))) / g.pitch;
    return ri >= g.i0 && ri < g.i0 + g.ni && rj >= g.j0 && rj < g.j0 + g.nj;
}

void slotRowCol(const SlotGrid& g, int i, int j, int* row, int* col)
{
    // rows run down the image, columns across, both from 1 at the top left
    const bool aDown = std::abs(g.a.y) > std::abs(g.a.x);
    const cv::Point2f down = aDown ? g.a : g.b, across = aDown ? g.b : g.a;
    int di = aDown ? i - g.i0 : j - g.j0, dj = aDown ? j - g.j0 : i - g.i0;
    const int nd = aDown ? g.ni : g.nj, na = aDown ? g.nj : g.ni;
    if (down.y < 0) di = nd - 1 - di;
    if (across.x < 0) dj = na - 1 - dj;
    if (row) *row = di + 1;
    if (col) *col = dj + 1;
}

SlotGrid fitSlots(const std::vector<cv::Point2f>& pts, int learnedA, int learnedB, const cv::Mat* gray, float codeSide, int expected)
{
    SlotGrid g;
    const size_t n = pts.size();
    if (n < static_cast<size_t>(kMinCodes)) return g;
    // nearest-neighbour difference vectors, folded to one half-plane
    std::vector<float> nn(n, 1e9f);
    for (size_t a = 0; a < n; ++a)
        for (size_t b = 0; b < n; ++b)
            if (a != b) nn[a] = std::min(nn[a], static_cast<float>(cv::norm(pts[a] - pts[b])));
    std::vector<float> nns = nn;
    std::nth_element(nns.begin(), nns.begin() + static_cast<long>(n / 2), nns.end());
    const float near = nns[n / 2];
    std::vector<cv::Point2f> vecs;
    for (size_t a = 0; a < n; ++a)
        for (size_t b = 0; b < n; ++b) {
            if (a == b) continue;
            cv::Point2f v = pts[b] - pts[a];
            if (cv::norm(v) > 1.6f * near) continue;
            if (v.y < 0 || (v.y == 0 && v.x < 0)) v = -v;
            vecs.push_back(v);
        }
    if (vecs.size() < 4) return g;
    std::vector<float> ang;
    for (const auto& v : vecs) ang.push_back(std::fmod(std::atan2(v.y, v.x) + static_cast<float>(CV_PI), static_cast<float>(CV_PI)));
    const auto density = [&](float a) {
        int c = 0;
        for (float x : ang) c += angleDist(x, a) < 0.12f;
        return c;
    };
    std::vector<size_t> order(ang.size());
    for (size_t i = 0; i < order.size(); ++i) order[i] = i;
    std::vector<int> dens(ang.size());
    for (size_t i = 0; i < ang.size(); ++i) dens[i] = density(ang[i]);
    std::sort(order.begin(), order.end(), [&](size_t x, size_t y) { return dens[x] > dens[y]; });
    const float a1 = ang[order[0]];
    float a2 = -1.f;
    for (size_t k : order)
        if (angleDist(ang[k], a1) > static_cast<float>(CV_PI / 6)) { a2 = ang[k]; break; }
    if (a2 < 0) return g;
    cv::Point2f basis[2];
    const float as[2] = {a1, a2};
    for (int k = 0; k < 2; ++k) {
        const cv::Point2f ref(std::cos(as[k]), std::sin(as[k]));
        std::vector<float> xs, ys;
        for (size_t i = 0; i < vecs.size(); ++i) {
            if (angleDist(ang[i], as[k]) >= 0.12f) continue;
            cv::Point2f v = vecs[i];
            if (v.dot(ref) < 0) v = -v;
            xs.push_back(v.x);
            ys.push_back(v.y);
        }
        std::nth_element(xs.begin(), xs.begin() + static_cast<long>(xs.size() / 2), xs.end());
        std::nth_element(ys.begin(), ys.begin() + static_cast<long>(ys.size() / 2), ys.end());
        basis[k] = cv::Point2f(xs[xs.size() / 2], ys[ys.size() / 2]);
    }
    g.a = basis[0];
    g.b = basis[1];
    reduce(g);
    g.origin = pts[0];
    for (const auto& p : pts)
        if (p.x + p.y < g.origin.x + g.origin.y) g.origin = p;
    // indices, then the affine lattice by least squares, three rounds
    std::vector<cv::Point> ij(n);
    bool restarted = false;
    for (int round = 0; round < 3; ++round) {
        for (size_t k = 0; k < n; ++k) {
            const cv::Point2f f = indexOf(g, pts[k]);
            ij[k] = cv::Point(static_cast<int>(std::lround(f.x)), static_cast<int>(std::lround(f.y)));
        }
        cv::Mat A(static_cast<int>(n), 3, CV_64F), B(static_cast<int>(n), 2, CV_64F), X;
        for (size_t k = 0; k < n; ++k) {
            A.at<double>(static_cast<int>(k), 0) = 1;
            A.at<double>(static_cast<int>(k), 1) = ij[k].x;
            A.at<double>(static_cast<int>(k), 2) = ij[k].y;
            B.at<double>(static_cast<int>(k), 0) = pts[k].x;
            B.at<double>(static_cast<int>(k), 1) = pts[k].y;
        }
        if (!cv::solve(A, B, X, cv::DECOMP_SVD)) return g;
        g.origin = cv::Point2f(static_cast<float>(X.at<double>(0, 0)), static_cast<float>(X.at<double>(0, 1)));
        g.a = cv::Point2f(static_cast<float>(X.at<double>(1, 0)), static_cast<float>(X.at<double>(1, 1)));
        g.b = cv::Point2f(static_cast<float>(X.at<double>(2, 0)), static_cast<float>(X.at<double>(2, 1)));
        // a sparse case can yield a diagonal as a pitch: the same nodes, but a skewed outline that
        // reaches past the case; the reduced basis is the two shortest, most nearly square pitches
        const cv::Point2f a0 = g.a, b0 = g.b;
        reduce(g);
        if ((a0 != g.a || b0 != g.b) && !restarted) { round = -1; restarted = true; } // indices again in the reduced basis, once
    }
    g.pitch = static_cast<float>(std::min(cv::norm(g.a), cv::norm(g.b)));
    if (g.pitch <= 1.f) return g;
    std::vector<float> off;
    std::map<std::pair<int, int>, int> used;
    for (size_t k = 0; k < n; ++k) {
        off.push_back(static_cast<float>(cv::norm(pts[k] - slotCentre(g, ij[k].x, ij[k].y))) / g.pitch);
        used[{ij[k].x, ij[k].y}]++;
    }
    std::vector<float> sorted = off;
    std::sort(sorted.begin(), sorted.end());
    g.offMedian = sorted[sorted.size() / 2];
    g.offMax = sorted.back();
    // two codes on one node: not a lattice of single codes (labels with two copies are one code)
    const bool shared = used.size() < n;
    int i0 = ij[0].x, i1 = i0, j0 = ij[0].y, j1 = j0;
    for (const auto& p : ij) { i0 = std::min(i0, p.x); i1 = std::max(i1, p.x); j0 = std::min(j0, p.y); j1 = std::max(j1, p.y); }
    g.i0 = i0; g.ni = i1 - i0 + 1; g.j0 = j0; g.nj = j1 - j0 + 1;
    // the case's size: learned rows x columns (either way round); the nodes between the read codes
    // may be a row or column short, on the side the image shows texture
    // before the size is learned, the expected count gives it: the rows x columns that multiply to
    // it and hold the read nodes with the least growth (a 24-code case read over 4 x 5 is 4 x 6)
    if ((learnedA <= 0 || learnedB <= 0) && expected > g.ni * g.nj) {
        int best = -1;
        for (int x = 2; x <= expected / 2; ++x) {
            if (expected % x) continue;
            const int y = expected / x;
            for (const auto& [ti, tj] : {std::pair<int, int>{x, y}, std::pair<int, int>{y, x}})
                if (ti >= g.ni && tj >= g.nj && (best < 0 || (ti - g.ni) + (tj - g.nj) < best)) {
                    best = (ti - g.ni) + (tj - g.nj);
                    learnedA = ti;
                    learnedB = tj;
                }
        }
        // only a short growth: more would be a guess at the case's shape
        if (best < 0 || best > 2) learnedA = learnedB = 0;
    }
    if (learnedA > 0 && learnedB > 0) {
        // the orientation of the learned size that holds the read nodes with the least growth
        int ti = -1, tj = -1;
        for (const auto& [x, y] : {std::pair<int, int>{learnedA, learnedB}, std::pair<int, int>{learnedB, learnedA}})
            if (g.ni <= x && g.nj <= y && (ti < 0 || (x - g.ni) + (y - g.nj) < (ti - g.ni) + (tj - g.nj))) { ti = x; tj = y; }
        // a node's texture: mean gradient in a code-sized window; -1 outside the image
        const auto texture = [&](int i, int j) {
            const cv::Point2f c = slotCentre(g, i, j);
            const int h = static_cast<int>(std::max(4.f, codeSide * 0.5f));
            const cv::Rect r(static_cast<int>(c.x) - h, static_cast<int>(c.y) - h, 2 * h, 2 * h);
            if (!gray || (r & cv::Rect(0, 0, gray->cols, gray->rows)) != r) return -1.0;
            cv::Mat gx, gy, m;
            cv::Sobel((*gray)(r), gx, CV_32F, 1, 0);
            cv::Sobel((*gray)(r), gy, CV_32F, 0, 1);
            cv::magnitude(gx, gy, m);
            return cv::mean(m)[0];
        };
        // grow one row or column at a time, on the side whose new nodes show more texture
        while (ti > 0 && (g.ni < ti || g.nj < tj)) {
            const bool growI = g.ni < ti;
            double lo = 0, hi = 0;
            const int cnt = growI ? g.nj : g.ni;
            for (int k = 0; k < cnt; ++k) {
                const double a = growI ? texture(g.i0 - 1, g.j0 + k) : texture(g.i0 + k, g.j0 - 1);
                const double b = growI ? texture(g.i0 + g.ni, g.j0 + k) : texture(g.i0 + k, g.j0 + g.nj);
                lo = a < 0 || lo < 0 ? -1 : lo + a;
                hi = b < 0 || hi < 0 ? -1 : hi + b;
            }
            if (lo < 0 && hi < 0) break; // both sides leave the image: not this case's size
            const bool before = hi < 0 || (lo >= 0 && lo > hi);
            if (growI) { if (before) --g.i0; ++g.ni; }
            else { if (before) --g.j0; ++g.nj; }
        }
    }
    g.valid = !shared && g.offMedian <= kMaxMedianOff && g.offMax <= kMaxOff && g.ni >= 2 && g.nj >= 2 &&
              static_cast<int>(used.size()) * 100 >= g.ni * g.nj * 35; // at least 35% of the nodes read (the off-lattice test keeps scattered labels out)
    return g;
}

cv::Point2f slotCentreNear(const SlotGrid& g, int i, int j, const std::vector<cv::Point2f>& read)
{
    const cv::Point2f c = slotCentre(g, i, j);
    // perspective and the lens bend the case's rows near the image edge: the affine node is
    // corrected by how far its nearest read neighbours sit from their own nodes
    std::vector<std::pair<float, cv::Point2f>> near;
    for (const auto& p : read) {
        int pi = 0, pj = 0;
        float off = 0;
        if (!slotOf(g, p, &pi, &pj, &off) || off > kMaxOff) continue;
        near.emplace_back(static_cast<float>(cv::norm(p - c)), p - slotCentre(g, pi, pj));
    }
    if (near.empty()) return c;
    std::sort(near.begin(), near.end(), [](const auto& x, const auto& y) { return x.first < y.first; });
    cv::Point2f d(0, 0);
    const size_t k = std::min<size_t>(4, near.size());
    for (size_t m = 0; m < k; ++m) d += near[m].second;
    return c + d * (1.f / static_cast<float>(k));
}

std::vector<std::pair<int, int>> emptySlots(const SlotGrid& g, const std::vector<cv::Point2f>& read)
{
    std::vector<std::pair<int, int>> out;
    if (!g.valid) return out;
    std::vector<char> full(static_cast<size_t>(g.ni * g.nj), 0);
    for (const auto& p : read) {
        int i = 0, j = 0;
        float off = 0;
        if (slotOf(g, p, &i, &j, &off) && off < kMaxOff) full[static_cast<size_t>((i - g.i0) * g.nj + (j - g.j0))] = 1;
    }
    for (int i = 0; i < g.ni; ++i)
        for (int j = 0; j < g.nj; ++j)
            if (!full[static_cast<size_t>(i * g.nj + j)]) out.emplace_back(g.i0 + i, g.j0 + j);
    return out;
}

} // namespace cr
