#!/usr/bin/env python3
"""Read CLIP STUDIO PAINT .clip layer metadata without decoding image pixels.

Requires Python 3.11+ with sqlite3.Connection.deserialize (standard library).
The CLIP container layout and layer columns were checked against clipparse 0.2.0.
"""

from __future__ import annotations

import io
import sqlite3
import struct
from contextlib import closing
from pathlib import Path


BLEND_MODES = {
    0: "正常", 1: "变暗", 2: "正片叠底", 3: "颜色加深", 4: "线性加深",
    5: "减去", 6: "深色", 7: "变亮", 8: "滤色", 9: "颜色减淡",
    10: "颜色减淡（发光）", 11: "添加", 12: "添加（发光）", 13: "浅色",
    14: "叠加", 15: "柔光", 16: "强光", 17: "亮光", 18: "线性光",
    19: "点光", 20: "实色混合", 21: "差值", 22: "排除", 23: "色相",
    24: "饱和度", 25: "颜色", 26: "亮度", 30: "穿透", 36: "除法",
}

REQUIRED_LAYER_COLUMNS = (
    "MainId", "LayerName", "LayerType", "LayerFolder", "LayerVisibility",
    "LayerOpacity", "LayerComposite", "LayerClip", "LayerFirstChildIndex",
    "LayerNextIndex",
)
OPTIONAL_LAYER_COLUMNS = (
    "LayerLock", "LayerMasking", "LayerOffsetX", "LayerOffsetY",
    "LayerSelect", "DraftLayer", "LayerUuid",
)
MAX_DB_BYTES = 512 * 1024 * 1024


class ClipError(ValueError):
    """The file is not a supported or well-formed CLIP container."""


def _read_db(path: Path) -> bytes:
    with path.open("rb") as file:
        size = file.seek(0, io.SEEK_END)
        if size < 64:
            raise ClipError("文件太短，不是有效的 .clip 文件")
        file.seek(0)
        header = file.read(24)
        magic, declared_size, header_len = struct.unpack(">8sQQ", header)
        if magic != b"CSFCHUNK":
            raise ClipError("文件头不是 CSFCHUNK；请确认输入是 CSP 的 .clip 文件")
        if declared_size != size:
            raise ClipError("文件头声明的长度与实际文件长度不一致")
        if header_len < 24 or header_len + 40 > size:
            raise ClipError("文件头长度无效")

        file.seek(header_len)
        chunk_head = file.read(40)
        if chunk_head[:8] != b"CHNKHead":
            raise ClipError("缺少 CHNKHead")
        head_size = struct.unpack_from(">Q", chunk_head, 8)[0]
        if head_size < 16 or header_len + 16 + head_size > size:
            raise ClipError("CHNKHead 长度无效")
        db_offset = struct.unpack_from(">Q", chunk_head, 24)[0]
        if db_offset < header_len + 16 + head_size or db_offset + 16 > size:
            raise ClipError("SQLite 区域偏移无效")

        file.seek(db_offset)
        db_head = file.read(16)
        if db_head[:8] != b"CHNKSQLi":
            raise ClipError("未在预期位置找到 CHNKSQLi")
        db_size = struct.unpack_from(">Q", db_head, 8)[0]
        if db_size < 100 or db_size > MAX_DB_BYTES or db_offset + 16 + db_size > size:
            raise ClipError("SQLite 元数据长度无效或超过 512 MiB 安全上限")
        db = file.read(db_size)
        if len(db) != db_size or not db.startswith(b"SQLite format 3\0"):
            raise ClipError("SQLite 元数据损坏")
        return db


def _int(value: object, default: int = 0) -> int:
    return default if value is None else int(value)


def _name(value: object) -> str:
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return "" if value is None else str(value)


def _uuid(value: object) -> str | None:
    if value is None:
        return None
    if isinstance(value, bytes):
        return value.hex()
    return str(value)


