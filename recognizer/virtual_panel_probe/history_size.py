"""How tall the History palette must be to list every undo step at once.

CSP keeps at most `UndoCount` undo steps (Preferences > Performance, stored in the user's
Preference/Config.sqlite), and the History palette lists one row per step under the document's
opening row. The palette is measured on screen (first row top, row pitch) because both follow CSP's
UI scale; before any measurement a 150 %-scale reading is scaled by the monitor DPI.
"""
from __future__ import annotations

import os
import sqlite3
from contextlib import closing
from pathlib import Path

import numpy as np

import paths  # noqa: F401
from recognizer_core.history_state.core import measure_rows

HISTORY_KIND = "palettekindhistory"
DEFAULT_UNDO_COUNT = 30  # CSP's default
# Measured on CSP 1.5 at 150 % (144 DPI): tab bar 33 px, rows 28 px apart.
REFERENCE = dict(dpi=144, top=33, pitch=28)


def user_roots() -> list[Path]:
    override = os.environ.get("MEMOLINE_CSP_USER_DIR")
    appdata = Path(os.environ.get("APPDATA", ""))
    return [Path(override)] if override else [
        appdata / "CELSYSUserData/CELSYS/CLIPStudioPaintVer1_5_0",
        appdata / "CELSYS/CLIPStudioPaintVer1_5_0",
        Path.home() / "Documents/CELSYS/CLIPStudioPaintVer1_5_0",
    ]


_cache: dict[str, tuple[int, tuple[int, str]]] = {}


def undo_count() -> tuple[int, str]:
    """CSP's undo limit and where it came from; read again only when the preferences file changes."""
    for root in user_roots():
        path = root / "Preference" / "Config.sqlite"
        try:
            stamp = path.stat().st_mtime_ns
        except OSError:
            continue
        cached = _cache.get(str(path))
        if cached and cached[0] == stamp:
            return cached[1]
        try:
            with closing(sqlite3.connect(f"file:{path}?mode=ro", uri=True, timeout=1)) as db:
                row = db.execute("select UndoCount from Performance").fetchone()
            if row and isinstance(row[0], int) and row[0] > 0:
                _cache[str(path)] = (stamp, (row[0], str(path)))
                return row[0], str(path)
        except sqlite3.Error:
            continue
    return DEFAULT_UNDO_COUNT, "默认值"


def estimate(dpi: int) -> tuple[int, int]:
    ratio = (dpi or 96) / REFERENCE["dpi"]
    return round(REFERENCE["top"] * ratio), round(REFERENCE["pitch"] * ratio)


def measure(image) -> tuple[int, int] | None:
    """(first row top, row pitch) in a capture of the palette (a PIL image)."""
    return measure_rows(np.asarray(image.convert("RGB")))


def target_height(top: int, pitch: int, steps: int) -> int:
    """The opening row and `steps` undo rows, with half a row to spare at the bottom."""
    return top + (steps + 1) * pitch + pitch // 2
