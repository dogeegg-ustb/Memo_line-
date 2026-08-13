"""Unit tests for screen↔canvas transform contracts (§12.1)."""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from canvas_border_autocrop.red_frame_hypothesis import rect_from_corner
from canvas_border_autocrop.transform_solver import solve_axis_aligned_transforms
from canvas_border_autocrop.transform_types import (
    Affine2D,
    CornerSemantic,
    HorizontalRay,
    TransformRequest,
    TransformStatus,
    VerticalRay,
    corner_semantic_from_rays,
)
from canvas_border_autocrop.transform_pipeline import run_transform
from canvas_border_autocrop.types import IntRect


def test_corner_ray_semantics():
    assert corner_semantic_from_rays(HorizontalRay.RIGHT, VerticalRay.DOWN) == CornerSemantic.LT
    assert corner_semantic_from_rays(HorizontalRay.LEFT, VerticalRay.DOWN) == CornerSemantic.RT
    assert corner_semantic_from_rays(HorizontalRay.RIGHT, VerticalRay.UP) == CornerSemantic.LB
    assert corner_semantic_from_rays(HorizontalRay.LEFT, VerticalRay.UP) == CornerSemantic.RB


def test_single_corner_completion():
    assert rect_from_corner(CornerSemantic.LT, 10, 20, 100, 50).as_tuple() == (10, 20, 110, 70)
    assert rect_from_corner(CornerSemantic.RT, 110, 20, 100, 50).as_tuple() == (10, 20, 110, 70)
    assert rect_from_corner(CornerSemantic.LB, 10, 70, 100, 50).as_tuple() == (10, 20, 110, 70)
    assert rect_from_corner(CornerSemantic.RB, 110, 70, 100, 50).as_tuple() == (10, 20, 110, 70)


def test_no_axis_swap_portrait_canvas_landscape_workspace():
    # Landscape workspace, portrait navigator canvas — must not swap X/Y
    ws = IntRect(100, 100, 500, 300)  # 400×200
    nav = IntRect(0, 0, 100, 200)  # portrait full canvas
    # Viewport aspect must match workspace 2:1
    vp = IntRect(10, 40, 90, 80)  # 80×40
    st, solved, msg = solve_axis_aligned_transforms(ws, nav, vp)
    assert st == TransformStatus.OK, (st, msg)
    assert solved is not None
    # Horizontal scale uses widths; vertical uses heights
    assert abs(solved.scale_kx - 80 / 400) < 1e-9
    assert abs(solved.scale_ky - 40 / 200) < 1e-9


def test_screen_y_down_canvas_y_up():
    ws = IntRect(0, 0, 200, 100)
    nav = IntRect(0, 0, 100, 50)
    vp = IntRect(0, 0, 100, 50)  # full canvas = full workspace view
    st, solved, _ = solve_axis_aligned_transforms(ws, nav, vp)
    assert st == TransformStatus.OK and solved is not None
    # Workspace bottom-left screen (0, 99) → canvas near (0,0)
    # Screen top-left (0,0) is workspace local y≈100 → near canvas top
    u0, v0 = solved.screen_to_canvas.matrix.apply(0.0, 99.0)
    u1, v1 = solved.screen_to_canvas.matrix.apply(0.0, 0.0)
    assert abs(u0) < 0.05
    assert abs(v0) < 0.05
    assert abs(v1 - 1.0) < 0.05


def test_matrix_roundtrip_and_half_open():
    ws = IntRect(50, 60, 250, 160)
    nav = IntRect(10, 20, 110, 70)
    vp = IntRect(20, 30, 100, 70)
    st, solved, _ = solve_axis_aligned_transforms(ws, nav, vp)
    assert st == TransformStatus.OK and solved is not None
    m = solved.screen_to_canvas.matrix
    inv = solved.canvas_to_screen.matrix
    for x, y in ((50.0, 60.0), (249.0, 159.0), (150.0, 110.0)):
        u, v = m.apply(x, y)
        x2, y2 = inv.apply(u, v)
        assert abs(x2 - x) < 1e-6
        assert abs(y2 - y) < 1e-6


def test_singular_reject():
    ws = IntRect(0, 0, 100, 100)
    nav = IntRect(0, 0, 50, 50)
    vp = IntRect(0, 0, 0, 50)  # zero width
    st, solved, _ = solve_axis_aligned_transforms(ws, nav, vp)
    assert st == TransformStatus.MATRIX_SINGULAR
    assert solved is None


def test_scale_constraint_reject():
    ws = IntRect(0, 0, 200, 100)  # aspect 2
    nav = IntRect(0, 0, 100, 100)
    vp = IntRect(0, 0, 50, 50)  # aspect 1 — conflict
    st, solved, _ = solve_axis_aligned_transforms(ws, nav, vp, max_scale_rel_err=0.02)
    assert st == TransformStatus.SCALE_CONSTRAINT_FAILED
    assert solved is None


def test_negative_virtual_desktop_origin():
    ws = IntRect(-200, 100, 0, 300)
    nav = IntRect(0, 0, 100, 100)
    vp = IntRect(10, 10, 90, 90)
    st, solved, _ = solve_axis_aligned_transforms(
        ws, nav, vp, capture_origin_x=-500, capture_origin_y=0
    )
    assert st == TransformStatus.OK and solved is not None
    # Round-trip still holds in screen space
    m, inv = solved.screen_to_canvas.matrix, solved.canvas_to_screen.matrix
    u, v = m.apply(-100.0, 200.0)
    x2, y2 = inv.apply(u, v)
    assert abs(x2 + 100.0) < 1e-6
    assert abs(y2 - 200.0) < 1e-6


def test_not_user_triggered_hard_reject():
    img = np.zeros((64, 64, 4), dtype=np.uint8)
    out = run_transform(
        TransformRequest(
            capture_id="c1",
            frozen_capture_buffer=img,
            workspace_user_roi_capture_px=IntRect(0, 0, 40, 40),
            navigator_user_roi_capture_px=IntRect(0, 0, 30, 30),
            user_triggered=False,
        )
    )
    assert out.status == TransformStatus.NOT_USER_TRIGGERED


def test_affine_matmul_identity():
    a = Affine2D.scale(2, 3).matmul(Affine2D.translate(1, 4))
    inv = a.invert()
    assert inv is not None
    i = inv.matmul(a)
    assert abs(i.m00 - 1) < 1e-9 and abs(i.m11 - 1) < 1e-9
    assert abs(i.m02) < 1e-9 and abs(i.m12) < 1e-9
