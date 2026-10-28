import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
sys.path.insert(0,str(Path(__file__).resolve().parents[3]/'CSP_Shortcut_Manager'))

from catalog import NAVIGATOR
from setup_ui import SetupDialog


class CachedNumbersSelectionTests(unittest.TestCase):
    def dialog(self, selection, source=("dock", [1000,500,320,200])):
        runtime = SimpleNamespace(settings=dict(navigatorOcrSelection=selection), navigator_source=lambda: source)
        return SimpleNamespace(runtime=runtime,navigator_rect=SetupDialog.navigator_rect,
                               selection_layout=dict(clientRect=[0,0,1920,1080],regions={},dpi=96))

    def test_changed_workspace_layout_still_probes_last_selection(self):
        # Settings from before the selection was kept relative to the navigator.
        selection = dict(layout=dict(clientRect=[8,8,1280,720],regions={},dpi=144),roi=[1030,560,160,100])
        self.assertEqual(SetupDialog.cached_relative(self.dialog(selection)),[30,60,160,100])

    def test_the_selection_follows_the_navigator_wherever_it_is(self):
        selection = dict(relative=[30,60,160,100], navigatorSize=[320,200])
        moved = ("dock", [2200,100,320,200])
        self.assertEqual(SetupDialog.cached_relative(self.dialog(selection, moved)),[30,60,160,100])
        floating = ("window", SimpleNamespace(hwnd=7, rect=(3000,1700,320,220)))
        self.assertEqual(SetupDialog.cached_relative(self.dialog(selection, floating)),[30,60,160,100])
        smaller = ("window", SimpleNamespace(hwnd=7, rect=(3000,1700,180,140)))
        self.assertIsNone(SetupDialog.cached_relative(self.dialog(selection, smaller)))

    def test_selection_that_cannot_hold_the_numbers_is_not_probed(self):
        self.assertIsNone(SetupDialog.cached_relative(self.dialog(dict(roi=[1300,560,160,100]))))
        self.assertIsNone(SetupDialog.cached_relative(self.dialog(dict(roi=[1030,560,20,100]))))
        self.assertIsNone(SetupDialog.cached_relative(self.dialog({})))
        self.assertIsNone(SetupDialog.cached_relative(self.dialog(dict(relative=[30,60,160,100]), None)))


if __name__ == "__main__":
    unittest.main()
