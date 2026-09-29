"""Replay the supplied screenshot with real OCR and compare selected-only cost."""
import json
import os
import statistics
import time
from pathlib import Path
from unittest.mock import patch

os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
import cv2
from PySide6.QtWidgets import QApplication
from PySide6.QtGui import QFont, QFontDatabase
from csp_panel_validator.models import CaptureFrame, MonitorProfile, Profile, Rect, RoiDefinition, utc_now
from csp_panel_validator.ocr_engine import RapidOcrEngine
from csp_panel_validator.recognition_pipeline import RecognitionPipeline
from csp_panel_validator.selected_regions import selected_regions
from csp_panel_validator.ui import MainWindow


def main():
    root = Path(__file__).resolve().parents[1]
    image = cv2.imread(str(root/'tests/fixtures/both_panels.png'))
    h, w = image.shape[:2]
    profile = Profile('both', MonitorProfile('test','test',Rect(0,0,w,h),(w,h),96,1), [
        RoiDefinition('list','子工具列表','tool_list',Rect(10,66,331,324)),
        RoiDefinition('props','工具属性','tool_properties',Rect(10,587,331,h-587))])
    engine = RapidOcrEngine()
    pipeline = RecognitionPipeline(engine)
    outcome = pipeline.process(CaptureFrame(1,utc_now(),image,'offline_fixture'),profile)
    crop = outcome.roi_images['list']
    regions = selected_regions(crop)
    timings = {'full_list': [], 'highlight_only': []}
    for _ in range(5):
        for kind in timings:
            started = time.perf_counter()
            if kind == 'full_list':
                engine.recognize(crop)
            else:
                engine.recognize_selected(crop, regions)
            timings[kind].append(round((time.perf_counter()-started)*1000,2))
    report = {'scope':'one supplied screenshot; warm model, 5 alternating trials; no desktop capture',
              'timings_ms':timings, 'median_ms':{k:statistics.median(v) for k,v in timings.items()},
              'pixels': {'full_list':crop.shape[0]*crop.shape[1], 'highlight_only':sum(r.width*r.height for r in regions)}}
    output = root/'examples'
    (output/'both_panels.actual.json').write_text(json.dumps(outcome.result,ensure_ascii=False,indent=2),encoding='utf-8')
    (output/'both_panels.benchmark.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    app = QApplication.instance() or QApplication([])
    font_id = QFontDatabase.addApplicationFont('C:/Windows/Fonts/msyh.ttc')
    families = QFontDatabase.applicationFontFamilies(font_id)
    if families:
        app.setFont(QFont(families[0], 10))
    with patch('csp_panel_validator.ui.QTimer.singleShot'):
        window = MainWindow(root/'.tmp/verify_ui')
        window.profile = profile
        window.show()
        app.processEvents()
        window.receive_outcome(outcome)
        window.roi_combo.setCurrentIndex(1)
        app.processEvents()
        window.grab().save(str(output/'both_panels.verified.png'))
        window.close()
    print(json.dumps({'brush':outcome.result['brush']['name'], 'benchmark':report},ensure_ascii=False))


if __name__ == '__main__':
    main()
