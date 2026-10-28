"""Real-image capture/IPC regression checks; no live CSP or native core is started.

Set MEMOLINE_PIPELINE_TEST_IMAGE to a CSP screenshot when using another machine.
CSP's window rendering (PrintWindow) and Windows APIs are mocked; PNG crop/save/load paths use Pillow normally.
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
import floating_panels
import pipeline
from catalog import BRUSH, CANVAS, NAVIGATOR, TOOLGROUP

FRAME = "__canvas_frame__"
NUMBERS = "导航器数字"
DEFAULT_IMAGE = Path("C:/Users/dogeegg/AppData/Local/Temp/"
                     "codex-clipboard-6b98953a-f6ca-4b08-811b-6d1ff32f2a9f.png")


class RecordedDesktop:
    """CSP's window as PrintWindow renders it: the fixture image, by default filling the monitor."""
    def __init__(self, image):
        self.image = image
        # Negative virtual-desktop origins expose missing absolute/local offsets.
        self.monitors = [dict(left=-100, top=-50, width=image.width, height=image.height)]
        self.window = (-100, -50, image.width, image.height)
        self.calls, self.regions = [], []

    def window_image(self, hwnd, region=None):
        self.calls.append(hwnd)
        self.regions.append(region)
        x, y, w, h = region or self.window
        left, top = x - self.monitors[0]["left"], y - self.monitors[0]["top"]
        return self.image.crop((left, top, left + w, top + h)), (x, y, w, h)


class RepaintingDesktop(RecordedDesktop):
    """Swaps the window's image before a render, as CSP repaints between captures."""
    def __init__(self, image, before_grab):
        super().__init__(image)
        self.before_grab = before_grab
        self.full_frame_times = []

    def window_image(self, hwnd, region=None):
        x, y, w, h = region or self.window
        replacement = self.before_grab(len(self.calls), dict(left=x, top=y, width=w, height=h))
        if replacement is not None:
            self.image = replacement
        self.full_frame_times.append(time.monotonic())
        return super().window_image(hwnd, region)


class CanvasFrameCaptureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(os.environ.get("MEMOLINE_PIPELINE_TEST_IMAGE", str(DEFAULT_IMAGE)))
        if path.is_file():
            with Image.open(path) as image:
                cls.image = image.convert("RGB")
        else:
            # Only exact pixel equality matters here: a fixed noise image exposes any offset error.
            import random
            generator = random.Random(20261018)
            cls.image = Image.frombytes("RGB", (2100, 1240), bytes(generator.getrandbits(8) for _ in range(2100 * 1240 * 3)))
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
        nav = self.regions[NAVIGATOR]
        self.pipe.numbers_relative = [self.numbers[0] - nav[0], self.numbers[1] - nav[1], *self.numbers[2:]]
        self.pipe.numbers_reference = None
        self.pipe.numbers_frame_roi = self.numbers
        # Initialized with the navigator docked at its region.
        self.pipe.navigator_reference = dict(rect=nav, image=self.screen_pixels(nav))
        self.pipe.floating = {}
        self.pipe.dimensions = (4961, 7016)
        self.pipe.sequence = 0
        self.pipe.closing = False
        self.pipe.timeline = Mock()
        self.pipe.record = lambda kind, ticks, refs, data, path="immediate": self.records.append(
            dict(kind=kind, ticks=ticks, refs=refs, data=data, path=path))
        self.pipe.notify = self.notices.append
        self.pipe.begin_capture_watermark = lambda: None
        self.pipe.finish_capture_watermark = lambda _: 3
        # Another application is in front of CSP (7): captures read CSP's own window and never ask.
        user = SimpleNamespace(GetForegroundWindow=Mock(return_value=99),
                               GetDpiForWindow=Mock(return_value=144))
        def frequency(pointer):
            pointer._obj.value = 10_000_000
            return 1
        windll = SimpleNamespace(user32=user, kernel32=SimpleNamespace(
            QueryPerformanceFrequency=frequency))
        ticks = itertools.count(1000, 1000)
        for context in (patch.object(floating_panels, "window_image", lambda hwnd, region=None: self.desktop.window_image(hwnd, region)),
                        patch.object(floating_panels, "window_rect", lambda hwnd: self.desktop.window),
                        patch.object(pipeline.ctypes, "windll", windll, create=True),
                        patch.object(pipeline, "now_ticks", side_effect=lambda: next(ticks)),
                        patch.dict(os.environ, {"MEMOLINE_CSP_HWND": "7"}),
                        # The frame is the monitor CSP is on: here the whole recorded desktop.
                        patch.object(floating_panels, "monitor_rect", lambda hwnd: tuple(
                            self.desktop.monitors[0][k] for k in ("left", "top", "width", "height"))),
                        patch.object(floating_panels, "same_process", lambda a, b: a == b)):
            context.start()
            self.addCleanup(context.stop)

    def screen_pixels(self, rect):
        import numpy as np
        x, y, w, h = rect
        left, top = x + 100, y + 50  # the recorded desktop starts at (-100, -50)
        with self.image.crop((left, top, left + w, top + h)) as crop:
            return np.asarray(crop).copy()

    def capture(self, panels=(CANVAS,), initialize=False, refs=(3,)):
        self.pipe.capture_queue.put((set(panels), self.regions.copy(), 10, list(refs),
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

    def test_canvas_batch_is_one_window_render_with_exact_unexpanded_roi_pixels(self):
        item = self.capture((CANVAS, BRUSH))
        frames, selected, spans = item[:3]
        try:
            # No navigator polling before evidence: one copy of CSP's whole window.
            self.assertEqual(self.desktop.calls, [7])
            self.assertEqual(self.desktop.regions, [None])
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
            self.assertEqual(self.desktop.calls, [7])
            self.assertEqual(selected[CANVAS], self.regions[CANVAS])
            self.assertEqual(selected[NAVIGATOR], self.regions[NAVIGATOR])
            for panel in frames:
                self.assertPixels(frames[panel], selected[panel])
        finally:
            for image in frames.values():
                image.close()

    def test_non_canvas_panel_is_its_exact_roi_of_the_window(self):
        item = self.capture((BRUSH,))
        frames, selected = item[:2]
        try:
            # Only the part of the window holding the panel is copied.
            self.assertEqual(self.desktop.calls, [7])
            self.assertEqual(self.desktop.regions, [self.brush])
            self.assertEqual(set(selected), {BRUSH})
            self.assertNotIn(FRAME, frames)
            self.assertPixels(frames[BRUSH], self.brush)
        finally:
            for image in frames.values():
                image.close()

    def test_several_panels_are_one_copy_of_the_part_holding_them(self):
        self.regions[TOOLGROUP] = (400, 550, 260, 430)
        item = self.capture((BRUSH, TOOLGROUP))
        try:
            self.assertEqual(self.desktop.regions, [(0, 550, 660, 430)])
            for panel in (BRUSH, TOOLGROUP):
                self.assertPixels(item[0][panel], item[1][panel])
        finally:
            self.release(item)

    def test_the_frame_is_black_where_csp_does_not_draw(self):
        # A CSP window smaller than its monitor: the desktop around it is not CSP's and is never read.
        self.desktop.window = (-60, -20, self.image.width - 80, self.image.height - 60)
        item = self.capture((CANVAS, BRUSH))
        try:
            frames, selected = item[:2]
            monitor = self.desktop.monitors[0]
            self.assertEqual(selected[FRAME], (monitor["left"], monitor["top"], monitor["width"], monitor["height"]))
            frame = frames[FRAME]
            self.assertEqual(frame.getpixel((0, 0)), (0, 0, 0))
            self.assertEqual(frame.getpixel((frame.width - 1, frame.height - 1)), (0, 0, 0))
            with frame.crop((40, 30, 40 + self.desktop.window[2], 30 + self.desktop.window[3])) as inside:
                self.assertPixels(inside, self.desktop.window)
            for panel in (CANVAS, NAVIGATOR, NUMBERS, BRUSH):
                self.assertPixels(frames[panel], selected[panel])
        finally:
            self.release(item)

    def test_a_panel_beyond_the_csp_window_is_not_read(self):
        self.desktop.window = (300, -50, self.image.width - 400, self.image.height)  # the brush panel is left of it
        self.pipe.capture_queue.put(({BRUSH}, self.regions.copy(), 10, [3], "pointerReleased", False, None,
                                     dict(analysisToken=None, finalEvidence=True)))
        self.pipe.capture_queue.put(None)
        self.pipe._capture_loop()
        self.assertFalse(self.notices[-1]["success"])
        self.assertIn("超出 CSP 窗口", next(r for r in self.records if r["kind"] == "captureUnavailable")["data"]["reason"])

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

    def test_a_requested_state_is_ambiguous_only_when_input_came_after_the_request(self):
        # The watermark at the screenshot is event 3 (see setUp).
        for refs, ambiguous in (((3,), False), ((2,), True), ((), True)):
            for job in self.pipe.directory.glob("*.job"):
                job.unlink()
            job = self.persist(self.capture((BRUSH,), refs=refs))[0]
            self.assertIs(job["causalAmbiguous"], ambiguous, refs)

    def test_successful_initialization_keeps_the_navigator_panel_as_reference(self):
        self.pipe.initialized, self.pipe.navigator_reference = False, None
        job = self.persist(self.capture(initialize=True))[0]
        self.pipe.transform = SimpleNamespace(poll=lambda: None)
        self.pipe.transform_ready = True
        self.pipe.transform_initial_payload = None
        box = lambda x, y, w, h: dict(left=x, top=y, width=w, height=h)
        self.pipe._transform_call = lambda payload: dict(success=True, snapshot={},
            canvasOriginScreenPx=dict(x=100, y=80), canvasWindowRoiScreenPx=box(*self.regions[CANVAS]),
            navigatorThumbnailRoiScreenPx=box(*self.navigator))
        self.pipe._analyze(job)
        self.assertFalse([entry for entry in self.records if entry["kind"] == "analysisError"])
        self.assertTrue(self.pipe.initialized)
        self.assertEqual(self.pipe.navigator_reference["rect"], tuple(self.regions[NAVIGATOR]))
        self.assertEqual(self.pipe.navigator_reference["image"].tobytes(),
                         self.screen_pixels(self.regions[NAVIGATOR]).tobytes())
        self.assertEqual(self.pipe.numbers_frame_roi, tuple(self.numbers))
        self.assertTrue(any(n.get("type") == "canvasInitialized" for n in self.notices))

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

    def window_capture(self, screen_box):
        """A floating window rendered by PrintWindow: the recorded desktop's pixels in `screen_box`
        (x, y, w, h), as BGR."""
        calls = []

        def capture(hwnd, rect):
            calls.append((hwnd, rect))
            return self.screen_pixels(screen_box)[:, :, ::-1].copy(), rect
        context = patch.object(floating_panels, "capture", capture)
        context.start()
        self.addCleanup(context.stop)
        return calls

    def test_a_floating_panel_is_read_from_its_own_window(self):
        rect = (3000, 1700, 260, 430)  # on the virtual screen, beyond the recorded desktop
        calls = self.window_capture((10, 20, 260, 430))
        self.pipe.floating = {BRUSH: floating_panels.FloatingPanel(BRUSH, 55, rect, "palettekindtooloption", True)}
        item = self.capture((BRUSH,))
        try:
            self.assertEqual(calls, [(55, rect)])
            self.assertEqual(self.desktop.calls, [])  # no screen grab at all
            self.assertEqual(item[1][BRUSH], rect)
            self.assertEqual(item[0][BRUSH].tobytes(), self.screen_pixels((10, 20, 260, 430)).tobytes())
            self.assertIn("PrintWindow", item[13])
        finally:
            self.release(item)

    def test_a_floating_navigator_is_put_back_where_the_core_was_initialized(self):
        # The window adds a 7 px frame and a title bar 13 px shorter than the dock's tab header.
        nx, ny, nw, nh = self.regions[NAVIGATOR]
        calls = self.window_capture((nx - 7, ny + 13, nw + 14, nh - 6))
        rect = (3000, 1700, nw + 14, nh - 6)
        self.pipe.floating = {NAVIGATOR: floating_panels.FloatingPanel(NAVIGATOR, 66, rect, "palettekindnavigator", True)}
        item = self.capture()
        try:
            frames, selected = item[:2]
            monitor = self.desktop.monitors[0]
            self.assertTrue(self.pipe.initialized)  # no new initialization: the ROIs stay
            self.assertEqual(selected[NAVIGATOR], self.navigator)
            self.assertEqual(selected[NUMBERS], self.numbers)
            self.assertEqual(selected[FRAME], (monitor["left"], monitor["top"], monitor["width"], monitor["height"]))
            self.assertEqual(calls, [(66, rect)])
            # The thumbnail ROI shows exactly what the docked navigator showed.
            self.assertPixels(frames[NAVIGATOR], self.navigator)
            self.assertPixels(frames[CANVAS], selected[CANVAS])
            self.assertEqual(item[14]["navigatorSource"], "floatingWindow")
            self.assertEqual(item[14]["navigatorAlignment"][:2], [7, -13])
            self.assertFalse(item[14]["sameFrame"])
        finally:
            self.release(item)

    def test_a_navigator_that_no_longer_lines_up_is_initialized_again(self):
        self.window_capture((100, 600, 330, 440))  # another part of the screen: nothing lines up
        self.pipe.floating = {NAVIGATOR: floating_panels.FloatingPanel(NAVIGATOR, 66, (500, 500, 330, 440), "palettekindnavigator", False)}
        self.pipe.capture_queue.put(({CANVAS}, self.regions.copy(), 10, [3], "pointerReleased", False, None,
                                     dict(analysisToken=None, finalEvidence=True)))
        self.pipe.capture_queue.put(None)
        self.pipe._capture_loop()
        self.assertFalse(self.notices[-1]["success"])
        self.assertIn("对不上", next(r for r in self.records if r["kind"] == "captureUnavailable")["data"]["reason"])
        self.assertFalse(self.pipe.initialized)

    def test_starting_with_a_floating_navigator_initializes_on_a_strip_beside_the_monitor(self):
        nx, ny, nw, nh = self.regions[NAVIGATOR]
        self.window_capture((nx - 7, ny + 13, nw + 14, nh - 6))
        # The digits were selected on the docked panel.
        self.pipe.numbers_reference = self.screen_pixels(self.regions[NAVIGATOR])
        self.pipe.initialized, self.pipe.navigator_reference = False, None
        self.pipe.floating = {NAVIGATOR: floating_panels.FloatingPanel(NAVIGATOR, 66, (500, 500, nw + 14, nh - 6), "palettekindnavigator", False)}
        item = self.capture(initialize=True)
        try:
            frames, selected = item[:2]
            monitor = self.desktop.monitors[0]
            strip = (monitor["left"] + monitor["width"], monitor["top"], nw + 14, nh - 6)
            self.assertEqual(selected[NAVIGATOR], strip)
            self.assertEqual(selected[FRAME], (monitor["left"], monitor["top"], monitor["width"] + nw + 14, monitor["height"]))
            # Docked offset plus the window's (7, -13): the same digits.
            dx, dy = self.numbers[0] - nx, self.numbers[1] - ny
            self.assertEqual(selected[NUMBERS], (strip[0] + dx + 7, strip[1] + dy - 13, *self.numbers[2:]))
            self.assertPixels(frames[NUMBERS], self.numbers)
        finally:
            self.release(item)

    def test_old_400ms_settings_do_not_delay_or_poll_evidence(self):
        desktop = self.repainting(lambda n, rect: None)
        started = time.monotonic()
        with patch.object(pipeline.time,"sleep",side_effect=AssertionError("Capture must not wait for quiet")):
            item = self.capture()
        try:
            self.assertLess(desktop.full_frame_times[0] - started, 0.15)
            self.assertEqual(item[14]["settleWaitMs"],0)
            self.assertFalse(item[14]["stabilityChecked"])
            self.assertEqual(desktop.calls,[7])
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
