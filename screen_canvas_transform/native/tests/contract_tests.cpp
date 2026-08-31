#include "sct/transform_solve.hpp"
#include "sct/types.hpp"
#include "sct/viewport_frame.hpp"
#include "sct/workspace_canvas_relation.hpp"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

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

void PutBgra(std::vector<uint8_t>& buf, int stride, int x, int y, uint8_t b, uint8_t g, uint8_t r) {
  uint8_t* p = buf.data() + static_cast<size_t>(y) * stride + static_cast<size_t>(x) * 4;
  p[0] = b;
  p[1] = g;
  p[2] = r;
  p[3] = 255;
}

void DrawRedRect1px(std::vector<uint8_t>& buf, int stride, int l, int t, int r, int b, uint8_t rb,
                    uint8_t rg, uint8_t rr) {
  for (int x = l; x <= r; ++x) {
    PutBgra(buf, stride, x, t, rb, rg, rr);
    PutBgra(buf, stride, x, b, rb, rg, rr);
  }
  for (int y = t; y <= b; ++y) {
    PutBgra(buf, stride, l, y, rb, rg, rr);
    PutBgra(buf, stride, r, y, rb, rg, rr);
  }
}

sct::ViewportCompletionInput MakeViewportInput(std::vector<uint8_t>& buf, int w, int h, int stride,
                                               wb::IntRect thumb) {
  sct::ViewportCompletionInput in;
  in.bgra = buf.data();
  in.width = w;
  in.height = h;
  in.stride = stride;
  in.thumbnail_roi = thumb;
  in.navigator_canvas_bounds = thumb;
  in.workspace_canvas_relation.canvas_aspect_ratio = 4.0 / 3.0;
  in.workspace_canvas_relation.workspace_roi = {0, 0, 800, 600};
  in.workspace_canvas_relation.visible_canvas_workspace_fraction_x = 0.5f;
  in.workspace_canvas_relation.visible_canvas_workspace_fraction_y = 0.5f;
  // 理论红框尺寸：W_nav/H_nav = navigator_canvas_* × visible_canvas_fraction_*
  in.workspace_canvas_relation.visible_canvas_fraction_x = 0.5f;
  in.workspace_canvas_relation.visible_canvas_fraction_y = 0.5f;
  in.dpi_scale = 1.f;
  return in;
}

void FillRect(std::vector<uint8_t>& buf, int stride, int l, int t, int r, int b, uint8_t bb,
              uint8_t bg, uint8_t br) {
  for (int y = t; y < b; ++y)
    for (int x = l; x < r; ++x) PutBgra(buf, stride, x, y, bb, bg, br);
}

void TestViewportRedFourEdgesGeometryStable() {
  constexpr int W = 120;
  constexpr int H = 100;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  // White fill already; draw thin pure-red rectangle inside thumbnail.
  constexpr int L = 20, T = 15, R = 80, B = 70;
  DrawRedRect1px(buf, stride, L, T, R, B, 0, 0, 220);
  wb::IntRect thumb{10, 10, 110, 90};
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "4-edge thin red ok");
  Expect(out.frame.red_evidence.confirmed_complete_edge_count == 4, "4 complete edges");
  Expect(out.frame.completion_strategy ==
             static_cast<int>(sct::ViewportCompletionPattern::FourCompleteEdges),
         "4.0 pattern");
  // Geometry must track raw centerline (~pixel centers), not dilate outward.
  Expect(std::abs(out.frame.origin_top_left_displayed.x - (L + 0.5)) < 1.0,
         "origin x within 1px of thin stroke");
  Expect(std::abs(out.frame.origin_top_left_displayed.y - (T + 0.5)) < 1.0,
         "origin y within 1px of thin stroke");
  Expect(std::abs(out.frame.axis_x_displayed.x - (R - L)) < 1.5, "axis x length within 1.5px");
  Expect(std::abs(out.frame.axis_y_displayed.y - (B - T)) < 1.5, "axis y length within 1.5px");
}

