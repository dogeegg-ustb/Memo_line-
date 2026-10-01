from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Literal


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
