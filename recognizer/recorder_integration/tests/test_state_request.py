from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'recorder_integration'), str(ROOT), str(ROOT.parent / 'CSP_Shortcut_Manager')]
from runtime import Runtime
from catalog import BRUSH, TOOLGROUP, LAYERS, COLOR, CANVAS, NAVIGATOR
from state_timeline import StateTimeline


class StateRequestTests(unittest.TestCase):
    def fixture(self, foreground=True):
        runtime = Runtime.__new__(Runtime)
        self.emitted, self.captures = [], []
        runtime.hwnd = 10
        runtime.user32 = types.SimpleNamespace(GetForegroundWindow=lambda: 10 if foreground else 20)
        runtime.engine = types.SimpleNamespace(suspended=False, regions={p: [0, 0, 100, 100] for p in
            (BRUSH, TOOLGROUP, LAYERS, COLOR, CANVAS, NAVIGATOR)})
        timeline = StateTimeline(self.emitted.append, lambda _: None,
            lambda package, update, initial, raw: self.emitted.append(dict(package=package, update=update)))
        runtime.pipeline = types.SimpleNamespace(timeline=timeline, clip_path=None)
        runtime.capture_panels = lambda panels, ticks, **kw: self.captures.append((panels, ticks, kw))
        runtime.initialization_configuration = {'clipPath': 'current.clip'}
        return runtime

    def test_query_captures_without_input_or_automatic_activation(self):
        runtime = self.fixture()
        runtime.request_states(dict(requestId='r', packageId='explicit-package', modules=['brushState','subtoolState','canvasViewState'], ticks=100))
        self.assertEqual(len(self.captures), 1)
        panels, ticks, options = self.captures[0]
        self.assertEqual(panels, {BRUSH, TOOLGROUP, CANVAS, NAVIGATOR})
        self.assertEqual(ticks, 100)
        self.assertEqual(options['package_id'], 'explicit-package')
        self.assertTrue(options['final'])
        self.assertEqual(options['reason'], 'externalStateRequest:r')
        self.assertEqual(runtime.pipeline.timeline.packages['explicit-package']['expected'], {'brushState','subtoolState','canvasViewState'})

    def test_hidden_or_background_panels_return_error_without_borrowing_old_state(self):
        runtime = self.fixture(foreground=False)
        runtime.request_states(dict(requestId='r', packageId='p', modules=['brushState','currentLayerState'], ticks=100))
        self.assertEqual(self.captures, [])
        results = [m['update'] for m in self.emitted if 'update' in m]
        self.assertEqual(len(results), 2)
        self.assertTrue(all(m['status'] == 'error' and m['state'] is None for m in results))
        self.assertIn('p', runtime.pipeline.timeline.resolved)

    def test_clip_query_preserves_save_identity_and_uses_current_document(self):
        runtime = self.fixture()
        queued = []
        runtime.pipeline.clip_path = 'current.clip'
        runtime.pipeline._parse_clip = object()
        runtime.pipeline.clip_executor = types.SimpleNamespace(submit=lambda *args: queued.append(args))
        runtime.request_states(dict(requestId='r', packageId='p', modules=['clipState'], ticks=100, saveId='our-save'))
        self.assertEqual(queued[0][1:], ('our-save', 100, 'p'))
        self.assertEqual(self.captures, [])

    def test_config_request_returns_direct_result_and_resolves_reserved_package(self):
        runtime = self.fixture()
        with patch('runtime.emit', side_effect=self.emitted.append):
            runtime.request_states(dict(requestId='r', packageId='p', modules=['initializationConfiguration'], ticks=100))
        self.assertEqual(self.emitted[0]['type'], 'stateRequestPart')
        self.assertEqual(self.emitted[0]['data']['clipPath'], 'current.clip')
        self.assertIn('p', runtime.pipeline.timeline.resolved)
        self.assertFalse(runtime.pipeline.timeline.packages)


if __name__ == '__main__':
    unittest.main()
