#include "sct/viewport_frame.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

namespace sct {
namespace {

// ---- 观测与成组硬常量（契约测试固定，禁止运行时自适应放宽）----
constexpr int kRedDilateRadius = 1;
constexpr int kPeakRefineRadius = 2;
constexpr int kMinRawRedPixels = 8;
constexpr int kMinSegmentSpan = 6;
constexpr float kMinEdgeSupport = 0.35f;
constexpr int kPeakMergeDist = 3;

// 组内空间相近 / 直角容差
constexpr int kGroupCornerTolPx = 6;
constexpr float kParallelOverlapRatio = 0.35f;
constexpr int kParallelOverlapMinPx = 8;
constexpr int kMinViewportSidePx = 12;

// 背景粘着（框外法向邻域）
constexpr int kBgOutwardProbePx = 3;
constexpr float kBgTouchRatio = 0.55f;
constexpr int kBgTouchMinPx = 6;
constexpr int kBgColorTol = 28;

// 理论导航器红框尺寸匹配
constexpr double kTheorySizeAbsPx = 8.0;
constexpr double kTheorySizeRel = 0.18;

constexpr int kEdgeL = 1;
constexpr int kEdgeT = 2;
constexpr int kEdgeR = 4;
constexpr int kEdgeB = 8;

inline bool IsNavigatorRedPixel(const uint8_t* p) {
  const int b = p[0], g = p[1], r = p[2];
  const int maxc = std::max(r, std::max(g, b));
  const int minc = std::min(r, std::min(g, b));
  if (maxc < 90) return false;
  const int delta = maxc - minc;
  if (delta < 22) return false;
  if (r + 12 < maxc) return false;
  const float sat = static_cast<float>(delta) / static_cast<float>(maxc);
  if (sat < 0.18f) return false;
  if (r >= 140 && r - g >= 40 && r - b >= 40 && r >= g + 20) return true;
  if (r >= 110 && r >= g + 12 && r >= b + 12 && (r - g) + (r - b) >= 45) return true;
  if (r >= 150 && g <= r - 8 && b <= r - 8 && sat >= 0.16f) return true;
  return false;
}

void DilateMask3x3(const std::vector<uint8_t>& src, int w, int h, std::vector<uint8_t>& dst) {
  dst.assign(static_cast<size_t>(w) * h, 0);
  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      bool on = false;
      for (int dy = -kRedDilateRadius; dy <= kRedDilateRadius && !on; ++dy) {
        const int ny = y + dy;
        if (ny < 0 || ny >= h) continue;
        for (int dx = -kRedDilateRadius; dx <= kRedDilateRadius; ++dx) {
          const int nx = x + dx;
          if (nx < 0 || nx >= w) continue;
          if (src[static_cast<size_t>(ny) * w + nx]) {
            on = true;
            break;
          }
        }
      }
      if (on) dst[static_cast<size_t>(y) * w + x] = 1;
    }
  }
}

void ProjectMask(const std::vector<uint8_t>& mask, int w, int h, std::vector<int>& col,
                 std::vector<int>& row) {
  col.assign(w, 0);
  row.assign(h, 0);
  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      if (!mask[static_cast<size_t>(y) * w + x]) continue;
      ++col[x];
      ++row[y];
    }
  }
}

std::vector<int> FindProjectionPeaks(const std::vector<int>& hist, int min_run) {
  std::vector<int> peaks;
  const int thr = std::max(3, min_run / 8);
  for (int i = 1; i + 1 < static_cast<int>(hist.size()); ++i) {
    if (hist[i] >= thr && hist[i] >= hist[i - 1] && hist[i] >= hist[i + 1]) peaks.push_back(i);
  }
  return peaks;
}

// 合并近邻峰，保留全部簇代表（不再只取外簇），供多组枚举。
std::vector<int> AllClusterPeaks(const std::vector<int>& peaks, const std::vector<int>& hist) {
  if (peaks.empty()) return {};
  std::vector<int> clustered;
  size_t i = 0;
  while (i < peaks.size()) {
    size_t j = i;
    int best = peaks[i];
    while (j + 1 < peaks.size() && peaks[j + 1] - peaks[i] <= kPeakMergeDist) {
      ++j;
      if (hist[peaks[j]] > hist[best]) best = peaks[j];
    }
    for (size_t k = i; k <= j; ++k) {
      if (hist[peaks[k]] > hist[best]) best = peaks[k];
    }
    clustered.push_back(best);
    i = j + 1;
  }
  return clustered;
}

struct ObservedEdge {
  RedLineSegment seg{};
  double coord = -1.0;  // local x (vertical) or y (horizontal)
  int workspace_edge = 0;  // L/T/R/B bit（组内指派）
  bool complete = false;
  bool has_start_corner = false;  // GroupRightAngle at start
  bool has_end_corner = false;    // GroupRightAngle at end
};

ViewportCompletionResult Fail(FailStatus st, const char* msg) {
  ViewportCompletionResult r;
  r.status = st;
  std::snprintf(r.message, sizeof(r.message), "%s", msg);
  return r;
}

void SetCorners(NavigatorViewportFrame& f) {
  f.semantic_corners[0] = f.origin_top_left_displayed;
  f.semantic_corners[1] = {f.origin_top_left_displayed.x + f.axis_x_displayed.x,
                           f.origin_top_left_displayed.y + f.axis_x_displayed.y};
  f.semantic_corners[2] = {f.origin_top_left_displayed.x + f.axis_x_displayed.x +
                               f.axis_y_displayed.x,
                           f.origin_top_left_displayed.y + f.axis_x_displayed.y +
                               f.axis_y_displayed.y};
  f.semantic_corners[3] = {f.origin_top_left_displayed.x + f.axis_y_displayed.x,
                           f.origin_top_left_displayed.y + f.axis_y_displayed.y};
  f.width = static_cast<float>(
      std::hypot(f.axis_x_displayed.x, f.axis_x_displayed.y));
  f.height = static_cast<float>(
      std::hypot(f.axis_y_displayed.x, f.axis_y_displayed.y));
}

bool MaskAt(const std::vector<uint8_t>& m, int w, int h, int x, int y) {
  if (x < 0 || y < 0 || x >= w || y >= h) return false;
  return m[static_cast<size_t>(y) * w + x] != 0;
}

