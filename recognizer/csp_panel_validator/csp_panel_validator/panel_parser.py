from __future__ import annotations

import re
from collections import defaultdict
from typing import Any

from .models import OcrText, RoiRecognition, RoiRole, VisualState
from .property_catalog import get_catalog


NUMBER_RE = re.compile(r"(?<![\w.])[-+]?\d+(?:[.,]\d+)?")
UNIT_RE = re.compile(r"(%|％|px|像素|度|°|ms|毫米|mm)", re.IGNORECASE)


def _clean(text: str) -> str:
    return re.sub(r"\s+", " ", text.replace("\u3000", " ")).strip()


def _rows(items: list[OcrText], states: list[VisualState]) -> list[list[tuple[OcrText, VisualState]]]:
    pairs = sorted(zip(items, states), key=lambda pair: (pair[0].bbox()[1], pair[0].bbox()[0]))
    rows: list[list[tuple[OcrText, VisualState]]] = []
    for item, state in pairs:
        _, y, _, h = item.bbox()
        center = y + h / 2
        placed = False
        for row in rows:
            centers = [p[0].bbox()[1] + p[0].bbox()[3] / 2 for p in row]
            if abs(center - sum(centers) / len(centers)) <= max(10.0, h * 0.75):
                row.append((item, state))
                placed = True
                break
        if not placed:
            rows.append([(item, state)])
    for row in rows:
        row.sort(key=lambda pair: pair[0].bbox()[0])
    return rows


