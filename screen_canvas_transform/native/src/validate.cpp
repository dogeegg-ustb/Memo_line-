#include "wb/validate.hpp"

#include "wb/color.hpp"

#include <algorithm>
#include <cmath>
#include <functional>

namespace wb {
namespace {

std::pair<float, float> SideMetrics(const IntRect& rect, OuterSide side, const FeatureMaps& features,
                                    const BackgroundModel& model, const DetectorConfig& cfg) {
  const bool vertical = side == OuterSide::Left || side == OuterSide::Right;
  const bool low = side == OuterSide::Left || side == OuterSide::Top;
  const int coord = vertical ? (low ? rect.left : rect.right)
                             : (low ? rect.top : rect.bottom);
  const int limit = vertical ? features.width : features.height;
  if (coord <= 0 || coord >= limit) return {0.f,0.f};
  const int begin = vertical ? rect.top : rect.left;
  const int end = vertical ? rect.bottom : rect.right;
  const int n = std::max(4,cfg.validate_sample_count);
  double outside = 0, transition = 0;
  // Stratified midpoints differ from the regular refinement grid. Rectangles
  // are half-open: right/bottom's outside pixel is coord, inside is coord-1.
  for (int i=0;i<n;++i) {
    const int along = std::min(end-1,begin+int((i+0.5)*(end-begin)/n));
    const int inside = low ? coord : coord-1, out = low ? coord-1 : coord;
    const float* il = vertical ? features.lab.At(inside,along) : features.lab.At(along,inside);
    const float* ol = vertical ? features.lab.At(out,along) : features.lab.At(along,out);
    const Lab inside_lab{il[0],il[1],il[2]};
    outside += std::clamp(DeltaE76(ol,model.center_lab)/std::max(model.weak_delta_e,1e-3f),0.f,1.f);
    // Both pixels differing from the model is NOT a boundary. Require an
    // actual cross-edge change; this also validates canvas-occluded endpoints.
    transition += std::clamp(DeltaE76(ol,inside_lab)/std::max(model.strong_delta_e,1e-3f),0.f,1.f);
  }
  return {float(outside/n),float(transition/n)};
}

bool ValidateCorners(const IntRect& rect, const FeatureMaps& features, const BackgroundModel& model) {
  const int h = features.height;
  const int w = features.width;
  const std::pair<int, int> corners[4] = {
      {rect.left, rect.top},
      {rect.right - 1, rect.top},
      {rect.left, rect.bottom - 1},
      {rect.right - 1, rect.bottom - 1},
  };
  int ok = 0;
  for (auto [x, y] : corners) {
    if (!(1 <= x && x < w - 1 && 1 <= y && y < h - 1)) continue;
    // OUTER corner: outside diagonal should be LESS bg-like (higher ΔE) than inside
    int ox = (x == rect.left) ? x - 2 : x + 2;
    int oy = (y == rect.top) ? y - 2 : y + 2;
    int ix = (x == rect.left) ? x + 2 : x - 2;
    int iy = (y == rect.top) ? y + 2 : y - 2;
    ox = std::max(0, std::min(w - 1, ox));
    oy = std::max(0, std::min(h - 1, oy));
    ix = std::max(0, std::min(w - 1, ix));
    iy = std::max(0, std::min(h - 1, iy));
    const float de_o = DeltaE76(features.lab.At(ox, oy), model.center_lab);
    const float de_i = DeltaE76(features.lab.At(ix, iy), model.center_lab);
    if (de_o > de_i + 1.f) ++ok;
  }
  return ok >= 3;
}

IntRect* ShiftSide(const IntRect& rect, OuterSide side, int delta, int w, int h, IntRect& out) {
  out = rect;
  if (side == OuterSide::Left)
    out.left += delta;
  else if (side == OuterSide::Right)
    out.right += delta;
  else if (side == OuterSide::Top)
    out.top += delta;
  else
    out.bottom += delta;
  if (out.left >= out.right - 1 || out.top >= out.bottom - 1) return nullptr;
  if (out.left < 0 || out.top < 0 || out.right > w || out.bottom > h) return nullptr;
  return &out;
}

}  // namespace

ValidateResult ValidateRectangle(const IntRect& rect, const Hypothesis& hyp,
                                 const FeatureMaps& features, const BackgroundModel& model,
                                 const ImageU8* grown_mask, const DetectorConfig& cfg) {
  ValidateResult res;
  std::vector<float> outside_scores, transition_scores;
  for (OuterSide side :
       {OuterSide::Left, OuterSide::Top, OuterSide::Right, OuterSide::Bottom}) {
    auto m = SideMetrics(rect, side, features, model, cfg);
    outside_scores.push_back(m.first);
    transition_scores.push_back(m.second);
    res.metrics[std::string(side == OuterSide::Left     ? "Left"
                            : side == OuterSide::Top    ? "Top"
                            : side == OuterSide::Right  ? "Right"
                                                        : "Bottom") +
                "_outside"] = m.first;
    res.metrics[std::string(side == OuterSide::Left     ? "Left"
                            : side == OuterSide::Top    ? "Top"
                            : side == OuterSide::Right  ? "Right"
                                                        : "Bottom") +
                "_transition"] = m.second;
  }
  float mean_out = 0, mean_tr = 0;
  for (float v : outside_scores) mean_out += v;
  for (float v : transition_scores) mean_tr += v;
  mean_out /= 4.f;
  mean_tr /= 4.f;
  res.metrics["mean_outside"] = mean_out;
  res.metrics["mean_transition"] = mean_tr;

  // Every outer side needs independent evidence; two strong sides cannot
  // compensate for a missing or unseparated third/fourth boundary.
  for (size_t i=0;i<4;++i)
    if (outside_scores[i] < cfg.validate_min_outside_score ||
        transition_scores[i] < cfg.validate_min_transition) return res;

  const bool corners_ok = ValidateCorners(rect, features, model);
  res.metrics["corners_ok"] = corners_ok ? 1.f : 0.f;
  // Corner pixels are frequently occupied by tabs, scroll bars or anti-aliased
  // separators. The four independent side strips above are stronger evidence;
  // retain corners as diagnostics rather than rejecting a verified rectangle.

  const int w = features.width;
  const int h = features.height;

  // Workspace rect must contain the grown background (and thus any interior canvas hole).
  if (grown_mask && grown_mask->width == w && grown_mask->height == h) {
    int total = 0, inside = 0;
    const int step = std::max(1, std::min(rect.width(), rect.height()) / 64);
    for (int y = 0; y < h; y += step) {
      for (int x = 0; x < w; x += step) {
        if (!grown_mask->At(x, y)) continue;
        ++total;
        if (rect.ContainsPoint(x, y)) ++inside;
      }
    }
    const float contain =
        total > 0 ? static_cast<float>(inside) / static_cast<float>(total) : 1.f;
    res.metrics["grown_contain_frac"] = contain;
    if (total >= 8 && contain < 0.90f) return res;

    // Reject rects that leave the canvas hole outside.
    int hole_out = 0, hole_tot = 0;
    int bx0 = w, bx1 = 0, by0 = h, by1 = 0;
    for (int y = 0; y < h; y += step) {
      for (int x = 0; x < w; x += step) {
        if (!grown_mask->At(x, y)) continue;
        bx0 = std::min(bx0, x);
        bx1 = std::max(bx1, x + 1);
        by0 = std::min(by0, y);
        by1 = std::max(by1, y + 1);
      }
    }
    if (bx1 > bx0 && by1 > by0) {
      for (int y = by0; y < by1; y += step) {
        for (int x = bx0; x < bx1; x += step) {
          if (grown_mask->At(x, y)) continue;
          ++hole_tot;
          if (!rect.ContainsPoint(x, y)) ++hole_out;
        }
      }
      const float hole_out_frac =
          hole_tot > 0 ? static_cast<float>(hole_out) / static_cast<float>(hole_tot) : 0.f;
      res.metrics["hole_outside_frac"] = hole_out_frac;
      if (hole_tot >= 8 && hole_out_frac > 0.25f) return res;
    }
  }

  const float base_score = 0.5f * mean_out + 0.5f * mean_tr;
  for (int pi = 0; pi < 2; ++pi) {
    const int pert = cfg.validate_perturbation_px[pi];
    int worse = 0, total = 0;
    for (OuterSide side :
         {OuterSide::Left, OuterSide::Top, OuterSide::Right, OuterSide::Bottom}) {
      for (int sign : {-1, 1}) {
        IntRect shifted;
        if (!ShiftSide(rect, side, sign * pert, w, h, shifted)) continue;
        std::vector<float> o, t;
        for (OuterSide s2 :
             {OuterSide::Left, OuterSide::Top, OuterSide::Right, OuterSide::Bottom}) {
          auto m = SideMetrics(shifted, s2, features, model, cfg);
          o.push_back(m.first);
          t.push_back(m.second);
        }
        float mo = 0, mt = 0;
        for (float v : o) mo += v;
        for (float v : t) mt += v;
        const float sc = 0.5f * (mo / 4.f) + 0.5f * (mt / 4.f);
        ++total;
        if (sc < base_score - cfg.validate_local_optima_margin) ++worse;
      }
    }
    const float ratio = worse / static_cast<float>(std::max(total, 1));
    res.metrics["pert_" + std::to_string(pert) + "_worse_ratio"] = ratio;
    // Require only mild local optimality; synthetic/anti-aliased edges are noisy.
    if (total > 0 && ratio < 0.20f) return res;
  }

  res.confidence =
      std::max(0.f, std::min(1.f, 0.4f * mean_out + 0.35f * mean_tr + 0.25f * hyp.confidence));
  res.metrics["confidence"] = res.confidence;
  res.ok = true;
  return res;
}

}  // namespace wb
