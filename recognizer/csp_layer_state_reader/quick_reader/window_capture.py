from __future__ import annotations

import ctypes
import os
from ctypes import wintypes
from dataclasses import dataclass
from typing import Any


IS_WINDOWS = os.name == "nt"


@dataclass(frozen=True)
class CspWindow:
    hwnd: int
    title: str
    exe_name: str
    pid: int
    width: int
    height: int
    minimized: bool = False
    dpi: float = 96.0

    def display_name(self) -> str:
        state = "（最小化）" if self.minimized else ""
        return f"{self.title or 'CLIP STUDIO PAINT'} | {self.width}×{self.height}{state}"


if IS_WINDOWS:
    USER32 = ctypes.windll.user32
    KERNEL32 = ctypes.windll.kernel32
    USER32.IsWindow.argtypes = [wintypes.HWND]
    USER32.IsWindow.restype = wintypes.BOOL
    USER32.IsWindowVisible.argtypes = [wintypes.HWND]
    USER32.IsWindowVisible.restype = wintypes.BOOL
    USER32.IsIconic.argtypes = [wintypes.HWND]
    USER32.IsIconic.restype = wintypes.BOOL
    USER32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    USER32.GetClientRect.restype = wintypes.BOOL
    USER32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
    USER32.GetWindowTextLengthW.restype = ctypes.c_int
    USER32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    USER32.GetWindowTextW.restype = ctypes.c_int
    USER32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    USER32.GetWindowThreadProcessId.restype = wintypes.DWORD
    if hasattr(USER32, "GetDpiForWindow"):
        USER32.GetDpiForWindow.argtypes = [wintypes.HWND]
        USER32.GetDpiForWindow.restype = wintypes.UINT
    KERNEL32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    KERNEL32.OpenProcess.restype = wintypes.HANDLE
    KERNEL32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    KERNEL32.QueryFullProcessImageNameW.restype = wintypes.BOOL
    KERNEL32.CloseHandle.argtypes = [wintypes.HANDLE]
    KERNEL32.CloseHandle.restype = wintypes.BOOL


def _window_title(hwnd: int) -> str:
    length = USER32.GetWindowTextLengthW(hwnd)
    buffer = ctypes.create_unicode_buffer(max(1, length + 1))
    USER32.GetWindowTextW(hwnd, buffer, len(buffer))
    return buffer.value


def _process_name(pid: int) -> str:
    handle = KERNEL32.OpenProcess(0x1000, False, pid)
    if not handle:
        return ""
    try:
        capacity = wintypes.DWORD(32768)
        buffer = ctypes.create_unicode_buffer(capacity.value)
        if KERNEL32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(capacity)):
            return os.path.basename(buffer.value)
        return ""
    finally:
        KERNEL32.CloseHandle(handle)


def enumerate_csp_windows() -> list[CspWindow]:
    if not IS_WINDOWS:
        return []
    windows: list[CspWindow] = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    @callback_type
    def callback(hwnd: int, _data: int) -> int:
        if not USER32.IsWindowVisible(hwnd):
            return 1
        pid = wintypes.DWORD(0)
        USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        exe_name = _process_name(pid.value)
        title = _window_title(hwnd)
        if not (exe_name.lower().startswith("clipstudiopaint") or "clip studio paint" in title.lower()):
            return 1
        client = wintypes.RECT()
        if not USER32.GetClientRect(hwnd, ctypes.byref(client)):
            return 1
        width, height = client.right - client.left, client.bottom - client.top
        if width > 100 and height > 100:
            dpi = float(USER32.GetDpiForWindow(hwnd)) if hasattr(USER32, "GetDpiForWindow") else 96.0
            windows.append(CspWindow(int(hwnd), title, exe_name, int(pid.value), width, height,
                                     bool(USER32.IsIconic(hwnd)), dpi))
        return 1

    USER32.EnumWindows(callback_type(callback), 0)
    return sorted(windows, key=lambda item: (item.minimized, item.title.casefold(), item.hwnd))


if IS_WINDOWS:
    class _BitmapInfoHeader(ctypes.Structure):
        _fields_ = [
            ("biSize", wintypes.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
            ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
            ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", ctypes.c_long), ("biYPelsPerMeter", ctypes.c_long),
            ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD),
        ]

    class _BitmapInfo(ctypes.Structure):
        _fields_ = [("bmiHeader", _BitmapInfoHeader), ("bmiColors", wintypes.DWORD * 1)]


