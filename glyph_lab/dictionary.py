"""Read existing project dictionaries without depending on the recorder."""
from __future__ import annotations

import json
import sqlite3
from pathlib import Path

DEFAULT_WORDS = Path(__file__).with_name("data") / "ui_words_zh_cn.txt"


def clean_words(words, max_length=32):
    return sorted({w.strip() for w in words if isinstance(w, str) and w.strip()
                   and len(w.strip()) <= max_length and not any(c in w for c in "\r\n\t")})


def load_words(path: str | Path = DEFAULT_WORDS, language="chinese_sc") -> list[str]:
    path = Path(path)
    if path.suffix in {".sqlite3", ".db"}:
        with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as conn:
            return clean_words(row[0] for row in conn.execute(
                "SELECT DISTINCT text FROM resource_strings WHERE language=?", (language,)))
    if path.suffix == ".txt":
        return clean_words(path.read_text(encoding="utf-8-sig").splitlines())
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if isinstance(data, list):
        if not data or isinstance(data[0], str):
            return clean_words(data)
        return clean_words(e.get("translations", {}).get(language, "") for e in data)
    # build_text_dictionary.py's {languages: {language: {words: [...]}}} format.
    if "languages" in data:
        block = data["languages"].get(language, {})
        return clean_words(block.get("words", []) if isinstance(block, dict) else block)
    if "words" in data:
        words = data["words"]
        return clean_words(words.keys() if isinstance(words, dict) else words)
    raise ValueError("词表须为逐行 TXT、字符串 JSON 数组、ui_strings.json 或 text_words.json")
