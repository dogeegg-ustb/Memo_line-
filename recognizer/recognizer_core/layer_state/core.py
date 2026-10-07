from __future__ import annotations

from pathlib import Path
import math
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
        return self._selected_name(snapshot)

    @staticmethod
    def _selected_name(snapshot):
        selected_names = [layer.name for layer in LayerStateCore._walk(snapshot.layers) if layer.selected]
        if len(selected_names) != 1:
            return None
        name = selected_names[0].strip()
        if not name or name.startswith(("（名称未识别", "（高亮行名称未识别")):
            return None
        return name

    def process_with_locator(self, panel_image, *, panel_roi, capture_frame_id):
        """Return legacy semantics and physical-screen row locations from one frame."""
        if panel_image is None or panel_image.size == 0:
            raise ValueError('A captured layer-panel image is required.')
        height, width = panel_image.shape[:2]
        if len(panel_roi) != 4 or panel_roi[2:] != [width, height]:
            raise ValueError('panel_roi must match the captured image dimensions.')
        if not capture_frame_id:
            raise ValueError('capture_frame_id is required.')
        snapshot, prepared = self._reader.read(panel_image)
        scale_x, scale_y = width/prepared.shape[1], height/prepared.shape[0]

        def project(rect):
            if rect is None:
                return None
            x, y, w, h = rect
            x0, y0 = max(0, math.floor(x*scale_x)), max(0, math.floor(y*scale_y))
            x1, y1 = min(width, math.ceil((x+w)*scale_x)), min(height, math.ceil((y+h)*scale_y))
            if x1 <= x0 or y1 <= y0:
                return None
            return [panel_roi[0]+x0, panel_roi[1]+y0, x1-x0, y1-y0]

        rows = []
        for layer in sorted(self._walk(snapshot.layers), key=lambda row: row.order_index):
            evidence = next((item for item in layer.evidence if 'row_bounds' in item), {})
            rows.append(dict(name=layer.name, selected=layer.selected, visible=layer.visible,
                             rowBounds=project(evidence.get('row_bounds')),
                             eyeBounds=project(evidence.get('eye_bounds'))))
        return dict(selectedName=self._selected_name(snapshot), layerLocator=dict(
            panelRoi=list(panel_roi), captureFrameId=capture_frame_id, rows=rows))

    @staticmethod
    def _walk(layers: list[LayerState]):
        for layer in layers:
            yield layer
            yield from LayerStateCore._walk(layer.children)
