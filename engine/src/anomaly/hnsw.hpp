/*
 * hnsw.hpp - In-Memory Hierarchical Navigable Small World (HNSW) Vector Index.
 * Self-contained, header-only C++17 implementation optimized for CPU vector search.
 */
#ifndef HNSW_HPP
#define HNSW_HPP

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <queue>
#include <random>
#include <vector>

class HNSWIndex {
public:
    struct DistPair {
        float dist;
        int id;

        bool operator>(const DistPair& o) const { return dist > o.dist; }
        bool operator<(const DistPair& o) const { return dist < o.dist; }
    };

    int dim;
    int M;
    int maxM0;
    int efConstruction;
    int efSearch;
    double mL;

    int enter_point;
    int max_level;

    std::vector<float> data; // Contiguous vector storage [num_points * dim]
    std::vector<int> levels;
    std::vector<std::vector<std::vector<int>>> graph; // graph[id][level][neighbor_index]

    std::mt19937 rng;
    std::uniform_real_distribution<double> uniform_dist;

    HNSWIndex(int dimension = 32, int m = 16, int ef_c = 64, int ef_s = 24)
        : dim(dimension), M(m), maxM0(2 * m), efConstruction(ef_c), efSearch(ef_s),
          enter_point(-1), max_level(-1), rng(1337), uniform_dist(0.0, 1.0)
    {
        mL = 1.0 / std::log(static_cast<double>(M));
    }

    static inline float l2_sq(const float* a, const float* b, int d) {
        float sum = 0.0f;
        int i = 0;
        for (; i + 4 <= d; i += 4) {
            float d0 = a[i] - b[i];
            float d1 = a[i+1] - b[i+1];
            float d2 = a[i+2] - b[i+2];
            float d3 = a[i+3] - b[i+3];
            sum += d0*d0 + d1*d1 + d2*d2 + d3*d3;
        }
        for (; i < d; ++i) {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }
        return sum;
    }

    int random_level() {
        double r = uniform_dist(rng);
        if (r == 0.0) r = 1e-9;
        return static_cast<int>(-std::log(r) * mL);
    }

    const float* get_vector(int id) const {
        return data.data() + id * dim;
    }

    size_t size() const {
        return levels.size();
    }

    void clear() {
        data.clear();
        levels.clear();
        graph.clear();
        enter_point = -1;
        max_level = -1;
    }

    // Search at a specific layer for ef nearest neighbors
    std::priority_queue<DistPair, std::vector<DistPair>, std::less<DistPair>> search_layer(
        const float* query,
        int ep,
        int ef,
        int level) const
    {
        // Result set W (max-heap keeping the ef closest elements)
        std::priority_queue<DistPair, std::vector<DistPair>, std::less<DistPair>> W;
        // Candidate set C (min-heap exploring closest candidates)
        std::priority_queue<DistPair, std::vector<DistPair>, std::greater<DistPair>> C;

        std::vector<uint8_t> visited(size(), 0);

        float ep_dist = l2_sq(query, get_vector(ep), dim);
        W.push({ep_dist, ep});
        C.push({ep_dist, ep});
        visited[ep] = 1;

        while (!C.empty()) {
            DistPair curr = C.top();
            C.pop();

            // Furthest element in W
            DistPair worst = W.top();
            if (curr.dist > worst.dist) {
                break;
            }

            // Inspect neighbors of curr at this level
            if (curr.id < static_cast<int>(graph.size()) &&
                level < static_cast<int>(graph[curr.id].size())) {
                const auto& neighbors = graph[curr.id][level];
                for (int nb : neighbors) {
                    if (!visited[nb]) {
                        visited[nb] = 1;
                        float d = l2_sq(query, get_vector(nb), dim);
                        if (d < worst.dist || static_cast<int>(W.size()) < ef) {
                            C.push({d, nb});
                            W.push({d, nb});
                            if (static_cast<int>(W.size()) > ef) {
                                W.pop();
                            }
                            worst = W.top();
                        }
                    }
                }
            }
        }
        return W;
    }

