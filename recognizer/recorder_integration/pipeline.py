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

import numpy as np

from catalog import BRUSH, CANVAS, NAVIGATOR, LAYERS, COLOR, COLOR_INPUT, TOOLGROUP
from state_timeline import StateTimeline, MODULES
from clip_witness import ClipWitness, link_or_copy
import floating_panels

CANVAS_FRAME = "__canvas_frame__"


def now_ticks():
    value = ctypes.c_longlong()
    ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(value))
    return value.value - int(os.environ["MEMOLINE_CLOCK_ORIGIN"])


_QPC_FREQUENCY = None


def qpc_frequency():
    global _QPC_FREQUENCY
    if _QPC_FREQUENCY is None:
        value = ctypes.c_longlong()
        ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(value))
        _QPC_FREQUENCY = value.value
    return _QPC_FREQUENCY


def transform_host_path():
    host = Path(__file__).parent / "transform_host" / "TransformHost.exe"
    return host if host.is_file() else host.parent / "publish" / host.name


def analysis_workers(settings):
    """Analysis jobs that may run at once. Each core stays serial, so more than one job per
    module never helps; 0 (the default) follows the CPU and leaves cores for CSP and the recorder.
    While the user draws, Pipeline._dispatch lowers it to one (see POINTER_QUIET_S)."""
    configured = int(settings.get("analysisConcurrency", 0) or 0)
    if configured > 0:
        return min(configured, len(MODULES))
    return max(2, min(len(MODULES), (os.cpu_count() or 4) // 4))


# OCR jobs side by side slow CSP's drawing however many cores are free and whatever their priority: they share
# the memory bandwidth and caches. Measured 2026-10-24 (Ryzen 9 8940HX, 16 cores, recorded panel crops): with four
# OCR jobs at once a single-threaded image operation the size of a few canvas tiles ran at 36-44 % of its speed,
# and below-normal, idle priority or EcoQoS changed that by a few points; one job at a time left it 78-83 %.
# So from pen down until this long after the last pointer release only one analysis runs at a time.
POINTER_QUIET_S = 0.7


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
# RapidOCR enlarges every image to a 736 px short side before detection; a 282 px wide layer panel
# became 736x1712 and one read took 3.4 s. Detecting at the panel's own size gave the same results
# on every recorded layer, sub tool and colour-slider panel (the layer reader re-reads the faint
# highlighted row enlarged) at 0.9 / 0.6 / 0.3 s. The brush panel keeps the enlargement: at its own
# size the palette menu glyph was read as the brush name.
FAST_DETECTION = {**OCR_PARAMS, "Det.limit_type":"max", "Det.limit_side_len":960}
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
    ocr = RapidOCR(params=OCR_PARAMS if module == BRUSH else FAST_DETECTION)
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


def build_history_core():
    """History palette reader with its own OCR engine (it runs on the history thread, not the analysis pool)."""
    with _build_lock:
        from rapidocr import RapidOCR
        from recognizer_core.history_state import HistoryStateCore
        return HistoryStateCore(ocr_engine=RapidOCR(params=OCR_PARAMS))


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
        # The digit selection relative to the navigator panel's top-left (dx, dy, w, h), and the panel
        # image it was selected on (RGB, or None): the navigator may now be docked elsewhere or floating.
        self.numbers_relative = None
        self.numbers_reference = None
        # Where the navigator panel was in the frame when the canvas core was initialized, and its pixels
        # then: {"rect": (x, y, w, h), "image": RGB array}. Later frames put the navigator back there,
        # aligned, so the core keeps its thumbnail and digit ROIs wherever the panel has gone since.
        self.navigator_reference = None
        self.numbers_frame_roi = None
        # Floating palette windows by panel (floating_panels.FloatingPanel), set by the runtime.
        self.floating = {}
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
        # A pen or mouse button down on CSP (UpdateEngine.pointer_operations), and when that last changed.
        self.pointer_down = False
        self.pointer_changed = float("-inf")
        self.closing = False
        self.executor = ThreadPoolExecutor(max_workers=analysis_workers(settings), thread_name_prefix="state-core")
        # The recorder's own saves wait for CSP's write in request order; requests that need no write of their own
        # (Memoline waited for its save, or none was made) must not queue behind those waits.
        self.clip_executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="clip-parser")
        self.clip_settled_executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="clip-settled")
        self.clip_witness = None
        self.capture_thread = threading.Thread(target=self._capture_loop, name="roi-capture")
        self.dispatch_thread = threading.Thread(target=self._dispatch, name="state-dispatch")
        self.evidence_thread = threading.Thread(target=self._persist_loop, name="roi-evidence")
        self.evidence_thread.start()
        self.capture_thread.start()
        self.dispatch_thread.start()
        from history_reader import HistoryReader
        self.history = HistoryReader(self, settings)

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

    def configure(self, dimensions, numbers_relative, clip_path,initial_package_id,subtools_available=False,
                  numbers_reference=None):
        self.dimensions, self.clip_path = dimensions, clip_path
        self.numbers_relative = list(numbers_relative) if numbers_relative is not None else None
        self.numbers_reference = numbers_reference
        self.initial_package_id = initial_package_id
        modules = set(MODULES.values()) - {'subtoolState'}
        if subtools_available:
            modules.add('subtoolState')
        self.timeline.expect(initial_package_id,modules | ({"clipState"} if clip_path else set()),True)
        if clip_path:
            self.clip_witness = ClipWitness(clip_path,self.directory,self.record,now_ticks)
            self.clip_witness.start()
            self.request_clip_state(uuid.uuid4().hex,0,initial_package_id,saved=False)

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

    # CSP prepares 0.4-0.9 s after Ctrl+S before it writes (about 2 s right after a full-canvas paste).
    CLIP_WRITE_TIMEOUT_S = 2.5
    # The witness keeps a version about 0.1 s after CSP wrote it; a request that needs no write of its own waits that long.
    CLIP_CATCH_UP_S = 1.0

    def request_clip_state(self, save_id, ticks, package_id, saved):
        """A requested clipState: `saved` when the requester made a save and waited for it (Memoline's bound request)."""
        self.clip_settled_executor.submit(self._parse_clip,save_id,ticks,package_id,saved,saved)

    def _parse_clip(self, save_id, ticks,package_id=None,settled=False,bound=True):
        """Parses a version the witness kept (clip_witness), evidence first.
        A save of the recorder's own (`bound`, not `settled`): the first version CSP wrote after its trigger that no
        earlier save took. Without one CSP wrote nothing for it (an unmodified document, a dropped Ctrl+S, or another
        document): the latest version is parsed as what it is, the document as of that write (writeObserved=false,
        saveBound=false), never as a save made at `ticks`.
        `settled`: the requester saved and waited for the .clip already (Memoline's bound request): the version on
        disk now. Not `bound`: no save was made; the file shows the layers as of CSP's last write, not of `ticks`."""
        try:
            from recognizer_core.clip_layers_core import read_clip_layers
            witness = self.clip_witness
            if witness is None:
                raise RuntimeError(".clip 存证尚未开始")
            if bound and not settled:
                write, observed = witness.bind_save(ticks,self.CLIP_WRITE_TIMEOUT_S)
            else:
                write = witness.current(self.CLIP_CATCH_UP_S)
                observed = write is not None and not write.preexisting and write.observed_ticks > ticks
            if write is None:
                raise RuntimeError("没有可解析的 .clip 版本：文件不存在或一直在被写入")
            if bound and not settled and not observed:
                self.record("clipSaveUnwritten",ticks,[],dict(saveId=save_id,triggerTicks=ticks,
                    waitedMs=int(self.CLIP_WRITE_TIMEOUT_S*1000),latestWriteId=write.id,
                    latestObservedTicks=write.observed_ticks),"delayed")
            # The save's own name for the kept version goes with the structure: the watcher exports a layer that
            # first appears in it as that layer's starting pixels (layerCreated baseline).
            snapshot = link_or_copy(write.path,self.directory / (save_id+".clip"))
            result = {**read_clip_layers(snapshot), "snapshotPath": str(snapshot)}
            saved = bound and (settled or observed)
            written = dict(writeId=write.id,writeObserved=observed,contentAsOfTicks=write.observed_ticks)
            self.record("clipParseResult",ticks,[],dict(saveId=save_id,result=result,
                snapshotPath=str(snapshot),sourcePath=str(self.clip_path),observedTicks=now_ticks(),
                sha256=write.sha256,saveBound=saved,saveCompletionConfirmed=observed,**written),"delayed")
            if package_id:
                self.timeline.complete(package_id,"clipState",result,dict(saveId=save_id,observedTicks=now_ticks(),
                    saveBound=saved,**written))
            else:
                self.timeline.observe("clipState",result,dict(saveId=save_id,observedTicks=now_ticks(),
                    triggerTicks=ticks,saveCompletionConfirmed=observed,saveBound=saved,**written))
        except Exception as ex:
            self.record("clipParseError",ticks,[],dict(saveId=save_id,error=str(ex)),"delayed")
            if package_id:
                self.timeline.complete(package_id,"clipState",None,dict(saveId=save_id),str(ex))
            else:
                self.timeline.observe("clipState",None,dict(saveId=save_id,triggerTicks=ticks),str(ex))

    def capture(self, panels, regions, ticks, refs=(), reason="operation", initialize=False,package_id=None,
                analysis_token=None, final=True, background=False):
        """`background`: nobody waits for the result (Memoline's refresh after an edit); its analysis runs alone
        and never starts while a pointer is down. The capture itself is never delayed."""
        if not self.stop.is_set():
            panels = set(panels)
            if BRUSH in panels and TOOLGROUP in regions:
                panels.add(TOOLGROUP)
            self.capture_queue.put((panels, dict(regions), ticks, list(refs), reason, initialize,package_id,
                                   dict(analysisToken=analysis_token,finalEvidence=final,background=background)))

    def observe_pointer(self, down):
        if down != self.pointer_down:
            self.pointer_down, self.pointer_changed = down, time.monotonic()

    def _drawing(self):
        return self.pointer_down or time.monotonic() - self.pointer_changed < POINTER_QUIET_S

    def _capture_loop(self):
        from PIL import Image
        frequency = ctypes.c_longlong()
        ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(frequency))
        while True:
            request = self.capture_queue.get()
            if request is None:
                return
            panels, regions, ticks, refs, reason, initialize,package_id, analysis = request
            frames, capture_times = {}, {}
            captured_success = False
            csp_image = None
            try:
                hwnd = int(os.environ.get("MEMOLINE_CSP_HWND", "0"))
                if not hwnd:
                    raise RuntimeError("未找到 CSP 主窗口，未截图")
                user = ctypes.windll.user32
                if CANVAS in panels or NAVIGATOR in panels:
                    panels.update((CANVAS, NAVIGATOR))
                floating = dict(self.floating)
                # Floating palettes are rendered from their own window (covered or on the virtual screen).
                windows = {p: floating[p] for p in panels if p in floating and p not in (CANVAS, NAVIGATOR)}
                selected = {p: tuple(regions[p]) for p in panels if p in regions and p not in windows and p != NAVIGATOR}
                navigator_window, pasted = None, False
                if CANVAS in selected:
                    # The canvas core stays where it was initialized: the navigator's current pixels are
                    # put back, aligned, at the place it had in the frame then. Moving, docking elsewhere,
                    # floating or stashing it changes no ROI; only a failed alignment re-initializes.
                    navigator_window = floating.get(NAVIGATOR)
                    dock = tuple(regions[NAVIGATOR]) if navigator_window is None and NAVIGATOR in regions else None
                    if navigator_window is None and dock is None:
                        raise RuntimeError("导航器面板既不在停靠栏也不是浮动窗口，无法读取画布视图")
                    if not self.initialized or self.navigator_reference is None:
                        initialize = True
                    monitor = floating_panels.monitor_rect(hwnd)
                    if initialize:
                        # A floating navigator gets a strip right of the monitor; it is never on screen there.
                        target = dock if dock is not None else (monitor[0] + monitor[2], monitor[1], *navigator_window.rect[2:])
                    else:
                        target = tuple(self.navigator_reference["rect"])
                    pasted = navigator_window is not None or dock != target
                    right = max(monitor[0] + monitor[2], target[0] + target[2])
                    bottom = max(monitor[1] + monitor[3], target[1] + target[3])
                    frame_rect = (monitor[0], monitor[1], right - monitor[0], bottom - monitor[1])
                    selected[NAVIGATOR] = target if initialize else tuple(self.navigator_roi)
                    if not initialize:
                        selected[CANVAS] = tuple(self.canvas_roi)
                elif NAVIGATOR in regions and NAVIGATOR in panels and NAVIGATOR not in floating:
                    selected[NAVIGATOR] = tuple(regions[NAVIGATOR])
                if not selected and not windows:
                    raise RuntimeError("请求的面板 ROI 当前不可用")
                captured = now_ticks()
                capture_end = captured
                canvas_validation = None
                capture_backend = "PrintWindow_ROI"
                if selected:
                    # Docked panels and the canvas come from CSP's own window, never from the screen: windows in
                    # front of CSP (Memoline's panels, the watcher, other applications) and CSP not being the
                    # foreground window change nothing. Only simulated input needs CSP in front.
                    window_rect = floating_panels.window_rect(hwnd)
                    def contains(box, roi):
                        x, y, w, h = roi
                        return w > 0 and h > 0 and x >= box[0] and y >= box[1] and x+w <= box[0]+box[2] and y+h <= box[1]+box[3]
                    for panel, roi in selected.items():
                        if panel == NAVIGATOR and pasted:
                            # Put back at its initialization place, which may lie beyond the window (the strip).
                            inside = contains(frame_rect, roi)
                        else:
                            inside = contains(window_rect, roi) and (CANVAS not in selected or contains(frame_rect, roi))
                        if not inside:
                            raise RuntimeError("请求的面板 ROI 超出 CSP 窗口，未扩张或截断 ROI")
                    # The canvas frame needs the whole window; panels alone only the part holding them.
                    region = None
                    if CANVAS not in selected:
                        left = min(x for x, _, _, _ in selected.values()); top = min(y for _, y, _, _ in selected.values())
                        region = (left, top, max(x+w for x, _, w, _ in selected.values()) - left,
                                  max(y+h for _, y, _, h in selected.values()) - top)
                    start = now_ticks()
                    csp_image, bounds = floating_panels.window_image(hwnd, region)
                    span = (start, now_ticks())
                if CANVAS in selected:
                    # The native solver uses true pixels beyond its thumbnail ROI: the monitor CSP is on (never
                    # the virtual screen), drawn from CSP's window; whatever is not CSP stays black.
                    selected[CANVAS_FRAME] = frame_rect
                    image = Image.new("RGB",frame_rect[2:],(0,0,0))
                    image.paste(csp_image,(bounds[0]-frame_rect[0],bounds[1]-frame_rect[1]))
                    alignment = None
                    if pasted:
                        if navigator_window is not None:
                            bgr,_ = floating_panels.capture(navigator_window.hwnd,navigator_window.rect)
                            panel_now = np.ascontiguousarray(bgr[:,:,::-1])
                        else:
                            x,y,w,h = dock
                            panel_now = np.asarray(image.crop((x-frame_rect[0],y-frame_rect[1],x-frame_rect[0]+w,y-frame_rect[1]+h)))
                        if initialize:
                            placed = panel_now
                        else:
                            reference = self.navigator_reference["image"]
                            alignment = floating_panels.align_panel(reference, panel_now)
                            if alignment[2] < floating_panels.ALIGNMENT_SCORE:
                                # Resized or another tab in front: initialize again on the next canvas capture.
                                self.initialized = False
                                raise RuntimeError(f"导航器画面与初始化时对不上（匹配度 {alignment[2]:.2f}），下一次画布操作重新初始化")
                            placed = floating_panels.shifted(panel_now, alignment[0], alignment[1], *reference.shape[1::-1])
                        image.paste(Image.fromarray(placed), (target[0]-frame_rect[0], target[1]-frame_rect[1]))
                    if initialize:
                        dx, dy, nw, nh = self.numbers_relative
                        if self.numbers_reference is not None:
                            # The digits were selected on another view of the panel (docked vs floating chrome).
                            x,y,w,h = target
                            here = np.asarray(image.crop((x-frame_rect[0],y-frame_rect[1],x-frame_rect[0]+w,y-frame_rect[1]+h)))
                            ox, oy, score = floating_panels.align_panel(self.numbers_reference, here)
                            if score >= floating_panels.ALIGNMENT_SCORE:
                                dx, dy = dx + ox, dy + oy
                        selected["导航器数字"] = (target[0] + dx, target[1] + dy, nw, nh)
                    else:
                        selected["导航器数字"] = tuple(self.numbers_frame_roi)
                    span = (start,now_ticks())
                    frames[CANVAS_FRAME] = image
                    captured,capture_end = span
                    for panel,(x,y,w,h) in selected.items():
                        capture_times[panel] = span
                        if panel != CANVAS_FRAME:
                            left,top = x-frame_rect[0],y-frame_rect[1]
                            frames[panel] = image.crop((left,top,left+w,top+h))
                    canvas_validation = dict(sameFrame=not pasted,verifiedTicks=capture_end,
                        stabilityChecked=False,settleWaitMs=0,policy="immediateEvidenceThenQuietAnalysis",
                        navigatorSource="floatingWindow" if navigator_window is not None else "dock",
                        navigatorPanelRect=list(navigator_window.rect if navigator_window is not None else dock),
                        navigatorPlacedAt=list(target) if pasted else None,
                        navigatorAlignment=list(alignment) if alignment else None)
                    capture_backend = ("PrintWindow_FRAME_ROI+floatingNavigator" if navigator_window is not None
                                       else "PrintWindow_FRAME_ROI+moved" if pasted else "PrintWindow_FRAME_ROI")
                elif selected:
                    for panel,(x,y,w,h) in selected.items():
                        left,top = x-bounds[0],y-bounds[1]
                        frames[panel] = csp_image.crop((left,top,left+w,top+h))
                        capture_times[panel] = span
                    captured,capture_end = span
                for panel, window in windows.items():
                    start = now_ticks()
                    pixels, rect = floating_panels.capture(window.hwnd, window.rect)
                    frames[panel] = Image.fromarray(np.ascontiguousarray(pixels[:,:,::-1]))
                    selected[panel] = rect
                    capture_times[panel] = (start, now_ticks())
                    capture_end = max(capture_end, capture_times[panel][1])
                    capture_backend += "+floatingPanel"
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
                if csp_image is not None:
                    csp_image.close()
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
            for future, (file, module, _) in list(running.items()):
                if future.done():
                    running.pop(future)
                    self.active.discard(module)
                    # Keep failed job inputs on disk for diagnosis; consumed metadata is marked.
                    file.replace(file.with_suffix(".done"))
            drawing = self._drawing() and not self.closing
            workers = 1 if drawing else analysis_workers(self.settings)
            background_running = any(entry[2] for entry in running.values())
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
                        background = bool(job.get("background"))
                        if background and not self.closing and (self.pointer_down or background_running):
                            continue
                        self.active.add(module)
                        running[self.executor.submit(self._analyze, job)] = (file, module, background)
                        background_running = background_running or background
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
                        navigator = crops[NAVIGATOR]
                        from PIL import Image
                        with Image.open(navigator["path"]) as image:
                            self.navigator_reference = dict(rect=tuple(navigator["roi"]), image=np.asarray(image.convert("RGB")).copy())
                        self.numbers_frame_roi = tuple(crops["导航器数字"]["roi"])
                        self.initialized = True
                        self.transform_ready = True
                        if payload["initialize"]:
                            self.transform_initial_payload = payload
                        self.notify(dict(type="canvasInitialized", roi=self.canvas_roi,navigatorRoi=self.navigator_roi,
                                         navigatorPanelRect=list(navigator["roi"])))
                    else:
                        self.notify(dict(type="initializationFailed", message=result.get("message", "画布初始化失败")))
            else:
                import cv2
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
                    job, result = self._follow_layer_command(job, result, pixels)
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
                retryOfCaptureId=job["reason"].partition(":")[2] if job["reason"].startswith(("canvasRetry:","layerRetry:","viewSettle","layerFollowUp")) else None,
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
                if self._settle_view(job, result):
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

    # A view shortcut or wheel step is applied by CSP when it gets to it: right after a save (the layer guard's
    # Ctrl+S) the first screenshot still shows the old view (2026-10-20: Ctrl+= read as unchanged 105 ms later,
    # and 77 s of strokes were mapped through the old zoom). Such a capture is not the command's result.
    VIEW_SETTLE_DELAYS_MS = (150, 300, 600, 1200)

    def _settle_view(self, job, result):
        """Look again, for the same package, while a view command still shows the old view."""
        reason = job["reason"]
        if (self.closing or job["initialize"] or not job.get("packageId") or not result.get("success")
                or not (reason in ("shortcut", "mouseWheel") or reason.startswith("viewSettle"))
                or not self.timeline.repeats(MODULES[CANVAS], result)):
            return False
        attempt = int(reason[len("viewSettle"):].partition(":")[0]) if reason.startswith("viewSettle") else 0
        if attempt >= len(self.VIEW_SETTLE_DELAYS_MS):
            return False
        self.notify(dict(type="viewSettle", ticks=job["triggerTicks"], refs=job["refs"], packageId=job["packageId"],
                         captureId=job["captureId"] if attempt == 0 else reason.partition(":")[2],
                         attempt=attempt+1, delayMs=self.VIEW_SETTLE_DELAYS_MS[attempt],
                         analysisToken=job.get("analysisToken")))
        return True

    # A layer command (a layer palette button, a layer shortcut) shows in the panel when CSP gets to it. Tapped right
    # after the layer guard's Ctrl+S, 新建栅格图层 had not shown 190 ms later (2026-10-24): the panel read then listed
    # the old layers as "unchanged" and six strokes were filed under the old layer. So the runtime takes the panel again
    # at these offsets after the command, as evidence at once (Runtime.follow_up_layers); only an analysis that still
    # shows the previous state looks at those captures.
    LAYER_COMMAND_REASONS = ("pointerReleased", "shortcut")
    LAYER_FOLLOW_UP_MS = (150, 300, 600, 1200)

    def _follow_layer_command(self, job, result, pixels):
        """The command's capture, or the earliest follow-up showing what the panel finally showed when that differs.
        The follow-up keeps the command's package, trigger and inputs; its own capture time and causal ambiguity
        say when the state was seen. Pixels equal to an analysed capture are not analysed again."""
        if (self.closing or job["initialize"] or job["reason"] not in self.LAYER_COMMAND_REASONS
                or job.get("analysisToken") is None or not job.get("packageId") or not isinstance(result, dict)
                or not self.timeline.repeats(MODULES[LAYERS], result)):
            return job, result
        import cv2
        wanted = len(self.LAYER_FOLLOW_UP_MS)
        waited = (now_ticks() - job["triggerTicks"]) / qpc_frequency()
        deadline = time.monotonic() + max(0.0, (self.LAYER_FOLLOW_UP_MS[-1] + 500) / 1000 - waited)
        while True:
            follow = self._layer_follow_ups(job["analysisToken"])
            if len(follow) >= wanted or self.closing or time.monotonic() >= deadline:
                break
            time.sleep(0.05)
        if not follow:
            return job, result
        seen = []
        for other in follow:
            image = cv2.imdecode(np.fromfile(other["crops"][LAYERS]["path"], dtype=np.uint8), cv2.IMREAD_COLOR)
            if image is not None:
                seen.append((other, image))
        def same(a, b):
            return a is not None and b is not None and a.shape == b.shape and np.array_equal(a, b)
        if not seen or same(seen[-1][1], pixels):
            return job, result
        final = seen[-1][1]
        other, image = next(item for item in seen if same(item[1], final))
        crop = other["crops"][LAYERS]
        try:
            later = self.cores[LAYERS].process_with_locator(image, panel_roi=list(crop["roi"]), capture_frame_id=other["captureId"])
        except Exception as ex:  # the command's own reading still stands
            self.record("analysisError", other["ticks"], job["refs"], dict(module=LAYERS, error=str(ex),
                screenshotIds=[crop["screenshotId"]], triggerTicks=job["triggerTicks"], followUpOf=job["captureId"]), "delayed")
            return job, result
        if not isinstance(later, dict) or not later.get("selectedName") or self.timeline.repeats(MODULES[LAYERS], later):
            return job, result
        return dict(other, packageId=job["packageId"], triggerTicks=job["triggerTicks"], refs=job["refs"],
                    reason=other["reason"] + ":" + job["captureId"], finalEvidence=True), later

    def _layer_follow_ups(self, token):
        """Follow-up captures of one layer command (evidence jobs, analysed or not), oldest first."""
        found = []
        for file in list(self.directory.glob("*.job")) + list(self.directory.glob("*.evidence")):
            try:
                other = json.loads(file.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue  # renamed by the dispatcher meanwhile; found under its new name next time
            if (other.get("module") == LAYERS and other.get("analysisToken") == token
                    and str(other.get("reason", "")).startswith("layerFollowUp") and LAYERS in other.get("crops", {})):
                found.append(other)
        return sorted({o["captureId"]: o for o in found}.values(), key=lambda o: o["ticks"])

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

    def reinitialize_canvas(self):
        """The canvas viewport moved: the next canvas capture initializes the core again."""
        self.initialized = False
        self.navigator_reference = None

    def request_history(self, ticks, event_id, command):
        """Undo or redo reached CSP: read the History palette once the keyboard is quiet."""
        if not self.closing:
            self.history.request(ticks, event_id, command)

    def close(self):
        self.closing = True
        if getattr(self, "history", None) is not None:
            self.history.close()
        self.capture_queue.put(None)
        self.capture_thread.join()
        self.evidence_queue.put(None)
        self.evidence_thread.join()
        self.stop.set()
        self.dispatch_thread.join()
        self.executor.shutdown(wait=True)
        self.clip_executor.shutdown(wait=True)
        self.clip_settled_executor.shutdown(wait=True)
        if self.clip_witness is not None:
            self.clip_witness.close()
        self.timeline.close(now_ticks())
        if self.transform:
            self.transform.close()
