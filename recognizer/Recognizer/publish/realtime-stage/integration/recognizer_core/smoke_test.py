"""Minimal local-input smoke runner for the Python recognition cores."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import cv2

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from recognizer_core.clip_layers_core import read_clip_layers
from recognizer_core.color_state import ColorStateCore
from recognizer_core.layer_state import LayerStateCore
from recognizer_core.panel_state import PanelStateCore


def _image(path: Path):
    image = cv2.imread(str(path), cv2.IMREAD_COLOR)
    if image is None:
        raise SystemExit(f"Unable to read image: {path}")
    return image


def main() -> None:
    parser = argparse.ArgumentParser(description="Run one recognizer core on a local input file.")
    parser.add_argument("core", choices=("clip", "layer", "panel", "color"))
    parser.add_argument("input", type=Path, help=".clip file, layer-panel, tool-properties, or color-panel crop")
    parser.add_argument("--catalog", type=Path, help="optional layer/property catalog override")
    args = parser.parse_args()

    if args.core == "clip":
        result = read_clip_layers(args.input)
    elif args.core == "layer":
        result = LayerStateCore(catalog_path=args.catalog).process(_image(args.input))
    elif args.core == "color":
        result = ColorStateCore().process(_image(args.input))
    else:
        result = PanelStateCore(catalog_path=args.catalog).process(_image(args.input))
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
