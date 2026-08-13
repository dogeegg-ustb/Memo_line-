"""Multi-depth background seed sampling inside user ROI.

Samples along each side at several inset depths so workspace-background
frames (between outer UI and inner canvas) are not missed. Edge-only
sampling would preferentially hit UI chrome; deep-center sampling would
preferentially hit canvas.
"""

from __future__ import annotations

import numpy as np

from .config import DetectorConfig
from .features import CanvasFeatureMaps
from .types import IntRect, SeedPatch, SemanticSide


# Inset ratios along ROI short/long axes: shallow → mid → deeper frame bands
_BAND_RATIOS: tuple[float, ...] = (0.04, 0.09, 0.15, 0.22, 0.30)


def sample_background_seeds(
    features: CanvasFeatureMaps,
    user_roi: IntRect,
    cfg: DetectorConfig,
    dpi_scale: float,
) -> list[SeedPatch]:
    h, w = features.height, features.width
    short = min(user_roi.width, user_roi.height)
    size = cfg.seed_size_px(dpi_scale, short)
    half = size // 2
    seeds: list[SeedPatch] = []
    seed_id = 0
    n = cfg.seeds_per_side

    def _patch_stats(cx: int, cy: int) -> tuple[tuple[float, float, float], float, float] | None:
        x0 = max(0, cx - half)
        y0 = max(0, cy - half)
        x1 = min(w, x0 + size)
        y1 = min(h, y0 + size)
        if x1 - x0 < size // 2 or y1 - y0 < size // 2:
            return None
        lab = features.lab[y0:y1, x0:x1]
        g = features.gradient_magnitude[y0:y1, x0:x1]
        v = features.local_variance[y0:y1, x0:x1]
        thr = float(np.percentile(g, 70)) if g.size else 0.0
        grad_density = float(np.mean(g > thr)) if g.size else 1.0
        local_var = float(np.median(v)) if v.size else 1e9
        mean_lab = (
            float(np.median(lab[..., 0])),
            float(np.median(lab[..., 1])),
            float(np.median(lab[..., 2])),
        )
        return mean_lab, grad_density, local_var

    def _reject(grad_d: float, local_var: float, cx: int, cy: int) -> str:
        rcx = (user_roi.left + user_roi.right) * 0.5
        rcy = (user_roi.top + user_roi.bottom) * 0.5
        dx = abs(cx - rcx) / max(user_roi.width * 0.5, 1.0)
        dy = abs(cy - rcy) / max(user_roi.height * 0.5, 1.0)
        # Hard-exclude deep center (canvas body)
        if max(dx, dy) < cfg.seed_center_exclude_ratio * 0.55:
            return "too_central"
        if grad_d > cfg.seed_max_grad_density:
            return "high_gradient"
        if local_var > cfg.seed_max_local_var:
            return "high_variance"
        return ""

    points: list[tuple[SemanticSide, int, int]] = []

    for ratio in _BAND_RATIOS:
        inset_x = max(half + 1, int(round(user_roi.width * ratio)))
        inset_y = max(half + 1, int(round(user_roi.height * ratio)))
        if inset_x * 2 >= user_roi.width - size or inset_y * 2 >= user_roi.height - size:
            continue

        xs = np.linspace(
            user_roi.left + inset_x,
            user_roi.right - inset_x - 1,
            n,
            dtype=np.int32,
        )
        ys = np.linspace(
            user_roi.top + inset_y,
            user_roi.bottom - inset_y - 1,
            n,
            dtype=np.int32,
        )

        y_top = user_roi.top + inset_y
        y_bot = user_roi.bottom - inset_y - 1
        x_left = user_roi.left + inset_x
        x_right = user_roi.right - inset_x - 1

        for x in xs:
            points.append((SemanticSide.TOP, int(x), y_top))
            points.append((SemanticSide.BOTTOM, int(x), y_bot))
        for y in ys:
            points.append((SemanticSide.LEFT, x_left, int(y)))
            points.append((SemanticSide.RIGHT, x_right, int(y)))

        # Corner samples of this band
        points.append((SemanticSide.TOP, x_left, y_top))
        points.append((SemanticSide.TOP, x_right, y_top))
        points.append((SemanticSide.BOTTOM, x_left, y_bot))
        points.append((SemanticSide.BOTTOM, x_right, y_bot))

    # Deduplicate while preserving order
    seen: set[tuple[int, int]] = set()
    for side, cx, cy in points:
        key = (cx, cy)
        if key in seen:
            continue
        seen.add(key)
        if not (0 <= cx < w and 0 <= cy < h):
            continue
        if not user_roi.contains_point(cx, cy):
            continue
        stats = _patch_stats(cx, cy)
        if stats is None:
            seeds.append(SeedPatch(seed_id, side, cx, cy, size, (0.0, 0.0, 0.0), False, "oob"))
            seed_id += 1
            continue
        mean_lab, grad_d, local_var = stats
        reason = _reject(grad_d, local_var, cx, cy)
        seeds.append(
            SeedPatch(
                seed_id,
                side,
                cx,
                cy,
                size,
                mean_lab,
                accepted=reason == "",
                reject_reason=reason,
            )
        )
        seed_id += 1

    return seeds