bool MeasureVerticalEdge(const std::vector<uint8_t>& det, const std::vector<uint8_t>& raw, int w,
                         int h, int peak_x, ObservedEdge& out) {
  const int x0 = std::clamp(peak_x, 0, w - 1);
  int y_lo = h, y_hi = -1;
  int red_on = 0;
  for (int y = 0; y < h; ++y) {
    bool hit = false;
    for (int dx = -1; dx <= 1; ++dx) {
      if (MaskAt(det, w, h, x0 + dx, y)) {
        hit = true;
        break;
      }
    }
    if (hit) {
      y_lo = std::min(y_lo, y);
      y_hi = std::max(y_hi, y);
      ++red_on;
    }
  }
  if (y_hi < y_lo || (y_hi - y_lo + 1) < kMinSegmentSpan) return false;
  const int span = y_hi - y_lo + 1;
  const float support = static_cast<float>(red_on) / static_cast<float>(span);
  if (support < kMinEdgeSupport) return false;

  double sum = 0, wt = 0;
  for (int y = y_lo; y <= y_hi; ++y) {
    for (int dx = -kPeakRefineRadius; dx <= kPeakRefineRadius; ++dx) {
      const int x = x0 + dx;
      if (!MaskAt(raw, w, h, x, y)) continue;
      sum += x;
      wt += 1;
    }
  }
  const double cx = wt > 0 ? sum / wt : static_cast<double>(x0);
  out.coord = cx;
  out.seg.horizontal = false;
  out.seg.x0 = cx;
  out.seg.x1 = cx;
  out.seg.y0 = y_lo;
  out.seg.y1 = y_hi;
  out.seg.support = support;
  out.seg.corner_at_start = -1;
  out.seg.corner_at_end = -1;
  return true;
}

bool MeasureHorizontalEdge(const std::vector<uint8_t>& det, const std::vector<uint8_t>& raw, int w,
                           int h, int peak_y, ObservedEdge& out) {
  const int y0 = std::clamp(peak_y, 0, h - 1);
  int x_lo = w, x_hi = -1;
  int red_on = 0;
  for (int x = 0; x < w; ++x) {
    bool hit = false;
    for (int dy = -1; dy <= 1; ++dy) {
      if (MaskAt(det, w, h, x, y0 + dy)) {
        hit = true;
        break;
      }
    }
    if (hit) {
      x_lo = std::min(x_lo, x);
      x_hi = std::max(x_hi, x);
      ++red_on;
    }
  }
  if (x_hi < x_lo || (x_hi - x_lo + 1) < kMinSegmentSpan) return false;
  const int span = x_hi - x_lo + 1;
  const float support = static_cast<float>(red_on) / static_cast<float>(span);
  if (support < kMinEdgeSupport) return false;

  double sum = 0, wt = 0;
  for (int x = x_lo; x <= x_hi; ++x) {
    for (int dy = -kPeakRefineRadius; dy <= kPeakRefineRadius; ++dy) {
      const int y = y0 + dy;
      if (!MaskAt(raw, w, h, x, y)) continue;
      sum += y;
      wt += 1;
    }
  }
  const double cy = wt > 0 ? sum / wt : static_cast<double>(y0);
  out.coord = cy;
  out.seg.horizontal = true;
  out.seg.x0 = x_lo;
  out.seg.x1 = x_hi;
  out.seg.y0 = cy;
  out.seg.y1 = cy;
  out.seg.support = support;
  out.seg.corner_at_start = -1;
  out.seg.corner_at_end = -1;
  return true;
}

double EdgeLen(const ObservedEdge& e) {
  return e.seg.horizontal ? (e.seg.x1 - e.seg.x0) : (e.seg.y1 - e.seg.y0);
}

// 正交邻接：端点相交/共端，或投影落到对方延长邻域（§4.2.2）。
bool OrthogonalAdjacent(const ObservedEdge& v, const ObservedEdge& h) {
  if (v.seg.horizontal || !h.seg.horizontal) return false;
  const double tol = kGroupCornerTolPx;
  const bool x_on_h = (v.coord >= h.seg.x0 - tol && v.coord <= h.seg.x1 + tol);
  const bool y_on_v = (h.coord >= v.seg.y0 - tol && h.coord <= v.seg.y1 + tol);
  if (x_on_h && y_on_v) return true;

  const double vx = v.coord;
  const double hy = h.coord;
  const double ends_v[2][2] = {{vx, v.seg.y0}, {vx, v.seg.y1}};
  const double ends_h[2][2] = {{h.seg.x0, hy}, {h.seg.x1, hy}};
  for (int i = 0; i < 2; ++i) {
    for (int j = 0; j < 2; ++j) {
      const double dx = ends_v[i][0] - ends_h[j][0];
      const double dy = ends_v[i][1] - ends_h[j][1];
      if (std::hypot(dx, dy) <= tol) return true;
    }
  }
  return false;
}

bool ParallelPairOk(const ObservedEdge& a, const ObservedEdge& b, double max_side) {
  if (a.seg.horizontal != b.seg.horizontal) return false;
  const double spacing = std::abs(a.coord - b.coord);
  if (spacing < kMinViewportSidePx) return false;
  if (spacing > max_side + kGroupCornerTolPx) return false;

  double overlap = 0;
  double len_a = EdgeLen(a);
  double len_b = EdgeLen(b);
  if (len_a < 1.0 || len_b < 1.0) return false;
  // 缺边平行对：长度须相近，禁止对角无关长短线拼装（§4.2.2.3）
  if (std::min(len_a, len_b) / std::max(len_a, len_b) < 0.5) return false;

  if (!a.seg.horizontal) {
    const double lo = std::max(a.seg.y0, b.seg.y0);
    const double hi = std::min(a.seg.y1, b.seg.y1);
    overlap = hi - lo;
  } else {
    const double lo = std::max(a.seg.x0, b.seg.x0);
    const double hi = std::min(a.seg.x1, b.seg.x1);
    overlap = hi - lo;
  }
  // 重叠相对较长边，避免短边偶然落入长边投影就成对
  const double need =
      std::max(static_cast<double>(kParallelOverlapMinPx),
               kParallelOverlapRatio * std::max(len_a, len_b));
  return overlap >= need;
}

