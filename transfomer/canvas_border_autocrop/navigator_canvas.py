"""Navigator complete-canvas detection via workspace-background rejection (§6.3)."""

from __future__ import annotations

from typing import Any

import cv2
import numpy as np

from .features import delta_e76
from .transform_types import NavigatorCanvasObservation, TransformStatus
from .types import BackgroundAppearanceModel, IntRect


def _bgr_roi_to_lab(bgr: np.ndarray) -> np.ndarray:
    lab_u8 = cv2.cvtColor(bgr, cv2.COLOR_BGR2Lab)
    lab = lab_u8.astype(np.float32)
    lab[..., 0] *= 100.0 / 255.0
    lab[..., 1] -= 128.0
    lab[..., 2] -= 128.0
    return lab


def detect_navigator_canvas(
    bgra_full: np.ndarray,
    navigator_roi: IntRect,
    bg_model: BackgroundAppearanceModel,
) -> tuple[TransformStatus, NavigatorCanvasObservation | None, str]:
    """Reject workspace-background color inside NavigatorRoi; find complete canvas body."""
    if navigator_roi is None or not navigator_roi.is_valid():
        return TransformStatus.NAVIGATOR_ROI_INVALID, None, "navigator ROI invalid"
    if navigator_roi.width < 24 or navigator_roi.height < 24:
        return TransformStatus.NAVIGATOR_ROI_INVALID, None, "navigator ROI too small"

    h, w = bgra_full.shape[:2]
    roi = navigator_roi.clamp(w, h)
    bgr = bgra_full[roi.top : roi.bottom, roi.left : roi.right, :3]
    lab = _bgr_roi_to_lab(bgr)
    de = delta_e76(lab, bg_model.center_lab)
    # Non-background = canvas / chrome content
    non_bg = de > bg_model.weak_delta_e
    bg_rejected = de <= bg_model.weak_delta_e

    # Morphological clean-up: keep solid canvas body
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (3, 3))
    mask_u8 = (non_bg.astype(np.uint8) * 255)
    mask_u8 = cv2.morphologyEx(mask_u8, cv2.MORPH_OPEN, kernel, iterations=1)
    mask_u8 = cv2.morphologyEx(mask_u8, cv2.MORPH_CLOSE, kernel, iterations=2)

    n_labels, labels, stats, _ = cv2.connectedComponentsWithStats(mask_u8, connectivity=8)
    if n_labels <= 1:
        return TransformStatus.NAVIGATOR_CANVAS_NOT_FOUND, None, "no non-background component"

    roi_area = max(1, roi.width * roi.height)
    candidates: list[dict[str, Any]] = []
    for lid in range(1, n_labels):
        x, y, bw, bh, area = stats[lid]
        if area < 0.05 * roi_area:
            continue
        if bw < 12 or bh < 12:
            continue
        # Exclude thin chrome strips (title bar / buttons): aspect extreme + near ROI edge
        aspect = bw / max(bh, 1)
        fill = area / max(bw * bh, 1)
        # Prefer rectangular bodies that dominate ROI interior
        cx = x + bw * 0.5
        cy = y + bh * 0.5
        margin_x = min(cx, roi.width - cx) / max(roi.width, 1)
        margin_y = min(cy, roi.height - cy) / max(roi.height, 1)
        near_border = margin_x < 0.04 or margin_y < 0.04
        # Title-bar-like: very short height relative to ROI
        chrome_like = (bh < 0.12 * roi.height and bw > 0.5 * roi.width) or (
            bw < 0.12 * roi.width and bh > 0.5 * roi.height
        )
        if chrome_like:
            continue
        cover = area / roi_area
        score = 0.45 * cover + 0.30 * fill + 0.15 * (1.0 if 0.25 <= aspect <= 4.0 else 0.2)
        if near_border and cover < 0.35:
            score *= 0.6
        abs_rect = IntRect(
            roi.left + int(x),
            roi.top + int(y),
            roi.left + int(x + bw),
            roi.top + int(y + bh),
        )
        # Must not be the entire user ROI
        if (
            abs_rect.left <= roi.left + 1
            and abs_rect.top <= roi.top + 1
            and abs_rect.right >= roi.right - 1
            and abs_rect.bottom >= roi.bottom - 1
            and cover > 0.92
        ):
            score *= 0.3  # penalize "whole ROI as canvas"
        candidates.append(
            {
                "label": int(lid),
                "rect": abs_rect,
                "area": int(area),
                "cover": float(cover),
                "fill": float(fill),
                "aspect": float(aspect),
                "score": float(score),
            }
        )

    if not candidates:
        return TransformStatus.NAVIGATOR_CANVAS_NOT_FOUND, None, "no valid canvas candidate"

    candidates.sort(key=lambda c: c["score"], reverse=True)
    best = candidates[0]
    if len(candidates) >= 2:
        second = candidates[1]
        # Ambiguous: close scores but geometrically different
        if abs(best["score"] - second["score"]) < 0.08:
            r0, r1 = best["rect"], second["rect"]
            iou = _iou(r0, r1)
            if iou < 0.55:
                obs = NavigatorCanvasObservation(
                    canvas_rect_capture_px=best["rect"],
                    candidates=[{"rect": c["rect"].as_tuple(), "score": c["score"]} for c in candidates[:5]],
                    confidence=0.0,
                )
                return TransformStatus.NAVIGATOR_CANVAS_AMBIGUOUS, obs, "ambiguous navigator canvas"

    rect: IntRect = best["rect"]
    # Build masks in full-frame coordinates (optional small)
    full_mask = np.zeros((h, w), dtype=bool)
    local = labels == best["label"]
    full_mask[roi.top : roi.bottom, roi.left : roi.right] = local
    bg_full = np.zeros((h, w), dtype=bool)
    bg_full[roi.top : roi.bottom, roi.left : roi.right] = bg_rejected

    sides = {
        "left": 0.8,
        "top": 0.8,
        "right": 0.8,
        "bottom": 0.8,
    }
    obs = NavigatorCanvasObservation(
        canvas_rect_capture_px=rect,
        canvas_mask=full_mask,
        background_rejected_mask=bg_full,
        boundary_confidence_by_side=sides,
        aspect_ratio=rect.width / max(rect.height, 1),
        confidence=float(np.clip(best["score"], 0.0, 1.0)),
        candidates=[{"rect": c["rect"].as_tuple(), "score": c["score"]} for c in candidates[:5]],
    )
    return TransformStatus.OK, obs, "ok"


def _iou(a: IntRect, b: IntRect) -> float:
    l = max(a.left, b.left)
    t = max(a.top, b.top)
    r = min(a.right, b.right)
    btm = min(a.bottom, b.bottom)
    if r <= l or btm <= t:
        return 0.0
    inter = (r - l) * (btm - t)
    union = a.area + b.area - inter
    return inter / max(union, 1)
