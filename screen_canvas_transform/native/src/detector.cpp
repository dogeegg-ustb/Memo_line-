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

std::vector<SeedPatch> FindNavigatorBackgroundSeeds(const BackgroundSimilarity& similarity,
                                                     const IntRect& roi,
                                                     const BackgroundModel& model) {
  struct Component {
    int area = 0;
    int min_x = 0;
    int min_y = 0;
    int max_x = 0;
    int max_y = 0;
    int strong_pixels = 0;
  };

  const int width = similarity.similarity.width;
  const int height = similarity.similarity.height;
  ImageU8 visited;
  visited.Allocate(width, height, 0);
  std::vector<Component> components;

  constexpr int kMinComponentPixels = 9;
  static constexpr int kDx[4] = {1, -1, 0, 0};
  static constexpr int kDy[4] = {0, 0, 1, -1};

  for (int y = roi.top; y < roi.bottom; ++y) {
    for (int x = roi.left; x < roi.right; ++x) {
      if (visited.At(x, y) || (!similarity.weak_mask.At(x, y) && !similarity.strong_mask.At(x, y))) {
        continue;
      }

      Component component{0, x, y, x + 1, y + 1, 0};
      std::queue<std::pair<int, int>> queue;
      queue.push({x, y});
      visited.At(x, y) = 1;

      while (!queue.empty()) {
        const auto [cx, cy] = queue.front();
        queue.pop();
        ++component.area;
        if (similarity.strong_mask.At(cx, cy)) ++component.strong_pixels;
        component.min_x = std::min(component.min_x, cx);
        component.min_y = std::min(component.min_y, cy);
        component.max_x = std::max(component.max_x, cx + 1);
        component.max_y = std::max(component.max_y, cy + 1);

        for (int i = 0; i < 4; ++i) {
          const int nx = cx + kDx[i];
          const int ny = cy + kDy[i];
          if (nx < roi.left || nx >= roi.right || ny < roi.top || ny >= roi.bottom ||
              visited.At(nx, ny) ||
              (!similarity.weak_mask.At(nx, ny) && !similarity.strong_mask.At(nx, ny))) {
            continue;
          }
          visited.At(nx, ny) = 1;
          queue.push({nx, ny});
        }
      }

      if (component.area >= kMinComponentPixels && component.strong_pixels > 0) {
        components.push_back(component);
      }
    }
  }

  std::sort(components.begin(), components.end(), [](const Component& a, const Component& b) {
    if (a.strong_pixels != b.strong_pixels) return a.strong_pixels > b.strong_pixels;
    return a.area > b.area;
  });

  constexpr size_t kMaxSeedComponents = 64;
  std::vector<SeedPatch> seeds;
  seeds.reserve(std::min(components.size(), kMaxSeedComponents));
  int seed_id = 1;
  for (size_t i = 0; i < components.size() && seeds.size() < kMaxSeedComponents; ++i) {
    const auto& c = components[i];
    const int center_x = (c.min_x + c.max_x - 1) / 2;
    const int center_y = (c.min_y + c.max_y - 1) / 2;
    int best_x = center_x;
    int best_y = center_y;
    bool found_strong = false;

    for (int y = c.min_y; y < c.max_y && !found_strong; ++y) {
      for (int x = c.min_x; x < c.max_x; ++x) {
        if (similarity.strong_mask.At(x, y)) {
          best_x = x;
          best_y = y;
          found_strong = true;
          break;
        }
      }
    }

    SeedPatch seed;
    seed.seed_id = seed_id++;
    seed.side = OuterSide::Top;
    seed.x = best_x;
    seed.y = best_y;
    seed.size = 1;
    seed.mean_lab = model.center_lab;
    seed.accepted = true;
    seeds.push_back(seed);
  }

  return seeds;
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

    GrownBackground grown;
    grown.mask.Allocate(full_feat.width, full_feat.height, 0);
    grown.source_label.Allocate(full_feat.width, full_feat.height, 0);

    auto synthetic_seeds = FindNavigatorBackgroundSeeds(sim, search_roi, model);
    if (synthetic_seeds.empty()) {
      return Fail(Status::NoConnectedBackgroundEvidence,
                  "no workspace-background component inside navigator ROI", capture_id);
    }
    model.seed_ids.clear();
    for (const auto& seed : synthetic_seeds) model.seed_ids.push_back(seed.seed_id);

    if (!GrowBackground(synthetic_seeds, model.seed_ids, sim, search_roi, cfg_, grown)) {
      return Fail(Status::NoConnectedBackgroundEvidence, "C-II grow failed", capture_id);
    }

    auto geo = ExtractGeometry(grown, search_roi, cfg_);
    if (geo.outer_sides.empty()) {
      return Fail(Status::InsufficientGeometry, "no outer sides for C-II", capture_id);
    }

    auto hyps = BuildHypotheses(geo, model, 0, grown, search_roi, cfg_);
    std::vector<Hypothesis> cii;
    for (auto& h : hyps) {
      if (h.grade != EvidenceGrade::C_II) continue;
      if (h.endpoints_truncated) continue;
      // Thumbnail MUST lie inside navigator ROI.
      if (h.rect.left < search_roi.left || h.rect.top < search_roi.top ||
          h.rect.right > search_roi.right || h.rect.bottom > search_roi.bottom) {
        continue;
      }
      cii.push_back(std::move(h));
    }
    if (cii.empty()) {
      return Fail(Status::InsufficientGeometry, "no C-II thumbnail candidate", capture_id);
    }

    auto selected = SelectBestHypothesis(std::move(cii), geo.outer_sides, cfg_);
    if (!selected.best) {
      if (selected.reason == "AmbiguousCandidates") {
        return Fail(Status::AmbiguousCandidates, selected.reason, capture_id);
      }
      return Fail(Status::InsufficientGeometry,
                  selected.reason.empty() ? "no C-II hypothesis" : selected.reason, capture_id);
    }

    Hypothesis best = *selected.best;
    IntRect refined;
    if (!RefineRectangle(best.rect, full_feat, model, cfg_, dpi_scale, refined)) {
      return Fail(Status::RefinementFailed, "C-II refine failed", capture_id);
    }
    refined = refined.Clamp(in.width, in.height);
    // Clamp into navigator ROI after refine.
    refined.left = std::max(refined.left, search_roi.left);
    refined.top = std::max(refined.top, search_roi.top);
    refined.right = std::min(refined.right, search_roi.right);
    refined.bottom = std::min(refined.bottom, search_roi.bottom);
    if (!refined.valid()) {
      return Fail(Status::RectangleClosureFailed, "C-II refined rect invalid", capture_id);
    }

    auto val = ValidateRectangle(refined, best, full_feat, model, &grown.mask, cfg_);
    if (!val.ok) {
      return Fail(Status::IndependentValidationFailed, "C-II validation failed", capture_id);
    }

    DetectionOutput out;
    out.status = Status::Ok;
    out.workspace_capture = refined;
    out.workspace_screen = {refined.left + in.origin_x, refined.top + in.origin_y,
                            refined.right + in.origin_x, refined.bottom + in.origin_y};
    out.grade = EvidenceGrade::C_II;
    out.confidence = std::max(best.confidence, val.confidence);
    out.message = "ok-cii";
    out.source_capture_id = capture_id;
    out.observed_sides = best.observed_sides;
    out.closed_sides = best.closed_sides;
    out.background_model = model;
    out.has_background_model = true;
    out.source_revision = "wb-cpu-ref-2-cii";
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
