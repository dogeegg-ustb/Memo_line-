import unittest
import struct

import numpy as np

from csp_panel_validator.models import CaptureFrame, MonitorProfile, Profile, Rect, RoiDefinition, OcrText, VisualState
from csp_panel_validator.recognition_pipeline import RecognitionPipeline
from csp_panel_validator.panel_parser import PanelParser
from csp_panel_validator.property_catalog import get_catalog
from csp_panel_validator.ocr_engine import RapidOcrEngine
from scripts.import_csp_resources import strings


class SelectedCatalogTests(unittest.TestCase):
    def fixture(self, y=20, highlighted=True):
        image = np.full((100, 240, 3), 50, dtype=np.uint8)
        if highlighted:
            image[y:y+24, :120] = (125, 95, 80)
        profile = Profile('test', MonitorProfile('test', 'test', Rect(0,0,240,100), (240,100),96,1), [
            RoiDefinition('list', 'list', 'tool_list', Rect(0,0,120,100)),
            RoiDefinition('props', 'props', 'tool_properties', Rect(120,0,120,100))])
        return image, profile

    def test_only_dynamic_highlight_pixels_reach_ocr(self):
        for y in (20, 60):
            image, profile = self.fixture(y)
            seen = []
            class Spy:
                def recognize(self, crop):
                    seen.append(crop.copy())
                    return [OcrText('G笔' if crop.shape[0] == 24 else '“G笔”工具属性', .99, [[2,2],[100,2],[100,18],[2,18]])]
            result = RecognitionPipeline(Spy()).process(CaptureFrame(99,'now',image), profile).result
            self.assertEqual([i.shape for i in seen], [(24,120,3),(100,120,3)])
            self.assertTrue(np.all(seen[0] == (125,95,80)))
            self.assertEqual(result['raw_ocr'][0]['bbox'][1], y+2)
            self.assertEqual([p['frame_id'] for p in result['panels']], [99,99])
            self.assertEqual(result['brush']['source'], 'tool_properties_and_tool_list')

    def test_no_highlight_does_not_ocr_list(self):
        image, profile = self.fixture(highlighted=False)
        calls = []
        class Spy:
            def recognize(self, crop):
                calls.append(crop.shape)
                return [OcrText('笔刷尺寸 5.4', .99, [[2,2],[100,2],[100,18],[2,18]])]
        result = RecognitionPipeline(Spy()).process(CaptureFrame(1,'now',image),profile).result
        self.assertEqual(len(calls), 1)
        self.assertEqual(result['panels'][0]['status'], 'no_highlight')
        self.assertEqual(result['brush']['properties'][0]['value'], 5.4)

    def test_one_panel_failure_preserves_other(self):
        image, profile = self.fixture()
        class Spy:
            def recognize(self, crop):
                if crop.shape[0] == 24:
                    raise ValueError('list failure')
                return [OcrText('笔刷尺寸 5.4', .99, [[2,2],[100,2],[100,18],[2,18]])]
        result = RecognitionPipeline(Spy()).process(CaptureFrame(1,'now',image),profile).result
        self.assertEqual(result['brush']['properties'][0]['value'], 5.4)
        self.assertEqual(result['status'],'partial')

    def test_unknown_numeric_label_does_not_invent_property(self):
        item = OcrText('不存在的参数 17', .99, [[0,0],[120,0],[120,20],[0,20]])
        result = PanelParser().parse('p','tool_properties',[item],[VisualState()])
        self.assertFalse(result.parameters)
        self.assertTrue(result.unresolved)

    def test_installed_alias_and_provenance(self):
        catalog = get_catalog()
        match = catalog.match('抖動修正 8', value_kind='number')
        self.assertEqual(match['definition']['key'],'stabilization')
        self.assertTrue(any(s.get('resource_key') for s in match['definition']['sources']))
        self.assertIsNone(match['definition']['internal_csp_id'])
        self.assertGreater(catalog.metadata['installed_resources']['ui_string_count'], 500)

    def test_resource_parser_validates_absolute_bounds(self):
        text = '筆刷尺寸'.encode('utf-8')
        leaf = struct.pack('>I',len(text))+text
        blob = struct.pack('>IIII',1,7,16,len(leaf))+leaf
        self.assertEqual(strings(blob), {(7,):'筆刷尺寸'})
        self.assertEqual(strings(blob[:-1]), {})

    def test_atlas_excludes_unselected_pixels_and_restores_boxes(self):
        image = np.full((100,120,3), 250, dtype=np.uint8)
        image[20:44] = (125,95,80)
        seen = []
        engine = RapidOcrEngine.__new__(RapidOcrEngine)
        def recognize(atlas):
            seen.append(atlas)
            return [OcrText('G笔', .99, [[18,18],[90,18],[90,32],[18,32]])]
        engine.recognize = recognize
        items = engine.recognize_selected(image, [Rect(0,20,120,24)])
        self.assertFalse(np.any(seen[0] == 250))
        self.assertEqual(items[0].bbox(), [2,22,72,14])


if __name__ == '__main__':
    unittest.main()
