"""Read only current values while a validated tool/label layout remains unchanged."""
from __future__ import annotations

import base64
import copy
import hashlib
import re
from dataclasses import dataclass
from typing import Any

import cv2
import numpy as np

from .models import OcrText, Rect, RoiRecognition, VisualState
from .panel_parser import NUMBER_RE, UNIT_RE
from .selected_regions import selected_regions
from .visual_state import VisualStateRecognizer


def _box_rect(box: list[int], shape: tuple[int, ...]) -> Rect | None:
    height, width = shape[:2]
    x, y, w, h = (int(v) for v in box)
    x0, y0 = max(0, x), max(0, y)
    x1, y1 = min(width, x + w), min(height, y + h)
    return Rect(x0, y0, x1 - x0, y1 - y0) if x1 > x0 and y1 > y0 else None


def _patch(image: np.ndarray, rect: Rect) -> np.ndarray:
    return image[rect.y:rect.y + rect.height, rect.x:rect.x + rect.width]


def _selected_signature(image: np.ndarray) -> bytes | None:
    regions = selected_regions(image)
    if not regions:
        return None
    digest = hashlib.blake2b(digest_size=16)
    for rect in regions:
        digest.update(bytes(str(rect.to_list()), 'ascii'))
        digest.update(_patch(image, rect).tobytes())
    return digest.digest()


def _label_box(parameter: dict[str, Any]) -> list[int] | None:
    for entry in parameter.get('evidence', []):
        if entry.get('bbox') and entry.get('type') in ('ocr_text', 'checkbox', 'selected_background'):
            return entry['bbox']
    return None


def _right_region(image: np.ndarray, label: list[int]) -> Rect | None:
    height, width = image.shape[:2]
    x, y, w, h = (int(v) for v in label)
    left = max(int(width * .42), x + w + max(4, int(h * .25)))
    top = max(0, int(y - h * .45))
    bottom = min(height, int(y + h * 1.75))
    return Rect(left, top, width - left, bottom - top) if width - left >= 12 and bottom - top >= 8 else None


def _row_options(recognition: RoiRecognition, label: list[int]) -> list[OcrText]:
    x, y, w, h = label
    center_y = y + h / 2
    return sorted((item for item in recognition.ocr
                   if item.bbox()[0] > x + w and abs(item.bbox()[1] + item.bbox()[3] / 2 - center_y) <= max(12, h * .75)),
                  key=lambda item: item.bbox()[0])


def _highlight_index(image: np.ndarray, options: list[OcrText]) -> int | None:
    if len(options) < 2:
        return None
    scores = []
    for option in options:
        x, y, w, h = option.bbox()
        selected, score = VisualStateRecognizer._selected_signal(image, x, y, w, h, h)
        scores.append((score or 0.0) if selected == 'selected' else 0.0)
    winners = [index for index, score in enumerate(scores) if score >= .12]
    return winners[0] + 1 if len(winners) == 1 else None


def _highlight_index_from_visual(options: list[OcrText], states_by_item: dict[int, VisualState]) -> int | None:
    """Reuse the exact per-item selection scores computed during analyze()."""
    if len(options) < 2 or any(id(option) not in states_by_item for option in options):
        return None
    scores = []
    for option in options:
        state = states_by_item[id(option)]
        scores.append((state.selected_score or 0.0) if state.selected == 'selected' else 0.0)
    winners = [index for index, score in enumerate(scores) if score >= .12]
    return winners[0] + 1 if len(winners) == 1 else None


