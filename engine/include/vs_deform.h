#ifndef VS_DEFORM_H
#define VS_DEFORM_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(__CYGWIN__)
  #ifdef VS_DEFORM_EXPORTS
    #define VS_DEFORM_API __declspec(dllexport)
  #else
    #define VS_DEFORM_API __declspec(dllimport)
  #endif
#else
  #define VS_DEFORM_API __attribute__((visibility("default")))
#endif

#define VS_DEFORM_OK 0
#define VS_DEFORM_ERR_NULL_PTR -1
#define VS_DEFORM_ERR_INVALID_DIM -2
#define VS_DEFORM_ERR_INVALID_MESH -3
#define VS_DEFORM_ERR_CONVERGENCE -4

/**
 * Parameters for Non-Rigid Deformable B-Spline Mesh Matcher.
 */
typedef struct vs_deform_params {
    int struct_size;                    /**< sizeof(vs_deform_params) */
    int pyramid_levels;                 /**< Hierarchical pyramid levels (1..4, default: 3) */
    int max_iterations_per_level;       /**< Maximum Gauss-Newton iterations per level (default: 15) */
    float smoothness_weight;            /**< Elastic thin-plate bending penalty lambda (default: 0.35f) */
    float convergence_epsilon;          /**< Displacement change threshold for early stop (default: 0.005f) */
    float rupture_strain_threshold;     /**< Area expansion threshold det(J)-1 flagging film rupture (default: 0.35f = 35%) */
    float crumple_bending_threshold;    /**< Maximum allowed bending energy before flagging crumple NG (default: 40.0f) */
    float min_alignment_score;          /**< Minimum normalized template match score post-unwarp (default: 0.70f) */
} vs_deform_params;

/**
 * Summary metrics of deformable match and material strain.
 */
typedef struct vs_deform_stats {
    int struct_size;                    /**< sizeof(vs_deform_stats) */
    float final_alignment_score;        /**< Normalized gradient alignment score post-match [0.0..1.0] */
    float mean_displacement_px;         /**< Average displacement magnitude across mesh nodes */
    float max_displacement_px;          /**< Maximum displacement magnitude across mesh nodes */
    float max_area_stretch;             /**< Max det(J) - 1.0 (positive expansion strain) */
    float max_area_compression;         /**< Max 1.0 - det(J) (compressive folding strain) */
    float total_bending_energy;         /**< Discrete thin-plate bending energy sum */
    int is_film_ruptured;               /**< 1 if area stretch exceeds rupture threshold (hole/tear risk) */
    int is_excessive_crumple;           /**< 1 if bending energy exceeds crumple threshold */
    int is_match_ok;                    /**< 1 if alignment score >= min_alignment_score and not ruptured */
} vs_deform_stats;

/**
 * Populate default parameters for packaging non-rigid inspection.
 */
VS_DEFORM_API void vs_deform_default_params(vs_deform_params* params);

/**
 * Estimate non-rigid displacement field between test image (wrinkled pouch/label)
 * and golden reference template using hierarchical regular mesh B-spline alignment.
 *
 * @param test_img Deformed/wrinkled input image (8-bit grayscale).
 * @param test_w Width of test image.
 * @param test_h Height of test image.
 * @param test_stride Stride in bytes of test image.
 * @param ref_img Golden flat reference template image (8-bit grayscale).
 * @param ref_w Width of reference template.
 * @param ref_h Height of reference template.
 * @param ref_stride Stride in bytes of reference template.
 * @param params Matcher configuration parameters (or NULL for defaults).
 * @param mesh_dx Output buffer for horizontal nodal displacements [mesh_rows * mesh_cols].
 * @param mesh_dy Output buffer for vertical nodal displacements [mesh_rows * mesh_cols].
 * @param mesh_cols Number of control mesh columns (e.g. 8..32).
 * @param mesh_rows Number of control mesh rows (e.g. 8..32).
 * @param stats Output summary metrics and strain diagnostics.
 * @return 0 on success, negative error code otherwise.
 */
VS_DEFORM_API int vs_deform_estimate(
    const uint8_t* test_img,
    int test_w,
    int test_h,
    int test_stride,
    const uint8_t* ref_img,
    int ref_w,
    int ref_h,
    int ref_stride,
    const vs_deform_params* params,
    float* mesh_dx,
    float* mesh_dy,
    int mesh_cols,
    int mesh_rows,
    vs_deform_stats* stats);

/**
 * Unwarp the wrinkled test image into the flat canonical template plane
 * using the estimated displacement mesh.
 *
 * @param test_img Deformed input image (8-bit grayscale).
 * @param test_w Width of test image.
 * @param test_h Height of test image.
 * @param test_stride Stride in bytes of test image.
 * @param mesh_dx Estimated horizontal nodal displacements [mesh_rows * mesh_cols].
 * @param mesh_dy Estimated vertical nodal displacements [mesh_rows * mesh_cols].
 * @param mesh_cols Number of mesh columns.
 * @param mesh_rows Number of mesh rows.
 * @param unwarped_out Output buffer for rectified flat image.
 * @param out_w Output image width (typically ref_w).
 * @param out_h Output image height (typically ref_h).
 * @param out_stride Output image stride in bytes.
 * @return 0 on success, negative error code otherwise.
 */
VS_DEFORM_API int vs_deform_unwarp(
    const uint8_t* test_img,
    int test_w,
    int test_h,
    int test_stride,
    const float* mesh_dx,
    const float* mesh_dy,
    int mesh_cols,
    int mesh_rows,
    uint8_t* unwarped_out,
    int out_w,
    int out_h,
    int out_stride);

/**
 * Analyze mechanical packaging strain (Jacobian determinant and bending energy)
 * from the displacement mesh to detect film tears, punctures, and excessive crumpling.
 *
 * @param mesh_dx Nodal horizontal displacements [mesh_rows * mesh_cols].
 * @param mesh_dy Nodal vertical displacements [mesh_rows * mesh_cols].
 * @param mesh_cols Number of mesh columns.
 * @param mesh_rows Number of mesh rows.
 * @param cell_w Physical or pixel width of each grid cell (ref_w / (cols - 1)).
 * @param cell_h Physical or pixel height of each grid cell (ref_h / (rows - 1)).
 * @param params Matcher configuration parameters (or NULL for defaults).
 * @param stats Output strain metrics.
 * @return 0 on success, negative error code otherwise.
 */
VS_DEFORM_API int vs_deform_analyze_strain(
    const float* mesh_dx,
    const float* mesh_dy,
    int mesh_cols,
    int mesh_rows,
    float cell_w,
    float cell_h,
    const vs_deform_params* params,
    vs_deform_stats* stats);

#ifdef __cplusplus
}
#endif

#endif /* VS_DEFORM_H */
