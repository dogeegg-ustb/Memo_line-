"""Complete side and endpoint detection from boundary evidence."""

from __future__ import annotations

from typing import Sequence

import numpy as np

from .config import DetectorConfig
from .geometry import median_mad, robust_mean
from .types import (
    BoundaryPoint,
    CompleteSide,
    Endpoint,
    IntRect,
    Orientation,
    SemanticSide,
)


def detect_complete_sides(
    boundary_points: dict[SemanticSide, list[BoundaryPoint]],
    search_roi: IntRect,
    capture_w: int,
    capture_h: int,
    cfg: DetectorConfig,
    dpi_scale: float,
) -> list[CompleteSide]:
    safety = cfg.safety_band_px(dpi_scale, min(search_roi.width, search_roi.height))
    sides: list[CompleteSide] = []
    for semantic in SemanticSide:
        pts = boundary_points.get(semantic, [])
        side = _fit_side(pts, semantic, search_roi, capture_w, capture_h, cfg, safety)
        if side is not None:
            sides.append(side)
    return sides


def _fit_side(
    pts: Sequence[BoundaryPoint],
    semantic: SemanticSide,
    search_roi: IntRect,
    capture_w: int,
    capture_h: int,
    cfg: DetectorConfig,
    safety: int,
) -> CompleteSide | None:
    if len(pts) < 8:
        return None

    vertical = semantic in (SemanticSide.LEFT, SemanticSide.RIGHT)
    orientation = Orientation.VERTICAL if vertical else Orientation.HORIZONTAL

    fixed_arr = np.array([p.x if vertical else p.y for p in pts], dtype=np.float64)
    along_arr = np.array([p.y if vertical else p.x for p in pts], dtype=np.float64)
    weights = np.array([max(p.weight, 1e-3) for p in pts], dtype=np.float64)

    # Weighted histogram peak for fixed coordinate
    fixed, mad, peak_weight = _weighted_peak(fixed_arr, weights, cfg.histogram_bin_px)
    if peak_weight < cfg.side_peak_min_weight_ratio * float(np.sum(weights)):
        return None

    inliers = np.abs(fixed_arr - fixed) <= max(cfg.max_coordinate_mad_px, 1.5 * mad + 1.0)
    if int(np.sum(inliers)) < 6:
        return None

    along_in = along_arr[inliers]
    w_in = weights[inliers]
    order = np.argsort(along_in)
    along_sorted = along_in[order]
    w_sorted = w_in[order]

    # Coverage along expected span inside search ROI
    if vertical:
        span_lo, span_hi = search_roi.top, search_roi.bottom
        expected = max(1, span_hi - span_lo)
    else:
        span_lo, span_hi = search_roi.left, search_roi.right
        expected = max(1, span_hi - span_lo)

    start = float(along_sorted[0])
    end = float(along_sorted[-1])
    # Tighten to dense support: trim low-density tails
    start, end = _dense_span(along_sorted, w_sorted)
    span = end - start
    if span < cfg.min_side_span_ratio * expected:
        return None

    # Coverage: weighted unique bins / span
    bins = np.round(along_sorted).astype(np.int32)
    uniq = np.unique(bins[(bins >= start) & (bins <= end)])
    coverage = float(len(uniq) / max(span, 1.0))
    if coverage < cfg.min_side_coverage:
        return None

    transition = float(
        np.average(
            [p.similarity_before - p.similarity_after for p, keep in zip(pts, inliers) if keep],
        )
    )
    transition = float(np.clip((transition + 0.2) / 0.8, 0.0, 1.0))
    outside = float(
        np.average([p.similarity_before for p, keep in zip(pts, inliers) if keep])
    )
    if transition < cfg.min_transition_score or outside < cfg.min_outside_bg_score:
        return None

    # Endpoints
    ep_start = _make_endpoint(
        semantic, fixed, start, True, pts, inliers, search_roi, capture_w, capture_h, safety, cfg
    )
    ep_end = _make_endpoint(
        semantic, fixed, end, False, pts, inliers, search_roi, capture_w, capture_h, safety, cfg
    )
    if ep_start is None or ep_end is None:
        return None

    is_truncated = ep_start.is_truncated or ep_end.is_truncated
    if is_truncated:
        # Still return for diagnostics but MUST NOT enter complete set — mark truncated
        pass

    if ep_start.score < cfg.min_endpoint_score or ep_end.score < cfg.min_endpoint_score:
        return None

    return CompleteSide(
        orientation=orientation,
        semantic_side=semantic,
        fixed_coordinate=float(fixed),
        start_coordinate=float(min(start, end)),
        end_coordinate=float(max(start, end)),
        start_endpoint=ep_start,
        end_endpoint=ep_end,
        coverage=coverage,
        coordinate_mad=float(mad),
        transition_score=transition,
        outside_background_score=outside,
        endpoint_scores=(ep_start.score, ep_end.score),
        is_truncated=is_truncated,
        supporting_sample_ids=list(range(int(np.sum(inliers)))),
    )


