import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import stash  # noqa: E402
from csp_windows import CspWindow  # noqa: E402
from edge_drawer import edge_hit  # noqa: E402
from virtual_display import Monitor, choose_mode, corner_position, scale_allowed, shares_edge  # noqa: E402

PHYSICAL = Monitor(r"\\.\DISPLAY1", "GPU", (0, 0, 2560, 1600), (0, 0, 2560, 1516), True, False, 168)
VIRTUAL = Monitor(r"\\.\DISPLAY2", "NVIDIA", (2560, 1600, 4480, 2680), (2560, 1600, 4480, 2680),
                  False, True, 96)
SCREENS = [PHYSICAL, VIRTUAL]
HALF = (2560, 1600, 3520, 2680)
MODES = [(1024, 768), (1280, 800), (1536, 960), (1920, 1080), (1920, 1200), (2048, 1280), (2560, 1440),
         (2560, 1600), (3072, 1920), (3840, 2160), (3840, 2400)]


def window(hwnd, left, top, width=300, height=500):
    bounds = (left, top, left + width, top + height)
    return CspWindow(hwnd, 42, "", "PaletteClass", bounds, bounds, 0x10, True)


def virtual_at(device, left, top, width=1920, height=1080):
    rect = (left, top, left + width, top + height)
    return Monitor(device, "NVIDIA", rect, rect, False, True, 96)


class CornerTests(unittest.TestCase):
    def test_window_corner_on_a_screen_corner(self):
        self.assertEqual(stash.corner_of((2260, -10, 2580, 500), PHYSICAL, 32), "top-right")
        self.assertEqual(stash.corner_of((-20, 1100, 280, 1530), PHYSICAL, 32), "bottom-left")
        self.assertEqual(stash.corner_of((10, 20, 310, 520), PHYSICAL, 32), "top-left")
        self.assertEqual(stash.corner_of((2240, 1000, 2540, 1500), PHYSICAL, 32), "bottom-right")

    def test_edges_alone_do_not_count(self):
        # CSP docks a palette dropped on the left/right edge; that must not stash it.
        self.assertIsNone(stash.corner_of((2260, 400, 2560, 900), PHYSICAL, 32))
        self.assertIsNone(stash.corner_of((0, 400, 300, 900), PHYSICAL, 32))
        self.assertIsNone(stash.corner_of((1000, 0, 1300, 500), PHYSICAL, 32))
        self.assertIsNone(stash.corner_of((1000, 300, 1300, 800), PHYSICAL, 32))

    def test_the_virtual_screen_is_the_area(self):
        self.assertEqual(stash.stash_area(SCREENS), VIRTUAL.rect)
        self.assertIsNone(stash.stash_area([PHYSICAL]))


class PackTests(unittest.TestCase):
    def test_pack_wraps_into_columns(self):
        positions = stash.pack([(300, 600), (200, 500), (250, 300)], VIRTUAL.rect, 8)
        self.assertEqual(positions, [(2568, 1608), (2876, 1608), (2876, 2116)])

    def test_pack_overflow_stays_inside_the_area(self):
        positions = stash.pack([(400, 1000)] * 4, HALF, 8)
        self.assertEqual(positions, [(2568, 1608), (2976, 1608), (2592, 1632), (3000, 1632)])

    def test_fits(self):
        self.assertTrue(stash.fits([(300, 600), (300, 600)], 316, 1224, 8))  # stacked: 8+600+8+600+8
        self.assertFalse(stash.fits([(300, 600), (300, 600)], 316, 1220, 8))  # needs a second column
        self.assertTrue(stash.fits([(300, 600), (300, 600)], 624, 1000, 8))  # two columns: 8+300+8+300+8
        self.assertFalse(stash.fits([(300, 600), (300, 600)], 620, 1000, 8))
        self.assertFalse(stash.fits([(300, 1200)], 1920, 1080, 8))  # taller than the screen
        self.assertTrue(stash.fits([], 1024, 768, 8))