def _highlighted_pattern(image: np.ndarray, label: list[int]) -> tuple[list[int], str] | None:
    """Return the actual highlighted image tile only when no text option identifies it."""
    region = _right_region(image, label)
    if region is None:
        return None
    crop = _patch(image, region)
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    mask = np.uint8((hsv[:, :, 1] >= 32) & (hsv[:, :, 2] >= 35))
    count, _, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
    candidates = []
    for index in range(1, count):
        x, y, w, h, area = (int(v) for v in stats[index])
        if w >= max(18, int(label[3] * .8)) and h >= max(12, int(label[3] * .55)) and area >= .45 * w * h:
            candidates.append((x, y, w, h, area))
    if len(candidates) != 1:
        return None
    x, y, w, h, _ = candidates[0]
    # A uniform swatch is an index choice; only export tiles with visible pattern.
    tile = crop[y:y+h, x:x+w]
    inner = tile[max(1, h//8):max(2, h-h//8), max(1, w//8):max(2, w-w//8)]
    if inner.size == 0 or np.std(cv2.cvtColor(inner, cv2.COLOR_BGR2GRAY)) < 12:
        return None
    # The colored rectangle marks the choice; export the visual content inside it.
    tile_hsv = cv2.cvtColor(tile, cv2.COLOR_BGR2HSV)
    colored = tile[tile_hsv[:, :, 1] >= 32]
    background = np.median(colored, axis=0) if len(colored) else np.median(tile.reshape(-1, 3), axis=0)
    contrast = np.max(np.abs(tile.astype(np.float32) - background), axis=2)
    foreground = contrast > 32
    margin_y, margin_x = max(2, h//8), max(2, w//8)
    foreground[:margin_y] = False; foreground[-margin_y:] = False
    foreground[:, :margin_x] = False; foreground[:, -margin_x:] = False
    ys, xs = np.where(foreground)
    if len(xs) >= max(12, w*h*.025):
        px0, px1 = max(0, int(xs.min())-2), min(w, int(xs.max())+3)
        py0, py1 = max(0, int(ys.min())-2), min(h, int(ys.max())+3)
        x, y, w, h = x+px0, y+py0, px1-px0, py1-py0
        tile = crop[y:y+h, x:x+w]
    ok, png = cv2.imencode('.png', tile)
    if not ok:
        return None
    return [region.x+x, region.y+y, w, h], base64.b64encode(png.tobytes()).decode('ascii')


def _solid_swatch_index(image: np.ndarray, label: list[int]) -> int | None:
    """Number a row of outlined, plain color cells from left to right (1-based)."""
    region = _right_region(image, label)
    if region is None:
        return None
    crop = _patch(image, region)
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    mask = np.uint8((hsv[:, :, 1] >= 32) & (hsv[:, :, 2] >= 35))
    count, _, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
    selected = [(int(x), int(y), int(w), int(h)) for x,y,w,h,area in stats[1:]
                if w >= 16 and h >= 12 and area >= .45*w*h]
    if len(selected) != 1:
        return None
    sx, sy, sw, sh = selected[0]
    inner = crop[sy+max(1,sh//8):sy+sh-max(1,sh//8), sx+max(1,sw//8):sx+sw-max(1,sw//8)]
    if inner.size == 0 or float(np.std(cv2.cvtColor(inner, cv2.COLOR_BGR2GRAY))) >= 12:
        return None
    gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
    edges = cv2.Canny(gray, 25, 90)
    contours, _ = cv2.findContours(edges, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    boxes = []
    for contour in contours:
        x,y,w,h = cv2.boundingRect(contour)
        if (w >= 16 and h >= 12 and .65*sh <= h <= 1.4*sh
                and abs(y+h/2-(sy+sh/2)) <= max(8, sh*.25)
                and .5*sw <= w <= 1.5*sw):
            boxes.append((x,y,w,h))
    boxes.append((sx,sy,sw,sh))
    centers = []
    for x,y,w,h in sorted(boxes, key=lambda box: box[0]+box[2]/2):
        center = x+w/2
        if not centers or center-centers[-1] > max(6, sw*.25):
            centers.append(center)
    if len(centers) < 2:
        return None
    nearest = min(range(len(centers)), key=lambda i: abs(centers[i]-(sx+sw/2)))
    return nearest+1 if abs(centers[nearest]-(sx+sw/2)) <= sw*.35 else None


@dataclass
class _Anchor:
    rect: Rect
    image: np.ndarray


class IncrementalReader:
    """One cache per recognition pipeline; no persistent initialization boxes."""

    def __init__(self):
        self._signature: tuple | None = None
        self._tool_signature: bytes | None = None
        self._anchors: list[_Anchor] = []
        self._baseline: dict[str, RoiRecognition] = {}
        self._uses = 0

    def clear(self) -> None:
        self._signature = None
        self._tool_signature = None
        self._anchors.clear()
        self._baseline.clear()
        self._uses = 0

    @staticmethod
    def _profile_signature(profile: Any) -> tuple:
        return tuple((r.id, r.role, *r.rect.to_list()) for r in profile.rois)

    def remember(self, profile: Any, crops: dict[str, np.ndarray], recognitions: list[RoiRecognition], brush: dict[str, Any]) -> None:
        roles = {r.role: r for r in profile.rois}
        properties_only = set(roles) == {'tool_properties'} and len(profile.rois) == 1
        legacy_dual = set(roles) == {'tool_list', 'tool_properties'} and len(profile.rois) == 2
        if not (properties_only or legacy_dual) or brush.get('status') != 'ok':
            self.clear(); return
        prop_image = crops[roles['tool_properties'].id]
        signature = _selected_signature(crops[roles['tool_list'].id]) if legacy_dual else None
        by_role = {r.role: r for r in recognitions}
        if (legacy_dual and signature is None) or set(by_role) != set(roles) or not by_role['tool_properties'].parameters:
            self.clear(); return
        boxes = [entry['bbox'] for c in by_role['tool_properties'].brush_candidates for entry in c.get('evidence', []) if entry.get('bbox')]
        boxes += [box for parameter in by_role['tool_properties'].parameters if (box := _label_box(parameter))]
        anchors = []
        for box in boxes:
            rect = _box_rect(box, prop_image.shape)
            if rect:
                anchors.append(_Anchor(rect, _patch(prop_image, rect).copy()))
        if len(anchors) < 2:
            self.clear(); return
        self._signature = self._profile_signature(profile)
        self._tool_signature = signature
        self._anchors = anchors
        self._baseline = by_role
        self._uses = 0

    def applicable(self, profile: Any, crops: dict[str, np.ndarray]) -> bool:
        if self._signature != self._profile_signature(profile) or self._uses >= 20:
            return False
        roles = {r.role: r for r in profile.rois}
        properties_only = set(roles) == {'tool_properties'} and len(profile.rois) == 1
        legacy_dual = set(roles) == {'tool_list', 'tool_properties'} and len(profile.rois) == 2
        if properties_only:
            if self._tool_signature is not None:
                return False
        elif not legacy_dual or self._tool_signature != _selected_signature(crops[roles['tool_list'].id]):
            return False
        image = crops[roles['tool_properties'].id]
        for anchor in self._anchors:
            current = _patch(image, anchor.rect)
            if current.shape != anchor.image.shape:
                return False
            diff = cv2.absdiff(current, anchor.image)
            if float(np.mean(diff)) > 12 or float(np.mean(diff > 28)) > .22:
                return False
        return True

    def read(self, profile: Any, crops: dict[str, np.ndarray], engine: Any) -> tuple[list[RoiRecognition], list[OcrText], int]:
        roles = {r.role: r for r in profile.rois}
        list_base = copy.deepcopy(self._baseline['tool_list']) if 'tool_list' in roles else None
        prop_base = copy.deepcopy(self._baseline['tool_properties'])
        image = crops[roles['tool_properties'].id]
        regions: list[Rect] = []
        reading: list[tuple[dict[str, Any], Rect]] = []
        current_ocr: list[OcrText] = []
        gray_image: np.ndarray | None = None
        for parameter in prop_base.parameters:
            label = _label_box(parameter)
            if label is None:
                parameter.update(value='unknown', status='partial', raw_text=None)
                continue
            options = _row_options(self._baseline['tool_properties'], label)
            index = _highlight_index(image, options)
            if index is not None:
                parameter.update(type='highlight_index', value=index, status='ok', raw_text=str(index), unit=None,
                                 observed={'label': parameter['label'], 'selected_index': index, 'option_text': options[index-1].text},
                                 value_source='image')
                continue
            if len(options) >= 2 and parameter['type'] == 'highlight_index':
                parameter.update(value='unknown', status='partial', raw_text=None,
                                 observed={'label': parameter['label'], 'option': 'unknown'}, value_source='image')
                continue
            if not options and parameter['type'] not in ('number', 'checkbox'):
                index = _solid_swatch_index(image, label)
                if index is not None:
                    parameter.update(type='highlight_index', value=index, status='ok', raw_text=str(index), unit=None,
                                     observed={'label': parameter['label'], 'selected_index': index}, value_source='image')
                    continue
            if parameter['type'] == 'checkbox':
                if gray_image is None:
                    gray_image = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
                value, _, box = VisualStateRecognizer._checkbox_signal(gray_image, label[0], label[1], label[3])
                parameter.update(value=value if value in ('checked','unchecked') else 'unknown', status='ok' if value in ('checked','unchecked') else 'partial',
                                 observed={'label': parameter['label'], 'option': value}, value_source='image')
                if box:
                    parameter['evidence'] = [{'roi_id': roles['tool_properties'].id, 'bbox': box, 'type': 'checkbox', 'reason': '当前帧的复选框'}]
                continue
            if not options and parameter['type'] not in ('number', 'checkbox'):
                pattern = _highlighted_pattern(image, label)
                if pattern is not None:
                    box, png = pattern
                    parameter.update(type='highlight_pattern', value={'mime_type': 'image/png', 'png_base64': png}, status='ok', raw_text=None,
                                     observed={'label': parameter['label'], 'image_bbox': box}, value_source='image',
                                     evidence=parameter['evidence'] + [{'roi_id': roles['tool_properties'].id, 'bbox': box, 'type': 'selected_pattern', 'reason': '当前帧高亮区域包含的图案 PNG'}])
                    continue
            region = _right_region(image, label)
            if region:
                regions.append(region)
                reading.append((parameter, region))
            else:
                parameter.update(value='unknown', status='partial', raw_text=None, observed={'label': parameter['label'], 'option': 'unknown'})
        if regions:
            if hasattr(engine, 'recognize_selected'):
                current_ocr = engine.recognize_selected(image, regions)
            else:
                for region in regions:
                    for item in engine.recognize(_patch(image, region)):
                        current_ocr.append(OcrText(item.text, item.score, [[x+region.x,y+region.y] for x,y in item.box]))
        for parameter, region in reading:
            items = [item for item in current_ocr if region.x <= item.bbox()[0] + item.bbox()[2]/2 < region.x+region.width
                     and region.y <= item.bbox()[1] + item.bbox()[3]/2 < region.y+region.height]
            items.sort(key=lambda item: item.bbox()[0])
            text = ' '.join(item.text for item in items).strip()
            kind = parameter['type']
            number_match = NUMBER_RE.search(text)
            if kind == 'number' and number_match:
                raw = number_match.group(0)
                number = float(raw.replace(',','.'))
                value = int(number) if number.is_integer() else number
                unit_match = UNIT_RE.search(text[number_match.end():])
                unit = unit_match.group(0) if unit_match else None
                parameter.update(value=value, status='ok', raw_text=raw, unit=unit,
                                 observed={'label': parameter['label'], 'number': value, 'unit': unit}, value_source='value_ocr')
            elif text and kind in ('enum', 'label', 'compound'):
                parameter.update(value=text, status='ok', raw_text=text,
                                 observed={'label': parameter['label'], 'option': text}, value_source='value_ocr')
            else:
                parameter.update(value='unknown', status='partial', raw_text=None,
                                 observed={'label': parameter['label'], 'option': 'unknown'}, value_source='value_ocr')
            parameter['evidence'] = ([{'roi_id': roles['tool_properties'].id, 'bbox': label, 'type': 'cached_label', 'reason': '本帧图像再次验证的标签'}]
                                     + [{'roi_id': roles['tool_properties'].id, 'bbox': item.bbox(), 'type': 'value_ocr', 'ocr_score': item.score,
                                         'reason': '当前帧的值 OCR'} for item in items])
        # OCR items from the baseline are labels only; old values never enter raw output.
        anchors = [anchor.rect for anchor in self._anchors]
        prop_base.ocr = [item for item in self._baseline['tool_properties'].ocr if any(
            a.x <= item.bbox()[0] + item.bbox()[2]/2 < a.x+a.width and a.y <= item.bbox()[1]+item.bbox()[3]/2 < a.y+a.height for a in anchors)] + current_ocr
        prop_base.visual = [VisualState() for _ in prop_base.ocr]
        self._uses += 1
        output = [list_base if r.role == 'tool_list' else prop_base for r in profile.rois]
        return [item for item in output if item is not None], current_ocr, sum(r.width*r.height for r in regions)


def apply_visual_values(image: np.ndarray, recognition: RoiRecognition) -> None:
    """Classify colored selection controls in a full OCR frame as well."""
    states_by_item = {id(item): state for item, state in zip(recognition.ocr, recognition.visual)}
    for parameter in recognition.parameters:
        label = _label_box(parameter)
        if label is None:
            continue
        options = _row_options(recognition, label)
        index = (_highlight_index_from_visual(options, states_by_item)
                 if len(states_by_item) == len(recognition.ocr)
                 else _highlight_index(image, options))
        if index is not None:
            parameter.update(type='highlight_index', value=index, raw_text=str(index), status='ok', unit=None,
                             observed={'label': parameter['label'], 'selected_index': index, 'option_text': options[index-1].text},
                             value_source='image')
        elif not options and parameter.get('value') == 'unknown' and parameter['type'] not in ('number', 'checkbox'):
            index = _solid_swatch_index(image, label)
            if index is not None:
                parameter.update(type='highlight_index', value=index, raw_text=str(index), status='ok', unit=None,
                                 observed={'label': parameter['label'], 'selected_index': index}, value_source='image')
                continue
            pattern = _highlighted_pattern(image, label)
            if pattern is not None:
                box, png = pattern
                parameter.update(type='highlight_pattern', value={'mime_type': 'image/png', 'png_base64': png}, raw_text=None, status='ok',
                                 observed={'label': parameter['label'], 'image_bbox': box}, value_source='image',
                                 evidence=parameter['evidence'] + [{'roi_id': recognition.roi_id, 'bbox': box, 'type': 'selected_pattern', 'reason': '当前帧高亮区域包含的图案 PNG'}])