void TestViewportRedSoftAaAndGapRecall() {
  constexpr int W = 100;
  constexpr int H = 80;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  constexpr int L = 25, T = 20, R = 75, B = 55;
  // Soft AA-like pink/orange red that old strict gate often missed.
  DrawRedRect1px(buf, stride, L, T, R, B, 40, 50, 180);
  // Break top edge with a 1px gap — dilate should reconnect for detection.
  PutBgra(buf, stride, (L + R) / 2, T, 255, 255, 255);
  wb::IntRect thumb{5, 5, 95, 75};
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "soft AA + gap red ok");
  Expect(out.frame.red_evidence.confirmed_complete_edge_count >= 3, "at least 3 edges after dilate");
  Expect(out.frame.completion_strategy ==
             static_cast<int>(sct::ViewportCompletionPattern::FourCompleteEdges) ||
             out.frame.completion_strategy ==
                 static_cast<int>(sct::ViewportCompletionPattern::ThreeCompleteEdges),
         "soft AA classifies as 3.0/4.0");
  Expect(std::abs(out.frame.origin_top_left_displayed.x - (L + 0.5)) < 1.25,
         "soft-red origin x still near raw stroke");
  Expect(std::abs(out.frame.origin_top_left_displayed.y - (T + 0.5)) < 1.25,
         "soft-red origin y still near raw stroke");
}

void DrawRedVLine(std::vector<uint8_t>& buf, int stride, int x, int y0, int y1, uint8_t rb,
                  uint8_t rg, uint8_t rr) {
  for (int y = y0; y <= y1; ++y) PutBgra(buf, stride, x, y, rb, rg, rr);
}

void DrawRedHLine(std::vector<uint8_t>& buf, int stride, int y, int x0, int x1, uint8_t rb,
                  uint8_t rg, uint8_t rr) {
  for (int x = x0; x <= x1; ++x) PutBgra(buf, stride, x, y, rb, rg, rr);
}

void TestViewportPattern01ParallelNoComplete() {
  constexpr int W = 120;
  constexpr int H = 100;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  // Single vertical red stroke — no right-angle stubs → 0 complete edges.
  DrawRedVLine(buf, stride, 40, 25, 70, 0, 0, 220);
  wb::IntRect thumb{10, 10, 110, 90};
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "0.1 parallel segments ok");
  Expect(out.frame.red_evidence.confirmed_complete_edge_count == 0, "0.1 has no complete edges");
  Expect(out.frame.completion_strategy ==
             static_cast<int>(sct::ViewportCompletionPattern::ParallelSegmentsNoCompleteEdge),
         "0.1 pattern code");
  Expect(out.frame.red_evidence.segment_count >= 1, "0.1 observed at least one segment");
  Expect(out.frame.axis_x_displayed.x > 4 && out.frame.axis_y_displayed.y > 4,
         "0.1 recovered positive axes via WCR");
}

void TestViewportPattern02IntersectingNoComplete() {
  constexpr int W = 120;
  constexpr int H = 100;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  // Pure L without far-end stubs: intersecting segments, each missing one corner → 0.2.
  constexpr int X = 35, Y = 30;
  DrawRedVLine(buf, stride, X, Y, Y + 40, 0, 0, 220);
  DrawRedHLine(buf, stride, Y, X, X + 45, 0, 0, 220);
  wb::IntRect thumb{10, 10, 110, 90};
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "0.2 intersecting segments ok");
  Expect(out.frame.red_evidence.confirmed_complete_edge_count == 0, "0.2 has no complete edges");
  Expect(out.frame.completion_strategy ==
             static_cast<int>(sct::ViewportCompletionPattern::IntersectingSegmentsNoCompleteEdge),
         "0.2 pattern code");
  Expect(out.frame.red_evidence.segment_count >= 2, "0.2 observed orthogonal segments");
  Expect(std::abs(out.frame.origin_top_left_displayed.x - (X + 0.5)) < 2.0,
         "0.2 origin near L corner x");
  Expect(std::abs(out.frame.origin_top_left_displayed.y - (Y + 0.5)) < 2.0,
         "0.2 origin near L corner y");
}

