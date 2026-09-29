"""Find installed CSP resource strings or icon templates by query."""

from __future__ import annotations

import argparse
from pathlib import Path
import sqlite3

from build_database import DEFAULT_OUTPUT, normalize


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("text", nargs="?", default="", help="Observed UI text or fragment")
    parser.add_argument("--language", default="chinese_tc")
    parser.add_argument("--database", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--icons", action="store_true", help="Search icon database instead of strings")
    parser.add_argument("--limit", type=int, default=20)
    args = parser.parse_args()

    with sqlite3.connect(f"file:{args.database.resolve().as_posix()}?mode=ro", uri=True) as db:
        if args.icons or not args.text:
            query = f"%{args.text}%" if args.text else "%"
            has_table = db.execute(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='icon_templates'"
            ).fetchone()[0]
            if not has_table:
                print("icon_templates 表尚未初始化。请先运行 build_icons.py 或启动 CSPevent。")
                return
            rows = db.execute("""
                SELECT icon_id, name_zh, name_tc, name_ja, name_en, category, command_hint
                FROM icon_templates
                WHERE icon_id LIKE ? OR name_zh LIKE ? OR name_tc LIKE ? OR name_ja LIKE ? OR name_en LIKE ?
                ORDER BY category, icon_id
                LIMIT ?
            """, (query, query, query, query, query, max(1, args.limit))).fetchall()
            print(f"找到 {len(rows)} 个图标模板：")
            for icon_id, n_zh, n_tc, n_ja, n_en, cat, cmd in rows:
                print(f"[{cat}] {icon_id}\t{n_zh} / {n_tc} / {n_ja} / {n_en}\t(Cmd: {cmd})")
            return

        fragment = normalize(args.text)
        if not fragment:
            parser.error("text must contain a non-whitespace character")
        rows = db.execute("""
            SELECT resource_file, node_path, text
            FROM resource_strings
            WHERE language = ? AND instr(normalized_text, ?) > 0
            ORDER BY CASE WHEN normalized_text = ? THEN 0 ELSE 1 END,
                     length(normalized_text), resource_file, node_path
            LIMIT ?
        """, (args.language, fragment, fragment, max(1, args.limit))).fetchall()
        for resource_file, node_path, label in rows:
            print(f"{resource_file}:{node_path}\t{label}")


if __name__ == "__main__":
    main()
