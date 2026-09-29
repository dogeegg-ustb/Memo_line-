import unittest

from csp_panel_validator.models import MonitorProfile, Profile, Rect, RoiDefinition
from csp_panel_validator.window_service import WindowInfo, match_windows


class WindowBindingTests(unittest.TestCase):
    def test_matching_uses_window_identity_without_persisting_hwnd(self):
        first = WindowInfo(101, "Canvas - CLIP STUDIO PAINT", "CLIPStudioPaintView", "CLIPStudioPaint.exe", 10, Rect(0, 0, 900, 700), Rect(8, 40, 892, 650), 144.0)
        second = WindowInfo(202, "Sub Tool - CLIP STUDIO PAINT", "CLIPStudioPaintView", "CLIPStudioPaint.exe", 10, Rect(10, 10, 900, 700), Rect(18, 50, 892, 650), 144.0)
        binding = first.binding()
        self.assertEqual([item.hwnd for item in match_windows(binding, [first, second])], [101])
        self.assertNotIn("hwnd", binding.to_dict())

    def test_new_profile_serializes_client_scope_and_target_size(self):
        window = WindowInfo(101, "Canvas", "CLIPStudioPaintView", "CLIPStudioPaint.exe", 10, Rect(0, 0, 900, 700), Rect(8, 40, 892, 650), 144.0)
        profile = Profile(
            "user",
            MonitorProfile("window", "Canvas", window.client_rect, window.capture_size, window.dpi, window.dpi / 96.0),
            [RoiDefinition("props", "props", "tool_properties", Rect(10, 10, 100, 100))],
            target_window=window.binding(),
        )
        data = profile.to_dict()
        self.assertEqual(data["target_window"]["capture_scope"], "client")
        self.assertEqual(data["target_window"]["capture_size"], [892, 650])
        self.assertNotIn("hwnd", data["target_window"])


if __name__ == "__main__":
    unittest.main()
