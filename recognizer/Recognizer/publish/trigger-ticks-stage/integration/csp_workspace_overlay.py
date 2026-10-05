"""Show CSP dock-layout candidates only when saved and live window placement match.

Run with ``py recognizer/csp_workspace_overlay.py``. The overlay is click-through;
close it with the control window's close button or Escape.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import queue
import sqlite3
import sys
import threading
import time
import tkinter as tk
from ctypes import wintypes
from pathlib import Path
from tkinter import messagebox

from csp_workspace_layout import parse_blob

sys.path.insert(0, str(Path(__file__).parent / "csp_panel_validator"))
from csp_panel_validator.window_service import (  # noqa: E402
    current_csp_window,
    enable_per_monitor_dpi_awareness,
    inspect_window,
)


class WindowPlacement(ctypes.Structure):
    _fields_ = [
        ("length", wintypes.UINT),
        ("flags", wintypes.UINT),
        ("showCmd", wintypes.UINT),
        ("ptMinPosition", wintypes.POINT),
        ("ptMaxPosition", wintypes.POINT),
        ("rcNormalPosition", wintypes.RECT),
    ]


def find_dock_file() -> Path:
    base = Path(os.environ["APPDATA"]) / "CELSYS" / "CLIPStudioPaint"
    candidates = [p for p in base.glob("*/Placement/dock") if p.is_file()]
    if not candidates:
        raise FileNotFoundError(f"找不到 CSP Placement/dock：{base}")
    return max(candidates, key=lambda path: path.stat().st_mtime_ns)


def load_dock(path: Path) -> dict:
    with sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True, timeout=0.2) as db:
        row = db.execute("SELECT dockplacementbasicdata FROM dockplacement LIMIT 1").fetchone()
    if not row or not row[0]:
        raise ValueError("dock 文件没有布局数据")
    return parse_blob(row[0])


def live_placement(hwnd: int) -> tuple[bool, tuple[int, int, int, int]]:
    user32 = ctypes.windll.user32
    user32.GetWindowPlacement.argtypes = [wintypes.HWND, ctypes.POINTER(WindowPlacement)]
    user32.GetWindowPlacement.restype = wintypes.BOOL
    placement = WindowPlacement()
    placement.length = ctypes.sizeof(WindowPlacement)
    if not user32.GetWindowPlacement(hwnd, ctypes.byref(placement)):
        raise OSError("GetWindowPlacement 失败")
    rect = placement.rcNormalPosition
    return placement.showCmd == 3, (rect.left, rect.top, rect.right, rect.bottom)


def saved_placement(tree: dict) -> tuple[bool, tuple[int, int, int, int], float]:
    rect = tree["dockplacementmainframerect"]
    ratio = tree["dockplacementmainframexscale"]
    dpi = 96 * ratio["numerator"] / ratio["denominator"]
    return bool(tree["dockplacementmainframemaximize"]), (
        rect["left"], rect["top"], rect["right"], rect["bottom"]
    ), dpi


def panel_rectangles(tree: dict, client: tuple[int, int, int, int]) -> dict[str, tuple[int, int, int, int]]:
    """Convert saved dock sizes to screen boxes; borders are visual candidates."""
    x, y, width, height = client
    root = tree["dockplacementpalette"]["mainframe"]["dock"]
    groups = root["subviews"]["_items"]
    widths = [entry["dockplacebasesize"]["width"] for entry in root["dockplaces"]["_items"]]
    if len(widths) != 5 or sum(widths) <= 0:
        raise ValueError("不支持当前停靠列结构")
    # The saved widths are physical pixels. Reject a different live layout.
    if abs(sum(widths) - width) > 2:
        raise ValueError(f"停靠宽度不一致：配置 {sum(widths)}，客户区 {width}")

    def sizes(dock: dict) -> list[int]:
        return [entry["dockplacebasesize"]["height"] for entry in dock["dockplaces"]["_items"]]

    def kinds(booth: dict) -> set[str]:
        return {entry.get("_type", "") for entry in booth["subviews"]["_items"]}

    left_dock = groups[1]["subviews"]["_items"][0]
    right_dock = groups[4]["subviews"]["_items"][0]
    left_booths = left_dock["subviews"]["_items"]
    right_booths = right_dock["subviews"]["_items"]
    if "palettekindtooloption" not in kinds(left_booths[1]):
        raise ValueError("找不到笔刷属性面板停靠位")
    if "palettekindnavigator" not in kinds(right_booths[0]):
        raise ValueError("找不到导航器面板停靠位")
    if "palettekindlayerorder" not in kinds(right_booths[2]):
        raise ValueError("找不到图层面板停靠位")
    if not all(booth.get("visible") for booth in (left_booths[1], right_booths[0], right_booths[2])):
        raise ValueError("目标面板在保存的布局中不可见")
    if not all(booth.get("currentindex", -1) == 0 for booth in (left_booths[1], right_booths[0], right_booths[2])):
        raise ValueError("目标面板标签未被选中")

    left_heights, right_heights = sizes(left_dock), sizes(right_dock)
    if len(left_heights) != 3 or len(right_heights) != 3:
        raise ValueError("不支持当前面板堆叠结构")
    # Locate the selected color tenant from the saved configuration, not its
    # localized title or an assumed tab index. All color tabs share one booth.
    color_kinds = {"palettekindcolorcircle", "palettekindcolorset", "palettekindcolorslider",
                   "palettekindmix", "palettekindcolorhistory", "palettekindcolorguide", "palettekindcolorblend"}
    color_slots = []
    for column, container, booths, heights in (
        (1, groups[1], left_booths, left_heights),
        (4, groups[4], right_booths, right_heights),
    ):
        for index, booth in enumerate(booths):
            tabs = booth["subviews"]["_items"]
            selected = booth.get("currentindex", -1)
            if (container.get("visible") and booth.get("visible") and
                    0 <= selected < len(tabs) and tabs[selected].get("_type") in color_kinds):
                color_slots.append((column, index, heights))
    if len(color_slots) != 1:
        raise ValueError("需要一个可见且已选中色彩标签的停靠区域")
    col_x = [x]
    for saved_width in widths:
        col_x.append(col_x[-1] + saved_width)
    # Side stacks are bottom aligned in the CSP dock area. The central canvas
    # has no palettekind node; the area above the saved timeline is its candidate.
    left_top = y + height - sum(left_heights)
    right_top = y + height - sum(right_heights)
    dock_height = max(entry["dockplacebasesize"]["height"] for entry in root["dockplaces"]["_items"])
    canvas_top = y + height - dock_height
    center = groups[2]
    timeline = center["subviews"]["_items"][-1]
    timeline_height = sizes(center)[-1] if timeline.get("visible") else 0
    result = {
        "笔刷属性": (col_x[1], left_top + left_heights[0], widths[1], left_heights[1]),
        "画布视口": (col_x[2], canvas_top, widths[2], height - (canvas_top - y) - timeline_height),
        "导航器": (col_x[4], right_top, widths[4], right_heights[0]),
        "图层": (col_x[4], right_top + right_heights[0] + right_heights[1], widths[4], right_heights[2]),
    }
    group_booth = left_booths[0]
    group_tabs = group_booth["subviews"]["_items"]
    group_selected = group_booth.get("currentindex", -1)
    if group_booth.get("visible") and 0 <= group_selected < len(group_tabs) and group_tabs[group_selected].get("_type") in {"palettekindsubtool", "palettekindtoolgroup"}:
        result["工具组"] = (col_x[1], left_top, widths[1], left_heights[0])
    tool_dock = groups[0]["subviews"]["_items"][0]
    tool_booths = tool_dock["subviews"]["_items"]
    if groups[0].get("visible") and any(
            booth.get("visible") and 0 <= booth.get("currentindex", -1) < len(booth["subviews"]["_items"])
            and booth["subviews"]["_items"][booth["currentindex"]].get("_type") == "palettekindtool"
            for booth in tool_booths):
        tool_height = root["dockplaces"]["_items"][0]["dockplacebasesize"]["height"]
        result["工具栏"] = (col_x[0], y + height - tool_height, widths[0], tool_height)
    color_column, color_index, color_heights = color_slots[0]
    result["色彩"] = (col_x[color_column], y + height - sum(color_heights) + sum(color_heights[:color_index]),
                      widths[color_column], color_heights[color_index])
    if any(w < 10 or h < 10 or rx < x or ry < y or rx + w > x + width or ry + h > y + height
           for rx, ry, w, h in result.values()):
        raise ValueError("解析出的面板区域超出 CSP 客户区")
    return result


def inspect(path: Path, hwnd: int | None = None) -> tuple[int, dict]:
    tree = load_dock(path)
    window = inspect_window(hwnd) if hwnd else current_csp_window()
    if window is None or not window.visible or window.minimized:
        raise ValueError("找不到可见的 CSP 主窗口")
    if window.exe_name.lower() != "clipstudiopaint.exe":
        raise ValueError("HWND 不属于 CLIPStudioPaint.exe")
    saved_max, saved_rect, saved_dpi = saved_placement(tree)
    actual_max, actual_rect = live_placement(window.hwnd)
    if saved_max != actual_max:
        raise ValueError(f"最大化状态不一致：配置 {int(saved_max)}，窗口 {int(actual_max)}")
    if saved_rect != actual_rect:
        raise ValueError(f"窗口还原矩形不一致：配置 {saved_rect}，窗口 {actual_rect}")
    if abs(saved_dpi - window.dpi) > 1:
        raise ValueError(f"DPI 不一致：配置 {saved_dpi:g}，窗口 {window.dpi:g}")
    client = window.client_rect.to_list()
    return window.hwnd, {
        "status": "match",
        "dock_file": str(path),
        "hwnd": window.hwnd,
        "maximized": actual_max,
        "saved_restore_rect": saved_rect,
        "client_rect": client,
        "regions": panel_rectangles(tree, tuple(client)),
    }


def border_strips(rect, thickness=4):
    x, y, width, height = rect
    return [(x, y, width, thickness), (x, y + height - thickness, width, thickness),
            (x, y, thickness, height), (x + width - thickness, y, thickness, height)]


class Overlay:
    """Four narrow windows per panel leave panel interiors entirely uncovered."""

    def __init__(self, path: Path, hwnd: int | None):
        self.path, self.target_hwnd = path, hwnd
        self.root = tk.Tk()
        self.root.title("CSP 工作区覆盖层")
        self.root.geometry("460x150")
        self.root.attributes("-topmost", True)
        self.status = tk.StringVar(value="正在核对工作区配置与窗口……")
        tk.Label(self.root, textvariable=self.status, wraplength=430, justify="left").pack(padx=12, pady=12)
        tk.Button(self.root, text="关闭覆盖层", command=self.close).pack(pady=4)
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.root.bind("<Escape>", lambda event: self.close())
        self.stop = threading.Event()
        self.results = queue.Queue()
        self.strips = []
        self.last_result = time.monotonic()
        self.last_signature = None
        self.user32 = ctypes.WinDLL("user32", use_last_error=True)
        self.user32.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
        self.user32.GetAncestor.restype = wintypes.HWND
        self.user32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
        self.user32.GetWindowLongW.restype = ctypes.c_long
        self.user32.SetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_long]
        self.user32.SetLayeredWindowAttributes.argtypes = [wintypes.HWND, wintypes.DWORD, wintypes.BYTE, wintypes.DWORD]
        self.user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                                           ctypes.c_int, ctypes.c_int, wintypes.UINT]
        self.user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
        self.worker = threading.Thread(target=self.read_loop, daemon=True)
        self.worker.start()
        self.root.after(50, self.refresh)

    def read_loop(self):
        while not self.stop.is_set():
            try:
                hwnd, result = inspect(self.path, self.target_hwnd)
                self.target_hwnd = hwnd
                self.results.put((time.monotonic(), result, None))
            except Exception as exc:
                self.results.put((time.monotonic(), None, str(exc)))
            self.stop.wait(0.5)

    def hide_borders(self):
        for window, hwnd in self.strips:
            self.user32.ShowWindow(hwnd, 0)
        self.last_signature = None

    def make_strip(self, color):
        window = tk.Toplevel(self.root)
        window.withdraw()
        window.overrideredirect(True)
        window.configure(bg=color)
        window.geometry("1x1+0+0")
        window.update_idletasks()
        # Tk's winfo_id is the inner HWND. Styles belong on the wrapper.
        hwnd = self.user32.GetAncestor(window.winfo_id(), 2)
        self.strips.append((window, hwnd))
        style = self.user32.GetWindowLongW(hwnd, -20)
        self.user32.SetWindowLongW(hwnd, -20, style | 0x80000 | 0x20 | 0x80 | 0x8000000)
        if not self.user32.SetLayeredWindowAttributes(hwnd, 0, 255, 2):
            raise ctypes.WinError(ctypes.get_last_error())
        return hwnd

    def draw(self, regions):
        signature = tuple((label, tuple(rect)) for label, rect in regions.items())
        if signature == self.last_signature:
            return
        if self.strips and len(self.strips) != len(regions) * 4:
            raise ValueError("覆盖层边框数量不一致")
        index = 0
        for label, rect in regions.items():
            color = "#ffff00" if label == "笔刷属性" else "#00ff40"
            for x, y, width, height in border_strips(rect):
                hwnd = self.strips[index][1] if index < len(self.strips) else self.make_strip(color)
                # Position the real top-level HWND; do not activate it.
                if not self.user32.SetWindowPos(hwnd, -1, x, y, width, height, 0x0010 | 0x0040):
                    raise ctypes.WinError(ctypes.get_last_error())
                index += 1
        self.last_signature = signature

    def refresh(self):
        latest = None
        while not self.results.empty():
            latest = self.results.get_nowait()
        try:
            if latest:
                self.last_result, result, error = latest
                if error:
                    self.hide_borders()
                    self.status.set("显示失败：" + error)
                elif time.monotonic() - self.last_result <= 2:
                    self.draw(result["regions"])
                    self.status.set("窗口匹配。黄色：笔刷属性；绿色：画布视口、导航器、图层、色彩。")
            if time.monotonic() - self.last_result > 2:
                self.hide_borders()
                self.status.set("显示失败：窗口或配置读取超时。可以直接关闭此窗口。")
        except Exception as exc:
            self.hide_borders()
            self.status.set("显示失败：" + str(exc))
        if not self.stop.is_set():
            self.root.after(50, self.refresh)

    def close(self):
        self.stop.set()
        self.root.destroy()

    def run(self):
        try:
            self.root.mainloop()
        finally:
            self.stop.set()
            try:
                self.root.destroy()
            except tk.TclError:
                pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dock", type=Path, help="Override automatic Placement/dock discovery")
    parser.add_argument("--hwnd", type=lambda value: int(value, 0), help="CSP main HWND, decimal or 0x-prefixed")
    parser.add_argument("--diagnose", action="store_true", help="Print match result without showing an overlay")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("仅支持 Windows")
    enable_per_monitor_dpi_awareness()
    try:
        path = args.dock or find_dock_file()
        if args.diagnose:
            hwnd, result = inspect(path, args.hwnd)
    except Exception as exc:
        if args.diagnose:
            print(json.dumps({"status": "failed", "reason": str(exc)}, ensure_ascii=False, indent=2))
        else:
            messagebox.showerror("CSP 工作区匹配失败", str(exc))
        return 1
    if args.diagnose:
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    Overlay(path, args.hwnd).run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
