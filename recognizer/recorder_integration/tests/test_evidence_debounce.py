"""Continuous activations preserve screenshots; only core work waits for quiet."""
from contextlib import contextmanager
from pathlib import Path
import queue
import sys
import threading
from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace
import unittest
import uuid
from unittest.mock import Mock, patch

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "recorder_integration"),str(ROOT),str(ROOT.parent / "CSP_Shortcut_Manager")]
from catalog import Catalog, BRUSH, COLOR, CANVAS, NAVIGATOR, LAYERS
from engine import UpdateEngine
from pipeline import Pipeline, CANVAS_FRAME
from runtime import Runtime
from state_timeline import StateTimeline


@contextmanager
def evidence_directory():
    root = Path(__file__).parent.resolve()
    directory = root / (".debounce-regression-"+uuid.uuid4().hex)
    assert directory.resolve().is_relative_to(root)
    directory.mkdir()
    try:
        yield directory
    finally:
        for file in directory.iterdir():
            file.unlink()
        directory.rmdir()


class EvidenceDebounceTests(unittest.TestCase):
    def setUp(self):
        self.clock = 0.0
        clock = patch("pipeline.time.monotonic",side_effect=lambda:self.clock)
        clock.start()
        self.addCleanup(clock.stop)
        ticks = patch("pipeline.now_ticks",side_effect=lambda:int(self.clock*10_000_000))
        ticks.start()
        self.addCleanup(ticks.stop)
        self.records, self.messages = [], []
        self.pipe = Pipeline.__new__(Pipeline)
        self.pipe.settings = dict(analysisQuietMs=150,analysisConcurrency=2)
        self.pipe.analysis_lock = threading.RLock()
        self.pipe.analysis_sequence = 0
        self.pipe.panel_activity = {}
        self.pipe.stop = threading.Event()
        self.pipe.closing = False
        self.pipe.active = set()
        self.pipe.capture_queue = queue.Queue()
        self.pipe.evidence_queue = queue.Queue()
        self.pipe.emit = self.records.append
        self.pipe.notify = self.messages.append
        self.pipe.timeline = StateTimeline(self.records.append,self.messages.append,self.pipe._publish_core_update)
        self.pipe.sequence = 0
        self.pipe.finish_capture_watermark = lambda _:100

        self.app = Runtime.__new__(Runtime)
        self.app.pipeline = self.pipe
        self.app.settings = self.pipe.settings
        self.app.pending_captures, self.app.scheduled_captures = {}, {}
        self.app.package_waiters = {}
        self.app.current_package_id = None
        self.app.canvas_roi = None
        self.app.captures_pending = self.app.capture_sequence = 0
        self.app.capture_quiet_until = 0
        self.app.closing = self.app.guard_busy = False
        self.app.stop = threading.Event()
        self.app.root = Mock()
        self.callbacks = []
        self.app.root.after.side_effect = lambda delay,callback:self.callbacks.append(callback)
        for name in ("show","show_origin","show_core_rois","show_viewport"):
            setattr(self.app,name,Mock())
        catalog = Catalog.__new__(Catalog)
        catalog.bindings, catalog.gestures, catalog.wheel_bindings = {},[],[]
        self.app.engine = UpdateEngine(catalog,self.app.on_state)
        self.app.engine.set_regions({BRUSH:[0,0,20,20],COLOR:[20,0,20,20],LAYERS:[0,20,20,20],
                                     CANVAS:[40,0,100,100],NAVIGATOR:[140,0,20,20]})
        output = patch("runtime.emit",side_effect=self.messages.append)
        output.start()
        self.addCleanup(output.stop)

    def activate(self, panels, package, when):
        self.clock = when
        self.app.schedule_capture(panels,int(when*10_000_000),[int(when*1000)+1],"shortcut",package)

    def submit(self):
        callbacks,self.callbacks = self.callbacks,[]
        for callback in callbacks:
            callback()

    def captures(self):
        result = []
        while not self.pipe.capture_queue.empty():
            result.append(self.pipe.capture_queue.get_nowait())
        return result

    def job(self, panel, token, package, final=True):
        return dict(module=panel,analysisToken=token,finalEvidence=final,packageId=package,
                    crops={panel:dict(screenshotId=str(token),path="unused.png",roi=[0,0,1,1],panel=panel)},
                    ticks=1,refs=[1],triggerTicks=0,captureId=str(token),initialize=False,reason="shortcut",
                    causalAmbiguous=False,observedAfterEventId=1,captureEndTicks=1,captureLatencyMs=1,
                    captureDurationMs=1,captureBudgetExceeded=False)

    def test_repeated_activations_capture_all_before_the_quiet_window(self):
        for i,when in enumerate((0,0.05,0.10)):
            self.activate([BRUSH],f"p{i}",when)
        self.submit()
        requests = self.captures()
        self.assertEqual(len(requests),3) # Even renewed during the overlay-hide delay.
        self.assertEqual([r[6] for r in requests],["p0","p1","p2"])
        self.assertTrue(all(not r[7]["finalEvidence"] for r in requests))
        self.clock = .249
        self.app.flush_pending_captures()
        self.assertFalse(self.callbacks)
        self.clock = .250
        self.app.flush_pending_captures()
        self.submit()
        latest = self.captures()
        self.assertEqual(len(latest),1)
        self.assertEqual(latest[0][6],"p2")
        self.assertTrue(latest[0][7]["finalEvidence"])

    def test_color_can_finish_while_brush_is_still_changing(self):
        self.activate([COLOR],"color",0)
        color_token = self.pipe.panel_activity[COLOR]["token"]
        self.activate([BRUSH],"brush1",.1)
        self.activate([BRUSH],"brush2",.14)
        self.submit()
        self.captures()
        self.clock = .15
        self.app.flush_pending_captures()
        self.submit()
        requests = self.captures()
        self.assertEqual([r[0] for r in requests],[{COLOR}])
        self.assertEqual(self.pipe._job_action(self.job(COLOR,color_token,"color")),"analyze")
        self.assertIn(BRUSH,self.app.pending_captures)

    def test_drag_evidence_is_not_blocked_until_pointer_release(self):
        self.app.engine.pointer_operations[("pen","pen")] = 1
        self.activate([BRUSH],"drag",0)
        self.submit()
        self.assertEqual(len(self.captures()),1)
        self.clock = .15
        self.app.flush_pending_captures()
        self.submit()
        self.assertTrue(self.captures()[0][7]["finalEvidence"])

    def test_hover_only_does_not_capture_or_postpone_analysis(self):
        self.app.engine.handle(dict(type="cursor",ticks=1,x=1,y=1,inCsp=True,
            foreground=True,pointerDown=False,relatedEventIds=[],heldKeys=[]))
        self.assertFalse(self.app.pending_captures)
        self.assertFalse(self.callbacks)

    def test_motion_samples_capture_but_stationary_cursor_does_not_reset_quiet(self):
        self.app.current_package_id = "start"
        self.app.engine.handle(dict(type="input",event=dict(kind="penBegin",ticks=1,eventId=1,
            data=dict(x=1,y=1,heldKeys=[]))))
        self.app.current_package_id = None
        self.app.engine.handle(dict(type="cursor",ticks=2,x=1,y=1,inCsp=True,
            foreground=True,pointerDown=True,relatedEventIds=[1],heldKeys=[]))
        token = self.pipe.panel_activity[BRUSH]["token"]
        self.app.engine.handle(dict(type="cursor",ticks=3,x=2,y=1,inCsp=True,
            foreground=True,pointerDown=True,relatedEventIds=[1],heldKeys=[]))
        moved_token = self.pipe.panel_activity[BRUSH]["token"]
        self.assertGreater(moved_token,token)
        self.clock = .1
        self.app.engine.handle(dict(type="cursor",ticks=4,x=2,y=1,inCsp=True,
            foreground=True,pointerDown=True,relatedEventIds=[1],heldKeys=[]))
        self.assertEqual(self.pipe.panel_activity[BRUSH]["token"],moved_token)
        self.assertEqual(self.pipe.panel_activity[BRUSH]["due"],.15)

    def test_plain_canvas_motion_does_not_activate_view(self):
        self.app.engine.handle(dict(type="input",event=dict(kind="penBegin",ticks=1,eventId=1,
            data=dict(x=60,y=30,heldKeys=[]))))
        for x in (60,70,80):
            self.app.engine.handle(dict(type="cursor",ticks=x,x=x,y=30,inCsp=True,
                foreground=True,pointerDown=True,relatedEventIds=[1],heldKeys=[]))
        self.assertNotIn(CANVAS,self.pipe.panel_activity)
        self.assertFalse(self.callbacks)

    def test_space_canvas_motion_captures_and_keeps_canvas_navigator_together(self):
        self.app.current_package_id = "pan"
        self.app.engine.handle(dict(type="input",event=dict(kind="penBegin",ticks=1,eventId=1,
            data=dict(x=60,y=30,heldKeys=[32]))))
        self.submit()
        requests = self.captures()
        self.assertEqual(requests[0][0],{CANVAS,NAVIGATOR})
        self.assertTrue(requests[0][7]["finalEvidence"])
        self.assertNotIn(CANVAS,self.app.pending_captures)

    def test_canvas_release_captures_immediately_and_analyzes_that_evidence_after_150ms(self):
        self.app.current_package_id = "pan"
        self.app.engine.handle(dict(type="input",event=dict(kind="penBegin",ticks=0,eventId=1,
            data=dict(x=60,y=30,heldKeys=[32]))))
        self.submit()
        self.captures()
        self.clock = .05
        self.app.current_package_id = "released"
        self.app.engine.handle(dict(type="input",event=dict(kind="penEnd",ticks=500_000,eventId=2,
            data=dict(x=80,y=30,heldKeys=[32]))))
        self.submit()
        requests = self.captures()
        self.assertEqual(len(requests),1)
        self.assertEqual(requests[0][3:7],([1,2],"pointerReleased",False,"released"))
        self.assertEqual(requests[0][2],500_000)
        self.assertTrue(requests[0][7]["finalEvidence"])
        token = requests[0][7]["analysisToken"]
        self.assertFalse(self.app.engine.pointer_operations)
        self.clock = .199
        self.assertEqual(self.pipe._job_action(self.job(CANVAS,token,"released")),"wait")
        self.clock = .200
        self.assertEqual(self.pipe._job_action(self.job(CANVAS,token,"released")),"analyze")
        self.app.flush_pending_captures()
        self.assertFalse(self.callbacks)
        self.assertFalse(self.captures()) # Quiet never takes a second canvas image.

    def test_canvas_renewed_motion_replaces_evidence_and_restarts_only_analysis_wait(self):
        self.activate([CANVAS,NAVIGATOR],"first",0)
        first = self.pipe.panel_activity[CANVAS]["token"]
        self.submit()
        self.activate([CANVAS,NAVIGATOR],"last",.1)
        last = self.pipe.panel_activity[CANVAS]["token"]
        self.submit()
        requests = self.captures()
        self.assertEqual([r[6] for r in requests],["first","last"])
        self.assertTrue(all(r[7]["finalEvidence"] for r in requests))
        self.assertEqual(self.pipe._job_action(self.job(CANVAS,first,"first")),"superseded")
        self.clock = .249
        self.assertEqual(self.pipe._job_action(self.job(CANVAS,last,"last")),"wait")
        self.clock = .250
        self.assertEqual(self.pipe._job_action(self.job(CANVAS,last,"last")),"analyze")
        self.app.flush_pending_captures()
        self.assertFalse(self.callbacks)

    def test_other_panel_activity_does_not_delay_canvas_quiet(self):
        self.pipe.settings["analysisQuietMs"] = 500
        self.activate([CANVAS],"canvas",0)
        token = self.pipe.panel_activity[CANVAS]["token"]
        self.activate([BRUSH],"brush",.14)
        self.submit()
        self.captures()
        self.assertEqual(self.pipe.panel_activity[CANVAS]["due"],.15)
        self.assertEqual(self.pipe.panel_activity[BRUSH]["due"],.64)
        self.clock = .15
        self.assertEqual(self.pipe._job_action(self.job(CANVAS,token,"canvas")),"analyze")
        self.app.flush_pending_captures()
        self.assertFalse(self.callbacks)
        self.assertEqual(set(self.app.pending_captures),{BRUSH})

    def test_mixed_activation_keeps_canvas_ready_and_other_panel_final_capture_separate(self):
        self.activate([CANVAS,BRUSH],"mixed",0)
        self.submit()
        requests = self.captures()
        self.assertEqual(len(requests),2)
        canvas = next(r for r in requests if CANVAS in r[0])
        brush = next(r for r in requests if BRUSH in r[0])
        self.assertEqual(canvas[0],{CANVAS,NAVIGATOR})
        self.assertTrue(canvas[7]["finalEvidence"])
        self.assertFalse(brush[7]["finalEvidence"])
        self.assertEqual(canvas[6],brush[6])
        self.assertEqual(canvas[7]["analysisToken"],brush[7]["analysisToken"])
        self.assertEqual(set(self.app.pending_captures),{BRUSH})
        self.clock = .15
        self.app.flush_pending_captures()
        self.submit()
        final = self.captures()
        self.assertEqual(len(final),1)
        self.assertEqual(final[0][0],{BRUSH})
        self.assertTrue(final[0][7]["finalEvidence"])

    def test_mixed_unanchored_activation_binds_each_reserved_package_to_its_own_panels(self):
        self.activate([CANVAS,BRUSH],None,0)
        waiters = self.app.package_waiters.copy()
        self.assertEqual(len(waiters),2)
        self.app.messages, self.app.layouts = queue.Queue(),queue.Queue()
        self.app.hwnd, self.app.last_result, self.app.last_input = 1,0,0
        self.app.user32 = SimpleNamespace(GetForegroundWindow=lambda:1)
        self.app.initial_capture_pending = False
        self.app.root.after.side_effect = lambda delay,callback:self.callbacks.append(callback) if delay == 16 else None
        for request_id,entry in sorted(waiters.items(),key=lambda item:BRUSH in item[1][0]):
            package_id = "brush-reserved" if BRUSH in entry[0] else "canvas-reserved"
            self.app.messages.put(dict(type="timelineReserved",requestId=request_id,packageId=package_id))
        self.app.refresh()
        self.submit()
        self.assertEqual(self.app.pending_captures[BRUSH][5],"brush-reserved")
        self.assertEqual(self.pipe.panel_activity[CANVAS]["packageId"],"canvas-reserved")
        self.assertEqual(self.pipe.panel_activity[BRUSH]["packageId"],"brush-reserved")
        requests = self.captures()
        self.assertEqual(next(r for r in requests if CANVAS in r[0])[6],"canvas-reserved")
        self.assertEqual(next(r for r in requests if BRUSH in r[0])[6],"brush-reserved")

    def test_releasing_navigation_key_stops_motion_activation(self):
        self.app.current_package_id = "pan"
        self.app.engine.handle(dict(type="input",event=dict(kind="penBegin",ticks=1,eventId=1,
            data=dict(x=60,y=30,heldKeys=[82]))))
        self.app.current_package_id = None
        for x in (60,70):
            self.app.engine.handle(dict(type="cursor",ticks=x,x=x,y=30,inCsp=True,
                foreground=True,pointerDown=True,relatedEventIds=[1],heldKeys=[82]))
        token = self.pipe.panel_activity[CANVAS]["token"]
        for x in (80,90):
            self.app.engine.handle(dict(type="cursor",ticks=x,x=x,y=30,inCsp=True,
                foreground=True,pointerDown=True,relatedEventIds=[1],heldKeys=[]))
        self.assertEqual(self.pipe.panel_activity[CANVAS]["token"],token)

    def test_scrollbar_mouse_motion_still_captures_without_navigation_key(self):
        self.app.engine.corrected_canvas_roi = [50,10,80,80]
        self.app.current_package_id = "scrollbar"
        self.app.engine.handle(dict(type="input",event=dict(kind="mouseDown",ticks=1,eventId=1,
            data=dict(x=45,y=30,heldKeys=[],button="left"))))
        self.app.current_package_id = None
        token = self.pipe.panel_activity[CANVAS]["token"]
        for y in (30,40):
            self.app.engine.handle(dict(type="cursor",ticks=y,x=45,y=y,inCsp=True,
                foreground=True,pointerDown=True,relatedEventIds=[1],heldKeys=[]))
        self.assertGreater(self.pipe.panel_activity[CANVAS]["token"],token)

    def test_intermediate_package_is_unknown_but_does_not_reopen_on_late_capture(self):
        self.activate([BRUSH],"old",0)
        self.activate([BRUSH],"new",.05)
        resolved = [m for m in self.records if m.get("type") == "timelineResult"]
        self.assertEqual(len(resolved),1)
        self.assertEqual(resolved[0]["data"]["status"],"unknown")
        self.pipe.expect_package("old",[BRUSH])
        self.assertNotIn("old",self.pipe.timeline.packages)
        self.assertFalse(self.pipe.timeline.complete_if_pending("old","brushState",None,{}))

    def test_jobs_wait_150ms_and_only_latest_version_is_eligible(self):
        self.activate([BRUSH],"first",0)
        first = self.pipe.panel_activity[BRUSH]["token"]
        self.activate([BRUSH],"last",.1)
        last = self.pipe.panel_activity[BRUSH]["token"]
        self.assertEqual(self.pipe._job_action(self.job(BRUSH,first,"first")),"superseded")
        self.assertEqual(self.pipe._job_action(self.job(BRUSH,last,"last",False)),"evidence")
        self.clock = .249
        self.assertEqual(self.pipe._job_action(self.job(BRUSH,last,"last")),"wait")
        self.clock = .250
        self.assertEqual(self.pipe._job_action(self.job(BRUSH,last,"last")),"analyze")

    def test_in_flight_older_result_cannot_publish_new_state_or_overlay(self):
        self.activate([CANVAS],"old",0)
        old = self.pipe.panel_activity[CANVAS]["token"]
        self.activate([CANVAS],"new",.1)
        self.messages.clear()
        self.pipe._complete_analysis(self.job(CANVAS,old,"old"),
                                    dict(success=True,canvasOriginScreenPx=dict(x=1,y=2)),0)
        self.assertFalse([m for m in self.messages if m.get("type") in {"canvasOrigin","canvasViewport"}])
        diagnostic = [m for m in self.records if m.get("kind") == "stateResult"][-1]
        self.assertTrue(diagnostic["data"]["superseded"])
        self.assertIn("new",self.pipe.timeline.packages)

    def test_current_canvas_success_is_published_when_an_unrelated_input_arrives(self):
        self.activate([CANVAS],"click",0)
        token = self.pipe.panel_activity[CANVAS]["token"]
        job = self.job(CANVAS,token,"click")
        job.update(causalAmbiguous=True,observedAfterEventId=11,refs=[9,10])
        result = dict(success=True,canvasOriginScreenPx=dict(x=1130,y=-163),
                      ocrScalePercent=30.2,ocrRotationDegrees=0)
        self.pipe._complete_analysis(job,result,0)
        update = next(m["data"] for m in self.records if m.get("kind") == "coreStateUpdated")
        self.assertEqual(update["status"],"changed")
        self.assertEqual(update["state"]["canvasOriginScreenPx"],result["canvasOriginScreenPx"])
        self.assertTrue(update["evidence"]["causalAmbiguous"])
        package = next(m for m in self.records if m.get("type") == "timelineResult")
        self.assertEqual(package["data"]["status"],"changed")
        self.assertIn(dict(type="canvasOrigin",point=result["canvasOriginScreenPx"]),self.messages)

    def test_close_drains_early_and_final_evidence_even_before_quiet(self):
        self.activate([BRUSH],"last",0)
        self.pipe.close = Mock()
        self.app.close()
        requests = self.captures()
        self.assertEqual([r[7]["finalEvidence"] for r in requests],[False,True])
        self.assertEqual([r[6] for r in requests],["last","last"])
        self.pipe.close.assert_called_once()

    def test_png_evidence_is_persisted_for_every_activation_and_only_last_is_dispatched(self):
        with evidence_directory() as directory:
            self.pipe.directory = Path(directory)
            for i,when in enumerate((0,.05,.1)):
                self.activate([BRUSH],f"p{i}",when)
            self.submit()
            requests = self.captures()
            self.clock = .250
            self.app.flush_pending_captures()
            self.submit()
            requests += self.captures()
            for i,request in enumerate(requests):
                panels,regions,ticks,refs,reason,initialize,package_id,analysis = request
                frame = Image.new("RGB",(20,20),(i*40,0,0))
                self.pipe.evidence_queue.put(({BRUSH:frame},{BRUSH:regions[BRUSH]},
                    {BRUSH:(i*10,i*10+1)},ticks,refs,reason,initialize,package_id,i*10,i*10+1,
                    panels,None,96,"MSS_ROI",None,analysis))
            self.pipe.evidence_queue.put(None)
            def frequency(pointer):
                pointer._obj.value = 10_000_000
            windll = SimpleNamespace(kernel32=SimpleNamespace(QueryPerformanceFrequency=frequency))
            with patch("pipeline.ctypes.windll",windll,create=True):
                self.pipe._persist_loop()
            evidence = [m for m in self.records if m.get("kind") == "screenshotBlob"]
            self.assertEqual(len(evidence),4)
            self.assertEqual(len({m["data"]["screenshotId"] for m in evidence}),4)
            self.assertEqual(len(list(self.pipe.directory.glob("*.png"))),4)
            analyzed = []
            self.pipe._analyze = analyzed.append
            self.pipe.executor = ThreadPoolExecutor(max_workers=2)
            self.pipe.stop.set()
            try:
                self.pipe._dispatch()
            finally:
                self.pipe.executor.shutdown()
            self.assertEqual(len(analyzed),1)
            self.assertEqual(analyzed[0]["packageId"],"p2")
            self.assertTrue(analyzed[0]["finalEvidence"])
            with Image.open(analyzed[0]["crops"][BRUSH]["path"]) as latest:
                self.assertEqual(latest.getpixel((0,0)),(120,0,0))
            self.assertEqual(len(list(self.pipe.directory.glob("*.superseded"))),2)
            self.assertEqual(len(list(self.pipe.directory.glob("*.evidence"))),1)
            self.assertEqual(len(list(self.pipe.directory.glob("*.done"))),1)

    def test_canvas_dispatch_analyzes_latest_immediate_png_without_a_quiet_recapture(self):
        with evidence_directory() as directory:
            self.pipe.directory = directory
            for i,when in enumerate((0,.05,.1)):
                self.activate([CANVAS],f"canvas{i}",when)
            self.submit()
            requests = self.captures()
            self.clock = .250
            self.app.flush_pending_captures()
            self.assertFalse(self.callbacks)
            self.assertEqual(len(requests),3)
            selected = {CANVAS:[40,0,100,100],NAVIGATOR:[140,0,20,20],
                        "导航器数字":[140,20,20,20],CANVAS_FRAME:[0,0,160,120]}
            for i,request in enumerate(requests):
                panels,regions,ticks,refs,reason,initialize,package_id,analysis = request
                frames = {p:Image.new("RGB",(r[2],r[3]),(i*40,0,0)) for p,r in selected.items()}
                spans = {p:(ticks+1,ticks+2) for p in selected}
                self.pipe.evidence_queue.put((frames,selected,spans,ticks,refs,reason,initialize,
                    package_id,ticks+1,ticks+2,panels,None,96,"MSS_FRAME_ROI",None,analysis))
            self.pipe.evidence_queue.put(None)
            def frequency(pointer):
                pointer._obj.value = 10_000_000
            windll = SimpleNamespace(kernel32=SimpleNamespace(QueryPerformanceFrequency=frequency))
            with patch("pipeline.ctypes.windll",windll,create=True):
                self.pipe._persist_loop()
            evidence = [m for m in self.records if m.get("kind") == "screenshotBlob"]
            self.assertEqual(len(evidence),12)
            self.assertEqual(len([m for m in evidence if m["data"]["panel"] == CANVAS_FRAME]),3)
            analyzed = []
            self.pipe._analyze = analyzed.append
            self.pipe.executor = ThreadPoolExecutor(max_workers=2)
            self.pipe.stop.set()
            try:
                self.pipe._dispatch()
            finally:
                self.pipe.executor.shutdown()
            self.assertEqual(len(analyzed),1)
            self.assertEqual(analyzed[0]["packageId"],"canvas2")
            self.assertEqual(analyzed[0]["triggerTicks"],1_000_000)
            with Image.open(analyzed[0]["crops"][CANVAS_FRAME]["path"]) as latest:
                self.assertEqual(latest.getpixel((0,0)),(80,0,0))
            self.assertEqual(len(list(directory.glob("*.superseded"))),2)
            self.assertEqual(len(list(directory.glob("*.evidence"))),0)
            self.assertEqual(len(list(directory.glob("*.done"))),1)


if __name__ == "__main__":
    unittest.main()
