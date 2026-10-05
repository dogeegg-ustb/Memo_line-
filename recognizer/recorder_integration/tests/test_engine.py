import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "recorder_integration"), str(ROOT.parent / "CSP_Shortcut_Manager")]
from catalog import Catalog, BRUSH, CANVAS, NAVIGATOR, LAYERS, TOOLBAR, targets_for_command, key_binding
from engine import UpdateEngine


class ActivatorTests(unittest.TestCase):
    def setUp(self):
        self.catalog = Catalog.__new__(Catalog)
        self.catalog.bindings = {
            (69, 4): [dict(command="layermergelowerlayer", targets=[LAYERS])],
            (66, 0): [dict(action_name="brush", pointerTargets=[], targets=[BRUSH])],
            (72, 0): [dict(action_name="hand", pointerTargets=[CANVAS, NAVIGATOR], targets=[BRUSH, CANVAS, NAVIGATOR])],
        }
        self.catalog.gestures = [dict(mask=8, targets=[CANVAS, NAVIGATOR], action="pan")]
        self.records = []
        self.engine = UpdateEngine(self.catalog, self.records.append)
        self.engine.set_regions({BRUSH: [0, 0, 100, 100], CANVAS: [100, 0, 500, 500],
                                 NAVIGATOR: [600, 0, 100, 100], LAYERS: [600, 100, 100, 400]})

    def cursor(self, x=200, y=100, keys=(), down=False, in_csp=True):
        self.engine.handle(dict(type="cursor", ticks=10, x=x, y=y, inCsp=in_csp,
            foreground=True, heldKeys=list(keys), pointerDown=down, relatedEventIds=[1] if down else []))

    def key(self, vk, keys, event_id=3):
        self.engine.handle(dict(type="input", event=dict(eventId=event_id, ticks=12,
            kind="keyInput", data=dict(vk=vk, heldKeys=keys))))

    def requests(self):
        return [r for r in self.records if r["kind"] == "panelUpdateRequested"]

    def test_hover_enters_once_and_ends(self):
        self.cursor(20, 20)
        self.cursor(21, 21)
        self.assertEqual(len(self.requests()), 1)
        self.assertEqual(self.requests()[0]["data"]["panels"], [BRUSH])
        self.cursor(in_csp=False)
        self.assertIsNone(self.engine.hover)
        self.assertEqual(self.records[-1]["kind"], "panelActivationEnded")

    def test_space_alone_does_not_activate_navigator(self):
        self.cursor(keys=[32])
        # Canvas hover prepares its bound navigator, but space alone must not start a gesture/update.
        self.assertNotIn(NAVIGATOR, self.engine.gesture_panels)
        self.assertFalse(any(r["data"]["phase"] == "invalidate" for r in self.requests()))

    def test_space_drag_has_both_targets_and_original_reference(self):
        self.cursor(keys=[32], down=True)
        request = self.requests()[-1]
        self.assertEqual(set(request["data"]["panels"]), {CANVAS, NAVIGATOR})
        self.assertEqual(request["relatedEventIds"], [1])
        self.assertEqual(request["ticks"], 10)
        self.cursor(keys=[], down=True)
        self.assertEqual(self.engine.gesture_panels, set())

    def test_layer_shortcut_resolves_and_references_input(self):
        self.key(69, [162, 69])
        self.assertEqual(self.requests()[-1]["data"]["panels"], [LAYERS])
        self.assertEqual(self.requests()[-1]["relatedEventIds"], [3])
        self.assertEqual(self.records[0]["kind"], "shortcutResolved")

    def test_navigation_tool_shortcut_does_not_arm_canvas_pointer_events(self):
        self.key(72, [72])
        self.cursor(down=True)
        self.assertEqual(self.engine.gesture_panels, set())
        self.assertFalse(any(CANVAS in r["data"]["panels"] for r in self.requests()))
        self.key(66, [66], 4)
        self.cursor(down=True)
        self.assertEqual(self.engine.gesture_panels, set())

    def test_unknown_binding_is_not_assumed_default(self):
        self.key(90, [162, 90])
        self.assertEqual(self.records[-1]["data"]["resolution"], "unmapped")
        self.assertEqual(self.requests(), [])

    def test_layout_missing_never_requests_old_regions(self):
        self.cursor(20, 20)
        self.engine.set_regions({})
        before = len(self.requests())
        self.key(69, [162, 69])
        self.assertEqual(before, len(self.requests()))
        self.assertEqual(self.engine.active_panels(), set())

    def test_layer_properties_and_image_transform_do_not_activate_layer(self):
        for command in ["layeropacityplus", "layerpropusetone", "layerchangebelowclip", "layerlockalpha", "transformstretch"]:
            self.assertEqual(targets_for_command(command), [])
        for command in ["layerdelete", "layernew", "layerselectupperlayer", "layermergelowerlayer"]:
            self.assertEqual(targets_for_command(command), [LAYERS])
        self.assertEqual(targets_for_command("viewrotateleft"), [CANVAS, NAVIGATOR])

    def test_menu_and_gesture_modifier_encodings_are_distinct(self):
        self.assertEqual(key_binding("Ctrl + Shift + N"), (78, 6))
        self.catalog.gestures.append(dict(mask=10, targets=[CANVAS, NAVIGATOR], action="zoom"))
        self.assertEqual(self.catalog.match_gesture([162, 32])[-1]["action"], "zoom")
        self.assertEqual(self.catalog.match_gesture([160, 32]), [])

    def test_toolbar_operation_captures_brush_roi_and_keeps_input_reference(self):
        self.engine.set_regions({**self.engine.regions,TOOLBAR:[-50,0,40,500]})
        self.cursor(-30,20)
        self.assertEqual(self.engine.active_panels(),set())
        self.engine.handle(dict(type="input",event=dict(eventId=7,ticks=15,kind="mouseDown",
            data=dict(x=-30,y=20,heldKeys=[]))))
        request=self.requests()[-1]
        self.assertEqual(request["data"]["activationPanels"],[TOOLBAR])
        self.assertEqual(request["data"]["panels"],[BRUSH])
        self.assertEqual(request["data"]["regions"],{BRUSH:self.engine.regions[BRUSH]})
        self.assertEqual(request["relatedEventIds"],[7])
        self.assertEqual(request["data"]["phase"],"prepare")
        self.engine.handle(dict(type="input",event=dict(eventId=8,ticks=16,kind="mouseUp",
            data=dict(x=-30,y=20,heldKeys=[]))))
        self.assertEqual(self.requests()[-1]["data"]["phase"],"invalidate")
        self.assertEqual(self.requests()[-1]["relatedEventIds"],[7,8])
        self.engine.flash_update([TOOLBAR])
        self.assertEqual(self.engine.active_panels(),{BRUSH})

    def test_initial_capture_maps_toolbar_to_existing_brush_state(self):
        self.assertEqual(self.engine.capture_targets([TOOLBAR,BRUSH,CANVAS]),{BRUSH,CANVAS})


if __name__ == "__main__":
    unittest.main()
