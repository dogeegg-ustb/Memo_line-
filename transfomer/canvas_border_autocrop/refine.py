"""Full-resolution joint outer-boundary refinement for workspace rect."""

from __future__ import annotations

import numpy as np

from .config import DetectorConfig
from .features import CanvasFeatureMaps, delta_e76
from .types import BackgroundAppearanceModel, IntRect


def refine_rectangle(
    coarse: IntRect,
    features: CanvasFeatureMaps,
    model: BackgroundAppearanceModel,
    cfg: DetectorConfig,
    dpi_scale: float,
) -> IntRect | None:
    """Refine each outer edge: inside≈workspace bg, outside≠bg."""
    h, w = features.height, features.width
    radius = cfg.refine_radius_px(dpi_scale, min(h, w))
    strip = cfg.refine_strip_half_width_px

    left = _refine_vertical_edge(
        features, model, coarse.top, coarse.bottom, coarse.left, -1, radius, strip, w, h, cfg
    )
    right = _refine_vertical_edge(
        features, model, coarse.top, coarse.bottom, coarse.right, +1, radius, strip, w, h, cfg
    )
    top = _refine_horizontal_edge(
        features, model, coarse.left, coarse.right, coarse.top, -1, radius, strip, w, h, cfg
    )
    bottom = _refine_horizontal_edge(
        features, model, coarse.left, coarse.right, coarse.bottom, +1, radius, strip, w, h, cfg
    )

    if None in (left, right, top, bottom):
        return None

    max_shift = cfg.refine_max_shift_px
    if abs(left - coarse.left) > max_shift:
        left = coarse.left + int(np.sign(left - coarse.left) * max_shift)
    if abs(right - coarse.right) > max_shift:
        right = coarse.right + int(np.sign(right - coarse.right) * max_shift)
    if abs(top - coarse.top) > max_shift:
        top = coarse.top + int(np.sign(top - coarse.top) * max_shift)
    if abs(bottom - coarse.bottom) > max_shift:
        bottom = coarse.bottom + int(np.sign(bottom - coarse.bottom) * max_shift)

    if left >= right - 2 or top >= bottom - 2:
        return None
    if left < 0 or top < 0 or right > w or bottom > h:
        return None
    return IntRect(int(left), int(top), int(right), int(bottom))


def _subsample_span(a0: int, a1: int, max_samples: int = 48) -> np.ndarray:
    n = a1 - a0
    if n <= max_samples:
        return np.arange(a0, a1, dtype=np.int32)
    return np.linspace(a0, a1 - 1, max_samples, dtype=np.int32)


def _refine_vertical_edge(
    features: CanvasFeatureMaps,
    model: BackgroundAppearanceModel,
    y0: int,
    y1: int,
    x_init: int,
    outward_sign: int,
    radius: int,
    strip: int,
    w: int,
    h: int,
    cfg: DetectorConfig,
) -> int | None:
    y0 = max(0, y0)
    y1 = min(h, y1)
    if y1 - y0 < 4:
        return None
    ys = _subsample_span(y0, y1)
    best_x = x_init
    best_cost = -1e9
    costs: list[tuple[int, float]] = []
    weak = max(model.weak_delta_e, 1e-3)

    for dx in range(-radius, radius + 1):
        x = x_init + dx
        if x < 1 or x >= w - 1:
            continue
        if outward_sign < 0:
            xo0, xo1 = max(0, x - strip), x
            xi0, xi1 = x, min(w, x + strip)
        else:
            xo0, xo1 = x, min(w, x + strip)
            xi0, xi1 = max(0, x - strip), x
        if xo1 <= xo0 or xi1 <= xi0:
            continue

        outside = features.lab[ys[:, None], np.arange(xo0, xo1)[None, :]]
        inside = features.lab[ys[:, None], np.arange(xi0, xi1)[None, :]]
        # Flatten last dims for delta_e
        de_out = delta_e76(outside.reshape(-1, outside.shape[-1]).reshape(outside.shape), model.center_lab)
        de_in = delta_e76(inside.reshape(-1, inside.shape[-1]).reshape(inside.shape), model.center_lab)
        out_diff = float(np.mean(np.clip(de_out / weak, 0, 1)))
        in_sim = float(np.mean(np.clip(1.0 - de_in / weak, 0, 1)))

        g = np.abs(features.gradient_x[ys, x])
        g_med = float(np.median(g)) if g.size else 0.0
        g_score = float(np.mean(g > g_med)) if g.size else 0.0
        trans = out_diff * 0.45 + in_sim * 0.40 + g_score * 0.25
        costs.append((x, trans))
        if trans > best_cost:
            best_cost = trans
            best_x = x

    if not costs:
        return None

    costs.sort(key=lambda t: t[1], reverse=True)
    if len(costs) >= 2:
        x1, c1 = costs[0]
        for x2, c2 in costs[1:6]:
            if abs(x2 - x1) <= cfg.border_double_peak_gap_px and c2 > c1 * 0.85:
                if outward_sign < 0:
                    best_x = min(x1, x2)
                else:
                    best_x = max(x1, x2)
                break
    return int(best_x)


def _refine_horizontal_edge(
    features: CanvasFeatureMaps,
    model: BackgroundAppearanceModel,
    x0: int,
    x1: int,
    y_init: int,
    outward_sign: int,
    radius: int,
    strip: int,
    w: int,
    h: int,
    cfg: DetectorConfig,
) -> int | None:
    x0 = max(0, x0)
    x1 = min(w, x1)
    if x1 - x0 < 4:
        return None
    xs = _subsample_span(x0, x1)
    best_y = y_init
    best_cost = -1e9
    costs: list[tuple[int, float]] = []
    weak = max(model.weak_delta_e, 1e-3)

    for dy in range(-radius, radius + 1):
        y = y_init + dy
        if y < 1 or y >= h - 1:
            continue
        if outward_sign < 0:
            yo0, yo1 = max(0, y - strip), y
            yi0, yi1 = y, min(h, y + strip)
        else:
            yo0, yo1 = y, min(h, y + strip)
            yi0, yi1 = max(0, y - strip), y
        if yo1 <= yo0 or yi1 <= yi0:
            continue

        outside = features.lab[np.arange(yo0, yo1)[:, None], xs[None, :]]
        inside = features.lab[np.arange(yi0, yi1)[:, None], xs[None, :]]
        de_out = delta_e76(outside, model.center_lab)
        de_in = delta_e76(inside, model.center_lab)
        out_diff = float(np.mean(np.clip(de_out / weak, 0, 1)))
        in_sim = float(np.mean(np.clip(1.0 - de_in / weak, 0, 1)))
        g = np.abs(features.gradient_y[y, xs])
        g_med = float(np.median(g)) if g.size else 0.0
        g_score = float(np.mean(g > g_med)) if g.size else 0.0
        trans = out_diff * 0.45 + in_sim * 0.40 + g_score * 0.25
        costs.append((y, trans))
        if trans > best_cost:
            best_cost = trans
            best_y = y

    if not costs:
        return None
    costs.sort(key=lambda t: t[1], reverse=True)
    if len(costs) >= 2:
        y1, c1 = costs[0]
        for y2, c2 in costs[1:6]:
            if abs(y2 - y1) <= cfg.border_double_peak_gap_px and c2 > c1 * 0.85:
                if outward_sign < 0:
                    best_y = min(y1, y2)
                else:
                    best_y = max(y1, y2)
                break
    return int(best_y)
