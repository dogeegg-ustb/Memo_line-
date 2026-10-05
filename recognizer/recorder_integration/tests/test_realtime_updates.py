import sys
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT / "recorder_integration"),str(ROOT.parent / "CSP_Shortcut_Manager")]
from pipeline import Pipeline
from state_timeline import StateTimeline, semantic_state


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


if __name__ == "__main__":
    unittest.main()
