"""Seeded 4-connected background region growing (OpenCV-accelerated)."""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass

import cv2
import numpy as np

from .config import DetectorConfig
from .similarity import BackgroundSimilarity
from .types import IntRect, SeedPatch, SemanticSide, SIDE_INDEX


@dataclass(slots=True)
class GrownBackground:
    mask: np.ndarray  # bool HxW
    source_label: np.ndarray  # int8 HxW, 0=none, 1..4 = sides
    pixel_count: int
    bbox_fill_ratio: float = 1.0
    hole_score: float = 0.0
    touches_search_border: float = 0.0


def grow_background(
    seeds: list[SeedPatch],
    model_seed_ids: set[int],
    similarity: BackgroundSimilarity,
    search_roi: IntRect,
    cfg: DetectorConfig,
) -> GrownBackground | None:
    h, w = similarity.similarity.shape
    x0, x1 = search_roi.left, search_roi.right
    y0, y1 = search_roi.top, search_roi.bottom
    if x1 <= x0 + 2 or y1 <= y0 + 2:
        return None

    start_seeds = [
        s
        for s in seeds
        if s.accepted and s.seed_id in model_seed_ids and search_roi.contains_point(s.x, s.y)
    ]
    if not start_seeds:
        start_seeds = [
            s for s in seeds if s.seed_id in model_seed_ids and search_roi.contains_point(s.x, s.y)
        ]
    if not start_seeds:
        return None

    strong = similarity.strong_mask
    weak = similarity.weak_mask
    barrier = similarity.barrier_mask

    # Growable: weak similarity, stopped by sustained barriers unless strong
    growable = weak.copy()
    growable[barrier & ~strong] = False
    growable[:y0, :] = False
    growable[y1:, :] = False
    growable[:, :x0] = False
    growable[:, x1:] = False

    # Bridge ≤ grow_gap_bridge_px isolated noise without filling large canvas holes
    gap = max(0, min(cfg.grow_gap_bridge_px, 2))
    sub = growable[y0:y1, x0:x1]
    if gap > 0 and sub.size:
        k = 2 * gap + 1
        kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (k, k))
        closed = cv2.morphologyEx(sub.astype(np.uint8), cv2.MORPH_CLOSE, kernel)
        # Only keep closed pixels that stay within weak band after bridging
        weak_sub = weak[y0:y1, x0:x1]
        strong_sub = strong[y0:y1, x0:x1]
        bridged = (closed > 0) & (weak_sub | strong_sub)
        growable[y0:y1, x0:x1] = bridged

    # Drop seeds that are not growable
    valid_seeds = [s for s in start_seeds if growable[s.y, s.x]]
    if not valid_seeds:
        return None

    nlab, labels = cv2.connectedComponents(growable.astype(np.uint8), connectivity=4)
    if nlab <= 1:
        return None

    keep_labels: set[int] = set()
    label_sides: dict[int, list[int]] = {}
    for s in valid_seeds:
        lid = int(labels[s.y, s.x])
        if lid <= 0:
            continue
        keep_labels.add(lid)
        label_sides.setdefault(lid, []).append(SIDE_INDEX[s.side] + 1)

    if not keep_labels:
        return None

    mask = np.zeros((h, w), dtype=bool)
    source = np.zeros((h, w), dtype=np.int8)
    count = 0
    max_pixels = int((x1 - x0) * (y1 - y0) * cfg.max_grown_fraction)

    for lid in keep_labels:
        comp = labels == lid
        c = int(np.count_nonzero(comp))
        if count + c > max_pixels:
            # Keep largest seed-touched components first
            continue
        mask |= comp
        maj = Counter(label_sides.get(lid, [1])).most_common(1)[0][0]
        source[comp] = maj
        count += c

    if count < 16:
        return None

    ys, xs = np.nonzero(mask)
    bx0, bx1 = int(xs.min()), int(xs.max()) + 1
    by0, by1 = int(ys.min()), int(ys.max()) + 1
    bbox_area = max(1, (bx1 - bx0) * (by1 - by0))
    fill_ratio = float(count) / float(bbox_area)
    hole_score = float(np.clip(1.0 - fill_ratio, 0.0, 1.0))

    # Fraction of search-roi border pixels that are grown (UI chrome leaks)
    border_hits = 0
    border_tot = 0
    for y in (y0, y1 - 1):
        if 0 <= y < h:
            row = mask[y, x0:x1]
            border_hits += int(np.count_nonzero(row))
            border_tot += row.size
    for x in (x0, x1 - 1):
        if 0 <= x < w:
            col = mask[y0:y1, x]
            border_hits += int(np.count_nonzero(col))
            border_tot += col.size
    touch = border_hits / max(border_tot, 1)

    return GrownBackground(
        mask=mask,
        source_label=source,
        pixel_count=count,
        bbox_fill_ratio=fill_ratio,
        hole_score=hole_score,
        touches_search_border=float(touch),
    )


def model_geometry_plausible(grown: GrownBackground, cfg: DetectorConfig) -> bool:
    """Hard filter: workspace bg is typically a frame/L/bands with a canvas hole.

    Solid fills that hug the search border are usually UI chrome.
    Solid fills that do not hug the border are usually the canvas itself.
    """
    # Large hole / frame → good
    if grown.hole_score >= cfg.min_bg_hole_score:
        return True
    # Thin bands may have moderate fill if bbox is tight around strips
    if grown.bbox_fill_ratio <= cfg.max_bg_bbox_fill_ratio and grown.touches_search_border < 0.55:
        return True
    # Solid + heavy search-border contact → UI
    if grown.touches_search_border >= cfg.max_bg_search_border_touch:
        return False
    # Solid interior blob → likely canvas
    if grown.bbox_fill_ratio >= 0.85 and grown.hole_score < 0.12:
        return False
    return grown.hole_score >= cfg.min_bg_hole_score * 0.5