def read_clip_layers(path: str | Path) -> dict:
    """Return canvas metadata and a depth-first list of document layers.

    Only the embedded SQLite section is read; pixel chunks are skipped.
    Opacity preserves CSP's raw 0..256 value and supplies a percentage.
    """
    source = Path(path)
    db = _read_db(source)
    if not hasattr(sqlite3.Connection, "deserialize"):
        raise RuntimeError("当前 Python 的 sqlite3 不支持 deserialize；需要 Python 3.11+ 的标准构建")

    with closing(sqlite3.connect(":memory:")) as con:
        try:
            con.deserialize(db)
            con.execute("PRAGMA query_only=ON")
            con.row_factory = sqlite3.Row
            tables = {row[0] for row in con.execute(
                "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('Canvas','Layer')"
            )}
            if tables != {"Canvas", "Layer"}:
                raise ClipError("SQLite 元数据缺少 Canvas 或 Layer 表")
            canvas = con.execute(
                "SELECT CanvasRootFolder, CanvasWidth, CanvasHeight, "
                "CanvasResolution, CanvasUnit, CanvasCurrentLayer FROM Canvas LIMIT 1"
            ).fetchone()
            if canvas is None:
                raise ClipError("Canvas 表为空")
            columns = {row[1] for row in con.execute('PRAGMA table_info("Layer")')}
            missing = set(REQUIRED_LAYER_COLUMNS) - columns
            if missing:
                raise ClipError("Layer 表缺少必要字段：" + ", ".join(sorted(missing)))
            chosen = REQUIRED_LAYER_COLUMNS + tuple(
                col for col in OPTIONAL_LAYER_COLUMNS if col in columns
            )
            sql = "SELECT " + ", ".join(f'"{col}"' for col in chosen) + ' FROM "Layer"'
            rows = {int(row["MainId"]): row for row in con.execute(sql)}
        except sqlite3.DatabaseError as exc:
            raise ClipError(f"SQLite 元数据无法解析：{exc}") from exc

    root_id = _int(canvas["CanvasRootFolder"])
    if root_id not in rows:
        raise ClipError("CanvasRootFolder 未指向有效图层")

    layers = []
    seen = {root_id}
    root = rows[root_id]
    stack = [(_int(root["LayerFirstChildIndex"]), root_id, 0, True)]
    while stack:
        layer_id, parent_id, depth, parent_visible = stack.pop()
        if not layer_id:
            continue
        if layer_id in seen:
            raise ClipError(f"图层树出现循环或重复引用：{layer_id}")
        row = rows.get(layer_id)
        if row is None:
            raise ClipError(f"图层树引用了不存在的图层：{layer_id}")
        seen.add(layer_id)
        folder_bits = _int(row["LayerFolder"])
        type_bits = _int(row["LayerType"])
        visible = bool(_int(row["LayerVisibility"]))
        effective_visible = parent_visible and visible
        opacity_raw = _int(row["LayerOpacity"])
        blend_id = _int(row["LayerComposite"])
        layer = {
            "id": layer_id,
            "parent_id": None if parent_id == root_id else parent_id,
            "depth": depth,
            "name": _name(row["LayerName"]),
            "opacity_raw": opacity_raw,
            "opacity_percent": round(opacity_raw * 100 / 256, 2),
            "visible": visible,
            "effective_visible": effective_visible,
            "blend_mode_id": blend_id,
            "blend_mode": BLEND_MODES.get(blend_id, f"未知 ({blend_id})"),
            "clipped": bool(_int(row["LayerClip"])),
            "locked": bool(_int(row["LayerLock"])) if "LayerLock" in row.keys() else None,
            "is_folder": bool(folder_bits & 1),
            "folder_collapsed": bool(folder_bits & 16),
            "is_adjustment": bool(type_bits & 4096),
            "is_paper": type_bits == 1584,
            "has_mask": bool(type_bits & 2),
            "masking_raw": _int(row["LayerMasking"]) if "LayerMasking" in row.keys() else None,
            "type_raw": type_bits,
            "folder_raw": folder_bits,
            "offset_x": row["LayerOffsetX"] if "LayerOffsetX" in row.keys() else None,
            "offset_y": row["LayerOffsetY"] if "LayerOffsetY" in row.keys() else None,
            "selected": bool(_int(row["LayerSelect"])) if "LayerSelect" in row.keys() else None,
            "draft": bool(_int(row["DraftLayer"])) if "DraftLayer" in row.keys() else None,
            "uuid": _uuid(row["LayerUuid"]) if "LayerUuid" in row.keys() else None,
        }
        layers.append(layer)
        stack.append((_int(row["LayerNextIndex"]), parent_id, depth, parent_visible))
        stack.append((_int(row["LayerFirstChildIndex"]), layer_id, depth + 1,
                      effective_visible))

    return {
        "source": str(source.resolve()),
        "canvas": {
            "width_raw": canvas["CanvasWidth"],
            "height_raw": canvas["CanvasHeight"],
            "unit_raw": canvas["CanvasUnit"],
            "resolution": canvas["CanvasResolution"],
            "current_layer_id": canvas["CanvasCurrentLayer"],
        },
        "layer_count": len(layers),
        "unlinked_layer_count": len(rows) - len(seen),
        "layers": layers,
    }

