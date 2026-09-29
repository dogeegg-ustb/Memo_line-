from __future__ import annotations

import ctypes
import threading
from dataclasses import dataclass
from typing import Any

import cv2
import numpy as np

from .models import CaptureFrame, Profile, Rect, utc_now
from .window_service import IS_WINDOWS, WindowInfo, inspect_window

@dataclass
class ScreenInfo:
    """Legacy display metadata retained for diagnostics; it is not a capture target."""

    screen_id: str
    name: str
    geometry: Rect
    logical_size: tuple[int, int]
    capture_size: tuple[int, int]
    dpi: float
    device_pixel_ratio: float
    output_index: int

    def as_dict(self) -> dict[str, Any]:
        return {
            "screen_id": self.screen_id,
            "name": self.name,
            "geometry": self.geometry.to_list(),
            "capture_size": list(self.capture_size),
            "dpi": self.dpi,
            "device_pixel_ratio": self.device_pixel_ratio,
            "output_index": self.output_index,
        }


def qt_screen_infos() -> list[ScreenInfo]:
    """Only used for optional diagnostics; never used as the live recognition target."""
    try:
        from PySide6.QtGui import QGuiApplication
    except ImportError:
        return []
    app = QGuiApplication.instance()
    if app is None:
        return []
    infos: list[ScreenInfo] = []
    for index, screen in enumerate(app.screens()):
        geometry = screen.geometry()
        dpr = float(screen.devicePixelRatio())
        physical_w = round(geometry.width() * dpr)
        physical_h = round(geometry.height() * dpr)
        serial = getattr(screen, "serialNumber", lambda: "")() or ""
        name = screen.name() or f"Display {index + 1}"
        screen_id = f"{name}|{serial}|{geometry.x()},{geometry.y()}"
        infos.append(ScreenInfo(screen_id, name, Rect(geometry.x(), geometry.y(), geometry.width(), geometry.height()), (geometry.width(), geometry.height()), (physical_w, physical_h), float(screen.logicalDotsPerInch()), dpr, index))
    return infos


class CaptureError(RuntimeError):
    pass


if IS_WINDOWS:
    class _BitmapInfoHeader(ctypes.Structure):
        _fields_ = [
            ("biSize", ctypes.c_uint32),
            ("biWidth", ctypes.c_int32),
            ("biHeight", ctypes.c_int32),
            ("biPlanes", ctypes.c_uint16),
            ("biBitCount", ctypes.c_uint16),
            ("biCompression", ctypes.c_uint32),
            ("biSizeImage", ctypes.c_uint32),
            ("biXPelsPerMeter", ctypes.c_int32),
            ("biYPelsPerMeter", ctypes.c_int32),
            ("biClrUsed", ctypes.c_uint32),
            ("biClrImportant", ctypes.c_uint32),
        ]

    class _BitmapInfo(ctypes.Structure):
        _fields_ = [("bmiHeader", _BitmapInfoHeader), ("bmiColors", ctypes.c_uint32 * 1)]


