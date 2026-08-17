#include "wb/validate.hpp"

#include "wb/color.hpp"

#include <algorithm>
#include <cmath>
#include <functional>

namespace wb {
namespace {

std::pair<float, float> SideMetrics(const IntRect& rect, OuterSide side, const FeatureMaps& features,
                                    const BackgroundModel& model, const DetectorConfig& cfg) {
  const int h = features.height;
  const int w = features.width;
  const int n = cfg.validate_sample_count;
  std::vector<float> de_out, de_in, g;

  if (side == OuterSide::Left || side == OuterSide::Right) {
    for (int i = 0; i < n; ++i) {
      const float t = (n == 1) ? 0.f : static_cast<float>(i) / static_cast<float>(n - 1);
      int y = static_cast<int>(std::round(rect.top + 1 + t * (rect.bottom - 2 - (rect.top + 1))));
      y = std::max(0, std::min(h - 1, y + (i % 3) - 1));
      int x = (side == OuterSide::Left) ? rect.left : rect.right - 1;
      x = std::max(1, std::min(w - 2, x));
      if (side == OuterSide::Left) {
        de_out.push_back(DeltaE76(features.lab.At(x - 1, y), model.center_lab));
        de_in.push_back(DeltaE76(features.lab.At(std::min(w - 1, x + 1), y), model.center_lab));
      } else {
        de_out.push_back(DeltaE76(features.lab.At(std::min(w - 1, x + 1), y), model.center_lab));
        de_in.push_back(DeltaE76(features.lab.At(std::max(0, x - 1), y), model.center_lab));
      }
      g.push_back(std::fabs(features.gradient_x.At(x, y)));
    }
  } else {
    for (int i = 0; i < n; ++i) {
      const float t = (n == 1) ? 0.f : static_cast<float>(i) / static_cast<float>(n - 1);
      int x = static_cast<int>(std::round(rect.left + 1 + t * (rect.right - 2 - (rect.left + 1))));
      x = std::max(0, std::min(w - 1, x + (i % 3) - 1));
      int y = (side == OuterSide::Top) ? rect.top : rect.bottom - 1;
      y = std::max(1, std::min(h - 2, y));
      if (side == OuterSide::Top) {
        de_out.push_back(DeltaE76(features.lab.At(x, y - 1), model.center_lab));
        de_in.push_back(DeltaE76(features.lab.At(x, std::min(h - 1, y + 1)), model.center_lab));
      } else {
        de_out.push_back(DeltaE76(features.lab.At(x, std::min(h - 1, y + 1)), model.center_lab));
        de_in.push_back(DeltaE76(features.lab.At(x, std::max(0, y - 1)), model.center_lab));
      }
      g.push_back(std::fabs(features.gradient_y.At(x, y)));
    }
  }

  auto mean_clip = [](const std::vector<float>& v, float denom, bool inv) {
    if (v.empty()) return 0.f;
    double acc = 0;
    for (float x : v) {
      float t = std::max(0.f, std::min(1.f, x / denom));
      acc += inv ? (1.f - t) : t;
    }
    return static_cast<float>(acc / v.size());
  };
  // outside score: high when outside similar to bg model (for OUTER edge, outside should be dissimilar!)
  // Architecture: outside should NOT match bg; Python validate uses out_score = clip(1 - de_out/weak)
  // which is HIGH when outside IS similar to bg — that matches their naming "outside_background_score"
  // meaning "outside of canvas was bg" in the old canvas-centric naming.
  // For OUTER workspace edges: outside should be UI (dissimilar), inside near edge should be bg.
  // Looking at Python validate again carefully:
  //   out_score = mean(clip(1 - de_out/weak))  — high if outside looks like bg
  //   in_diff = mean(clip(de_in/strong)) — high if inside differs from bg
  //   transition = 0.55*out_score + 0.45*in_diff
  // And validate_min_outside_score — so they want outside≈bg.
  // That's canvas-edge semantics (outside=workspace bg)!
  //
  // Architecture says outer edge: inside≈bg, outside≠bg.
  // For our OUTER refine we use out_diff (dissimilar outside) + in_sim.
  // For validate of OUTER edges we should flip: outside_score = dissimilar outside,
  // and inside near edge similar to bg.
  //
  // User said architecture fixes prefer OUTER. So validate for outer:
  //   outside_score = how well outside differs from bg (1 - sim)
  //   transition combines outside_diff + inside_sim

  const float out_diff = 1.f - mean_clip(de_out, std::max(model.weak_delta_e, 1e-3f), true);
  // mean_clip with inv=true gives mean(1 - de/weak) = similarity. So out_diff = 1 - similarity = dissimilarity.
  // Wait I messed up. Let me compute clearly:
  float out_sim = 0, in_sim = 0;
  {
    double a = 0, b = 0;
    for (float d : de_out) a += std::max(0.f, std::min(1.f, 1.f - d / std::max(model.weak_delta_e, 1e-3f)));
    for (float d : de_in) b += std::max(0.f, std::min(1.f, 1.f - d / std::max(model.weak_delta_e, 1e-3f)));
    out_sim = de_out.empty() ? 0.f : static_cast<float>(a / de_out.size());
    in_sim = de_in.empty() ? 0.f : static_cast<float>(b / de_in.size());
  }
  // OUTER: want low out_sim, high in_sim
  const float outside_score = 1.f - out_sim;  // high when outside ≠ bg
  const float transition = std::max(0.f, std::min(1.f, 0.55f * outside_score + 0.45f * in_sim));
  (void)g;
  (void)mean_clip;
  return {outside_score, transition};
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

  // Prefer the stronger two sides so one inset edge cannot veto an otherwise solid rect.
  std::vector<float> out_sorted = outside_scores;
  std::vector<float> tr_sorted = transition_scores;
  std::sort(out_sorted.begin(), out_sorted.end(), std::greater<float>());
  std::sort(tr_sorted.begin(), tr_sorted.end(), std::greater<float>());
  const float top2_out = 0.5f * (out_sorted[0] + out_sorted[1]);
  const float top2_tr = 0.5f * (tr_sorted[0] + tr_sorted[1]);
  res.metrics["top2_outside"] = top2_out;
  res.metrics["top2_transition"] = top2_tr;
  if (mean_out < cfg.validate_min_outside_score && top2_out < cfg.validate_min_outside_score + 0.12f)
    return res;
  if (mean_tr < cfg.validate_min_transition && top2_tr < cfg.validate_min_transition + 0.12f)
    return res;

  const bool corners_ok = ValidateCorners(rect, features, model);
  res.metrics["corners_ok"] = corners_ok ? 1.f : 0.f;
  if (!corners_ok) return res;

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
