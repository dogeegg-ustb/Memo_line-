#include "sct/c_api.h"

#include "sct/canvas_observe.hpp"
#include "sct/transform_solve.hpp"
#include "sct/viewport_frame.hpp"
#include "wb/detector.hpp"

#include <cstdio>
#include <cstring>

namespace {

void CopyStr(char* dst, size_t n, const std::string& s) {
  if (!dst || n == 0) return;
  std::snprintf(dst, n, "%s", s.c_str());
}

void CopyStrC(char* dst, size_t n, const char* s) {
  if (!dst || n == 0) return;
  std::snprintf(dst, n, "%s", s ? s : "");
}

wb::BackgroundModel FromC(const SctBackgroundModel& m) {
  wb::BackgroundModel b;
  b.center_lab = {m.center_lab_l, m.center_lab_a, m.center_lab_b};
  b.strong_delta_e = m.strong_delta_e;
  b.weak_delta_e = m.weak_delta_e;
  b.confidence = m.confidence;
  return b;
}

SctBackgroundModel ToC(const wb::BackgroundModel& b) {
  SctBackgroundModel m{};
  m.center_lab_l = b.center_lab.L;
  m.center_lab_a = b.center_lab.a;
  m.center_lab_b = b.center_lab.b;
  m.strong_delta_e = b.strong_delta_e;
  m.weak_delta_e = b.weak_delta_e;
  m.confidence = b.confidence;
  return m;
}

void FillDetectResult(SctDetectResult* result, const wb::DetectionOutput& out) {
  result->status = static_cast<int>(out.status);
  result->workspace_capture = {out.workspace_capture.left, out.workspace_capture.top,
                               out.workspace_capture.right, out.workspace_capture.bottom};
  result->workspace_screen = {out.workspace_screen.left, out.workspace_screen.top,
                              out.workspace_screen.right, out.workspace_screen.bottom};
  result->evidence_grade = static_cast<int>(out.grade);
  result->confidence = out.confidence;
  CopyStr(result->message, sizeof(result->message), out.message);
  CopyStr(result->source_capture_id, sizeof(result->source_capture_id), out.source_capture_id);
  result->has_background = out.has_background_model ? 1 : 0;
  if (out.has_background_model) result->background = ToC(out.background_model);
  result->source_backend = 0;
  CopyStr(result->source_revision, sizeof(result->source_revision), out.source_revision);
  result->api_version = SCT_API_VERSION;
}

SctCanvasObservation ToCObs(const sct::CanvasObservation& o, int status) {
  SctCanvasObservation c{};
  c.status = status;
  c.bounds_capture = {o.bounds_capture.left, o.bounds_capture.top, o.bounds_capture.right,
                      o.bounds_capture.bottom};
  c.bounds_screen = {o.bounds_screen.left, o.bounds_screen.top, o.bounds_screen.right,
                     o.bounds_screen.bottom};
  c.aspect_ratio = o.aspect_ratio;
  c.confidence = o.confidence;
  c.visible_edges_mask = o.visible_edges_mask;
  for (int i = 0; i < 4; ++i) c.boundary_support[i] = o.boundary_support[i];
  c.four_sides_complete = o.four_sides_complete ? 1 : 0;
  c.ambiguous = o.ambiguous ? 1 : 0;
  CopyStrC(c.ambiguity_reason, sizeof(c.ambiguity_reason), o.ambiguity_reason);
  return c;
}

sct::CanvasObservation FromCObs(const SctCanvasObservation& c) {
  sct::CanvasObservation o;
  o.bounds_capture = {c.bounds_capture.left, c.bounds_capture.top, c.bounds_capture.right,
                      c.bounds_capture.bottom};
  o.bounds_screen = {c.bounds_screen.left, c.bounds_screen.top, c.bounds_screen.right,
                     c.bounds_screen.bottom};
  o.aspect_ratio = c.aspect_ratio;
  o.confidence = c.confidence;
  o.visible_edges_mask = c.visible_edges_mask;
  for (int i = 0; i < 4; ++i) o.boundary_support[i] = c.boundary_support[i];
  o.four_sides_complete = c.four_sides_complete != 0;
  o.ambiguous = c.ambiguous != 0;
  CopyStrC(o.ambiguity_reason, sizeof(o.ambiguity_reason), c.ambiguity_reason);
  return o;
}

void CopyAffine(SctAffine2D& dst, const sct::Affine2D& src) {
  for (int i = 0; i < 6; ++i) dst.m[i] = src.m[i];
}

}  // namespace

