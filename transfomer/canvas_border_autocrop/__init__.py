"""Canvas workspace rectangle detection + screen↔canvas transform."""

from .config import DEFAULT_CONFIG, DetectorConfig
from .detector import CanvasBorderDetector, detect_canvas_rect, detect_workspace_rect
from .capture_screen import capture_virtual_screen_bgra, freeze_desktop_session
from .overlay import WorkspaceOverlayController, WorkspaceOverlayStyle
from .session import WorkspaceCaptureSession, begin_capture_session
from .transform_pipeline import run_transform
from .transform_session import TransformCaptureSession, wrap_transform_session
from .transform_types import (
    Affine2D,
    OverlayScene,
    TransformRequest,
    TransformResult,
    TransformStatus,
    ValidationStatus,
)
from .types import (
    DetectionInput,
    DetectionOutput,
    DetectionStatus,
    EvidenceGrade,
    IntRect,
    PixelFormat,
    SemanticSide,
)

__all__ = [
    "Affine2D",
    "CanvasBorderDetector",
    "DEFAULT_CONFIG",
    "DetectorConfig",
    "DetectionInput",
    "DetectionOutput",
    "DetectionStatus",
    "EvidenceGrade",
    "IntRect",
    "OverlayScene",
    "PixelFormat",
    "SemanticSide",
    "TransformCaptureSession",
    "TransformRequest",
    "TransformResult",
    "TransformStatus",
    "ValidationStatus",
    "WorkspaceCaptureSession",
    "WorkspaceOverlayController",
    "WorkspaceOverlayStyle",
    "begin_capture_session",
    "capture_virtual_screen_bgra",
    "detect_canvas_rect",
    "detect_workspace_rect",
    "freeze_desktop_session",
    "run_transform",
    "wrap_transform_session",
]

__version__ = "0.4.0"
