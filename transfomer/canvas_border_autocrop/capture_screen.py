"""Freeze the current desktop into a CapturePx BGRA buffer (Windows)."""

from __future__ import annotations

import sys
from dataclasses import dataclass

import numpy as np

from .session import MonitorDescriptor, begin_capture_session
from .types import IntRect


@dataclass(slots=True)
class ScreenCaptureResult:
    bgra: np.ndarray
    origin_x: int
    origin_y: int
    width: int
    height: int
    dpi_scale_x: float
    dpi_scale_y: float


def ensure_dpi_awareness() -> None:
    """Per-monitor DPI awareness so capture/overlay use physical pixels."""
    if sys.platform != "win32":
        return
    try:
        import ctypes

        # DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
        ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:
        try:
            import ctypes

            ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
        except Exception:
            try:
                import ctypes

                ctypes.windll.user32.SetProcessDPIAware()
            except Exception:
                pass


def virtual_screen_bounds() -> IntRect:
    if sys.platform != "win32":
        # Fallback single display
        from PIL import ImageGrab

        img = ImageGrab.grab()
        return IntRect(0, 0, img.width, img.height)

    import ctypes

    user32 = ctypes.windll.user32
    SM_XVIRTUALSCREEN = 76
    SM_YVIRTUALSCREEN = 77
    SM_CXVIRTUALSCREEN = 78
    SM_CYVIRTUALSCREEN = 79
    left = int(user32.GetSystemMetrics(SM_XVIRTUALSCREEN))
    top = int(user32.GetSystemMetrics(SM_YVIRTUALSCREEN))
    width = int(user32.GetSystemMetrics(SM_CXVIRTUALSCREEN))
    height = int(user32.GetSystemMetrics(SM_CYVIRTUALSCREEN))
    return IntRect(left, top, left + max(width, 1), top + max(height, 1))


def primary_dpi_scale() -> tuple[float, float]:
    if sys.platform != "win32":
        return 1.0, 1.0
    try:
        import ctypes

        hdc = ctypes.windll.user32.GetDC(None)
        dpi_x = int(ctypes.windll.gdi32.GetDeviceCaps(hdc, 88))  # LOGPIXELSX
        dpi_y = int(ctypes.windll.gdi32.GetDeviceCaps(hdc, 90))  # LOGPIXELSY
        ctypes.windll.user32.ReleaseDC(None, hdc)
        return max(dpi_x / 96.0, 0.5), max(dpi_y / 96.0, 0.5)
    except Exception:
        return 1.0, 1.0


def capture_virtual_screen_bgra() -> ScreenCaptureResult:
    """Capture the full virtual desktop in physical pixels as BGRA uint8."""
    ensure_dpi_awareness()
    bounds = virtual_screen_bounds()
    dpi_x, dpi_y = primary_dpi_scale()

    if sys.platform == "win32":
        bgra = _capture_win32_bitblt(bounds)
    else:
        from PIL import ImageGrab

        rgb = np.asarray(ImageGrab.grab(bbox=(bounds.left, bounds.top, bounds.right, bounds.bottom)))
        if rgb.ndim != 3:
            raise RuntimeError("screen capture failed")
        h, w = rgb.shape[:2]
        bgra = np.empty((h, w, 4), dtype=np.uint8)
        bgra[..., 0] = rgb[..., 2]
        bgra[..., 1] = rgb[..., 1]
        bgra[..., 2] = rgb[..., 0]
        bgra[..., 3] = 255

    return ScreenCaptureResult(
        bgra=np.ascontiguousarray(bgra),
        origin_x=bounds.left,
        origin_y=bounds.top,
        width=bgra.shape[1],
        height=bgra.shape[0],
        dpi_scale_x=dpi_x,
        dpi_scale_y=dpi_y,
    )


def freeze_desktop_session():
    """Capture now and return a WorkspaceCaptureSession bound to this frame."""
    cap = capture_virtual_screen_bgra()
    return begin_capture_session(
        cap.bgra,
        origin_x=cap.origin_x,
        origin_y=cap.origin_y,
        monitors=[
            MonitorDescriptor(
                cap.origin_x,
                cap.origin_y,
                cap.origin_x + cap.width,
                cap.origin_y + cap.height,
                dpi_scale_x=cap.dpi_scale_x,
                dpi_scale_y=cap.dpi_scale_y,
                name="virtual",
            )
        ],
    ), cap


def _capture_win32_bitblt(bounds: IntRect) -> np.ndarray:
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    gdi32 = ctypes.windll.gdi32

    width = bounds.width
    height = bounds.height
    if width <= 0 or height <= 0:
        raise RuntimeError("invalid virtual screen size")

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [
            ("biSize", wintypes.DWORD),
            ("biWidth", wintypes.LONG),
            ("biHeight", wintypes.LONG),
            ("biPlanes", wintypes.WORD),
            ("biBitCount", wintypes.WORD),
            ("biCompression", wintypes.DWORD),
            ("biSizeImage", wintypes.DWORD),
            ("biXPelsPerMeter", wintypes.LONG),
            ("biYPelsPerMeter", wintypes.LONG),
            ("biClrUsed", wintypes.DWORD),
            ("biClrImportant", wintypes.DWORD),
        ]

    SRCCOPY = 0x00CC0020
    hdc_screen = user32.GetDC(None)
    if not hdc_screen:
        raise RuntimeError("GetDC failed")
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    bmi = BITMAPINFOHEADER()
    bmi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.biWidth = width
    bmi.biHeight = -height  # top-down
    bmi.biPlanes = 1
    bmi.biBitCount = 32
    bmi.biCompression = 0

    bits = ctypes.c_void_p()
    hbmp = gdi32.CreateDIBSection(hdc_mem, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    if not hbmp or not bits:
        gdi32.DeleteDC(hdc_mem)
        user32.ReleaseDC(None, hdc_screen)
        raise RuntimeError("CreateDIBSection failed")

    old = gdi32.SelectObject(hdc_mem, hbmp)
    ok = gdi32.BitBlt(hdc_mem, 0, 0, width, height, hdc_screen, bounds.left, bounds.top, SRCCOPY)
    gdi32.SelectObject(hdc_mem, old)

    buf_size = width * height * 4
    raw = (ctypes.c_ubyte * buf_size).from_address(bits.value)
    bgra = np.frombuffer(raw, dtype=np.uint8).reshape((height, width, 4)).copy()
    # Force opaque alpha (screen DIB often has 0 alpha)
    bgra[..., 3] = 255

    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(None, hdc_screen)

    if not ok:
        raise RuntimeError("BitBlt failed")
    return bgra
