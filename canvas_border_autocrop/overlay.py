"""Screen overlay: transparent light-green workspace rectangle.

Primary path: Tk Toplevel + Win32 extended styles (mouse-through, no-activate).
Fallback: raw Win32 layered window.
"""

from __future__ import annotations

import sys
import tkinter as tk
from dataclasses import dataclass
from typing import Optional

from .session import WorkspaceCaptureSession, result_matches_session
from .types import DetectionOutput, IntRect


@dataclass(slots=True)
class WorkspaceOverlayStyle:
    fill_color: tuple[int, int, int] = (120, 220, 140)  # RGB
    fill_opacity: float = 0.18
    border_color: tuple[int, int, int] = (60, 180, 100)
    border_opacity: float = 0.90
    border_thickness_physical_px: int = 2
    corner_radius_physical_px: int = 0


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
        return self._show_rect(rect)

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
        self._destroy_window()
        self.last_error = ""

        # Prefer Tk path when we have a master (our app always does)
        if self._master is not None:
            try:
                if self._show_tk(rect):
                    self._visible_rect = rect
                    return True
            except Exception as e:
                self.last_error = f"tk overlay: {e}"

        if sys.platform == "win32":
            try:
                if self._show_win32(rect):
                    self._visible_rect = rect
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

        if self._hwnd is not None and sys.platform == "win32":
            try:
                import ctypes

                ctypes.windll.user32.DestroyWindow(self._hwnd)
            except Exception:
                pass
            self._hwnd = None

    def _show_tk(self, rect: IntRect) -> bool:
        master = self._master
        if master is None:
            return False

        width = max(1, rect.width)
        height = max(1, rect.height)
        thick = max(1, min(3, self.style.border_thickness_physical_px))
        fill = _rgb_hex(self.style.fill_color)
        border = _rgb_hex(self.style.border_color)

        win = tk.Toplevel(master)
        win.withdraw()
        win.overrideredirect(True)
        win.attributes("-topmost", True)
        # Whole-window alpha approximates fill opacity (architecture: 0.12–0.20)
        alpha = float(max(0.12, min(0.35, self.style.fill_opacity)))
        try:
            win.attributes("-alpha", alpha)
        except Exception:
            pass

        win.geometry(f"{width}x{height}+{rect.left}+{rect.top}")
        win.configure(bg=border)

        canvas = tk.Canvas(
            win,
            width=width,
            height=height,
            highlightthickness=0,
            bd=0,
            bg=border,
        )
        canvas.pack(fill=tk.BOTH, expand=True)
        # Outer border ring + inner fill (same color family; window alpha provides translucency)
        canvas.create_rectangle(0, 0, width, height, outline=border, width=thick, fill=fill)

        win.deiconify()
        win.update_idletasks()
        win.lift()

        if sys.platform == "win32":
            self._apply_click_through(win)

        # Keep reference
        self._tk_win = win
        self._photo_keepalive = canvas  # prevent GC of canvas children
        return True

    def _apply_click_through(self, win: tk.Toplevel) -> None:
        """WS_EX_TRANSPARENT | LAYERED | TOOLWINDOW | NOACTIVATE — mouse-through, no focus."""
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
        # Tk may nest; prefer top-level owner
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
        """Raw layered popup; fixed ctypes WNDCLASS / CreateWindowEx usage."""
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
            if msg == 0x0010:  # WM_CLOSE
                user32.DestroyWindow(hwnd)
                return 0
            return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

        self._wndproc = WNDPROC(_proc)
        hinst = user32.GetModuleHandleW(None)
        class_name = "ArtLineWorkspaceOverlayV2"

        wc = WNDCLASS()
        wc.lpfnWndProc = self._wndproc
        wc.hInstance = hinst
        wc.lpszClassName = class_name
        atom = user32.RegisterClassW(ctypes.byref(wc))
        if not atom and ctypes.get_last_error() not in (0, ERROR_CLASS_ALREADY_EXISTS):
            # Retry after clearing last error — already-exists is OK
            err = ctypes.get_last_error()
            if err and err != ERROR_CLASS_ALREADY_EXISTS:
                # Still try CreateWindowEx; class may exist from prior run
                pass

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
