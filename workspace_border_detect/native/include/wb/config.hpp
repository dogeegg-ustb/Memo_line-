#pragma once

#include <algorithm>
#include <cmath>

namespace wb {

struct DetectorConfig {
  // --- ROI ---
  // UserRoi is sampling-only; grow uses full capture. search_expand_* unused.
  int min_roi_size_px = 32;
  float search_expand_ratio = 0.18f;
  float search_expand_max_ratio = 0.35f;
  float coarse_scale = 0.5f;

  // --- seeds ---
  int seeds_per_side = 4;
  float seed_size_ratio = 0.045f;
  int seed_size_min_px = 7;
  int seed_size_max_px = 28;
  float seed_center_exclude_ratio = 0.55f;
  float seed_max_grad_density = 0.42f;
  float seed_max_local_var = 180.f;

  // --- background clustering ---
  float cluster_delta_e = 7.5f;
  int min_seeds_per_model = 3;
  int max_background_models = 6;
  float min_model_confidence = 0.25f;
  float min_model_rect_support = 0.25f;  // at least ~1 side; multi-side preferred

  // --- similarity / grow ---
  float strong_sim_threshold = 0.62f;
  float weak_sim_threshold = 0.38f;
  float barrier_grad_percentile = 78.f;
  float barrier_var_percentile = 78.f;
  int grow_gap_bridge_px = 1;
  // <=0 disables area cap; >0 is soft safety vs grow_roi area (barriers are primary stop).
  float max_grown_fraction = 0.f;
  float min_bg_hole_score = 0.18f;
  float max_bg_bbox_fill_ratio = 0.72f;
  float max_bg_capture_border_touch = 0.85f;

  // --- geometry ---
  float min_side_coverage = 0.55f;
  float min_band_length_ratio = 0.55f;
  int safety_band_min_px = 2;

  // --- refine ---
  float refine_radius_ratio = 0.012f;
  int refine_radius_min_px = 4;
  int refine_radius_max_px = 10;
  float max_refine_shift_ratio = 0.08f;

  // --- hypothesis hard-select (no weighted scoring on accept path) ---
  float ambiguity_iou_max = 0.55f;

  // --- validate ---
  int validate_sample_count = 24;
  float validate_min_outside_score = 0.28f;
  float validate_min_transition = 0.32f;
  int validate_perturbation_px[2] = {1, 2};
  float validate_local_optima_margin = 0.01f;
  unsigned ransac_seed = 1337u;

  float DpiPx(float base_px, float dpi_scale, int short_side) const {
    const float s = std::max(0.5f, dpi_scale);
    float v = base_px * s;
    const float lo = 1.f;
    const float hi = std::max(lo, short_side * 0.08f);
    return std::max(lo, std::min(hi, v));
  }

  int SeedSizePx(float dpi_scale, int short_side) const {
    const float raw = short_side * seed_size_ratio * std::max(0.5f, dpi_scale);
    int v = static_cast<int>(std::round(raw));
    v = std::max(seed_size_min_px, std::min(seed_size_max_px, v));
    if ((v % 2) == 0) ++v;
    return v;
  }

  int SearchExpandPx(int short_side, float dpi_scale) const {
    const float r = std::min(search_expand_max_ratio, search_expand_ratio * std::max(0.75f, dpi_scale));
    return std::max(8, static_cast<int>(std::round(short_side * r)));
  }

  float CoarseScale(int short_side) const {
    if (short_side >= 1600) return coarse_scale;
    if (short_side >= 900) return std::max(0.6f, coarse_scale);
    return 1.f;
  }

  int SafetyBandPx(float dpi_scale, int short_side) const {
    return std::max(safety_band_min_px,
                    static_cast<int>(std::round(DpiPx(2.f, dpi_scale, short_side))));
  }

  int RefineRadiusPx(float dpi_scale, int short_side) const {
    const float raw = short_side * refine_radius_ratio * std::max(0.75f, dpi_scale);
    int v = static_cast<int>(std::round(raw));
    return std::max(refine_radius_min_px, std::min(refine_radius_max_px, v));
  }
};

}  // namespace wb
