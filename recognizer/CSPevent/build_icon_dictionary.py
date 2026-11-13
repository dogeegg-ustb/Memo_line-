"""Build an icon dictionary from the extracted CSP master sheets.

Most CSP icon sheets are mipmap atlases: 21s x 4ks pixels holding k icons, each stored at
s (one column), 2s (two columns) and 4s (four columns), row-major. Button sheets (w x 4s) hold one
button per s-px column in four states (normal, hover, pressed, disabled). The remaining sheets are
cut into connected components and same-shape components are merged into one icon.
Icons are monochrome alpha masks tinted at run time, so the dictionary stores alpha.

Live UI size (measured on 150 %/175 % panel captures): base-20 icons are drawn at
20 * dpi / 96 px.

Outputs (in --out): icons.json (metadata), masks.npz (alpha of every variant), index.html.
"""

from __future__ import annotations

import argparse
import base64
from collections import Counter, defaultdict
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sqlite3

import cv2
import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
DEFAULT_SHEETS = HERE / "extracted_icons" / "master_sheets"
DEFAULT_OUT = HERE / "icon_dictionary"
DEFAULT_DB = HERE / "csp_strings.sqlite3"
DEFAULT_SCAN = [
    Path(r"D:/CLIP STUDIO 1.5/CLIP STUDIO PAINT/resource"),
    Path(r"D:/CLIP STUDIO 1.5/CLIP STUDIO PAINT/CLIPStudioPaint.exe"),
    Path(r"D:/CLIP STUDIO 1.5/Common"),
    Path(r"D:/CLIP STUDIO 1.5/CLIP STUDIO/resource"),
]
NORM = 32           # shape signature size
SAME_ICON = 0.97    # signature NCC for "same icon" across sheets
SAME_SCALE = 0.85   # signature NCC for "same icon at another size" within a sheet
MAX_ICON = 160      # larger components are artwork/skins, not icons


def ncc(a: np.ndarray, b: np.ndarray) -> float:
    a = a - a.mean(); b = b - b.mean()
    d = float(np.sqrt((a * a).sum() * (b * b).sum()))
    return float((a * b).sum() / d) if d > 0 else 0.0


