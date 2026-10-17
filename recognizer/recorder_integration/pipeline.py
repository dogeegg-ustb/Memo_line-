"""Capture every activation; analyze the latest evidence after per-panel quiet."""
from __future__ import annotations

import base64
import ctypes
import json
import os
from pathlib import Path
import queue
import shutil
import hashlib
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from concurrent.futures import ThreadPoolExecutor

from catalog import BRUSH, CANVAS, NAVIGATOR, LAYERS, COLOR, COLOR_INPUT, TOOLGROUP
from state_timeline import StateTimeline, MODULES

CANVAS_FRAME = "__canvas_frame__"


def now_ticks():
    value = ctypes.c_longlong()
    ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(value))
    return value.value - int(os.environ["MEMOLINE_CLOCK_ORIGIN"])


def transform_host_path():
    host = Path(__file__).parent / "transform_host" / "TransformHost.exe"
    return host if host.is_file() else host.parent / "publish" / host.name


def analysis_workers(settings):
    """Analysis jobs that may run at once. Each core stays serial, so more than one job per
    module never helps; 0 (the default) follows the CPU and leaves cores for CSP and the recorder."""
    configured = int(settings.get("analysisConcurrency", 0) or 0)
    if configured > 0:
        return min(configured, len(MODULES))
    return max(2, min(len(MODULES), (os.cpu_count() or 4) // 4))


class TransformHost:
    """One persistent canvas host process; JSONL requests are answered in order."""

    def __init__(self):
        self.process = subprocess.Popen([str(transform_host_path())], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=None, text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW)
        self.lines = queue.Queue()
        self.lock = threading.Lock()
        stream, lines = self.process.stdout, self.lines
        def receive():
            for line in stream:
                lines.put(line)
            lines.put("")
        threading.Thread(target=receive, daemon=True, name="transform-results").start()

    def poll(self):
        return self.process.poll()

    def call(self, payload, timeout=120):
        """Raises OSError when the request cannot be sent, TimeoutError (host killed) and EOFError."""
        with self.lock:
            try:
                self.process.stdin.write(json.dumps(payload)+"\n")
                self.process.stdin.flush()
            except OSError:
                self.kill()
                raise
            try:
                line = self.lines.get(timeout=timeout)
            except queue.Empty:
                self.kill()
                raise TimeoutError(timeout) from None
            if not line:
                raise EOFError("canvas host exited")
            return json.loads(line)

    def kill(self):
        if self.process.poll() is None:
            self.process.kill()

    def close(self):
        try:
            self.process.stdin.close()
            self.process.wait(timeout=10)
        except (OSError, subprocess.TimeoutExpired):
            self.kill()


# The setup probe and the pipeline share one host, started while the recorder is still being set up:
# its process start and OCR model load (about 2.7 s) then happen before the initial state package.
_host_lock = threading.Lock()
_shared_host = None


def shared_transform_host():
    global _shared_host
    with _host_lock:
        if _shared_host is None or _shared_host.poll() is not None:
            _shared_host = TransformHost()
        return _shared_host


def take_transform_host():
    """Hands the prepared host to the pipeline, which owns and closes it from then on."""
    global _shared_host
    with _host_lock:
        host, _shared_host = _shared_host, None
    return host if host is not None and host.poll() is None else TransformHost()


def close_shared_transform_host():
    global _shared_host
    with _host_lock:
        host, _shared_host = _shared_host, None
    if host is not None:
        host.close()


def warm_transform_host():
    """Starts the shared host and loads its OCR with a throwaway probe; a probe leaves the transform state untouched."""
    from PIL import Image, ImageDraw
    descriptor, name = tempfile.mkstemp(prefix="memoline-warm-", suffix=".png")
    os.close(descriptor)
    frame = Path(name)
    try:
        image = Image.new("RGB", (160, 48), (255, 255, 255))
        ImageDraw.Draw(image).text((8, 16), "100.0  0", fill=(0, 0, 0))
        image.save(frame)
        shared_transform_host().call(dict(probeNumbers=True, dpi=96, frame=dict(path=str(frame), roi=[0, 0, 160, 48]),
                                          crops=dict(numbers=dict(roi=[0, 0, 160, 48]))), timeout=60)
    finally:
        frame.unlink(missing_ok=True)


def probe_numbers(frame_path, frame_roi, numbers_roi, dpi, timeout=30):
    """Ask the canvas host whether a digit selection still reads both navigator numbers,
    with the same acceptance as initialization."""
    request = dict(probeNumbers=True, dpi=dpi, frame=dict(path=str(frame_path), roi=list(frame_roi)),
                   crops=dict(numbers=dict(roi=list(numbers_roi))))
    try:
        return shared_transform_host().call(request, timeout)
    except TimeoutError:
        raise RuntimeError(f"画布核心 {timeout} 秒内未返回数字识别结果") from None
    except EOFError:
        return dict(success=False, message="画布核心未返回数字识别结果")


OCR_PARAMS = {"EngineConfig.onnxruntime.intra_op_num_threads":2,
              "EngineConfig.onnxruntime.inter_op_num_threads":1,"Global.log_level":"warning"}
PREPARED_MODULES = (LAYERS, BRUSH, TOOLGROUP, COLOR_INPUT, COLOR)


# First imports of recognizer_core from several analysis threads deadlock on Python's module locks
# ("deadlock detected by _ModuleLock('recognizer_core.panel_state.core')", seen at startup on 2026-10-06
# and 2026-10-12), so cores are only ever built one at a time.
_build_lock = threading.Lock()


def build_core(module):
    """A new recognition core and its OCR engine (None for the color wheel)."""
    with _build_lock:
        return _build_core(module)


def _build_core(module):
    if module == COLOR:
        from recognizer_core.color_state import ColorStateCore
        return ColorStateCore(), None
    from rapidocr import RapidOCR
    ocr = RapidOCR(params=OCR_PARAMS)
    if module == BRUSH:
        from recognizer_core.panel_state import PanelStateCore
        return PanelStateCore(ocr_engine=ocr), ocr
    if module == LAYERS:
        from recognizer_core.layer_state import LayerStateCore
        return LayerStateCore(ocr_engine=ocr), ocr
    if module == TOOLGROUP:
        from recognizer_core.subtool_state import SubtoolPanelCore
        return SubtoolPanelCore(ocr_engine=ocr), ocr
    if module == COLOR_INPUT:
        from recognizer_core.color_input_state import ColorInputStateCore
        return ColorInputStateCore(ocr_engine=ocr), ocr
    raise ValueError(module)


_prepared = {}
_preparing = {module: threading.Lock() for module in PREPARED_MODULES}


def _prepare_core(module):
    with _preparing[module]:
        if module in _prepared:
            return
        core, ocr = build_core(module)
        if ocr is not None:
            # The first inference allocates onnxruntime's buffers; text makes both det and rec run.
            import cv2
            import numpy as np
            image = np.full((48, 200, 3), 255, np.uint8)
            cv2.putText(image, "Layer 100", (6, 34), cv2.FONT_HERSHEY_SIMPLEX, 1, (0, 0, 0), 2)
            ocr(image)
        _prepared[module] = core


def take_core(module):
    """The core prepared ahead of this module's first job (waiting while it still loads), or a new one."""
    lock = _preparing.get(module)
    if lock is not None:
        with lock:
            core = _prepared.pop(module, None)
        if core is not None:
            return core
    return build_core(module)[0]


def prepare_analysis():
    """Load the canvas host and every OCR core in the background while setup runs, so the
    initial state package does not pay for imports, model loads and first inferences."""
    def report(name, run):
        try:
            run()
        except Exception as ex:
            print(f"[Initialization] 预加载{name}失败，首次解析时再加载：{ex}", file=sys.stderr, flush=True)
    threading.Thread(target=report, args=("画布核心", warm_transform_host), daemon=True, name="prepare-canvas-host").start()
    def cores():
        # Import once on this thread; the builds then run side by side.
        import cv2, numpy, rapidocr  # noqa: F401
        import recognizer_core.panel_state, recognizer_core.layer_state, recognizer_core.subtool_state  # noqa: F401
        import recognizer_core.color_state, recognizer_core.color_input_state  # noqa: F401
        for module in PREPARED_MODULES:
            threading.Thread(target=report, args=(module, lambda m=module: _prepare_core(m)), daemon=True,
                             name="prepare-core").start()
    threading.Thread(target=report, args=("识别核心", cores), daemon=True, name="prepare-cores").start()


class Pipeline:
    def __init__(self, settings, emit, notify):
        self.settings, self.emit, self.notify = settings, emit, notify
        self.directory = Path(os.environ["MEMOLINE_SPOOL"])
        self.directory.mkdir(parents=True, exist_ok=True)
        self.capture_queue = queue.Queue()
        self.evidence_queue = queue.Queue(maxsize=4)
        self.stop = threading.Event()
        self.cores, self.locks = {}, {name: threading.Lock() for name in (BRUSH, TOOLGROUP, LAYERS, COLOR, COLOR_INPUT, CANVAS)}
        self.tool_catalog = {}
        self.transform = None
        self.transform_ready = False
        self.transform_initial_payload = None
        self.initialized = False
        self.canvas_roi = None
        self.navigator_roi = None
        self.dimensions = None
        self.numbers_roi = None
        self.clip_path = None
        self.sequence = 0
        self.active = set()
        self.timeline = StateTimeline(emit,notify,self._publish_core_update)
        self.watermarks = {}
        self.watermark_lock = threading.Lock()
        self.last_event_id = 0
        self.analysis_lock = threading.RLock()
        self.analysis_sequence = 0
        self.panel_activity = {}
        self.closing = False
        self.executor = ThreadPoolExecutor(max_workers=analysis_workers(settings), thread_name_prefix="state-core")
        self.clip_executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="clip-parser")
        self.capture_thread = threading.Thread(target=self._capture_loop, name="roi-capture")
        self.dispatch_thread = threading.Thread(target=self._dispatch, name="state-dispatch")
        self.evidence_thread = threading.Thread(target=self._persist_loop, name="roi-evidence")
        self.evidence_thread.start()
        self.capture_thread.start()
        self.dispatch_thread.start()

    def record(self, kind, ticks, refs, data, path="immediate"):
        self.emit(dict(type="state", kind=kind, ticks=ticks, relatedEventIds=list(refs), data=data, path=path))

    def _publish_core_update(self, package_id, update, initial, raw_result):
        evidence = dict(update.get("evidence") or {})
        completed = now_ticks()
        evidence.setdefault("completedTicks",completed)
        ticks = evidence.get("capturedTicks",evidence.get("observedTicks",completed))
        value_fields = {}
        if update.get('module') == 'colorInputState':
            # Clickable regions appear only alongside confirmed same-frame
            # semantics. Raw/observed diagnostics remain available when invalid.
            state = update.get('state') or {}
            value_fields = {key:state.get(key,[]) for key in ('valueRegions','tabRegions','colorSlotRegions')}
        if update.get("module") == "brushState":
            panel_roi = evidence.get("panelRoi")
            regions = raw_result.get("value_regions", []) if isinstance(raw_result, dict) else []
            projected = []
            for region in regions:
                item = dict(propertyKey=region["property_key"], propertyIndex=region["property_index"],
                    category=region["category"], value=region["value"], bbox=region["bbox"],
                    coordinateSpace="panel", source=region["source"], status=region["status"])
                for field in ("score", "text", "unit"):
                    if field in region:
                        item[field] = region[field]
                if panel_roi is not None:
                    x, y, w, h = region["bbox"]
                    item["screenBbox"] = [panel_roi[0]+x, panel_roi[1]+y, w, h]
                projected.append(item)
            value_fields["valueRegions"] = projected
        if update.get('module') == 'subtoolState':
            origin = evidence.get('panelRoi')
            for source,target in [('entries','ocrEntries'),('groupEntries','groupEntries'),('toolEntries','toolEntries')]:
                entries = raw_result.get(source, []) if isinstance(raw_result,dict) else []
                projected = []
                for entry in entries:
                    item = dict(entry)
                    if origin is not None:
                        x,y,w,h = item['bbox']
                        item['screenBbox'] = [origin[0]+x,origin[1]+y,w,h]
                    projected.append(item)
                value_fields[target] = projected
        self.record("coreStateUpdated",ticks,evidence.get("relatedEventIds",[]),dict(
            packageId=package_id,initial=initial,rawResult=raw_result,**{**update,"evidence":evidence},**value_fields),"delayed")

    def set_tool_catalog(self, catalog):
        self.tool_catalog = catalog

    def configure(self, dimensions, numbers_roi, clip_path,initial_package_id,subtools_available=False):
        self.dimensions, self.numbers_roi, self.clip_path = dimensions, numbers_roi, clip_path
        self.initial_package_id = initial_package_id
        modules = set(MODULES.values()) - {'subtoolState'}
        if subtools_available:
            modules.add('subtoolState')
        self.timeline.expect(initial_package_id,modules | ({"clipState"} if clip_path else set()),True)
        if clip_path:
            self.clip_executor.submit(self._parse_clip,uuid.uuid4().hex,0,initial_package_id)

    def expect_package(self,package_id,panels,initial=False):
        panels = set(panels)
        if panels & {CANVAS,NAVIGATOR}:
            panels.add(CANVAS)
        self.timeline.expect(package_id,{MODULES[p] for p in panels if p in MODULES},initial)

    @staticmethod
    def analysis_panels(panels):
        panels = set(panels)
        if NAVIGATOR in panels:
            panels.add(CANVAS)
        return panels & MODULES.keys()

    def activate(self, panels, ticks, refs, package_id=None):
        """Only analysis is debounced. Every activation still queues a capture."""
        with self.analysis_lock:
            self.analysis_sequence += 1
            token = self.analysis_sequence
            activated = time.monotonic()
            due = activated+max(0,float(self.settings.get("analysisQuietMs",150)))/1000
            for panel in self.analysis_panels(panels):
                previous = self.panel_activity.get(panel)
                self.panel_activity[panel] = dict(token=token,due=activated+self.analysis_quiet_ms(panel)/1000,
                    packageId=package_id,ticks=ticks,refs=list(refs))
                if previous and previous["packageId"] and previous["packageId"] != package_id:
                    self.timeline.complete_if_pending(previous["packageId"],MODULES[panel],None,dict(
                        reason="supersededBeforeAnalysis",deferredToPackageId=package_id,
                        relatedEventIds=previous["refs"],triggerTicks=previous["ticks"],
                        evidencePolicy="captureEveryActivation"))
            return token, due

    def analysis_quiet_ms(self, panel):
        # Canvas navigation has a fixed 150 ms analysis quiet period. Capture
        # runs immediately, including release, and renewed motion replaces its token.
        return 150 if panel == CANVAS else max(0,float(self.settings.get("analysisQuietMs",150)))

    def bind_activation(self, token, panels, package_id):
        with self.analysis_lock:
            for panel in self.analysis_panels(panels):
                current = self.panel_activity.get(panel)
                if current and current["token"] == token:
                    current["packageId"] = package_id
                else:
                    self.timeline.complete_if_pending(package_id,MODULES[panel],None,dict(
                        reason="supersededBeforeAnalysis",evidencePolicy="captureEveryActivation",
                        deferredToPackageId=current["packageId"] if current else None))

    def _analysis_current(self, job):
        token = job.get("analysisToken")
        current = self.panel_activity.get(job["module"])
        return token is None or bool(current and current["token"] == token)

    def _defer_job(self, job):
        current = self.panel_activity.get(job["module"])
        if job.get("packageId") and (not current or current["packageId"] != job["packageId"]):
            self.timeline.complete_if_pending(job["packageId"],MODULES[job["module"]],None,dict(
                reason="supersededBeforeAnalysis",evidenceRetained=True,
                deferredToPackageId=current["packageId"] if current else None,
                screenshotIds=[c["screenshotId"] for c in job["crops"].values()],
                capturedTicks=job["ticks"],triggerTicks=job["triggerTicks"],relatedEventIds=job["refs"]))

    def _job_action(self, job):
        if not self._analysis_current(job):
            return "superseded"
        if not job.get("finalEvidence",True):
            return "evidence"
        current = self.panel_activity.get(job["module"])
        if (job["module"] in self.active or (current and job.get("analysisToken") is not None
                and time.monotonic() < current["due"] and not self.closing)):
            return "wait"
        return "analyze"

    def watermark_reply(self,msg):
        with self.watermark_lock:
            self.last_event_id = msg["lastEventId"]
            if msg["requestId"] in self.watermarks:
                self.watermarks[msg["requestId"]].put(msg["lastEventId"])

    def begin_capture_watermark(self):
        if self.closing:
            return None
        request_id = uuid.uuid4().hex
        reply = queue.Queue()
        with self.watermark_lock:
            self.watermarks[request_id] = reply
        self.emit(dict(type="captureWatermark",requestId=request_id))
        return request_id, reply

    def finish_capture_watermark(self, ticket):
        if ticket is None:
            return self.last_event_id
        request_id, reply = ticket
        try:
            return reply.get(timeout=3)
        except queue.Empty:
            return None
        finally:
            with self.watermark_lock:
                self.watermarks.pop(request_id,None)

    def request_clip(self, save_id, ticks):
        if self.clip_path:
            self.clip_executor.submit(self._parse_clip,save_id,ticks)
        else:
            self.record("clipParseError",ticks,[],dict(saveId=save_id,error="尚未选择 .clip 路径；保存请求仍已登记"),"delayed")
            self.timeline.observe("clipState",None,dict(saveId=save_id,triggerTicks=ticks),"尚未选择 .clip 路径")

    def _parse_clip(self, save_id, ticks,package_id=None):
        try:
            from recognizer_core.clip_layers_core import read_clip_layers
            source = Path(self.clip_path)
            snapshot = self.directory / (save_id+".clip")
            deadline = time.monotonic()+15
            previous = None
            stable_since = time.monotonic()
            while time.monotonic()<deadline:
                try:
                    stat = source.stat()
                    signature = (stat.st_size,stat.st_mtime_ns)
                    if signature!=previous:
                        previous,stable_since = signature,time.monotonic()
                    elif time.monotonic()-stable_since>=0.6:
                        shutil.copyfile(source,snapshot)
                        after = source.stat()
                        if (after.st_size,after.st_mtime_ns)==signature:
                            result = read_clip_layers(snapshot)
                            with snapshot.open("rb") as stream:
                                digest = hashlib.file_digest(stream,"sha256").hexdigest()
                            self.record("clipParseResult",ticks,[],dict(saveId=save_id,result=result,
                                snapshotPath=str(snapshot),sourcePath=str(source),observedTicks=now_ticks(),
                                sha256=digest,
                                saveCompletionConfirmed=False),"delayed")
                            if package_id:
                                self.timeline.complete(package_id,"clipState",result,dict(saveId=save_id,observedTicks=now_ticks()))
                            else:
                                self.timeline.observe("clipState",result,dict(saveId=save_id,observedTicks=now_ticks(),
                                    triggerTicks=ticks,saveCompletionConfirmed=False))
                            return
                except (OSError,ValueError):
                    pass
                time.sleep(0.1)
            raise RuntimeError("15 秒内未取得可解析且写入稳定的 .clip 副本")
        except Exception as ex:
            self.record("clipParseError",ticks,[],dict(saveId=save_id,error=str(ex)),"delayed")
            if package_id:
                self.timeline.complete(package_id,"clipState",None,dict(saveId=save_id),str(ex))
            else:
                self.timeline.observe("clipState",None,dict(saveId=save_id,triggerTicks=ticks),str(ex))

    def capture(self, panels, regions, ticks, refs=(), reason="operation", initialize=False,package_id=None,
                analysis_token=None, final=True):
        if not self.stop.is_set():
            panels = set(panels)
            if BRUSH in panels and TOOLGROUP in regions:
                panels.add(TOOLGROUP)
            self.capture_queue.put((panels, dict(regions), ticks, list(refs), reason, initialize,package_id,
                                   dict(analysisToken=analysis_token,finalEvidence=final)))

    def _capture_loop(self):
        from PIL import Image
        import mss
        grabber = mss.mss()
        frequency = ctypes.c_longlong()
        ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(frequency))
        while True:
            request = self.capture_queue.get()
            if request is None:
                grabber.close()
                return
            panels, regions, ticks, refs, reason, initialize,package_id, analysis = request
            frames, capture_times = {}, {}
            captured_success = False
            try:
                hwnd = int(os.environ.get("MEMOLINE_CSP_HWND", "0"))
                user = ctypes.windll.user32
                user.GetForegroundWindow.restype = ctypes.c_void_p
                if not hwnd or user.GetForegroundWindow() != hwnd:
                    raise RuntimeError("CSP 主窗口不在前台，未截图；返回 CSP 后重新触发")
                if CANVAS in panels or NAVIGATOR in panels:
                    panels.update((CANVAS, NAVIGATOR))
                selected = {p: tuple(regions[p]) for p in panels if p in regions}
                if CANVAS in selected:
                    if not self.initialized:
                        initialize = True
                    if not initialize:
                        selected[CANVAS] = tuple(self.canvas_roi)
                        selected[NAVIGATOR] = tuple(self.navigator_roi)
                    selected["导航器数字"] = tuple(self.numbers_roi)
                if not selected:
                    raise RuntimeError("请求的面板 ROI 当前不可用")
                desktop = grabber.monitors[0]
                for x,y,w,h in selected.values():
                    if (w <= 0 or h <= 0 or x < desktop["left"] or y < desktop["top"] or
                            x+w > desktop["left"]+desktop["width"] or y+h > desktop["top"]+desktop["height"]):
                        raise RuntimeError("请求的面板 ROI 超出屏幕，未扩张或截断 ROI")
                captured = now_ticks()
                raw_frames = {}
                def acquire(panel):
                    if user.GetForegroundWindow() != hwnd:
                        raise RuntimeError("ROI 截图过程中 CSP 失去前台，已丢弃该批截图")
                    x,y,w,h = selected[panel]
                    start = now_ticks()
                    pixels = grabber.grab(dict(left=x,top=y,width=w,height=h))
                    return pixels, (start,now_ticks())
                canvas_validation = None
                capture_backend = "MSS_ROI"
                if CANVAS in selected:
                    # The native solver uses true pixels beyond its thumbnail ROI.
                    # Capture once; ROI evidence and native context share this frame,
                    # with no resize, padding, synthetic gaps or navigator expansion.
                    selected[CANVAS_FRAME] = tuple(desktop[k] for k in ("left","top","width","height"))
                    pixels,span = acquire(CANVAS_FRAME)
                    image = Image.frombytes("RGB",pixels.size,pixels.bgra,"raw","BGRX")
                    frames[CANVAS_FRAME] = image
                    captured,capture_end = span
                    if user.GetForegroundWindow() != hwnd:
                        raise RuntimeError("完整帧截图过程中 CSP 失去前台，已丢弃该批截图")
                    for panel,(x,y,w,h) in selected.items():
                        capture_times[panel] = span
                        if panel != CANVAS_FRAME:
                            left,top = x-desktop["left"],y-desktop["top"]
                            frames[panel] = image.crop((left,top,left+w,top+h))
                    canvas_validation = dict(sameFrame=True,verifiedTicks=capture_end,
                        stabilityChecked=False,settleWaitMs=0,policy="immediateEvidenceThenQuietAnalysis")
                    capture_backend = "MSS_FRAME_ROI"
                else:
                    acquired_rois = {}
                    for panel in selected:
                        roi = selected[panel]
                        if roi not in acquired_rois:
                            acquired_rois[roi] = acquire(panel)
                        raw_frames[panel],capture_times[panel] = acquired_rois[roi]
                    capture_end = now_ticks()
                for panel,pixels in raw_frames.items():
                    frames[panel] = Image.frombytes("RGB",pixels.size,pixels.bgra,"raw","BGRX")
                user.GetDpiForWindow.argtypes = [ctypes.c_void_p]
                user.GetDpiForWindow.restype = ctypes.c_uint
                dpi = user.GetDpiForWindow(hwnd) or 96
                # Publish when the evidence pixels exist, before PNG encoding,
                # disk persistence, quiet-period scheduling or core analysis.
                analysis = dict(analysis, evidenceId=uuid.uuid4().hex)
                for panel in selected:
                    if panel in MODULES:
                        start, end = capture_times[panel]
                        self.record("coreEvidenceCaptured", ticks, refs, dict(
                            module=MODULES[panel], packageId=package_id, initial=initialize,
                            evidence=dict(triggerTicks=ticks, capturedTicks=start, captureEndTicks=end,
                                captureId=analysis["evidenceId"], relatedEventIds=list(refs),
                                triggerReason=reason, roi=list(selected[panel]), roiRole="panelCapture",
                                analysisToken=analysis["analysisToken"], finalEvidence=analysis["finalEvidence"],
                                analysisQuietMs=self.analysis_quiet_ms(panel),
                                encodingPending=True, analysisPending=True)), "immediate")
                ticket = self.begin_capture_watermark()
                self.evidence_queue.put((frames,selected,capture_times,ticks,refs,reason,initialize,
                    package_id,captured,capture_end,panels,ticket,dpi,capture_backend,canvas_validation,analysis))
                captured_success = True
            except Exception as ex:
                self.record("captureUnavailable",ticks,refs,dict(reason=str(ex),panels=sorted(panels)))
                if initialize:
                    self.notify(dict(type="initializationFailed",message=str(ex)))
                if package_id and (analysis["finalEvidence"] or analysis["analysisToken"] is None):
                    for p in set(panels) | ({CANVAS} if NAVIGATOR in panels else set()):
                        if p in MODULES:
                            self.timeline.complete_if_pending(package_id,MODULES[p],None,dict(triggerTicks=ticks),str(ex))
            finally:
                if not captured_success:
                    for image in frames.values():
                        image.close()
                self.notify(dict(type="captureFinished",success=captured_success,panels=sorted(panels)))

    def _persist_loop(self):
        frequency = ctypes.c_longlong()
        ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(frequency))
        while True:
            item = self.evidence_queue.get()
            if item is None:
                return
            frames, selected, capture_times, ticks, refs, reason, initialize, package_id, captured, capture_end, panels, ticket, dpi, capture_backend, canvas_validation, analysis = item
            latency_ms = (capture_end-ticks)*1000/frequency.value
            duration_ms = (capture_end-captured)*1000/frequency.value
            budget_ms = self.settings.get("captureBudgetMs",100)
            try:
                watermark = self.finish_capture_watermark(ticket)
                ambiguous = not initialize and (watermark is None or not refs or watermark > max(refs))
                capture_id = analysis.get("evidenceId") or uuid.uuid4().hex
                crops = {}
                for panel, (x, y, w, h) in selected.items():
                    screenshot_id = uuid.uuid4().hex
                    file = self.directory / (screenshot_id + ".png")
                    frames[panel].save(file,compress_level=1)
                    roi_start,roi_end = capture_times[panel]
                    crop = dict(screenshotId=screenshot_id, path=str(file), roi=[x,y,w,h], panel=panel,
                                capturedTicks=roi_start,captureEndTicks=roi_end)
                    crops[panel] = crop
                    raw = file.read_bytes()
                    block_size = 512*1024
                    part_count = (len(raw)+block_size-1)//block_size
                    digest = hashlib.sha256(raw).hexdigest()
                    for part in range(part_count):
                        self.record("screenshotBlob", roi_start, refs, dict(screenshotId=screenshot_id,
                            captureId=capture_id, panel=panel, roi=crop["roi"], mimeType="image/png",
                            encoding="base64", image=base64.b64encode(raw[part*block_size:(part+1)*block_size]).decode("ascii"),
                            partIndex=part,partCount=part_count,byteLength=len(raw),sha256=digest,
                            triggerTicks=ticks, triggerReason=reason, capturedTicks=roi_start, captureEndTicks=roi_end,
                            captureBatchStartTicks=captured,captureBatchEndTicks=capture_end,
                            captureLatencyMs=(roi_end-ticks)*1000/frequency.value,
                            captureDurationMs=(roi_end-roi_start)*1000/frequency.value,
                            captureBudgetMs=budget_ms, captureBudgetExceeded=(roi_end-ticks)*1000/frequency.value>budget_ms,dpi=dpi,captureBackend=capture_backend,
                            navigatorThumbnailRoi=list(selected[NAVIGATOR]) if panel == NAVIGATOR and not initialize else None,
                            canvasCaptureValidation=canvas_validation if panel in (CANVAS,NAVIGATOR,"导航器数字",CANVAS_FRAME) else None,
                            analysisToken=analysis["analysisToken"],finalEvidence=analysis["finalEvidence"],
                            statePackageId=package_id))
                modules = [p for p in crops if p not in (NAVIGATOR, "导航器数字",CANVAS_FRAME)]
                for module in modules:
                    group = {p: crops[p] for p in (CANVAS, NAVIGATOR, "导航器数字",CANVAS_FRAME)} if module == CANVAS else {module: crops[module]}
                    if module == BRUSH and TOOLGROUP in crops:
                        group[TOOLGROUP] = crops[TOOLGROUP]
                    if module == COLOR and COLOR_INPUT in crops:
                        group[COLOR_INPUT] = crops[COLOR_INPUT]
                    self.sequence += 1
                    sample_start, sample_end = capture_times[module] if module in (TOOLGROUP,COLOR_INPUT) else (capture_end,capture_end)
                    job = dict(module=module, crops=group, ticks=sample_start, triggerTicks=ticks,
                               refs=refs, reason=reason, initialize=initialize, captureId=capture_id,
                               packageId=package_id,causalAmbiguous=ambiguous,observedAfterEventId=watermark,dpi=dpi,captureBackend=capture_backend,
                               captureEndTicks=sample_end,
                               captureLatencyMs=(sample_end-ticks)*1000/frequency.value if module in (TOOLGROUP,COLOR_INPUT) else latency_ms,
                               captureDurationMs=(sample_end-sample_start)*1000/frequency.value if module in (TOOLGROUP,COLOR_INPUT) else duration_ms,
                               captureBudgetMs=budget_ms,captureBudgetExceeded=(sample_end-ticks)*1000/frequency.value>budget_ms,
                               canvasCaptureValidation=canvas_validation if module == CANVAS else None,**analysis)
                    target = self.directory / f"{self.sequence:012d}_{uuid.uuid4().hex}.job"
                    temporary = target.with_suffix(".tmp")
                    temporary.write_text(json.dumps(job, ensure_ascii=False), encoding="utf-8")
                    temporary.replace(target)
            except Exception as ex:
                self.record("captureUnavailable", ticks, refs, dict(reason=str(ex), panels=sorted(panels)))
                if initialize:
                    self.notify(dict(type="initializationFailed", message=str(ex)))
                if package_id and (analysis["finalEvidence"] or analysis["analysisToken"] is None):
                    for p in panels:
                        if p in MODULES:
                            self.timeline.complete_if_pending(package_id,MODULES[p],None,dict(triggerTicks=ticks),str(ex))
            finally:
                for image in frames.values():
                    image.close()

    def _dispatch(self):
        running = {}
        while not self.stop.is_set() or any(self.directory.glob("*.job")) or running:
            for future, (file, module) in list(running.items()):
                if future.done():
                    running.pop(future)
                    self.active.discard(module)
                    # Keep failed job inputs on disk for diagnosis; consumed metadata is marked.
                    file.replace(file.with_suffix(".done"))
            workers = analysis_workers(self.settings)
            if len(running) < workers:
                for file in sorted(self.directory.glob("*.job")):
                    if any(file == entry[0] for entry in running.values()):
                        continue
                    job = json.loads(file.read_text(encoding="utf-8"))
                    module = job["module"]
                    with self.analysis_lock:
                        action = self._job_action(job)
                        if action == "superseded":
                            self._defer_job(job)
                            file.replace(file.with_suffix(".superseded"))
                            continue
                        if action == "evidence":
                            # Metadata and PNG evidence remain available. This capture
                            # is deliberately not analyzed, not canceled or overwritten.
                            file.replace(file.with_suffix(".evidence"))
                            continue
                        if action == "wait":
                            continue
                        self.active.add(module)
                        running[self.executor.submit(self._analyze, job)] = (file, module)
                    if len(running) >= workers:
                        break
            time.sleep(0.025)

    def _analyze(self, job):
        module, crops = job["module"], job["crops"]
        result = None
        with self.analysis_lock:
            if not self._analysis_current(job):
                self._defer_job(job)
                return
        analysis_started = now_ticks()
        try:
            if module == CANVAS:
                if self.transform is None or self.transform.poll() is not None:
                    self.transform_ready = False
                    self.transform = take_transform_host()
                if not self.transform_ready and self.transform_initial_payload:
                    restored = self._transform_call(self.transform_initial_payload)
                    if not restored.get("success"):
                        raise RuntimeError("画布宿主恢复初始证据失败："+restored.get("message", "unknown"))
                    for key,expected in (("canvasWindowRoiScreenPx",self.canvas_roi),
                                         ("navigatorThumbnailRoiScreenPx",self.navigator_roi)):
                        r = restored[key]
                        if [r[k] for k in ("left","top","width","height")] != expected:
                            raise RuntimeError("画布宿主恢复返回了不同 ROI，请重新初始化")
                    self.transform_ready = True
                payload = dict(initialize=job["initialize"] and not self.initialized, width=self.dimensions[0], height=self.dimensions[1],dpi=job.get("dpi",96),captureBackend=job.get("captureBackend"),
                    frame=crops[CANVAS_FRAME],
                    crops={key:crops[panel] for key,panel in (("workspace",CANVAS),("navigator",NAVIGATOR),("numbers","导航器数字"))})
                result = self._transform_call(payload)
                if payload["initialize"]:
                    if result.get("success"):
                        r = result["canvasWindowRoiScreenPx"]
                        self.canvas_roi = [r["left"],r["top"],r["width"],r["height"]]
                        r = result["navigatorThumbnailRoiScreenPx"]
                        self.navigator_roi = [r["left"],r["top"],r["width"],r["height"]]
                        if self.navigator_roi[2] <= 0 or self.navigator_roi[3] <= 0:
                            raise RuntimeError("画布核心返回了无效的导航器缩略图 ROI")
                        self.initialized = True
                        self.transform_ready = True
                        if payload["initialize"]:
                            self.transform_initial_payload = payload
                        self.notify(dict(type="canvasInitialized", roi=self.canvas_roi,navigatorRoi=self.navigator_roi))
                    else:
                        self.notify(dict(type="initializationFailed", message=result.get("message", "画布初始化失败")))
            else:
                import cv2
                import numpy as np
                if module not in self.cores and module != COLOR_INPUT:
                    self.cores[module] = take_core(module)
                pixels = cv2.imdecode(np.fromfile(crops[module]["path"], dtype=np.uint8), cv2.IMREAD_COLOR)
                if module == TOOLGROUP:
                    result = self.cores[module].process(pixels,catalog=self.tool_catalog)
                elif module == BRUSH:
                    companion = crops.get(TOOLGROUP)
                    subtool_pixels = (cv2.imdecode(np.fromfile(companion['path'], dtype=np.uint8), cv2.IMREAD_COLOR)
                                      if companion else None)
                    result = self.cores[module].process(pixels,tool_catalog=self.tool_catalog,
                        subtool_image=subtool_pixels,subtool_evidence=({
                            **{k:companion.get(k) for k in ('screenshotId','roi','capturedTicks','captureEndTicks')},
                            'captureId':job['captureId']} if companion else None))
                elif module == LAYERS:
                    result = self.cores[module].process_with_locator(pixels,
                        panel_roi=list(crops[module]['roi']), capture_frame_id=job['captureId'])
                elif module == COLOR_INPUT:
                    result = self._process_color_input(pixels, list(crops[module]['roi']), job['captureId'])
                elif module == COLOR and COLOR_INPUT in crops:
                    visible = self._process_color_input(pixels, list(crops[module]['roi']), job['captureId'])
                    if visible.get('status') == 'ok' and visible.get('activeColorSlot') != 'transparent':
                        rgb = [next(r['value'] for r in visible['valueRegions'] if r['channel']==c) for c in 'rgb']
                        result = dict(kind='rgb',rgb=rgb,hex='#'+''.join(f'{v:02X}' for v in rgb),source='rgbSlider')
                    else:
                        result = (dict(kind='unknown',rgb=None,hex=None,detail='colorWheelNotVisible')
                                  if visible.get('status') != 'panelNotVisible' else self.cores[module].process(pixels))
                else:
                    result = self.cores[module].process(pixels)
            self._complete_analysis(job,result,analysis_started)
        except Exception as ex:
            with self.analysis_lock:
                current = self._analysis_current(job)
                self.record("analysisError", job["ticks"], job["refs"], dict(module=module, error=str(ex),
                    screenshotIds=[c["screenshotId"] for c in crops.values()],triggerTicks=job["triggerTicks"],
                    superseded=not current,analysisStartedTicks=analysis_started), "delayed")
                if not current:
                    self._defer_job(job)
                    return
                if module == CANVAS:
                    self.notify(dict(type="canvasOrigin", point=None))
                    self.notify(dict(type="canvasViewport", points=None))
                    if job["initialize"]:
                        self.notify(dict(type="initializationFailed", message=str(ex)))
                    if self._retry_canvas(job):
                        return
                if job.get("packageId"):
                    self.timeline.complete_if_pending(job["packageId"],MODULES[module],None,dict(
                        triggerTicks=job["triggerTicks"],relatedEventIds=job["refs"],completedTicks=now_ticks(),
                        **(dict(captureId=job['captureId'],capturedTicks=job['ticks'],captureEndTicks=job['captureEndTicks'],
                                panelRoi=list(crops[module]['roi'])) if module in (BRUSH,TOOLGROUP,LAYERS,COLOR_INPUT) else {})),str(ex))

    def _process_color_input(self, pixels, roi, frame_id):
        # The color-wheel and slider readers may be scheduled concurrently for
        # one shared booth. Keep the OCR core resident and serialize its calls.
        with self.locks[COLOR_INPUT]:
            if COLOR_INPUT not in self.cores:
                self.cores[COLOR_INPUT] = take_core(COLOR_INPUT)
            return self.cores[COLOR_INPUT].process(pixels,panel_roi=roi,capture_frame_id=frame_id)

    def _complete_analysis(self, job, result, analysis_started):
        # Activation and publication share a lock: an older in-flight analysis
        # cannot replace the state of a panel activated again while its core ran.
        with self.analysis_lock:
            module, crops = job["module"], job["crops"]
            current = self._analysis_current(job)
            self.record("stateResult", job["ticks"], job["refs"], dict(module=MODULES[module],result=result,
                screenshotIds=[c["screenshotId"] for c in crops.values()],captureId=job["captureId"],
                statePackageId=job.get("packageId"),triggerTicks=job["triggerTicks"],capturedTicks=job["ticks"],
                completedTicks=now_ticks(),analysisStartedTicks=analysis_started,superseded=not current,
                analysisQuietMs=self.analysis_quiet_ms(module),analysisToken=job.get("analysisToken"),
                captureEndTicks=job["captureEndTicks"],captureLatencyMs=job["captureLatencyMs"],
                captureDurationMs=job["captureDurationMs"],captureBudgetExceeded=job["captureBudgetExceeded"],
                captureBackend=job.get("captureBackend"),dpi=job.get("dpi"),
                canvasCaptureValidation=job.get("canvasCaptureValidation"),
                retryOfCaptureId=job["reason"].partition(":")[2] if job["reason"].startswith(("canvasRetry:","layerRetry:")) else None,
                triggerReason=job["reason"],initialization=job["initialize"]),"delayed")
            if not current:
                self._defer_job(job)
                return
            if module == CANVAS:
                self.notify(dict(type="canvasOrigin",point=result.get("canvasOriginScreenPx") if result.get("success") else None))
                self.notify(dict(type="canvasViewport",points=result.get("completedViewportScreenPx") if result.get("success") else None))
                if (not job["initialize"] and not result.get("success") and result.get("failedStage") == 9
                        and self._retry_canvas(job)):
                    return
            if module == LAYERS and self._retry_layers(job, result):
                return
            if job.get("packageId"):
                self.timeline.complete_if_pending(job["packageId"],MODULES[module],result,dict(
                    screenshotIds=[c["screenshotId"] for c in crops.values()],capturedTicks=job["ticks"],
                    captureId=job["captureId"],
                    **(dict(captureEndTicks=job['captureEndTicks']) if module in (TOOLGROUP,LAYERS,COLOR_INPUT) else {}),
                    **(dict(panelRoi=list(crops[module]["roi"])) if module in (BRUSH,TOOLGROUP,LAYERS,COLOR_INPUT) else {}),
                    triggerTicks=job["triggerTicks"],causalAmbiguous=job["causalAmbiguous"],
                    relatedEventIds=job["refs"],completedTicks=now_ticks(),analysisStartedTicks=analysis_started,
                    analysisQuietMs=self.analysis_quiet_ms(module),
                    analysisToken=job.get("analysisToken"),finalEvidence=job.get("finalEvidence",True),
                    observedAfterEventId=job["observedAfterEventId"]),
                    result.get("message","画布初始化失败") if module == CANVAS and not result.get("success") else None)

    def _retry_canvas(self, job):
        if self.closing or job["initialize"] or job["reason"].startswith("canvasRetry:"):
            return False
        # Preserve the first evidence/result. Resolve this package only after one
        # fresh ROI batch, keeping its original causal slot and ambiguity checks.
        self.notify(dict(type="canvasRetry",ticks=job["triggerTicks"],refs=job["refs"],
                         packageId=job.get("packageId"),captureId=job["captureId"]))
        return True

    def _retry_layers(self, job, result):
        # Rows without a selected one: CSP was still redrawing the layer panel (after a delete, a paste,
        # a scroll). Read one fresh frame for the same package instead of publishing an unknown layer.
        locator = result.get("layerLocator") if isinstance(result, dict) else None
        if (self.closing or job["initialize"] or job["reason"].startswith("layerRetry:")
                or not isinstance(locator, dict) or not locator.get("rows") or result.get("selectedName")):
            return False
        self.notify(dict(type="layerRetry",ticks=job["triggerTicks"],refs=job["refs"],
                         packageId=job.get("packageId"),captureId=job["captureId"]))
        return True

    def _transform_call(self, payload):
        try:
            return self.transform.call(payload, timeout=120)
        except OSError:
            self.transform_ready = False
            raise
        except TimeoutError:
            self.transform_ready = False
            raise RuntimeError("画布核心 120 秒未返回；原始截图已保留") from None
        except EOFError:
            self.transform_ready = False
            raise RuntimeError("画布核心进程已退出") from None

    def close(self):
        self.closing = True
        self.capture_queue.put(None)
        self.capture_thread.join()
        self.evidence_queue.put(None)
        self.evidence_thread.join()
        self.stop.set()
        self.dispatch_thread.join()
        self.executor.shutdown(wait=True)
        self.clip_executor.shutdown(wait=True)
        self.timeline.close(now_ticks())
        if self.transform:
            self.transform.close()
