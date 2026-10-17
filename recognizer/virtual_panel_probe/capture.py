"""On-demand image of one CSP palette window, for the drawer and the external interface.

The recognizer's WindowSnapshot does PrintWindow(PW_RENDERFULLCONTENT): CSP's own pixels for one
HWND, on any monitor (the virtual ones too) and even when covered by other windows.
"""
from __future__ import annotations

import io
import time
from dataclasses import dataclass

from PIL import Image

import paths  # noqa: F401
from csp_workspace_overlay import WindowSnapshot


@dataclass(frozen=True)
class Capture:
    hwnd: int
    rect: tuple[int, int, int, int]  # screen rectangle the pixels cover: the window's visible frame
    width: int
    height: int
    bgrx: bytes                      # top-down rows, 4 bytes per pixel, the 4th byte is 0
    captured_at: float               # time.time()

    def image(self) -> Image.Image:
        return Image.frombytes("RGB", (self.width, self.height), self.bgrx, "raw", "BGRX")

    def png(self) -> bytes:
        buffer = io.BytesIO()
        self.image().save(buffer, "PNG", compress_level=1)
        return buffer.getvalue()


def capture(hwnd: int, bounds: tuple[int, int, int, int] | None = None) -> Capture:
    """Render `hwnd`, cropped to `bounds` (its DWM visible frame) when given."""
    snapshot = WindowSnapshot(hwnd)
    taken = time.time()
    left, top = snapshot.left, snapshot.top
    right, bottom = left + snapshot.width, top + snapshot.height
    if bounds:
        # GetWindowRect includes invisible resize borders; keep only the visible frame.
        left, top = max(left, bounds[0]), max(top, bounds[1])
        right, bottom = min(right, bounds[2]), min(bottom, bounds[3])
    width, height = max(0, right - left), max(0, bottom - top)
    if (width, height) == (snapshot.width, snapshot.height):
        pixels = bytes(snapshot.pixels)
    else:
        stride, x0 = snapshot.width * 4, (left - snapshot.left) * 4
        rows = range(top - snapshot.top, bottom - snapshot.top)
        pixels = b"".join(bytes(snapshot.pixels[r * stride + x0:r * stride + x0 + width * 4]) for r in rows)
    return Capture(hwnd, (left, top, right, bottom), width, height, pixels, taken)
