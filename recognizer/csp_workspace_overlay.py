"""Show CSP dock-layout candidates only when saved and live window placement match.

Run with ``py recognizer/csp_workspace_overlay.py``. The overlay is click-through;
close it with the control window's close button or Escape.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
import queue
import sqlite3
import sys
import threading
import time
import tkinter as tk
from collections import Counter
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
    dpi = 96 * _ratio(tree["dockplacementmainframexscale"])
    return bool(tree["dockplacementmainframemaximize"]), (
        rect["left"], rect["top"], rect["right"], rect["bottom"]
    ), dpi


def _ratio(value: dict) -> float:
    return value["numerator"] / value["denominator"]


def _half_up(value: float) -> int:
    # CSP rounds rescaled sizes half up: 345 at 150% becomes 403 at 175%.
    return math.floor(value + 0.5)


def same_restore_rect(saved: tuple[int, int, int, int], live: tuple[int, int, int, int], ratio: float) -> bool:
    """CSP keeps the restore origin and rescales only its size with the DPI."""
    if abs(ratio - 1) < 1e-6:
        return saved == live
    return (saved[:2] == live[:2]
            and abs(_half_up((saved[2] - saved[0]) * ratio) - (live[2] - live[0])) <= 2
            and abs(_half_up((saved[3] - saved[1]) * ratio) - (live[3] - live[1])) <= 2)


class _Scaler:
    """Saved dock sizes are physical pixels at the DPI ratio stored beside each size."""

    def __init__(self, tree: dict, live_scale: float | None):
        self.saved = _ratio(tree["dockplacementmainframeyscale"])
        self.live = live_scale or self.saved
        self.converted = abs(self.live - self.saved) > 1e-6

    def size(self, place: dict, axis: str) -> int:
        stored = place.get(f"dockplacebase{axis}scale")
        saved = _ratio(stored) if stored else self.saved
        return _half_up(place["dockplacebasesize"]["width" if axis == "x" else "height"] * self.live / saved)

    def logical(self, value: float) -> int:
        return _half_up(value * self.live)


# Menu bar plus command bar above the dock columns, in 96-DPI pixels. Measured
# 72 px at 150% and 84 px at 175%; only used when the saved stack overflows.
CHROME_HEIGHT_96 = 48
# Collapse bar above each side column's stack, in 96-DPI pixels (35 px at 175%).
COLUMN_HEADER_96 = 20
BOUNDARY_TOLERANCE = 3
MIN_SQUEEZED_HEIGHT = 20
SQUEEZE_RETRY_SECONDS = 3


class WindowSnapshot:
    """One PrintWindow render of the CSP window; works while CSP is covered."""

    def __init__(self, hwnd: int):
        user32, gdi32 = ctypes.WinDLL("user32"), ctypes.WinDLL("gdi32")
        user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
        user32.GetWindowDC.argtypes = [wintypes.HWND]
        user32.GetWindowDC.restype = wintypes.HDC
        user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
        user32.PrintWindow.argtypes = [wintypes.HWND, wintypes.HDC, wintypes.UINT]
        gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
        gdi32.CreateCompatibleDC.restype = wintypes.HDC
        gdi32.CreateCompatibleBitmap.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int]
        gdi32.CreateCompatibleBitmap.restype = wintypes.HBITMAP
        gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HGDIOBJ]
        gdi32.SelectObject.restype = wintypes.HGDIOBJ
        gdi32.GetDIBits.argtypes = [wintypes.HDC, wintypes.HBITMAP, wintypes.UINT, wintypes.UINT,
                                    ctypes.c_void_p, ctypes.c_void_p, wintypes.UINT]
        gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
        gdi32.DeleteDC.argtypes = [wintypes.HDC]
        rect = wintypes.RECT()
        if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
            raise OSError("GetWindowRect 失败")
        self.left, self.top = rect.left, rect.top
        self.width, self.height = rect.right - rect.left, rect.bottom - rect.top
        window_dc = user32.GetWindowDC(hwnd)
        memory_dc = gdi32.CreateCompatibleDC(window_dc)
        bitmap = gdi32.CreateCompatibleBitmap(window_dc, self.width, self.height)
        previous = gdi32.SelectObject(memory_dc, bitmap)
        try:
            # PW_RENDERFULLCONTENT renders CSP's own pixels, never covering windows.
            if not user32.PrintWindow(hwnd, memory_dc, 2):
                raise OSError("PrintWindow 失败")
            gdi32.SelectObject(memory_dc, previous)
            header = (ctypes.c_uint32 * 10)()
            header[0], header[1], header[2] = 40, self.width, ctypes.c_uint32(-self.height).value
            header[3] = 1 | (32 << 16)
            self.pixels = ctypes.create_string_buffer(self.width * self.height * 4)
            if gdi32.GetDIBits(memory_dc, bitmap, 0, self.height, self.pixels, header, 0) != self.height:
                raise OSError("GetDIBits 失败")
        finally:
            gdi32.DeleteObject(bitmap)
            gdi32.DeleteDC(memory_dc)
            user32.ReleaseDC(hwnd, window_dc)
        self.pixels = bytearray(self.pixels.raw)
        self.pixels[3::4] = bytes(len(self.pixels) // 4)

    def row(self, x0: int, x1: int, y: int) -> bytes:
        """BGR0 pixels of screen row ``y`` between ``x0`` and ``x1``."""
        bx0, bx1, by = x0 - self.left, x1 - self.left, y - self.top
        if not (0 <= by < self.height and 0 <= bx0 < bx1 <= self.width):
            return b""
        start = (by * self.width + bx0) * 4
        return bytes(self.pixels[start:start + (bx1 - bx0) * 4])


def _dominant(row: bytes) -> tuple[bytes, int]:
    color, count = Counter(row[i:i + 4] for i in range(0, len(row), 4)).most_common(1)[0]
    return color, count


def _find_boundary(snapshot, x0: int, x1: int, expected: int) -> tuple[int, bytes] | None:
    """A booth starts with a full-width uniform separator row above a header of another color."""
    for y in sorted(range(expected - BOUNDARY_TOLERANCE, expected + BOUNDARY_TOLERANCE + 1),
                    key=lambda value: abs(value - expected)):
        row = snapshot.row(x0, x1, y)
        if not row or row != row[:4] * (len(row) // 4) or snapshot.row(x0, x1, y - 1) == row:
            continue
        below = y + 1
        while below < y + 4 and snapshot.row(x0, x1, below) == row:
            below += 1
        header = snapshot.row(x0, x1, below)
        if not header:
            continue
        color, count = _dominant(header)
        if color != row[:4] and count >= 0.9 * (len(header) // 4):
            return y, row[:4]
    return None


def _squeezed_stack(label: str, heights: list[int], top: int, bottom: int,
                    column_x: int, column_width: int, snapshot_factory) -> list[tuple[int, int]]:
    """CSP keeps every booth but one at its saved height; the pixels decide which one shrank."""
    if snapshot_factory is None:
        raise ValueError(f"{label}面板总高 {sum(heights)} px 超出可用高度 {bottom - top} px")
    snapshot = snapshot_factory()
    x0, x1 = column_x + 4, column_x + column_width - 4
    matches = {}
    for squeezed in range(len(heights)):
        tops = [top + sum(heights[:i]) if i <= squeezed else bottom - sum(heights[i:])
                for i in range(len(heights))]
        ends = tops[1:] + [bottom]
        if ends[squeezed] - tops[squeezed] < MIN_SQUEEZED_HEIGHT:
            continue
        found = [_find_boundary(snapshot, x0, x1, expected) for expected in tops]
        if all(found) and len({separator for _, separator in found}) == 1:
            measured = tuple(y for y, _ in found)
            matches[measured] = squeezed
    if len(matches) != 1:
        raise ValueError(
            f"{label}面板总高 {sum(heights)} px 超出可用高度 {bottom - top} px，"
            f"截图中{'找不到' if not matches else '有多种'}符合“仅一个面板被压缩”的分界，"
            "请在 CSP 调整面板高度后保存工作区")
    tops = list(next(iter(matches)))
    return [(y, end - y) for y, end in zip(tops, tops[1:] + [bottom])]


def panel_rectangles(tree: dict, client: tuple[int, int, int, int], live_scale: float | None = None,
                     snapshot_factory=None) -> dict[str, tuple[int, int, int, int]]:
    """Convert saved dock sizes to screen boxes; borders are visual candidates.

    ``live_scale`` is the window's DPI / 96. Saved sizes are rescaled to it the
    way CSP does. When a rescaled side stack no longer fits, CSP shrinks one
    booth; ``snapshot_factory`` supplies the window pixels that identify it.
    """
    x, y, width, height = client
    scaler = _Scaler(tree, live_scale)
    root = tree["dockplacementpalette"]["mainframe"]["dock"]
    groups = root["subviews"]["_items"]
    places = root["dockplaces"]["_items"]
    widths = [scaler.size(entry, "x") for entry in places]
    if len(widths) != 5 or sum(widths) <= 0:
        raise ValueError("不支持当前停靠列结构")
    if abs(sum(widths) - width) > 2:
        # The saved widths are physical pixels. Reject a different live layout,
        # unless the DPI changed: then only the canvas column takes up the rest.
        if not scaler.converted:
            raise ValueError(f"停靠宽度不一致：配置 {sum(widths)}，客户区 {width}")
        widths[2] = width - (sum(widths) - widths[2])
        if widths[2] < 10:
            raise ValueError(f"换算到当前缩放后侧栏总宽超出客户区：{sum(widths) - widths[2]} / {width}")

    def sizes(dock: dict) -> list[int]:
        return [scaler.size(entry, "y") for entry in dock["dockplaces"]["_items"]]

    # Side columns: any number of booths each. A panel is located by the booth whose selected tab
    # it is, wherever that booth sits; a panel that is not the selected tab of exactly one visible
    # booth (another tab is in front, it was torn off as a floating window, or it is hidden) simply
    # has no dock region. Floating windows are found live, not here.
    side_columns = {}
    for column in (1, 4):
        group = groups[column]
        docks = (group.get("subviews") or {}).get("_items") or []
        if not docks or "dockplaces" not in docks[0]:
            continue
        booths, heights = docks[0]["subviews"]["_items"], sizes(docks[0])
        if not booths or len(booths) != len(heights):
            raise ValueError("不支持当前面板堆叠结构")
        side_columns[column] = (group, booths, heights)
    if set(side_columns) != {1, 4}:
        raise ValueError("不支持当前停靠列结构")
    left_heights, right_heights = side_columns[1][2], side_columns[4][2]
    col_x = [x]
    for column_width in widths:
        col_x.append(col_x[-1] + column_width)
    column_heights = sizes(root)
    bottom = y + height
    # The dock starts below the menu and command bars. The saved column height
    # can be stale (seen live: 1227 px saved for a 1455 px dock), so it is only
    # used when the command bar is hidden and the chrome height is unknown.
    chrome = scaler.logical(CHROME_HEIGHT_96) if tree.get("dockplacementcommandbarvisible") == 1 else None
    if chrome is not None:
        dock_height = height - chrome
    else:
        dock_height = max(column_heights)
        if scaler.converted and dock_height > height:
            raise ValueError("命令栏隐藏时无法推算缩放后的停靠区顶部，请在 CSP 保存工作区")
    dock_top = bottom - dock_height

    def saved_header(column: int, heights: list[int]) -> int | None:
        # The collapse bar is the saved column height minus its stack, but only
        # while that saved height is the live dock height and the stack fits.
        remainder = column_heights[column] - sum(heights)
        return remainder if abs(column_heights[column] - dock_height) <= 2 and remainder > 0 else None

    remainders = [r for r in (saved_header(1, left_heights), saved_header(4, right_heights)) if r]
    column_header = remainders[0] if remainders else scaler.logical(COLUMN_HEADER_96)

    def stack(label: str, column: int, heights: list[int]) -> list[tuple[int, int]]:
        # Side stacks are bottom aligned in the CSP dock area, below the column's collapse bar.
        header = saved_header(column, heights) or column_header
        if sum(heights) <= bottom - (dock_top + header) + 2:
            tops = [bottom - sum(heights[i:]) for i in range(len(heights))]
            return list(zip(tops, heights))
        return _squeezed_stack(label, heights, dock_top + header, bottom,
                               col_x[column], widths[column], snapshot_factory)

    stacks = {1: stack("左栏", 1, left_heights), 4: stack("右栏", 4, right_heights)}

    def selected_kind(group: dict, booth: dict) -> str | None:
        tabs = booth["subviews"]["_items"]
        selected = booth.get("currentindex", -1)
        if not (group.get("visible", 1) and booth.get("visible") and 0 <= selected < len(tabs)):
            return None
        return tabs[selected].get("_type")

    def located(wanted: set[str]) -> tuple[int, int, int, int] | None:
        slots = [(column, index) for column, (group, booths, _) in side_columns.items()
                 for index, booth in enumerate(booths) if selected_kind(group, booth) in wanted]
        if len(slots) != 1:
            return None
        column, index = slots[0]
        top, booth_height = stacks[column][index]
        return col_x[column], top, widths[column], booth_height

    # The central canvas has no palettekind node; the area above the saved
    # timeline is its candidate.
    center = groups[2]
    timeline = center["subviews"]["_items"][-1]
    timeline_height = sizes(center)[-1] if timeline.get("visible") else 0
    result = {"画布视口": (col_x[2], dock_top, widths[2], bottom - dock_top - timeline_height)}
    # Locate the selected color tenant from the saved configuration, not its
    # localized title or an assumed tab index. All color tabs share one booth.
    color_kinds = {"palettekindcolorcircle", "palettekindcolorset", "palettekindcolorslider",
                   "palettekindmix", "palettekindcolorhistory", "palettekindcolorguide", "palettekindcolorblend"}
    for label, wanted in (("笔刷属性", {"palettekindtooloption"}), ("导航器", {"palettekindnavigator"}),
                          ("图层", {"palettekindlayerorder"}), ("工具组", {"palettekindsubtool", "palettekindtoolgroup"}),
                          ("色彩", color_kinds)):
        region = located(wanted)
        if region is not None:
            result[label] = region
    tool_dock = groups[0]["subviews"]["_items"][0]
    tool_booths = tool_dock["subviews"]["_items"]
    if groups[0].get("visible") and any(
            booth.get("visible") and 0 <= booth.get("currentindex", -1) < len(booth["subviews"]["_items"])
            and booth["subviews"]["_items"][booth["currentindex"]].get("_type") == "palettekindtool"
            for booth in tool_booths):
        # The tool column spans the whole dock; its saved height can be stale.
        result["工具栏"] = (col_x[0], dock_top, widths[0], dock_height)
    if "色彩" in result:
        # Both readers inspect the same booth; fresh pixels decide which tab is
        # visible. The alias keeps geometry stable when the user manually changes
        # color palettes and never treats a saved selection as live input proof.
        result["色彩滑块"] = result["色彩"]
    if any(w < 10 or h < 10 or rx < x or ry < y or rx + w > x + width or ry + h > y + height
           for rx, ry, w, h in result.values()):
        raise ValueError("解析出的面板区域超出 CSP 客户区")
    return result


# Squeezed layouts need a window render; polling callers reuse its verdict
# until the window, its DPI or the saved dock changes.
_SQUEEZE_CACHE: dict[tuple, tuple[dict | None, str | None, float]] = {}


def _live_regions(tree: dict, client: tuple[int, int, int, int], hwnd: int, dpi: float) -> dict:
    key = (hwnd, client, dpi, hashlib.sha1(json.dumps(tree, sort_keys=True).encode()).hexdigest())
    cached = _SQUEEZE_CACHE.get(key)
    if cached:
        regions, error, stamp = cached
        if regions is not None:
            return dict(regions)
        if time.monotonic() - stamp < SQUEEZE_RETRY_SECONDS:
            raise ValueError(error)
    snapshots = []

    def snapshot_factory():
        if not snapshots:
            snapshots.append(WindowSnapshot(hwnd))
        return snapshots[0]

    try:
        regions = panel_rectangles(tree, client, dpi / 96, snapshot_factory)
    except ValueError as exc:
        if snapshots:
            _SQUEEZE_CACHE[key] = (None, str(exc), time.monotonic())
        raise
    if snapshots:
        if len(_SQUEEZE_CACHE) > 32:
            _SQUEEZE_CACHE.clear()
        _SQUEEZE_CACHE[key] = (regions, None, 0.0)
    return dict(regions)


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
    # The live DPI is Windows' effective display scale for CSP's monitor.
    if not same_restore_rect(saved_rect, actual_rect, window.dpi / saved_dpi):
        raise ValueError(f"窗口还原矩形不一致：配置 {saved_rect}（DPI {saved_dpi:g}），"
                         f"窗口 {actual_rect}（DPI {window.dpi:g}）")
    client = window.client_rect.to_list()
    return window.hwnd, {
        "status": "match",
        "dock_file": str(path),
        "hwnd": window.hwnd,
        "maximized": actual_max,
        "saved_restore_rect": saved_rect,
        "saved_dpi": saved_dpi,
        "dpi": window.dpi,
        "client_rect": client,
        "regions": _live_regions(tree, tuple(client), window.hwnd, window.dpi),
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
