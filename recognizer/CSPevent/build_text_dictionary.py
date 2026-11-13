"""Build the glyph reader's word list (recognizer_core/panel_state/data/text_words.json).

Reads the installed CSP language resources directly (resource/<language>/*), so the list matches the
installed CSP version and includes Simplified Chinese, which the older csp_strings.sqlite3 lacks.
Only short single-line strings are kept: panel labels, values and names, not help sentences.
Brush-package names are not in here; the recorder adds them from EditImageTool.todb at run time.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from build_database import DEFAULT_INSTALLATION, parse_node  # noqa: E402
DEFAULT_OUTPUT = HERE.parent / "recognizer_core" / "panel_state" / "data" / "text_words.json"
MAX_CHARS = 24


def words_of(folder: Path) -> list[str]:
    words = set()
    for source in sorted(p for p in folder.iterdir() if p.is_file()):
        for value in parse_node(source.read_bytes()).values():
            w = value.strip()
            if w and "\n" not in w and "\r" not in w and len(w) <= MAX_CHARS:
                words.add(w)
    return sorted(words)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("installation", nargs="?", type=Path, default=DEFAULT_INSTALLATION)
    ap.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = ap.parse_args()
    root = args.installation / "resource"
    langs = {p.name: words_of(p) for p in sorted(root.iterdir()) if p.is_dir() and p.name != "other"}
    exe = args.installation / "CLIPStudioPaint.exe"
    body = {"schemaVersion": 1, "installation": str(args.installation),
            "executableSize": exe.stat().st_size if exe.is_file() else None,
            "maxChars": MAX_CHARS, "languages": langs}
    args.output.write_text(json.dumps(body, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(json.dumps({k: len(v) for k, v in langs.items()}))


if __name__ == "__main__":
    main()
