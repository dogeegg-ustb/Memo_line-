"""Inspect Clip Studio Paint's saved dock placement (read-only)."""

import argparse
import ctypes
import json
import os
import sqlite3
import struct
from ctypes import wintypes
from pathlib import Path


def parse_blob(data):
    def parse_value(pos, kind):
        if kind in (0x48, 0x49):
            size = int.from_bytes(data[pos:pos + 4], "big")
            pos += 4
            value, end = parse_object(pos, kind)
            assert end == pos + size
            return value, end
        size = data[pos]
        pos += 1
        if kind in (0x06, 0x07):
            byte_count = int.from_bytes(data[pos:pos + size], "big")
            pos += size
            raw = data[pos:pos + byte_count]
            return (raw.decode("utf-8", "replace") if kind == 0x06 else raw.hex()), pos + byte_count
        raw = data[pos:pos + size]
        if kind == 0x15:
            return struct.unpack(">d", raw)[0], pos + size
        if kind in (0x11, 0x13, 0x14):
            return int.from_bytes(raw, "big", signed=kind == 0x14), pos + size
        raise ValueError(f"Unknown value type {kind:#x} at {pos}")

    def parse_field(pos):
        assert data[pos] == 1
        pos += 1
        size = data[pos]
        pos += 1
        name = data[pos:pos + size].decode("utf-8", "replace")
        pos += size
        kind = data[pos]
        value, end = parse_value(pos + 1, kind)
        return name, value, end

    def parse_object(pos, kind):
        assert data[pos:pos + 2] == b"\x01\x01"
        pos += 2
        name, typename, pos = parse_field(pos)
        assert name == "typename"
        count_size = data[pos]
        pos += 1
        count = int.from_bytes(data[pos:pos + count_size], "big")
        pos += count_size
        if kind == 0x48:
            items = []
            for _ in range(count):
                item_kind = data[pos]
                item, pos = parse_value(pos + 1, item_kind)
                items.append(item)
            return {"_type": typename, "_items": items}, pos
        result = {"_type": typename}
        for _ in range(count):
            name, value, pos = parse_field(pos)
            if name in result:
                old = result[name]
                result[name] = old + [value] if isinstance(old, list) else [old, value]
            else:
                result[name] = value
        return result, pos

    result, end = parse_object(0, 0x49)
    assert end == len(data)
    return result


def client_rect_from_hwnd(hwnd):
    if os.name != "nt":
        raise RuntimeError("--hwnd is available only on Windows")
    user32 = ctypes.windll.user32
    user32.IsWindow.argtypes = [wintypes.HWND]
    user32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    user32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
    if not user32.IsWindow(hwnd):
        raise ValueError(f"Invalid HWND: {hwnd:#x}")
    bounds = wintypes.RECT()
    origin = wintypes.POINT(0, 0)
    if not user32.GetClientRect(hwnd, ctypes.byref(bounds)) or not user32.ClientToScreen(hwnd, ctypes.byref(origin)):
        raise OSError("Unable to read the window client rectangle")
    return [origin.x, origin.y, bounds.right - bounds.left, bounds.bottom - bounds.top]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("dock", type=Path)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--rect", nargs=4, type=int, metavar=("X", "Y", "W", "H"),
                        help="Estimate candidate regions from a main window CLIENT rectangle")
    parser.add_argument("--hwnd", type=lambda value: int(value, 0),
                        help="Read the live main window client rectangle from HWND")
    args = parser.parse_args()
    if args.rect and args.hwnd:
        parser.error("--rect and --hwnd cannot be used together")
    with sqlite3.connect(f"file:{args.dock.as_posix()}?mode=ro", uri=True) as db:
        blob = db.execute("SELECT dockplacementbasicdata FROM dockplacement").fetchone()[0]
    tree = parse_blob(blob)
    target_rect = client_rect_from_hwnd(args.hwnd) if args.hwnd else args.rect
    if target_rect:
        x, y, width, height = target_rect
        root = tree["dockplacementpalette"]["mainframe"]["dock"]
        columns = root["dockplaces"]["_items"]
        widths = [item["dockplacebasesize"]["width"] for item in columns]
        total_width = sum(widths)
        def rect(column, top, bottom):
            left = sum(widths[:column]) / total_width
            right = sum(widths[:column + 1]) / total_width
            return [round(x + left * width), round(y + top * height),
                    round((right - left) * width), round((bottom - top) * height)]
        groups = root["subviews"]["_items"]
        def heights(group):
            return [item["dockplacebasesize"]["height"]
                    for item in group["dockplaces"]["_items"]]
        subtool = heights(groups[1]["subviews"]["_items"][0])
        center = heights(groups[2])
        right = heights(groups[4]["subviews"]["_items"][0])
        estimates = {
            "canvas_candidate": rect(2, center[0] / sum(center),
                                    sum(center[:2]) / sum(center)),
            "brush_properties_candidate": rect(1, subtool[0] / sum(subtool),
                                     sum(subtool[:2]) / sum(subtool)),
            "navigator_candidate": rect(4, 0, right[0] / sum(right)),
            "layers_candidate": rect(4, sum(right[:2]) / sum(right), 1),
        }
        print(json.dumps({"basis": "saved dock size ratios; unverified candidate screen pixels",
                          "client_rect_xywh": target_rect,
                          "rectangles_xywh": estimates}, ensure_ascii=False, indent=2))
        return
    if args.json:
        print(json.dumps(tree, ensure_ascii=False, indent=2))
    else:
        def walk(node, path=""):
            if isinstance(node, dict):
                names = [v for k, v in node.items() if k == "_type" or k.startswith("palettekind")]
                if names:
                    print(path, names)
                for key, value in node.items():
                    if key not in ("_type",):
                        walk(value, path + "/" + key)
            elif isinstance(node, list):
                for i, value in enumerate(node):
                    walk(value, path + f"/{i}")
        walk(tree)


if __name__ == "__main__":
    main()
