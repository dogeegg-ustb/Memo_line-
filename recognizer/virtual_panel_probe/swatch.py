"""A small solid-colour window on the virtual screen for CSP's screen colour picker.

Memoline sets CSP's drawing colour without typing RGB values: it shows the colour here, parks the
cursor on it and presses CSP's "obtain screen colour" shortcut (applicationscreeneyecolorpicker),
which takes the pixel under the cursor. The window never activates (CSP keeps the keyboard), stays
above the stashed palettes, and belongs to this process, so the guard's patrol leaves it alone.
"""
from __future__ import annotations

import ctypes
import time
import tkinter as tk
from ctypes import wintypes

USER32 = ctypes.WinDLL("user32", use_last_error=True)
GDI32 = ctypes.WinDLL("gdi32")
USER32.GetDC.restype = wintypes.HDC
USER32.GetDC.argtypes = [wintypes.HWND]
USER32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
USER32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
USER32.SetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_long]
USER32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                wintypes.UINT]
GDI32.GetPixel.restype = wintypes.DWORD
GDI32.GetPixel.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int]

GWL_EXSTYLE = -20
WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE = 0x80, 0x08000000
HWND_TOPMOST = -1
SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x1, 0x2, 0x10
OFFSCREEN = -32000
CLR_INVALID = 0xFFFFFFFF
SIZE = 32  # physical pixels; the picker reads one pixel, the rest is room for the cursor to land


def placement(area: tuple[int, int, int, int], gap: int) -> tuple[int, int, int, int]:
    """Top-right corner of the virtual screen: the stashed palettes are packed against its left edge."""
    left, top = area[2] - gap - SIZE, area[1] + gap
    return left, top, left + SIZE, top + SIZE


def parse_rgb(value) -> tuple[int, int, int]:
    if (not isinstance(value, list) or len(value) != 3
            or any(isinstance(v, bool) or not isinstance(v, int) or not 0 <= v <= 255 for v in value)):
        raise ValueError("rgb 必须是 3 个 0–255 的整数")
    return value[0], value[1], value[2]


def screen_pixel(x: int, y: int) -> tuple[int, int, int] | None:
    """The composed desktop's pixel at (x, y), physical coordinates; None off every monitor."""
    dc = USER32.GetDC(None)
    try:
        value = GDI32.GetPixel(dc, x, y)
    finally:
        USER32.ReleaseDC(None, dc)
    return None if value == CLR_INVALID else (value & 0xFF, (value >> 8) & 0xFF, (value >> 16) & 0xFF)


def wait_for_pixel(point: tuple[int, int], rgb: tuple[int, int, int], timeout: float = 0.5,
                   read=screen_pixel) -> tuple[int, int, int] | None:
    """Poll until the screen shows `rgb` at `point` (the compositor shows a new window a frame or two
    later); returns the last pixel read. A different final pixel means something transforms the
    colour on the way to the screen (HDR, a colour filter) and the picker would read that instead."""
    deadline = time.monotonic() + timeout
    while True:
        pixel = read(*point)
        if pixel == rgb or time.monotonic() >= deadline:
            return pixel
        time.sleep(0.02)


class Swatch:
    """Created once offscreen with WS_EX_NOACTIVATE (as the edge drawer's popups), then only recoloured and
    moved: mapping a new Tk window would make it the foreground window and take the keyboard from CSP."""

    def __init__(self, root: tk.Tk):
        self.root = root
        self.window: tk.Toplevel | None = None
        self.rect: tuple[int, int, int, int] | None = None
        self.rgb: tuple[int, int, int] | None = None

    @property
    def shown(self) -> bool:
        return self.rect is not None

    def show(self, rgb: tuple[int, int, int], rect: tuple[int, int, int, int]) -> None:
        colour = "#%02x%02x%02x" % rgb
        if self.window is None:
            self.window = tk.Toplevel(self.root, bg=colour)
            self.window.overrideredirect(True)
            self.window.attributes("-topmost", True)
            self.window.geometry(f"1x1+{OFFSCREEN}+{OFFSCREEN}")
            self.window.update_idletasks()
            hwnd = self.hwnd
            USER32.SetWindowLongW(hwnd, GWL_EXSTYLE,
                                  USER32.GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
        self.window.configure(bg=colour)
        self.place(rect)
        self.rect, self.rgb = rect, rgb

    def place(self, rect: tuple[int, int, int, int]) -> None:
        self.window.geometry(f"{rect[2] - rect[0]}x{rect[3] - rect[1]}+{rect[0]}+{rect[1]}")
        self.window.update_idletasks()
        USER32.SetWindowPos(self.hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE)

    @property
    def hwnd(self) -> int:
        return int(self.window.wm_frame(), 16)

    def hide(self) -> None:
        """Back offscreen: destroying and recreating it would map a new window, which activates."""
        if self.window is not None and self.rect is not None:
            self.place((OFFSCREEN, OFFSCREEN, OFFSCREEN + 1, OFFSCREEN + 1))
        self.rect, self.rgb = None, None

    def destroy(self) -> None:
        if self.window is not None:
            self.window.destroy()
        self.window, self.rect, self.rgb = None, None, None