// 组内直角：端点 P 附近与组内正交边相交/贴合（§4.3）。
bool OrthogonalMeetAtPoint(const ObservedEdge& a, double px, double py, const ObservedEdge& b) {
  if (a.seg.horizontal == b.seg.horizontal) return false;
  const double tol = kGroupCornerTolPx;
  const ObservedEdge& v = a.seg.horizontal ? b : a;
  const ObservedEdge& h = a.seg.horizontal ? a : b;
  // 交点候选 (v.coord, h.coord) 须靠近 (px,py)，且落在两边延长邻域。
  const double cx = v.coord;
  const double cy = h.coord;
  if (std::hypot(cx - px, cy - py) > tol) return false;
  if (cx < h.seg.x0 - tol || cx > h.seg.x1 + tol) return false;
  if (cy < v.seg.y0 - tol || cy > v.seg.y1 + tol) return false;
  return true;
}

bool EndpointHasGroupRightAngle(const ObservedEdge& a, bool at_start,
                                const std::vector<ObservedEdge>& group_edges) {
  const double px = at_start ? a.seg.x0 : a.seg.x1;
  const double py = at_start ? a.seg.y0 : a.seg.y1;
  for (const auto& b : group_edges) {
    if (&b == &a) continue;
    if (OrthogonalMeetAtPoint(a, px, py, b)) return true;
  }
  return false;
}

void AnnotateGroupRightAngles(std::vector<ObservedEdge>& group_edges) {
  for (auto& e : group_edges) {
    e.has_start_corner = EndpointHasGroupRightAngle(e, true, group_edges);
    e.has_end_corner = EndpointHasGroupRightAngle(e, false, group_edges);
    e.complete = e.has_start_corner && e.has_end_corner;
    e.seg.corner_at_start = e.has_start_corner ? 1 : -1;
    e.seg.corner_at_end = e.has_end_corner ? 1 : -1;
  }
}

// 组内指派至多一套 L/T/R/B。
bool AssignGroupWorkspaceEdges(std::vector<ObservedEdge>& edges) {
  std::vector<ObservedEdge*> verts, hors;
  for (auto& e : edges) {
    e.workspace_edge = 0;
    if (e.seg.horizontal)
      hors.push_back(&e);
    else
      verts.push_back(&e);
  }
  if (verts.size() > 2 || hors.size() > 2) return false;
  std::sort(verts.begin(), verts.end(),
            [](const ObservedEdge* a, const ObservedEdge* b) { return a->coord < b->coord; });
  std::sort(hors.begin(), hors.end(),
            [](const ObservedEdge* a, const ObservedEdge* b) { return a->coord < b->coord; });

  if (verts.size() == 2) {
    if (verts[1]->coord - verts[0]->coord < kMinViewportSidePx) return false;
    verts[0]->workspace_edge = kEdgeL;
    verts[1]->workspace_edge = kEdgeR;
  } else if (verts.size() == 1) {
    verts[0]->workspace_edge = kEdgeL;  // 单竖直边：占位 L，pattern 路径再按几何解释
  }
  if (hors.size() == 2) {
    if (hors[1]->coord - hors[0]->coord < kMinViewportSidePx) return false;
    hors[0]->workspace_edge = kEdgeT;
    hors[1]->workspace_edge = kEdgeB;
  } else if (hors.size() == 1) {
    hors[0]->workspace_edge = kEdgeT;
  }
  return true;
}

ObservedEdge* FindEdge(std::vector<ObservedEdge>& edges, int mask) {
  for (auto& e : edges)
    if (e.workspace_edge == mask) return &e;
  return nullptr;
}

bool GroupSpatialGeometryOk(const std::vector<ObservedEdge>& edges, double max_w, double max_h) {
  if (edges.empty() || edges.size() > 4) return false;
  int n_v = 0, n_h = 0;
  for (const auto& e : edges) {
    if (e.seg.horizontal)
      ++n_h;
    else
      ++n_v;
  }
  if (n_v > 2 || n_h > 2) return false;

  // 平行对边
  std::vector<const ObservedEdge*> verts, hors;
  for (const auto& e : edges) {
    if (e.seg.horizontal)
      hors.push_back(&e);
    else
      verts.push_back(&e);
  }
  if (verts.size() == 2) {
    if (!ParallelPairOk(*verts[0], *verts[1], max_w)) return false;
  }
  if (hors.size() == 2) {
    if (!ParallelPairOk(*hors[0], *hors[1], max_h)) return false;
  }

  // 正交邻接：凡同时存在的相邻角色边必须空间相近；
  // 仅有一条竖直+一条水平时也必须正交邻接，否则远距拼装。
  if (verts.size() >= 1 && hors.size() >= 1) {
    bool any_adj = false;
    for (const auto* v : verts) {
      for (const auto* h : hors) {
        if (OrthogonalAdjacent(*v, *h)) any_adj = true;
      }
    }
    if (!any_adj) return false;
  }

  // 四边齐全时：每个角都应有邻接（矩形闭合假设）
  if (verts.size() == 2 && hors.size() == 2) {
    int adj_count = 0;
    for (const auto* v : verts)
      for (const auto* h : hors)
        if (OrthogonalAdjacent(*v, *h)) ++adj_count;
    if (adj_count < 3) return false;  // 至少三个角贴合，允许一侧略弱
  }
  return true;
}

struct BgColor {
  int b = 128, g = 128, r = 128;
  bool valid = false;
};

