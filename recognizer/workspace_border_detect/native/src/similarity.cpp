#include "wb/similarity.hpp"

#include "wb/color.hpp"
#include "wb/features.hpp"

#include <algorithm>
#include <cmath>

namespace wb {

BackgroundSimilarity BuildSimilarity(const FeatureMaps& features, const BackgroundModel& model,
                                     const IntRect& search_roi, const DetectorConfig& cfg) {
  BackgroundSimilarity out;
  const int h = features.height;
  const int w = features.width;
  out.similarity.Allocate(w, h, 0.f);
  out.strong_mask.Allocate(w, h, 0);
  out.weak_mask.Allocate(w, h, 0);
  out.barrier_mask.Allocate(w, h, 0);

  const int y0 = search_roi.top;
  const int y1 = search_roi.bottom;
  const int x0 = search_roi.left;
  const int x1 = search_roi.right;
  if (x1 <= x0 || y1 <= y0) return out;

  std::vector<float> gvals, vvals;
  gvals.reserve(static_cast<size_t>(x1 - x0) * (y1 - y0));
  vvals.reserve(gvals.capacity());
  for (int y = y0; y < y1; ++y) {
    for (int x = x0; x < x1; ++x) {
      gvals.push_back(features.gradient_magnitude.At(x, y));
      vvals.push_back(features.local_variance.At(x, y));
    }
  }
  const float g_thr = Percentile(gvals, cfg.barrier_grad_percentile);
  const float v_thr = Percentile(vvals, cfg.barrier_var_percentile);
  const float weak = std::max(model.weak_delta_e, 1e-3f);

  for (int y = y0; y < y1; ++y) {
    for (int x = x0; x < x1; ++x) {
      const float de = DeltaE76(features.lab.At(x, y), model.center_lab);
      float sim = std::max(0.f, std::min(1.f, 1.f - de / weak));
      const float g = features.gradient_magnitude.At(x, y);
      const float v = features.local_variance.At(x, y);
      float weight = 1.f;
      weight *= std::max(0.15f, std::min(1.f, 1.f - (g / std::max(g_thr * 1.5f, 1e-3f))));
      weight *= std::max(0.15f, std::min(1.f, 1.f - (v / std::max(v_thr * 1.5f, 1e-3f))));
      sim *= weight;
      out.similarity.At(x, y) = sim;
      out.strong_mask.At(x, y) = sim >= cfg.strong_sim_threshold ? 1 : 0;
      out.weak_mask.At(x, y) = sim >= cfg.weak_sim_threshold ? 1 : 0;
      out.barrier_mask.At(x, y) = (g >= g_thr && v >= v_thr * 0.5f) ? 1 : 0;
    }
  }
  return out;
}

}  // namespace wb
