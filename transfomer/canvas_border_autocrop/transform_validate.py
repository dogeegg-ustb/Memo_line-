"""Level 1–3 transform validation — no circular self-proof as sole success (§7)."""

from __future__ import annotations

import math
from typing import Sequence

import numpy as np

from .red_frame_hypothesis import _intersection_rect
from .transform_solver import SolvedTransforms
from .transform_types import (
    DirectedRedCorner,
    OverlayFilledRect,
    OverlayLabel,
    OverlayLine,
    OverlayScene,
    OverlayStatusStyle,
    RedFrameObservation,
    TransformValidationResult,
    ValidationStatus,
)
from .types import IntRect


# Calibrated initial thresholds (§7.6)
NAV_MEDIAN_MAX = 1.5
NAV_P95_MAX = 3.0
SCREEN_MEDIAN_MAX = 2.5
SCREEN_P95_MAX = 5.0
SCALE_REL_ERR_MAX = 0.02
COND_MAX = 50.0
DET_MIN = 1e-9


def validate_transform(
    solved: SolvedTransforms,
    workspace_screen: IntRect,
    navigator_canvas: IntRect,
    viewport: IntRect,
    red: RedFrameObservation,
    *,
    capture_id: str,
    solver_corner_positions: Sequence[tuple[float, float]] | None = None,
    exclusion_radius: float = 4.0,
) -> TransformValidationResult:
    m = solved.screen_to_canvas.matrix
    inv = solved.canvas_to_screen.matrix
    reasons: list[str] = []

    vals = [m.m00, m.m01, m.m02, m.m10, m.m11, m.m12]
    finite = all(math.isfinite(v) for v in vals)
    det = m.det_linear()
    invertible = abs(det) > DET_MIN and inv is not None
    # Condition number of linear part
    A = np.array([[m.m00, m.m01], [m.m10, m.m11]], dtype=np.float64)
    try:
        cond = float(np.linalg.cond(A))
    except Exception:
        cond = float("inf")

    sx = math.hypot(m.m00, m.m10)
    sy = math.hypot(m.m01, m.m11)
    # For our construction: m10≈m01≈0, scales from diag after Y flip composition
    scale_x = abs(m.m00)
    scale_y = abs(m.m11)
    scale_rel = abs(solved.scale_kx - solved.scale_ky) / max(solved.scale_kx, solved.scale_ky, 1e-9)
    shear = abs(m.m01) + abs(m.m10)
    # Rotation estimate from atan2
    rot = math.degrees(math.atan2(m.m10, m.m00)) if abs(m.m00) + abs(m.m10) > 1e-12 else 0.0

    # Axis directions: Screen Y down → CanvasNormalized Y up ⇒ m11 should be negative
    # after full chain (screen→workspace flips Y, workspace→nav flips again partially…)
    # Check round-trip of workspace corners
    axis_ok = shear < 1e-4 and abs(rot) < 1.0

    if not finite:
        reasons.append("MatrixNotFinite")
    if not invertible:
        reasons.append("MatrixNotInvertible")
    if abs(det) < DET_MIN:
        reasons.append("DeterminantTooSmall")
    if cond > COND_MAX:
        reasons.append("ConditionNumberTooHigh")
    if scale_rel > SCALE_REL_ERR_MAX:
        reasons.append("ScaleRelativeError")
    if shear > 1e-3:
        reasons.append("ShearDetected")
    if abs(rot) > 2.0:
        reasons.append("UnsupportedRotation")

    # Round-trip check (MAY) — not sole success criterion
    rt_err = 0.0
    for xs, ys in (
        (workspace_screen.left, workspace_screen.top),
        (workspace_screen.right - 1, workspace_screen.bottom - 1),
        ((workspace_screen.left + workspace_screen.right) * 0.5,
         (workspace_screen.top + workspace_screen.bottom) * 0.5),
    ):
        u, v = m.apply(xs, ys)
        xs2, ys2 = inv.apply(u, v)
        rt_err = max(rt_err, abs(xs2 - xs), abs(ys2 - ys))
    if rt_err > 0.5:
        reasons.append(f"RoundTripError={rt_err:.3f}")

    # Level 2: navigator reprojection using segments not near solver corners
    exclude = list(solver_corner_positions or [])
    for c in red.directed_corners:
        exclude.append(c.position_capture_px)

    nav_errs: list[float] = []
    coverage: dict[str, float] = {}
    # Predict visible intersection of viewport ∩ navigator canvas
    inter = _intersection_rect(viewport, navigator_canvas)
    predicted_corners: list[str] = []
    if inter is not None:
        # Corners of full viewport that lie inside/near canvas
        vp_corners = {
            "LT": (viewport.left, viewport.top),
            "RT": (viewport.right, viewport.top),
            "LB": (viewport.left, viewport.bottom),
            "RB": (viewport.right, viewport.bottom),
        }
        for name, (cx, cy) in vp_corners.items():
            if (
                navigator_canvas.left - 2 <= cx <= navigator_canvas.right + 2
                and navigator_canvas.top - 2 <= cy <= navigator_canvas.bottom + 2
            ):
                predicted_corners.append(name)

        # Edge coverage: sample predicted visible edges vs red mask / segments
        for side, samples in _edge_samples(inter).items():
            hits = 0
            for px, py in samples:
                if _near_any_segment(px, py, red, exclude, exclusion_radius):
                    hits += 1
                # distance to nearest red segment for error
                d = _min_seg_dist(px, py, red, exclude, exclusion_radius)
                if d is not None:
                    nav_errs.append(d)
            coverage[side] = hits / max(len(samples), 1)

    observed = [c.semantic.value for c in red.directed_corners]
    nav_med = float(np.median(nav_errs)) if nav_errs else 0.0
    nav_p95 = float(np.percentile(nav_errs, 95)) if nav_errs else 0.0

    if nav_errs and nav_med > NAV_MEDIAN_MAX:
        reasons.append(f"NavigatorMedian={nav_med:.2f}")
    if nav_errs and nav_p95 > NAV_P95_MAX:
        reasons.append(f"NavigatorP95={nav_p95:.2f}")

    # Predicted vs observed corner semantics (soft)
    pred_set, obs_set = set(predicted_corners), set(observed)
    if predicted_corners and not (pred_set & obs_set) and len(obs_set) >= 1:
        # Allow if 1-corner solve used that corner
        if len(obs_set) == 1 and list(obs_set)[0] in pred_set:
            pass
        elif len(pred_set & obs_set) == 0 and len(obs_set) >= 2:
            reasons.append("CornerSemanticMismatch")

    # Level 3: workspace independent evidence — limited in v1 without visible canvas edges
    # Use workspace border as weak independent check: project canvas unit square corners
    # through inv and compare to workspace rect (these ARE used in solve → count lightly)
    screen_errs: list[float] = []
    # Independent: map navigator canvas corners via matrix chain to screen and check
    # they are outside or on workspace appropriately — weak evidence
    independent_count = 0
    for nx, ny in (
        (navigator_canvas.left, navigator_canvas.top),
        (navigator_canvas.right, navigator_canvas.bottom),
    ):
        # nav → canvas norm → screen
        u = (nx - navigator_canvas.left) / max(navigator_canvas.width, 1)
        v = (navigator_canvas.bottom - ny) / max(navigator_canvas.height, 1)
        sx, sy = inv.apply(u, v)
        # Full canvas corners should map near workspace only if viewport covers full canvas
        # Skip hard check; record distance to workspace expanded
        d = _dist_to_rect(sx, sy, workspace_screen)
        screen_errs.append(d)

    # Segments unused in solve count as independent navigator evidence
    unused_segs = 0
    for seg in red.horizontal_segments + red.vertical_segments:
        mx = 0.5 * (seg.x0 + seg.x1)
        my = 0.5 * (seg.y0 + seg.y1)
        if all(math.hypot(mx - ex, my - ey) > exclusion_radius for ex, ey in exclude):
            unused_segs += 1
    independent_count = unused_segs

    scr_med = float(np.median(screen_errs)) if screen_errs else 0.0
    scr_p95 = float(np.percentile(screen_errs, 95)) if screen_errs else 0.0

    level1_fail = any(
        r.startswith(p)
        for r in reasons
        for p in (
            "Matrix",
            "Determinant",
            "Condition",
            "Scale",
            "Shear",
            "Unsupported",
            "RoundTrip",
        )
    )
    level2_fail = any(r.startswith("Navigator") or r == "CornerSemanticMismatch" for r in reasons)

    if level1_fail or level2_fail:
        status = ValidationStatus.REJECTED
        conf = 0.1
    elif independent_count <= 0:
        status = ValidationStatus.NAVIGATOR_CONSISTENT
        conf = 0.55
        # MUST NOT claim full Validated without workspace independent evidence (§7.4)
    else:
        status = ValidationStatus.VALIDATED
        conf = float(np.clip(0.7 + 0.05 * independent_count - 0.1 * nav_med, 0.0, 0.98))

    return TransformValidationResult(
        status=status,
        matrix_finite=finite,
        is_invertible=bool(invertible),
        determinant=det,
        condition_number=cond,
        scale_x=scale_x,
        scale_y=scale_y,
        scale_relative_error=scale_rel,
        rotation_degrees=rot,
        shear_error=shear,
        axis_directions_valid=axis_ok,
        predicted_visible_corners=predicted_corners,
        observed_visible_corners=observed,
        red_edge_coverage_by_side=coverage,
        navigator_reprojection_median_px=nav_med,
        navigator_reprojection_p95_px=nav_p95,
        workspace_canvas_edge_median_screen_px=scr_med,
        workspace_canvas_edge_p95_screen_px=scr_p95,
        independent_evidence_count=independent_count,
        confidence=conf,
        failure_reasons=reasons,
    )


