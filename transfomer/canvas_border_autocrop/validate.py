"""Independent resampled validation of refined rectangles."""

from __future__ import annotations

from typing import Sequence

import numpy as np

from .config import DetectorConfig
from .features import CanvasFeatureMaps, delta_e76
from .refine import refine_rectangle
from .types import (
    BackgroundAppearanceModel,
    EvidenceGrade,
    IntRect,
    RectangleHypothesis,
    SemanticSide,
)


def validate_rectangle(
    rect: IntRect,
    hyp: RectangleHypothesis,
    features: CanvasFeatureMaps,
    model: BackgroundAppearanceModel,
    grown_mask: np.ndarray | None,
    cfg: DetectorConfig,
    rng: np.random.Generator,
) -> tuple[bool, dict, float]:
    metrics: dict = {}
    h, w = features.height, features.width

    outside_scores = []
    transition_scores = []
    for side in SemanticSide:
        o, t = _side_metrics(rect, side, features, model, cfg, rng)
        outside_scores.append(o)
        transition_scores.append(t)
        metrics[f"{side.value}_outside"] = o
        metrics[f"{side.value}_transition"] = t

    mean_out = float(np.mean(outside_scores))
    mean_tr = float(np.mean(transition_scores))
    metrics["mean_outside"] = mean_out
    metrics["mean_transition"] = mean_tr

    if mean_out < cfg.validate_min_outside_score:
        return False, metrics, 0.0
    if mean_tr < cfg.validate_min_transition:
        return False, metrics, 0.0

    # Corners local topology
    corner_ok = _validate_corners(rect, features, model, cfg)
    metrics["corners_ok"] = corner_ok
    if not corner_ok:
        return False, metrics, 0.0

    # Interior must not be same unbounded connected bg domain
    if grown_mask is not None:
        # Sample interior; should be mostly non-grown-bg
        xs = rng.integers(rect.left + 2, max(rect.left + 3, rect.right - 2), size=32)
        ys = rng.integers(rect.top + 2, max(rect.top + 3, rect.bottom - 2), size=32)
        interior_bg = float(np.mean(grown_mask[ys, xs])) if xs.size else 1.0
        metrics["interior_bg_fraction"] = interior_bg
        if interior_bg > 0.55:
            return False, metrics, 0.0

    # Local optima under ±1/±2 perturbation
    base_score = 0.5 * mean_out + 0.5 * mean_tr
    for pert in cfg.validate_perturbation_px:
        worse = 0
        total = 0
        for side in SemanticSide:
            for sign in (-1, 1):
                shifted = _shift_side(rect, side, sign * pert, w, h)
                if shifted is None:
                    continue
                o, t = [], []
                for s2 in SemanticSide:
                    oo, tt = _side_metrics(shifted, s2, features, model, cfg, rng)
                    o.append(oo)
                    t.append(tt)
                sc = 0.5 * float(np.mean(o)) + 0.5 * float(np.mean(t))
                total += 1
                if sc < base_score - cfg.validate_local_optima_margin:
                    worse += 1
        # Majority of perturbations should not improve significantly
        metrics[f"pert_{pert}_worse_ratio"] = worse / max(total, 1)
        if total > 0 and (worse / total) < 0.35:
            # Many perturbations equal/better → not local optimum
            return False, metrics, 0.0

    conf = float(
        np.clip(
            0.4 * mean_out + 0.35 * mean_tr + 0.25 * hyp.confidence,
            0.0,
            1.0,
        )
    )
    metrics["confidence"] = conf
    return True, metrics, conf


