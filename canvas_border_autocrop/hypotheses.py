"""Enumerate A / B / C-L / C-II rectangle hypotheses."""

from __future__ import annotations

from itertools import combinations
from typing import Sequence

import numpy as np

from .config import DetectorConfig
from .types import (
    CompleteSide,
    EvidenceGrade,
    IntRect,
    Orientation,
    RectangleHypothesis,
    SemanticSide,
)


def build_hypotheses(
    complete_sides: Sequence[CompleteSide],
    search_roi: IntRect,
    user_roi: IntRect,
    cfg: DetectorConfig,
    weak_support_fn,
) -> list[RectangleHypothesis]:
    """Enumerate all grades then return list for unified scoring.

    weak_support_fn(semantic_side, fixed_coord, start, end) -> coverage float
    """
    sides = {s.semantic_side: s for s in complete_sides if not s.is_truncated}
    hyps: list[RectangleHypothesis] = []

    hyps.extend(_hypotheses_a(sides, cfg))
    hyps.extend(_hypotheses_b(sides, search_roi, cfg, weak_support_fn))
    hyps.extend(_hypotheses_c_l(sides, search_roi, cfg, weak_support_fn))
    hyps.extend(_hypotheses_c_ii(sides, cfg))

    # Filter hard geometry
    filtered: list[RectangleHypothesis] = []
    for h in hyps:
        r = h.rect
        if r.width < cfg.min_canvas_side_px or r.height < cfg.min_canvas_side_px:
            continue
        if r.area < cfg.min_canvas_area_px:
            continue
        if not search_roi.contains_point(r.left, r.top) and not (
            search_roi.left <= r.left < search_roi.right
            and search_roi.top <= r.top < search_roi.bottom
        ):
            # Allow rect inside search; edges should be within search
            pass
        if r.left < search_roi.left or r.right > search_roi.right:
            continue
        if r.top < search_roi.top or r.bottom > search_roi.bottom:
            continue
        # Must contain user ROI interior anchor
        if not r.intersects_interior(user_roi, inset=max(1, min(user_roi.width, user_roi.height) // 10)):
            # softer: center of user ROI inside candidate
            if not r.contains_point(
                (user_roi.left + user_roi.right) // 2,
                (user_roi.top + user_roi.bottom) // 2,
            ):
                continue
        filtered.append(h)
    return filtered


def _rect_from_sides(
    left: float | None,
    top: float | None,
    right: float | None,
    bottom: float | None,
) -> IntRect | None:
    if None in (left, top, right, bottom):
        return None
    l, t, r, b = int(round(left)), int(round(top)), int(round(right)), int(round(bottom))
    if l >= r or t >= b:
        return None
    return IntRect(l, t, r, b)


def _hypotheses_a(sides: dict[SemanticSide, CompleteSide], cfg: DetectorConfig) -> list[RectangleHypothesis]:
    need = [SemanticSide.LEFT, SemanticSide.TOP, SemanticSide.RIGHT, SemanticSide.BOTTOM]
    if not all(s in sides for s in need):
        return []
    l, t, r, b = (
        sides[SemanticSide.LEFT].fixed_coordinate,
        sides[SemanticSide.TOP].fixed_coordinate,
        sides[SemanticSide.RIGHT].fixed_coordinate,
        sides[SemanticSide.BOTTOM].fixed_coordinate,
    )
    # Corner closure: horizontal ends vs vertical fixed etc.
    err = 0.0
    for hs, vs, use_start in (
        (SemanticSide.TOP, SemanticSide.LEFT, True),
        (SemanticSide.TOP, SemanticSide.RIGHT, False),
        (SemanticSide.BOTTOM, SemanticSide.LEFT, True),
        (SemanticSide.BOTTOM, SemanticSide.RIGHT, False),
    ):
        hside = sides[hs]
        vside = sides[vs]
        h_along = hside.start_coordinate if use_start else hside.end_coordinate
        # For top: along is x; vertical fixed is x
        err = max(err, abs(h_along - vside.fixed_coordinate))
        v_along = vside.start_coordinate if (hs == SemanticSide.TOP) else vside.end_coordinate
        err = max(err, abs(v_along - hside.fixed_coordinate))
    if err > cfg.corner_closure_tol_px * 2:
        # soft check with spans containing corners
        if not (
            sides[SemanticSide.TOP].start_coordinate - cfg.corner_closure_tol_px
            <= l
            <= sides[SemanticSide.TOP].end_coordinate + cfg.corner_closure_tol_px
        ):
            return []
    rect = _rect_from_sides(l, t, r, b)
    if rect is None:
        return []
    return [
        RectangleHypothesis(
            rect=rect,
            grade=EvidenceGrade.A,
            observed_sides=list(need),
            inferred_sides=[],
            score=0.0,
            confidence=0.0,
            side_refs={s.value: sides[s] for s in need},
        )
    ]


def _hypotheses_b(
    sides: dict[SemanticSide, CompleteSide],
    search_roi: IntRect,
    cfg: DetectorConfig,
    weak_support_fn,
) -> list[RectangleHypothesis]:
    hyps: list[RectangleHypothesis] = []
    all_sides = list(SemanticSide)
    for missing in all_sides:
        present = [s for s in all_sides if s != missing]
        if not all(s in sides for s in present):
            continue
        # Need at least one adjacent pair
        coords = {
            SemanticSide.LEFT: sides[SemanticSide.LEFT].fixed_coordinate if SemanticSide.LEFT in sides else None,
            SemanticSide.TOP: sides[SemanticSide.TOP].fixed_coordinate if SemanticSide.TOP in sides else None,
            SemanticSide.RIGHT: sides[SemanticSide.RIGHT].fixed_coordinate if SemanticSide.RIGHT in sides else None,
            SemanticSide.BOTTOM: sides[SemanticSide.BOTTOM].fixed_coordinate if SemanticSide.BOTTOM in sides else None,
        }
        # Infer missing from opposite span endpoints of adjacent sides
        if missing == SemanticSide.LEFT:
            # left from top/bottom start
            cands = []
            if SemanticSide.TOP in sides:
                cands.append(sides[SemanticSide.TOP].start_coordinate)
            if SemanticSide.BOTTOM in sides:
                cands.append(sides[SemanticSide.BOTTOM].start_coordinate)
            if not cands:
                continue
            coords[SemanticSide.LEFT] = float(np.median(cands))
            start = coords[SemanticSide.TOP] if coords[SemanticSide.TOP] is not None else search_roi.top
            end = coords[SemanticSide.BOTTOM] if coords[SemanticSide.BOTTOM] is not None else search_roi.bottom
        elif missing == SemanticSide.RIGHT:
            cands = []
            if SemanticSide.TOP in sides:
                cands.append(sides[SemanticSide.TOP].end_coordinate)
            if SemanticSide.BOTTOM in sides:
                cands.append(sides[SemanticSide.BOTTOM].end_coordinate)
            if not cands:
                continue
            coords[SemanticSide.RIGHT] = float(np.median(cands))
            start = coords[SemanticSide.TOP] or search_roi.top
            end = coords[SemanticSide.BOTTOM] or search_roi.bottom
        elif missing == SemanticSide.TOP:
            cands = []
            if SemanticSide.LEFT in sides:
                cands.append(sides[SemanticSide.LEFT].start_coordinate)
            if SemanticSide.RIGHT in sides:
                cands.append(sides[SemanticSide.RIGHT].start_coordinate)
            coords[SemanticSide.TOP] = float(np.median(cands))
            start = coords[SemanticSide.LEFT] or search_roi.left
            end = coords[SemanticSide.RIGHT] or search_roi.right
        else:
            cands = []
            if SemanticSide.LEFT in sides:
                cands.append(sides[SemanticSide.LEFT].end_coordinate)
            if SemanticSide.RIGHT in sides:
                cands.append(sides[SemanticSide.RIGHT].end_coordinate)
            coords[SemanticSide.BOTTOM] = float(np.median(cands))
            start = coords[SemanticSide.LEFT] or search_roi.left
            end = coords[SemanticSide.RIGHT] or search_roi.right

        fixed = coords[missing]
        weak = weak_support_fn(missing, fixed, min(start, end), max(start, end))
        if weak < cfg.weak_inferred_min_coverage:
            continue
        rect = _rect_from_sides(
            coords[SemanticSide.LEFT],
            coords[SemanticSide.TOP],
            coords[SemanticSide.RIGHT],
            coords[SemanticSide.BOTTOM],
        )
        if rect is None:
            continue
        hyps.append(
            RectangleHypothesis(
                rect=rect,
                grade=EvidenceGrade.B,
                observed_sides=present,
                inferred_sides=[missing],
                score=0.0,
                confidence=0.0,
                side_refs={s.value: sides[s] for s in present},
            )
        )
    return hyps


def _hypotheses_c_l(
    sides: dict[SemanticSide, CompleteSide],
    search_roi: IntRect,
    cfg: DetectorConfig,
    weak_support_fn,
) -> list[RectangleHypothesis]:
    hyps: list[RectangleHypothesis] = []
    # Adjacent pairs: L-T, T-R, R-B, B-L
    adjacent = [
        (SemanticSide.LEFT, SemanticSide.TOP),
        (SemanticSide.TOP, SemanticSide.RIGHT),
        (SemanticSide.RIGHT, SemanticSide.BOTTOM),
        (SemanticSide.BOTTOM, SemanticSide.LEFT),
    ]
    for a, b in adjacent:
        if a not in sides or b not in sides:
            continue
        sa, sb = sides[a], sides[b]
        if sa.orientation == sb.orientation:
            continue
        # Shared corner endpoint
        corner = _shared_corner(sa, sb, cfg.corner_closure_tol_px)
        if corner is None:
            continue
        # Both other endpoints complete (already in CompleteSide)
        left = top = right = bottom = None
        observed = [a, b]
        # Determine rect from L-shape spans
        if a == SemanticSide.LEFT and b == SemanticSide.TOP:
            left = sa.fixed_coordinate
            top = sb.fixed_coordinate
            right = sb.end_coordinate
            bottom = sa.end_coordinate
            # shared should be near (left, top)
        elif a == SemanticSide.TOP and b == SemanticSide.RIGHT:
            top = sa.fixed_coordinate
            right = sb.fixed_coordinate
            left = sa.start_coordinate
            bottom = sb.end_coordinate
        elif a == SemanticSide.RIGHT and b == SemanticSide.BOTTOM:
            right = sa.fixed_coordinate
            bottom = sb.fixed_coordinate
            left = sb.start_coordinate
            top = sa.start_coordinate
        elif a == SemanticSide.BOTTOM and b == SemanticSide.LEFT:
            bottom = sa.fixed_coordinate
            left = sb.fixed_coordinate
            right = sa.end_coordinate
            top = sb.start_coordinate
        else:
            continue

        rect = _rect_from_sides(left, top, right, bottom)
        if rect is None:
            continue

        # Weak verify two inferred sides
        inferred = [s for s in SemanticSide if s not in observed]
        ok = True
        for inf in inferred:
            if inf == SemanticSide.LEFT:
                fixed, lo, hi = rect.left, rect.top, rect.bottom
            elif inf == SemanticSide.RIGHT:
                fixed, lo, hi = rect.right, rect.top, rect.bottom
            elif inf == SemanticSide.TOP:
                fixed, lo, hi = rect.top, rect.left, rect.right
            else:
                fixed, lo, hi = rect.bottom, rect.left, rect.right
            if weak_support_fn(inf, float(fixed), float(lo), float(hi)) < cfg.weak_inferred_min_coverage:
                ok = False
                break
        if not ok:
            continue
        hyps.append(
            RectangleHypothesis(
                rect=rect,
                grade=EvidenceGrade.C_L,
                observed_sides=observed,
                inferred_sides=inferred,
                score=0.0,
                confidence=0.0,
                side_refs={a.value: sa, b.value: sb},
            )
        )
    return hyps


def _shared_corner(sa: CompleteSide, sb: CompleteSide, tol: float) -> tuple[float, float] | None:
    """Find shared high-confidence endpoint between horizontal and vertical side."""
    ends_a = [sa.start_endpoint, sa.end_endpoint]
    ends_b = [sb.start_endpoint, sb.end_endpoint]
    best = None
    best_d = 1e9
    for ea in ends_a:
        for eb in ends_b:
            d = abs(ea.x - eb.x) + abs(ea.y - eb.y)
            if d < best_d:
                best_d = d
                best = ((ea.x + eb.x) * 0.5, (ea.y + eb.y) * 0.5, (ea.score + eb.score) * 0.5)
    if best is None or best_d > tol * 2:
        return None
    if best[2] < 0.3:
        return None
    return best[0], best[1]


def _hypotheses_c_ii(sides: dict[SemanticSide, CompleteSide], cfg: DetectorConfig) -> list[RectangleHypothesis]:
    hyps: list[RectangleHypothesis] = []
    pairs = [
        (SemanticSide.LEFT, SemanticSide.RIGHT),
        (SemanticSide.TOP, SemanticSide.BOTTOM),
    ]
    for a, b in pairs:
        if a not in sides or b not in sides:
            continue
        sa, sb = sides[a], sides[b]
        if sa.orientation != sb.orientation:
            continue
        # Endpoint alignment
        if abs(sa.start_coordinate - sb.start_coordinate) > cfg.c_ii_endpoint_align_tol_px:
            continue
        if abs(sa.end_coordinate - sb.end_coordinate) > cfg.c_ii_endpoint_align_tol_px:
            continue
        len_a, len_b = sa.span, sb.span
        if abs(len_a - len_b) > max(cfg.c_ii_length_abs_tol_px, cfg.c_ii_length_rel_tol * max(len_a, len_b)):
            continue
        if a in (SemanticSide.LEFT, SemanticSide.RIGHT):
            left = min(sa.fixed_coordinate, sb.fixed_coordinate)
            right = max(sa.fixed_coordinate, sb.fixed_coordinate)
            top = float(np.median([sa.start_coordinate, sb.start_coordinate]))
            bottom = float(np.median([sa.end_coordinate, sb.end_coordinate]))
        else:
            top = min(sa.fixed_coordinate, sb.fixed_coordinate)
            bottom = max(sa.fixed_coordinate, sb.fixed_coordinate)
            left = float(np.median([sa.start_coordinate, sb.start_coordinate]))
            right = float(np.median([sa.end_coordinate, sb.end_coordinate]))
        rect = _rect_from_sides(left, top, right, bottom)
        if rect is None:
            continue
        hyps.append(
            RectangleHypothesis(
                rect=rect,
                grade=EvidenceGrade.C_II,
                observed_sides=[a, b],
                inferred_sides=[],
                score=0.0,
                confidence=0.0,
                side_refs={a.value: sa, b.value: sb},
            )
        )
    return hyps
