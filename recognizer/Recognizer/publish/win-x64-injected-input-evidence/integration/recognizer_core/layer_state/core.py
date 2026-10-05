from __future__ import annotations

from pathlib import Path
from typing import Any

from .layer_catalog import LayerCatalog, get_layer_catalog
from .layer_reader import LayerOcrReader
from .layer_state import LayerState


class LayerStateCore:
    """Read one already-cropped CSP layer-panel image."""

    def __init__(
        self,
        *,
        ocr_engine: Any = None,
        catalog_path: str | Path | None = None,
        max_side: int = 1800,
        method: str = "rapidocr",
    ) -> None:
        catalog = get_layer_catalog(Path(catalog_path) if catalog_path else None)
        self._reader = LayerOcrReader(
            max_side=max_side,
            method=method,
            ocr_engine=ocr_engine,
            catalog=catalog,
        )

    def process(self, panel_image) -> str | None:
        """Return the uniquely selected layer name, or None when it is unknown."""
        snapshot, _ = self._reader.read(panel_image)
        selected_names = [layer.name for layer in self._walk(snapshot.layers) if layer.selected]
        if len(selected_names) != 1:
            return None
        name = selected_names[0].strip()
        if not name or name.startswith(("（名称未识别", "（高亮行名称未识别")):
            return None
        return name

    @staticmethod
    def _walk(layers: list[LayerState]):
        for layer in layers:
            yield layer
            yield from LayerStateCore._walk(layer.children)
