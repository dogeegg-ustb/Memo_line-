"""Recorder-owned, non-activating overlay process. stdin/stdout are JSONL IPC."""
from __future__ import annotations

import argparse
import ctypes
import json
import math
import os
import queue
import sys
import threading
import time
import tkinter as tk
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
if (HERE / "python_libs").is_dir():
    sys.path.insert(0,str(HERE / "python_libs"))
if (HERE.parent / "csp_workspace_overlay.py").is_file():
    sys.path[:0] = [str(HERE.parent), str(HERE.parent.parent / "CSP_Shortcut_Manager")]

from catalog import Catalog, BRUSH, CANVAS, NAVIGATOR, LAYERS, COLOR
from engine import UpdateEngine
from csp_workspace_overlay import Overlay, border_strips, find_dock_file, inspect
from csp_panel_validator.window_service import enable_per_monitor_dpi_awareness
from pipeline import Pipeline, now_ticks
from setup_ui import SetupDialog

OUTPUT_LOCK = threading.Lock()
IPC_OUTPUT = sys.stdout


def emit(value):
    with OUTPUT_LOCK:
        print(json.dumps(value, ensure_ascii=False, separators=(",", ":"), default=lambda x: x.item() if hasattr(x,"item") else str(x)), file=IPC_OUTPUT, flush=True)


