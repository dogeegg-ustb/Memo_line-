"""Public brush locations refer exclusively to observed property values."""
from __future__ import annotations

from typing import Any


def _without_locations(value: Any) -> Any:
    """Label/title locations stay inside parsing, including partial diagnostics."""
    if isinstance(value, dict):
        return {key: _without_locations(item) for key, item in value.items()
                if not key.startswith('_') and key not in {'bbox', 'box', 'image_bbox', 'ocr_regions'}}
    if isinstance(value, list):
        return [_without_locations(item) for item in value]
    return value


def export_value_regions(result: dict[str, Any], image_shape: tuple[int, ...]) -> dict[str, Any]:
    """Project internal label/value evidence into the value-only schema v4."""
    height, width = image_shape[:2]
    exported = _without_locations(result)
    regions = []
    raw_ocr = []
    properties = result['brush']['properties']
    for index, (original, parameter) in enumerate(zip(properties, exported['brush']['properties'])):
        category = original.get('_value_category') or {
            'number': 'number', 'checkbox': 'icon', 'highlight_pattern': 'icon',
            'highlight_index': 'icon',
        }.get(original['type'], 'text')
        parameter['value_category'] = category
        evidence = []
        seen = set()
        if original.get('value') != 'unknown' and original.get('status') in {'ok', 'disabled'}:
            for entry in original.get('_value_evidence', []):
                box = entry.get('bbox')
                if not isinstance(box, (list, tuple)) or len(box) != 4:
                    continue
                x, y, w, h = box
                if not all(isinstance(v, int) for v in box) or w <= 0 or h <= 0:
                    continue
                # Intersect with the supplied crop, never with a guessed label box.
                left, top = max(0, x), max(0, y)
                right, bottom = min(width, x+w), min(height, y+h)
                if left >= right or top >= bottom:
                    continue
                bbox = [left, top, right-left, bottom-top]
                if tuple(bbox) in seen:
                    continue
                seen.add(tuple(bbox))
                evidence.append({**entry, 'bbox': bbox})
                region = dict(property_key=original['key'], property_index=index,
                              category=category, value=original['value'], bbox=bbox,
                              coordinate_space='panel', source='ocr' if entry['type'] == 'ocr_text' else 'image',
                              roi_id=entry['roi_id'], status=original['status'])
                if entry.get('ocr_score') is not None:
                    region['score'] = entry['ocr_score']
                if original.get('unit') is not None:
                    region['unit'] = original['unit']
                if region['source'] == 'ocr':
                    region['text'] = entry['text']
                regions.append(region)
                if region['source'] == 'ocr':
                    raw_ocr.append(dict(roi_id=entry['roi_id'], property_key=original['key'],
                                        category=category, text=entry['text'], bbox=bbox, score=entry.get('ocr_score')))
        parameter['evidence'] = evidence
        parameter['value_regions'] = [region for region in regions if region['property_index'] == index]
        parameter['value_location_status'] = 'located' if evidence else 'unresolved'
    exported['value_regions'] = regions
    # Legacy access is still supported, but no labels, headings, or unselected
    # choices can enter it. Image-only values live in value_regions instead.
    exported['raw_ocr'] = raw_ocr
    return exported
