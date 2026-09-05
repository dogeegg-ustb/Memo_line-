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
constexpr double kParallelDotMin = 0.92;   // |dir·dir|：平行
constexpr double kOrthogonalDotMax = 0.35; // |dir·dir|：垂直（相对正交，非贴轴）

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
  double ux = 1.0;  // unit tangent
  double uy = 0.0;
  double nx = 0.0;  // unit normal (−uy, ux)
  double ny = 1.0;
  double coord = -1.0;  // signed offset along normal: n·p
  int family = 0;       // 0 = along primary angle, 1 = along primary+90°
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

inline double Dot2(double ax, double ay, double bx, double by) { return ax * bx + ay * by; }

inline bool DirsParallel(double ux0, double uy0, double ux1, double uy1) {
  return std::abs(Dot2(ux0, uy0, ux1, uy1)) >= kParallelDotMin;
}

inline bool DirsOrthogonal(double ux0, double uy0, double ux1, double uy1) {
  return std::abs(Dot2(ux0, uy0, ux1, uy1)) <= kOrthogonalDotMax;
}

inline bool EdgeIsFamily0(const ObservedEdge& e) { return e.family == 0; }

inline double EdgePosX(const ObservedEdge& e) { return 0.5 * (e.seg.x0 + e.seg.x1); }
inline double EdgePosY(const ObservedEdge& e) { return 0.5 * (e.seg.y0 + e.seg.y1); }

// 结构张量估计红掩膜主边缘朝向（边方向，非梯度方向）。
double EstimateDominantEdgeAngle(const std::vector<uint8_t>& det, int w, int h) {
  double ixx = 0, iyy = 0, ixy = 0;
  int n = 0;
  for (int y = 1; y + 1 < h; ++y) {
    for (int x = 1; x + 1 < w; ++x) {
      if (!det[static_cast<size_t>(y) * w + x]) continue;
      const int gx = static_cast<int>(det[static_cast<size_t>(y) * w + (x + 1)]) -
                     static_cast<int>(det[static_cast<size_t>(y) * w + (x - 1)]);
      const int gy = static_cast<int>(det[static_cast<size_t>((y + 1)) * w + x]) -
                     static_cast<int>(det[static_cast<size_t>((y - 1)) * w + x]);
      if (gx == 0 && gy == 0) continue;
      ixx += static_cast<double>(gx) * gx;
      iyy += static_cast<double>(gy) * gy;
      ixy += static_cast<double>(gx) * gy;
      ++n;
    }
  }
  if (n < 8) return 0.0;
  // 主梯度角；边方向与之正交
  const double grad = 0.5 * std::atan2(2.0 * ixy, ixx - iyy);
  double edge = grad + 1.5707963267948966;
  // 归一化到 (-π/2, π/2]，使 family0 更偏「水平族」便于与旧路径兼容
  while (edge <= -1.5707963267948966) edge += 3.141592653589793;
  while (edge > 1.5707963267948966) edge -= 3.141592653589793;
  return edge;
}

bool MeasureOrientedEdge(const std::vector<uint8_t>& det, const std::vector<uint8_t>& raw, int w,
                         int h, double ux, double uy, double nx, double ny, double peak_offset,
                         int family, ObservedEdge& out) {
  const double inv_len = 1.0;  // ux,uy already unit
  (void)inv_len;
  double t_lo = 1e100, t_hi = -1e100;
  int red_on = 0;
  int span_bins = 0;

  // 沿切向扫描：用包围盒对角投影范围
  const double corners_t[4] = {
      Dot2(0, 0, ux, uy), Dot2(w - 1, 0, ux, uy), Dot2(0, h - 1, ux, uy),
      Dot2(w - 1, h - 1, ux, uy)};
  double t_min = corners_t[0], t_max = corners_t[0];
  for (int i = 1; i < 4; ++i) {
    t_min = std::min(t_min, corners_t[i]);
    t_max = std::max(t_max, corners_t[i]);
  }
  const int t0 = static_cast<int>(std::floor(t_min));
  const int t1 = static_cast<int>(std::ceil(t_max));
  for (int ti = t0; ti <= t1; ++ti) {
    const double cx = nx * peak_offset + ux * static_cast<double>(ti);
    const double cy = ny * peak_offset + uy * static_cast<double>(ti);
    // 带宽内任一点有红则计命中
    bool hit = false;
    for (int d = -1; d <= 1 && !hit; ++d) {
      const int x = static_cast<int>(std::lround(cx + nx * d));
      const int y = static_cast<int>(std::lround(cy + ny * d));
      if (MaskAt(det, w, h, x, y)) hit = true;
    }
    if (hit) {
      t_lo = std::min(t_lo, static_cast<double>(ti));
      t_hi = std::max(t_hi, static_cast<double>(ti));
      ++red_on;
    }
  }
  if (!(t_hi >= t_lo) || (t_hi - t_lo + 1.0) < kMinSegmentSpan) return false;
  const double span = t_hi - t_lo + 1.0;
  const float support = static_cast<float>(red_on) / static_cast<float>(span);
  if (support < kMinEdgeSupport) return false;

  // raw CoM 精修 offset，并收集端点
  double sum_o = 0, wt = 0;
  double sum_t = 0;
  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      if (!MaskAt(raw, w, h, x, y)) continue;
      const double o = Dot2(x, y, nx, ny);
      if (std::abs(o - peak_offset) > kPeakRefineRadius + 0.5) continue;
      const double t = Dot2(x, y, ux, uy);
      if (t < t_lo - 1.0 || t > t_hi + 1.0) continue;
      sum_o += o;
      sum_t += t;
      wt += 1;
    }
  }
  const double refined_o = wt > 0 ? sum_o / wt : peak_offset;
  const double x0 = nx * refined_o + ux * t_lo;
  const double y0 = ny * refined_o + uy * t_lo;
  const double x1 = nx * refined_o + ux * t_hi;
  const double y1 = ny * refined_o + uy * t_hi;

  out.ux = ux;
  out.uy = uy;
  out.nx = nx;
  out.ny = ny;
  out.coord = refined_o;
  out.family = family;
  out.seg.x0 = x0;
  out.seg.y0 = y0;
  out.seg.x1 = x1;
  out.seg.y1 = y1;
  out.seg.horizontal = (std::abs(ux) >= std::abs(uy));
  out.seg.support = support;
  out.seg.corner_at_start = -1;
  out.seg.corner_at_end = -1;
  return true;
}

