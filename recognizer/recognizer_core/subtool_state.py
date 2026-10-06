"""Locate visible subtool names in a caller-supplied CSP panel crop."""
from __future__ import annotations

import re
import unicodedata
from typing import Any

import numpy as np

from .panel_state.core import PanelStateCore
from .panel_state.models import utc_now
from .panel_state.ocr_engine import RapidOcrEngine
from .panel_state.visual_state import VisualStateRecognizer


def normalize_name(text: str) -> str:
    # The installed UI names and OCR can differ between traditional/simplified
    # Chinese. Keep matching exact after these common UI glyph substitutions.
    translation = str.maketrans('筆圓擬澀麥極細號簽線圖層選擇範圍畫氣噴裝飾體較強軟濃',
                               '笔圆拟涩麦极细号签线图层选择范围画气喷装饰体较强软浓')
    return re.sub(r'[^\w]', '', unicodedata.normalize('NFKC', text).casefold().translate(translation))


class SubtoolPanelCore:
    def __init__(self, ocr_engine: Any = None):
        self.ocr = ocr_engine or RapidOcrEngine()

    def process(self, image: np.ndarray, *, catalog: dict | None = None) -> dict:
        PanelStateCore._validate_image(image, 'subtool_image')
        observed_at = utc_now()
        raw = self.ocr.recognize(image) if hasattr(self.ocr, 'recognize') else self.ocr(image)
        items = PanelStateCore._normalize(raw)
        names, groups, tools = {}, {}, {}
        for node in (catalog or {}).get('nodes', []):
            if node['kind'] in ('subtool','group','tool') and node['name'] and not node.get('hidden'):
                target = {'subtool': names, 'group': groups, 'tool': tools}[node['kind']]
                target.setdefault(normalize_name(node['name']), []).append(node)
        rows = []
        for item in sorted(items, key=lambda item:(item.bbox()[1],item.bbox()[0])):
            x,y,w,h = item.bbox()
            row = next((row for row in rows if abs(y+h/2-row[0].bbox()[1]-row[0].bbox()[3]/2) <= max(8,h*.5)), None)
            if row is None:
                rows.append([item])
            else:
                row.append(item)
        entries, unmatched = self._locate(image,rows,names,'subtool')
        group_entries, _ = self._locate(image,rows,groups,'group')
        tool_entries, _ = self._locate(image,rows,tools,'tool')
        # A heading can share its name with a child. Only the top occurrences
        # preceding an actual child row are headings; don't promote them to a
        # selected subtool merely because the heading background is colored.
        headers = group_entries + tool_entries
        header_boxes = {tuple(e['bbox']) for e in headers}
        child_rows = [e['bbox'][1] for e in entries if tuple(e['bbox']) not in header_boxes]
        first_child = min(child_rows) if child_rows else None
        if first_child is not None:
            entries = [e for e in entries if not (tuple(e['bbox']) in header_boxes and e['bbox'][1] < first_child)]
        else:
            # When only a shared heading/child name is readable, its identity
            # cannot establish that it belongs to an actual child card.
            entries = [e for e in entries if tuple(e['bbox']) not in header_boxes]
        if entries:
            first_name_y = min(entry['bbox'][1] for entry in entries)
            group_entries = [entry for entry in group_entries if entry['bbox'][1]+entry['bbox'][3] <= first_name_y]
            tool_entries = [entry for entry in tool_entries if entry['bbox'][1]+entry['bbox'][3] <= first_name_y]
        unmatched = [entry for entry in unmatched if normalize_name(entry['text']) not in groups]
        return dict(schemaVersion=1,status='ok' if entries or group_entries else 'unknown' if names or groups else 'unavailable',
                    observedAt=observed_at,entries=entries,groupEntries=group_entries,toolEntries=tool_entries,unmatchedText=unmatched,
                    catalogSourceFile=(catalog or {}).get('sourceFile'),
                    selectionSource='imageBackgroundHeuristic')

    @staticmethod
    def _locate(image,rows,names,kind):
        entries, unmatched = [], []
        height, width = image.shape[:2]
        for row in rows:
            row.sort(key=lambda item:item.bbox()[0])
            used = set()
            # OCR sometimes splits one long brush name into adjacent segments.
            # Only combine segments when the entire name matches the catalog.
            for start in range(len(row)):
                if start in used:
                    continue
                matched = None
                for end in range(start+1, min(len(row),start+4)+1):
                    if any(index in used for index in range(start,end)):
                        break
                    text = ' '.join(item.text for item in row[start:end])
                    candidates = names.get(normalize_name(text), [])
                    if candidates:
                        matched = end,text,candidates
                if matched is None:
                    unmatched.append(dict(text=row[start].text,score=row[start].score))
                    continue
                end,text,candidates = matched
                parts = row[start:end]
                boxes = [item.bbox() for item in parts]
                left,top = max(0,min(b[0] for b in boxes)),max(0,min(b[1] for b in boxes))
                right,bottom = min(width,max(b[0]+b[2] for b in boxes)),min(height,max(b[1]+b[3] for b in boxes))
                if left >= right or top >= bottom:
                    continue
                bbox = [left,top,right-left,bottom-top]
                selection, selection_score = VisualStateRecognizer._selected_signal(image,*bbox,bbox[3])
                scores = [item.score for item in parts if item.score is not None]
                entries.append(dict(kind=kind,text=text,name=candidates[0]['name'],bbox=bbox,coordinateSpace='panel',
                    score=min(scores) if scores else None,selectionState=selection,selectionScore=selection_score,
                    matchStatus='unique' if len(candidates)==1 else 'ambiguous',
                    matches=[{key:node.get(key) for key in ('id','uuid','name','toolId','groupId','path','pathIds')}
                             for node in candidates]))
                used.update(range(start,end))
        return entries,unmatched
