"""Frozen capture session contract for workspace detection + overlay binding."""

from __future__ import annotations

import time
import uuid
from dataclasses import dataclass, field
from typing import Optional, Sequence

import numpy as np

from .types import DetectionOutput, DetectionStatus, IntRect, PixelFormat


@dataclass(slots=True)
class MonitorDescriptor:
    left: int
    top: int
    right: int
    bottom: int
    dpi_scale_x: float = 1.0
    dpi_scale_y: float = 1.0
    name: str = ""


@dataclass(slots=True)
class CaptureToScreenTransform:
    """CapturePx → virtual-desktop physical px: add origin."""

    origin_x: int = 0
    origin_y: int = 0

    def to_screen(self, rect: IntRect) -> IntRect:
        return IntRect(
            rect.left + self.origin_x,
            rect.top + self.origin_y,
            rect.right + self.origin_x,
            rect.bottom + self.origin_y,
        )


@dataclass(slots=True)
class WorkspaceCaptureSession:
    """Frozen screenshot session. Detection MUST use this same buffer."""

    capture_id: str
    captured_at: float
    virtual_screen_bounds_physical_px: IntRect
    monitor_descriptors: list[MonitorDescriptor]
    frozen_capture_bgra: np.ndarray
    user_roi_capture_px: Optional[IntRect] = None
    capture_to_screen: CaptureToScreenTransform = field(default_factory=CaptureToScreenTransform)
    active: bool = True

    @property
    def width(self) -> int:
        return int(self.frozen_capture_bgra.shape[1])

    @property
    def height(self) -> int:
        return int(self.frozen_capture_bgra.shape[0])

    def set_user_roi(self, roi: IntRect) -> None:
        self.user_roi_capture_px = roi.clamp(self.width, self.height)

    def invalidate(self) -> None:
        self.active = False


def begin_capture_session(
    frozen_bgra: np.ndarray,
    *,
    origin_x: int = 0,
    origin_y: int = 0,
    monitors: Sequence[MonitorDescriptor] | None = None,
    capture_id: str | None = None,
) -> WorkspaceCaptureSession:
    h, w = frozen_bgra.shape[:2]
    mons = list(monitors) if monitors else [
        MonitorDescriptor(origin_x, origin_y, origin_x + w, origin_y + h)
    ]
    return WorkspaceCaptureSession(
        capture_id=capture_id or uuid.uuid4().hex,
        captured_at=time.time(),
        virtual_screen_bounds_physical_px=IntRect(origin_x, origin_y, origin_x + w, origin_y + h),
        monitor_descriptors=mons,
        frozen_capture_bgra=np.ascontiguousarray(frozen_bgra),
        capture_to_screen=CaptureToScreenTransform(origin_x, origin_y),
    )


def result_matches_session(out: DetectionOutput, session: WorkspaceCaptureSession) -> bool:
    if not session.active:
        return False
    if out.status != DetectionStatus.OK:
        return False
    if out.source_capture_id and out.source_capture_id != session.capture_id:
        return False
    return True
