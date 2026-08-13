"""Interactive dual-ROI screen↔canvas transform GUI (architecture §3).

Flow: freeze → workspace ROI (blue) → navigator ROI (orange) → user clicks Start Compute.
"""

from __future__ import annotations

import time
import tkinter as tk
from tkinter import messagebox, ttk
from typing import Callable

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
from canvas_border_autocrop import debug_trace as dbg
from canvas_border_autocrop.session import WorkspaceCaptureSession
from canvas_border_autocrop.transform_pipeline import run_transform
from canvas_border_autocrop.transform_session import TransformCaptureSession, wrap_transform_session
from canvas_border_autocrop.transform_types import (
    TransformRequest,
    TransformSessionState,
    TransformStatus,
    ValidationStatus,
)


class RoiSelectWindow(tk.Toplevel):
    """Fullscreen frozen-frame ROI selector. Coordinates == CapturePx."""

    def __init__(
        self,
        master: tk.Tk,
        session: WorkspaceCaptureSession,
        on_done: Callable[[IntRect], None],
        on_cancel: Callable[[], None],
        *,
        hint: str,
        outline: str = "#4ea1ff",
        existing_rois: list[tuple[IntRect, str]] | None = None,
    ):
        super().__init__(master)
        self.session = session
        self.on_done = on_done
        self.on_cancel = on_cancel
        self._outline = outline
        self._drag_start: tuple[int, int] | None = None
        self._roi: IntRect | None = None
        self._roi_id = None
        self._hint_id = None

        origin = session.capture_to_screen
        w, h = session.width, session.height
        dbg.stage("RoiSelect.begin", w=w, h=h, ox=origin.origin_x, oy=origin.origin_y)
        self.geometry(f"{w}x{h}+{origin.origin_x}+{origin.origin_y}")
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        self.configure(bg="#000000", cursor="crosshair")
        self.focus_force()

        bgra = session.frozen_capture_bgra
        dbg.stage("RoiSelect.before_rgba_copy", nbytes=int(bgra.nbytes))
        rgba = bgra[:, :, [2, 1, 0, 3]]
        dbg.stage("RoiSelect.before_pil")
        self._pil = Image.fromarray(rgba, mode="RGBA")
        dbg.stage("RoiSelect.before_photoimage")
        self._photo = ImageTk.PhotoImage(self._pil)
        dbg.stage("RoiSelect.after_photoimage")

        self.canvas = tk.Canvas(self, width=w, height=h, highlightthickness=0, cursor="crosshair", bg="#000")
        self.canvas.pack(fill=tk.BOTH, expand=True)
        self.canvas.create_image(0, 0, anchor=tk.NW, image=self._photo)
        dbg.stage("RoiSelect.ready", hint=hint[:24])

        for rect, color in existing_rois or []:
            self.canvas.create_rectangle(
                rect.left, rect.top, rect.right, rect.bottom, outline=color, width=2
            )

        self.canvas.create_rectangle(12, 12, 720, 52, fill="#000000", outline="#333333")
        self._hint_id = self.canvas.create_text(
            24,
            24,
            anchor=tk.NW,
            fill="#ffffff",
            font=("Segoe UI", 14),
            text=hint,
        )

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
            l, t, r, b, outline=self._outline, width=2
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
        self.canvas.itemconfigure(self._hint_id, text="已记录 ROI…")
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
    """Dual-ROI transform control panel — compute only on explicit user trigger."""

    def __init__(self) -> None:
        ensure_dpi_awareness()
        super().__init__()
        self.title("屏幕—画布坐标转换")
        self.geometry("520x260")
        self.minsize(480, 220)
        self.resizable(False, False)

        self._tx: TransformCaptureSession | None = None
        self._select: RoiSelectWindow | None = None
        self._overlay = WorkspaceOverlayController(master=self)
        self._busy = False

        self._build()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self.bind("<F2>", lambda e: self.start_capture())
        self.bind("<F5>", lambda e: self.start_compute())
        self.bind("<Escape>", lambda e: self.hide_overlay())

    def _build(self) -> None:
        pad = ttk.Frame(self, padding=16)
        pad.pack(fill=tk.BOTH, expand=True)

        title = ttk.Label(pad, text="屏幕 ↔ 画布坐标转换", font=("Segoe UI", 14, "bold"))
        title.pack(anchor=tk.W)

        tip = ttk.Label(
            pad,
            text="1) 冻结桌面后先框选工作区（蓝）  2) 再框选导航器（橙）\n"
            "3) 两个 ROI 就绪后点「开始计算」——不会自动求解矩阵",
            justify=tk.LEFT,
        )
        tip.pack(anchor=tk.W, pady=(8, 12))

        row = ttk.Frame(pad)
        row.pack(fill=tk.X)
        ttk.Button(row, text="新建坐标转换 (F2)", command=self.start_capture).pack(side=tk.LEFT)
        self.btn_compute = ttk.Button(
            row, text="开始计算 (F5)", command=self.start_compute, state=tk.DISABLED
        )
        self.btn_compute.pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(row, text="隐藏覆盖层 (Esc)", command=self.hide_overlay).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(row, text="退出", command=self._on_close).pack(side=tk.RIGHT)

        self.status = tk.StringVar(value="就绪：切换到绘画软件后点「新建坐标转换」")
        ttk.Label(pad, textvariable=self.status, wraplength=480).pack(anchor=tk.W, pady=(14, 0))

    def _refresh_compute_button(self) -> None:
        ready = self._tx is not None and self._tx.both_rois_ready() and not self._busy
        self.btn_compute.configure(state=tk.NORMAL if ready else tk.DISABLED)

    def hide_overlay(self) -> None:
        self._overlay.hide()
        self.status.set("已隐藏覆盖层")

    def start_capture(self) -> None:
        if self._busy:
            return
        self._overlay.hide()
        if self._tx is not None:
            self._tx.invalidate()
            self._tx = None
        self._refresh_compute_button()

        self.status.set("正在冻结桌面…")
        self.update_idletasks()
        self.withdraw()
        self.update_idletasks()
        dbg.stage("capture.withdraw_done")
        time.sleep(0.12)

        try:
            dbg.stage("capture.freeze_begin")
            session, cap = freeze_desktop_session()
            dbg.stage(
                "capture.freeze_done",
                w=cap.width,
                h=cap.height,
                ox=cap.origin_x,
                oy=cap.origin_y,
            )
        except Exception as e:
            self.deiconify()
            self.lift()
            self.status.set(f"截屏失败: {e}")
            messagebox.showerror("截屏失败", str(e))
            return

        self._tx = wrap_transform_session(session)
        self._tx.state = TransformSessionState.SELECTING_WORKSPACE_ROI
        self.status.set(
            f"已冻结 {cap.width}×{cap.height} @({cap.origin_x},{cap.origin_y}) — 请框选工作区"
        )
        dbg.stage("capture.open_workspace_roi_ui")
        self._select = RoiSelectWindow(
            self,
            session,
            on_done=self._on_workspace_roi,
            on_cancel=self._on_roi_cancelled,
            hint="拖拽框选工作区大致范围（蓝框）· Esc 取消",
            outline="#4ea1ff",
        )

    def _on_roi_cancelled(self) -> None:
        if self._tx is not None:
            self._tx.invalidate()
            self._tx = None
        self._select = None
        self._refresh_compute_button()
        self.deiconify()
        self.lift()
        self.status.set("已取消框选")

    def _on_workspace_roi(self, roi: IntRect) -> None:
        self._select = None
        tx = self._tx
        if tx is None or not tx.active:
            self.deiconify()
            self.status.set("会话已失效，请重新开始")
            return

        tx.set_workspace_roi(roi)
        # MAY correct workspace immediately, MUST NOT solve final matrix (§3.1)
        self._busy = True
        self.deiconify()
        self.lift()
        self.status.set("工作区 ROI 已记录，正在机器修正（尚未求解矩阵）…")
        self.update_idletasks()
        dbg.stage("workspace_roi.recorded", roi=roi.as_tuple())

        bgra = tx.frozen_capture_bgra
        h, w = bgra.shape[:2]
        origin = tx.capture_to_screen
        dbg.stage("workspace_detect.begin", frame=f"{w}x{h}")
        out = detect_workspace_rect(
            DetectionInput(
                capture_buffer=bgra,
                capture_width=w,
                capture_height=h,
                stride=w * 4,
                user_roi_capture_px=roi,
                dpi_scale_x=tx.monitor_descriptors[0].dpi_scale_x,
                dpi_scale_y=tx.monitor_descriptors[0].dpi_scale_y,
                pixel_format=PixelFormat.BGRA,
                capture_id=tx.capture_id,
                capture_origin_screen_physical_x=origin.origin_x,
                capture_origin_screen_physical_y=origin.origin_y,
            )
        )
        self._busy = False
        tx.workspace_detection = out
        dbg.stage(
            "workspace_detect.done",
            status=out.status.value,
            rect=None if out.workspace_rect_capture_px is None else out.workspace_rect_capture_px.as_tuple(),
        )

        if out.status != DetectionStatus.OK:
            self.status.set(
                f"工作区修正失败: {out.status.value} — 仍可继续框选导航器，计算时会重试"
            )
        else:
            r = out.workspace_rect_capture_px
            self.status.set(
                f"工作区已修正 {r.as_tuple() if r else '?'} — 请框选导航器（不会自动计算）"
            )

        tx.state = TransformSessionState.SELECTING_NAVIGATOR_ROI
        existing = [(roi, "#4ea1ff")]
        if out.workspace_rect_capture_px is not None:
            existing.append((out.workspace_rect_capture_px, "#7CFC00"))
        self.withdraw()
        self.update_idletasks()
        dbg.stage("capture.open_navigator_roi_ui")
        self._select = RoiSelectWindow(
            self,
            tx.base,
            on_done=self._on_navigator_roi,
            on_cancel=self._on_roi_cancelled,
            hint="拖拽框选导航器范围（橙框）· 松手后需手动点「开始计算」",
            outline="#ff9a3c",
            existing_rois=existing,
        )

    def _on_navigator_roi(self, roi: IntRect) -> None:
        self._select = None
        tx = self._tx
        if tx is None or not tx.active:
            self.deiconify()
            self.status.set("会话已失效，请重新开始")
            return
        tx.set_navigator_roi(roi)
        dbg.stage("navigator_roi.recorded", roi=roi.as_tuple())
        self.deiconify()
        self.lift()
        self._refresh_compute_button()
        self.status.set(
            "两个 ROI 已就绪 — 矩阵尚未计算。请点击「开始计算」(F5)。"
        )

    def start_compute(self) -> None:
        tx = self._tx
        if tx is None or not tx.both_rois_ready() or self._busy:
            return
        assert tx.workspace_user_roi is not None and tx.navigator_user_roi is not None

        gen = tx.session_generation
        self._busy = True
        self._refresh_compute_button()
        self.status.set("计算中：导航器几何 → 红框补全 → 矩阵求解 → 独立验证…")
        self.update_idletasks()

        origin = tx.capture_to_screen
        t0 = time.perf_counter()
        tx.triggered_at = t0
        tx.state = TransformSessionState.COMPUTING_NAVIGATOR_GEOMETRY
        dbg.stage("compute.begin")

        result = run_transform(
            TransformRequest(
                capture_id=tx.capture_id,
                frozen_capture_buffer=tx.frozen_capture_bgra,
                workspace_user_roi_capture_px=tx.workspace_user_roi,
                navigator_user_roi_capture_px=tx.navigator_user_roi,
                capture_origin_screen_physical_x=origin.origin_x,
                capture_origin_screen_physical_y=origin.origin_y,
                workspace_detection_output=tx.workspace_detection,
                user_triggered=True,
                session_generation=gen,
            )
        )
        ms = (time.perf_counter() - t0) * 1000.0
        self._busy = False
        dbg.stage(
            "compute.done",
            status=result.status.value,
            ms=f"{ms:.0f}",
            message=(result.message or "")[:80],
        )

        # Discard stale async results (§3.2)
        if not tx.active or tx.session_generation != gen:
            self._overlay.hide()
            self.status.set("会话已变更，已丢弃过期计算结果")
            self._refresh_compute_button()
            return

        tx.transform_result = result
        self._refresh_compute_button()

        if result.overlay_scene is not None:
            # Failure scenes use non-green style; success uses Validated green
            ok_show = self._overlay.show_scene(result.overlay_scene, tx.capture_id)
        else:
            ok_show = False
            self._overlay.hide()

        if result.status != TransformStatus.OK:
            tx.state = TransformSessionState.REJECTED
            self.status.set(
                f"失败: {result.status.value} — {result.message}  ({ms:.0f} ms)"
            )
            messagebox.showwarning(
                "坐标转换失败",
                f"状态: {result.status.value}\n原因: {result.message or '未知'}\n"
                f"可重新框选任一 ROI。\n耗时: {ms:.0f} ms",
            )
            return

        val = result.validation
        tx.state = TransformSessionState.VALIDATED
        vstat = val.status.value if val else "?"
        grade = result.red_frame_evidence_grade.value if result.red_frame_evidence_grade else "?"
        style_note = ""
        if val and val.status == ValidationStatus.NAVIGATOR_CONSISTENT:
            style_note = "（黄覆盖：仅导航器一致）"
        elif val and val.status == ValidationStatus.VALIDATED:
            style_note = "（绿覆盖：矩阵已验证）"
        overlay_note = "覆盖层已显示" if ok_show else f"覆盖层失败: {self._overlay.last_error}"
        self.status.set(
            f"成功 validation={vstat} grade={grade}  {ms:.0f} ms {style_note}\n{overlay_note}"
        )

    def _on_close(self) -> None:
        self._overlay.hide()
        if self._tx is not None:
            self._tx.invalidate()
        if self._select is not None:
            try:
                self._select.destroy()
            except Exception:
                pass
        self.destroy()


def main() -> None:
    dbg.reset_log()
    dbg.stage("app.main_begin")
    ensure_dpi_awareness()
    app = App()
    dbg.stage("app.mainloop")
    app.mainloop()
    dbg.stage("app.exit")


if __name__ == "__main__":
    main()
