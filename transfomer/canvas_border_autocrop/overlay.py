"""Screen overlay: transparent multi-primitive OverlayScene (§8).

Primary path: Tk Toplevel + Win32 extended styles (mouse-through, no-activate).
Fallback: raw Win32 layered window (filled rect only).
"""

from __future__ import annotations

import sys
import tkinter as tk
from dataclasses import dataclass
from typing import Optional

from .session import WorkspaceCaptureSession, result_matches_session
from .transform_types import OverlayFilledRect, OverlayScene, OverlayStatusStyle
from .types import DetectionOutput, IntRect


@dataclass(slots=True)
class WorkspaceOverlayStyle:
    fill_color: tuple[int, int, int] = (120, 220, 140)  # RGB
    fill_opacity: float = 0.18
    border_color: tuple[int, int, int] = (60, 180, 100)
    border_opacity: float = 0.90
    border_thickness_physical_px: int = 2
    corner_radius_physical_px: int = 0


_STYLE_PRESETS = {
    OverlayStatusStyle.SUCCESS: WorkspaceOverlayStyle(
        fill_color=(120, 220, 140), border_color=(60, 180, 100)
    ),
    OverlayStatusStyle.NAVIGATOR_ONLY: WorkspaceOverlayStyle(
        fill_color=(220, 200, 80), border_color=(200, 170, 40)
    ),
    OverlayStatusStyle.FAILURE: WorkspaceOverlayStyle(
        fill_color=(220, 100, 80), border_color=(200, 70, 50), fill_opacity=0.14
    ),
}


def _rgb_hex(rgb: tuple[int, int, int]) -> str:
    return f"#{rgb[0]:02x}{rgb[1]:02x}{rgb[2]:02x}"