void ObserveEdgesAtAngle(const std::vector<uint8_t>& det, const std::vector<uint8_t>& raw, int w,
                         int h, double angle_rad, int family, std::vector<ObservedEdge>& pool) {
  const double ux = std::cos(angle_rad);
  const double uy = std::sin(angle_rad);
  const double nx = -uy;
  const double ny = ux;

  // 连续 offset → 离散直方图
  double o_min = 1e100, o_max = -1e100;
  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      if (!det[static_cast<size_t>(y) * w + x]) continue;
      const double o = Dot2(x, y, nx, ny);
      o_min = std::min(o_min, o);
      o_max = std::max(o_max, o);
    }
  }
  if (!(o_max >= o_min)) return;
  const int base = static_cast<int>(std::floor(o_min));
  const int bins = static_cast<int>(std::ceil(o_max) - base) + 1;
  if (bins < 3 || bins > 4096) return;

  std::vector<int> hist(static_cast<size_t>(bins), 0);
  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      if (!det[static_cast<size_t>(y) * w + x]) continue;
      const int bi = static_cast<int>(std::lround(Dot2(x, y, nx, ny))) - base;
      if (bi >= 0 && bi < bins) ++hist[static_cast<size_t>(bi)];
    }
  }

  // min_run ≈ 沿切向的典型跨度（用图像对角线比例）
  const int min_run = std::max(w, h);
  auto peaks = AllClusterPeaks(FindProjectionPeaks(hist, min_run), hist);
  for (int pi : peaks) {
    const double peak_o = static_cast<double>(base + pi);
    ObservedEdge e;
    if (MeasureOrientedEdge(det, raw, w, h, ux, uy, nx, ny, peak_o, family, e)) {
      // 去重：同族近邻 offset
      bool dup = false;
      for (const auto& ex : pool) {
        if (ex.family != family) continue;
        if (std::abs(ex.coord - e.coord) <= kPeakMergeDist) {
          dup = true;
          break;
        }
      }
      if (!dup) pool.push_back(e);
    }
  }
}

void ObserveAllOrientedEdges(const std::vector<uint8_t>& det, const std::vector<uint8_t>& raw,
                             int w, int h, float display_rot_deg, float display_rot_conf,
                             std::vector<ObservedEdge>& pool) {
  pool.clear();
  const bool have_display_rot = display_rot_conf >= 0.2f && std::isfinite(display_rot_deg);
  double primary = 0.0;
  if (have_display_rot) {
    // 显示角 = 画布 X；family0 沿 X（T/B），family1 沿 Y（L/R）——不折到水平
    primary = display_rot_deg * 0.017453292519943295;
  } else {
    primary = EstimateDominantEdgeAngle(det, w, h);
  }
  while (primary <= -1.5707963267948966) primary += 3.141592653589793;
  while (primary > 1.5707963267948966) primary -= 3.141592653589793;
  if (!have_display_rot && std::abs(primary) > 0.7853981633974483) {
    // 无显示角时：family0 取更接近水平的一族，避免竖边占优把 L/R 与 T/B 对调
    primary = primary > 0 ? primary - 1.5707963267948966 : primary + 1.5707963267948966;
  }

  ObserveEdgesAtAngle(det, raw, w, h, primary, 0, pool);
  ObserveEdgesAtAngle(det, raw, w, h, primary + 1.5707963267948966, 1, pool);

  if (pool.empty() || (!have_display_rot && std::abs(std::sin(2.0 * primary)) > 0.25 &&
                       pool.size() < 2)) {
    ObserveEdgesAtAngle(det, raw, w, h, 0.0, 0, pool);
    ObserveEdgesAtAngle(det, raw, w, h, 1.5707963267948966, 1, pool);
  }
}

double EdgeLen(const ObservedEdge& e) {
  return std::hypot(e.seg.x1 - e.seg.x0, e.seg.y1 - e.seg.y0);
}

bool LineIntersection(const ObservedEdge& a, const ObservedEdge& b, double& ix, double& iy) {
  // a: n_a·p = o_a, b: n_b·p = o_b
  const double det = a.nx * b.ny - a.ny * b.nx;
  if (std::abs(det) < 1e-8) return false;
  ix = (a.coord * b.ny - b.coord * a.ny) / det;
  iy = (a.nx * b.coord - b.nx * a.coord) / det;
  return true;
}

// 正交邻接：相对垂直 + 端点/交点落入容差（不要求贴屏幕轴）。
bool OrthogonalAdjacent(const ObservedEdge& a, const ObservedEdge& b) {
  if (!DirsOrthogonal(a.ux, a.uy, b.ux, b.uy)) return false;
  const double tol = kGroupCornerTolPx;
  double ix = 0, iy = 0;
  if (!LineIntersection(a, b, ix, iy)) return false;

  auto on_seg = [&](const ObservedEdge& e, double x, double y) {
    const double t = Dot2(x - e.seg.x0, y - e.seg.y0, e.ux, e.uy);
    const double len = EdgeLen(e);
    return t >= -tol && t <= len + tol &&
           std::abs(Dot2(x, y, e.nx, e.ny) - e.coord) <= tol;
  };
  if (on_seg(a, ix, iy) && on_seg(b, ix, iy)) return true;

  const double ends_a[2][2] = {{a.seg.x0, a.seg.y0}, {a.seg.x1, a.seg.y1}};
  const double ends_b[2][2] = {{b.seg.x0, b.seg.y0}, {b.seg.x1, b.seg.y1}};
  for (int i = 0; i < 2; ++i)
    for (int j = 0; j < 2; ++j)
      if (std::hypot(ends_a[i][0] - ends_b[j][0], ends_a[i][1] - ends_b[j][1]) <= tol)
        return true;
  return false;
}

bool ParallelPairOk(const ObservedEdge& a, const ObservedEdge& b, double max_side) {
  if (!DirsParallel(a.ux, a.uy, b.ux, b.uy)) return false;
  const double spacing = std::abs(a.coord - b.coord);
  if (spacing < kMinViewportSidePx) return false;
  if (spacing > max_side + kGroupCornerTolPx) return false;

  const double len_a = EdgeLen(a);
  const double len_b = EdgeLen(b);
  if (len_a < 1.0 || len_b < 1.0) return false;
  if (std::min(len_a, len_b) / std::max(len_a, len_b) < 0.5) return false;

  // 沿共同切向的投影重叠
  const double ux = a.ux, uy = a.uy;
  const double a0 = Dot2(a.seg.x0, a.seg.y0, ux, uy);
  const double a1 = Dot2(a.seg.x1, a.seg.y1, ux, uy);
  const double b0 = Dot2(b.seg.x0, b.seg.y0, ux, uy);
  const double b1 = Dot2(b.seg.x1, b.seg.y1, ux, uy);
  const double lo = std::max(std::min(a0, a1), std::min(b0, b1));
  const double hi = std::min(std::max(a0, a1), std::max(b0, b1));
  const double overlap = hi - lo;
  const double need =
      std::max(static_cast<double>(kParallelOverlapMinPx),
               kParallelOverlapRatio * std::max(len_a, len_b));
  return overlap >= need;
}

