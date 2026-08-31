#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#ifdef SCT_NATIVE_EXPORTS
#define SCT_API __declspec(dllexport)
#else
#define SCT_API __declspec(dllimport)
#endif
#else
#define SCT_API
#endif

#define SCT_API_VERSION 3
#define SCT_COORD_CONVENTION_VERSION 1
#define SCT_SOURCE_REVISION "sct-embedded-wb"

typedef struct SctIntRect {
  int left;
  int top;
  int right;
  int bottom;
} SctIntRect;

typedef struct SctBackgroundModel {
  float center_lab_l;
  float center_lab_a;
  float center_lab_b;
  float strong_delta_e;
  float weak_delta_e;
  float confidence;
} SctBackgroundModel;

typedef struct SctDetectRequest {
  const unsigned char* bgra;
  int width;
  int height;
  int stride;
  SctIntRect user_roi;
  float dpi_x;
  float dpi_y;
  int origin_x;
  int origin_y;
  const char* capture_id;
} SctDetectRequest;

typedef struct SctDetectResult {
  int status;
  SctIntRect workspace_capture;
  SctIntRect workspace_screen;
  int evidence_grade;
  float confidence;
  char message[256];
  char source_capture_id[64];
  SctBackgroundModel background;
  int has_background;
  int source_backend;  // 0=CPU reference compatibility
  char source_revision[64];
  int api_version;
} SctDetectResult;

typedef struct SctCiiRequest {
  const unsigned char* bgra;
  int width;
  int height;
  int stride;
  SctIntRect navigator_roi;
  float dpi_x;
  float dpi_y;
  int origin_x;
  int origin_y;
  const char* capture_id;
  SctBackgroundModel background;
} SctCiiRequest;

typedef struct SctCanvasObserveRequest {
  const unsigned char* bgra;
  int width;
  int height;
  int stride;
  SctIntRect roi_capture;
  int origin_x;
  int origin_y;
  SctBackgroundModel background;
  float dpi_scale;
} SctCanvasObserveRequest;

typedef struct SctCanvasObservation {
  int status;
  SctIntRect bounds_capture;
  SctIntRect bounds_screen;
  float aspect_ratio;
  float confidence;
  int visible_edges_mask;
  float boundary_support[4];
  int four_sides_complete;
  int ambiguous;
  char ambiguity_reason[128];
} SctCanvasObservation;

typedef struct SctVec2 {
  double x;
  double y;
} SctVec2;

typedef struct SctWorkspaceCanvasRelation {
  SctIntRect workspace_roi;
  SctIntRect full_canvas_model_workspace_local;
  SctIntRect visible_canvas_bounds_workspace_local;
  SctIntRect visible_canvas_bounds_screen;
  SctVec2 canvas_axis_x_workspace_local;
  SctVec2 canvas_axis_y_workspace_local;
  int canvas_edges_in_workspace;
  int full_canvas_edge_evidence;
  int visible_canvas_edge_evidence;
  int occluded_canvas_edges;
  int canvas_crop_sides;
  float canvas_aspect_ratio;
  int canvas_pixel_width;
  int canvas_pixel_height;
  int workspace_width;
  int workspace_height;
  float canvas_to_workspace_scale_x;
  float canvas_to_workspace_scale_y;
  float visible_canvas_workspace_fraction_x;
  float visible_canvas_workspace_fraction_y;
  float visible_canvas_fraction_x;
  float visible_canvas_fraction_y;
  float confidence;
  int ambiguous;
  char ambiguity_reason[128];
  char source_capture_id[64];
  char source_revision[64];
} SctWorkspaceCanvasRelation;

typedef struct SctWorkspaceCanvasRelationRequest {
  SctIntRect workspace_roi_screen;
  SctCanvasObservation workspace_canvas;
  int canvas_pixel_width;
  int canvas_pixel_height;
  const char* capture_id;
} SctWorkspaceCanvasRelationRequest;

typedef struct SctViewportRequest {
  const unsigned char* bgra;
  int width;
  int height;
  int stride;
  SctIntRect thumbnail_roi;
  SctIntRect navigator_canvas_bounds;
  SctWorkspaceCanvasRelation workspace_canvas_relation;
  float dpi_scale;
} SctViewportRequest;

typedef struct SctCompleteEdge {
  SctVec2 p0_capture;
  SctVec2 p1_capture;
  int workspace_edge;
  int reserved;
} SctCompleteEdge;