class Runtime:
    def __init__(self):
        self.root = tk.Tk()
        self.root.withdraw()
        self.stop = threading.Event()
        self.messages = queue.Queue()
        self.layouts = queue.Queue(maxsize=1)
        self.settings_path = HERE / "settings.json"
        self.settings = json.loads(self.settings_path.read_text(encoding="utf-8"))
        self.settings["analysisConcurrency"] = max(1,min(4,int(self.settings.get("analysisConcurrency",2))))
        self.pipeline = None
        self.setup = None
        self.driver_configurations = None
        self.initial_capture_pending = False
        self.canvas_roi = None
        self.navigator_roi = None
        self.core_roi_strips = {}
        self.canvas_origin = None
        self.origin_hwnd = None
        self.viewport_points = None
        self.viewport_windows = []
        self.viewport_geometry = {}
        self.capture_quiet_until = 0
        self.captures_pending = 0
        self.pending_captures = {}
        self.scheduled_captures = {}
        self.capture_sequence = 0
        self.closing = False
        self.guard_busy = False
        self.guard_ids = set()
        self.initial_package_id = None
        self.current_package_id = None
        self.package_waiters = {}
        self.catalog = Catalog()
        self.engine = UpdateEngine(self.catalog, self.on_state)
        self.engine.record("shortcutConfiguration", self.catalog.snapshot(), 0)
        self.path = Path(os.environ["MEMOLINE_CSP_DOCK"]) if os.environ.get("MEMOLINE_CSP_DOCK") else find_dock_file()
        self.user32 = ctypes.WinDLL("user32", use_last_error=True)
        self.user32.GetAncestor.argtypes = [ctypes.c_void_p, ctypes.c_uint]
        self.user32.GetAncestor.restype = ctypes.c_void_p
        self.user32.GetWindowLongW.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.user32.SetWindowLongW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_long]
        self.user32.SetLayeredWindowAttributes.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_byte, ctypes.c_uint]
        self.user32.SetWindowPos.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_uint]
        self.user32.ShowWindow.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.user32.GetForegroundWindow.restype = ctypes.c_void_p
        self.strips = []
        self.panel_strips = {}
        self.displayed = set()
        self.last_result = time.monotonic()
        self.last_input = time.monotonic()
        self.initial_signature = None
        self.workspace_layout = None
        self.last_status = None
        self.hwnd = None
        threading.Thread(target=self.read_input, daemon=True).start()
        threading.Thread(target=self.read_layout, daemon=True).start()
        self.root.after(25, self.refresh)
        self.root.after(150, self.open_setup)

    def open_setup(self):
        self.setup = SetupDialog(self)

    def request_driver_configuration(self, request_id, path, screen_index):
        emit(dict(type="selectDriverConfiguration", requestId=request_id, path=path, screenIndex=screen_index))

    def start_pipeline(self, dimensions, numbers_roi, clip_path):
        self.pipeline = Pipeline(self.settings,emit,self.messages.put)
        self.pipeline.configure(dimensions,numbers_roi,clip_path,self.initial_package_id)
        self.initial_capture_pending = True
        self.engine.record("initializationConfiguration",dict(canvasPixelSize=dimensions,
            numbersRoi=numbers_roi,clipPath=clip_path,settings=self.settings),now_ticks())
        self.publish_guard()
        print("[Initialization] 请切回 CSP，随后自动截取初始状态。",file=sys.stderr)

    def publish_guard(self):
        bindings = []
        for (vk,mods), entries in self.catalog.bindings.items():
            if any(LAYERS in e.get("targets",[]) and e.get("command") and
                   not e["command"].lower().startswith("layerselect")
                   for e in entries):
                bindings.append(dict(vk=vk,mods=mods))
        emit(dict(type="guardConfig",data=dict(enabled=bool(self.pipeline and self.engine.regions and not self.engine.suspended),
            region=self.engine.regions.get(LAYERS),bindings=bindings,saveSettleMs=0,
            autoHotkeyPath=self.settings.get("autoHotkeyPath",""))))

    def on_state(self, value):
        emit(value)
        if (self.pipeline and value["kind"] == "panelUpdateRequested" and
                value["data"]["reason"] != "cursorEnter"):
            panels = value["data"]["panels"]
            # Pointer start/movement, shortcuts and release all preserve evidence.
            # Merely hovering prepares monitoring without activating a state update.
            self.schedule_capture(panels,value["ticks"],value["relatedEventIds"],value["data"]["reason"],self.current_package_id)

    def schedule_capture(self, panels, ticks, refs, reason, package_id=None):
        panels = self.engine.capture_targets(panels)
        if not panels:
            return
        if panels & {CANVAS,NAVIGATOR}:
            panels.update((CANVAS,NAVIGATOR))
        if package_id:
            self.pipeline.expect_package(package_id,panels)
        token,due = self.pipeline.activate(panels,ticks,refs,package_id)
        for panel in self.pipeline.analysis_panels(panels):
            group = [CANVAS,NAVIGATOR] if panel == CANVAS else [panel]
            self.pending_captures[panel] = (due,ticks,list(refs),reason,group,package_id,token)
        # No debounce on this path: even superseded activations are captured.
        self.capture_panels(panels,ticks,refs,reason,package_id=package_id,analysis_token=token,final=False)

    def capture_panels(self, panels, ticks, refs=(), reason="operation", initialize=False,package_id=None,
                       analysis_token=None,final=True):
        panels = self.engine.capture_targets(panels)
        if initialize:
            package_id = self.initial_package_id
        if package_id is None:
            request_id = uuid.uuid4().hex
            self.package_waiters[request_id] = (list(panels),ticks,list(refs),reason,analysis_token,final)
            emit(dict(type="timelineReserve",requestId=request_id,ticks=ticks))
            return
        self.pipeline.expect_package(package_id,panels,initialize)
        if analysis_token is not None:
            self.pipeline.bind_activation(analysis_token,panels,package_id)
        regions = dict(self.engine.regions)
        if self.canvas_roi:
            regions[CANVAS] = self.canvas_roi
        self.show(set())
        self.show_origin(False)
        self.show_core_rois(False)
        self.show_viewport(False)
        self.capture_quiet_until = time.monotonic()+0.15
        self.captures_pending += 1
        self.capture_sequence += 1
        token = self.capture_sequence
        self.scheduled_captures[token] = (list(panels),ticks,list(refs),reason,package_id,analysis_token,final,initialize)
        def submit():
            if self.scheduled_captures.pop(token,None) is None:
                return
            if initialize and not self.closing and (self.guard_busy or self.engine.pointer_operations):
                self.captures_pending -= 1
                self.initial_capture_pending = True
            elif not self.closing and self.engine.regions:
                self.pipeline.capture(panels,regions,ticks,refs,reason,initialize,package_id,analysis_token,final)
            else:
                self.captures_pending -= 1
                if initialize and not self.closing:
                    self.initial_capture_pending = True
        self.root.after(16, submit)

    def flush_pending_captures(self):
        for key,(due,ticks,refs,reason,panels,package_id,analysis_token) in list(self.pending_captures.items()):
            if time.monotonic() >= due and package_id is not None:
                self.pending_captures.pop(key)
                self.capture_panels(panels,ticks,refs,reason,package_id=package_id,
                                    analysis_token=analysis_token,final=True)

    def read_input(self):
        try:
            for line in sys.stdin:
                msg = json.loads(line)
                if msg["type"] == "captureWatermark" and self.pipeline:
                    self.pipeline.watermark_reply(msg)
                    continue
                self.messages.put(msg)
        except Exception as ex:
            self.messages.put(dict(type="error", message=str(ex)))
        finally:
            self.messages.put(dict(type="stop"))

    def read_layout(self):
        def signature():
            result = []
            for path in self.catalog.paths:
                for file in (path, Path(str(path) + "-wal")):
                    try:
                        stat = file.stat()
                        result.append((str(file), stat.st_mtime_ns, stat.st_size))
                    except OSError:
                        result.append((str(file), None, None))
            return result
        saved = signature()
        next_config = time.monotonic() + 2
        while not self.stop.is_set():
            try:
                _, result = inspect(self.path, self.hwnd)
                item = (time.monotonic(), result, None)
            except Exception as ex:
                item = (time.monotonic(), None, str(ex))
            try:
                self.layouts.get_nowait()
            except queue.Empty:
                pass
            self.layouts.put(item)
            if time.monotonic() >= next_config:
                current = signature()
                if current != saved:
                    try:
                        catalog = Catalog(self.catalog.root)
                        self.messages.put(dict(type="catalog", catalog=catalog))
                        saved = current
                    except Exception as ex:
                        self.messages.put(dict(type="configurationError", message=str(ex)))
                next_config = time.monotonic() + 2
            self.stop.wait(0.5)

    def make_strip(self, color):
        hwnd = Overlay.make_strip(self, color)
        # Disabled layered windows are skipped by WindowFromPoint as well as by
        # pointer delivery. The monitor must not mistake its own borders for CSP.
        style = self.user32.GetWindowLongW(hwnd, -16)
        self.user32.SetWindowLongW(hwnd, -16, style | 0x08000000)
        return hwnd

    def show(self, panels):
        if self.captures_pending or not self.settings.get("flashBorders", True) or time.monotonic() < self.capture_quiet_until:
            panels = set()
        on = int(time.monotonic() * 4) % 2 == 0
        wanted = set(panels) if on else set()
        for panel, rect in self.engine.regions.items():
            if panel not in self.panel_strips:
                self.panel_strips[panel] = [self.make_strip("#ffcc00" if panel == BRUSH else "#00ee88") for _ in range(4)]
            if panel in wanted and panel not in self.displayed:
                for hwnd, (x, y, w, h) in zip(self.panel_strips[panel], border_strips(rect, 3)):
                    if not self.user32.SetWindowPos(hwnd, -1, x, y, w, h, 0x0010 | 0x0040):
                        raise ctypes.WinError(ctypes.get_last_error())
        for panel in self.displayed - wanted:
            for hwnd in self.panel_strips[panel]:
                self.user32.ShowWindow(hwnd, 0)
        self.displayed = wanted

    def show_origin(self, visible):
        # Independent of border flashing. Hide during capture so it never enters evidence.
        visible = (visible and self.canvas_origin is not None and not self.closing
                   and not self.captures_pending and bool(self.engine.regions)
                   and time.monotonic() >= self.capture_quiet_until)
        if not visible:
            if self.origin_hwnd:
                self.user32.ShowWindow(self.origin_hwnd, 0)
            return
        if self.origin_hwnd is None:
            self.origin_hwnd = self.make_strip("#168cff")
            # Shape the solid native window rather than drawing an unmapped Tk child.
            gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
            gdi32.CreateEllipticRgn.argtypes = [ctypes.c_int]*4
            gdi32.CreateEllipticRgn.restype = ctypes.c_void_p
            gdi32.DeleteObject.argtypes = [ctypes.c_void_p]
            self.user32.SetWindowRgn.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int]
            region = gdi32.CreateEllipticRgn(0, 0, 28, 28)
            if not region:
                raise ctypes.WinError(ctypes.get_last_error())
            if not self.user32.SetWindowRgn(self.origin_hwnd, region, 1):
                gdi32.DeleteObject(region)
                raise ctypes.WinError(ctypes.get_last_error())
            # Windows owns the region after successful SetWindowRgn.
        x, y = self.canvas_origin
        if not self.user32.SetWindowPos(self.origin_hwnd, -1, round(x)-14, round(y)-14,
                                       28, 28, 0x0010 | 0x0040):
            raise ctypes.WinError(ctypes.get_last_error())

    def show_core_rois(self, visible):
        visible = (visible and not self.closing and not self.captures_pending
                   and bool(self.engine.regions) and time.monotonic() >= self.capture_quiet_until)
        for name, rect, color in (("workspace", self.canvas_roi, "#ff8a00"),
                                  ("thumbnail", self.navigator_roi, "#a855f7")):
            windows = self.core_roi_strips.get(name, [])
            if not visible or rect is None:
                for hwnd in windows:
                    self.user32.ShowWindow(hwnd, 0)
                continue
            if not windows:
                windows = self.core_roi_strips[name] = [self.make_strip(color) for _ in range(4)]
            for hwnd, (x,y,w,h) in zip(windows, border_strips(rect, 4)):
                if not self.user32.SetWindowPos(hwnd, -1, x, y, w, h, 0x0010 | 0x0040):
                    raise ctypes.WinError(ctypes.get_last_error())

    def show_viewport(self, visible):
        visible = (visible and self.viewport_points is not None and not self.closing
                   and not self.captures_pending and bool(self.engine.regions)
                   and time.monotonic() >= self.capture_quiet_until)
        if not visible:
            for hwnd in self.viewport_windows:
                self.user32.ShowWindow(hwnd, 0)
            return
        if not self.viewport_windows:
            self.viewport_windows = [self.make_strip("#ff0000") for _ in range(4)]
        # Clip each edge to the virtual desktop, preserving rotated geometry.
        left,top = self.user32.GetSystemMetrics(76),self.user32.GetSystemMetrics(77)
        right,bottom = left+self.user32.GetSystemMetrics(78),top+self.user32.GetSystemMetrics(79)
        class Point(ctypes.Structure):
            _fields_ = [("x",ctypes.c_long),("y",ctypes.c_long)]
        gdi32 = ctypes.WinDLL("gdi32",use_last_error=True)
        gdi32.CreatePolygonRgn.argtypes = [ctypes.POINTER(Point),ctypes.c_int,ctypes.c_int]
        gdi32.CreatePolygonRgn.restype = ctypes.c_void_p
        gdi32.DeleteObject.argtypes = [ctypes.c_void_p]
        self.user32.SetWindowRgn.argtypes = [ctypes.c_void_p,ctypes.c_void_p,ctypes.c_int]
        for i,hwnd in enumerate(self.viewport_windows):
            x0,y0 = self.viewport_points[i]
            x1,y1 = self.viewport_points[(i+1)%4]
            dx,dy = x1-x0,y1-y0
            start,end = 0.0,1.0
            for p,q in ((-dx,x0-left),(dx,right-x0),(-dy,y0-top),(dy,bottom-y0)):
                if p == 0:
                    if q < 0:
                        end = -1
                        break
                elif p < 0:
                    start = max(start,q/p)
                else:
                    end = min(end,q/p)
            if start > end or math.hypot(dx,dy) < 0.5:
                self.user32.ShowWindow(hwnd,0)
                continue
            ax,ay,bx,by = x0+start*dx,y0+start*dy,x0+end*dx,y0+end*dy
            length = math.hypot(dx,dy)
            nx,ny = -dy/length*2,dx/length*2
            polygon = [(ax+nx,ay+ny),(bx+nx,by+ny),(bx-nx,by-ny),(ax-nx,ay-ny)]
            x,y = math.floor(min(p[0] for p in polygon)),math.floor(min(p[1] for p in polygon))
            w,h = max(1,math.ceil(max(p[0] for p in polygon))-x+1),max(1,math.ceil(max(p[1] for p in polygon))-y+1)
            shape = tuple((round(px-x),round(py-y)) for px,py in polygon)
            geometry = (x,y,w,h,shape)
            if self.viewport_geometry.get(i) != geometry:
                points = (Point*4)(*(Point(px,py) for px,py in shape))
                region = gdi32.CreatePolygonRgn(points,4,1)
                if not region:
                    raise ctypes.WinError(ctypes.get_last_error())
                if not self.user32.SetWindowRgn(hwnd,region,1):
                    gdi32.DeleteObject(region)
                    raise ctypes.WinError(ctypes.get_last_error())
                self.viewport_geometry[i] = geometry
            if not self.user32.SetWindowPos(hwnd,-1,x,y,w,h,0x0010 | 0x0040):
                raise ctypes.WinError(ctypes.get_last_error())

    def refresh(self):
        try:
            try:
                at, result, error = self.layouts.get_nowait()
                self.last_result = at
                if result and not self.engine.suspended:
                    signature = json.dumps({k: result[k] for k in ("hwnd", "client_rect", "regions")}, sort_keys=True)
                    if self.initial_signature is None:
                        self.initial_signature = signature
                    elif signature != self.initial_signature:
                        error = "工作区位置发生变化，请恢复启动时布局或重新启动记录器"
                        result = None
                    if result:
                        self.workspace_layout = result
                        self.hwnd = result["hwnd"]
                        os.environ["MEMOLINE_CSP_HWND"] = str(self.hwnd)
                        self.engine.set_regions(result["regions"])
                status = error or ("suspended" if self.engine.suspended else "ready")
                if status != self.last_status:
                    self.engine.record("workspaceStatus", dict(status=status, layout=result))
                    self.last_status = status
                if error or self.engine.suspended:
                    self.engine.set_regions({})
                self.publish_guard()
            except queue.Empty:
                pass
            for _ in range(400):
                try:
                    msg = self.messages.get_nowait()
                except queue.Empty:
                    break
                self.last_input = time.monotonic()
                if msg["type"] == "timelineSession":
                    self.initial_package_id = msg["initialPackageId"]
                    continue
                if msg["type"] == "timelineReserved":
                    panels,ticks,refs,reason,analysis_token,final = self.package_waiters.pop(msg["requestId"])
                    for panel,entry in list(self.pending_captures.items()):
                        if entry[6] == analysis_token and entry[5] is None:
                            self.pending_captures[panel] = (*entry[:5],msg["packageId"],entry[6])
                    self.capture_panels(panels,ticks,refs,reason,package_id=msg["packageId"],
                                        analysis_token=analysis_token,final=final)
                    continue
                if msg["type"] == "captureWatermark":
                    if self.pipeline:
                        self.pipeline.watermark_reply(msg)
                    continue
                if msg["type"] == "initialStateReady":
                    emit(msg)
                    continue
                if msg["type"] == "stop":
                    self.close()
                    return
                if msg["type"] == "canvasInitialized":
                    self.canvas_roi = msg["roi"]
                    self.navigator_roi = msg["navigatorRoi"]
                    self.engine.corrected_canvas_roi = self.canvas_roi
                    self.engine.record("initializationStatus",dict(status="canvasReady",canvasRoi=self.canvas_roi,navigatorThumbnailRoi=msg["navigatorRoi"]),now_ticks())
                    continue
                if msg["type"] == "driverConfigurations":
                    self.driver_configurations = msg["data"]
                    if self.setup and self.setup.window.winfo_exists():
                        self.setup.update_driver_configurations(msg["data"])
                    continue
                if msg["type"] == "driverConfigurationResult":
                    if self.setup and self.setup.window.winfo_exists():
                        self.setup.driver_configuration_result(msg)
                    continue
                if msg["type"] == "canvasOrigin":
                    point = msg.get("point")
                    self.canvas_origin = None
                    if point and all(isinstance(point.get(k), (int, float)) and
                                     math.isfinite(point[k]) for k in ("x", "y")):
                        self.canvas_origin = (point["x"], point["y"])
                    continue
                if msg["type"] == "canvasViewport":
                    points = msg.get("points")
                    self.viewport_points = None
                    if (isinstance(points,list) and len(points) == 4 and
                            all(isinstance(p,dict) and all(isinstance(p.get(k),(int,float)) and
                                math.isfinite(p[k]) for k in ("x","y")) for p in points)):
                        self.viewport_points = [(p["x"],p["y"]) for p in points]
                    continue
                if msg["type"] == "canvasRetry":
                    self.schedule_capture([CANVAS,NAVIGATOR],msg["ticks"],msg["refs"],
                                          "canvasRetry:"+msg["captureId"],msg.get("packageId"))
                    continue
                if msg["type"] == "captureFinished":
                    self.captures_pending = max(0,self.captures_pending-1)
                    if msg.get("success"):
                        self.engine.flash_update(msg.get("panels", []))
                    continue
                if msg["type"] == "initializationFailed":
                    self.engine.record("initializationStatus",dict(status="failed",message=msg["message"]),now_ticks())
                    print("[Initialization] "+msg["message"]+"；修正画布显示后，相关操作将重试初始化。",file=sys.stderr)
                    continue
                if msg["type"] == "guardReleased":
                    self.guard_ids.discard(msg["saveId"])
                    self.guard_busy = bool(self.guard_ids)
                    if self.pipeline:
                        self.pipeline.request_clip(msg["saveId"],msg["saveTicks"])
                    continue
                if msg["type"] == "guardStarted":
                    self.guard_ids.add(msg["saveId"])
                    self.guard_busy = True
                    continue
                if msg["type"] == "error":
                    raise RuntimeError(msg["message"])
                if msg["type"] == "catalog":
                    self.catalog = self.engine.catalog = msg["catalog"]
                    self.engine.tool_state_targets.clear()
                    self.engine.gesture_signature = None
                    self.engine.record("shortcutConfiguration", self.catalog.snapshot())
                    self.publish_guard()
                    continue
                if msg["type"] == "configurationError":
                    self.engine.record("updateActivatorError", dict(message=msg["message"]))
                    continue
                self.current_package_id = msg.get("statePackageId") if msg["type"] == "input" else None
                self.engine.handle(msg)
                if self.current_package_id and self.pipeline:
                    self.pipeline.timeline.not_requested(self.current_package_id)
                self.current_package_id = None
            stale = time.monotonic() - self.last_result > 1.5 or time.monotonic() - self.last_input > 1
            hovering = self.engine.last_cursor and self.engine.last_cursor.get("inCsp")
            visible = not stale and self.hwnd and (hovering or self.user32.GetForegroundWindow() == self.hwnd)
            foreground = self.hwnd and self.user32.GetForegroundWindow() == self.hwnd
            if self.pipeline and foreground and self.engine.regions and not self.guard_busy:
                if self.initial_capture_pending:
                    self.initial_capture_pending = False
                    self.capture_panels(self.engine.regions,now_ticks(),reason="initialization",initialize=True)
                self.flush_pending_captures()
            self.show(self.engine.active_panels() if visible else set())
            self.show_origin(bool(foreground and time.monotonic()-self.last_result <= 1.5))
            self.show_core_rois(bool(foreground and time.monotonic()-self.last_result <= 1.5))
            self.show_viewport(bool(foreground and time.monotonic()-self.last_result <= 1.5))
        except Exception as ex:
            self.engine.record("updateActivatorError", dict(message=str(ex)))
            self.close()
            return
        self.root.after(25, self.refresh)

    def close(self):
        if self.closing:
            return
        self.closing = True
        self.stop.set()
        self.show(set())
        self.show_origin(False)
        self.show_core_rois(False)
        self.show_viewport(False)
        if self.pipeline:
            regions = dict(self.engine.regions)
            if self.canvas_roi:
                regions[CANVAS] = self.canvas_roi
            # Drain evidence already requested, then a final observation of each
            # active panel. Shutdown never discards a continuous interaction.
            for panels,ticks,refs,reason,package_id,analysis_token,final,initialize in self.scheduled_captures.values():
                self.pipeline.capture(panels,regions,ticks,refs,reason,initialize,package_id,analysis_token,final)
            self.scheduled_captures.clear()
            for _,ticks,refs,reason,panels,package_id,analysis_token in self.pending_captures.values():
                if package_id:
                    self.pipeline.capture(panels,regions,ticks,refs,reason,False,package_id,analysis_token,True)
                else:
                    self.pipeline.record("captureUnavailable",ticks,refs,dict(panels=list(panels),
                        reason="sessionStoppedBeforePackageReserved",triggerReason=reason))
            self.pending_captures.clear()
            self.pipeline.close()
        self.root.destroy()

    def run(self):
        try:
            self.root.mainloop()
        finally:
            self.stop.set()


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")
    # Recognition libraries may print diagnostics. Reserve the original pipe for emit().
    sys.stdout = sys.stderr
    parser = argparse.ArgumentParser()
    parser.add_argument("--diagnose", action="store_true")
    args = parser.parse_args()
    enable_per_monitor_dpi_awareness()
    if args.diagnose:
        catalog = Catalog()
        try:
            _, layout = inspect(Path(os.environ["MEMOLINE_CSP_DOCK"]) if os.environ.get("MEMOLINE_CSP_DOCK") else find_dock_file())
        except Exception as ex:
            layout = dict(status="unavailable", reason=str(ex))
        emit(dict(catalog=catalog.snapshot(), layout=layout))
        return
    try:
        Runtime().run()
    except Exception as ex:
        emit(dict(type="state", kind="updateActivatorError", ticks=0,
                  relatedEventIds=[], data=dict(message=str(ex))))
        raise


if __name__ == "__main__":
    main()