def _weighted_peak(
    values: np.ndarray, weights: np.ndarray, bin_px: float
) -> tuple[float, float, float]:
    lo = float(np.min(values))
    hi = float(np.max(values))
    if hi - lo < 1e-6:
        return lo, 0.0, float(np.sum(weights))
    nb = max(1, int(np.ceil((hi - lo) / max(bin_px, 0.5))) + 1)
    edges = np.linspace(lo, hi + 1e-6, nb + 1)
    hist, _ = np.histogram(values, bins=edges, weights=weights)
    peak_i = int(np.argmax(hist))
    peak_val = 0.5 * (edges[peak_i] + edges[peak_i + 1])
    # Refine with inliers near peak
    near = np.abs(values - peak_val) <= 2.0
    if np.any(near):
        peak_val = float(np.average(values[near], weights=weights[near]))
        _, mad = median_mad(values[near])
    else:
        mad = float(np.std(values))
    return peak_val, mad, float(hist[peak_i])


def _dense_span(along_sorted: np.ndarray, weights: np.ndarray) -> tuple[float, float]:
    if along_sorted.size < 4:
        return float(along_sorted[0]), float(along_sorted[-1])
    # Cumulative weight; take central 90% mass
    w = weights / max(float(np.sum(weights)), 1e-9)
    cdf = np.cumsum(w)
    i0 = int(np.searchsorted(cdf, 0.05))
    i1 = int(np.searchsorted(cdf, 0.95))
    i1 = min(max(i1, i0 + 1), along_sorted.size - 1)
    return float(along_sorted[i0]), float(along_sorted[i1])


def _make_endpoint(
    semantic: SemanticSide,
    fixed: float,
    along: float,
    is_start: bool,
    pts: Sequence[BoundaryPoint],
    inliers: np.ndarray,
    search_roi: IntRect,
    capture_w: int,
    capture_h: int,
    safety: int,
    cfg: DetectorConfig,
) -> Endpoint | None:
    vertical = semantic in (SemanticSide.LEFT, SemanticSide.RIGHT)
    if vertical:
        x, y = fixed, along
    else:
        x, y = along, fixed

    # Truncation against search ROI / capture borders
    truncated = False
    if vertical:
        if along <= search_roi.top + safety or along >= search_roi.bottom - 1 - safety:
            truncated = True
        if along <= safety or along >= capture_h - 1 - safety:
            truncated = True
        if fixed <= search_roi.left + safety or fixed >= search_roi.right - 1 - safety:
            # fixed on left/right near search bound can also mean incomplete observation
            if semantic == SemanticSide.LEFT and fixed <= search_roi.left + safety:
                truncated = True
            if semantic == SemanticSide.RIGHT and fixed >= search_roi.right - 1 - safety:
                truncated = True
    else:
        if along <= search_roi.left + safety or along >= search_roi.right - 1 - safety:
            truncated = True
        if along <= safety or along >= capture_w - 1 - safety:
            truncated = True
        if fixed <= search_roi.top + safety or fixed >= search_roi.bottom - 1 - safety:
            if semantic == SemanticSide.TOP and fixed <= search_roi.top + safety:
                truncated = True
            if semantic == SemanticSide.BOTTOM and fixed >= search_roi.bottom - 1 - safety:
                truncated = True

    # Local support near endpoint
    along_vals = np.array([p.y if vertical else p.x for p in pts], dtype=np.float64)
    near = inliers & (np.abs(along_vals - along) <= 4.0)
    if int(np.sum(near)) < 2:
        score = 0.15
    else:
        near_pts = [p for p, k in zip(pts, near) if k]
        score = float(np.mean([p.weight for p in near_pts]))
        # Corner topology: expect orthogonal evidence — approximate via variance of fixed
        score = float(np.clip(score * 1.2, 0.0, 1.0))

    return Endpoint(x=float(x), y=float(y), score=score, is_truncated=truncated)


def complete_sides_only(sides: Sequence[CompleteSide]) -> list[CompleteSide]:
    """Complete sides eligible for A/B/C-L/C-II (non-truncated)."""
    return [s for s in sides if not s.is_truncated]
