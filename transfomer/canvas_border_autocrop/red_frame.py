"""Navigator red-frame detection: chroma + thin HV lines + directed 90° corners (§6.4)."""

from __future__ import annotations

from typing import Any

import cv2
import numpy as np

from .transform_types import (
    CornerSemantic,
    DirectedRedCorner,
    HorizontalRay,
    RedFrameObservation,
    RedSegment,
    TransformStatus,
    VerticalRay,
    corner_semantic_from_rays,
)
from .types import IntRect


def detect_red_frame(
    bgra_full: np.ndarray,
    navigator_roi: IntRect,
    canvas_rect: IntRect,
) -> tuple[TransformStatus, RedFrameObservation | None, str]:
    h, w = bgra_full.shape[:2]
    roi = navigator_roi.clamp(w, h)
    bgr = bgra_full[roi.top : roi.bottom, roi.left : roi.right, :3].copy()
    # Work relative to ROI; map back to capture px at the end
    red_mask = _red_chroma_mask(bgr)
    # Restrict to near canvas (viewport lives on/near canvas)
    local_canvas = IntRect(
        max(0, canvas_rect.left - roi.left - 4),
        max(0, canvas_rect.top - roi.top - 4),
        min(roi.width, canvas_rect.right - roi.left + 4),
        min(roi.height, canvas_rect.bottom - roi.top + 4),
    )
    clip = np.zeros_like(red_mask)
    if local_canvas.is_valid():
        clip[
            local_canvas.top : local_canvas.bottom,
            local_canvas.left : local_canvas.right,
        ] = True
        red_mask = red_mask & clip

    if int(red_mask.sum()) < 20:
        return TransformStatus.RED_FRAME_NOT_FOUND, None, "insufficient red pixels"

    # Thin HV structure via morphological opening with line kernels
    h_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (9, 1))
    v_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (1, 9))
    h_lines = cv2.morphologyEx(red_mask.astype(np.uint8) * 255, cv2.MORPH_OPEN, h_kernel)
    v_lines = cv2.morphologyEx(red_mask.astype(np.uint8) * 255, cv2.MORPH_OPEN, v_kernel)
    struct = ((h_lines > 0) | (v_lines > 0))

    horiz_segs = _extract_segments(h_lines > 0, horizontal=True, ox=roi.left, oy=roi.top)
    vert_segs = _extract_segments(v_lines > 0, horizontal=False, ox=roi.left, oy=roi.top)

    corners, rejected = _find_directed_corners(
        struct, h_lines > 0, v_lines > 0, ox=roi.left, oy=roi.top
    )
    if not corners:
        obs = RedFrameObservation(
            horizontal_segments=horiz_segs,
            vertical_segments=vert_segs,
            pixel_mask=struct,
            rejected_red_components=rejected,
            confidence=0.0,
        )
        return TransformStatus.INSUFFICIENT_RED_FRAME_GEOMETRY, obs, "no directed 90° corners"

    # Deduplicate by semantic (keep highest confidence)
    best_by_sem: dict[CornerSemantic, DirectedRedCorner] = {}
    for c in corners:
        prev = best_by_sem.get(c.semantic)
        if prev is None or c.confidence > prev.confidence:
            best_by_sem[c.semantic] = c
    uniq = list(best_by_sem.values())
    conf = float(np.mean([c.confidence for c in uniq])) if uniq else 0.0
    obs = RedFrameObservation(
        directed_corners=uniq,
        horizontal_segments=horiz_segs,
        vertical_segments=vert_segs,
        pixel_mask=struct,
        rejected_red_components=rejected,
        confidence=conf,
    )
    return TransformStatus.OK, obs, "ok"


def _red_chroma_mask(bgr: np.ndarray) -> np.ndarray:
    """High-chroma red appearance — not mere RGB threshold."""
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    h, s, v = hsv[..., 0], hsv[..., 1], hsv[..., 2]
    # OpenCV H: 0–179; red wraps around 0
    red_hue = (h <= 10) | (h >= 170)
    mask = red_hue & (s >= 80) & (v >= 60)
    # Also accept strong R channel dominance
    b, g, r = bgr[..., 0].astype(np.int16), bgr[..., 1].astype(np.int16), bgr[..., 2].astype(np.int16)
    dominant = (r > g + 40) & (r > b + 40) & (r >= 90)
    return mask | dominant