    // Add a vector to the HNSW index
    int add_point(const float* vec) {
        int id = static_cast<int>(levels.size());
        int level = random_level();

        data.insert(data.end(), vec, vec + dim);
        levels.push_back(level);
        graph.push_back(std::vector<std::vector<int>>(level + 1));

        if (enter_point == -1) {
            enter_point = id;
            max_level = level;
            return id;
        }

        int curr_obj = enter_point;
        float curr_dist = l2_sq(vec, get_vector(curr_obj), dim);

        // 1. Zoom in greedily from top down to level + 1
        for (int l = max_level; l > level; --l) {
            bool changed = true;
            while (changed) {
                changed = false;
                if (curr_obj < static_cast<int>(graph.size()) &&
                    l < static_cast<int>(graph[curr_obj].size())) {
                    for (int nb : graph[curr_obj][l]) {
                        float d = l2_sq(vec, get_vector(nb), dim);
                        if (d < curr_dist) {
                            curr_dist = d;
                            curr_obj = nb;
                            changed = true;
                        }
                    }
                }
            }
        }

        // 2. Connect from min(level, max_level) down to 0
        int connect_top = std::min(level, max_level);
        for (int l = connect_top; l >= 0; --l) {
            auto W = search_layer(vec, curr_obj, efConstruction, l);

            // Collect neighbors from W
            std::vector<DistPair> candidates;
            while (!W.empty()) {
                candidates.push_back(W.top());
                W.pop();
            }
            std::sort(candidates.begin(), candidates.end(), [](const DistPair& a, const DistPair& b) {
                return a.dist < b.dist;
            });

            int max_links = (l == 0) ? maxM0 : M;
            int num_links = std::min(max_links, static_cast<int>(candidates.size()));

            for (int i = 0; i < num_links; ++i) {
                int nb = candidates[i].id;
                graph[id][l].push_back(nb);
                graph[nb][l].push_back(id);

                // Shrink if nb exceeded max links
                int nb_max = (l == 0) ? maxM0 : M;
                if (static_cast<int>(graph[nb][l].size()) > nb_max) {
                    // Simple heuristic: keep closest
                    std::vector<DistPair> nb_dists;
                    const float* nb_vec = get_vector(nb);
                    for (int cand : graph[nb][l]) {
                        nb_dists.push_back({l2_sq(nb_vec, get_vector(cand), dim), cand});
                    }
                    std::sort(nb_dists.begin(), nb_dists.end(), [](const DistPair& a, const DistPair& b) {
                        return a.dist < b.dist;
                    });
                    graph[nb][l].clear();
                    for (int k = 0; k < nb_max; ++k) {
                        graph[nb][l].push_back(nb_dists[k].id);
                    }
                }
            }

            if (!candidates.empty()) {
                curr_obj = candidates[0].id;
            }
        }

        if (level > max_level) {
            max_level = level;
            enter_point = id;
        }

        return id;
    }

    // Query k nearest neighbors
    void search_knn(
        const float* query,
        int k,
        std::vector<int>& out_indices,
        std::vector<float>& out_dists) const
    {
        out_indices.clear();
        out_dists.clear();

        if (enter_point == -1 || size() == 0) return;

        int curr_obj = enter_point;
        float curr_dist = l2_sq(query, get_vector(curr_obj), dim);

        // Zoom from top level down to layer 1
        for (int l = max_level; l > 0; --l) {
            bool changed = true;
            while (changed) {
                changed = false;
                if (curr_obj < static_cast<int>(graph.size()) &&
                    l < static_cast<int>(graph[curr_obj].size())) {
                    for (int nb : graph[curr_obj][l]) {
                        float d = l2_sq(query, get_vector(nb), dim);
                        if (d < curr_dist) {
                            curr_dist = d;
                            curr_obj = nb;
                            changed = true;
                        }
                    }
                }
            }
        }

        // Search at layer 0 with efSearch
        auto W = search_layer(query, curr_obj, std::max(k, efSearch), 0);

        std::vector<DistPair> res;
        while (!W.empty()) {
            res.push_back(W.top());
            W.pop();
        }
        std::sort(res.begin(), res.end(), [](const DistPair& a, const DistPair& b) {
            return a.dist < b.dist;
        });

        int return_count = std::min(k, static_cast<int>(res.size()));
        out_indices.resize(return_count);
        out_dists.resize(return_count);
        for (int i = 0; i < return_count; ++i) {
            out_indices[i] = res[i].id;
            out_dists[i] = std::sqrt(res[i].dist); // Euclidean distance L2
        }
    }
};

#endif // HNSW_HPP
