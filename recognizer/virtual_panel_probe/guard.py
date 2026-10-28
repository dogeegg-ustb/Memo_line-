"""Keep the virtual screen for the stashed palettes only.

Windows has no per-monitor allow list, so it is approximated:

- A black backdrop flagged as maximized lies under every other window on the virtual screen.
  Wallpaper programs that pause while a maximized window covers a monitor (Wallpaper Engine's
  "other application maximized: pause") stop rendering there; a borderless window covering the
  screen without the maximized flag did not make Wallpaper Engine pause.
- Top-level windows of processes not on the allow list that end up on the virtual screen are
  moved back to the main screen. The shell's own windows (taskbar, desktop) stay; they are static.
  So do topmost windows covering the whole virtual screen: per-monitor overlays such as a screen
  capture's frozen picture of that monitor.
"""
from __future__ import annotations

import ctypes
import os
import tkinter as tk
from ctypes import wintypes
from dataclasses import dataclass

import paths  # noqa: F401
from csp_panel_validator.window_service import _read_class_name, _read_process_name

USER32 = ctypes.WinDLL("user32", use_last_error=True)
DWMAPI = ctypes.WinDLL("dwmapi")
for name in ("IsWindowVisible", "IsIconic"):
    getattr(USER32, name).argtypes = [wintypes.HWND]
USER32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
USER32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
USER32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
USER32.SetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_long]
USER32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                wintypes.UINT]
DWMAPI.DwmGetWindowAttribute.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]

GWL_STYLE, GWL_EXSTYLE = -16, -20
WS_MAXIMIZE, WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE, WS_EX_TRANSPARENT = 0x01000000, 0x80, 0x08000000, 0x20
WS_EX_TOPMOST = 0x8
HWND_BOTTOM = 1
SWP_NOSIZE, SWP_NOZORDER, SWP_NOACTIVATE, SWP_SHOWWINDOW, SWP_FRAMECHANGED, SWP_ASYNCWINDOWPOS = (
    0x1, 0x4, 0x10, 0x40, 0x20, 0x4000)
DWMWA_CLOAKED = 14
SHELL_CLASSES = {"Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW"}
# Windows' pen handwriting surface and input-method windows span or follow the whole desktop and
# carry pen input; moving them was seen live (TabTip.exe ShellHandwritingCanvas, 2026-10-18).
SYSTEM_INPUT_PROCESSES = {"tabtip.exe", "textinputhost.exe", "ctfmon.exe"}
MAX_EVICTIONS = 3  # a window that keeps coming back is left alone


@dataclass(frozen=True)
class TopWindow:
    hwnd: int
    pid: int
    exe: str
    class_name: str
    rect: tuple[int, int, int, int]
    topmost: bool = False


def top_level_windows() -> list[TopWindow]:
    """Visible, not minimized, not cloaked top-level windows."""
    found, names = [], {}
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    @callback_type
    def callback(hwnd, _data):
        if not USER32.IsWindowVisible(hwnd) or USER32.IsIconic(hwnd):
            return 1
        # Click-through overlays (the recorder's borders) take no input and cost nothing there.
        ex_style = USER32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        if ex_style & WS_EX_TRANSPARENT:
            return 1
        cloaked = ctypes.c_int(0)
        DWMAPI.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, ctypes.byref(cloaked), ctypes.sizeof(cloaked))
        rect = wintypes.RECT()
        if cloaked.value or not USER32.GetWindowRect(hwnd, ctypes.byref(rect)):
            return 1
        pid = wintypes.DWORD()
        USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value not in names:
            names[pid.value] = _read_process_name(pid.value)
        found.append(TopWindow(int(hwnd), pid.value, names[pid.value], _read_class_name(hwnd),
                               (rect.left, rect.top, rect.right, rect.bottom), bool(ex_style & WS_EX_TOPMOST)))
        return 1

    USER32.EnumWindows(callback, 0)
    return found


