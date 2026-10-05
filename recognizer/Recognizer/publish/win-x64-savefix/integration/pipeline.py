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
import threading
import time
import uuid
from concurrent.futures import ThreadPoolExecutor

from catalog import BRUSH, CANVAS, NAVIGATOR, LAYERS, COLOR
from state_timeline import StateTimeline, MODULES

CANVAS_FRAME = "__canvas_frame__"


def now_ticks():
    value = ctypes.c_longlong()
    ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(value))
    return value.value - int(os.environ["MEMOLINE_CLOCK_ORIGIN"])


class Pipeline:
    def __init__(self, settings, emit, notify):
        self.settings, self.emit, self.notify = settings, emit, notify
        self.directory = Path(os.environ["MEMOLINE_SPOOL"])
        self.directory.mkdir(parents=True, exist_ok=True)
        self.capture_queue = queue.Queue()
        self.evidence_queue = queue.Queue(maxsize=4)
        self.stop = threading.Event()
        self.cores, self.locks = {}, {name: threading.Lock() for name in (BRUSH, LAYERS, COLOR, CANVAS)}
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
        self.executor = ThreadPoolExecutor(max_workers=settings["analysisConcurrency"], thread_name_prefix="state-core")
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
        self.record("coreStateUpdated",ticks,evidence.get("relatedEventIds",[]),dict(
            packageId=package_id,initial=initial,rawResult=raw_result,**{**update,"evidence":evidence}),"delayed")

    def configure(self, dimensions, numbers_roi, clip_path,initial_package_id):
        self.dimensions, self.numbers_roi, self.clip_path = dimensions, numbers_roi, clip_path
        self.initial_package_id = initial_package_id
        self.timeline.expect(initial_package_id,set(MODULES.values()) | ({"clipState"} if clip_path else set()),True)
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
            due = time.monotonic()+max(0,float(self.settings.get("analysisQuietMs",150)))/1000
            for panel in self.analysis_panels(panels):
                previous = self.panel_activity.get(panel)
                self.panel_activity[panel] = dict(token=token,due=due,packageId=package_id,ticks=ticks,refs=list(refs))
                if previous and previous["packageId"] and previous["packageId"] != package_id:
                    self.timeline.complete_if_pending(previous["packageId"],MODULES[panel],None,dict(
                        reason="supersededBeforeAnalysis",deferredToPackageId=package_id,
                        relatedEventIds=previous["refs"],triggerTicks=previous["ticks"],
                        evidencePolicy="captureEveryActivation"))
            return token, due

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
            self.capture_queue.put((set(panels), dict(regions), ticks, list(refs), reason, initialize,package_id,
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
                        stabilityChecked=False,settleWaitMs=0,policy="latestEvidenceAfterQuietPeriod")
                    capture_backend = "MSS_FRAME_ROI"
                else:
                    for panel in selected:
                        raw_frames[panel],capture_times[panel] = acquire(panel)
                    capture_end = now_ticks()
                for panel,pixels in raw_frames.items():
                    frames[panel] = Image.frombytes("RGB",pixels.size,pixels.bgra,"raw","BGRX")
                user.GetDpiForWindow.argtypes = [ctypes.c_void_p]
                user.GetDpiForWindow.restype = ctypes.c_uint
                dpi = user.GetDpiForWindow(hwnd) or 96
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
                capture_id = uuid.uuid4().hex
                crops = {}
                for panel, (x, y, w, h) in selected.items():
                    screenshot_id = uuid.uuid4().hex
                    file = self.directory / (screenshot_id + ".png")
                    frames[panel].save(file,compress_level=1)
                    roi_start,roi_end = capture_times[panel]
                    crop = dict(screenshotId=screenshot_id, path=str(file), roi=[x,y,w,h], panel=panel)
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
                    self.sequence += 1
                    job = dict(module=module, crops=group, ticks=capture_end, triggerTicks=ticks,
                               refs=refs, reason=reason, initialize=initialize, captureId=capture_id,
                               packageId=package_id,causalAmbiguous=ambiguous,observedAfterEventId=watermark,dpi=dpi,captureBackend=capture_backend,
                               captureEndTicks=capture_end,captureLatencyMs=latency_ms,captureDurationMs=duration_ms,
                               captureBudgetMs=budget_ms,captureBudgetExceeded=latency_ms>budget_ms,
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
            if len(running) < self.settings["analysisConcurrency"]:
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
                    if len(running) >= self.settings["analysisConcurrency"]:
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
                    host = Path(__file__).parent / "transform_host" / "TransformHost.exe"
                    if not host.is_file():
                        host = host.parent / "publish" / host.name
                    self.transform = subprocess.Popen([str(host)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=None, text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW)
                    self.transform_lines = queue.Queue()
                    stream, lines = self.transform.stdout, self.transform_lines
                    def receive():
                        for line in stream:
                            lines.put(line)
                        lines.put("")
                    threading.Thread(target=receive,daemon=True,name="transform-results").start()
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
                if module not in self.cores:
                    if module in (BRUSH,LAYERS):
                        from rapidocr import RapidOCR
                        ocr = RapidOCR(params={"EngineConfig.onnxruntime.intra_op_num_threads":2,
                            "EngineConfig.onnxruntime.inter_op_num_threads":1,"Global.log_level":"warning"})
                    if module == BRUSH:
                        from recognizer_core.panel_state import PanelStateCore
                        core = PanelStateCore(ocr_engine=ocr)
                    elif module == LAYERS:
                        from recognizer_core.layer_state import LayerStateCore
                        core = LayerStateCore(ocr_engine=ocr)
                    else:
                        from recognizer_core.color_state import ColorStateCore
                        core = ColorStateCore()
                    self.cores[module] = core
                pixels = cv2.imdecode(np.fromfile(crops[module]["path"], dtype=np.uint8), cv2.IMREAD_COLOR)
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
                        triggerTicks=job["triggerTicks"],relatedEventIds=job["refs"],completedTicks=now_ticks()),str(ex))

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
                analysisQuietMs=self.settings.get("analysisQuietMs",150),analysisToken=job.get("analysisToken"),
                captureEndTicks=job["captureEndTicks"],captureLatencyMs=job["captureLatencyMs"],
                captureDurationMs=job["captureDurationMs"],captureBudgetExceeded=job["captureBudgetExceeded"],
                captureBackend=job.get("captureBackend"),dpi=job.get("dpi"),
                canvasCaptureValidation=job.get("canvasCaptureValidation"),
                retryOfCaptureId=job["reason"].partition(":")[2] if job["reason"].startswith("canvasRetry:") else None,
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
            if job.get("packageId"):
                self.timeline.complete_if_pending(job["packageId"],MODULES[module],result,dict(
                    screenshotIds=[c["screenshotId"] for c in crops.values()],capturedTicks=job["ticks"],
                    triggerTicks=job["triggerTicks"],causalAmbiguous=job["causalAmbiguous"],
                    relatedEventIds=job["refs"],completedTicks=now_ticks(),analysisStartedTicks=analysis_started,
                    analysisQuietMs=self.settings.get("analysisQuietMs",150),
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

    def _transform_call(self, payload):
        try:
            self.transform.stdin.write(json.dumps(payload)+"\n")
            self.transform.stdin.flush()
        except OSError:
            self.transform_ready = False
            if self.transform.poll() is None:
                self.transform.kill()
            raise
        try:
            line = self.transform_lines.get(timeout=120)
        except queue.Empty:
            self.transform.kill()
            self.transform_ready = False
            raise RuntimeError("画布核心 120 秒未返回；原始截图已保留")
        if not line:
            self.transform_ready = False
            raise RuntimeError("画布核心进程已退出")
        return json.loads(line)

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
            try:
                self.transform.stdin.close()
                self.transform.wait(timeout=10)
            except (OSError,subprocess.TimeoutExpired):
                if self.transform.poll() is None:
                    self.transform.kill()
