"""Read saved CSP bindings and resolve their update targets. No default-key fallback."""
from __future__ import annotations

import ctypes
import os
import re
import sqlite3
import struct
from contextlib import closing
from pathlib import Path

from csp_shortcuts import read_menu_shortcuts, read_tool_shortcuts, read_tool_inventory, tool_descendants, saved_selected_subtool

BRUSH = "笔刷属性"
TOOLBAR = "工具栏"
TOOLGROUP = "工具组"
CANVAS = "画布视口"
NAVIGATOR = "导航器"
LAYERS = "图层"
COLOR = "色彩"
VIEW = [CANVAS, NAVIGATOR]


def find_user_dir():
    override = os.environ.get("MEMOLINE_CSP_USER_DIR")
    roots = [Path(override)] if override else [
        Path(os.environ.get("APPDATA", "")) / "CELSYSUserData/CELSYS/CLIPStudioPaintVer1_5_0",
        Path(os.environ.get("APPDATA", "")) / "CELSYS/CLIPStudioPaintVer1_5_0",
        Path.home() / "Documents/CELSYS/CLIPStudioPaintVer1_5_0",
    ]
    for root in roots:
        if (root / "Tool/EditImageTool.todb").is_file():
            return root
    raise FileNotFoundError("找不到 CSP 用户配置，可通过 MEMOLINE_CSP_USER_DIR 指定")


def targets_for_command(command):
    c = command.lower()
    if c in {"applicationchangecurrentcolor", "applicationchangecolortransparent"}:
        return [COLOR]
    if c.startswith(("tool", "subtool")):
        return [BRUSH, TOOLGROUP]
    if c.startswith(("viewrotate", "viewzoom", "viewflip")) or c in {
        "viewpixelsize", "viewwholesize", "viewreset", "viewresetrotation",
        "viewscrollup", "viewscrolldown", "viewscrollleft", "viewscrollright",
    }:
        return VIEW[:]
    # Layer opacity, blend modes, visibility and pixel edits deliberately have no
    # structural/current-layer target. Match commands, never localized descriptions.
    if c.startswith(("layermerge", "layerorder")) or c in {
        "layernew", "layercopy", "layerdelete", "layerdeleteallempty", "layerdeleteallhidden",
        "layercancelfolder", "layerfoldernewandinsert", "layerselectupperlayer",
        "layerselectlowerlayer", "layerselecttoplayer", "layerselectbottomlayer",
        "layerconvert", "layerrasterize", "layercarboncopylayer", "paste", "editpasteatshownposition",
    } or (c.startswith("layer") and c.endswith("new")):
        return [LAYERS]
    return []


def key_binding(shortcut):
    """Normalize configured symbols using the active Windows keyboard layout."""
    parts = re.split(r"\s+\+\s+", shortcut.strip())
    mods = 0
    while len(parts) > 1 and parts[0].upper() in {"CTRL", "SHIFT", "ALT"}:
        mods |= {"CTRL": 4, "SHIFT": 2, "ALT": 1}[parts.pop(0).upper()]
    if len(parts) != 1:
        return None
    key = parts[0].upper()
    named = {"SPACE": 32, "TAB": 9, "ENTER": 13, "RETURN": 13, "ESC": 27,
             "ESCAPE": 27, "DELETE": 46, "BACKSPACE": 8, "INSERT": 45,
             "HOME": 36, "END": 35, "PAGEUP": 33, "PAGEDOWN": 34,
             "LEFT": 37, "UP": 38, "RIGHT": 39, "DOWN": 40,
             "NUM+": 107, "NUM-": 109, "NUM*": 106, "NUM/": 111, "NUM.": 110}
    if key in named:
        return named[key], mods
    if re.fullmatch(r"F([1-9]|1[0-9]|2[0-4])", key):
        return 111 + int(key[1:]), mods
    if re.fullmatch(r"NUM[0-9]", key):
        return 96 + int(key[-1]), mods
    if len(key) == 1 and key.isascii() and key.isalnum():
        return ord(key), mods
    if len(key) == 1 and os.name == "nt":
        user = ctypes.windll.user32
        user.GetForegroundWindow.restype = ctypes.c_void_p
        user.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        user.GetKeyboardLayout.restype = ctypes.c_void_p
        user.VkKeyScanExW.argtypes = [ctypes.c_wchar, ctypes.c_void_p]
        user.VkKeyScanExW.restype = ctypes.c_short
        thread = user.GetWindowThreadProcessId(user.GetForegroundWindow(), None)
        code = user.VkKeyScanExW(parts[0], user.GetKeyboardLayout(thread))
        if code != -1:
            shift = (code >> 8) & 7
            mods |= (2 if shift & 1 else 0) | (4 if shift & 2 else 0) | (1 if shift & 4 else 0)
            return code & 255, mods
    return None


