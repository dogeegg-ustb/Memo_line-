from __future__ import annotations

from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from typing import Any, Literal


SCHEMA_VERSION = 1
RESULT_SCHEMA_VERSION = 3
RoiRole = Literal["tool_list", "tool_properties"]


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


@dataclass
class Rect:
    x: int
    y: int
    width: int
    height: int

    def to_list(self) -> list[int]:
        return [self.x, self.y, self.width, self.height]

    @classmethod
    def from_any(cls, value: Any) -> "Rect":
        if isinstance(value, dict):
            return cls(int(value["x"]), int(value["y"]), int(value["width"]), int(value["height"]))
        if len(value) != 4:
            raise ValueError("rect must contain x, y, width and height")
        return cls(*(int(v) for v in value))

    def contains_size(self, image_width: int, image_height: int) -> bool:
        return self.x >= 0 and self.y >= 0 and self.width > 0 and self.height > 0 and self.x + self.width <= image_width and self.y + self.height <= image_height


@dataclass
class RoiDefinition:
    id: str
    name: str
    role: RoiRole
    rect: Rect

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "RoiDefinition":
        return cls(str(value["id"]), str(value.get("name", value["id"])), str(value["role"]), Rect.from_any(value["rect"]))

    def to_dict(self) -> dict[str, Any]:
        return {"id": self.id, "name": self.name, "role": self.role, "rect": self.rect.to_list()}


@dataclass
class MonitorProfile:
    screen_id: str
    name: str
    geometry: Rect
    capture_size: tuple[int, int]
    dpi: float
    device_pixel_ratio: float
    output_index: int = 0

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "MonitorProfile":
        return cls(
            screen_id=str(value.get("screen_id", value.get("name", "screen-0"))),
            name=str(value.get("name", "display")),
            geometry=Rect.from_any(value.get("geometry", [0, 0, value["capture_size"][0], value["capture_size"][1]])),
            capture_size=(int(value["capture_size"][0]), int(value["capture_size"][1])),
            dpi=float(value.get("dpi", 96.0)),
            device_pixel_ratio=float(value.get("device_pixel_ratio", 1.0)),
            output_index=int(value.get("output_index", 0)),
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "screen_id": self.screen_id,
            "name": self.name,
            "geometry": self.geometry.to_list(),
            "capture_size": list(self.capture_size),
            "dpi": self.dpi,
            "device_pixel_ratio": self.device_pixel_ratio,
            "output_index": self.output_index,
        }


@dataclass
class TargetWindowProfile:
    """Persistent window identity; hwnd is deliberately not stored."""

    exe_name: str
    class_name: str
    title: str
    title_normalized: str
    capture_scope: Literal["client"] = "client"
    capture_size: tuple[int, int] = (0, 0)
    client_size: tuple[int, int] = (0, 0)
    dpi: float = 96.0
    device_pixel_ratio: float = 1.0
    initial_client_rect: Rect = field(default_factory=lambda: Rect(0, 0, 0, 0))

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "TargetWindowProfile":
        return cls(
            exe_name=str(value.get("exe_name", "")),
            class_name=str(value.get("class_name", "")),
            title=str(value.get("title", "")),
            title_normalized=str(value.get("title_normalized", value.get("title", "")).strip().lower()),
            capture_scope=str(value.get("capture_scope", "client")),
            capture_size=(int(value["capture_size"][0]), int(value["capture_size"][1])),
            client_size=(int(value.get("client_size", value["capture_size"])[0]), int(value.get("client_size", value["capture_size"])[1])),
            dpi=float(value.get("dpi", 96.0)),
            device_pixel_ratio=float(value.get("device_pixel_ratio", 1.0)),
            initial_client_rect=Rect.from_any(value.get("initial_client_rect", [0, 0, value["capture_size"][0], value["capture_size"][1]])),
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "exe_name": self.exe_name,
            "class_name": self.class_name,
            "title": self.title,
            "title_normalized": self.title_normalized,
            "capture_scope": self.capture_scope,
            "capture_size": list(self.capture_size),
            "client_size": list(self.client_size),
            "dpi": self.dpi,
            "device_pixel_ratio": self.device_pixel_ratio,
            "initial_client_rect": self.initial_client_rect.to_list(),
        }


