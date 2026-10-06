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

def _solid_swatch_index(image: np.ndarray, label: list[int]) -> tuple[int, list[int]] | None:
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
    if abs(centers[nearest]-(sx+sw/2)) <= sw*.35:
        return nearest+1, [region.x+sx, region.y+sy, sw, sh]
    return None

def _hardness_indicator(image, label):
    """Count the filled prefix of the five-cell hardness indicator.

    These cells are cumulative, unlike the exclusive anti-alias buttons.
    Never number the entire connected filled region as one radio choice.
    """
    height,width=image.shape[:2]
    left=max(0,int(label[0]+label[2]-2))
    top=max(0,int(label[1]-label[3]*.65))
    bottom=min(height,int(label[1]+label[3]*1.8))
    crop=image[top:bottom,left:width]
    if crop.size==0:
        return None
    gray=cv2.cvtColor(crop,cv2.COLOR_BGR2GRAY)
    contours,_=cv2.findContours(cv2.Canny(gray,15,50),cv2.RETR_LIST,cv2.CHAIN_APPROX_SIMPLE)
    boxes=[]
    for contour in contours:
        x,y,w,h=cv2.boundingRect(contour)
        if (w>=18 and label[3]*.7<=h<=label[3]*1.8 and 1<=w/h<=2.2
                and abs(top+y+h/2-(label[1]+label[3]/2))<=label[3]*.65):
            boxes.append((x,y,w,h))
    if not boxes:
        return None
    typical=float(np.median([b[2] for b in boxes]))
    unique=[]
    for box in sorted(boxes,key=lambda b:b[0]+b[2]/2):
        if abs(box[2]-typical)>typical*.2:
            continue
        if not unique or abs(box[0]+box[2]/2-(unique[-1][0]+unique[-1][2]/2))>typical*.5:
            unique.append(box)
    if len(unique)!=5:
        return None
    centers=[b[0]+b[2]/2 for b in unique]
    gaps=np.diff(centers)
    if max(gaps)-min(gaps)>typical*.15:
        return None
    active=[]
    for x,y,w,h in unique:
        hsv=cv2.cvtColor(crop[y+3:y+h-3,x+3:x+w-3],cv2.COLOR_BGR2HSV)
        active.append(float(np.mean(hsv[:,:,1]>=32))>=.5)
    count=sum(active)
    if not count or active!=[True]*count+[False]*(5-count):
        return None
    x,y,w,h=unique[count-1]
    return count,[left+x,top+y,w,h]


def _antialias_choice(image, label, options, definition):
    """Identify a missed text choice using the verified four-button order.

    Two or more recognized labels anchor the grid. A unique filled highlight
    must align with it; unreadable dropdowns or irregular grids stay unknown.
    """
    from .property_catalog import normalize
    values = definition.get('enum_values', [])
    if len(values) != 4:
        return None
    aliases = {normalize(text): i for i, texts in enumerate(values) for text in texts}
    anchors = [(aliases[normalize(o.text)], o.bbox()[0]+o.bbox()[2]/2)
               for o in options if normalize(o.text) in aliases]
    if len(anchors) < 2 or len({i for i,_ in anchors}) != len(anchors):
        return None
    indices, centers = zip(*anchors)
    slope, origin = np.polyfit(indices, centers, 1)
    if slope < max(18, label[3]) or any(abs(origin+slope*i-x) > slope*.15 for i,x in anchors):
        return None
    height, width = image.shape[:2]
    left, right = max(label[0]+label[2]+2, int(origin-slope*.6)), min(width, int(origin+slope*3.6))
    top, bottom = max(0,int(label[1]-label[3]*.6)), min(height,int(label[1]+label[3]*1.7))
    if left >= right or top >= bottom:
        return None
    hsv = cv2.cvtColor(image[top:bottom,left:right], cv2.COLOR_BGR2HSV)
    mask = np.uint8((hsv[:,:,1] >= 32) & (hsv[:,:,2] >= 35))
    _,_,stats,_ = cv2.connectedComponentsWithStats(mask,8)
    tiles = [(int(x),int(y),int(w),int(h)) for x,y,w,h,area in stats[1:]
             if slope*.45 <= w <= slope*1.2 and h >= max(12,label[3]*.55) and area >= .45*w*h]
    if len(tiles) != 1:
        return None
    x,y,w,h = tiles[0]
    coordinate = (left+x+w/2-origin)/slope
    index = round(coordinate)
    if not 0 <= index < 4 or abs(coordinate-index) > .25:
        return None
    return index+1, [left+x,top+y,w,h], values[index][0]