typedef struct SctViewportFrame {
  int status;
  SctVec2 origin_top_left_displayed;
  SctVec2 axis_x_displayed;
  SctVec2 axis_y_displayed;
  float width;
  float height;
  SctVec2 semantic_corners[4];
  int visible_edge_count;
  int completion_strategy;
  float confidence;
  char message[128];
  int confirmed_complete_edge_count;
  SctCompleteEdge complete_edges[4];
} SctViewportFrame;

typedef struct SctNumericReading {
  float scale_percent;
  float rotation_degrees;
  float scale_confidence;
  float rotation_confidence;
  char scale_raw[64];
  char rotation_raw[64];
  char capture_id[64];
} SctNumericReading;

typedef struct SctAffine2D {
  double m[6];
} SctAffine2D;

typedef struct SctMarkerGeometry {
  SctVec2 anchor_screen;
  SctVec2 x_arm_end_screen;
  SctVec2 y_arm_end_screen;
  int offscreen;
  float target_arm_display_px;
  float target_stroke_display_px;
  double arm_length_canvas;
} SctMarkerGeometry;

typedef struct SctSolveRequest {
  char capture_id[64];
  uint64_t generation;
  uint64_t recompute_generation;
  int canvas_pixel_width;
  int canvas_pixel_height;
  SctIntRect workspace_roi_screen;
  SctIntRect navigator_roi_screen;
  SctIntRect navigator_thumbnail_roi_screen;
  SctCanvasObservation workspace_canvas;
  SctCanvasObservation navigator_canvas;
  SctWorkspaceCanvasRelation workspace_canvas_relation;
  SctNumericReading numbers;
  SctViewportFrame viewport;
  float previous_scale_percent;
  float initial_scale_percent;
  float injected_scale_percent;
  int require_ocr_rotation;
  double marker_epsilon_canvas;
} SctSolveRequest;

typedef struct SctFailure {
  int stage;
  int status;
  char message[256];
  char capture_id[64];
  uint64_t generation;
  char source_revision[64];
  char evidence_summary[256];
} SctFailure;

typedef struct SctTransformSnapshot {
  int status;
  char snapshot_id[64];
  uint64_t generation;
  uint64_t recompute_generation;
  char capture_id[64];
  int canvas_pixel_width;
  int canvas_pixel_height;
  SctIntRect workspace_roi;
  SctIntRect navigator_roi;
  SctIntRect navigator_thumbnail_roi;
  SctCanvasObservation workspace_canvas;
  SctCanvasObservation navigator_canvas;
  SctWorkspaceCanvasRelation workspace_canvas_relation;
  SctNumericReading numbers;
  SctViewportFrame viewport;
  float scale_reference;
  float relative_scale;
  float cumulative_relative_scale;
  float rotation_degrees_geometry;
  float rotation_degrees_ocr_or_injected;
  float rotation_degrees;
  float scale_percent_ocr_or_injected;
  float scale_geometry_estimate;
  float scale_consistency_error;
  SctAffine2D screen_to_workspace;
  SctAffine2D workspace_to_screen;
  SctAffine2D workspace_to_canvas;
  SctAffine2D canvas_to_workspace;
  SctAffine2D screen_to_canvas;
  SctAffine2D canvas_to_screen;
  SctMarkerGeometry marker;
  float confidence;
  int used_direct_workspace_path;
  char source_revision[64];
  int coordinate_convention_version;
  SctFailure failure;
} SctTransformSnapshot;

SCT_API int sct_api_version(void);
SCT_API const char* sct_status_name(int status);
SCT_API const char* sct_source_revision(void);

SCT_API int sct_detect_workspace(const SctDetectRequest* req, SctDetectResult* result);
SCT_API int sct_detect_navigator_thumbnail_cii(const SctCiiRequest* req, SctDetectResult* result);
SCT_API int sct_observe_canvas(const SctCanvasObserveRequest* req, SctCanvasObservation* out);
SCT_API int sct_build_workspace_canvas_relation(const SctWorkspaceCanvasRelationRequest* req,
                                                SctWorkspaceCanvasRelation* out);
SCT_API int sct_complete_viewport_frame(const SctViewportRequest* req, SctViewportFrame* out);
SCT_API int sct_solve_transform(const SctSolveRequest* req, SctTransformSnapshot* out);

#ifdef __cplusplus
}
#endif
