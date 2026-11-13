"""Find dictionary icons in recorded live panel captures (recorder spool PNGs).

Atlas and button-sheet icons are drawn at base * dpi / 96 px (base 20 -> 30 px at 150 %). The capture is turned
into a stroke map (|gray - local median|) so that icon colour and highlight boxes do not matter.
Writes sightings JSON for build_icon_dictionary.py --sightings.
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import time

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
DEFAULT_SPOOL = HERE.parents[1] / "Memoline_demo_csponly" / "publish" / "win-x64" / "Recognizer" / "procedure" / "stroke"
PANELS = ("图层", "笔刷属性", "工具组", "色彩滑块", "色彩", "导航器")


def strokes(gray: np.ndarray, px: int) -> np.ndarray:
    k = max(5, px // 2) | 1
    return np.abs(gray.astype(np.float32) - cv2.medianBlur(gray, k).astype(np.float32))


def spool_crops(spool: Path, per_panel: int):
    """Distinct panel images from recorder spools, spread over sessions."""
    seen, picked = set(), defaultdict(list)
    for d in sorted(spool.glob("*.spool")):
        for f in sorted(d.glob("*.done")):
            try:
                o = json.loads(f.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            for name, c in (o.get("crops") or {}).items():
                p = d / Path(c.get("path", "")).name
                key = (name, o.get("dpi"))
                if name not in PANELS or len(picked[key]) >= per_panel or not p.exists():
                    continue
                h = hashlib.md5(p.read_bytes()).hexdigest()
                if h not in seen:
                    seen.add(h); picked[key].append(p)
    return picked


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--dictionary", type=Path, default=HERE / "icon_dictionary")
    ap.add_argument("--spool", type=Path, default=DEFAULT_SPOOL)
    ap.add_argument("--per-panel", type=int, default=4)
    ap.add_argument("--threshold", type=float, default=0.85)
    ap.add_argument("--out", type=Path, default=HERE / "icon_dictionary" / "sightings.json")
    args = ap.parse_args()

    icons = json.loads((args.dictionary / "icons.json").read_text(encoding="utf-8"))["icons"]
    masks = np.load(args.dictionary / "masks.npz")
    atlas = [i for i in icons if i["layout"] in ("atlas", "states") and i["mono"]]
    crops = spool_crops(args.spool, args.per_panel)
    found = []
    for (panel, dpi), paths in sorted(crops.items(), key=lambda kv: str(kv[0])):
        tpls = []
        for ic in atlas:
            px = round(ic["base"] * dpi / 96) if ic["base"] == 20 else round(ic["base"] * dpi / 144)
            big = masks[f'{ic["id"]}_0' if ic["layout"] == "states" else f'{ic["id"]}_2']
            a = cv2.resize(big.astype(np.float32), (px, px), interpolation=cv2.INTER_AREA)
            if a.std() > 1:
                tpls.append((ic["id"], px, a))
        for p in paths:
            im = cv2.imdecode(np.fromfile(str(p), np.uint8), cv2.IMREAD_GRAYSCALE)
            t0 = time.perf_counter(); hits = []
            maps = {}
            for iid, px, a in tpls:
                if px not in maps:
                    maps[px] = strokes(im, px)
                m = maps[px]
                if m.shape[0] < px or m.shape[1] < px:
                    continue
                r = cv2.matchTemplate(m, a, cv2.TM_CCOEFF_NORMED)
                ys, xs = np.where(r >= args.threshold)
                hits += [(float(r[y, x]), int(x), int(y), px, iid) for y, x in zip(ys, xs)]
            hits.sort(reverse=True); keep = []
            for h in hits:
                if all(abs(h[1] - k[1]) > k[3] * 0.6 or abs(h[2] - k[2]) > k[3] * 0.6 for k in keep):
                    keep.append(h)
            found += [{"icon": iid, "panel": panel, "dpi": dpi, "x": x, "y": y, "px": px, "score": round(s, 3),
                       "capture": p.name} for s, x, y, px, iid in keep]
            print(f"{panel} {dpi} {p.name[:8]}: {len(keep)} icons, {time.perf_counter() - t0:.1f} s", flush=True)
    args.out.write_text(json.dumps(found, ensure_ascii=False, indent=1), encoding="utf-8")
    print("distinct icons seen:", len({f["icon"] for f in found}), dict(Counter(f["panel"] for f in found)))


if __name__ == "__main__":
    main()
