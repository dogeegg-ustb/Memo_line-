"""While the user draws, analysis runs one job at a time; background requests wait for the pen to lift."""
import json
from pathlib import Path
import queue
import sys
import tempfile
import threading
import unittest
from concurrent.futures import Future
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "recorder_integration"),str(ROOT),str(ROOT.parent / "CSP_Shortcut_Manager")]
from catalog import BRUSH, COLOR, LAYERS
from pipeline import Pipeline, POINTER_QUIET_S
from runtime import Runtime


class OnePass(Exception):
    pass


class HeldExecutor:
    """Submitted jobs stay running, so one dispatcher pass shows how many may run at once."""
    def __init__(self):
        self.jobs = []

    def submit(self, function, job):
        self.jobs.append(job)
        return Future()


class DrawingDispatchTests(unittest.TestCase):
    def setUp(self):
        self.clock = 100.0
        clock = patch("pipeline.time.monotonic",side_effect=lambda:self.clock)
        clock.start()
        self.addCleanup(clock.stop)
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.pipe = Pipeline.__new__(Pipeline)
        self.pipe.directory = Path(temporary.name)
        self.pipe.settings = dict(analysisConcurrency=4)
        self.pipe.analysis_lock = threading.RLock()
        self.pipe.panel_activity = {}
        self.pipe.active = set()
        self.pipe.stop = threading.Event()
        self.pipe.closing = False
        self.pipe.pointer_down, self.pipe.pointer_changed = False, float("-inf")
        self.pipe.executor = HeldExecutor()
        self.sequence = 0

    def queue_job(self, module, background=False):
        self.sequence += 1
        job = dict(module=module,analysisToken=None,finalEvidence=True,packageId=f"p{self.sequence}",background=background)
        (self.pipe.directory / f"{self.sequence:012d}_{module}.job").write_text(json.dumps(job),encoding="utf-8")

    def dispatched(self):
        with patch("pipeline.time.sleep",side_effect=OnePass):
            with self.assertRaises(OnePass):
                self.pipe._dispatch()
        return [(job["module"],job["background"]) for job in self.pipe.executor.jobs]

    def test_jobs_run_side_by_side_when_nobody_draws(self):
        for module in (BRUSH,LAYERS,COLOR):
            self.queue_job(module)
        self.assertEqual(len(self.dispatched()),3)

    def test_only_one_job_runs_while_the_pen_is_down(self):
        for module in (BRUSH,LAYERS,COLOR):
            self.queue_job(module)
        self.pipe.observe_pointer(True)
        self.assertEqual(self.dispatched(),[(BRUSH,False)])

    def test_one_job_until_the_pointer_has_been_up_for_the_quiet_time(self):
        self.pipe.observe_pointer(True)
        self.clock += 1
        self.pipe.observe_pointer(False)
        self.clock += POINTER_QUIET_S - 0.05
        for module in (BRUSH,LAYERS):
            self.queue_job(module)
        self.assertEqual(len(self.dispatched()),1)
        self.clock += 0.1
        self.assertEqual(len(self.dispatched()),2)

    def test_background_analysis_waits_for_the_pen_and_runs_alone(self):
        self.queue_job(BRUSH,background=True)
        self.queue_job(LAYERS,background=True)
        self.pipe.observe_pointer(True)
        self.assertEqual(self.dispatched(),[])
        self.pipe.observe_pointer(False)
        self.clock += 5
        self.assertEqual(self.dispatched(),[(BRUSH,True)])

    def test_a_running_background_job_leaves_room_for_requests_someone_waits_for(self):
        self.queue_job(BRUSH,background=True)
        self.queue_job(LAYERS,background=True)
        self.queue_job(COLOR)
        self.assertEqual(self.dispatched(),[(BRUSH,True),(COLOR,False)])

    def test_closing_does_not_hold_background_jobs(self):
        self.queue_job(BRUSH,background=True)
        self.pipe.observe_pointer(True)
        self.pipe.closing = True
        self.assertEqual(self.dispatched(),[(BRUSH,True)])


class StateRequestBackgroundTests(unittest.TestCase):
    def test_runtime_passes_the_background_flag_to_the_capture(self):
        app = Runtime.__new__(Runtime)
        app.pipeline = type("P",(),{})()
        app.pipeline.timeline = type("T",(),{"expect":lambda *a:None,"not_requested":lambda *a:None})()
        app.engine = type("E",(),{"regions":{BRUSH:[0,0,10,10]},"suspended":False})()
        captured = []
        app.capture_panels = lambda panels,ticks,**options: captured.append(options)
        app.request_states(dict(packageId="p",modules=["brushState"],ticks=1,requestId="r",afterEventId=3,background=True))
        app.request_states(dict(packageId="q",modules=["brushState"],ticks=2,requestId="s",afterEventId=4))
        self.assertEqual([o.get("background",False) for o in captured],[True,False])

    def test_an_unchanged_shortcut_catalog_is_recorded_once(self):
        app = Runtime.__new__(Runtime)
        records = []
        app.engine = type("E",(),{"record":lambda self,kind,data,ticks=None:records.append((kind,ticks))})()
        app.recorded_catalog = None
        app.record_catalog({"bindings":[{"id":"tool_1","shortcut":"G"}]},0)
        app.record_catalog({"bindings":[{"shortcut":"G","id":"tool_1"}]},5)
        app.record_catalog({"bindings":[{"id":"tool_1","shortcut":"P"}]},9)
        self.assertEqual(records,[("shortcutConfiguration",0),("shortcutConfiguration",9)])


if __name__ == "__main__":
    unittest.main()