class ModeTests(unittest.TestCase):
    def test_scale_needs_an_800x600_desktop(self):
        # Matches what Windows offered for the driver's modes on the development machine.
        self.assertTrue(scale_allowed((1024, 768), 125))
        self.assertFalse(scale_allowed((1024, 768), 150))
        self.assertTrue(scale_allowed((1536, 960), 150))
        self.assertFalse(scale_allowed((1536, 960), 175))
        self.assertTrue(scale_allowed((1920, 1200), 200))
        self.assertTrue(scale_allowed((2560, 1600), 250))
        self.assertFalse(scale_allowed((1280, 720), 125))

    def test_smallest_mode_that_fits_with_the_main_scale(self):
        one = lambda size: stash.fits([(450, 1000)], *size, 8)  # noqa: E731
        self.assertEqual(choose_mode(MODES, one, 150), (1920, 1080))  # 1536x960 is too short
        self.assertEqual(choose_mode(MODES, one, 100), (1920, 1080))
        small = lambda size: stash.fits([(450, 700)], *size, 8)  # noqa: E731
        self.assertEqual(choose_mode(MODES, small, 100), (1024, 768))
        self.assertEqual(choose_mode(MODES, small, 150), (1536, 960))  # 1024x768 cannot be 150 %
        many = lambda size: stash.fits([(450, 1000)] * 9, *size, 8)  # noqa: E731
        self.assertEqual(choose_mode(MODES, many, 150), (3840, 2160))

    def test_scale_wins_when_nothing_fits(self):
        huge = lambda size: False  # noqa: E731
        self.assertEqual(choose_mode(MODES, huge, 150), (3840, 2400))
        self.assertEqual(choose_mode([(1024, 768)], huge, 150), (1024, 768))


class StashTests(unittest.TestCase):
    def setUp(self):
        self.moves = []
        self.original_move = stash.move_window
        stash.move_window = lambda w, x, y: self.moves.append((w.hwnd, x, y))
        self.directory = tempfile.TemporaryDirectory()
        self.manager = stash.StashManager(Path(self.directory.name) / "state.json", 32, 8, lambda *a, **k: None)

    def tearDown(self):
        stash.move_window = self.original_move
        self.directory.cleanup()

    def test_only_windows_moved_onto_a_corner_during_the_press_are_dropped(self):
        still, moved, edge = window(1, 2260, 0), window(2, 900, 300), window(10, 900, 300)
        self.manager.begin_press([still, moved, edge])
        torn = window(3, -10, 1030)  # torn off the dock: no window at the press
        dropped = self.manager.dropped([still, window(2, 2270, -5), window(10, 2260, 500), torn], SCREENS)
        self.assertEqual([(w.hwnd, corner, prior) for w, corner, prior in dropped],
                         [(2, "top-right", moved.bounds), (3, "bottom-left", None)])

    def test_drops_are_rechecked_until_the_press_ends(self):
        self.manager.begin_press([])
        torn = window(3, 2270, -5)
        self.assertEqual(len(self.manager.dropped([torn], SCREENS)), 1)
        self.assertEqual(len(self.manager.dropped([torn], SCREENS)), 1)
        self.manager.end_press()
        self.assertEqual(self.manager.dropped([torn], SCREENS), [])

    def test_window_dragged_into_the_virtual_screen_is_adopted(self):
        self.manager.begin_press([window(4, 2000, 1200)])
        dropped = self.manager.dropped([window(4, 3700, 1700)], SCREENS)
        self.assertEqual([corner for _, corner, _ in dropped], ["virtual"])
        self.manager.stash(dropped[0][0], SCREENS, {4: dropped[0][0]}, dropped[0][2], "virtual")
        self.assertEqual(self.manager.records[4].side, "right")

    def test_no_press_snapshot_means_no_drop(self):
        self.assertEqual(self.manager.dropped([window(5, 0, 0)], SCREENS), [])

    def test_both_sides_share_one_screen_in_stash_order(self):
        right, left = window(2, 2270, -5), window(3, -10, 1030)
        windows = {2: right, 3: left}
        self.assertTrue(self.manager.stash(right, SCREENS, windows, prior=(900, 300, 1200, 800), corner="top-right"))
        self.assertTrue(self.manager.stash(left, SCREENS, windows, corner="bottom-left"))
        self.assertEqual(self.moves[-2:], [(2, 2568, 1608), (3, 2568, 2116)])
        self.assertEqual([r.hwnd for r in self.manager.on_side("left", windows)], [3])
        self.assertEqual([r.hwnd for r in self.manager.on_side("right", windows)], [2])
        self.assertEqual(self.manager.sizes(windows, [window(9, 0, 0, 100, 200)]), [(300, 500), (300, 500), (100, 200)])

    def test_restore_returns_to_pre_drag_position(self):
        moved = window(2, 2270, -5)
        self.manager.stash(moved, SCREENS, {2: moved}, prior=(900, 300, 1200, 800), corner="top-right")
        hidden = window(2, 2568, 1608, 171, 286)  # CSP rescaled it for 96 DPI
        self.assertTrue(self.manager.restore(2, SCREENS, hidden))
        self.assertEqual(self.moves[-1], (2, 900, 300))
        self.assertEqual(self.manager.records, {})

    def test_torn_off_palette_restores_inside_the_work_area(self):
        torn = window(3, -10, 1030)
        self.manager.stash(torn, SCREENS, {3: torn}, prior=None, corner="bottom-left")
        self.manager.restore(3, SCREENS, window(3, 2568, 1608, 171, 286))
        self.assertEqual(self.moves[-1], (3, 0, 1016))

    def test_drawer_brings_it_to_that_edge_clear_of_the_corner_zone(self):
        hidden = window(2, 2568, 1608, 171, 286)
        self.manager.stash(window(2, 2270, -5), SCREENS, {2: hidden}, prior=(900, 300, 1200, 800), corner="top-right")
        self.assertTrue(self.manager.bring_here(2, SCREENS, hidden, PHYSICAL, "right", 800, margin=16))
        self.assertEqual(self.moves[-1], (2, 2560 - 299 - 48, 800 - 250))
        self.assertEqual(self.manager.records, {})
        self.manager.stash(window(2, -10, 1030), SCREENS, {2: hidden}, corner="bottom-left")
        self.manager.bring_here(2, SCREENS, hidden, PHYSICAL, "left", 1500, margin=16)
        self.assertEqual(self.moves[-1], (2, 48, 1516 - 500 - 48))
        self.assertFalse(self.manager.bring_here(2, SCREENS, hidden, PHYSICAL, "left", 800))

    def test_manual_stash_uses_the_nearer_side(self):
        near_left, near_right = window(5, 100, 100), window(6, 2000, 100)
        windows = {5: near_left, 6: near_right}
        self.manager.stash(near_left, SCREENS, windows)
        self.manager.stash(near_right, SCREENS, windows)
        self.assertEqual((self.manager.records[5].side, self.manager.records[6].side), ("left", "right"))

    def test_explicit_side_wins_over_the_nearer_side(self):
        near_left = window(5, 100, 100)
        self.manager.stash(near_left, SCREENS, {5: near_left}, corner="api", side="right")
        self.assertEqual(self.manager.records[5].side, "right")

    def test_no_virtual_screen_means_no_stash(self):
        self.assertFalse(self.manager.stash(window(7, 0, 0), [PHYSICAL], {}))

    def test_relayout_gives_up_when_csp_keeps_moving_it(self):
        moved = window(8, 2270, -5)
        self.manager.stash(moved, SCREENS, {8: moved}, prior=moved.bounds, corner="top-right")
        for _ in range(10):
            self.manager.layout({8: window(8, 4000, 2000)}, SCREENS)
        self.assertEqual(len(self.moves), stash.MAX_RELAYOUTS)

    def test_state_survives_for_restore_all(self):
        moved = window(9, 2270, -5)
        self.manager.stash(moved, SCREENS, {9: moved}, prior=(900, 300, 1200, 800), corner="top-right")
        records = self.manager.load()
        self.assertEqual([(r.hwnd, r.origin, r.origin_device, r.side, r.corner) for r in records],
                         [(9, (900, 300), r"\\.\DISPLAY1", "right", "top-right")])


