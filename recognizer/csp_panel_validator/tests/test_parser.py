import unittest

from csp_panel_validator.models import OcrText, VisualState
from csp_panel_validator.panel_parser import PanelParser
from csp_panel_validator.state_assembler import StateAssembler


def text(value, x, y, w=60, h=16, score=0.98):
    return OcrText(value, score, [[x, y], [x + w, y], [x + w, y + h], [x, y + h]])


class ParserTests(unittest.TestCase):
    def test_rebuilds_parameters_from_current_frame(self):
        parser = PanelParser()
        items = [text("笔刷尺寸 5.4", 10, 20, 130), text("不透明度 80%", 10, 55, 150)]
        recognition = parser.parse("props", "tool_properties", items, [VisualState(), VisualState()])
        self.assertEqual([item["key"] for item in recognition.parameters], ["brush_size", "opacity"])
        new_items = [text("消除锯齿 无 弱 强", 10, 20, 160)]
        new_recognition = parser.parse("props", "tool_properties", new_items, [VisualState()])
        self.assertEqual([item["key"] for item in new_recognition.parameters], ["antialiasing"])
        self.assertEqual(new_recognition.parameters[0]["value"], "unknown")
        self.assertFalse(any(item["key"] == "brush_size" for item in new_recognition.parameters))

        reordered = parser.parse("props", "tool_properties", [text("不透明度 80%", 10, 20, 150), text("笔刷尺寸 2.0", 10, 55, 130)], [VisualState(), VisualState()])
        self.assertEqual([item["key"] for item in reordered.parameters], ["opacity", "brush_size"])

    def test_selected_enum_uses_visual_evidence(self):
        parser = PanelParser()
        items = [text("消除锯齿", 10, 20, 60), text("无", 80, 20, 20), text("弱", 110, 20, 20), text("强", 140, 20, 20)]
        states = [VisualState(), VisualState("not_selected", 0.05), VisualState("selected", 0.8), VisualState("not_selected", 0.05)]
        recognition = parser.parse("props", "tool_properties", items, states)
        self.assertEqual(recognition.parameters[0]["value"], "弱")

    def test_conflict_is_ambiguous(self):
        parser = PanelParser()
        a = parser.parse("props", "tool_properties", [text("G笔", 5, 5)], [VisualState()])
        b = parser.parse("list", "tool_list", [text("铅笔", 5, 5)], [VisualState("selected", 0.9)])
        result = StateAssembler().assemble(1, "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", [a, b], {"capture": 1, "ocr": 2, "parse": 1, "total": 4})
        self.assertEqual(result["status"], "ambiguous")
        self.assertEqual(result["brush"]["status"], "ambiguous")


if __name__ == "__main__":
    unittest.main()
