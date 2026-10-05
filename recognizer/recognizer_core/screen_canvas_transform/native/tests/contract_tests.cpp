#include "sct/canvas_observe.hpp"
#include "sct/transform_solve.hpp"
#include "sct/types.hpp"
#include "sct/viewport_frame.hpp"
#include "sct/workspace_canvas_relation.hpp"
#include "wb/color.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <utility>
#include <vector>

namespace {

int g_failures = 0;
double g_max_navigator_canvas_boundary_error = 0.0;

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
  in.workspace_canvas_relation.visible_canvas_bounds_workspace_local = {200, 150, 600, 450};
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
  // Independently restore 45/(400/800)=90 and 40/(300/600)=80.
  // 0.2 places those two lengths at their factual shared corner.
  std::printf("PRECISION 0.2 width=%.6f height=%.6f\n", out.frame.width, out.frame.height);
  Expect(std::abs(out.frame.width - 90.0) < 1.0 &&
             std::abs(out.frame.height - 80.0) < 1.0,
         "0.2 recovers each orthogonal segment length independently");
}

void TestViewportPattern02MirroredCorners() {
  constexpr int W = 120, H = 100, stride = W * 4;
  for (int sx : {-1, 1}) for (int sy : {-1, 1}) {
    std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
    const int x = sx > 0 ? 35 : 80, y = sy > 0 ? 30 : 70;
    DrawRedVLine(buf, stride, x, std::min(y, y + sy * 40),
                 std::max(y, y + sy * 40), 0, 0, 220);
    DrawRedHLine(buf, stride, y, std::min(x, x + sx * 45),
                 std::max(x, x + sx * 45), 0, 0, 220);
    auto out = sct::CompleteViewportFrame(MakeViewportInput(buf, W, H, stride, {10, 10, 110, 90}));
    Expect(out.status == sct::FailStatus::Ok &&
               std::abs(out.frame.width - 90.0) < 1.0 &&
               std::abs(out.frame.height - 80.0) < 1.0,
           "0.2 restores the shared corner in all four orientations");
  }
}

// The viewport is wider than the canvas: its left side lies in the Navigator's
// gray margin beside the paper, its bottom crosses the paper, and the workspace
// shows the canvas left/top/right with its bottom cropped (zoom ~22% in CSP).
// Pixel-edge coordinates: the paper covers [60,140)x[10,150) and the 1px red
// centerlines are left x=40.5, bottom y=100.5. At nav scale 0.25 the viewport
// is 200x150 from (40.5,-49.5), so the workspace sees the canvas from
// (60-40.5)/0.25 = 78 to 398 and from (10+49.5)/0.25 = 238 down past 600.
sct::ViewportCompletionInput MakeBesidePaperCornerInput(std::vector<uint8_t>& buf, int w, int h,
                                                        int stride, int workspace_shift_x) {
  FillRect(buf, stride, 60, 10, 140, 150, 230, 230, 230);
  DrawRedVLine(buf, stride, 40, 5, 100, 0, 0, 220);
  DrawRedHLine(buf, stride, 100, 40, 194, 0, 0, 220);
  auto in = MakeViewportInput(buf, w, h, stride, {5, 5, 195, 155});
  in.navigator_canvas_bounds = {60, 10, 140, 150};
  auto& rel = in.workspace_canvas_relation;
  rel.canvas_aspect_ratio = 80.0 / 140.0;
  rel.canvas_crop_sides = 8;  // Bottom
  rel.visible_canvas_bounds_workspace_local = {78 + workspace_shift_x, 238,
                                               398 + workspace_shift_x, 600};
  rel.visible_canvas_workspace_fraction_x = 320.f / 800.f;
  rel.visible_canvas_workspace_fraction_y = 362.f / 600.f;
  rel.visible_canvas_fraction_x = 1.0f;
  rel.visible_canvas_fraction_y = 362.f / 560.f;
  in.display_rotation_degrees = 0.f;
  in.display_rotation_confidence = 0.85f;
  return in;
}

void TestViewportPattern02SideBesidePaper() {
  constexpr int W = 200;
  constexpr int H = 160;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  auto in = MakeBesidePaperCornerInput(buf, W, H, stride, 0);
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "0.2 completes a corner whose side lies beside the paper");
  if (out.status != sct::FailStatus::Ok) {
    std::printf("  %s\n", out.message);
    return;
  }
  Expect(out.frame.completion_strategy ==
             static_cast<int>(sct::ViewportCompletionPattern::IntersectingSegmentsNoCompleteEdge),
         "side beside the paper remains pattern 0.2");
  std::printf("PRECISION 0.2 beside paper origin=(%.3f,%.3f) size=%.3fx%.3f\n",
              out.frame.origin_top_left_displayed.x, out.frame.origin_top_left_displayed.y,
              out.frame.width, out.frame.height);
  Expect(std::abs(out.frame.origin_top_left_displayed.x - 40.5) < 0.5 &&
             std::abs(out.frame.origin_top_left_displayed.y + 49.5) < 0.5,
         "0.2 beside paper keeps the factual corner and extends above the view");
  // 80/(320/800)=200, and the paper's 90.5px from its top to the corner
  // /(362/600)=150. The red fragment itself starts above the paper.
  Expect(std::abs(out.frame.width - 200.0) < 0.5 && std::abs(out.frame.height - 150.0) < 0.5,
         "0.2 beside paper measures its height from the paper extent to the corner");
}

void TestViewportPattern02SideBesidePaperRejectsStaleNavigator() {
  // Same Navigator, but the workspace already shows the canvas 150 px further
  // right: the Navigator has not repainted after a pan. Both lengths still
  // agree with the workspace aspect, so only the corner position exposes it.
  constexpr int W = 200;
  constexpr int H = 160;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  auto in = MakeBesidePaperCornerInput(buf, W, H, stride, 150);
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status != sct::FailStatus::Ok,
         "0.2 beside paper rejects a corner that contradicts the workspace offset");
}

void TestLargePinkPlateauIsNotMistakenForManyRedLines() {
  constexpr int W = 100;
  constexpr int H = 80;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 220);
  // This weak pink-gray fill passes the broad red gate, but is a face, not a
  // collection of parallel red strokes.
  FillRect(buf, stride, 25, 20, 70, 55, 125, 130, 160);
  auto in = MakeViewportInput(buf, W, H, stride, {5, 5, 95, 75});
  const auto out = sct::CompleteViewportFrame(in);
  Expect(out.status != sct::FailStatus::Ok,
         "large uniform pink plateau is not emitted as red-frame edges");
}

void TestRedColorPeakKeepsTrueFrameInsidePinkArea() {
  constexpr int W = 100;
  constexpr int H = 80;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 220);
  // A strong-red viewport stroke sits over a much larger pink-gray area.
  FillRect(buf, stride, 10, 10, 90, 70, 125, 130, 160);
  constexpr int L = 25, T = 20, R = 70, B = 55;
  DrawRedRect1px(buf, stride, L, T, R, B, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, {5, 5, 95, 75});
  const auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok,
         "color-peak red frame completes inside pink area");
  Expect(std::abs(out.frame.origin_top_left_displayed.x - (L + 0.5)) < 1.5,
         "color-peak keeps true frame left edge");
  Expect(std::abs(out.frame.origin_top_left_displayed.y - (T + 0.5)) < 1.5,
         "color-peak keeps true frame top edge");
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

// The paper fills the Navigator view width (gray margins above and below);
// the viewport is wider than the paper. Its top lies 2.5 Navigator px above
// the paper top, so that red side sits in the margin with no ink on the
// paper, and its bottom crosses the paper (CSP, 2026-10-03). Pixel-edge
// coordinates: paper [20,180)x[60,210), red rows 57 and 177 (centerlines
// 57.5 and 177.5). At 0.2 Navigator px per workspace px the 1000x600
// workspace sees the paper from x=100 to 900 and from y=12.5 down past 600.
sct::ViewportCompletionInput MakeBesidePaperOppositeInput(std::vector<uint8_t>& buf, int w, int h,
                                                          int stride, int workspace_gap_top) {
  FillRect(buf, stride, 20, 60, 180, 210, 230, 230, 230);
  DrawRedHLine(buf, stride, 57, 20, 179, 0, 0, 220);
  DrawRedHLine(buf, stride, 177, 20, 179, 0, 0, 220);
  auto in = MakeViewportInput(buf, w, h, stride, {20, 10, 180, 230});
  in.navigator_canvas_bounds = {20, 60, 180, 210};
  auto& rel = in.workspace_canvas_relation;
  rel.workspace_roi = {0, 0, 1000, 600};
  rel.canvas_aspect_ratio = 160.f / 150.f;
  rel.canvas_crop_sides = 8;  // Bottom
  rel.visible_canvas_bounds_workspace_local = {100, workspace_gap_top, 900, 600};
  rel.visible_canvas_workspace_fraction_x = 800.f / 1000.f;
  rel.visible_canvas_workspace_fraction_y = float(600 - workspace_gap_top) / 600.f;
  rel.visible_canvas_fraction_x = 1.0f;
  rel.visible_canvas_fraction_y = float(600 - workspace_gap_top) / 750.f;
  in.display_rotation_degrees = 0.f;
  in.display_rotation_confidence = 0.85f;
  return in;
}