def held_modifier_mask(keys):
    keys = set(keys)
    return (4 if keys & {17, 162, 163} else 0) | (2 if keys & {16, 160, 161} else 0) | (1 if keys & {18, 164, 165} else 0)


class Catalog:
    def __init__(self, root=None):
        self.root = Path(root) if root else find_user_dir()
        self.paths = [self.root / p for p in (
            "Shortcut/default.khc", "Tool/EditImageTool.todb", "Shortcut/DefaultToolModifyKey.tomd")]
        self.bindings = {}
        self.gestures = []
        self.wheel_bindings = []
        self.warnings = []
        self.entries = []
        try:
            self.tool_catalog = read_tool_inventory(self.paths[1])
            self.warnings.extend(self.tool_catalog['warnings'])
        except (OSError, sqlite3.Error, ValueError) as ex:
            self.tool_catalog = dict(schemaVersion=1, status='unavailable', sourceFile=str(self.paths[1]),
                roots=[], nodes=[], savedCurrentNodeIds=[], selectionSource='savedCspConfiguration', warnings=[str(ex)])
            self.warnings.append(f"子工具目录读取失败: {ex}")
        tools_with_groups = {node['toolId'] for node in self.tool_catalog['nodes'] if node['kind'] == 'group'}
        self.brush_packages = [dict(id=node['id'], name=node['name'], toolId=node['toolId'],
            path=node['path'], subtools=tool_descendants(self.tool_catalog, node['id']),
            savedSelectedSubtoolId=saved_selected_subtool(self.tool_catalog, node['id']))
            for node in self.tool_catalog['nodes'] if node['kind'] == 'group'
            or (node['kind'] == 'tool' and node['id'] not in tools_with_groups
                and tool_descendants(self.tool_catalog, node['id']))]
        tool_nodes = {node['id']:node for node in self.tool_catalog['nodes']}
        tool_targets = {}
        try:
            with closing(sqlite3.connect(self.paths[1].resolve().as_uri() + "?mode=ro", uri=True)) as db:
                for node, identity in db.execute("SELECT _PW_ID, NodeDefaultIdentifier FROM Node"):
                    tool_targets[f"tool_{node}"] = VIEW[:] if identity in {10, 200, 201, 202, 203} else [LAYERS] if identity == 211 else [COLOR] if identity in {16, 210} else []
        except (OSError, sqlite3.Error) as ex:
            self.warnings.append(f"工具类别读取失败: {ex}")
        for path, reader in zip(self.paths, (read_menu_shortcuts, read_tool_shortcuts)):
            try:
                items = reader(path)
                for item in items:
                    binding = key_binding(item["shortcut"])
                    entry = dict(item, sourceFile=str(path),
                                 targets=targets_for_command(item.get("command", "")) if "command" in item else [BRUSH, TOOLGROUP])
                    if "command" not in item:
                        entry["pointerTargets"] = tool_targets.get(item["id"], [])
                        entry["targets"] = sorted(set(entry["targets"] + entry["pointerTargets"]))
                        entry["tool"] = tool_nodes.get(item['id'])
                        entry["subtools"] = tool_descendants(self.tool_catalog, item['id'])
                        entry["savedSelectedSubtoolId"] = saved_selected_subtool(self.tool_catalog, item['id'])
                    self.entries.append(entry)
                    if binding:
                        self.bindings.setdefault(binding, []).append(entry)
                    else:
                        self.warnings.append(f"无法匹配键码: {item['shortcut']} ({item['id']})")
                if not items:
                    self.warnings.append(f"没有已保存的快捷键: {path.name}")
            except (OSError, sqlite3.Error, ValueError) as ex:
                self.warnings.append(f"{path.name}: {ex}")
        try:
            self._load_gestures()
        except (OSError, sqlite3.Error, ValueError, struct.error) as ex:
            self.warnings.append(f"修饰键解析失败: {ex}")

    def _load_gestures(self):
        with closing(sqlite3.connect(self.paths[1].resolve().as_uri() + "?mode=ro", uri=True)) as db:
            nodes = {bytes(uuid): (name, default_id) for uuid, name, default_id in
                     db.execute("SELECT NodeUuid, NodeName, NodeDefaultIdentifier FROM Node") if uuid}
        with closing(sqlite3.connect(self.paths[2].resolve().as_uri() + "?mode=ro", uri=True)) as db:
            rows = db.execute("SELECT InputOperation, OutputOperation, RangeOperation, SettingData FROM ModifyKeySetting").fetchall()
        for inp, out, scope, blob in rows:
            if not blob or len(blob) < 8:
                continue
            header, count = struct.unpack_from(">II", blob)
            if header != 8 or count > 4096:
                raise ValueError("未知的 ModifyKeySetting 数据头")
            pos = 8
            for _ in range(count):
                size, mask, kind = struct.unpack_from(">III", blob, pos)
                if size < 12 or pos + size > len(blob):
                    raise ValueError("修饰键记录长度错误")
                data = blob[pos + 12:pos + size]
                pos += size
                wheel = bool(mask & 0x1000)
                if mask & ~0x100F:  # Other pointer buttons retain their raw CSP behavior.
                    continue
                targets, name = [], ""
                if kind == 2 and len(data) == 24 and int.from_bytes(data[4:8], "big") == 16:
                    name, identity = nodes.get(data[8:], ("未知工具", None))
                    if identity in {200, 201, 202, 203}:
                        targets = VIEW[:]
                    elif identity == 211:
                        targets = [LAYERS]
                    elif identity == 210:
                        targets = [COLOR]
                    elif identity in {20, 21, 22, 23, 24, 25, 26, 27}:
                        targets = [BRUSH]
                elif kind == 64:
                    targets, name = [BRUSH], "临时调整笔刷大小"
                elif wheel and kind == 4 and len(data) == 4:
                    # The global wheel records select canvas-view operations. Keep the
                    # configured operation code as evidence rather than inventing its label.
                    operation = int.from_bytes(data,"big")
                    if operation:
                        targets, name = VIEW[:], "配置中的画布滚轮操作"
                if targets:
                    entry = dict(mask=mask, targets=targets, action=name,
                        context=[inp, out, scope], contextDependent=bool(inp or out or scope),
                        sourceFile=str(self.paths[2]), outputType=kind)
                    if wheel:
                        entry.update(axis="vertical",operationCode=int.from_bytes(data,"big") if kind == 4 else None)
                        self.wheel_bindings.append(entry)
                    else:
                        self.gestures.append(entry)

    def match_key(self, vk, held_keys):
        if set(held_keys) & {91, 92}:
            return []
        mods = held_modifier_mask(held_keys)
        direct = self.bindings.get((vk, mods), [])
        if direct:
            return direct
        # Drivers commonly emit OEM_PLUS/OEM_MINUS while CSP stores NUM+/NUM-.
        # Add equivalence only for an already configured zoom command; other keys
        # and commands keep their exact configured mapping.
        command = "viewzoomin" if vk in {0xBB,107} else "viewzoomout" if vk in {0xBD,109} else None
        family = {0xBB,107} if command == "viewzoomin" else {0xBD,109}
        matches = []
        if command:
            for (configured_vk,configured_mods),entries in self.bindings.items():
                if configured_vk in family and configured_mods == mods:
                    matches.extend(dict(entry,matchSource="equivalentZoomKey",observedVk=vk)
                                   for entry in entries if entry.get("command","").lower() == command)
        return matches

    def match_wheel(self, held_keys, axis="vertical"):
        if axis != "vertical" or set(held_keys) & {91,92}:
            return []
        mods = held_modifier_mask(held_keys)
        mask = 0x1000 | (1 if mods & 2 else 0) | (2 if mods & 4 else 0) | (4 if mods & 1 else 0) | (8 if 32 in held_keys else 0)
        return [entry for entry in self.wheel_bindings if entry["mask"] == mask]

    def match_gesture(self, held_keys):
        if set(held_keys) & {91, 92}:
            return []
        # .tomd uses Shift=1, Ctrl=2, Alt=4, Space=8 (different from .khc).
        mods = held_modifier_mask(held_keys)
        mask = (1 if mods & 2 else 0) | (2 if mods & 4 else 0) | (4 if mods & 1 else 0) | (8 if 32 in held_keys else 0)
        return [entry for entry in self.gestures if entry["mask"] == mask] if mask else []

    def snapshot(self):
        return dict(configRoot=str(self.root), bindings=self.entries, gestures=self.gestures,
                    wheelBindings=self.wheel_bindings, warnings=self.warnings, source="savedCspConfiguration",
                    toolCatalog=self.tool_catalog, brushPackages=self.brush_packages,
                    brushPackageSource="installedCspToolGroups",
                    sourceFiles=[str(path) for path in self.paths])
