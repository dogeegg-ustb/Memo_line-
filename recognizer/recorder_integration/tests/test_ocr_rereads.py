import sys
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from recognizer_core.layer_state.layer_reader import LayerOcrReader, TextBox
from recognizer_core.panel_state import PanelStateCore
from recognizer_core.panel_state.models import OcrText, VisualState


def word(text, x, y, w=70, h=18, score=.99):
    return OcrText(text, score, [[x, y], [x + w, y], [x + w, y + h], [x, y + h]])


def entry(text, x, y, w, h, score=1.0):
    return [[x, y], [x + w, y], [x + w, y + h], [x, y + h]], text, score


class BrushNumberRereadTests(unittest.TestCase):
    PANEL = (260, 360, 3)

    def process(self, crop_items):
        panel = [word('笔刷尺寸', 10, 0), word('40.0', 250, 2, 40), word('手颤修正', 10, 40)]

        def ocr(image):
            return panel if image.shape == self.PANEL else crop_items
        core = PanelStateCore(ocr_engine=ocr)
        # A flat test image would read as greyed-out controls.
        enabled = lambda _image, items: [VisualState(enabled='enabled') for _ in items]
        with patch.object(core.visual, 'analyze', side_effect=enabled), \
                patch('recognizer_core.panel_state.visual_value_extraction._apply_disabled_styles'):
            return core.process(np.full(self.PANEL, 50, dtype=np.uint8))

    def stabilization(self, result):
        return next(p for p in result['brush']['properties'] if p['key'] == 'stabilization')

    def test_digit_missed_by_the_panel_scan_is_read_right_of_its_label(self):
        # The crop starts at 42% of the width and 0.45 label heights above it,
        # inside an 8 px border.
        result = self.process([word('6', 8 + 40, 8 + 10, 12, 18)])
        stabilization = self.stabilization(result)
        self.assertEqual((stabilization['value'], stabilization['status']), (6, 'ok'))
        self.assertEqual(stabilization['value_regions'][0]['bbox'], [191, 41, 12, 18])

    def test_reread_without_one_plain_number_stays_partial(self):
        for crop_items in ([], [word('6', 48, 18, 12), word('7', 90, 18, 12)], [word('6~', 48, 18, 24)]):
            with self.subTest(crop_items=[item.text for item in crop_items]):
                stabilization = self.stabilization(self.process(crop_items))
                self.assertEqual((stabilization['value'], stabilization['status']), ('unknown', 'partial'))


class LayerNameRereadTests(unittest.TestCase):
    def test_fragments_on_one_line_are_joined(self):
        boxes = [TextBox(text, points, score) for points, text, score in (
            entry('100 %正常', 134, 185, 102, 25), entry('图层', 135, 205, 46, 24), entry('1', 187, 207, 8, 20))]
        self.assertEqual([box.text for box in LayerOcrReader._join_line_fragments(boxes)], ['100 %正常', '图层 1'])

    def test_widely_spaced_default_name_is_recovered_on_retry(self):
        # At 175% CSP spaces "图层  1" widely: the panel scan sees only "图层"
        # on the line below the opacity, and the enlarged retry splits it.
        def ocr(image):
            if image.ndim == 3:
                return [entry('100 %正常', 134, 185, 102, 25, .96), entry('图层', 135, 205, 46, 24)]
            # Retry crop: x from 66, y from 196, enlarged twice.
            return [entry('图层', 138, 18, 92, 48), entry('1', 242, 22, 16, 40)]
        reader = LayerOcrReader(ocr_engine=ocr)
        snapshot, _ = reader.read(np.full((560, 329, 3), 50, dtype=np.uint8))
        self.assertEqual([layer.name for layer in snapshot.layers], ['图层1'])

    def test_name_at_the_retry_crop_edge_is_read_with_a_border(self):
        # Selected hidden layer at 175%: the retry crop sees only "1", and its
        # bordered version also picks up the misread opacity line above.
        def ocr(image):
            if image.ndim == 3:
                return [entry('100.%正常', 134, 185, 102, 25), entry('图层 2', 134, 204, 70, 27),
                        entry('100 %正常', 133, 235, 103, 24), entry('图层', 135, 254, 46, 26),
                        entry('纸张', 176, 304, 48, 27)]
            if image.shape[0] < 100:
                return [entry('1', 242, 25, 32, 37)]
            return [entry('100', 159, 0, 83, 53), entry('%0', 245, 6, 38, 43), entry('正吊', 277, 0, 94, 59),
                    entry('图层', 154, 36, 103, 60), entry('1', 255, 46, 42, 43)]
        snapshot, _ = LayerOcrReader(ocr_engine=ocr).read(np.full((560, 329, 3), 50, dtype=np.uint8))
        self.assertEqual([layer.name for layer in snapshot.layers], ['图层2', '图层1', '纸张'])


if __name__ == '__main__':
    unittest.main()