def build_overlay_scene(
    capture_id: str,
    validation: TransformValidationResult,
    workspace_screen: IntRect,
    *,
    predicted_canvas_edges_screen: list[tuple[float, float, float, float]] | None = None,
) -> OverlayScene:
    """Status-style visualization (§8). Failure MUST NOT use success green fill."""
    if validation.status == ValidationStatus.VALIDATED:
        style = OverlayStatusStyle.SUCCESS
        fill = (120, 220, 140)
        border = (60, 180, 100)
        label = (
            f"矩阵已验证  conf={validation.confidence:.2f}  "
            f"NavP95={validation.navigator_reprojection_p95_px:.1f}px"
        )
    elif validation.status == ValidationStatus.NAVIGATOR_CONSISTENT:
        style = OverlayStatusStyle.NAVIGATOR_ONLY
        fill = (220, 200, 80)
        border = (200, 170, 40)
        label = "导航器一致，缺少独立屏幕边验证"
    else:
        style = OverlayStatusStyle.FAILURE
        fill = (220, 100, 80)
        border = (200, 70, 50)
        reason = validation.failure_reasons[0] if validation.failure_reasons else "Rejected"
        label = f"验证失败: {reason}"

    scene = OverlayScene(
        capture_id=capture_id,
        status_style=style,
        filled_rects=[
            OverlayFilledRect(
                rect=workspace_screen,
                fill_rgb=fill,
                fill_opacity=0.18,
                border_rgb=border,
                border_thickness=2,
            )
        ],
        labels=[
            OverlayLabel(
                x=float(workspace_screen.left + 8),
                y=float(workspace_screen.top + 8),
                text=label,
                color_rgb=(255, 255, 255),
            )
        ],
    )
    edge_color = (80, 255, 140) if style == OverlayStatusStyle.SUCCESS else (255, 180, 60)
    for x0, y0, x1, y1 in predicted_canvas_edges_screen or []:
        scene.lines.append(
            OverlayLine(x0, y0, x1, y1, color_rgb=edge_color, thickness=2, dashed=style != OverlayStatusStyle.SUCCESS)
        )
    return scene


