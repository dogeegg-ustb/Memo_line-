"""Stash chosen CSP floating palettes on a virtual screen and serve them to other programs.

    py -3 recognizer/virtual_panel_probe/panel_host.py                control window + interface
    py -3 recognizer/virtual_panel_probe/panel_host.py --diagnose     monitors, virtual displays, CSP windows
    py -3 recognizer/virtual_panel_probe/panel_host.py --restore-all  bring palettes back, detach the virtual screens
    py -3 recognizer/virtual_panel_probe/panel_host.py --query listWindows
    py -3 recognizer/virtual_panel_probe/panel_host.py --query "{\"command\":\"captureWindow\",\"kind\":\"palettekindhistory\"}" --out history.png
"""
from __future__ import annotations

import argparse
import base64
import ctypes
import json
import os
import queue
import sys
import threading
import time
import tkinter as tk
import traceback
from dataclasses import asdict, dataclass, field
from pathlib import Path
from tkinter import ttk

import paths  # noqa: F401
import virtual_display
from api import SCHEMA_VERSION, AlreadyRunning, PipeClient, PipeServer
from capture import capture
from csp_panel_validator.window_service import enable_per_monitor_dpi_awareness
from csp_windows import CspWindow, csp_windows, is_window, root_at, window_pid
from edge_drawer import EdgeDrawer, cursor
from guard import Guard
from stash import StashManager, bring_back, fits, is_hidden, physical_screen, stash_area
from virtual_display import VirtualDisplayMissing, monitor_at
from workspace import PALETTE_NAMES, load_workspace, palette_name

HERE = Path(__file__).resolve().parent
DEFAULTS = dict(
    virtualHardwareIds=[r"Root\MttVDD"],
    matchMainScale=True,
    detachOnExit=True,
    detachDelaySeconds=5.0,
    virtualMinimumSize=None,
    virtualScreenAllowedProcesses=["CLIPStudioPaint.exe", "CLIPStudioPaintVer1.exe", "CLIPStudioPaintVer2.exe"],
    wallpaperBackdrop=True,
    pipeName="MemoLine.PanelStash",
    cornerThresholdPx=32,
    edgeDrawer=True,
    edgeHoverPx=2,
    edgeDwellMs=150,
    stashGapPx=8,
)
SIDES = {"left": "左侧", "right": "右侧"}
CORNERS = {"top-left": "左上角", "bottom-left": "左下角", "top-right": "右上角", "bottom-right": "右下角",
           "virtual": "直接拖进虚拟屏", "api": "外部程序", "": "手动"}
# CSP creates a torn-off palette's window a few hundred milliseconds after the release.
DROP_CHECKS_MS = (150, 450, 900, 1500)
# Windows that appear while a button is held and vanish soon after the release are CSP's drag
# preview and dock-hint windows, not palettes.
PROVISIONAL_SECONDS = 1.0
VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, SM_SWAPBUTTON = 0x01, 0x02, 0x04, 23
CALL_TIMEOUT = 5.0
FORMATS = ("png", "bgrx")
SELECTOR = ("hwnd", "kind")
OPTIONS = {"getStatus": (), "listWindows": ("stashedOnly",), "captureWindow": (*SELECTOR, "format"),
           "stashWindow": (*SELECTOR, "side"), "restoreWindow": SELECTOR}
USER32 = ctypes.WinDLL("user32")
USER32.GetAsyncKeyState.restype = ctypes.c_short


def load_settings() -> dict:
    path = HERE / "settings.json"
    settings = dict(DEFAULTS)
    try:
        settings.update(json.loads(path.read_text(encoding="utf-8")))
    except FileNotFoundError:
        path.write_text(json.dumps(DEFAULTS, ensure_ascii=False, indent=2), encoding="utf-8")
    return settings


@dataclass(frozen=True)
class WindowLabel:
    kinds: tuple[str, ...]
    current: str | None
    source: str

    def display(self) -> str:
        if not self.kinds:
            return "未识别"
        return " | ".join(("▶" if k == self.current and len(self.kinds) > 1 else "") + palette_name(k)
                          for k in self.kinds)


@dataclass
class Pending:
    """A palette waiting for room on the virtual screen."""
    corner: str
    prior: tuple | None
    side: str | None
    waiters: list = field(default_factory=list)

    def notify(self, error: str | None) -> None:
        for waiter in self.waiters:
            waiter(error)


