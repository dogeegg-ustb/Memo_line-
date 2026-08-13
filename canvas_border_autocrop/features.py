"""CIE Lab feature extraction with gradients and local variance."""

from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np

from .config import DetectorConfig
from .types import IntRect


@dataclass(slots=True)
class CanvasFeatureMaps:
    lab: np.ndarray  # HxWx3 float32
    gray: np.ndarray  # HxW float32
    gradient_x: np.ndarray
    gradient_y: np.ndarray
    gradient_magnitude: np.ndarray
    local_variance: np.ndarray
    width: int
    height: int
    scale_to_capture: float
    full_lab: np.ndarray | None = None
    full_gray: np.ndarray | None = None
    full_grad_mag: np.ndarray | None = None
    full_local_var: np.ndarray | None = None
    full_width: int = 0
    full_height: int = 0


def _maps_from_bgr(work: np.ndarray, cfg: DetectorConfig) -> tuple[np.ndarray, ...]:
    lab_u8 = cv2.cvtColor(work, cv2.COLOR_BGR2Lab)
    lab = lab_u8.astype(np.float32)
    lab[..., 0] *= 100.0 / 255.0
    lab[..., 1] -= 128.0
    lab[..., 2] -= 128.0
    gray = cv2.cvtColor(work, cv2.COLOR_BGR2GRAY).astype(np.float32)
    gx = cv2.Scharr(gray, cv2.CV_32F, 1, 0)
    gy = cv2.Scharr(gray, cv2.CV_32F, 0, 1)
    gmag = cv2.magnitude(gx, gy)
    win = cfg.variance_window | 1
    mean = cv2.boxFilter(gray, cv2.CV_32F, (win, win), normalize=True)
    mean2 = cv2.boxFilter(gray * gray, cv2.CV_32F, (win, win), normalize=True)
    local_var = np.maximum(mean2 - mean * mean, 0.0)
    return lab, gray, gx, gy, gmag, local_var


def extract_features(
    bgr: np.ndarray,
    cfg: DetectorConfig,
    scale_to_capture: float = 1.0,
    roi: IntRect | None = None,
) -> CanvasFeatureMaps:
    """Extract Lab/gray/gradients/variance.

    If roi is set, computes only inside an expanded ROI and embeds into full-size maps.
    """
    h, w = bgr.shape[:2]
    k = 3 if cfg.blur_radius_cap_px >= 1 else 1

    if roi is not None:
        pad = max(16, cfg.refine_radius_max_px + 4)
        x0 = max(0, roi.left - pad)
        y0 = max(0, roi.top - pad)
        x1 = min(w, roi.right + pad)
        y1 = min(h, roi.bottom + pad)
        crop = bgr[y0:y1, x0:x1]
        if k >= 3:
            crop = cv2.GaussianBlur(crop, (k, k), 0.6)
        lab_c, gray_c, gx_c, gy_c, gmag_c, var_c = _maps_from_bgr(crop, cfg)
        lab = np.zeros((h, w, 3), dtype=np.float32)
        gray = np.zeros((h, w), dtype=np.float32)
        gx = np.zeros((h, w), dtype=np.float32)
        gy = np.zeros((h, w), dtype=np.float32)
        gmag = np.zeros((h, w), dtype=np.float32)
        local_var = np.zeros((h, w), dtype=np.float32)
        lab[y0:y1, x0:x1] = lab_c
        gray[y0:y1, x0:x1] = gray_c
        gx[y0:y1, x0:x1] = gx_c
        gy[y0:y1, x0:x1] = gy_c
        gmag[y0:y1, x0:x1] = gmag_c
        local_var[y0:y1, x0:x1] = var_c
        return CanvasFeatureMaps(
            lab=lab,
            gray=gray,
            gradient_x=gx,
            gradient_y=gy,
            gradient_magnitude=gmag,
            local_variance=local_var,
            width=w,
            height=h,
            scale_to_capture=scale_to_capture,
        )

    work = cv2.GaussianBlur(bgr, (k, k), 0.6) if k >= 3 else bgr
    lab, gray, gx, gy, gmag, local_var = _maps_from_bgr(work, cfg)
    return CanvasFeatureMaps(
        lab=lab,
        gray=gray,
        gradient_x=gx,
        gradient_y=gy,
        gradient_magnitude=gmag,
        local_variance=local_var,
        width=w,
        height=h,
        scale_to_capture=scale_to_capture,
    )


def downsample_bgr(bgr: np.ndarray, scale: float) -> np.ndarray:
    if scale >= 0.999:
        return bgr
    h, w = bgr.shape[:2]
    nw = max(1, int(round(w * scale)))
    nh = max(1, int(round(h * scale)))
    return cv2.resize(bgr, (nw, nh), interpolation=cv2.INTER_AREA)


def delta_e76(lab: np.ndarray, center: tuple[float, float, float]) -> np.ndarray:
    d0 = lab[..., 0] - center[0]
    d1 = lab[..., 1] - center[1]
    d2 = lab[..., 2] - center[2]
    return np.sqrt(d0 * d0 + d1 * d1 + d2 * d2, dtype=np.float32)


def delta_e76_pixels(labs: np.ndarray, center: tuple[float, float, float]) -> np.ndarray:
    d = labs - np.asarray(center, dtype=np.float32)
    return np.sqrt(np.sum(d * d, axis=1), dtype=np.float32)
