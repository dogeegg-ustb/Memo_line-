import base64
import os
import sys
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT), str(ROOT / 'recorder_integration'), str(ROOT.parent / 'CSP_Shortcut_Manager')]
os.environ.setdefault("MEMOLINE_CLOCK_ORIGIN", "0")
import floating_panels
import history_reader
from history_reader import HistoryReader
from engine import UpdateEngine

FIXTURE = ROOT / 'recognizer_core' / 'history_state' / 'tests' / 'history_three_active.png'


class FakeTimeline:
    def __init__(self):
        self.observed = []
        self.done = threading.Event()

    def observe(self, module, result, evidence, error=None):
        self.observed.append((module, result, evidence, error))
        self.done.set()


class FakePipeline:
    def __init__(self):
        self.timeline = FakeTimeline()
        self.records = []

    def record(self, kind, ticks, refs, data, path="immediate"):
        self.records.append((kind, ticks, list(refs), data))

    def begin_capture_watermark(self):
        return object()

    def finish_capture_watermark(self, ticket):
        return 13


class FakeClient:
    """The panel stash names the History window; its pixels are rendered in-process (patched here)."""
    def __init__(self, error=None):
        self.requests, self.error = [], error
        self.image = cv2.imdecode(np.fromfile(str(FIXTURE), np.uint8), cv2.IMREAD_COLOR)
        self.height, self.width = self.image.shape[:2]

    def request(self, **request):
        self.requests.append(request)
        if self.error:
            raise self.error
        return dict(success=True, windows=[dict(hwnd=7, kinds=["palettekindhistory"], currentKind="palettekindhistory",
                                                rect=[100, 50, 100 + self.width, 50 + self.height], onVirtualScreen=True)])


class FakeCore:
    def process(self, image):
        return dict(status='ok', rows=[dict(text='插画', undone=False), dict(text='G筆', undone=True)], currentIndex=0)


class HistoryReaderTests(unittest.TestCase):
    def reader(self, client):
        captured = []

        def capture(hwnd, rect):
            captured.append((hwnd, rect))
            return client.image, rect
        original = floating_panels.capture
        floating_panels.capture = capture
        self.addCleanup(setattr, floating_panels, "capture", original)
        self.captured = captured
        pipeline = FakePipeline()
        reader = HistoryReader(pipeline, dict(historyQuietMs=60), client)
        reader.core = FakeCore()
        self.addCleanup(reader.close)
        return pipeline, reader

    def test_a_burst_of_presses_is_read_once_after_the_keyboard_is_quiet(self):
        client = FakeClient()
        pipeline, reader = self.reader(client)
        with patch('pipeline.now_ticks', side_effect=range(1000, 2000)):
            for event in (11, 12, 13):
                reader.request(event * 10, event, 'undo')
                time.sleep(0.02)
            self.assertTrue(pipeline.timeline.done.wait(3))
            time.sleep(0.15)
        self.assertEqual(client.requests, [dict(command='listWindows')])
        self.assertEqual(self.captured, [(7, (100, 50, client.width, client.height))])
        module, result, evidence, error = pipeline.timeline.observed[0]
        self.assertEqual((module, error), ('historyState', None))
        self.assertEqual(evidence['relatedEventIds'], [11, 12, 13])
        self.assertEqual(evidence['commands'], ['undo'] * 3)
        self.assertEqual((evidence['triggerTicks'], evidence['observedAfterEventId']), (130, 13))
        self.assertFalse(evidence['causalAmbiguous'])
        blobs = [r for r in pipeline.records if r[0] == 'screenshotBlob']
        self.assertEqual(blobs[0][3]['screenshotId'], evidence['screenshotIds'][0])

    def test_without_the_panel_stash_the_reading_is_an_explicit_error(self):
        client = FakeClient(ConnectionError('面板收纳程序没有运行'))
        pipeline, reader = self.reader(client)
        with patch('pipeline.now_ticks', side_effect=range(1000, 2000)):
            reader.request(50, 5, 'redo')
            self.assertTrue(pipeline.timeline.done.wait(3))
        module, result, evidence, error = pipeline.timeline.observed[0]
        self.assertIsNone(result)
        self.assertIn('没有运行', error)
        self.assertEqual(evidence['commands'], ['redo'])


class EngineHistoryTriggerTests(unittest.TestCase):
    def test_only_undo_and_redo_shortcuts_ask_for_the_history(self):
        class Catalog:
            def match_key(self, vk, held):
                return {90: [dict(command='undo', targets=[])], 89: [dict(command='redo', targets=[])],
                        66: [dict(command='toolbrush', targets=[])]}.get(vk, [])
        engine = UpdateEngine(Catalog(), lambda message: None)
        calls = []
        engine.on_history = lambda event, command: calls.append((event['eventId'], command))
        for event_id, vk in ((1, 90), (2, 66), (3, 89), (4, 65)):
            engine.handle(dict(type='input', event=dict(kind='keyInput', ticks=event_id, eventId=event_id,
                                                         data=dict(vk=vk, heldKeys=[17]))))
        self.assertEqual(calls, [(1, 'undo'), (3, 'redo')])


if __name__ == '__main__':
    unittest.main()
