#include "wb/scoring.hpp"

#include <algorithm>
#include <cmath>
#include <map>

namespace wb {
namespace {

float MinObservedCoverage(const Hypothesis& hyp, const std::vector<SideSegment>& sides) {
  std::map<OuterSide, float> cov;
  for (const auto& s : sides) {
    if (s.is_workspace_outer) cov[s.side] = s.coverage;
  }
  float m = 1.f;
  int n = 0;
  for (OuterSide side : hyp.observed_sides) {
    auto it = cov.find(side);
    if (it == cov.end()) continue;
    m = std::min(m, it->second);
    ++n;
  }
  return n ? m : 0.f;
}

// A/B/C are alternate construction paths, not a quality ranking.
// Tie-break only by geometric strength (coverage, then area as outer completeness).
bool BetterPath(const Hypothesis& a, const Hypothesis& b, const std::vector<SideSegment>& sides) {
  const float ca = MinObservedCoverage(a, sides);
  const float cb = MinObservedCoverage(b, sides);
  if (std::fabs(ca - cb) > 0.04f) return ca > cb;
  const int aa = a.rect.area();
  const int ba = b.rect.area();
  if (aa != ba) return aa > ba;
  return static_cast<int>(a.observed_sides.size()) > static_cast<int>(b.observed_sides.size());
}

}  // namespace

SelectResult SelectBestHypothesis(std::vector<Hypothesis> hyps,
                                  const std::vector<SideSegment>& sides,
                                  const DetectorConfig& cfg) {
  SelectResult res;
  if (hyps.empty()) {
    res.reason = "no_hypotheses";
    return res;
  }

  for (auto& h : hyps) {
    h.score = 0.f;
    h.confidence = std::max(0.f, std::min(1.f, 0.35f + 0.65f * MinObservedCoverage(h, sides)));
  }

  std::sort(hyps.begin(), hyps.end(),
            [&](const Hypothesis& a, const Hypothesis& b) { return BetterPath(a, b, sides); });
  res.ranked = std::move(hyps);
  res.best = &res.ranked[0];
  res.margin = 1.f;

  // Distinct construction paths that disagree on the rectangle → ambiguous.
  if (res.ranked.size() >= 2) {
    const float iou = RectIou(res.ranked[0].rect, res.ranked[1].rect);
    res.margin = iou;
    if (iou < cfg.ambiguity_iou_max) {
      res.best = nullptr;
      res.reason = "AmbiguousCandidates";
      return res;
    }
  }
  return res;
}

}  // namespace wb