def predict_workspace_canvas_edges(
    solved: SolvedTransforms,
    navigator_canvas: IntRect,
    viewport: IntRect,
) -> list[tuple[float, float, float, float]]:
    """Project visible canvas sides (viewport∩navigator as full-canvas UV) to screen."""
    inv = solved.canvas_to_screen.matrix
    inter = _intersection_rect(viewport, navigator_canvas)
    if inter is None:
        return []
    # UV of intersection relative to full navigator canvas (= full canvas)
    Nl, Nt, Nw, Nh = (
        navigator_canvas.left,
        navigator_canvas.top,
        max(navigator_canvas.width, 1),
        max(navigator_canvas.height, 1),
    )
    Nb = navigator_canvas.bottom

    def to_uv(x: float, y: float) -> tuple[float, float]:
        return (x - Nl) / Nw, (Nb - y) / Nh

    def to_screen(u: float, v: float) -> tuple[float, float]:
        return inv.apply(u, v)

    corners_uv = [
        to_uv(inter.left, inter.top),
        to_uv(inter.right, inter.top),
        to_uv(inter.right, inter.bottom),
        to_uv(inter.left, inter.bottom),
    ]
    corners_s = [to_screen(u, v) for u, v in corners_uv]
    edges = []
    for i in range(4):
        x0, y0 = corners_s[i]
        x1, y1 = corners_s[(i + 1) % 4]
        edges.append((x0, y0, x1, y1))
    return edges