class Host:
    def __init__(self, settings: dict):
        self.settings = settings
        # First: a second instance stops here (AlreadyRunning) before touching the log or any window.
        self.server = PipeServer(settings["pipeName"], self.handle_request, self.log)
        self.hardware = tuple(settings["virtualHardwareIds"])
        self.log_stream = (HERE / "panel_host.log").open("w", encoding="utf-8")
        self.root = tk.Tk()
        self.root.title("CSP 浮动面板收纳")
        self.scale = self.root.winfo_fpixels("1i") / 96
        self.calls, self.jobs, self.results = queue.SimpleQueue(), queue.SimpleQueue(), queue.SimpleQueue()
        self.workspace = load_workspace()
        self.virtual_devices: set[str] = set()
        self.screens = virtual_display.monitors()
        self.virtual_error: str | None = None
        self.display_busy = self.provide_again = self.virtual_active = False
        self.pending: dict[int, Pending] = {}
        self.empty_since: float | None = None
        self.main: CspWindow | None = None
        self.windows: dict[int, CspWindow] = {}
        self.labels: dict[int, WindowLabel] = {}
        self.previews: dict[int, object] = {}
        self.provisional: dict[int, float] = {}
        self.primary = VK_RBUTTON if USER32.GetSystemMetrics(SM_SWAPBUTTON) else VK_LBUTTON
        self.pressed = self.any_button = False
        self.last_release = 0.0
        self.next_refresh = self.next_config = self.next_thumbs = 0.0
        self.closing = False
        self.stash = StashManager(HERE / "state.json", settings["cornerThresholdPx"], settings["stashGapPx"], self.log)
        self.guard = Guard(self.root, settings["virtualScreenAllowedProcesses"], self.log)
        self.build_ui()
        self.drawer = EdgeDrawer(self.root, self.px, lambda: [m for m in self.screens if not m.virtual],
                                 lambda side: len(self.stash.on_side(side, self.windows)), self.drawer_items,
                                 self.bring_from_drawer, lambda: self.drawer_enabled.get() and not self.closing,
                                 settings["edgeHoverPx"], settings["edgeDwellMs"] / 1000)
        self.worker = threading.Thread(target=self.capture_loop, name="panel-thumbnails", daemon=True)
        self.worker.start()
        self.server.start()
        self.log(f"接口已开启：\\\\.\\pipe\\{settings['pipeName']}")
        self.adopt_state()
        self.refresh()
        if not any(h in self.windows for h in self.stash.records):
            # Windows may have attached the virtual monitor on its own (e.g. as a clone at boot).
            self.detach("启动时没有收纳的面板")
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.root.after(15, self.tick)

    # ---------- UI ----------
    def px(self, value: float) -> int:
        return round(value * self.scale)

    def build_ui(self) -> None:
        root = self.root
        root.geometry(f"{self.px(860)}x{self.px(560)}")
        top = ttk.Frame(root, padding=8)
        top.pack(fill="x")
        self.status = tk.StringVar()
        ttk.Label(top, textvariable=self.status, justify="left", wraplength=self.px(560)).pack(
            side="left", fill="x", expand=True)
        options = ttk.Frame(root, padding=(8, 0))
        options.pack(fill="x")
        self.corner_enabled = tk.BooleanVar(value=True)
        ttk.Checkbutton(options, text="窗口角拖到屏幕角时收纳（左角→左侧，右角→右侧）",
                        variable=self.corner_enabled).pack(side="left")
        self.drawer_enabled = tk.BooleanVar(value=self.settings["edgeDrawer"])
        ttk.Checkbutton(options, text="屏幕边缘抽拉按键", variable=self.drawer_enabled).pack(side="left", padx=12)
        body = ttk.PanedWindow(root, orient="horizontal")
        body.pack(fill="both", expand=True, padx=8, pady=6)
        left, right = ttk.Frame(body), ttk.Frame(body)
        body.add(left, weight=3)
        body.add(right, weight=2)
        columns = [("name", "面板（工作区名称）", 220), ("state", "状态", 100), ("source", "名称来源", 130),
                   ("size", "尺寸", 90)]
        self.tree = ttk.Treeview(left, columns=[c[0] for c in columns], show="headings", selectmode="browse")
        for key, title, width in columns:
            self.tree.heading(key, text=title)
            self.tree.column(key, width=self.px(width), anchor="w")
        self.tree.pack(fill="both", expand=True)
        self.tree.bind("<<TreeviewSelect>>", lambda event: self.on_select())
        actions = ttk.Frame(left)
        actions.pack(fill="x", pady=4)
        for text, command in (("收纳到左侧", lambda: self.stash_selected("left")),
                              ("收纳到右侧", lambda: self.stash_selected("right")),
                              ("取回", self.restore_selected), ("全部取回", self.restore_everything)):
            ttk.Button(actions, text=text, command=command).pack(side="left", padx=2)
        names = ttk.Frame(left)
        names.pack(fill="x")
        ttk.Label(names, text="指定名称：").pack(side="left")
        self.kind_box = ttk.Combobox(names, state="readonly", width=28)
        self.kind_box.pack(side="left")
        ttk.Button(names, text="应用", command=self.assign_kind).pack(side="left", padx=2)
        self.preview = ttk.Label(right, text="选择一行查看窗口画面", anchor="center")
        self.preview.pack(fill="both", expand=True)
        self.detail = tk.StringVar()
        ttk.Label(right, textvariable=self.detail, wraplength=self.px(300), justify="left").pack(fill="x")
        self.log_text = tk.Text(root, height=8, state="disabled", wrap="none")
        self.log_text.pack(fill="x", padx=8, pady=(0, 8))
        self.update_kind_choices()

    def update_kind_choices(self) -> None:
        self.kind_choices = list(dict.fromkeys([*self.workspace.kinds(), *PALETTE_NAMES]))
        self.kind_box["values"] = [f"{palette_name(k)}（{k.removeprefix('palettekind')}）" for k in self.kind_choices]

    def log(self, message: str, **_data) -> None:
        line = time.strftime("%H:%M:%S ") + message
        print(line, flush=True)
        try:
            self.log_stream.write(line + "\n")
            self.log_stream.flush()
        except ValueError:
            pass  # closed during shutdown
        if threading.current_thread() is not threading.main_thread():
            self.calls.put(lambda: self.append_log(line))
        elif not self.closing:
            self.append_log(line)

    def append_log(self, line: str) -> None:
        if self.closing:
            return
        self.log_text.configure(state="normal")
        self.log_text.insert("end", line + "\n")
        if int(self.log_text.index("end-1c").split(".")[0]) > 400:
            self.log_text.delete("1.0", "100.0")
        self.log_text.see("end")
        self.log_text.configure(state="disabled")

    def selected(self) -> int | None:
        selection = self.tree.selection()
        return int(selection[0]) if selection else None

    def display_name(self, hwnd: int) -> str:
        label, window = self.labels.get(hwnd), self.windows.get(hwnd)
        if label and label.kinds:
            return " | ".join(palette_name(k) for k in label.kinds)
        return (window.title if window else "") or "未命名面板"

    def name(self, hwnd: int) -> str:
        return f"{self.display_name(hwnd)} [{hwnd:#x}]"

    def update_rows(self) -> None:
        present = set()
        for hwnd, window in self.windows.items():
            label, record = self.labels[hwnd], self.stash.records.get(hwnd)
            state = (f"已收纳·{SIDES.get(record.side, record.side)}" if record
                     else "在虚拟屏" if is_hidden(window, self.screens) else "可见")
            values = (label.display(), state, label.source, f"{window.size[0]}×{window.size[1]}")
            iid = str(hwnd)
            present.add(iid)
            if self.tree.exists(iid):
                self.tree.item(iid, values=values)
            else:
                self.tree.insert("", "end", iid=iid, values=values)
        for iid in self.tree.get_children():
            if iid not in present:
                self.tree.delete(iid)
        virtual = [m for m in self.screens if m.virtual]
        screen = ("调整中…" if self.display_busy else self.virtual_error if self.virtual_error and not virtual
                  else "、".join(f"{m.device} {m.width}×{m.height}" for m in virtual) or "未接入（收纳时自动接入）")
        self.status.set("  ·  ".join((
            f"CSP：{'已连接' if self.main else '未找到主窗口'}",
            f"浮动窗口 {len(self.windows)}",
            "收纳 " + " / ".join(f"{SIDES[s]} {len(self.stash.on_side(s, self.windows))}" for s in SIDES),
            f"虚拟屏：{screen}",
            f"接口 {self.settings['pipeName']}",
            "工作区不可用：" + self.workspace.error if self.workspace.error else f"工作区 {len(self.workspace.kinds())} 个面板")))

    def on_select(self) -> None:
        hwnd = self.selected()
        if hwnd in self.windows:
            self.jobs.put(self.windows[hwnd])
        self.show_preview()

    def show_preview(self) -> None:
        hwnd = self.selected()
        if hwnd is None:
            return
        from PIL import ImageTk
        image = self.previews.get(hwnd)
        if image is not None:
            self.preview_image = ImageTk.PhotoImage(image)
            self.preview.configure(image=self.preview_image, text="")
        else:
            self.preview.configure(image="", text="正在截图…")
        label, window = self.labels.get(hwnd), self.windows.get(hwnd)
        kinds = ", ".join(label.kinds) if label else ""
        self.detail.set(f"HWND {hwnd:#x}  类名 {window.class_name if window else '?'}\n"
                        f"标题「{window.title if window else ''}」\n面板 ID：{kinds or '无'}")

    # ---------- main loop ----------
    def tick(self) -> None:
        if self.closing:
            return
        now = time.monotonic()
        try:
            while True:
                try:
                    self.calls.get_nowait()()
                except queue.Empty:
                    break
            self.poll_buttons(now)
            if now >= self.next_refresh:
                self.refresh()
            self.drain_thumbnails()
            self.request_drawer_thumbnails(now)
            self.drawer.tick(now, self.any_button)
        except Exception as ex:
            self.log(f"内部错误：{ex!r}\n{traceback.format_exc()}")
        self.root.after(15, self.tick)

    def button(self, vk: int) -> bool:
        return bool(USER32.GetAsyncKeyState(vk) & 0x8000)

    def poll_buttons(self, now: float) -> None:
        """Mouse buttons are polled rather than hooked: a low-level hook in this process would add
        input latency to CSP whenever Python is busy (e.g. encoding a capture)."""
        down = self.button(self.primary)
        self.any_button = down or self.button(VK_LBUTTON ^ VK_RBUTTON ^ self.primary) or self.button(VK_MBUTTON)
        if down and not self.pressed:
            x, y = cursor()
            self.drawer.on_press(x, y)
            if self.over_csp(x, y):
                self.stash.begin_press(self.windows.values())
        elif self.pressed and not down:
            self.last_release = now
            if self.stash.press_bounds is not None:
                for delay in DROP_CHECKS_MS:
                    self.root.after(delay, self.check_drop, self.stash.press_id, delay == DROP_CHECKS_MS[-1])
        self.pressed = down

    def over_csp(self, x: int, y: int) -> bool:
        if self.main is None:
            return False
        root = root_at(x, y)
        return bool(root) and (root == self.main.hwnd or root in self.windows or window_pid(root) == self.main.pid)

    def refresh(self) -> None:
        now = time.monotonic()
        self.next_refresh = now + 0.4
        if now >= self.next_config:
            self.next_config = now + 2
            self.reload_configuration()
            if not self.display_busy:
                try:
                    found = virtual_display.virtual_targets(self.hardware)
                    self.virtual_devices = virtual_display.extended(found)
                    self.virtual_active = any(t.active for t in found)  # extended or cloned
                except OSError as ex:
                    self.log(f"读取显示器配置失败：{ex}")
        self.screens = virtual_display.monitors(self.virtual_devices)
        main, floating = csp_windows()
        if (main and main.hwnd) != (self.main and self.main.hwnd):
            self.log(f"CSP 主窗口：{hex(main.hwnd) if main else '未找到'}")
        self.main = main
        current = {w.hwnd: w for w in floating}
        tracked = {}
        for hwnd, window in current.items():
            if hwnd not in self.labels:
                if self.any_button and hwnd not in self.provisional:
                    self.provisional[hwnd] = now
                if hwnd in self.provisional and (self.any_button or now - self.last_release < PROVISIONAL_SECONDS):
                    continue
                self.provisional.pop(hwnd, None)
                self.labels[hwnd] = self.identify(window)
                self.log(f"发现浮动窗口 {self.name(hwnd)}，名称来源：{self.labels[hwnd].source}")
            tracked[hwnd] = window
        self.provisional = {h: t for h, t in self.provisional.items() if h in current}
        for hwnd in [h for h in self.labels if h not in current and not is_window(h)]:
            self.log(f"浮动窗口已关闭 {self.name(hwnd)}")
            self.labels.pop(hwnd, None)
            self.previews.pop(hwnd, None)
        self.windows = tracked
        self.stash.maintain(tracked, self.screens)
        self.detach_when_idle(now)
        self.keep_virtual_screen_private()
        self.update_rows()

    def reload_configuration(self) -> None:
        path = self.workspace.path
        try:
            changed = path is None or path.stat().st_mtime_ns != self.workspace.mtime_ns
        except OSError:
            changed = True
        if changed:
            self.workspace = load_workspace()
            self.update_kind_choices()
            for hwnd, label in list(self.labels.items()):
                if not label.kinds and hwnd in self.windows:
                    self.labels[hwnd] = self.identify(self.windows[hwnd])
            self.log("重新读取工作区配置" + (f"失败：{self.workspace.error}" if self.workspace.error else ""))

    def identify(self, window: CspWindow) -> WindowLabel:
        monitor = monitor_at(self.screens, *window.center)
        definition = self.workspace.match_floating([window.rect, window.bounds], monitor.dpi if monitor else 96)
        if definition:
            return WindowLabel(definition.kinds, definition.current, f"工作区浮动窗口{definition.index + 1}")
        kind = self.workspace.match_title(window.title)
        if kind:
            return WindowLabel((kind,), kind, "窗口标题")
        return WindowLabel((), None, "未识别，可指定名称")

    def adopt_state(self) -> None:
        """Take over palettes a previous run left stashed (it crashed or was killed)."""
        records = [r for r in self.stash.load() if is_window(r.hwnd)]
        self.stash.records = {r.hwnd: r for r in records}
        if records:
            self.log(f"接管上次未取回的 {len(records)} 个面板")

    # ---------- stash ----------
    def check_drop(self, press: int, last: bool) -> None:
        if press != self.stash.press_id or self.stash.press_bounds is None:
            return  # a newer press took over
        self.refresh()
        if not self.corner_enabled.get() or self.main is None or not self.main.enabled:
            self.stash.end_press()  # stashing is off, or a modal CSP dialog is open
            return
        for window, corner, prior in self.stash.dropped(list(self.windows.values()), self.screens):
            self.request_stash(window, corner, prior)
        if last:
            self.stash.end_press()

    def stash_window(self, window: CspWindow, corner: str, prior=None, side: str | None = None) -> bool:
        if not self.stash.stash(window, self.screens, self.windows, prior, corner, side):
            return False
        side = self.stash.records[window.hwnd].side
        self.log(f"已收纳 {self.name(window.hwnd)}（{CORNERS.get(corner, corner)} → {SIDES[side]}）")
        self.root.after(700, self.request_thumbnail, window.hwnd)
        return True

    def restore(self, hwnd: int) -> bool:
        if hwnd in self.stash.records:
            restored = self.stash.restore(hwnd, self.screens, self.windows.get(hwnd))
            self.log(f"已取回 {self.name(hwnd)}")
            return restored
        window = self.windows.get(hwnd)
        if window is not None and is_hidden(window, self.screens):
            bring_back(window, self.screens)
            self.log(f"已取回 {self.name(hwnd)}（无收纳记录，放到主屏左上）")
            return True
        return False

    def restore_everything(self) -> int:
        # Before restore_all: afterwards the restored windows look like hidden windows with no record.
        others = [w for h, w in self.windows.items() if h not in self.stash.records and is_hidden(w, self.screens)]
        count = self.stash.restore_all(self.screens, self.windows)
        for index, window in enumerate(others):
            bring_back(window, self.screens, index)
        if count or others:
            self.log(f"已取回 {count + len(others)} 个浮动窗口")
        return count + len(others)

    def wait_until_off_virtual(self, timeout=1.5) -> None:
        """Moves are asynchronous (SWP_ASYNCWINDOWPOS); let CSP apply them before a monitor goes away."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            _, floating = csp_windows()
            if not any(is_hidden(w, self.screens) for w in floating):
                return
            time.sleep(0.05)

    # ---------- virtual screen: attached only while something is stashed ----------
    def request_stash(self, window: CspWindow, corner: str, prior=None, side: str | None = None,
                      waiter=None) -> None:
        """Stash `window` once the virtual screen has room for it; `waiter(error or None)` reports the outcome."""
        if window.hwnd in self.stash.records:
            if waiter:
                waiter(None)
            return
        entry = self.pending.setdefault(window.hwnd, Pending(corner, prior, side))
        if waiter:
            entry.waiters.append(waiter)
        self.provide()

    def provide(self) -> None:
        if self.display_busy:
            self.provide_again = True
            return
        incoming = [self.windows[h] for h in self.pending if h in self.windows]
        sizes, gap = self.stash.sizes(self.windows, incoming), self.settings["stashGapPx"]
        area = stash_area(self.screens)
        if area and fits(sizes, area[2] - area[0], area[3] - area[1], gap):
            self.flush_pending()
            return
        # Room to spare from the start: every later resize stalls the desktop for about a second,
        # while unused area costs next to nothing.
        main = physical_screen(self.screens)
        least = tuple(self.settings["virtualMinimumSize"] or (main.width, main.height))

        def roomy(size):
            return size[0] >= least[0] and size[1] >= least[1] and fits(sizes, *size, gap)
        self.display_job(lambda: virtual_display.provide(roomy, self.hardware, self.settings["matchMainScale"]),
                         self.provided)

    def provided(self, result, error: str | None) -> None:
        if error:
            self.virtual_error = error
            self.log(f"虚拟屏不可用：{error}")
            pending, self.pending = self.pending, {}
            for entry in pending.values():
                entry.notify(error)
            return
        screen, steps = result
        self.virtual_error = None
        self.virtual_devices, self.virtual_active = {screen.device}, True
        self.screens = [m for m in self.screens if not m.virtual] + [screen]
        if steps:
            self.log(f"虚拟屏 {screen.device}：" + "，".join(steps))
        if self.provide_again:
            self.provide_again = False
            self.provide()  # more palettes arrived meanwhile; check the size again
        else:
            self.flush_pending()

    def flush_pending(self) -> None:
        pending, self.pending = self.pending, {}
        for hwnd, entry in pending.items():
            window = self.windows.get(hwnd)
            if window is None:
                entry.notify("窗口已关闭")
            else:
                entry.notify(None if self.stash_window(window, entry.corner, entry.prior, entry.side) else "收纳失败")

    def keep_virtual_screen_private(self) -> None:
        """Backdrop under the palettes and no other program's windows on the virtual screen (guard.py)."""
        virtual = next((m for m in self.screens if m.virtual), None)
        if virtual is None or self.display_busy:
            if virtual is None and self.guard.backdrop is not None:
                self.guard.hide()
            return
        if self.settings["wallpaperBackdrop"] and self.guard.backdrop_rect != virtual.rect:
            self.guard.show(virtual.rect)
        self.guard.patrol(virtual.rect, physical_screen(self.screens).work)

    def detach_when_idle(self, now: float) -> None:
        """Detach the virtual screen a few seconds after the last stashed palette left it.

        Attaching or resizing it later stalls desktop composition for about a second (the display
        reconfiguration); `detachDelaySeconds: null` keeps it attached instead.
        """
        delay = self.settings["detachDelaySeconds"]
        busy = self.pending or self.display_busy or any(h in self.windows for h in self.stash.records)
        if busy or not self.virtual_active or delay is None:
            self.empty_since = None
        elif self.empty_since is None:
            self.empty_since = now
        elif now - self.empty_since >= delay:
            self.empty_since = None
            self.detach("没有收纳的面板")

    def detach(self, reason: str) -> None:
        def done(detached, error):
            if error:
                self.log(f"断开虚拟屏失败：{error}")
                return
            self.virtual_devices, self.virtual_active = set(), False
            self.screens = [m for m in self.screens if not m.virtual]
            if detached:
                self.log(f"{reason}，虚拟屏已断开")
            if self.pending:
                self.provide()
        self.display_job(lambda: virtual_display.release(self.hardware), done)

    def display_job(self, work, done) -> None:
        """Run a display change off the Tk thread, then `done(result, error)` on it.

        Only one runs at a time; display changes take 0.1–1 s and must not freeze the drawer.
        """
        self.display_busy = True

        def run():
            try:
                outcome = (work(), None)
            except (VirtualDisplayMissing, OSError, TimeoutError) as ex:
                outcome = (None, str(ex))
            self.calls.put(lambda: self.finish_display_job(done, *outcome))
        threading.Thread(target=run, name="virtual-display", daemon=True).start()

    def finish_display_job(self, done, result, error) -> None:
        self.display_busy = False
        done(result, error)
        self.next_config = 0.0  # re-read the display configuration on the next refresh

    # ---------- thumbnails (drawer cards and the preview pane) ----------
    def request_thumbnail(self, hwnd: int) -> None:
        if hwnd in self.windows:
            self.jobs.put(self.windows[hwnd])

    def request_drawer_thumbnails(self, now: float) -> None:
        side = self.drawer.drawer_side if self.drawer.drawer_open else self.drawer.tab_side if self.drawer.tab_shown else None
        if side is None or now < self.next_thumbs:
            return
        self.next_thumbs = now + 1.0
        for record in self.stash.on_side(side, self.windows):
            self.jobs.put(self.windows[record.hwnd])

    def capture_loop(self) -> None:
        while True:
            window = self.jobs.get()
            if window is None:
                return
            try:
                image = capture(window.hwnd, window.bounds).image()
                image.thumbnail((self.px(320), self.px(420)))
                self.results.put((window.hwnd, image))
            except Exception as ex:
                self.results.put((window.hwnd, ex))

    def drain_thumbnails(self) -> None:
        while True:
            try:
                hwnd, image = self.results.get_nowait()
            except queue.Empty:
                return
            if isinstance(image, Exception):
                continue  # the window closed meanwhile; the next request tries again
            if hwnd in self.windows:
                self.previews[hwnd] = image
                if hwnd == self.selected():
                    self.show_preview()

    # ---------- edge drawer ----------
    def drawer_items(self, side: str) -> list[dict]:
        return [dict(hwnd=r.hwnd, name=self.display_name(r.hwnd), image=self.previews.get(r.hwnd))
                for r in self.stash.on_side(side, self.windows)]

    def bring_from_drawer(self, hwnd: int, side: str, monitor, y: int) -> None:
        window = self.windows.get(hwnd)
        if window is not None and self.stash.bring_here(hwnd, self.screens, window, monitor, side, y, self.px(16)):
            self.log(f"已从{SIDES[side]}抽屉带回 {self.name(hwnd)}")
            self.root.after(300, self.refresh)

    # ---------- control window buttons ----------
    def stash_selected(self, side: str) -> None:
        hwnd = self.selected()
        if hwnd in self.windows:
            self.request_stash(self.windows[hwnd], "", side=side)

    def restore_selected(self) -> None:
        hwnd = self.selected()
        if hwnd is not None:
            self.restore(hwnd)

    def assign_kind(self) -> None:
        hwnd, index = self.selected(), self.kind_box.current()
        if hwnd in self.labels and index >= 0:
            kind = self.kind_choices[index]
            self.labels[hwnd] = WindowLabel((kind,), kind, "手动指定")
            self.log(f"{hwnd:#x} 指定为 {palette_name(kind)}")
            self.update_rows()
            self.show_preview()

    # ---------- interface (pipe client threads) ----------
    def handle_request(self, request: dict) -> dict:
        command = request["command"]
        if command not in OPTIONS:
            raise ValueError(f"未知命令 {command}，可用：{', '.join(OPTIONS)}")
        unknown = set(request) - {"command", "requestId", *OPTIONS[command]}
        if unknown:
            raise ValueError(f"{command} 不支持的字段：{', '.join(sorted(unknown))}")
        if command == "getStatus":
            return self.call(self.status_info)
        if command == "listWindows":
            stashed_only = bool(request.get("stashedOnly", False))
            return self.call(lambda: dict(windows=[self.window_info(h) for h in self.windows
                                                   if not stashed_only or h in self.stash.records]))
        if command == "captureWindow":
            image_format = request.get("format", "png")
            if image_format not in FORMATS:
                raise ValueError(f"format 只能是 {' / '.join(FORMATS)}")
            hwnd, info, bounds = self.call(lambda: self.lookup(request, with_bounds=True))
            shot = capture(hwnd, bounds)
            data = shot.png() if image_format == "png" else shot.bgrx
            return dict(window=info, format=image_format, width=shot.width, height=shot.height, rect=list(shot.rect),
                        capturedAt=shot.captured_at, data=base64.b64encode(data).decode("ascii"))
        if command == "stashWindow":
            side = request.get("side")
            if side not in (None, *SIDES):
                raise ValueError("side 只能是 left / right")
            done, box = threading.Event(), {}

            def waiter(error):
                box["error"] = error
                done.set()
            hwnd = self.call(lambda: self.api_stash(request, side, waiter))
            if not done.wait(CALL_TIMEOUT * 3):
                raise TimeoutError("等待虚拟屏超时")
            if box["error"]:
                raise RuntimeError(box["error"])
            return self.call(lambda: dict(window=self.window_info(hwnd)))
        return self.call(lambda: self.api_restore(request))

    def call(self, function):
        """Run `function` on the Tk thread, which owns every window and stash record."""
        if self.closing:
            raise RuntimeError("程序正在退出")
        done, box = threading.Event(), {}

        def run():
            try:
                box["value"] = function()
            except Exception as ex:
                box["error"] = ex
            done.set()
        self.calls.put(run)
        if not done.wait(CALL_TIMEOUT):
            raise TimeoutError("界面线程没有响应")
        if "error" in box:
            raise box["error"]
        return box["value"]

    def lookup(self, request: dict, with_bounds=False):
        hwnd, kind = request.get("hwnd"), request.get("kind")
        if (hwnd is None) == (kind is None):
            raise ValueError("hwnd 和 kind 必须且只能给一个")
        if hwnd is not None:
            if not isinstance(hwnd, int) or hwnd not in self.windows:
                raise LookupError(f"没有 HWND 为 {hwnd} 的 CSP 浮动窗口")
        else:
            wanted = kind if str(kind).startswith("palettekind") else self.workspace.match_title(str(kind))
            matches = [h for h in self.windows if wanted and wanted in self.labels[h].kinds]
            if not matches:
                raise LookupError(f"没有名为 {kind} 的 CSP 浮动窗口")
            hwnd = next((h for h in matches if self.labels[h].current == wanted), matches[0])
        if with_bounds:
            return hwnd, self.window_info(hwnd), self.windows[hwnd].bounds
        return hwnd

    def window_info(self, hwnd: int) -> dict:
        window, label, record = self.windows[hwnd], self.labels[hwnd], self.stash.records.get(hwnd)
        monitor = monitor_at(self.screens, *window.center)
        return dict(hwnd=hwnd, name=self.display_name(hwnd), kinds=list(label.kinds), currentKind=label.current,
                    nameSource=label.source, title=window.title, className=window.class_name,
                    stashed=record is not None, side=record.side if record else None,
                    onVirtualScreen=is_hidden(window, self.screens), rect=list(window.bounds),
                    width=window.size[0], height=window.size[1], dpi=monitor.dpi if monitor else None)

    def status_info(self) -> dict:
        virtual = [m for m in self.screens if m.virtual]
        return dict(schemaVersion=SCHEMA_VERSION, pipeName=self.settings["pipeName"], processId=os.getpid(),
                    csp=dict(running=self.main is not None, mainHwnd=self.main.hwnd if self.main else None,
                             processId=self.main.pid if self.main else None),
                    virtualDisplay=dict(attached=bool(virtual), changing=self.display_busy,
                                        error=self.virtual_error,
                                        monitors=[dict(device=m.device, rect=list(m.rect), dpi=m.dpi) for m in virtual]),
                    windows=len(self.windows), stashed=len(self.stash.on_side("left", self.windows))
                    + len(self.stash.on_side("right", self.windows)))

    def api_stash(self, request: dict, side: str | None, waiter) -> int:
        hwnd = self.lookup(request)
        self.request_stash(self.windows[hwnd], "api", side=side, waiter=waiter)
        return hwnd

    def api_restore(self, request: dict) -> dict:
        hwnd = self.lookup(request)
        if not self.restore(hwnd):
            raise LookupError(f"{self.name(hwnd)} 没有收纳，也不在虚拟屏上")
        return dict(window=self.window_info(hwnd))

    # ---------- shutdown ----------
    def close(self) -> None:
        if self.closing:
            return
        try:
            self.refresh()
            self.restore_everything()
            self.guard.hide()
            if self.settings["detachOnExit"]:
                self.wait_until_off_virtual()
                if virtual_display.release(self.hardware):
                    self.log("虚拟屏已断开")
        except Exception as ex:
            self.log(f"退出时取回或断开失败：{ex!r}")
        self.closing = True
        self.server.stop()
        self.jobs.put(None)
        self.worker.join(timeout=2)
        self.log_stream.close()
        self.root.destroy()

    def run(self) -> None:
        try:
            self.root.mainloop()
        finally:
            self.close()


