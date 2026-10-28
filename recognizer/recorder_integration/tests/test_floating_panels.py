import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT), str(ROOT / 'recorder_integration'), str(ROOT.parent / 'CSP_Shortcut_Manager')]
import floating_panels
from catalog import BRUSH, COLOR, COLOR_INPUT, LAYERS, NAVIGATOR


def window(hwnd, kinds, current=None, rect=(100, 100, 400, 700), virtual=False):
    return dict(hwnd=hwnd, kinds=list(kinds), currentKind=current, rect=list(rect), onVirtualScreen=virtual)


class FloatingPanelTests(unittest.TestCase):
    def test_each_window_stands_for_the_panel_on_its_current_tab(self):
        found = floating_panels.floating_panels([
            window(1, ["palettekindnavigator"], "palettekindnavigator", (2600, 1700, 2900, 2200), True),
            window(2, ["palettekindlayerorder", "palettekindhistory"], "palettekindhistory"),
            window(3, ["palettekindtooloption"]),  # one tab: no saved selection needed
            window(4, ["palettekindcolorcircle", "palettekindcolorslider"], "palettekindcolorslider", (10, 10, 310, 410)),
        ])
        self.assertEqual(found[NAVIGATOR].rect, (2600, 1700, 300, 500))
        self.assertTrue(found[NAVIGATOR].on_virtual_screen)
        self.assertNotIn(LAYERS, found)  # the History tab is in front
        self.assertEqual(found[BRUSH].hwnd, 3)
        self.assertEqual((found[COLOR].hwnd, found[COLOR_INPUT].hwnd, found[COLOR_INPUT].rect), (4, 4, (10, 10, 300, 400)))

    def test_a_panel_in_two_windows_is_ambiguous_and_tiny_windows_are_ignored(self):
        found = floating_panels.floating_panels([
            window(1, ["palettekindnavigator"], "palettekindnavigator"),
            window(2, ["palettekindnavigator"], "palettekindnavigator"),
            window(3, ["palettekindlayerorder"], "palettekindlayerorder", (0, 0, 5, 5)),
        ])
        self.assertEqual(found, {})

    def test_without_the_panel_stash_no_window_is_known(self):
        class Missing:
            def request(self, **_):
                raise ConnectionError("面板收纳程序没有运行")
        self.assertEqual(floating_panels.read(Missing()), {})


if __name__ == '__main__':
    unittest.main()
