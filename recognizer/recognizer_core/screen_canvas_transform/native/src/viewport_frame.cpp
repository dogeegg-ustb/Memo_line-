#include "sct/viewport_frame.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>
#include <utility>
#include <vector>

namespace sct {
namespace {

// ---- 观测与成组硬常量（契约测试固定，禁止运行时自适应放宽）----
constexpr int kMinRawRedPixels = 8;
constexpr float kMinEdgeSupport = 0.28f;
constexpr int kPeakMergeDist = 3;
constexpr double kParallelDotMin = 0.996194698;   // parallel within 5 degrees
constexpr double kOrthogonalDotMax = 0.087155743; // perpendicular within 5 degrees

// 组内空间相近 / 直角容差
// Projection/raster extraction can trim an anti-aliased oblique stroke several
// pixels before its mathematical corner. Keep group/corner identity tolerant
// enough for that quantization while still far below a valid viewport side.
constexpr int kGroupCornerTolPx = 10;
constexpr float kParallelOverlapRatio = 0.35f;
constexpr int kParallelOverlapMinPx = 8;
constexpr int kMinViewportSidePx = 12;
constexpr int kCollinearGapMaxPx = 8;

// Viewport validation uses workspace aspect and independently known scale.
constexpr double kTheorySizeAbsPx = 8.0;
constexpr double kTheorySizeRel = 0.18;
constexpr double kViewportAspectRelTol = 0.22;

// 多组消歧：窄红色度（严于观测门控 IsNavigatorRedPixel）
constexpr float kNarrowRedSatMin = 0.28f;
constexpr float kNarrowRedSupportMin = 0.45f;
constexpr int kNarrowRedMinSamples = 8;
constexpr float kRawRedWeightMin = 0.10f;
constexpr int kSeedMinLengthPx = 8;
constexpr int kObservedMinLengthPx = 16;

// Strict-core Hough red-line discovery. This stage only observes red
// lines; rectangle grouping and viewport completion remain downstream.
constexpr int kHoughThetaBins = 360;       // 0.5 degree bins
constexpr int kHoughRhoSmoothRadius = 2;   // collect anti-aliased stroke width
constexpr int kHoughPeakRhoRadius = 3;
constexpr int kHoughPeakThetaRadius = 2;
constexpr int kHoughMaxPeaks = 64;
constexpr int kHoughMaxObservedEdges = 32;
constexpr double kHoughDuplicateParallelDotMin = 0.999;
constexpr double kRedFrameAngleToleranceDegrees = 5.0;
constexpr int kLineProbeHalfWidthPx = 3;
constexpr int kCoreCenterTolerancePx = 1;
constexpr int kLineMaxThicknessPx = 7;
constexpr int kLineMaxBridgeGapPx = 6;
constexpr int kCoreSeedMaxGapPx = 3;
constexpr float kLineMinProfileSupport = 0.35f;
constexpr float kAaProfileRiseTolerance = 0.08f;
constexpr float kAaBlendResidualMax = 42.f;
// Pixel-center quantization is unavoidable; sustained drift and curvature are
// not. Fit actual stroke centers instead of declaring the Hough projection straight.
constexpr double kStraightLineMaxRmsPx = 0.40;
constexpr double kStraightLineMaxResidualPx = 0.65; // at least 95% of stroke centers
constexpr double kStraightLineMaxWindowDriftPx = 0.40;

constexpr int kEdgeL = 1;
constexpr int kEdgeT = 2;
constexpr int kEdgeR = 4;
constexpr int kEdgeB = 8;

// Continuous red evidence used after the binary candidate gate.  The first
// term is the red advantage over the strongest non-red channel; the second
// keeps dark red strokes detectable without treating neutral gray as red.
// This is deliberately based on channel relationships, not one RGB literal,
// so anti-aliased mixtures retain a fractional contribution.
inline float NavigatorRedWeight(const uint8_t* p) {
  const int b = p[0], g = p[1], r = p[2];
  const int maxc = std::max(r, std::max(g, b));
  const int minc = std::min(r, std::min(g, b));
  if (maxc < 35) return 0.f;
  const int dominance = r - std::max(g, b);
  if (dominance < 6) return 0.f;
  const int delta = maxc - minc;
  if (delta < 10) return 0.f;
  const float sat = static_cast<float>(delta) / static_cast<float>(maxc);
  const float advantage = std::clamp(static_cast<float>(dominance) / 255.f, 0.f, 1.f);
  const float chroma = std::clamp(sat * std::min(1.f, static_cast<float>(maxc) / 180.f), 0.f, 1.f);
  return std::clamp(0.80f * advantage + 0.20f * chroma, 0.f, 1.f);
}

// 多组筛选用：比观测门控更窄的“真红框”色度，用于压粉红/灰红 UI 干扰。
bool IsNarrowNavigatorRedPixel(const uint8_t* p) {
  const int b = p[0], g = p[1], r = p[2];
  const int maxc = std::max(r, std::max(g, b));
  const int minc = std::min(r, std::min(g, b));
  if (maxc < 100) return false;
  const int delta = maxc - minc;
  if (delta < 28) return false;
  if (r + 4 < maxc) return false;
  const float sat = static_cast<float>(delta) / static_cast<float>(maxc);
  if (sat < kNarrowRedSatMin) return false;
  if (r >= 140 && r - g >= 45 && r - b >= 45) return true;
  if (r >= 120 && r >= g + 20 && r >= b + 20 && (r - g) + (r - b) >= 55) return true;
  if (r >= 100 && r - g >= 60 && r - b >= 60) return true;
  return false;
}

struct ObservedEdge {
  RedLineSegment seg{};
  double ux = 1.0;  // unit tangent
  double uy = 0.0;
  double nx = 0.0;  // unit normal (−uy, ux)
  double ny = 1.0;
  double coord = -1.0;  // signed offset along normal: n·p
  int family = 0;       // 0 = screen-horizontal family, 1 = screen-vertical family
  int workspace_edge = 0;  // L/T/R/B bit（组内指派）
  bool complete = false;
  bool has_start_corner = false;  // GroupRightAngle at start
  bool has_end_corner = false;    // GroupRightAngle at end
  bool recovered_from_corner = false;
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

struct HoughPeak {
  int theta_bin = 0;
  int rho_bin = 0;
  float score = 0.f;
};

bool ClipLineToImage(double ox, double oy, double ux, double uy,
                     int w, int h, double& t0, double& t1) {
  t0 = -std::numeric_limits<double>::infinity();
  t1 = std::numeric_limits<double>::infinity();
  auto clip_axis = [&](double origin, double direction, double limit) {
    if (std::abs(direction) < 1e-9)
      return origin >= 0.0 && origin <= limit;
    double a = (0.0 - origin) / direction;
    double b = (limit - origin) / direction;
    if (a > b) std::swap(a, b);
    t0 = std::max(t0, a);
    t1 = std::min(t1, b);
    return t0 <= t1;
  };
  return clip_axis(ox, ux, w - 1.0) && clip_axis(oy, uy, h - 1.0);
}

struct BgrPixel {
  uint8_t b = 0;
  uint8_t g = 0;
  uint8_t r = 0;
};

struct BgraView {
  const uint8_t* bgra = nullptr;
  int width = 0;
  int height = 0;
  int stride = 0;
  int origin_x = 0;
  int origin_y = 0;
};

const uint8_t* PixelAt(const BgraView& view, int x, int y) {
  if (x < 0 || y < 0 || x >= view.width || y >= view.height) return nullptr;
  return view.bgra + static_cast<size_t>(y + view.origin_y) * view.stride +
         static_cast<size_t>(x + view.origin_x) * 4;
}

float RedWeightAt(const BgraView& view, int x, int y) {
  const uint8_t* p = PixelAt(view, x, y);
  return p ? NavigatorRedWeight(p) : 0.f;
}

bool NarrowRedAt(const BgraView& view, int x, int y) {
  const uint8_t* p = PixelAt(view, x, y);
  return p && IsNarrowNavigatorRedPixel(p);
}

BgrPixel BgrAt(const uint8_t* p) { return {p[0], p[1], p[2]}; }

struct LineProfileEvidence {
  bool supported = false;
  bool narrow_core = false;
  int center_offset = 0;
  double center_normal_offset = 0;
};

// An accepted wide-red pixel must look like coverage blending between the
// strict red core and the local colour immediately outside that side of the
// stroke. This admits raster anti-aliasing while rejecting arbitrary pink/red
// image regions that merely pass a broad colour threshold.
bool FitsCoverageBlend(const BgrPixel& pixel, const BgrPixel& core,
                       const BgrPixel& background, float& alpha) {
  const float vb = static_cast<float>(core.b) - background.b;
  const float vg = static_cast<float>(core.g) - background.g;
  const float vr = static_cast<float>(core.r) - background.r;
  const float denom = vb * vb + vg * vg + vr * vr;
  if (denom < 64.f) return false;
  const float pb = static_cast<float>(pixel.b) - background.b;
  const float pg = static_cast<float>(pixel.g) - background.g;
  const float pr = static_cast<float>(pixel.r) - background.r;
  alpha = (pb * vb + pg * vg + pr * vr) / denom;
  if (alpha < -0.08f || alpha > 1.12f) return false;
  const float eb = pb - alpha * vb;
  const float eg = pg - alpha * vg;
  const float er = pr - alpha * vr;
  return std::sqrt(eb * eb + eg * eg + er * er) <= kAaBlendResidualMax;
}

LineProfileEvidence AnalyzeLineProfile(const BgraView& view, double x, double y,
                                       double nx, double ny) {
  LineProfileEvidence out;
  float best = 0.f;
  for (int d = -kLineProbeHalfWidthPx; d <= kLineProbeHalfWidthPx; ++d) {
    const int px = static_cast<int>(std::lround(x + nx * d));
    const int py = static_cast<int>(std::lround(y + ny * d));
    const float response = RedWeightAt(view, px, py);
    if (response > best) { best = response; out.center_offset = d; }
  }
  if (best < kRawRedWeightMin) return out;
  if (std::abs(out.center_offset) > kCoreCenterTolerancePx) return out;

  const int cx = static_cast<int>(std::lround(x + nx * out.center_offset));
  const int cy = static_cast<int>(std::lround(y + ny * out.center_offset));
  const uint8_t* core_pixel = PixelAt(view, cx, cy);
  if (!core_pixel) return out;
  const BgrPixel core = BgrAt(core_pixel);

  int thickness = 1;
  bool blend_ok = true;
  out.narrow_core = std::abs(out.center_offset) <= kCoreCenterTolerancePx &&
                    NarrowRedAt(view, cx, cy);
  for (int sign : {-1, 1}) {
    const int bg_distance = kLineMaxThicknessPx + 2;
    const int bx = static_cast<int>(std::lround(x + nx * (out.center_offset + sign * bg_distance)));
    const int by = static_cast<int>(std::lround(y + ny * (out.center_offset + sign * bg_distance)));
    const uint8_t* background_pixel = PixelAt(view, bx, by);
    float previous_weight = best;
    float previous_alpha = 1.12f;
    for (int d = 1; d <= kLineMaxThicknessPx + 1; ++d) {
      const double offset = out.center_offset + sign * d;
      const int px = static_cast<int>(std::lround(x + nx * offset));
      const int py = static_cast<int>(std::lround(y + ny * offset));
      const float response = RedWeightAt(view, px, py);
      if (response < kRawRedWeightMin) break;
      const uint8_t* current_pixel = PixelAt(view, px, py);
      if (background_pixel && current_pixel) {
        float alpha = 0.f;
        if (!FitsCoverageBlend(BgrAt(current_pixel), core, BgrAt(background_pixel), alpha) ||
            alpha < 0.12f)
          break;
        if (alpha > previous_alpha + 0.15f) blend_ok = false;
        previous_alpha = alpha;
      }
      if (d > kLineMaxThicknessPx) return out;
      ++thickness;
      if (std::abs(out.center_offset + sign * d) <= kCoreCenterTolerancePx)
        out.narrow_core = out.narrow_core || NarrowRedAt(view, px, py);
      if (response > previous_weight + kAaProfileRiseTolerance) blend_ok = false;
      previous_weight = response;
    }
  }
  out.supported = thickness <= kLineMaxThicknessPx && blend_ok;
  if (out.supported) {
    // Unique raster pixels, weighted by red contrast, give the subpixel
    // center of a 1/2/3-pixel AA stroke without favoring its first peak pixel.
    double weight_sum=0,offset_sum=0;
    int previous_x=std::numeric_limits<int>::min(),previous_y=previous_x;
    for (int d=-kLineProbeHalfWidthPx;d<=kLineProbeHalfWidthPx;++d) {
      const int px=int(std::lround(x+nx*(out.center_offset+d)));
      const int py=int(std::lround(y+ny*(out.center_offset+d)));
      if (px==previous_x && py==previous_y) continue;
      previous_x=px;previous_y=py;
      const double response=RedWeightAt(view,px,py);
      if (response<kRawRedWeightMin) continue;
      const double weight=response*response;
      weight_sum+=weight;
      offset_sum+=weight*((px-x)*nx+(py-y)*ny);
    }
    out.center_normal_offset=weight_sum>0 ? offset_sum/weight_sum : out.center_offset;
  }
  return out;
}

bool RefineAndValidateStraightLine(ObservedEdge& edge,const BgraView& view) {
  const double original_length=std::hypot(edge.seg.x1-edge.seg.x0,edge.seg.y1-edge.seg.y0);
  if (original_length<kObservedMinLengthPx-1) return false;
  const double ux=edge.ux,uy=edge.uy,nx=edge.nx,ny=edge.ny;
  double seed_x=0,seed_y=0;
  bool have_seed=false;
  // A crossing can obscure the midpoint. Find a supported seed in this
  // observation, then follow the stroke itself in both tangent directions.
  for (int distance=0;distance<=int(std::ceil(original_length/2))+1 && !have_seed;++distance) {
    for (int sign:{-1,1}) {
      const double t=original_length/2+sign*distance;
      if (t<0 || t>original_length) continue;
      const double x=edge.seg.x0+ux*t,y=edge.seg.y0+uy*t;
      const auto profile=AnalyzeLineProfile(view,x,y,nx,ny);
      if (!profile.supported) continue;
      seed_x=x+nx*profile.center_normal_offset;
      seed_y=y+ny*profile.center_normal_offset;
      have_seed=true;break;
    }
  }
  if (!have_seed) return false;
  struct Sample { double x,y,t; };
  std::vector<Sample> before,after;
  auto follow=[&](int sign,std::vector<Sample>& samples) {
    double offset=0;
    int gap=0;
    for (int step=1;step<=2*(view.width+view.height);++step) {
      const double t=sign*step;
      const double x=seed_x+ux*t+nx*offset,y=seed_y+uy*t+ny*offset;
      if (x<0 || y<0 || x>view.width-1.0 || y>view.height-1.0) break;
      const auto profile=AnalyzeLineProfile(view,x,y,nx,ny);
      if (!profile.supported) {
        if (++gap>kLineMaxBridgeGapPx) break;
        continue;
      }
      gap=0;
      offset+=profile.center_normal_offset;
      samples.push_back({seed_x+ux*t+nx*offset,seed_y+uy*t+ny*offset,t});
    }
  };
  follow(-1,before);follow(1,after);
  std::reverse(before.begin(),before.end());
  std::vector<Sample> samples=std::move(before);
  samples.push_back({seed_x,seed_y,0});
  samples.insert(samples.end(),after.begin(),after.end());
  if (samples.size()<kMinRawRedPixels) return false;
  // Corners/crossings can change the local AA profile. Fit the interior; use
  // the actual first/last observations for the exported span afterwards.
  const size_t trim=std::min<size_t>(3,(samples.size()-kMinRawRedPixels)/2);
  const size_t count=samples.size()-2*trim;
  double mx=0,my=0;
  for (size_t i=trim;i<samples.size()-trim;++i) { mx+=samples[i].x;my+=samples[i].y; }
  mx/=count;my/=count;
  double xx=0,xy=0,yy=0;
  for (size_t i=trim;i<samples.size()-trim;++i) {
    const double dx=samples[i].x-mx,dy=samples[i].y-my;
    xx+=dx*dx;xy+=dx*dy;yy+=dy*dy;
  }
  const double angle=0.5*std::atan2(2*xy,xx-yy);
  double fit_ux=std::cos(angle),fit_uy=std::sin(angle);
  if (Dot2(fit_ux,fit_uy,ux,uy)<0) { fit_ux=-fit_ux;fit_uy=-fit_uy; }
  if (Dot2(fit_ux,fit_uy,ux,uy)<kParallelDotMin) return false;
  const double fit_nx=-fit_uy,fit_ny=fit_ux;
  double residual_sum=0,max_residual=0;
  size_t outliers=0,outlier_run=0,max_outlier_run=0;
  double window_sum[6]={};int window_count[6]={};
  for (size_t i=trim;i<samples.size()-trim;++i) {
    const double residual=(samples[i].x-mx)*fit_nx+(samples[i].y-my)*fit_ny;
    residual_sum+=residual*residual;max_residual=std::max(max_residual,std::abs(residual));
    if (std::abs(residual)>kStraightLineMaxResidualPx) {
      ++outliers;++outlier_run;max_outlier_run=std::max(max_outlier_run,outlier_run);
    } else outlier_run=0;
    const size_t bin=std::min<size_t>(5,(i-trim)*6/count);
    window_sum[bin]+=residual;++window_count[bin];
  }
  // A red drawing can overlap an otherwise exact frame and perturb a few
  // profile centers. Reject sustained deviation, while allowing a bounded
  // occlusion just as the line grower allows a small gap. A single contaminated
  // pixel is not evidence that a 300-pixel UI side bends.
  if (std::sqrt(residual_sum/count)>kStraightLineMaxRmsPx || outliers*20>count ||
      max_outlier_run>kLineMaxBridgeGapPx || max_residual>kLineProbeHalfWidthPx) return false;
  for (int bin=0;bin<6;++bin)
    if (window_count[bin]>=6 &&
        std::abs(window_sum[bin]/window_count[bin])>kStraightLineMaxWindowDriftPx) return false;
  const auto& first=samples.front();const auto& last=samples.back();
  const double t0=(first.x-mx)*fit_ux+(first.y-my)*fit_uy;
  const double t1=(last.x-mx)*fit_ux+(last.y-my)*fit_uy;
  if (t1-t0<kObservedMinLengthPx-1) return false;
  edge.ux=fit_ux;edge.uy=fit_uy;edge.nx=fit_nx;edge.ny=fit_ny;
  edge.coord=mx*fit_nx+my*fit_ny;
  edge.seg.x0=mx+fit_ux*t0;edge.seg.y0=my+fit_uy*t0;
  edge.seg.x1=mx+fit_ux*t1;edge.seg.y1=my+fit_uy*t1;
  edge.family=std::abs(fit_ux)>=std::abs(fit_uy) ? 0 : 1;
  edge.seg.horizontal=edge.family==0;
  edge.seg.support=float(std::min(1.0,samples.size()/(last.t-first.t+1.0)));
  return true;
}

void AddUniqueObservedEdge(ObservedEdge edge, std::vector<ObservedEdge>& pool) {
  const double n0 = Dot2(edge.seg.x0, edge.seg.y0, edge.ux, edge.uy);
  const double n1 = Dot2(edge.seg.x1, edge.seg.y1, edge.ux, edge.uy);
  for (auto& existing : pool) {
    if (std::abs(Dot2(existing.ux, existing.uy, edge.ux, edge.uy)) <
        kHoughDuplicateParallelDotMin)
      continue;
    const double normal_distance =
        std::abs(Dot2(EdgePosX(edge), EdgePosY(edge), existing.nx, existing.ny) -
                 existing.coord);
    if (normal_distance > kPeakMergeDist) continue;
    const double e0 = Dot2(existing.seg.x0, existing.seg.y0, edge.ux, edge.uy);
    const double e1 = Dot2(existing.seg.x1, existing.seg.y1, edge.ux, edge.uy);
    const bool overlaps =
        std::min(std::max(e0, e1), std::max(n0, n1)) + kLineMaxBridgeGapPx >=
        std::max(std::min(e0, e1), std::min(n0, n1));
    if (!overlaps) continue;
    const double old_span = std::abs(e1 - e0);
    const double new_span = std::abs(n1 - n0);
    if (new_span > old_span ||
        (std::abs(new_span - old_span) < 1.0 && edge.seg.support > existing.seg.support))
      existing = edge;
    return;
  }
  if (pool.size() < kHoughMaxObservedEdges) {
    pool.push_back(edge);
  } else {
    auto evidence=[](const ObservedEdge& e) {
      return std::hypot(e.seg.x1-e.seg.x0,e.seg.y1-e.seg.y0)*e.seg.support;
    };
    auto weakest=std::min_element(pool.begin(),pool.end(),[&](const ObservedEdge& a,const ObservedEdge& b) {
      return evidence(a)<evidence(b);
    });
    if (evidence(edge)>evidence(*weakest)) *weakest=edge;
  }
}

void EmitSupportedLineRuns(const BgraView& thumbnail,
                           double nx, double ny, double rho,
                           std::vector<ObservedEdge>& pool) {
  const int w = thumbnail.width;
  const int h = thumbnail.height;
  const double point_x = nx * rho;
  const double point_y = ny * rho;
  double ux = -ny, uy = nx;
  if (ux < 0.0 || (std::abs(ux) < 1e-9 && uy < 0.0)) {
    ux = -ux;
    uy = -uy;
  }
  nx = -uy;
  ny = ux;
  rho = Dot2(point_x, point_y, nx, ny);

  const double ox = nx * rho, oy = ny * rho;
  double line_t0 = 0.0, line_t1 = 0.0;
  if (!ClipLineToImage(ox, oy, ux, uy, w, h, line_t0, line_t1)) return;
  const int sample_count = static_cast<int>(std::floor(line_t1 - line_t0)) + 1;
  const int required_length = std::max(kObservedMinLengthPx, std::min(w, h) / 12);
  if (sample_count < required_length) return;

  std::vector<uint8_t> supported(static_cast<size_t>(sample_count), 0);
  std::vector<uint8_t> narrow_supported(static_cast<size_t>(sample_count), 0);
  for (int i = 0; i < sample_count; ++i) {
    const double t = line_t0 + i;
    const double x = ox + ux * t;
    const double y = oy + uy * t;
    const auto profile = AnalyzeLineProfile(thumbnail, x, y, nx, ny);
    if (profile.supported) {
      supported[static_cast<size_t>(i)] = 1;
      narrow_supported[static_cast<size_t>(i)] = profile.narrow_core ? 1 : 0;
    }
  }

  // Strict-core runs are the only seeds. Grow each seed in both tangent
  // directions through AA-profile support, with a bounded raster gap.
  std::vector<uint8_t> emitted(static_cast<size_t>(sample_count), 0);
  for (int seed_start = 0; seed_start < sample_count;) {
    while (seed_start < sample_count && !narrow_supported[static_cast<size_t>(seed_start)])
      ++seed_start;
    if (seed_start >= sample_count) break;
    int seed_end = seed_start;
    int core_count = 1;
    int last_core = seed_start;
    for (int i = seed_start + 1; i < sample_count; ++i) {
      if (narrow_supported[static_cast<size_t>(i)]) {
        if (i - last_core - 1 > kCoreSeedMaxGapPx) break;
        last_core = i;
        seed_end = i;
        ++core_count;
      }
    }
    const int seed_span = seed_end - seed_start + 1;
    seed_start = std::max(seed_start + 1, seed_end + 1);
    if (seed_span < kSeedMinLengthPx || core_count < kMinRawRedPixels) continue;

    int first = seed_end - seed_span + 1;
    int last = seed_end;
    int gap = 0;
    for (int i = first - 1; i >= 0; --i) {
      if (supported[static_cast<size_t>(i)]) { first = i; gap = 0; }
      else if (++gap > kLineMaxBridgeGapPx) break;
    }
    gap = 0;
    for (int i = last + 1; i < sample_count; ++i) {
      if (supported[static_cast<size_t>(i)]) { last = i; gap = 0; }
      else if (++gap > kLineMaxBridgeGapPx) break;
    }
    if (emitted[static_cast<size_t>((first + last) / 2)]) continue;
    const int span = last - first + 1;
    int supported_count = 0;
    int narrow_count = 0;
    for (int i = first; i <= last; ++i) {
      supported_count += supported[static_cast<size_t>(i)] != 0;
      narrow_count += narrow_supported[static_cast<size_t>(i)] != 0;
    }
    const float support = static_cast<float>(supported_count) / std::max(1, span);
    if (span >= required_length && support >= kLineMinProfileSupport &&
        narrow_count >= kMinRawRedPixels) {
      for (int i = first; i <= last; ++i) emitted[static_cast<size_t>(i)] = 1;
      const double t0 = line_t0 + first;
      const double t1 = line_t0 + last;
      ObservedEdge edge;
      edge.ux = ux;
      edge.uy = uy;
      edge.nx = nx;
      edge.ny = ny;
      edge.coord = rho;
      edge.family = std::abs(ux) >= std::abs(uy) ? 0 : 1;
      edge.seg.x0 = ox + ux * t0;
      edge.seg.y0 = oy + uy * t0;
      edge.seg.x1 = ox + ux * t1;
      edge.seg.y1 = oy + uy * t1;
      edge.seg.horizontal = std::abs(ux) >= std::abs(uy);
      edge.seg.support = support;
      edge.seg.corner_at_start = -1;
      edge.seg.corner_at_end = -1;
      if (RefineAndValidateStraightLine(edge,thumbnail)) AddUniqueObservedEdge(edge, pool);
    }
  }
}

// Discover red lines globally. Strict red cores determine candidate direction;
// supported AA profiles determine their visible endpoints. Downstream code
// decides corners, complete sides, groups, and viewport completion patterns.
void DetectGlobalRedLines(const std::vector<float>& red_weight,
                          const std::vector<uint8_t>& narrow_red,
                          const BgraView& thumbnail,
                          std::vector<ObservedEdge>& pool) {
  pool.clear();
  const int w = thumbnail.width;
  const int h = thumbnail.height;
  const int rho_extent = static_cast<int>(std::ceil(std::hypot(w - 1.0, h - 1.0)));
  const int rho_bins = rho_extent * 2 + 1;
  if (rho_bins <= 1) return;

  constexpr double kPi = 3.14159265358979323846;
  std::vector<double> cos_table(kHoughThetaBins), sin_table(kHoughThetaBins);
  for (int theta = 0; theta < kHoughThetaBins; ++theta) {
    const double angle = kPi * theta / kHoughThetaBins;
    cos_table[static_cast<size_t>(theta)] = std::cos(angle);
    sin_table[static_cast<size_t>(theta)] = std::sin(angle);
  }

  std::vector<float> accumulator(static_cast<size_t>(kHoughThetaBins) * rho_bins, 0.f);
  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      const size_t pixel = static_cast<size_t>(y) * w + x;
      // Direction is owned exclusively by strict red core pixels. Wide red
      // evidence is reserved for profile-constrained growth after seeding.
      if (!narrow_red[pixel]) continue;
      const float vote = std::max(0.25f, red_weight[pixel]);
      for (int theta = 0; theta < kHoughThetaBins; ++theta) {
        const int rho = static_cast<int>(std::lround(
            x * cos_table[static_cast<size_t>(theta)] +
            y * sin_table[static_cast<size_t>(theta)]));
        const int bin = rho + rho_extent;
        if (bin >= 0 && bin < rho_bins)
          accumulator[static_cast<size_t>(theta) * rho_bins + bin] += vote;
      }
    }
  }

