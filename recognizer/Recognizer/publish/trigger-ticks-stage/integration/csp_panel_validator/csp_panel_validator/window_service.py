from __future__ import annotations

import ctypes
import os
import re
from ctypes import wintypes
from dataclasses import dataclass
from typing import Any

from .models import Rect, TargetWindowProfile


IS_WINDOWS = os.name == "nt"
USER32 = ctypes.windll.user32 if IS_WINDOWS else None
KERNEL32 = ctypes.windll.kernel32 if IS_WINDOWS else None

# ctypes defaults to 32-bit integers; HWND/HANDLE are pointer-sized on Win64.
if IS_WINDOWS:
    USER32.GetForegroundWindow.restype = wintypes.HWND
    for name in ("IsWindow", "IsWindowVisible", "IsIconic", "GetWindowTextLengthW"):
        getattr(USER32, name).argtypes = [wintypes.HWND]
    USER32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    USER32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    USER32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
    USER32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    USER32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    USER32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    USER32.GetDpiForWindow.argtypes = [wintypes.HWND]
    KERNEL32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    KERNEL32.OpenProcess.restype = wintypes.HANDLE
    KERNEL32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    KERNEL32.CloseHandle.argtypes = [wintypes.HANDLE]


def current_csp_window(binding: TargetWindowProfile | None = None) -> WindowInfo | None:
    """Resolve automatically; never ask the artist to select process/thread handles."""
    windows = [w for w in enumerate_csp_windows()
               if w.visible and not w.minimized and min(w.capture_size) > 32]
    foreground = USER32.GetForegroundWindow() if IS_WINDOWS else None
    active = next((w for w in windows if w.hwnd == foreground), None)
    if active is not None:
        return active
    if binding is not None:
        matched = match_windows(binding, windows)
        if len(matched) == 1:
            return matched[0]
    return max(windows, key=lambda w: w.client_rect.width * w.client_rect.height, default=None)


def enable_per_monitor_dpi_awareness() -> None:
    if not IS_WINDOWS:
        return
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
    except Exception:
        try:
            USER32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # PMv2
        except Exception:
            pass


def normalize_title(title: str) -> str:
    return re.sub(r"\s+", " ", title).strip().lower()


@dataclass
class WindowInfo:
    hwnd: int
    title: str
    class_name: str
    exe_name: str
    pid: int
    frame_rect: Rect
    client_rect: Rect
    dpi: float
    output_index: int = 0
    output_origin: tuple[int, int] = (0, 0)
    minimized: bool = False
    visible: bool = True

    @property
    def client_size(self) -> tuple[int, int]:
        return self.client_rect.width, self.client_rect.height

    @property
    def capture_size(self) -> tuple[int, int]:
        return self.client_size

    @property
    def title_normalized(self) -> str:
        return normalize_title(self.title)

    def display_name(self) -> str:
        state = "最小化" if self.minimized else "可见"
        return f"{self.title or '(无标题)'} | {self.exe_name} | {self.class_name} | {self.client_rect.width}x{self.client_rect.height} | DPI {self.dpi:g} | {state}"

    def binding(self) -> TargetWindowProfile:
        return TargetWindowProfile(
            exe_name=self.exe_name,
            class_name=self.class_name,
            title=self.title,
            title_normalized=self.title_normalized,
            capture_scope="client",
            capture_size=self.capture_size,
            client_size=self.client_size,
            dpi=self.dpi,
            device_pixel_ratio=self.dpi / 96.0,
            initial_client_rect=self.client_rect,
        )

    def as_dict(self) -> dict[str, Any]:
        return {
            "hwnd": self.hwnd,
            "title": self.title,
            "title_normalized": self.title_normalized,
            "class_name": self.class_name,
            "exe_name": self.exe_name,
            "pid": self.pid,
            "frame_rect": self.frame_rect.to_list(),
            "client_rect": self.client_rect.to_list(),
            "capture_size": list(self.capture_size),
            "dpi": self.dpi,
            "device_pixel_ratio": self.dpi / 96.0,
            "output_index": self.output_index,
            "output_origin": list(self.output_origin),
            "minimized": self.minimized,
            "visible": self.visible,
        }


if IS_WINDOWS:
    class _MonitorInfo(ctypes.Structure):
        _fields_ = [
            ("cbSize", wintypes.DWORD),
            ("rcMonitor", wintypes.RECT),
            ("rcWork", wintypes.RECT),
            ("dwFlags", wintypes.DWORD),
            ("szDevice", wintypes.WCHAR * 32),
        ]


def _display_monitors() -> list[tuple[int, Rect]]:
    if not IS_WINDOWS:
        return []
    result: list[tuple[int, Rect]] = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HMONITOR, wintypes.HDC, ctypes.POINTER(wintypes.RECT), wintypes.LPARAM)

    @callback_type
    def callback(_monitor: Any, _dc: Any, rect_ptr: Any, _data: Any) -> int:
        rect = rect_ptr.contents
        result.append((len(result), Rect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top)))
        return 1

    USER32.EnumDisplayMonitors(0, 0, callback, 0)
    return result