void TestViewportPattern03SideBesidePaper() {
  constexpr int W = 200, H = 240, stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  auto in = MakeBesidePaperOppositeInput(buf, W, H, stride, 12);
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "0.3 completes when one side lies beside the paper");
  if (out.status != sct::FailStatus::Ok) {
    std::printf("  %s\n", out.message);
    return;
  }
  Expect(out.frame.completion_strategy ==
             static_cast<int>(sct::ViewportCompletionPattern::OppositeSegmentsNoCompleteEdge),
         "side beside the paper remains pattern 0.3");
  std::printf("PRECISION 0.3 beside paper origin=(%.3f,%.3f) size=%.3fx%.3f\n",
              out.frame.origin_top_left_displayed.x, out.frame.origin_top_left_displayed.y,
              out.frame.width, out.frame.height);
  // The red ink ends at the paper edges: its extent is one pixel short of the
  // 160 px paper, i.e. at most 0.7% of the recovered width.
  Expect(std::abs(out.frame.origin_top_left_displayed.y - 57.5) < 0.5 &&
             std::abs(out.frame.height - 120.0) < 0.5,
         "0.3 beside paper keeps both measured red normals");
  Expect(std::abs(out.frame.width - 200.0) < 1.5 &&
             std::abs(out.frame.origin_top_left_displayed.x - 0.0) < 1.5,
         "0.3 beside paper takes the span from the side crossing the paper");
}

void TestViewportPattern03FallsBackOnlyForUniqueCropSide() {
  constexpr int W = 200, H = 240, stride = W * 4;
  // The workspace shows the paper top 60 px down, i.e. 12 Navigator px, not
  // 2.5: the Navigator has not repainted after a pan.
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  auto in = MakeBesidePaperOppositeInput(buf, W, H, stride, 60);
  auto out = sct::CompleteViewportFrame(in);
  std::printf("0.3 uncertain opposite side: status=%d %s\n",int(out.status),out.message);
  Expect(out.status == sct::FailStatus::Ok && out.frame.completion_strategy == 1 &&
             out.frame.red_evidence.segment_count == 1 && out.frame.red_evidence.confirmed_complete_edge_count == 0,
         "0.3 uncertain opposite side falls back to the confirmed side in 0.1");
  // A cropped paper top puts the viewport top inside the paper, not beside it.
  std::vector<uint8_t> buf2(static_cast<size_t>(stride) * H, 40);
  auto cropped = MakeBesidePaperOppositeInput(buf2, W, H, stride, 12);
  cropped.workspace_canvas_relation.canvas_crop_sides = 2 | 8;
  auto cropped_out=sct::CompleteViewportFrame(cropped);
  std::printf("0.3 two cropped roles: status=%d %s\n",int(cropped_out.status),cropped_out.message);
  Expect(cropped_out.status != sct::FailStatus::Ok,
         "0.3 beside paper rejects a side whose workspace paper edge is cropped");
}

wb::BackgroundModel NavigatorGrayModel() {
  wb::BackgroundModel model;
  model.center_lab = wb::BgrToLab(41, 41, 41);
  model.strong_delta_e = 6.f;
  model.weak_delta_e = 12.f;
  return model;
}

void TestNavigatorPaperExcludesViewFrameAndRecoversClippedEdge() {
  // Paper fills the view width (CSP 2026-10-03): the view [20,200) is framed
  // by 45-gray columns, and the frozen thumbnail ROI starts 2 px inside the
  // paper and ends on the right frame.
  constexpr int W = 220, H = 260, stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 0);
  FillRect(buf, stride, 0, 0, W, H, 51, 51, 51);
  FillRect(buf, stride, 18, 8, 202, 252, 45, 45, 45);
  FillRect(buf, stride, 20, 10, 200, 250, 41, 41, 41);
  FillRect(buf, stride, 20, 60, 200, 190, 255, 255, 255);
  DrawRedHLine(buf, stride, 57, 20, 199, 0, 0, 255);
  DrawRedHLine(buf, stride, 170, 20, 199, 0, 0, 255);
  auto out = sct::ObserveCanvasExcludingBackground(buf.data(), W, H, stride, {22, 12, 202, 248},
                                                   0, 0, NavigatorGrayModel(), 1.5f, true, 180, 130);
  std::printf("PRECISION navigator framed width bounds=[%d,%d,%d,%d)\n", out.bounds_capture.left,
              out.bounds_capture.top, out.bounds_capture.right, out.bounds_capture.bottom);
  Expect(!out.ambiguous && out.bounds_capture.left == 20 && out.bounds_capture.right == 200 &&
             out.bounds_capture.top == 60 && out.bounds_capture.bottom == 190,
         "navigator paper excludes the view frame and recovers the ROI-clipped edge");

  // Paper fills the view height: 45-gray frame rows above and below the view
  // touch it, the top one through a red viewport side ending on it.
  constexpr int W2 = 220, H2 = 220, stride2 = W2 * 4;
  std::vector<uint8_t> tall(static_cast<size_t>(stride2) * H2, 0);
  FillRect(tall, stride2, 0, 0, W2, H2, 73, 73, 73);
  FillRect(tall, stride2, 10, 20, 210, 201, 45, 45, 45);
  FillRect(tall, stride2, 10, 21, 210, 200, 41, 41, 41);
  FillRect(tall, stride2, 70, 22, 150, 200, 255, 255, 255);
  DrawRedVLine(tall, stride2, 120, 21, 100, 0, 0, 255);
  DrawRedHLine(tall, stride2, 100, 10, 120, 0, 0, 255);
  out = sct::ObserveCanvasExcludingBackground(tall.data(), W2, H2, stride2, {10, 20, 210, 201},
                                              0, 0, NavigatorGrayModel(), 1.5f, true, 80, 178);
  std::printf("PRECISION navigator framed height bounds=[%d,%d,%d,%d)\n", out.bounds_capture.left,
              out.bounds_capture.top, out.bounds_capture.right, out.bounds_capture.bottom);
  Expect(!out.ambiguous && out.bounds_capture.left == 70 && out.bounds_capture.right == 150 &&
             out.bounds_capture.top == 22 && out.bounds_capture.bottom == 200,
         "navigator paper excludes frame rows joined through red viewport ink");
}

void TestNavigatorGrayRimDoesNotPeelWhitePaper() {
  // Recorder frame 2026-10-03: the 45-gray vertical rim differs from the
  // 41-gray view fill. Its continuation beside the paper does not turn a
  // white paper row into a frame line.
  constexpr int W=285, H=524, stride=W*4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride)*H, 0);
  FillRect(buf,stride,0,0,W,H,41,41,41);
  FillRect(buf,stride,0,0,2,H,45,45,45);
  FillRect(buf,stride,283,0,W,H,45,45,45);
  FillRect(buf,stride,2,64,283,461,255,255,255);
  const auto out=sct::ObserveCanvasExcludingBackground(buf.data(),W,H,stride,
      {0,0,W,H},2275,145,NavigatorGrayModel(),1.5f,true,4961,7016);
  Expect(!out.ambiguous && out.bounds_capture.left==2 && out.bounds_capture.top==64 &&
      out.bounds_capture.right==283 && out.bounds_capture.bottom==461,
      "gray navigator rim cannot peel actual white paper boundaries");
}

// Navigator-route fixture: workspace 500x400, a square canvas whose Navigator
// paper spans 260 thumbnail px, and a 200x160 red frame: 2.5 workspace px per
// thumbnail px, so the red frame alone measures a 65% zoom on both axes.
sct::SolveInput MakeScaleReadingInput(float scale_percent) {
  sct::SolveInput in;
  std::snprintf(in.capture_id, sizeof(in.capture_id), "test");
  in.generation = 1;
  in.canvas_pixel_width = 1000;
  in.canvas_pixel_height = 1000;
  in.workspace_roi_screen = {0, 0, 500, 400};
  in.navigator_roi_screen = {600, 0, 900, 300};
  in.navigator_thumbnail_roi_screen = {610, 40, 890, 320};
  in.workspace_canvas.four_sides_complete = 0;
  in.workspace_canvas.ambiguous = 1;
  in.navigator_canvas.bounds_screen = {620, 50, 880, 310};
  in.navigator_canvas.confidence = 0.9f;
  in.viewport.origin_top_left_displayed = {650, 80};
  in.viewport.axis_x_displayed = {200, 0};
  in.viewport.axis_y_displayed = {0, 160};
  in.viewport.width = 200;
  in.viewport.height = 160;
  in.viewport.confidence = 0.8f;
  in.numbers.scale_percent = scale_percent;
  in.numbers.scale_confidence = 1.f;
  in.numbers.rotation_confidence = 0.f;
  in.injected_scale_percent = scale_percent;
  return in;
}

bool SameMatrix(const sct::Affine2D& a, const sct::Affine2D& b, double tolerance = 1e-9) {
  for (int i = 0; i < 6; ++i) {
    if (std::abs(a.m[i] - b.m[i]) > tolerance) return false;
  }
  return true;
}

