import sys
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
sys.path.insert(0,str(Path(__file__).resolve().parents[3]/'CSP_Shortcut_Manager'))

import pipeline
from catalog import BRUSH, COLOR, COLOR_INPUT, LAYERS, TOOLGROUP


class AnalysisWorkersTests(unittest.TestCase):
    def test_automatic_count_follows_the_cpu_within_the_module_count(self):
        for cpus, expected in ((None, 2), (4, 2), (16, 4), (32, 6), (128, 6)):
            with patch("pipeline.os.cpu_count", return_value=cpus):
                self.assertEqual(pipeline.analysis_workers(dict(analysisConcurrency=0)), expected)
                self.assertEqual(pipeline.analysis_workers({}), expected)

    def test_an_explicit_count_is_kept_but_never_exceeds_one_job_per_module(self):
        self.assertEqual(pipeline.analysis_workers(dict(analysisConcurrency=3)), 3)
        self.assertEqual(pipeline.analysis_workers(dict(analysisConcurrency=9)), len(pipeline.MODULES))


class CoreBuildTests(unittest.TestCase):
    def setUp(self):
        pipeline._prepared.clear()
        self.addCleanup(pipeline._prepared.clear)

    def test_cores_are_built_one_at_a_time_across_analysis_threads(self):
        active, peak, lock = [0], [0], threading.Lock()
        def build(module):
            with lock:
                active[0] += 1; peak[0] = max(peak[0], active[0])
            time.sleep(0.05)
            with lock:
                active[0] -= 1
            return object(), None
        with patch("pipeline._build_core", side_effect=build):
            threads = [threading.Thread(target=pipeline.take_core, args=(m,)) for m in (BRUSH, LAYERS, TOOLGROUP, COLOR_INPUT, COLOR)]
            for thread in threads: thread.start()
            for thread in threads: thread.join()
        self.assertEqual(peak[0], 1)

    def test_a_prepared_core_is_handed_out_once(self):
        prepared = object()
        with patch("pipeline._build_core", return_value=(prepared, None)):
            pipeline._prepare_core(LAYERS)
        with patch("pipeline._build_core", return_value=(object(), None)) as build:
            self.assertIs(pipeline.take_core(LAYERS), prepared)
            self.assertIsNot(pipeline.take_core(LAYERS), prepared)
            self.assertEqual(build.call_count, 1)


class SharedHostTests(unittest.TestCase):
    class Host:
        started = 0
        def __init__(self):
            type(self).started += 1
            self.requests, self.exited = [], None
        def poll(self):
            return self.exited
        def call(self, payload, timeout=120):
            self.requests.append(payload)
            return dict(success=True, scalePercent=17.9, rotationDegrees=0)
        def close(self):
            self.exited = 0

    def setUp(self):
        self.Host.started = 0
        patcher = patch("pipeline.TransformHost", self.Host)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.addCleanup(pipeline.close_shared_transform_host)
        pipeline.close_shared_transform_host()

    def test_setup_probe_and_pipeline_use_one_host_process(self):
        result = pipeline.probe_numbers("frame.png", [0, 0, 10, 10], [1, 1, 5, 5], 96)
        self.assertTrue(result["success"])
        host = pipeline.take_transform_host()
        self.assertEqual(self.Host.started, 1)
        self.assertTrue(host.requests[0]["probeNumbers"])
        # The pipeline owns it now; a later probe starts its own host.
        pipeline.probe_numbers("frame.png", [0, 0, 10, 10], [1, 1, 5, 5], 96)
        self.assertEqual(self.Host.started, 2)

    def test_an_exited_host_is_replaced(self):
        pipeline.shared_transform_host().exited = 1
        self.assertIsNone(pipeline.take_transform_host().exited)
        self.assertEqual(self.Host.started, 2)


if __name__ == "__main__":
    unittest.main()