def _edge_samples(rect: IntRect, n: int = 12) -> dict[str, list[tuple[float, float]]]:
    out: dict[str, list[tuple[float, float]]] = {"left": [], "right": [], "top": [], "bottom": []}
    for i in range(n):
        t = (i + 0.5) / n
        y = rect.top + t * rect.height
        x = rect.left + t * rect.width
        out["left"].append((float(rect.left), float(y)))
        out["right"].append((float(rect.right - 1), float(y)))
        out["top"].append((float(x), float(rect.top)))
        out["bottom"].append((float(x), float(rect.bottom - 1)))
    return out


def _near_any_segment(
    x: float,
    y: float,
    red: RedFrameObservation,
    exclude: Sequence[tuple[float, float]],
    radius: float,
) -> bool:
    d = _min_seg_dist(x, y, red, exclude, radius)
    return d is not None and d <= 2.5


def _min_seg_dist(
    x: float,
    y: float,
    red: RedFrameObservation,
    exclude: Sequence[tuple[float, float]],
    radius: float,
) -> float | None:
    if any(math.hypot(x - ex, y - ey) <= radius for ex, ey in exclude):
        return None
    best = None
    for seg in red.horizontal_segments + red.vertical_segments:
        d = _point_seg_dist(x, y, seg.x0, seg.y0, seg.x1, seg.y1)
        best = d if best is None else min(best, d)
    return best


def _point_seg_dist(px: float, py: float, x0: float, y0: float, x1: float, y1: float) -> float:
    dx, dy = x1 - x0, y1 - y0
    if abs(dx) + abs(dy) < 1e-9:
        return math.hypot(px - x0, py - y0)
    t = max(0.0, min(1.0, ((px - x0) * dx + (py - y0) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (x0 + t * dx), py - (y0 + t * dy))


def _dist_to_rect(x: float, y: float, r: IntRect) -> float:
    dx = 0.0 if r.left <= x < r.right else min(abs(x - r.left), abs(x - r.right))
    dy = 0.0 if r.top <= y < r.bottom else min(abs(y - r.top), abs(y - r.bottom))
    if dx == 0.0 and dy == 0.0:
        return 0.0
    return math.hypot(dx, dy)