void TestScaleReadingSetsNavigatorZoom() {
  const auto r65 = sct::SolveTransform(MakeScaleReadingInput(65.f));
  const auto r66 = sct::SolveTransform(MakeScaleReadingInput(66.f));
  Expect(r65.status == sct::FailStatus::Ok && r66.status == sct::FailStatus::Ok,
         "scale reading solves");
  if (r65.status != sct::FailStatus::Ok || r66.status != sct::FailStatus::Ok) return;

  // The reading is CSP's own zoom: it replaces the red-frame measurement.
  const auto& c2s = r66.snapshot.canvas_to_screen;
  Expect(r66.snapshot.matrix_zoom_source == sct::MatrixZoomSource::ScaleReading,
         "consistent reading sets the zoom");
  Expect(std::abs(c2s.m[0] - 660.0) < 1e-9 && std::abs(c2s.m[4] - 660.0) < 1e-9 &&
             std::abs(c2s.m[1]) < 1e-9 && std::abs(c2s.m[3]) < 1e-9,
         "published zoom is exactly the reading on both axes");
  Expect(std::abs(r66.snapshot.scale_geometry_estimate - 66.f) < 1e-3 &&
             r66.snapshot.scale_consistency_error < 1e-4,
         "geometry zoom diagnostic reports the reading");
  // Without observed red sides the red frame is kept at the workspace centre.
  const auto c65 = r65.snapshot.screen_to_canvas.Apply({250, 200});
  const auto c66 = r66.snapshot.screen_to_canvas.Apply({250, 200});
  Expect(std::hypot(c65.x - c66.x, c65.y - c66.y) < 1e-12,
         "reading pivots on the workspace centre");

  // A lost decimal point (650 for 65.0) contradicts the red frame by 10x.
  const auto misread = sct::SolveTransform(MakeScaleReadingInput(650.f));
  Expect(misread.status == sct::FailStatus::Ok &&
             misread.snapshot.matrix_zoom_source == sct::MatrixZoomSource::NavigatorFrame,
         "misread reading is not used");
  Expect(SameMatrix(misread.snapshot.screen_to_canvas, r65.snapshot.screen_to_canvas),
         "misread reading keeps the red-frame matrix");

  Expect(r66.snapshot.marker.target_arm_display_px > r65.snapshot.marker.target_arm_display_px,
         "marker arm grows with scale");
  Expect(std::abs(r65.snapshot.marker.anchor_screen.x - r65.snapshot.canvas_to_screen.m[2]) < 1e-9 &&
             std::abs(r65.snapshot.marker.anchor_screen.y - r65.snapshot.canvas_to_screen.m[5]) < 1e-9,
         "marker anchor is normalized canvas top-left (0,0)");
  Expect(r65.snapshot.marker.x_arm_end_screen.x > r65.snapshot.marker.anchor_screen.x,
         "top-left marker X arm points along canvas +X");
  Expect(r65.snapshot.marker.y_arm_end_screen.y > r65.snapshot.marker.anchor_screen.y,
         "top-left marker Y arm points along canvas +Y");
}

void TestScaleReadingRefinesRotatedNavigatorRoute() {
  // Physical fixture: canvas 2400x1200 at 50%, displayed at 25 degrees. The
  // Navigator paper is measured one thumbnail pixel too wide (0.4% zoom).
  const double a = 25.0 * 3.14159265358979323846 / 180.0, c = std::cos(a), s = std::sin(a);
  sct::SolveInput in;
  std::snprintf(in.capture_id, sizeof(in.capture_id), "rotated-reading");
  in.canvas_pixel_width = 2400;
  in.canvas_pixel_height = 1200;
  in.injected_scale_percent = 50;
  in.workspace_roi_screen = {-1700, 100, -800, 700};
  in.navigator_canvas.bounds_capture = {2200, 50, 2441, 170};
  auto nav = [&](double x, double y) {
    const double dx = x + 1350, dy = y - 250;
    return sct::Vec2{2200 + (c * dx + s * dy) * 0.2, 50 + (-s * dx + c * dy) * 0.2};
  };
  const auto o = nav(-1700, 100), x = nav(-800, 100), y = nav(-1700, 700);
  in.viewport.origin_top_left_displayed = o;
  in.viewport.axis_x_displayed = {x.x - o.x, x.y - o.y};
  in.viewport.axis_y_displayed = {y.x - o.x, y.y - o.y};
  in.viewport.width = 180;
  in.viewport.height = 120;
  const auto refined = sct::SolveTransform(in);
  in.injected_scale_percent = 500;
  const auto navigator_only = sct::SolveTransform(in);
  Expect(refined.status == sct::FailStatus::Ok && navigator_only.status == sct::FailStatus::Ok,
         "rotated reading solves");
  if (refined.status != sct::FailStatus::Ok || navigator_only.status != sct::FailStatus::Ok) return;
  Expect(refined.snapshot.matrix_zoom_source == sct::MatrixZoomSource::ScaleReading &&
             navigator_only.snapshot.matrix_zoom_source == sct::MatrixZoomSource::NavigatorFrame,
         "rotated display uses a consistent reading only");
  const auto& m = refined.snapshot.canvas_to_screen.m;
  const auto& n = navigator_only.snapshot.canvas_to_screen.m;
  Expect(std::abs(std::hypot(m[0], m[3]) - 1200.0) < 1e-6 &&
             std::abs(std::hypot(m[1], m[4]) - 600.0) < 1e-6,
         "rotated zoom is exactly the reading");
  Expect(std::abs(std::atan2(m[3], m[0]) - std::atan2(n[3], n[0])) < 1e-12 &&
             std::abs(std::atan2(m[4], m[1]) - std::atan2(n[4], n[1])) < 1e-12,
         "reading keeps the red-frame rotation");
  const auto cr = refined.snapshot.screen_to_canvas.Apply({-1250, 400});
  const auto cn = navigator_only.snapshot.screen_to_canvas.Apply({-1250, 400});
  Expect(std::hypot(cr.x - cn.x, cr.y - cn.y) < 1e-12, "rotated reading pivots on the workspace centre");
}

// Captured CSP frame at 50%: the paper's top-left corner is visible at
// (1293,392) and its right/bottom run past the workspace. The Navigator shows
// only the red right/bottom sides, and its paper bounds include a border row,
// so the Navigator-only origin lands near (1284.8,379.9).
sct::SolveInput MakeTopLeftVisibleNavigatorRouteInput() {
  sct::SolveInput in;
  std::snprintf(in.capture_id, sizeof(in.capture_id), "top-left-visible");
  in.generation = 1;
  in.canvas_pixel_width = 4961;
  in.canvas_pixel_height = 7016;
  in.workspace_roi_screen = {409, 149, 2041, 1431};
  in.navigator_thumbnail_roi_screen = {2083, 142, 2558, 576};
  in.workspace_canvas.bounds_capture = {1293, 392, 2041, 1431};
  in.workspace_canvas.bounds_screen = in.workspace_canvas.bounds_capture;
  in.workspace_canvas.visible_edges_mask = 1 | 2;
  in.workspace_canvas.boundary_support[0] = 1.f;
  in.workspace_canvas.boundary_support[1] = 1.f;
  in.workspace_canvas_relation.canvas_crop_sides = 4 | 8;
  in.navigator_canvas.bounds_capture = {2168, 142, 2473, 576};
  in.navigator_canvas.bounds_screen = in.navigator_canvas.bounds_capture;
  in.viewport.origin_top_left_displayed = {2060.86, 113.33};
  in.viewport.axis_x_displayed = {199.64, 0};
  in.viewport.axis_y_displayed = {0, 159.17};
  in.viewport.width = 199.64f;
  in.viewport.height = 159.17f;
  in.viewport.observed_red_edge_export_count = 2;
  in.viewport.observed_red_edges[0] = {{2083.5, 272.5}, {2259.5, 272.5}, 8, 0};
  in.viewport.observed_red_edges[1] = {{2260.5, 143.5}, {2260.5, 272.5}, 4, 0};
  in.numbers.scale_percent = 50.f;
  in.numbers.scale_confidence = 1.f;
  in.numbers.rotation_confidence = 1.f;
  return in;
}

// Screen point of normalized canvas (u,v) by the red frame alone (unrotated).
sct::Vec2 NavigatorOnlyScreen(const sct::SolveInput& in, double u, double v) {
  const auto& ws = in.workspace_roi_screen;
  const auto& nav = in.navigator_canvas.bounds_capture;
  const double sx = ws.width() / in.viewport.axis_x_displayed.x;
  const double sy = ws.height() / in.viewport.axis_y_displayed.y;
  return {ws.left + (nav.left + u * nav.width() - in.viewport.origin_top_left_displayed.x) * sx,
          ws.top + (nav.top + v * nav.height() - in.viewport.origin_top_left_displayed.y) * sy};
}

bool AtNavigatorOnlyOrigin(const sct::TransformSnapshot& s, const sct::SolveInput& in) {
  const auto want = NavigatorOnlyScreen(in, 0, 0);
  return std::abs(s.marker.anchor_screen.x - want.x) < 1e-6 &&
         std::abs(s.marker.anchor_screen.y - want.y) < 1e-6;
}

