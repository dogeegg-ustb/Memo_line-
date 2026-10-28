"""CSP palettes torn off as floating windows, and their pixels.

The dock layout (csp_workspace_overlay) only knows docked palettes. Floating ones are listed by the
panel stash (Memoline runs it; it names each window from the saved workspace), and are rendered in
this process with PrintWindow: that works while the window is covered or sits on the virtual screen,
and moves no pixels through the pipe.
"""
from __future__ import annotations

import ctypes
import sys
from ctypes import wintypes
from dataclasses import dataclass

import cv2
import numpy as np

from catalog import BRUSH, COLOR, COLOR_INPUT, LAYERS, NAVIGATOR, TOOLBAR, TOOLGROUP

COLOR_KINDS = frozenset({"palettekindcolorcircle", "palettekindcolorset", "palettekindcolorslider", "palettekindmix",
                         "palettekindcolorhistory", "palettekindcolorguide", "palettekindcolorblend"})
PANEL_KINDS = {
    BRUSH: frozenset({"palettekindtooloption"}),
    TOOLGROUP: frozenset({"palettekindsubtool", "palettekindtoolgroup"}),
    NAVIGATOR: frozenset({"palettekindnavigator"}),
    LAYERS: frozenset({"palettekindlayerorder"}),
    COLOR: COLOR_KINDS,
    TOOLBAR: frozenset({"palettekindtool"}),
}


@dataclass(frozen=True)
class FloatingPanel:
    panel: str
    hwnd: int
    rect: tuple[int, int, int, int]  # visible frame x, y, width, height in physical screen pixels
    kind: str
    on_virtual_screen: bool


def floating_panels(windows: list[dict]) -> dict[str, FloatingPanel]:
    """Panels shown as the current tab of exactly one floating window (`listWindows` of the panel stash)."""
    found: dict[str, list[FloatingPanel]] = {}
    for window in windows:
        kind = window.get("currentKind") or (window["kinds"][0] if len(window.get("kinds") or []) == 1 else None)
        left, top, right, bottom = window.get("rect") or (0, 0, 0, 0)
        if not kind or right - left < 10 or bottom - top < 10:
            continue
        for panel, kinds in PANEL_KINDS.items():
            if kind in kinds:
                found.setdefault(panel, []).append(FloatingPanel(panel, int(window["hwnd"]), (left, top, right - left, bottom - top),
                                                                 kind, bool(window.get("onVirtualScreen"))))
    result = {panel: entries[0] for panel, entries in found.items() if len(entries) == 1}
    if COLOR in result:
        # One booth, two readers (wheel and sliders), as in the dock.
        result[COLOR_INPUT] = FloatingPanel(COLOR_INPUT, *(getattr(result[COLOR], f) for f in ("hwnd", "rect", "kind", "on_virtual_screen")))
    return result


def read(client) -> dict[str, FloatingPanel]:
    """Ask the panel stash; no panel stash (or an error) means no floating panels are known."""
    try:
        return floating_panels(client.request(command="listWindows").get("windows", []))
    except (ConnectionError, RuntimeError, OSError, ValueError):
        return {}


def find_window(client, kind: str) -> FloatingPanel | None:
    """The one floating window holding the palette `kind` (as any of its tabs)."""
    windows = [w for w in client.request(command="listWindows").get("windows", []) if kind in (w.get("kinds") or [])]
    if len(windows) == 1:
        left, top, right, bottom = windows[0]["rect"]
        return FloatingPanel("", int(windows[0]["hwnd"]), (left, top, right - left, bottom - top), kind,
                             bool(windows[0].get("onVirtualScreen")))
    return None


ALIGNMENT_SCORE = 0.8


def align_panel(reference, panel) -> tuple[int, int, float]:
    """Offset (dx, dy) with panel[y + dy, x + dx] showing reference[y, x], and the match score.

    Matched on the bottom control bar (zoom and rotation buttons), which keeps its place under the
    thumbnail whether the panel is docked (tab header above) or floating (window frame and title bar:
    seen live as (7, -13) at 150 %). Both images RGB or both BGR.
    """
    height, width = reference.shape[:2]
    top, bottom, left, right = max(0, height - 75), height - 5, 20, width - 20
    if bottom - top < 20 or right - left < 40 or panel.shape[0] < bottom - top or panel.shape[1] < right - left:
        return 0, 0, 0.0
    template = cv2.cvtColor(np.ascontiguousarray(reference[top:bottom, left:right]), cv2.COLOR_RGB2GRAY)
    target = cv2.cvtColor(np.ascontiguousarray(panel), cv2.COLOR_RGB2GRAY)
    _, score, _, (x, y) = cv2.minMaxLoc(cv2.matchTemplate(target, template, cv2.TM_CCOEFF_NORMED))
    return x - left, y - top, float(score)


