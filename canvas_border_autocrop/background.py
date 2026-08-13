"""Robust multi-candidate workspace-background appearance estimation."""

from __future__ import annotations

from typing import Sequence

import numpy as np

from .config import DetectorConfig
from .features import delta_e76_pixels
from .geometry import median_mad
from .types import BackgroundAppearanceModel, IntRect, SeedPatch, SemanticSide


def estimate_background_models(
    seeds: Sequence[SeedPatch],
    cfg: DetectorConfig,
    user_roi: IntRect | None = None,
) -> list[BackgroundAppearanceModel]:
    accepted = [s for s in seeds if s.accepted]
    if len(accepted) < cfg.min_seed_accept_count:
        rejected = [s for s in seeds if not s.accepted and s.reject_reason != "oob"]
        rejected.sort(key=lambda s: 0 if s.reject_reason == "too_central" else 1)
        need = cfg.min_seed_accept_count - len(accepted)
        accepted = list(accepted) + rejected[:need]

    if len(accepted) < 3:
        return []

    labs = np.array([s.mean_lab for s in accepted], dtype=np.float32)
    clusters = _cluster_labs(labs, cfg.max_background_clusters, cfg.strong_delta_e_max)

    models: list[BackgroundAppearanceModel] = []
    for cid, idxs in enumerate(clusters):
        if len(idxs) < 2:
            continue
        pts = labs[idxs]
        center = (
            float(np.median(pts[:, 0])),
            float(np.median(pts[:, 1])),
            float(np.median(pts[:, 2])),
        )
        de = delta_e76_pixels(pts, center)
        _, scale = median_mad(de)
        scale = max(scale, cfg.mad_scale_floor)
        strong = float(np.clip(2.5 * scale, cfg.strong_delta_e_min, cfg.strong_delta_e_max))
        weak = float(np.clip(4.0 * scale, cfg.weak_delta_e_min, cfg.weak_delta_e_max))
        weak = max(weak, strong + 2.0)

        member_seeds = [accepted[i] for i in idxs]
        sides = {s.side for s in member_seeds}
        coverage = len(sides) / 4.0

        # Frame / ring preference: mid-inset seeds beat pure outer UI and pure canvas
        frame_score = _frame_support_score(member_seeds, user_roi)
        # Color stability: prefer tight Lab clusters
        stability = float(np.clip(1.0 - scale / 12.0, 0.0, 1.0))

        conf = (
            0.25 * min(1.0, len(idxs) / max(cfg.min_seed_accept_count, 1))
            + 0.25 * coverage
            + 0.35 * frame_score
            + 0.15 * stability
        )
        if coverage < cfg.min_spatial_coverage and len(idxs) < cfg.min_seed_accept_count + 2:
            conf *= 0.7

        models.append(
            BackgroundAppearanceModel(
                center_lab=center,
                robust_scale=scale,
                strong_delta_e=strong,
                weak_delta_e=weak,
                accepted_seed_ids=[s.seed_id for s in member_seeds],
                spatial_coverage=coverage,
                confidence=float(np.clip(conf, 0.0, 1.0)),
                cluster_id=cid,
            )
        )

    # Prefer frame-like mid-band clusters over sheer seed count
    models.sort(
        key=lambda m: (m.confidence, m.spatial_coverage, -m.robust_scale),
        reverse=True,
    )
    return models


def _frame_support_score(seeds: Sequence[SeedPatch], user_roi: IntRect | None) -> float:
    """Score how well seeds look like a workspace-bg frame (not UI rim / canvas core)."""
    if not seeds:
        return 0.0
    if user_roi is None or user_roi.width < 8 or user_roi.height < 8:
        # Fallback: prefer multi-side coverage
        return len({s.side for s in seeds}) / 4.0

    rw = max(user_roi.width, 1)
    rh = max(user_roi.height, 1)
    depths: list[float] = []
    side_hits = {s: 0 for s in SemanticSide}
    for s in seeds:
        # Normalized inset depth from nearest ROI edge in [0, 0.5]
        d_left = (s.x - user_roi.left) / rw
        d_right = (user_roi.right - 1 - s.x) / rw
        d_top = (s.y - user_roi.top) / rh
        d_bottom = (user_roi.bottom - 1 - s.y) / rh
        depth = min(d_left, d_right, d_top, d_bottom)
        depths.append(depth)
        side_hits[s.side] += 1

    depths_a = np.asarray(depths, dtype=np.float64)
    # Ideal workspace-bg frame sits roughly in 0.05–0.28 of ROI (between UI and canvas)
    in_band = float(np.mean((depths_a >= 0.05) & (depths_a <= 0.30)))
    # Penalize exclusive extreme rim (<0.04) — typical UI chrome
    rim_only = float(np.mean(depths_a < 0.04))
    # Penalize deep interior (>0.32) — typical canvas body
    deep = float(np.mean(depths_a > 0.32))
    multi = sum(1 for v in side_hits.values() if v > 0) / 4.0
    score = 0.45 * in_band + 0.30 * multi + 0.25 * (1.0 - 0.6 * rim_only - 0.8 * deep)
    return float(np.clip(score, 0.0, 1.0))


def _cluster_labs(
    labs: np.ndarray,
    max_clusters: int,
    merge_thresh: float,
) -> list[list[int]]:
    n = labs.shape[0]
    clusters: list[list[int]] = []
    order = np.lexsort((labs[:, 2], labs[:, 1], labs[:, 0]))
    for i in order:
        i = int(i)
        best_c = -1
        best_d = 1e9
        for cidx, members in enumerate(clusters):
            center = np.median(labs[members], axis=0)
            d = float(np.linalg.norm(labs[i] - center))
            if d < best_d:
                best_d = d
                best_c = cidx
        if best_c >= 0 and best_d <= merge_thresh and len(clusters) >= 1:
            clusters[best_c].append(i)
        elif len(clusters) < max_clusters:
            clusters.append([i])
        else:
            clusters[best_c].append(i)
    return clusters