def _monitor_for_rect(rect: Rect) -> tuple[int, tuple[int, int]]:
    center_x = rect.x + rect.width // 2
    center_y = rect.y + rect.height // 2
    monitors = _display_monitors()
    for index, monitor in monitors:
        if monitor.x <= center_x < monitor.x + monitor.width and monitor.y <= center_y < monitor.y + monitor.height:
            return index, (monitor.x, monitor.y)
    return 0, (0, 0)


def _read_window_text(hwnd: int) -> str:
    length = USER32.GetWindowTextLengthW(hwnd)
    buffer = ctypes.create_unicode_buffer(max(1, length + 1))
    USER32.GetWindowTextW(hwnd, buffer, len(buffer))
    return buffer.value


def _read_class_name(hwnd: int) -> str:
    buffer = ctypes.create_unicode_buffer(256)
    USER32.GetClassNameW(hwnd, buffer, len(buffer))
    return buffer.value


def _read_process_name(pid: int) -> str:
    if not IS_WINDOWS:
        return ""
    handle = KERNEL32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return ""
    try:
        size = wintypes.DWORD(32768)
        buffer = ctypes.create_unicode_buffer(size.value)
        if KERNEL32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            return os.path.basename(buffer.value)
    finally:
        KERNEL32.CloseHandle(handle)
    return ""


def _window_info(hwnd: int) -> WindowInfo | None:
    if not IS_WINDOWS or not USER32.IsWindow(hwnd):
        return None
    frame = wintypes.RECT()
    client = wintypes.RECT()
    if not USER32.GetWindowRect(hwnd, ctypes.byref(frame)) or not USER32.GetClientRect(hwnd, ctypes.byref(client)):
        return None
    point = wintypes.POINT(0, 0)
    if not USER32.ClientToScreen(hwnd, ctypes.byref(point)):
        return None
    client_rect = Rect(point.x, point.y, client.right - client.left, client.bottom - client.top)
    pid = wintypes.DWORD(0)
    USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    dpi = float(USER32.GetDpiForWindow(hwnd)) if hasattr(USER32, "GetDpiForWindow") else 96.0
    output_index, output_origin = _monitor_for_rect(client_rect)
    return WindowInfo(
        hwnd=int(hwnd),
        title=_read_window_text(hwnd),
        class_name=_read_class_name(hwnd),
        exe_name=_read_process_name(pid.value),
        pid=int(pid.value),
        frame_rect=Rect(frame.left, frame.top, frame.right - frame.left, frame.bottom - frame.top),
        client_rect=client_rect,
        dpi=dpi or 96.0,
        output_index=output_index,
        output_origin=output_origin,
        minimized=bool(USER32.IsIconic(hwnd)),
        visible=bool(USER32.IsWindowVisible(hwnd)),
    )


def inspect_window(hwnd: int) -> WindowInfo | None:
    return _window_info(hwnd)


def enumerate_csp_windows() -> list[WindowInfo]:
    if not IS_WINDOWS:
        return []
    candidates: list[WindowInfo] = []
    known_exes = {"clipstudiopaint.exe", "clipstudiopaintver1.exe", "clipstudiopaintver2.exe"}
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    @callback_type
    def callback(hwnd: int, _data: int) -> int:
        info = _window_info(hwnd)
        if info is None or not info.visible:
            return 1
        exe = info.exe_name.lower()
        title = info.title.lower()
        if exe in known_exes or "clip studio paint" in title or "clipstudiopaint" in exe:
            candidates.append(info)
        return 1

    USER32.EnumWindows(callback, 0)
    return sorted(candidates, key=lambda item: (item.minimized, item.title_normalized, item.hwnd))


def match_windows(binding: TargetWindowProfile, windows: list[WindowInfo] | None = None) -> list[WindowInfo]:
    windows = windows if windows is not None else enumerate_csp_windows()
    target_exe = binding.exe_name.lower()
    target_class = binding.class_name
    target_title = binding.title_normalized
    exact: list[tuple[int, WindowInfo]] = []
    for window in windows:
        score = 0
        if target_exe and window.exe_name.lower() == target_exe:
            score += 4
        else:
            continue
        if target_class and window.class_name == target_class:
            score += 3
        if target_title and window.title_normalized == target_title:
            score += 3
        elif target_title and (target_title in window.title_normalized or window.title_normalized in target_title):
            score += 1
        if window.capture_size == binding.capture_size:
            score += 2
        if abs(window.dpi - binding.dpi) <= 1.0:
            score += 1
        exact.append((score, window))
    exact.sort(key=lambda pair: (-pair[0], pair[1].hwnd))
    if not exact:
        return []
    best = exact[0][0]
    return [window for score, window in exact if score == best]