class CaptureService:
    """Capture a selected HWND client area, not the validator window or the whole desktop."""

    def __init__(self):
        self._cameras: dict[int, Any] = {}
        self._lock = threading.Lock()
        self._frame_id = 0

    @property
    def last_frame_id(self) -> int:
        return self._frame_id

    def close(self) -> None:
        with self._lock:
            for camera in self._cameras.values():
                try:
                    if hasattr(camera, "release"):
                        camera.release()
                    else:
                        camera.stop()
                except Exception:
                    pass
            self._cameras.clear()

    def _camera(self, output_index: int) -> Any:
        # DXcam is only needed if PrintWindow cannot capture the target.
        # Import it on that fallback path so normal startup stays lightweight.
        try:
            import dxcam  # type: ignore
        except ImportError:  # pragma: no cover - depends on Windows installation
            return None
        if output_index not in self._cameras:
            self._cameras[output_index] = dxcam.create(output_idx=output_index, output_color="BGR")
        return self._cameras[output_index]

    def capture(self, profile: Profile, target_window: WindowInfo | None) -> CaptureFrame:
        if profile.target_window is None:
            raise CaptureError("needs_reselection: 配置没有目标 CSP 窗口绑定")
        if target_window is None:
            raise CaptureError("unavailable: 没有已确认的 CSP 窗口")
        with self._lock:
            return self._capture_window_locked(target_window, profile.target_window.capture_size, profile.target_window.dpi)

    def capture_window_snapshot(self, target_window: WindowInfo) -> CaptureFrame:
        """Capture a frozen client-area image before ROI configuration exists."""
        with self._lock:
            return self._capture_window_locked(target_window, None, None)

    def _capture_window_locked(self, target_window: WindowInfo, expected_size: tuple[int, int] | None, expected_dpi: float | None) -> CaptureFrame:
        fresh = inspect_window(target_window.hwnd)
        if fresh is None:
            raise CaptureError("unavailable: CSP 窗口已关闭或 HWND 已失效")
        if fresh.minimized or not fresh.visible:
            raise CaptureError("unavailable: CSP 窗口当前不可见或已最小化")
        if expected_size and target_window.exe_name and fresh.exe_name.lower() != target_window.exe_name.lower():
            raise CaptureError("unavailable: 运行时 HWND 已不再指向原确认的 CSP 进程")
        if target_window.class_name and fresh.class_name != target_window.class_name:
            raise CaptureError("unavailable: 运行时 HWND 已不再指向原确认的 CSP 窗口类")
        if expected_size and fresh.capture_size != tuple(expected_size):
            raise CaptureError(f"needs_reselection: CSP 窗口客户区尺寸变化，配置 {expected_size}，当前 {fresh.capture_size}")
        if expected_dpi and abs(fresh.dpi - expected_dpi) > 1.0:
            raise CaptureError(f"needs_reselection: CSP 窗口 DPI 变化，配置 {expected_dpi:g}，当前 {fresh.dpi:g}")

        image = self._capture_printwindow_client(fresh)
        backend = "win32_printwindow_client"
        if image is None:
            image = self._capture_visible_dxcam_client(fresh)
            backend = "dxcam_visible_client_crop"
        if image is None:
            raise CaptureError("unavailable: 目标窗口定向捕获失败，且可见 DXcam 后端不可用或窗口被遮挡")
        actual_size = (int(image.shape[1]), int(image.shape[0]))
        if expected_size and actual_size != tuple(expected_size):
            raise CaptureError(f"needs_reselection: 捕获图像尺寸变化，配置 {expected_size}，当前 {actual_size}")
        self._frame_id += 1
        state = "visible_printwindow" if backend.startswith("win32") else "visible_screen_crop"
        return CaptureFrame(self._frame_id, utc_now(), np.ascontiguousarray(image), backend, fresh.hwnd, state)

    @staticmethod
    def _capture_printwindow_client(window: WindowInfo) -> np.ndarray | None:
        if not IS_WINDOWS or window.capture_size[0] <= 0 or window.capture_size[1] <= 0:
            return None
        user32 = ctypes.windll.user32
        gdi32 = ctypes.windll.gdi32
        handle = ctypes.c_void_p
        user32.GetDC.argtypes = [ctypes.c_void_p]
        user32.GetDC.restype = handle
        user32.ReleaseDC.argtypes = [ctypes.c_void_p, handle]
        user32.ReleaseDC.restype = ctypes.c_int
        user32.PrintWindow.argtypes = [handle, handle, ctypes.c_uint]
        user32.PrintWindow.restype = ctypes.c_int
        gdi32.CreateCompatibleDC.argtypes = [handle]
        gdi32.CreateCompatibleDC.restype = handle
        gdi32.CreateCompatibleBitmap.argtypes = [handle, ctypes.c_int, ctypes.c_int]
        gdi32.CreateCompatibleBitmap.restype = handle
        gdi32.SelectObject.argtypes = [handle, handle]
        gdi32.SelectObject.restype = handle
        gdi32.GetDIBits.argtypes = [handle, handle, ctypes.c_uint, ctypes.c_uint, ctypes.c_void_p, ctypes.POINTER(_BitmapInfo), ctypes.c_uint]
        gdi32.GetDIBits.restype = ctypes.c_int
        gdi32.DeleteObject.argtypes = [handle]
        gdi32.DeleteObject.restype = ctypes.c_int
        gdi32.DeleteDC.argtypes = [handle]
        gdi32.DeleteDC.restype = ctypes.c_int
        width, height = window.capture_size
        screen_dc = user32.GetDC(0)
        memory_dc = gdi32.CreateCompatibleDC(screen_dc)
        bitmap = gdi32.CreateCompatibleBitmap(screen_dc, width, height)
        if not screen_dc or not memory_dc or not bitmap:
            if memory_dc:
                gdi32.DeleteDC(memory_dc)
            if screen_dc:
                user32.ReleaseDC(0, screen_dc)
            return None
        old_bitmap = gdi32.SelectObject(memory_dc, bitmap)
        try:
            flags = 0x00000001 | 0x00000002  # PW_CLIENTONLY | PW_RENDERFULLCONTENT
            ok = user32.PrintWindow(window.hwnd, memory_dc, flags)
            if not ok:
                return None
            bitmap_info = _BitmapInfo()
            bitmap_info.bmiHeader.biSize = ctypes.sizeof(_BitmapInfoHeader)
            bitmap_info.bmiHeader.biWidth = width
            bitmap_info.bmiHeader.biHeight = -height
            bitmap_info.bmiHeader.biPlanes = 1
            bitmap_info.bmiHeader.biBitCount = 32
            bitmap_info.bmiHeader.biCompression = 0
            buffer = ctypes.create_string_buffer(width * height * 4)
            copied = gdi32.GetDIBits(memory_dc, bitmap, 0, height, buffer, ctypes.byref(bitmap_info), 0)
            if copied != height:
                return None
            rgba = np.frombuffer(buffer, dtype=np.uint8, count=width * height * 4).reshape((height, width, 4)).copy()
            return cv2.cvtColor(rgba, cv2.COLOR_BGRA2BGR)
        finally:
            gdi32.SelectObject(memory_dc, old_bitmap)
            gdi32.DeleteObject(bitmap)
            gdi32.DeleteDC(memory_dc)
            user32.ReleaseDC(0, screen_dc)

    def _capture_visible_dxcam_client(self, window: WindowInfo) -> np.ndarray | None:
        camera = self._camera(window.output_index)
        if camera is None:
            return None
        left = window.client_rect.x - window.output_origin[0]
        top = window.client_rect.y - window.output_origin[1]
        right = left + window.client_rect.width
        bottom = top + window.client_rect.height
        if right <= left or bottom <= top:
            return None
        try:
            image = camera.grab(region=(left, top, right, bottom), new_frame_only=False)
        except TypeError:
            image = camera.grab(region=(left, top, right, bottom))
        return None if image is None else np.ascontiguousarray(image)

    @staticmethod
    def crop(frame: CaptureFrame, rect: Rect) -> np.ndarray:
        height, width = frame.image.shape[:2]
        if not rect.contains_size(width, height):
            raise CaptureError(f"ROI 越界: {rect.to_list()} / image={width}x{height}")
        return frame.image[rect.y:rect.y + rect.height, rect.x:rect.x + rect.width].copy()

    @staticmethod
    def crop_all(frame: CaptureFrame, profile: Profile) -> dict[str, np.ndarray]:
        return {roi.id: CaptureService.crop(frame, roi.rect) for roi in profile.rois}