class PanelParser:
    """Rebuilds relationships from only the current frame's OCR and visual evidence."""

    NON_NAME = ("工具属性", "工具屬性", "子工具", "sub tool", "tool property")

    def parse(self, roi_id: str, role: RoiRole, items: list[OcrText], visual: list[VisualState]) -> RoiRecognition:
        for state in visual:
            for evidence in state.evidence:
                evidence["roi_id"] = roi_id
        recognition = RoiRecognition(roi_id, role, items, visual)
        if not items:
            recognition.unresolved.append({"roi_id": roi_id, "reason": "当前帧没有可用 OCR 文字", "status": "unknown"})
            return recognition
        rows = _rows(items, visual)
        if role == "tool_list":
            recognition.brush_candidates = self._parse_tool_list(roi_id, rows)
        else:
            recognition.parameters, recognition.brush_candidates, recognition.unresolved = self._parse_properties(roi_id, rows)
        return recognition

    def _parse_tool_list(self, roi_id: str, rows: list[list[tuple[OcrText, VisualState]]]) -> list[dict[str, Any]]:
        candidates: list[dict[str, Any]] = []
        for row in rows:
            text = _clean(" ".join(item.text for item, _ in row))
            if not text or self._is_heading(text):
                continue
            selected_states = [state for _, state in row if state.selected == "selected"]
            selected = bool(selected_states)
            if not selected:
                continue
            score = max((state.selected_score or 0.0 for state in selected_states), default=0.0)
            first = row[0][0]
            candidates.append({
                "name": text,
                "status": "selected" if selected else "candidate",
                "incomplete": self._looks_truncated(text, first.bbox()),
                "evidence": self._evidence(roi_id, row, "selected_background" if selected else "ocr_text", "子工具列表当前行的选中背景与文字证据"),
                "selection_score": score,
            })
        return candidates

    def _parse_properties(self, roi_id: str, rows: list[list[tuple[OcrText, VisualState]]]) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
        parameters: list[dict[str, Any]] = []
        brush_candidates: list[dict[str, Any]] = []
        unresolved: list[dict[str, Any]] = []
        title_names = set()
        for row in rows:
            title = _clean(" ".join(item.text for item, _ in row))
            match = re.search(r"(.+?)(?:工具属性|工具屬性|tool property)", title, re.IGNORECASE)
            if match:
                name = match.group(1).strip(' "“”「」『』')
                if name:
                    title_names.add(name)
                    brush_candidates.append({"name": name, "status": "candidate", "incomplete": self._looks_truncated(name, row[0][0].bbox()), "evidence": self._evidence(roi_id, row, "ocr_text", "当前工具属性标题明确显示的工具名称")})
        for row in rows:
            row_text = _clean(" ".join(item.text for item, _ in row))
            if not row_text or self._is_heading(row_text) or row_text in title_names:
                continue

            # Discover labels independently for each OCR segment. A segment may
            # also contain the label and its value (e.g. "笔刷尺寸 5.4").
            labels = []
            for index, (item, state) in enumerate(row):
                value_hint = 'number' if NUMBER_RE.search(item.text) else None
                if value_hint is None and index + 1 < len(row):
                    following, _ = row[index + 1]
                    if following.bbox()[0] >= item.bbox()[0] + item.bbox()[2] * .55 and NUMBER_RE.search(following.text):
                        value_hint = 'number'
                item_match = get_catalog().match(item.text, value_kind=value_hint)
                definition = item_match.get('definition')
                if definition:
                    label_text, inline_value = self._split_label_value(item.text, definition, item_match.get('matched_alias', ''))
                    labels.append((index, definition, label_text, inline_value, state))

            if not labels:
                # Older OCR often puts a label and its numeric value in one
                # box. Catalog prefix matching handles that as a fallback.
                inline = get_catalog().match(row_text)
                definition = inline.get('definition')
                if definition:
                    labels = [(0, definition, definition.get('label_zh_tw') or definition['label_en'],
                               self._tail_after_label(row_text, definition), row[0][1])]

            if not labels:
                if not title_names and not parameters and not brush_candidates and not unresolved and self._is_name_candidate(row_text):
                    brush_candidates.append({'name': row_text, 'status': 'candidate', 'incomplete': self._looks_truncated(row_text, row[0][0].bbox()), 'evidence': self._evidence(roi_id, row, 'ocr_text', '当前帧工具名称候选')})
                elif len(row_text) >= 2 and not self._is_heading(row_text):
                    unresolved.append({'roi_id': roi_id, 'raw_text': row_text, 'reason': '未匹配到已知属性标签', 'evidence': self._evidence(roi_id, row, 'ocr_text', '当前帧文字未匹配属性库')})
                continue

            used_value_indexes = set()
            for label_pos, (label_index, definition, label_text, inline_value, label_state) in enumerate(labels):
                key = definition['key']
                label_item = row[label_index][0]
                next_label_index = labels[label_pos + 1][0] if label_pos + 1 < len(labels) else len(row)
                right_items = [(i, item, state) for i, (item, state) in enumerate(row)
                               if label_index < i < next_label_index and item.bbox()[0] >= label_item.bbox()[0] + label_item.bbox()[2] * .55]
                right_text = _clean(' '.join(item.text for _, item, _ in right_items))
                check_state = label_state if label_state.checkbox in ('checked', 'unchecked') else next((s for _, s in row if s.checkbox in ('checked', 'unchecked')), None)

                inline_number = NUMBER_RE.search(inline_value or '')
                raw_number = inline_number.group(0) if inline_number else next((m.group(0) for _, item, _ in right_items if (m := NUMBER_RE.search(item.text))), None)
                if raw_number:
                    number = self._number(raw_number)
                    after_number = right_text[right_text.find(raw_number) + len(raw_number):] if raw_number in right_text else ''
                    unit_match = UNIT_RE.search(after_number)
                    value = number
                    parameters.append(self._parameter(roi_id, key, label_text, 'number', value, raw_number, unit_match.group(1) if unit_match else None,
                    self._evidence(roi_id, [(label_item, label_state)] + [(item, state) for _, item, state in right_items], 'ocr_text', '标签右侧的数字词段'), self._enabled([row[label_index]] + [(item,state) for _,item,state in right_items])))
                    parameters[-1]['observed'] = {'label': label_text, 'number': number, 'unit': unit_match.group(1) if unit_match else None}
                    used_value_indexes.update(i for i, _, _ in right_items)
                    continue

                if check_state:
                    value = check_state.checkbox if check_state.checkbox in ('checked', 'unchecked') else 'unknown'
                    parameters.append(self._parameter(roi_id, key, label_text, 'checkbox', value, value, None,
                        self._evidence(roi_id, [(label_item, label_state)], 'checkbox', '标签左侧复选框的当前勾选状态'), label_state.enabled,
                        status='ok' if value != 'unknown' else 'partial'))
                    parameters[-1]['observed'] = {'label': label_text, 'option': value}
                    continue

                inline_is_option = bool(inline_value) and (definition['value_kind'] == 'enum' or (definition['key'] == 'antialiasing' and len(inline_value) <= 8))
                if definition['key'] == 'antialiasing' and len(inline_value.split()) > 1:
                    inline_is_option = False
                selected_options = [(i, item, state) for i, item, state in right_items if state.selected == 'selected']
                plain_values = [(i, item, state) for i, item, state in right_items if not NUMBER_RE.search(item.text) and len(_clean(item.text)) > 1]
                if inline_is_option and not selected_options:
                    selected_options = [(label_index, label_item, label_state)]
                if not selected_options and len(plain_values) == 1 and definition['value_kind'] in ('enum', 'label'):
                    selected_options = plain_values
                if len(selected_options) == 1:
                    value = inline_value if selected_options[0][0] == label_index and inline_is_option else _clean(selected_options[0][1].text)
                    i, option_item, option_state = selected_options[0]
                    parameters.append(self._parameter(roi_id, key, label_text, 'enum', value, value, None,
                        self._evidence(roi_id, [(label_item, label_state), (option_item, option_state)], 'selected_background', '标签右侧选项中唯一高亮的一项'), label_state.enabled))
                    parameters[-1]['observed'] = {'label': label_text, 'option': value}
                    if i != label_index:
                        used_value_indexes.add(i)
                    continue

                if definition['read_support'] == 'label_only':
                    parameters.append(self._parameter(roi_id, key, label_text, 'label', 'unknown', None, None,
                        self._evidence(roi_id, [(label_item, label_state)], 'ocr_text', '属性标签已识别，当前行没有可读的值词段'), label_state.enabled, status='partial'))
                    parameters[-1]['observed'] = {'label': label_text, 'option': 'unknown'}
                    continue

                parameters.append(self._parameter(roi_id, key, label_text, definition['value_kind'], 'unknown', None, None,
                    self._evidence(roi_id, [(label_item, label_state)] + [(item,state) for _,item,state in right_items], 'ocr_text', '已识别标签，但右侧没有唯一可读的当前值'), label_state.enabled, status='partial'))
                parameters[-1]['observed'] = {'label': label_text, 'option': 'unknown'}
                used_value_indexes.update(i for i, _, _ in right_items)

            # Keep unrelated text visible as unresolved evidence without
            # mistaking the other unselected choices for current values.
            label_indexes = {entry[0] for entry in labels}
            leftovers = [item.text for i,(item,_) in enumerate(row) if i not in label_indexes and i not in used_value_indexes]
            if leftovers and not parameters:
                unresolved.append({'roi_id': roi_id, 'raw_text': _clean(' '.join(leftovers)), 'reason': '未绑定到属性标签'})
        for parameter in parameters:
            parameter['definition'] = get_catalog().describe(parameter['key'])
            matched = get_catalog().match(parameter['label'], value_kind=parameter['type'])
            parameter['definition']['match_method'] = matched.get('method')
            parameter['definition']['context_candidates'] = matched.get('definition_candidates', [])
        return parameters, brush_candidates, unresolved

    @staticmethod
    def _parameter(roi_id: str, key: str, label: str, kind: str, value: Any, raw: str | None, unit: str | None, evidence: list[dict[str, Any]], enabled: str, status: str = "ok") -> dict[str, Any]:
        return {"key": key or "unresolved", "label": label, "type": kind, "value": value, "unit": unit, "raw_text": raw, "enabled": enabled, "status": status, "evidence": evidence}

    @staticmethod
    def _evidence(roi_id: str, row: list[tuple[OcrText, VisualState]], kind: str, reason: str) -> list[dict[str, Any]]:
        evidence: list[dict[str, Any]] = []
        for item, state in row:
            evidence.append({"roi_id": roi_id, "bbox": item.bbox(), "type": kind, "ocr_score": item.score, "reason": reason})
            evidence.extend({**entry, "roi_id": roi_id} for entry in state.evidence if entry.get("type") == kind and entry.get("bbox") != item.bbox())
        return evidence

    def _key_for_label(self, text: str) -> str | None:
        match = get_catalog().match(text)
        return match['definition']['key'] if match['status'] == 'matched' else None

    @staticmethod
    def _split_label_value(text: str, definition: dict[str, Any], matched_alias: str) -> tuple[str, str]:
        """Split a label and trailing value embedded in one OCR word segment."""
        from .property_catalog import normalize
        normalized = normalize(text)
        aliases = sorted(definition.get('aliases', []), key=lambda value: len(normalize(value)), reverse=True)
        for alias in aliases:
            alias_norm = normalize(alias)
            if alias_norm and normalized.startswith(alias_norm):
                # Chinese UI labels have a one-to-one normalized character map.
                cut = len(alias)
                return _clean(text[:cut]).strip(' :：-'), _clean(text[cut:]).strip(' :：-')
        return _clean(definition.get('label_zh_tw') or definition['label_en']), ''

    @classmethod
    def _tail_after_label(cls, text: str, definition: dict[str, Any]) -> str:
        _, tail = cls._split_label_value(text, definition, '')
        return tail

    def _stable_key(self, label: str) -> str:
        return re.sub(r"[^\w]+", "_", label.lower()).strip("_") or "unresolved"

    @staticmethod
    def _number(raw: str) -> int | float:
        value = float(raw.replace(",", "."))
        return int(value) if value.is_integer() else value

    @staticmethod
    def _enabled(row: list[tuple[OcrText, VisualState]]) -> str:
        values = [state.enabled for _, state in row if state.enabled != "unknown"]
        return values[0] if values and all(value == values[0] for value in values) else "unknown"

    @staticmethod
    def _is_heading(text: str) -> bool:
        low = text.lower()
        return any(word.lower() in low for word in PanelParser.NON_NAME) or len(text) <= 1

    def _is_name_candidate(self, text: str) -> bool:
        return not self._is_heading(text) and not any(NUMBER_RE.search(text) for _ in [0]) and len(text) >= 2

    @staticmethod
    def _looks_truncated(text: str, bbox: list[int]) -> bool:
        return text.endswith(("…", "..."))
