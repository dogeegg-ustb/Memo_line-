import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "recorder_integration"), str(ROOT.parent / "CSP_Shortcut_Manager")]
from catalog import Catalog, BRUSH, CANVAS, NAVIGATOR, LAYERS, COLOR
from engine import UpdateEngine


class PenActivationTests(unittest.TestCase):
    def setUp(self):
        catalog = Catalog.__new__(Catalog)
        catalog.bindings = {}
        catalog.gestures = [dict(mask=8, targets=[CANVAS, NAVIGATOR], action="pan")]
        catalog.wheel_bindings = [dict(mask=0x1000, targets=[CANVAS, NAVIGATOR], action="zoom")]
        self.records = []
        self.engine = UpdateEngine(catalog, self.records.append)
        self.engine.set_regions({BRUSH: [0, 0, 100, 100], CANVAS: [100, 0, 500, 500],
                                 NAVIGATOR: [600, 0, 100, 100], LAYERS: [600, 100, 100, 400]})

    def pen(self, kind, event_id, x, y, keys=()):
        # UI scheduling must use the observed x/y, even when reconstructed screenX/Y differ.
        self.engine.handle(dict(type="input", event=dict(eventId=event_id, ticks=event_id * 10,
            kind=kind, data=dict(x=x, y=y, screenX=-1500, screenY=-100,
                                 heldKeys=list(keys), interactionCoordinateSource="windowsCursor"))))

    def invalidations(self):
        return [r for r in self.records if r["kind"] == "panelUpdateRequested"
                and r["data"]["phase"] == "invalidate"]

    def test_repeated_pen_clicks_activate_brush_and_layer_updates(self):
        self.pen("penBegin", 1, 20, 20)
        self.pen("penEnd", 2, 20, 20)
        self.pen("penBegin", 3, 620, 200)
        self.pen("penEnd", 4, 620, 200)
        updates = self.invalidations()
        self.assertEqual([r["data"]["panels"] for r in updates], [[BRUSH], [LAYERS]])
        self.assertEqual([r["relatedEventIds"] for r in updates], [[1, 2], [3, 4]])
        self.assertFalse(self.engine.pointer_operations)

    def test_pen_space_drag_updates_canvas_and_navigator_on_release(self):
        self.pen("penBegin", 5, 200, 100, keys=[32])
        self.engine.handle(dict(type="cursor", ticks=55, x=300, y=120, inCsp=True,
            foreground=True, heldKeys=[32], pointerDown=True, relatedEventIds=[5]))
        self.pen("penEnd", 7, 300, 120, keys=[32])
        updates = self.invalidations()
        self.assertEqual(len(updates), 1)
        self.assertEqual(set(updates[0]["data"]["panels"]), {CANVAS, NAVIGATOR})
        self.assertEqual(updates[0]["relatedEventIds"], [5, 7])
        self.assertFalse(self.engine.pointer_operations)

    def key(self, vk, keys, event_id):
        self.engine.handle(dict(type="input",event=dict(kind="keyInput",eventId=event_id,ticks=event_id*10,
            data=dict(vk=vk,heldKeys=list(keys)))))

    def view_requests(self):
        return [r for r in self.records if r["kind"] == "panelUpdateRequested"
                and CANVAS in r["data"]["panels"]]

    def test_plain_strokes_never_update_canvas_even_outside_corrected_roi(self):
        self.engine.corrected_canvas_roi=[150,50,350,350]
        for start,x in ((1,200),(4,120)):
            self.pen("penBegin",start,x,100)
            self.pen("penSample",start+1,x+10,120)
            self.engine.handle(dict(type="cursor",ticks=(start+1)*10,x=x+10,y=120,inCsp=True,
                foreground=True,heldKeys=[],pointerDown=True,relatedEventIds=[start]))
            self.pen("penEnd",start+2,x+10,120)
        self.assertEqual(self.view_requests(),[])

    def test_released_R_shortcut_does_not_leak_into_following_strokes(self):
        self.engine.catalog.bindings[(82,0)]=[dict(action_name="rotate",pointerTargets=[CANVAS,NAVIGATOR],
            targets=[BRUSH,CANVAS,NAVIGATOR])]
        self.key(82,[82],1)
        self.assertEqual(self.view_requests(),[]) # selecting the tool without a drag
        self.pen("penBegin",2,200,100,keys=[82])
        self.pen("penSample",3,300,120,keys=[82])
        self.pen("penEnd",4,300,120,keys=[])
        self.assertEqual(len([r for r in self.view_requests() if r["data"]["phase"]=="invalidate"]),1)
        self.records.clear()
        self.pen("penBegin",5,200,100)
        self.pen("penSample",6,300,120)
        self.engine.handle(dict(type="cursor",ticks=65,x=300,y=120,inCsp=True,
            foreground=True,heldKeys=[],pointerDown=True,relatedEventIds=[5]))
        self.pen("penEnd",7,300,120)
        self.assertEqual(self.view_requests(),[])

    def test_navigation_tool_selection_without_Space_or_R_never_arms_drawing(self):
        for vk in (72,90):
            with self.subTest(vk=vk):
                self.engine.catalog.bindings[(vk,0)]=[dict(pointerTargets=[CANVAS,NAVIGATOR],targets=[BRUSH,CANVAS,NAVIGATOR])]
                self.records.clear()
                self.key(vk,[vk],1)
                self.pen("penBegin",2,200,100)
                self.pen("penEnd",3,300,120)
                self.assertEqual(self.view_requests(),[])

    def test_R_held_drag_updates_on_pen_release_without_cursor_poll(self):
        self.pen("penBegin",1,200,100,keys=[82])
        self.pen("penSample",2,300,120,keys=[82])
        self.assertEqual(self.invalidations(),[])
        self.pen("penEnd",3,300,120,keys=[82])
        self.assertEqual(set(self.invalidations()[0]["data"]["panels"]),{CANVAS,NAVIGATOR})
        self.assertEqual(self.invalidations()[0]["relatedEventIds"],[1,3])

    def test_navigation_key_pressed_during_stroke_is_detected_by_pen_samples(self):
        for key in (32,82):
            with self.subTest(key=key):
                self.records.clear()
                self.pen("penBegin",1,200,100)
                self.pen("penSample",2,300,120,keys=[key])
                self.pen("penEnd",3,300,120)
                self.assertEqual(len(self.invalidations()),1)
                self.assertEqual(set(self.invalidations()[0]["data"]["panels"]),{CANVAS,NAVIGATOR})
                self.assertEqual(self.invalidations()[0]["relatedEventIds"],[1,2,3])

    def test_context_modifier_without_navigation_key_does_not_activate_view(self):
        self.engine.catalog.gestures.append(dict(mask=2,targets=[CANVAS,NAVIGATOR],contextDependent=True))
        self.pen("penBegin",1,200,100,keys=[162])
        self.pen("penSample",2,300,120,keys=[162])
        self.engine.handle(dict(type="cursor",ticks=25,x=300,y=120,inCsp=True,
            foreground=True,heldKeys=[162],pointerDown=True,relatedEventIds=[1]))
        self.pen("penEnd",3,300,120,keys=[162])
        self.assertEqual(self.view_requests(),[])

    def test_navigator_pen_click_and_configured_view_shortcuts_still_update(self):
        self.pen("penBegin",1,620,20)
        self.pen("penEnd",2,620,20)
        self.assertEqual(set(self.invalidations()[-1]["data"]["panels"]),{CANVAS,NAVIGATOR})
        self.engine.catalog.bindings[(107,0)]=[dict(command="viewzoomin",targets=[CANVAS,NAVIGATOR])]
        self.key(107,[107],3)
        self.assertEqual(set(self.invalidations()[-1]["data"]["panels"]),{CANVAS,NAVIGATOR})
        self.engine.handle(dict(type="input",event=dict(kind="mouseWheel",eventId=4,ticks=40,
            data=dict(x=200,y=100,delta=120,axis="vertical",heldKeys=[]))))
        self.assertEqual(set(self.invalidations()[-1]["data"]["panels"]),{CANVAS,NAVIGATOR})

    def test_mouse_navigation_also_requires_current_keys_inside_canvas(self):
        for keys in ([],[32],[82]):
            with self.subTest(keys=keys):
                self.records.clear()
                for kind,event_id,x in (("mouseDown",1,200),("mouseDrag",2,300),("mouseUp",3,300)):
                    self.engine.handle(dict(type="input",event=dict(kind=kind,eventId=event_id,ticks=event_id*10,
                        data=dict(button="left",x=x,y=100,heldKeys=keys))))
                self.assertEqual(len(self.invalidations()),1 if keys else 0)

    def test_hover_and_R_without_contact_do_not_update_view(self):
        self.engine.handle(dict(type="cursor",ticks=10,x=200,y=100,inCsp=True,
            foreground=True,heldKeys=[82],pointerDown=False,relatedEventIds=[]))
        self.assertEqual(self.view_requests(),[])

    def test_non_navigation_tool_targets_still_update_their_own_state(self):
        self.engine.set_regions({**self.engine.regions,COLOR:[600,600,100,100]})
        self.engine.catalog.bindings[(73,0)]=[dict(pointerTargets=[COLOR],targets=[BRUSH,COLOR])]
        self.key(73,[73],1)
        self.records.clear()
        self.pen("penBegin",2,200,100)
        self.pen("penSample",3,300,100)
        self.pen("penEnd",4,300,100)
        self.assertEqual(self.invalidations()[0]["data"]["panels"],[COLOR])
        self.assertEqual(self.view_requests(),[])


if __name__ == "__main__":
    unittest.main()
