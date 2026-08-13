"""CLI for workspace detection from an image file (legacy / automation)."""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image

from canvas_border_autocrop import (
    DetectionInput,
    IntRect,
    PixelFormat,
    detect_workspace_rect,
)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Detect workspace rectangle from a screenshot file")
    p.add_argument("image", type=Path, help="Screenshot path")
    p.add_argument("--roi", type=int, nargs=4, metavar=("L", "T", "R", "B"), required=True)
    p.add_argument("--dpi", type=float, default=1.0)
    p.add_argument("--json", action="store_true", help="Print JSON result")
    args = p.parse_args(argv)

    img = Image.open(args.image).convert("RGBA")
    arr = np.array(img)
    bgra = arr[:, :, [2, 1, 0, 3]].copy()
    h, w = bgra.shape[:2]
    roi = IntRect(*args.roi)

    t0 = time.perf_counter()
    out = detect_workspace_rect(
        DetectionInput(
            capture_buffer=bgra,
            capture_width=w,
            capture_height=h,
            stride=w * 4,
            user_roi_capture_px=roi,
            dpi_scale_x=args.dpi,
            dpi_scale_y=args.dpi,
            pixel_format=PixelFormat.BGRA,
        )
    )
    ms = (time.perf_counter() - t0) * 1000.0

    payload = {
        "status": out.status.value,
        "grade": None if out.evidence_grade is None else out.evidence_grade.value,
        "confidence": out.confidence,
        "rect_capture_px": None
        if out.workspace_rect_capture_px is None
        else list(out.workspace_rect_capture_px.as_tuple()),
        "rect_screen_physical_px": None
        if out.workspace_rect_screen_physical_px is None
        else list(out.workspace_rect_screen_physical_px.as_tuple()),
        "observed_sides": [s.value for s in out.observed_sides],
        "inferred_sides": [s.value for s in out.inferred_sides],
        "message": out.message,
        "elapsed_ms": round(ms, 2),
        "timings_ms": {k: round(v, 2) for k, v in out.diagnostics.timings.items()},
    }
    if args.json:
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    else:
        print(f"status={payload['status']} grade={payload['grade']} rect={payload['rect_capture_px']}")
        print(f"confidence={payload['confidence']:.3f} elapsed_ms={payload['elapsed_ms']}")
        if out.message:
            print(f"message={out.message}")
    return 0 if out.status.value == "Ok" else 1


if __name__ == "__main__":
    raise SystemExit(main())
