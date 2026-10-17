import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT)]
from csp_workspace_overlay import panel_rectangles, same_restore_rect

SEPARATOR, HEADER = bytes((45, 45, 45, 0)), bytes((51, 51, 51, 0))


def ratio(numerator, denominator):
    return {"_type": "rational", "numerator": numerator, "denominator": denominator}


def place(width, height, scale):
    return {"dockplacebasesize": {"_type": "size", "width": width, "height": height},
            "dockplacebasexscale": scale, "dockplacebaseyscale": scale}


def booth(*kinds, current=0):
    return {"visible": 1, "currentindex": current, "subviews": {"_items": [{"_type": k} for k in kinds]}}


def column(booths, heights, width, scale):
    return {"visible": 1, "subviews": {"_items": [{
        "subviews": {"_items": booths},
        "dockplaces": {"_items": [place(width, h, scale) for h in heights]}}]}}


def dock_tree(scale=ratio(3, 2)):
    """The 150% workspace measured on a 2560x1527 maximized client."""
    left = column([booth("palettekindsubtool"), booth("palettekindtooloption", "palettekindbrushsize"),
                   booth("palettekindcolorcircle", "palettekindcolorslider", current=1)], [422, 520, 483], 315, scale)
    right = column([booth("palettekindnavigator"), booth("palettekindproperty"), booth("palettekindlayerorder")],
                   [646, 71, 708], 363, scale)
    tools = column([booth("palettekindtool")], [873], 63, scale)
    center = {"subviews": {"_items": [{}, {}, {"visible": 0}]},
              "dockplaces": {"_items": [place(48, 48, scale), place(2, 1339, scale), place(363, 23, scale)]}}
    return {
        "dockplacementmainframexscale": scale, "dockplacementmainframeyscale": scale,
        "dockplacementcommandbarvisible": 1,
        "dockplacementpalette": {"mainframe": {"dock": {
            "subviews": {"_items": [tools, left, center, {"visible": 1}, right]},
            "dockplaces": {"_items": [place(65, 1455, scale), place(345, 1455, scale), place(1847, 857, scale),
                                      place(21, 30, scale), place(282, 1455, scale)]}}}},
    }


class PaintedColumns:
    """Separator rows at booth tops, a header below, and non-uniform content elsewhere."""

    def __init__(self, tops):
        self.left = self.top = 0
        self.rows = {}
        for y in tops:
            self.rows[y] = self.rows[y + 1] = SEPARATOR
            self.rows[y + 2] = HEADER

    def row(self, x0, x1, y):
        color = self.rows.get(y)
        if color:
            return color * (x1 - x0)
        return b"".join(bytes((60 + (x + y) % 7,) * 3 + (0,)) for x in range(x0, x1))