def capture_client(window: CspWindow) -> Any:
    """Capture the selected CSP client area with PrintWindow, without screen overlays."""
    if not IS_WINDOWS:
        raise RuntimeError("CSP 窗口捕获仅支持 Windows")
    if not USER32.IsWindow(window.hwnd) or not USER32.IsWindowVisible(window.hwnd):
        raise RuntimeError("CSP 窗口已关闭或不可见")
    pid = wintypes.DWORD(0)
    USER32.GetWindowThreadProcessId(window.hwnd, ctypes.byref(pid))
    if int(pid.value) != window.pid or _process_name(pid.value).casefold() != window.exe_name.casefold():
        raise RuntimeError("原 CSP 窗口标识已变化，请重新选择窗口")
    if USER32.IsIconic(window.hwnd):
        raise RuntimeError("CSP 窗口已最小化")
    client = wintypes.RECT()
    if not USER32.GetClientRect(window.hwnd, ctypes.byref(client)):
        raise RuntimeError("无法读取 CSP 窗口客户区")
    width, height = client.right - client.left, client.bottom - client.top
    if width <= 0 or height <= 0:
        raise RuntimeError("CSP 窗口客户区尺寸无效")
    if (width, height) != (window.width, window.height):
        raise RuntimeError("CSP 窗口尺寸已变化，请重新选择图层面板区域")

    user32 = USER32
    gdi32 = ctypes.windll.gdi32
    handle = wintypes.HANDLE
    user32.GetDC.argtypes = [wintypes.HWND]
    user32.GetDC.restype = handle
    user32.ReleaseDC.argtypes = [wintypes.HWND, handle]
    user32.ReleaseDC.restype = ctypes.c_int
    user32.PrintWindow.argtypes = [wintypes.HWND, handle, wintypes.UINT]
    user32.PrintWindow.restype = wintypes.BOOL
    gdi32.CreateCompatibleDC.argtypes = [handle]
    gdi32.CreateCompatibleDC.restype = handle
    gdi32.CreateCompatibleBitmap.argtypes = [handle, ctypes.c_int, ctypes.c_int]
    gdi32.CreateCompatibleBitmap.restype = handle
    gdi32.SelectObject.argtypes = [handle, handle]
    gdi32.SelectObject.restype = handle
    gdi32.GetDIBits.argtypes = [handle, handle, wintypes.UINT, wintypes.UINT, ctypes.c_void_p, ctypes.POINTER(_BitmapInfo), wintypes.UINT]
    gdi32.GetDIBits.restype = ctypes.c_int
    gdi32.DeleteObject.argtypes = [handle]
    gdi32.DeleteObject.restype = wintypes.BOOL
    gdi32.DeleteDC.argtypes = [handle]
    gdi32.DeleteDC.restype = wintypes.BOOL

    screen_dc = user32.GetDC(0)
    memory_dc = gdi32.CreateCompatibleDC(screen_dc)
    bitmap = gdi32.CreateCompatibleBitmap(screen_dc, width, height)
    if not screen_dc or not memory_dc or not bitmap:
        if bitmap:
            gdi32.DeleteObject(bitmap)
        if memory_dc:
            gdi32.DeleteDC(memory_dc)
        if screen_dc:
            user32.ReleaseDC(0, screen_dc)
        raise RuntimeError("无法创建 CSP 窗口截图缓冲区")
    old_bitmap = gdi32.SelectObject(memory_dc, bitmap)
    try:
        if not user32.PrintWindow(window.hwnd, memory_dc, 0x00000001 | 0x00000002):
            raise RuntimeError("PrintWindow 无法捕获 CSP 窗口")
        info = _BitmapInfo()
        info.bmiHeader.biSize = ctypes.sizeof(_BitmapInfoHeader)
        info.bmiHeader.biWidth = width
        info.bmiHeader.biHeight = -height
        info.bmiHeader.biPlanes = 1
        info.bmiHeader.biBitCount = 32
        info.bmiHeader.biCompression = 0
        buffer = ctypes.create_string_buffer(width * height * 4)
        copied = gdi32.GetDIBits(memory_dc, bitmap, 0, height, buffer, ctypes.byref(info), 0)
        if copied != height:
            raise RuntimeError("无法读取 CSP 窗口截图像素")
        import cv2
        import numpy as np

        bgra = np.frombuffer(buffer, dtype=np.uint8).reshape((height, width, 4))
        return cv2.cvtColor(bgra, cv2.COLOR_BGRA2BGR)
    finally:
        gdi32.SelectObject(memory_dc, old_bitmap)
        gdi32.DeleteObject(bitmap)
        gdi32.DeleteDC(memory_dc)
        user32.ReleaseDC(0, screen_dc)
