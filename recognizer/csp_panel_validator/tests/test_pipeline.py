import unittest

import numpy as np

from csp_panel_validator.models import CaptureFrame, MonitorProfile, OcrText, Profile, Rect, RoiDefinition
from csp_panel_validator.recognition_pipeline import RecognitionPipeline


class FakeSidecar:
    def recognize(self, image, roi_id):
        if roi_id == "props":
            return [
                OcrText("笔刷尺寸 5.4", 0.99, [[5, 5], [120, 5], [120, 20], [5, 20]]),
                OcrText("“自定义笔刷”工具属性", 0.95, [[5, 40], [90, 40], [90, 55], [5, 55]]),
            ]
        return [OcrText("自定义笔刷", 0.96, [[5, 5], [90, 5], [90, 20], [5, 20]])]


class PipelineTests(unittest.TestCase):
    def test_replay_pipeline_is_current_frame_complete(self):
        profile = Profile(
            "test",
            MonitorProfile("screen", "Display", Rect(-1920, 0, 1920, 1080), (200, 100), 96, 1, 0),
            [RoiDefinition("props", "props", "tool_properties", Rect(0, 0, 100, 80)), RoiDefinition("list", "list", "tool_list", Rect(100, 0, 100, 80))],
        )
        outcome = RecognitionPipeline(object()).process(CaptureFrame(7, "2026-01-01T00:00:00Z", np.zeros((100, 200, 3), dtype=np.uint8)), profile, sidecar=FakeSidecar())
        self.assertEqual(outcome.result["frame_id"], 7)
        self.assertEqual(outcome.result["brush"]["name"], "自定义笔刷")
        self.assertEqual(outcome.result["brush"]["properties"][0]["key"], "brush_size")
        self.assertIn("bbox", outcome.result["brush"]["properties"][0]["evidence"][0])


if __name__ == "__main__":
    unittest.main()