  auto score_at = [&](int theta, int rho_bin) {
    theta %= kHoughThetaBins;
    if (theta < 0) theta += kHoughThetaBins;
    float score = 0.f;
    for (int dr = -kHoughRhoSmoothRadius; dr <= kHoughRhoSmoothRadius; ++dr) {
      const int rb = rho_bin + dr;
      if (rb < 0 || rb >= rho_bins) continue;
      const float kernel = 1.f / (1.f + std::abs(dr));
      score += kernel * accumulator[static_cast<size_t>(theta) * rho_bins + rb];
    }
    return score;
  };

  const int required_length = std::max(kObservedMinLengthPx, std::min(w, h) / 12);
  const float min_peak_score = std::max(4.f, required_length * 0.25f);
  std::vector<HoughPeak> peaks;
  for (int theta = 0; theta < kHoughThetaBins; ++theta) {
    for (int rb = 0; rb < rho_bins; ++rb) {
      const float score = score_at(theta, rb);
      if (score < min_peak_score) continue;
      bool local_maximum = true;
      for (int dt = -kHoughPeakThetaRadius; dt <= kHoughPeakThetaRadius && local_maximum; ++dt) {
        for (int dr = -kHoughPeakRhoRadius; dr <= kHoughPeakRhoRadius; ++dr) {
          if (dt == 0 && dr == 0) continue;
          const float neighbor = score_at(theta + dt, rb + dr);
          if (neighbor > score + 1e-6f ||
              (std::abs(neighbor - score) <= 1e-6f &&
               (dt < 0 || (dt == 0 && dr < 0)))) {
            local_maximum = false;
            break;
          }
        }
      }
      if (local_maximum) peaks.push_back({theta, rb, score});
    }
  }
  std::sort(peaks.begin(), peaks.end(), [](const HoughPeak& a, const HoughPeak& b) {
    return a.score > b.score;
  });
  if (peaks.size() > kHoughMaxPeaks) peaks.resize(kHoughMaxPeaks);

