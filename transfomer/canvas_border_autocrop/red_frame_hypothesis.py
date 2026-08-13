"""Complete red-frame (viewport) hypotheses from 4/3/2/1 directed corners (§6.5–6.6)."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

from .transform_types import (
    CornerSemantic,
    DirectedRedCorner,
    RedFrameEvidenceGrade,
    RedFrameObservation,
    TransformStatus,
)
from .types import IntRect


@dataclass(slots=True)
class ViewportHypothesis:
    rect: IntRect
    grade: RedFrameEvidenceGrade
    used_semantics: list[str]
    score: float
    confidence: float


def build_viewport_hypotheses(
    red: RedFrameObservation,
    workspace_rect: IntRect,
    navigator_canvas: IntRect,
    *,
    aspect_tol: float = 0.08,
) -> tuple[TransformStatus, ViewportHypothesis | None, list[ViewportHypothesis], str]:
    """Unified 4/3/2/1 completion. Axes never swapped (§6.6)."""
    corners = list(red.directed_corners)
    if not corners:
        return TransformStatus.INSUFFICIENT_RED_FRAME_GEOMETRY, None, [], "no corners"

    Ww = max(1, workspace_rect.width)
    Wh = max(1, workspace_rect.height)
    target_aspect = Ww / Wh  # Vw/Vh ≈ Ww/Wh

    hyps: list[ViewportHypothesis] = []

    by_sem = {c.semantic: c for c in corners}
    n = len(by_sem)

    if n >= 4:
        hyp = _from_four(by_sem, target_aspect, aspect_tol)
        if hyp:
            hyps.append(hyp)
    if n >= 3:
        hyp = _from_three(by_sem, target_aspect, aspect_tol)
        if hyp:
            hyps.append(hyp)
    if n >= 2:
        hyps.extend(_from_two(list(by_sem.values()), target_aspect, aspect_tol, navigator_canvas))
    if n >= 1:
        hyps.extend(_from_one(list(by_sem.values()), target_aspect, aspect_tol, navigator_canvas, workspace_rect))

    # Filter: viewport must intersect navigator canvas
    valid: list[ViewportHypothesis] = []
    for h in hyps:
        if not h.rect.is_valid() or h.rect.width < 3 or h.rect.height < 3:
            continue
        if _intersection_area(h.rect, navigator_canvas) <= 0:
            continue
        # Scale constraint: aspect vs workspace
        va = h.rect.width / max(h.rect.height, 1)
        if abs(va - target_aspect) / max(target_aspect, 1e-6) > aspect_tol * 2.5:
            continue
        valid.append(h)

    if not valid:
        # Distinguish aspect failure
        if hyps:
            return TransformStatus.SCALE_CONSTRAINT_FAILED, None, hyps, "viewport aspect vs workspace failed"
        return TransformStatus.INSUFFICIENT_RED_FRAME_GEOMETRY, None, [], "no viewport hypothesis"

    valid.sort(key=lambda h: (h.score, h.confidence), reverse=True)
    best = valid[0]
    if len(valid) >= 2:
        second = valid[1]
        if abs(best.score - second.score) < 0.05 and _iou(best.rect, second.rect) < 0.6:
            return TransformStatus.RED_FRAME_AMBIGUOUS, None, valid, "ambiguous viewport hypotheses"
    return TransformStatus.OK, best, valid, "ok"


def rect_from_corner(semantic: CornerSemantic, x: float, y: float, W: float, H: float) -> IntRect:
    """Architecture §6.5 single-corner completion (image Y down)."""
    if semantic == CornerSemantic.LT:
        return _iround(x, y, x + W, y + H)
    if semantic == CornerSemantic.RT:
        return _iround(x - W, y, x, y + H)
    if semantic == CornerSemantic.LB:
        return _iround(x, y - H, x + W, y)
    return _iround(x - W, y - H, x, y)


def _iround(l: float, t: float, r: float, b: float) -> IntRect:
    return IntRect(int(round(l)), int(round(t)), int(round(r)), int(round(b)))


def _from_four(
    by_sem: dict[CornerSemantic, DirectedRedCorner],
    target_aspect: float,
    tol: float,
) -> Optional[ViewportHypothesis]:
    need = (CornerSemantic.LT, CornerSemantic.RT, CornerSemantic.LB, CornerSemantic.RB)
    if not all(s in by_sem for s in need):
        return None
    xs = [by_sem[s].position_capture_px[0] for s in need]
    ys = [by_sem[s].position_capture_px[1] for s in need]
    left, right = min(xs), max(xs)
    top, bottom = min(ys), max(ys)
    rect = _iround(left, top, right, bottom)
    if not rect.is_valid():
        return None
    va = rect.width / max(rect.height, 1)
    if abs(va - target_aspect) / max(target_aspect, 1e-6) > tol * 3:
        return None
    return ViewportHypothesis(
        rect=rect,
        grade=RedFrameEvidenceGrade.CORNERS_4,
        used_semantics=[s.value for s in need],
        score=0.95,
        confidence=0.95,
    )


def _from_three(
    by_sem: dict[CornerSemantic, DirectedRedCorner],
    target_aspect: float,
    tol: float,
) -> Optional[ViewportHypothesis]:
    if len(by_sem) < 3:
        return None
    # Infer missing corner from axis-aligned rectangle
    xs = {s: c.position_capture_px[0] for s, c in by_sem.items()}
    ys = {s: c.position_capture_px[1] for s, c in by_sem.items()}
    # Estimate L/R/T/B from available semantics
    left_cands = [xs[s] for s in (CornerSemantic.LT, CornerSemantic.LB) if s in xs]
    right_cands = [xs[s] for s in (CornerSemantic.RT, CornerSemantic.RB) if s in xs]
    top_cands = [ys[s] for s in (CornerSemantic.LT, CornerSemantic.RT) if s in ys]
    bot_cands = [ys[s] for s in (CornerSemantic.LB, CornerSemantic.RB) if s in ys]
    if not left_cands or not right_cands or not top_cands or not bot_cands:
        # Fall back: min/max of known
        left, right = min(xs.values()), max(xs.values())
        top, bottom = min(ys.values()), max(ys.values())
    else:
        left, right = float(np_mean(left_cands)), float(np_mean(right_cands))
        top, bottom = float(np_mean(top_cands)), float(np_mean(bot_cands))
    if right - left < 3 or bottom - top < 3:
        return None
    rect = _iround(left, top, right, bottom)
    va = rect.width / max(rect.height, 1)
    if abs(va - target_aspect) / max(target_aspect, 1e-6) > tol * 3:
        return None
    return ViewportHypothesis(
        rect=rect,
        grade=RedFrameEvidenceGrade.CORNERS_3,
        used_semantics=[s.value for s in by_sem],
        score=0.85,
        confidence=0.85,
    )


def _from_two(
    corners: list[DirectedRedCorner],
    target_aspect: float,
    tol: float,
    canvas: IntRect,
) -> list[ViewportHypothesis]:
    out: list[ViewportHypothesis] = []
    for i in range(len(corners)):
        for j in range(i + 1, len(corners)):
            a, b = corners[i], corners[j]
            sa, sb = a.semantic, b.semantic
            ax, ay = a.position_capture_px
            bx, by = b.position_capture_px
            # Diagonal pairs
            diag = {
                frozenset({CornerSemantic.LT, CornerSemantic.RB}),
                frozenset({CornerSemantic.RT, CornerSemantic.LB}),
            }
            pair = frozenset({sa, sb})
            if pair in diag:
                left, right = min(ax, bx), max(ax, bx)
                top, bottom = min(ay, by), max(ay, by)
                rect = _iround(left, top, right, bottom)
                va = rect.width / max(rect.height, 1)
                if abs(va - target_aspect) / max(target_aspect, 1e-6) > tol * 3:
                    continue
                out.append(
                    ViewportHypothesis(
                        rect=rect,
                        grade=RedFrameEvidenceGrade.CORNERS_2_DIAGONAL,
                        used_semantics=[sa.value, sb.value],
                        score=0.75,
                        confidence=0.75,
                    )
                )
                continue
            # Same horizontal edge: LT+RT or LB+RB → known width, derive height
            same_h = {
                frozenset({CornerSemantic.LT, CornerSemantic.RT}),
                frozenset({CornerSemantic.LB, CornerSemantic.RB}),
            }
            same_v = {
                frozenset({CornerSemantic.LT, CornerSemantic.LB}),
                frozenset({CornerSemantic.RT, CornerSemantic.RB}),
            }
            if pair in same_h:
                width = abs(ax - bx)
                if width < 3:
                    continue
                height = width / max(target_aspect, 1e-6)
                top_edge = sa in (CornerSemantic.LT, CornerSemantic.RT)
                y = (ay + by) * 0.5
                left, right = min(ax, bx), max(ax, bx)
                if top_edge:
                    rect = _iround(left, y, right, y + height)
                else:
                    rect = _iround(left, y - height, right, y)
                out.append(
                    ViewportHypothesis(
                        rect=rect,
                        grade=RedFrameEvidenceGrade.CORNERS_2_ADJACENT,
                        used_semantics=[sa.value, sb.value],
                        score=0.65,
                        confidence=0.65,
                    )
                )
            elif pair in same_v:
                height = abs(ay - by)
                if height < 3:
                    continue
                width = height * target_aspect
                left_edge = sa in (CornerSemantic.LT, CornerSemantic.LB)
                x = (ax + bx) * 0.5
                top, bottom = min(ay, by), max(ay, by)
                if left_edge:
                    rect = _iround(x, top, x + width, bottom)
                else:
                    rect = _iround(x - width, top, x, bottom)
                out.append(
                    ViewportHypothesis(
                        rect=rect,
                        grade=RedFrameEvidenceGrade.CORNERS_2_ADJACENT,
                        used_semantics=[sa.value, sb.value],
                        score=0.65,
                        confidence=0.65,
                    )
                )
    return out


def _from_one(
    corners: list[DirectedRedCorner],
    target_aspect: float,
    tol: float,
    canvas: IntRect,
    workspace: IntRect,
) -> list[ViewportHypothesis]:
    """1 corner: size from axis ratios + canvas intersection (§6.5 / §6.6)."""
    out: list[ViewportHypothesis] = []
    Ww, Wh = max(1, workspace.width), max(1, workspace.height)
    # Candidate scale: viewport fraction of navigator canvas using workspace aspect
    # Prefer sizes where intersection with canvas matches partial visibility
    for c in corners:
        if c.horizontal_support < 6 or c.vertical_support < 6:
            continue
        x, y = c.position_capture_px
        # Enumerate plausible widths from intersection with canvas
        for frac in (0.25, 0.35, 0.45, 0.55, 0.65, 0.75, 0.9, 1.0, 1.2):
            Vw = canvas.width * frac
            Vh = Vw / max(target_aspect, 1e-6)
            # Also try tying to workspace pixel scale via navigator canvas as full canvas
            # kx≈ky: Vw/Ww ≈ Vh/Wh already by aspect
            rect = rect_from_corner(c.semantic, x, y, Vw, Vh)
            if not rect.is_valid():
                continue
            inter = _intersection_rect(rect, canvas)
            if inter is None or inter.area < 8:
                continue
            # Visible corner count consistency: at least this corner inside/near canvas
            if not _near_rect(x, y, canvas, pad=4):
                continue
            score = 0.45 + 0.2 * min(1.0, c.confidence)
            # Prefer kx≈ area ratio stability
            kx = rect.width / Ww
            ky = rect.height / Wh
            if abs(kx - ky) / max(kx, ky, 1e-6) > 0.05:
                continue
            out.append(
                ViewportHypothesis(
                    rect=rect,
                    grade=RedFrameEvidenceGrade.CORNERS_1,
                    used_semantics=[c.semantic.value],
                    score=score,
                    confidence=c.confidence * 0.7,
                )
            )
    # Keep top few
    out.sort(key=lambda h: h.score, reverse=True)
    return out[:6]


def np_mean(vals: list[float]) -> float:
    return sum(vals) / max(len(vals), 1)


def _intersection_area(a: IntRect, b: IntRect) -> int:
    r = _intersection_rect(a, b)
    return r.area if r else 0


def _intersection_rect(a: IntRect, b: IntRect) -> Optional[IntRect]:
    l = max(a.left, b.left)
    t = max(a.top, b.top)
    r = min(a.right, b.right)
    btm = min(a.bottom, b.bottom)
    if r <= l or btm <= t:
        return None
    return IntRect(l, t, r, btm)


def _iou(a: IntRect, b: IntRect) -> float:
    inter = _intersection_area(a, b)
    union = a.area + b.area - inter
    return inter / max(union, 1)


def _near_rect(x: float, y: float, r: IntRect, pad: int = 0) -> bool:
    return (r.left - pad) <= x < (r.right + pad) and (r.top - pad) <= y < (r.bottom + pad)
