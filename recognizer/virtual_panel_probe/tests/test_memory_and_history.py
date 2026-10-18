"""Remembered palettes across restarts, and the History palette stretched to list every undo step."""
import dataclasses
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import history_size  # noqa: E402
import panel_host  # noqa: E402
import stash  # noqa: E402
import virtual_display  # noqa: E402
from test_interface import FakeRoot, PHYSICAL, SMALL, sync_job, window  # noqa: E402
from workspace import Workspace  # noqa: E402

HISTORY = "palettekindhistory"


class Base(unittest.TestCase):
    def setUp(self):
        self.moves, self.resizes = [], []
        for module, name, fake in ((stash, "move_window", lambda w, x, y: self.moves.append((w.hwnd, x, y))),
                                   (stash, "resize_window", lambda w, width, height: self.resizes.append((w.hwnd, width, height))),
                                   (panel_host, "resize_window", lambda w, width, height: self.resizes.append((w.hwnd, width, height))),
                                   (history_size, "undo_count", lambda: (30, "test")),
                                   (virtual_display, "provide", self.provide)):
            original = getattr(module, name)
            setattr(module, name, fake)
            self.addCleanup(setattr, module, name, original)
        self.asked = []
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.state = Path(directory.name) / "state.json"
        self.host = self.make_host()

    def provide(self, fits, hardware, match_scale):
        self.asked.append((fits((1536, 960)), fits((1536, 6144))))
        return SMALL, ["接入虚拟屏"]

    def make_host(self):
        host = panel_host.Host.__new__(panel_host.Host)
        host.settings = dict(panel_host.DEFAULTS)
        host.closing = host.display_busy = host.provide_again = host.virtual_active = host.any_button = False
        host.virtual_error = host.empty_since = None
        host.virtual_devices, host.pending = set(), {}
        host.display_job = sync_job
        host.hardware = tuple(host.settings["virtualHardwareIds"])
        host.history_rows, host.history_resizes, host.auto_tried = {11: (33, 28)}, {}, set()
        host.main = window(1, 0, 0, 2560, 1600)
        host.root = FakeRoot()
        host.scale = 1.5
        host.workspace = Workspace()
        host.screens = [PHYSICAL]
        host.windows = {11: window(11, 100, 100, 300, 500), 12: window(12, 1800, 200)}
        host.labels = {11: panel_host.WindowLabel((HISTORY,), HISTORY, "窗口标题"),
                       12: panel_host.WindowLabel(("palettekindnavigator",), "palettekindnavigator", "工作区浮动窗口1")}
        host.stash = stash.StashManager(self.state, 32, 8, lambda *a, **k: None)
        host.stash.records = {r.hwnd: r for r in host.stash.load()}
        host.call = lambda function: function()
        host.log = lambda *a, **k: None
        return host

    def on_virtual(self, hwnd, height=None):
        """The fake windows do not move by themselves; put one where the stash moved it."""
        x, y = next((x, y) for h, x, y in reversed(self.moves) if h == hwnd)
        old = self.host.windows[hwnd]
        self.host.windows[hwnd] = window(hwnd, x, y, old.size[0], height or old.size[1])


class HistoryHeightTests(Base):
    def test_the_screen_is_sized_for_every_undo_step(self):
        # 30 steps: 33 + 31 * 28 + 14 = 915, which 1536x960 holds; 200 steps (5675) would not.
        self.assertEqual(self.host.planned_size(self.host.windows[11]), (300, 915))
        self.host.request_stash(self.host.windows[11], "top-left")
        self.assertEqual(self.asked, [(True, True)])
        history_size.undo_count = lambda: (200, "test")
        self.assertEqual(self.host.planned_size(self.host.windows[11]), (300, 5675))

    def test_the_history_is_stretched_on_the_virtual_screen_and_shrunk_when_it_leaves(self):
        self.host.request_stash(self.host.windows[11], "top-left")
        self.on_virtual(11)
        self.host.fit_history()
        self.assertEqual(self.resizes[-1], (11, 300, 915))
        self.assertEqual(self.host.stash.records[11].size, (300, 500))
        self.on_virtual(11, 915)
        self.host.fit_history()
        self.assertEqual(len(self.resizes), 1)  # already at its height
        self.host.restore(11)
        self.assertEqual(self.resizes[-1], (11, 300, 500))
        self.assertEqual(self.moves[-1], (11, 100, 100))

    def test_never_taller_than_the_virtual_screen(self):
        history_size.undo_count = lambda: (200, "test")
        self.host.request_stash(self.host.windows[11], "top-left")
        self.on_virtual(11)
        self.host.fit_history()
        self.assertEqual(self.resizes[-1], (11, 300, 960 - 16))

    def test_a_palette_that_is_not_the_history_keeps_its_size(self):
        self.host.request_stash(self.host.windows[12], "top-right")
        self.on_virtual(12)
        self.host.fit_history()
        self.assertEqual(self.resizes, [])


class RememberedTests(Base):
    def test_a_stashed_palette_goes_back_after_a_restart(self):
        self.host.request_stash(self.host.windows[12], "top-right")
        self.on_virtual(12)
        # Memoline exits: palettes come back for CSP, but stay remembered.
        self.host.restore_everything(forget=False)
        self.assertEqual(self.host.stash.records, {})
        restarted = self.make_host()
        restarted.windows[12] = window(99, 1800, 200)  # CSP made a new window for the same palette
        restarted.windows = {11: restarted.windows[11], 99: restarted.windows[12]}
        restarted.labels[99] = restarted.labels.pop(12)
        restarted.stash_remembered()
        self.assertEqual(set(restarted.stash.records), {99})
        self.assertEqual(restarted.stash.records[99].side, "right")
        restarted.stash_remembered()  # tried once per window
        self.assertEqual(len([m for m in self.moves if m[0] == 99]), 1)

    def test_a_palette_the_user_took_back_is_not_stashed_again(self):
        self.host.request_stash(self.host.windows[12], "top-right")
        self.on_virtual(12)
        self.host.restore(12)
        restarted = self.make_host()
        restarted.stash_remembered()
        self.assertEqual(restarted.stash.records, {})
        self.assertEqual(restarted.stash.remembered, [])

    def test_drawer_pick_forgets_too(self):
        self.host.request_stash(self.host.windows[12], "top-left")
        self.on_virtual(12)
        self.host.bring_from_drawer(12, "left", PHYSICAL, 600)
        self.assertEqual(self.host.stash.remembered, [])

    def test_nothing_is_stashed_while_a_button_is_held_or_csp_is_busy(self):
        self.host.request_stash(self.host.windows[12], "top-right")
        self.on_virtual(12)
        self.host.restore_everything(forget=False)
        restarted = self.make_host()
        restarted.any_button = True
        restarted.stash_remembered()
        restarted.any_button = False
        restarted.main = dataclasses.replace(restarted.main, enabled=False)  # a modal CSP dialog is open
        restarted.stash_remembered()
        self.assertEqual(restarted.stash.records, {})

    def test_unknown_palettes_are_not_stashed_by_the_corner_gesture(self):
        self.host.labels[12] = panel_host.WindowLabel((), None, "未识别")
        self.host.stash.dropped = lambda windows, screens: [(self.host.windows[12], "top-right", None)]
        self.host.stash.press_id, self.host.stash.press_bounds = 1, {}
        self.host.refresh = lambda: None
        self.host.corner_enabled = type("Var", (), dict(get=lambda self: True))()
        self.host.check_drop(1, True)
        self.assertEqual(self.host.pending, {})
        self.assertEqual(self.host.stash.records, {})


if __name__ == "__main__":
    unittest.main()
