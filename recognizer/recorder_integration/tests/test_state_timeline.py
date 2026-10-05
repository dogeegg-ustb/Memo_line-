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

    def test_later_snapshot_confirms_observed_state_and_preserves_timing_evidence(self):
        emitted=[]
        t=StateTimeline(emitted.append,lambda _:None)
        t.expect("input1",{"currentLayerState"})
        t.complete("input1","currentLayerState","Layer2",{"causalAmbiguous":True,"observedAfterEventId":3})
        self.assertEqual(emitted[0]["data"]["status"],"changed")
        update=emitted[0]["data"]["updates"][0]
        self.assertEqual(update["state"],"Layer2")
        self.assertTrue(update["evidence"]["causalAmbiguous"])
        self.assertEqual(update["evidence"]["observedAfterEventId"],3)
        self.assertIn("currentLayerState",t.previous)

    def test_recovered_canvas_is_confirmed_and_compared_despite_later_input(self):
        emitted=[]
        t=StateTimeline(emitted.append,lambda _:None)
        recovered=dict(success=True,canvasOriginScreenPx=dict(x=1130,y=-162.85656872093534),
                       ocrScalePercent=30.2,ocrRotationDegrees=0,
                       snapshot=dict(canvasPixelWidth=4961,canvasPixelHeight=7016))
        for package,watermark in (("click",11),("sameView",None)):
            t.expect(package,{"canvasViewState"})
            t.complete(package,"canvasViewState",recovered,
                       dict(causalAmbiguous=True,relatedEventIds=[9,10],observedAfterEventId=watermark))
        self.assertEqual([e["data"]["status"] for e in emitted],["changed","unchanged"])
        self.assertEqual(emitted[0]["data"]["updates"][0]["state"]["canvasOriginScreenPx"],
                         recovered["canvasOriginScreenPx"])

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

    def test_core_callback_is_immediate_before_package_is_complete(self):
        emitted,notified,updates=[],[],[]
        t=StateTimeline(emitted.append,notified.append,lambda *args:updates.append(args))
        t.expect("initial",{"currentLayerState","colorState"},True)
        raw={"kind":"color","rgb":[1,2,3],"hex":"#010203","confidence":0.9}
        t.complete("initial","colorState",raw,{"capturedTicks":100})
        self.assertEqual(emitted,[])
        self.assertEqual(notified,[])
        self.assertEqual(len(updates),1)
        package,update,initial,result=updates[0]
        self.assertEqual(package,"initial")
        self.assertTrue(initial)
        self.assertEqual(update["status"],"changed")
        self.assertNotIn("confidence",update["state"])
        self.assertEqual(result,raw)
        t.complete("initial","currentLayerState","Layer1",{})
        self.assertEqual(len(updates),2)
        self.assertEqual(len(emitted),1)

    def test_independent_clip_observation_has_no_package_and_ignores_source_path(self):
        updates=[]
        t=StateTimeline(lambda _:self.fail("independent updates must not resolve a package"),lambda _:None,
                        lambda *args:updates.append(args))
        t.observe("clipState",{"source":"snapshot1.clip","layers":["Layer1"]},{"saveId":"s1"})
        t.observe("clipState",{"source":"snapshot2.clip","layers":["Layer1"]},{"saveId":"s2"})
        self.assertEqual([u[1]["status"] for u in updates],["changed","unchanged"])
        self.assertIsNone(updates[0][0])
        self.assertNotIn("source",updates[0][1]["state"])

    def test_failed_updates_do_not_replace_latest_successful_observation(self):
        updates=[]
        t=StateTimeline(lambda _:None,lambda _:None,lambda *args:updates.append(args))
        t.observe("colorState",{"kind":"color","rgb":[1,2,3]}, {})
        t.observe("colorState",{"kind":"color","rgb":[9,9,9]}, {"causalAmbiguous":True})
        t.observe("colorState",{"kind":"unknown"}, {})
        t.observe("colorState",None, {}, "OCR failed")
        t.observe("colorState",{"kind":"color","rgb":[9,9,9]}, {})
        t.observe("colorState",{"kind":"color","rgb":[1,2,3]}, {})
        self.assertEqual([u[1]["status"] for u in updates],["changed","changed","unknown","error","unchanged","changed"])

    def test_session_stop_publishes_error_for_unresolved_core(self):
        updates=[]
        t=StateTimeline(lambda _:None,lambda _:None,lambda *args:updates.append(args))
        t.expect("initial",{"currentLayerState"},True)
        t.close(123)
        self.assertEqual(len(updates),1)
        self.assertEqual(updates[0][1]["status"],"error")
        self.assertEqual(updates[0][1]["evidence"]["completedTicks"],123)

if __name__ == "__main__":
    unittest.main()
