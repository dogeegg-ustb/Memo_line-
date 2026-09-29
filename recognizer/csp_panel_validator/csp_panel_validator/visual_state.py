from __future__ import annotations

from typing import Any

import cv2
import numpy as np

from .models import OcrText, VisualState


class VisualStateRecognizer:
    """Visual evidence is computed from the same ROI image passed to OCR."""

    def analyze(self, image: np.ndarray, items: list[OcrText]) -> list[VisualState]:
        if image is None or image.size == 0:
            return [VisualState() for _ in items]
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        states: list[VisualState] = []
        heights = [item.bbox()[3] for item in items if item.bbox()[3] > 0]
        median_h = float(np.median(heights)) if heights else 14.0
        for item in items:
            x, y, w, h = item.bbox()
            y0, y1 = max(0, int(y - 0.45 * h)), min(gray.shape[0], int(y + 1.45 * h))
            x0, x1 = max(0, int(x - 0.8 * h)), min(gray.shape[1], int(x + w + 0.8 * h))
            row = gray[y0:y1, x0:x1]
            evidence: list[dict[str, Any]] = []
            selected, selected_score = self._selected_signal(image, x, y, w, h, median_h)
            if selected_score is not None:
                evidence.append({"roi_id": "", "bbox": [x, y, w, h], "type": "selected_background", "ocr_score": item.score, "reason": "当前文字区域的背景色饱和度；无明确颜色证据时不推断选中"})
            checkbox, enabled, checkbox_box = self._checkbox_signal(gray, x, y, h)
            if checkbox_box:
                evidence.append({"roi_id": "", "bbox": checkbox_box, "type": "checkbox", "ocr_score": item.score, "reason": "当前帧文字左侧方框的边框与填充分析"})
            if row.size:
                # A disabled control is often low contrast, but this remains an explicit heuristic.
                contrast = float(np.std(row))
                if contrast < 8.0:
                    enabled = "disabled"
                    evidence.append({"roi_id": "", "bbox": [x, y, w, h], "type": "disabled_style", "ocr_score": item.score, "reason": "控件区域对比度过低，标记为可能禁用"})
            states.append(VisualState(selected=selected, selected_score=selected_score, checkbox=checkbox, enabled=enabled, evidence=evidence))
        return states

    @staticmethod
    def _selected_signal(image: np.ndarray, x: int, y: int, w: int, h: int, median_h: float) -> tuple[str, float | None]:
        if h <= 0:
            return "unknown", None
        pad = max(1, int(h * .2))
        patch = image[max(0, y - pad):min(image.shape[0], y + h + pad), max(0, x - pad):min(image.shape[1], x + w + pad)]
        if not patch.size:
            return "unknown", None
        # Local median suppresses white glyphs/icons. Wide row averages used to
        # leak a neighboring selected button into all other options.
        hsv = cv2.cvtColor(patch, cv2.COLOR_BGR2HSV)
        score = float(np.median(hsv[:, :, 1])) / 255.0
        if score >= .12:
            return "selected", score
        return "unknown", score

    @staticmethod
    def _checkbox_signal(gray: np.ndarray, text_x: int, text_y: int, h: int) -> tuple[str, str, list[int] | None]:
        # Search strictly LEFT of the text. Searching inside a Chinese glyph
        # turns rectangular strokes into false checkboxes.
        if h < 4:
            return "not_checkbox", "unknown", None
        x0, x1 = max(0, int(text_x - 1.8 * h)), max(0, text_x - 2)
        y0, y1 = max(0, int(text_y - .3 * h)), min(gray.shape[0], int(text_y + 1.3 * h))
        search = gray[y0:y1, x0:x1]
        if not search.size:
            return "not_checkbox", "unknown", None
        contours, _ = cv2.findContours(cv2.Canny(search, 10, 30), cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
        candidates = []
        for contour in contours:
            x, y, w, height = cv2.boundingRect(contour)
            if not (.65 * h <= w <= 1.4 * h and .65 * h <= height <= 1.4 * h and .8 <= w / height <= 1.25):
                continue
            if abs(y0 + y + height / 2 - (text_y + h / 2)) > .35 * h:
                continue
            # A checkbox needs a rectangular outline, not just a checkmark/plus.
            if abs(cv2.contourArea(contour)) / (w * height) < .72:
                continue
            candidates.append((x, y, w, height))
        if not candidates:
            return "not_checkbox", "unknown", None
        x, y, w, height = min(candidates, key=lambda b: (text_x - (x0 + b[0] + b[2]), -b[2] * b[3]))
        patch = search[y:y + height, x:x + w]
        inner = patch[int(height * .22):int(height * .78), int(w * .22):int(w * .78)]
        background = float(np.median(inner))
        # Relative contrast works with dark and light themes; border is excluded.
        ink = float(np.mean(np.abs(inner.astype(float) - background) > 20))
        checkbox = "checked" if ink > .12 else "unchecked" if ink < .03 else "unknown"
        return checkbox, "unknown", [x0 + x, y0 + y, w, height]
