"""Evidence first for the saved document: every version CSP writes to the .clip is kept as it is written.

A save request used to be parsed from whatever the file held once it had been still for a while. When CSP had not
written yet, had dropped the Ctrl+S, or was writing another document altogether (2026-10-24: the wrong .clip was
picked and fifteen "saves" were the same two-day-old file), that old file was published under the new save's trigger
as if CSP had saved it then. Now a watcher copies each version the moment CSP lets go of the file and records it
(clipWriteObserved); a save is bound afterwards to the first version written after it, or is recorded as unwritten.
"""
from __future__ import annotations

import ctypes
import hashlib
import os
import shutil
import threading
import time
import uuid
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class ClipWrite:
    id: str
    path: str
    sha256: str
    size: int
    mtime_ns: int
    observed_ticks: int  # when the watcher first saw this version on disk (QPC ticks of the recording)
    kept_ticks: int
    preexisting: bool = False


_GENERIC_READ, _FILE_SHARE_READ, _OPEN_EXISTING, _INVALID = 0x80000000, 1, 3, ctypes.c_void_p(-1).value


def writer_free(path):
    """True when nobody holds the file open for writing: opening it while denying writers succeeds."""
    kernel = ctypes.windll.kernel32
    kernel.CreateFileW.restype = ctypes.c_void_p
    handle = kernel.CreateFileW(str(path), _GENERIC_READ, _FILE_SHARE_READ, None, _OPEN_EXISTING, 0, None)
    if handle in (None, _INVALID):
        return False
    kernel.CloseHandle(ctypes.c_void_p(handle))
    return True


class ClipWitness:
    """Keeps every version of one .clip in `directory`; binds save requests to the versions in the order they come."""

    def __init__(self, path, directory, record, now_ticks, poll_s=0.02, quiet_s=0.06, writer_free=writer_free):
        self.source = Path(path)
        self.directory = Path(directory)
        self.record, self.now_ticks = record, now_ticks
        self.poll_s, self.quiet_s, self.writer_free = poll_s, quiet_s, writer_free
        self.writes: list[ClipWrite] = []
        self.bound: set[str] = set()
        self.changed = threading.Condition()
        self.stopping = threading.Event()
        self.thread = threading.Thread(target=self._watch, name="clip-witness", daemon=True)

    def start(self):
        self.thread.start()

    def close(self):
        self.stopping.set()
        with self.changed:
            self.changed.notify_all()
        if self.thread.is_alive():
            self.thread.join(timeout=2)

    def latest(self):
        with self.changed:
            return self.writes[-1] if self.writes else None

    def bind_save(self, ticks, timeout_s):
        """The first version written after a save request's trigger that no earlier request took, waited for up to
        `timeout_s` (CSP prepares 0.4-0.9 s before it writes, about 2 s right after a full-canvas paste). Without one
        the save made no write: (latest version, False)."""
        deadline = time.monotonic() + timeout_s
        with self.changed:
            while True:
                for write in self.writes:
                    if write.observed_ticks > ticks and not write.preexisting and write.id not in self.bound:
                        self.bound.add(write.id)
                        return write, True
                remaining = deadline - time.monotonic()
                if remaining <= 0 or self.stopping.is_set():
                    return (self.writes[-1] if self.writes else None), False
                self.changed.wait(remaining)

    def current(self, timeout_s):
        """The kept version equal to the file on disk now, once the watcher has it (it lags a write by ~0.1 s)."""
        deadline = time.monotonic() + timeout_s
        with self.changed:
            while True:
                try:
                    stat = self.source.stat()
                    signature = (stat.st_size, stat.st_mtime_ns)
                except OSError:
                    signature = None
                for write in reversed(self.writes):
                    if (write.size, write.mtime_ns) == signature:
                        return write
                remaining = deadline - time.monotonic()
                if remaining <= 0 or self.stopping.is_set():
                    return self.writes[-1] if self.writes else None
                self.changed.wait(min(remaining, self.poll_s * 2))

    def _watch(self):
        # First the file as it is when recording starts (preexisting: no save request is bound to it; CSP may hold
        # changes it has not written), then each version CSP writes.
        last, seen, preexisting = None, None, True
        while not self.stopping.is_set():
            try:
                stat = self.source.stat()
                signature = (stat.st_size, stat.st_mtime_ns)
            except OSError:
                signature = None
            if signature is not None and signature != last:
                if seen is None or seen[0] != signature:
                    seen = (signature, self.now_ticks(), time.monotonic())
                if (preexisting or time.monotonic() - seen[2] >= self.quiet_s) and self._keep(signature, seen[1], preexisting):
                    last, seen, preexisting = signature, None, False
            time.sleep(self.poll_s)

    def _keep(self, signature, observed_ticks, preexisting=False):
        """Copies the version with `signature` if CSP has let go of the file and it did not change meanwhile."""
        if not self.writer_free(self.source):
            return False
        write_id = uuid.uuid4().hex
        copy = self.directory / f"write-{write_id}.clip"
        try:
            shutil.copyfile(self.source, copy)
            after = self.source.stat()
            if (after.st_size, after.st_mtime_ns) != signature:
                copy.unlink(missing_ok=True)
                return False
            with copy.open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
        except OSError:
            copy.unlink(missing_ok=True)
            return False
        write = ClipWrite(write_id, str(copy), digest, signature[0], signature[1], observed_ticks, self.now_ticks(), preexisting)
        self.record("clipWriteObserved", observed_ticks, [], dict(writeId=write.id, snapshotPath=write.path,
            sourcePath=str(self.source), sha256=write.sha256, byteLength=write.size, mtimeNs=write.mtime_ns,
            observedTicks=write.observed_ticks, keptTicks=write.kept_ticks, preexisting=preexisting), "immediate")
        with self.changed:
            self.writes.append(write)
            self.changed.notify_all()
        return True


def link_or_copy(source, target):
    """The save's own name for a kept version (rawResult.snapshotPath), without a second copy where NTFS allows."""
    target = Path(target)
    if target.exists():
        target.unlink()
    try:
        os.link(source, target)
    except OSError:
        shutil.copyfile(source, target)
    return target