// 组内直角：端点附近与组内相对正交边相交/贴合。
bool OrthogonalMeetAtPoint(const ObservedEdge& a, double px, double py, const ObservedEdge& b) {
  if (!DirsOrthogonal(a.ux, a.uy, b.ux, b.uy)) return false;
  const double tol = kGroupCornerTolPx;
  double ix = 0, iy = 0;
  if (!LineIntersection(a, b, ix, iy)) return false;
  if (std::hypot(ix - px, iy - py) > tol) return false;
  auto near_seg = [&](const ObservedEdge& e) {
    const double t = Dot2(ix - e.seg.x0, iy - e.seg.y0, e.ux, e.uy);
    const double len = EdgeLen(e);
    return t >= -tol && t <= len + tol;
  };
  return near_seg(a) && near_seg(b);
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

// 遗留：组内局部排序定 L/T/R/B（仅无切割边时回退；禁止作为切割对应主路径）。
bool AssignGroupWorkspaceEdgesLegacy(std::vector<ObservedEdge>& edges) {
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
            [](const ObservedEdge* a, const ObservedEdge* b) {
              return EdgePosX(*a) < EdgePosX(*b);
            });
  std::sort(hors.begin(), hors.end(),
            [](const ObservedEdge* a, const ObservedEdge* b) {
              return EdgePosY(*a) < EdgePosY(*b);
            });

  if (verts.size() == 2) {
    if (EdgePosX(*verts[1]) - EdgePosX(*verts[0]) < kMinViewportSidePx) return false;
    verts[0]->workspace_edge = kEdgeL;
    verts[1]->workspace_edge = kEdgeR;
  } else if (verts.size() == 1) {
    verts[0]->workspace_edge = kEdgeL;
  }
  if (hors.size() == 2) {
    if (EdgePosY(*hors[1]) - EdgePosY(*hors[0]) < kMinViewportSidePx) return false;
    hors[0]->workspace_edge = kEdgeT;
    hors[1]->workspace_edge = kEdgeB;
  } else if (hors.size() == 1) {
    hors[0]->workspace_edge = kEdgeT;
  }
  return true;
}

// 枚举可行性：仅检查平行对边间距，不写入角色。
bool GroupEdgeCardinalityOk(const std::vector<ObservedEdge>& edges) {
  int n_v = 0, n_h = 0;
  double v0 = 0, v1 = 0, h0 = 0, h1 = 0;
  bool have_v0 = false, have_h0 = false;
  for (const auto& e : edges) {
    if (e.seg.horizontal) {
      if (n_h == 0) {
        h0 = e.coord;
        have_h0 = true;
      } else if (n_h == 1) {
        h1 = e.coord;
      }
      ++n_h;
    } else {
      if (n_v == 0) {
        v0 = e.coord;
        have_v0 = true;
      } else if (n_v == 1) {
        v1 = e.coord;
      }
      ++n_v;
    }
  }
  if (n_v > 2 || n_h > 2) return false;
  if (n_v == 2 && std::abs(v1 - v0) < kMinViewportSidePx) return false;
  if (n_h == 2 && std::abs(h1 - h0) < kMinViewportSidePx) return false;
  (void)have_v0;
  (void)have_h0;
  return true;
}

constexpr int kEdgeOrder[4] = {kEdgeL, kEdgeT, kEdgeR, kEdgeB};

int EdgeBitIndex(int bit) {
  for (int i = 0; i < 4; ++i)
    if (kEdgeOrder[i] == bit) return i;
  return -1;
}

// 显示顺时针 q*90° 时：屏幕侧 S 上出现的画布语义边 = order[(idx(S) - q) mod 4]。
int SemanticRoleAtScreenSide(int screen_side_bit, int quarters_cw) {
  const int idx = EdgeBitIndex(screen_side_bit);
  if (idx < 0) return 0;
  const int q = ((quarters_cw % 4) + 4) % 4;
  return kEdgeOrder[(idx - q + 4) % 4];
}

// 语义边 R 在显示顺时针 q*90° 后落在哪条屏幕侧（挑切割红边用；标签仍用 R 本身）。
int ScreenSideForSemanticRole(int semantic_role_bit, int quarters_cw) {
  const int idx = EdgeBitIndex(semantic_role_bit);
  if (idx < 0) return 0;
  const int q = ((quarters_cw % 4) + 4) % 4;
  return kEdgeOrder[(idx + q) % 4];
}

int OppositeEdge(int bit) {
  switch (bit) {
    case kEdgeL:
      return kEdgeR;
    case kEdgeR:
      return kEdgeL;
    case kEdgeT:
      return kEdgeB;
    case kEdgeB:
      return kEdgeT;
    default:
      return 0;
  }
}

int NearestQuarter(double degrees) {
  double n = std::fmod(degrees, 360.0);
  if (n < 0) n += 360.0;
  int q = static_cast<int>(std::lround(n / 90.0)) % 4;
  if (q < 0) q += 4;
  return q;
}

// 已知显示旋转类时：按画布轴族（family1=L/R，family0=T/B）映射语义角色。
bool AssignEdgesByDisplayRotation(std::vector<ObservedEdge>& edges, int quarters_cw, int rw,
                                  int rh) {
  if (quarters_cw < 0) return false;
  std::vector<ObservedEdge*> verts, hors;
  for (auto& e : edges) {
    e.workspace_edge = 0;
    if (EdgeIsFamily0(e))
      hors.push_back(&e);
    else
      verts.push_back(&e);
  }
  if (verts.size() > 2 || hors.size() > 2) return false;
  auto mid_x = [](const ObservedEdge* e) { return 0.5 * (e->seg.x0 + e->seg.x1); };
  auto mid_y = [](const ObservedEdge* e) { return 0.5 * (e->seg.y0 + e->seg.y1); };
  std::sort(verts.begin(), verts.end(),
            [&](const ObservedEdge* a, const ObservedEdge* b) { return mid_x(a) < mid_x(b); });
  std::sort(hors.begin(), hors.end(),
            [&](const ObservedEdge* a, const ObservedEdge* b) { return mid_y(a) < mid_y(b); });

  if (verts.size() == 2) {
    if (std::abs(verts[1]->coord - verts[0]->coord) < kMinViewportSidePx) return false;
    verts[0]->workspace_edge = SemanticRoleAtScreenSide(kEdgeL, quarters_cw);
    verts[1]->workspace_edge = SemanticRoleAtScreenSide(kEdgeR, quarters_cw);
  } else if (verts.size() == 1) {
    const int screen_side = mid_x(verts[0]) < rw * 0.5 ? kEdgeL : kEdgeR;
    verts[0]->workspace_edge = SemanticRoleAtScreenSide(screen_side, quarters_cw);
  }

  if (hors.size() == 2) {
    if (std::abs(hors[1]->coord - hors[0]->coord) < kMinViewportSidePx) return false;
    hors[0]->workspace_edge = SemanticRoleAtScreenSide(kEdgeT, quarters_cw);
    hors[1]->workspace_edge = SemanticRoleAtScreenSide(kEdgeB, quarters_cw);
  } else if (hors.size() == 1) {
    const int screen_side = mid_y(hors[0]) < rh * 0.5 ? kEdgeT : kEdgeB;
    hors[0]->workspace_edge = SemanticRoleAtScreenSide(screen_side, quarters_cw);
  }

  if (verts.empty() && hors.empty()) return false;
  return true;
}