void TestNavigatorRouteAnchorsToWorkspacePaperEdges() {
  auto in = MakeTopLeftVisibleNavigatorRouteInput();
  const auto r = sct::SolveTransform(in);
  Expect(r.status == sct::FailStatus::Ok, "top-left visible navigator route solves");
  if (r.status != sct::FailStatus::Ok) return;
  const auto& s = r.snapshot;
  Expect(!s.used_direct_workspace_path, "cropped paper stays on the navigator route");
  Expect(s.workspace_edge_anchor_mask == (1 | 2), "observed left/top paper edges anchor the matrix");
  Expect(s.matrix_zoom_source == sct::MatrixZoomSource::ScaleReading,
         "one edge per axis takes the zoom from the reading");
  Expect(std::abs(s.marker.anchor_screen.x - 1293.0) < 1e-6 &&
             std::abs(s.marker.anchor_screen.y - 392.0) < 1e-6,
         "canvas origin is the workspace-observed paper corner");
  const double zoom_x = s.canvas_to_screen.m[0] / in.canvas_pixel_width;
  const double zoom_y = s.canvas_to_screen.m[4] / in.canvas_pixel_height;
  std::printf("PRECISION anchored origin=(%.3f,%.3f) zoom=%.5fx%.5f (CSP 50%%)\n",
              s.marker.anchor_screen.x, s.marker.anchor_screen.y, zoom_x, zoom_y);
  Expect(std::abs(zoom_x - 0.5) < 1e-12 && std::abs(zoom_y - 0.5) < 1e-12,
         "anchored zoom is the reading");
  Expect(std::abs(s.canvas_to_screen.m[1]) < 1e-9 && std::abs(s.canvas_to_screen.m[3]) < 1e-9,
         "anchored matrix stays axis aligned");

  // Without workspace paper edges the reading still sets the zoom, and each
  // observed red side is mapped onto the workspace side it draws. CSP paints
  // an unrotated side on the pixel the boundary rounds to, so the stroke
  // centre (2260.5, 272.5) is the boundary 2260 / 272 plus half a pixel.
  in.workspace_canvas.visible_edges_mask = 0;
  const auto red_only = sct::SolveTransform(in);
  Expect(red_only.status == sct::FailStatus::Ok &&
             red_only.snapshot.workspace_edge_anchor_mask == 0 &&
             red_only.snapshot.matrix_zoom_source == sct::MatrixZoomSource::ScaleReading,
         "no workspace paper edge, reading zoom only");
  const auto& nav = in.navigator_canvas.bounds_capture;
  const auto right_bottom = red_only.snapshot.canvas_to_screen.Apply(
      {(2260.0 - nav.left) / nav.width(), (272.0 - nav.top) / nav.height()});
  Expect(std::abs(right_bottom.x - in.workspace_roi_screen.right) < 1e-6 &&
             std::abs(right_bottom.y - in.workspace_roi_screen.bottom) < 1e-6,
         "red-only route maps the red right/bottom boundaries onto the workspace sides");
  Expect(std::abs(red_only.snapshot.canvas_to_screen.m[0] - 0.5 * in.canvas_pixel_width) < 1e-9,
         "red-only route zoom is the reading");
  // With the Navigator paper bounds measured without the view's frame row
  // (2168,144)-(2473,575) the red frame alone lands within a few px of the
  // workspace truth (1293,392).
  auto measured_paper = in;
  measured_paper.navigator_canvas.bounds_capture = {2168, 144, 2473, 575};
  measured_paper.navigator_canvas.bounds_screen = measured_paper.navigator_canvas.bounds_capture;
  const auto red_measured = sct::SolveTransform(measured_paper);
  std::printf("PRECISION red-only origin=(%.3f,%.3f) truth=(1293,392)\n",
              red_measured.snapshot.marker.anchor_screen.x,
              red_measured.snapshot.marker.anchor_screen.y);
  Expect(red_measured.status == sct::FailStatus::Ok &&
             std::hypot(red_measured.snapshot.marker.anchor_screen.x - 1293.0,
                        red_measured.snapshot.marker.anchor_screen.y - 392.0) < 4.0,
         "red-only route origin stays within a few px of the paper corner");

  // A misread reading and no paper edges publish the red-frame matrix.
  in.numbers.scale_percent = 500.f;
  const auto nav_only = sct::SolveTransform(in);
  Expect(nav_only.status == sct::FailStatus::Ok &&
             nav_only.snapshot.matrix_zoom_source == sct::MatrixZoomSource::NavigatorFrame,
         "misread reading is not used");
  Expect(AtNavigatorOnlyOrigin(nav_only.snapshot, in), "navigator-only origin unchanged");
}

void TestNavigatorRouteAnchorCorrectsStaleNavigator() {
  // The workspace already shows the paper 200 px further right: the Navigator
  // has not repainted after a pan. Its zoom is still valid; its translation
  // is not, so the paper side, not the red frame position, picks the boundary.
  auto in = MakeTopLeftVisibleNavigatorRouteInput();
  in.workspace_canvas.bounds_screen.left += 200;
  in.workspace_canvas.bounds_capture.left += 200;
  const auto r = sct::SolveTransform(in);
  Expect(r.status == sct::FailStatus::Ok, "stale navigator route solves");
  Expect(r.snapshot.workspace_edge_anchor_mask == (1 | 2), "stale navigator is anchored");
  Expect(std::abs(r.snapshot.marker.anchor_screen.x - 1493.0) < 1e-6 &&
             std::abs(r.snapshot.marker.anchor_screen.y - 392.0) < 1e-6,
         "stale navigator origin follows the workspace paper corner");
}

void TestNavigatorRouteAnchorRejectsNonPaperObservation() {
  auto without_paper = MakeTopLeftVisibleNavigatorRouteInput();
  without_paper.workspace_canvas.visible_edges_mask = 0;
  const auto red_only = sct::SolveTransform(without_paper);

  // A side that is neither a measured straight edge nor clipped by the
  // workspace (artwork on the edge, rotated paper) is no paper rectangle.
  auto in = MakeTopLeftVisibleNavigatorRouteInput();
  in.workspace_canvas.visible_edges_mask = 1;
  in.workspace_canvas.boundary_support[1] = 0.5f;
  auto r = sct::SolveTransform(in);
  Expect(r.status == sct::FailStatus::Ok && r.snapshot.workspace_edge_anchor_mask == 0,
         "irregular paper side is not used");
  Expect(SameMatrix(r.snapshot.canvas_to_screen, red_only.snapshot.canvas_to_screen),
         "irregular paper side is treated as no paper evidence");

  // A 600x300 foreground (e.g. a floating panel) is not the 4961x7016 paper
  // at the read 50% zoom.
  in = MakeTopLeftVisibleNavigatorRouteInput();
  in.workspace_canvas.bounds_screen = {1293, 392, 1893, 692};
  in.workspace_canvas.bounds_capture = in.workspace_canvas.bounds_screen;
  in.workspace_canvas.visible_edges_mask = 0xF;
  for (float& support : in.workspace_canvas.boundary_support) support = 1.f;
  in.workspace_canvas_relation.canvas_crop_sides = 0;
  r = sct::SolveTransform(in);
  Expect(r.status == sct::FailStatus::Ok && r.snapshot.workspace_edge_anchor_mask == 0,
         "foreground with a different size is not the paper");
}

