import sys
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT / "recorder_integration"),str(ROOT.parent / "CSP_Shortcut_Manager")]
from pipeline import Pipeline
from state_timeline import StateTimeline
from catalog import CANVAS, NAVIGATOR


def view(scale, x=863):
    return dict(success=True,ocrScalePercent=scale,ocrRotationDegrees=0,canvasOriginScreenPx=dict(x=x,y=161))


class ViewSettleTests(unittest.TestCase):
    """A view command whose capture still shows the old view is looked at again (2026-10-20, Ctrl+= after a save)."""

    def setUp(self):
        self.records,self.notes=[],[]
        self.pipe=Pipeline.__new__(Pipeline)
        self.pipe.emit=self.records.append
        self.pipe.notify=self.notes.append
        self.pipe.analysis_lock=threading.RLock()
        self.pipe.panel_activity={}
        self.pipe.settings=dict(analysisQuietMs=150)
        self.pipe.closing=False
        self.pipe.timeline=StateTimeline(lambda _:None,lambda _:None,self.pipe._publish_core_update)
        with patch("pipeline.now_ticks",return_value=1):
            self.pipe.timeline.observe("canvasViewState",view(18.6),dict(capturedTicks=1))

    def finish(self, reason, result, package="p"):
        self.pipe.timeline.expect(package,{"canvasViewState"})
        job=dict(module=CANVAS,crops={CANVAS:dict(screenshotId="s",roi=[0,0,10,10])},ticks=100,refs=[248],
                 captureId="c1",packageId=package,triggerTicks=90,captureEndTicks=105,captureLatencyMs=15,
                 captureDurationMs=5,captureBudgetExceeded=False,reason=reason,initialize=False,
                 causalAmbiguous=False,observedAfterEventId=248,analysisToken=None)
        self.notes.clear()
        with patch("pipeline.now_ticks",return_value=200):
            self.pipe._complete_analysis(job,result,150)
        return [n for n in self.notes if n["type"]=="viewSettle"]

    def published(self):
        return [r["data"] for r in self.records if r["kind"]=="coreStateUpdated" and r["data"]["module"]=="canvasViewState"]

    def test_unchanged_view_after_a_zoom_shortcut_is_checked_again_without_publishing(self):
        before=len(self.published())
        settle=self.finish("shortcut",view(18.6))
        self.assertEqual(len(settle),1)
        self.assertEqual((settle[0]["attempt"],settle[0]["delayMs"],settle[0]["captureId"],settle[0]["refs"]),(1,150,"c1",[248]))
        self.assertEqual(len(self.published()),before)

    def test_the_command_result_publishes_once_the_view_changed(self):
        self.assertEqual(self.finish("viewSettle2:c1",view(25,734)),[])
        self.assertEqual(self.published()[-1]["status"],"changed")
        self.assertEqual(self.published()[-1]["state"]["ocrScalePercent"],25)

    def test_rechecks_end_and_publish_unchanged(self):
        self.assertEqual(self.finish("viewSettle4:c1",view(18.6)),[])
        self.assertEqual(self.published()[-1]["status"],"unchanged")
        self.assertEqual(self.finish("viewSettle3:c1",view(18.6),"q")[0]["attempt"],4)

    def test_other_triggers_publish_unchanged_at_once(self):
        for reason in ("pointerReleased","externalStateRequest:r","canvasRetry:c0"):
            with self.subTest(reason=reason):
                self.assertEqual(self.finish(reason,view(18.6),reason),[])
                self.assertEqual(self.published()[-1]["status"],"unchanged")


class RuntimeSettleTests(unittest.TestCase):
    def test_a_newer_canvas_activation_cancels_the_recheck(self):
        import runtime
        app=runtime.Runtime.__new__(runtime.Runtime)
        app.closing=False
        app.pipeline=Pipeline.__new__(Pipeline)
        app.pipeline.panel_activity={CANVAS:dict(token=7)}
        calls=[]
        app.schedule_capture=lambda *args: calls.append(args)
        msg=dict(analysisToken=6,ticks=90,refs=[248],attempt=1,captureId="c1",packageId="p")
        app.settle_view(msg)
        self.assertEqual(calls,[])
        app.settle_view(dict(msg,analysisToken=7))
        self.assertEqual(calls,[([CANVAS,NAVIGATOR],90,[248],"viewSettle1:c1","p")])


if __name__ == "__main__":
    unittest.main()