void TestViewportNoRedPixelsIsEdgeFailureNotFrameFound() {
  constexpr int W = 80;
  constexpr int H = 60;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  wb::IntRect thumb{5, 5, 75, 55};
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::InsufficientViewportGeometry,
         "no red edge evidence → InsufficientViewportGeometry");
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

// ---- §10 红框成组契约测试 ----

void TestInterferenceOrthogonalRedDoesNotFakeComplete() {
  // U 形三边（仅顶边两端有组内直角）+ 底端旁无关正交红 stub（够短不成段）：
  // 旧掩膜探针会把左边抬成完整边；组内直角不得抬升。
  constexpr int W = 140;
  constexpr int H = 110;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  constexpr int L = 30, T = 25, R = 100, B = 75;
  DrawRedVLine(buf, stride, L, T, B, 0, 0, 220);
  DrawRedVLine(buf, stride, R, T, B, 0, 0, 220);
  DrawRedHLine(buf, stride, T, L, R, 0, 0, 220);
  // 水平 stub 跨度 < kMinSegmentSpan，不会成为观测红段，但足以骗过旧 Probe* stub
  DrawRedHLine(buf, stride, B, L, L + 4, 0, 0, 220);
  wb::IntRect thumb{5, 5, 135, 105};
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "interference U-shape ok");
  Expect(out.frame.red_evidence.confirmed_complete_edge_count == 1,
         "only top edge complete via group right-angles; stub must not fake L/R complete");
  Expect(out.frame.completion_strategy ==
             static_cast<int>(sct::ViewportCompletionPattern::OneCompleteEdge),
         "interference keeps 1.0 not inflated complete count");
}

void TestTwoSeparableRectanglesFormTwoGroupsDisambiguateBySize() {
  // 两套可分离平行/垂直结构 → 成两组；无背景粘着时靠理论尺寸唯一匹配
  constexpr int W = 200;
  constexpr int H = 160;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 200);  // 画布灰
  wb::IntRect thumb{5, 5, 195, 155};
  wb::IntRect canvas{10, 10, 190, 150};
  // 组 A：接近理论尺寸（nav≈180×140，fraction=0.5 → W_nav=90 H_nav=70）
  DrawRedRect1px(buf, stride, 20, 25, 110, 95, 0, 0, 220);  // 90×70
  // 组 B：明显偏小，不应匹配理论尺寸
  DrawRedRect1px(buf, stride, 140, 30, 170, 55, 0, 0, 220);  // 30×25
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.visible_canvas_fraction_x = 0.5f;
  in.workspace_canvas_relation.visible_canvas_fraction_y = 0.5f;
  in.workspace_canvas_relation.canvas_aspect_ratio = 90.0 / 70.0;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "two groups: size-unique ok");
  Expect(std::abs(out.frame.width - 90.0) < 4.0, "two groups: selected ~90 wide");
  Expect(std::abs(out.frame.height - 70.0) < 4.0, "two groups: selected ~70 tall");
  Expect(out.frame.red_evidence.confirmed_complete_edge_count == 4,
         "two groups: target has 4 complete");
}

void TestBackgroundUniqueSelectsWithoutSizeBranch() {
  // 两组中仅一组外侧粘导航器背景 → 不进入尺寸分支即选中
  constexpr int W = 200;
  constexpr int H = 160;
  constexpr int stride = W * 4;
  // 缩略图外圈 = 导航器背景（深灰），画布内 = 浅色
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  wb::IntRect thumb{5, 5, 195, 155};
  wb::IntRect canvas{20, 20, 180, 140};
  FillRect(buf, stride, canvas.left, canvas.top, canvas.right, canvas.bottom, 210, 210, 210);
  // 贴顶边的框（外侧探到背景）
  DrawRedRect1px(buf, stride, 40, 20, 100, 70, 0, 0, 220);
  // 画布中部另一框（不粘背景），尺寸也接近以免误判依赖
  DrawRedRect1px(buf, stride, 110, 80, 170, 130, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  // 故意给错误理论尺寸：若误入尺寸分支会失败或选错
  in.workspace_canvas_relation.visible_canvas_fraction_x = 0.15f;
  in.workspace_canvas_relation.visible_canvas_fraction_y = 0.15f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "bg-unique selects ok");
  Expect(std::abs(out.frame.origin_top_left_displayed.y - (20 + 0.5)) < 2.5,
         "bg-unique picked top-touching rect");
  Expect(std::abs(out.frame.origin_top_left_displayed.x - (40 + 0.5)) < 2.5,
         "bg-unique picked left of top rect");
}

