"""Geometry and buffer helpers."""

from __future__ import annotations

from typing import Optional

import numpy as np

from .types import DetectionInput, DetectionStatus, IntRect, PixelFormat


def is_cancelled(inp: DetectionInput) -> bool:
    tok = inp.cancellation_token
    return bool(tok and tok())


def fail_status(status: DetectionStatus) -> DetectionStatus:
    return status


def normalize_user_roi(
    roi: IntRect,
    width: int,
    height: int,
    min_side: int,
) -> tuple[Optional[IntRect], Optional[DetectionStatus]]:
    if width <= 0 or height <= 0:
        return None, DetectionStatus.INVALID_INPUT
    clipped = roi.clamp(width, height)
    if not clipped.is_valid():
        return None, DetectionStatus.INVALID_INPUT
    if clipped.width < min_side or clipped.height < min_side:
        return None, DetectionStatus.ROI_TOO_SMALL
    return clipped, None


def buffer_to_bgra(
    capture_buffer,
    width: int,
    height: int,
    stride: int,
    pixel_format: PixelFormat,
) -> np.ndarray:
    """Return contiguous HxWx4 uint8 BGRA view/copy."""
    if isinstance(capture_buffer, np.ndarray):
        arr = capture_buffer
        if arr.ndim == 3 and arr.shape[0] == height and arr.shape[1] == width:
            if arr.shape[2] == 4:
                out = arr
            elif arr.shape[2] == 3:
                alpha = np.full((height, width, 1), 255, dtype=np.uint8)
                out = np.concatenate([arr, alpha], axis=2)
            else:
                raise ValueError("Unsupported channel count")
            if pixel_format in (PixelFormat.RGBA, PixelFormat.RGB):
                out = out.copy()
                out[..., [0, 2]] = out[..., [2, 0]]
            elif not out.flags["C_CONTIGUOUS"]:
                out = np.ascontiguousarray(out)
            if out.dtype != np.uint8:
                out = out.astype(np.uint8, copy=False)
            if out.shape[2] == 3:
                alpha = np.full((height, width, 1), 255, dtype=np.uint8)
                out = np.concatenate([out, alpha], axis=2)
            return out
        if arr.ndim == 1 or (arr.ndim == 2 and arr.shape[0] == height):
            capture_buffer = arr.tobytes() if hasattr(arr, "tobytes") else bytes(arr)

    raw = np.frombuffer(capture_buffer, dtype=np.uint8)
    row_bytes = stride if stride > 0 else width * 4
    expected = row_bytes * height
    if raw.size < expected:
        raise ValueError("Capture buffer too small for stride/height")
    if row_bytes == width * 4:
        img = raw[:expected].reshape(height, width, 4).copy()
    else:
        img = np.empty((height, width, 4), dtype=np.uint8)
        for y in range(height):
            start = y * row_bytes
            row = raw[start : start + width * 4]
            if row.size < width * 4:
                # odd stride with 3-channel packed is uncommon; pad
                tmp = np.zeros(width * 4, dtype=np.uint8)
                tmp[: row.size] = row
                row = tmp
            img[y] = row.reshape(width, 4)

    if pixel_format in (PixelFormat.RGBA, PixelFormat.RGB):
        img = img.copy()
        img[..., [0, 2]] = img[..., [2, 0]]
    return img


def bgra_to_bgr(bgra: np.ndarray) -> np.ndarray:
    return np.ascontiguousarray(bgra[..., :3])


def scale_rect(rect: IntRect, scale: float, width: int, height: int) -> IntRect:
    if scale == 1.0:
        return rect.clamp(width, height)
    inv = 1.0  # already in target space when scaling down source coords
    l = int(math_floor(rect.left * scale))
    t = int(math_floor(rect.top * scale))
    r = int(math_ceil(rect.right * scale))
    b = int(math_ceil(rect.bottom * scale))
    return IntRect(l, t, r, b).clamp(width, height)


def unscale_coord(v: float, scale: float) -> float:
    if scale == 1.0:
        return v
    return v / scale


def unscale_rect(rect: IntRect, scale: float, width: int, height: int) -> IntRect:
    if scale == 1.0:
        return rect.clamp(width, height)
    l = int(round(rect.left / scale))
    t = int(round(rect.top / scale))
    r = int(round(rect.right / scale))
    b = int(round(rect.bottom / scale))
    return IntRect(l, t, r, b).clamp(width, height)


def math_floor(x: float) -> int:
    import math

    return int(math.floor(x))


def math_ceil(x: float) -> int:
    import math

    return int(math.ceil(x))


def rect_iou(a: IntRect, b: IntRect) -> float:
    xl = max(a.left, b.left)
    xr = min(a.right, b.right)
    yt = max(a.top, b.top)
    yb = min(a.bottom, b.bottom)
    inter = max(0, xr - xl) * max(0, yb - yt)
    if inter <= 0:
        return 0.0
    union = a.area + b.area - inter
    return inter / union if union > 0 else 0.0


def median_mad(values: np.ndarray) -> tuple[float, float]:
    if values.size == 0:
        return 0.0, 0.0
    med = float(np.median(values))
    mad = float(np.median(np.abs(values - med)))
    return med, mad * 1.4826


def robust_mean(values: np.ndarray, z: float = 2.5) -> float:
    if values.size == 0:
        return 0.0
    med, scale = median_mad(values)
    if scale < 1e-6:
        return med
    mask = np.abs(values - med) <= z * scale
    if not np.any(mask):
        return med
    return float(np.mean(values[mask]))
