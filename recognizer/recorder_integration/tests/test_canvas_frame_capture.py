"""Real-image capture/IPC regression checks; no live CSP or native core is started.

Set MEMOLINE_PIPELINE_TEST_IMAGE to a CSP screenshot when using another machine.
MSS and Windows APIs are mocked; PNG crop/save/load paths use Pillow normally.
"""
from __future__ import annotations

import itertools
import json
import os
from pathlib import Path
import queue
import sys
import threading
import time
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
import uuid

from PIL import Image, ImageDraw

INTEGRATION = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(INTEGRATION), str(INTEGRATION.parents[1] / "CSP_Shortcut_Manager")]
import pipeline
from catalog import BRUSH, CANVAS, NAVIGATOR, TOOLGROUP

FRAME = "__canvas_frame__"
NUMBERS = "导航器数字"
DEFAULT_IMAGE = Path("C:/Users/dogeegg/AppData/Local/Temp/"
                     "codex-clipboard-6b98953a-f6ca-4b08-811b-6d1ff32f2a9f.png")


class RecordedDesktop:
    def __init__(self, image):
        self.image = image
        # Negative virtual-desktop origins expose missing absolute/local offsets.
        self.monitors = [dict(left=-100, top=-50, width=image.width, height=image.height)]
        self.calls = []
        self.closed = False

    def grab(self, rectangle):
        rectangle = dict(rectangle)
        self.calls.append(rectangle)
        desktop = self.monitors[0]
        left = rectangle["left"] - desktop["left"]
        top = rectangle["top"] - desktop["top"]
        crop = self.image.crop((left, top, left + rectangle["width"], top + rectangle["height"]))
        try:
            return SimpleNamespace(size=crop.size, bgra=crop.tobytes("raw", "BGRX"),
                                   rgb=crop.tobytes())
        finally:
            crop.close()

    def close(self):
        self.closed = True


class RepaintingDesktop(RecordedDesktop):
    """Swaps the visible image before a grab, as CSP repaints between captures."""
    def __init__(self, image, before_grab):
        super().__init__(image)
        self.before_grab = before_grab
        self.full_frame_times = []

    def grab(self, rectangle):
        replacement = self.before_grab(len(self.calls), dict(rectangle))
        if replacement is not None:
            self.image = replacement
        if dict(rectangle) == self.monitors[0]:
            self.full_frame_times.append(time.monotonic())
        return super().grab(rectangle)


class CanvasFrameCaptureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(os.environ.get("MEMOLINE_PIPELINE_TEST_IMAGE", str(DEFAULT_IMAGE)))
        if not path.is_file():
            raise unittest.SkipTest("Provide MEMOLINE_PIPELINE_TEST_IMAGE for the real-image fixture")
        with Image.open(path) as image:
            cls.image = image.convert("RGB")
        if cls.image.width < 2050 or cls.image.height < 1200:
            raise unittest.SkipTest("Fixture must be at least 2050 x 1200 pixels")

    @classmethod
    def tearDownClass(cls):
        cls.image.close()

    def setUp(self):
        test_root = Path(__file__).resolve().parent
        directory = test_root / (".capture-regression-" + uuid.uuid4().hex)
        directory.mkdir()
        self.assertTrue(directory.resolve().is_relative_to(test_root))
        def cleanup():
            for file in directory.iterdir():
                file.unlink()
            directory.rmdir()
        self.addCleanup(cleanup)
        self.records, self.notices = [], []
        self.desktop = RecordedDesktop(self.image)
        self.canvas = (100, 80, 1320, 1050)
        self.navigator = (1640, 50, 300, 280)
        self.numbers = (1640, 355, 160, 60)
        self.brush = (0, 550, 260, 430)
        self.regions = {CANVAS: (80, 20, 1360, 1110),
                        NAVIGATOR: (1620, 0, 330, 440), BRUSH: self.brush}
        self.pipe = pipeline.Pipeline.__new__(pipeline.Pipeline)
        self.pipe.settings = dict(captureBudgetMs=100, navigatorCaptureMarginPx=32,
                                  canvasSettleMinMs=400, canvasSettleQuietMs=150,
                                  canvasSettleMaxMs=1000, canvasSettlePollMs=5)
        self.pipe.analysis_lock = threading.RLock()
        self.pipe.analysis_sequence = 0
        self.pipe.panel_activity = {}
        self.pipe.capture_queue = queue.Queue()
        self.pipe.evidence_queue = queue.Queue()
        self.pipe.directory = directory
        self.pipe.initialized = True
        self.pipe.canvas_roi = self.canvas
        self.pipe.navigator_roi = self.navigator
        self.pipe.numbers_roi = self.numbers
        self.pipe.dimensions = (4961, 7016)
        self.pipe.sequence = 0
        self.pipe.closing = False
        self.pipe.timeline = Mock()
        self.pipe.record = lambda kind, ticks, refs, data, path="immediate": self.records.append(
            dict(kind=kind, ticks=ticks, refs=refs, data=data, path=path))
        self.pipe.notify = self.notices.append
        self.pipe.begin_capture_watermark = lambda: None
        self.pipe.finish_capture_watermark = lambda _: 3
        user = SimpleNamespace(GetForegroundWindow=Mock(return_value=7),
                               GetDpiForWindow=Mock(return_value=144))
        def frequency(pointer):
            pointer._obj.value = 10_000_000
            return 1
        windll = SimpleNamespace(user32=user, kernel32=SimpleNamespace(
            QueryPerformanceFrequency=frequency))
        ticks = itertools.count(1000, 1000)
        for context in (patch.dict(sys.modules, {"mss": SimpleNamespace(mss=lambda: self.desktop)}),
                        patch.object(pipeline.ctypes, "windll", windll, create=True),
                        patch.object(pipeline, "now_ticks", side_effect=lambda: next(ticks)),
                        patch.dict(os.environ, {"MEMOLINE_CSP_HWND": "7"})):
            context.start()
            self.addCleanup(context.stop)

    def capture(self, panels=(CANVAS,), initialize=False):
        self.pipe.capture_queue.put((set(panels), self.regions.copy(), 10, [3],
                                     "initialization" if initialize else "pointerReleased",
                                     initialize, None,dict(analysisToken=None,finalEvidence=True)))
        self.pipe.capture_queue.put(None)
        self.pipe._capture_loop()
        self.assertTrue(self.notices[-1]["success"], self.records)
        self.assertEqual(self.pipe.evidence_queue.qsize(), 1)
        return self.pipe.evidence_queue.get_nowait()

    def expected(self, roi):
        x, y, width, height = roi
        monitor = self.desktop.monitors[0]
        return self.image.crop((x-monitor["left"], y-monitor["top"],
                                x-monitor["left"]+width, y-monitor["top"]+height))

    def assertPixels(self, image, roi):
        with self.expected(roi) as expected:
            self.assertEqual(image.size, expected.size)
            self.assertEqual(image.tobytes(), expected.tobytes())

    def persist(self, item):
        self.pipe.evidence_queue.put(item)
        self.pipe.evidence_queue.put(None)
        self.pipe._persist_loop()
        return [json.loads(path.read_text(encoding="utf-8"))
                for path in sorted(self.pipe.directory.glob("*.job"))]

    def test_canvas_batch_is_one_desktop_grab_with_exact_unexpanded_roi_pixels(self):
        item = self.capture((CANVAS, BRUSH))
        frames, selected, spans = item[:3]
        try:
            # No navigator polling before evidence: one exact full-desktop grab.
            full = [call for call in self.desktop.calls if call == self.desktop.monitors[0]]
            self.assertEqual(len(full), 1)
            self.assertEqual(self.desktop.calls, full)
            self.assertTrue(self.desktop.closed)
            self.assertEqual(selected[CANVAS], self.canvas)
            self.assertEqual(selected[NAVIGATOR], self.navigator)
            self.assertEqual(selected[NUMBERS], self.numbers)
            self.assertEqual(selected[BRUSH], self.brush)
            self.assertEqual(selected[FRAME], (-100, -50, self.image.width, self.image.height))
            self.assertEqual(set(frames), {CANVAS, NAVIGATOR, NUMBERS, BRUSH, FRAME})
            self.assertEqual(len(set(spans.values())), 1)
            for panel in frames:
                self.assertPixels(frames[panel], selected[panel])
        finally:
            for image in frames.values():
                image.close()

    def test_initialization_uses_configured_rois_without_expansion(self):
        self.pipe.initialized = False
        item = self.capture(initialize=True)
        frames, selected = item[:2]
        try:
            self.assertEqual(self.desktop.calls.count(self.desktop.monitors[0]), 1)
            self.assertEqual(selected[CANVAS], self.regions[CANVAS])
            self.assertEqual(selected[NAVIGATOR], self.regions[NAVIGATOR])
            for panel in frames:
                self.assertPixels(frames[panel], selected[panel])
        finally:
            for image in frames.values():
                image.close()

    def test_non_canvas_panel_still_acquires_only_its_exact_roi(self):
        item = self.capture((BRUSH,))
        frames, selected = item[:2]
        try:
            x, y, w, h = self.brush
            self.assertEqual(self.desktop.calls, [dict(left=x, top=y, width=w, height=h)])
            self.assertEqual(set(selected), {BRUSH})
            self.assertNotIn(FRAME, frames)
            self.assertPixels(frames[BRUSH], self.brush)
        finally:
            for image in frames.values():
                image.close()

    def test_persisted_full_frame_and_roi_evidence_share_capture_clock_without_extra_job(self):
        jobs = self.persist(self.capture((CANVAS, BRUSH)))
        self.assertEqual({job["module"] for job in jobs}, {CANVAS, BRUSH})
        canvas = next(job for job in jobs if job["module"] == CANVAS)
        self.assertEqual(set(canvas["crops"]), {CANVAS, NAVIGATOR, NUMBERS, FRAME})
        self.assertTrue(canvas["canvasCaptureValidation"]["sameFrame"])
        self.assertFalse(canvas["canvasCaptureValidation"]["stabilityChecked"])
        evidence = [entry for entry in self.records if entry["kind"] == "screenshotBlob"]
        self.assertEqual({entry["data"]["panel"] for entry in evidence},
                         {CANVAS, NAVIGATOR, NUMBERS, BRUSH, FRAME})
        self.assertEqual(len({entry["data"]["capturedTicks"] for entry in evidence}), 1)
        self.assertEqual(len({entry["data"]["captureEndTicks"] for entry in evidence}), 1)
        self.assertEqual(len({entry["data"]["captureId"] for entry in evidence}), 1)
        self.assertEqual(len({entry["ticks"] for entry in evidence}), 1)
        for crop in canvas["crops"].values():
            with Image.open(crop["path"]) as image:
                self.assertPixels(image.convert("RGB"), crop["roi"])

    def test_live_evidence_boundary_precedes_encoding_persistence_and_analysis(self):
        item = self.capture((CANVAS, BRUSH))
        early = [entry for entry in self.records if entry["kind"] == "coreEvidenceCaptured"]
        self.assertEqual({entry["data"]["module"] for entry in early}, {"canvasViewState", "brushState"})
        self.assertFalse(list(self.pipe.directory.iterdir()))
        self.assertFalse(any(entry["kind"] in {"screenshotBlob", "coreStateUpdated"} for entry in self.records))
        for entry in early:
            self.assertEqual(entry["path"], "immediate")
            self.assertEqual(entry["ticks"], 10)
            self.assertEqual(entry["data"]["evidence"]["triggerTicks"], 10)
            self.assertTrue(entry["data"]["evidence"]["encodingPending"])
            self.assertTrue(entry["data"]["evidence"]["analysisPending"])
        capture_id = early[0]["data"]["evidence"]["captureId"]
        jobs = self.persist(item)
        self.assertTrue(all(job["captureId"] == capture_id for job in jobs))
        kinds = [entry["kind"] for entry in self.records]
        self.assertLess(max(i for i, kind in enumerate(kinds) if kind == "coreEvidenceCaptured"), kinds.index("screenshotBlob"))

    def test_subtool_job_uses_its_exact_panel_capture_start_and_end(self):
        self.regions[TOOLGROUP]=(400,550,260,430)
        item=self.capture((BRUSH,TOOLGROUP))
        spans=item[2].copy()
        jobs=self.persist(item)
        job=next(job for job in jobs if job['module']==TOOLGROUP)
        self.assertEqual(job['ticks'],spans[TOOLGROUP][0])
        self.assertEqual(job['captureEndTicks'],spans[TOOLGROUP][1])
        self.assertEqual(job['crops'][TOOLGROUP]['roi'],list(self.regions[TOOLGROUP]))
        notice=next(entry for entry in self.records if entry['kind']=='coreEvidenceCaptured'
                    and entry['data']['module']=='subtoolState')
        self.assertEqual(notice['data']['evidence']['captureId'],job['captureId'])
        self.assertEqual(notice['data']['evidence']['capturedTicks'],job['ticks'])

    def test_canvas_ipc_sends_full_frame_and_three_exact_crop_descriptors(self):
        job = self.persist(self.capture())[0]
        self.pipe.transform = SimpleNamespace(poll=lambda: None)
        self.pipe.transform_ready = True
        self.pipe.transform_initial_payload = None
        sent = []
        def transform_call(payload):
            sent.append(payload)
            return dict(success=True, snapshot={}, canvasOriginScreenPx=dict(x=100, y=80))
        self.pipe._transform_call = transform_call
        self.pipe._analyze(job)
        self.assertFalse([entry for entry in self.records if entry["kind"] == "analysisError"])
        self.assertEqual(len(sent), 1)
        self.assertFalse(sent[0]["initialize"])
        self.assertEqual(sent[0]["frame"], job["crops"][FRAME])
        self.assertEqual(set(sent[0]["crops"]), {"workspace", "navigator", "numbers"})
        for key, panel in (("workspace", CANVAS), ("navigator", NAVIGATOR), ("numbers", NUMBERS)):
            self.assertEqual(sent[0]["crops"][key], job["crops"][panel])

    def repainted(self, image=None, offset=0):
        """Draws a red-frame edge into the Navigator thumbnail of a fixture copy."""
        image = image or self.image.copy()
        x, y, w, _ = self.navigator
        left, top = x - self.desktop.monitors[0]["left"], y - self.desktop.monitors[0]["top"]
        ImageDraw.Draw(image).line((left+5, top+10+offset, left+w-5, top+10+offset), fill=(255, 0, 0))
        return image

    def repainting(self, before_grab):
        self.desktop = RepaintingDesktop(self.image, before_grab)
        return self.desktop

    @staticmethod
    def release(item):
        for image in item[0].values():
            image.close()

    def test_old_400ms_settings_do_not_delay_or_poll_evidence(self):
        desktop = self.repainting(lambda n, rect: None)
        started = time.monotonic()
        with patch.object(pipeline.time,"sleep",side_effect=AssertionError("Capture must not wait for quiet")):
            item = self.capture()
        try:
            self.assertLess(desktop.full_frame_times[0] - started, 0.15)
            self.assertEqual(item[14]["settleWaitMs"],0)
            self.assertFalse(item[14]["stabilityChecked"])
            self.assertEqual(desktop.calls,[desktop.monitors[0]])
        finally:
            self.release(item)

    def test_repaint_during_grab_is_preserved_as_one_real_frame(self):
        repaint = self.repainted()
        self.addCleanup(repaint.close)
        desktop = self.repainting(lambda n, rect: repaint if rect == self.desktop.monitors[0] else None)
        item = self.capture()
        try:
            validation = item[14]
            self.assertTrue(validation["sameFrame"])
            self.assertFalse(validation["stabilityChecked"])
            self.assertEqual(len(desktop.full_frame_times), 1)
            self.assertEqual(item[0][FRAME].tobytes(), repaint.tobytes())
        finally:
            self.release(item)

    def test_continuous_repaints_keep_every_queued_capture(self):
        repaint = self.repainted()
        self.addCleanup(repaint.close)
        desktop = self.repainting(lambda n, rect: repaint if n % 2 else self.image)
        for trigger in (10,20,30):
            self.pipe.capture_queue.put(({CANVAS}, self.regions.copy(), trigger, [3],
                                         "mouseWheel", False, None,dict(analysisToken=trigger,finalEvidence=False)))
        self.pipe.capture_queue.put(None)
        self.pipe._capture_loop()
        items = [self.pipe.evidence_queue.get_nowait() for _ in range(3)]
        try:
            self.assertEqual(len(desktop.full_frame_times),3)
            self.assertEqual([item[3] for item in items],[10,20,30])
            self.assertEqual(items[0][0][FRAME].tobytes(),self.image.tobytes())
            self.assertEqual(items[1][0][FRAME].tobytes(),repaint.tobytes())
            self.assertEqual(items[2][0][FRAME].tobytes(),self.image.tobytes())
            self.assertTrue(all(item[14]["settleWaitMs"] == 0 for item in items))
        finally:
            for item in items:
                self.release(item)


if __name__ == "__main__":
    unittest.main()