  for (const auto& peak : peaks) {
    const double nx = cos_table[static_cast<size_t>(peak.theta_bin)];
    const double ny = sin_table[static_cast<size_t>(peak.theta_bin)];
    const double rho = peak.rho_bin - rho_extent;
    EmitSupportedLineRuns(thumbnail, nx, ny, rho, pool);
    // Examine every retained Hough peak. Crowded handwriting must not fill
    // the output capacity before a later, better-supported frame side is seen.
  }
}

// Hough seeds stay inside the Navigator thumbnail, but a visible viewport
// stroke can continue past that rectangle. Follow only an already accepted
// segment across the thumbnail boundary and stop at its last supported pixel
// in the frozen screen capture.
void ExtendObservedLinePastThumbnail(ObservedEdge& edge, const wb::IntRect& roi,
                                     const BgraView& capture) {
  const double original_length = std::hypot(edge.seg.x1 - edge.seg.x0,
                                            edge.seg.y1 - edge.seg.y0);
  int added_support = 0;
  auto extend_end = [&](bool at_start) {
    const double x = at_start ? edge.seg.x0 : edge.seg.x1;
    const double y = at_start ? edge.seg.y0 : edge.seg.y1;
    const double ux = at_start ? -edge.ux : edge.ux;
    const double uy = at_start ? -edge.uy : edge.uy;
    double roi_enter = 0.0, roi_leave = 0.0;
    if (!ClipLineToImage(x, y, ux, uy, roi.width(), roi.height(),
                         roi_enter, roi_leave) ||
        roi_leave > kLineMaxBridgeGapPx + 1.0) {
      return;
    }

    const double screen_x = roi.left + x;
    const double screen_y = roi.top + y;
    double screen_enter = 0.0, screen_leave = 0.0;
    if (!ClipLineToImage(screen_x, screen_y, ux, uy,
                         capture.width, capture.height,
                         screen_enter, screen_leave)) {
      return;
    }

    int gap = 0;
    int last_supported = 0;
    int supported_count = 0;
    for (int step = 1; step <= static_cast<int>(std::floor(screen_leave)); ++step) {
      const auto profile = AnalyzeLineProfile(
          capture, screen_x + ux * step, screen_y + uy * step, edge.nx, edge.ny);
      if (profile.supported) {
        last_supported = step;
        ++supported_count;
        gap = 0;
      } else if (++gap > kLineMaxBridgeGapPx) {
        break;
      }
    }
    if (last_supported == 0) return;

    if (at_start) {
      edge.seg.x0 = x + ux * last_supported;
      edge.seg.y0 = y + uy * last_supported;
    } else {
      edge.seg.x1 = x + ux * last_supported;
      edge.seg.y1 = y + uy * last_supported;
    }
    added_support += supported_count;
  };

  extend_end(true);
  extend_end(false);
  if (added_support > 0) {
    const double extended_length = std::hypot(edge.seg.x1 - edge.seg.x0,
                                               edge.seg.y1 - edge.seg.y0);
    edge.seg.support = static_cast<float>(std::clamp(
        (edge.seg.support * original_length + added_support) /
            std::max(extended_length, 1.0),
        0.0, 1.0));
  }
}

bool RedLineMatchesDisplayRotation(const ObservedEdge& edge, double rotation_degrees) {
  if (!std::isfinite(rotation_degrees) || !std::isfinite(edge.ux) || !std::isfinite(edge.uy))
    return false;
  // CSP's positive rotation maps to a negative slope in screen coordinates
  // (the same convention used by CompleteDirectedGroup). Lines are undirected,
  // so compare modulo 180° against the OCR angle and its perpendicular.
  const double target = std::remainder(-rotation_degrees, 180.0);
  const double angle = std::atan2(edge.uy, edge.ux) * 180.0 / 3.14159265358979323846;
  const double parallel_delta = std::abs(std::remainder(angle - target, 180.0));
  const double perpendicular_delta =
      std::abs(std::remainder(angle - (target + 90.0), 180.0));
  return std::min(parallel_delta, perpendicular_delta) <= kRedFrameAngleToleranceDegrees;
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
  auto endpoint_distance = [&](const ObservedEdge& e) {
    return std::min(std::hypot(ix - e.seg.x0, iy - e.seg.y0),
                    std::hypot(ix - e.seg.x1, iy - e.seg.y1));
  };
  // Crossing inside two strokes is not a rectangle corner. Both strokes must
  // end near the intersection; otherwise artwork creates false complete sides.
  if (on_seg(a, ix, iy) && on_seg(b, ix, iy) &&
      endpoint_distance(a) <= tol && endpoint_distance(b) <= tol) return true;

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
  const double spacing = std::abs(Dot2(EdgePosX(b), EdgePosY(b), a.nx, a.ny) - a.coord);
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
  const double other_end = std::min(std::hypot(ix - b.seg.x0, iy - b.seg.y0),
                                    std::hypot(ix - b.seg.x1, iy - b.seg.y1));
  return near_seg(a) && near_seg(b) && other_end <= tol;
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

// Recover weak perpendicular sides from factual red endpoints BEFORE pattern
// completion. The old recovery ran only after a pattern succeeded, so a pale
// closed frame could lose its corners, become 0.1, and use a clipped length.
// These are pixel probes, never edges manufactured from a completed rectangle.
void RecoverEndpointAdjacentRedLines(std::vector<ObservedEdge>& pool,
                                    const wb::IntRect& roi, const BgraView& capture) {
  for (int round = 0; round < 3; ++round) {
    const auto anchors = pool;
    const size_t before = pool.size();
    for (const auto& anchor_local : anchors) {
      ObservedEdge anchor = anchor_local;
      anchor.seg.x0 += roi.left; anchor.seg.x1 += roi.left;
      anchor.seg.y0 += roi.top; anchor.seg.y1 += roi.top;
      anchor.coord += roi.left * anchor.nx + roi.top * anchor.ny;
      for (Vec2 endpoint : {Vec2{anchor.seg.x0, anchor.seg.y0},
                            Vec2{anchor.seg.x1, anchor.seg.y1}}) {
        for (int offset : {0, -1, 1, -2, 2}) {
          ObservedEdge candidate;
          candidate.ux = -anchor.uy; candidate.uy = anchor.ux;
          if (candidate.ux < 0 || (std::abs(candidate.ux) < 1e-9 && candidate.uy < 0)) {
            candidate.ux = -candidate.ux; candidate.uy = -candidate.uy;
          }
          candidate.nx = -candidate.uy; candidate.ny = candidate.ux;
          const Vec2 point{endpoint.x + anchor.ux * offset,
                           endpoint.y + anchor.uy * offset};
          double t0 = 0, t1 = 0;
          if (!ClipLineToImage(point.x, point.y, candidate.ux, candidate.uy,
                               capture.width, capture.height, t0, t1)) continue;
          const double probe = 2 * kObservedMinLengthPx;
          t0 = std::max(t0, -probe); t1 = std::min(t1, probe);
          if (t1 - t0 < kObservedMinLengthPx) continue;
          candidate.seg.x0 = point.x + candidate.ux * t0;
          candidate.seg.y0 = point.y + candidate.uy * t0;
          candidate.seg.x1 = point.x + candidate.ux * t1;
          candidate.seg.y1 = point.y + candidate.uy * t1;
          if (!RefineAndValidateStraightLine(candidate, capture) ||
              candidate.seg.support < 0.75f || !OrthogonalAdjacent(anchor, candidate)) continue;
          candidate.seg.x0 -= roi.left; candidate.seg.x1 -= roi.left;
          candidate.seg.y0 -= roi.top; candidate.seg.y1 -= roi.top;
          candidate.coord -= roi.left * candidate.nx + roi.top * candidate.ny;
          candidate.recovered_from_corner = true;
          AddUniqueObservedEdge(candidate, pool);
          break;
        }
      }
    }
    if (pool.size() == before) break;
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
  if (edges.empty()) return false;
  int n_v = 0, n_h = 0;
  double v0 = 0, v1 = 0, h0 = 0, h1 = 0;
  bool have_v0 = false, have_h0 = false;
  for (const auto& e : edges) {
    if (DirsParallel(e.ux, e.uy, edges[0].ux, edges[0].uy)) {
      if (n_h == 0) {
        h0 = Dot2(EdgePosX(e), EdgePosY(e), edges[0].nx, edges[0].ny);
        have_h0 = true;
      } else if (n_h == 1) {
        h1 = Dot2(EdgePosX(e), EdgePosY(e), edges[0].nx, edges[0].ny);
      }
      ++n_h;
    } else if (DirsOrthogonal(e.ux, e.uy, edges[0].ux, edges[0].uy)) {
      if (n_v == 0) {
        v0 = Dot2(EdgePosX(e), EdgePosY(e), edges[0].ux, edges[0].uy);
        have_v0 = true;
      } else if (n_v == 1) {
        v1 = Dot2(EdgePosX(e), EdgePosY(e), edges[0].ux, edges[0].uy);
      }
      ++n_v;
    } else return false;
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

// Length of the observed viewport edge that actually passes through the
// Navigator's displayed canvas. Red ink outside that paper is not part of the
// workspace/canvas overlap ratio and must not inflate the 0.1/0.2 numerator.
bool ClipEdgeToCanvas(const ObservedEdge& e, const wb::IntRect& canvas_local,
                      Vec2* clipped_start, Vec2* clipped_end) {
  if (!canvas_local.valid()) return false;
  const double x0 = e.seg.x0;
  const double y0 = e.seg.y0;
  const double dx = e.seg.x1 - x0;
  const double dy = e.seg.y1 - y0;
  double enter = 0.0;
  double leave = 1.0;
  auto clip = [&](double p, double q) {
    if (std::abs(p) < 1e-12) return q >= 0.0;
    const double r = q / p;
    if (p < 0.0) {
      if (r > leave) return false;
      enter = std::max(enter, r);
    } else {
      if (r < enter) return false;
      leave = std::min(leave, r);
    }
    return true;
  };
  const double left = canvas_local.left - 0.5;
  const double top = canvas_local.top - 0.5;
  const double right = canvas_local.right - 0.5;
  const double bottom = canvas_local.bottom - 0.5;
  if (!clip(-dx, x0 - left) || !clip(dx, right - x0) ||
      !clip(-dy, y0 - top) || !clip(dy, bottom - y0) || leave <= enter) {
    return false;
  }
  if (clipped_start) *clipped_start = {x0 + dx * enter, y0 + dy * enter};
  if (clipped_end) *clipped_end = {x0 + dx * leave, y0 + dy * leave};
  return true;
}

double EdgeLengthInsideCanvas(const ObservedEdge& e, const wb::IntRect& canvas_local) {
  Vec2 a{}, b{};
  if (!ClipEdgeToCanvas(e, canvas_local, &a, &b)) return 0.0;
  return std::hypot(b.x - a.x, b.y - a.y);
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
  auto same_assignment = [](const std::vector<ObservedEdge>& a,
                            const std::vector<ObservedEdge>& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
      if (a[i].workspace_edge != b[i].workspace_edge) return false;
    }
    return true;
  };
  for (int q : candidates) {
    auto trial = edges;
    bool q_ambiguous = false;
    if (!try_quarters(q, trial, &q_ambiguous)) {
      if (q_ambiguous) any_role_ambiguous = true;
      continue;
    }
    if (success_q >= 0 && success_q != q) {
      // With only one visible cutting edge, 0° and 180° (or 90° and
      // 270°) can both be admissible while producing exactly the same
      // semantic edge assignment.  That is not an edge-role ambiguity.  The
      // old q-only comparison rejected these common 0.1/0.2 observations when
      // OCR missed the displayed "0.0" rotation value.
      if (!same_assignment(best, trial)) return CropAssignResult::Ambiguous;
      continue;
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
    if (DirsParallel(e.ux, e.uy, edges[0].ux, edges[0].uy))
      ++n_h;
    else
      ++n_v;
  }
  if (n_v > 2 || n_h > 2) return false;

  // 平行对边：仅当两条均为切割边时才校验间距（非切割外框边不得与切割边拼成平行对）
  std::vector<const ObservedEdge*> verts, hors;
  for (const auto& e : edges) {
    if (DirsParallel(e.ux, e.uy, edges[0].ux, edges[0].uy))
      hors.push_back(&e);
    else
      verts.push_back(&e);
  }
  if (verts.size() == 2) {
    const bool a_cut = EdgeIntersectsCanvasLocal(*verts[0], canvas_local);
    const bool b_cut = EdgeIntersectsCanvasLocal(*verts[1], canvas_local);
    if (a_cut && b_cut && !ParallelPairOk(*verts[0], *verts[1], std::hypot(max_w,max_h))) return false;
  }
  if (hors.size() == 2) {
    const bool a_cut = EdgeIntersectsCanvasLocal(*hors[0], canvas_local);
    const bool b_cut = EdgeIntersectsCanvasLocal(*hors[1], canvas_local);
    if (a_cut && b_cut && !ParallelPairOk(*hors[0], *hors[1], std::hypot(max_w,max_h))) return false;
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

bool SizeMatchesTheory(double w, double h, double W_nav, double H_nav) {
  if (!(w > 2.0 && h > 2.0 && W_nav > 2.0 && H_nav > 2.0)) return false;
  const double tw = std::max(kTheorySizeAbsPx, kTheorySizeRel * W_nav);
  const double th = std::max(kTheorySizeAbsPx, kTheorySizeRel * H_nav);
  return std::abs(w - W_nav) <= tw && std::abs(h - H_nav) <= th;
}

bool AspectMatchesWorkspace(double w, double h, double workspace_aspect) {
  if (!(w > 2.0 && h > 2.0 && workspace_aspect > 1e-4)) return false;
  const double a = w / h;
  return std::abs(a - workspace_aspect) <= kViewportAspectRelTol * workspace_aspect;
}

// The viewport has workspace aspect; paper aspect only describes the thumbnail.
bool ShapeMatchesWorkspaceViewport(double w, double h, double W_nav, double H_nav,
                                   double workspace_aspect, bool theory_available) {
  if (!(w > 2 && h > 2 && std::isfinite(w) && std::isfinite(h))) return false;
  if (theory_available && !SizeMatchesTheory(w, h, W_nav, H_nav)) return false;
  if (workspace_aspect > 1e-4 && !AspectMatchesWorkspace(w, h, workspace_aspect)) return false;
  return true;
}

struct GroupCandidate {
  std::vector<int> indices;  // into pool
  std::vector<ObservedEdge> edges;
  int complete_count = 0;
  int partial_count = 0;
  int unanchored = 0;
  int confirmed_corners = 0;
  int recovered_edge_count = 0;
  bool completed_ok = false;
  bool used_crop_correspondence = false;
  ViewportCompletionPattern pattern = ViewportCompletionPattern::FourCompleteEdges;
  NavigatorViewportFrame frame{};
  char completion_failure[128] = {};
};

// 沿组内观测红段采样：窄红支撑率达到门槛才通过（多组并列时用）。
bool GroupNarrowRedChromaOk(const GroupCandidate& g, const ViewportCompletionInput& in,
                            const wb::IntRect& roi) {
  int total = 0;
  int narrow = 0;
  for (const auto& e : g.edges) {
    const double len = EdgeLen(e);
    const int steps = std::max(4, static_cast<int>(len / 3.0));
    for (int i = 0; i <= steps; ++i) {
      const double t = static_cast<double>(i) / static_cast<double>(steps);
      const int lx = static_cast<int>(std::lround(e.seg.x0 + (e.seg.x1 - e.seg.x0) * t));
      const int ly = static_cast<int>(std::lround(e.seg.y0 + (e.seg.y1 - e.seg.y0) * t));
      const int ax = roi.left + lx;
      const int ay = roi.top + ly;
      if (ax < roi.left || ax >= roi.right || ay < roi.top || ay >= roi.bottom) continue;
      const uint8_t* p =
          in.bgra + static_cast<size_t>(ay) * in.stride + static_cast<size_t>(ax) * 4;
      ++total;
      if (IsNarrowNavigatorRedPixel(p)) ++narrow;
    }
  }
  if (total < kNarrowRedMinSamples) return false;
  return static_cast<float>(narrow) / static_cast<float>(total) >= kNarrowRedSupportMin;
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

struct RecoveredSide {
  double start = 0;
  double end = 0;
  double normal = 0;
  double observed_start = 0;
  double observed_end = 0;
  double length() const { return end - start; }
};

// Shared single-side length recovery for 0.1 / 0.2 / 0.3. Complete red sides
// retain their entire observed interval, including pixels outside the paper.
// Partial sides use both the workspace contact length and its offset.
bool RecoverSideLength(const ObservedEdge& edge, const ViewportCompletionInput& in,
                       const wb::IntRect& canvas_local, Vec2 tangent, Vec2 normal,
                       bool along_workspace_x, RecoveredSide& side) {
  if (!DirsParallel(edge.ux, edge.uy, tangent.x, tangent.y)) return false;
  const double observed0 = Dot2(edge.seg.x0, edge.seg.y0, tangent.x, tangent.y);
  const double observed1 = Dot2(edge.seg.x1, edge.seg.y1, tangent.x, tangent.y);
  side.observed_start = std::min(observed0, observed1);
  side.observed_end = std::max(observed0, observed1);
  side.normal = Dot2(EdgePosX(edge), EdgePosY(edge), normal.x, normal.y);
  if (edge.complete) {
    side.start = side.observed_start;
    side.end = side.observed_end;
    return side.length() > 4;
  }
  const auto& rel = in.workspace_canvas_relation;
  const auto& workspace = rel.workspace_roi;
  const auto& visible = rel.visible_canvas_bounds_workspace_local;
  if (!workspace.valid() || rel.ambiguous) return false;
  Vec2 cut0{}, cut1{};
  if (!ClipEdgeToCanvas(edge, canvas_local, &cut0, &cut1)) return false;
  const double total = along_workspace_x ? workspace.width() : workspace.height();
  const double fraction = along_workspace_x ? rel.visible_canvas_workspace_fraction_x
                                            : rel.visible_canvas_workspace_fraction_y;
  const double contact = visible.valid()
      ? (along_workspace_x ? visible.width() : visible.height()) : total * fraction;
  const double offset = visible.valid()
      ? (along_workspace_x ? visible.left : visible.top) : 0;
  if (!(contact > 1e-4 && contact <= total && std::isfinite(contact))) return false;
  const double p0 = Dot2(cut0.x, cut0.y, tangent.x, tangent.y);
  const double p1 = Dot2(cut1.x, cut1.y, tangent.x, tangent.y);
  const double contact_length = std::abs(p1 - p0);
  if (contact_length < 2) return false;
  const double scale = contact_length / contact;
  side.start = std::min(p0, p1) - offset * scale;
  side.end = side.start + total * scale;
  return side.length() > 4 && std::isfinite(side.start) && std::isfinite(side.end);
}

bool RecoveredSideContainsObservation(const RecoveredSide& side) {
  // Recovery may extend a clipped observation; it may never shorten factual
  // pixels. Allow only the same bounded endpoint uncertainty as corner fitting.
  return side.start <= side.observed_start + kGroupCornerTolPx &&
         side.end >= side.observed_end - kGroupCornerTolPx;
}

bool CompleteOppositePartialSides(GroupCandidate& g, const ViewportCompletionInput& in,
                                  const wb::IntRect& roi) {
  if (g.edges.size() != 2 || g.edges[0].complete || g.edges[1].complete ||
      !DirsParallel(g.edges[0].ux, g.edges[0].uy, g.edges[1].ux, g.edges[1].uy)) return false;
  g.pattern = ViewportCompletionPattern::OppositeSegmentsNoCompleteEdge;
  const auto& workspace = in.workspace_canvas_relation.workspace_roi;
  if (!workspace.valid()) return false;
  const double aspect = double(workspace.width()) / workspace.height();
  constexpr double rad = 0.017453292519943295;
  const bool have_angle = in.display_rotation_confidence >= 0.2f &&
                          std::isfinite(in.display_rotation_degrees);
  const double angle = have_angle ? -in.display_rotation_degrees * rad : 0;
  const Vec2 hint{std::cos(angle), std::sin(angle)};
  Vec2 ax{g.edges[0].ux, g.edges[0].uy};
  if (std::abs(Dot2(ax.x, ax.y, hint.x, hint.y)) < 0.70710678) ax = {-ax.y, ax.x};
  if (Dot2(ax.x, ax.y, hint.x, hint.y) < 0) ax = {-ax.x, -ax.y};
  if (have_angle && Dot2(ax.x, ax.y, hint.x, hint.y) < std::cos(5 * rad)) return false;
  const Vec2 ay{-ax.y, ax.x};
  const bool along_x = DirsParallel(g.edges[0].ux, g.edges[0].uy, ax.x, ax.y);
  const Vec2 tangent = along_x ? ax : ay, normal = along_x ? ay : ax;
  wb::IntRect canvas = in.navigator_canvas_bounds.Clamp(in.width, in.height);
  if (!canvas.valid()) canvas = roi;
  const wb::IntRect canvas_local{canvas.left - roi.left, canvas.top - roi.top,
                                canvas.right - roi.left, canvas.bottom - roi.top};
  RecoveredSide sides[2];
  if (!RecoverSideLength(g.edges[0], in, canvas_local, tangent, normal, along_x, sides[0]) ||
      !RecoverSideLength(g.edges[1], in, canvas_local, tangent, normal, along_x, sides[1])) {
    std::snprintf(g.completion_failure, sizeof(g.completion_failure),
                  "0.3 side recovery lacks workspace contact geometry");
    return false;
  }
  for (const auto& side : sides) {
    if (!RecoveredSideContainsObservation(side)) {
      std::snprintf(g.completion_failure, sizeof(g.completion_failure),
          "0.3 contact conflict: observed=%.1f recovered=%.1f relation=%.2f",
          side.observed_end - side.observed_start, side.length(),
          in.workspace_canvas_relation.confidence);
      return false;
    }
  }
  const double tolerance = std::max(3.0, 0.02 * std::max(sides[0].length(), sides[1].length()));
  for (int i=0;i<2;++i) {
    const auto& e=g.edges[i];
    if (std::abs(Dot2(e.seg.x0,e.seg.y0,normal.x,normal.y)-sides[i].normal)>kPeakMergeDist ||
        std::abs(Dot2(e.seg.x1,e.seg.y1,normal.x,normal.y)-sides[i].normal)>kPeakMergeDist) {
      std::snprintf(g.completion_failure,sizeof(g.completion_failure),
                    "0.3 observed opposite sides diverge");
      return false;
    }
  }
  if (std::abs(sides[0].start - sides[1].start) > tolerance ||
      std::abs(sides[0].end - sides[1].end) > tolerance) {
    std::snprintf(g.completion_failure, sizeof(g.completion_failure),
                  "0.3 opposite recovered side intervals disagree");
    return false;
  }
  // Independent single-side recovery has finished. Fuse only compatible
  // tangential intervals; the two measured normals fix the opposite sides.
  const double start = 0.5 * (sides[0].start + sides[1].start);
  const double end = 0.5 * (sides[0].end + sides[1].end);
  const double low = std::min(sides[0].normal, sides[1].normal);
  const double high = std::max(sides[0].normal, sides[1].normal);
  const double width = along_x ? end - start : high - low;
  const double height = along_x ? high - low : end - start;
  if (!AspectMatchesWorkspace(width, height, aspect)) {
    std::snprintf(g.completion_failure, sizeof(g.completion_failure),
        "0.3 spacing conflict: recovered=%.1f measured=%.1f relation=%.2f",
        along_x ? width / aspect : height * aspect, high - low,
        in.workspace_canvas_relation.confidence);
    return false;
  }
  const double l = along_x ? start : low, t = along_x ? low : start;
  auto& frame = g.frame;
  frame = NavigatorViewportFrame{};
  frame.origin_top_left_displayed = {roi.left + 0.5 + ax.x * l + ay.x * t,
                                    roi.top + 0.5 + ax.y * l + ay.y * t};
  frame.axis_x_displayed = {ax.x * width, ax.y * width};
  frame.axis_y_displayed = {ay.x * height, ay.y * height};
  SetCorners(frame);
  g.complete_count = g.partial_count = g.confirmed_corners = 0;
  g.unanchored = 2;
  frame.visible_edge_count = frame.red_evidence.segment_count = 2;
  frame.red_evidence.unanchored_segment_count = 2;
  frame.red_evidence.completion_pattern = g.pattern;
  frame.completion_strategy = static_cast<int>(g.pattern);
  for (int i = 0; i < 2; ++i) {
    g.edges[i].workspace_edge = along_x
        ? (sides[i].normal == low ? kEdgeT : kEdgeB)
        : (sides[i].normal == low ? kEdgeL : kEdgeR);
    frame.red_evidence.segments[i] = g.edges[i].seg;
  }
  frame.confidence = 0.3f * std::min(g.edges[0].seg.support, g.edges[1].seg.support);
  g.completed_ok = true;
  return true;
}

// Recover in the screen viewport's directed basis in thumbnail pixels, never
// in the thumbnail AABB. A main-canvas clockwise rotation gives an inverse
// rotation of screen axes in the (unrotated) navigator.
bool CompleteDirectedGroup(GroupCandidate& g, const ViewportCompletionInput& in,
                           const wb::IntRect& roi) {
  if (g.edges.empty()) return false;
  wb::IntRect canvas_abs = in.navigator_canvas_bounds.Clamp(in.width, in.height);
  if (!canvas_abs.valid()) canvas_abs = roi;
  const wb::IntRect canvas_local{
      canvas_abs.left - roi.left, canvas_abs.top - roi.top,
      canvas_abs.right - roi.left, canvas_abs.bottom - roi.top};
  constexpr double rad = 0.017453292519943295;
  const bool have_angle = in.display_rotation_confidence >= 0.2f &&
                          std::isfinite(in.display_rotation_degrees);
  const double expected = have_angle ? -in.display_rotation_degrees * rad : 0.0;
  const Vec2 hint{std::cos(expected), std::sin(expected)};
  Vec2 ax{g.edges[0].ux, g.edges[0].uy};
  if (std::abs(ax.x * hint.x + ax.y * hint.y) < 0.70710678)
    ax = {-ax.y, ax.x};
  if (ax.x * hint.x + ax.y * hint.y < 0) ax = {-ax.x, -ax.y};
  if (have_angle && ax.x * hint.x + ax.y * hint.y < std::cos(5 * rad)) return false;
  const Vec2 ay{-ax.y, ax.x};
  std::vector<ObservedEdge*> xs, ys; // constant x (L/R), constant y (T/B)
  auto px = [&](const ObservedEdge* e) { return ax.x * EdgePosX(*e) + ax.y * EdgePosY(*e); };
  auto py = [&](const ObservedEdge* e) { return ay.x * EdgePosX(*e) + ay.y * EdgePosY(*e); };
  g.complete_count = g.partial_count = g.unanchored = g.confirmed_corners = 0;
  for (auto& e : g.edges) {
    if (std::abs(e.ux * ax.x + e.uy * ax.y) > 0.70710678) ys.push_back(&e);
    else xs.push_back(&e);
    if (e.complete) ++g.complete_count;
    else if (e.has_start_corner || e.has_end_corner) ++g.partial_count;
    else ++g.unanchored;
    g.confirmed_corners += int(e.has_start_corner) + int(e.has_end_corner);
  }
  if (xs.empty() || ys.empty() || xs.size() > 2 || ys.size() > 2) return false;
  std::sort(xs.begin(), xs.end(), [&](auto a, auto b) { return px(a) < px(b); });
  std::sort(ys.begin(), ys.end(), [&](auto a, auto b) { return py(a) < py(b); });
  double l = px(xs.front()), r = px(xs.back());
  double t = py(ys.front()), b = py(ys.back());
  const auto& wr = in.workspace_canvas_relation.workspace_roi;
  const double aspect = wr.valid() ? double(wr.width()) / wr.height() : 0;
  if (g.edges.size() == 3 && g.complete_count == 0) return false;
  // A clipped three-side U determines one size and which way the absent
  // opposite edge lies. Workspace aspect supplies the other size.
  if (xs.size() == 1 && ys.size() == 2) {
    if (!(aspect > 0 && b - t > 2)) return false;
    const double mid = 0.5 * (px(ys[0]) + px(ys[1]));
    if (std::abs(mid - l) < 2) return false;
    if (mid > l) r = l + (b - t) * aspect;
    else l = r - (b - t) * aspect;
  } else if (ys.size() == 1 && xs.size() == 2) {
    if (!(aspect > 0 && r - l > 2)) return false;
    const double mid = 0.5 * (py(xs[0]) + py(xs[1]));
    if (std::abs(mid - t) < 2) return false;
    if (mid > t) b = t + (r - l) / aspect;
    else t = b - (r - l) / aspect;
  } else if (xs.size() == 1 && ys.size() == 1) {
    // Two adjacent clipped sides still identify one viewport corner.  This is
    // the oblique equivalent of pattern 0.2 in the axis-aligned path. Restore
    // each arm independently with the shared single-side length recovery.
    RecoveredSide horizontal{}, vertical{};
    g.pattern=ViewportCompletionPattern::IntersectingSegmentsNoCompleteEdge;
    if (!RecoverSideLength(*ys.front(), in, canvas_local, ax, ay, true, horizontal) ||
        !RecoverSideLength(*xs.front(), in, canvas_local, ay, ax, false, vertical) ||
        horizontal.length() + kGroupCornerTolPx < horizontal.observed_end-horizontal.observed_start ||
        vertical.length() + kGroupCornerTolPx < vertical.observed_end-vertical.observed_start) {
      std::snprintf(g.completion_failure,sizeof(g.completion_failure),
                    "0.2 independent side recovery conflicts with workspace contact");
      return false;
    }
    const double width = horizontal.length(), height = vertical.length();
    // Each side supplies its own length. Geometry validation may reject a
    // conflict, but must not replace one observation with a shared scale fit.
    if (!AspectMatchesWorkspace(width, height, aspect)) {
      std::snprintf(g.completion_failure,sizeof(g.completion_failure),
          "0.2 independent lengths conflict: %.1fx%.1f workspace aspect=%.3f",width,height,aspect);
      return false;
    }

    double ix = 0, iy = 0;
    if (!LineIntersection(*xs.front(), *ys.front(), ix, iy)) return false;
    const double corner_x = ax.x * ix + ax.y * iy;
    const double corner_y = ay.x * ix + ay.y * iy;
    // The observed arm midpoints tell which direction each side leaves the
    // corner, so this works for all four corners without screen-axis guesses.
    const bool extends_right = px(ys.front()) >= corner_x;
    const bool extends_down = py(xs.front()) >= corner_y;
    l = extends_right ? corner_x : corner_x - width;
    r = l + width;
    t = extends_down ? corner_y : corner_y - height;
    b = t + height;
  }
  if (!(r - l > 2 && b - t > 2)) return false;
  for (auto* e : xs) e->workspace_edge = std::abs(px(e) - l) < std::abs(px(e) - r) ? kEdgeL : kEdgeR;
  for (auto* e : ys) e->workspace_edge = std::abs(py(e) - t) < std::abs(py(e) - b) ? kEdgeT : kEdgeB;
  auto& f = g.frame;
  f = NavigatorViewportFrame{};
  f.origin_top_left_displayed = {roi.left + 0.5 + ax.x * l + ay.x * t,
                                roi.top + 0.5 + ax.y * l + ay.y * t};
  f.axis_x_displayed = {ax.x * (r - l), ax.y * (r - l)};
  f.axis_y_displayed = {ay.x * (b - t), ay.y * (b - t)};
  SetCorners(f);
  g.pattern = g.complete_count >= 4 ? ViewportCompletionPattern::FourCompleteEdges
      : g.complete_count == 3 ? ViewportCompletionPattern::ThreeCompleteEdges
      : g.complete_count == 2 ? ViewportCompletionPattern::TwoIntersectingCompleteEdges
      : g.complete_count == 1 ? ViewportCompletionPattern::OneCompleteEdge
      : ViewportCompletionPattern::IntersectingSegmentsNoCompleteEdge;
  f.completion_strategy = static_cast<int>(g.pattern);
  f.visible_edge_count = static_cast<int>(g.edges.size());
  f.red_evidence.segment_count = f.visible_edge_count;
  for (int i = 0; i < f.visible_edge_count; ++i) f.red_evidence.segments[i] = g.edges[i].seg;
  f.red_evidence.confirmed_complete_edge_count = g.complete_count;
  f.red_evidence.partial_edge_count = g.partial_count;
  f.red_evidence.unanchored_segment_count = g.unanchored;
  f.red_evidence.confirmed_corner_count = g.confirmed_corners;
  f.red_evidence.completion_pattern = g.pattern;
  float min_support = 1.f;
  for (const auto& edge : g.edges) min_support = std::min(min_support, edge.seg.support);
  f.confidence = std::min(
      1.f, (0.15f * f.visible_edge_count + 0.1f * g.complete_count) *
               std::clamp(min_support, 0.25f, 1.f));
  g.completed_ok = true;
  return true;
}

bool CompleteGroupPattern(GroupCandidate& g, const ViewportCompletionInput& in,
                          const wb::IntRect& roi, int rw, int rh,
                          bool* used_crop_correspondence) {
  AnnotateGroupRightAngles(g.edges);
  if (g.edges.size() == 2 && !g.edges[0].complete && !g.edges[1].complete &&
      DirsParallel(g.edges[0].ux, g.edges[0].uy, g.edges[1].ux, g.edges[1].uy)) {
    return CompleteOppositePartialSides(g, in, roi);
  }
  // In 0.1/0.2/0.3 the observed red fragment is scaled by the corresponding
  // displayed-canvas/workspace ratio, then the missing axis follows the
  // workspace aspect.  Do not substitute a whole-workspace prediction here.
  bool oblique = false;
  for (const auto& e : g.edges)
    if (std::abs(e.ux) > 0.04 && std::abs(e.uy) > 0.04) oblique = true;
  if (in.display_rotation_confidence >= 0.2f &&
      std::isfinite(in.display_rotation_degrees) &&
      std::abs(std::remainder(in.display_rotation_degrees, 90.0)) > 0.05) oblique = true;
  if (oblique && g.edges.size() > 1)
    return CompleteDirectedGroup(g, in, roi);

  wb::IntRect canvas_abs = in.navigator_canvas_bounds.Clamp(in.width, in.height);
  if (!canvas_abs.valid()) canvas_abs = roi;
  const wb::IntRect canvas_local{
      canvas_abs.left - roi.left, canvas_abs.top - roi.top,
      canvas_abs.right - roi.left, canvas_abs.bottom - roi.top};

  const int crop_sides = in.workspace_canvas_relation.canvas_crop_sides;
  const bool has_measured_side = std::any_of(g.edges.begin(), g.edges.end(),
                                           [](const ObservedEdge& e) { return e.complete; });
  bool crop_path = false;
  bool rotation_path = false;
  if (crop_sides != 0) {
    const CropAssignResult cr = AssignEdgesByCropCorrespondence(
        g.edges, crop_sides, canvas_local, in.display_rotation_degrees,
        in.display_rotation_confidence);
    if (cr == CropAssignResult::Applied) {
      crop_path = true;
      g.used_crop_correspondence = true;
      if (used_crop_correspondence) *used_crop_correspondence = true;
    } else if (cr == CropAssignResult::Ambiguous && !has_measured_side) {
      return false;
    } else {
      // Failed / NotApplicable：边不够或切不到时回退，勿整组直接杀掉
      const int q =
          ResolveDisplayQuarter(in.display_rotation_degrees, in.display_rotation_confidence);
      if (q >= 0 && AssignEdgesByDisplayRotation(g.edges, q, rw, rh)) {
        rotation_path = true;
      } else if (!AssignGroupWorkspaceEdgesLegacy(g.edges)) {
        return false;
      }
    }
  } else {
    const int q =
        ResolveDisplayQuarter(in.display_rotation_degrees, in.display_rotation_confidence);
    if (q >= 0 && AssignEdgesByDisplayRotation(g.edges, q, rw, rh)) {
      rotation_path = true;
    } else if (!AssignGroupWorkspaceEdgesLegacy(g.edges)) {
      return false;
    }
  }

  if (in.display_rotation_confidence >= 0.2f &&
      (g.edges.size() == 4 ||
       ((g.edges.size() == 3 || g.edges.size() == 2) &&
        in.workspace_canvas_relation.workspace_roi.valid())))
    return CompleteDirectedGroup(g, in, roi);

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

  const auto& workspace = in.workspace_canvas_relation.workspace_roi;
  const double aspect = workspace.valid() ? double(workspace.width()) / workspace.height()
      : in.workspace_canvas_relation.canvas_aspect_ratio > 1e-6
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
    float min_support = 1.f;
    for (const auto& edge : g.edges) min_support = std::min(min_support, edge.seg.support);
    frame.confidence = std::clamp(
        (0.15f * conf_edges + 0.1f * g.complete_count) *
            std::clamp(min_support, 0.25f, 1.f),
        0.f, 1.f);
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

  // 0.2: independently restore the horizontal and vertical side lengths.
  // Use their intersection to place them later; no joint scale averaging.
  auto recover_size_from_orthogonal_pair = [&](const ObservedEdge& v,
                                               const ObservedEdge& h,
                                               double& w, double& hgt) -> bool {
    RecoveredSide horizontal{}, vertical{};
    if (!RecoverSideLength(h, in, canvas_local, {1, 0}, {0, 1}, true, horizontal) ||
        !RecoverSideLength(v, in, canvas_local, {0, 1}, {1, 0}, false, vertical) ||
        horizontal.length() + kGroupCornerTolPx < horizontal.observed_end-horizontal.observed_start ||
        vertical.length() + kGroupCornerTolPx < vertical.observed_end-vertical.observed_start) return false;
    w = horizontal.length();
    hgt = vertical.length();
    return AspectMatchesWorkspace(w, hgt, aspect);
  };

  // Complete one observed fragment into one factual viewport side.  The red
  // fragment inside the Navigator paper corresponds to the interval where the
  // matching workspace boundary touches the visible canvas.  Both interval
  // length and interval offset are required; centering the recovered side on
  // the fragment would invent axial symmetry and lose translation.
  auto complete_partial_edge_as_one = [&](const ObservedEdge& e) -> bool {
    const int role = e.workspace_edge;
    const bool semantic_horizontal = role == kEdgeT || role == kEdgeB;
    const bool semantic_vertical = role == kEdgeL || role == kEdgeR;
    if (!semantic_horizontal && !semantic_vertical) return false;
    if (!workspace.valid()) return false;
    const auto& visible = wcr.visible_canvas_bounds_workspace_local;
    if (!visible.valid()) return false;

    Vec2 ax{}, ay{};
    if (in.display_rotation_confidence >= 0.2f &&
        std::isfinite(in.display_rotation_degrees)) {
      constexpr double kRad = 0.017453292519943295;
      const double a = -in.display_rotation_degrees * kRad;
      ax = {std::cos(a), std::sin(a)};
      ay = {-ax.y, ax.x};
    } else if (semantic_horizontal) {
      // The sign cannot be proven without a directed angle.  Preserve the
      // ordinary screen-forward orientation; importantly, still use the
      // workspace contact offset rather than a symmetric midpoint.
      ax = std::abs(e.ux) >= std::abs(e.uy) ? Vec2{1, 0} : Vec2{0, 1};
      ay = {-ax.y, ax.x};
    } else {
      ay = std::abs(e.ux) >= std::abs(e.uy) ? Vec2{1, 0} : Vec2{0, 1};
      ax = {ay.y, -ay.x};
    }

    const Vec2 tangent = semantic_horizontal ? ax : ay;
    const Vec2 normal = semantic_horizontal ? ay : ax;
    RecoveredSide recovered{};
    if (!RecoverSideLength(e, in, canvas_local, tangent, normal,
                           semantic_horizontal, recovered) ||
        !RecoveredSideContainsObservation(recovered)) return false;
    const double side_length = recovered.length();
    const Vec2 side_start{tangent.x * recovered.start + normal.x * recovered.normal,
                          tangent.y * recovered.start + normal.y * recovered.normal};

    frame = NavigatorViewportFrame{};
    frame.visible_edge_count = static_cast<int>(g.edges.size());
    frame.red_evidence.segment_count = 0;
    for (const auto& observed : g.edges) {
      if (frame.red_evidence.segment_count >= 32) break;
      frame.red_evidence.segments[frame.red_evidence.segment_count++] = observed.seg;
    }
    frame.red_evidence.confirmed_complete_edge_count = 0;
    frame.red_evidence.partial_edge_count = g.partial_count;
    frame.red_evidence.unanchored_segment_count = g.unanchored;
    frame.red_evidence.confirmed_corner_count = g.confirmed_corners;
    set_pattern(ViewportCompletionPattern::ParallelSegmentsNoCompleteEdge);

    if (semantic_horizontal) {
      const double height = side_length / aspect;
      if (!(height > 4.0 && std::isfinite(height))) return false;
      frame.axis_x_displayed = {tangent.x * side_length, tangent.y * side_length};
      frame.axis_y_displayed = {ay.x * height, ay.y * height};
      frame.origin_top_left_displayed = {
          abs_x(side_start.x) - (role == kEdgeB ? frame.axis_y_displayed.x : 0.0),
          abs_y(side_start.y) - (role == kEdgeB ? frame.axis_y_displayed.y : 0.0)};
    } else {
      const double width = side_length * aspect;
      if (!(width > 4.0 && std::isfinite(width))) return false;
      frame.axis_x_displayed = {ax.x * width, ax.y * width};
      frame.axis_y_displayed = {tangent.x * side_length, tangent.y * side_length};
      frame.origin_top_left_displayed = {
          abs_x(side_start.x) - (role == kEdgeR ? frame.axis_x_displayed.x : 0.0),
          abs_y(side_start.y) - (role == kEdgeR ? frame.axis_x_displayed.y : 0.0)};
    }
    return finish_ok(1);
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
      const double l0=std::min(L->seg.y0,L->seg.y1),l1=std::max(L->seg.y0,L->seg.y1);
      const double r0=std::min(R->seg.y0,R->seg.y1),r1=std::max(R->seg.y0,R->seg.y1);
      if(std::abs(l0-r0)>kGroupCornerTolPx || std::abs(l1-r1)>kGroupCornerTolPx) return false;
      // Both opposite sides are complete: their observed endpoints already
      // supply the tangential extent. Aspect is a validator, not a replacement.
      return complete_from_ltrb(left,right,0.5*(l0+r0),0.5*(l1+r1));
    }
    if (T && B && T->complete && B->complete && !(L && L->complete) && !(R && R->complete)) {
      set_pattern(ViewportCompletionPattern::TwoParallelCompleteEdges);
      const double top = std::min(EdgePosY(*T), EdgePosY(*B));
      const double bottom = std::max(EdgePosY(*T), EdgePosY(*B));
      const double t0=std::min(T->seg.x0,T->seg.x1),t1=std::max(T->seg.x0,T->seg.x1);
      const double b0=std::min(B->seg.x0,B->seg.x1),b1=std::max(B->seg.x0,B->seg.x1);
      if(std::abs(t0-b0)>kGroupCornerTolPx || std::abs(t1-b1)>kGroupCornerTolPx) return false;
      return complete_from_ltrb(0.5*(t0+b0),0.5*(t1+b1),top,bottom);
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
      // Both edges are complete red sides, so use their independently fitted
      // centerline lengths.  A complete perpendicular pair is the strongest
      // evidence available here; do not let one side plus aspect replace the
      // other side and silently hide a disagreement.
      double h = EdgeLen(*p.a);
      double w = EdgeLen(*p.b);
      if (!(w > 4.0 && h > 4.0 && std::isfinite(w) && std::isfinite(h))) return false;
      if (aspect > 0 && std::abs((w / h) / aspect - 1.0) > 0.20) return false;
      const bool left = p.a->workspace_edge == kEdgeL
          ? true
          : p.a->workspace_edge == kEdgeR
              ? false
              : (0.5 * (canvas_local.left + canvas_local.right) > vx);
      const bool top = p.b->workspace_edge == kEdgeT
          ? true
          : p.b->workspace_edge == kEdgeB
              ? false
              : (0.5 * (canvas_local.top + canvas_local.bottom) > hy);
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
    // 1.0 means this *whole* viewport side was observed.  Its two endpoints
    // already fix the tangential component of the viewport frame, so it must
    // never go through the 0.x fragment-scale recovery or be re-centred.
    // Preserve that side verbatim and complete only its missing normal axis
    // from the workspace aspect; the resulting frame then goes straight to
    // the common Screen→Canvas matrix solver.
    const double edge_len = EdgeLen(*e);
    if (!(edge_len > 4.0) || !std::isfinite(edge_len)) return false;
    double interior_direction=0;
    for(const auto& neighbor:g.edges) {
      if(&neighbor==e || !OrthogonalAdjacent(*e,neighbor)) continue;
      const double delta=e->seg.horizontal ? EdgePosY(neighbor)-EdgePosY(*e)
                                           : EdgePosX(neighbor)-EdgePosX(*e);
      if(std::abs(delta)<=2) continue;
      if(interior_direction*delta<0) return false;
      interior_direction=delta;
    }
    if(interior_direction==0) return false;
    if (e->seg.horizontal) {
      const double w = edge_len;
      const double h = w / aspect;
      if (!(h > 4.0) || !std::isfinite(h)) return false;
      const double left = std::min(e->seg.x0, e->seg.x1);
      const double ey = EdgePosY(*e);
      const bool is_top = interior_direction > 0;
      frame.origin_top_left_displayed = {abs_x(left), is_top ? abs_y(ey) : abs_y(ey) - h};
      frame.axis_x_displayed = {w, 0};
      frame.axis_y_displayed = {0, h};
    } else {
      const double h = edge_len;
      const double w = h * aspect;
      if (!(w > 4.0) || !std::isfinite(w)) return false;
      const double ex = EdgePosX(*e);
      const double top = std::min(e->seg.y0, e->seg.y1);
      const bool is_left = interior_direction > 0;
      frame.origin_top_left_displayed = {is_left ? abs_x(ex) : abs_x(ex) - w, abs_y(top)};
      frame.axis_x_displayed = {w, 0};
      frame.axis_y_displayed = {0, h};
    }
    return finish_ok(1);
  }

  // complete_count == 0: 0.1 is a single side. Opposite pairs already use
  // 0.3 above; adjacent pairs independently recover their lengths in 0.2.
  const bool intersecting = (n_h > 0 && n_v > 0);
  if (!intersecting) {
    if (g.edges.size() != 1) return false;
    set_pattern(ViewportCompletionPattern::ParallelSegmentsNoCompleteEdge);
    return complete_partial_edge_as_one(g.edges.front());
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
  if (!v || !hz) return false;

  double w = 0, h = 0;
  if (!recover_size_from_orthogonal_pair(*v, *hz, w, h)) return false;

  // A visible L should meet at its inferred corner.  With a crop path the
  // corner may be clipped out of the navigator canvas, so the two infinite
  // centerlines are still valid evidence; without crop correspondence keep
  // the stricter endpoint-adjacency guard.
  double ix = 0, iy = 0;
  if (!LineIntersection(*v, *hz, ix, iy)) return false;
  if (!crop_path && best_dist > kGroupCornerTolPx) return false;

  const double vx = ix;
  const double hy = iy;
  const bool left = v->workspace_edge == kEdgeL
      ? true
      : v->workspace_edge == kEdgeR
          ? false
          : (0.5 * (canvas_local.left + canvas_local.right) > vx);
  const bool top = hz->workspace_edge == kEdgeT
      ? true
      : hz->workspace_edge == kEdgeB
          ? false
          : (0.5 * (canvas_local.top + canvas_local.bottom) > hy);
  frame.origin_top_left_displayed = {left ? abs_x(vx) : abs_x(vx) - w,
                                     top ? abs_y(hy) : abs_y(hy) - h};
  frame.axis_x_displayed = {w, 0};
  frame.axis_y_displayed = {0, h};
  return finish_ok(0);
}

// Hough seeds can miss an entire pale AA side. Existing factual corners give
// bounded line probes; a completed frame alone never supplies a red edge.
void RecoverMissingObservedEdges(GroupCandidate& g,const ViewportCompletionInput& in,
                                 const wb::IntRect& roi,int rw,int rh) {
  if (g.edges.size()<2 || g.edges.size()>=4 || g.confirmed_corners<2) return;
  const GroupCandidate original=g;
  const BgraView capture{in.bgra,in.width,in.height,in.stride,0,0};
  const double w=g.frame.width,h=g.frame.height;
  if (!(w>0 && h>0)) return;
  const Vec2 ax{g.frame.axis_x_displayed.x/w,g.frame.axis_x_displayed.y/w};
  const Vec2 ay{g.frame.axis_y_displayed.x/h,g.frame.axis_y_displayed.y/h};
  std::vector<std::pair<Vec2,Vec2>> probes;
  auto rectangle_probes=[&](double l,double r,double t,double b) {
    auto point=[&](double x,double y) { return Vec2{ax.x*x+ay.x*y,ax.y*x+ay.y*y}; };
    const Vec2 corners[]={point(l,t),point(r,t),point(r,b),point(l,b)};
    for (int i=0;i<4;++i) probes.push_back({corners[i],corners[(i+1)%4]});
  };
  const auto& origin=g.frame.origin_top_left_displayed;
  const double l=origin.x*ax.x+origin.y*ax.y,t=origin.x*ay.x+origin.y*ay.y;
  rectangle_probes(l,l+w,t,t+h);
  // Fragment-scale completion can be inaccurate when WCR is clipped or low
  // confidence. Also probe the actual observed endpoints. Their envelope is
  // a search hint, not a claim that either unobserved side exists.
  double raw_l=std::numeric_limits<double>::infinity(),raw_r=-raw_l;
  double raw_t=raw_l,raw_b=raw_r;
  for (const auto& e:g.edges) {
    for (Vec2 p:{Vec2{e.seg.x0+roi.left,e.seg.y0+roi.top},
                Vec2{e.seg.x1+roi.left,e.seg.y1+roi.top}}) {
      const double x=p.x*ax.x+p.y*ax.y,y=p.x*ay.x+p.y*ay.y;
      raw_l=std::min(raw_l,x);raw_r=std::max(raw_r,x);
      raw_t=std::min(raw_t,y);raw_b=std::max(raw_b,y);
    }
  }
  if (raw_r-raw_l>kObservedMinLengthPx && raw_b-raw_t>kObservedMinLengthPx)
    rectangle_probes(raw_l,raw_r,raw_t,raw_b);
  for (const auto& probe:probes) {
    if (g.edges.size()>=4) break;
    const Vec2 p0=probe.first,p1=probe.second;
    const double length=std::hypot(p1.x-p0.x,p1.y-p0.y);
    if (length<kObservedMinLengthPx) continue;
    ObservedEdge candidate;
    candidate.ux=(p1.x-p0.x)/length;candidate.uy=(p1.y-p0.y)/length;
    candidate.nx=-candidate.uy;candidate.ny=candidate.ux;
    const double coord=p0.x*candidate.nx+p0.y*candidate.ny;
    auto already_observed=[&](const ObservedEdge& e) {
      return DirsParallel(e.ux,e.uy,candidate.ux,candidate.uy) &&
          std::abs((EdgePosX(e)+roi.left)*candidate.nx+
                   (EdgePosY(e)+roi.top)*candidate.ny-coord)<=kPeakMergeDist;
    };
    if (std::any_of(g.edges.begin(),g.edges.end(),already_observed)) continue;
    double cut0=0,cut1=0;
    if (!ClipLineToImage(p0.x,p0.y,candidate.ux,candidate.uy,
                         in.width,in.height,cut0,cut1)) continue;
    cut0=std::max(0.0,cut0);cut1=std::min(length,cut1);
    if (cut1-cut0<kObservedMinLengthPx) continue;
    candidate.seg.x0=p0.x+candidate.ux*cut0;candidate.seg.y0=p0.y+candidate.uy*cut0;
    candidate.seg.x1=p0.x+candidate.ux*cut1;candidate.seg.y1=p0.y+candidate.uy*cut1;
    if (!RefineAndValidateStraightLine(candidate,capture) || candidate.seg.support<0.75f) continue;
    const double observed_length=EdgeLen(candidate);
    const Vec2 first{p0.x+(p1.x-p0.x)*cut0/length,p0.y+(p1.y-p0.y)*cut0/length};
    const Vec2 last{p0.x+(p1.x-p0.x)*cut1/length,p0.y+(p1.y-p0.y)*cut1/length};
    const double start=(first.x-candidate.seg.x0)*candidate.ux+(first.y-candidate.seg.y0)*candidate.uy;
    const double end=(last.x-candidate.seg.x0)*candidate.ux+(last.y-candidate.seg.y0)*candidate.uy;
    // Both extent and position need pixel support. Long unrelated strokes or
    // a small red speck somewhere on a predicted side cannot be promoted.
    const double lo=std::min(start,end),hi=std::max(start,end);
    if (std::abs(first.x*candidate.nx+first.y*candidate.ny-candidate.coord)>kPeakMergeDist ||
        std::abs(last.x*candidate.nx+last.y*candidate.ny-candidate.coord)>kPeakMergeDist ||
        observed_length<0.75*(cut1-cut0) || lo<-kGroupCornerTolPx ||
        lo>kGroupCornerTolPx || hi<observed_length-kGroupCornerTolPx ||
        hi>observed_length+kGroupCornerTolPx) continue;
    candidate.seg.x0-=roi.left;candidate.seg.x1-=roi.left;
    candidate.seg.y0-=roi.top;candidate.seg.y1-=roi.top;
    candidate.coord-=roi.left*candidate.nx+roi.top*candidate.ny;
    candidate.seg.corner_at_start=-1;candidate.seg.corner_at_end=-1;
    g.edges.push_back(candidate);
  }
  if (g.edges.size()!=original.edges.size()) {
    if (!CompleteGroupPattern(g,in,roi,rw,rh,nullptr)) g=original;
    else g.recovered_edge_count+=int(g.edges.size()-original.edges.size());
  }
}

// 同向共线且端点接近/投影重叠的红段合并为一条，减少断缝假多组。
bool CanMergeCollinear(const ObservedEdge& a, const ObservedEdge& b) {
  if (!DirsParallel(a.ux, a.uy, b.ux, b.uy)) return false;
  // Signed Hough normals may flip near 90 degrees. Compare physical points,
  // and require both endpoints to agree, so two diverging strokes cannot merge.
  auto near_line = [](const ObservedEdge& line, const ObservedEdge& segment) {
    return std::abs(Dot2(segment.seg.x0, segment.seg.y0, line.nx, line.ny) - line.coord) <= kPeakMergeDist &&
           std::abs(Dot2(segment.seg.x1, segment.seg.y1, line.nx, line.ny) - line.coord) <= kPeakMergeDist;
  };
  if (!near_line(a,b) || !near_line(b,a)) return false;
  const double ux = a.ux, uy = a.uy;
  const double a0 = Dot2(a.seg.x0, a.seg.y0, ux, uy);
  const double a1 = Dot2(a.seg.x1, a.seg.y1, ux, uy);
  const double b0 = Dot2(b.seg.x0, b.seg.y0, ux, uy);
  const double b1 = Dot2(b.seg.x1, b.seg.y1, ux, uy);
  const double lo_a = std::min(a0, a1), hi_a = std::max(a0, a1);
  const double lo_b = std::min(b0, b1), hi_b = std::max(b0, b1);
  const double gap = std::max(0.0, std::max(lo_a, lo_b) - std::min(hi_a, hi_b));
  if (gap > kCollinearGapMaxPx) return false;
  return true;
}

ObservedEdge MergeCollinearPair(const ObservedEdge& a, const ObservedEdge& b) {
  ObservedEdge out = a;
  const double ux = a.ux, uy = a.uy;
  const double a0 = Dot2(a.seg.x0, a.seg.y0, ux, uy);
  const double a1 = Dot2(a.seg.x1, a.seg.y1, ux, uy);
  const double b0 = Dot2(b.seg.x0, b.seg.y0, ux, uy);
  const double b1 = Dot2(b.seg.x1, b.seg.y1, ux, uy);
  const double lo = std::min(std::min(a0, a1), std::min(b0, b1));
  const double hi = std::max(std::max(a0, a1), std::max(b0, b1));
  const double len_a = EdgeLen(a), len_b = EdgeLen(b);
  const double mid = (a.coord * len_a +
      Dot2(EdgePosX(b), EdgePosY(b), a.nx, a.ny) * len_b) / (len_a + len_b);
  // 原点取 a 线段在法向上的落点近似：p = mid*n + t*u
  out.coord = mid;
  out.seg.x0 = mid * a.nx + lo * ux;
  out.seg.y0 = mid * a.ny + lo * uy;
  out.seg.x1 = mid * a.nx + hi * ux;
  out.seg.y1 = mid * a.ny + hi * uy;
  out.seg.horizontal = a.seg.horizontal;
  out.seg.support = static_cast<float>((a.seg.support*len_a + b.seg.support*len_b)/(len_a+len_b));
  out.ux = a.ux;
  out.uy = a.uy;
  out.nx = a.nx;
  out.ny = a.ny;
  out.family = a.family;
  return out;
}

void MergeCollinearNearbyEdges(std::vector<ObservedEdge>& pool) {
  bool changed = true;
  while (changed) {
    changed = false;
    for (size_t i = 0; i < pool.size() && !changed; ++i) {
      for (size_t j = i + 1; j < pool.size(); ++j) {
        if (!CanMergeCollinear(pool[i], pool[j])) continue;
        pool[i] = MergeCollinearPair(pool[i], pool[j]);
        pool.erase(pool.begin() + static_cast<std::ptrdiff_t>(j));
        changed = true;
        break;
      }
    }
  }
}

double ParallelLinkMaxSide(const ObservedEdge& e, double max_w, double max_h) {
  // 法向更偏水平 → 对边间距受导航器宽度约束；否则受高度约束。
  return (std::abs(e.nx) >= std::abs(e.ny)) ? max_w : max_h;
}

bool EdgesAdjacencyLinkable(const ObservedEdge& a, const ObservedEdge& b, double max_w,
                            double max_h) {
  if (OrthogonalAdjacent(a, b)) return true;
  if (DirsParallel(a.ux, a.uy, b.ux, b.uy)) {
    return ParallelPairOk(a, b, ParallelLinkMaxSide(a, max_w, max_h));
  }
  return false;
}

struct EdgeDsu {
  std::vector<int> p;
  explicit EdgeDsu(int n) : p(static_cast<size_t>(n)) {
    for (int i = 0; i < n; ++i) p[static_cast<size_t>(i)] = i;
  }
  int Find(int x) {
    if (p[static_cast<size_t>(x)] != x) p[static_cast<size_t>(x)] = Find(p[static_cast<size_t>(x)]);
    return p[static_cast<size_t>(x)];
  }
  void Unite(int a, int b) {
    a = Find(a);
    b = Find(b);
    if (a != b) p[static_cast<size_t>(a)] = b;
  }
};

bool GroupValidForEmit(const std::vector<ObservedEdge>& edges, double max_w, double max_h,
                       const wb::IntRect& canvas_local) {
  return GroupEdgeCardinalityOk(edges) && GroupSpatialGeometryOk(edges, max_w, max_h, canvas_local);
}

// 分量过大时：按边长贪心加入，保持仍为合法单矩形假设（不再做全子集枚举）。
std::vector<ObservedEdge> GreedyValidSubset(const std::vector<ObservedEdge>& edges, double max_w,
                                            double max_h, const wb::IntRect& canvas_local) {
  std::vector<int> order(edges.size());
  for (size_t i = 0; i < edges.size(); ++i) order[i] = static_cast<int>(i);
  std::sort(order.begin(), order.end(), [&](int ia, int ib) {
    return EdgeLen(edges[static_cast<size_t>(ia)]) > EdgeLen(edges[static_cast<size_t>(ib)]);
  });
  std::vector<ObservedEdge> chosen;
  for (int idx : order) {
    std::vector<ObservedEdge> trial = chosen;
    trial.push_back(edges[static_cast<size_t>(idx)]);
    if (GroupValidForEmit(trial, max_w, max_h, canvas_local)) chosen = std::move(trial);
  }
  return chosen;
}

// Merge physical collinear strokes, recover closed corner cycles, then group
// the remaining fragments. Oversized components retain all unchosen evidence.
std::vector<GroupCandidate> ClusterGroupsByAdjacency(std::vector<ObservedEdge> pool, double max_w,
                                                     double max_h,
                                                     const wb::IntRect& canvas_local) {
  const int n = static_cast<int>(pool.size());
  std::vector<GroupCandidate> out;
  if (n == 0) return out;

  // Recover closed four-corner cycles before connected components. A parallel
  // artwork stroke can bridge two otherwise independent rectangles in a DSU;
  // that bridge must not destroy either rectangle's four factual corners.
  std::vector<uint8_t> accounted(static_cast<size_t>(n),0);
  std::vector<std::vector<int>> cycles;
  for (int i=0;i<n;++i) for (int j=i+1;j<n;++j) {
    if (!DirsParallel(pool[i].ux,pool[i].uy,pool[j].ux,pool[j].uy)) continue;
    for (int k=0;k<n;++k) {
      if (k==i || k==j || !DirsOrthogonal(pool[i].ux,pool[i].uy,pool[k].ux,pool[k].uy)) continue;
      for (int l=k+1;l<n;++l) {
        if (l==i || l==j || !DirsParallel(pool[k].ux,pool[k].uy,pool[l].ux,pool[l].uy)) continue;
        if (!OrthogonalAdjacent(pool[i],pool[k]) || !OrthogonalAdjacent(pool[i],pool[l]) ||
            !OrthogonalAdjacent(pool[j],pool[k]) || !OrthogonalAdjacent(pool[j],pool[l])) continue;
        std::vector<int> ids{i,j,k,l};
        std::sort(ids.begin(),ids.end());
        if (std::find(cycles.begin(),cycles.end(),ids)!=cycles.end()) continue;
        std::vector<ObservedEdge> edges{pool[i],pool[j],pool[k],pool[l]};
        if (!GroupValidForEmit(edges,max_w,max_h,canvas_local)) continue;
        cycles.push_back(ids);
        GroupCandidate g;
        g.indices=ids; g.edges=std::move(edges);
        out.push_back(std::move(g));
        for (int id:ids) accounted[id]=1;
      }
    }
  }

  EdgeDsu dsu(n);
  for (int i = 0; i < n; ++i) {
    if (accounted[i]) continue;
    for (int j = i + 1; j < n; ++j) {
      if (accounted[j]) continue;
      if (EdgesAdjacencyLinkable(pool[static_cast<size_t>(i)], pool[static_cast<size_t>(j)], max_w,
                                 max_h)) {
        dsu.Unite(i, j);
      }
    }
  }

  std::vector<std::vector<int>> comps(static_cast<size_t>(n));
  for (int i = 0; i < n; ++i) {
    if (accounted[i]) continue;
    comps[static_cast<size_t>(dsu.Find(i))].push_back(i);
  }

  for (const auto& idxs : comps) {
    if (idxs.empty()) continue;
    std::vector<ObservedEdge> edges;
    edges.reserve(idxs.size());
    for (int i : idxs) edges.push_back(pool[static_cast<size_t>(i)]);

    while (!edges.empty()) {
      auto chosen = GroupValidForEmit(edges,max_w,max_h,canvas_local) ? edges
          : GreedyValidSubset(edges,max_w,max_h,canvas_local);
      if (chosen.empty()) break;
      // Preserve the unchosen evidence for another group. Previously the first
      // greedy subset discarded every other stroke in the connected component.
      edges.erase(std::remove_if(edges.begin(),edges.end(),[&](const ObservedEdge& e) {
        return std::any_of(chosen.begin(),chosen.end(),[&](const ObservedEdge& c) {
          return e.seg.x0==c.seg.x0 && e.seg.y0==c.seg.y0 &&
                 e.seg.x1==c.seg.x1 && e.seg.y1==c.seg.y1;
        });
      }),edges.end());
      GroupCandidate g;
      g.edges=std::move(chosen);
      out.push_back(std::move(g));
    }
  }
  return out;
}

}  // namespace

ViewportCompletionResult CompleteViewportFrame(const ViewportCompletionInput& in) {
  if (!in.bgra || in.width <= 0 || in.height <= 0 ||
      in.width>std::numeric_limits<int>::max()/4 || in.stride<in.width*4 ||
      !in.thumbnail_roi.valid()) {
    return Fail(FailStatus::InvalidCapture, "invalid viewport input");
  }

  const wb::IntRect roi = in.thumbnail_roi.Clamp(in.width, in.height);
  if (!roi.valid()) return Fail(FailStatus::InsufficientViewportGeometry, "thumbnail roi empty");

  const int rw = roi.width();
  const int rh = roi.height();

  std::vector<float> red_weight(static_cast<size_t>(rw) * rh, 0.f);
  std::vector<uint8_t> narrow_red(static_cast<size_t>(rw) * rh, 0);
  const BgraView thumbnail{in.bgra, rw, rh, in.stride, roi.left, roi.top};
  const BgraView capture{in.bgra, in.width, in.height, in.stride, 0, 0};
  int red_count = 0;
  for (int y = roi.top; y < roi.bottom; ++y) {
    for (int x = roi.left; x < roi.right; ++x) {
      const uint8_t* p =
          in.bgra + static_cast<size_t>(y) * in.stride + static_cast<size_t>(x) * 4;
      const float weight = NavigatorRedWeight(p);
      const size_t index = static_cast<size_t>(y - roi.top) * rw + (x - roi.left);
      red_weight[index] = weight;
      if (weight >= kRawRedWeightMin) {
        ++red_count;
      }
      if (IsNarrowNavigatorRedPixel(p)) narrow_red[index] = 1;
    }
  }
  if (red_count < kMinRawRedPixels) {
    return Fail(FailStatus::InsufficientViewportGeometry, "no red edge pixels");
  }

  const int narrow_count = static_cast<int>(
      std::count(narrow_red.begin(), narrow_red.end(), static_cast<uint8_t>(1)));
  if (narrow_count < kMinRawRedPixels) {
    return Fail(FailStatus::InsufficientViewportGeometry, "no narrow-red seed pixels");
  }

  // A. 窄红核心投票确定直线，沿线用抗锯齿覆盖轮廓生长观测红边。
  std::vector<ObservedEdge> pool;
  DetectGlobalRedLines(red_weight, narrow_red, thumbnail, pool);
  for (auto& edge : pool) ExtendObservedLinePastThumbnail(edge, roi, capture);
  if (in.display_rotation_confidence >= 0.2f &&
      std::isfinite(in.display_rotation_degrees)) {
    pool.erase(std::remove_if(pool.begin(), pool.end(), [&](const ObservedEdge& edge) {
      return !RedLineMatchesDisplayRotation(edge, in.display_rotation_degrees);
    }), pool.end());
  }
  if (pool.empty()) {
    return Fail(FailStatus::InsufficientViewportGeometry, "no global red line segments");
  }
  MergeCollinearNearbyEdges(pool);
  // Recheck merged/extended observations against the entire frozen capture.
  // Merging must not turn offset pieces of a bent stroke into a straight side.
  pool.erase(std::remove_if(pool.begin(),pool.end(),[&](ObservedEdge& edge) {
    edge.seg.x0+=roi.left;edge.seg.x1+=roi.left;
    edge.seg.y0+=roi.top;edge.seg.y1+=roi.top;
    if (!RefineAndValidateStraightLine(edge,capture)) return true;
    edge.seg.x0-=roi.left;edge.seg.x1-=roi.left;
    edge.seg.y0-=roi.top;edge.seg.y1-=roi.top;
    edge.coord-=roi.left*edge.nx+roi.top*edge.ny;
    return in.display_rotation_confidence>=0.2f &&
        !RedLineMatchesDisplayRotation(edge,in.display_rotation_degrees);
  }),pool.end());
  if (pool.empty())
    return Fail(FailStatus::InsufficientViewportGeometry,"no sufficiently straight red line segments");
  RecoverEndpointAdjacentRedLines(pool, roi, capture);
  if (in.display_rotation_confidence >= 0.2f &&
      std::isfinite(in.display_rotation_degrees)) {
    pool.erase(std::remove_if(pool.begin(), pool.end(), [&](const ObservedEdge& edge) {
      return !RedLineMatchesDisplayRotation(edge, in.display_rotation_degrees);
    }), pool.end());
  }

  wb::IntRect canvas = in.navigator_canvas_bounds.Clamp(in.width, in.height);
  if (!canvas.valid()) canvas = roi;
  const wb::IntRect canvas_local{
      canvas.left - roi.left, canvas.top - roi.top,
      canvas.right - roi.left, canvas.bottom - roi.top};
  // 导航器画布在 ROI 局部坐标下的边长上限（平行对边间距）
  const double nav_w_local =
      static_cast<double>(std::max(1, std::min(canvas.right, roi.right) - std::max(canvas.left, roi.left)));
  const double nav_h_local =
      static_cast<double>(std::max(1, std::min(canvas.bottom, roi.bottom) - std::max(canvas.top, roi.top)));

  // B. Physical line merging, closed cycles, and remaining fragment groups.
  auto groups = ClusterGroupsByAdjacency(std::move(pool), nav_w_local, nav_h_local, canvas_local);
  if (groups.empty()) {
    return Fail(FailStatus::AmbiguousViewportGeometry, "no valid red frame edge group");
  }

  const auto& wcr = in.workspace_canvas_relation;
  const auto& workspace=wcr.workspace_roi;
  const double workspace_aspect=workspace.valid() ? double(workspace.width())/workspace.height() : 0;
  double W_nav=0,H_nav=0;
  bool theory_available=false;
  const auto& visible=wcr.visible_canvas_bounds_workspace_local;
  // A viewport describes the whole workspace, including its gray margins.
  // Visible/full-paper fractions therefore cannot be its dimensions. An
  // uncropped paper axis supplies a scale, which also works for viewports
  // extending outside the paper. Oblique AABBs do not supply that scale.
  const double angle=std::remainder(double(in.display_rotation_degrees),360.0);
  const int quarter=std::isfinite(angle) ? int(std::lround(angle/90.0)) : 0;
  if (workspace.valid() && visible.valid() && !wcr.ambiguous &&
      in.display_rotation_confidence>=0.2f && std::isfinite(angle) &&
      std::abs(angle-quarter*90.0)<0.05) {
    const bool swap=(std::abs(quarter)%2)!=0;
    const bool full_x=(wcr.canvas_crop_sides&(kEdgeL|kEdgeR))==0;
    const bool full_y=(wcr.canvas_crop_sides&(kEdgeT|kEdgeB))==0;
    const double sx=(swap ? canvas.height() : canvas.width())/double(visible.width());
    const double sy=(swap ? canvas.width() : canvas.height())/double(visible.height());
    double scale=full_x ? sx : full_y ? sy : 0;
    if (full_x && full_y && std::abs(sx/sy-1.0)>0.05) scale=0;
    if (std::isfinite(scale) && scale>0) {
      W_nav=workspace.width()*scale; H_nav=workspace.height()*scale;
      theory_available=true;
    }
  } else if (!visible.valid() && !wcr.full_canvas_model_workspace_local.valid() &&
             wcr.visible_canvas_fraction_x>1e-4f && wcr.visible_canvas_fraction_y>1e-4f) {
    // Legacy standalone callers can supply normalized viewport dimensions.
    // Real pipeline relations carry visible bounds and use the scale above.
    W_nav=nav_w_local*wcr.visible_canvas_fraction_x;
    H_nav=nav_h_local*wcr.visible_canvas_fraction_y;
    theory_available=std::isfinite(W_nav) && std::isfinite(H_nav);
  }

  // C+D. 组内直角 → 完整边 → pattern 补全
  std::vector<GroupCandidate*> survivors;
  for (auto& g : groups) {
    g.recovered_edge_count = static_cast<int>(std::count_if(
        g.edges.begin(), g.edges.end(), [](const ObservedEdge& e) { return e.recovered_from_corner; }));
    if (!CompleteGroupPattern(g, in, roi, rw, rh, nullptr)) continue;
    RecoverMissingObservedEdges(g,in,roi,rw,rh);
    // Multiple extraction paths can describe the same physical frame. They
    // are duplicate evidence, not competing viewport locations.
    auto same_frame=[&](const GroupCandidate* other) {
      auto near=[](Vec2 a,Vec2 b) { return std::hypot(a.x-b.x,a.y-b.y)<=3.0; };
      return near(g.frame.origin_top_left_displayed,other->frame.origin_top_left_displayed) &&
             near(g.frame.axis_x_displayed,other->frame.axis_x_displayed) &&
             near(g.frame.axis_y_displayed,other->frame.axis_y_displayed);
    };
    auto duplicate=std::find_if(survivors.begin(),survivors.end(),same_frame);
    if (duplicate!=survivors.end()) {
      if (g.complete_count>(*duplicate)->complete_count ||
          (g.complete_count==(*duplicate)->complete_count && g.edges.size()>(*duplicate)->edges.size()))
        *duplicate=&g;
      continue;
    }
    survivors.push_back(&g);
  }

  // E. Validate workspace geometry; retain ambiguity between distinct frames.
  if (survivors.empty()) {
    for (const auto& g : groups) {
      if (g.completion_failure[0])
        return Fail(FailStatus::AmbiguousViewportGeometry, g.completion_failure);
    }
    return Fail(FailStatus::AmbiguousViewportGeometry, "no group completed via pattern");
  }

  GroupCandidate* target = nullptr;
  int fragment_fallbacks = 0;
  const bool validate_theory=theory_available && wcr.confidence>=0.5f;
  if (survivors.size() == 1 &&
      ShapeMatchesWorkspaceViewport(survivors[0]->frame.width,survivors[0]->frame.height,
          W_nav,H_nav,workspace_aspect,validate_theory)) {
    target = survivors[0];
  } else {
    if (!theory_available && workspace_aspect<=0) {
      return Fail(FailStatus::AmbiguousViewportGeometry,
                  "displayed canvas shape unavailable for disambiguation");
    }
    std::vector<GroupCandidate*> S;
    // Legacy standalone inputs can provide only explicit normalized sizes
    // for disambiguation. Real pipeline WCR estimates need the same confidence
    // gate regardless of whether there is one candidate or several.
    const bool validate_multiple=validate_theory || (theory_available && !visible.valid());
    for (auto* g : survivors) {
      if (ShapeMatchesWorkspaceViewport(g->frame.width, g->frame.height, W_nav, H_nav,
                                        workspace_aspect, validate_multiple)) {
        S.push_back(g);
      }
    }
    // A complete edge is a measured viewport span between two observed
    // corners. In 0.x the missing extent is instead constructed from
    // fragment length and workspace aspect, so passing that same aspect
    // check supplies no independent evidence. Use such fragments as fallback
    // only when no shape-compatible group has a measured complete side.
    // Multiple anchored frames remain competitors regardless of their size
    // or number of complete edges; two real rectangles must stay ambiguous.
    if (std::any_of(S.begin(),S.end(),[](const GroupCandidate* g) {
          return g->complete_count>0;
        })) {
      const size_t before=S.size();
      S.erase(std::remove_if(S.begin(),S.end(),[](const GroupCandidate* g) {
        return g->complete_count==0;
      }),S.end());
      fragment_fallbacks=int(before-S.size());
    } else if (std::any_of(S.begin(),S.end(),[](const GroupCandidate* g) {
          return g->pattern==ViewportCompletionPattern::IntersectingSegmentsNoCompleteEdge ||
                 g->pattern==ViewportCompletionPattern::OppositeSegmentsNoCompleteEdge;
        })) {
      // 0.2 supplies two independently measured lengths and a corner; 0.3
      // additionally observes both opposing normals. A lone 0.1 fragment has
      // only one observation, so it is a fallback to these compatible pairs.
      const size_t before=S.size();
      S.erase(std::remove_if(S.begin(),S.end(),[](const GroupCandidate* g) {
        return g->pattern==ViewportCompletionPattern::ParallelSegmentsNoCompleteEdge;
      }),S.end());
      fragment_fallbacks=int(before-S.size());
    }
    if (S.size() == 1) {
      target = S[0];
    } else if (S.empty()) {
      char message[128];
      std::snprintf(message,sizeof(message),
          "viewport shape mismatch: expected %.1fx%.1f aspect=%.3f completed=%d",
          W_nav,H_nav,workspace_aspect,int(survivors.size()));
      return Fail(FailStatus::AmbiguousViewportGeometry, message);
    } else {
      // Color can reject UI/artwork with different chroma. Pure-red artwork
      // shares the frame color, so it cannot replace the geometry decision.
      std::vector<GroupCandidate*> N;
      for (auto* g : S) {
        if (GroupNarrowRedChromaOk(*g, in, roi)) N.push_back(g);
      }
      if (N.size() == 1) {
        target = N[0];
      } else {
        char message[128];
        const int measured=int(std::count_if(S.begin(),S.end(),[](const GroupCandidate* g) {
          return g->complete_count>0;
        }));
        std::snprintf(message,sizeof(message),
            "ambiguous red viewport groups: shape=%d red=%d measured=%d fallback=%d",
            int(S.size()),int(N.size()),measured,fragment_fallbacks);
        return Fail(FailStatus::AmbiguousViewportGeometry,message);
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
                  "ok crop_correspondence pattern=%d complete=%d segs=%d groups=%d fallback=%d recovered=%d",
                  r.frame.completion_strategy, target->complete_count,
                  static_cast<int>(target->edges.size()), static_cast<int>(survivors.size()),fragment_fallbacks,
                  target->recovered_edge_count);
  } else {
    std::snprintf(r.message, sizeof(r.message),
                  "ok legacy_group_sort pattern=%d complete=%d segs=%d groups=%d fallback=%d recovered=%d",
                  r.frame.completion_strategy, target->complete_count,
                  static_cast<int>(target->edges.size()), static_cast<int>(survivors.size()),fragment_fallbacks,
                  target->recovered_edge_count);
  }
  return r;
}

}  // namespace sct
