#pragma once

#include "sct/types.hpp"
#include "wb/types.hpp"

namespace sct {

struct SolveInput {
  char capture_id[64] = {};
  uint64_t generation = 0;
  uint64_t recompute_generation = 0;
  int canvas_pixel_width = 0;
  int canvas_pixel_height = 0;
  wb::IntRect workspace_roi_screen{};
  wb::IntRect navigator_roi_screen{};
  wb::IntRect navigator_thumbnail_roi_screen{};
  CanvasObservation workspace_canvas{};
  CanvasObservation navigator_canvas{};
  WorkspaceCanvasRelation workspace_canvas_relation{};
  NavigatorNumericReading numbers{};
  NavigatorViewportFrame viewport{};
  float previous_scale_percent = 0.f;
  float initial_scale_percent = 0.f;
  float injected_scale_percent = 0.f;  // >0 bypasses OCR requirement for scale
  int require_ocr_rotation = 0;      // 0 = geometry authority, 1 = strict OCR
  double marker_epsilon_canvas = 0.04;  // fallback when scale unavailable
};

struct SolveResult {
  FailStatus status = FailStatus::Ok;
  TransformSnapshot snapshot{};
  Failure failure{};
};

SolveResult SolveTransform(const SolveInput& in);

Affine2D InvertAffine(const Affine2D& a, bool* ok);
Affine2D Multiply(const Affine2D& a, const Affine2D& b);
double ConditionEstimate(const Affine2D& a);

}  // namespace sct