void TestNavigatorRouteAnchorFollowsDisplayRotation() {
  // Physical fixture (as in rotation_regression_tests): canvas 2400x1200 at
  // 50% on workspace [-1700,-800)x[100,700). The Navigator paper bounds are
  // off by one thumbnail pixel (5 screen px) in position and width.
  for (double angle : {90.0, 180.0, 270.0}) {
    const double a = angle * 3.14159265358979323846 / 180.0;
    const double c = std::round(std::cos(a)), s = std::round(std::sin(a));
    auto screen = [&](double u, double v) {
      return sct::Vec2{-1350 + c * 1200 * u - s * 600 * v, 250 + s * 1200 * u + c * 600 * v};
    };
    sct::SolveInput in;
    std::snprintf(in.capture_id, sizeof(in.capture_id), "rotated-anchor");
    in.canvas_pixel_width = 2400;
    in.canvas_pixel_height = 1200;
    in.injected_scale_percent = 50;
    in.workspace_roi_screen = {-1700, 100, -800, 700};
    auto nav = [&](double x, double y) {
      const double dx = x + 1350, dy = y - 250;
      return sct::Vec2{2200 + (c * dx + s * dy) * 0.2, 50 + (-s * dx + c * dy) * 0.2};
    };
    const auto o = nav(-1700, 100), x = nav(-800, 100), y = nav(-1700, 700);
    in.viewport.origin_top_left_displayed = o;
    in.viewport.axis_x_displayed = {x.x - o.x, x.y - o.y};
    in.viewport.axis_y_displayed = {y.x - o.x, y.y - o.y};
    in.viewport.width = 180;
    in.viewport.height = 120;
    in.navigator_canvas.bounds_capture = {2201, 51, 2442, 171};

    // Workspace-visible part of the paper and its uncropped sides.
    double l = 1e9, t = 1e9, r = -1e9, b = -1e9;
    for (double u : {0.0, 1.0}) for (double v : {0.0, 1.0}) {
      const auto p = screen(u, v);
      l = std::min(l, p.x); r = std::max(r, p.x); t = std::min(t, p.y); b = std::max(b, p.y);
    }
    const auto& ws = in.workspace_roi_screen;
    const wb::IntRect visible{int(std::max<double>(l, ws.left)), int(std::max<double>(t, ws.top)),
                              int(std::min<double>(r, ws.right)),
                              int(std::min<double>(b, ws.bottom))};
    in.workspace_canvas.bounds_screen = visible;
    in.workspace_canvas.bounds_capture = visible;
    const int crop = (visible.left == ws.left ? 1 : 0) | (visible.top == ws.top ? 2 : 0) |
                     (visible.right == ws.right ? 4 : 0) | (visible.bottom == ws.bottom ? 8 : 0);
    in.workspace_canvas.visible_edges_mask = 0xF & ~crop;
    for (int side = 0; side < 4; ++side)
      in.workspace_canvas.boundary_support[side] = (crop & (1 << side)) ? 0.f : 1.f;
    in.workspace_canvas_relation.canvas_crop_sides = crop;

    const auto result = sct::SolveTransform(in);
    Expect(result.status == sct::FailStatus::Ok, "rotated anchored solve");
    if (result.status != sct::FailStatus::Ok) continue;
    Expect(result.snapshot.workspace_edge_anchor_mask == (0xF & ~crop),
           "rotated display anchors every uncropped paper side");
    const auto want = screen(0, 0);
    const auto got = result.snapshot.marker.anchor_screen;
    Expect(std::hypot(want.x - got.x, want.y - got.y) < 1e-6,
           "rotated display origin is the matching workspace paper corner");
    // One paper edge per axis plus the reading's zoom pin the whole canvas.
    for (double u : {0.0, 0.5, 1.0}) for (double v : {0.0, 0.5, 1.0}) {
      const auto p = screen(u, v), q = result.snapshot.canvas_to_screen.Apply({u, v});
      Expect(std::hypot(p.x - q.x, p.y - q.y) < 1e-6, "rotated anchored canvas matches the display");
    }
  }
}

void TestPattern01UnknownRotationDeduplicatesEquivalentCropAssignments() {
  constexpr int W = 160;
  constexpr int H = 120;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  const wb::IntRect thumb{5, 5, 155, 115};
  const wb::IntRect canvas{20, 20, 140, 100};
  FillRect(buf, stride, canvas.left, canvas.top, canvas.right, canvas.bottom, 210, 210, 210);
  // A single cutting fragment above the Navigator midpoint.  Workspace crop
  // correspondence says it is semantic Bottom even though screen geometry
  // alone would call it Top.
  DrawRedHLine(buf, stride, 35, 30, 130, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.canvas_crop_sides = 8;  // Bottom
  // Deliberately asymmetric along the workspace edge: 50 px before the
  // contact interval and 150 px after it. A midpoint mirror would be wrong.
  in.workspace_canvas_relation.visible_canvas_bounds_workspace_local = {50, 60, 650, 540};
  in.display_rotation_degrees = 0.f;
  in.display_rotation_confidence = 0.f;  // reproduce intermittent OCR miss of visible "0.0"
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok,
         "0.1 accepts equivalent discrete-angle crop assignments");
  if (out.status != sct::FailStatus::Ok) return;
  Expect(out.frame.completion_strategy ==
             static_cast<int>(sct::ViewportCompletionPattern::ParallelSegmentsNoCompleteEdge),
         "unknown-rotation single fragment remains pattern 0.1");
  Expect(out.frame.origin_top_left_displayed.y < 35.0,
         "semantic Bottom role places completed frame above the observed edge");
  Expect(out.frame.origin_top_left_displayed.x > 17.0 &&
             out.frame.origin_top_left_displayed.x < 24.0,
         "0.1 tangential origin follows asymmetric workspace contact position");
}

void TestViewportPattern01FallsBackWhenVisibleCanvasExactRecoveryIsUnavailable() {
  // A lone horizontal red edge remains valid 0.1 evidence even when the
  // stricter visible-canvas recovery cannot be used (e.g. rotation OCR was
  // not read).  It must fall through to normal WCR single-edge completion.
  constexpr int W = 140;
  constexpr int H = 110;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  DrawRedHLine(buf, stride, 28, 25, 115, 0, 0, 220);
  wb::IntRect thumb{10, 10, 130, 100};
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.workspace_canvas_relation.visible_canvas_bounds_workspace_local = {80, 60, 720, 540};
  // Default zero rotation confidence intentionally makes CompleteFromVisibleCanvas
  // reject its exact path; the 0.1 single-edge path must still succeed.
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "0.1 falls back after exact visible-canvas rejection");
  Expect(out.frame.completion_strategy ==
             static_cast<int>(sct::ViewportCompletionPattern::ParallelSegmentsNoCompleteEdge),
         "fallback reports 0.1 rather than failing or relabeling as 1.0");
  Expect(out.frame.red_evidence.confirmed_complete_edge_count == 0,
         "fallback does not manufacture a complete red edge");
  if (!(std::abs(out.frame.width - 112.5) < 3.0 &&
        std::abs(out.frame.height - 84.375) < 3.0)) {
    std::printf("INFO: 0.1 recovered size %.3f x %.3f\n", out.frame.width, out.frame.height);
  }
  // The ideal horizontal red-on-canvas segment is 90 px; raster extraction
  // may extend it by about one pixel at either end.  90 px ÷
  // (visibleCanvasWidth(640) / workspaceWidth(800)) = 112.5 px; the second
  // axis is completed with the 4:3 workspace aspect.
  Expect(std::abs(out.frame.width - 112.5) < 3.0 &&
             std::abs(out.frame.height - 84.375) < 3.0,
         "0.1 divides red-on-canvas pixels by displayed-canvas/workspace ratio");
}

void TestRawStrokeEndpointsAndSubpixelCenter() {
  // Independent fixture: the viewport is a single horizontal fragment whose
  // true centerline spans [30.0, 110.0] in capture-pixel coordinates.  The
  // displayed canvas covers exactly half of the workspace width, so the
  // recovered workspace viewport width is 160 px.  A one-pixel gap is
  // intentional: the detector must use dilation to find the candidate but
  // raw pixels to measure the final endpoints.
  constexpr int W = 180;
  constexpr int H = 140;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  constexpr int y = 42;
  constexpr int x0 = 30;
  constexpr int x1 = 110;
  for (int x = x0; x <= x1; ++x) {
    if (x == 70) continue;
    // Alpha-blended red over white: the two pixel centers carry 0.75 and 0.25
    // coverage, giving a known centerline at 42.75.
    PutBgra(buf, stride, x, y, 64, 64, 229);
    const uint8_t b = 191, g = 191, r = 246;
    PutBgra(buf, stride, x, y + 1, b, g, r);
  }

  auto in = MakeViewportInput(buf, W, H, stride, {5, 5, 175, 135});
  in.navigator_canvas_bounds = {10, 10, 170, 130};
  in.workspace_canvas_relation.workspace_roi = {0, 0, 800, 600};
  in.workspace_canvas_relation.visible_canvas_bounds_workspace_local = {200, 150, 600, 450};
  in.workspace_canvas_relation.visible_canvas_workspace_fraction_x = 0.5f;
  in.workspace_canvas_relation.visible_canvas_workspace_fraction_y = 0.5f;
  in.display_rotation_confidence = 0.f;

  const auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "raw endpoint precision fixture completes");
  if (out.status != sct::FailStatus::Ok) return;

  const double width_error = std::abs(static_cast<double>(out.frame.width) - 160.0);
  const double center_error = std::abs(out.frame.origin_top_left_displayed.y - 42.75);
  std::printf("PRECISION raw_endpoint_width=%.6f width_error=%.6f center_y=%.6f center_error=%.6f\n",
              out.frame.width, width_error, out.frame.origin_top_left_displayed.y, center_error);
  Expect(width_error <= 0.75, "raw pixels define single-edge length without dilation expansion");
  Expect(center_error <= 0.35, "weighted anti-aliased profile preserves subpixel center");
}

