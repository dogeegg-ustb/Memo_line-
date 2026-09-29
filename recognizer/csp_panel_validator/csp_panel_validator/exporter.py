from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import cv2


class Exporter:
    @staticmethod
    def write_json(result: dict[str, Any], path: str | Path) -> Path:
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        return path

    @staticmethod
    def append_jsonl(result: dict[str, Any], path: str | Path) -> Path:
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n")
        return path

    @staticmethod
    def write_overlay(image: Any, path: str | Path) -> Path:
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        if not cv2.imwrite(str(path), image):
            raise OSError(f"无法写入证据叠加图: {path}")
        return path