// 与导航器画布观测同源的背景色：优先 ThumbnailRoi \ NavigatorCanvas，否则 ROI 边框非红采样。
BgColor EstimateNavigatorBackground(const ViewportCompletionInput& in, const wb::IntRect& roi) {
  BgColor bg;
  long sb = 0, sg = 0, sr = 0;
  int n = 0;
  wb::IntRect canvas = in.navigator_canvas_bounds.Clamp(in.width, in.height);
  const bool has_chrome =
      canvas.valid() &&
      (canvas.left > roi.left || canvas.top > roi.top || canvas.right < roi.right ||
       canvas.bottom < roi.bottom);

  auto accum = [&](int x, int y) {
    if (x < roi.left || x >= roi.right || y < roi.top || y >= roi.bottom) return;
    const uint8_t* p =
        in.bgra + static_cast<size_t>(y) * in.stride + static_cast<size_t>(x) * 4;
    if (IsNavigatorRedPixel(p)) return;
    sb += p[0];
    sg += p[1];
    sr += p[2];
    ++n;
  };

  if (has_chrome) {
    for (int y = roi.top; y < roi.bottom; ++y) {
      for (int x = roi.left; x < roi.right; ++x) {
        if (x >= canvas.left && x < canvas.right && y >= canvas.top && y < canvas.bottom) continue;
        accum(x, y);
      }
    }
  } else {
    for (int x = roi.left; x < roi.right; ++x) {
      accum(x, roi.top);
      accum(x, roi.bottom - 1);
    }
    for (int y = roi.top; y < roi.bottom; ++y) {
      accum(roi.left, y);
      accum(roi.right - 1, y);
    }
  }
  if (n < 8) return bg;
  bg.b = static_cast<int>(sb / n);
  bg.g = static_cast<int>(sg / n);
  bg.r = static_cast<int>(sr / n);
  bg.valid = true;
  return bg;
}

bool PixelMatchesBg(const uint8_t* p, const BgColor& bg) {
  if (!bg.valid) return false;
  return std::abs(static_cast<int>(p[0]) - bg.b) <= kBgColorTol &&
         std::abs(static_cast<int>(p[1]) - bg.g) <= kBgColorTol &&
         std::abs(static_cast<int>(p[2]) - bg.r) <= kBgColorTol;
}

// 外侧 = 朝向该组矩形假设的框外方向。
// 粘着判定优先认「落在 NavigatorCanvas 外」；仅有画布外色带时才用背景色模型，
// 避免 ThumbnailRoi==Canvas 且整幅同色时把所有边都判成粘背景。
bool EdgeTouchesBackground(const ObservedEdge& e, double left, double right, double top,
                           double bottom, const ViewportCompletionInput& in, const wb::IntRect& roi,
                           const wb::IntRect& canvas, const BgColor& bg, bool has_chrome) {
  const int rw = roi.width();
  const int rh = roi.height();
  int hit = 0, total = 0;

  auto sample = [&](int lx, int ly) {
    if (lx < 0 || ly < 0 || lx >= rw || ly >= rh) return;
    const int ax = roi.left + lx;
    const int ay = roi.top + ly;
    ++total;
    const bool outside_canvas =
        ax < canvas.left || ax >= canvas.right || ay < canvas.top || ay >= canvas.bottom;
    if (outside_canvas) {
      ++hit;
      return;
    }
    if (!has_chrome || !bg.valid) return;
    const uint8_t* p =
        in.bgra + static_cast<size_t>(ay) * in.stride + static_cast<size_t>(ax) * 4;
    if (PixelMatchesBg(p, bg)) ++hit;
  };

  if (!e.seg.horizontal) {
    const bool outward_left =
        (e.workspace_edge == kEdgeL) ||
        (e.workspace_edge == 0 && std::abs(e.coord - left) <= std::abs(e.coord - right));
    const int x0 = static_cast<int>(std::lround(e.coord));
    const int y0 = static_cast<int>(std::lround(e.seg.y0));
    const int y1 = static_cast<int>(std::lround(e.seg.y1));
    for (int y = y0; y <= y1; y += std::max(1, (y1 - y0) / 16)) {
      for (int d = 1; d <= kBgOutwardProbePx; ++d) {
        sample(outward_left ? x0 - d : x0 + d, y);
      }
    }
  } else {
    const bool outward_top =
        (e.workspace_edge == kEdgeT) ||
        (e.workspace_edge == 0 && std::abs(e.coord - top) <= std::abs(e.coord - bottom));
    const int y0 = static_cast<int>(std::lround(e.coord));
    const int x0 = static_cast<int>(std::lround(e.seg.x0));
    const int x1 = static_cast<int>(std::lround(e.seg.x1));
    for (int x = x0; x <= x1; x += std::max(1, (x1 - x0) / 16)) {
      for (int d = 1; d <= kBgOutwardProbePx; ++d) {
        sample(x, outward_top ? y0 - d : y0 + d);
      }
    }
  }
  if (total < kBgTouchMinPx) return false;
  return hit >= kBgTouchMinPx &&
         static_cast<float>(hit) / static_cast<float>(total) >= kBgTouchRatio;
}

bool GroupTouchesBackground(const std::vector<ObservedEdge>& edges, double left, double right,
                            double top, double bottom, const ViewportCompletionInput& in,
                            const wb::IntRect& roi, const wb::IntRect& canvas, const BgColor& bg,
                            bool has_chrome) {
  for (const auto& e : edges) {
    if (EdgeTouchesBackground(e, left, right, top, bottom, in, roi, canvas, bg, has_chrome))
      return true;
  }
  return false;
}

bool SizeMatchesTheory(double w, double h, double W_nav, double H_nav) {
  if (!(w > 2.0 && h > 2.0 && W_nav > 2.0 && H_nav > 2.0)) return false;
  const double tw = std::max(kTheorySizeAbsPx, kTheorySizeRel * W_nav);
  const double th = std::max(kTheorySizeAbsPx, kTheorySizeRel * H_nav);
  return std::abs(w - W_nav) <= tw && std::abs(h - H_nav) <= th;
}

struct GroupCandidate {
  std::vector<int> indices;  // into pool
  std::vector<ObservedEdge> edges;
  int complete_count = 0;
  int partial_count = 0;
  int unanchored = 0;
  int confirmed_corners = 0;
  bool touches_background = false;
  bool completed_ok = false;
  ViewportCompletionPattern pattern = ViewportCompletionPattern::FourCompleteEdges;
  NavigatorViewportFrame frame{};
};

bool IndicesEqualSorted(std::vector<int> a, std::vector<int> b) {
  std::sort(a.begin(), a.end());
  std::sort(b.begin(), b.end());
  return a == b;
}

bool IsSubsetIndices(const std::vector<int>& sub, const std::vector<int>& super) {
  for (int x : sub) {
    if (std::find(super.begin(), super.end(), x) == super.end()) return false;
  }
  return true;
}

