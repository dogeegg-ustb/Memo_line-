"""Extract directional boundary termination evidence from grown background."""

from __future__ import annotations

from typing import Sequence

import numpy as np

from .config import DetectorConfig
from .features import CanvasFeatureMaps
from .grower import GrownBackground
from .similarity import BackgroundSimilarity
from .types import BoundaryPoint, IntRect, SemanticSide


def extract_boundary_points(
    grown: GrownBackground,
    similarity: BackgroundSimilarity,
    features: CanvasFeatureMaps,
    search_roi: IntRect,
    cfg: DetectorConfig,
) -> dict[SemanticSide, list[BoundaryPoint]]:
    """From connected background, find inward termination points per semantic side.

    Left side: for each row, leftmost background pixel's left edge (canvas left).
    Actually: workspace background surrounds canvas. Growing from ROI periphery,
    background is OUTSIDE the canvas. Termination going inward = canvas edge.

    For LEFT semantic side of canvas: scan each row left→right within search ROI,
    find transition from background to non-background (first inward exit from bg
    when coming from left? Wait.

    Architecture: "按四个方向从已连接背景向内提取终止点"
    Background is peripheral. Canvas is the hole / interior non-bg region.
    From left: walk rightward through bg; termination = last bg before non-bg
    when approaching canvas from left → that x is near left canvas edge.

    So for LEFT: per row y, find contiguous bg from left; the rightmost bg pixel
    of the left strip before hitting interior = candidate left edge.
    Simpler robust approach: for each row, find all bg→nonbg transitions going
    left-to-right; take the first strong transition that has bg on left.
    """
    mask = grown.mask
    labels = grown.source_label
    sim = similarity.similarity
    gmag = features.gradient_magnitude
    var = features.local_variance
    gx = features.gradient_x
    gy = features.gradient_y

    x0, x1 = search_roi.left, search_roi.right
    y0, y1 = search_roi.top, search_roi.bottom
    roi_mask = mask[y0:y1, x0:x1]
    if not np.any(roi_mask):
        return {s: [] for s in SemanticSide}

    # Precompute interior non-bg inside search
    out: dict[SemanticSide, list[BoundaryPoint]] = {s: [] for s in SemanticSide}

    # Vectorized-ish per-row/col scans using numpy
    sub = mask[y0:y1, x0:x1]
    lab_sub = labels[y0:y1, x0:x1]
    sim_sub = sim[y0:y1, x0:x1]
    g_sub = gmag[y0:y1, x0:x1]
    v_sub = var[y0:y1, x0:x1]
    gx_sub = gx[y0:y1, x0:x1]
    gy_sub = gy[y0:y1, x0:x1]
    hh, ww = sub.shape

    # Filter thresholds for content interference
    var_cut = float(np.percentile(v_sub[sub], 92)) if np.any(sub) else 1e9

    # LEFT: bg True then False when scanning L→R
    for yi in range(hh):
        row = sub[yi]
        if not row.any() or row.all():
            continue
        # find transitions 1→0
        prev = row[:-1]
        nxt = row[1:]
        edges = np.where(prev & ~nxt)[0]
        for xi in edges:
            # require some bg run to the left
            left_run = 0
            j = xi
            while j >= 0 and row[j]:
                left_run += 1
                j -= 1
            if left_run < 2:
                continue
            # require non-bg depth to the right
            right_run = 0
            j = xi + 1
            while j < ww and not row[j]:
                right_run += 1
                j += 1
            if right_run < 2:
                continue
            if v_sub[yi, xi] > var_cut and g_sub[yi, xi] < float(np.median(g_sub)):
                continue
            sb = float(sim_sub[yi, max(0, xi - 1)])
            sa = float(sim_sub[yi, min(ww - 1, xi + 1)])
            if sb < 0.3 or sa > 0.75:
                continue
            w = float(min(1.0, left_run / 8.0) * min(1.0, (sb - sa + 0.5)))
            w *= float(np.clip(abs(gx_sub[yi, xi]) / (abs(gx_sub[yi, xi]) + abs(gy_sub[yi, xi]) + 1e-3), 0.2, 1.0))
            if w < 0.12:
                continue
            out[SemanticSide.LEFT].append(
                BoundaryPoint(
                    x=x0 + int(xi),
                    y=y0 + yi,
                    direction=SemanticSide.LEFT,
                    source_side=int(lab_sub[yi, xi]),
                    similarity_before=sb,
                    similarity_after=sa,
                    directional_gradient=float(abs(gx_sub[yi, xi])),
                    local_variance=float(v_sub[yi, xi]),
                    weight=w,
                )
            )

    # RIGHT: bg True then False scanning R→L ≡ False→True scanning L→R near right strip
    for yi in range(hh):
        row = sub[yi]
        if not row.any() or row.all():
            continue
        prev = row[:-1]
        nxt = row[1:]
        # nonbg → bg going L→R means entering right background from canvas
        edges = np.where(~prev & nxt)[0]
        for xi in edges:
            # xi is last canvas pixel; edge at xi+1 is first bg
            edge_x = xi + 1
            right_run = 0
            j = edge_x
            while j < ww and row[j]:
                right_run += 1
                j += 1
            if right_run < 2:
                continue
            left_run = 0
            j = edge_x - 1
            while j >= 0 and not row[j]:
                left_run += 1
                j -= 1
            if left_run < 2:
                continue
            if v_sub[yi, edge_x] > var_cut and g_sub[yi, edge_x] < float(np.median(g_sub)):
                continue
            sb = float(sim_sub[yi, min(ww - 1, edge_x)])
            sa = float(sim_sub[yi, max(0, edge_x - 1)])
            if sb < 0.3 or sa > 0.75:
                continue
            w = float(min(1.0, right_run / 8.0) * min(1.0, (sb - sa + 0.5)))
            w *= float(np.clip(abs(gx_sub[yi, edge_x]) / (abs(gx_sub[yi, edge_x]) + abs(gy_sub[yi, edge_x]) + 1e-3), 0.2, 1.0))
            if w < 0.12:
                continue
            out[SemanticSide.RIGHT].append(
                BoundaryPoint(
                    x=x0 + int(edge_x),
                    y=y0 + yi,
                    direction=SemanticSide.RIGHT,
                    source_side=int(lab_sub[yi, min(edge_x, ww - 1)]),
                    similarity_before=sb,
                    similarity_after=sa,
                    directional_gradient=float(abs(gx_sub[yi, edge_x])),
                    local_variance=float(v_sub[yi, edge_x]),
                    weight=w,
                )
            )

    # TOP
    for xi in range(ww):
        col = sub[:, xi]
        if not col.any() or col.all():
            continue
        prev = col[:-1]
        nxt = col[1:]
        edges = np.where(prev & ~nxt)[0]
        for yi in edges:
            up_run = 0
            j = yi
            while j >= 0 and col[j]:
                up_run += 1
                j -= 1
            if up_run < 2:
                continue
            down_run = 0
            j = yi + 1
            while j < hh and not col[j]:
                down_run += 1
                j += 1
            if down_run < 2:
                continue
            if v_sub[yi, xi] > var_cut and g_sub[yi, xi] < float(np.median(g_sub)):
                continue
            sb = float(sim_sub[max(0, yi - 1), xi])
            sa = float(sim_sub[min(hh - 1, yi + 1), xi])
            if sb < 0.3 or sa > 0.75:
                continue
            w = float(min(1.0, up_run / 8.0) * min(1.0, (sb - sa + 0.5)))
            w *= float(np.clip(abs(gy_sub[yi, xi]) / (abs(gx_sub[yi, xi]) + abs(gy_sub[yi, xi]) + 1e-3), 0.2, 1.0))
            if w < 0.12:
                continue
            out[SemanticSide.TOP].append(
                BoundaryPoint(
                    x=x0 + xi,
                    y=y0 + int(yi),
                    direction=SemanticSide.TOP,
                    source_side=int(lab_sub[yi, xi]),
                    similarity_before=sb,
                    similarity_after=sa,
                    directional_gradient=float(abs(gy_sub[yi, xi])),
                    local_variance=float(v_sub[yi, xi]),
                    weight=w,
                )
            )

    # BOTTOM
    for xi in range(ww):
        col = sub[:, xi]
        if not col.any() or col.all():
            continue
        prev = col[:-1]
        nxt = col[1:]
        edges = np.where(~prev & nxt)[0]
        for yi in edges:
            edge_y = yi + 1
            down_run = 0
            j = edge_y
            while j < hh and col[j]:
                down_run += 1
                j += 1
            if down_run < 2:
                continue
            up_run = 0
            j = edge_y - 1
            while j >= 0 and not col[j]:
                up_run += 1
                j -= 1
            if up_run < 2:
                continue
            if v_sub[edge_y, xi] > var_cut and g_sub[edge_y, xi] < float(np.median(g_sub)):
                continue
            sb = float(sim_sub[min(hh - 1, edge_y), xi])
            sa = float(sim_sub[max(0, edge_y - 1), xi])
            if sb < 0.3 or sa > 0.75:
                continue
            w = float(min(1.0, down_run / 8.0) * min(1.0, (sb - sa + 0.5)))
            w *= float(np.clip(abs(gy_sub[edge_y, xi]) / (abs(gx_sub[edge_y, xi]) + abs(gy_sub[edge_y, xi]) + 1e-3), 0.2, 1.0))
            if w < 0.12:
                continue
            out[SemanticSide.BOTTOM].append(
                BoundaryPoint(
                    x=x0 + xi,
                    y=y0 + int(edge_y),
                    direction=SemanticSide.BOTTOM,
                    source_side=int(lab_sub[min(edge_y, hh - 1), xi]),
                    similarity_before=sb,
                    similarity_after=sa,
                    directional_gradient=float(abs(gy_sub[edge_y, xi])),
                    local_variance=float(v_sub[edge_y, xi]),
                    weight=w,
                )
            )

    # Suppress isolated points: keep only points near others along the side
    for side in list(out.keys()):
        out[side] = _suppress_isolated(out[side], side, min_neighbors=2, radius=6)

    return out


def _suppress_isolated(
    points: list[BoundaryPoint],
    side: SemanticSide,
    min_neighbors: int,
    radius: int,
) -> list[BoundaryPoint]:
    if len(points) < 3:
        return []
    coords = np.array(
        [(p.y if side in (SemanticSide.LEFT, SemanticSide.RIGHT) else p.x) for p in points],
        dtype=np.int32,
    )
    kept: list[BoundaryPoint] = []
    for i, p in enumerate(points):
        d = np.abs(coords - coords[i])
        if int(np.sum(d <= radius)) - 1 >= min_neighbors:
            kept.append(p)
    return kept
