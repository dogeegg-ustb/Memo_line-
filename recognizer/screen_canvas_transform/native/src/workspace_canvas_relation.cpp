#include "sct/workspace_canvas_relation.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>

namespace sct {
namespace {

constexpr int kMaxCanvasPixel = 65536;
constexpr int kEdgeL = 1;
constexpr int kEdgeT = 2;
constexpr int kEdgeR = 4;
constexpr int kEdgeB = 8;

WorkspaceCanvasRelationResult Fail(FailStatus st, const char* msg) {
  WorkspaceCanvasRelationResult r;
  r.status = st;
  std::snprintf(r.message, sizeof(r.message), "%s", msg);
  return r;
}

}  // namespace

WorkspaceCanvasRelationResult BuildWorkspaceCanvasRelation(
    const WorkspaceCanvasRelationInput& in) {
  if (in.canvas_pixel_width <= 0 || in.canvas_pixel_height <= 0 ||
      in.canvas_pixel_width > kMaxCanvasPixel || in.canvas_pixel_height > kMaxCanvasPixel) {
    return Fail(FailStatus::InvalidCanvasPixelSize, "invalid canvas pixel size");
  }
  if (!in.workspace_roi_screen.valid()) {
    return Fail(FailStatus::WorkspaceDetectionFailed, "invalid workspace roi");
  }

  WorkspaceCanvasRelation rel;
  rel.workspace_roi = in.workspace_roi_screen;
  rel.canvas_pixel_width = in.canvas_pixel_width;
  rel.canvas_pixel_height = in.canvas_pixel_height;
  rel.workspace_width = in.workspace_roi_screen.width();
  rel.workspace_height = in.workspace_roi_screen.height();
  rel.canvas_aspect_ratio =
      static_cast<float>(in.canvas_pixel_width) / static_cast<float>(in.canvas_pixel_height);
  rel.canvas_axis_x_workspace_local = {1, 0};
  rel.canvas_axis_y_workspace_local = {0, 1};
  std::snprintf(rel.source_revision, sizeof(rel.source_revision), "%s", "sct-embedded-wb");
  if (in.capture_id) {
    std::snprintf(rel.source_capture_id, sizeof(rel.source_capture_id), "%s", in.capture_id);
  }

  const auto& obs = in.workspace_canvas;
  if (!obs.bounds_screen.valid() && !obs.bounds_capture.valid()) {
    rel.ambiguous = true;
    std::snprintf(rel.ambiguity_reason, sizeof(rel.ambiguity_reason),
                  "no visible canvas bounds");
    rel.confidence = 0.1f;
    WorkspaceCanvasRelationResult r;
    r.status = FailStatus::Ok;
    r.relation = rel;
    return r;
  }

  wb::IntRect visible_screen = obs.bounds_screen.valid() ? obs.bounds_screen : obs.bounds_capture;
  rel.visible_canvas_bounds_screen = visible_screen;

  const int wl = in.workspace_roi_screen.left;
  const int wt = in.workspace_roi_screen.top;
  rel.visible_canvas_bounds_workspace_local = {
      visible_screen.left - wl, visible_screen.top - wt, visible_screen.right - wl,
      visible_screen.bottom - wt};

  const int vw = rel.visible_canvas_bounds_workspace_local.width();
  const int vh = rel.visible_canvas_bounds_workspace_local.height();
  if (vw < 1 || vh < 1) {
    return Fail(FailStatus::WorkspaceCanvasAmbiguous, "visible canvas degenerate");
  }

  rel.visible_canvas_edge_evidence = obs.visible_edges_mask;
  rel.canvas_edges_in_workspace = obs.visible_edges_mask;

  constexpr int margin = 4;
  const int vl = rel.visible_canvas_bounds_workspace_local.left;
  const int vt = rel.visible_canvas_bounds_workspace_local.top;
  const int vr = rel.visible_canvas_bounds_workspace_local.right;
  const int vb = rel.visible_canvas_bounds_workspace_local.bottom;

  if (vl <= margin) rel.canvas_crop_sides |= kEdgeL;
  if (vt <= margin) rel.canvas_crop_sides |= kEdgeT;
  if (vr >= rel.workspace_width - margin) rel.canvas_crop_sides |= kEdgeR;
  if (vb >= rel.workspace_height - margin) rel.canvas_crop_sides |= kEdgeB;

  rel.occluded_canvas_edges = rel.canvas_crop_sides;

  const float vis_aspect = static_cast<float>(vw) / static_cast<float>(vh);
  const float canvas_aspect = rel.canvas_aspect_ratio;

  double full_w = vw;
  double full_h = vh;
  if (canvas_aspect > vis_aspect * 1.05) {
    full_w = vh * canvas_aspect;
  } else if (canvas_aspect < vis_aspect * 0.95) {
    full_h = vw / canvas_aspect;
  }

  double full_l = vl;
  double full_t = vt;
  if (rel.canvas_crop_sides & kEdgeL) {
    full_l = 0;
    full_w = std::max(full_w, static_cast<double>(vr));
  } else if (rel.canvas_crop_sides & kEdgeR) {
    full_w = rel.workspace_width - full_l;
  }
  if (rel.canvas_crop_sides & kEdgeT) {
    full_t = 0;
    full_h = std::max(full_h, static_cast<double>(vb));
  } else if (rel.canvas_crop_sides & kEdgeB) {
    full_h = rel.workspace_height - full_t;
  }

  rel.full_canvas_model_workspace_local = {
      static_cast<int>(std::floor(full_l)), static_cast<int>(std::floor(full_t)),
      static_cast<int>(std::ceil(full_l + full_w)), static_cast<int>(std::ceil(full_t + full_h))};

  rel.canvas_to_workspace_scale_x =
      static_cast<float>(full_w) / static_cast<float>(in.canvas_pixel_width);
  rel.canvas_to_workspace_scale_y =
      static_cast<float>(full_h) / static_cast<float>(in.canvas_pixel_height);
  // These fractions are the bridge between the workspace view and the navigator:
  // a navigator canvas edge covered by the red frame represents a workspace edge
  // whose complete extent is recovered by dividing by the visible canvas share.
  rel.visible_canvas_workspace_fraction_x =
      static_cast<float>(vw) / static_cast<float>(rel.workspace_width);
  rel.visible_canvas_workspace_fraction_y =
      static_cast<float>(vh) / static_cast<float>(rel.workspace_height);
  rel.visible_canvas_fraction_x =
      static_cast<float>(vw) / static_cast<float>(full_w);
  rel.visible_canvas_fraction_y =
      static_cast<float>(vh) / static_cast<float>(full_h);
  rel.full_canvas_edge_evidence = obs.four_sides_complete ? 0xF : obs.visible_edges_mask;
  rel.confidence = std::clamp(obs.confidence, 0.f, 1.f);
  rel.ambiguous = obs.ambiguous;

  if (obs.ambiguous && obs.ambiguity_reason[0]) {
    std::snprintf(rel.ambiguity_reason, sizeof(rel.ambiguity_reason), "%s",
                  obs.ambiguity_reason);
  }

  WorkspaceCanvasRelationResult r;
  r.status = FailStatus::Ok;
  r.relation = rel;
  std::snprintf(r.message, sizeof(r.message), "ok crop=0x%x", rel.canvas_crop_sides);
  return r;
}

}  // namespace sct