def _side_metrics(
    rect: IntRect,
    side: SemanticSide,
    features: CanvasFeatureMaps,
    model: BackgroundAppearanceModel,
    cfg: DetectorConfig,
    rng: np.random.Generator,
) -> tuple[float, float]:
    h, w = features.height, features.width
    n = cfg.validate_sample_count
    # Stratified samples along side, offset from fit samples by +1 pattern
    if side in (SemanticSide.LEFT, SemanticSide.RIGHT):
        ys = np.linspace(rect.top + 1, rect.bottom - 2, n, dtype=np.int32)
        # Independent offset: use odd indices shifted
        ys = np.unique(np.clip(ys + (np.arange(len(ys)) % 3) - 1, 0, h - 1))
        x = rect.left if side == SemanticSide.LEFT else rect.right - 1
        x = int(np.clip(x, 1, w - 2))
        if side == SemanticSide.LEFT:
            out_labs = features.lab[ys, x - 1]
            in_labs = features.lab[ys, min(w - 1, x + 1)]
            g = np.abs(features.gradient_x[ys, x])
        else:
            out_labs = features.lab[ys, min(w - 1, x + 1)]
            in_labs = features.lab[ys, max(0, x - 1)]
            g = np.abs(features.gradient_x[ys, x])
    else:
        xs = np.linspace(rect.left + 1, rect.right - 2, n, dtype=np.int32)
        xs = np.unique(np.clip(xs + (np.arange(len(xs)) % 3) - 1, 0, w - 1))
        y = rect.top if side == SemanticSide.TOP else rect.bottom - 1
        y = int(np.clip(y, 1, h - 2))
        if side == SemanticSide.TOP:
            out_labs = features.lab[y - 1, xs]
            in_labs = features.lab[min(h - 1, y + 1), xs]
            g = np.abs(features.gradient_y[y, xs])
        else:
            out_labs = features.lab[min(h - 1, y + 1), xs]
            in_labs = features.lab[max(0, y - 1), xs]
            g = np.abs(features.gradient_y[y, xs])

    de_out = np.sqrt(np.sum((out_labs - np.asarray(model.center_lab, dtype=np.float32)) ** 2, axis=-1))
    de_in = np.sqrt(np.sum((in_labs - np.asarray(model.center_lab, dtype=np.float32)) ** 2, axis=-1))
    out_score = float(np.mean(np.clip(1.0 - de_out / max(model.weak_delta_e, 1e-3), 0, 1)))
    in_diff = float(np.mean(np.clip(de_in / max(model.strong_delta_e, 1e-3), 0, 1)))
    g_score = float(np.mean(g >= np.median(g))) if g.size else 0.0
    transition = float(np.clip(0.5 * in_diff + 0.3 * (1.0 - out_score + out_score) * 0.5 + 0.2 * g_score, 0, 1))
    # clearer: outside high sim + inside dissimilar
    transition = float(np.clip(0.55 * out_score + 0.45 * in_diff, 0, 1))
    return out_score, transition


def _validate_corners(
    rect: IntRect,
    features: CanvasFeatureMaps,
    model: BackgroundAppearanceModel,
    cfg: DetectorConfig,
) -> bool:
    h, w = features.height, features.width
    corners = [
        (rect.left, rect.top),
        (rect.right - 1, rect.top),
        (rect.left, rect.bottom - 1),
        (rect.right - 1, rect.bottom - 1),
    ]
    ok = 0
    for x, y in corners:
        if not (1 <= x < w - 1 and 1 <= y < h - 1):
            continue
        # Outside diagonal sample should be more bg-like than inside
        ox = x - 2 if x == rect.left else x + 2
        oy = y - 2 if y == rect.top else y + 2
        ix = x + 2 if x == rect.left else x - 2
        iy = y + 2 if y == rect.top else y - 2
        ox, oy = int(np.clip(ox, 0, w - 1)), int(np.clip(oy, 0, h - 1))
        ix, iy = int(np.clip(ix, 0, w - 1)), int(np.clip(iy, 0, h - 1))
        de_o = float(np.linalg.norm(features.lab[oy, ox] - model.center_lab))
        de_i = float(np.linalg.norm(features.lab[iy, ix] - model.center_lab))
        if de_o + 1.0 < de_i:
            ok += 1
    return ok >= 3


def _shift_side(rect: IntRect, side: SemanticSide, delta: int, w: int, h: int) -> IntRect | None:
    l, t, r, b = rect.left, rect.top, rect.right, rect.bottom
    if side == SemanticSide.LEFT:
        l += delta
    elif side == SemanticSide.RIGHT:
        r += delta
    elif side == SemanticSide.TOP:
        t += delta
    else:
        b += delta
    if l >= r - 1 or t >= b - 1:
        return None
    if l < 0 or t < 0 or r > w or b > h:
        return None
    return IntRect(l, t, r, b)