def apply_visual_values(image: np.ndarray, recognition: RoiRecognition, catalog=None) -> None:
    """Classify colored selection controls in a full OCR frame as well."""
    _apply_disabled_styles(image, recognition)
    states_by_item = {id(item): state for item, state in zip(recognition.ocr, recognition.visual)}
    for parameter in recognition.parameters:
        label = _label_box(parameter)
        if label is None or parameter['type'] in ('number', 'checkbox'):
            continue
        options = _row_options(recognition, label)
        # Do not treat a second property's label/value on this row as a choice.
        next_labels = [box[0] for other in recognition.parameters if other is not parameter
                       and (box := _label_box(other)) and box[0] > label[0]
                       and abs(box[1]+box[3]/2-(label[1]+label[3]/2)) <= max(12, label[3]*.75)]
        if next_labels:
            options = [option for option in options if option.bbox()[0] < min(next_labels)]
        if parameter['key'] in ('brush_tip.hardness','dual.brush_tip.hardness') and not options:
            indicator=_hardness_indicator(image,label)
            if indicator is not None:
                index,box=indicator
                parameter.update(type='highlight_index',value=index,raw_text=str(index),status='ok',unit=None,
                    observed={'label':parameter['label'],'selected_index':index,'total_cells':5,
                              'control_kind':'cumulative_indicator'},value_source='image')
                parameter['_value_category']='icon'
                parameter['_value_evidence']=[dict(roi_id=recognition.roi_id,bbox=box,type='selected_icon',
                    reason='五格累积硬度指示器，定位最后一格填色单元')]
                continue
            if parameter.get('value') == 'unknown':
                continue
        if catalog and parameter['key'] in ('antialiasing','2_brush_shape.anti_aliasing'):
            choice = _antialias_choice(image,label,options,catalog.by_key[parameter['key']])
            if choice is not None:
                index,box,text = choice
                parameter.update(type='highlight_index',value=index,raw_text=str(index),status='ok',unit=None,
                                 observed={'label':parameter['label'],'selected_index':index,'option_text':text},
                                 value_source='image')
                parameter['_value_category'] = 'text'
                parameter['_value_evidence'] = [dict(roi_id=recognition.roi_id,bbox=box,type='selected_background',
                    text=text,reason='已识别选项锚定四档顺序，同帧唯一高亮单元格')]
                continue
        index = (_highlight_index_from_visual(options, states_by_item)
                 if len(states_by_item) == len(recognition.ocr)
                 else _highlight_index(image, options))
        if index is not None:
            selected = options[index-1]
            if catalog and parameter['key'] in ('antialiasing','2_brush_shape.anti_aliasing'):
                from .property_catalog import normalize
                values = catalog.by_key[parameter['key']].get('enum_values', [])
                known = [i+1 for i,aliases in enumerate(values) if normalize(selected.text) in {normalize(a) for a in aliases}]
                if len(known) != 1:
                    continue
                index = known[0]
            parameter.update(type='highlight_index', value=index, raw_text=str(index), status='ok', unit=None,
                             observed={'label': parameter['label'], 'selected_index': index, 'option_text': selected.text},
                             value_source='image')
            parameter['_value_category'] = 'text'
            parameter['_value_evidence'] = [{'roi_id': recognition.roi_id, 'bbox': selected.bbox(),
                'type': 'ocr_text', 'text': selected.text, 'ocr_score': selected.score, 'reason': '当前高亮文字选项'}]
        elif not options and parameter.get('value') == 'unknown' and parameter['type'] not in ('number', 'checkbox'):
            swatch = _solid_swatch_index(image, label)
            if swatch is not None:
                index, box = swatch
                parameter.update(type='highlight_index', value=index, raw_text=str(index), status='ok', unit=None,
                                 observed={'label': parameter['label'], 'selected_index': index}, value_source='image')
                parameter['_value_category'] = 'icon'
                parameter['_value_evidence'] = [{'roi_id': recognition.roi_id, 'bbox': box,
                    'type': 'selected_icon', 'reason': '当前高亮图标单元格'}]
                continue
            pattern = _highlighted_pattern(image, label)
            if pattern is not None:
                box, png = pattern
                parameter.update(type='highlight_pattern', value={'mime_type': 'image/png', 'png_base64': png}, raw_text=None, status='ok',
                                 observed={'label': parameter['label'], 'image_bbox': box}, value_source='image',
                                 evidence=parameter['evidence'] + [{'roi_id': recognition.roi_id, 'bbox': box, 'type': 'selected_pattern', 'reason': '当前帧高亮区域包含的图案 PNG'}])
                parameter['_value_category'] = 'icon'
                parameter['_value_evidence'] = [{'roi_id': recognition.roi_id, 'bbox': box,
                    'type': 'selected_pattern', 'reason': '当前高亮图案'}]
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