class WorkspaceGeometryTests(unittest.TestCase):
    def test_saved_layout_that_fits_is_bottom_aligned(self):
        regions = panel_rectangles(dock_tree(), (0, 0, 2560, 1527), 1.5, None)
        self.assertEqual(regions["笔刷属性"], (65, 524, 345, 520))
        self.assertEqual(regions["导航器"], (2278, 102, 282, 646))
        self.assertEqual(regions["图层"], (2278, 819, 282, 708))
        self.assertEqual(regions["画布视口"], (410, 72, 1847, 1455))

    def test_rescaled_overflow_uses_the_one_squeezed_booth_seen_in_pixels(self):
        # 175% on the same screen: CSP rescales every size by 7/6, the canvas
        # column takes the leftover width, and one booth per column shrinks.
        pixels = PaintedColumns([119, 343, 950, 872, 955])
        regions = panel_rectangles(dock_tree(), (0, 0, 2560, 1515), 1.75, lambda: pixels)
        self.assertEqual(regions["工具组"], (76, 119, 403, 224))
        self.assertEqual(regions["笔刷属性"], (76, 343, 403, 607))
        self.assertEqual(regions["色彩"], (76, 950, 403, 565))
        self.assertEqual(regions["导航器"], (2231, 119, 329, 753))
        self.assertEqual(regions["图层"], (2231, 955, 329, 560))
        self.assertEqual(regions["画布视口"], (479, 84, 1727, 1431))

    def test_dock_saved_at_the_new_scale_gives_the_same_layout(self):
        tree = dock_tree(ratio(7, 4))
        root = tree["dockplacementpalette"]["mainframe"]["dock"]
        for entry in root["dockplaces"]["_items"]:
            size = entry["dockplacebasesize"]
            size["width"], size["height"] = (int(v * 7 / 6 + 0.5) for v in (size["width"], size["height"]))
        root["dockplaces"]["_items"][2]["dockplacebasesize"]["width"] = 1727
        for group in (1, 4):
            for entry in root["subviews"]["_items"][group]["subviews"]["_items"][0]["dockplaces"]["_items"]:
                entry["dockplacebasesize"]["height"] = int(entry["dockplacebasesize"]["height"] * 7 / 6 + 0.5)
        pixels = PaintedColumns([119, 343, 950, 872, 955])
        expected = panel_rectangles(dock_tree(), (0, 0, 2560, 1515), 1.75, lambda: pixels)
        self.assertEqual(panel_rectangles(tree, (0, 0, 2560, 1515), 1.75, lambda: pixels), expected)

    def test_overflow_at_the_saved_scale_keeps_the_column_collapse_bar(self):
        # Measured live at 175% (no DPI change): the right stack (1663) overflows its saved 1431 px
        # column, so the column remainder no longer measures the 35 px collapse bar above it.
        scale = ratio(7, 4)
        left = column([booth("palettekindsubtool"), booth("palettekindtooloption", "palettekindbrushsize"),
                       booth("palettekindcolorcircle", "palettekindcolorslider", current=1)], [225, 607, 564], 403, scale)
        right = column([booth("palettekindnavigator"), booth("palettekindproperty"), booth("palettekindlayerorder")],
                       [754, 83, 826], 329, scale)
        tools = column([booth("palettekindtool")], [1431], 76, scale)
        center = {"subviews": {"_items": [{}, {}, {"visible": 0}]},
                  "dockplaces": {"_items": [place(48, 48, scale), place(2, 1000, scale), place(363, 23, scale)]}}
        tree = {"dockplacementmainframexscale": scale, "dockplacementmainframeyscale": scale,
                "dockplacementcommandbarvisible": 1,
                "dockplacementpalette": {"mainframe": {"dock": {
                    "subviews": {"_items": [tools, left, center, {"visible": 1}, right]},
                    "dockplaces": {"_items": [place(76, 1431, scale), place(403, 1431, scale), place(1727, 1000, scale),
                                              place(25, 35, scale), place(329, 1431, scale)]}}}}}
        regions = panel_rectangles(tree, (0, 0, 2560, 1515), 1.75, lambda: PaintedColumns([119, 872, 955]))
        self.assertEqual(regions["导航器"], (2231, 119, 329, 753))
        self.assertEqual(regions["图层"], (2231, 955, 329, 560))
        self.assertEqual(regions["笔刷属性"], (76, 344, 403, 607))

    def test_overflow_without_matching_pixels_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "超出可用高度"):
            panel_rectangles(dock_tree(), (0, 0, 2560, 1515), 1.75, None)
        with self.assertRaisesRegex(ValueError, "找不到"):
            panel_rectangles(dock_tree(), (0, 0, 2560, 1515), 1.75, lambda: PaintedColumns([]))

    def test_restore_rect_keeps_origin_and_rescales_size(self):
        self.assertTrue(same_restore_rect((1372, 0, 2401, 771), (1372, 0, 2572, 900), 7 / 6))
        self.assertFalse(same_restore_rect((1372, 0, 2401, 771), (1300, 0, 2572, 900), 7 / 6))
        self.assertFalse(same_restore_rect((1372, 0, 2401, 771), (1372, 0, 2402, 771), 1.0))


if __name__ == "__main__":
    unittest.main()
