from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import numpy as np

from .models import OcrText


class OcrUnavailable(RuntimeError):
    pass


class RapidOcrEngine:
    """Adapter for rapidocr_onnxruntime; the model is kept resident for the process lifetime."""

    def __init__(self):
        self.available = False
        self.error = ""
        self._engine: Any = None
        self._factory: Any = None
        self.package_name = ""
        try:
            from rapidocr import RapidOCR  # type: ignore

            # Import is cheap; instantiate lazily on the worker so model setup never blocks Qt.
            self._factory = RapidOCR
            self.package_name = "rapidocr"
            self.available = True
        except Exception as modern_exc:  # pragma: no cover - depends on installed environment
            try:
                from rapidocr_onnxruntime import RapidOCR  # type: ignore

                self._factory = RapidOCR
                self.package_name = "rapidocr_onnxruntime"
                self.available = True
            except Exception as legacy_exc:  # includes missing runtime/model download errors
                self.error = f"rapidocr: {modern_exc}; legacy adapter: {legacy_exc}"

    def warmup(self) -> None:
        if self.available:
            blank = np.zeros((32, 128, 3), dtype=np.uint8)
            self.recognize(blank)

    def recognize(self, image: np.ndarray) -> list[OcrText]:
        if not self.available or self._factory is None:
            raise OcrUnavailable(self.error or "RapidOCR 不可用")
        if self._engine is None:
            self._engine = self._factory()
        raw = self._engine(image)
        return normalize_rapid_result(raw[0] if isinstance(raw, tuple) else raw)

    def recognize_selected(self, image: np.ndarray, regions: list) -> list[OcrText]:
        """Pack current highlighted pixels once; square padding avoids min-side inflation.

        Only highlighted pixels and synthetic neutral padding reach the detector.
        All returned boxes are mapped back into this frame's original ROI.
        """
        if not regions:
            return []
        width = max(r.width for r in regions) + 32
        height = sum(r.height + 16 for r in regions) + 16
        atlas = np.full((max(height, width), width, 3), 50, dtype=np.uint8)
        placements = []
        top = 16
        for r in regions:
            atlas[top:top+r.height, 16:16+r.width] = image[r.y:r.y+r.height, r.x:r.x+r.width]
            placements.append((r, top))
            top += r.height + 16
        items = []
        for item in self.recognize(atlas):
            x, y, w, h = item.bbox()
            for r, top in placements:
                if 16 <= x+w/2 < 16+r.width and top <= y+h/2 < top+r.height:
                    box = [[max(r.x, min(r.x+r.width, px-16+r.x)), max(r.y, min(r.y+r.height, py-top+r.y))] for px, py in item.box]
                    items.append(OcrText(item.text, item.score, box))
                    break
        return items


def normalize_rapid_result(result: Any) -> list[OcrText]:
    if result is None:
        return []
    if hasattr(result, "boxes") and hasattr(result, "txts"):
        boxes = result.boxes if result.boxes is not None else []
        texts = result.txts if result.txts is not None else ()
        scores = getattr(result, "scores", None)
        scores = scores if scores is not None else ()
        entries = []
        for index, (box, text) in enumerate(zip(boxes, texts)):
            entries.append([box, text, scores[index] if index < len(scores) else None])
        return normalize_rapid_result(entries)
    if isinstance(result, np.ndarray) and result.size == 0:
        return []
    normalized: list[OcrText] = []
    for item in result:
        if isinstance(item, dict):
            box = next((item[k] for k in ("box", "points", "poly") if item.get(k) is not None), None)
            text = item.get("text") or item.get("txt") or ""
            score = item.get("score", item.get("confidence"))
        elif isinstance(item, (list, tuple)) and len(item) >= 3:
            box, text, score = item[0], item[1], item[2]
        else:
            continue
        if box is None or text is None:
            continue
        try:
            points = np.asarray(box, dtype=float)
            if points.ndim != 2 or points.shape[0] < 4 or points.shape[1] != 2 or not np.isfinite(points).all():
                continue
            normalized.append(OcrText(str(text), float(score) if score is not None else None, points.tolist()))
        except (TypeError, ValueError, IndexError):
            continue
    return normalized


class SidecarOcrEngine:
    """Deterministic offline OCR input for parser/replay validation."""

    def __init__(self, path: str | Path):
        data = json.loads(Path(path).read_text(encoding="utf-8"))
        self.data = data
        self.available = True

    def recognize(self, image: np.ndarray, roi_id: str | None = None) -> list[OcrText]:
        entries = self.data.get(roi_id or "", [])
        return normalize_rapid_result(entries)


