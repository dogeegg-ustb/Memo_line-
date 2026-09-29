from __future__ import annotations

import threading
from typing import Any

from .capture_service import CaptureError, CaptureService
from .models import RESULT_SCHEMA_VERSION, Profile, RecognitionOutcome, utc_now
from .recognition_pipeline import RecognitionPipeline, changed, profile_signature

try:
    from PySide6.QtCore import QObject, QRunnable, QThreadPool, QTimer, Signal
except ImportError:  # pragma: no cover
    QObject = object  # type: ignore
    QRunnable = object  # type: ignore
    QThreadPool = None  # type: ignore
    QTimer = None  # type: ignore
    Signal = None  # type: ignore


if Signal is not None:
    _OCR_POOL = QThreadPool()
    _OCR_POOL.setMaxThreadCount(1)

    class _Task(QRunnable):
        def __init__(self, owner: "RecognitionScheduler", force: bool):
            super().__init__()
            self.owner = owner
            self.force = force

        def run(self) -> None:
            self.owner._execute(self.force)


    class RecognitionScheduler(QObject):
        result_ready = Signal(object)
        message = Signal(str)
        running_changed = Signal(bool)

        def __init__(self, profile: Profile, capture: CaptureService, pipeline: RecognitionPipeline, target_window: Any = None, frozen_frame: Any = None):
            super().__init__()
            self.profile = profile
            self.capture = capture
            self.pipeline = pipeline
            self.target_window = target_window
            self.pool = _OCR_POOL
            self._frozen_frame = frozen_frame
            self._stopped = False
            self.timer = QTimer(self)
            self.timer.timeout.connect(lambda: self.request(False))
            self._lock = threading.Lock()
            self._busy = False
            self._pending = False
            self._pending_force = False
            self._previous_signature = None
            self._last_observed = None

        def request(self, force: bool = True) -> None:
            with self._lock:
                if self._stopped:
                    return
                if self._busy:
                    self._pending = True
                    self._pending_force = self._pending_force or force
                    return
                self._busy = True
            self.running_changed.emit(True)
            self.pool.start(_Task(self, force))

        def start(self) -> None:
            self.timer.start(max(100, int(self.profile.sampling.interval_ms)))
            self.request(True)

        def stop(self) -> None:
            self.timer.stop()
            with self._lock:
                self._stopped = True
                self._pending = False
                self._pending_force = False

        def _execute(self, force: bool) -> None:
            try:
                if self._stopped:
                    return
                frame = self._frozen_frame
                self._frozen_frame = None
                if frame is None:
                    frame = self.capture.capture(self.profile, self.target_window)
                signature = profile_signature(frame.image, self.profile)
                if not force and not changed(self._previous_signature, signature, self.profile.sampling.change_threshold):
                    self._last_observed = frame.captured_at
                    self.message.emit("画面未变化，跳过 OCR")
                    return
                outcome = self.pipeline.process(frame, self.profile)
                # Failed inference must remain retryable even if the screenshot is unchanged.
                self._previous_signature = signature if outcome.result.get("status") != "unavailable" else None
                self._last_observed = frame.captured_at
                if not self._stopped:
                    self.result_ready.emit(outcome)
            except Exception as exc:
                message = str(exc)
                status = "needs_reselection" if isinstance(exc, CaptureError) and message.startswith("needs_reselection") else "unavailable"
                if not self._stopped:
                    self.result_ready.emit(self._error_outcome(status, message))
                    self.message.emit(f"状态：{status}；{message}")
            finally:
                self._finish()

        def _error_outcome(self, status: str, reason: str) -> RecognitionOutcome:
            now = utc_now()
            return RecognitionOutcome({
                "schema_version": RESULT_SCHEMA_VERSION,
                "frame_id": self.capture.last_frame_id,
                "captured_at": now,
                "recognized_at": now,
                "last_observed_at": now,
                "status": status,
                "brush": {"name": "unknown", "status": "unknown", "candidates": [], "evidence": [], "properties": [], "source": "capture_error"},
                "unresolved": [{"status": status, "reason": reason}],
                "timings_ms": {"capture": 0, "ocr": 0, "parse": 0, "total": 0},
            })

        def _finish(self) -> None:
            with self._lock:
                if self._pending and not self._stopped:
                    force = self._pending_force
                    self._pending = False
                    self._pending_force = False
                    self.pool.start(_Task(self, force))
                    return
                self._busy = False
            self.running_changed.emit(False)
else:
    class RecognitionScheduler:  # type: ignore
        def __init__(self, *args: Any, **kwargs: Any):
            raise RuntimeError("PySide6 未安装，无法创建后台调度器")
