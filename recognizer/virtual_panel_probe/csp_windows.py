"""Top-level windows of the running CLIP STUDIO PAINT process."""
from __future__ import annotations

import ctypes
from ctypes import wintypes
from dataclasses import dataclass

import paths  # noqa: F401
from csp_panel_validator.window_service import _read_class_name, _read_process_name, _read_window_text

USER32 = ctypes.WinDLL("user32", use_last_error=True)
DWMAPI = ctypes.WinDLL("dwmapi")

CSP_EXES = {"clipstudiopaint.exe", "clipstudiopaintver1.exe", "clipstudiopaintver2.exe"}
# System popups that share the CSP process but are never palettes.
IGNORED_CLASSES = {"#32768", "tooltips_class32", "SysShadow", "IME", "MSCTFIME UI", "Default IME"}
GW_OWNER, GA_ROOT = 4, 2
SWP_NOSIZE, SWP_NOZORDER, SWP_NOACTIVATE, SWP_ASYNCWINDOWPOS = 0x1, 0x4, 0x10, 0x4000

for name in ("IsWindow", "IsWindowVisible", "IsIconic", "IsWindowEnabled"):
    getattr(USER32, name).argtypes = [wintypes.HWND]
USER32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
USER32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
USER32.GetWindow.restype = wintypes.HWND
USER32.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
USER32.GetAncestor.restype = wintypes.HWND
USER32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
USER32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
USER32.GetForegroundWindow.restype = wintypes.HWND
USER32.WindowFromPoint.argtypes = [wintypes.POINT]
USER32.WindowFromPoint.restype = wintypes.HWND
USER32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                                ctypes.c_int, ctypes.c_int, wintypes.UINT]
DWMAPI.DwmGetWindowAttribute.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]


@dataclass(frozen=True)
class CspWindow:
    hwnd: int
    pid: int
    title: str
    class_name: str
    rect: tuple[int, int, int, int]    # GetWindowRect: the coordinates SetWindowPos uses
    bounds: tuple[int, int, int, int]  # DWM visible frame; excludes invisible resize borders
    owner: int
    enabled: bool

    @property
    def size(self) -> tuple[int, int]:
        return self.bounds[2] - self.bounds[0], self.bounds[3] - self.bounds[1]

    @property
    def center(self) -> tuple[float, float]:
        return (self.bounds[0] + self.bounds[2]) / 2, (self.bounds[1] + self.bounds[3]) / 2


def window_pid(hwnd: int) -> int:
    pid = wintypes.DWORD()
    USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def _rect(hwnd: int) -> tuple[int, int, int, int] | None:
    rect = wintypes.RECT()
    if not USER32.GetWindowRect(hwnd, ctypes.byref(rect)):
        return None
    return rect.left, rect.top, rect.right, rect.bottom


def _bounds(hwnd: int, fallback):
    rect = wintypes.RECT()
    # DWMWA_EXTENDED_FRAME_BOUNDS
    if DWMAPI.DwmGetWindowAttribute(hwnd, 9, ctypes.byref(rect), ctypes.sizeof(rect)) == 0:
        return rect.left, rect.top, rect.right, rect.bottom
    return fallback


def describe(hwnd: int, pid: int | None = None) -> CspWindow | None:
    rect = _rect(hwnd)
    if rect is None:
        return None
    return CspWindow(int(hwnd), pid or window_pid(hwnd), _read_window_text(hwnd), _read_class_name(hwnd),
                     rect, _bounds(hwnd, rect), int(USER32.GetWindow(hwnd, GW_OWNER) or 0),
                     bool(USER32.IsWindowEnabled(hwnd)))


def csp_windows() -> tuple[CspWindow | None, list[CspWindow]]:
    """Return the main frame and the other visible top-level windows (floating palettes)."""
    found: list[CspWindow] = []
    exe_cache: dict[int, bool] = {}
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    @callback_type
    def callback(hwnd, _data):
        if not USER32.IsWindowVisible(hwnd) or USER32.IsIconic(hwnd):
            return 1
        pid = window_pid(hwnd)
        if pid not in exe_cache:
            exe_cache[pid] = _read_process_name(pid).lower() in CSP_EXES
        if exe_cache[pid] and _read_class_name(hwnd) not in IGNORED_CLASSES:
            info = describe(hwnd, pid)
            if info and min(info.size) >= 24:
                found.append(info)
        return 1

    USER32.EnumWindows(callback, 0)
    unowned = [w for w in found if not w.owner]
    main = max(unowned or found, key=lambda w: w.size[0] * w.size[1], default=None)
    if main is None:
        return None, []
    # Another CSP instance is not part of this workspace.
    return main, [w for w in found if w.hwnd != main.hwnd and w.pid == main.pid]


def root_at(x: int, y: int) -> int:
    hwnd = USER32.WindowFromPoint(wintypes.POINT(int(x), int(y)))
    return int(USER32.GetAncestor(hwnd, GA_ROOT) or 0) if hwnd else 0


def foreground() -> int:
    return int(USER32.GetForegroundWindow() or 0)


def is_window(hwnd: int) -> bool:
    return bool(USER32.IsWindow(hwnd))


def move_window(window: CspWindow, left: int, top: int) -> None:
    """Move so the visible frame starts at (left, top); asynchronous, never activates CSP."""
    x = left + window.rect[0] - window.bounds[0]
    y = top + window.rect[1] - window.bounds[1]
    if not USER32.SetWindowPos(window.hwnd, None, x, y, 0, 0,
                               SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS):
        raise ctypes.WinError(ctypes.get_last_error())
