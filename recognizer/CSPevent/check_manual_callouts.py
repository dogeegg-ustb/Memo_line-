"""Check callout detection against 47 manually marked centres in five official images.

Downloads only the five referenced screenshots into --cache (or uses --offline).
This measures detection, not digit reading or icon-to-meaning accuracy.
"""

import argparse
import hashlib
import json
from pathlib import Path

import cv2
import numpy as np

from manual_icon_meanings import HERE, callout_discs, fetch


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--cache", type=Path, default=HERE / "icon_dictionary/manual_cache/images")
    ap.add_argument("--offline", action="store_true")
    args = ap.parse_args()
    args.cache.mkdir(parents=True, exist_ok=True)
    fixtures = json.loads((HERE / "tests/fixtures/manual_callouts.json").read_text(encoding="utf-8"))
    results = []
    for fixture in fixtures:
        path = args.cache / fixture["image"]
        if not path.exists() and (args.offline or not fetch(fixture["url"], path)):
            ap.error(f"screenshot unavailable: {path}")
        if hashlib.sha256(path.read_bytes()).hexdigest() != fixture["sha256"]:
            ap.error(f"screenshot changed; review reference centres before rechecking: {path}")
        img = cv2.imdecode(np.fromfile(str(path), np.uint8), cv2.IMREAD_COLOR)
        discs, centres = callout_discs(img), fixture["centers"]
        matched = sum(any(np.hypot(x - a, y - b) <= 4 for x, y, *_ in discs) for a, b in centres)
        extras = sum(not any(np.hypot(x - a, y - b) <= 4 for a, b in centres) for x, y, *_ in discs)
        results.append({"image": fixture["image"], "expected": len(centres), "detected": matched,
                        "extra_candidates": extras})
    print(json.dumps({"screenshots": results, "expected": sum(r["expected"] for r in results),
                      "detected": sum(r["detected"] for r in results),
                      "extra_candidates": sum(r["extra_candidates"] for r in results)}, indent=2))
    raise SystemExit(0 if all(r["expected"] == r["detected"] for r in results) else 1)


if __name__ == "__main__":
    main()