def _extract_segments(
    mask: np.ndarray,
    *,
    horizontal: bool,
    ox: int,
    oy: int,
    min_len: int = 8,
) -> list[RedSegment]:
    segs: list[RedSegment] = []
    u8 = mask.astype(np.uint8)
    n, labels, stats, _ = cv2.connectedComponentsWithStats(u8, connectivity=8)
    for lid in range(1, n):
        x, y, bw, bh, area = stats[lid]
        if area < min_len:
            continue
        if horizontal:
            if bw < min_len or bh > max(6, bw // 3):
                continue
            segs.append(
                RedSegment(
                    orientation="Horizontal",
                    x0=float(ox + x),
                    y0=float(oy + y + bh * 0.5),
                    x1=float(ox + x + bw),
                    y1=float(oy + y + bh * 0.5),
                    thickness=float(bh),
                    score=float(min(1.0, bw / 40.0)),
                )
            )
        else:
            if bh < min_len or bw > max(6, bh // 3):
                continue
            segs.append(
                RedSegment(
                    orientation="Vertical",
                    x0=float(ox + x + bw * 0.5),
                    y0=float(oy + y),
                    x1=float(ox + x + bw * 0.5),
                    y1=float(oy + y + bh),
                    thickness=float(bw),
                    score=float(min(1.0, bh / 40.0)),
                )
            )
    return segs


def _find_directed_corners(
    struct: np.ndarray,
    h_mask: np.ndarray,
    v_mask: np.ndarray,
    *,
    ox: int,
    oy: int,
    arm_min: int = 6,
) -> tuple[list[DirectedRedCorner], list[dict[str, Any]]]:
    """Locate L-junctions; arm directions define semantic (MUST NOT guess by ROI side)."""
    ys, xs = np.where(struct)
    if len(xs) == 0:
        return [], []

    # Candidate junction: nearby both H and V support
    corners: list[DirectedRedCorner] = []
    rejected: list[dict[str, Any]] = []
    step = max(1, len(xs) // 800)
    seen: set[tuple[int, int]] = set()

    for i in range(0, len(xs), step):
        x, y = int(xs[i]), int(ys[i])
        key = (x // 3, y // 3)
        if key in seen:
            continue
        # Need both orientations in local neighborhood
        y0, y1 = max(0, y - 2), min(struct.shape[0], y + 3)
        x0, x1 = max(0, x - 2), min(struct.shape[1], x + 3)
        if not (h_mask[y0:y1, x0:x1].any() and v_mask[y0:y1, x0:x1].any()):
            continue

        right = _ray_support(struct, x, y, dx=1, dy=0, max_len=40)
        left = _ray_support(struct, x, y, dx=-1, dy=0, max_len=40)
        down = _ray_support(struct, x, y, dx=0, dy=1, max_len=40)
        up = _ray_support(struct, x, y, dx=0, dy=-1, max_len=40)

        # Pick dominant horizontal and vertical arms
        h_dir: HorizontalRay | None = None
        h_sup = 0.0
        if right >= arm_min and right >= left:
            h_dir, h_sup = HorizontalRay.RIGHT, float(right)
        elif left >= arm_min:
            h_dir, h_sup = HorizontalRay.LEFT, float(left)

        v_dir: VerticalRay | None = None
        v_sup = 0.0
        if down >= arm_min and down >= up:
            v_dir, v_sup = VerticalRay.DOWN, float(down)
        elif up >= arm_min:
            v_dir, v_sup = VerticalRay.UP, float(up)

        if h_dir is None or v_dir is None:
            if max(right, left, down, up) >= arm_min:
                rejected.append(
                    {
                        "xy": (ox + x, oy + y),
                        "reason": "missing_orthogonal_arm",
                        "supports": (right, left, down, up),
                    }
                )
            continue

        # Reject if opposite arms also strong (cross / T, not clean L)
        opp_h = left if h_dir == HorizontalRay.RIGHT else right
        opp_v = up if v_dir == VerticalRay.DOWN else down
        if opp_h > 0.55 * h_sup or opp_v > 0.55 * v_sup:
            rejected.append(
                {
                    "xy": (ox + x, oy + y),
                    "reason": "not_L_junction",
                    "supports": (right, left, down, up),
                }
            )
            continue

        seen.add(key)
        semantic = corner_semantic_from_rays(h_dir, v_dir)
        angle_err = 0.0  # axis-aligned masks → approximate 0
        conf = float(
            np.clip(
                0.4 * min(1.0, h_sup / 20.0) + 0.4 * min(1.0, v_sup / 20.0) + 0.2,
                0.0,
                1.0,
            )
        )
        corners.append(
            DirectedRedCorner(
                position_capture_px=(float(ox + x), float(oy + y)),
                horizontal_ray=h_dir,
                vertical_ray=v_dir,
                semantic=semantic,
                horizontal_support=h_sup,
                vertical_support=v_sup,
                right_angle_error=angle_err,
                thickness=2.0,
                color_score=0.85,
                confidence=conf,
            )
        )

    return corners, rejected


def _ray_support(mask: np.ndarray, x: int, y: int, dx: int, dy: int, max_len: int) -> int:
    h, w = mask.shape
    n = 0
    cx, cy = x + dx, y + dy
    gap = 0
    while 0 <= cx < w and 0 <= cy < h and n < max_len:
        if mask[cy, cx]:
            n += 1
            gap = 0
        else:
            gap += 1
            if gap > 1:
                break
        cx += dx
        cy += dy
    return n
