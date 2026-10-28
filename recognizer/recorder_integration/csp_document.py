"""The document CSP itself last saved or opened, as a check of the .clip Memoline was told to watch.

CSP's window title carries no document name ("CLIP STUDIO PAINT"), but CSP keeps the path of the last saved canvas in
PreviousDocument/lastmodifiedcanvaspath (SQLite, table lastmodifiedcanvaspathcategory). On 2026-10-24 it named
memolinetest10.clip, written at the Save As, while Memoline watched memoline test9.clip: every save of the session
then read a two-day-old file. When it is updated besides Save As is not known, so a mismatch is a warning only.
"""
from __future__ import annotations

import os
import sqlite3
from contextlib import closing
from pathlib import Path


def user_roots() -> list[Path]:
    override = os.environ.get("MEMOLINE_CSP_USER_DIR")
    appdata = Path(os.environ.get("APPDATA", ""))
    return [Path(override)] if override else [
        appdata / "CELSYSUserData/CELSYS/CLIPStudioPaintVer1_5_0",
        appdata / "CELSYS/CLIPStudioPaintVer1_5_0",
        Path.home() / "Documents/CELSYS/CLIPStudioPaintVer1_5_0",
    ]


def last_document() -> str | None:
    for root in user_roots():
        path = root / "PreviousDocument" / "lastmodifiedcanvaspath"
        if not path.is_file():
            continue
        try:
            with closing(sqlite3.connect(f"file:{path}?mode=ro", uri=True, timeout=1)) as db:
                row = db.execute("select lastmodifiedcanvaspath from lastmodifiedcanvaspathcategory "
                                 "order by _PW_ID desc limit 1").fetchone()
        except sqlite3.Error:
            continue
        if row and isinstance(row[0], str) and row[0].strip():
            return row[0].strip()
    return None


def same_file(a: str, b: str) -> bool:
    return os.path.normcase(os.path.abspath(a)) == os.path.normcase(os.path.abspath(b))


def mismatch(clip_path: str) -> dict | None:
    """The warning to record when CSP's last document is another .clip than the watched one."""
    last = last_document()
    if not last or not clip_path or same_file(last, clip_path):
        return None
    return dict(watchedClip=clip_path, cspLastDocument=last,
                message=f"CSP 最近保存/打开的文档是「{last}」，Memoline 监视的是「{clip_path}」。"
                        "如果 CSP 正在画的不是后者，保存写不到它：请停止录制并重新选择 .clip 文件。")
