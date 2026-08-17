#include "wb/refine.hpp"

#include "wb/color.hpp"

#include <algorithm>
#include <cmath>
#include <vector>

namespace wb {
namespace {

struct Cost {
  int coord = 0;
  float score = 0;
  bool operator<(const Cost& o) const { return score > o.score; }
};

float SideCostAt(const FeatureMaps& features, const BackgroundModel& model, OuterSide side,
                 int coord, int along0, int along1) {
  const int w = features.width;
  const int h = features.height;
  if (along1 <= along0 + 2) return -1e9f;
  const float weak = std::max(model.weak_delta_e, 1e-3f);
  double acc = 0;
  int n = 0;
  const int step = std::max(1, (along1 - along0) / 48);

  if (side == OuterSide::Left || side == OuterSide::Right) {
    if (coord < 1 || coord >= w - 1) return -1e9f;
    for (int y = along0; y < along1; y += step) {
      const int yy = std::max(0, std::min(h - 1, y));
      const int xin = (side == OuterSide::Left) ? std::min(w - 1, coord + 1) : std::max(0, coord - 1);
      const int xout =
          (side == OuterSide::Left) ? std::max(0, coord - 1) : std::min(w - 1, coord + 1);
      const float de_in = DeltaE76(features.lab.At(xin, yy), model.center_lab);
      const float de_out = DeltaE76(features.lab.At(xout, yy), model.center_lab);
      const float in_sim = std::max(0.f, std::min(1.f, 1.f - de_in / weak));
      const float out_diff = std::max(0.f, std::min(1.f, de_out / weak));
      const float g = std::fabs(features.gradient_x.At(coord, yy));
      acc += 0.45f * in_sim + 0.40f * out_diff + 0.15f * std::min(1.f, g / 80.f);
      ++n;
    }
  } else {
    if (coord < 1 || coord >= h - 1) return -1e9f;
    for (int x = along0; x < along1; x += step) {
      const int xx = std::max(0, std::min(w - 1, x));
      const int yin = (side == OuterSide::Top) ? std::min(h - 1, coord + 1) : std::max(0, coord - 1);
      const int yout =
          (side == OuterSide::Top) ? std::max(0, coord - 1) : std::min(h - 1, coord + 1);
      const float de_in = DeltaE76(features.lab.At(xx, yin), model.center_lab);
      const float de_out = DeltaE76(features.lab.At(xx, yout), model.center_lab);
      const float in_sim = std::max(0.f, std::min(1.f, 1.f - de_in / weak));
      const float out_diff = std::max(0.f, std::min(1.f, de_out / weak));
      const float g = std::fabs(features.gradient_y.At(xx, coord));
      acc += 0.45f * in_sim + 0.40f * out_diff + 0.15f * std::min(1.f, g / 80.f);
      ++n;
    }
  }
  return n ? static_cast<float>(acc / n) : -1e9f;
}

int RefineVertical(const FeatureMaps& features, const BackgroundModel& model, int coarse, int y0,
                   int y1, int radius, int lo, int hi, const DetectorConfig& /*cfg*/) {
  std::vector<Cost> costs;
  for (int c = coarse - radius; c <= coarse + radius; ++c) {
    if (c < lo || c > hi) continue;
    Cost k;
    k.coord = c;
    k.score = SideCostAt(features, model, OuterSide::Left, c, y0, y1);
    // Use Left cost shape; caller chooses side for xout/xin via separate calls.
    costs.push_back(k);
  }
  if (costs.empty()) return coarse;
  std::sort(costs.begin(), costs.end());
  return costs.front().coord;
}

int RefineHorizontal(const FeatureMaps& features, const BackgroundModel& model, int coarse, int x0,
                     int x1, int radius, int lo, int hi, const DetectorConfig& /*cfg*/) {
  std::vector<Cost> costs;
  for (int c = coarse - radius; c <= coarse + radius; ++c) {
    if (c < lo || c > hi) continue;
    Cost k;
    k.coord = c;
    k.score = SideCostAt(features, model, OuterSide::Top, c, x0, x1);
    costs.push_back(k);
  }
  if (costs.empty()) return coarse;
  std::sort(costs.begin(), costs.end());
  return costs.front().coord;
}

}  // namespace

IntRect* RefineRectangle(const IntRect& coarse, const FeatureMaps& features,
                         const BackgroundModel& model, const DetectorConfig& cfg, float dpi_scale,
                         IntRect& out) {
  if (!coarse.valid()) return nullptr;
  const int short_side = std::min(features.width, features.height);
  const int radius = cfg.RefineRadiusPx(dpi_scale, short_side);
  const int max_shift =
      std::max(radius, static_cast<int>(std::round(short_side * cfg.max_refine_shift_ratio)));

  IntRect r = coarse.Clamp(features.width, features.height);
  if (!r.valid()) return nullptr;

  auto refine_side = [&](OuterSide side, int coarse_coord) {
    if (side == OuterSide::Left) {
      int c = coarse_coord;
      float best = -1e9f;
      int best_c = c;
      for (int x = c - radius; x <= c + radius; ++x) {
        if (x < 1 || x >= r.right - 1) continue;
        float s = SideCostAt(features, model, OuterSide::Left, x, r.top, r.bottom);
        if (s > best) {
          best = s;
          best_c = x;
        }
      }
      return best_c;
    }
    if (side == OuterSide::Right) {
      int c = coarse_coord;
      float best = -1e9f;
      int best_c = c;
      for (int x = c - radius; x <= c + radius; ++x) {
        if (x <= r.left + 1 || x > features.width - 1) continue;
        float s = SideCostAt(features, model, OuterSide::Right, x, r.top, r.bottom);
        if (s > best) {
          best = s;
          best_c = x;
        }
      }
      return best_c;
    }
    if (side == OuterSide::Top) {
      int c = coarse_coord;
      float best = -1e9f;
      int best_c = c;
      for (int y = c - radius; y <= c + radius; ++y) {
        if (y < 1 || y >= r.bottom - 1) continue;
        float s = SideCostAt(features, model, OuterSide::Top, y, r.left, r.right);
        if (s > best) {
          best = s;
          best_c = y;
        }
      }
      return best_c;
    }
    int c = coarse_coord;
    float best = -1e9f;
    int best_c = c;
    for (int y = c - radius; y <= c + radius; ++y) {
      if (y <= r.top + 1 || y > features.height - 1) continue;
      float s = SideCostAt(features, model, OuterSide::Bottom, y, r.left, r.right);
      if (s > best) {
        best = s;
        best_c = y;
      }
    }
    return best_c;
  };

  out.left = refine_side(OuterSide::Left, r.left);
  out.right = refine_side(OuterSide::Right, r.right);
  out.top = refine_side(OuterSide::Top, r.top);
  out.bottom = refine_side(OuterSide::Bottom, r.bottom);
  out = out.Clamp(features.width, features.height);
  if (!out.valid() || out.width() < cfg.min_roi_size_px || out.height() < cfg.min_roi_size_px)
    return nullptr;

  auto shifted = [&](int a, int b) { return std::abs(a - b) > max_shift; };
  if (shifted(out.left, coarse.left) || shifted(out.right, coarse.right) ||
      shifted(out.top, coarse.top) || shifted(out.bottom, coarse.bottom)) {
    return nullptr;  // refine shift exceeded
  }
  (void)RefineVertical;
  (void)RefineHorizontal;
  return &out;
}

}  // namespace wb
