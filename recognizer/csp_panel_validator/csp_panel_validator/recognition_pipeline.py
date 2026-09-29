from __future__ import annotations

import time
from typing import Any

import cv2

from .capture_service import CaptureService
from .models import CaptureFrame, Profile, RecognitionOutcome, OcrText, Rect, RoiRecognition, utc_now
from .selected_regions import selected_regions
from .property_catalog import get_catalog
from .panel_parser import PanelParser
from .state_assembler import StateAssembler
from .visual_state import VisualStateRecognizer
from .incremental_reader import IncrementalReader, apply_visual_values


class RecognitionPipeline:
    def __init__(self, ocr_engine: Any, visual: VisualStateRecognizer | None = None, parser: PanelParser | None = None, assembler: StateAssembler | None = None):
        self.ocr = ocr_engine
        self.visual = visual or VisualStateRecognizer()
        self.parser = parser or PanelParser()
        self.assembler = assembler or StateAssembler()
        self.incremental = IncrementalReader()

    def process(self, frame: CaptureFrame, profile: Profile, crops: dict[str, Any] | None = None, sidecar: Any = None) -> RecognitionOutcome:
        started = time.perf_counter()
        crops = crops or CaptureService.crop_all(frame, profile)
        capture_ms = round((time.perf_counter() - started) * 1000)
        ocr_started = time.perf_counter()
        recognitions = []
        overlay_images: dict[str, Any] = {}
        raw_ocr: list[dict[str, Any]] = []
        diagnostics = []
        errors = []
        mode = 'full'
        if sidecar is None and self.incremental.applicable(profile, crops):
            try:
                read_started = time.perf_counter()
                recognitions, value_ocr, input_pixels = self.incremental.read(profile, crops, self.ocr)
                elapsed = round((time.perf_counter() - read_started) * 1000)
                mode = 'values_only'
                for roi in profile.rois:
                    image = crops[roi.id]
                    recognition = next(item for item in recognitions if item.roi_id == roi.id)
                    overlay_images[roi.id] = self._overlay(image, recognition)
                    diagnostics.append({'roi_id': roi.id, 'role': roi.role, 'frame_id': frame.frame_id,
                                        'mode': mode, 'ocr_regions': [],
                                        'input_pixels': input_pixels if roi.role == 'tool_properties' else 0,
                                        'roi_pixels': image.shape[0] * image.shape[1],
                                        'ocr_ms': elapsed if roi.role == 'tool_properties' else 0,
                                        'status': 'values_read' if roi.role == 'tool_properties' else 'cached_selection'})
                prop_id = next(roi.id for roi in profile.rois if roi.role == 'tool_properties')
                raw_ocr.extend({'roi_id': prop_id, 'text': item.text, 'bbox': item.bbox(), 'score': item.score,
                                'source': 'current_value_ocr'} for item in value_ocr)
            except Exception:
                self.incremental.clear()
                recognitions.clear(); overlay_images.clear(); diagnostics.clear(); raw_ocr.clear()
        for roi in ([] if mode == 'values_only' else profile.rois):
            image = crops[roi.id]
            regions = selected_regions(image) if roi.role == 'tool_list' else [Rect(0, 0, image.shape[1], image.shape[0])]
            info = {'roi_id': roi.id, 'role': roi.role, 'frame_id': frame.frame_id,
                    'ocr_regions': [r.to_list() for r in regions],
                    'input_pixels': sum(r.width * r.height for r in regions),
                    'roi_pixels': image.shape[0] * image.shape[1]}
            diagnostics.append(info)
            roi_started = time.perf_counter()
            try:
                items = []
                if sidecar is not None:
                    # Replay annotations use ROI coordinates; filter by selected crop.
                    annotated = sidecar.recognize(image, roi.id) if regions else []
                    for item in annotated:
                        x, y, w, h = item.bbox()
                        if any(r.x <= x + w/2 < r.x+r.width and r.y <= y+h/2 < r.y+r.height for r in regions):
                            items.append(item)
                elif roi.role == 'tool_list' and hasattr(self.ocr, 'recognize_selected'):
                    items = self.ocr.recognize_selected(image, regions)
                else:
                    for region in regions:
                        crop = image[region.y:region.y+region.height, region.x:region.x+region.width]
                        for item in self.ocr.recognize(crop):
                            items.append(OcrText(item.text, item.score, [[x+region.x, y+region.y] for x,y in item.box]))
                info['ocr_ms'] = round((time.perf_counter() - roi_started) * 1000)
                info['status'] = 'read' if items else 'no_highlight' if not regions else 'no_text'
                raw_ocr.extend({'roi_id': roi.id, 'text': item.text, 'bbox': item.bbox(), 'score': item.score} for item in items)
                states = self.visual.analyze(image, items)
                if roi.role == 'tool_list':
                    for state in states:
                        state.selected, state.selected_score = 'selected', 1.0
                recognition = self.parser.parse(roi.id, roi.role, items, states)
                if roi.role == 'tool_properties':
                    apply_visual_values(image, recognition)
                if not regions:
                    recognition.unresolved = [{'roi_id': roi.id, 'reason': '未找到可靠的彩色高亮行，未读取未选中的子工具', 'status': 'unknown'}]
                recognitions.append(recognition)
                overlay_images[roi.id] = self._overlay(image, recognition)
                for region in regions:
                    cv2.rectangle(overlay_images[roi.id], (region.x,region.y), (region.x+region.width,region.y+region.height), (255,180,0), 1)
            except Exception as exc:
                reason = f'{roi.id}: {type(exc).__name__}: {exc}'
                errors.append({'roi_id': roi.id, 'reason': reason, 'status': 'unavailable'})
                info.update(status='unavailable', error=reason)
                recognitions.append(RoiRecognition(roi.id, roi.role, [], [], unresolved=[errors[-1]]))
                overlay_images[roi.id] = image.copy()
        ocr_ms = sum(info.get('ocr_ms', 0) for info in diagnostics)
        processing_ms = round((time.perf_counter() - ocr_started) * 1000) - ocr_ms
        parse_started = time.perf_counter()
        recognized_at = __import__("datetime").datetime.now(__import__("datetime").timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
        result = self.assembler.assemble(frame.frame_id, frame.captured_at, recognized_at, recognitions, {"capture": capture_ms, "ocr": ocr_ms, "parse": 0, "total": 0})
        result['recognition_mode'] = mode
        result["timings_ms"]["parse"] = max(0, processing_ms) + round((time.perf_counter() - parse_started) * 1000)
        result["timings_ms"]["total"] = round((time.perf_counter() - started) * 1000)
        result["capture_backend"] = frame.capture_backend
        result["capture_scope"] = "client"
        result["raw_ocr"] = raw_ocr
        result['panels'] = diagnostics
        result['catalog'] = {'version': get_catalog().metadata['catalog_version'], 'property_count': len(get_catalog().definitions)}
        if mode == 'full' and sidecar is None:
            self.incremental.remember(profile, crops, recognitions, result['brush'])
        missing = {'tool_properties'} - {r.role for r in profile.rois}
        if missing:
            result['unresolved'].append({'reason': '尚未配置面板：' + ', '.join(sorted(missing)), 'status': 'needs_reselection'})
            if result['status'] == 'ok':
                result['status'] = 'partial'
        return RecognitionOutcome(result, crops, overlay_images)

    @staticmethod
    def _overlay(image: Any, recognition: Any) -> Any:
        overlay = image.copy()
        for item in recognition.ocr:
            x, y, w, h = item.bbox()
            color = (0, 220, 0)
            cv2.rectangle(overlay, (x, y), (x + w, y + h), color, 1)
            cv2.putText(overlay, item.text[:24], (x, max(12, y - 3)), cv2.FONT_HERSHEY_SIMPLEX, 0.42, color, 1, cv2.LINE_AA)
        for parameter in recognition.parameters:
            for evidence in parameter.get("evidence", []):
                x, y, w, h = evidence.get("bbox", [0, 0, 0, 0])
                cv2.rectangle(overlay, (int(x), int(y)), (int(x + w), int(y + h)), (0, 165, 255), 2)
        return overlay


def image_signature(image: Any) -> Any:
    """Keep the captured ROI pixels so localized value edits remain observable."""
    return image.copy()


def profile_signature(image: Any, profile: Profile) -> tuple[tuple[str, Any], ...]:
    """Sign only configured panels; changes elsewhere cannot hide panel edits."""
    signatures = []
    for roi in profile.rois:
        rect = roi.rect
        crop = image[rect.y:rect.y + rect.height, rect.x:rect.x + rect.width]
        signatures.append((roi.id, image_signature(crop)))
    if not signatures:
        signatures.append(("full_frame", image_signature(image)))
    return tuple(signatures)


def changed(previous: tuple[tuple[str, Any], ...] | None, current: tuple[tuple[str, Any], ...], threshold: float) -> bool:
    if previous is None:
        return True
    old_by_id = dict(previous)
    if old_by_id.keys() != dict(current).keys():
        return True
    for roi_id, current_image in current:
        previous_image = old_by_id[roi_id]
        if previous_image.shape != current_image.shape:
            return True
        if current_image.size == 0:
            continue
        difference = cv2.absdiff(previous_image, current_image)
        if difference.ndim == 3:
            difference = cv2.max(cv2.max(difference[:, :, 0], difference[:, :, 1]), difference[:, :, 2])
        if float(cv2.mean(difference)[0]) / 255.0 * 100.0 >= threshold:
            return True
        # The previous 64x64 whole-window average diluted small value edits.
        # Check 16px neighborhoods too, while requiring a visible local change
        # so tiny capture noise does not continuously retrigger OCR.
        tile_width = max(1, (difference.shape[1] + 15) // 16)
        tile_height = max(1, (difference.shape[0] + 15) // 16)
        tile_means = cv2.resize(difference, (tile_width, tile_height), interpolation=cv2.INTER_AREA)
        if float(tile_means.max()) >= max(6.0, threshold * 2.0):
            return True
    return False