@dataclass
class OcrConfig:
    engine: str = "rapidocr"
    language: str = "ch"
    model: str = "default"
    use_gpu: bool = False

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class SamplingConfig:
    interval_ms: int = 500
    change_threshold: float = 3.0
    coalesce_ms: int = 150
    max_wait_ms: int = 800
    force_full_recognition: bool = True

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class Profile:
    name: str
    monitor: MonitorProfile
    rois: list[RoiDefinition]
    ocr: OcrConfig = field(default_factory=OcrConfig)
    sampling: SamplingConfig = field(default_factory=SamplingConfig)
    schema_version: int = SCHEMA_VERSION
    profile_kind: str = "user"
    target_window: TargetWindowProfile | None = None

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "Profile":
        return cls(
            name=str(value.get("name", "CSP 面板配置")),
            monitor=MonitorProfile.from_dict(value["monitor"]),
            rois=[RoiDefinition.from_dict(item) for item in value.get("rois", [])],
            ocr=OcrConfig(**{k: value.get("ocr", {}).get(k, v) for k, v in OcrConfig().__dict__.items()}),
            sampling=SamplingConfig(**{k: value.get("sampling", {}).get(k, v) for k, v in SamplingConfig().__dict__.items()}),
            schema_version=int(value.get("schema_version", 0)),
            profile_kind=str(value.get("profile_kind", "legacy")),
            target_window=TargetWindowProfile.from_dict(value["target_window"]) if value.get("target_window") else None,
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "schema_version": self.schema_version,
            "name": self.name,
            "monitor": self.monitor.to_dict(),
            "rois": [roi.to_dict() for roi in self.rois],
            "ocr": self.ocr.to_dict(),
            "sampling": self.sampling.to_dict(),
            "profile_kind": self.profile_kind,
            "target_window": self.target_window.to_dict() if self.target_window else None,
        }

    def property_rois(self) -> list[RoiDefinition]:
        return [roi for roi in self.rois if roi.role == "tool_properties"]

    def capture_size(self) -> tuple[int, int]:
        return self.target_window.capture_size if self.target_window else self.monitor.capture_size


@dataclass
class OcrText:
    text: str
    score: float | None
    box: list[list[float]]

    def bbox(self) -> list[int]:
        xs = [float(point[0]) for point in self.box]
        ys = [float(point[1]) for point in self.box]
        if not xs or not ys:
            return [0, 0, 0, 0]
        left, top, right, bottom = min(xs), min(ys), max(xs), max(ys)
        return [round(left), round(top), max(1, round(right - left)), max(1, round(bottom - top))]


@dataclass
class VisualState:
    selected: Literal["selected", "not_selected", "unknown"] = "unknown"
    selected_score: float | None = None
    checkbox: Literal["checked", "unchecked", "unknown", "not_checkbox"] = "not_checkbox"
    enabled: Literal["enabled", "disabled", "unknown"] = "unknown"
    evidence: list[dict[str, Any]] = field(default_factory=list)


@dataclass
class RoiRecognition:
    roi_id: str
    role: RoiRole
    ocr: list[OcrText]
    visual: list[VisualState]
    parameters: list[dict[str, Any]] = field(default_factory=list)
    brush_candidates: list[dict[str, Any]] = field(default_factory=list)
    unresolved: list[dict[str, Any]] = field(default_factory=list)


@dataclass
class CaptureFrame:
    frame_id: int
    captured_at: str
    image: Any
    capture_backend: str = "unknown"
    target_hwnd: int | None = None
    target_window_state: str = "visible"


@dataclass
class RecognitionOutcome:
    result: dict[str, Any]
    roi_images: dict[str, Any] = field(default_factory=dict)
    overlays: dict[str, Any] = field(default_factory=dict)
