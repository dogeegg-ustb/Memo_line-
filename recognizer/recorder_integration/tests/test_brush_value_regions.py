"""Production brush core: values retain locations while labels never leave it."""
from __future__ import annotations

import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from recognizer_core.panel_state import PanelStateCore
from recognizer_core.panel_state.models import OcrText, VisualState
from recognizer_core.panel_state.ocr_engine import OcrUnavailable


def word(text, x, y, w=70, h=18):
    return OcrText(text, .99, [[x,y], [x+w,y], [x+w,y+h], [x,y+h]])


class BrushValueRegionsTests(unittest.TestCase):
    def process(self, items, states=None, image=None):
        image = np.full((260, 360, 3), 50, dtype=np.uint8) if image is None else image
        core = PanelStateCore(ocr_engine=lambda _: items)
        if states is None:
            return core.process(image)
        with patch.object(core.visual, 'analyze', return_value=states):
            return core.process(image)

    def assertOnlyValueBoxes(self, result, expected):
        found = []
        def visit(value):
            if isinstance(value, dict):
                self.assertFalse(any(key.startswith('_') for key in value))
                for key, item in value.items():
                    if key == 'bbox':
                        found.append(item)
                    self.assertNotIn(key, ('box', 'image_bbox', 'ocr_regions'))
                    visit(item)
            elif isinstance(value, list):
                for item in value:
                    visit(item)
        visit(result)
        self.assertEqual({tuple(box) for box in found}, {tuple(box) for box in expected})
        json.dumps(result)  # No internal OCR objects can leak into the API.

    def test_number_text_and_checkbox_export_only_their_value_boxes(self):
        items = [word('G笔工具属性',10,3), word('笔刷尺寸',10,40), word('5.4',210,40,40),
                 word('混合模式',10,80), word('正常',210,80,50), word('取样连续的像素',35,120)]
        states = [VisualState() for _ in items]
        states[-1] = VisualState(checkbox='checked', evidence=[
            dict(type='checkbox', bbox=[10,122,16,16], reason='勾选图标')])
        result = self.process(items, states)
        self.assertEqual(result['schema_version'],4)
        regions = result['value_regions']
        self.assertEqual([r['category'] for r in regions],['number','text','icon'])
        self.assertEqual([r['value'] for r in regions],[5.4,'正常','checked'])
        self.assertOnlyValueBoxes(result,[[210,40,40,18],[210,80,50,18],[10,122,16,16]])
        self.assertEqual([r['text'] for r in result['raw_ocr']],['5.4','正常'])

    def test_inline_label_and_number_keep_value_without_exporting_the_mixed_box(self):
        result = self.process([word('G笔工具属性',10,3), word('笔刷尺寸 5.4',10,40,220)],
                              [VisualState(),VisualState()])
        parameter = result['brush']['properties'][0]
        self.assertEqual(parameter['value'],5.4)
        self.assertEqual(parameter['value_category'],'number')
        self.assertEqual(parameter['value_location_status'],'unresolved')
        self.assertEqual(result['value_regions'],[])
        self.assertEqual(result['raw_ocr'],[])
        self.assertOnlyValueBoxes(result,[])

    def test_inline_text_unknown_labels_and_titles_do_not_leak_positions(self):
        result = self.process([word('G笔工具属性',10,3), word('混合模式 正常',10,40,220),
                               word('没有匹配的属性标签',10,100,180)],
                              [VisualState(),VisualState(),VisualState()])
        self.assertEqual(result['brush']['name'],'G笔')
        self.assertEqual(result['brush']['properties'][0]['value'],'正常')
        self.assertTrue(result['unresolved'])
        self.assertOnlyValueBoxes(result,[])

    def test_highlighted_text_is_text_even_when_semantic_value_is_an_index(self):
        items = [word('G笔工具属性',10,3), word('消除锯齿',10,40),
                 word('无',140,40,20),word('弱',185,40,20),word('强',230,40,20)]
        states = [VisualState() for _ in items]
        states[3] = VisualState(selected='selected',selected_score=.8)
        result = self.process(items,states)
        parameter = result['brush']['properties'][0]
        self.assertEqual(parameter['type'],'highlight_index')
        self.assertEqual(parameter['value'],2)
        self.assertEqual(parameter['value_category'],'text')
        self.assertOnlyValueBoxes(result,[[185,40,20,18]])
        self.assertEqual(result['raw_ocr'][0]['text'],'弱')
        self.assertEqual(result['value_regions'][0]['text'],'弱')

    def test_a_numeric_box_overlapping_the_property_name_is_not_exported(self):
        items = [word('G笔工具属性',10,3),word('笔刷尺寸',10,40,100),word('5.4',80,40,50)]
        result = self.process(items,[VisualState(),VisualState(),VisualState()])
        self.assertEqual(result['brush']['properties'][0]['value'],5.4)
        self.assertEqual(result['brush']['properties'][0]['value_location_status'],'unresolved')
        self.assertOnlyValueBoxes(result,[])

    def test_missing_selected_antialias_word_is_recovered_from_the_anchored_grid(self):
        image=np.full((160,360,3),50,dtype=np.uint8)
        image[55:90,235:278]=(180,130,80)
        items=[word('G笔工具属性',10,3),word('消除锯齿',10,62,90,18),
               word('无',135,62,20),word('弱',190,62,20),word('强',300,62,20)]
        result=self.process(items,[VisualState() for _ in items],image)
        parameter=result['brush']['properties'][0]
        self.assertEqual(parameter['value'],3)
        self.assertEqual(parameter['observed']['option_text'],'中')
        self.assertEqual(parameter['status'],'ok')
        self.assertEqual(parameter['value_category'],'text')
        self.assertEqual(parameter['value_regions'][0]['source'],'image')

    def test_missing_preceding_word_does_not_renumber_a_recognized_selected_option(self):
        items=[word('G笔工具属性',10,3),word('消除锯齿',10,62,90,18),
               word('无',135,62,20),word('弱',190,62,20),word('强',300,62,20)]
        states=[VisualState() for _ in items]
        states[-1]=VisualState(selected='selected',selected_score=.8)
        parameter=self.process(items,states)['brush']['properties'][0]
        self.assertEqual(parameter['value'],4)

    def test_multiple_highlighted_enum_cells_leave_the_value_unconfirmed(self):
        image=np.full((160,360,3),50,dtype=np.uint8)
        image[55:90,180:223]=(180,130,80)
        image[55:90,235:278]=(180,130,80)
        items=[word('G笔工具属性',10,3),word('消除锯齿',10,62,90,18),
               word('无',135,62,20),word('弱',190,62,20),word('强',300,62,20)]
        result=self.process(items,[VisualState() for _ in items],image)
        self.assertEqual(result['brush']['properties'][0]['status'],'partial')

    def test_tip_and_stroke_color_jitter_numbers_use_the_visible_parent_context(self):
        items=[word('G笔工具属性',10,3),word('Change brush tip color',10,35,180),
               word('色相',10,65),word('8',230,65,20),
               word('Randomize per stroke',10,105,180),word('色相',10,140),word('15',230,140,25)]
        result=self.process(items,[VisualState() for _ in items])
        properties={p['key']:p for p in result['brush']['properties']}
        self.assertEqual(properties['color_jitter.tip_hue']['value'],8)
        self.assertEqual(properties['color_jitter.stroke_hue']['value'],15)
        self.assertEqual(properties['color_jitter.tip_hue']['value_regions'][0]['bbox'],[230,65,20,18])
        unknown=self.process([word('G笔工具属性',10,3),word('色相',10,65),word('8',230,65,20)])
        self.assertFalse(any(p['key'].startswith('color_jitter.') for p in unknown['brush']['properties']))

    def test_hardness_is_a_filled_prefix_not_a_single_selected_rectangle(self):
        image=np.full((160,360,3),50,dtype=np.uint8)
        for i,x in enumerate((100,148,196,244,292)):
            cv2.rectangle(image,(x,58),(x+44,90),(110,110,110),1)
            if i<3:
                cv2.rectangle(image,(x+1,59),(x+43,89),(180,130,80),-1)
        items=[word('G笔工具属性',10,3),word('硬度',20,65,50,20)]
        result=self.process(items,[VisualState() for _ in items],image)
        parameter=result['brush']['properties'][0]
        self.assertEqual(parameter['value'],3)
        self.assertEqual(parameter['observed']['control_kind'],'cumulative_indicator')
        self.assertEqual(parameter['value_category'],'icon')

    def test_selected_swatch_is_icon_with_the_actual_cell_box(self):
        image = np.full((160,320,3),50,dtype=np.uint8)
        for x in (150,195,240):
            cv2.rectangle(image,(x,60),(x+34,88),(90,90,90),1)
        cv2.rectangle(image,(196,61),(228,87),(200,130,40),-1)
        result = self.process([word('G笔工具属性',10,3),word('消除锯齿',10,62,85,18)],
                              [VisualState(),VisualState()],image)
        region = result['value_regions'][0]
        self.assertEqual(region['category'],'icon')
        self.assertEqual(region['value'],2)
        self.assertEqual(region['bbox'],[196,61,33,27])
        self.assertEqual(result['raw_ocr'],[])
        self.assertOnlyValueBoxes(result,[region['bbox']])

    def test_selected_pattern_exports_an_icon_without_the_label_box(self):
        image = np.full((160,320,3),50,dtype=np.uint8)
        cv2.rectangle(image,(155,60),(210,92),(200,130,40),-1)
        cv2.rectangle(image,(173,68),(196,84),(240,240,240),-1)
        result = self.process([word('选区工具属性',10,3),word('创建方式',10,62,85,18)],
                              [VisualState(),VisualState()],image)
        region = result['value_regions'][0]
        self.assertEqual(region['category'],'icon')
        self.assertEqual(region['value']['mime_type'],'image/png')
        self.assertTrue(region['value']['png_base64'])
        self.assertGreater(region['bbox'][0],155)
        self.assertOnlyValueBoxes(result,[region['bbox']])

    def test_disabled_number_still_has_a_value_location(self):
        items = [word('G笔工具属性',10,3),word('笔刷尺寸',10,40),word('8',210,40,20)]
        result = self.process(items,[VisualState(),VisualState(enabled='disabled'),VisualState(enabled='disabled')])
        region = result['value_regions'][0]
        self.assertEqual(region['status'],'disabled')
        self.assertEqual(region['category'],'number')
        self.assertOnlyValueBoxes(result,[[210,40,20,18]])

    def test_checkbox_is_not_borrowed_by_an_adjacent_property(self):
        items = [word('选区工具属性',10,3),word('取样连续的像素',30,40,110),word('闭合间隙',190,40,65)]
        states = [VisualState(),VisualState(checkbox='checked',evidence=[dict(type='checkbox',bbox=[10,40,16,16])]),VisualState()]
        result = self.process(items,states)
        self.assertEqual(result['brush']['properties'][0]['value'],'checked')
        self.assertEqual(result['brush']['properties'][1]['value'],'unknown')
        self.assertOnlyValueBoxes(result,[[10,40,16,16]])

    def test_empty_and_failed_ocr_publish_no_positions(self):
        result = self.process([])
        self.assertEqual(result['value_regions'],[])
        self.assertOnlyValueBoxes(result,[])
        def fail(_):
            raise OcrUnavailable('fixture failure')
        result = PanelStateCore(ocr_engine=fail).process(np.zeros((40,100,3),dtype=np.uint8))
        self.assertEqual(result['status'],'unavailable')
        self.assertEqual(result['raw_ocr'],[])
        self.assertOnlyValueBoxes(result,[])

    def test_recorded_ocr_on_real_panel_pixels_keeps_numeric_text_and_icon_values(self):
        fixture_root = ROOT / 'csp_panel_validator'
        for image_name, ocr_name in [('brush_panel.png','both_panels.actual.json'),
                                     ('selection_panel.png','selection_panel.actual.json')]:
            with self.subTest(panel=image_name):
                recorded = json.loads((fixture_root / 'examples' / ocr_name).read_text(encoding='utf-8'))
                entries = [entry for entry in recorded['raw_ocr'] if entry['roi_id']=='props']
                items = [word(entry['text'],*entry['bbox']) for entry in entries]
                image = cv2.imread(str(fixture_root / 'tests' / 'fixtures' / image_name))
                result = self.process(items,image=image)
                properties = {p['key']:p for p in result['brush']['properties']}
                if image_name == 'brush_panel.png':
                    self.assertEqual(properties['brush_size']['value'],5.4)
                    self.assertEqual(properties['opacity']['value'],100)
                    self.assertEqual(properties['stabilization']['value'],8)
                    self.assertEqual(properties['antialiasing']['value_category'],'text')
                    self.assertEqual(properties['antialiasing']['value'],2)
                else:
                    self.assertEqual(properties['color_tolerance']['value'],10)
                    self.assertEqual(properties['contiguous_pixels']['value'],'checked')
                    self.assertEqual(properties['creation_mode']['value_category'],'icon')
                    self.assertTrue(properties['creation_mode']['value_regions'])
                self.assertTrue(result['value_regions'])
                value_boxes = [r['bbox'] for r in result['value_regions']]
                self.assertOnlyValueBoxes(result,value_boxes)
                labels = {tuple(entry['bbox']) for entry in entries
                          if not entry['text'].replace('.','').isdigit()
                          and entry['text'] not in {'无','弱','中','强'}}
                self.assertTrue(all(tuple(box) not in labels for box in value_boxes))


if __name__ == '__main__':
    unittest.main()
