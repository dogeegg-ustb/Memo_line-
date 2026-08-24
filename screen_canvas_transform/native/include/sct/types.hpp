#pragma once

#include "wb/types.hpp"

#include <array>
#include <cstdint>
#include <cstring>
#include <string>

namespace sct {

enum class Stage : int {
  Idle = 0,
  CaptureFrozen = 1,
  DetectingWorkspace = 2,
  DetectingNavigatorThumbnailCII = 3,
  ObservingWorkspaceCanvas = 4,
  ObservingNavigatorCanvas = 5,
  ReadingNavigatorNumbers = 6,
  CompletingViewportFrame = 7,
  SolvingTransform = 8,
  ShowingMarker = 9,
  TrackingStable = 10,
};

enum class FailStatus : int {
  Ok = 0,
  InvalidCapture = 100,
  WorkspaceDetectionFailed = 101,
  WorkspaceBackgroundUnavailable = 102,
  NavigatorRoiInvalid = 103,
  NavigatorThumbnailCiiFailed = 104,
  NavigatorCanvasAmbiguous = 105,
  WorkspaceCanvasAmbiguous = 106,
  OcrScaleFailed = 107,
  OcrRotationFailed = 108,
  RedFrameNotFound = 109,
  InsufficientViewportGeometry = 110,
  AmbiguousViewportGeometry = 111,
  ScaleGeometryConflict = 112,
  MatrixSingular = 113,
  MatrixIllConditioned = 114,
  CaptureIdMismatch = 115,
  StaleGeneration = 116,
  MarkerOffscreen = 117,
  GpuUnavailable = 118,
  GpuDeviceLost = 119,
  GpuAnalysisUnavailable = 120,
  InvalidCanvasPixelSize = 121,
  RotationGeometryConflict = 122,
};

// ViewportCompletionPattern encoding: 1=0.1, 2=0.2, 10=1.0, 20=2.0, 21=2.1, 30=3.0, 40=4.0
enum class ViewportCompletionPattern : int {
  ParallelSegmentsNoCompleteEdge = 1,   // 0.1
  IntersectingSegmentsNoCompleteEdge = 2,  // 0.2
  OneCompleteEdge = 10,                 // 1.0
  TwoIntersectingCompleteEdges = 20,    // 2.0
  TwoParallelCompleteEdges = 21,        // 2.1
  ThreeCompleteEdges = 30,              // 3.0
  FourCompleteEdges = 40,                // 4.0
};

inline const char* FailStatusName(FailStatus s) {
  switch (s) {
    case FailStatus::Ok:
      return "Ok";
    case FailStatus::InvalidCapture:
      return "InvalidCapture";
    case FailStatus::WorkspaceDetectionFailed:
      return "WorkspaceDetectionFailed";
    case FailStatus::WorkspaceBackgroundUnavailable:
      return "WorkspaceBackgroundUnavailable";
    case FailStatus::NavigatorRoiInvalid:
      return "NavigatorRoiInvalid";
    case FailStatus::NavigatorThumbnailCiiFailed:
      return "NavigatorThumbnailCiiFailed";
    case FailStatus::NavigatorCanvasAmbiguous:
      return "NavigatorCanvasAmbiguous";
    case FailStatus::WorkspaceCanvasAmbiguous:
      return "WorkspaceCanvasAmbiguous";
    case FailStatus::OcrScaleFailed:
      return "OcrScaleFailed";
    case FailStatus::OcrRotationFailed:
      return "OcrRotationFailed";
    case FailStatus::RedFrameNotFound:
      return "RedFrameNotFound";
    case FailStatus::InsufficientViewportGeometry:
      return "InsufficientViewportGeometry";
    case FailStatus::AmbiguousViewportGeometry:
      return "AmbiguousViewportGeometry";
    case FailStatus::ScaleGeometryConflict:
      return "ScaleGeometryConflict";
    case FailStatus::MatrixSingular:
      return "MatrixSingular";
    case FailStatus::MatrixIllConditioned:
      return "MatrixIllConditioned";
    case FailStatus::CaptureIdMismatch:
      return "CaptureIdMismatch";
    case FailStatus::StaleGeneration:
      return "StaleGeneration";
    case FailStatus::MarkerOffscreen:
      return "MarkerOffscreen";
    case FailStatus::GpuUnavailable:
      return "GpuUnavailable";
    case FailStatus::GpuDeviceLost:
      return "GpuDeviceLost";
    case FailStatus::GpuAnalysisUnavailable:
      return "GpuAnalysisUnavailable";
    case FailStatus::InvalidCanvasPixelSize:
      return "InvalidCanvasPixelSize";
    case FailStatus::RotationGeometryConflict:
      return "RotationGeometryConflict";
  }
  return "Unknown";
}

struct Vec2 {
  double x = 0;
  double y = 0;
};

// Row-major 3x3 affine: [m00 m01 m02; m10 m11 m12; 0 0 1]
struct Affine2D {
  std::array<double, 6> m{1, 0, 0, 0, 1, 0};

