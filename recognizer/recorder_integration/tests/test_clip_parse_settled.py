import itertools
import os
import sys
import tempfile
import time
import types
import unittest
from pathlib import Path
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(HERE), str(HERE.parent), str(HERE.parent.parent / 'CSP_Shortcut_Manager')]
os.environ.setdefault("MEMOLINE_CLOCK_ORIGIN", "0")
import pipeline
import state_timeline
import recognizer_core.clip_layers_core as clip_layers_core
from clip_witness import ClipWitness


class Clock:
    def __init__(self):
        self.ticks = itertools.count(1000, 10)

    def __call__(self):
        return next(self.ticks)


class WitnessFixture:
    def __init__(self, directory, writer_free=lambda path: True):
        self.directory = Path(directory)
        self.source = self.directory / "drawing.clip"
        self.source.write_bytes(b"saved before recording")
        self.records = []
        self.clock = Clock()
        self.witness = ClipWitness(self.source, self.directory, lambda *args: self.records.append(args), self.clock,
                                   poll_s=0.005, quiet_s=0.02, writer_free=writer_free)

    def write(self, content):
        self.source.write_bytes(content)
        stat = self.source.stat()
        os.utime(self.source, ns=(stat.st_atime_ns, stat.st_mtime_ns + 1_000_000))  # a distinct mtime per write

    def kept(self, count, timeout=2):
        deadline = time.monotonic() + timeout
        while len(self.witness.writes) < count and time.monotonic() < deadline:
            time.sleep(0.01)
        return self.witness.writes


class ClipWitnessTests(unittest.TestCase):
    """Every version CSP writes is kept as it is written; saves bind to the versions in order (2026-10-24)."""

    def test_versions_are_kept_as_written_and_bound_to_saves_in_order(self):
        with tempfile.TemporaryDirectory() as directory:
            f = WitnessFixture(directory)
            f.witness.start()
            try:
                self.assertTrue(f.kept(1)[0].preexisting)
                before_save = f.clock()
                f.write(b"written for the first save")
                write, observed = f.witness.bind_save(before_save, 2)
                self.assertTrue(observed)
                self.assertEqual(Path(write.path).read_bytes(), b"written for the first save")
                # A second save in the same window gets no version of its own: CSP wrote once.
                again, observed = f.witness.bind_save(before_save, 0.1)
                self.assertFalse(observed)
                self.assertEqual(again.id, write.id)
                kinds = [r[0] for r in f.records]
                self.assertEqual(kinds.count("clipWriteObserved"), 2)
            finally:
                f.witness.close()

    def test_a_version_is_kept_only_once_csp_let_go_of_the_file(self):
        with tempfile.TemporaryDirectory() as directory:
            held = [True]
            f = WitnessFixture(directory, writer_free=lambda path: not held[0])
            f.witness.start()
            try:
                time.sleep(0.1)
                self.assertEqual(f.witness.writes, [])
                held[0] = False
                self.assertEqual(len(f.kept(1)), 1)
            finally:
                f.witness.close()


class ClipParseTests(unittest.TestCase):
    def parse(self, directory, settled, bound, write=None):
        f = WitnessFixture(directory)
        f.witness.start()
        f.kept(1)
        trigger = f.clock()
        if write is not None:
            f.write(write)
        completed, observed = [], []
        pipe = types.SimpleNamespace(clip_path=str(f.source), directory=Path(directory), clip_witness=f.witness,
                                     CLIP_WRITE_TIMEOUT_S=0.3, CLIP_CATCH_UP_S=1.0,
                                     record=lambda *args: f.records.append(args),
                                     timeline=types.SimpleNamespace(complete=lambda *args: completed.append(args),
                                                                    observe=lambda *args: observed.append(args)))
        try:
            with patch.object(clip_layers_core, "read_clip_layers", lambda path: dict(layers=[], content=Path(path).read_bytes())):
                start = time.monotonic()
                pipeline.Pipeline._parse_clip(pipe, "save", trigger, "package" if settled or not bound else None, settled, bound)
                elapsed = time.monotonic() - start
        finally:
            f.witness.close()
        return f, completed or observed, elapsed

    def test_a_save_memoline_waited_out_is_parsed_at_once(self):
        with tempfile.TemporaryDirectory() as directory:
            f, published, elapsed = self.parse(directory, settled=True, bound=True)
            self.assertLess(elapsed, 0.3)
            package, module, result, evidence = published[0][:4]
            self.assertEqual((package, module, result["snapshotPath"]), ("package", "clipState", str(Path(directory) / "save.clip")))
            self.assertTrue(evidence["saveBound"])

    def test_the_recorders_save_is_the_version_written_after_it(self):
        with tempfile.TemporaryDirectory() as directory:
            f, published, _ = self.parse(directory, settled=False, bound=True, write=b"written after the save")
            module, result, evidence = published[0][:3]
            self.assertEqual(result["content"], b"written after the save")
            self.assertTrue(evidence["saveBound"] and evidence["writeObserved"])
            self.assertNotIn("clipSaveUnwritten", [r[0] for r in f.records])

    def test_a_save_csp_did_not_write_is_never_published_as_that_save(self):
        # 2026-10-24: the wrong .clip was watched and every save showed the same two-day-old file as "unchanged".
        with tempfile.TemporaryDirectory() as directory:
            f, published, elapsed = self.parse(directory, settled=False, bound=True)
            module, result, evidence = published[0][:3]
            self.assertGreaterEqual(elapsed, 0.3)
            self.assertEqual(result["content"], b"saved before recording")
            self.assertFalse(evidence["saveBound"])
            self.assertFalse(evidence["writeObserved"])
            self.assertIn("clipSaveUnwritten", [r[0] for r in f.records])

    def test_the_kept_copy_does_not_make_an_identical_structure_a_change(self):
        # The watcher seeds a new layer from rawResult.snapshotPath; every save has its own copy.
        first = dict(canvas=dict(current_layer_id=3), layers=[dict(id=3)], source="a.clip", snapshotPath="a.clip")
        second = dict(first, source="b.clip", snapshotPath="b.clip")
        self.assertEqual(state_timeline.semantic_state("clipState", first), state_timeline.semantic_state("clipState", second))


if __name__ == "__main__":
    unittest.main()