// 对单组做 pattern 补全；失败则 completed_ok=false（淘汰，不硬编矩形）。
bool CompleteGroupPattern(GroupCandidate& g, const ViewportCompletionInput& in,
                          const wb::IntRect& roi, int rw, int rh) {
  AnnotateGroupRightAngles(g.edges);
  if (!AssignGroupWorkspaceEdges(g.edges)) return false;

  g.complete_count = 0;
  g.partial_count = 0;
  g.unanchored = 0;
  g.confirmed_corners = 0;
  int n_h = 0, n_v = 0;
  for (const auto& e : g.edges) {
    if (e.seg.horizontal)
      ++n_h;
    else
      ++n_v;
    if (e.complete)
      ++g.complete_count;
    else if (e.has_start_corner || e.has_end_corner)
      ++g.partial_count;
    else
      ++g.unanchored;
    if (e.has_start_corner) ++g.confirmed_corners;
    if (e.has_end_corner) ++g.confirmed_corners;
  }

  NavigatorViewportFrame& frame = g.frame;
  frame = NavigatorViewportFrame{};
  frame.visible_edge_count = static_cast<int>(g.edges.size());
  frame.red_evidence.segment_count = 0;
  for (const auto& e : g.edges) {
    if (frame.red_evidence.segment_count >= 32) break;
    frame.red_evidence.segments[frame.red_evidence.segment_count++] = e.seg;
  }
  frame.red_evidence.confirmed_complete_edge_count = g.complete_count;
  frame.red_evidence.partial_edge_count = g.partial_count;
  frame.red_evidence.unanchored_segment_count = g.unanchored;
  frame.red_evidence.confirmed_corner_count = g.confirmed_corners;

  auto set_pattern = [&](ViewportCompletionPattern p) {
    frame.completion_strategy = static_cast<int>(p);
    frame.red_evidence.completion_pattern = p;
    g.pattern = p;
  };

  const double aspect = in.workspace_canvas_relation.canvas_aspect_ratio > 1e-6
                            ? in.workspace_canvas_relation.canvas_aspect_ratio
                            : 1.0;
  const auto& wcr = in.workspace_canvas_relation;
  auto abs_x = [&](double lx) { return roi.left + lx + 0.5; };
  auto abs_y = [&](double ly) { return roi.top + ly + 0.5; };

  frame.complete_edge_export_count = 0;
  for (const auto& e : g.edges) {
    if (!e.complete) continue;
    if (frame.complete_edge_export_count >= 4) break;
    auto& ce = frame.complete_edges[frame.complete_edge_export_count++];
    ce.p0 = {abs_x(e.seg.x0), abs_y(e.seg.y0)};
    ce.p1 = {abs_x(e.seg.x1), abs_y(e.seg.y1)};
    ce.workspace_edge = e.workspace_edge;
  }

  auto finish_ok = [&](int conf_edges) -> bool {
    SetCorners(frame);
    if (!(frame.width > 2.f && frame.height > 2.f) ||
        !(std::isfinite(frame.width) && std::isfinite(frame.height))) {
      return false;
    }
    frame.confidence =
        std::clamp(0.15f * conf_edges + 0.1f * g.complete_count, 0.f, 1.f);
    g.completed_ok = true;
    return true;
  };

  auto complete_from_ltrb = [&](double left, double right, double top, double bottom) -> bool {
    if (!(right > left + 2.0 && bottom > top + 2.0)) return false;
    frame.origin_top_left_displayed = {abs_x(left), abs_y(top)};
    frame.axis_x_displayed = {abs_x(right) - abs_x(left), 0};
    frame.axis_y_displayed = {0, abs_y(bottom) - abs_y(top)};
    if (frame.axis_x_displayed.x <= 0 || frame.axis_y_displayed.y <= 0) return false;
    return finish_ok(g.complete_count);
  };

  auto recover_size_from_vertical = [&](const ObservedEdge& e, double& w, double& h) -> bool {
    const float share_y = wcr.visible_canvas_workspace_fraction_y;
    const double covered = EdgeLen(e);
    if (share_y > 1e-4f && covered >= 2.0) {
      h = covered / share_y;
      w = h * aspect;
      return w > 4.0 && h > 4.0;
    }
    return false;
  };
  auto recover_size_from_horizontal = [&](const ObservedEdge& e, double& w, double& h) -> bool {
    const float share_x = wcr.visible_canvas_workspace_fraction_x;
    const double covered = EdgeLen(e);
    if (share_x > 1e-4f && covered >= 2.0) {
      w = covered / share_x;
      h = w / aspect;
      return w > 4.0 && h > 4.0;
    }
    return false;
  };

  auto place_vertical_edge = [&](const ObservedEdge& e, double w, double h) {
    const double cy = abs_y(0.5 * (e.seg.y0 + e.seg.y1));
    const bool is_left = (e.workspace_edge == kEdgeL) ||
                         (e.workspace_edge == 0 && e.coord < rw * 0.5);
    frame.origin_top_left_displayed = {is_left ? abs_x(e.coord) : abs_x(e.coord) - w, cy - h * 0.5};
    frame.axis_x_displayed = {w, 0};
    frame.axis_y_displayed = {0, h};
  };
  auto place_horizontal_edge = [&](const ObservedEdge& e, double w, double h) {
    const double cx = abs_x(0.5 * (e.seg.x0 + e.seg.x1));
    const bool is_top = (e.workspace_edge == kEdgeT) ||
                        (e.workspace_edge == 0 && e.coord < rh * 0.5);
    frame.origin_top_left_displayed = {cx - w * 0.5, is_top ? abs_y(e.coord) : abs_y(e.coord) - h};
    frame.axis_x_displayed = {w, 0};
    frame.axis_y_displayed = {0, h};
  };

  ObservedEdge* L = FindEdge(g.edges, kEdgeL);
  ObservedEdge* R = FindEdge(g.edges, kEdgeR);
  ObservedEdge* T = FindEdge(g.edges, kEdgeT);
  ObservedEdge* B = FindEdge(g.edges, kEdgeB);

  // 单边时 Assign 固定为 L/T；若几何上更像 R/B，按 ROI 中线改指派。
  if (n_v == 1 && L && !R) {
    if (L->coord >= rw * 0.5) {
      L->workspace_edge = kEdgeR;
      R = L;
      L = nullptr;
    }
  }
  if (n_h == 1 && T && !B) {
    if (T->coord >= rh * 0.5) {
      T->workspace_edge = kEdgeB;
      B = T;
      T = nullptr;
    }
  }

  if (g.complete_count >= 4 && L && R && T && B && L->complete && R->complete && T->complete &&
      B->complete) {
    set_pattern(ViewportCompletionPattern::FourCompleteEdges);
    return complete_from_ltrb(L->coord, R->coord, T->coord, B->coord);
  }

  if (g.complete_count == 3) {
    set_pattern(ViewportCompletionPattern::ThreeCompleteEdges);
    if (L && R && T && B) {
      double left = L->coord, right = R->coord, top = T->coord, bottom = B->coord;
      if (!L->complete && R->complete && T->complete && B->complete) {
        left = right - (bottom - top) * aspect;
        return complete_from_ltrb(left, right, top, bottom);
      }
      if (L->complete && !R->complete && T->complete && B->complete) {
        right = left + (bottom - top) * aspect;
        return complete_from_ltrb(left, right, top, bottom);
      }
      if (L->complete && R->complete && !T->complete && B->complete) {
        top = bottom - (right - left) / aspect;
        return complete_from_ltrb(left, right, top, bottom);
      }
      if (L->complete && R->complete && T->complete && !B->complete) {
        bottom = top + (right - left) / aspect;
        return complete_from_ltrb(left, right, top, bottom);
      }
    }
    return false;
  }

  if (g.complete_count == 2) {
    if (L && R && L->complete && R->complete && !(T && T->complete) && !(B && B->complete)) {
      set_pattern(ViewportCompletionPattern::TwoParallelCompleteEdges);
      const double w_local = R->coord - L->coord;
      const double h_local = w_local / aspect;
      double cy = 0.5 * (std::min(L->seg.y0, R->seg.y0) + std::max(L->seg.y1, R->seg.y1));
      frame.origin_top_left_displayed = {abs_x(L->coord), abs_y(cy - h_local * 0.5)};
      frame.axis_x_displayed = {abs_x(R->coord) - abs_x(L->coord), 0};
      frame.axis_y_displayed = {0, h_local};
      const double len_l = EdgeLen(*L);
      const double len_r = EdgeLen(*R);
      if (std::abs(len_l - h_local) > std::max(8.0, 0.35 * h_local) &&
          std::abs(len_r - h_local) > std::max(8.0, 0.35 * h_local)) {
        return false;
      }
      return finish_ok(2);
    }
    if (T && B && T->complete && B->complete && !(L && L->complete) && !(R && R->complete)) {
      set_pattern(ViewportCompletionPattern::TwoParallelCompleteEdges);
      const double h_local = B->coord - T->coord;
      const double w_local = h_local * aspect;
      double cx = 0.5 * (std::min(T->seg.x0, B->seg.x0) + std::max(T->seg.x1, B->seg.x1));
      frame.origin_top_left_displayed = {abs_x(cx - w_local * 0.5), abs_y(T->coord)};
      frame.axis_x_displayed = {w_local, 0};
      frame.axis_y_displayed = {0, abs_y(B->coord) - abs_y(T->coord)};
      const double len_t = EdgeLen(*T);
      const double len_b = EdgeLen(*B);
      if (std::abs(len_t - w_local) > std::max(8.0, 0.35 * w_local) &&
          std::abs(len_b - w_local) > std::max(8.0, 0.35 * w_local)) {
        return false;
      }
      return finish_ok(2);
    }

    struct Pair {
      ObservedEdge* a;
      ObservedEdge* b;
    };
    const Pair pairs[] = {{L, T}, {R, T}, {L, B}, {R, B}};
    for (const auto& p : pairs) {
      if (!p.a || !p.b || !p.a->complete || !p.b->complete) continue;
      set_pattern(ViewportCompletionPattern::TwoIntersectingCompleteEdges);
      const double vx = p.a->coord;
      const double hy = p.b->coord;
      double w = 0, h = 0;
      if (!recover_size_from_vertical(*p.a, w, h) && !recover_size_from_horizontal(*p.b, w, h)) {
        h = EdgeLen(*p.a);
        w = EdgeLen(*p.b);
        if (w < 4 || h < 4) return false;
        const double h2 = w / aspect;
        const double w2 = h * aspect;
        if (std::abs(h2 - h) <= std::abs(w2 - w))
          h = h2;
        else
          w = w2;
      }
      const bool left = (p.a->workspace_edge == kEdgeL);
      const bool top = (p.b->workspace_edge == kEdgeT);
      const double ox = left ? abs_x(vx) : abs_x(vx) - w;
      const double oy = top ? abs_y(hy) : abs_y(hy) - h;
      frame.origin_top_left_displayed = {ox, oy};
      frame.axis_x_displayed = {w, 0};
      frame.axis_y_displayed = {0, h};
      return finish_ok(2);
    }
    return false;
  }

  if (g.complete_count == 1) {
    set_pattern(ViewportCompletionPattern::OneCompleteEdge);
    ObservedEdge* e = nullptr;
    for (auto& ed : g.edges)
      if (ed.complete) {
        e = &ed;
        break;
      }
    if (!e) return false;
    double w = 0, h = 0;
    if (e->seg.horizontal) {
      if (!recover_size_from_horizontal(*e, w, h)) return false;
      place_horizontal_edge(*e, w, h);
    } else {
      if (!recover_size_from_vertical(*e, w, h)) return false;
      place_vertical_edge(*e, w, h);
    }
    return finish_ok(1);
  }

  // complete_count == 0 → 0.1 or 0.2
  const bool intersecting = (n_h > 0 && n_v > 0);
  if (!intersecting) {
    set_pattern(ViewportCompletionPattern::ParallelSegmentsNoCompleteEdge);
    const ObservedEdge* best = &g.edges[0];
    for (const auto& e : g.edges)
      if (EdgeLen(e) > EdgeLen(*best)) best = &e;
    double w = 0, h = 0;
    if (best->seg.horizontal) {
      if (!recover_size_from_horizontal(*best, w, h)) return false;
      place_horizontal_edge(*best, w, h);
    } else {
      if (!recover_size_from_vertical(*best, w, h)) return false;
      place_vertical_edge(*best, w, h);
    }
    if (g.edges.size() >= 2 && !best->seg.horizontal) {
      const ObservedEdge* other = nullptr;
      for (const auto& e : g.edges)
        if (&e != best && !e.seg.horizontal) {
          other = &e;
          break;
        }
      if (other) {
        const double span = std::abs(other->coord - best->coord);
        if (span > 4.0 && std::abs(span - w) > std::max(10.0, 0.4 * w)) return false;
      }
    }
    if (g.edges.size() >= 2 && best->seg.horizontal) {
      const ObservedEdge* other = nullptr;
      for (const auto& e : g.edges)
        if (&e != best && e.seg.horizontal) {
          other = &e;
          break;
        }
      if (other) {
        const double span = std::abs(other->coord - best->coord);
        if (span > 4.0 && std::abs(span - h) > std::max(10.0, 0.4 * h)) return false;
      }
    }
    return finish_ok(0);
  }

  set_pattern(ViewportCompletionPattern::IntersectingSegmentsNoCompleteEdge);
  ObservedEdge* v = nullptr;
  ObservedEdge* hz = nullptr;
  double best_dist = 1e100;
  for (auto& a : g.edges) {
    if (a.seg.horizontal) continue;
    for (auto& b : g.edges) {
      if (!b.seg.horizontal) continue;
      const double dx = std::min(std::abs(a.coord - b.seg.x0), std::abs(a.coord - b.seg.x1));
      const double dy = std::min(std::abs(b.coord - a.seg.y0), std::abs(b.coord - a.seg.y1));
      const double d = dx + dy;
      if (d < best_dist) {
        best_dist = d;
        v = &a;
        hz = &b;
      }
    }
  }
  if (!v || !hz || best_dist > 12.0) return false;

  double w = 0, h = 0;
  if (!recover_size_from_vertical(*v, w, h) && !recover_size_from_horizontal(*hz, w, h)) {
    h = std::max(EdgeLen(*v), 8.0);
    w = std::max(EdgeLen(*hz), 8.0);
    const double h2 = w / aspect;
    const double w2 = h * aspect;
    if (std::abs(h2 - h) <= std::abs(w2 - w))
      h = h2;
    else
      w = w2;
  }

  const bool left = (v->workspace_edge == kEdgeL) || (v->workspace_edge == 0 && v->coord < rw * 0.5);
  const bool top =
      (hz->workspace_edge == kEdgeT) || (hz->workspace_edge == 0 && hz->coord < rh * 0.5);
  frame.origin_top_left_displayed = {left ? abs_x(v->coord) : abs_x(v->coord) - w,
                                     top ? abs_y(hz->coord) : abs_y(hz->coord) - h};
  frame.axis_x_displayed = {w, 0};
  frame.axis_y_displayed = {0, h};
  return finish_ok(0);
}

