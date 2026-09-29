"""Real RapidOCR, real user screenshots. No sidecar or mocked text recognition."""
import unittest
import base64
from pathlib import Path

import cv2
import numpy as np

from csp_panel_validator.incremental_reader import _solid_swatch_index
from csp_panel_validator.models import CaptureFrame, MonitorProfile, Profile, Rect, RoiDefinition
from csp_panel_validator.ocr_engine import RapidOcrEngine
from csp_panel_validator.recognition_pipeline import RecognitionPipeline


class RealPanelTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.engine = RapidOcrEngine()
        if not cls.engine.available:
            raise unittest.SkipTest(cls.engine.error)

    def recognize(self, name):
        image = cv2.imread(str(Path(__file__).parent / "fixtures" / name))
        self.assertIsNotNone(image)
        h, w = image.shape[:2]
        profile = Profile("fixture", MonitorProfile("fixture", "fixture", Rect(0, 0, w, h), (w, h), 96, 1), [RoiDefinition("props", "props", "tool_properties", Rect(0, 0, w, h))])
        result = RecognitionPipeline(self.engine).process(CaptureFrame(1, "fixture", image), profile).result
        self.assertNotEqual(result["status"], "unavailable", result.get("unresolved"))
        self.assertGreater(len(result["raw_ocr"]), 5)
        return result, {p["key"]: p for p in result["brush"]["properties"]}

    def test_selection_tool_screenshot(self):
        result, params = self.recognize("selection_panel.png")
        self.assertEqual(result["brush"]["name"], "僅參照編輯圖層選擇")
        self.assertEqual(params["color_tolerance"]["type"], "number")
        self.assertEqual(params["color_tolerance"]["value"], 10)
        for key in ("contiguous_pixels", "close_gap", "antialiasing"):
            self.assertEqual(params[key]["value"], "checked", key)
        for key in ("area_scaling", "reference_layers"):
            self.assertEqual(params[key]["value"], "unchecked", key)
        self.assertEqual(params["creation_mode"]["type"], "highlight_pattern")
        self.assertEqual(params["creation_mode"]["value"]["mime_type"], "image/png")
        pattern = cv2.imdecode(np.frombuffer(base64.b64decode(params["creation_mode"]["value"]["png_base64"]), dtype=np.uint8), cv2.IMREAD_COLOR)
        self.assertIsNotNone(pattern)
        self.assertLess(pattern.shape[1], 71)

    def test_original_brush_screenshot(self):
        result, params = self.recognize("brush_panel.png")
        self.assertIn(result["brush"]["name"], ("G筆", "G笔"))
        self.assertEqual(params["brush_size"]["value"], 5.4)
        self.assertEqual(params["opacity"]["value"], 100)
        self.assertIsNone(params["opacity"]["unit"])
        self.assertEqual(params["stabilization"]["value"], 8)
        self.assertEqual(params["antialiasing"]["value"], 2)

    def test_both_panels_same_screenshot(self):
        image = cv2.imread(str(Path(__file__).parent / 'fixtures' / 'both_panels.png'))
        h, w = image.shape[:2]
        profile = Profile('both', MonitorProfile('test','test',Rect(0,0,w,h),(w,h),96,1), [
            RoiDefinition('list','子工具列表','tool_list',Rect(10,66,331,324)),
            RoiDefinition('props','工具属性','tool_properties',Rect(10,587,331,h-587))])
        result = RecognitionPipeline(self.engine).process(CaptureFrame(2,'fixture',image),profile).result
        self.assertIn(result['brush']['name'], ('G筆','G笔'))
        self.assertEqual(result['brush']['source'], 'tool_properties_and_tool_list')
        list_text = [i['text'] for i in result['raw_ocr'] if i['roi_id']=='list']
        self.assertFalse(any('丸' in s or '粗' in s or '美術' in s for s in list_text), list_text)
        self.assertLess(result['panels'][0]['input_pixels'], result['panels'][0]['roi_pixels']/2)

    def test_same_tool_reuses_labels_but_not_old_values(self):
        image = cv2.imread(str(Path(__file__).parent / 'fixtures' / 'both_panels.png'))
        h, w = image.shape[:2]
        profile = Profile('both', MonitorProfile('test','test',Rect(0,0,w,h),(w,h),96,1), [
            RoiDefinition('list','子工具列表','tool_list',Rect(10,66,331,324)),
            RoiDefinition('props','工具属性','tool_properties',Rect(10,587,331,h-587))])
        pipeline = RecognitionPipeline(self.engine)
        first = pipeline.process(CaptureFrame(1,'fixture',image),profile).result
        self.assertEqual(first['recognition_mode'], 'full')
        second = pipeline.process(CaptureFrame(2,'fixture',image),profile).result
        self.assertEqual(second['recognition_mode'], 'values_only')
        self.assertLess(second['panels'][1]['input_pixels'], second['panels'][1]['roi_pixels'])
        changed = image.copy()
        changed[587+100:587+135, 10+247:10+310] = 50
        third = pipeline.process(CaptureFrame(3,'fixture',changed),profile).result
        self.assertEqual(third['recognition_mode'], 'values_only')
        size = next(p for p in third['brush']['properties'] if p['key']=='brush_size')
        self.assertEqual(size['value'], 'unknown')
        changed[587+95:587+125, 10+20:10+105] = 50
        fourth = pipeline.process(CaptureFrame(4,'fixture',changed),profile).result
        self.assertEqual(fourth['recognition_mode'], 'full')

    def test_solid_swatch_outputs_one_based_position(self):
        image = np.full((100, 260, 3), 50, dtype=np.uint8)
        for x in (115, 155, 195):
            cv2.rectangle(image, (x, 27), (x+29, 56), (100,100,100), 1)
        cv2.rectangle(image, (155, 27), (184, 56), (140,100,80), -1)
        self.assertEqual(_solid_swatch_index(image, [10,32,42,18]), 2)
