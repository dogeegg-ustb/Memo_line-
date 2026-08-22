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

#define SCT_API_VERSION 1
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

typedef struct SctViewportRequest {
  const unsigned char* bgra;
  int width;
  int height;
  int stride;
  SctIntRect thumbnail_roi;
  SctIntRect navigator_canvas_bounds;
  float workspace_aspect;
  float dpi_scale;
} SctViewportRequest;

typedef struct SctVec2 {
  double x;
  double y;
} SctVec2;

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
} SctMarkerGeometry;

typedef struct SctSolveRequest {
  char capture_id[64];
  uint64_t generation;
  SctIntRect workspace_roi_screen;
  SctIntRect navigator_roi_screen;
  SctIntRect navigator_thumbnail_roi_screen;
  SctCanvasObservation workspace_canvas;
  SctCanvasObservation navigator_canvas;
  SctNumericReading numbers;
  SctViewportFrame viewport;
  float previous_scale_percent;
  float initial_scale_percent;
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
  char capture_id[64];
  SctIntRect workspace_roi;
  SctIntRect navigator_roi;
  SctIntRect navigator_thumbnail_roi;
  SctCanvasObservation workspace_canvas;
  SctCanvasObservation navigator_canvas;
  SctNumericReading numbers;
  SctViewportFrame viewport;
  float scale_reference;
  float relative_scale;
  float cumulative_relative_scale;
  float rotation_degrees;
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
SCT_API int sct_complete_viewport_frame(const SctViewportRequest* req, SctViewportFrame* out);
SCT_API int sct_solve_transform(const SctSolveRequest* req, SctTransformSnapshot* out);

#ifdef __cplusplus
}
#endif
