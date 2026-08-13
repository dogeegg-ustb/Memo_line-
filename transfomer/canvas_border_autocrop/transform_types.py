"""Named coordinate-space types for screen ↔ canvas transform (architecture §1–4, §6–8)."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Optional

from .types import BackgroundAppearanceModel, CancellationToken, IntRect


class TransformSessionState(str, Enum):
    IDLE = "Idle"
    CAPTURE_REQUESTED = "CaptureRequested"
    FROZEN_FRAME_READY = "FrozenFrameReady"
    SELECTING_WORKSPACE_ROI = "SelectingWorkspaceRoi"
    WORKSPACE_ROI_STORED = "WorkspaceRoiStored"
    SELECTING_NAVIGATOR_ROI = "SelectingNavigatorRoi"
    BOTH_ROIS_READY = "BothRoisReady"
    COMPUTING_WORKSPACE_CORRECTION = "ComputingWorkspaceCorrection"
    COMPUTING_NAVIGATOR_GEOMETRY = "ComputingNavigatorGeometry"
    SOLVING_TRANSFORM = "SolvingTransform"
    VALIDATING_TRANSFORM = "ValidatingTransform"
    VALIDATED = "Validated"
    REJECTED = "Rejected"


class TransformStatus(str, Enum):
    OK = "Ok"
    NOT_USER_TRIGGERED = "NotUserTriggered"
    SESSION_MISMATCH = "SessionMismatch"
    WORKSPACE_DETECTION_FAILED = "WorkspaceDetectionFailed"
    NAVIGATOR_ROI_INVALID = "NavigatorRoiInvalid"
    NAVIGATOR_CANVAS_NOT_FOUND = "NavigatorCanvasNotFound"
    NAVIGATOR_CANVAS_AMBIGUOUS = "NavigatorCanvasAmbiguous"
    RED_FRAME_NOT_FOUND = "RedFrameNotFound"
    RED_FRAME_AMBIGUOUS = "RedFrameAmbiguous"
    INSUFFICIENT_RED_FRAME_GEOMETRY = "InsufficientRedFrameGeometry"
    UNSUPPORTED_ROTATION_OR_FLIP = "UnsupportedRotationOrFlip"
    SCALE_CONSTRAINT_FAILED = "ScaleConstraintFailed"
    MATRIX_SINGULAR = "MatrixSingular"
    MATRIX_ILL_CONDITIONED = "MatrixIllConditioned"
    INDEPENDENT_VALIDATION_FAILED = "IndependentValidationFailed"
    CANCELLED = "Cancelled"


class ValidationStatus(str, Enum):
    VALIDATED = "Validated"
    NAVIGATOR_CONSISTENT = "NavigatorConsistent"
    REJECTED = "Rejected"


class OverlayStatusStyle(str, Enum):
    SUCCESS = "Success"  # Validated — light green
    NAVIGATOR_ONLY = "NavigatorOnly"  # yellow/amber
    FAILURE = "Failure"  # red/orange — MUST NOT use success green fill


class HorizontalRay(str, Enum):
    LEFT = "Left"
    RIGHT = "Right"


class VerticalRay(str, Enum):
    UP = "Up"
    DOWN = "Down"


class CornerSemantic(str, Enum):
    LT = "LT"
    RT = "RT"
    LB = "LB"
    RB = "RB"


class RedFrameEvidenceGrade(str, Enum):
    CORNERS_4 = "Corners4"
    CORNERS_3 = "Corners3"
    CORNERS_2_DIAGONAL = "Corners2Diagonal"
    CORNERS_2_ADJACENT = "Corners2Adjacent"
    CORNERS_1 = "Corners1"


@dataclass(frozen=True, slots=True)
class Affine2D:
    """Row-major 3×3 affine matrix for 2D homogeneous coordinates."""

    m00: float
    m01: float
    m02: float
    m10: float
    m11: float
    m12: float

    def as_rows(self) -> tuple[tuple[float, float, float], tuple[float, float, float], tuple[float, float, float]]:
        return (
            (self.m00, self.m01, self.m02),
            (self.m10, self.m11, self.m12),
            (0.0, 0.0, 1.0),
        )

    def apply(self, x: float, y: float) -> tuple[float, float]:
        return (
            self.m00 * x + self.m01 * y + self.m02,
            self.m10 * x + self.m11 * y + self.m12,
        )

    def matmul(self, other: "Affine2D") -> "Affine2D":
        a, b = self, other
        return Affine2D(
            a.m00 * b.m00 + a.m01 * b.m10,
            a.m00 * b.m01 + a.m01 * b.m11,
            a.m00 * b.m02 + a.m01 * b.m12 + a.m02,
            a.m10 * b.m00 + a.m11 * b.m10,
            a.m10 * b.m01 + a.m11 * b.m11,
            a.m10 * b.m02 + a.m11 * b.m12 + a.m12,
        )

    def det_linear(self) -> float:
        return self.m00 * self.m11 - self.m01 * self.m10

    def invert(self) -> Optional["Affine2D"]:
        det = self.det_linear()
        if abs(det) < 1e-12:
            return None
        inv_det = 1.0 / det
        n00 = self.m11 * inv_det
        n01 = -self.m01 * inv_det
        n10 = -self.m10 * inv_det
        n11 = self.m00 * inv_det
        n02 = -(n00 * self.m02 + n01 * self.m12)
        n12 = -(n10 * self.m02 + n11 * self.m12)
        return Affine2D(n00, n01, n02, n10, n11, n12)

    @staticmethod
    def identity() -> "Affine2D":
        return Affine2D(1.0, 0.0, 0.0, 0.0, 1.0, 0.0)

    @staticmethod
    def translate(tx: float, ty: float) -> "Affine2D":
        return Affine2D(1.0, 0.0, tx, 0.0, 1.0, ty)

    @staticmethod
    def scale(sx: float, sy: float) -> "Affine2D":
        return Affine2D(sx, 0.0, 0.0, 0.0, sy, 0.0)


@dataclass(slots=True)
class ScreenPhysicalToCanvasNormalized:
    matrix: Affine2D
    input_space: str = "ScreenPhysicalPx"
    output_space: str = "CanvasNormalized"


@dataclass(slots=True)
class CanvasNormalizedToScreenPhysical:
    matrix: Affine2D
    input_space: str = "CanvasNormalized"
    output_space: str = "ScreenPhysicalPx"


@dataclass(slots=True)
class WorkspaceLocalToCanvasNormalized:
    matrix: Affine2D
    input_space: str = "WorkspaceLocal"
    output_space: str = "CanvasNormalized"


@dataclass(slots=True)
class CanvasNormalizedToWorkspaceLocal:
    matrix: Affine2D
    input_space: str = "CanvasNormalized"
    output_space: str = "WorkspaceLocal"


@dataclass(slots=True)
class DirectedRedCorner:
    position_capture_px: tuple[float, float]
    horizontal_ray: HorizontalRay
    vertical_ray: VerticalRay
    semantic: CornerSemantic
    horizontal_support: float
    vertical_support: float
    right_angle_error: float
    thickness: float
    color_score: float
    confidence: float


@dataclass(slots=True)
class RedSegment:
    orientation: str  # "Horizontal" | "Vertical"
    x0: float
    y0: float
    x1: float
    y1: float
    thickness: float
    score: float


@dataclass(slots=True)
class RedFrameObservation:
    directed_corners: list[DirectedRedCorner] = field(default_factory=list)
    horizontal_segments: list[RedSegment] = field(default_factory=list)
    vertical_segments: list[RedSegment] = field(default_factory=list)
    pixel_mask: Any = None
    rejected_red_components: list[dict[str, Any]] = field(default_factory=list)
    confidence: float = 0.0


@dataclass(slots=True)
class NavigatorCanvasObservation:
    canvas_rect_capture_px: IntRect
    canvas_mask: Any = None
    background_rejected_mask: Any = None
    boundary_confidence_by_side: dict[str, float] = field(default_factory=dict)
    aspect_ratio: float = 1.0
    confidence: float = 0.0
    candidates: list[dict[str, Any]] = field(default_factory=list)


@dataclass(slots=True)
class WorkspaceObservation:
    workspace_rect_capture_px: IntRect
    workspace_rect_screen_physical_px: IntRect
    background_appearance_model: BackgroundAppearanceModel
    visible_canvas_mask_or_edges: Any = None
    evidence_grade: str = ""
    confidence: float = 0.0
    observed_sides: list[str] = field(default_factory=list)


@dataclass(slots=True)
class OverlayLine:
    x0: float
    y0: float
    x1: float
    y1: float
    color_rgb: tuple[int, int, int] = (80, 220, 120)
    thickness: int = 2
    dashed: bool = False


@dataclass(slots=True)
class OverlayLabel:
    x: float
    y: float
    text: str
    color_rgb: tuple[int, int, int] = (255, 255, 255)


@dataclass(slots=True)
class OverlayCross:
    x: float
    y: float
    size: int = 6
    color_rgb: tuple[int, int, int] = (0, 220, 220)


@dataclass(slots=True)
class OverlayFilledRect:
    rect: IntRect
    fill_rgb: tuple[int, int, int]
    fill_opacity: float = 0.18
    border_rgb: tuple[int, int, int] | None = None
    border_thickness: int = 2


@dataclass(slots=True)
class OverlayScene:
    capture_id: str
    status_style: OverlayStatusStyle
    filled_rects: list[OverlayFilledRect] = field(default_factory=list)
    lines: list[OverlayLine] = field(default_factory=list)
    polylines: list[list[tuple[float, float]]] = field(default_factory=list)
    cross_markers: list[OverlayCross] = field(default_factory=list)
    error_vectors: list[OverlayLine] = field(default_factory=list)
    labels: list[OverlayLabel] = field(default_factory=list)
    lifetime_policy: str = "UntilInvalidated"


@dataclass(slots=True)
class TransformValidationResult:
    status: ValidationStatus
    matrix_finite: bool = False
    is_invertible: bool = False
    determinant: float = 0.0
    condition_number: float = 0.0
    scale_x: float = 0.0
    scale_y: float = 0.0
    scale_relative_error: float = 0.0
    rotation_degrees: float = 0.0
    shear_error: float = 0.0
    axis_directions_valid: bool = False
    predicted_visible_corners: list[str] = field(default_factory=list)
    observed_visible_corners: list[str] = field(default_factory=list)
    red_edge_coverage_by_side: dict[str, float] = field(default_factory=dict)
    navigator_reprojection_median_px: float = 0.0
    navigator_reprojection_p95_px: float = 0.0
    workspace_canvas_edge_median_screen_px: float = 0.0
    workspace_canvas_edge_p95_screen_px: float = 0.0
    independent_evidence_count: int = 0
    confidence: float = 0.0
    failure_reasons: list[str] = field(default_factory=list)


@dataclass(slots=True)
class TransformDiagnostics:
    capture_id: str = ""
    workspace_user_roi: Optional[IntRect] = None
    navigator_user_roi: Optional[IntRect] = None
    corrected_workspace_rect: Optional[IntRect] = None
    workspace_background_model: Optional[BackgroundAppearanceModel] = None
    navigator_canvas_candidates: list[dict[str, Any]] = field(default_factory=list)
    selected_navigator_canvas_rect: Optional[IntRect] = None
    red_pixel_components: list[dict[str, Any]] = field(default_factory=list)
    directed_red_corners: list[dict[str, Any]] = field(default_factory=list)
    rejected_corners_and_reasons: list[dict[str, Any]] = field(default_factory=list)
    red_frame_hypotheses: list[dict[str, Any]] = field(default_factory=list)
    selected_viewport_rect: Optional[IntRect] = None
    scale_constraints: dict[str, float] = field(default_factory=dict)
    matrices: dict[str, Any] = field(default_factory=dict)
    matrix_structure_metrics: dict[str, float] = field(default_factory=dict)
    independent_validation_samples: list[dict[str, Any]] = field(default_factory=list)
    reprojection_errors: dict[str, float] = field(default_factory=dict)
    overlay_scene: Optional[OverlayScene] = None
    rejection_reasons: list[str] = field(default_factory=list)
    timings: dict[str, float] = field(default_factory=dict)


@dataclass(slots=True)
class TransformRequest:
    capture_id: str
    frozen_capture_buffer: Any
    workspace_user_roi_capture_px: IntRect
    navigator_user_roi_capture_px: IntRect
    capture_origin_screen_physical_x: int = 0
    capture_origin_screen_physical_y: int = 0
    workspace_detection_output: Any = None  # DetectionOutput | None
    user_triggered: bool = False
    cancellation_token: Optional[CancellationToken] = None
    session_generation: int = 0


@dataclass(slots=True)
class TransformResult:
    status: TransformStatus
    workspace_rect_screen_physical_px: Optional[IntRect] = None
    navigator_canvas_rect_capture_px: Optional[IntRect] = None
    navigator_viewport_rect_capture_px: Optional[IntRect] = None
    red_frame_evidence_grade: Optional[RedFrameEvidenceGrade] = None
    screen_physical_to_canvas_normalized: Optional[ScreenPhysicalToCanvasNormalized] = None
    canvas_normalized_to_screen_physical: Optional[CanvasNormalizedToScreenPhysical] = None
    workspace_local_to_canvas_normalized: Optional[WorkspaceLocalToCanvasNormalized] = None
    canvas_normalized_to_workspace_local: Optional[CanvasNormalizedToWorkspaceLocal] = None
    validation: Optional[TransformValidationResult] = None
    diagnostics: TransformDiagnostics = field(default_factory=TransformDiagnostics)
    source_capture_id: str = ""
    message: str = ""
    overlay_scene: Optional[OverlayScene] = None


def corner_semantic_from_rays(h: HorizontalRay, v: VerticalRay) -> CornerSemantic:
    """Architecture §6.4: ray directions alone determine LT/RT/LB/RB."""
    if h == HorizontalRay.RIGHT and v == VerticalRay.DOWN:
        return CornerSemantic.LT
    if h == HorizontalRay.LEFT and v == VerticalRay.DOWN:
        return CornerSemantic.RT
    if h == HorizontalRay.RIGHT and v == VerticalRay.UP:
        return CornerSemantic.LB
    return CornerSemantic.RB
