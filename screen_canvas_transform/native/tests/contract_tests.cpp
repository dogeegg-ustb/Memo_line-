#include "sct/transform_solve.hpp"
#include "sct/types.hpp"
#include "sct/workspace_canvas_relation.hpp"

#include <cmath>
#include <cstdio>
#include <cstring>

namespace {

int g_failures = 0;

void Expect(bool cond, const char* msg) {
  if (!cond) {
    std::printf("FAIL: %s\n", msg);
    ++g_failures;
  }
}

void TestWorkspaceCanvasRelationBuild() {
  sct::WorkspaceCanvasRelationInput in;
  in.canvas_pixel_width = 2000;
  in.canvas_pixel_height = 1000;
  in.workspace_roi_screen = {0, 0, 800, 600};
  in.workspace_canvas.bounds_screen = {50, 40, 750, 560};
  in.workspace_canvas.confidence = 0.8f;
  in.workspace_canvas.visible_edges_mask = 0xF;
  auto r = sct::BuildWorkspaceCanvasRelation(in);
  Expect(r.status == sct::FailStatus::Ok, "relation build ok");
  Expect(r.relation.canvas_aspect_ratio > 1.9f && r.relation.canvas_aspect_ratio < 2.1f,
         "canvas aspect from pixel size");
  Expect(r.relation.visible_canvas_bounds_workspace_local.width() > 0, "visible bounds");
  Expect(r.relation.full_canvas_model_workspace_local.width() >=
             r.relation.visible_canvas_bounds_workspace_local.width(),
         "full model not smaller than visible");
}

void TestScalePercentDoesNotChangeMatrix() {
  sct::SolveInput base;
  std::snprintf(base.capture_id, sizeof(base.capture_id), "test");
  base.generation = 1;
  base.canvas_pixel_width = 1000;
  base.canvas_pixel_height = 1000;
  base.workspace_roi_screen = {0, 0, 500, 400};
  base.navigator_roi_screen = {600, 0, 900, 300};
  base.navigator_thumbnail_roi_screen = {610, 40, 890, 260};
  base.workspace_canvas.four_sides_complete = 0;
  base.workspace_canvas.ambiguous = 1;
  base.navigator_canvas.bounds_screen = {620, 50, 880, 250};
  base.navigator_canvas.confidence = 0.9f;
  base.viewport.origin_top_left_displayed = {650, 80};
  base.viewport.axis_x_displayed = {200, 0};
  base.viewport.axis_y_displayed = {0, 150};
  base.viewport.width = 200;
  base.viewport.height = 150;
  base.viewport.confidence = 0.8f;
  base.numbers.scale_percent = 100.f;
  base.numbers.scale_confidence = 1.f;
  base.numbers.rotation_confidence = 0.f;
  base.injected_scale_percent = 100.f;

  auto r100 = sct::SolveTransform(base);
  Expect(r100.status == sct::FailStatus::Ok, "solve 100% ok");

  base.injected_scale_percent = 200.f;
  base.numbers.scale_percent = 200.f;
  auto r200 = sct::SolveTransform(base);
  Expect(r200.status == sct::FailStatus::Ok, "solve 200% ok");

  const auto& m100 = r100.snapshot.screen_to_canvas;
  const auto& m200 = r200.snapshot.screen_to_canvas;
  for (int i = 0; i < 6; ++i) {
    Expect(std::abs(m100.m[i] - m200.m[i]) < 1e-9, "ScreenToCanvas invariant to ScalePercent");
  }

  Expect(r200.snapshot.marker.target_arm_display_px >
             r100.snapshot.marker.target_arm_display_px,
         "marker arm grows with scale");
}

}  // namespace

int main() {
  TestWorkspaceCanvasRelationBuild();
  TestScalePercentDoesNotChangeMatrix();
  if (g_failures == 0) {
    std::printf("OK: all contract tests passed\n");
    return 0;
  }
  std::printf("FAILED: %d test(s)\n", g_failures);
  return 1;
}