class WorkspaceOverlayController:
    """Bind overlay lifetime to a capture session CaptureId."""

    def __init__(
        self,
        style: WorkspaceOverlayStyle | None = None,
        master: tk.Misc | None = None,
    ):
        self.style = style or WorkspaceOverlayStyle()
        self._master = master
        self._tk_win: tk.Toplevel | None = None
        self._hwnd = None
        self._bound_capture_id: str = ""
        self._visible_rect: Optional[IntRect] = None
        self.last_error: str = ""
        self._canvas_ref = None
        self._wndproc = None

    @property
    def is_visible(self) -> bool:
        return self._visible_rect is not None and (
            self._tk_win is not None or self._hwnd is not None
        )

    def set_master(self, master: tk.Misc) -> None:
        self._master = master

    def show_for_result(
        self,
        out: DetectionOutput,
        session: WorkspaceCaptureSession,
    ) -> bool:
        if not result_matches_session(out, session):
            self.last_error = "session mismatch or not Success"
            self.hide()
            return False
        rect = out.workspace_rect_screen_physical_px
        if rect is None or not rect.is_valid():
            self.last_error = "invalid screen rect"
            self.hide()
            return False
        self._bound_capture_id = session.capture_id
        self.style = _STYLE_PRESETS[OverlayStatusStyle.SUCCESS]
        return self._show_rect(rect)

    def show_scene(self, scene: OverlayScene, expected_capture_id: str) -> bool:
        """Multi-primitive overlay. Rejects stale CaptureId (§8.4)."""
        if scene.capture_id != expected_capture_id:
            self.last_error = "CaptureId mismatch — refusing stale overlay"
            self.hide()
            return False
        if scene.status_style == OverlayStatusStyle.FAILURE:
            self.style = _STYLE_PRESETS[OverlayStatusStyle.FAILURE]
        elif scene.status_style == OverlayStatusStyle.NAVIGATOR_ONLY:
            self.style = _STYLE_PRESETS[OverlayStatusStyle.NAVIGATOR_ONLY]
        else:
            self.style = _STYLE_PRESETS[OverlayStatusStyle.SUCCESS]

        if not scene.filled_rects:
            self.last_error = "empty OverlayScene"
            self.hide()
            return False

        rects = [fr.rect for fr in scene.filled_rects]
        left = min(r.left for r in rects)
        top = min(r.top for r in rects)
        right = max(r.right for r in rects)
        bottom = max(r.bottom for r in rects)
        pad = 24
        bounds = IntRect(left - pad, top - pad, right + pad, bottom + pad)
        self._bound_capture_id = scene.capture_id
        return self._show_scene(bounds, scene)

    def hide(self) -> None:
        self._visible_rect = None
        self._bound_capture_id = ""
        self._destroy_window()

    def on_session_invalidated(self, session: WorkspaceCaptureSession) -> None:
        if self._bound_capture_id and self._bound_capture_id == session.capture_id:
            self.hide()
        elif not session.active:
            self.hide()

    def _show_rect(self, rect: IntRect) -> bool:
        scene = OverlayScene(
            capture_id=self._bound_capture_id or "legacy",
            status_style=OverlayStatusStyle.SUCCESS,
        )
        scene.filled_rects.append(
            OverlayFilledRect(
                rect=rect,
                fill_rgb=self.style.fill_color,
                fill_opacity=self.style.fill_opacity,
                border_rgb=self.style.border_color,
                border_thickness=self.style.border_thickness_physical_px,
            )
        )
        return self._show_scene(rect, scene)

    def _show_scene(self, bounds: IntRect, scene: OverlayScene) -> bool:
        self._destroy_window()
        self.last_error = ""

        if self._master is not None:
            try:
                if self._show_tk_scene(bounds, scene):
                    self._visible_rect = bounds
                    return True
            except Exception as e:
                self.last_error = f"tk overlay: {e}"

        if sys.platform == "win32" and scene.filled_rects:
            try:
                fr = scene.filled_rects[0]
                self.style = WorkspaceOverlayStyle(
                    fill_color=fr.fill_rgb,
                    fill_opacity=fr.fill_opacity,
                    border_color=fr.border_rgb or fr.fill_rgb,
                    border_thickness_physical_px=fr.border_thickness,
                )
                if self._show_win32(fr.rect):
                    self._visible_rect = fr.rect
                    return True
            except Exception as e:
                self.last_error = (self.last_error + "; " if self.last_error else "") + f"win32: {e}"

        if not self.last_error:
            self.last_error = "overlay create failed"
        return False

    def _destroy_window(self) -> None:
        if self._tk_win is not None:
            try:
                self._tk_win.destroy()
            except Exception:
                pass
            self._tk_win = None
        self._canvas_ref = None

        if self._hwnd is not None and sys.platform == "win32":
            try:
                import ctypes

                ctypes.windll.user32.DestroyWindow(self._hwnd)
            except Exception:
                pass
            self._hwnd = None

    def _show_tk_scene(self, bounds: IntRect, scene: OverlayScene) -> bool:
        master = self._master
        if master is None:
            return False

        width = max(1, bounds.width)
        height = max(1, bounds.height)
        win = tk.Toplevel(master)
        win.withdraw()
        win.overrideredirect(True)
        win.attributes("-topmost", True)
        alpha = float(max(0.12, min(0.40, self.style.fill_opacity + 0.05)))
        try:
            win.attributes("-alpha", alpha)
        except Exception:
            pass

        win.geometry(f"{width}x{height}+{bounds.left}+{bounds.top}")
        bg = "#010101"
        win.configure(bg=bg)

        canvas = tk.Canvas(
            win,
            width=width,
            height=height,
            highlightthickness=0,
            bd=0,
            bg=bg,
        )
        canvas.pack(fill=tk.BOTH, expand=True)

        ox, oy = bounds.left, bounds.top
        for fr in scene.filled_rects:
            r = fr.rect
            fill = _rgb_hex(fr.fill_rgb)
            border = _rgb_hex(fr.border_rgb or fr.fill_rgb)
            thick = max(1, min(3, fr.border_thickness))
            canvas.create_rectangle(
                r.left - ox,
                r.top - oy,
                r.right - ox,
                r.bottom - oy,
                outline=border,
                width=thick,
                fill=fill,
            )

        for line in scene.lines + scene.error_vectors:
            dash = (4, 3) if line.dashed else ()
            canvas.create_line(
                line.x0 - ox,
                line.y0 - oy,
                line.x1 - ox,
                line.y1 - oy,
                fill=_rgb_hex(line.color_rgb),
                width=max(1, line.thickness),
                dash=dash,
            )

        for cross in scene.cross_markers:
            s = cross.size
            col = _rgb_hex(cross.color_rgb)
            cx, cy = cross.x - ox, cross.y - oy
            canvas.create_line(cx - s, cy, cx + s, cy, fill=col, width=1)
            canvas.create_line(cx, cy - s, cx, cy + s, fill=col, width=1)

        for lab in scene.labels:
            canvas.create_text(
                lab.x - ox,
                lab.y - oy,
                anchor=tk.NW,
                fill=_rgb_hex(lab.color_rgb),
                font=("Segoe UI", 11),
                text=lab.text,
            )

        win.deiconify()
        win.update_idletasks()
        win.lift()

        if sys.platform == "win32":
            self._apply_click_through(win)

        self._tk_win = win
        self._canvas_ref = canvas
        return True

    def _apply_click_through(self, win: tk.Toplevel) -> None:
        import ctypes

        user32 = ctypes.windll.user32
        GWL_EXSTYLE = -20
        WS_EX_LAYERED = 0x00080000
        WS_EX_TRANSPARENT = 0x00000020
        WS_EX_TOOLWINDOW = 0x00000080
        WS_EX_NOACTIVATE = 0x08000000
        HWND_TOPMOST = -1
        SWP_NOMOVE = 0x0002
        SWP_NOSIZE = 0x0001
        SWP_NOACTIVATE = 0x0010
        SWP_SHOWWINDOW = 0x0040
        SWP_FRAMECHANGED = 0x0020

        win.update_idletasks()
        hwnd = int(win.winfo_id())
        parent = user32.GetParent(hwnd)
        if parent:
            hwnd = int(parent)

        style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        style |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        user32.SetWindowLongW(hwnd, GWL_EXSTYLE, style)
        user32.SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED,
        )
        self._hwnd = hwnd

    def _show_win32(self, rect: IntRect) -> bool:
        import ctypes
        from ctypes import wintypes

        import numpy as np

        user32 = ctypes.windll.user32
        gdi32 = ctypes.windll.gdi32

        WS_EX_LAYERED = 0x00080000
        WS_EX_TRANSPARENT = 0x00000020
        WS_EX_TOOLWINDOW = 0x00000080
        WS_EX_NOACTIVATE = 0x08000000
        WS_EX_TOPMOST = 0x00000008
        WS_POPUP = 0x80000000
        HWND_TOPMOST = -1
        SWP_NOACTIVATE = 0x0010
        SWP_SHOWWINDOW = 0x0040
        ULW_ALPHA = 0x02
        AC_SRC_OVER = 0x00
        AC_SRC_ALPHA = 0x01
        ERROR_CLASS_ALREADY_EXISTS = 1410

        width = max(1, rect.width)
        height = max(1, rect.height)

        WNDPROC = ctypes.WINFUNCTYPE(
            ctypes.c_long, wintypes.HWND, ctypes.c_uint, wintypes.WPARAM, wintypes.LPARAM
        )

        class WNDCLASS(ctypes.Structure):
            _fields_ = [
                ("style", ctypes.c_uint),
                ("lpfnWndProc", WNDPROC),
                ("cbClsExtra", ctypes.c_int),
                ("cbWndExtra", ctypes.c_int),
                ("hInstance", wintypes.HINSTANCE),
                ("hIcon", wintypes.HICON),
                ("hCursor", wintypes.HANDLE),
                ("hbrBackground", wintypes.HBRUSH),
                ("lpszMenuName", wintypes.LPCWSTR),
                ("lpszClassName", wintypes.LPCWSTR),
            ]

        def _proc(hwnd, msg, wparam, lparam):
            if msg == 0x0010:
                user32.DestroyWindow(hwnd)
                return 0
            return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

        self._wndproc = WNDPROC(_proc)
        hinst = user32.GetModuleHandleW(None)
        class_name = "ArtLineWorkspaceOverlayV3"

        wc = WNDCLASS()
        wc.lpfnWndProc = self._wndproc
        wc.hInstance = hinst
        wc.lpszClassName = class_name
        user32.RegisterClassW(ctypes.byref(wc))

        ex = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST
        hwnd = user32.CreateWindowExW(
            ex,
            class_name,
            "WorkspaceOverlay",
            WS_POPUP,
            int(rect.left),
            int(rect.top),
            int(width),
            int(height),
            0,
            0,
            hinst,
            0,
        )
        if not hwnd:
            self.last_error = f"CreateWindowEx failed err={ctypes.get_last_error()}"
            return False

        fill_a = int(round(self.style.fill_opacity * 255))
        border_a = int(round(self.style.border_opacity * 255))
        fr, fg, fb = self.style.fill_color
        br, bg, bb = self.style.border_color
        thick = max(1, min(3, self.style.border_thickness_physical_px))
        img = np.empty((height, width, 4), dtype=np.uint8)
        img[..., 0] = fb
        img[..., 1] = fg
        img[..., 2] = fr
        img[..., 3] = fill_a
        img[:thick, :] = (bb, bg, br, border_a)
        img[-thick:, :] = (bb, bg, br, border_a)
        img[:, :thick] = (bb, bg, br, border_a)
        img[:, -thick:] = (bb, bg, br, border_a)
        buf = np.ascontiguousarray(img).tobytes()

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

        class POINT(ctypes.Structure):
            _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]

        class SIZE(ctypes.Structure):
            _fields_ = [("cx", wintypes.LONG), ("cy", wintypes.LONG)]

        class BLENDFUNCTION(ctypes.Structure):
            _fields_ = [
                ("BlendOp", ctypes.c_byte),
                ("BlendFlags", ctypes.c_byte),
                ("SourceConstantAlpha", ctypes.c_byte),
                ("AlphaFormat", ctypes.c_byte),
            ]

        bmi = BITMAPINFOHEADER()
        bmi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        bmi.biWidth = width
        bmi.biHeight = -height
        bmi.biPlanes = 1
        bmi.biBitCount = 32
        bmi.biCompression = 0

        hdc_screen = user32.GetDC(0)
        hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
        bits = ctypes.c_void_p()
        hbmp = gdi32.CreateDIBSection(
            hdc_mem, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0
        )
        if not hbmp or not bits:
            user32.DestroyWindow(hwnd)
            self.last_error = "CreateDIBSection failed"
            return False

        ctypes.memmove(bits, buf, len(buf))
        gdi32.SelectObject(hdc_mem, hbmp)

        blend = BLENDFUNCTION(AC_SRC_OVER, 0, 255, AC_SRC_ALPHA)
        pt_dst = POINT(int(rect.left), int(rect.top))
        size = SIZE(int(width), int(height))
        pt_src = POINT(0, 0)
        ok = user32.UpdateLayeredWindow(
            hwnd,
            hdc_screen,
            ctypes.byref(pt_dst),
            ctypes.byref(size),
            hdc_mem,
            ctypes.byref(pt_src),
            0,
            ctypes.byref(blend),
            ULW_ALPHA,
        )
        user32.SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            int(rect.left),
            int(rect.top),
            int(width),
            int(height),
            SWP_NOACTIVATE | SWP_SHOWWINDOW,
        )

        gdi32.DeleteObject(hbmp)
        gdi32.DeleteDC(hdc_mem)
        user32.ReleaseDC(0, hdc_screen)

        if not ok:
            user32.DestroyWindow(hwnd)
            self.last_error = f"UpdateLayeredWindow failed err={ctypes.get_last_error()}"
            return False

        self._hwnd = hwnd
        return True
