#include "wb/seeds.hpp"

#include <algorithm>
#include <cmath>
#include <set>
#include <utility>

namespace wb {
namespace {

const float kBandRatios[] = {0.04f, 0.09f, 0.15f, 0.22f, 0.30f};

float MedianF(std::vector<float>& v) {
  if (v.empty()) return 0.f;
  const size_t m = v.size() / 2;
  std::nth_element(v.begin(), v.begin() + static_cast<std::ptrdiff_t>(m), v.end());
  return v[m];
}

}  // namespace

std::vector<SeedPatch> SampleBackgroundSeeds(const FeatureMaps& features, const IntRect& user_roi,
                                             const DetectorConfig& cfg, float dpi_scale) {
  const int h = features.height;
  const int w = features.width;
  const int short_side = std::min(user_roi.width(), user_roi.height());
  const int size = cfg.SeedSizePx(dpi_scale, short_side);
  const int half = size / 2;
  std::vector<SeedPatch> seeds;
  int seed_id = 0;
  const int n = cfg.seeds_per_side;

  auto patch_stats = [&](int cx, int cy, Lab& mean_lab, float& grad_density,
                         float& local_var) -> bool {
    const int x0 = std::max(0, cx - half);
    const int y0 = std::max(0, cy - half);
    const int x1 = std::min(w, x0 + size);
    const int y1 = std::min(h, y0 + size);
    if (x1 - x0 < size / 2 || y1 - y0 < size / 2) return false;
    std::vector<float> L, A, B, gvals, vvals;
    for (int y = y0; y < y1; ++y) {
      for (int x = x0; x < x1; ++x) {
        const float* lab = features.lab.At(x, y);
        L.push_back(lab[0]);
        A.push_back(lab[1]);
        B.push_back(lab[2]);
        gvals.push_back(features.gradient_magnitude.At(x, y));
        vvals.push_back(features.local_variance.At(x, y));
      }
    }
    std::vector<float> gcopy = gvals;
    const float thr = Percentile(gcopy, 70.f);
    int above = 0;
    for (float g : gvals)
      if (g > thr) ++above;
    grad_density = gvals.empty() ? 1.f : static_cast<float>(above) / static_cast<float>(gvals.size());
    local_var = MedianF(vvals);
    mean_lab = {MedianF(L), MedianF(A), MedianF(B)};
    return true;
  };

  auto reject = [&](float grad_d, float local_var, int cx, int cy) -> std::string {
    const float rcx = (user_roi.left + user_roi.right) * 0.5f;
    const float rcy = (user_roi.top + user_roi.bottom) * 0.5f;
    const float dx = std::fabs(cx - rcx) / std::max(user_roi.width() * 0.5f, 1.f);
    const float dy = std::fabs(cy - rcy) / std::max(user_roi.height() * 0.5f, 1.f);
    if (std::max(dx, dy) < cfg.seed_center_exclude_ratio * 0.55f) return "too_central";
    if (grad_d > cfg.seed_max_grad_density) return "high_gradient";
    if (local_var > cfg.seed_max_local_var) return "high_variance";
    return {};
  };

  std::vector<std::tuple<OuterSide, int, int>> points;
  for (float ratio : kBandRatios) {
    const int inset_x = std::max(half + 1, static_cast<int>(std::round(user_roi.width() * ratio)));
    const int inset_y = std::max(half + 1, static_cast<int>(std::round(user_roi.height() * ratio)));
    if (inset_x * 2 >= user_roi.width() - size || inset_y * 2 >= user_roi.height() - size) continue;

    const int y_top = user_roi.top + inset_y;
    const int y_bot = user_roi.bottom - inset_y - 1;
    const int x_left = user_roi.left + inset_x;
    const int x_right = user_roi.right - inset_x - 1;

    for (int i = 0; i < n; ++i) {
      const float t = (n == 1) ? 0.f : static_cast<float>(i) / static_cast<float>(n - 1);
      const int x = static_cast<int>(std::round(user_roi.left + inset_x +
                                                t * (user_roi.right - inset_x - 1 - (user_roi.left + inset_x))));
      const int y = static_cast<int>(std::round(user_roi.top + inset_y +
                                                t * (user_roi.bottom - inset_y - 1 - (user_roi.top + inset_y))));
      points.emplace_back(OuterSide::Top, x, y_top);
      points.emplace_back(OuterSide::Bottom, x, y_bot);
      points.emplace_back(OuterSide::Left, x_left, y);
      points.emplace_back(OuterSide::Right, x_right, y);
    }
    points.emplace_back(OuterSide::Top, x_left, y_top);
    points.emplace_back(OuterSide::Top, x_right, y_top);
    points.emplace_back(OuterSide::Bottom, x_left, y_bot);
    points.emplace_back(OuterSide::Bottom, x_right, y_bot);
  }

  std::set<std::pair<int, int>> seen;
  for (const auto& tup : points) {
    OuterSide side;
    int cx, cy;
    std::tie(side, cx, cy) = tup;
    if (!seen.insert({cx, cy}).second) continue;
    if (cx < 0 || cx >= w || cy < 0 || cy >= h) continue;
    if (!user_roi.ContainsPoint(cx, cy)) continue;
    SeedPatch s;
    s.seed_id = seed_id++;
    s.side = side;
    s.x = cx;
    s.y = cy;
    s.size = size;
    Lab mean{};
    float gd = 1.f, lv = 1e9f;
    if (!patch_stats(cx, cy, mean, gd, lv)) {
      s.accepted = false;
      s.reject_reason = "oob";
      seeds.push_back(s);
      continue;
    }
    s.mean_lab = mean;
    s.reject_reason = reject(gd, lv, cx, cy);
    s.accepted = s.reject_reason.empty();
    seeds.push_back(s);
  }
  return seeds;
}

}  // namespace wb