int ResolveDisplayQuarter(float rotation_degrees, float rotation_confidence) {
  if (rotation_confidence < 0.2f) return -1;
  return NearestQuarter(rotation_degrees);
}

// 红边是否与缩略图显示画布相交（局部 ROI 坐标；支持任意朝向线段）。
bool EdgeIntersectsCanvasLocal(const ObservedEdge& e, const wb::IntRect& canvas_local) {
  if (!canvas_local.valid()) return false;
  const double x0 = e.seg.x0, y0 = e.seg.y0, x1 = e.seg.x1, y1 = e.seg.y1;
  auto inside = [&](double x, double y) {
    return x >= canvas_local.left - 0.5 && x <= canvas_local.right - 0.5 &&
           y >= canvas_local.top - 0.5 && y <= canvas_local.bottom - 0.5;
  };
  if (inside(x0, y0) || inside(x1, y1)) return true;
  for (int i = 1; i < 8; ++i) {
    const double t = static_cast<double>(i) / 8.0;
    if (inside(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t)) return true;
  }
  // 轴对齐快路径：整段投影与画布重叠
  const double minx = std::min(x0, x1), maxx = std::max(x0, x1);
  const double miny = std::min(y0, y1), maxy = std::max(y0, y1);
  const double ox0 = std::max(minx, static_cast<double>(canvas_local.left));
  const double ox1 = std::min(maxx, static_cast<double>(canvas_local.right));
  const double oy0 = std::max(miny, static_cast<double>(canvas_local.top));
  const double oy1 = std::min(maxy, static_cast<double>(canvas_local.bottom));
  return (ox1 - ox0 > 0.5) && (oy1 - oy0 > 0.5);
}

enum class CropAssignResult { Applied, NotApplicable, Ambiguous, Failed };

// 在切割边集合中，按 0° 语义角色选取几何极值边（T=最上横边…），避免 interior_ok 与旋转后几何冲突。
ObservedEdge* PickCuttingEdgeForSemanticRole(const std::vector<ObservedEdge*>& cutting,
                                              int sem_role) {
  std::vector<ObservedEdge*> pool;
  for (ObservedEdge* ep : cutting) {
    if (ep->workspace_edge != 0) continue;
    if (sem_role == kEdgeL || sem_role == kEdgeR) {
      if (!ep->seg.horizontal) pool.push_back(ep);
    } else if (sem_role == kEdgeT || sem_role == kEdgeB) {
      if (ep->seg.horizontal) pool.push_back(ep);
    }
  }
  if (pool.empty()) return nullptr;
  if (pool.size() == 1) return pool[0];

  ObservedEdge* best = nullptr;
  for (ObservedEdge* ep : pool) {
    if (!best) {
      best = ep;
      continue;
    }
    const bool pick_min = (sem_role == kEdgeL || sem_role == kEdgeT);
    const double av = (sem_role == kEdgeL || sem_role == kEdgeR) ? EdgePosX(*ep) : EdgePosY(*ep);
    const double bv =
        (sem_role == kEdgeL || sem_role == kEdgeR) ? EdgePosX(*best) : EdgePosY(*best);
    if (pick_min ? av < bv : av > bv) best = ep;
  }
  return best;
}

