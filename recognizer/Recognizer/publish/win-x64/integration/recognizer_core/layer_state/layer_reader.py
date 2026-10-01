from __future__ import annotations

from dataclasses import dataclass
import re
import unicodedata
from typing import Any

import cv2
import numpy as np

from .layer_state import LayerSnapshot, LayerState, utc_now
from .layer_catalog import LayerCatalog, get_layer_catalog


@dataclass
class TextBox:
    text: str
    points: list[list[float]]
    score: float | None

    @property
    def bounds(self) -> tuple[int, int, int, int]:
        xs = [point[0] for point in self.points]
        ys = [point[1] for point in self.points]
        x0, y0, x1, y1 = min(xs), min(ys), max(xs), max(ys)
        return round(x0), round(y0), max(1, round(x1 - x0)), max(1, round(y1 - y0))


class LayerOcrReader:
    """Read visible layer rows and identify a uniquely highlighted row."""

    MIN_RECOGNITION_CONFIDENCE = 0.8

    def __init__(self, max_side: int = 1800, method: str = "rapidocr",
                 ocr_engine: Any = None, catalog: LayerCatalog | None = None):
        self.max_side = max_side
        if method not in {"rapidocr", "contrast", "ensemble"}:
            raise ValueError(f"未知 OCR 方案：{method}")
        self.method = method
        self._engine: Any = ocr_engine
        self.catalog = catalog or get_layer_catalog()

    def read(self, panel_image: np.ndarray) -> tuple[LayerSnapshot, np.ndarray]:
        if panel_image is None:
            return LayerSnapshot(source="csp_panel_ocr", status="unavailable",
                                 unresolved=[{"reason": "图层面板截图为空", "status": "unavailable"}]), panel_image
        if not isinstance(panel_image, np.ndarray):
            raise TypeError("panel_image must be a NumPy array")
        if panel_image.size == 0:
            return LayerSnapshot(source="csp_panel_ocr", status="unavailable",
                                 unresolved=[{"reason": "图层面板截图为空", "status": "unavailable"}]), panel_image
        if panel_image.ndim != 3 or panel_image.shape[2] != 3 or panel_image.dtype != np.uint8:
            raise ValueError("panel_image must be a uint8 BGR image with shape (height, width, 3)")
        image = self._prepare_resolution(panel_image)
        boxes = self._recognize(image)
        # CSP renders each row as two text lines: opacity/blend above, name below.
        # Require the property line as an anchor so toolbar text is not a layer.
        line_rows = []
        for row in self._group_rows(boxes):
            parsed = self._parse_layer_row_details(row)
            center = float(np.median([item.bounds[1] + item.bounds[3] / 2 for item in row]))
            line_rows.append({"boxes": row, "parsed": parsed, "center": center})
        unmatched_property_rows = [line for line in line_rows
                                   if line["parsed"][1] is not None and line["parsed"][2] is None]
        anchors = [index for index, line in enumerate(line_rows)
                   if line["parsed"][1] is not None and line["parsed"][2] is not None
                   and line["parsed"][3]["status"] == "matched"]
        anchor_centers = [line_rows[index]["center"] for index in anchors]
        gaps = [anchor_centers[i + 1] - anchor_centers[i] for i in range(len(anchor_centers) - 1)
                if anchor_centers[i + 1] > anchor_centers[i]]
        heights = [box.bounds[3] for line in line_rows for box in line["boxes"]]
        row_pitch = float(np.median(gaps)) if gaps else max(34.0, float(np.median(heights)) * 3.0 if heights else 34.0)
        row_data = []
        used_name_lines: set[int] = set()
        for anchor_position, line_index in enumerate(anchors):
            prop_line = line_rows[line_index]
            prop_name, opacity, blend_mode, blend_match = prop_line["parsed"]
            name = prop_name
            name_boxes = self._name_boxes(prop_line["boxes"], min_x=int(image.shape[1] * 0.22))
            if name_boxes:
                inline_name = self._parse_layer_row(name_boxes)[0]
                if inline_name:
                    name = inline_name
            next_anchor = anchors[anchor_position + 1] if anchor_position + 1 < len(anchors) else len(line_rows)
            if not name:
                for candidate_index in range(line_index + 1, next_anchor):
                    candidate = line_rows[candidate_index]
                    delta = candidate["center"] - prop_line["center"]
                    if delta <= 0 or delta > row_pitch * 0.72:
                        continue
                    candidate_name_boxes = self._name_boxes(
                        candidate["boxes"], min_x=int(image.shape[1] * 0.22))
                    candidate_name = self._parse_layer_row(candidate_name_boxes)[0] if candidate_name_boxes else ""
                    if candidate_name:
                        name = candidate_name
                        name_boxes = candidate_name_boxes
                        used_name_lines.add(candidate_index)
                        break
            if not name:
                # The property line is still strong evidence of a layer even if
                # low-contrast selected text was missed by OCR. Keep the row and
                # make the missing name explicit instead of silently dropping it.
                name = f"（名称未识别 {anchor_position + 1}）"
                name_boxes = [box for box in prop_line["boxes"]
                              if box.bounds[0] >= int(image.shape[1] * 0.22)]
            name_boxes = name_boxes or self._name_boxes(prop_line["boxes"], min_x=int(image.shape[1] * 0.22)) or prop_line["boxes"]
            name_x = min(box.bounds[0] for box in name_boxes)
            original_name = name
            name = self._normalize_ocr_default_name(name)
            is_group = self._is_group_name(name)
            if is_group:
                name = self._normalize_default_group_name(name)
            combined_boxes = prop_line["boxes"] + (name_boxes if prop_name == "" else [])
            center = float(np.median([box.bounds[1] + box.bounds[3] / 2 for box in combined_boxes]))
            property_boxes = [box for box in prop_line["boxes"]
                              if box.bounds[0] >= int(image.shape[1] * 0.22)] or prop_line["boxes"]
            relevant_scores = [float(box.score) for box in property_boxes + name_boxes
                               if box.score is not None]
            name_scores = [float(box.score) for box in name_boxes if box.score is not None]
            property_scores = [float(box.score) for box in property_boxes if box.score is not None]
            if blend_match["method"] == "fuzzy":
                relevant_scores.append(float(blend_match["score"]))
            recognition_confidence = min(relevant_scores) if relevant_scores else 0.0
            row_data.append({"boxes": combined_boxes, "name": name, "opacity": opacity,
                             "blend_mode": blend_mode, "name_x": name_x, "is_group": is_group,
                             "blend_match": blend_match,
                             "recognition_confidence": recognition_confidence,
                             "name_confidence": min(name_scores) if name_scores else 0.0,
                             "property_confidence": min(property_scores) if property_scores else 0.0,
                             "name_had_ocr_noise": name != original_name,
                             "center": center, "property_rect": self._union_box(property_boxes),
                             "name_rect": self._union_box(name_boxes), "name_boxes": name_boxes,
                             "paper": False})

        # Paper is the standard layer row without an opacity/mode text line.
        for line_index, line in enumerate(line_rows):
            if line_index in anchors or line_index in used_name_lines:
                continue
            name = line["parsed"][0]
            if not ("纸张" in name or "paper" in name.casefold()):
                continue
            paper_boxes = [box for box in line["boxes"]
                           if box.text.strip().casefold() in {"纸张", "paper"}]
            if not paper_boxes:
                candidates = [box for box in line["boxes"]
                              if "纸张" in box.text or "paper" in box.text.casefold()]
                if candidates:
                    paper_boxes = [min(candidates, key=lambda box: len(box.text.strip()))]
            paper_boxes = paper_boxes or line["boxes"]
            rect = self._union_box(paper_boxes)
            row_data.append({"boxes": paper_boxes, "name": "纸张" if "纸张" in name else "Paper", "opacity": None,
                             "blend_mode": None, "name_x": rect[0], "is_group": False,
                             "recognition_confidence": min((float(box.score) for box in paper_boxes
                                                             if box.score is not None), default=0.0),
                             "center": line["center"], "property_rect": None,
                             "name_rect": rect, "paper": True})

        row_data.sort(key=lambda item: item["center"])
        rows = [item["boxes"] for item in row_data]
        selected_index, scores, bands, ambiguous = self._selected_row(image, rows)
        # Re-read the highlighted row after the full panel scan. Selection colors
        # can hide a group name or introduce punctuation into default layer names.
        retry_indices = ([selected_index] if selected_index is not None else [])
        retry_indices.extend(index for index, item in enumerate(row_data)
                             if index not in retry_indices and item["property_rect"]
                             and (item["name"].startswith("（名称未识别")
                                  or self._is_incomplete_default_name(item["name"])))
        for row_index in retry_indices[:2]:
            data = row_data[row_index]
            is_selected_row = row_index == selected_index
            if (not data["property_rect"] or not is_selected_row
                    and not data["name"].startswith("（名称未识别")
                    and not self._is_incomplete_default_name(data["name"])):
                continue
            property_box = data["property_rect"]
            property_center = property_box[1] + property_box[3] / 2
            target_y = property_center + row_pitch * 0.38
            half_band = max(10, int(row_pitch * 0.4))
            enhanced_boxes = self._enhanced_name_boxes(
                image, int(target_y - half_band), int(target_y + half_band),
                min_x=int(image.shape[1] * 0.22), target_y=target_y,
            )
            enhanced_name = self._parse_layer_row(enhanced_boxes)[0] if enhanced_boxes else ""
            enhanced_name = self._normalize_ocr_default_name(enhanced_name)
            if enhanced_name and not self._is_incomplete_default_name(enhanced_name):
                alternate_score = min((float(box.score) for box in enhanced_boxes
                                       if box.score is not None), default=0.0)
                old_name = data["name"]
                old_is_group = self._is_group_name(old_name)
                alternate_is_group = self._is_group_name(enhanced_name)
                expects_group = (
                    row_index + 1 < len(row_data)
                    and not row_data[row_index + 1]["paper"]
                    and row_data[row_index + 1]["name_x"] - data["name_x"] > max(8, int(image.shape[1] * 0.024))
                )
                needs_retry = (
                    old_name.startswith("（名称未识别")
                    or self._is_incomplete_default_name(old_name)
                    or (expects_group and alternate_is_group and not old_is_group)
                    or (old_is_group == alternate_is_group
                        and alternate_score >= data.get("name_confidence", 0.0) + 0.15)
                )
                if not needs_retry:
                    continue
                data["name"] = (self._normalize_default_group_name(enhanced_name)
                                 if self._is_group_name(enhanced_name) else enhanced_name)
                data["name_boxes"] = enhanced_boxes
                data["name_x"] = min(box.bounds[0] for box in enhanced_boxes)
                data["is_group"] = self._is_group_name(enhanced_name)
                data["name_rect"] = self._union_box(enhanced_boxes)
                data["name_confidence"] = alternate_score
                confidence_parts = [data.get("property_confidence", 0.0), alternate_score]
                if data["blend_match"].get("method") == "fuzzy":
                    confidence_parts.append(float(data["blend_match"].get("score", 0.0)))
                data["recognition_confidence"] = min(confidence_parts)

        # Complete the row classification only after the entire list has been
        # recognized. A row followed by a clearly deeper indentation is a group,
        # including a selected group whose highlighted title was unreadable.
        indent_threshold = max(8, int(image.shape[1] * 0.024))
        for index, data in enumerate(row_data[:-1]):
            next_data = row_data[index + 1]
            if (not data["paper"] and not next_data["paper"] and not data["is_group"]
                    and next_data["name_x"] - data["name_x"] > indent_threshold):
                data["is_group"] = True

        rejected_rows = [{
            "name": "",
            "raw_text": " ".join(box.text for box in line["boxes"]),
            "confidence": round(min((float(box.score) for box in line["boxes"] if box.score is not None), default=0.0), 3),
            "reason": "检测到不透明度，但混合模式未在词库中得到唯一匹配",
            "catalog_candidates": line["parsed"][3].get("candidates", []),
        } for line in unmatched_property_rows]
        accepted_rows = []
        for index, data in enumerate(row_data):
            confidence = data["recognition_confidence"]
            has_name = (
                data["name"] and not data["name"].startswith("（名称未识别")
                and not self._is_incomplete_default_name(data["name"])
            )
            has_required_text = data["paper"] or (
                has_name and data["opacity"] is not None and data["blend_mode"] is not None
            )
            if (index == selected_index and not has_name and data["opacity"] is not None
                    and data["blend_mode"] is not None):
                # Keep a confidently detected selected row visible for review;
                # a missed highlighted title must not drop its group and children.
                data["name"] = "（高亮行名称未识别）"
                has_required_text = True
            if confidence >= self.MIN_RECOGNITION_CONFIDENCE and has_required_text:
                accepted_rows.append(data)
            else:
                rejected_rows.append({"name": data["name"], "confidence": round(confidence, 3),
                                      "reason": ("默认图层名疑似被截断，需重新确认"
                                                 if self._is_incomplete_default_name(data["name"])
                                                 else "识别置信度低于 0.8 或缺少必要字段")})
        row_data = accepted_rows
        rows = [item["boxes"] for item in row_data]
        selected_index, selection_scores, bands, ambiguous = self._selected_row(image, rows)
        records = []
        for index, data in enumerate(row_data, start=1):
            y0, y1 = bands[index - 1]
            is_group = data["is_group"]
            layer_id = f"visual-row-{index:04d}"
            opacity_text = "" if data["opacity"] is None else f"{data['opacity'] * 100:.0f}%"
            formatted_text = f"{data['name']} {opacity_text} {data['blend_mode'] or ''}".strip()
            layer = LayerState(
                layer_id=layer_id, name=data["name"],
                layer_type="group" if is_group else "paper" if data["paper"] else "layer",
                order_index=index, visible=self._visible_state(image, y0, y1),
                opacity=data["opacity"], blend_mode=data["blend_mode"],
                selected=index - 1 == selected_index,
                evidence=[{
                    "type": "group_row" if is_group else "paper_row" if data["paper"] else "layer_row",
                    "bbox": self._compact_rect(data["property_rect"], data["name_rect"], image.shape[1], image.shape[0]),
                    "name_bbox": list(data["name_rect"]),
                    "property_bbox": list(data["property_rect"]) if data["property_rect"] else None,
                    "recognized_name": data["name"],
                    "recognized_opacity": data["opacity"],
                    "recognized_blend_mode": data["blend_mode"],
                    "blend_catalog_match": data.get("blend_match", {}),
                    "indent_x": int(data["name_x"]),
                    "confidence": round(float(data["recognition_confidence"]), 3),
                    "selection_confidence": round(float(selection_scores[index - 1]), 3),
                    "formatted_text": formatted_text,
                    "note": "截图派生临时序号，不是 CSP 内部图层 ID",
                    "thumbnail_signature": self._thumbnail_signature(image, y0, y1),
                }],
            )
            # Group row labels start after a wider folder glyph; compensate so
            # group and ordinary-layer indentation share the same depth grid.
            effective_x = (0 if data["paper"] else
                           int(data["name_x"] - image.shape[1] * (0.016 if is_group else 0)))
            records.append((layer, effective_x, is_group))
        layers = self._build_hierarchy(records, max(1, int(image.shape[1] * 0.045)))
        unresolved = ([{"reason": "以下行未达到输出条件，未输出为图层", "status": "unknown",
                        "rows": rejected_rows}] if rejected_rows else [])
        if not layers:
            unresolved.append({"reason": "OCR 未识别到图层行文字", "status": "unknown"})
        elif selected_index is None:
            reason = "检测到多个相近的高亮图层行" if ambiguous else "没有达到置信条件的图层高亮行"
            unresolved.append({"reason": reason, "status": "ambiguous" if ambiguous else "unknown"})
        if layers:
            unavailable_fields = sorted({field for layer in layers for field, value in (
                ("visible", layer.visible), ("opacity", layer.opacity), ("blend_mode", layer.blend_mode),
                ("layer_type", layer.layer_type),
                ("mask_relation", layer.mask_relation), ("clipping_base_id", layer.clipping_base_id),
            ) if value is None or value == "unknown"})
            if unavailable_fields:
                unresolved.append({"reason": "截图中未能可靠识别这些图层字段", "status": "unknown",
                                  "fields": unavailable_fields})
        selected_layer = next((layer for layer in layers if layer.selected), None)
        snapshot = LayerSnapshot(
            captured_at=utc_now(),
            selected_layer_id=selected_layer.layer_id if selected_layer else None,
            layers=layers,
            source="csp_window_screenshot_ocr",
            status=("unavailable" if not layers else
                    "ok" if selected_layer and all(layer.visible is not None and layer.opacity is not None
                                                    and layer.blend_mode is not None for layer in layers)
                    else "partial"),
            unresolved=unresolved,
        )
        return snapshot, image

    def _parse_layer_row(self, row: list[TextBox]) -> tuple[str, float | None, str | None]:
        name, opacity, blend_mode, _ = self._parse_layer_row_details(row)
        return name, opacity, blend_mode

    def _parse_layer_row_details(self, row: list[TextBox]) -> tuple[str, float | None, str | None, dict[str, Any]]:
        raw = " ".join(item.text.strip() for item in row if item.text.strip())
        text = re.sub(r"\s+", " ", raw).strip()
        percent = re.search(r"(?<!\d)(\d{1,3})(?:\s*[.,]\s*(\d*)|\s*[_])?\s*[%％]", text)
        opacity = None
        blend_mode = None
        if percent:
            try:
                value = percent.group(1)
                if percent.group(2):
                    value += "." + percent.group(2)
                opacity = min(1.0, max(0.0, float(value) / 100.0))
            except ValueError:
                pass

        blend_match: dict[str, Any] = {"status": "unknown", "score": 0.0, "method": "not_read"}
        if percent:
            # Anything before the percentage is in CSP's eye/status columns,
            # not part of the layer name. Inline names follow opacity and blend mode.
            tail = text[percent.end():]
            blend_mode, tail, blend_match = self._split_blend_mode(tail)
            text = tail
        # These are palette/control labels, never layer names. Keep arbitrary
        # user names intact, including names such as "图层 2" or "组 1".
        ignored = ("透明度", "不透明度", "状态", "可见性", "锁定", "图层属性", "图层面板", "历史记录")
        for token in ignored:
            text = text.replace(token, " ")
        text = re.sub(r"\b(?:opacity|status|visible|visibility|locked|lock)\b", " ", text, flags=re.IGNORECASE)
        text = re.sub(r"[|｜:：]+", " ", text)
        text = re.sub(r"\s+", " ", text).strip(" -_\t")
        text = re.sub(r"(?<=[\u3400-\u9fff])\s+(?=[\u3400-\u9fff])", "", text)
        if not text or not re.search(r"[A-Za-z\u3400-\u9fff]", text):
            return "", opacity, blend_mode, blend_match
        catalog = self.catalog
        control_blend_labels = {value["canonical"].casefold() for value in catalog.blend_modes}
        control_blend_labels |= {alias.casefold() for value in catalog.blend_modes for alias in value["aliases"]}
        if (text.casefold() in {"layer", "group", "opacity", "status", "visible", "visibility", "history"}
                or text.casefold() in control_blend_labels or text in {"图层", "历史记录"}):
            return "", opacity, blend_mode, blend_match
        return text, opacity, blend_mode, blend_match

    def _split_blend_mode(self, tail: str) -> tuple[str | None, str, dict[str, Any]]:
        """Match a known blend label at the start, leaving any inline name intact."""
        catalog = self.catalog
        entries = sorted(
            ((alias, entry["canonical"]) for entry in catalog.blend_modes for alias in entry["aliases"]),
            key=lambda pair: len(pair[0]), reverse=True,
        )
        for alias, canonical in entries:
            match = re.match(r"\s*" + re.escape(alias) + r"(?=$|\s|[\u3400-\u9fff])", tail, re.IGNORECASE)
            if match:
                matched = catalog.match_blend_mode(alias)
                matched["method"] = "exact"
                return canonical, tail[match.end():].strip(), matched

        # Fuzzy correction is restricted to the first space-delimited mode token;
        # it cannot consume a following custom layer name.
        candidate_match = re.match(r"\s*([^\s]+)(.*)$", tail)
        if candidate_match:
            candidate = candidate_match.group(1)
            matched = catalog.match_blend_mode(candidate)
            if matched["status"] == "matched":
                return matched["canonical"], candidate_match.group(2).strip(), matched
        return None, tail, {"status": "unknown", "score": 0.0, "method": "no_match"}

    @staticmethod
    def _is_group_name(name: str) -> bool:
        # OCR may render the gap in CSP's default ``组 1`` label as punctuation
        # (for example ``组°1``); allow separators before the generated index.
        return bool(re.match(r"^(?:组|文件夹)(?:[\s\W_]*\d+)?(?:\s|$)|^(?:group|folder)\s*\d*(?:\s|$)",
                             name, re.IGNORECASE))

    @staticmethod
    def _normalize_default_group_name(name: str) -> str:
        match = re.match(r"^(组|文件夹)[\s\W_]*(\d+)\s*$", name)
        if match:
            return f"{match.group(1)} {match.group(2)}"
        return name.strip()

    @staticmethod
    def _normalize_ocr_default_name(name: str) -> str:
        """Remove OCR noise only when the whole name matches a generated CSP name."""
        text = re.sub(r"\s+", " ", unicodedata.normalize("NFKC", name)).strip()
        text = re.sub(r"^\d{1,3}[\s.、:：)_-]*(?=(?:图层|组|文件夹))", "", text)
        match = re.fullmatch(r"(图层|组|文件夹)[\s~～﹏·•_.,-]*(\d+)", text)
        if match:
            prefix, number = match.groups()
            return f"{prefix}{number}" if prefix == "图层" else f"{prefix} {number}"
        return text

    @staticmethod
    def _is_incomplete_default_name(name: str) -> bool:
        # These are common OCR truncations of CSP's default generated names.
        # Do not silently publish them as if they were complete user names.
        return name.strip() in {"图", "图层", "组", "文件夹"}

    def _name_boxes(self, row: list[TextBox], min_x: int = 0) -> list[TextBox]:
        result = []
        for box in row:
            if box.bounds[0] < min_x:
                continue
            name, _, _ = self._parse_layer_row([box])
            if name:
                result.append(box)
        return result

    @staticmethod
    def _union_box(boxes: list[TextBox]) -> tuple[int, int, int, int]:
        x0 = min(box.bounds[0] for box in boxes)
        y0 = min(box.bounds[1] for box in boxes)
        x1 = max(box.bounds[0] + box.bounds[2] for box in boxes)
        y1 = max(box.bounds[1] + box.bounds[3] for box in boxes)
        return x0, y0, max(1, x1 - x0), max(1, y1 - y0)

    @staticmethod
    def _compact_rect(first: tuple[int, int, int, int] | None,
                      second: tuple[int, int, int, int] | None,
                      width: int, height: int) -> list[int]:
        boxes = [box for box in (first, second) if box]
        if not boxes:
            return [0, 0, 1, 1]
        x0 = max(0, min(box[0] for box in boxes) - 2)
        y0 = max(0, min(box[1] for box in boxes) - 2)
        x1 = min(width, max(box[0] + box[2] for box in boxes) + 2)
        y1 = min(height, max(box[1] + box[3] for box in boxes) + 2)
        return [x0, y0, max(1, x1 - x0), max(1, y1 - y0)]

    def _enhanced_name_boxes(self, image: np.ndarray, top: int, bottom: int, min_x: int,
                             target_y: float | None = None) -> list[TextBox]:
        """Retry only a missed name line with local contrast enhancement."""
        height, width = image.shape[:2]
        x0 = min(width - 1, max(0, min_x - 6))
        x1 = min(width, max(x0 + 1, int(width * 0.78)))
        top, bottom = max(0, top), min(height, bottom)
        crop = image[top:bottom, x0:x1]
        if crop.size == 0 or crop.shape[0] < 5:
            return []
        gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY) if crop.ndim == 3 else crop
        enhanced = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(8, 8)).apply(gray)
        enlarged = cv2.resize(enhanced, None, fx=2.0, fy=2.0, interpolation=cv2.INTER_CUBIC)
        local_boxes = self._recognize(enlarged)
        result = []
        for box in local_boxes:
            if not self._parse_layer_row([box])[0]:
                continue
            points = [[point[0] / 2.0 + x0, point[1] / 2.0 + top] for point in box.points]
            mapped = TextBox(box.text, points, box.score)
            if mapped.bounds[0] >= min_x:
                result.append(mapped)
        if target_y is not None and result:
            result.sort(key=lambda box: abs(box.bounds[1] + box.bounds[3] / 2 - target_y))
            nearest_center = result[0].bounds[1] + result[0].bounds[3] / 2
            result = [box for box in result
                      if abs(box.bounds[1] + box.bounds[3] / 2 - nearest_center) <= max(8, box.bounds[3] * 0.5)]
        return result

    @staticmethod
    def _build_hierarchy(records: list[tuple[LayerState, int, bool]], indent_step: int) -> list[LayerState]:
        if not records:
            return []
        # Cluster nearby label starts first. Folder names start slightly farther
        # right because of the folder icon, even when the group is root-level.
        # This prevents that icon offset from inventing an extra hierarchy level.
        tolerance = max(8, int(indent_step * 0.75))
        clusters: list[list[int]] = []
        cluster_values = sorted(indent for layer, indent, _ in records if layer.layer_type != "paper")
        if not cluster_values:
            cluster_values = sorted(indent for _, indent, _ in records)
        for indent in cluster_values:
            if not clusters or indent - round(sum(clusters[-1]) / len(clusters[-1])) > tolerance:
                clusters.append([indent])
            else:
                clusters[-1].append(indent)
        cluster_centers = [sum(cluster) / len(cluster) for cluster in clusters]

        def level_for(indent: int) -> int:
            return min(range(len(cluster_centers)), key=lambda index: abs(cluster_centers[index] - indent))

        rows = [(layer, level_for(indent_x), is_group)
                for layer, indent_x, is_group in records]
        # Normalize against the shallowest visible module. The first row may be
        # nested when the panel is scrolled and its parent is outside the crop.
        root_level = min(row_level for _, row_level, _ in rows)
        rows = [(layer, 0 if layer.layer_type == "paper" else row_level - root_level, is_group)
                for layer, row_level, is_group in rows]

        def parse_scope(start: int, level: int, parent: LayerState | None = None) -> tuple[list[LayerState], int]:
            """Consume one sibling scope, recursively counting each group's children."""
            layers: list[LayerState] = []
            groups: list[LayerState] = []
            index = start
            while index < len(rows):
                layer, row_level, is_group = rows[index]
                if row_level < level:
                    break
                if row_level > level:
                    # A parent can be outside a scrolled ROI. Preserve this
                    # group at the current scope and still count/recurse through
                    # its visible children.
                    layer.parent_id = parent.layer_id if parent else None
                    index += 1
                    if is_group:
                        groups.append(layer)
                        layer.children.clear()
                        if index < len(rows) and rows[index][1] > row_level:
                            children, index = parse_scope(index, rows[index][1], layer)
                            layer.children.extend(children)
                        if layer.evidence:
                            direct_groups = sum(child.layer_type == "group" for child in layer.children)
                            layer.evidence[0]["direct_module_count"] = len(layer.children)
                            layer.evidence[0]["direct_layer_count"] = len(layer.children) - direct_groups
                            layer.evidence[0]["direct_group_count"] = direct_groups
                    else:
                        layers.append(layer)
                    continue
                layer.parent_id = parent.layer_id if parent else None
                if not is_group:
                    layers.append(layer)
                    index += 1
                    continue

                layer.children.clear()
                groups.append(layer)
                index += 1
                if index < len(rows) and rows[index][1] > level:
                    child_level = rows[index][1]
                    children, index = parse_scope(index, child_level, layer)
                    layer.children.extend(children)
                if layer.evidence:
                    direct_groups = sum(child.layer_type == "group" for child in layer.children)
                    layer.evidence[0]["direct_module_count"] = len(layer.children)
                    layer.evidence[0]["direct_layer_count"] = len(layer.children) - direct_groups
                    layer.evidence[0]["direct_group_count"] = direct_groups

            # The requested ordering applies independently at every level.
            siblings = sorted(layers, key=lambda item: item.order_index)
            siblings.extend(sorted(groups, key=lambda item: item.order_index))
            return siblings, index

        roots, _ = parse_scope(0, root_level)
        return roots

    @staticmethod
    def _visible_state(image: np.ndarray, top: int, bottom: int) -> bool | None:
        height, width = image.shape[:2]
        # CSP's eye column sits at the far-left edge of each layer row.
        eye = image[top:bottom, :max(1, int(width * 0.095))]
        if eye.size == 0:
            return None
        gray = cv2.cvtColor(eye, cv2.COLOR_BGR2GRAY)
        bright = float(np.mean(gray >= 150))
        if bright >= 0.018:
            return True
        if bright <= 0.006:
            return False
        return None

    @staticmethod
    def _thumbnail_signature(image: np.ndarray, top: int, bottom: int) -> list[int]:
        height, width = image.shape[:2]
        # Sample the layer thumbnail itself and subtract the row background so
        # selection highlight changes do not masquerade as a content transfer.
        x0, x1 = int(width * 0.13), max(int(width * 0.14), int(width * 0.22))
        patch = image[max(0, top):min(height, bottom), x0:x1]
        if patch.size == 0:
            return []
        small = cv2.resize(patch, (4, 4), interpolation=cv2.INTER_AREA)
        gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
        background = image[max(0, top):min(height, bottom), int(width * 0.90):]
        background_gray = cv2.cvtColor(background, cv2.COLOR_BGR2GRAY) if background.size else gray
        offset = float(np.median(background_gray))
        normalized = np.clip(gray.astype(np.float32) - offset + 128.0, 0, 255)
        return [int(value) for value in normalized.reshape(-1)]

    def _recognize(self, image: np.ndarray) -> list[TextBox]:
        if self.method == "rapidocr":
            return self._recognize_rapid(image)
        if self.method == "contrast":
            return self._recognize_rapid(self._contrast_view(image))

        scale = 1.5 if max(image.shape[:2]) <= self.max_side / 1.5 else 1.0
        upscaled = cv2.resize(image, None, fx=scale, fy=scale,
                              interpolation=cv2.INTER_CUBIC) if scale > 1 else image
        views = [image, self._contrast_view(image), upscaled]
        detected: list[TextBox] = []
        for view in views:
            boxes = self._recognize_rapid(view)
            if view is upscaled and scale > 1:
                for box in boxes:
                    box.points = [[x / scale, y / scale] for x, y in box.points]
            detected.extend(boxes)
        return self._merge_boxes(detected)

    @staticmethod
    def _contrast_view(image: np.ndarray) -> np.ndarray:
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        enhanced = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8)).apply(gray)
        return cv2.cvtColor(enhanced, cv2.COLOR_GRAY2BGR)

    @staticmethod
    def _merge_boxes(boxes: list[TextBox]) -> list[TextBox]:
        """Keep the strongest overlapping OCR detection across image views."""
        ranked = sorted(boxes, key=lambda box: box.score or 0.0, reverse=True)
        kept: list[TextBox] = []
        for candidate in ranked:
            x, y, width, height = candidate.bounds
            duplicate = False
            for existing in kept:
                ex, ey, ew, eh = existing.bounds
                intersection = max(0, min(x + width, ex + ew) - max(x, ex)) * max(
                    0, min(y + height, ey + eh) - max(y, ey))
                union = width * height + ew * eh - intersection
                if union and intersection / union >= 0.45:
                    duplicate = True
                    break
            if not duplicate:
                kept.append(candidate)
        return kept

    def _recognize_rapid(self, image: np.ndarray) -> list[TextBox]:
        if self._engine is None:
            try:
                from rapidocr import RapidOCR
            except ImportError as exc:
                raise RuntimeError("缺少 RapidOCR；请重新运行 start.bat 安装依赖") from exc
            self._engine = RapidOCR()
        raw = self._engine(image)
        result = raw[0] if isinstance(raw, tuple) else raw
        if result is None:
            return []
        if hasattr(result, "boxes") and hasattr(result, "txts"):
            raw_boxes = result.boxes if result.boxes is not None else []
            raw_texts = result.txts if result.txts is not None else []
            raw_scores = result.scores if result.scores is not None else []
            entries = ((points, text, raw_scores[index] if index < len(raw_scores) else None)
                       for index, (points, text) in enumerate(zip(raw_boxes, raw_texts)))
        else:
            entries = result
        boxes = []
        for entry in entries:
            try:
                points, text, score = entry[0], entry[1], entry[2] if len(entry) > 2 else None
                points = np.asarray(points, dtype=float)
                if points.ndim == 2 and points.shape[0] >= 4 and points.shape[1] == 2 and np.isfinite(points).all():
                    boxes.append(TextBox(str(text), points[:4].tolist(), float(score) if score is not None else None))
            except (TypeError, ValueError, IndexError):
                continue
        return boxes

    def _group_rows(self, boxes: list[TextBox]) -> list[list[TextBox]]:
        ordered = sorted(boxes, key=lambda item: (item.bounds[1] + item.bounds[3] / 2, item.bounds[0]))
        rows: list[list[TextBox]] = []
        centers: list[float] = []
        for item in ordered:
            _, y, _, height = item.bounds
            center = y + height / 2
            target = next((i for i, old_center in enumerate(centers)
                           if abs(center - old_center) <= max(7.0, height * 0.82)), None)
            if target is None:
                rows.append([item])
                centers.append(center)
            else:
                rows[target].append(item)
                centers[target] = sum(box.bounds[1] + box.bounds[3] / 2 for box in rows[target]) / len(rows[target])
        return [sorted(row, key=lambda item: item.bounds[0]) for _, row in sorted(zip(centers, rows), key=lambda pair: pair[0])]

    def _selected_row(self, image: np.ndarray, rows: list[list[TextBox]]) -> tuple[int | None, list[float], list[tuple[int, int]], bool]:
        if not rows:
            return None, [], [], False
        height, width = image.shape[:2]
        centers = [float(np.median([item.bounds[1] + item.bounds[3] / 2 for item in row])) for row in rows]
        row_heights = [float(np.median([item.bounds[3] for item in row])) for row in rows]
        hsv_image = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
        if len(rows) > 1:
            gaps = [centers[i + 1] - centers[i] for i in range(len(centers) - 1) if centers[i + 1] > centers[i]]
            typical_gap = float(np.median(gaps)) if gaps else max(row_heights)
        else:
            typical_gap = max(24.0, row_heights[0] * 1.8)
        bands = []
        features = []
        x0 = min(width - 1, max(0, int(width * 0.38)))
        for index, center in enumerate(centers):
            half = max(row_heights[index] * 0.85, typical_gap * 0.44)
            top = max(0, int(center - half))
            bottom = min(height, max(top + 1, int(center + half)))
            bands.append((top, bottom))
            strip = image[top:bottom, x0:]
            if strip.size == 0:
                features.append((0.0, np.zeros(3, dtype=float)))
                continue
            hsv = hsv_image[top:bottom, x0:]
            # A selected row often has a blue accent; median-color contrast also
            # supports neutral gray themes without requiring a fixed UI language.
            blue = ((hsv[:, :, 0] >= 82) & (hsv[:, :, 0] <= 142)
                    & (hsv[:, :, 1] >= 52) & (hsv[:, :, 2] >= 28))
            blue_ratio = float(np.mean(blue))
            median_bgr = np.median(strip.reshape(-1, 3), axis=0)
            features.append((blue_ratio, median_bgr))
        reference = np.median(np.stack([feature[1] for feature in features]), axis=0)
        # Prefer direct blue-band evidence when present. Mixing it with relative
        # color distance makes a two-row panel falsely tie the normal row with a
        # blue-selected row because their medians straddle the reference color.
        has_blue_highlight = max((feature[0] for feature in features), default=0.0) >= 0.08
        if has_blue_highlight:
            scores = [feature[0] for feature in features]
        else:
            scores = [min(1.0, float(np.linalg.norm(median_bgr - reference)) / 45.0)
                      for _, median_bgr in features]
        ranked = sorted(range(len(scores)), key=lambda index: scores[index], reverse=True)
        best = ranked[0]
        runner_up = scores[ranked[1]] if len(ranked) > 1 else 0.0
        ambiguous = len(ranked) > 1 and scores[best] >= 0.22 and scores[best] - runner_up < 0.10
        # Blue/colored row highlights can occupy only part of a short group row.
        # Treat the strongest unambiguous highlighted band as selected for both
        # group and ordinary-layer rows.
        minimum_score = 0.12 if has_blue_highlight else 0.22
        selected = best if scores[best] >= minimum_score and not ambiguous else None
        return selected, scores, bands, ambiguous

    def _reduce_resolution(self, image: np.ndarray) -> np.ndarray:
        height, width = image.shape[:2]
        long_side = max(height, width)
        if long_side <= self.max_side:
            return image.copy()
        scale = self.max_side / long_side
        return cv2.resize(image, (max(1, round(width * scale)), max(1, round(height * scale))),
                          interpolation=cv2.INTER_AREA)

    def _prepare_resolution(self, image: np.ndarray) -> np.ndarray:
        image = self._reduce_resolution(image)
        height, width = image.shape[:2]
        # Small captured panels lose thin Chinese strokes. Mild whole-panel
        # enlargement improves OCR while staying below the existing max_side cap.
        if min(height, width) >= 420 and max(height, width) < 900:
            scale = min(1.5, self.max_side / max(height, width))
            image = cv2.resize(image, (round(width * scale), round(height * scale)),
                               interpolation=cv2.INTER_CUBIC)
        return image