def diagnose(settings: dict) -> dict:
    hardware = tuple(settings["virtualHardwareIds"])
    targets = virtual_display.virtual_targets(hardware)
    screens = virtual_display.monitors(virtual_display.extended(targets))
    main, floating = csp_windows()
    workspace = load_workspace()
    windows = []
    for window in floating:
        monitor = monitor_at(screens, *window.center)
        definition = workspace.match_floating([window.rect, window.bounds], monitor.dpi if monitor else 96)
        windows.append(dict(asdict(window), onVirtualScreen=is_hidden(window, screens),
                            workspaceWindow=definition.index if definition else None,
                            kinds=list(definition.kinds) if definition else [],
                            titleKind=workspace.match_title(window.title)))
    return dict(
        monitors=[asdict(m) for m in screens],
        virtualDisplays=[asdict(t) for t in targets],
        csp=dict(main=asdict(main) if main else None, floating=windows),
        workspace=dict(path=str(workspace.path), error=workspace.error,
                       floatingDefinitions=[asdict(d) for d in workspace.floating],
                       palettes=[asdict(p) for p in workspace.palettes]))


def restore_from_state(settings: dict) -> str:
    hardware = tuple(settings["virtualHardwareIds"])
    screens = virtual_display.screens(hardware)
    _, floating = csp_windows()
    windows = {w.hwnd: w for w in floating}
    manager = StashManager(HERE / "state.json", settings["cornerThresholdPx"], settings["stashGapPx"], print)
    manager.records = {r.hwnd: r for r in manager.load() if is_window(r.hwnd)}
    others = [w for h, w in windows.items() if h not in manager.records and is_hidden(w, screens)]
    count = manager.restore_all(screens, windows)
    for index, window in enumerate(others):
        bring_back(window, screens, index)
    time.sleep(0.5)  # let CSP apply the asynchronous moves before the monitors go away
    detached = virtual_display.release(hardware)
    return f"已取回 {count + len(others)} 个窗口" + ("，虚拟屏已断开" if detached else "")