int PopCountBits(unsigned m) {
  int c = 0;
  while (m) {
    c += static_cast<int>(m & 1u);
    m >>= 1;
  }
  return c;
}

std::vector<GroupCandidate> EnumerateMaximalGroups(const std::vector<ObservedEdge>& pool,
                                                   double max_w, double max_h) {
  const int n = static_cast<int>(pool.size());
  std::vector<std::vector<int>> raw;
  // 枚举至多 2 竖直 + 2 水平的子集
  std::vector<int> verts, hors;
  for (int i = 0; i < n; ++i) {
    if (pool[i].seg.horizontal)
      hors.push_back(i);
    else
      verts.push_back(i);
  }

  auto try_push = [&](const std::vector<int>& idx) {
    if (idx.empty()) return;
    std::vector<ObservedEdge> edges;
    edges.reserve(idx.size());
    for (int i : idx) edges.push_back(pool[i]);
    if (!GroupSpatialGeometryOk(edges, max_w, max_h)) return;
    // 指派可行性（几何）
    auto tmp = edges;
    if (!AssignGroupWorkspaceEdges(tmp)) return;
    for (auto& existing : raw) {
      if (IndicesEqualSorted(existing, idx)) return;
    }
    raw.push_back(idx);
  };

  // 所有非空子集：|V|<=2, |H|<=2
  const int nv = static_cast<int>(verts.size());
  const int nh = static_cast<int>(hors.size());
  // 限制枚举规模，避免极端噪声下指数爆炸
  const int nv_use = std::min(nv, 8);
  const int nh_use = std::min(nh, 8);
  for (int vm = 1; vm < (1 << nv_use); ++vm) {
    if (PopCountBits(static_cast<unsigned>(vm)) > 2) continue;
    std::vector<int> vs;
    for (int i = 0; i < nv_use; ++i)
      if (vm & (1 << i)) vs.push_back(verts[i]);
    // 仅竖直
    try_push(vs);
    for (int hm = 1; hm < (1 << nh_use); ++hm) {
      if (PopCountBits(static_cast<unsigned>(hm)) > 2) continue;
      std::vector<int> idx = vs;
      for (int j = 0; j < nh_use; ++j)
        if (hm & (1 << j)) idx.push_back(hors[j]);
      try_push(idx);
    }
  }
  for (int hm = 1; hm < (1 << nh_use); ++hm) {
    if (PopCountBits(static_cast<unsigned>(hm)) > 2) continue;
    std::vector<int> hs;
    for (int j = 0; j < nh_use; ++j)
      if (hm & (1 << j)) hs.push_back(hors[j]);
    try_push(hs);
  }

  // 仅保留极大组（不被其它合法组真包含）
  std::vector<std::vector<int>> maximal;
  for (const auto& a : raw) {
    bool dominated = false;
    for (const auto& b : raw) {
      if (a.size() >= b.size()) continue;
      if (IsSubsetIndices(a, b)) {
        dominated = true;
        break;
      }
    }
    if (!dominated) maximal.push_back(a);
  }

  std::vector<GroupCandidate> out;
  for (const auto& idx : maximal) {
    GroupCandidate g;
    g.indices = idx;
    for (int i : idx) g.edges.push_back(pool[i]);
    out.push_back(std::move(g));
  }
  return out;
}

}  // namespace