// 切割对应主路径：C_w ↔ C_v；角仅定旋转类；禁止 ROI 中线 / 纯组内排序定案。
CropAssignResult AssignEdgesByCropCorrespondence(std::vector<ObservedEdge>& edges,
                                                 int canvas_crop_sides,
                                                 const wb::IntRect& canvas_local,
                                                 float rotation_degrees,
                                                 float rotation_confidence) {
  if (canvas_crop_sides == 0) return CropAssignResult::NotApplicable;

  std::vector<ObservedEdge*> cutting;
  for (auto& e : edges) {
    e.workspace_edge = 0;
    if (EdgeIntersectsCanvasLocal(e, canvas_local)) cutting.push_back(&e);
  }
  if (cutting.empty()) return CropAssignResult::Failed;

  std::vector<int> crop_bits;
  for (int bit : {kEdgeL, kEdgeT, kEdgeR, kEdgeB}) {
    if (canvas_crop_sides & bit) crop_bits.push_back(bit);
  }
  if (crop_bits.empty()) return CropAssignResult::NotApplicable;

  auto try_quarters = [&](int q, std::vector<ObservedEdge>& work, bool* saw_ambiguous) -> bool {
    if (saw_ambiguous) *saw_ambiguous = false;
    for (auto& e : work) e.workspace_edge = 0;

    std::vector<ObservedEdge*> cutting_local;
    for (auto& e : work) {
      if (EdgeIntersectsCanvasLocal(e, canvas_local)) cutting_local.push_back(&e);
    }
    if (cutting_local.empty()) return false;

    // 每个 C_w 须在 C_v 上找到唯一切割边。
    // 角只决定「去哪条屏侧挑边」；标签 MUST 仍是工作区切割边 cb（禁止再写旋转后角色 → 负负得正）。
    std::vector<ObservedEdge*> crop_matched;
    crop_matched.reserve(crop_bits.size());
    for (int cb : crop_bits) {
      const int screen_slot = ScreenSideForSemanticRole(cb, q);
      ObservedEdge* match = PickCuttingEdgeForSemanticRole(cutting_local, screen_slot);
      if (!match) return false;
      if (match->workspace_edge != 0) {
        if (saw_ambiguous) *saw_ambiguous = true;
        return false;
      }
      match->workspace_edge = cb;
      crop_matched.push_back(match);
    }

    // 多切割边同屏侧冲突：仅校验本次 C_w↔C_v 直接指派，不含传播边。
    const double ccx = 0.5 * (canvas_local.left + canvas_local.right);
    const double ccy = 0.5 * (canvas_local.top + canvas_local.bottom);
    const double side_tol = static_cast<double>(kGroupCornerTolPx);
    ObservedEdge* by_crop_side[4] = {nullptr, nullptr, nullptr, nullptr};
    for (size_t i = 0; i < crop_bits.size(); ++i) {
      const int idx = EdgeBitIndex(crop_bits[i]);
      if (idx >= 0 && idx < 4) by_crop_side[idx] = crop_matched[i];
    }
    if (by_crop_side[0] && by_crop_side[2]) {
      if (EdgePosX(*by_crop_side[0]) > ccx + side_tol &&
          EdgePosX(*by_crop_side[2]) > ccx + side_tol)
        return false;
      if (EdgePosX(*by_crop_side[0]) < ccx - side_tol &&
          EdgePosX(*by_crop_side[2]) < ccx - side_tol)
        return false;
    }
    if (by_crop_side[1] && by_crop_side[3]) {
      if (EdgePosY(*by_crop_side[1]) > ccy + side_tol &&
          EdgePosY(*by_crop_side[3]) > ccy + side_tol)
        return false;
      if (EdgePosY(*by_crop_side[1]) < ccy - side_tol &&
          EdgePosY(*by_crop_side[3]) < ccy - side_tol)
        return false;
    }

    // §5.3 传播：由已赋值切割边推对边；未赋值的平行对按旋转类套屏侧语义
    std::vector<ObservedEdge*> all_v, all_h;
    for (auto& e : work) {
      if (e.seg.horizontal)
        all_h.push_back(&e);
      else
        all_v.push_back(&e);
    }
    std::sort(all_v.begin(), all_v.end(),
              [](const ObservedEdge* a, const ObservedEdge* b) {
                return EdgePosX(*a) < EdgePosX(*b);
              });
    std::sort(all_h.begin(), all_h.end(),
              [](const ObservedEdge* a, const ObservedEdge* b) {
                return EdgePosY(*a) < EdgePosY(*b);
              });

    auto propagate_pair = [](std::vector<ObservedEdge*>& pair) -> bool {
      if (pair.size() != 2) return true;
      ObservedEdge* a = pair[0];
      ObservedEdge* b = pair[1];
      if (a->workspace_edge && b->workspace_edge) {
        return b->workspace_edge == OppositeEdge(a->workspace_edge);
      }
      if (a->workspace_edge && !b->workspace_edge) {
        b->workspace_edge = OppositeEdge(a->workspace_edge);
        return b->workspace_edge != 0;
      }
      if (b->workspace_edge && !a->workspace_edge) {
        a->workspace_edge = OppositeEdge(b->workspace_edge);
        return a->workspace_edge != 0;
      }
      return true;  // 均未赋值：留给旋转类套用
    };
    if (!propagate_pair(all_v)) return false;
    if (!propagate_pair(all_h)) return false;

    if (all_v.size() == 2 && all_v[0]->workspace_edge == 0 && all_v[1]->workspace_edge == 0) {
      const int lo = SemanticRoleAtScreenSide(kEdgeL, q);
      all_v[0]->workspace_edge = lo;
      all_v[1]->workspace_edge = OppositeEdge(lo);
    }
    if (all_h.size() == 2 && all_h[0]->workspace_edge == 0 && all_h[1]->workspace_edge == 0) {
      const int lo = SemanticRoleAtScreenSide(kEdgeT, q);
      all_h[0]->workspace_edge = lo;
      all_h[1]->workspace_edge = OppositeEdge(lo);
    }

    return true;
  };

  std::vector<int> candidates;
  if (rotation_confidence >= 0.2f) {
    candidates.push_back(NearestQuarter(rotation_degrees));
  } else {
    // 角不可用：离散 0/90/180/270 候选；多解则歧义
    for (int q = 0; q < 4; ++q) candidates.push_back(q);
  }

  int success_q = -1;
  std::vector<ObservedEdge> best;
  bool any_role_ambiguous = false;
  for (int q : candidates) {
    auto trial = edges;
    bool q_ambiguous = false;
    if (!try_quarters(q, trial, &q_ambiguous)) {
      if (q_ambiguous) any_role_ambiguous = true;
      continue;
    }
    if (success_q >= 0 && success_q != q) {
      return CropAssignResult::Ambiguous;
    }
    if (success_q < 0) {
      success_q = q;
      best = std::move(trial);
    }
  }

  if (success_q < 0 && any_role_ambiguous) return CropAssignResult::Ambiguous;
  if (success_q < 0) return CropAssignResult::Failed;
  edges = std::move(best);
  return CropAssignResult::Applied;
}

ObservedEdge* FindEdge(std::vector<ObservedEdge>& edges, int mask) {
  for (auto& e : edges)
    if (e.workspace_edge == mask) return &e;
  return nullptr;
}

