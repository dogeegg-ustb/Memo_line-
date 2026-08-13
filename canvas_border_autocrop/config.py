"""Centralized thresholds for canvas border auto-crop.

All magic numbers live here. Values are in capture pixels unless noted as ratios.
DPI-bounded scaling uses short_side and dpi_scale.
"""

from __future__ import annotations

from dataclasses import dataclass
import math


@dataclass(slots=True)
class DetectorConfig:
    # ROI / search
    min_roi_side_px: int = 32
    search_expand_min_px: int = 8
    search_expand_max_px: int = 96
    search_expand_ratio: float = 0.08
    boundary_safety_band_px: int = 3

    # Downsample for coarse search (full-res refine/validate still required)
    coarse_max_side_px: int = 960
    coarse_min_scale: float = 0.25

    # Feature extraction
    blur_radius_cap_px: int = 2
    variance_window: int = 5

    # Seed sampling
    seeds_per_side: int = 5
    seed_size_min_px: int = 8
    seed_size_max_px: int = 16
    seed_center_exclude_ratio: float = 0.35
    seed_max_grad_density: float = 0.35
    seed_max_local_var: float = 180.0

    # Background model
    max_background_clusters: int = 4
    strong_delta_e_min: float = 4.0
    strong_delta_e_max: float = 18.0
    weak_delta_e_min: float = 8.0
    weak_delta_e_max: float = 28.0
    mad_scale_floor: float = 1.5
    min_seed_accept_count: int = 4
    min_spatial_coverage: float = 0.35

    # Similarity / barriers
    barrier_grad_percentile: float = 88.0
    barrier_var_percentile: float = 90.0
    strong_sim_threshold: float = 0.72
    weak_sim_threshold: float = 0.45

    # Region growing
    grow_gap_bridge_px: int = 2
    max_grown_fraction: float = 0.95

    # Complete side
    min_side_coverage: float = 0.55
    min_side_span_ratio: float = 0.18
    max_coordinate_mad_px: float = 2.5
    min_endpoint_score: float = 0.35
    min_transition_score: float = 0.30
    min_outside_bg_score: float = 0.40
    histogram_bin_px: float = 1.0
    side_peak_min_weight_ratio: float = 0.12

    # Hypothesis geometry
    corner_closure_tol_px: float = 3.0
    c_ii_endpoint_align_tol_px: float = 4.0
    c_ii_length_rel_tol: float = 0.08
    c_ii_length_abs_tol_px: float = 6.0
    weak_inferred_min_coverage: float = 0.28
    min_canvas_side_px: int = 24
    min_canvas_area_px: int = 24 * 24

    # Scoring
    weight_outside: float = 1.0
    weight_transition: float = 1.1
    weight_coverage: float = 1.0
    weight_endpoint: float = 1.2
    weight_closure: float = 0.8
    weight_uniformity: float = 0.6
    weight_variance_penalty: float = 0.7
    weight_ambiguity_penalty: float = 0.5
    ambiguity_score_margin: float = 0.06
    ambiguity_iou_max: float = 0.82
    min_accept_score: float = 0.42

    # Refinement
    refine_radius_min_px: int = 4
    refine_radius_max_px: int = 10
    refine_strip_half_width_px: int = 3
    refine_max_shift_px: int = 8
    border_double_peak_gap_px: int = 3

    # Validation
    validate_sample_count: int = 48
    validate_min_outside_score: float = 0.38
    validate_min_transition: float = 0.28
    validate_perturbation_px: tuple[int, ...] = (1, 2)
    validate_local_optima_margin: float = 0.02

    # Determinism
    ransac_seed: int = 0xC5A1_B0A7

    def dpi_px(self, base: float, dpi_scale: float, short_side: int) -> float:
        """Bounded DPI-aware pixel scaling."""
        s = max(0.75, min(3.0, float(dpi_scale)))
        side_factor = max(0.85, min(1.35, short_side / 1080.0))
        return base * s * side_factor

    def search_expand_px(self, roi_short: int, dpi_scale: float) -> int:
        raw = int(round(roi_short * self.search_expand_ratio))
        raw = max(self.search_expand_min_px, min(self.search_expand_max_px, raw))
        return int(round(self.dpi_px(raw, dpi_scale, max(roi_short, 1))))

    def seed_size_px(self, dpi_scale: float, short_side: int) -> int:
        v = int(round(self.dpi_px(12.0, dpi_scale, short_side)))
        return max(self.seed_size_min_px, min(self.seed_size_max_px, v))

    def safety_band_px(self, dpi_scale: float, short_side: int) -> int:
        return max(2, int(round(self.dpi_px(self.boundary_safety_band_px, dpi_scale, short_side))))

    def refine_radius_px(self, dpi_scale: float, short_side: int) -> int:
        v = int(round(self.dpi_px(6.0, dpi_scale, short_side)))
        return max(self.refine_radius_min_px, min(self.refine_radius_max_px, v))

    def coarse_scale(self, width: int, height: int) -> float:
        side = max(width, height)
        if side <= self.coarse_max_side_px:
            return 1.0
        scale = self.coarse_max_side_px / float(side)
        return max(self.coarse_min_scale, scale)


DEFAULT_CONFIG = DetectorConfig()
