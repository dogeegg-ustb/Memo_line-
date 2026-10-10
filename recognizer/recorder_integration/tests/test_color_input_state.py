"""Synthetic RGB editor pixels/OCR only; no live desktop capture or input."""
from importlib import import_module
import json
from pathlib import Path
import queue
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import cv2
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT), str(ROOT / 'recorder_integration'), str(ROOT.parent / 'CSP_Shortcut_Manager')]
from recognizer_core.panel_state.models import OcrText
from catalog import COLOR, COLOR_INPUT
from engine import UpdateEngine
from pipeline import Pipeline
from runtime import Runtime
from state_timeline import StateTimeline


def word(text, x, y, w=26, h=16):
    return OcrText(text, .99, [[x,y], [x+w,y], [x+w,y+h], [x,y+h]])


class ColorInputStateTests(unittest.TestCase):
    def fixture(self, row_labels='RGB', editors=True, roles=False):
        image = np.full((220, 320, 3), 50, dtype=np.uint8)
        items = [word(tab, 8, y, 38) for tab, y in zip(('RGB', 'HSV', 'CMYK'), (20, 52, 84))]
        ys = (50,85,120,155) if len(row_labels) == 4 else (50,90,130)
        for channel, value, y in zip(row_labels, ('12', '34', '56', '78'), ys):
            items.extend([word(channel, 60, y, 12), word(value, 240, y, 26)])
            if editors:
                cv2.rectangle(image, (225, y-6), (283, y+22), (140,140,140), 1)
                cv2.rectangle(image, (285, y-6), (303, y+22), (140,140,140), 1)
                cv2.line(image, (286,y+8), (302,y+8), (140,140,140), 1)
        if roles:
            for role, x in zip(('Main','Sub','Transparent'), (50,130,210)):
                items.append(word(role, x, 185, 50))
            cv2.rectangle(image, (44,177), (107,209), (200,100,30), 2)
        return image, items

    def read(self, image, items, **kwargs):
        cls = import_module('recognizer_core.color_input_state').ColorInputStateCore
        return cls(ocr_engine=lambda _: items).process(image, panel_roi=[-200,300,320,220],
            capture_frame_id='capture-1', **kwargs)

    def test_rgb_numbers_are_editor_value_boxes_from_the_same_frame(self):
        image, items = self.fixture(roles=True)
        result = self.read(image, items)
        self.assertEqual(result['status'], 'ambiguous')
        self.assertEqual(result['activeTab'], 'rgb')
        self.assertEqual(result['activeColorSlot'], 'unknown')
        self.assertEqual(result['captureFrameId'], 'capture-1')
        self.assertEqual(result['panelRoi'], [-200,300,320,220])
        self.assertEqual([(r['channel'],r['value'],r['screenBbox']) for r in result['valueRegions']],
            [('r',12,[40,350,26,16]),('g',34,[40,390,26,16]),('b',56,[40,430,26,16])])
        self.assertTrue(all(r['status'] == 'ok' for r in result['valueRegions']))
        self.assertFalse(any(r['screenBbox'][0] == 85 for r in result['valueRegions']))  # Arrow buttons.

    def test_unverified_capsule_roles_are_explicitly_unknown(self):
        result = self.read(*self.fixture())
        self.assertEqual(result['activeColorSlot'], 'unknown')
        self.assertEqual(result['status'], 'ambiguous')
        self.assertEqual(len(result['valueRegions']), 3)

    def test_decorative_role_text_and_border_are_not_proof_of_the_active_color_slot(self):
        result = self.read(*self.fixture(roles=True))
        self.assertEqual(result['activeColorSlot'], 'unknown')
        self.assertEqual(result['status'], 'ambiguous')

    def test_hidden_slider_or_read_only_color_wheel_has_no_click_targets(self):
        image = np.full((220,320,3),50,dtype=np.uint8)
        result = self.read(image, [word('R0 G0 B0',70,190,150)])
        self.assertEqual(result['status'], 'panelNotVisible')
        self.assertEqual(result['valueRegions'], [])

    def test_hsv_tab_is_reported_without_rgb_click_targets(self):
        result = self.read(*self.fixture('HSV'))
        self.assertEqual(result['status'], 'tabNotRgb')
        self.assertEqual(result['activeTab'], 'hsv')
        self.assertEqual(result['valueRegions'], [])

    def test_cmyk_tab_is_reported_without_rgb_click_targets(self):
        result = self.read(*self.fixture('CMYK'))
        self.assertEqual(result['status'], 'tabNotRgb')
        self.assertEqual(result['activeTab'], 'cmyk')
        self.assertEqual(result['valueRegions'], [])

    def test_borderless_rgb_digits_remain_observed_but_need_selected_capsule(self):
        result = self.read(*self.fixture(editors=False, roles=True))
        self.assertEqual(result['status'], 'ambiguous')
        self.assertTrue(all(r['status'] == 'ok' and r['screenBbox'] is not None for r in result['valueRegions']))
        self.assertEqual(result['activeColorSlot'],'unknown')

    def test_duplicate_values_and_out_of_range_values_cannot_become_targets(self):
        for bad in ('duplicate', 'range'):
            image, items = self.fixture(roles=True)
            if bad == 'duplicate': items.append(word('99',235,50,26))
            else: items = [word('256',240,50,26) if i.text == '12' else i for i in items]
            result = self.read(image,items)
            self.assertEqual(result['status'],'ambiguous')
            self.assertTrue(any(r['channel'] == 'r' and r['status'] != 'ok' for r in result['valueRegions']))

    def test_roi_and_capture_identity_are_required(self):
        cls = import_module('recognizer_core.color_input_state').ColorInputStateCore
        core = cls(ocr_engine=lambda _: [])
        image = np.zeros((220,320,3),dtype=np.uint8)
        with self.assertRaises(ValueError): core.process(image,panel_roi=[0,0,300,220],capture_frame_id='f')
        with self.assertRaises(ValueError): core.process(image,panel_roi=[0,0,320,220],capture_frame_id='')

    def valid_state(self):
        return dict(status='ok',activeTab='rgb',activeColorSlot='main',panelRoi=[-200,300,320,220],
            captureFrameId='capture-1',valueRegions=[dict(channel=c,value=v,screenBbox=[40,y,26,16],status='ok')
            for c,v,y in (('r',12,350),('g',34,390),('b',56,430))],tabRegions=[])

    def test_timeline_keeps_nonvisible_and_nonrgb_status_without_confirming_them(self):
        for status in ('panelNotVisible','tabNotRgb','ambiguous'):
            updates = []
            timeline = StateTimeline(lambda _:None,lambda _:None,lambda p,u,i,r: updates.append(u))
            raw = self.valid_state(); raw['status'] = status
            timeline.observe('colorInputState',raw,dict(captureId='capture-1',panelRoi=raw['panelRoi']))
            self.assertEqual(updates[0]['status'],'unknown')
            self.assertIsNone(updates[0]['state'])
            self.assertEqual(updates[0]['observedState']['status'],status)

    def test_semantic_state_and_live_publication_reject_a_different_capture_frame(self):
        records = []
        pipeline = Pipeline.__new__(Pipeline); pipeline.emit = records.append
        timeline = StateTimeline(lambda _:None,lambda _:None,pipeline._publish_core_update)
        raw = self.valid_state()
        evidence = dict(captureId='capture-1',panelRoi=raw['panelRoi'],capturedTicks=100,captureEndTicks=101)
        with patch('pipeline.now_ticks',return_value=200):
            timeline.observe('colorInputState',raw,evidence)
            timeline.observe('colorInputState',raw,{**evidence,'captureId':'different-frame'})
        self.assertEqual(records[0]['data']['state'],raw)
        self.assertEqual(records[0]['data']['rawResult'],raw)
        self.assertEqual(records[0]['data']['valueRegions'],raw['valueRegions'])
        self.assertEqual(records[1]['data']['status'],'unknown')
        self.assertIsNone(records[1]['data']['state'])

    def test_explicit_hidden_request_resolves_panel_not_visible_without_switching_tabs(self):
        runtime = Runtime.__new__(Runtime)
        updates = []; captures = []
        runtime.hwnd = 10; runtime.user32 = SimpleNamespace(GetForegroundWindow=lambda:10)
        runtime.engine = SimpleNamespace(suspended=False,regions={})
        runtime.pipeline = SimpleNamespace(timeline=StateTimeline(lambda _:None,lambda _:None,
            lambda p,u,i,r: updates.append(u)),clip_path=None)
        runtime.capture_panels = lambda *a,**k:captures.append((a,k))
        runtime.request_states(dict(requestId='r',packageId='p',modules=['colorInputState'],ticks=100))
        self.assertEqual(captures,[])
        self.assertEqual(len(updates),1)
        self.assertEqual(updates[0]['observedState']['status'],'panelNotVisible')

    def test_native_request_and_realtime_lists_include_the_new_module(self):
        source = (ROOT/'Recognizer/src/BehaviorRecognizer/Capture/RecorderStateRequest.cs').read_text(encoding='utf-8-sig')
        realtime = (ROOT/'core/memoline/Realtime/RecorderRealtimeHub.cs').read_text(encoding='utf-8-sig')
        self.assertIn('"colorInputState"',source)
        self.assertIn('"colorInputState"',realtime)

    def test_runtime_fresh_request_captures_the_registered_alias_without_any_click(self):
        runtime = Runtime.__new__(Runtime)
        captures = []
        runtime.hwnd = 10; runtime.user32 = SimpleNamespace(GetForegroundWindow=lambda:10)
        runtime.engine = SimpleNamespace(suspended=False,regions={COLOR_INPUT:[-200,300,320,220]})
        runtime.pipeline = SimpleNamespace(timeline=StateTimeline(lambda _:None,lambda _:None),clip_path=None)
        runtime.capture_panels = lambda *a,**k:captures.append((a,k))
        runtime.request_states(dict(requestId='rgb',packageId='p',modules=['colorInputState'],ticks=100))
        self.assertEqual(captures, [(({COLOR_INPUT},100),dict(reason='externalStateRequest:rgb',package_id='p',final=True))])
        self.assertEqual(runtime.pipeline.timeline.packages['p']['expected'],{'colorInputState'})

    def test_original_color_roi_is_reused_without_recalibration(self):
        runtime = Runtime.__new__(Runtime)
        captures = []
        roi = [-200,300,320,220]
        runtime.hwnd = 10; runtime.user32 = SimpleNamespace(GetForegroundWindow=lambda:10)
        runtime.engine = SimpleNamespace(suspended=False,regions={COLOR:roi})
        runtime.pipeline = SimpleNamespace(timeline=StateTimeline(lambda _:None,lambda _:None),clip_path=None)
        runtime.capture_panels = lambda *a,**k:captures.append((a,k))
        runtime.request_states(dict(requestId='rgb',packageId='p',modules=['colorInputState'],ticks=100))
        self.assertEqual(runtime.engine.regions[COLOR_INPUT],roi)
        self.assertEqual(captures[0][0],({COLOR_INPUT},100))
        engine = UpdateEngine(SimpleNamespace(),lambda _:None)
        engine.set_regions({COLOR:roi})
        self.assertEqual(engine.regions[COLOR_INPUT],roi)
        self.assertEqual(UpdateEngine.capture_targets([COLOR]) & engine.regions.keys(),{COLOR,COLOR_INPUT})

    def test_color_activation_and_initial_expectation_include_the_independent_input_reader(self):
        self.assertEqual(UpdateEngine.capture_targets([COLOR]),{COLOR,COLOR_INPUT})
        pipeline = Pipeline.__new__(Pipeline)
        pipeline.timeline = StateTimeline(lambda _:None,lambda _:None)
        pipeline.configure([8,8],None,None,'initial')
        self.assertIn('colorInputState',pipeline.timeline.packages['initial']['expected'])

    def test_pipeline_uses_the_supplied_pixels_and_slider_does_not_become_color_wheel_state(self):
        image,items = self.fixture(roles=True)
        cls = import_module('recognizer_core.color_input_state').ColorInputStateCore
        core = cls(ocr_engine=lambda pixels: items if pixels is image else [])
        pipeline = Pipeline.__new__(Pipeline)
        pipeline.analysis_lock = threading.RLock(); pipeline._analysis_current = lambda _:True
        pipeline.locks = {COLOR_INPUT:threading.Lock()}; wheel = Mock()
        pipeline.cores = {COLOR_INPUT:core,COLOR:wheel}; pipeline._complete_analysis = Mock()
        crop = dict(path='mock.png',roi=[-200,300,320,220])
        job = dict(module=COLOR_INPUT,crops={COLOR_INPUT:crop,COLOR:crop},captureId='capture-1')
        with patch('pipeline.now_ticks',return_value=100), patch('numpy.fromfile',return_value=np.zeros(1,dtype=np.uint8)), \
                patch('cv2.imdecode',return_value=image):
            pipeline._analyze(job)
            self.assertEqual(pipeline._complete_analysis.call_args.args[1]['status'],'ambiguous')
            self.assertEqual(pipeline._complete_analysis.call_args.args[1]['captureFrameId'],'capture-1')
            pipeline._analyze({**job,'module':COLOR})
        self.assertEqual(pipeline._complete_analysis.call_args.args[1]['kind'],'unknown')
        self.assertEqual(pipeline._complete_analysis.call_args.args[1]['detail'],'colorWheelNotVisible')
        wheel.process.assert_not_called()

    def test_mismatched_roi_bad_bounds_and_bad_capture_time_clear_current_confirmation(self):
        for wrong in ('roi','bounds','time'):
            raw = self.valid_state()
            evidence = dict(captureId='capture-1',panelRoi=raw['panelRoi'],capturedTicks=100,captureEndTicks=101)
            if wrong == 'roi': evidence['panelRoi'] = [0,0,320,220]
            elif wrong == 'bounds': raw['valueRegions'][0]['screenBbox'] = [500,350,26,16]
            else: evidence['captureEndTicks'] = 90
            timeline = StateTimeline(lambda _:None,lambda _:None)
            update = timeline.observe('colorInputState',raw,evidence)
            self.assertEqual(update['status'],'unknown')
            self.assertIsNone(update['state'])

    def test_malformed_value_region_is_unknown_instead_of_crashing_publication(self):
        raw = self.valid_state(); raw['valueRegions'][0] = None
        evidence = dict(captureId='capture-1',panelRoi=raw['panelRoi'],capturedTicks=100,captureEndTicks=101)
        update = StateTimeline(lambda _:None,lambda _:None).observe('colorInputState',raw,evidence)
        self.assertEqual(update['status'],'unknown')
        self.assertIsNone(update['state'])

    def test_persisted_jobs_keep_actual_slider_capture_span_and_color_wheel_guard(self):
        pipeline = Pipeline.__new__(Pipeline)
        pipeline.settings = dict(captureBudgetMs=100)
        pipeline.sequence = 0; pipeline.evidence_queue = queue.Queue()
        pipeline.finish_capture_watermark = lambda _:3
        pipeline.record = Mock(); pipeline.notify = Mock(); pipeline.timeline = Mock()
        selected = {COLOR:[-200,300,320,220],COLOR_INPUT:[-200,300,320,220]}
        frames = {p:Image.new('RGB',(320,220),(50,50,50)) for p in selected}
        spans = {p:(110,120) for p in selected}
        analysis = dict(evidenceId='same-frame',analysisToken=None,finalEvidence=True)
        pipeline.evidence_queue.put((frames,selected,spans,100,[3],'request',False,'fresh',
            110,900,set(selected),None,96,'MSS_ROI',None,analysis))
        pipeline.evidence_queue.put(None)
        def frequency(pointer): pointer._obj.value = 1000
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent,prefix='.color-input-') as directory:
            pipeline.directory = Path(directory)
            self.assertTrue(pipeline.directory.resolve().is_relative_to(Path(__file__).parent.resolve()))
            with patch('pipeline.ctypes.windll',SimpleNamespace(kernel32=SimpleNamespace(
                    QueryPerformanceFrequency=frequency)),create=True):
                pipeline._persist_loop()
            jobs = {j['module']:j for path in pipeline.directory.glob('*.job')
                    for j in [json.loads(path.read_text(encoding='utf-8'))]}
            with self.subTest(contract='wheelGuard'):
                self.assertIn(COLOR_INPUT,jobs[COLOR]['crops'])
            for key,expected in [('ticks',110),('captureEndTicks',120),('captureId','same-frame'),
                                 ('captureLatencyMs',20),('captureDurationMs',10)]:
                with self.subTest(contract=key):
                    self.assertEqual(jobs[COLOR_INPUT][key],expected)


if __name__ == '__main__': unittest.main()
