from __future__ import annotations

from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from typing import Any


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


@dataclass
class LayerState:
    """One layer's observable state; IDs remain source-provided when available."""

    layer_id: str
    name: str
    layer_type: str = "unknown"
    parent_id: str | None = None
    order_index: int = 0
    visible: bool | None = None
    opacity: float | None = None
    blend_mode: str | None = None
    selected: bool = False
    mask_relation: str | None = None
    clipping_base_id: str | None = None
    children: list["LayerState"] = field(default_factory=list)
    evidence: list[dict[str, Any]] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class LayerSnapshot:
    schema_version: int = 1
    captured_at: str = field(default_factory=utc_now)
    document_name: str | None = None
    selected_layer_id: str | None = None
    layers: list[LayerState] = field(default_factory=list)
    source: str = "unavailable"
    status: str = "unavailable"
    unresolved: list[dict[str, Any]] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)
