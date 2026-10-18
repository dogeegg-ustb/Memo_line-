import sys
import unittest
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT), str(ROOT / 'recorder_integration')]
from recognizer_core.history_state import HistoryStateCore
from recognizer_core.history_state.core import normalize

FIXTURES = ROOT / 'recognizer_core' / 'history_state' / 'tests'


def load(name):
    return cv2.imdecode(np.fromfile(str(FIXTURES / f'{name}.png'), np.uint8), cv2.IMREAD_COLOR)


class FakeOcr:
    """Counts reads; returns the text the fixture is known to show."""
    def __init__(self, texts):
        self.texts, self.calls = list(texts), 0

    def __call__(self, image, **options):
        assert options == dict(use_det=False, use_cls=False)
        self.calls += 1
        return type('Result', (), dict(txts=(self.texts.pop(0),)))()


class HistoryStateTests(unittest.TestCase):
    def test_three_strokes_active_and_six_undone(self):
        # Screenshot taken in CSP 1.5 at 150 %: three G-pen strokes drawn, six more undone.
        ocr = FakeOcr(['插画', '沾水筆/沾水筆/G筆', '沾水筆/沾水筆/G筆', '沾水筆/沾水筆/G筆', '沾水筆/沾水筆/G筆'])
        result = HistoryStateCore(ocr).process(load('history_three_active'))
        self.assertEqual(result['status'], 'ok')
        self.assertEqual((result['rowCount'], result['currentIndex'], result['undoneCount']), (10, 3, 6))
        self.assertEqual([r['undone'] for r in result['rows']], [False] * 4 + [True] * 6)
        self.assertEqual([r['selected'] for r in result['rows']], [False, False, False, True] + [False] * 6)
        self.assertEqual(result['rowPitch'], 28)
        # Ten rows, one OCR read per distinct glyph mask: the root, a plain active row, the
        # highlighted one and an undone one.
        self.assertEqual(ocr.calls, 4)
        self.assertTrue(all(r['text'] == '沾水筆/沾水筆/G筆' for r in result['rows'][1:]))

    def test_everything_undone_back_to_the_opening_state(self):
        ocr = FakeOcr(['插画', '沾水筆/沾水筆/G筆'])
        result = HistoryStateCore(ocr).process(load('history_all_undone'))
        self.assertEqual((result['status'], result['currentIndex'], result['undoneCount']), ('ok', 0, 9))
        self.assertEqual(ocr.calls, 2)

    def test_a_document_with_no_history_has_only_its_opening_row(self):
        ocr = FakeOcr(['笔触粗细测试'])
        result = HistoryStateCore(ocr).process(load('history_root_only'))
        self.assertEqual((result['status'], result['rowCount'], result['currentIndex']), ('ok', 1, 0))
        self.assertEqual(result['rows'][0]['text'], '笔触粗细测试')

    def test_a_cut_off_list_is_reported_as_truncated(self):
        image = load('history_three_active')[:200]
        result = HistoryStateCore(FakeOcr(['a'] * 10)).process(image)
        self.assertNotEqual(result['status'], 'ok')

    def test_ocr_path_separators_are_normalized(self):
        self.assertEqual(normalize('沾水筆//沾水筆／G筆 '), '沾水筆/沾水筆/G筆')


if __name__ == '__main__':
    unittest.main()
