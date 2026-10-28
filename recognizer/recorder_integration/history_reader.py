"""Read CSP's History palette after undo and redo, through the panel stash's interface.

The palette is not read on a screen region: it sits on the virtual screen (or anywhere else as a
floating window). The panel stash names its window; this process renders it with PrintWindow (a
fully stretched History palette is several megapixels, too much to move through the pipe). Only
undo/redo trigger a read. A burst of presses is read once, after the keyboard has been quiet for
`historyQuietMs`; that reading names every press of the burst in `relatedEventIds`.
"""
from __future__ import annotations

import base64
import hashlib
import json
import threading
import time
import uuid

import numpy as np

PIPE_PREFIX = "\\\\.\\pipe\\"
DEFAULT_PIPE = "MemoLine.PanelStash"
HISTORY_KIND = "palettekindhistory"
MODULE = "historyState"


class PanelStashClient:
    """One JSON line out, one JSON line back (see virtual_panel_probe/README.md)."""

    def __init__(self, name=DEFAULT_PIPE, timeout=3.0):
        self.path = PIPE_PREFIX + name
        self.timeout = timeout

    def request(self, **request):
        deadline = time.monotonic() + self.timeout
        while True:
            try:
                stream = open(self.path, "r+b", buffering=0)
                break
            except FileNotFoundError:
                raise ConnectionError("面板收纳程序没有运行") from None
            except OSError:
                # All pipe instances busy: the server opens a new one right after accepting.
                if time.monotonic() >= deadline:
                    raise ConnectionError("面板收纳程序没有响应") from None
                time.sleep(0.02)
        with stream:
            stream.write(json.dumps(request, ensure_ascii=False).encode("utf-8") + b"\n")
            buffer = bytearray()
            while not buffer.endswith(b"\n"):
                chunk = stream.read(1 << 16)
                if not chunk:
                    break
                buffer.extend(chunk)
        response = json.loads(buffer.decode("utf-8"))
        if not response.get("success"):
            raise RuntimeError(response.get("error") or "面板收纳程序返回失败")
        return response


class HistoryReader:
    def __init__(self, pipeline, settings, client=None):
        self.pipeline = pipeline
        self.quiet = max(0.05, float(settings.get("historyQuietMs", 250)) / 1000)
        self.client = client or PanelStashClient(settings.get("panelStashPipe") or DEFAULT_PIPE)
        self.core = None
        self.lock = threading.Condition()
        self.refs, self.commands, self.ticks, self.due = [], [], None, None
        self.closing = False
        self.thread = threading.Thread(target=self._loop, name="history-reader", daemon=True)
        self.thread.start()

    def request(self, ticks, event_id, command):
        with self.lock:
            self.refs.append(int(event_id))
            self.commands.append(command)
            self.ticks = ticks
            self.due = time.monotonic() + self.quiet
            self.lock.notify()

    def close(self):
        with self.lock:
            self.closing = True
            self.lock.notify()
        self.thread.join(timeout=10)

    def _loop(self):
        while True:
            with self.lock:
                while not self.closing and (self.due is None or time.monotonic() < self.due):
                    self.lock.wait(None if self.due is None else max(0.0, self.due - time.monotonic()))
                if self.due is None:
                    return
                refs, commands, ticks = self.refs, self.commands, self.ticks
                self.refs, self.commands, self.due = [], [], None
            try:
                self.read(ticks, refs, commands)
            except Exception as ex:  # one failed read must not stop later ones
                self.pipeline.record("analysisError", ticks, refs, dict(module=MODULE, error=str(ex)), "delayed")
            if self.closing and self.due is None:
                return

    def read(self, ticks, refs, commands):
        from pipeline import now_ticks
        ticket = self.pipeline.begin_capture_watermark()
        captured = now_ticks()
        evidence = dict(triggerTicks=ticks, relatedEventIds=list(refs), commands=list(commands), triggerReason="undoRedo")
        try:
            import floating_panels
            window = floating_panels.find_window(self.client, HISTORY_KIND)
            if window is None:
                raise RuntimeError("没有浮动的历史记录面板")
            pixels, rect = floating_panels.capture(window.hwnd, window.rect)
            shot = dict(rect=[rect[0], rect[1], rect[0] + rect[2], rect[1] + rect[3]], window=dict(hwnd=window.hwnd))
        except (ConnectionError, RuntimeError, OSError, ValueError) as ex:
            self.pipeline.finish_capture_watermark(ticket)
            evidence.update(capturedTicks=captured, captureEndTicks=now_ticks())
            self.pipeline.record("captureUnavailable", ticks, refs, dict(reason=f"历史面板：{ex}", panels=["历史记录"]))
            self.pipeline.timeline.observe(MODULE, None, evidence, f"历史面板不可读：{ex}")
            return
        captured_end = now_ticks()
        watermark = self.pipeline.finish_capture_watermark(ticket)
        screenshot_id = self._persist(pixels, shot, ticks, refs, captured, captured_end)
        if self.core is None:
            from pipeline import build_history_core
            self.core = build_history_core()
        result = self.core.process(np.ascontiguousarray(pixels))
        evidence.update(capturedTicks=captured, captureEndTicks=captured_end, screenshotIds=[screenshot_id],
                        panelRect=shot.get("rect"), window=shot.get("window", {}).get("hwnd"),
                        observedAfterEventId=watermark,
                        causalAmbiguous=watermark is None or watermark > max(refs), completedTicks=now_ticks(),
                captureBackend="PrintWindow")
        error = None if result.get("status") == "ok" else f"历史面板读取不完整：{result.get('status')} {result.get('reason', '')}".strip()
        self.pipeline.timeline.observe(MODULE, result, evidence, error)

    def _persist(self, pixels, shot, ticks, refs, captured, captured_end):
        """The read pixels are evidence like every panel screenshot: a PNG in the native file."""
        import io
        from PIL import Image
        buffer = io.BytesIO()
        Image.fromarray(np.ascontiguousarray(pixels[:, :, ::-1])).save(buffer, "PNG", compress_level=1)
        raw = buffer.getvalue()
        screenshot_id = uuid.uuid4().hex
        block = 512 * 1024
        parts = (len(raw) + block - 1) // block
        digest = hashlib.sha256(raw).hexdigest()
        for part in range(parts):
            self.pipeline.record("screenshotBlob", captured, refs, dict(
                screenshotId=screenshot_id, captureId=screenshot_id, panel="历史记录", roi=list(shot.get("rect") or []),
                mimeType="image/png", encoding="base64", image=base64.b64encode(raw[part * block:(part + 1) * block]).decode("ascii"),
                partIndex=part, partCount=parts, byteLength=len(raw), sha256=digest, triggerTicks=ticks,
                triggerReason="undoRedo", capturedTicks=captured, captureEndTicks=captured_end,
                captureBackend="PanelStash.PrintWindow"))
        return screenshot_id

