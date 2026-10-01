import sys
import unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from state_timeline import StateTimeline

class StatePackageTests(unittest.TestCase):
    def test_initial_completion_waits_for_every_module(self):
        emitted,notified=[],[]
        t=StateTimeline(emitted.append,notified.append)
        t.expect("initial",{"currentLayerState","colorState"},True)
        t.complete("initial","currentLayerState","Layer1",{})
        self.assertEqual(emitted,[])
        t.complete("initial","colorState",{"kind":"color","rgb":[1,2,3],"confidence":0.5},{})
        self.assertEqual(len(emitted),1)
        self.assertEqual(notified,[{"type":"initialStateReady","degraded":False,"unavailableModules":[]}])

    def test_confidence_changes_do_not_create_color_changes(self):
        emitted=[]
        t=StateTimeline(emitted.append,lambda _:None)
        for id,confidence in (("first",0.9),("second",0.7)):
            t.expect(id,{"colorState"})
            t.complete(id,"colorState",{"kind":"color","rgb":[1,2,3],"confidence":confidence},{})
        self.assertEqual([e["data"]["status"] for e in emitted],["changed","unchanged"])

    def test_later_snapshot_does_not_confirm_an_earlier_change(self):
        emitted=[]
        t=StateTimeline(emitted.append,lambda _:None)
        t.expect("input1",{"currentLayerState"})
        t.complete("input1","currentLayerState","Layer2",{"causalAmbiguous":True,"observedAfterEventId":3})
        self.assertEqual(emitted[0]["data"]["status"],"ambiguous")
        self.assertNotIn("currentLayerState",t.previous)

    def test_brush_evidence_changes_do_not_change_brush_values(self):
        emitted=[]
        t=StateTimeline(emitted.append,lambda _:None)
        for id,score in (("first",0.9),("second",0.7)):
            t.expect(id,{"brushState"})
            t.complete(id,"brushState",dict(brush=dict(name="Pen",status="ok",evidence=[score],properties=[
                dict(key="size",value=12,type="number",status="ok",evidence=[score])])),{})
        self.assertEqual([e["data"]["status"] for e in emitted],["changed","unchanged"])

    def test_failed_initialization_finishes_attempt_and_preserves_error(self):
        emitted,notified=[],[]
        t=StateTimeline(emitted.append,notified.append)
        t.expect("initial",{"canvasViewState"},True)
        t.complete("initial","canvasViewState",None,{},"native failure")
        self.assertEqual(notified,[{"type":"initialStateReady","degraded":True,"unavailableModules":["canvasViewState"]}])
        self.assertEqual(emitted[0]["data"]["status"],"error")
        t.expect("retry",{"canvasViewState"})
        t.complete("retry","canvasViewState",{"success":True,"snapshot":{}},{})
        self.assertEqual(emitted[1]["data"]["status"],"changed")

    def test_partial_brush_observation_is_retained_without_confirming_state(self):
        emitted=[]
        t=StateTimeline(emitted.append,lambda _:None)
        observation=dict(brush=dict(name="Pen",status="ok",properties=[
            dict(key="size",value=30,status="ok"),
            dict(key="vector_magnet",value="unknown",status="partial")]))
        t.expect("partial",{"brushState"})
        t.complete("partial","brushState",observation,{})
        update=emitted[0]["data"]["updates"][0]
        self.assertEqual(update["status"],"unknown")
        self.assertIsNone(update["state"])
        self.assertEqual(update["observedState"],observation)
        self.assertNotIn("brushState",t.previous)

    def test_disabled_property_with_unreadable_value_is_a_known_inactive_state(self):
        emitted=[]
        t=StateTimeline(emitted.append,lambda _:None)
        t.expect("disabled",{"brushState"})
        t.complete("disabled","brushState",dict(brush=dict(name="Pen",status="ok",properties=[
            dict(key="size",value=30,status="ok"),
            dict(key="vector_magnet",value="unknown",enabled="disabled",status="disabled")
        ])),{})
        update=emitted[0]["data"]["updates"][0]
        self.assertEqual(update["status"],"changed")
        disabled=next(p for p in update["state"]["properties"] if p["key"]=="vector_magnet")
        self.assertEqual(disabled["status"],"disabled")
        self.assertIn("brushState",t.previous)

if __name__ == "__main__":
    unittest.main()