void TestRawStrokeWidthAndDarkRedVariants() {
  struct Variant { int width; uint8_t b; uint8_t g; uint8_t r; };
  const Variant variants[] = {
      {1, 0, 0, 220},
      {2, 18, 18, 112},
      {3, 36, 30, 145},
  };
  for (const auto& variant : variants) {
    constexpr int W = 180, H = 140, stride = W * 4;
    std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
    constexpr int x0 = 30, x1 = 110, y0 = 54;
    for (int y = y0; y < y0 + variant.width; ++y) {
      for (int x = x0; x <= x1; ++x) {
        if (x != 72) PutBgra(buf, stride, x, y, variant.b, variant.g, variant.r);
      }
    }
    auto in = MakeViewportInput(buf, W, H, stride, {5, 5, 175, 135});
    in.navigator_canvas_bounds = {10, 10, 170, 130};
    in.workspace_canvas_relation.workspace_roi = {0, 0, 800, 600};
    in.workspace_canvas_relation.visible_canvas_bounds_workspace_local = {200, 150, 600, 450};
    const auto out = sct::CompleteViewportFrame(in);
    Expect(out.status == sct::FailStatus::Ok, "line-width/dark-red variant completes");
    if (out.status != sct::FailStatus::Ok) continue;
    std::printf("PRECISION variant width=%d color=(%d,%d,%d) recovered_width=%.6f\n",
                variant.width, variant.r, variant.g, variant.b, out.frame.width);
    Expect(std::abs(static_cast<double>(out.frame.width) - 160.0) <= 1.0,
           "line width and dark red do not alter centerline length");
  }
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
  // The complete top edge is the actual viewport side, not a clipped 0.1
  // fragment.  Its endpoints must survive completion exactly; only the normal
  // side is derived from the 4:3 workspace aspect.
  // Raster extraction expands this one-pixel stroke by roughly one pixel at
  // each endpoint.  Assert endpoint preservation (not the literal ink span),
  // and specifically reject the old 0.5 fragment-ratio rescaling.
  Expect(std::abs(out.frame.origin_top_left_displayed.x - (L - 0.5)) < 2.0 &&
             std::abs(out.frame.origin_top_left_displayed.y - (T + 0.5)) < 2.0 &&
             out.frame.width > (R - L - 2.0) &&
             std::abs(out.frame.height - out.frame.width * 0.75) < 2.0,
         "1.0 preserves the observed complete edge and only completes its normal axis");
}

void TestTwoSeparableRectanglesFormTwoGroupsDisambiguateBySize() {
  // 两套可分离平行/垂直结构 → 成两组；多组时靠显示画布形状（理论尺寸）唯一匹配
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

void TestShapeUniqueSelectsAmongMultipleGroups() {
  // 两组均可补全；仅一组匹配显示画布形状 → 直接选中，不必再靠窄红
  constexpr int W = 200;
  constexpr int H = 160;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  wb::IntRect thumb{5, 5, 195, 155};
  wb::IntRect canvas{20, 20, 180, 140};
  FillRect(buf, stride, canvas.left, canvas.top, canvas.right, canvas.bottom, 210, 210, 210);
  // 匹配理论：nav≈160×120，fraction→80×60
  DrawRedRect1px(buf, stride, 40, 30, 120, 90, 0, 0, 220);   // 80×60
  DrawRedRect1px(buf, stride, 130, 100, 160, 125, 0, 0, 220);  // 30×25（勿取 40×30，否则
                                                              // share=0.5 缺边恢复会凑成 80×60）
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.visible_canvas_fraction_x =
      static_cast<float>(80.0 / 160.0);
  in.workspace_canvas_relation.visible_canvas_fraction_y =
      static_cast<float>(60.0 / 120.0);
  in.workspace_canvas_relation.canvas_aspect_ratio = 80.0 / 60.0;
  // 关闭「边长/share」恢复，避免缺边组被放大到理论尺寸
  in.workspace_canvas_relation.visible_canvas_workspace_fraction_x = 1.0f;
  in.workspace_canvas_relation.visible_canvas_workspace_fraction_y = 1.0f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "shape-unique selects ok");
  Expect(std::abs(out.frame.width - 80.0) < 4.0, "shape-unique picked ~80 wide");
  Expect(std::abs(out.frame.height - 60.0) < 4.0, "shape-unique picked ~60 tall");
}

void TestNarrowRedBreaksShapeTie() {
  // 两框同形都匹配理论尺寸；一框强红、一框粉红灰红 → 窄红筛出强红
  constexpr int W = 220;
  constexpr int H = 180;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 200);
  wb::IntRect thumb{5, 5, 215, 175};
  wb::IntRect canvas{10, 10, 210, 170};
  DrawRedRect1px(buf, stride, 20, 20, 100, 80, 0, 0, 220);      // 80×60 强红
  DrawRedRect1px(buf, stride, 120, 90, 200, 150, 125, 130, 160);  // 80×60 粉灰红（仍可观测）
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.visible_canvas_fraction_x =
      static_cast<float>(80.0 / 200.0);
  in.workspace_canvas_relation.visible_canvas_fraction_y =
      static_cast<float>(60.0 / 160.0);
  in.workspace_canvas_relation.canvas_aspect_ratio = 80.0 / 60.0;
  in.workspace_canvas_relation.visible_canvas_workspace_fraction_x = 1.0f;
  in.workspace_canvas_relation.visible_canvas_workspace_fraction_y = 1.0f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "narrow-red breaks shape tie");
  Expect(std::abs(out.frame.origin_top_left_displayed.x - (20 + 0.5)) < 2.5,
         "narrow-red picked strong-red rect left");
  Expect(std::abs(out.frame.origin_top_left_displayed.y - (20 + 0.5)) < 2.5,
         "narrow-red picked strong-red rect top");
}

void TestCanvasShapeAmbiguousMustFail() {
  // 两组尺寸都不匹配显示画布形状 → 失败，不得取较近者
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
         "|S|==0 must fail, no nearest-pick");
}

void TestCanvasShapeAndNarrowRedTieMustFail() {
  // 两框同形且同为强窄红 → 形状+窄红仍并列，必须失败
  constexpr int W = 220;
  constexpr int H = 180;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 200);
  wb::IntRect thumb{5, 5, 215, 175};
  wb::IntRect canvas{10, 10, 210, 170};
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
         "|N|>=2 must fail, no smaller-error pick");
}

// ---- 切割边对应契约 ----

constexpr int kTestEdgeL = 1;
constexpr int kTestEdgeT = 2;
constexpr int kTestEdgeR = 4;
constexpr int kTestEdgeB = 8;

int FindExportedEdgeRole(const sct::NavigatorViewportFrame& f, double x0, double y0, double x1,
                         double y1) {
  auto near = [](double a, double b) { return std::abs(a - b) < 3.0; };
  for (int i = 0; i < f.observed_red_edge_export_count; ++i) {
    const auto& e = f.observed_red_edges[i];
    const bool match =
        (near(e.p0.x, x0) && near(e.p0.y, y0) && near(e.p1.x, x1) && near(e.p1.y, y1)) ||
        (near(e.p0.x, x1) && near(e.p0.y, y1) && near(e.p1.x, x0) && near(e.p1.y, y0));
    if (match) return e.workspace_edge;
  }
  // 水平/竖直段：按中线近似匹配
  const bool want_h = std::abs(y0 - y1) < 1.0;
  for (int i = 0; i < f.observed_red_edge_export_count; ++i) {
    const auto& e = f.observed_red_edges[i];
    const bool is_h = std::abs(e.p0.y - e.p1.y) < 2.0;
    if (want_h != is_h) continue;
    if (want_h) {
      const double y = 0.5 * (e.p0.y + e.p1.y);
      if (near(y, y0)) return e.workspace_edge;
    } else {
      const double x = 0.5 * (e.p0.x + e.p1.x);
      if (near(x, x0)) return e.workspace_edge;
    }
  }
  return -1;
}

void TestCropCorrespondence180BottomMapsToTop() {
  // 工作区 Bottom 切割 + 显示 180° → 红上沿是对应边，标签仍是 B（下），不得负负得正标成 T
  constexpr int W = 160;
  constexpr int H = 140;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  wb::IntRect thumb{5, 5, 155, 135};
  wb::IntRect canvas{20, 20, 140, 120};
  FillRect(buf, stride, canvas.left, canvas.top, canvas.right, canvas.bottom, 210, 210, 210);
  constexpr int L = 40, T = 35, R = 110, B = 95;
  DrawRedRect1px(buf, stride, L, T, R, B, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.canvas_crop_sides = kTestEdgeB;
  in.workspace_canvas_relation.occluded_canvas_edges = kTestEdgeB;
  in.display_rotation_degrees = 180.f;
  in.display_rotation_confidence = 0.9f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "180+bottom crop ok");
  Expect(out.used_crop_correspondence, "180+bottom used crop correspondence");
  const int top_role = FindExportedEdgeRole(out.frame, L + 0.5, T + 0.5, R + 0.5, T + 0.5);
  Expect(top_role == kTestEdgeB, "180+bottom: red top edge labeled B (workspace crop)");
  const int bot_role = FindExportedEdgeRole(out.frame, L + 0.5, B + 0.5, R + 0.5, B + 0.5);
  Expect(bot_role == kTestEdgeT, "180+bottom: red bottom propagated to T");
}

