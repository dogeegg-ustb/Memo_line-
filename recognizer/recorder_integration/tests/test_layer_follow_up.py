import json
import os
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

import cv2
import numpy as np

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT / "recorder_integration"),str(ROOT.parent / "CSP_Shortcut_Manager")]
os.environ.setdefault("MEMOLINE_CLOCK_ORIGIN", "0")
from pipeline import Pipeline
from state_timeline import StateTimeline
from catalog import LAYERS


class FakeLayers:
    def __init__(self, names):
        self.names, self.calls = names, []

    def process_with_locator(self, pixels, panel_roi, capture_frame_id):
        self.calls.append(capture_frame_id)
        return dict(selectedName=self.names[int(pixels[0, 0, 0])])


class LayerFollowUpTests(unittest.TestCase):
    """A layer command whose panel read still shows the old state looks at the captures taken right after it
    (2026-10-24: 新建栅格图层 tapped right after the guard's Ctrl+S, the new layer showed only later)."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.pipe = Pipeline.__new__(Pipeline)
        self.pipe.directory = Path(self.folder.name)
        self.pipe.closing = False
        self.pipe.analysis_lock = threading.RLock()
        self.pipe.panel_activity = {}
        self.pipe.timeline = StateTimeline(lambda _: None, lambda _: None, lambda *_: None)
        with patch("pipeline.now_ticks", return_value=1):
            self.pipe.timeline.observe("currentLayerState", dict(selectedName="图层1"), dict(capturedTicks=1))
        self.core = FakeLayers({1: "图层1", 2: "图层2", 3: "图层1"})
        self.pipe.cores = {LAYERS: self.core}

    def tearDown(self):
        self.folder.cleanup()

    def image(self, value):
        return np.full((4, 4, 3), value, dtype=np.uint8)

    def follow_up(self, attempt, value, ticks, token=5, suffix="evidence"):
        path = self.pipe.directory / f"f{attempt}.png"
        cv2.imencode(".png", self.image(value))[1].tofile(str(path))
        job = dict(module=LAYERS, crops={LAYERS: dict(path=str(path), roi=[0, 0, 4, 4], screenshotId=f"s{attempt}")},
                   ticks=ticks, triggerTicks=90, refs=[248], reason=f"layerFollowUp{attempt}", initialize=False,
                   captureId=f"f{attempt}", packageId="p", causalAmbiguous=False, analysisToken=token, finalEvidence=False)
        (self.pipe.directory / f"{attempt:012d}_x.{suffix}").write_text(json.dumps(job), encoding="utf-8")

    def command(self, reason="pointerReleased"):
        return dict(module=LAYERS, crops={LAYERS: dict(path="main.png", roi=[0, 0, 4, 4])}, ticks=95, triggerTicks=90,
                    refs=[248], reason=reason, initialize=False, captureId="c1", packageId="p", analysisToken=5)

    def follow(self, job, result, pixels):
        with patch("pipeline.now_ticks", return_value=10**12), patch("pipeline.qpc_frequency", return_value=10**7):
            return self.pipe._follow_layer_command(job, result, pixels)

    def test_the_earliest_capture_showing_the_final_panel_is_published_for_the_command(self):
        for attempt, (value, ticks) in enumerate([(1, 100), (2, 110), (2, 120), (2, 130)], 1):
            self.follow_up(attempt, value, ticks, suffix="job" if attempt == 4 else "evidence")
        job, result = self.follow(self.command(), dict(selectedName="图层1"), self.image(1))
        self.assertEqual(result["selectedName"], "图层2")
        self.assertEqual((job["captureId"], job["ticks"], job["packageId"], job["refs"], job["triggerTicks"]),
                         ("f2", 110, "p", [248], 90))
        self.assertEqual(job["reason"], "layerFollowUp2:c1")
        self.assertTrue(job["finalEvidence"])
        self.assertEqual(self.core.calls, ["f2"])

    def test_a_panel_that_never_moved_publishes_the_command_capture_without_analysing_again(self):
        for attempt in range(1, 5):
            self.follow_up(attempt, 1, 100 + attempt)
        job, result = self.follow(self.command(), dict(selectedName="图层1"), self.image(1))
        self.assertEqual((job["captureId"], result["selectedName"]), ("c1", "图层1"))
        self.assertEqual(self.core.calls, [])

    def test_pixels_that_changed_without_changing_the_layer_keep_the_command_capture(self):
        for attempt in range(1, 5):
            self.follow_up(attempt, 3, 100 + attempt)  # hover highlight: other pixels, same layer
        job, _ = self.follow(self.command(), dict(selectedName="图层1"), self.image(1))
        self.assertEqual(job["captureId"], "c1")

    def test_a_changed_reading_or_another_trigger_is_published_at_once(self):
        self.follow_up(1, 2, 101)
        changed = self.command()
        self.assertIs(self.follow(changed, dict(selectedName="图层2"), self.image(1))[0], changed)
        for reason in ("externalStateRequest:r", "layerRetry:c0", "cursorEnter"):
            with self.subTest(reason=reason):
                job = self.command(reason)
                self.assertIs(self.follow(job, dict(selectedName="图层1"), self.image(1))[0], job)
        self.assertEqual(self.core.calls, [])

    def test_captures_of_another_command_are_not_used(self):
        for attempt in range(1, 5):
            self.follow_up(attempt, 2, 100 + attempt, token=4)
        job, _ = self.follow(self.command(), dict(selectedName="图层1"), self.image(1))
        self.assertEqual(job["captureId"], "c1")


class RuntimeFollowUpTests(unittest.TestCase):
    def test_follow_ups_are_taken_for_the_current_command_only(self):
        import runtime
        app = runtime.Runtime.__new__(runtime.Runtime)
        app.closing = False
        app.engine = type("Engine", (), dict(regions={LAYERS: [0, 0, 10, 10]}))()
        app.pipeline = Pipeline.__new__(Pipeline)
        app.pipeline.panel_activity = {LAYERS: dict(token=7, packageId="p")}
        app.pending_captures = {}
        calls = []
        app.capture_panels = lambda *args, **kwargs: calls.append((args, kwargs))
        app.follow_up_layers(90, [248], 6, 1)
        self.assertEqual(calls, [])
        app.follow_up_layers(90, [248], 7, 2)
        self.assertEqual(calls, [(([LAYERS], 90, [248], "layerFollowUp2"),
                                  dict(package_id="p", analysis_token=7, final=False))])


if __name__ == "__main__":
    unittest.main()
