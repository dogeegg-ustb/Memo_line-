import unittest
from pathlib import Path

from csp_panel_validator.models import MonitorProfile, Profile, Rect, RoiDefinition
from csp_panel_validator.profile_store import ProfileError, ProfileStore


class ProfileTests(unittest.TestCase):
    def test_save_load_and_bounds(self):
        root = Path(__file__).resolve().parents[1] / "tests" / "_profile_store_runtime"
        path = root / "test.json"
        store = ProfileStore(root)
        profile = Profile("test", MonitorProfile("id", "display", Rect(-1920, 0, 1920, 1080), (1920, 1080), 96, 1, 0), [RoiDefinition("props", "props", "tool_properties", Rect(10, 10, 100, 100))])
        try:
            path = store.save(profile, path)
        except PermissionError as exc:
            self.skipTest(f"当前沙箱禁止运行时文件写入：{exc}")
        loaded = store.load(path)
        self.assertEqual(loaded.monitor.geometry.x, -1920)
        self.assertEqual(loaded.rois[0].rect.width, 100)
        try:
            path.unlink()
            root.rmdir()
        except OSError:
            pass

    def test_invalid_roi_is_rejected(self):
        profile = Profile("bad", MonitorProfile("id", "display", Rect(0, 0, 100, 100), (100, 100), 96, 1, 0), [RoiDefinition("props", "props", "tool_properties", Rect(90, 90, 20, 20))])
        with self.assertRaises(ProfileError):
            ProfileStore.validate(profile)


if __name__ == "__main__":
    unittest.main()
