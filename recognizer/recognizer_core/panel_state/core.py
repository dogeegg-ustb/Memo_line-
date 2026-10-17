from __future__ import annotations

import time
from pathlib import Path
from typing import Any

import cv2
import numpy as np

from .visual_value_extraction import _label_box, _right_region, apply_visual_values
from .models import OcrText, Rect, RoiRecognition, RoiRole, utc_now
from .ocr_engine import OcrUnavailable, RapidOcrEngine, normalize_rapid_result
from .panel_parser import NUMBER_RE, UNIT_RE, VALUE_NUMBER_RE, PanelParser
from .property_catalog import PropertyCatalog
from .state_assembler import StateAssembler
from .visual_state import VisualStateRecognizer
from .value_regions import export_value_regions


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
        tool_catalog: dict | None = None,
        subtool_image: np.ndarray | None = None,
        subtool_evidence: dict | None = None,
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
            states = self.visual.analyze(image, items)
            recognition = self.parser.parse(roi_id, role, items, states)
            self._reread_missing_numbers(image, recognition)
            apply_visual_values(image, recognition, self.catalog)
            recognitions.append(recognition)

        name_resolution = None
        if tool_catalog and tool_catalog.get('nodes'):
            name_resolution = self._resolve_name(recognitions, tool_catalog, subtool_image, subtool_evidence)
        recognized_at = utc_now()
        processing_ms = round((time.perf_counter() - parse_started) * 1000)
        result = self.assembler.assemble(
            frame_id,
            captured_at,
            recognized_at,
            recognitions,
            {"capture": 0, "ocr": ocr_ms, "parse": max(0, processing_ms - ocr_ms), "total": processing_ms},
        )
        result["panels"] = diagnostics
        if name_resolution is not None:
            result['brush']['name_resolution'] = name_resolution
        result["catalog"] = {
            "version": self.catalog.metadata["catalog_version"],
            "property_count": len(self.catalog.definitions),
        }
        return export_value_regions(result, tool_properties_image.shape)

    def _resolve_name(self, recognitions, catalog, subtool_image, companion_evidence):
        # Resolve against installed subtools. Panel titles and menu glyphs are
        # not identities, and the saved configuration's selected node is stale.
        from ..subtool_state import SubtoolPanelCore, normalize_name
        names = {}
        for node in catalog['nodes']:
            if node['kind'] == 'subtool' and node['name'] and not node.get('hidden'):
                names.setdefault(normalize_name(node['name']), []).append(node)
        rejected = []
        for recognition in recognitions:
            valid = []
            for candidate in recognition.brush_candidates:
                matches = names.get(normalize_name(candidate.get('name', '')), [])
                if matches:
                    valid.append({**candidate, 'name': matches[0]['name']})
                else:
                    rejected.append(candidate.get('name'))
            recognition.brush_candidates = valid
        if any(r.brush_candidates for r in recognitions):
            return dict(source='tool_properties', rejectedNames=rejected, catalogMatched=True)
        resolution = dict(source='unresolved', rejectedNames=rejected, catalogMatched=False)
        if subtool_image is None:
            resolution['reason'] = '本次采集没有可见子工具面板，属性面板名称未匹配工具目录'
            return resolution
        subtools = SubtoolPanelCore(ocr_engine=self.ocr).process(subtool_image, catalog=catalog)
        selected = [e for e in subtools['entries'] if e['selectionState'] == 'selected']
        groups = {m['id'] for e in subtools['groupEntries'] if e['selectionState'] == 'selected' for m in e['matches']}
        tools = {m['id'] for e in subtools.get('toolEntries', []) if e['selectionState'] == 'selected' for m in e['matches']}
        resolution.update(companionEvidence=companion_evidence or {}, selectedEntries=selected,
                          selectedGroupIds=sorted(groups), selectedToolIds=sorted(tools))
        if len(selected) != 1:
            resolution['reason'] = '子工具面板没有唯一高亮的子工具行'
            return resolution
        entry = selected[0]
        matches = [m for m in entry['matches'] if (not groups or m['groupId'] in groups)
                   and (not tools or m['toolId'] in tools)]
        if len(matches) != 1:
            resolution['reason'] = '高亮子工具名称在当前大类／组中不能唯一匹配'
            return resolution
        match = matches[0]
        candidate = dict(name=match['name'], status='selected', evidence=[dict(
            roi_id='tool_list', bbox=entry['bbox'], type='selected_background',
            reason='同次采集的唯一高亮子工具名称，已匹配本机工具目录', node_id=match['id'])])
        recognitions.append(RoiRecognition('tool_list', 'tool_list', [], [], brush_candidates=[candidate]))
        resolution.update(source='selected_subtool', catalogMatched=True, nodeId=match['id'], name=match['name'])
        return resolution

    MIN_REREAD_SCORE = 0.8

    def _reread_missing_numbers(self, image: np.ndarray, recognition: RoiRecognition) -> None:
        """Re-read a number whose label matched but whose digits the panel OCR missed.

        A lone small digit such as the stabilization "6" can drop out of
        full-panel detection after a one-pixel shift of the crop; OCR of the
        enlarged area right of its label finds it again. Only one plain number
        there is accepted, otherwise the value stays partial.
        """
        for parameter in recognition.parameters:
            if parameter['type'] != 'number' or parameter['value'] != 'unknown' or parameter['status'] != 'partial':
                continue
            label = _label_box(parameter)
            region = _right_region(image, label) if label else None
            if region is None:
                continue
            # Stop before a second property's label on the same row.
            center = label[1] + label[3] / 2
            right = min([box[0] for other in recognition.parameters if other is not parameter
                         and (box := _label_box(other)) and box[0] > label[0]
                         and abs(box[1] + box[3] / 2 - center) <= max(12, label[3] * .75)],
                        default=region.x + region.width)
            if right - region.x < 12:
                continue
            crop = image[region.y:region.y + region.height, region.x:right]
            item = None
            # A border keeps the detector from fusing the digit with the
            # spinner arrows at the crop edge ("6~" instead of "6").
            for scale, pad in ((1, 8), (2, 16)):
                view = cv2.resize(crop, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC) if scale > 1 else crop
                view = cv2.copyMakeBorder(view, pad, pad, pad, pad, cv2.BORDER_REPLICATE)
                numbers = [OcrText(found.text, found.score,
                                   [[(point[0] - pad) / scale + region.x, (point[1] - pad) / scale + region.y]
                                    for point in found.box])
                           for found in self._normalize(self._recognize(view))
                           if VALUE_NUMBER_RE.fullmatch(found.text)]
                if len(numbers) == 1 and (numbers[0].score or 0.0) >= self.MIN_REREAD_SCORE:
                    item = numbers[0]
                    break
            if item is None:
                continue
            raw_number = NUMBER_RE.search(item.text).group(0)
            number = PanelParser._number(raw_number)
            unit_match = UNIT_RE.search(item.text[item.text.find(raw_number) + len(raw_number):])
            unit = unit_match.group(1) if unit_match else None
            evidence = dict(roi_id=recognition.roi_id, bbox=item.bbox(), type='ocr_text', text=item.text,
                            ocr_score=item.score, reason='标签右侧局部放大重读的数字值')
            parameter.update(value=number, raw_text=raw_number, unit=unit, status='ok',
                             evidence=parameter['evidence'] + [evidence],
                             observed={'label': parameter['label'], 'number': number, 'unit': unit})
            parameter['_value_evidence'] = [evidence]
            parameter['_value_category'] = 'number'

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
