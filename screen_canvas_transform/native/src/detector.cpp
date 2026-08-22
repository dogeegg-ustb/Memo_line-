#include "wb/detector.hpp"

#include "wb/background.hpp"
#include "wb/features.hpp"
#include "wb/geometry.hpp"
#include "wb/grower.hpp"
#include "wb/image.hpp"
#include "wb/refine.hpp"
#include "wb/scoring.hpp"
#include "wb/seeds.hpp"
#include "wb/similarity.hpp"
#include "wb/validate.hpp"

#include <algorithm>
#include <cmath>
#include <queue>
#include <utility>
#include <vector>

namespace wb {
namespace {

DetectionOutput Fail(Status s, const std::string& msg, const std::string& capture_id = {}) {
  DetectionOutput o;
  o.status = s;
  o.message = msg;
  o.source_capture_id = capture_id;
  return o;
}

std::pair<IntRect, Status> NormalizeUserRoi(const IntRect& roi, int w, int h, int min_size) {
  IntRect r = roi;
  if (r.left > r.right) std::swap(r.left, r.right);
  if (r.top > r.bottom) std::swap(r.top, r.bottom);
  r = r.Clamp(w, h);
  if (r.width() < min_size || r.height() < min_size) return {r, Status::RoiTooSmall};
  return {r, Status::Ok};
}

struct NavigatorCiiCandidate {
  IntRect rect{};
  float score = 0.f;
  float left_support = 0.f;
  float right_support = 0.f;
  float top_support = 0.f;
  float bottom_support = 0.f;
};

bool IsBackground(const BackgroundSimilarity& similarity, int x, int y) {
  return similarity.weak_mask.At(x, y) != 0 || similarity.strong_mask.At(x, y) != 0;
}

int CountBackground(const BackgroundSimilarity& similarity, const IntRect& rect) {
  int count = 0;
  for (int y = rect.top; y < rect.bottom; ++y) {
    for (int x = rect.left; x < rect.right; ++x) count += IsBackground(similarity, x, y) ? 1 : 0;
  }
  return count;
}

bool ContainsStrongBackground(const BackgroundSimilarity& similarity, const IntRect& rect) {
  for (int y = rect.top; y < rect.bottom; ++y) {
    for (int x = rect.left; x < rect.right; ++x) {
      if (similarity.strong_mask.At(x, y)) return true;
    }
  }
  return false;
}

void SubdivideNavigatorBackground(const BackgroundSimilarity& similarity, const IntRect& root,
                                  int min_block_px, std::vector<IntRect>& leaves) {
  std::vector<IntRect> pending{root};
  while (!pending.empty()) {
    const IntRect block = pending.back();
    pending.pop_back();
    const int area = block.area();
    if (area <= 0) continue;

    const int background = CountBackground(similarity, block);
    if (background == 0) continue;
    if (background == area) {
      leaves.push_back(block);
      continue;
    }

    if (block.width() <= min_block_px && block.height() <= min_block_px) {
      for (int y = block.top; y < block.bottom; ++y) {
        for (int x = block.left; x < block.right; ++x) {
          const IntRect pixel{x, y, x + 1, y + 1};
          if (IsBackground(similarity, x, y)) leaves.push_back(pixel);
        }
      }
      continue;
    }

    if (block.width() >= block.height() && block.width() > 1) {
      const int mid = block.left + block.width() / 2;
      pending.push_back({block.left, block.top, mid, block.bottom});
      pending.push_back({mid, block.top, block.right, block.bottom});
    } else if (block.height() > 1) {
      const int mid = block.top + block.height() / 2;
      pending.push_back({block.left, block.top, block.right, mid});
      pending.push_back({block.left, mid, block.right, block.bottom});
    }
  }
}

float SideBackgroundSupport(const BackgroundSimilarity& similarity, const IntRect& roi,
                            const IntRect& rect, OuterSide side, int band_px) {
  int hits = 0;
  int total = 0;
  if (side == OuterSide::Left || side == OuterSide::Right) {
    const int x0 = side == OuterSide::Left ? rect.left - band_px : rect.right;
    const int x1 = side == OuterSide::Left ? rect.left : rect.right + band_px;
    for (int y = rect.top; y < rect.bottom; ++y) {
      for (int x = std::max(roi.left, x0); x < std::min(roi.right, x1); ++x) {
        ++total;
        hits += IsBackground(similarity, x, y) ? 1 : 0;
      }
    }
  } else {
    const int y0 = side == OuterSide::Top ? rect.top - band_px : rect.bottom;
    const int y1 = side == OuterSide::Top ? rect.top : rect.bottom + band_px;
    for (int y = std::max(roi.top, y0); y < std::min(roi.bottom, y1); ++y) {
      for (int x = rect.left; x < rect.right; ++x) {
        ++total;
        hits += IsBackground(similarity, x, y) ? 1 : 0;
      }
    }
  }
  return total > 0 ? static_cast<float>(hits) / total : 0.f;
}

bool AddNavigatorCiiCandidate(const BackgroundSimilarity& similarity, const IntRect& roi,
                              const IntRect& rect, std::vector<NavigatorCiiCandidate>& candidates) {
  if (!rect.valid() || rect.width() < 24 || rect.height() < 24 ||
      rect.left < roi.left || rect.top < roi.top || rect.right > roi.right ||
      rect.bottom > roi.bottom) {
    return false;
  }

  const int interior = rect.area();
  const float interior_background =
      static_cast<float>(CountBackground(similarity, rect)) / static_cast<float>(std::max(1, interior));
  if (interior_background > 0.82f) return false;

  const int band = std::max(2, std::min(6, std::min(rect.width(), rect.height()) / 12));
  NavigatorCiiCandidate candidate;
  candidate.rect = rect;
  candidate.left_support = SideBackgroundSupport(similarity, roi, rect, OuterSide::Left, band);
  candidate.right_support = SideBackgroundSupport(similarity, roi, rect, OuterSide::Right, band);
  candidate.top_support = SideBackgroundSupport(similarity, roi, rect, OuterSide::Top, band);
  candidate.bottom_support = SideBackgroundSupport(similarity, roi, rect, OuterSide::Bottom, band);

  const float vertical_pair = std::min(candidate.left_support, candidate.right_support);
  const float horizontal_pair = std::min(candidate.top_support, candidate.bottom_support);
  if (std::max(vertical_pair, horizontal_pair) < 0.32f) return false;

  candidate.score = std::max(vertical_pair, horizontal_pair) * (1.f - interior_background) *
                    std::sqrt(static_cast<float>(rect.area()));
  candidates.push_back(candidate);
  return true;
}

bool DetectNavigatorThumbnailFromBackgroundMask(const BackgroundSimilarity& similarity,
                                                const IntRect& roi, const DetectorConfig& cfg,
                                                IntRect& thumbnail, float& confidence,
                                                std::string& reason) {
  std::vector<IntRect> leaves;
  SubdivideNavigatorBackground(similarity, roi, std::max(4, cfg.SafetyBandPx(1.f, std::min(roi.width(), roi.height()))),
                               leaves);
  if (leaves.empty() || !ContainsStrongBackground(similarity, roi)) {
    reason = "no workspace-background pixels in navigator ROI";
    return false;
  }

  std::vector<int> left(roi.height(), roi.right);
  std::vector<int> right(roi.height(), roi.left);
  std::vector<int> top(roi.width(), roi.bottom);
  std::vector<int> bottom(roi.width(), roi.top);
  for (const auto& leaf : leaves) {
    for (int y = leaf.top; y < leaf.bottom; ++y) {
      left[y - roi.top] = std::min(left[y - roi.top], leaf.left);
      right[y - roi.top] = std::max(right[y - roi.top], leaf.right);
    }
    for (int x = leaf.left; x < leaf.right; ++x) {
      top[x - roi.left] = std::min(top[x - roi.left], leaf.top);
      bottom[x - roi.left] = std::max(bottom[x - roi.left], leaf.bottom);
    }
  }

  std::vector<NavigatorCiiCandidate> candidates;
  for (int y = roi.top; y < roi.bottom; ++y) {
    int x = roi.left;
    while (x < roi.right) {
      while (x < roi.right && IsBackground(similarity, x, y)) ++x;
      const int start = x;
      while (x < roi.right && !IsBackground(similarity, x, y)) ++x;
      const int end = x;
      if (end - start < 24) continue;
      int support_rows = 0;
      int y0 = y;
      int y1 = y + 1;
      while (y0 > roi.top) {
        int probe = (start + end) / 2;
        if (IsBackground(similarity, probe, y0 - 1)) break;
        --y0;
      }
      while (y1 < roi.bottom) {
        int probe = (start + end) / 2;
        if (IsBackground(similarity, probe, y1)) break;
        ++y1;
      }
      for (int yy = y0; yy < y1; ++yy) {
        int non_bg = 0;
        for (int xx = start; xx < end; ++xx) non_bg += IsBackground(similarity, xx, yy) ? 0 : 1;
        if (non_bg * 100 >= (end - start) * 55) ++support_rows;
      }
      if (support_rows >= 24) AddNavigatorCiiCandidate(similarity, roi, {start, y0, end, y1}, candidates);
    }
  }

  if (candidates.empty()) {
    reason = "no C-II background-to-nonbackground rectangle";
    return false;
  }

  std::sort(candidates.begin(), candidates.end(), [](const auto& a, const auto& b) {
    return a.score > b.score;
  });
  if (candidates.size() > 1 && candidates[1].score >= candidates[0].score * 0.92f &&
      RectIou(candidates[0].rect, candidates[1].rect) < cfg.ambiguity_iou_max) {
    reason = "AmbiguousCandidates";
    return false;
  }

  thumbnail = candidates.front().rect;
  confidence = std::min(1.f, candidates.front().score /
                                  std::max(1.f, std::sqrt(static_cast<float>(roi.area()))));
  return true;
}

struct NavigatorEvidenceRun {
  int left = 0;
  int right = 0;
  int strong = 0;
};

struct NavigatorSeedPair {
  int seed_x = 0;
  int seed_y = 0;
  IntRect left_evidence{};
  IntRect right_evidence{};
  IntRect rect{};
  float score = 0.f;
};

NavigatorEvidenceRun FindBackgroundRun(const BackgroundSimilarity& similarity, int y, int start,
                                       int end, bool from_left) {
  NavigatorEvidenceRun run;
  int x = from_left ? start : end - 1;
  const int step = from_left ? 1 : -1;
  while (x >= start && x < end && !IsBackground(similarity, x, y)) x += step;
  if (x < start || x >= end) return run;
  if (from_left) {
    run.left = x;
    while (x >= start && x < end && IsBackground(similarity, x, y)) {
      if (similarity.strong_mask.At(x, y)) ++run.strong;
      x += step;
    }
    run.right = x;
  } else {
    run.right = x + 1;
    while (x >= start && x < end && IsBackground(similarity, x, y)) {
      if (similarity.strong_mask.At(x, y)) ++run.strong;
      x += step;
    }
    run.left = x + 1;
  }
  return run;
}

bool IsNavigatorEvidenceRow(const BackgroundSimilarity& similarity, const IntRect& roi, int y,
                            NavigatorEvidenceRun& left, NavigatorEvidenceRun& right) {
  left = FindBackgroundRun(similarity, y, roi.left, roi.right, true);
  right = FindBackgroundRun(similarity, y, roi.left, roi.right, false);
  if (left.right - left.left < 4 || right.right - right.left < 4 || left.right >= right.left)
    return false;
  const int gap = right.left - left.right;
  return gap >= 16 && gap >= std::max(16, roi.width() / 20);
}

bool DetectNavigatorThumbnailFromSeedPair(const BackgroundSimilarity& similarity, const IntRect& roi,
                                          IntRect& thumbnail, float& confidence,
                                          std::string& reason) {
  std::vector<int> valid_rows;
  std::vector<NavigatorEvidenceRun> left_runs;
  std::vector<NavigatorEvidenceRun> right_runs;
  for (int y = roi.top; y < roi.bottom; ++y) {
    NavigatorEvidenceRun left;
    NavigatorEvidenceRun right;
    if (!IsNavigatorEvidenceRow(similarity, roi, y, left, right)) continue;
    valid_rows.push_back(y);
    left_runs.push_back(left);
    right_runs.push_back(right);
  }
  if (valid_rows.empty()) {
    reason = "no paired background evidence";
    return false;
  }

  std::vector<NavigatorSeedPair> candidates;
  size_t group_start = 0;
  while (group_start < valid_rows.size()) {
    size_t group_end = group_start + 1;
    while (group_end < valid_rows.size() && valid_rows[group_end] == valid_rows[group_end - 1] + 1)
      ++group_end;
    if (group_end - group_start >= 12) {
      int left_outer = roi.right;
      int left_inner = roi.left;
      int right_inner = roi.right;
      int right_outer = roi.left;
      int seed_x = roi.left;
      int seed_y = valid_rows[group_start];
      int strong_best = -1;
      for (size_t i = group_start; i < group_end; ++i) {
        left_outer = std::min(left_outer, left_runs[i].left);
        left_inner = std::max(left_inner, left_runs[i].right);
        right_inner = std::min(right_inner, right_runs[i].left);
        right_outer = std::max(right_outer, right_runs[i].right);
        const int candidate_x = left_runs[i].left + (left_runs[i].right - left_runs[i].left) / 2;
        if (left_runs[i].strong > strong_best) {
          strong_best = left_runs[i].strong;
          seed_x = candidate_x;
          seed_y = valid_rows[i];
        }
      }

      const IntRect left_evidence{left_outer, valid_rows[group_start], left_inner,
                                  valid_rows[group_end - 1] + 1};
      const IntRect right_evidence{right_inner, valid_rows[group_start], right_outer,
                                   valid_rows[group_end - 1] + 1};
      const IntRect rect{left_outer, valid_rows[group_start], right_outer,
                         valid_rows[group_end - 1] + 1};
      if (left_evidence.valid() && right_evidence.valid() && rect.width() >= 48 && rect.height() >= 24) {
        const float height_support = static_cast<float>(group_end - group_start) / roi.height();
        const float gap_support = static_cast<float>(right_inner - left_inner) / roi.width();
        NavigatorSeedPair pair;
        pair.seed_x = seed_x;
        pair.seed_y = seed_y;
        pair.left_evidence = left_evidence;
        pair.right_evidence = right_evidence;
        pair.rect = rect;
        pair.score = height_support * 0.55f + gap_support * 0.45f;
        candidates.push_back(pair);
      }
    }
    group_start = group_end;
  }

  if (candidates.empty()) {
    reason = "paired background evidence too short";
    return false;
  }
  std::sort(candidates.begin(), candidates.end(),
            [](const NavigatorSeedPair& a, const NavigatorSeedPair& b) { return a.score > b.score; });
  if (candidates.size() > 1 && candidates[1].score >= candidates[0].score * 0.92f &&
      RectIou(candidates[0].rect, candidates[1].rect) < 0.8f) {
    reason = "AmbiguousCandidates";
    return false;
  }

  thumbnail = candidates.front().rect.Clamp(roi.right, roi.bottom);
  confidence = std::min(1.f, candidates.front().score * 1.5f);
  return thumbnail.valid();
}

struct NavigatorBackgroundComponent {
  IntRect bbox{};
  int area = 0;
  int strong_pixels = 0;
};

bool DetectNavigatorThumbnailFromBackgroundComponents(const BackgroundSimilarity& similarity,
                                                      const IntRect& roi, IntRect& thumbnail,
                                                      float& confidence, std::string& reason) {
  ImageU8 visited;
  visited.Allocate(similarity.similarity.width, similarity.similarity.height, 0);
  std::vector<NavigatorBackgroundComponent> components;
  static constexpr int dx[4] = {1, -1, 0, 0};
  static constexpr int dy[4] = {0, 0, 1, -1};

  for (int y = roi.top; y < roi.bottom; ++y) {
    for (int x = roi.left; x < roi.right; ++x) {
      if (visited.At(x, y) || !IsBackground(similarity, x, y)) continue;
      NavigatorBackgroundComponent component;
      component.bbox = {x, y, x + 1, y + 1};
      std::queue<std::pair<int, int>> pending;
      pending.push({x, y});
      visited.At(x, y) = 1;
      while (!pending.empty()) {
        const auto [cx, cy] = pending.front();
        pending.pop();
        ++component.area;
        if (similarity.strong_mask.At(cx, cy)) ++component.strong_pixels;
        component.bbox.left = std::min(component.bbox.left, cx);
        component.bbox.top = std::min(component.bbox.top, cy);
        component.bbox.right = std::max(component.bbox.right, cx + 1);
        component.bbox.bottom = std::max(component.bbox.bottom, cy + 1);
        for (int i = 0; i < 4; ++i) {
          const int nx = cx + dx[i];
          const int ny = cy + dy[i];
          if (nx < roi.left || nx >= roi.right || ny < roi.top || ny >= roi.bottom ||
              visited.At(nx, ny) || !IsBackground(similarity, nx, ny)) continue;
          visited.At(nx, ny) = 1;
          pending.push({nx, ny});
        }
      }
      if (component.area >= 16 && component.strong_pixels > 0) components.push_back(component);
    }
  }

  struct Pair {
    IntRect rect{};
    int seed_x = 0;
    int seed_y = 0;
    float score = 0.f;
  };
  std::vector<Pair> pairs;
  for (size_t i = 0; i < components.size(); ++i) {
    for (size_t j = i + 1; j < components.size(); ++j) {
      const auto& a = components[i];
      const auto& b = components[j];
      const NavigatorBackgroundComponent* left = &a;
      const NavigatorBackgroundComponent* right = &b;
      if (left->bbox.left > right->bbox.left) std::swap(left, right);
      const int overlap_top = std::max(left->bbox.top, right->bbox.top);
      const int overlap_bottom = std::min(left->bbox.bottom, right->bbox.bottom);
      const int overlap = overlap_bottom - overlap_top;
      const int gap = right->bbox.left - left->bbox.right;
      if (left->bbox.width() < 4 || right->bbox.width() < 4 || overlap < 12 || gap < 8) continue;
      const IntRect rect{left->bbox.left, overlap_top, right->bbox.right, overlap_bottom};
      if (!rect.valid() || rect.width() < 48 || rect.height() < 24 ||
          rect.left < roi.left || rect.top < roi.top || rect.right > roi.right ||
          rect.bottom > roi.bottom) continue;
      const float overlap_ratio = static_cast<float>(overlap) /
                                  static_cast<float>(std::min(left->bbox.height(), right->bbox.height()));
      const float area_score = std::log1p(static_cast<float>(left->area + right->area));
      Pair pair;
      pair.rect = rect;
      pair.seed_x = (left->bbox.left + left->bbox.right) / 2;
      pair.seed_y = (overlap_top + overlap_bottom) / 2;
      pair.score = overlap_ratio * 2.f + area_score * 0.15f;
      pairs.push_back(pair);
    }
  }

  if (pairs.empty()) {
    reason = "no paired background components";
    return false;
  }
  std::sort(pairs.begin(), pairs.end(), [](const Pair& a, const Pair& b) {
    return a.score > b.score;
  });
  if (pairs.size() > 1 && pairs[1].score >= pairs[0].score * 0.92f &&
      RectIou(pairs[0].rect, pairs[1].rect) < 0.8f) {
    reason = "AmbiguousCandidates";
    return false;
  }
  thumbnail = pairs.front().rect;
  confidence = std::min(1.f, pairs.front().score / 2.5f);
  return true;
}

}  // namespace

DetectionOutput WorkspaceBorderDetector::Detect(const DetectionInput& in) const {
  const std::string capture_id = in.capture_id ? in.capture_id : "";
  if (!in.bgra || in.width <= 0 || in.height <= 0) {
    return Fail(Status::InvalidInput, "invalid buffer", capture_id);
  }

  try {
    auto [user_roi, roi_status] =
        NormalizeUserRoi(in.user_roi, in.width, in.height, cfg_.min_roi_size_px);
    if (roi_status != Status::Ok) {
      return Fail(roi_status, "ROI too small", capture_id);
    }

    const float dpi_scale = std::max(in.dpi_x, in.dpi_y) / 96.f;
    // UserRoi is sampling-only; grow / similarity / geometry use full capture.
    const IntRect grow_roi{0, 0, in.width, in.height};

    ImageBGRA bgra = CopyBgraBuffer(in.bgra, in.width, in.height, in.stride);
    ImageBGR full_bgr = BgraToBgr(bgra);

    const float coarse_scale = cfg_.CoarseScale(std::min(in.width, in.height));
    ImageBGR work_bgr = (coarse_scale < 0.999f) ? DownsampleBgrBilinear(full_bgr, coarse_scale)
                                                : full_bgr;
    IntRect work_user = ScaleRect(user_roi, coarse_scale).Clamp(work_bgr.width, work_bgr.height);
    IntRect work_grow{0, 0, work_bgr.width, work_bgr.height};
    if (!work_user.valid() || !work_grow.valid()) {
      return Fail(Status::InvalidInput, "scaled ROI invalid", capture_id);
    }

    FeatureMaps coarse_feat = ExtractFeatures(work_bgr, cfg_, dpi_scale, &work_grow);
    auto seeds = SampleBackgroundSeeds(coarse_feat, work_user, cfg_, dpi_scale);
    auto models = EstimateBackgroundModels(seeds, cfg_, &work_user);
    if (models.empty()) {
      return Fail(Status::NoStableWorkspaceBackground, "no stable workspace background", capture_id);
    }

    std::vector<Hypothesis> all_hyps;
    std::vector<SideSegment> all_sides;
    std::vector<BackgroundModel> accepted_models;
    std::vector<GrownBackground> accepted_grown;
    bool any_grown = false;

    for (size_t mi = 0; mi < models.size(); ++mi) {
      const auto& model = models[mi];
      auto sim = BuildSimilarity(coarse_feat, model, work_grow, cfg_);
      GrownBackground grown;
      if (!GrowBackground(seeds, model.seed_ids, sim, work_grow, cfg_, grown)) continue;
      any_grown = true;
      // Background-model layer: eliminate canvas-like solid blobs before hypotheses.
      if (!IsWorkspaceBackgroundModel(grown, model, cfg_)) continue;

      auto geo = ExtractGeometry(grown, work_grow, cfg_);
      if (geo.outer_sides.empty()) continue;

      const int accepted_index = static_cast<int>(accepted_models.size());
      auto hyps = BuildHypotheses(geo, model, accepted_index, grown, work_grow, cfg_);
      for (auto& h : hyps) {
        if (h.endpoints_truncated &&
            (h.grade == EvidenceGrade::C_L || h.grade == EvidenceGrade::C_II)) {
          continue;
        }
        all_hyps.push_back(std::move(h));
      }
      for (const auto& s : geo.outer_sides) all_sides.push_back(s);

      accepted_models.push_back(model);
      accepted_grown.push_back(std::move(grown));
    }

    if (!any_grown) {
      return Fail(Status::NoConnectedBackgroundEvidence, "grow failed", capture_id);
    }
    if (accepted_models.empty() || all_hyps.empty()) {
      return Fail(Status::InsufficientGeometry,
                  accepted_models.empty() ? "no workspace background model" : "insufficient geometry",
                  capture_id);
    }

    std::vector<Hypothesis> filtered;
    for (auto& h : all_hyps) {
      if (h.endpoints_truncated &&
          (h.grade == EvidenceGrade::C_L || h.grade == EvidenceGrade::C_II)) {
        continue;
      }
      filtered.push_back(std::move(h));
    }
    if (filtered.empty()) {
      return Fail(Status::EndpointTruncated, "endpoints truncated", capture_id);
    }

    auto selected = SelectBestHypothesis(std::move(filtered), all_sides, cfg_);
    if (!selected.best) {
      if (selected.reason == "AmbiguousCandidates") {
        return Fail(Status::AmbiguousCandidates, selected.reason, capture_id);
      }
      return Fail(Status::InsufficientGeometry,
                  selected.reason.empty() ? "no hypothesis" : selected.reason, capture_id);
    }

    Hypothesis best = *selected.best;
    if (best.endpoints_truncated && best.grade != EvidenceGrade::A) {
      bool any_ok = false;
      for (const auto& s : all_sides) {
        if (!s.truncated && s.coverage >= cfg_.min_side_coverage) any_ok = true;
      }
      if (!any_ok) return Fail(Status::EndpointTruncated, "endpoints truncated", capture_id);
    }

    IntRect coarse_full = UnscaleRectFloorCeil(best.rect, coarse_scale).Clamp(in.width, in.height);
    if (!coarse_full.valid()) {
      return Fail(Status::RectangleClosureFailed, "rectangle closure failed", capture_id);
    }

    FeatureMaps full_feat = ExtractFeatures(full_bgr, cfg_, dpi_scale, &grow_roi);
    BackgroundModel refine_model = accepted_models[0];
    if (best.model_index >= 0 && best.model_index < static_cast<int>(accepted_models.size())) {
      refine_model = accepted_models[best.model_index];
    }

    IntRect refined;
    if (!RefineRectangle(coarse_full, full_feat, refine_model, cfg_, dpi_scale, refined)) {
      return Fail(Status::RefinementFailed, "refine failed / refine shift exceeded", capture_id);
    }

    ImageU8* grown_ptr = nullptr;
    GrownBackground full_grown;
    {
      auto sim = BuildSimilarity(full_feat, refine_model, grow_roi, cfg_);
      auto full_seeds = SampleBackgroundSeeds(full_feat, user_roi, cfg_, dpi_scale);
      if (GrowBackground(full_seeds, refine_model.seed_ids, sim, grow_roi, cfg_, full_grown)) {
        grown_ptr = &full_grown.mask;
      }
    }

    auto val = ValidateRectangle(refined, best, full_feat, refine_model, grown_ptr, cfg_);
    if (!val.ok) {
      return Fail(Status::IndependentValidationFailed, "validation failed", capture_id);
    }

    DetectionOutput out;
    out.status = Status::Ok;
    out.workspace_capture = refined;
    out.workspace_screen = {refined.left + in.origin_x, refined.top + in.origin_y,
                            refined.right + in.origin_x, refined.bottom + in.origin_y};
    out.grade = best.grade;
    out.confidence = std::max(best.confidence, val.confidence);
    out.message = "ok";
    out.source_capture_id = capture_id;
    out.observed_sides = best.observed_sides;
    out.closed_sides = best.closed_sides;
    out.background_model = refine_model;
    out.has_background_model = true;
    out.source_revision = "wb-cpu-ref-2";
    return out;
  } catch (const std::exception& ex) {
    return Fail(Status::InvalidInput, ex.what(), capture_id);
  } catch (...) {
    return Fail(Status::InvalidInput, "unknown exception", capture_id);
  }
}

DetectionOutput WorkspaceBorderDetector::DetectCiiWithExternalBackground(
    const DetectionInput& in, const BackgroundModel& external_model) const {
  const std::string capture_id = in.capture_id ? in.capture_id : "";
  if (!in.bgra || in.width <= 0 || in.height <= 0) {
    return Fail(Status::InvalidInput, "invalid buffer", capture_id);
  }

  try {
    auto [search_roi, roi_status] =
        NormalizeUserRoi(in.user_roi, in.width, in.height, cfg_.min_roi_size_px);
    if (roi_status != Status::Ok) {
      return Fail(roi_status, "navigator ROI too small", capture_id);
    }

    const float dpi_scale = std::max(in.dpi_x, in.dpi_y) / 96.f;
    ImageBGRA bgra = CopyBgraBuffer(in.bgra, in.width, in.height, in.stride);
    ImageBGR full_bgr = BgraToBgr(bgra);

    // The navigator ROI is a panel ROI, not a background boundary. Reuse the
    // confirmed workspace Lab model, then find matching background components
    // anywhere inside that panel before applying the fixed C-II geometry path.
    // No navigator seed sampling or background-model re-estimation is performed.
    FeatureMaps full_feat = ExtractFeatures(full_bgr, cfg_, dpi_scale, &search_roi);
    BackgroundModel model = external_model;
    auto sim = BuildSimilarity(full_feat, model, search_roi, cfg_);

    IntRect detected;
    float confidence = 0.f;
    std::string reason;
    if (!DetectNavigatorThumbnailFromBackgroundComponents(sim, search_roi, detected, confidence, reason)) {
      if (reason == "AmbiguousCandidates") {
        return Fail(Status::AmbiguousCandidates, reason, capture_id);
      }
      return Fail(Status::InsufficientGeometry, reason.empty() ? "no C-II thumbnail candidate" : reason,
                  capture_id);
    }

    IntRect refined;
    if (!RefineRectangle(detected, full_feat, model, cfg_, dpi_scale, refined)) {
      return Fail(Status::RefinementFailed, "C-II refine failed", capture_id);
    }
    refined = refined.Clamp(in.width, in.height);
    refined.left = std::max(refined.left, search_roi.left);
    refined.top = std::max(refined.top, search_roi.top);
    refined.right = std::min(refined.right, search_roi.right);
    refined.bottom = std::min(refined.bottom, search_roi.bottom);
    if (!refined.valid()) {
      return Fail(Status::RectangleClosureFailed, "C-II refined rect invalid", capture_id);
    }

    DetectionOutput out;
    out.status = Status::Ok;
    out.workspace_capture = refined;
    out.workspace_screen = {refined.left + in.origin_x, refined.top + in.origin_y,
                            refined.right + in.origin_x, refined.bottom + in.origin_y};
    out.grade = EvidenceGrade::C_II;
    out.confidence = confidence;
    out.message = "ok-cii-seed-pair";
    out.source_capture_id = capture_id;
    out.background_model = model;
    out.has_background_model = true;
    out.source_revision = "wb-cpu-ref-4-cii-seed-pair";
    return out;
  } catch (const std::exception& ex) {
    return Fail(Status::InvalidInput, ex.what(), capture_id);
  } catch (...) {
    return Fail(Status::InvalidInput, "unknown exception", capture_id);
  }
}

DetectionOutput DetectWorkspace(const DetectionInput& in, const DetectorConfig* cfg) {
  WorkspaceBorderDetector det(cfg ? *cfg : DetectorConfig{});
  return det.Detect(in);
}

DetectionOutput DetectNavigatorThumbnailCii(const DetectionInput& in, const BackgroundModel& model,
                                            const DetectorConfig* cfg) {
  WorkspaceBorderDetector det(cfg ? *cfg : DetectorConfig{});
  return det.DetectCiiWithExternalBackground(in, model);
}

}  // namespace wb
