"""Image-derived value extraction used by the stateless panel core."""
from __future__ import annotations

import base64
from typing import Any

import cv2
import numpy as np

from .models import OcrText, Rect, RoiRecognition, VisualState
from .visual_state import VisualStateRecognizer
def _patch(image: np.ndarray, rect: Rect) -> np.ndarray:
    return image[rect.y:rect.y + rect.height, rect.x:rect.x + rect.width]

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

def apply_visual_values(image: np.ndarray, recognition: RoiRecognition) -> None:
    """Classify colored selection controls in a full OCR frame as well."""
    _apply_disabled_styles(image, recognition)
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
    for parameter in recognition.parameters:
        if parameter.get('enabled') == 'disabled':
            parameter['status'] = 'disabled'


def _apply_disabled_styles(image: np.ndarray, recognition: RoiRecognition) -> None:
    """Compare matched labels within this frame; dark and light themes use the same contrast measure."""
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    height, width = gray.shape
    measurements = []
    for parameter in recognition.parameters:
        box = _label_box(parameter)
        if box is None:
            continue
        x, y, w, h = map(int, box)
        patch = gray[max(0,y):min(height,y+h),max(0,x):min(width,x+w)]
        if patch.size < 32:
            continue
        background = float(np.median(patch))
        contrast = max(float(np.percentile(patch,99))-background,
                       background-float(np.percentile(patch,1)))
        measurements.append((parameter,box,background,contrast))
    # An entirely faint panel cannot provide an active-style reference.
    if len(measurements) < 3:
        return
    baseline = float(np.percentile([m[3] for m in measurements],75))
    background = float(np.median([m[2] for m in measurements]))
    if baseline < 60:
        return
    for parameter, box, local_background, contrast in measurements:
        if abs(local_background-background) > 15:
            continue
        if 12 <= contrast < baseline*.65 and baseline-contrast >= 25:
            parameter['enabled'] = 'disabled'
            parameter.setdefault('evidence',[]).append(dict(
                roi_id=recognition.roi_id,bbox=box,type='disabled_style',
                reason='属性标签相对同帧其他属性明显灰显，标记为禁用',
                label_contrast=round(contrast,2),reference_contrast=round(baseline,2)))
