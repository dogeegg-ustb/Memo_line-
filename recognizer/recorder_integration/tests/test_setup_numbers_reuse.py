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
    def dialog(self, selection):
        runtime = SimpleNamespace(settings=dict(navigatorOcrSelection=selection),
                                  engine=SimpleNamespace(regions={NAVIGATOR:[1000,500,320,200]}))
        return SimpleNamespace(runtime=runtime,selection_layout=dict(clientRect=[0,0,1920,1080],regions={},dpi=96))

    def test_changed_workspace_layout_still_probes_last_selection(self):
        selection = dict(layout=dict(clientRect=[8,8,1280,720],regions={},dpi=144),roi=[1030,560,160,100])
        self.assertEqual(SetupDialog.cached_roi(self.dialog(selection)),[1030,560,160,100])

    def test_selection_that_cannot_hold_the_numbers_is_not_probed(self):
        self.assertIsNone(SetupDialog.cached_roi(self.dialog(dict(roi=[1300,560,160,100]))))
        self.assertIsNone(SetupDialog.cached_roi(self.dialog(dict(roi=[1030,560,20,100]))))
        self.assertIsNone(SetupDialog.cached_roi(self.dialog({})))


if __name__ == "__main__":
    unittest.main()
