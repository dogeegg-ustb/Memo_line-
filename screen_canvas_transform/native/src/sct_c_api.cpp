#include "sct/c_api.h"

#include "sct/canvas_observe.hpp"
#include "sct/transform_solve.hpp"
#include "sct/viewport_frame.hpp"
#include "sct/workspace_canvas_relation.hpp"
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

SctVec2 ToCVec(const sct::Vec2& v) { return {v.x, v.y}; }

sct::Vec2 FromCVec(const SctVec2& v) { return {v.x, v.y}; }

SctWorkspaceCanvasRelation ToCRel(const sct::WorkspaceCanvasRelation& r) {
  SctWorkspaceCanvasRelation c{};
  c.workspace_roi = {r.workspace_roi.left, r.workspace_roi.top, r.workspace_roi.right,
                     r.workspace_roi.bottom};
  c.full_canvas_model_workspace_local = {
      r.full_canvas_model_workspace_local.left, r.full_canvas_model_workspace_local.top,
      r.full_canvas_model_workspace_local.right, r.full_canvas_model_workspace_local.bottom};
  c.visible_canvas_bounds_workspace_local = {
      r.visible_canvas_bounds_workspace_local.left, r.visible_canvas_bounds_workspace_local.top,
      r.visible_canvas_bounds_workspace_local.right, r.visible_canvas_bounds_workspace_local.bottom};
  c.visible_canvas_bounds_screen = {r.visible_canvas_bounds_screen.left,
                                    r.visible_canvas_bounds_screen.top,
                                    r.visible_canvas_bounds_screen.right,
                                    r.visible_canvas_bounds_screen.bottom};
  c.canvas_axis_x_workspace_local = ToCVec(r.canvas_axis_x_workspace_local);
  c.canvas_axis_y_workspace_local = ToCVec(r.canvas_axis_y_workspace_local);
  c.canvas_edges_in_workspace = r.canvas_edges_in_workspace;
  c.full_canvas_edge_evidence = r.full_canvas_edge_evidence;
  c.visible_canvas_edge_evidence = r.visible_canvas_edge_evidence;
  c.occluded_canvas_edges = r.occluded_canvas_edges;
  c.canvas_crop_sides = r.canvas_crop_sides;
  c.canvas_aspect_ratio = r.canvas_aspect_ratio;
  c.canvas_pixel_width = r.canvas_pixel_width;
  c.canvas_pixel_height = r.canvas_pixel_height;
  c.workspace_width = r.workspace_width;
  c.workspace_height = r.workspace_height;
  c.canvas_to_workspace_scale_x = r.canvas_to_workspace_scale_x;
  c.canvas_to_workspace_scale_y = r.canvas_to_workspace_scale_y;
  c.visible_canvas_workspace_fraction_x = r.visible_canvas_workspace_fraction_x;
  c.visible_canvas_workspace_fraction_y = r.visible_canvas_workspace_fraction_y;
  c.visible_canvas_fraction_x = r.visible_canvas_fraction_x;
  c.visible_canvas_fraction_y = r.visible_canvas_fraction_y;
  c.confidence = r.confidence;
  c.ambiguous = r.ambiguous ? 1 : 0;
  CopyStrC(c.ambiguity_reason, sizeof(c.ambiguity_reason), r.ambiguity_reason);
  CopyStrC(c.source_capture_id, sizeof(c.source_capture_id), r.source_capture_id);
  CopyStrC(c.source_revision, sizeof(c.source_revision), r.source_revision);
  return c;
}