ViewportCompletionResult CompleteViewportFrame(const ViewportCompletionInput& in) {
  if (!in.bgra || in.width <= 0 || in.height <= 0 || !in.thumbnail_roi.valid()) {
    return Fail(FailStatus::InvalidCapture, "invalid viewport input");
  }

  const wb::IntRect roi = in.thumbnail_roi.Clamp(in.width, in.height);
  if (!roi.valid()) return Fail(FailStatus::InsufficientViewportGeometry, "thumbnail roi empty");

  const int rw = roi.width();
  const int rh = roi.height();

  std::vector<uint8_t> red_raw(static_cast<size_t>(rw) * rh, 0);
  int red_count = 0;
  for (int y = roi.top; y < roi.bottom; ++y) {
    for (int x = roi.left; x < roi.right; ++x) {
      const uint8_t* p =
          in.bgra + static_cast<size_t>(y) * in.stride + static_cast<size_t>(x) * 4;
      if (IsNavigatorRedPixel(p)) {
        red_raw[static_cast<size_t>(y - roi.top) * rw + (x - roi.left)] = 1;
        ++red_count;
      }
    }
  }
  if (red_count < kMinRawRedPixels) {
    return Fail(FailStatus::InsufficientViewportGeometry, "no red edge pixels");
  }

  std::vector<uint8_t> red_det;
  DilateMask3x3(red_raw, rw, rh, red_det);

  std::vector<int> col_det, row_det, col_raw, row_raw;
  ProjectMask(red_det, rw, rh, col_det, row_det);
  ProjectMask(red_raw, rw, rh, col_raw, row_raw);

  // A. 观测全部轴向红段（全部投影簇，供多组枚举）
  auto vpeaks = AllClusterPeaks(FindProjectionPeaks(col_det, rh), col_det);
  auto hpeaks = AllClusterPeaks(FindProjectionPeaks(row_det, rw), row_det);

  std::vector<ObservedEdge> pool;
  for (int px : vpeaks) {
    ObservedEdge e;
    if (MeasureVerticalEdge(red_det, red_raw, rw, rh, px, e)) pool.push_back(e);
  }
  for (int py : hpeaks) {
    ObservedEdge e;
    if (MeasureHorizontalEdge(red_det, red_raw, rw, rh, py, e)) pool.push_back(e);
  }
  if (pool.empty()) {
    return Fail(FailStatus::InsufficientViewportGeometry, "no oriented red edges");
  }

  wb::IntRect canvas = in.navigator_canvas_bounds.Clamp(in.width, in.height);
  if (!canvas.valid()) canvas = roi;
  const bool has_chrome =
      canvas.left > roi.left || canvas.top > roi.top || canvas.right < roi.right ||
      canvas.bottom < roi.bottom;
  // 导航器画布在 ROI 局部坐标下的边长上限（平行对边间距）
  const double nav_w_local =
      static_cast<double>(std::max(1, std::min(canvas.right, roi.right) - std::max(canvas.left, roi.left)));
  const double nav_h_local =
      static_cast<double>(std::max(1, std::min(canvas.bottom, roi.bottom) - std::max(canvas.top, roi.top)));

  // B. 枚举合法极大 RedFrameEdgeGroup
  auto groups = EnumerateMaximalGroups(pool, nav_w_local, nav_h_local);
  if (groups.empty()) {
    return Fail(FailStatus::AmbiguousViewportGeometry, "no valid red frame edge group");
  }

  const BgColor bg = EstimateNavigatorBackground(in, roi);
  const auto& wcr = in.workspace_canvas_relation;
  const double W_nav = nav_w_local * static_cast<double>(wcr.visible_canvas_fraction_x);
  const double H_nav = nav_h_local * static_cast<double>(wcr.visible_canvas_fraction_y);
  const bool theory_available =
      wcr.visible_canvas_fraction_x > 1e-4f && wcr.visible_canvas_fraction_y > 1e-4f;

  // C+D. 组内直角 → 完整边 → pattern 补全
  std::vector<GroupCandidate*> survivors;
  for (auto& g : groups) {
    if (!CompleteGroupPattern(g, in, roi, rw, rh)) continue;
    // 背景粘着：用补全后矩形框定义外侧
    const double left = g.frame.origin_top_left_displayed.x - roi.left - 0.5;
    const double top = g.frame.origin_top_left_displayed.y - roi.top - 0.5;
    const double right = left + g.frame.axis_x_displayed.x;
    const double bottom = top + g.frame.axis_y_displayed.y;
    g.touches_background = GroupTouchesBackground(g.edges, left, right, top, bottom, in, roi,
                                                  canvas, bg, has_chrome);
    survivors.push_back(&g);
  }

  // E. 多组硬消歧（§6）——纯 if-else，禁止打分
  if (survivors.empty()) {
    return Fail(FailStatus::AmbiguousViewportGeometry, "no group completed via pattern");
  }

  GroupCandidate* target = nullptr;
  if (survivors.size() == 1) {
    target = survivors[0];
  } else {
    std::vector<GroupCandidate*> B;
    for (auto* g : survivors)
      if (g->touches_background) B.push_back(g);

    if (B.size() == 1) {
      target = B[0];
    } else if (B.size() >= 2) {
      return Fail(FailStatus::AmbiguousViewportGeometry,
                  "multiple groups touch navigator background");
    } else {
      // |B|==0 → 理论尺寸分支
      if (!theory_available) {
        return Fail(FailStatus::AmbiguousViewportGeometry,
                    "theory navigator size unavailable for disambiguation");
      }
      std::vector<GroupCandidate*> M;
      for (auto* g : survivors) {
        if (SizeMatchesTheory(g->frame.width, g->frame.height, W_nav, H_nav)) M.push_back(g);
      }
      if (M.size() == 1) {
        target = M[0];
      } else {
        return Fail(FailStatus::AmbiguousViewportGeometry,
                    "theory size match not unique");
      }
    }
  }

  // F. 仅发布目标组
  ViewportCompletionResult r;
  r.status = FailStatus::Ok;
  r.frame = target->frame;
  std::snprintf(r.message, sizeof(r.message), "ok pattern=%d complete=%d segs=%d groups=%d",
                r.frame.completion_strategy, target->complete_count,
                static_cast<int>(target->edges.size()), static_cast<int>(survivors.size()));
  return r;
}

}  // namespace sct