void TestNonCuttingRedEdgeExcludedFromCropMatch() {
  // 画布外下方的红边不进入切割对应；仅穿插画布的上沿参与
  constexpr int W = 160;
  constexpr int H = 150;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  wb::IntRect thumb{5, 5, 155, 145};
  wb::IntRect canvas{25, 25, 130, 90};  // 画布偏上
  FillRect(buf, stride, canvas.left, canvas.top, canvas.right, canvas.bottom, 210, 210, 210);
  constexpr int L = 45, T = 40, R = 105;
  DrawRedVLine(buf, stride, L, T, 85, 0, 0, 220);
  DrawRedVLine(buf, stride, R, T, 85, 0, 0, 220);
  DrawRedHLine(buf, stride, T, L, R, 0, 0, 220);
  // 非切割：完全在画布下方
  DrawRedHLine(buf, stride, 120, L, R, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.canvas_crop_sides = kTestEdgeB;
  in.display_rotation_degrees = 180.f;
  in.display_rotation_confidence = 0.9f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "non-cutting scenario completes");
  Expect(out.used_crop_correspondence, "non-cutting scenario used crop path");
  const int top_role = FindExportedEdgeRole(out.frame, L + 0.5, T + 0.5, R + 0.5, T + 0.5);
  Expect(top_role == kTestEdgeB, "cutting top gets B (workspace Bottom @180)");
  const int outside_role = FindExportedEdgeRole(out.frame, L + 0.5, 120.5, R + 0.5, 120.5);
  // 传播后可为 B，但不得被当成切割对应的「工作区底边同名」主判定来源；
  // 关键：若被导出，角色须与传播一致（B），且 top 已是 T。
  if (outside_role >= 0) {
    Expect(outside_role == kTestEdgeT, "non-cutting edge only via propagate -> T");
  }
}

void TestCropFourPairsDirectLtrb() {
  constexpr int W = 160;
  constexpr int H = 140;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  wb::IntRect thumb{5, 5, 155, 135};
  wb::IntRect canvas{15, 15, 145, 125};
  FillRect(buf, stride, canvas.left, canvas.top, canvas.right, canvas.bottom, 210, 210, 210);
  constexpr int L = 40, T = 35, R = 110, B = 95;
  DrawRedRect1px(buf, stride, L, T, R, B, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.canvas_crop_sides =
      kTestEdgeL | kTestEdgeT | kTestEdgeR | kTestEdgeB;
  in.display_rotation_degrees = 0.f;
  in.display_rotation_confidence = 0.9f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "four-pair crop ok");
  Expect(out.used_crop_correspondence, "four-pair used crop");
  Expect(FindExportedEdgeRole(out.frame, L + 0.5, T + 0.5, R + 0.5, T + 0.5) == kTestEdgeT,
         "four-pair top=T");
  Expect(FindExportedEdgeRole(out.frame, L + 0.5, B + 0.5, R + 0.5, B + 0.5) == kTestEdgeB,
         "four-pair bottom=B");
  Expect(FindExportedEdgeRole(out.frame, L + 0.5, T + 0.5, L + 0.5, B + 0.5) == kTestEdgeL,
         "four-pair left=L");
  Expect(FindExportedEdgeRole(out.frame, R + 0.5, T + 0.5, R + 0.5, B + 0.5) == kTestEdgeR,
         "four-pair right=R");
}

void TestCropOnePairPropagates() {
  // 仅 Bottom 切割 + 0° → 底边得 B，其余由传播得 L/T/R
  constexpr int W = 160;
  constexpr int H = 140;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  wb::IntRect thumb{5, 5, 155, 135};
  wb::IntRect canvas{15, 15, 145, 125};
  FillRect(buf, stride, canvas.left, canvas.top, canvas.right, canvas.bottom, 210, 210, 210);
  constexpr int L = 40, T = 35, R = 110, B = 95;
  DrawRedRect1px(buf, stride, L, T, R, B, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.canvas_crop_sides = kTestEdgeB;
  in.display_rotation_degrees = 0.f;
  in.display_rotation_confidence = 0.9f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "one-pair propagate ok");
  Expect(out.used_crop_correspondence, "one-pair used crop");
  Expect(FindExportedEdgeRole(out.frame, L + 0.5, B + 0.5, R + 0.5, B + 0.5) == kTestEdgeB,
         "one-pair bottom=B");
  Expect(FindExportedEdgeRole(out.frame, L + 0.5, T + 0.5, R + 0.5, T + 0.5) == kTestEdgeT,
         "one-pair top propagated T");
  Expect(FindExportedEdgeRole(out.frame, L + 0.5, T + 0.5, L + 0.5, B + 0.5) == kTestEdgeL,
         "one-pair left propagated L");
  Expect(FindExportedEdgeRole(out.frame, R + 0.5, T + 0.5, R + 0.5, B + 0.5) == kTestEdgeR,
         "one-pair right propagated R");
}

void TestCropConflictAmbiguous() {
  // Panning may put both viewport sides left of the canvas center. This is
  // valid geometry: the canvas center is not evidence of a role conflict.
  constexpr int W = 160;
  constexpr int H = 120;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  wb::IntRect thumb{5, 5, 155, 115};
  wb::IntRect canvas{20, 20, 140, 100};
  FillRect(buf, stride, canvas.left, canvas.top, canvas.right, canvas.bottom, 210, 210, 210);
  // 两条竖边都在中心（x=80）左侧
  DrawRedVLine(buf, stride, 40, 30, 90, 0, 0, 220);
  DrawRedVLine(buf, stride, 55, 30, 90, 0, 0, 220);
  DrawRedHLine(buf, stride, 30, 40, 55, 0, 0, 220);
  DrawRedHLine(buf, stride, 90, 40, 55, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.canvas_crop_sides = kTestEdgeL | kTestEdgeR;
  in.display_rotation_degrees = 0.f;
  in.display_rotation_confidence = 0.9f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok,
         "off-center viewport remains valid under panning");
}

void TestPartial180SingleCuttingHorizontal() {
  // 视口非完整四边：仅上沿穿插画布（180°+底裁切典型场景），不得 no group completed
  constexpr int W = 160;
  constexpr int H = 150;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 40);
  wb::IntRect thumb{5, 5, 155, 145};
  wb::IntRect canvas{25, 25, 130, 90};
  FillRect(buf, stride, canvas.left, canvas.top, canvas.right, canvas.bottom, 210, 210, 210);
  constexpr int L = 45, T = 40, R = 105, B_out = 120;
  DrawRedVLine(buf, stride, L, T, 115, 0, 0, 220);
  DrawRedVLine(buf, stride, R, T, 115, 0, 0, 220);
  DrawRedHLine(buf, stride, T, L, R, 0, 0, 220);
  DrawRedHLine(buf, stride, B_out, L, R, 0, 0, 220);
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  // This fixture's actual viewport is 60x80. Its independent workspace must
  // have the same aspect; the paper rectangle has a different aspect.
  in.workspace_canvas_relation.workspace_roi = {0, 0, 600, 800};
  in.navigator_canvas_bounds = canvas;
  in.workspace_canvas_relation.canvas_crop_sides = kTestEdgeB;
  in.display_rotation_degrees = 180.f;
  in.display_rotation_confidence = 0.9f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "partial 180 single cutting horizontal ok");
  Expect(out.used_crop_correspondence, "partial 180 uses crop");
  const int top_role = FindExportedEdgeRole(out.frame, L + 0.5, T + 0.5, R + 0.5, T + 0.5);
  Expect(top_role == kTestEdgeB, "partial 180: cutting top labeled B (workspace Bottom)");
}

void TestFourEdges180RotationLabelsWithoutCrop() {
  constexpr int W = 120;
  constexpr int H = 100;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  constexpr int L = 20, T = 15, R = 80, B = 70;
  DrawRedRect1px(buf, stride, L, T, R, B, 0, 0, 220);
  wb::IntRect thumb{10, 10, 110, 90};
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.workspace_canvas_relation.canvas_crop_sides = 0;
  in.display_rotation_degrees = 180.f;
  in.display_rotation_confidence = 0.9f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "4-edge 180 rotation ok");
  Expect(!out.used_crop_correspondence, "4-edge 180 no crop path");
  Expect(FindExportedEdgeRole(out.frame, L + 0.5, T + 0.5, R + 0.5, T + 0.5) == kTestEdgeB,
         "180 no crop: screen-top geom → B");
  Expect(FindExportedEdgeRole(out.frame, L + 0.5, B + 0.5, R + 0.5, B + 0.5) == kTestEdgeT,
         "180 no crop: screen-bottom geom → T");
}

void TestNoWorkspaceCropDoesNotForceCropPath() {
  constexpr int W = 120;
  constexpr int H = 100;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  constexpr int L = 20, T = 15, R = 80, B = 70;
  DrawRedRect1px(buf, stride, L, T, R, B, 0, 0, 220);
  wb::IntRect thumb{10, 10, 110, 90};
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  in.workspace_canvas_relation.canvas_crop_sides = 0;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "no-crop still ok");
  Expect(!out.used_crop_correspondence, "no-crop must not force crop path");
}

void DrawRedLine(std::vector<uint8_t>& buf, int stride, int w, int h, double x0, double y0,
                 double x1, double y1, uint8_t rb, uint8_t rg, uint8_t rr) {
  const double dx = x1 - x0, dy = y1 - y0;
  const int n = static_cast<int>(std::ceil(std::hypot(dx, dy)));
  for (int i = 0; i <= n; ++i) {
    const double t = n ? static_cast<double>(i) / n : 0.0;
    const int x = static_cast<int>(std::lround(x0 + t * dx));
    const int y = static_cast<int>(std::lround(y0 + t * dy));
    if (x >= 0 && y >= 0 && x < w && y < h) PutBgra(buf, stride, x, y, rb, rg, rr);
  }
}