void TestMultipleGroupsTouchBackgroundMustFail() {
  constexpr int W = 200;
  constexpr int H = 160;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  wb::IntRect thumb{5, 5, 195, 155};
  wb::IntRect canvas{20, 20, 180, 140};
  FillRect(buf, stride, canvas.left, canvas.top, canvas.right, canvas.bottom, 210, 210, 210);
  // 两框分别贴左/右画布边 → 两组都粘背景
  DrawRedRect1px(buf, stride, 20, 40, 70, 100, 0, 0, 220);
  DrawRedRect1px(buf, stride, 130, 40, 180, 100, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::AmbiguousViewportGeometry,
         "|B|>=2 must fail AmbiguousViewportGeometry");
}

void TestNoBackgroundTheoryAmbiguousMustFail() {
  // |B|==0 且两组尺寸都不匹配 / 或都匹配 → 失败，不得取较近者
  constexpr int W = 200;
  constexpr int H = 160;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 200);
  wb::IntRect thumb{5, 5, 195, 155};
  wb::IntRect canvas{10, 10, 190, 150};
  DrawRedRect1px(buf, stride, 30, 30, 70, 60, 0, 0, 220);    // 40×30
  DrawRedRect1px(buf, stride, 100, 70, 160, 120, 0, 0, 220);  // 60×50
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  // 理论尺寸约 90×70，两组都不匹配
  in.workspace_canvas_relation.visible_canvas_fraction_x = 0.5f;
  in.workspace_canvas_relation.visible_canvas_fraction_y = 0.5f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::AmbiguousViewportGeometry,
         "|M|==0 must fail, no nearest-pick");
}

void TestNoBackgroundTheoryTieMustFail() {
  constexpr int W = 220;
  constexpr int H = 180;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 200);
  wb::IntRect thumb{5, 5, 215, 175};
  wb::IntRect canvas{10, 10, 210, 170};
  // 两框同为 80×60，理论也是 80×60 → |M|>=2 失败
  DrawRedRect1px(buf, stride, 20, 20, 100, 80, 0, 0, 220);
  DrawRedRect1px(buf, stride, 120, 90, 200, 150, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.visible_canvas_fraction_x =
      static_cast<float>(80.0 / 200.0);
  in.workspace_canvas_relation.visible_canvas_fraction_y =
      static_cast<float>(60.0 / 160.0);
  in.workspace_canvas_relation.canvas_aspect_ratio = 80.0 / 60.0;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::AmbiguousViewportGeometry,
         "|M|>=2 must fail, no smaller-error pick");
}

}  // namespace

int main() {
  TestWorkspaceCanvasRelationBuild();
  TestViewportRedFourEdgesGeometryStable();
  TestViewportRedSoftAaAndGapRecall();
  TestViewportPattern01ParallelNoComplete();
  TestViewportPattern02IntersectingNoComplete();
  TestViewportNoRedPixelsIsEdgeFailureNotFrameFound();
  TestScalePercentDoesNotChangeMatrix();
  TestInterferenceOrthogonalRedDoesNotFakeComplete();
  TestTwoSeparableRectanglesFormTwoGroupsDisambiguateBySize();
  TestBackgroundUniqueSelectsWithoutSizeBranch();
  TestMultipleGroupsTouchBackgroundMustFail();
  TestNoBackgroundTheoryAmbiguousMustFail();
  TestNoBackgroundTheoryTieMustFail();
  if (g_failures == 0) {
    std::printf("OK: all contract tests passed\n");
    return 0;
  }
  std::printf("FAILED: %d test(s)\n", g_failures);
  return 1;
}