def signature(alpha: np.ndarray) -> np.ndarray:
    """Ink-box crop, padded square, resized: compares shapes independent of size and offset."""
    ys, xs = np.where(alpha > 16)
    a = alpha[ys.min():ys.max() + 1, xs.min():xs.max() + 1].astype(np.float32)
    h, w = a.shape; n = max(h, w)
    sq = np.zeros((n, n), np.float32); sq[(n - h) // 2:(n - h) // 2 + h, (n - w) // 2:(n - w) // 2 + w] = a
    return cv2.resize(sq, (NORM, NORM), interpolation=cv2.INTER_AREA) / 255.0


def atlas_slots(w: int, h: int):
    for s in (20, 30, 40, 16, 24, 32):
        if w == 21 * s and h % (4 * s) == 0:
            return s, [[(0, i * s, s),
                        (s + (i % 2) * 2 * s, (i // 2) * 2 * s, 2 * s),
                        (5 * s + (i % 4) * 4 * s, (i // 4) * 4 * s, 4 * s)] for i in range(h // s)]
    return None, None


def state_slots(w: int, h: int):
    """Button sheets: one s-px column per button, rows = normal, hover, pressed, disabled."""
    for s in (20, 30):
        if h == 4 * s and w % s == 0 and w > s:
            return s, [[(c * s, r * s, s) for r in range(4)] for c in range(w // s)]
    return None, None


def is_frame_piece(alpha: np.ndarray) -> bool:
    """Pieces of 9-slice frames (corners, edges) share the button-sheet layout but are not icons:
    their ink touches the cell border, or is a thin sliver."""
    ys, xs = np.where(alpha > 16)
    h, w = alpha.shape
    if ys.min() == 0 or xs.min() == 0 or ys.max() == h - 1 or xs.max() == w - 1:
        return True
    return min(ys.max() - ys.min(), xs.max() - xs.min()) + 1 < max(3, h // 5)


def state_icons(name: str, im: np.ndarray):
    s, slots = state_slots(im.shape[1], im.shape[0])
    for i, states in enumerate(slots):
        crops = [im[y:y + z, x:x + z] for x, y, z in states]
        if int((crops[0][:, :, 3] > 16).sum()) < 6 or is_frame_piece(crops[0][:, :, 3]):
            continue
        yield {"sheet": name, "layout": "states", "slot": i, "base": s, "agree": 1.0,
               "variants": [(x, y, z, z) for x, y, z in states], "crops": crops}


def is_mono(rgba: np.ndarray) -> bool:
    rgb = rgba[:, :, :3][rgba[:, :, 3] > 128]
    return bool(len(rgb) == 0 or rgb.astype(np.float32).std(axis=0).max() < 8)


def atlas_icons(name: str, im: np.ndarray):
    s, slots = atlas_slots(im.shape[1], im.shape[0])
    for i, variants in enumerate(slots):
        crops = [im[y:y + z, x:x + z] for x, y, z in variants]
        if max(int((c[:, :, 3] > 16).sum()) for c in crops) < 6:
            continue
        sigs = [signature(c[:, :, 3]) if (c[:, :, 3] > 16).any() else None for c in crops]
        agree = min((ncc(g, sigs[2]) for g in sigs[:2] if g is not None), default=0.0)
        yield {"sheet": name, "layout": "atlas", "slot": i, "base": s, "agree": round(agree, 3),
               "variants": [(x, y, z, z) for x, y, z in variants], "crops": crops}


def component_icons(name: str, im: np.ndarray):
    alpha = im[:, :, 3]
    mask = cv2.morphologyEx((alpha > 40).astype(np.uint8), cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
    n, _, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
    boxes = [tuple(int(v) for v in stats[i][:4]) for i in range(1, n)
             if stats[i][4] >= 6 and max(stats[i][2], stats[i][3]) <= MAX_ICON and max(stats[i][2], stats[i][3]) >= 8]
    # merge boxes that overlap (icons made of several parts)
    merged = True
    while merged:
        merged = False
        for i in range(len(boxes)):
            for j in range(i + 1, len(boxes)):
                a, b = boxes[i], boxes[j]
                if a[0] < b[0] + b[2] + 1 and b[0] < a[0] + a[2] + 1 and a[1] < b[1] + b[3] + 1 and b[1] < a[1] + a[3] + 1:
                    x0, y0 = min(a[0], b[0]), min(a[1], b[1])
                    x1, y1 = max(a[0] + a[2], b[0] + b[2]), max(a[1] + a[3], b[1] + b[3])
                    boxes[i] = (x0, y0, x1 - x0, y1 - y0); boxes.pop(j); merged = True; break
            if merged:
                break
    boxes = [b for b in boxes if max(b[2], b[3]) <= MAX_ICON]
    items = [(b, im[b[1]:b[1] + b[3], b[0]:b[0] + b[2]]) for b in boxes]
    items = [(b, c, signature(c[:, :, 3])) for b, c in items if (c[:, :, 3] > 16).any()]
    groups: list[list] = []
    for it in sorted(items, key=lambda t: -t[0][2] * t[0][3]):
        for g in groups:
            if ncc(g[0][2], it[2]) >= SAME_SCALE and abs(np.log((g[0][0][2] / g[0][0][3]) / (it[0][2] / it[0][3]))) < 0.2:
                g.append(it); break
        else:
            groups.append([it])
    for k, g in enumerate(groups):
        g.sort(key=lambda t: t[0][2] * t[0][3])
        yield {"sheet": name, "layout": "components", "slot": k, "base": None,
               "agree": round(min((ncc(t[2], g[-1][2]) for t in g), default=1.0), 3),
               "variants": [b for b, _, _ in g], "crops": [c for _, c, _ in g]}


def carve_sources(sheets_dir: Path, scan: list[Path]) -> dict[str, list[list]]:
    """Which CSP file (and offset) holds each master sheet; byte-identical PNG match."""
    masters = {hashlib.sha256(p.read_bytes()).hexdigest(): p.name for p in sheets_dir.glob("*.png")}
    out: dict[str, list[list]] = defaultdict(list)
    files = []
    for t in scan:
        if t.is_file():
            files.append(t)
        elif t.is_dir():
            files += [Path(r) / f for r, _, fs in os.walk(t) for f in fs]
    for fp in files:
        try:
            data = fp.read_bytes()
        except OSError:
            continue
        pos = 0
        while (i := data.find(b"\x89PNG\r\n\x1a\n", pos)) >= 0 and (j := data.find(b"IEND", i)) >= 0:
            h = hashlib.sha256(data[i:j + 8]).hexdigest()
            if h in masters:
                out[masters[h]].append([str(fp), i])
            pos = j + 8
    return dict(out)


def named_templates(db_path: Path):
    if not db_path.exists():
        return []
    with sqlite3.connect(f"file:{db_path}?mode=ro", uri=True) as db:
        rows = db.execute("select icon_id,name_zh,name_tc,name_en,category,command_hint,grid_size,matrix_data from icon_templates").fetchall()
    out = []
    for icon_id, zh, tc, en, cat, cmd, g, m in rows:
        a = np.array([255 if ch == "#" else 0 for ch in m.replace("\n", "")][:g * g], np.uint8).reshape(g, g)
        out.append({"icon_id": icon_id, "name_zh": zh, "name_tc": tc, "name_en": en, "category": cat,
                    "command_hint": cmd, "sig": signature(a)})
    return out


def thumb(rgba: np.ndarray, px: int = 40) -> str:
    if is_mono(rgba):  # stored mid-grey and tinted by CSP; show it light
        rgba = rgba.copy(); rgba[:, :, :3] = 225
    im = Image.fromarray(rgba, "RGBA")
    scale = px / max(im.size)
    im = im.resize((max(1, round(im.width * scale)), max(1, round(im.height * scale))), Image.LANCZOS)
    bg = Image.new("RGBA", (px, px), (58, 58, 58, 255))
    bg.alpha_composite(im, ((px - im.width) // 2, (px - im.height) // 2))
    buf = io.BytesIO(); bg.convert("RGB").save(buf, "PNG")
    return base64.b64encode(buf.getvalue()).decode()


def write_html(path: Path, icons: list[dict], thumbs: dict[str, str], summary: dict) -> None:
    sections = [("实时界面中出现过", lambda i: bool(i.get("seen"))),
                ("已命名", lambda i: bool(i.get("name")) and not i.get("seen")),
                ("单色图标 · 三级图集", lambda i: i["mono"] and i["layout"] == "atlas" and not i.get("seen") and not i.get("name")),
                ("按钮图（4 态）", lambda i: i["layout"] == "states" and not i.get("seen") and not i.get("name")),
                ("单色图标 · 其他排法", lambda i: i["mono"] and i["layout"] == "components" and not i.get("seen") and not i.get("name")),
                ("彩色图形", lambda i: not i["mono"] and i["layout"] != "states" and not i.get("seen") and not i.get("name"))]
    parts = []
    for title, pred in sections:
        items = [i for i in icons if pred(i)]
        cells = "".join(
            f'<figure title="{i["id"]}&#10;{i["sheet"]} #{i["slot"]}&#10;{len(i["variants"])} 个尺寸'
            f'{("&#10;" + i["name"]) if i.get("name") else ""}"><img src="data:image/png;base64,{thumbs[i["id"]]}">'
            f'<figcaption>{i.get("name") or i["id"]}</figcaption></figure>' for i in items)
        parts.append(f"<h2>{title} <small>{len(items)}</small></h2><div class=grid>{cells}</div>")
    stats = " · ".join(f"{k} {v}" for k, v in summary.items())
    path.write_text(f"""<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>CSP 图标字典</title>
<style>body{{background:#1b1b20;color:#ddd;font:14px "Microsoft YaHei",sans-serif;margin:24px}}h1{{font-size:28px;margin:0}}
h2{{font-size:18px;margin:28px 0 10px;color:#8cf}}small{{color:#888}}.grid{{display:flex;flex-wrap:wrap;gap:6px}}
figure{{margin:0;width:64px;text-align:center}}img{{width:40px;height:40px;image-rendering:pixelated}}
figcaption{{font-size:10px;color:#999;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}}</style></head>
<body><h1>CSP 图标字典</h1><p>{stats}</p>{''.join(parts)}</body></html>""", encoding="utf-8")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--sheets", type=Path, default=DEFAULT_SHEETS)
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--db", type=Path, default=DEFAULT_DB)
    ap.add_argument("--sightings", type=Path, help="JSON from check_icon_dictionary.py to mark icons seen live")
    # labels.json in --out: hand-checked names keyed by (sheet, slot), applied on every build
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    src_cache = args.out / "sources.json"
    sources = json.loads(src_cache.read_text(encoding="utf-8")) if src_cache.exists() else carve_sources(args.sheets, DEFAULT_SCAN)
    src_cache.write_text(json.dumps(sources, ensure_ascii=False, indent=1), encoding="utf-8")

    raw = []
    skipped = Counter()
    for p in sorted(args.sheets.glob("*.png")):
        im = np.asarray(Image.open(p).convert("RGBA"))
        if atlas_slots(im.shape[1], im.shape[0])[0] is not None:
            raw += list(atlas_icons(p.name, im))
        elif state_slots(im.shape[1], im.shape[0])[0] is not None:
            raw += list(state_icons(p.name, im))
        elif max(im.shape[:2]) >= 1024:
            skipped["跳过的大贴图"] += 1
        else:
            raw += list(component_icons(p.name, im))

    # order of atlas sheets inside their CSP file -> running icon index (for NodeIcon experiments)
    order = defaultdict(list)
    for name, locs in sources.items():
        for fp, off in locs:
            order[(fp, atlas_slots(*Image.open(args.sheets / name).size)[0])].append((off, name))
    rank = {}
    for (fp, base), lst in order.items():
        if base is None:
            continue
        for r, (_, name) in enumerate(sorted(lst)):
            rank.setdefault(name, (fp, r))

    icons, masks = [], {}
    sig_list: list[np.ndarray] = []
    for it in raw:
        big = it["crops"][0] if it["layout"] == "states" else it["crops"][-1]
        sig = signature(big[:, :, 3])
        dup = next((icons[k] for k, g in enumerate(sig_list) if ncc(g, sig) >= SAME_ICON), None)
        where = {"sheet": it["sheet"], "slot": it["slot"]}
        if dup is not None:
            dup["copies"].append(where); continue
        iid = f"icon_{len(icons):05d}"
        fp, r = rank.get(it["sheet"], (None, None))
        icons.append({"id": iid, **where, "layout": it["layout"], "base": it["base"], "agree": it["agree"],
                      "mono": is_mono(big), "variants": [list(v) for v in it["variants"]],
                      "sizes": [int(c.shape[0]) for c in it["crops"]],
                      "source": (sources.get(it["sheet"]) or [[None]])[0][0],
                      "resource_index": r * (len(atlas_slots(*Image.open(args.sheets / it["sheet"]).size)[1] or [])) + it["slot"]
                      if r is not None and it["layout"] == "atlas" else None,
                      "copies": []})
        sig_list.append(sig)
        for k, c in enumerate(it["crops"]):
            masks[f"{iid}_{k}"] = c[:, :, 3].copy()
        masks[f"{iid}_rgba"] = big.copy()

    for t in named_templates(args.db):
        scored = sorted(((ncc(t["sig"], g), k) for k, g in enumerate(sig_list)), reverse=True)
        if scored and scored[0][0] >= 0.6 and scored[0][0] - scored[1][0] >= 0.05:
            ic = icons[scored[0][1]]
            ic.update(name=t["name_zh"], name_en=t["name_en"], command_hint=t["command_hint"],
                      template_id=t["icon_id"], template_score=round(scored[0][0], 3))

    labels_path = args.out / "labels.json"
    if labels_path.exists():
        where = {}
        for ic in icons:
            for w in [{"sheet": ic["sheet"], "slot": ic["slot"]}] + ic["copies"]:
                where[(w["sheet"], w["slot"])] = ic
        for lb in json.loads(labels_path.read_text(encoding="utf-8")):
            ic = where.get((lb["match"]["sheet"], lb["match"]["slot"]))
            if ic is not None:
                ic.update(name=lb["name"], name_en=lb["name_en"], category=lb["category"])

    if args.sightings and args.sightings.exists():
        seen = json.loads(args.sightings.read_text(encoding="utf-8"))
        by = defaultdict(list)
        for s in seen:
            by[s["icon"]].append({k: s[k] for k in ("panel", "dpi", "x", "y", "px", "score")})
        for ic in icons:
            if ic["id"] in by:
                ic["seen"] = by[ic["id"]]

    summary = {"母版图": len(list(args.sheets.glob("*.png"))), "图标": len(icons),
               "跨图重复已合并": sum(len(i["copies"]) for i in icons),
               "三级图集": sum(i["layout"] == "atlas" for i in icons),
               "单色": sum(i["mono"] for i in icons),
               "已命名": sum(bool(i.get("name")) for i in icons),
               "实时界面出现": sum(bool(i.get("seen")) for i in icons), **dict(skipped)}
    np.savez_compressed(args.out / "masks.npz", **masks)
    (args.out / "icons.json").write_text(json.dumps({"summary": summary, "icons": icons}, ensure_ascii=False, indent=1), encoding="utf-8")
    write_html(args.out / "index.html", icons, {i["id"]: thumb(masks[i["id"] + "_rgba"]) for i in icons}, summary)
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    main()
