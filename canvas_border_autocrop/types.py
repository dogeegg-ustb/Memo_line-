"""Core types for canvas border auto-crop detection."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum, IntEnum
from typing import Any, Callable, Optional, Sequence


class DetectionStatus(str, Enum):
    OK = "Ok"
    INVALID_INPUT = "InvalidInput"
    ROI_TOO_SMALL = "RoiTooSmall"
    NO_STABLE_BACKGROUND_MODEL = "NoStableBackgroundModel"
    NO_CONNECTED_BACKGROUND = "NoConnectedBackground"
    INSUFFICIENT_COMPLETE_SIDES = "InsufficientCompleteSides"
    ENDPOINT_TRUNCATED = "EndpointTruncated"
    AMBIGUOUS_CANDIDATES = "AmbiguousCandidates"
    RECTANGLE_CLOSURE_FAILED = "RectangleClosureFailed"
    REFINEMENT_FAILED = "RefinementFailed"
    INDEPENDENT_VALIDATION_FAILED = "IndependentValidationFailed"
    CANCELLED = "Cancelled"


class EvidenceGrade(str, Enum):
    A = "A"
    B = "B"
    C_L = "C_L"
    C_II = "C_II"


class Orientation(str, Enum):
    HORIZONTAL = "Horizontal"
    VERTICAL = "Vertical"


class SemanticSide(str, Enum):
    LEFT = "Left"
    TOP = "Top"
    RIGHT = "Right"
    BOTTOM = "Bottom"


class PixelFormat(str, Enum):
    BGRA = "BGRA"
    RGBA = "RGBA"
    BGR = "BGR"
    RGB = "RGB"


SIDE_INDEX = {
    SemanticSide.LEFT: 0,
    SemanticSide.TOP: 1,
    SemanticSide.RIGHT: 2,
    SemanticSide.BOTTOM: 3,
}


@dataclass(frozen=True, slots=True)
class IntRect:
    """Half-open axis-aligned rectangle [left, right) x [top, bottom)."""

    left: int
    top: int
    right: int
    bottom: int

    @property
    def width(self) -> int:
        return self.right - self.left

    @property
    def height(self) -> int:
        return self.bottom - self.top

    @property
    def area(self) -> int:
        return max(0, self.width) * max(0, self.height)

    def is_valid(self) -> bool:
        return self.left < self.right and self.top < self.bottom

    def clamp(self, width: int, height: int) -> "IntRect":
        l = max(0, min(self.left, width))
        r = max(0, min(self.right, width))
        t = max(0, min(self.top, height))
        b = max(0, min(self.bottom, height))
        return IntRect(l, t, r, b)

    def expand(self, margin: int, width: int, height: int) -> "IntRect":
        return IntRect(
            self.left - margin,
            self.top - margin,
            self.right + margin,
            self.bottom + margin,
        ).clamp(width, height)

    def contains_point(self, x: int, y: int) -> bool:
        return self.left <= x < self.right and self.top <= y < self.bottom

    def contains_rect_center(self, other: "IntRect") -> bool:
        cx = (other.left + other.right) // 2
        cy = (other.top + other.bottom) // 2
        return self.contains_point(cx, cy)

    def intersects_interior(self, other: "IntRect", inset: int = 1) -> bool:
        """True if other overlaps this rect after shrinking other by inset."""
        ol = other.left + inset
        or_ = other.right - inset
        ot = other.top + inset
        ob = other.bottom - inset
        if ol >= or_ or ot >= ob:
            return self.contains_point(
                (other.left + other.right) // 2,
                (other.top + other.bottom) // 2,
            )
        return not (
            or_ <= self.left
            or ol >= self.right
            or ob <= self.top
            or ot >= self.bottom
        )

    def as_tuple(self) -> tuple[int, int, int, int]:
        return self.left, self.top, self.right, self.bottom


CancellationToken = Callable[[], bool]


@dataclass(slots=True)
class DetectionInput:
    capture_buffer: Any  # numpy ndarray HxWxC or flat bytes + dims
    capture_width: int
    capture_height: int
    stride: int
    user_roi_capture_px: IntRect
    dpi_scale_x: float = 1.0
    dpi_scale_y: float = 1.0
    pixel_format: PixelFormat = PixelFormat.BGRA
    cancellation_token: Optional[CancellationToken] = None
    capture_id: str = ""
    capture_origin_screen_physical_x: int = 0
    capture_origin_screen_physical_y: int = 0


@dataclass(slots=True)
class Endpoint:
    x: float
    y: float
    score: float
    is_truncated: bool = False


@dataclass(slots=True)
class CompleteSide:
    orientation: Orientation
    semantic_side: SemanticSide
    fixed_coordinate: float
    start_coordinate: float
    end_coordinate: float
    start_endpoint: Endpoint
    end_endpoint: Endpoint
    coverage: float
    coordinate_mad: float
    transition_score: float
    outside_background_score: float
    endpoint_scores: tuple[float, float]
    is_truncated: bool
    supporting_sample_ids: list[int] = field(default_factory=list)

    @property
    def span(self) -> float:
        return abs(self.end_coordinate - self.start_coordinate)


@dataclass(slots=True)
class BoundaryPoint:
    x: int
    y: int
    direction: SemanticSide
    source_side: int
    similarity_before: float
    similarity_after: float
    directional_gradient: float
    local_variance: float
    weight: float


@dataclass(slots=True)
class SeedPatch:
    seed_id: int
    side: SemanticSide
    x: int
    y: int
    size: int
    mean_lab: tuple[float, float, float]
    accepted: bool
    reject_reason: str = ""


@dataclass(slots=True)
class BackgroundAppearanceModel:
    center_lab: tuple[float, float, float]
    robust_scale: float
    strong_delta_e: float
    weak_delta_e: float
    accepted_seed_ids: list[int]
    spatial_coverage: float
    confidence: float
    cluster_id: int = 0


@dataclass(slots=True)
class RectangleHypothesis:
    rect: IntRect
    grade: EvidenceGrade
    observed_sides: list[SemanticSide]
    inferred_sides: list[SemanticSide]
    score: float
    confidence: float
    side_refs: dict[str, Any] = field(default_factory=dict)
    rejection_reason: str = ""


@dataclass(slots=True)
class Diagnostics:
    search_roi: Optional[IntRect] = None
    background_models_and_scores: list[dict[str, Any]] = field(default_factory=list)
    accepted_and_rejected_seeds: list[dict[str, Any]] = field(default_factory=list)
    side_candidates: list[dict[str, Any]] = field(default_factory=list)
    endpoint_candidates: list[dict[str, Any]] = field(default_factory=list)
    rectangle_hypotheses: list[dict[str, Any]] = field(default_factory=list)
    rejection_reasons: list[str] = field(default_factory=list)
    coarse_rect: Optional[IntRect] = None
    refined_rect: Optional[IntRect] = None
    per_side_validation_metrics: dict[str, Any] = field(default_factory=dict)
    ambiguity_margin: float = 0.0
    timings: dict[str, float] = field(default_factory=dict)
    scale_used: float = 1.0


@dataclass(slots=True)
class DetectionOutput:
    status: DetectionStatus
    workspace_rect_capture_px: Optional[IntRect] = None
    workspace_rect_screen_physical_px: Optional[IntRect] = None
    evidence_grade: Optional[EvidenceGrade] = None
    confidence: float = 0.0
    observed_sides: list[SemanticSide] = field(default_factory=list)
    inferred_sides: list[SemanticSide] = field(default_factory=list)
    source_capture_id: str = ""
    diagnostics: Diagnostics = field(default_factory=Diagnostics)
    message: str = ""

    # Backward-compatible aliases (legacy canvas-content naming)
    @property
    def canvas_content_rect_capture_px(self) -> Optional[IntRect]:
        return self.workspace_rect_capture_px

    @property
    def canvas_content_rect_screen_physical_px(self) -> Optional[IntRect]:
        return self.workspace_rect_screen_physical_px