def shifted(panel, dx: int, dy: int, width: int, height: int):
    """The `width` x `height` part of `panel` that lines up with a reference image (see align_panel)."""
    result = np.zeros((height, width, panel.shape[2]), panel.dtype)
    y0, y1 = max(0, -dy), min(height, panel.shape[0] - dy)
    x0, x1 = max(0, -dx), min(width, panel.shape[1] - dx)
    if y1 > y0 and x1 > x0:
        result[y0:y1, x0:x1] = panel[y0 + dy:y1 + dy, x0 + dx:x1 + dx]
    return result


def capture(hwnd: int, rect: tuple[int, int, int, int]):
    """The window's own pixels inside `rect` (screen x, y, w, h) as a BGR numpy array, plus the rect read."""
    import numpy as np
    from csp_workspace_overlay import WindowSnapshot
    shot = WindowSnapshot(hwnd)
    pixels = np.frombuffer(bytes(shot.pixels), np.uint8).reshape(shot.height, shot.width, 4)
    x, y, w, h = rect
    left, top = max(x, shot.left), max(y, shot.top)
    right, bottom = min(x + w, shot.left + shot.width), min(y + h, shot.top + shot.height)
    if right - left < 10 or bottom - top < 10:
        raise RuntimeError("浮动面板窗口截图为空")
    crop = pixels[top - shot.top:bottom - shot.top, left - shot.left:right - shot.left, :3]
    return np.ascontiguousarray(crop), (left, top, right - left, bottom - top)


_user32 = _gdi32 = None


def _api():
    global _user32, _gdi32
    if _user32 is None:
        user32, gdi32 = ctypes.WinDLL("user32"), ctypes.WinDLL("gdi32")
        user32.IsIconic.argtypes = [wintypes.HWND]
        user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
        user32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
        user32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
        user32.GetDC.argtypes = user32.GetWindowDC.argtypes = [wintypes.HWND]
        user32.GetDC.restype = user32.GetWindowDC.restype = wintypes.HDC
        user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
        user32.PrintWindow.argtypes = [wintypes.HWND, wintypes.HDC, wintypes.UINT]
        gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
        gdi32.CreateCompatibleDC.restype = wintypes.HDC
        gdi32.CreateDIBSection.argtypes = [wintypes.HDC, ctypes.c_void_p, wintypes.UINT, ctypes.POINTER(ctypes.c_void_p),
                                           wintypes.HANDLE, wintypes.DWORD]
        gdi32.CreateDIBSection.restype = wintypes.HBITMAP
        gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HGDIOBJ]
        gdi32.SelectObject.restype = wintypes.HGDIOBJ
        gdi32.BitBlt.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                 wintypes.HDC, ctypes.c_int, ctypes.c_int, wintypes.DWORD]
        gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
        gdi32.DeleteDC.argtypes = [wintypes.HDC]
        _user32, _gdi32 = user32, gdi32
    return _user32, _gdi32


def window_rect(hwnd: int) -> tuple[int, int, int, int]:
    """The window's screen rect (x, y, w, h); a minimized window has no pixels to read."""
    user32, _ = _api()
    if user32.IsIconic(hwnd):
        raise RuntimeError("CSP 窗口已最小化，无法读取它的画面")
    rect = wintypes.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        raise OSError("读取 CSP 窗口位置失败")
    if rect.right <= rect.left or rect.bottom <= rect.top:
        raise RuntimeError("CSP 窗口没有可截取的区域")
    return rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top


def _render(hwnd: int, x: int, y: int, width: int, height: int, print_window: bool) -> bytes:
    """BGRX rows of the window part at (x, y) (window coordinates). PrintWindow always draws from the
    window's top-left corner, so it is only asked for the whole window."""
    user32, gdi32 = _api()
    header = (ctypes.c_uint32 * 10)(40, width, ctypes.c_uint32(-height).value, 1 | (32 << 16), 0, 0, 0, 0, 0, 0)
    screen = user32.GetDC(None)
    memory = gdi32.CreateCompatibleDC(screen)
    user32.ReleaseDC(None, screen)
    bits = ctypes.c_void_p()
    bitmap = gdi32.CreateDIBSection(memory, header, 0, ctypes.byref(bits), None, 0)
    if not bitmap:
        gdi32.DeleteDC(memory)
        raise OSError("创建 CSP 窗口截图缓冲失败")
    previous = gdi32.SelectObject(memory, bitmap)
    try:
        if print_window:
            if not user32.PrintWindow(hwnd, memory, 2):  # PW_RENDERFULLCONTENT
                raise OSError("PrintWindow 无法读取 CSP 窗口")
        else:
            source = user32.GetWindowDC(hwnd)
            try:
                if not gdi32.BitBlt(memory, 0, 0, width, height, source, x, y, 0x00CC0020):  # SRCCOPY
                    raise OSError("无法复制 CSP 窗口的画面")
            finally:
                user32.ReleaseDC(hwnd, source)
        gdi32.GdiFlush()
        return ctypes.string_at(bits, width * height * 4)
    finally:
        gdi32.SelectObject(memory, previous)
        gdi32.DeleteObject(bitmap)
        gdi32.DeleteDC(memory)


