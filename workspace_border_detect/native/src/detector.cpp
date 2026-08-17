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
#include <utility>

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

}  // namespace wb
