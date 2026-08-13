"""Axis-aligned ScreenPhysical ↔ CanvasNormalized transform solver (§6.7)."""

from __future__ import annotations

from dataclasses import dataclass

from .transform_types import (
    Affine2D,
    CanvasNormalizedToScreenPhysical,
    CanvasNormalizedToWorkspaceLocal,
    ScreenPhysicalToCanvasNormalized,
    TransformStatus,
    WorkspaceLocalToCanvasNormalized,
)
from .types import IntRect


@dataclass(slots=True)
class SolvedTransforms:
    screen_to_canvas: ScreenPhysicalToCanvasNormalized
    canvas_to_screen: CanvasNormalizedToScreenPhysical
    workspace_to_canvas: WorkspaceLocalToCanvasNormalized
    canvas_to_workspace: CanvasNormalizedToWorkspaceLocal
    screen_to_workspace: Affine2D
    workspace_to_screen: Affine2D
    navigator_to_canvas: Affine2D
    canvas_to_navigator: Affine2D
    scale_kx: float
    scale_ky: float


def solve_axis_aligned_transforms(
    workspace_screen: IntRect,
    navigator_canvas: IntRect,
    viewport: IntRect,
    *,
    capture_origin_x: int = 0,
    capture_origin_y: int = 0,
    max_scale_rel_err: float = 0.05,
) -> tuple[TransformStatus, SolvedTransforms | None, str]:
    """Zero rotation / no flip. Horizontal→horizontal, vertical→vertical only.

    `navigator_canvas` and `viewport` are CapturePx; converted to ScreenPhysicalPx
    via capture origin so all intermediate spaces stay consistent.
    """
    # Lift navigator geometry into ScreenPhysicalPx
    nav_s = IntRect(
        navigator_canvas.left + capture_origin_x,
        navigator_canvas.top + capture_origin_y,
        navigator_canvas.right + capture_origin_x,
        navigator_canvas.bottom + capture_origin_y,
    )
    vp_s = IntRect(
        viewport.left + capture_origin_x,
        viewport.top + capture_origin_y,
        viewport.right + capture_origin_x,
        viewport.bottom + capture_origin_y,
    )

    Ww = float(workspace_screen.width)
    Wh = float(workspace_screen.height)
    Nw = float(nav_s.width)
    Nh = float(nav_s.height)
    Vw = float(vp_s.width)
    Vh = float(vp_s.height)
    if min(Ww, Wh, Nw, Nh, Vw, Vh) < 1.0:
        return TransformStatus.MATRIX_SINGULAR, None, "degenerate rect dimensions"

    Wl = float(workspace_screen.left)
    Wb = float(workspace_screen.bottom)

    Nl = float(nav_s.left)
    Nb = float(nav_s.bottom)

    Vl = float(vp_s.left)
    Vb = float(vp_s.bottom)

    kx = Vw / Ww
    ky = Vh / Wh
    if abs(kx - ky) / max(kx, ky) > max_scale_rel_err:
        return (
            TransformStatus.SCALE_CONSTRAINT_FAILED,
            None,
            f"kx={kx:.5f} ky={ky:.5f} relative error too large",
        )

    # ScreenPhysical → WorkspaceLocal (Y flips: bottom origin)
    # x_w = x_s - W_l
    # y_w = W_b - y_s
    screen_to_workspace = Affine2D(1.0, 0.0, -Wl, 0.0, -1.0, Wb)
    workspace_to_screen = Affine2D(1.0, 0.0, Wl, 0.0, -1.0, Wb)

    # WorkspaceLocal → NavigatorPx (viewport = full workspace)
    # x_n = V_l + (x_w / W_w) * V_w
    # y_n = V_b - (y_w / W_h) * V_h
    workspace_to_nav = Affine2D(Vw / Ww, 0.0, Vl, 0.0, -Vh / Wh, Vb)
    nav_to_workspace = Affine2D(Ww / Vw, 0.0, -Vl * Ww / Vw, 0.0, -Wh / Vh, Vb * Wh / Vh)

    # NavigatorPx → CanvasNormalized
    # u = (x_n - N_l) / N_w
    # v = (N_b - y_n) / N_h
    nav_to_canvas = Affine2D(1.0 / Nw, 0.0, -Nl / Nw, 0.0, -1.0 / Nh, Nb / Nh)
    canvas_to_nav = Affine2D(Nw, 0.0, Nl, 0.0, -Nh, Nb)

    workspace_to_canvas_m = nav_to_canvas.matmul(workspace_to_nav)
    canvas_to_workspace_m = nav_to_workspace.matmul(canvas_to_nav)

    screen_to_canvas_m = workspace_to_canvas_m.matmul(screen_to_workspace)
    inv = screen_to_canvas_m.invert()
    if inv is None:
        return TransformStatus.MATRIX_SINGULAR, None, "screen→canvas not invertible"

    # Structural: off-axis terms should be ~0 for axis-aligned
    if abs(screen_to_canvas_m.m01) > 1e-6 or abs(screen_to_canvas_m.m10) > 1e-6:
        return TransformStatus.UNSUPPORTED_ROTATION_OR_FLIP, None, "non-zero cross terms"

    solved = SolvedTransforms(
        screen_to_canvas=ScreenPhysicalToCanvasNormalized(matrix=screen_to_canvas_m),
        canvas_to_screen=CanvasNormalizedToScreenPhysical(matrix=inv),
        workspace_to_canvas=WorkspaceLocalToCanvasNormalized(matrix=workspace_to_canvas_m),
        canvas_to_workspace=CanvasNormalizedToWorkspaceLocal(matrix=canvas_to_workspace_m),
        screen_to_workspace=screen_to_workspace,
        workspace_to_screen=workspace_to_screen,
        navigator_to_canvas=nav_to_canvas,
        canvas_to_navigator=canvas_to_nav,
        scale_kx=kx,
        scale_ky=ky,
    )
    return TransformStatus.OK, solved, "ok"
