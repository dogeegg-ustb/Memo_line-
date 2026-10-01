#include "wb/background.hpp"

#include "wb/color.hpp"

#include <algorithm>
#include <cmath>
#include <numeric>

namespace wb {
namespace {

float MedianF(std::vector<float> v) {
  if (v.empty()) return 0.f;
  const size_t m = v.size() / 2;
  std::nth_element(v.begin(), v.begin() + static_cast<std::ptrdiff_t>(m), v.end());
  return v[m];
}

float MadF(const std::vector<float>& v, float med) {
  if (v.empty()) return 0.f;
  std::vector<float> d;
  d.reserve(v.size());
  for (float x : v) d.push_back(std::fabs(x - med));
  return MedianF(std::move(d));
}

}  // namespace

std::vector<BackgroundModel> EstimateBackgroundModels(const std::vector<SeedPatch>& seeds,
                                                      const DetectorConfig& cfg,
                                                      const IntRect* /*user_roi*/) {
  std::vector<const SeedPatch*> accepted;
  for (const auto& s : seeds) {
    if (s.accepted) accepted.push_back(&s);
  }
  if (static_cast<int>(accepted.size()) < cfg.min_seeds_per_model) {
    // Fallback: use all seeds if too few accepted.
    accepted.clear();
    for (const auto& s : seeds) accepted.push_back(&s);
  }
  if (accepted.empty()) return {};

  std::vector<char> used(accepted.size(), 0);
  std::vector<BackgroundModel> models;

  for (size_t i = 0; i < accepted.size() &&
                     static_cast<int>(models.size()) < cfg.max_background_models;
       ++i) {
    if (used[i]) continue;
    std::vector<const SeedPatch*> cluster;
    cluster.push_back(accepted[i]);
    used[i] = 1;
    for (size_t j = i + 1; j < accepted.size(); ++j) {
      if (used[j]) continue;
      if (DeltaE76(accepted[i]->mean_lab, accepted[j]->mean_lab) <= cfg.cluster_delta_e) {
        cluster.push_back(accepted[j]);
        used[j] = 1;
      }
    }
    if (static_cast<int>(cluster.size()) < cfg.min_seeds_per_model &&
        static_cast<int>(accepted.size()) >= cfg.min_seeds_per_model) {
      continue;
    }

    std::vector<float> L, A, B;
    for (auto* s : cluster) {
      L.push_back(s->mean_lab.L);
      A.push_back(s->mean_lab.a);
      B.push_back(s->mean_lab.b);
    }
    BackgroundModel m;
    m.center_lab = {MedianF(L), MedianF(A), MedianF(B)};
    const float mad =
        (MadF(L, m.center_lab.L) + MadF(A, m.center_lab.a) + MadF(B, m.center_lab.b)) / 3.f;
    m.strong_delta_e = std::max(3.5f, std::min(10.f, 2.5f * mad + 3.f));
    m.weak_delta_e = std::max(m.strong_delta_e + 2.f, std::min(18.f, 4.5f * mad + 6.f));
    for (auto* s : cluster) m.seed_ids.push_back(s->seed_id);

    // Spatial spread / multi-side support as rectangular prior.
    int side_mask = 0;
    for (auto* s : cluster) side_mask |= (1 << SideIndex(s->side));
    const int side_count = ((side_mask & 1) ? 1 : 0) + ((side_mask & 2) ? 1 : 0) +
                           ((side_mask & 4) ? 1 : 0) + ((side_mask & 8) ? 1 : 0);
    m.rectangular_support_score = side_count / 4.f;
    m.confidence = std::min(
        1.f, 0.35f * (cluster.size() / 8.f) + 0.45f * m.rectangular_support_score + 0.2f);
    // Hard: canvas-like interior colors usually lack multi-side rim support.
    if (m.rectangular_support_score < cfg.min_model_rect_support) continue;
    if (m.confidence < cfg.min_model_confidence &&
        static_cast<int>(cluster.size()) < cfg.min_seeds_per_model)
      continue;
    models.push_back(std::move(m));
  }

  std::sort(models.begin(), models.end(),
            [](const BackgroundModel& a, const BackgroundModel& b) {
              return a.confidence > b.confidence;
            });
  if (static_cast<int>(models.size()) > cfg.max_background_models)
    models.resize(cfg.max_background_models);
  return models;
}

}  // namespace wb
