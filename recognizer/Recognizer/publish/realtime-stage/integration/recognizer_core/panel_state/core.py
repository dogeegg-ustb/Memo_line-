from __future__ import annotations

import time
from pathlib import Path
from typing import Any

import numpy as np

from .visual_value_extraction import apply_visual_values
from .models import OcrText, Rect, RoiRecognition, RoiRole, utc_now
from .ocr_engine import OcrUnavailable, RapidOcrEngine, normalize_rapid_result
from .panel_parser import PanelParser
from .property_catalog import PropertyCatalog
from .state_assembler import StateAssembler
from .visual_state import VisualStateRecognizer


class PanelStateCore:
    """Recognize CSP brush properties from one caller-supplied crop."""

    def __init__(self, ocr_engine: Any = None, catalog_path: str | Path | None = None):
        self.ocr = ocr_engine or RapidOcrEngine()
        self.catalog = PropertyCatalog(Path(catalog_path) if catalog_path else None)
        self.visual = VisualStateRecognizer()
        self.parser = PanelParser(self.catalog)
        self.assembler = StateAssembler()

    def process(
        self,
        tool_properties_image: np.ndarray,
        *,
        frame_id: int = 0,
        captured_at: str | None = None,
    ) -> dict[str, Any]:
        """Process one frame from a Tool Properties BGR crop."""
        if not isinstance(frame_id, int):
            raise TypeError("frame_id must be an int")
        if captured_at is not None and not isinstance(captured_at, str):
            raise TypeError("captured_at must be a string or None")
        self._validate_image(tool_properties_image, "tool_properties_image")

        captured_at = captured_at or utc_now()
        panels: list[tuple[str, RoiRole, np.ndarray]] = [
            ("tool_properties", "tool_properties", tool_properties_image),
        ]

        recognitions: list[RoiRecognition] = []
        raw_ocr: list[dict[str, Any]] = []
        diagnostics: list[dict[str, Any]] = []
        ocr_ms = 0
        parse_started = time.perf_counter()

        for roi_id, role, image in panels:
            if image is None or image.size == 0:
                recognition = RoiRecognition(
                    roi_id, role, [], [],
                    unresolved=[{"roi_id": roi_id, "reason": "面板截图为空", "status": "unavailable"}],
                )
                diagnostics.append({"roi_id": roi_id, "role": role, "status": "unavailable", "ocr_regions": []})
                recognitions.append(recognition)
                continue

            regions = [Rect(0, 0, image.shape[1], image.shape[0])]
            info: dict[str, Any] = {
                "roi_id": roi_id,
                "role": role,
                "ocr_regions": [region.to_list() for region in regions],
                "input_pixels": sum(region.width * region.height for region in regions),
                "roi_pixels": int(image.shape[0] * image.shape[1]),
            }
            started = time.perf_counter()
            try:
                items = []
                for region in regions:
                    crop = image[region.y:region.y + region.height, region.x:region.x + region.width]
                    items.extend(self._normalize(self._recognize(crop), region.x, region.y))
            except OcrUnavailable as exc:
                elapsed = round((time.perf_counter() - started) * 1000)
                ocr_ms += elapsed
                info.update(ocr_ms=elapsed, status="unavailable", error=str(exc))
                diagnostics.append(info)
                recognitions.append(RoiRecognition(
                    roi_id, role, [], [],
                    unresolved=[{"roi_id": roi_id, "reason": str(exc), "status": "unavailable"}],
                ))
                continue

            elapsed = round((time.perf_counter() - started) * 1000)
            ocr_ms += elapsed
            info.update(ocr_ms=elapsed, status="read" if items else "no_text")
            diagnostics.append(info)
            raw_ocr.extend({
                "roi_id": roi_id,
                "text": item.text,
                "bbox": item.bbox(),
                "score": item.score,
            } for item in items)

            states = self.visual.analyze(image, items)
            recognition = self.parser.parse(roi_id, role, items, states)
            apply_visual_values(image, recognition)
            recognitions.append(recognition)

        recognized_at = utc_now()
        processing_ms = round((time.perf_counter() - parse_started) * 1000)
        result = self.assembler.assemble(
            frame_id,
            captured_at,
            recognized_at,
            recognitions,
            {"capture": 0, "ocr": ocr_ms, "parse": max(0, processing_ms - ocr_ms), "total": processing_ms},
        )
        result["raw_ocr"] = raw_ocr
        result["panels"] = diagnostics
        result["catalog"] = {
            "version": self.catalog.metadata["catalog_version"],
            "property_count": len(self.catalog.definitions),
        }
        return result

    def _recognize(self, image: np.ndarray) -> Any:
        if hasattr(self.ocr, "recognize"):
            return self.ocr.recognize(image)
        if callable(self.ocr):
            return self.ocr(image)
        raise TypeError("ocr_engine must be callable or provide recognize(image)")

    @staticmethod
    def _normalize(raw: Any, offset_x: int = 0, offset_y: int = 0) -> list[OcrText]:
        if isinstance(raw, (list, tuple)) and all(isinstance(item, OcrText) for item in raw):
            items = list(raw)
        else:
            items = normalize_rapid_result(raw)
        if not offset_x and not offset_y:
            return items
        return [OcrText(item.text, item.score, [
            [point[0] + offset_x, point[1] + offset_y] for point in item.box
        ]) for item in items]

    @staticmethod
    def _validate_image(image: np.ndarray, name: str) -> None:
        if not isinstance(image, np.ndarray):
            raise TypeError(f"{name} must be a NumPy array")
        if image.size == 0:
            raise ValueError(f"{name} must not be empty")
        if image.ndim != 3 or image.shape[2] != 3 or image.dtype != np.uint8:
            raise ValueError(f"{name} must be a uint8 BGR image with shape (height, width, 3)")