sct::WorkspaceCanvasRelation FromCRel(const SctWorkspaceCanvasRelation& c) {
  sct::WorkspaceCanvasRelation r;
  r.workspace_roi = {c.workspace_roi.left, c.workspace_roi.top, c.workspace_roi.right,
                     c.workspace_roi.bottom};
  r.full_canvas_model_workspace_local = {
      c.full_canvas_model_workspace_local.left, c.full_canvas_model_workspace_local.top,
      c.full_canvas_model_workspace_local.right, c.full_canvas_model_workspace_local.bottom};
  r.visible_canvas_bounds_workspace_local = {
      c.visible_canvas_bounds_workspace_local.left, c.visible_canvas_bounds_workspace_local.top,
      c.visible_canvas_bounds_workspace_local.right, c.visible_canvas_bounds_workspace_local.bottom};
  r.visible_canvas_bounds_screen = {c.visible_canvas_bounds_screen.left,
                                    c.visible_canvas_bounds_screen.top,
                                    c.visible_canvas_bounds_screen.right,
                                    c.visible_canvas_bounds_screen.bottom};
  r.canvas_axis_x_workspace_local = FromCVec(c.canvas_axis_x_workspace_local);
  r.canvas_axis_y_workspace_local = FromCVec(c.canvas_axis_y_workspace_local);
  r.canvas_edges_in_workspace = c.canvas_edges_in_workspace;
  r.full_canvas_edge_evidence = c.full_canvas_edge_evidence;
  r.visible_canvas_edge_evidence = c.visible_canvas_edge_evidence;
  r.occluded_canvas_edges = c.occluded_canvas_edges;
  r.canvas_crop_sides = c.canvas_crop_sides;
  r.canvas_aspect_ratio = c.canvas_aspect_ratio;
  r.canvas_pixel_width = c.canvas_pixel_width;
  r.canvas_pixel_height = c.canvas_pixel_height;
  r.workspace_width = c.workspace_width;
  r.workspace_height = c.workspace_height;
  r.canvas_to_workspace_scale_x = c.canvas_to_workspace_scale_x;
  r.canvas_to_workspace_scale_y = c.canvas_to_workspace_scale_y;
  r.visible_canvas_workspace_fraction_x = c.visible_canvas_workspace_fraction_x;
  r.visible_canvas_workspace_fraction_y = c.visible_canvas_workspace_fraction_y;
  r.visible_canvas_fraction_x = c.visible_canvas_fraction_x;
  r.visible_canvas_fraction_y = c.visible_canvas_fraction_y;
  r.confidence = c.confidence;
  r.ambiguous = c.ambiguous != 0;
  CopyStrC(r.ambiguity_reason, sizeof(r.ambiguity_reason), c.ambiguity_reason);
  CopyStrC(r.source_capture_id, sizeof(r.source_capture_id), c.source_capture_id);
  CopyStrC(r.source_revision, sizeof(r.source_revision), c.source_revision);
  return r;
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

SCT_API int sct_build_workspace_canvas_relation(const SctWorkspaceCanvasRelationRequest* req,
                                                  SctWorkspaceCanvasRelation* out) {
  if (!out) return static_cast<int>(sct::FailStatus::InvalidCapture);
  std::memset(out, 0, sizeof(*out));
  if (!req) {
  return static_cast<int>(sct::FailStatus::InvalidCapture);
  }
  sct::WorkspaceCanvasRelationInput in;
  in.workspace_roi_screen = {req->workspace_roi_screen.left, req->workspace_roi_screen.top,
                             req->workspace_roi_screen.right, req->workspace_roi_screen.bottom};
  in.workspace_canvas = FromCObs(req->workspace_canvas);
  in.canvas_pixel_width = req->canvas_pixel_width;
  in.canvas_pixel_height = req->canvas_pixel_height;
  in.capture_id = req->capture_id;
  auto r = sct::BuildWorkspaceCanvasRelation(in);
  *out = ToCRel(r.relation);
  return static_cast<int>(r.status);
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
  in.workspace_canvas_relation = FromCRel(req->workspace_canvas_relation);
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
  out->confirmed_complete_edge_count = r.frame.red_evidence.confirmed_complete_edge_count;
  const int nce = r.frame.complete_edge_export_count < 4 ? r.frame.complete_edge_export_count : 4;
  for (int i = 0; i < nce; ++i) {
    out->complete_edges[i].p0_capture = {r.frame.complete_edges[i].p0.x,
                                         r.frame.complete_edges[i].p0.y};
    out->complete_edges[i].p1_capture = {r.frame.complete_edges[i].p1.x,
                                         r.frame.complete_edges[i].p1.y};
    out->complete_edges[i].workspace_edge = r.frame.complete_edges[i].workspace_edge;
    out->complete_edges[i].reserved = 0;
  }
  for (int i = nce; i < 4; ++i) {
    out->complete_edges[i] = {};
  }
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
  in.recompute_generation = req->recompute_generation;
  in.canvas_pixel_width = req->canvas_pixel_width;
  in.canvas_pixel_height = req->canvas_pixel_height;
  in.workspace_roi_screen = {req->workspace_roi_screen.left, req->workspace_roi_screen.top,
                             req->workspace_roi_screen.right, req->workspace_roi_screen.bottom};
  in.navigator_roi_screen = {req->navigator_roi_screen.left, req->navigator_roi_screen.top,
                             req->navigator_roi_screen.right, req->navigator_roi_screen.bottom};
  in.navigator_thumbnail_roi_screen = {
      req->navigator_thumbnail_roi_screen.left, req->navigator_thumbnail_roi_screen.top,
      req->navigator_thumbnail_roi_screen.right, req->navigator_thumbnail_roi_screen.bottom};
  in.workspace_canvas = FromCObs(req->workspace_canvas);
  in.navigator_canvas = FromCObs(req->navigator_canvas);
  in.workspace_canvas_relation = FromCRel(req->workspace_canvas_relation);
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
  in.viewport.red_evidence.confirmed_complete_edge_count =
      req->viewport.confirmed_complete_edge_count;
  in.viewport.complete_edge_export_count = 0;
  for (int i = 0; i < 4; ++i) {
    if (i >= req->viewport.confirmed_complete_edge_count) break;
    auto& ce = in.viewport.complete_edges[in.viewport.complete_edge_export_count++];
    ce.p0 = {req->viewport.complete_edges[i].p0_capture.x,
             req->viewport.complete_edges[i].p0_capture.y};
    ce.p1 = {req->viewport.complete_edges[i].p1_capture.x,
             req->viewport.complete_edges[i].p1_capture.y};
    ce.workspace_edge = req->viewport.complete_edges[i].workspace_edge;
  }
  in.previous_scale_percent = req->previous_scale_percent;
  in.initial_scale_percent = req->initial_scale_percent;
  in.injected_scale_percent = req->injected_scale_percent;
  in.require_ocr_rotation = req->require_ocr_rotation;
  in.marker_epsilon_canvas =
      req->marker_epsilon_canvas > 0 ? req->marker_epsilon_canvas : 0.04;

  auto r = sct::SolveTransform(in);
  out->status = static_cast<int>(r.status);
  const auto& s = r.snapshot;
  CopyStrC(out->snapshot_id, sizeof(out->snapshot_id), s.snapshot_id);
  out->generation = s.generation;
  out->recompute_generation = s.recompute_generation;
  CopyStrC(out->capture_id, sizeof(out->capture_id), s.capture_id);
  out->canvas_pixel_width = s.canvas_pixel_width;
  out->canvas_pixel_height = s.canvas_pixel_height;
  out->workspace_roi = {s.workspace_roi.left, s.workspace_roi.top, s.workspace_roi.right,
                        s.workspace_roi.bottom};
  out->navigator_roi = {s.navigator_roi.left, s.navigator_roi.top, s.navigator_roi.right,
                        s.navigator_roi.bottom};
  out->navigator_thumbnail_roi = {s.navigator_thumbnail_roi.left, s.navigator_thumbnail_roi.top,
                                  s.navigator_thumbnail_roi.right, s.navigator_thumbnail_roi.bottom};
  out->workspace_canvas = ToCObs(s.workspace_canvas, 0);
  out->navigator_canvas = ToCObs(s.navigator_canvas, 0);
  out->workspace_canvas_relation = ToCRel(s.workspace_canvas_relation);
  out->numbers = req->numbers;
  out->viewport = req->viewport;
  out->viewport.width = s.viewport.width;
  out->viewport.height = s.viewport.height;
  out->viewport.confidence = s.viewport.confidence;
  out->viewport.completion_strategy = s.viewport.completion_strategy;
  out->viewport.visible_edge_count = s.viewport.visible_edge_count;
  out->viewport.confirmed_complete_edge_count =
      s.viewport.red_evidence.confirmed_complete_edge_count;
  {
    const int nce = s.viewport.complete_edge_export_count < 4
                        ? s.viewport.complete_edge_export_count
                        : 4;
    for (int i = 0; i < nce; ++i) {
      out->viewport.complete_edges[i].p0_capture = {s.viewport.complete_edges[i].p0.x,
                                                    s.viewport.complete_edges[i].p0.y};
      out->viewport.complete_edges[i].p1_capture = {s.viewport.complete_edges[i].p1.x,
                                                    s.viewport.complete_edges[i].p1.y};
      out->viewport.complete_edges[i].workspace_edge =
          s.viewport.complete_edges[i].workspace_edge;
      out->viewport.complete_edges[i].reserved = 0;
    }
    for (int i = nce; i < 4; ++i) out->viewport.complete_edges[i] = {};
  }
  out->scale_reference = s.scale_reference;
  out->relative_scale = s.relative_scale;
  out->cumulative_relative_scale = s.cumulative_relative_scale;
  out->rotation_degrees_geometry = s.rotation_degrees_geometry;
  out->rotation_degrees_ocr_or_injected = s.rotation_degrees_ocr_or_injected;
  out->rotation_degrees = s.rotation_degrees;
  out->scale_percent_ocr_or_injected = s.scale_percent_ocr_or_injected;
  out->scale_geometry_estimate = s.scale_geometry_estimate;
  out->scale_consistency_error = s.scale_consistency_error;
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
  out->marker.target_arm_display_px = s.marker.target_arm_display_px;
  out->marker.target_stroke_display_px = s.marker.target_stroke_display_px;
  out->marker.arm_length_canvas = s.marker.arm_length_canvas;
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
