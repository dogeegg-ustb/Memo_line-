"""floating_panels.window_image: the window-DC copy is used only where it matches PrintWindow.
No window is read: the Win32 rendering is replaced by fixed pixel buffers."""
from __future__ import annotations

from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np

INTEGRATION = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(INTEGRATION), str(INTEGRATION.parents[1] / "CSP_Shortcut_Manager")]
import floating_panels

RECT = (-11, -11, 64, 48)  # a maximized window overhangs its monitor by its frame


class WindowImageTests(unittest.TestCase):
    def setUp(self):
        generator = np.random.default_rng(20261022)
        self.window = generator.integers(0, 256, (RECT[3], RECT[2], 4), np.uint8)
        self.window[:, :, 3] = 0
        self.window_dc = self.window.copy()
        self.calls = []

        def render(hwnd, x, y, width, height, print_window):
            self.calls.append(("PrintWindow" if print_window else "BitBlt", x, y, width, height))
            source = self.window if print_window else self.window_dc
            return np.ascontiguousarray(source[y:y + height, x:x + width]).tobytes()

        def client_rect(hwnd, rect):
            rect._obj.right, rect._obj.bottom = RECT[2] - 22, RECT[3] - 22
            return True

        def client_to_screen(hwnd, point):
            point._obj.x, point._obj.y = 0, 0
            return True
        user32 = SimpleNamespace(GetClientRect=client_rect, ClientToScreen=client_to_screen)
        for context in (patch.object(floating_panels, "_render", render),
                        patch.object(floating_panels, "window_rect", lambda hwnd: RECT),
                        patch.object(floating_panels, "_api", lambda: (user32, None)),
                        patch.dict(floating_panels._window_dc_ok, clear=True)):
            context.start()
            self.addCleanup(context.stop)

    def expected(self, x, y, w, h):
        left, top = x - RECT[0], y - RECT[1]
        return np.ascontiguousarray(self.window[top:top + h, left:left + w, 2::-1])

    def test_a_matching_window_dc_is_checked_once_then_copied_part_by_part(self):
        image, rect = floating_panels.window_image(7)
        self.assertEqual(rect, RECT)
        self.assertTrue(np.array_equal(np.asarray(image), self.expected(*RECT)))
        self.calls.clear()
        image, rect = floating_panels.window_image(7, (5, 6, 20, 10))
        self.assertEqual(rect, (5, 6, 20, 10))
        self.assertEqual(self.calls, [("BitBlt", 16, 17, 20, 10)])
        self.assertTrue(np.array_equal(np.asarray(image), self.expected(5, 6, 20, 10)))

    def test_a_window_dc_without_csps_pixels_falls_back_to_printwindow(self):
        self.window_dc[20:30, 20:40] = 0  # say, a canvas drawn outside the window's DWM surface
        image, rect = floating_panels.window_image(7, (5, 6, 20, 10))
        self.assertEqual([c[0] for c in self.calls], ["BitBlt", "PrintWindow"] * 3 + ["PrintWindow"])
        self.assertEqual(rect, (5, 6, 20, 10))
        self.assertTrue(np.array_equal(np.asarray(image), self.expected(5, 6, 20, 10)))
        self.calls.clear()
        floating_panels.window_image(7)
        self.assertEqual(self.calls, [("PrintWindow", 0, 0, RECT[2], RECT[3])])

    def test_the_invisible_frame_outside_the_client_area_does_not_count(self):
        self.window_dc[-11:] = 0  # the overhang below the work area
        floating_panels.window_image(7)
        self.assertTrue(floating_panels._window_dc_ok[7])

    def test_a_region_beyond_the_window_is_refused(self):
        with self.assertRaises(RuntimeError):
            floating_panels.window_image(7, (40, 0, 30, 10))


if __name__ == "__main__":
    unittest.main()
