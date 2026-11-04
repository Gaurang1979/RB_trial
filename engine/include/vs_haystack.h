#ifndef VS_HAYSTACK_H
#define VS_HAYSTACK_H

#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
#  ifdef VS_HAYSTACK_BUILD
#    define VS_HAYSTACK_API __declspec(dllexport)
#  else
#    define VS_HAYSTACK_API __declspec(dllimport)
#  endif
#else
#  define VS_HAYSTACK_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define VS_HAYSTACK_MAX_CLUSTERS 32
#define VS_HAYSTACK_NAME_MAX 64

enum vs_haystack_verdict {
    VS_HAYSTACK_VERDICT_OK = 0,
    VS_HAYSTACK_VERDICT_NG = 1,
    VS_HAYSTACK_VERDICT_AMBIGUOUS = 2  /* In borderline margin: active learning candidate */
};

typedef struct vs_haystack_point_t {
    uint64_t image_id;
    float u;                        /* 2D projected coordinate [-1.0, 1.0] */
    float v;                        /* 2D projected coordinate [-1.0, 1.0] */
    int label;                      /* 0=OK, 1=NG, 2=AMBIGUOUS */
    int cluster_id;                 /* 0=Golden OK, 1..K=Defect modes, -1=Outlier */
    float confidence;               /* 0.0 to 1.0 */
    float distance_to_center;       /* Distance in 2D space to assigned cluster center */
    char defect_name[VS_HAYSTACK_NAME_MAX];
    uint8_t is_triaged;             /* 0=Auto inference, 1=Human operator triaged */
    uint64_t timestamp_ms;
} vs_haystack_point_t;

typedef struct vs_haystack_cluster_t {
    int cluster_id;
    char name[VS_HAYSTACK_NAME_MAX];
    float center_u;
    float center_v;
    float radius;                   /* Decision boundary radius */
    int point_count;
    uint32_t color_rgba;            /* 0xRRGGBBAA */
} vs_haystack_cluster_t;

typedef struct vs_haystack_params_t {
    int feature_dim;                /* Dimension of embeddings, e.g. 64 */
    int max_points;                 /* Capacity of circular buffer, e.g. 1000 */
    float golden_radius;            /* Radius around golden center for OK (e.g. 0.35) */
    float ambiguity_margin;         /* Borderline margin width (e.g. 0.15) */
} vs_haystack_params_t;

typedef struct vs_haystack_t vs_haystack_t;

VS_HAYSTACK_API int vs_haystack_create(const vs_haystack_params_t* params, vs_haystack_t** out_engine);
VS_HAYSTACK_API void vs_haystack_destroy(vs_haystack_t* engine);

/* Set projection weights explicitly: u = w1^T (z - mean) / scale1, v = w2^T (z - mean) / scale2 */
VS_HAYSTACK_API int vs_haystack_set_projection(vs_haystack_t* engine,
                                               const float* mean,
                                               const float* w1,
                                               const float* w2,
                                               float scale1,
                                               float scale2);

/* Fit 2D projection via power iteration PCA from training embeddings */
VS_HAYSTACK_API int vs_haystack_fit_pca(vs_haystack_t* engine,
                                        const float* embeddings,
                                        int num_samples,
                                        int feature_dim);

/* Register or update a defect cluster or golden cluster */
VS_HAYSTACK_API int vs_haystack_add_cluster(vs_haystack_t* engine,
                                            int cluster_id,
                                            const char* name,
                                            float center_u,
                                            float center_v,
                                            float radius,
                                            uint32_t color_rgba);

/* Project a live high-dimensional embedding into 2D space, classify and add to rolling buffer */
VS_HAYSTACK_API int vs_haystack_project(vs_haystack_t* engine,
                                        const float* embedding,
                                        int feature_dim,
                                        uint64_t image_id,
                                        uint64_t timestamp_ms,
                                        vs_haystack_point_t* out_point);

/* Active Learning: Operator triages a point in the buffer, updating its label and defect name */
VS_HAYSTACK_API int vs_haystack_triage_point(vs_haystack_t* engine,
                                             uint64_t image_id,
                                             int new_label,
                                             int new_cluster_id,
                                             const char* defect_name);

/* Retrieve rolling points for visualization in HaystackMap.razor */
VS_HAYSTACK_API int vs_haystack_get_points(const vs_haystack_t* engine,
                                           vs_haystack_point_t* out_points,
                                           int max_count,
                                           int* actual_count);

/* Retrieve clusters */
VS_HAYSTACK_API int vs_haystack_get_clusters(const vs_haystack_t* engine,
                                             vs_haystack_cluster_t* out_clusters,
                                             int max_count,
                                             int* actual_count);

/* Retrieve points currently pending triage (label == AMBIGUOUS and is_triaged == 0) */
VS_HAYSTACK_API int vs_haystack_get_ambiguous_points(const vs_haystack_t* engine,
                                                     vs_haystack_point_t* out_points,
                                                     int max_count,
                                                     int* actual_count);

/* Clear points buffer */
VS_HAYSTACK_API int vs_haystack_clear_points(vs_haystack_t* engine);

#ifdef __cplusplus
}
#endif

#endif /* VS_HAYSTACK_H */
