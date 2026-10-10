"""Read RGB slider editors from one supplied image; never operate the desktop."""
from __future__ import annotations

import re
from typing import Any

import cv2
import numpy as np

from ..panel_state.models import OcrText
from ..panel_state.ocr_engine import RapidOcrEngine, normalize_rapid_result


class ColorInputStateCore:
    def __init__(self, *, ocr_engine: Any = None):
        self.ocr = ocr_engine or RapidOcrEngine()

    def process(self, image: np.ndarray, *, panel_roi, capture_frame_id):
        if (not isinstance(image, np.ndarray) or image.dtype != np.uint8 or image.ndim != 3
                or image.shape[2] != 3 or image.size == 0):
            raise ValueError('A uint8 BGR panel image is required.')
        height, width = image.shape[:2]
        if (not isinstance(panel_roi, (list, tuple)) or len(panel_roi) != 4
                or any(not isinstance(v, int) or isinstance(v, bool) for v in panel_roi)
                or list(panel_roi[2:]) != [width, height] or not capture_frame_id):
            raise ValueError('panel_roi and capture_frame_id must identify this exact image.')
        result = dict(panelRoi=list(panel_roi), captureFrameId=capture_frame_id, activeTab='unknown',
                      activeColorSlot='unknown', valueRegions=[], tabRegions=[], colorSlotRegions=[],
                      status='panelNotVisible')
        raw = self._recognize(image)
        items = raw if isinstance(raw, list) and all(isinstance(v, OcrText) for v in raw) else normalize_rapid_result(
            raw[0] if isinstance(raw, tuple) else raw)
        items = [item for item in items if item.score is not None and item.score >= .90
                 and self._inside(item.bbox(), width, height)]
        text = lambda item: re.sub(r'\s+', '', item.text).upper()
        tabs = {tab: [item for item in items if text(item) == tab] for tab in ('RGB','HSV','CMYK')}
        # Color-wheel footer numbers have neither the three vertical tabs nor
        # separated channel rows. They must never be exported as inputs.
        if any(len(tabs[tab]) != 1 for tab in tabs):
            # CSP's small vertical tabs and isolated digits are often omitted
            # by full-panel OCR. Detect the actual RGB ramps, then OCR a padded
            # atlas of the channel labels and value column from those pixels.
            return self._read_rgb_ramps(image, panel_roi, capture_frame_id, result)
        tab_boxes = [tabs[t][0].bbox() for t in tabs]
        if (max(b[0] for b in tab_boxes) - min(b[0] for b in tab_boxes) > max(b[3] for b in tab_boxes)
                or len({b[1] for b in tab_boxes}) != 3):
            return result
        project = lambda box: [panel_roi[0]+box[0], panel_roi[1]+box[1], box[2], box[3]]
        result['tabRegions'] = [dict(tab=tab.lower(),screenBbox=project(tabs[tab][0].bbox())) for tab in tabs]
        rows = {}
        for tab, channels in (('rgb','RGB'),('hsv','HSV'),('cmyk','CMYK')):
            found = [[item for item in items if text(item) == channel] for channel in channels]
            if all(len(entries) == 1 for entries in found):
                boxes = [entries[0].bbox() for entries in found]
                if (max(b[0] for b in boxes) - min(b[0] for b in boxes) <= max(b[3] for b in boxes)
                        and min(b[0] for b in boxes) > max(b[0]+b[2] for b in tab_boxes)
                        and len({b[1] for b in boxes}) == len(channels)):
                    rows[tab] = [entries[0] for entries in found]
        if len(rows) != 1:
            result['status'] = 'ambiguous'
            return result
        active, labels = next(iter(rows.items()))
        result['activeTab'] = active
        if active != 'rgb':
            result['status'] = 'tabNotRgb'
            return result
        result['status'] = 'ambiguous'
        for channel, label in zip('rgb', labels):
            lx, ly, lw, lh = label.bbox()
            numeric = [item for item in items if re.fullmatch(r'\d{1,3}', text(item)) and
                       item.bbox()[0] > lx+lw+lh and
                       abs(item.bbox()[1]+item.bbox()[3]/2-(ly+lh/2)) <= lh/2]
            region = dict(channel=channel, value=None, screenBbox=None, status='unresolved')
            if len(numeric) == 1:
                item = numeric[0]; value = int(text(item))
                editor = self._editor(image, item.bbox(), label.bbox())
                if value <= 255:
                    region.update(value=value, screenBbox=project(item.bbox()), status='ok',
                                  editorScreenBbox=project(editor) if editor else project(item.bbox()))
            result['valueRegions'].append(region)
        # The actual main/sub/transparent capsule roles and selection marker
        # have not been calibrated against a real CSP frame. Text, position,
        # a bright border or the sampled color cannot prove the active slot.
        # Keep the observation partial until that independent proof exists.
        self._slot(image, result, project, max(label.bbox()[1] for label in labels))
        if all(r['status'] == 'ok' for r in result['valueRegions']) and result['activeColorSlot'] != 'unknown':
            result['status'] = 'ok'
        return result

    def _recognize(self, image):
        return self.ocr.recognize(image) if hasattr(self.ocr, 'recognize') else self.ocr(image)

    def _read_rgb_ramps(self, image, roi, frame, result):
        h, w = image.shape[:2]
        bars = []
        for channel in (2, 1, 0):  # BGR pixels, RGB rows
            other = [i for i in range(3) if i != channel]
            mask = ((image[:,:,channel] > 100) & (image[:,:,channel].astype(int) -
                    np.maximum(image[:,:,other[0]], image[:,:,other[1]]) > 65)).astype(np.uint8)*255
            contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            found = [cv2.boundingRect(c) for c in contours]
            found = [b for b in found if b[2] > w*.20 and b[2] > b[3]*5 and b[3] >= 4]
            if len(found) != 1:
                return result
            bars.append(found[0])
        centres = [y+bh/2 for x,y,bw,bh in bars]
        gap = (centres[2]-centres[0])/2
        if gap < 12 or abs(centres[1]-centres[0]-gap) > gap*.15:
            return result
        # The saturated section may be shorter at its dark end, but every
        # channel track must share the same right edge.
        rights = [x+bw for x,y,bw,bh in bars]
        if max(rights)-min(rights) > 3:
            return result
        right = max(rights)
        regions = []
        for cy in centres:
            x = int(right+gap*.15); y = int(cy-gap*.4)
            cw = int(gap*1.1); ch = int(gap*.8)
            if not self._inside([x,y,cw,ch],w,h):
                return result
            regions.append([x,y,cw,ch])
        # One padded atlas avoids OCR dropping three isolated zero glyphs.
        cell = max(int(gap),32); aw = max(150, max(r[2] for r in regions)+80)
        atlas = np.full((max(cell*4,aw),aw,3), image[regions[0][1],regions[0][0]], dtype=np.uint8)
        placements = []
        for i,(x,y,cw,ch) in enumerate(regions):
            top = i*cell+4
            atlas[top:top+ch,40:40+cw] = image[y:y+ch,x:x+cw]
            placements.append((x,y,cw,ch,top))
        raw = self._recognize(cv2.resize(atlas,None,fx=3,fy=3))
        items = raw if isinstance(raw,list) and all(isinstance(v,OcrText) for v in raw) else normalize_rapid_result(raw[0] if isinstance(raw,tuple) else raw)
        result.update(activeTab='rgb',status='ambiguous',valueRegions=[])
        for channel,(x,y,cw,ch,top) in zip('rgb',placements):
            candidates = [t for t in items if t.score is not None and t.score >= .90 and
                          re.fullmatch(r'\d{1,3}',t.text.strip()) and top*3 <= t.bbox()[1]+t.bbox()[3]/2 < (top+ch)*3]
            region = dict(channel=channel,value=None,screenBbox=None,status='unresolved')
            if len(candidates) == 1 and int(candidates[0].text) <= 255:
                bx,by,bw,bh = candidates[0].bbox()
                box = [x+int(bx/3)-40,y+int(by/3)-top,max(1,int(np.ceil(bw/3))),max(1,int(np.ceil(bh/3)))]
                if self._inside(box,w,h) and x <= box[0] and box[0]+box[2] <= x+cw:
                    region.update(value=int(candidates[0].text),screenBbox=[roi[0]+box[0],roi[1]+box[1],box[2],box[3]],status='ok')
            result['valueRegions'].append(region)
        project = lambda b: [roi[0]+b[0],roi[1]+b[1],b[2],b[3]]
        self._slot(image,result,project,int(centres[-1]+gap*.4))
        if all(r['status'] == 'ok' for r in result['valueRegions']) and result['activeColorSlot'] != 'unknown':
            result['status']='ok'
        return result

    @staticmethod
    def _slot(image, result, project, bottom):
        # Selected CSP capsule has a continuous muted blue rounded outline.
        # Restrict to pixels below all channel rows; saturated ramps, role text
        # and rectangular decorations cannot establish selection.
        hsv = cv2.cvtColor(image,cv2.COLOR_BGR2HSV)
        mask = cv2.inRange(hsv,np.array([95,25,100]),np.array([125,150,255]))
        mask[:min(bottom+8,image.shape[0])] = 0
        contours,_ = cv2.findContours(mask,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_SIMPLE)
        candidates=[]
        for c in contours:
            x,y,w,h=cv2.boundingRect(c)
            if w < h*1.8 or h < 15 or cv2.contourArea(c) < w*h*.65:
                continue
            # A capsule's curved upper corners are inset from its middle edge.
            if np.count_nonzero(mask[y:y+3,x:x+4]) or np.count_nonzero(mask[y:y+3,x+w-4:x+w]):
                continue
            candidates.append([x,y,w,h])
        if len(candidates)!=1:
            return
        box=candidates[0]
        centre=box[0]+box[2]/2
        slot='main' if centre < image.shape[1]*.5 else 'sub' if centre < image.shape[1]*.77 else 'transparent'
        result['activeColorSlot']=slot
        result['colorSlotRegions']=[dict(slot=slot,screenBbox=project(box),selected=True)]
        result['selectionEvidence']='roundedBlueCapsule'

    @staticmethod
    def _inside(box, width, height):
        x,y,w,h = box
        return all(isinstance(v,int) for v in box) and x >= 0 and y >= 0 and w > 0 and h > 0 and x+w <= width and y+h <= height

    @staticmethod
    def _editor(image, number, label):
        nx,ny,nw,nh = number; lx,ly,lw,lh = label
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        contours, _ = cv2.findContours(cv2.Canny(gray, 20, 60), cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
        candidates = []
        for contour in contours:
            x,y,w,h = cv2.boundingRect(contour)
            if (x <= nx and y <= ny and x+w >= nx+nw and y+h >= ny+nh
                    and x > lx+lw+lh and x >= image.shape[1]*.55 and
                    nw+2 <= w <= max(nw*3,lh*5) and nh+2 <= h <= lh*2.5
                    and abs(cv2.contourArea(contour)) >= w*h*.70):
                candidates.append((x,y,w,h))
        # Nested edge contours are the same observed editor; the smallest
        # enclosure excludes adjacent spin buttons and the slider track.
        return list(min(candidates,key=lambda b:b[2]*b[3])) if candidates else None

