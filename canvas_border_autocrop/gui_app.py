"""Interactive workspace detection: freeze screen → ROI → detect → green overlay."""

from __future__ import annotations

import time
import tkinter as tk
from tkinter import messagebox, ttk

import numpy as np
from PIL import Image, ImageTk

from canvas_border_autocrop import (
    DetectionInput,
    DetectionStatus,
    IntRect,
    PixelFormat,
    WorkspaceOverlayController,
    detect_workspace_rect,
)
from canvas_border_autocrop.capture_screen import ensure_dpi_awareness, freeze_desktop_session
from canvas_border_autocrop.session import WorkspaceCaptureSession


class RoiSelectWindow(tk.Toplevel):
    """Fullscreen frozen-frame ROI selector. Coordinates == CapturePx."""

    def __init__(self, master: tk.Tk, session: WorkspaceCaptureSession, on_done, on_cancel):
        super().__init__(master)
        self.session = session
        self.on_done = on_done
        self.on_cancel = on_cancel
        self._drag_start: tuple[int, int] | None = None
        self._roi: IntRect | None = None
        self._roi_id = None
        self._hint_id = None

        origin = session.capture_to_screen
        w, h = session.width, session.height
        self.geometry(f"{w}x{h}+{origin.origin_x}+{origin.origin_y}")
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        self.configure(bg="#000000", cursor="crosshair")
        self.focus_force()

        bgra = session.frozen_capture_bgra
        rgba = bgra[:, :, [2, 1, 0, 3]]
        self._pil = Image.fromarray(rgba, mode="RGBA")
        self._photo = ImageTk.PhotoImage(self._pil)

        self.canvas = tk.Canvas(self, width=w, height=h, highlightthickness=0, cursor="crosshair", bg="#000")
        self.canvas.pack(fill=tk.BOTH, expand=True)
        self.canvas.create_image(0, 0, anchor=tk.NW, image=self._photo)
        self._hint_id = self.canvas.create_text(
            24,
            24,
            anchor=tk.NW,
            fill="#ffffff",
            font=("Segoe UI", 14),
            text="拖拽框选工作区大致范围 · 松开后自动检测 · Esc 取消",
        )
        # Dim tip background via outline rect
        self.canvas.create_rectangle(12, 12, 520, 52, fill="#000000", stipple="gray50", outline="")
        self.canvas.tag_raise(self._hint_id)

        self.canvas.bind("<ButtonPress-1>", self._on_press)
        self.canvas.bind("<B1-Motion>", self._on_drag)
        self.canvas.bind("<ButtonRelease-1>", self._on_release)
        self.bind("<Escape>", lambda e: self._cancel())
        self.protocol("WM_DELETE_WINDOW", self._cancel)

    def _on_press(self, event: tk.Event) -> None:
        self._drag_start = (event.x, event.y)
        self._roi = None
        if self._roi_id is not None:
            self.canvas.delete(self._roi_id)
            self._roi_id = None

    def _on_drag(self, event: tk.Event) -> None:
        if self._drag_start is None:
            return
        x0, y0 = self._drag_start
        x1, y1 = event.x, event.y
        l, r = sorted((x0, x1))
        t, b = sorted((y0, y1))
        l = max(0, min(self.session.width - 1, l))
        r = max(0, min(self.session.width, r))
        t = max(0, min(self.session.height - 1, t))
        b = max(0, min(self.session.height, b))
        if self._roi_id is not None:
            self.canvas.delete(self._roi_id)
        self._roi_id = self.canvas.create_rectangle(
            l, t, r, b, outline="#4ea1ff", width=2
        )

    def _on_release(self, event: tk.Event) -> None:
        if self._drag_start is None:
            return
        x0, y0 = self._drag_start
        x1, y1 = event.x, event.y
        self._drag_start = None
        l, r = sorted((int(x0), int(x1)))
        t, b = sorted((int(y0), int(y1)))
        l = max(0, min(self.session.width - 1, l))
        r = max(0, min(self.session.width, r))
        t = max(0, min(self.session.height - 1, t))
        b = max(0, min(self.session.height, b))
        if r - l < 32 or b - t < 32:
            if self._roi_id is not None:
                self.canvas.delete(self._roi_id)
                self._roi_id = None
            return
        self._roi = IntRect(l, t, r, b)
        self.session.set_user_roi(self._roi)
        # Freeze UI briefly then finish
        self.canvas.itemconfigure(self._hint_id, text="检测中…")
        self.update_idletasks()
        self.after(10, self._finish)

    def _finish(self) -> None:
        roi = self._roi
        cb = self.on_done
        self.destroy()
        if roi is not None:
            cb(roi)

    def _cancel(self) -> None:
        cb = self.on_cancel
        self.destroy()
        cb()


