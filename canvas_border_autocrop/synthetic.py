"""Synthetic screenshot generators for contract tests."""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from canvas_border_autocrop.types import IntRect


@dataclass
class SynthCase:
    name: str
    image_bgra: np.ndarray
    user_roi: IntRect
    true_rect: IntRect
    grade_hint: str
    must_fail: bool = False


def _bg(h: int, w: int, color_bgr: tuple[int, int, int]) -> np.ndarray:
    img = np.zeros((h, w, 4), dtype=np.uint8)
    img[..., 0] = color_bgr[0]
    img[..., 1] = color_bgr[1]
    img[..., 2] = color_bgr[2]
    img[..., 3] = 255
    return img


def _fill_rect(img: np.ndarray, rect: IntRect, color_bgr: tuple[int, int, int]) -> None:
    img[rect.top : rect.bottom, rect.left : rect.right, 0] = color_bgr[0]
    img[rect.top : rect.bottom, rect.left : rect.right, 1] = color_bgr[1]
    img[rect.top : rect.bottom, rect.left : rect.right, 2] = color_bgr[2]


def _draw_border(img: np.ndarray, rect: IntRect, color_bgr: tuple[int, int, int], width: int = 2) -> None:
    for t in range(width):
        r = IntRect(rect.left - t, rect.top - t, rect.right + t, rect.bottom + t)
        # top/bottom
        img[r.top, r.left : r.right] = (*color_bgr, 255)
        img[r.bottom - 1, r.left : r.right] = (*color_bgr, 255)
        img[r.top : r.bottom, r.left] = (*color_bgr, 255)
        img[r.top : r.bottom, r.right - 1] = (*color_bgr, 255)


def make_full_workspace(
    name: str = "A_dark",
    bg=(45, 45, 48),
    canvas=(250, 250, 250),
    border=(30, 30, 32),
    border_w: int = 2,
    h: int = 720,
    w: int = 1280,
    canvas_rect: IntRect | None = None,
    occlude: str | None = None,
) -> SynthCase:
    """Full four-edge canvas in workspace background."""
    img = _bg(h, w, bg)
    if canvas_rect is None:
        canvas_rect = IntRect(180, 90, 1100, 620)
    _fill_rect(img, canvas_rect, canvas)
    if border_w > 0:
        # border just outside content
        br = IntRect(
            canvas_rect.left - border_w,
            canvas_rect.top - border_w,
            canvas_rect.right + border_w,
            canvas_rect.bottom + border_w,
        )
        # paint border ring then restore content
        _fill_rect(img, br, border)
        _fill_rect(img, canvas_rect, canvas)

    # Optional occlusion of one side's middle (still keep endpoints for B)
    if occlude == "right_middle":
        y0 = (canvas_rect.top + canvas_rect.bottom) // 2 - 40
        y1 = y0 + 80
        img[y0:y1, canvas_rect.right - 2 : canvas_rect.right + 30] = (*bg, 255)
        # cover right border locally with panel
        img[y0:y1, canvas_rect.right : canvas_rect.right + 40] = (60, 60, 70, 255)

    # User ROI roughly around canvas with background margin
    pad = 40
    user = IntRect(
        max(0, canvas_rect.left - pad),
        max(0, canvas_rect.top - pad),
        min(w, canvas_rect.right + pad),
        min(h, canvas_rect.bottom + pad),
    )
    grade = "B" if occlude else "A"
    return SynthCase(name, img, user, canvas_rect, grade)


def make_l_shape(name: str = "C_L_top_left") -> SynthCase:
    """Only top and left workspace margins visible; right/bottom flush to image (but sides complete via ROI)."""
    h, w = 640, 960
    bg = (40, 42, 45)
    canvas = (245, 245, 248)
    img = _bg(h, w, bg)
    # Canvas occupies lower-right of ROI leaving L of bg
    canvas_rect = IntRect(200, 120, 820, 520)
    _fill_rect(img, canvas_rect, canvas)
    # Cover right and bottom exterior with similar-to-canvas UI so those sides incomplete
    img[:, canvas_rect.right :] = (*canvas, 255)
    img[canvas_rect.bottom :, :] = (*canvas, 255)
    # Keep left and top bg
    img[: canvas_rect.top, :] = (*bg, 255)
    img[:, : canvas_rect.left] = (*bg, 255)
    # restore canvas
    _fill_rect(img, canvas_rect, canvas)

    user = IntRect(120, 40, 900, 600)
    return SynthCase(name, img, user, canvas_rect, "C_L")


def make_ii_horizontal(name: str = "C_II_vertical_sides") -> SynthCase:
    """Left and right complete sides visible; top/bottom occluded by bars."""
    h, w = 720, 1100
    bg = (50, 50, 55)
    canvas = (230, 232, 235)
    img = _bg(h, w, bg)
    canvas_rect = IntRect(160, 100, 940, 600)
    _fill_rect(img, canvas_rect, canvas)
    # Occlude top and bottom edges with thick bars matching canvas-ish
    img[canvas_rect.top - 30 : canvas_rect.top + 25, canvas_rect.left - 10 : canvas_rect.right + 10] = (
        210,
        210,
        215,
        255,
    )
    img[canvas_rect.bottom - 25 : canvas_rect.bottom + 30, canvas_rect.left - 10 : canvas_rect.right + 10] = (
        210,
        210,
        215,
        255,
    )
    # Restore left/right bg corridors
    img[canvas_rect.top : canvas_rect.bottom, : canvas_rect.left] = (*bg, 255)
    img[canvas_rect.top : canvas_rect.bottom, canvas_rect.right :] = (*bg, 255)
    _fill_rect(img, canvas_rect, canvas)

    user = IntRect(80, 50, 1020, 670)
    return SynthCase(name, img, user, canvas_rect, "C_II")


def make_must_fail_single_side() -> SynthCase:
    h, w = 500, 700
    bg = (35, 35, 38)
    canvas = (240, 240, 240)
    img = _bg(h, w, bg)
    canvas_rect = IntRect(100, 80, 600, 420)
    _fill_rect(img, canvas_rect, canvas)
    # Only leave a bit of top bg; other three sides flush / same as canvas exterior
    img[canvas_rect.top :, :] = (*canvas, 255)
    img[: canvas_rect.top, :] = (*bg, 255)
    _fill_rect(img, canvas_rect, canvas)
    user = IntRect(50, 20, 650, 480)
    return SynthCase("fail_single_side", img, user, canvas_rect, "none", must_fail=True)


def make_must_fail_truncated_endpoints() -> SynthCase:
    """Sides visible mid-span but endpoints cut by ROI."""
    h, w = 600, 800
    bg = (42, 42, 46)
    canvas = (250, 250, 250)
    img = _bg(h, w, bg)
    canvas_rect = IntRect(150, 100, 650, 480)
    _fill_rect(img, canvas_rect, canvas)
    # ROI cuts through all four sides mid-edge
    user = IntRect(200, 150, 600, 430)
    return SynthCase("fail_truncated", img, user, canvas_rect, "none", must_fail=True)


def all_synth_cases() -> list[SynthCase]:
    return [
        make_full_workspace("A_dark"),
        make_full_workspace("A_light", bg=(220, 220, 225), canvas=(30, 30, 35), border=(180, 180, 185)),
        make_full_workspace("B_occlude_right", occlude="right_middle"),
        make_l_shape(),
        make_ii_horizontal(),
        make_must_fail_single_side(),
        make_must_fail_truncated_endpoints(),
    ]
