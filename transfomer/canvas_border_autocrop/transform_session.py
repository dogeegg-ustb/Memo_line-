"""Transform capture session: dual ROI, generation, user-triggered compute (§3)."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional

from .session import WorkspaceCaptureSession
from .transform_types import TransformSessionState
from .types import DetectionOutput, IntRect


@dataclass(slots=True)
class TransformCaptureSession:
    """Same frozen frame for workspace + navigator ROIs; matrix invalidates on ROI change."""

    base: WorkspaceCaptureSession
    state: TransformSessionState = TransformSessionState.FROZEN_FRAME_READY
    session_generation: int = 0
    workspace_user_roi: Optional[IntRect] = None
    navigator_user_roi: Optional[IntRect] = None
    workspace_detection: Optional[DetectionOutput] = None
    triggered_at: float = 0.0
    transform_result: Optional[object] = None  # TransformResult

    @property
    def capture_id(self) -> str:
        return self.base.capture_id

    @property
    def active(self) -> bool:
        return self.base.active

    @property
    def width(self) -> int:
        return self.base.width

    @property
    def height(self) -> int:
        return self.base.height

    @property
    def frozen_capture_bgra(self):
        return self.base.frozen_capture_bgra

    @property
    def capture_to_screen(self):
        return self.base.capture_to_screen

    @property
    def monitor_descriptors(self):
        return self.base.monitor_descriptors

    def bump_generation(self) -> None:
        self.session_generation += 1
        self.transform_result = None

    def set_workspace_roi(self, roi: IntRect) -> None:
        self.workspace_user_roi = roi.clamp(self.width, self.height)
        self.navigator_user_roi = None
        self.workspace_detection = None
        self.transform_result = None
        self.bump_generation()
        self.state = TransformSessionState.WORKSPACE_ROI_STORED
        # Keep legacy single-ROI field in sync for overlay helpers
        self.base.set_user_roi(self.workspace_user_roi)

    def set_navigator_roi(self, roi: IntRect) -> None:
        if self.workspace_user_roi is None:
            raise RuntimeError("workspace ROI required before navigator ROI")
        self.navigator_user_roi = roi.clamp(self.width, self.height)
        self.transform_result = None
        self.bump_generation()
        self.state = TransformSessionState.BOTH_ROIS_READY

    def both_rois_ready(self) -> bool:
        return (
            self.active
            and self.workspace_user_roi is not None
            and self.navigator_user_roi is not None
            and self.state
            in (
                TransformSessionState.BOTH_ROIS_READY,
                TransformSessionState.VALIDATED,
                TransformSessionState.REJECTED,
            )
        )

    def invalidate(self) -> None:
        self.base.invalidate()
        self.state = TransformSessionState.IDLE
        self.bump_generation()
        self.workspace_user_roi = None
        self.navigator_user_roi = None
        self.workspace_detection = None
        self.transform_result = None


def wrap_transform_session(base: WorkspaceCaptureSession) -> TransformCaptureSession:
    return TransformCaptureSession(
        base=base,
        state=TransformSessionState.FROZEN_FRAME_READY,
    )