class LayoutTests(unittest.TestCase):
    def test_shared_edges_versus_corners(self):
        self.assertTrue(shares_edge(PHYSICAL, virtual_at("V", 2560, 0)))
        self.assertTrue(shares_edge(PHYSICAL, virtual_at("V", 300, 1600)))
        self.assertFalse(shares_edge(PHYSICAL, VIRTUAL))

    def test_virtual_screen_goes_to_the_bottom_right_corner_of_all_physical_screens(self):
        self.assertEqual(corner_position([PHYSICAL, VIRTUAL]), (2560, 1600))
        second = Monitor(r"\\.\DISPLAY4", "GPU", (2560, 0, 4480, 2160), (2560, 0, 4480, 2120), False, False, 96)
        self.assertEqual(corner_position([PHYSICAL, second, VIRTUAL]), (4480, 2160))


class EdgeTests(unittest.TestCase):
    def test_outer_left_and_right_edges(self):
        self.assertEqual(edge_hit([PHYSICAL], 0, 700, 2), ("left", PHYSICAL))
        self.assertEqual(edge_hit([PHYSICAL], 2559, 700, 2), ("right", PHYSICAL))
        self.assertIsNone(edge_hit([PHYSICAL], 3, 700, 2))
        self.assertIsNone(edge_hit([PHYSICAL], 1200, 0, 2))

    def test_edge_shared_with_another_physical_screen_is_not_an_edge(self):
        second = Monitor(r"\\.\DISPLAY4", "GPU", (2560, 0, 4480, 1080), (2560, 0, 4480, 1040), False, False, 96)
        self.assertIsNone(edge_hit([PHYSICAL, second], 2559, 500, 2))
        self.assertEqual(edge_hit([PHYSICAL, second], 2559, 1300, 2), ("right", PHYSICAL))
        self.assertEqual(edge_hit([PHYSICAL, second], 4479, 500, 2), ("right", second))


if __name__ == "__main__":
    unittest.main()