bool GroupSpatialGeometryOk(const std::vector<ObservedEdge>& edges, double max_w, double max_h,
                            const wb::IntRect& canvas_local) {
  if (edges.empty() || edges.size() > 4) return false;
  int n_v = 0, n_h = 0;
  for (const auto& e : edges) {
    if (e.seg.horizontal)
      ++n_h;
    else
      ++n_v;
  }
  if (n_v > 2 || n_h > 2) return false;

  // 平行对边：仅当两条均为切割边时才校验间距（非切割外框边不得与切割边拼成平行对）
  std::vector<const ObservedEdge*> verts, hors;
  for (const auto& e : edges) {
    if (e.seg.horizontal)
      hors.push_back(&e);
    else
      verts.push_back(&e);
  }
  if (verts.size() == 2) {
    const bool a_cut = EdgeIntersectsCanvasLocal(*verts[0], canvas_local);
    const bool b_cut = EdgeIntersectsCanvasLocal(*verts[1], canvas_local);
    if (a_cut && b_cut && !ParallelPairOk(*verts[0], *verts[1], max_w)) return false;
  }
  if (hors.size() == 2) {
    const bool a_cut = EdgeIntersectsCanvasLocal(*hors[0], canvas_local);
    const bool b_cut = EdgeIntersectsCanvasLocal(*hors[1], canvas_local);
    if (a_cut && b_cut && !ParallelPairOk(*hors[0], *hors[1], max_h)) return false;
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

  // 外侧 = 沿边法向朝框外；框心取当前假设矩形中心（轴对齐近似，旋转时用边中点相对）
  const double cx = 0.5 * (left + right);
  const double cy = 0.5 * (top + bottom);
  const double mx = 0.5 * (e.seg.x0 + e.seg.x1);
  const double my = 0.5 * (e.seg.y0 + e.seg.y1);
  double onx = e.nx, ony = e.ny;
  if ((mx - cx) * onx + (my - cy) * ony < 0) {
    onx = -onx;
    ony = -ony;
  }
  const double len = EdgeLen(e);
  const int steps = std::max(4, static_cast<int>(len / 4.0));
  for (int i = 0; i <= steps; ++i) {
    const double t = static_cast<double>(i) / static_cast<double>(steps);
    const double px = e.seg.x0 + (e.seg.x1 - e.seg.x0) * t;
    const double py = e.seg.y0 + (e.seg.y1 - e.seg.y0) * t;
    for (int d = 1; d <= kBgOutwardProbePx; ++d) {
      sample(static_cast<int>(std::lround(px + onx * d)),
             static_cast<int>(std::lround(py + ony * d)));
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
  bool used_crop_correspondence = false;
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
void ExportGroupRedEdges(NavigatorViewportFrame& frame, const std::vector<ObservedEdge>& edges,
                         const wb::IntRect& roi) {
  auto abs_x = [&](double lx) { return roi.left + lx + 0.5; };
  auto abs_y = [&](double ly) { return roi.top + ly + 0.5; };
  frame.complete_edge_export_count = 0;
  frame.observed_red_edge_export_count = 0;
  for (const auto& e : edges) {
    const Vec2 p0{abs_x(e.seg.x0), abs_y(e.seg.y0)};
    const Vec2 p1{abs_x(e.seg.x1), abs_y(e.seg.y1)};
    if (frame.observed_red_edge_export_count < kMaxObservedRedEdgeExport) {
      auto& oe = frame.observed_red_edges[frame.observed_red_edge_export_count++];
      oe.p0 = p0;
      oe.p1 = p1;
      oe.workspace_edge = e.workspace_edge;
      oe.is_complete = e.complete ? 1 : 0;
    }
    if (e.complete && frame.complete_edge_export_count < 4) {
      auto& ce = frame.complete_edges[frame.complete_edge_export_count++];
      ce.p0 = p0;
      ce.p1 = p1;
      ce.workspace_edge = e.workspace_edge;
    }
  }
}

bool CompleteGroupPattern(GroupCandidate& g, const ViewportCompletionInput& in,
                          const wb::IntRect& roi, int rw, int rh,
                          bool* used_crop_correspondence) {
  AnnotateGroupRightAngles(g.edges);

  wb::IntRect canvas_abs = in.navigator_canvas_bounds.Clamp(in.width, in.height);
  if (!canvas_abs.valid()) canvas_abs = roi;
  const wb::IntRect canvas_local{
      canvas_abs.left - roi.left, canvas_abs.top - roi.top,
      canvas_abs.right - roi.left, canvas_abs.bottom - roi.top};

  const int crop_sides = in.workspace_canvas_relation.canvas_crop_sides;
  bool crop_path = false;
  bool rotation_path = false;
  if (crop_sides != 0) {
    const CropAssignResult cr = AssignEdgesByCropCorrespondence(
        g.edges, crop_sides, canvas_local, in.display_rotation_degrees,
        in.display_rotation_confidence);
    if (cr != CropAssignResult::Applied) return false;
    crop_path = true;
    g.used_crop_correspondence = true;
    if (used_crop_correspondence) *used_crop_correspondence = true;
  } else {
    const int q =
        ResolveDisplayQuarter(in.display_rotation_degrees, in.display_rotation_confidence);
    if (q >= 0 && AssignEdgesByDisplayRotation(g.edges, q, rw, rh)) {
      rotation_path = true;
    } else if (!AssignGroupWorkspaceEdgesLegacy(g.edges)) {
      return false;
    }
  }

  const bool use_geom_placement = crop_path || rotation_path;

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

  // 任意朝向：用 L∩T / R∩T / L∩B 交点恢复旋转矩形（相对正交边组）。
  auto complete_from_role_intersections = [&](ObservedEdge* Ledge, ObservedEdge* Redge,
                                              ObservedEdge* Tedge, ObservedEdge* Bedge) -> bool {
    if (!Ledge || !Redge || !Tedge || !Bedge) return false;
    double tlx = 0, tly = 0, trx = 0, try_ = 0, blx = 0, bly = 0;
    if (!LineIntersection(*Ledge, *Tedge, tlx, tly)) return false;
    if (!LineIntersection(*Redge, *Tedge, trx, try_)) return false;
    if (!LineIntersection(*Ledge, *Bedge, blx, bly)) return false;
    frame.origin_top_left_displayed = {abs_x(tlx), abs_y(tly)};
    frame.axis_x_displayed = {abs_x(trx) - abs_x(tlx), abs_y(try_) - abs_y(tly)};
    frame.axis_y_displayed = {abs_x(blx) - abs_x(tlx), abs_y(bly) - abs_y(tly)};
    const double cross = frame.axis_x_displayed.x * frame.axis_y_displayed.y -
                         frame.axis_x_displayed.y * frame.axis_y_displayed.x;
    if (!(std::abs(cross) > 4.0)) return false;
    if (cross < 0) {
      // 角色与屏幕朝向不一致时，保持语义 TL，反转 Y 使 a_x×a_y>0
      frame.axis_y_displayed.x = -frame.axis_y_displayed.x;
      frame.axis_y_displayed.y = -frame.axis_y_displayed.y;
    }
    return finish_ok(g.complete_count);
  };

  auto edges_near_axis_aligned = [&]() {
    for (const auto& e : g.edges) {
      if (std::abs(e.ux) > 0.12 && std::abs(e.uy) > 0.12) return false;
    }
    return true;
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

  auto recover_h = [&](const ObservedEdge& e, double& w, double& h) -> bool {
    if (recover_size_from_horizontal(e, w, h)) return true;
    if (!use_geom_placement) return false;
    h = std::max(EdgeLen(e), 8.0);
    w = std::max(h * aspect, 8.0);
    return true;
  };
  auto recover_v = [&](const ObservedEdge& e, double& w, double& h) -> bool {
    if (recover_size_from_vertical(e, w, h)) return true;
    if (!use_geom_placement) return false;
    w = std::max(EdgeLen(e), 8.0);
    h = std::max(w / aspect, 8.0);
    return true;
  };

  auto place_vertical_edge = [&](const ObservedEdge& e, double w, double h) {
    const double cy = abs_y(EdgePosY(e));
    const double ex = EdgePosX(e);
    const bool is_geom_left =
        use_geom_placement ? (0.5 * (canvas_local.left + canvas_local.right) > ex)
                           : ((e.workspace_edge == kEdgeL) ||
                              (e.workspace_edge == 0 && ex < rw * 0.5));
    frame.origin_top_left_displayed = {
        is_geom_left ? abs_x(ex) : abs_x(ex) - w, cy - h * 0.5};
    frame.axis_x_displayed = {w, 0};
    frame.axis_y_displayed = {0, h};
  };
  auto place_horizontal_edge = [&](const ObservedEdge& e, double w, double h) {
    const double cx = abs_x(EdgePosX(e));
    const double ey = EdgePosY(e);
    const bool is_geom_top =
        use_geom_placement ? (0.5 * (canvas_local.top + canvas_local.bottom) > ey)
                           : ((e.workspace_edge == kEdgeT) ||
                              (e.workspace_edge == 0 && ey < rh * 0.5));
    frame.origin_top_left_displayed = {
        cx - w * 0.5, is_geom_top ? abs_y(ey) : abs_y(ey) - h};
    frame.axis_x_displayed = {w, 0};
    frame.axis_y_displayed = {0, h};
  };

  ObservedEdge* L = FindEdge(g.edges, kEdgeL);
  ObservedEdge* R = FindEdge(g.edges, kEdgeR);
  ObservedEdge* T = FindEdge(g.edges, kEdgeT);
  ObservedEdge* B = FindEdge(g.edges, kEdgeB);

  // 语义角色可能因旋转与几何左右对调；拼显示矩形一律用几何 min/max（中点，非法向 offset）。
  auto geom_span_from_roles = [&](double& left, double& right, double& top,
                                  double& bottom) {
    if (L && R) {
      left = std::min(EdgePosX(*L), EdgePosX(*R));
      right = std::max(EdgePosX(*L), EdgePosX(*R));
    }
    if (T && B) {
      top = std::min(EdgePosY(*T), EdgePosY(*B));
      bottom = std::max(EdgePosY(*T), EdgePosY(*B));
    }
  };

  // 遗留回退：单边占位 L/T 后按 ROI 中线改指派。切割/旋转路径 MUST NOT 走此分支。
  if (!use_geom_placement) {
    if (n_v == 1 && L && !R) {
      if (EdgePosX(*L) >= rw * 0.5) {
        L->workspace_edge = kEdgeR;
        R = L;
        L = nullptr;
      }
    }
    if (n_h == 1 && T && !B) {
      if (EdgePosY(*T) >= rh * 0.5) {
        T->workspace_edge = kEdgeB;
        B = T;
        T = nullptr;
      }
    }
  }

  if (g.complete_count >= 4 && L && R && T && B && L->complete && R->complete && T->complete &&
      B->complete) {
    set_pattern(ViewportCompletionPattern::FourCompleteEdges);
    if (!edges_near_axis_aligned()) {
      return complete_from_role_intersections(L, R, T, B);
    }
    double left = 0, right = 0, top = 0, bottom = 0;
    geom_span_from_roles(left, right, top, bottom);
    return complete_from_ltrb(left, right, top, bottom);
  }

  if (g.complete_count == 3) {
    set_pattern(ViewportCompletionPattern::ThreeCompleteEdges);
    if (L && R && T && B) {
      double left = 0, right = 0, top = 0, bottom = 0;
      geom_span_from_roles(left, right, top, bottom);
      auto replace_role_side = [&](ObservedEdge* missing, bool vertical) {
        if (vertical) {
          const double mx = EdgePosX(*missing);
          if (std::abs(mx - left) <= std::abs(mx - right))
            left = right - (bottom - top) * aspect;
          else
            right = left + (bottom - top) * aspect;
        } else {
          const double my = EdgePosY(*missing);
          if (std::abs(my - top) <= std::abs(my - bottom))
            top = bottom - (right - left) / aspect;
          else
            bottom = top + (right - left) / aspect;
        }
      };
      if (!L->complete && R->complete && T->complete && B->complete) {
        replace_role_side(L, true);
        return complete_from_ltrb(left, right, top, bottom);
      }
      if (L->complete && !R->complete && T->complete && B->complete) {
        replace_role_side(R, true);
        return complete_from_ltrb(left, right, top, bottom);
      }
      if (L->complete && R->complete && !T->complete && B->complete) {
        replace_role_side(T, false);
        return complete_from_ltrb(left, right, top, bottom);
      }
      if (L->complete && R->complete && T->complete && !B->complete) {
        replace_role_side(B, false);
        return complete_from_ltrb(left, right, top, bottom);
      }
    }
    return false;
  }

  if (g.complete_count == 2) {
    if (L && R && L->complete && R->complete && !(T && T->complete) && !(B && B->complete)) {
      set_pattern(ViewportCompletionPattern::TwoParallelCompleteEdges);
      const double left = std::min(EdgePosX(*L), EdgePosX(*R));
      const double right = std::max(EdgePosX(*L), EdgePosX(*R));
      const double w_local = right - left;
      const double h_local = w_local / aspect;
      double cy = 0.5 * (std::min(L->seg.y0, R->seg.y0) + std::max(L->seg.y1, R->seg.y1));
      frame.origin_top_left_displayed = {abs_x(left), abs_y(cy - h_local * 0.5)};
      frame.axis_x_displayed = {abs_x(right) - abs_x(left), 0};
      frame.axis_y_displayed = {0, h_local};
      if (!crop_path) {
        const double len_l = EdgeLen(*L);
        const double len_r = EdgeLen(*R);
        if (std::abs(len_l - h_local) > std::max(8.0, 0.35 * h_local) &&
            std::abs(len_r - h_local) > std::max(8.0, 0.35 * h_local)) {
          return false;
        }
      }
      return finish_ok(2);
    }
    if (T && B && T->complete && B->complete && !(L && L->complete) && !(R && R->complete)) {
      set_pattern(ViewportCompletionPattern::TwoParallelCompleteEdges);
      const double top = std::min(EdgePosY(*T), EdgePosY(*B));
      const double bottom = std::max(EdgePosY(*T), EdgePosY(*B));
      const double h_local = bottom - top;
      const double w_local = h_local * aspect;
      double cx = 0.5 * (std::min(T->seg.x0, B->seg.x0) + std::max(T->seg.x1, B->seg.x1));
      frame.origin_top_left_displayed = {abs_x(cx - w_local * 0.5), abs_y(top)};
      frame.axis_x_displayed = {w_local, 0};
      frame.axis_y_displayed = {0, abs_y(bottom) - abs_y(top)};
      if (!crop_path) {
        const double len_t = EdgeLen(*T);
        const double len_b = EdgeLen(*B);
        if (std::abs(len_t - w_local) > std::max(8.0, 0.35 * w_local) &&
            std::abs(len_b - w_local) > std::max(8.0, 0.35 * w_local)) {
          return false;
        }
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
      const double vx = EdgePosX(*p.a);
      const double hy = EdgePosY(*p.b);
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
      const bool left =
          use_geom_placement ? (0.5 * (canvas_local.left + canvas_local.right) > vx)
                             : (p.a->workspace_edge == kEdgeL);
      const bool top =
          use_geom_placement ? (0.5 * (canvas_local.top + canvas_local.bottom) > hy)
                             : (p.b->workspace_edge == kEdgeT);
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
    if (!e && use_geom_placement) {
      for (auto& ed : g.edges)
        if (ed.workspace_edge != 0) {
          e = &ed;
          break;
        }
    }
    if (!e) return false;
    double w = 0, h = 0;
    if (e->seg.horizontal) {
      if (!recover_h(*e, w, h)) return false;
      place_horizontal_edge(*e, w, h);
    } else {
      if (!recover_v(*e, w, h)) return false;
      place_vertical_edge(*e, w, h);
    }
    return finish_ok(1);
  }

  // 无完整直角边时：切割/旋转路径仍可用已指派切割边补全视口
  if (g.complete_count == 0 && use_geom_placement) {
    const ObservedEdge* anchor = nullptr;
    for (const auto& ed : g.edges)
      if (ed.workspace_edge != 0) {
        anchor = &ed;
        break;
      }
    if (anchor) {
      set_pattern(g.edges.size() >= 2 && n_h > 0 && n_v > 0
                      ? ViewportCompletionPattern::IntersectingSegmentsNoCompleteEdge
                      : ViewportCompletionPattern::ParallelSegmentsNoCompleteEdge);
      double w = 0, h = 0;
      if (anchor->seg.horizontal) {
        if (!recover_h(*anchor, w, h)) return false;
        place_horizontal_edge(*anchor, w, h);
      } else {
        if (!recover_v(*anchor, w, h)) return false;
        place_vertical_edge(*anchor, w, h);
      }
      return finish_ok(0);
    }
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
      if (!recover_h(*best, w, h)) return false;
      place_horizontal_edge(*best, w, h);
    } else {
      if (!recover_v(*best, w, h)) return false;
      place_vertical_edge(*best, w, h);
    }
    if (g.edges.size() >= 2 && !best->seg.horizontal) {
      const ObservedEdge* other = nullptr;
      for (const auto& e : g.edges)
        if (&e != best && !e.seg.horizontal) {
          other = &e;
          break;
        }
      if (other && !crop_path) {
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
      if (other && !crop_path) {
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
      double d = 1e100;
      if (OrthogonalAdjacent(a, b)) {
        d = 0;
      } else {
        double ix = 0, iy = 0;
        if (LineIntersection(a, b, ix, iy)) {
          const double da =
              std::min(std::hypot(ix - a.seg.x0, iy - a.seg.y0),
                       std::hypot(ix - a.seg.x1, iy - a.seg.y1));
          const double db =
              std::min(std::hypot(ix - b.seg.x0, iy - b.seg.y0),
                       std::hypot(ix - b.seg.x1, iy - b.seg.y1));
          d = da + db;
        }
      }
      if (d < best_dist) {
        best_dist = d;
        v = &a;
        hz = &b;
      }
    }
  }
  if (!v || !hz || best_dist > 24.0) return false;

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

  const double vx = EdgePosX(*v);
  const double hy = EdgePosY(*hz);
  const bool left =
      use_geom_placement ? (0.5 * (canvas_local.left + canvas_local.right) > vx)
                         : ((v->workspace_edge == kEdgeL) || (v->workspace_edge == 0 && vx < rw * 0.5));
  const bool top =
      use_geom_placement ? (0.5 * (canvas_local.top + canvas_local.bottom) > hy)
                         : ((hz->workspace_edge == kEdgeT) ||
                            (hz->workspace_edge == 0 && hy < rh * 0.5));
  frame.origin_top_left_displayed = {left ? abs_x(vx) : abs_x(vx) - w,
                                     top ? abs_y(hy) : abs_y(hy) - h};
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
                                                   double max_w, double max_h,
                                                   const wb::IntRect& canvas_local) {
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
    if (!GroupSpatialGeometryOk(edges, max_w, max_h, canvas_local)) return;
    if (!GroupEdgeCardinalityOk(edges)) return;
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

  // A. 观测定向红段：主朝向 + 正交朝向（彼此垂直，不要求贴屏幕轴）
  std::vector<ObservedEdge> pool;
  ObserveAllOrientedEdges(red_det, red_raw, rw, rh, in.display_rotation_degrees,
                          in.display_rotation_confidence, pool);
  if (pool.empty()) {
    return Fail(FailStatus::InsufficientViewportGeometry, "no oriented red edges");
  }

  wb::IntRect canvas = in.navigator_canvas_bounds.Clamp(in.width, in.height);
  if (!canvas.valid()) canvas = roi;
  const wb::IntRect canvas_local{
      canvas.left - roi.left, canvas.top - roi.top,
      canvas.right - roi.left, canvas.bottom - roi.top};
  const bool has_chrome =
      canvas.left > roi.left || canvas.top > roi.top || canvas.right < roi.right ||
      canvas.bottom < roi.bottom;
  // 导航器画布在 ROI 局部坐标下的边长上限（平行对边间距）
  const double nav_w_local =
      static_cast<double>(std::max(1, std::min(canvas.right, roi.right) - std::max(canvas.left, roi.left)));
  const double nav_h_local =
      static_cast<double>(std::max(1, std::min(canvas.bottom, roi.bottom) - std::max(canvas.top, roi.top)));

  // B. 枚举合法极大 RedFrameEdgeGroup
  auto groups = EnumerateMaximalGroups(pool, nav_w_local, nav_h_local, canvas_local);
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
    if (!CompleteGroupPattern(g, in, roi, rw, rh, nullptr)) continue;
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

  // F. 仅发布目标组（导出最终 workspace_edge 指派后的全部观测红边）
  ExportGroupRedEdges(target->frame, target->edges, roi);
  ViewportCompletionResult r;
  r.status = FailStatus::Ok;
  r.frame = target->frame;
  r.used_crop_correspondence = target->used_crop_correspondence;
  if (target->used_crop_correspondence) {
    std::snprintf(r.message, sizeof(r.message),
                  "ok crop_correspondence pattern=%d complete=%d segs=%d groups=%d",
                  r.frame.completion_strategy, target->complete_count,
                  static_cast<int>(target->edges.size()), static_cast<int>(survivors.size()));
  } else {
    std::snprintf(r.message, sizeof(r.message),
                  "ok legacy_group_sort pattern=%d complete=%d segs=%d groups=%d",
                  r.frame.completion_strategy, target->complete_count,
                  static_cast<int>(target->edges.size()), static_cast<int>(survivors.size()));
  }
  return r;
}

}  // namespace sct