def intruders(windows, area, allowed, own_pid: int) -> list[TopWindow]:
    """Windows centred on `area` that belong to neither an allowed process nor the shell."""
    allowed = {name.lower() for name in allowed}
    result = []
    for window in windows:
        left, top, right, bottom = window.rect
        x, y = (left + right) / 2, (top + bottom) / 2
        if not (area[0] <= x < area[2] and area[1] <= y < area[3]) or right - left <= 0 or bottom - top <= 0:
            continue
        if (window.pid == own_pid or window.exe.lower() in allowed or window.class_name in SHELL_CLASSES
                or window.exe.lower() in SYSTEM_INPUT_PROCESSES or monitor_overlay(window, area)):
            continue
        result.append(window)
    return result


def monitor_overlay(window: TopWindow, area) -> bool:
    """A topmost window over the whole virtual screen: some program's overlay on every monitor.

    Win+Shift+S shows its frozen picture of each monitor in such a window (SnippingTool.exe
    XamlWindow, exactly the monitor's rect); moved to the main screen it covered most of it with
    the virtual screen's black picture and the snip no longer worked (seen live 2026-10-25).
    """
    left, top, right, bottom = window.rect
    return window.topmost and left <= area[0] and top <= area[1] and right >= area[2] and bottom >= area[3]


def evicted_position(rect, area, work) -> tuple[int, int]:
    """Same offset from the main screen's work area as it had on the virtual screen, kept inside."""
    width, height = rect[2] - rect[0], rect[3] - rect[1]
    x = work[0] + (rect[0] - area[0])
    y = work[1] + (rect[1] - area[1])
    return (min(max(x, work[0]), max(work[0], work[2] - width)),
            min(max(y, work[1]), max(work[1], work[3] - height)))


class Guard:
    def __init__(self, root: tk.Tk, allowed, log):
        self.root, self.allowed, self.log = root, tuple(allowed), log
        self.own_pid = os.getpid()
        self.backdrop: tk.Toplevel | None = None
        self.backdrop_rect = None
        self.evictions: dict[int, int] = {}

    def show(self, rect: tuple[int, int, int, int]) -> None:
        """Cover `rect` (the virtual screen) with the maximized backdrop, below every other window."""
        if self.backdrop is None:
            self.backdrop = tk.Toplevel(self.root, bg="black")
            self.backdrop.overrideredirect(True)
            self.backdrop.geometry(f"1x1+{rect[0]}+{rect[1]}")
            self.backdrop.update_idletasks()
            hwnd = self.hwnd
            USER32.SetWindowLongW(hwnd, GWL_EXSTYLE,
                                  USER32.GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
            USER32.SetWindowLongW(hwnd, GWL_STYLE, USER32.GetWindowLongW(hwnd, GWL_STYLE) | WS_MAXIMIZE)
        USER32.SetWindowPos(self.hwnd, HWND_BOTTOM, rect[0], rect[1], rect[2] - rect[0], rect[3] - rect[1],
                            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED)
        self.backdrop_rect = rect

    @property
    def hwnd(self) -> int:
        return int(self.backdrop.wm_frame(), 16)

    def hide(self) -> None:
        if self.backdrop is not None:
            self.backdrop.destroy()
        self.backdrop, self.backdrop_rect = None, None

    def patrol(self, area, work) -> None:
        """Move windows of other programs off the virtual screen `area` into the main `work` area."""
        for window in intruders(top_level_windows(), area, self.allowed, self.own_pid):
            count = self.evictions.get(window.hwnd, 0)
            if count >= MAX_EVICTIONS:
                continue
            self.evictions[window.hwnd] = count + 1
            x, y = evicted_position(window.rect, area, work)
            USER32.SetWindowPos(window.hwnd, None, x, y, 0, 0,
                                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS)
            note = "，之后不再移动它" if count + 1 >= MAX_EVICTIONS else ""
            self.log(f"{window.exe}（{window.class_name}）的窗口出现在虚拟屏上，已移回主屏{note}")
