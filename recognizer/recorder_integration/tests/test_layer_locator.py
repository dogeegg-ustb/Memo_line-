import sys
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT), str(ROOT / 'recorder_integration'), str(ROOT.parent / 'CSP_Shortcut_Manager')]
from recognizer_core.layer_state.core import LayerStateCore
from recognizer_core.layer_state.layer_state import LayerSnapshot, LayerState
from pipeline import Pipeline
from state_timeline import StateTimeline


class LayerLocatorTests(unittest.TestCase):
    def test_legacy_name_and_locator_are_from_one_read_and_mapped_to_capture_pixels(self):
        image = np.zeros((100, 200, 3), dtype=np.uint8)
        prepared = np.zeros((150, 300, 3), dtype=np.uint8)
        row = LayerState('visual-row-0001', 'Ink', selected=True, visible=False,
                         evidence=[dict(row_bounds=[0, 30, 300, 60], eye_bounds=[0, 30, 28, 60])])
        core = LayerStateCore.__new__(LayerStateCore)
        core._reader = Mock()
        core._reader.read.return_value = (LayerSnapshot(layers=[row], status='ok'), prepared)
        result = core.process_with_locator(image, panel_roi=[-200, 300, 200, 100], capture_frame_id='frame-1')
        core._reader.read.assert_called_once_with(image)
        self.assertEqual(result['selectedName'], 'Ink')
        locator = result['layerLocator']
        self.assertEqual(locator['panelRoi'], [-200, 300, 200, 100])
        self.assertEqual(locator['captureFrameId'], 'frame-1')
        self.assertEqual(locator['rows'][0]['rowBounds'], [-200, 320, 200, 40])
        self.assertEqual(locator['rows'][0]['eyeBounds'], [-200, 320, 19, 40])
        self.assertFalse(locator['rows'][0]['visible'])
        self.assertTrue(locator['rows'][0]['selected'])
        core._reader.reset_mock()
        self.assertEqual(core.process(image), 'Ink')
        core._reader.read.assert_called_once_with(image)

    def test_unknown_or_duplicate_selection_does_not_invent_selected_name(self):
        image = np.zeros((100, 200, 3), dtype=np.uint8)
        core = LayerStateCore.__new__(LayerStateCore)
        core._reader = Mock()
        rows = [LayerState('1', 'Ink', selected=True), LayerState('2', 'Other', selected=True)]
        core._reader.read.return_value = (LayerSnapshot(layers=rows, status='ambiguous'), image)
        result = core.process_with_locator(image, panel_roi=[0, 0, 200, 100], capture_frame_id='f')
        self.assertIsNone(result['selectedName'])
        self.assertIsNone(core.process(image))
        self.assertEqual(result['layerLocator']['rows'][0]['eyeBounds'], None)

    def test_pipeline_keeps_string_semantics_and_same_frame_raw_locator(self):
        pipeline = Pipeline.__new__(Pipeline)
        records = []
        pipeline.emit = records.append
        timeline = StateTimeline(lambda _: None, lambda _: None, pipeline._publish_core_update)
        raw = dict(selectedName='Ink', layerLocator=dict(panelRoi=[10, 20, 200, 100],
                   captureFrameId='capture-2', rows=[dict(name='Ink', selected=True, visible=True,
                   rowBounds=[10, 40, 200, 30], eyeBounds=[10, 40, 19, 30])]))
        with patch('pipeline.now_ticks', return_value=200):
            timeline.observe('currentLayerState', raw, dict(captureId='capture-2', capturedTicks=100))
        data = records[0]['data']
        self.assertEqual(data['state'], 'Ink')
        self.assertEqual(data['rawResult']['layerLocator']['captureFrameId'], data['evidence']['captureId'])
        self.assertEqual(data['rawResult'], raw)


if __name__ == '__main__':
    unittest.main()