extern "C" {

SCT_API int sct_api_version(void) { return SCT_API_VERSION; }

SCT_API const char* sct_source_revision(void) { return SCT_SOURCE_REVISION; }

SCT_API const char* sct_status_name(int status) {
  if (status >= 0 && status < 100) return wb::StatusName(static_cast<wb::Status>(status));
  return sct::FailStatusName(static_cast<sct::FailStatus>(status));
}

SCT_API int sct_detect_workspace(const SctDetectRequest* req, SctDetectResult* result) {
  // Embedded WorkspaceBorderDetector (source-integrated; no wb C API / DLL).
  if (!result) return static_cast<int>(wb::Status::InvalidInput);
  std::memset(result, 0, sizeof(*result));
  result->api_version = SCT_API_VERSION;
  CopyStrC(result->source_revision, sizeof(result->source_revision), SCT_SOURCE_REVISION);
  if (!req || !req->bgra || req->width <= 0 || req->height <= 0) {
    result->status = static_cast<int>(wb::Status::InvalidInput);
    CopyStrC(result->message, sizeof(result->message), "InvalidInput");
    return result->status;
  }

  wb::DetectionInput in;
  in.bgra = req->bgra;
  in.width = req->width;
  in.height = req->height;
  in.stride = req->stride;
  in.user_roi = {req->user_roi.left, req->user_roi.top, req->user_roi.right, req->user_roi.bottom};
  in.dpi_x = req->dpi_x;
  in.dpi_y = req->dpi_y;
  in.origin_x = req->origin_x;
  in.origin_y = req->origin_y;
  in.capture_id = req->capture_id;

  wb::WorkspaceBorderDetector detector;
  wb::DetectionOutput out = detector.Detect(in);
  FillDetectResult(result, out);
  return result->status;
}

SCT_API int sct_detect_navigator_thumbnail_cii(const SctCiiRequest* req, SctDetectResult* result) {
  // Embedded C-II path: reuse workspace background model, no re-estimation.
  if (!result) return static_cast<int>(wb::Status::InvalidInput);
  std::memset(result, 0, sizeof(*result));
  result->api_version = SCT_API_VERSION;
  CopyStrC(result->source_revision, sizeof(result->source_revision), SCT_SOURCE_REVISION);
  if (!req || !req->bgra || req->width <= 0 || req->height <= 0) {
    result->status = static_cast<int>(sct::FailStatus::NavigatorRoiInvalid);
    CopyStrC(result->message, sizeof(result->message), "NavigatorRoiInvalid");
    return result->status;
  }

  wb::DetectionInput in;
  in.bgra = req->bgra;
  in.width = req->width;
  in.height = req->height;
  in.stride = req->stride;
  in.user_roi = {req->navigator_roi.left, req->navigator_roi.top, req->navigator_roi.right,
                 req->navigator_roi.bottom};
  in.dpi_x = req->dpi_x;
  in.dpi_y = req->dpi_y;
  in.origin_x = req->origin_x;
  in.origin_y = req->origin_y;
  in.capture_id = req->capture_id;

  wb::BackgroundModel model = FromC(req->background);
  if (model.strong_delta_e <= 0.f) model.strong_delta_e = 6.f;
  if (model.weak_delta_e <= 0.f) model.weak_delta_e = 12.f;

  wb::WorkspaceBorderDetector detector;
  wb::DetectionOutput out = detector.DetectCiiWithExternalBackground(in, model);
  FillDetectResult(result, out);
  if (out.status != wb::Status::Ok) {
    result->status = static_cast<int>(sct::FailStatus::NavigatorThumbnailCiiFailed);
  }
  return result->status;
}

SCT_API int sct_observe_canvas(const SctCanvasObserveRequest* req, SctCanvasObservation* out) {
  if (!out) return static_cast<int>(sct::FailStatus::InvalidCapture);
  std::memset(out, 0, sizeof(*out));
  if (!req || !req->bgra) {
    out->status = static_cast<int>(sct::FailStatus::InvalidCapture);
    CopyStrC(out->ambiguity_reason, sizeof(out->ambiguity_reason), "invalid");
    return out->status;
  }
  auto obs = sct::ObserveCanvasExcludingBackground(
      req->bgra, req->width, req->height, req->stride,
      {req->roi_capture.left, req->roi_capture.top, req->roi_capture.right, req->roi_capture.bottom},
      req->origin_x, req->origin_y, FromC(req->background), req->dpi_scale);
  *out = ToCObs(obs, obs.ambiguous ? static_cast<int>(sct::FailStatus::WorkspaceCanvasAmbiguous)
                                   : static_cast<int>(sct::FailStatus::Ok));
  return out->status;
}

SCT_API int sct_complete_viewport_frame(const SctViewportRequest* req, SctViewportFrame* out) {
  if (!out) return static_cast<int>(sct::FailStatus::InvalidCapture);
  std::memset(out, 0, sizeof(*out));
  if (!req || !req->bgra) {
    out->status = static_cast<int>(sct::FailStatus::InvalidCapture);
    CopyStrC(out->message, sizeof(out->message), "invalid");
    return out->status;
  }
  sct::ViewportCompletionInput in;
  in.bgra = req->bgra;
  in.width = req->width;
  in.height = req->height;
  in.stride = req->stride;
  in.thumbnail_roi = {req->thumbnail_roi.left, req->thumbnail_roi.top, req->thumbnail_roi.right,
                      req->thumbnail_roi.bottom};
  in.navigator_canvas_bounds = {req->navigator_canvas_bounds.left, req->navigator_canvas_bounds.top,
                                req->navigator_canvas_bounds.right,
                                req->navigator_canvas_bounds.bottom};
  in.workspace_aspect = req->workspace_aspect;
  in.dpi_scale = req->dpi_scale;
  auto r = sct::CompleteViewportFrame(in);
  out->status = static_cast<int>(r.status);
  out->origin_top_left_displayed = {r.frame.origin_top_left_displayed.x,
                                    r.frame.origin_top_left_displayed.y};
  out->axis_x_displayed = {r.frame.axis_x_displayed.x, r.frame.axis_x_displayed.y};
  out->axis_y_displayed = {r.frame.axis_y_displayed.x, r.frame.axis_y_displayed.y};
  out->width = r.frame.width;
  out->height = r.frame.height;
  for (int i = 0; i < 4; ++i) {
    out->semantic_corners[i] = {r.frame.semantic_corners[i].x, r.frame.semantic_corners[i].y};
  }
  out->visible_edge_count = r.frame.visible_edge_count;
  out->completion_strategy = r.frame.completion_strategy;
  out->confidence = r.frame.confidence;
  CopyStrC(out->message, sizeof(out->message), r.message);
  return out->status;
}

SCT_API int sct_solve_transform(const SctSolveRequest* req, SctTransformSnapshot* out) {
  if (!out) return static_cast<int>(sct::FailStatus::InvalidCapture);
  std::memset(out, 0, sizeof(*out));
  if (!req) {
    out->status = static_cast<int>(sct::FailStatus::InvalidCapture);
    return out->status;
  }

  sct::SolveInput in;
  CopyStrC(in.capture_id, sizeof(in.capture_id), req->capture_id);
  in.generation = req->generation;
  in.workspace_roi_screen = {req->workspace_roi_screen.left, req->workspace_roi_screen.top,
                             req->workspace_roi_screen.right, req->workspace_roi_screen.bottom};
  in.navigator_roi_screen = {req->navigator_roi_screen.left, req->navigator_roi_screen.top,
                             req->navigator_roi_screen.right, req->navigator_roi_screen.bottom};
  in.navigator_thumbnail_roi_screen = {
      req->navigator_thumbnail_roi_screen.left, req->navigator_thumbnail_roi_screen.top,
      req->navigator_thumbnail_roi_screen.right, req->navigator_thumbnail_roi_screen.bottom};
  in.workspace_canvas = FromCObs(req->workspace_canvas);
  in.navigator_canvas = FromCObs(req->navigator_canvas);
  in.numbers.scale_percent = req->numbers.scale_percent;
  in.numbers.rotation_degrees = req->numbers.rotation_degrees;
  in.numbers.scale_confidence = req->numbers.scale_confidence;
  in.numbers.rotation_confidence = req->numbers.rotation_confidence;
  CopyStrC(in.numbers.scale_raw, sizeof(in.numbers.scale_raw), req->numbers.scale_raw);
  CopyStrC(in.numbers.rotation_raw, sizeof(in.numbers.rotation_raw), req->numbers.rotation_raw);
  CopyStrC(in.numbers.capture_id, sizeof(in.numbers.capture_id), req->numbers.capture_id);

  in.viewport.origin_top_left_displayed = {req->viewport.origin_top_left_displayed.x,
                                           req->viewport.origin_top_left_displayed.y};
  in.viewport.axis_x_displayed = {req->viewport.axis_x_displayed.x, req->viewport.axis_x_displayed.y};
  in.viewport.axis_y_displayed = {req->viewport.axis_y_displayed.x, req->viewport.axis_y_displayed.y};
  in.viewport.width = req->viewport.width;
  in.viewport.height = req->viewport.height;
  for (int i = 0; i < 4; ++i) {
    in.viewport.semantic_corners[i] = {req->viewport.semantic_corners[i].x,
                                       req->viewport.semantic_corners[i].y};
  }
  in.viewport.visible_edge_count = req->viewport.visible_edge_count;
  in.viewport.completion_strategy = req->viewport.completion_strategy;
  in.viewport.confidence = req->viewport.confidence;
  in.previous_scale_percent = req->previous_scale_percent;
  in.initial_scale_percent = req->initial_scale_percent;
  in.marker_epsilon_canvas =
      req->marker_epsilon_canvas > 0 ? req->marker_epsilon_canvas : 0.04;

  auto r = sct::SolveTransform(in);
  out->status = static_cast<int>(r.status);
  const auto& s = r.snapshot;
  CopyStrC(out->snapshot_id, sizeof(out->snapshot_id), s.snapshot_id);
  out->generation = s.generation;
  CopyStrC(out->capture_id, sizeof(out->capture_id), s.capture_id);
  out->workspace_roi = {s.workspace_roi.left, s.workspace_roi.top, s.workspace_roi.right,
                        s.workspace_roi.bottom};
  out->navigator_roi = {s.navigator_roi.left, s.navigator_roi.top, s.navigator_roi.right,
                        s.navigator_roi.bottom};
  out->navigator_thumbnail_roi = {s.navigator_thumbnail_roi.left, s.navigator_thumbnail_roi.top,
                                  s.navigator_thumbnail_roi.right, s.navigator_thumbnail_roi.bottom};
  out->workspace_canvas = ToCObs(s.workspace_canvas, 0);
  out->navigator_canvas = ToCObs(s.navigator_canvas, 0);
  out->numbers = req->numbers;
  out->viewport = req->viewport;
  out->viewport.width = s.viewport.width;
  out->viewport.height = s.viewport.height;
  out->viewport.confidence = s.viewport.confidence;
  out->scale_reference = s.scale_reference;
  out->relative_scale = s.relative_scale;
  out->cumulative_relative_scale = s.cumulative_relative_scale;
  out->rotation_degrees = s.rotation_degrees;
  CopyAffine(out->screen_to_workspace, s.screen_to_workspace);
  CopyAffine(out->workspace_to_screen, s.workspace_to_screen);
  CopyAffine(out->workspace_to_canvas, s.workspace_to_canvas);
  CopyAffine(out->canvas_to_workspace, s.canvas_to_workspace);
  CopyAffine(out->screen_to_canvas, s.screen_to_canvas);
  CopyAffine(out->canvas_to_screen, s.canvas_to_screen);
  out->marker.anchor_screen = {s.marker.anchor_screen.x, s.marker.anchor_screen.y};
  out->marker.x_arm_end_screen = {s.marker.x_arm_end_screen.x, s.marker.x_arm_end_screen.y};
  out->marker.y_arm_end_screen = {s.marker.y_arm_end_screen.x, s.marker.y_arm_end_screen.y};
  out->marker.offscreen = s.marker.offscreen ? 1 : 0;
  out->confidence = s.confidence;
  out->used_direct_workspace_path = s.used_direct_workspace_path;
  CopyStrC(out->source_revision, sizeof(out->source_revision), s.source_revision);
  out->coordinate_convention_version = s.coordinate_convention_version;
  out->failure.stage = static_cast<int>(r.failure.stage);
  out->failure.status = static_cast<int>(r.failure.status);
  CopyStrC(out->failure.message, sizeof(out->failure.message), r.failure.message);
  CopyStrC(out->failure.capture_id, sizeof(out->failure.capture_id), r.failure.capture_id);
  out->failure.generation = r.failure.generation;
  CopyStrC(out->failure.source_revision, sizeof(out->failure.source_revision),
           r.failure.source_revision);
  CopyStrC(out->failure.evidence_summary, sizeof(out->failure.evidence_summary),
           r.failure.evidence_summary);
  return out->status;
}

}  // extern "C"
