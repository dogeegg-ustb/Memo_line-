"""Every palette in the saved CSP workspace (Placement/dock), with display names.

For a floating window on the virtual screen the workspace only supplies names:
the window itself is found and captured live through its HWND.
"""
from __future__ import annotations

import math
import re
from dataclasses import dataclass, field
from pathlib import Path

import paths  # noqa: F401
from csp_workspace_overlay import find_dock_file, load_dock

# palettekind -> (简体中文, English). The kind ID stays the authoritative key.
PALETTE_NAMES = {
    "palettekindtool": ("工具", "Tool"),
    "palettekindsubtool": ("辅助工具", "Sub Tool"),
    "palettekindtooloption": ("工具属性", "Tool Property"),
    "palettekindbrushsize": ("笔刷尺寸", "Brush Size"),
    "palettekindtoolslider": ("工具滑块", "Tool Slider"),
    "palettekindrelatedtool": ("工具相关", "Related Tool"),
    "palettekindrelatedcolor": ("颜色相关", "Related Color"),
    "palettekindcolorcircle": ("色环", "Color Wheel"),
    "palettekindcolorset": ("色板", "Color Set"),
    "palettekindmix": ("混色", "Color Mixing"),
    "palettekindcolorhistory": ("颜色历史记录", "Color History"),
    "palettekindcolorslider": ("颜色滑块", "Color Slider"),
    "palettekindcolorguide": ("近似色", "Approximate Color"),
    "palettekindcolorblend": ("中间色", "Intermediate Color"),
    "palettekindnavigator": ("导航器", "Navigator"),
    "palettekindsubview": ("辅助查看", "Sub View"),
    "palettekinditembank": ("道具库", "Item Bank"),
    "palettekindmemoryinformation": ("内存信息", "Memory Information"),
    "palettekindproperty": ("图层属性", "Layer Property"),
    "palettekindlayerfind": ("图层搜索", "Layer Search"),
    "palettekindlighttable": ("光台", "Light Table"),
    "palettekindaligndistribute": ("对齐/分布", "Align/Distribute"),
    "palettekindlayerorder": ("图层", "Layer"),
    "palettekindhistory": ("历史记录", "History"),
    "palettekindaction": ("自动动作", "Auto Action"),
    "palettekindtimeline": ("时间轴", "Timeline"),
    "palettekindmultiview": ("多视图", "Multi View"),
    "palettekindquickaccess": ("快捷访问", "Quick Access"),
    "palettekindlayercomp": ("图层构图", "Layer Comps"),
}
FRAME_TOLERANCE = 8


def palette_name(kind: str) -> str:
    material = re.fullmatch(r"palettekindmaterial(\d+)", kind)
    if material:
        return f"素材[{int(material.group(1)) + 1}]"
    return PALETTE_NAMES.get(kind, (kind.removeprefix("palettekind") or "画布",))[0]


def _scale(value) -> float:
    return value["numerator"] / value["denominator"] if isinstance(value, dict) and value.get("denominator") else 1.0


@dataclass(frozen=True)
class Palette:
    kind: str
    name: str
    place: str      # 停靠 / 浮动 / 图标化
    container: str
    selected: bool  # the tab saved as current; not live evidence
    visible: bool


@dataclass(frozen=True)
class FloatingDefinition:
    index: int
    frame: tuple[int, int, int, int]
    scale: float
    kinds: tuple[str, ...]
    current: str | None

    def name(self) -> str:
        names = [palette_name(k) for k in self.kinds]
        return " | ".join(names) or f"浮动窗口{self.index + 1}"


@dataclass
class Workspace:
    path: Path | None = None
    mtime_ns: int = 0
    palettes: list[Palette] = field(default_factory=list)
    floating: list[FloatingDefinition] = field(default_factory=list)
    error: str | None = None

    def kinds(self) -> list[str]:
        return list(dict.fromkeys(p.kind for p in self.palettes))

    def match_floating(self, rects, dpi: float) -> FloatingDefinition | None:
        """Saved frame rectangles are physical pixels at the stored scale."""
        best, best_error = None, FRAME_TOLERANCE + 1
        for definition in self.floating:
            ratio = (dpi / 96) / definition.scale
            left, top, right, bottom = definition.frame
            expected = (left, top, left + math.floor((right - left) * ratio + 0.5),
                        top + math.floor((bottom - top) * ratio + 0.5))
            for rect in rects:
                error = max(abs(a - b) for a, b in zip(expected, rect))
                if error < best_error:
                    best, best_error = definition, error
        return best

    def match_title(self, title: str) -> str | None:
        text = title.strip().lower()
        if not text:
            return None
        for kind in dict.fromkeys([*self.kinds(), *PALETTE_NAMES]):
            names = {palette_name(kind).lower(), *(n.lower() for n in PALETTE_NAMES.get(kind, ()))}
            if text in names:
                return kind
        return None


def _booths(node):
    if not isinstance(node, dict):
        return
    if node.get("_type") == "PWBooth":
        yield node
        return
    for child in (node.get("subviews") or {}).get("_items", []):
        yield from _booths(child)


def _tabs(booth) -> tuple[list[str], str | None]:
    tabs = [s.get("_type", "") for s in booth["subviews"]["_items"]]
    index = booth.get("currentindex", -1)
    current = tabs[index] if 0 <= index < len(tabs) else (tabs[0] if len(tabs) == 1 else None)
    return [t for t in tabs if t], current or None


def load_workspace(path: Path | None = None) -> Workspace:
    try:
        path = path or find_dock_file()
        mtime = path.stat().st_mtime_ns
        tree = load_dock(path)
    except Exception as ex:
        return Workspace(path=path, error=str(ex))
    palettes: list[Palette] = []
    floating: list[FloatingDefinition] = []
    root = tree["dockplacementpalette"]
    columns = root["mainframe"]["dock"]["subviews"]["_items"]
    for column_index, column in enumerate(columns):
        for booth in _booths(column):
            tabs, current = _tabs(booth)
            for kind in tabs:
                palettes.append(Palette(kind, palette_name(kind), "停靠", f"主窗口第{column_index + 1}列",
                                        kind == current, bool(booth.get("visible")) and bool(column.get("visible", 1))))
    for index, window in enumerate(root.get("window", {}).get("_items", [])):
        kinds, current = [], None
        for booth in _booths(window.get("dock", {})):
            tabs, selected = _tabs(booth)
            kinds.extend(tabs)
            current = current or selected
            for kind in tabs:
                palettes.append(Palette(kind, palette_name(kind), "浮动", f"浮动窗口{index + 1}",
                                        kind == selected, bool(booth.get("visible"))))
        rect = window.get("framerect") or window.get("restorerect") or {}
        if rect:
            floating.append(FloatingDefinition(index, (rect["left"], rect["top"], rect["right"], rect["bottom"]),
                                               _scale(window.get("framexscale")), tuple(kinds), current))
    for index, bubble in enumerate(root.get("bubble", {}).get("_items", []) + root.get("slimbubble", {}).get("_items", [])):
        for booth in _booths(bubble.get("dock", bubble)):
            tabs, current = _tabs(booth)
            for kind in tabs:
                palettes.append(Palette(kind, palette_name(kind), "图标化", f"图标组{index + 1}", kind == current, True))
    return Workspace(path, mtime, palettes, floating)