def query(settings: dict, text: str, out: str | None) -> int:
    request = json.loads(text) if text.lstrip().startswith("{") else dict(command=text)
    try:
        with PipeClient(settings["pipeName"]) as client:
            response = client.request(**request)
    except ConnectionError as ex:
        print(f"{ex}：panel_host.py 没有在运行？")
        return 1
    if out and response.get("data"):
        Path(out).write_bytes(base64.b64decode(response["data"]))
        response["data"] = f"<{len(response['data'])} 个 base64 字符，已写入 {out}>"
    elif isinstance(response.get("data"), str) and len(response["data"]) > 200:
        response["data"] = response["data"][:80] + f"…（共 {len(response['data'])} 个字符，用 --out 保存）"
    print(json.dumps(response, ensure_ascii=False, indent=2))
    return 0 if response.get("success") else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--diagnose", action="store_true", help="只读：显示器、虚拟显示器、CSP 窗口、工作区面板")
    parser.add_argument("--restore-all", action="store_true", help="把虚拟屏上的 CSP 浮动窗口移回物理屏并断开虚拟屏")
    parser.add_argument("--query", metavar="REQUEST", help="向正在运行的程序发一个接口请求：命令名或完整 JSON")
    parser.add_argument("--out", help="与 --query 一起用：把返回的图像数据写入这个文件")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding=None if sys.stdout.isatty() else "utf-8", errors="replace")
    # Before any window or monitor query: every rectangle here is physical pixels.
    enable_per_monitor_dpi_awareness()
    settings = load_settings()
    if args.diagnose:
        print(json.dumps(diagnose(settings), ensure_ascii=False, indent=2))
        return 0
    if args.restore_all:
        print(restore_from_state(settings))
        return 0
    if args.query:
        return query(settings, args.query, args.out)
    try:
        host = Host(settings)
    except AlreadyRunning as ex:
        print(ex)
        return 1
    host.run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