# hwnd -> whether BitBlt from the window DC shows what PrintWindow does (checked on the first read).
_window_dc_ok: dict[int, bool] = {}


def _window_dc_matches(hwnd: int, rect: tuple[int, int, int, int]) -> bool:
    """Both ways render the client area alike. Checked up to three times: CSP may repaint in between."""
    user32, _ = _api()
    client, corner = wintypes.RECT(), wintypes.POINT()
    if not user32.GetClientRect(hwnd, ctypes.byref(client)) or not user32.ClientToScreen(hwnd, ctypes.byref(corner)):
        return False
    left, top = corner.x - rect[0], corner.y - rect[1]
    box = (slice(max(0, top), min(rect[3], top + client.bottom)), slice(max(0, left), min(rect[2], left + client.right)))
    for _ in range(3):
        fast = np.frombuffer(_render(hwnd, 0, 0, rect[2], rect[3], False), np.uint8).reshape(rect[3], rect[2], 4)
        slow = np.frombuffer(_render(hwnd, 0, 0, rect[2], rect[3], True), np.uint8).reshape(rect[3], rect[2], 4)
        a, b = fast[box][:, :, :3].astype(np.int16), slow[box][:, :, :3].astype(np.int16)
        if a.size and (np.abs(a - b).max(axis=2) > 8).mean() <= 0.001:
            return True
    return False


def window_image(hwnd: int, region: tuple[int, int, int, int] | None = None):
    """CSP's own rendering of the top-level window `hwnd` (or of its screen `region` x, y, w, h) as a PIL RGB
    image, and the screen rect it covers.

    These are the pixels CSP drew, whatever lies in front of the window (Memoline's panels, the watcher, a
    terminal) and whether or not it is the foreground window. They are copied from the window's own DWM surface
    (BitBlt from its window DC, measured live: the whole 2582x1550 window about 20 ms, 6 of them the copy and the
    rest the conversion to RGB; one panel about 1.5 ms). A window drawn some other way may be missing from that
    surface, so the first read of each window compares it with PrintWindow (PW_RENDERFULLCONTENT, about 50 ms,
    always the whole window) and keeps using PrintWindow when they differ.
    """
    from PIL import Image
    rect = window_rect(hwnd)
    x, y, width, height = region or rect
    if width <= 0 or height <= 0 or x < rect[0] or y < rect[1] or x + width > rect[0] + rect[2] or y + height > rect[1] + rect[3]:
        raise RuntimeError("所需区域超出 CSP 窗口")
    if hwnd not in _window_dc_ok:
        _window_dc_ok[hwnd] = _window_dc_matches(hwnd, rect)
        if not _window_dc_ok[hwnd]:
            print(f"[Capture] CSP 窗口 {hwnd:#x} 的窗口画面与 PrintWindow 不一致，改用 PrintWindow（每次约 50 ms）",
                  file=sys.stderr, flush=True)
    if _window_dc_ok[hwnd]:
        data = _render(hwnd, x - rect[0], y - rect[1], width, height, False)
        return Image.frombuffer("RGB", (width, height), data, "raw", "BGRX", 0, 1), (x, y, width, height)
    whole = Image.frombuffer("RGB", rect[2:], _render(hwnd, 0, 0, rect[2], rect[3], True), "raw", "BGRX", 0, 1)
    if (x, y, width, height) == rect:
        return whole, rect
    with whole:
        return whole.crop((x - rect[0], y - rect[1], x - rect[0] + width, y - rect[1] + height)), (x, y, width, height)


class MonitorInfo(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT), ("rcWork", wintypes.RECT),
                ("dwFlags", wintypes.DWORD)]


def monitor_rect(hwnd: int) -> tuple[int, int, int, int]:
    """The monitor (x, y, w, h) holding most of the window: the canvas frame never spans the virtual screen."""
    user32 = ctypes.windll.user32
    user32.MonitorFromWindow.argtypes = [wintypes.HWND, wintypes.DWORD]
    user32.MonitorFromWindow.restype = wintypes.HMONITOR
    user32.GetMonitorInfoW.argtypes = [wintypes.HMONITOR, ctypes.POINTER(MonitorInfo)]
    info = MonitorInfo(cbSize=ctypes.sizeof(MonitorInfo))
    monitor = user32.MonitorFromWindow(hwnd, 2)  # MONITOR_DEFAULTTONEAREST
    if not monitor or not user32.GetMonitorInfoW(monitor, ctypes.byref(info)):
        raise OSError("读取 CSP 所在显示器失败")
    r = info.rcMonitor
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def same_process(hwnd: int, other: int) -> bool:
    if not hwnd or not other:
        return False
    user32 = ctypes.windll.user32
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    a, b = wintypes.DWORD(), wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(a))
    user32.GetWindowThreadProcessId(other, ctypes.byref(b))
    return a.value != 0 and a.value == b.value
