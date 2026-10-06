import sys
import unittest
import threading
from pathlib import Path
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT / "recorder_integration"),str(ROOT.parent / "CSP_Shortcut_Manager")]
from pipeline import Pipeline
from state_timeline import StateTimeline, semantic_state
from catalog import BRUSH


class CoreRealtimeContractTests(unittest.TestCase):
    def setUp(self):
        # Exercise the output boundary without starting screenshot workers or OCR.
        self.records=[]
        self.pipeline=Pipeline.__new__(Pipeline)
        self.pipeline.emit=self.records.append
        self.timeline=StateTimeline(lambda _:None,lambda _:None,self.pipeline._publish_core_update)

    def test_observation_preserves_capture_time_references_and_raw_diagnostics(self):
        self.timeline.expect("package1",{"colorState","currentLayerState"})
        raw=dict(kind="color",rgb=[1,2,3],hex="#010203",confidence=0.8)
        evidence=dict(capturedTicks=100,relatedEventIds=[5,6],screenshotIds=["image1"])
        with patch("pipeline.now_ticks",return_value=200):
            self.timeline.complete("package1","colorState",raw,evidence)
        message=self.records[0]
        self.assertEqual(message["kind"],"coreStateUpdated")
        self.assertEqual(message["path"],"delayed")
        self.assertEqual(message["ticks"],100)
        self.assertEqual(message["relatedEventIds"],[5,6])
        self.assertEqual(message["data"]["rawResult"],raw)
        self.assertEqual(message["data"]["evidence"]["completedTicks"],200)
        self.assertNotIn("completedTicks",evidence)

    def test_independent_clip_update_uses_observation_time(self):
        with patch("pipeline.now_ticks",return_value=300):
            self.timeline.observe("clipState",dict(layers=[],source="snapshot.clip"),
                                  dict(observedTicks=250,saveId="save1",saveCompletionConfirmed=False))
        message=self.records[0]
        self.assertEqual(message["ticks"],250)
        self.assertIsNone(message["data"]["packageId"])
        self.assertEqual(message["data"]["module"],"clipState")
        self.assertEqual(message["data"]["status"],"changed")
        self.assertFalse(message["data"]["evidence"]["saveCompletionConfirmed"])

    def test_all_cores_publish_the_current_result_without_one_observation_lag(self):
        observations = {
            "brushState": [dict(brush=dict(name=name,status="ok",properties=[dict(key="size",value=v,status="ok")]))
                           for name,v in (("Pen1",10),("Pen2",20))],
            "currentLayerState": ["Layer1","Layer2"],
            "colorState": [dict(kind="color",rgb=rgb,hex=h) for rgb,h in (([1,2,3],"#010203"),([4,5,6],"#040506"))],
            "canvasViewState": [dict(success=True,ocrScalePercent=v,ocrRotationDegrees=0,canvasOriginScreenPx=dict(x=v,y=0)) for v in (10,20)],
            "clipState": [dict(layers=[dict(id=v)],source=f"snapshot{v}.clip") for v in (1,2)]
        }
        for module,values in observations.items():
            with self.subTest(module=module):
                for index,raw in enumerate(values):
                    with patch("pipeline.now_ticks",return_value=200+index):
                        self.timeline.observe(module,raw,dict(capturedTicks=100+index,causalAmbiguous=True))
                    msg=self.records[-1]["data"]
                    self.assertEqual(msg["status"],"changed")
                    self.assertEqual(msg["state"],semantic_state(module,raw))
                    self.assertEqual(msg["rawResult"],raw)
                    self.assertEqual(msg["evidence"]["capturedTicks"],100+index)

    def test_error_is_published_with_its_completion_time(self):
        with patch("pipeline.now_ticks",return_value=300):
            self.timeline.observe("colorState",None,{},"OCR failed")
        message=self.records[0]
        self.assertEqual(message["ticks"],300)
        self.assertEqual(message["data"]["status"],"error")
        self.assertEqual(message["data"]["error"],"OCR failed")
        self.assertIsNone(message["data"]["state"])

    def test_brush_value_locations_are_projected_to_screen_without_changing_value_state(self):
        raw = dict(schema_version=4,brush=dict(name="Pen",status="ok",properties=[
            dict(key="brush_size",type="number",value=5.4,status="ok")]),value_regions=[
            dict(property_key="brush_size",property_index=0,category="number",value=5.4,
                 bbox=[200,40,30,18],source="ocr",status="ok",score=.99,text="5.4",unit="px")])
        with patch("pipeline.now_ticks",return_value=200):
            self.timeline.observe("brushState",raw,dict(captureId="first",panelRoi=[-300,500,360,260]))
        first = self.records[-1]["data"]
        self.assertEqual(first["valueRegions"][0]["screenBbox"],[-100,540,30,18])
        self.assertEqual(first["valueRegions"][0]["category"],"number")
        self.assertEqual(first["valueRegions"][0]["text"],"5.4")
        self.assertEqual(first["valueRegions"][0]["unit"],"px")
        self.assertEqual(first["rawResult"],raw)
        raw["value_regions"][0]["bbox"]=[210,60,30,18]
        with patch("pipeline.now_ticks",return_value=300):
            self.timeline.observe("brushState",raw,dict(captureId="second",panelRoi=[100,600,360,260]))
        second = self.records[-1]["data"]
        self.assertEqual(second["status"],"unchanged")
        self.assertEqual(second["valueRegions"][0]["screenBbox"],[310,660,30,18])
        self.assertEqual(second["evidence"]["captureId"],"second")
        self.assertEqual(second["state"],first["state"])

    def test_brush_error_returns_empty_locations_and_missing_origin_has_no_screen_box(self):
        raw = dict(brush=dict(name="Pen",status="ok",properties=[]),value_regions=[
            dict(property_key="mode",property_index=0,category="icon",value=1,
                 bbox=[40,50,20,20],source="image",status="ok")])
        with patch("pipeline.now_ticks",return_value=200):
            self.timeline.observe("brushState",raw,{})
        self.assertNotIn("screenBbox",self.records[-1]["data"]["valueRegions"][0])
        with patch("pipeline.now_ticks",return_value=300):
            self.timeline.observe("brushState",None,{},"OCR failed")
        self.assertEqual(self.records[-1]["data"]["valueRegions"],[])
        self.assertEqual(self.records[-1]["data"]["status"],"error")

    def test_brush_completion_carries_the_origin_of_the_same_capture(self):
        self.pipeline.analysis_lock=threading.RLock()
        self.pipeline.panel_activity={}
        self.pipeline.settings=dict(analysisQuietMs=150)
        self.pipeline.timeline=self.timeline
        self.timeline.expect("p",{"brushState"})
        job=dict(module=BRUSH,crops={BRUSH:dict(screenshotId="image1",roi=[-300,500,360,260])},
                 ticks=100,refs=[7],captureId="capture1",packageId="p",triggerTicks=90,
                 captureEndTicks=105,captureLatencyMs=15,captureDurationMs=5,captureBudgetExceeded=False,
                 reason="operation",initialize=False,causalAmbiguous=False,observedAfterEventId=7)
        raw=dict(brush=dict(name="Pen",status="ok",properties=[]),value_regions=[
            dict(property_key="size",property_index=0,category="number",value=5.4,
                 bbox=[200,40,30,18],source="ocr",status="ok")])
        with patch("pipeline.now_ticks",return_value=200):
            self.pipeline._complete_analysis(job,raw,150)
        data=self.records[-1]["data"]
        self.assertEqual(data["evidence"]["panelRoi"],[-300,500,360,260])
        self.assertEqual(data["evidence"]["captureId"],"capture1")
        self.assertEqual(data["valueRegions"][0]["screenBbox"],[-100,540,30,18])


if __name__ == "__main__":
    unittest.main()