class App(tk.Tk):
    """Control panel for capture → ROI → detect → screen overlay."""

    def __init__(self) -> None:
        ensure_dpi_awareness()
        super().__init__()
        self.title("工作区矩形识别")
        self.geometry("460x180")
        self.minsize(420, 160)
        self.resizable(False, False)

        self._session: WorkspaceCaptureSession | None = None
        self._select: RoiSelectWindow | None = None
        self._overlay = WorkspaceOverlayController()
        self._busy = False

        self._build()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self.bind("<F2>", lambda e: self.start_capture())
        self.bind("<Escape>", lambda e: self.hide_overlay())

    def _build(self) -> None:
        pad = ttk.Frame(self, padding=16)
        pad.pack(fill=tk.BOTH, expand=True)

        title = ttk.Label(pad, text="绘画软件工作区识别", font=("Segoe UI", 14, "bold"))
        title.pack(anchor=tk.W)

        tip = ttk.Label(
            pad,
            text="点击开始后冻结当前桌面，在截图上拖拽粗略框选；\n成功后在真实屏幕显示淡绿色透明矩形（鼠标穿透）。",
            justify=tk.LEFT,
        )
        tip.pack(anchor=tk.W, pady=(8, 12))

        row = ttk.Frame(pad)
        row.pack(fill=tk.X)
        ttk.Button(row, text="开始截屏框选 (F2)", command=self.start_capture).pack(side=tk.LEFT)
        ttk.Button(row, text="隐藏覆盖层 (Esc)", command=self.hide_overlay).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(row, text="退出", command=self._on_close).pack(side=tk.RIGHT)

        self.status = tk.StringVar(value="就绪：请先切换到绘画软件窗口，再点开始")
        ttk.Label(pad, textvariable=self.status, wraplength=420).pack(anchor=tk.W, pady=(14, 0))

    def hide_overlay(self) -> None:
        self._overlay.hide()
        self.status.set("已隐藏覆盖层")

    def start_capture(self) -> None:
        if self._busy:
            return
        # Architecture: hide overlay before capture to avoid feedback pollution
        self._overlay.hide()
        if self._session is not None:
            self._session.invalidate()
            self._session = None

        self.status.set("正在冻结桌面…")
        self.update_idletasks()
        # Withdraw control panel so it is not in the screenshot
        self.withdraw()
        self.update_idletasks()
        time.sleep(0.12)

        try:
            session, cap = freeze_desktop_session()
        except Exception as e:
            self.deiconify()
            self.lift()
            self.status.set(f"截屏失败: {e}")
            messagebox.showerror("截屏失败", str(e))
            return

        self._session = session
        self.status.set(
            f"已冻结 {cap.width}×{cap.height} @({cap.origin_x},{cap.origin_y}) — 请拖拽框选"
        )
        self._select = RoiSelectWindow(
            self,
            session,
            on_done=self._on_roi_confirmed,
            on_cancel=self._on_roi_cancelled,
        )

    def _on_roi_cancelled(self) -> None:
        if self._session is not None:
            self._session.invalidate()
            self._session = None
        self._select = None
        self.deiconify()
        self.lift()
        self.status.set("已取消框选")

    def _on_roi_confirmed(self, roi: IntRect) -> None:
        self._select = None
        session = self._session
        if session is None or not session.active:
            self.deiconify()
            self.status.set("会话已失效，请重新截屏")
            return

        self._busy = True
        self.deiconify()
        self.lift()
        self.status.set("检测中…")
        self.update_idletasks()

        bgra = session.frozen_capture_bgra
        h, w = bgra.shape[:2]
        origin = session.capture_to_screen
        t0 = time.perf_counter()
        out = detect_workspace_rect(
            DetectionInput(
                capture_buffer=bgra,
                capture_width=w,
                capture_height=h,
                stride=w * 4,
                user_roi_capture_px=roi,
                dpi_scale_x=session.monitor_descriptors[0].dpi_scale_x,
                dpi_scale_y=session.monitor_descriptors[0].dpi_scale_y,
                pixel_format=PixelFormat.BGRA,
                capture_id=session.capture_id,
                capture_origin_screen_physical_x=origin.origin_x,
                capture_origin_screen_physical_y=origin.origin_y,
            )
        )
        ms = (time.perf_counter() - t0) * 1000.0
        self._busy = False

        if out.status != DetectionStatus.OK or out.workspace_rect_screen_physical_px is None:
            self._overlay.hide()
            self.status.set(f"失败: {out.status.value} — {out.message}  ({ms:.0f} ms)")
            messagebox.showwarning(
                "检测失败",
                f"状态: {out.status.value}\n原因: {out.message or '未知'}\n"
                f"请重新截屏框选。\n耗时: {ms:.0f} ms",
            )
            return

        ok = self._overlay.show_for_result(out, session)
        r = out.workspace_rect_screen_physical_px
        grade = out.evidence_grade.value if out.evidence_grade else "?"
        if ok:
            self.status.set(
                f"成功 grade={grade} conf={out.confidence:.2f}  "
                f"屏幕矩形=[{r.left},{r.right})×[{r.top},{r.bottom})  {ms:.0f} ms\n"
                f"淡绿色覆盖层已显示（鼠标穿透，Esc 隐藏）"
            )
        else:
            self.status.set(
                f"检测成功但覆盖层显示失败 grade={grade} rect={r.as_tuple()}  {ms:.0f} ms"
            )
            messagebox.showwarning("覆盖层", "检测成功，但无法创建屏幕覆盖层窗口。")

    def _on_close(self) -> None:
        self._overlay.hide()
        if self._session is not None:
            self._session.invalidate()
        if self._select is not None:
            try:
                self._select.destroy()
            except Exception:
                pass
        self.destroy()


def main() -> None:
    ensure_dpi_awareness()
    app = App()
    app.mainloop()


if __name__ == "__main__":
    main()