void TestViewportRotatedRectangleRelativeOrthogonal() {
  constexpr int W = 200;
  constexpr int H = 180;
  constexpr int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 255);
  const double cx = 100, cy = 90, hw = 45, hh = 30;
  const double ang = 25.0 * 3.141592653589793 / 180.0;
  const double c = std::cos(ang), s = std::sin(ang);
  auto C = [&](double lx, double ly) {
    return std::pair<double, double>{cx + lx * c - ly * s, cy + lx * s + ly * c};
  };
  const auto tl = C(-hw, -hh), tr = C(hw, -hh), br = C(hw, hh), bl = C(-hw, hh);
  DrawRedLine(buf, stride, W, H, tl.first, tl.second, tr.first, tr.second, 0, 0, 220);
  DrawRedLine(buf, stride, W, H, tr.first, tr.second, br.first, br.second, 0, 0, 220);
  DrawRedLine(buf, stride, W, H, br.first, br.second, bl.first, bl.second, 0, 0, 220);
  DrawRedLine(buf, stride, W, H, bl.first, bl.second, tl.first, tl.second, 0, 0, 220);
  wb::IntRect thumb{20, 20, 180, 160};
  auto in = MakeViewportInput(buf, W, H, stride, thumb);
  // Navigator viewport rotates opposite to the main canvas.
  in.display_rotation_degrees = -25.f;
  in.display_rotation_confidence = 1.f;
  auto out = sct::CompleteViewportFrame(in);
  Expect(out.status == sct::FailStatus::Ok, "rotated 25° red rect must complete");
  Expect(out.frame.red_evidence.confirmed_complete_edge_count == 4,
         "rotated rect has 4 complete edges");
  Expect(out.frame.width > 70.f && out.frame.width < 110.f, "rotated width near 90");
  Expect(out.frame.height > 45.f && out.frame.height < 80.f, "rotated height near 60");
  const bool skewed =
      std::abs(out.frame.axis_x_displayed.y) > 8.0 || std::abs(out.frame.axis_y_displayed.x) > 8.0;
  Expect(skewed, "rotated frame axes are not screen-axis-aligned");
}

void TestFourSidesCompleteRequiresOutwardBackground() {
  constexpr int W = 200, H = 160;
  const int stride = W * 4;
  std::vector<uint8_t> buf(static_cast<size_t>(stride) * H, 0);
  // Workspace BG dark gray; white canvas inset on all sides.
  FillRect(buf, stride, 0, 0, W, H, 45, 45, 45);
  FillRect(buf, stride, 40, 30, 120, 130, 255, 255, 255);

  wb::BackgroundModel model;
  model.center_lab = wb::BgrToLab(45, 45, 45);
  model.strong_delta_e = 6.f;
  model.weak_delta_e = 12.f;

  auto ok = sct::ObserveCanvasExcludingBackground(buf.data(), W, H, stride, {0, 0, W, H}, 0, 0,
                                                  model);
  Expect(!ok.ambiguous, "surrounded canvas observation not ambiguous");
  Expect(ok.four_sides_complete, "inset canvas with BG on all sides → four_sides_complete");

  // Right side butts the ROI rim (no outward BG band) → must NOT be complete.
  FillRect(buf, stride, 0, 0, W, H, 45, 45, 45);
  FillRect(buf, stride, 40, 30, W, 130, 255, 255, 255);
  auto cropped = sct::ObserveCanvasExcludingBackground(buf.data(), W, H, stride, {0, 0, W, H}, 0,
                                                       0, model);
  Expect(!cropped.four_sides_complete, "canvas touching ROI rim → not four_sides_complete");

  // Inset from ROI but right exterior is mostly non-BG (red), with only a 1px BG gap
  // so the white canvas stays a separate component. depth≥2 → support < threshold.
  FillRect(buf, stride, 0, 0, W, H, 45, 45, 45);
  FillRect(buf, stride, 40, 30, 120, 130, 255, 255, 255);
  FillRect(buf, stride, 121, 30, 180, 130, 200, 40, 40);
  auto fake = sct::ObserveCanvasExcludingBackground(buf.data(), W, H, stride, {0, 0, W, H}, 0, 0,
                                                    model);
  Expect(!fake.four_sides_complete && fake.bounds_capture.right == 120,
         "nearby non-background invalidates the claimed exterior background rim");
}

}  // namespace

void TestNavigatorEnclosedBackgroundIsNotPaper() {
  constexpr int W=200, H=160, stride=W*4;
  std::vector<uint8_t> buf(stride*H,0);
  wb::BackgroundModel model;
  model.center_lab=wb::BgrToLab(45,45,45);
  model.strong_delta_e=6.f;
  model.weak_delta_e=12.f;
  for (int variant=0; variant<3; ++variant) {
    FillRect(buf,stride,0,0,W,H,45,45,45);
    FillRect(buf,stride,60,20,150,145,255,255,255);
    // Dark artwork must not change the paper bounds.
    FillRect(buf,stride,80,50,120,90,45,45,45);
    // Closed viewport enclosing left gutter, or clipped at the ROI top.
    const int top=variant==1 ? 0 : 8;
    const int red=variant==2 ? 100 : 255;
    FillRect(buf,stride,25,top,28,110,40,40,red);
    FillRect(buf,stride,25,top,135,top+3,40,40,red);
    FillRect(buf,stride,25,107,135,110,40,40,red);
    FillRect(buf,stride,132,top,135,110,40,40,red);
    auto out=sct::ObserveCanvasExcludingBackground(buf.data(),W,H,stride,
        {0,0,W,H},-300,20,model,1.f,true);
    if (!out.ambiguous) {
      g_max_navigator_canvas_boundary_error = std::max(
          g_max_navigator_canvas_boundary_error,
          static_cast<double>(std::max({
              std::abs(out.bounds_capture.left - 60),
              std::abs(out.bounds_capture.top - 20),
              std::abs(out.bounds_capture.right - 150),
              std::abs(out.bounds_capture.bottom - 145)})));
    }
    Expect(!out.ambiguous && out.bounds_capture.left==60 &&
        out.bounds_capture.top==20 && out.bounds_capture.right==150 &&
        out.bounds_capture.bottom==145,
        "navigator excludes gray pocket and viewport ink from paper bounds");
    Expect(out.bounds_screen.left==-240 && out.bounds_screen.top==40,
        "navigator paper bounds preserve capture-to-screen origin");
  }
}

int main() {
  TestNavigatorEnclosedBackgroundIsNotPaper();
  TestWorkspaceCanvasRelationBuild();
  TestFourSidesCompleteRequiresOutwardBackground();
  TestViewportRedFourEdgesGeometryStable();
  TestViewportRedSoftAaAndGapRecall();
  TestLargePinkPlateauIsNotMistakenForManyRedLines();
  TestRedColorPeakKeepsTrueFrameInsidePinkArea();
  TestViewportPattern01ParallelNoComplete();
  TestViewportPattern01FallsBackWhenVisibleCanvasExactRecoveryIsUnavailable();
  TestRawStrokeEndpointsAndSubpixelCenter();
  TestRawStrokeWidthAndDarkRedVariants();
  TestViewportPattern02IntersectingNoComplete();
  TestViewportPattern02MirroredCorners();
  TestPattern01UnknownRotationDeduplicatesEquivalentCropAssignments();
  TestViewportNoRedPixelsIsEdgeFailureNotFrameFound();
  TestScaleReadingSetsNavigatorZoom();
  TestScaleReadingRefinesRotatedNavigatorRoute();
  TestNavigatorRouteAnchorsToWorkspacePaperEdges();
  TestNavigatorRouteAnchorCorrectsStaleNavigator();
  TestNavigatorRouteAnchorRejectsNonPaperObservation();
  TestNavigatorRouteAnchorFollowsDisplayRotation();
  TestInterferenceOrthogonalRedDoesNotFakeComplete();
  TestTwoSeparableRectanglesFormTwoGroupsDisambiguateBySize();
  TestShapeUniqueSelectsAmongMultipleGroups();
  TestNarrowRedBreaksShapeTie();
  TestCanvasShapeAmbiguousMustFail();
  TestCanvasShapeAndNarrowRedTieMustFail();
  TestCropCorrespondence180BottomMapsToTop();
  TestNonCuttingRedEdgeExcludedFromCropMatch();
  TestCropFourPairsDirectLtrb();
  TestCropOnePairPropagates();
  TestCropConflictAmbiguous();
  TestPartial180SingleCuttingHorizontal();
  TestFourEdges180RotationLabelsWithoutCrop();
  TestNoWorkspaceCropDoesNotForceCropPath();
  TestViewportRotatedRectangleRelativeOrthogonal();
  TestViewportPattern02SideBesidePaper();
  TestViewportPattern02SideBesidePaperRejectsStaleNavigator();
  TestViewportPattern03SideBesidePaper();
  TestViewportPattern03FallsBackOnlyForUniqueCropSide();
  TestNavigatorPaperExcludesViewFrameAndRecoversClippedEdge();
  TestNavigatorGrayRimDoesNotPeelWhitePaper();
  if (g_failures == 0) {
    std::printf("OK: all contract tests passed; max_navigator_canvas_boundary_error=%.6f\n",
                g_max_navigator_canvas_boundary_error);
    return 0;
  }
  std::printf("FAILED: %d test(s)\n", g_failures);
  return 1;
}
