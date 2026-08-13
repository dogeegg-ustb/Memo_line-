"""Canvas workspace rectangle detection from painting-software screenshots."""

from .config import DEFAULT_CONFIG, DetectorConfig
from .detector import CanvasBorderDetector, detect_canvas_rect, detect_workspace_rect
from .overlay import WorkspaceOverlayController, WorkspaceOverlayStyle
from .session import WorkspaceCaptureSession, begin_capture_session
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
    "CanvasBorderDetector",
    "DEFAULT_CONFIG",
    "DetectorConfig",
    "DetectionInput",
    "DetectionOutput",
    "DetectionStatus",
    "EvidenceGrade",
    "IntRect",
    "PixelFormat",
    "SemanticSide",
    "WorkspaceCaptureSession",
    "WorkspaceOverlayController",
    "WorkspaceOverlayStyle",
    "begin_capture_session",
    "detect_canvas_rect",
    "detect_workspace_rect",
]

__version__ = "0.3.0"
