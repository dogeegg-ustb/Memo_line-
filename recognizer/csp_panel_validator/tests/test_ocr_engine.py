import unittest
from types import SimpleNamespace

import numpy as np

from csp_panel_validator.ocr_engine import normalize_rapid_result


class OcrAdapterTests(unittest.TestCase):
    def test_real_numpy_boxes_are_not_boolean_tested(self):
        box = np.array([[1, 2], [30, 2], [30, 15], [1, 15]], dtype=np.float32)
        output = SimpleNamespace(boxes=np.array([box]), txts=("颜色容差",), scores=np.array([.98]))
        items = normalize_rapid_result(output)
        self.assertEqual(items[0].text, "颜色容差")
        self.assertEqual(items[0].bbox(), [1, 2, 29, 13])
        self.assertEqual(normalize_rapid_result([{"box": box, "text": "10.0", "score": .99}])[0].text, "10.0")

    def test_empty_and_invalid_boxes_do_not_crash(self):
        self.assertEqual(normalize_rapid_result(SimpleNamespace(boxes=None, txts=None)), [])
        self.assertEqual(normalize_rapid_result([[np.array([]), "bad", .9]]), [])