  double m00() const { return m[0]; }
  double m01() const { return m[1]; }
  double m02() const { return m[2]; }
  double m10() const { return m[3]; }
  double m11() const { return m[4]; }
  double m12() const { return m[5]; }

  Vec2 Apply(Vec2 p) const {
    return {m[0] * p.x + m[1] * p.y + m[2], m[3] * p.x + m[4] * p.y + m[5]};
  }

  static Affine2D Identity() { return {}; }

  static Affine2D Translation(double tx, double ty) {
    Affine2D a;
    a.m = {1, 0, tx, 0, 1, ty};
    return a;
  }

  static Affine2D Scale(double sx, double sy) {
    Affine2D a;
    a.m = {sx, 0, 0, 0, sy, 0};
    return a;
  }

  static Affine2D FromCorners(Vec2 src00, Vec2 src10, Vec2 src01, Vec2 dst00, Vec2 dst10,
                              Vec2 dst01);
};

struct CanvasObservation {
  wb::IntRect bounds_capture{};
  wb::IntRect bounds_screen{};
  float aspect_ratio = 0.f;
  float confidence = 0.f;
  int visible_edges_mask = 0;  // bit0=L bit1=T bit2=R bit3=B
  float boundary_support[4] = {0, 0, 0, 0};
  bool four_sides_complete = false;
  bool ambiguous = false;
  char ambiguity_reason[128] = {};
};

struct NavigatorNumericReading {
  float scale_percent = 0.f;  // OCR or injected; marker/diagnostics only for matrix
  float rotation_degrees = 0.f;  // OCR or injected; diagnostic/validation only
  float scale_confidence = 0.f;
  float rotation_confidence = 0.f;
  char scale_raw[64] = {};
  char rotation_raw[64] = {};
  char capture_id[64] = {};
};

struct RedLineSegment {
  double x0 = 0;
  double y0 = 0;
  double x1 = 0;
  double y1 = 0;
  bool horizontal = false;
  float support = 0.f;
  int corner_at_start = -1;
  int corner_at_end = -1;
};

struct WorkspaceCanvasRelation {
  wb::IntRect workspace_roi{};
  wb::IntRect full_canvas_model_workspace_local{};
  wb::IntRect visible_canvas_bounds_workspace_local{};
  wb::IntRect visible_canvas_bounds_screen{};
  Vec2 canvas_axis_x_workspace_local{1, 0};
  Vec2 canvas_axis_y_workspace_local{0, 1};
  int canvas_edges_in_workspace = 0;
  int full_canvas_edge_evidence = 0;
  int visible_canvas_edge_evidence = 0;
  int occluded_canvas_edges = 0;
  int canvas_crop_sides = 0;
  float canvas_aspect_ratio = 1.f;
  int canvas_pixel_width = 0;
  int canvas_pixel_height = 0;
  int workspace_width = 0;
  int workspace_height = 0;
  float canvas_to_workspace_scale_x = 0.f;
  float canvas_to_workspace_scale_y = 0.f;
  // Visible canvas share of the workspace background on each directed axis.
  float visible_canvas_workspace_fraction_x = 0.f;
  float visible_canvas_workspace_fraction_y = 0.f;
  // Visible canvas share of the complete canvas on each directed axis.
  float visible_canvas_fraction_x = 0.f;
  float visible_canvas_fraction_y = 0.f;
  float confidence = 0.f;
  bool ambiguous = false;
  char ambiguity_reason[128] = {};
  char source_capture_id[64] = {};
  char source_revision[64] = {};
};

struct RedFrameEvidence {
  RedLineSegment segments[32]{};
  int segment_count = 0;
  int confirmed_corner_count = 0;
  int confirmed_complete_edge_count = 0;
  int partial_edge_count = 0;
  int unanchored_segment_count = 0;
  ViewportCompletionPattern completion_pattern = ViewportCompletionPattern::FourCompleteEdges;
};

struct NavigatorViewportFrame {
  Vec2 origin_top_left_displayed{};  // o_v
  Vec2 axis_x_displayed{};           // a_x
  Vec2 axis_y_displayed{};           // a_y
  float width = 0.f;
  float height = 0.f;
  Vec2 semantic_corners[4]{};  // TL, TR, BR, BL
  int visible_edge_count = 0;
  int completion_strategy = 0;  // ViewportCompletionPattern encoding
  float confidence = 0.f;
  RedFrameEvidence red_evidence{};
};

struct MarkerGeometry {
  Vec2 anchor_screen{};
  Vec2 x_arm_end_screen{};
  Vec2 y_arm_end_screen{};
  bool offscreen = false;
  float target_arm_display_px = 0.f;
  float target_stroke_display_px = 0.f;
  double arm_length_canvas = 0.f;
};

struct TransformSnapshot {
  char snapshot_id[64] = {};
  uint64_t generation = 0;
  uint64_t recompute_generation = 0;
  char capture_id[64] = {};
  int canvas_pixel_width = 0;
  int canvas_pixel_height = 0;
  wb::IntRect workspace_roi{};
  wb::IntRect navigator_roi{};
  wb::IntRect navigator_thumbnail_roi{};
  CanvasObservation workspace_canvas{};
  CanvasObservation navigator_canvas{};
  WorkspaceCanvasRelation workspace_canvas_relation{};
  NavigatorNumericReading numbers{};
  NavigatorViewportFrame viewport{};
  float scale_reference = 100.f;
  float relative_scale = 1.f;
  float cumulative_relative_scale = 1.f;
  float rotation_degrees_geometry = 0.f;
  float rotation_degrees_ocr_or_injected = 0.f;
  float rotation_degrees = 0.f;  // alias of geometry authority
  float scale_percent_ocr_or_injected = 0.f;
  float scale_geometry_estimate = 0.f;
  float scale_consistency_error = 0.f;
  Affine2D screen_to_workspace{};
  Affine2D workspace_to_screen{};
  Affine2D workspace_to_canvas{};
  Affine2D canvas_to_workspace{};
  Affine2D screen_to_canvas{};
  Affine2D canvas_to_screen{};
  MarkerGeometry marker{};
  float confidence = 0.f;
  int used_direct_workspace_path = 0;
  char source_revision[64] = {};
  int coordinate_convention_version = 1;
};

struct Failure {
  Stage stage = Stage::Idle;
  FailStatus status = FailStatus::Ok;
  char message[256] = {};
  char capture_id[64] = {};
  uint64_t generation = 0;
  char source_revision[64] = {};
  char evidence_summary[256] = {};
};

}  // namespace sct
