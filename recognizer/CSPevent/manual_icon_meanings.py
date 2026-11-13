"""Attach CLIP STUDIO PAINT manual meanings to the icon dictionary.

The online manual (help.clip-studio.com, here the Traditional Chinese edition, which has the same
page set as the other languages) documents palette icons with screenshots carrying numbered
callouts (black discs with a white digit) and a numbered list in the page text
("① 縮小 / 詳情請參照…"). For every screenshot:

  1. find dictionary icons in it (stroke-map template matching, all scales),
  2. find the numbered discs and read their digit (RapidOCR on the disc),
  3. give each icon the nearest disc in its column (or row),
  4. map the number to the page's numbered item -> title + description.

Pages are fetched once into --cache (HTML is parsed as text only). Output: manual_meanings.json
in the dictionary folder; build_icon_dictionary.py merges it into icons.json.
"""

from __future__ import annotations

import argparse
from collections import defaultdict
import concurrent.futures as cf
import hashlib
import html as htmllib
import json
import os
from pathlib import Path
import re
import sys
import urllib.parse
import urllib.request

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
MANUAL = "https://help.clip-studio.com/zh-tw/"
CIRCLED = {chr(0x2460 + i): i + 1 for i in range(20)}  # ① .. ⑳
NUMBER_RE = re.compile(r"^(?:\((\d{1,2})\)|([①-⑳]))$")


# ---------- manual pages ----------

def fetch(url: str, dst: Path) -> bool:
    if dst.exists():
        return True
    try:
        data = urllib.request.urlopen(urllib.parse.quote(url, safe=":/%#"), timeout=30).read()
    except OSError:
        return False
    dst.write_bytes(data)
    return True


def page_urls(cache: Path) -> list[str]:
    brs = cache / "brsdata.js"
    fetch(MANUAL + "whxdata/brsdata.js", brs)
    text = brs.read_text(encoding="utf-8")
    return sorted(set(re.findall(r'"link":"([^"#]+\.htm)', text)))


def page_items(html: str) -> tuple[str, list[dict]]:
    """Return (page title, sections); each section = screenshot + its numbered items."""
    body = re.sub(r"<(script|style)[^>]*>.*?</\1>", "", html, flags=re.S)
    body = re.sub(r'<img[^>]*src="([^"]+)"[^>]*>', lambda m: f"\n[[IMG {m.group(1)}]]\n", body)
    body = re.sub(r"<br\s*/?>", "\n", body)
    body = re.sub(r"<[^>]+>", "\n", body)
    lines = [htmllib.unescape(l).strip() for l in body.split("\n")]
    lines = [l for l in lines if l]
    title = lines[0] if lines else ""
    sections, cur, i = [], None, 0
    while i < len(lines):
        l = lines[i]
        if l.startswith("[[IMG "):
            cur = {"image": os.path.basename(l[6:-2]), "heading": "", "items": {}}
            sections.append(cur)
            j = i - 1
            while j >= 0 and lines[j].startswith("[[IMG "):
                j -= 1
            cur["heading"] = lines[j] if j >= 0 else ""
            i += 1; continue
        m = NUMBER_RE.match(l)
        if m and cur is not None and i + 1 < len(lines):
            n = int(m.group(1)) if m.group(1) else CIRCLED[m.group(2)]
            name = lines[i + 1]
            desc = []
            k = i + 2
            while k < len(lines) and not NUMBER_RE.match(lines[k]) and not lines[k].startswith("[[IMG ") and len(desc) < 12:
                desc.append(lines[k]); k += 1
            if n not in cur["items"]:
                cur["items"][n] = {"name": name, "description": "".join(desc)[:400]}
            i = k; continue
        i += 1
    return title, [s for s in sections if s["items"]]


# ---------- screenshot analysis ----------

def strokes(gray: np.ndarray, k: int) -> np.ndarray:
    return np.abs(gray.astype(np.float32) - cv2.medianBlur(gray, k | 1).astype(np.float32))


ORANGE = ((5, 150, 170), (25, 255, 255))  # HSV range of the manual's callout fill


def callout_discs(img: np.ndarray) -> list[tuple[int, int, int, str]]:
    """Numbered callouts: dark discs, or orange teardrops whose tip points at the item.
    Returns (cx, cy, r, kind) of the round body."""
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
    masks = {"dark": (gray < 60).astype(np.uint8),
             "orange": cv2.inRange(hsv, *ORANGE).astype(np.uint8) // 255}
    out = []
    for kind, m in masks.items():
        m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
        cnts, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        for c in cnts:
            x, y, w, h = cv2.boundingRect(c)
            if not (12 <= w <= 40 and 12 <= h <= 48 and 0.6 <= w / h <= 1.4):
                continue
            filled = np.zeros((h, w), np.uint8)
            cv2.drawContours(filled, [c - [x, y]], -1, 1, -1)
            r = int(cv2.distanceTransform(filled, cv2.DIST_L2, 5).max())
            if r < 6 or filled.mean() < 0.6:
                continue
            # round body = the inscribed circle; the glyph inside has the opposite brightness
            dt = cv2.distanceTransform(filled, cv2.DIST_L2, 5)
            cy, cx = (int(v) for v in np.unravel_index(int(dt.argmax()), dt.shape))
            inner = gray[max(0, y + cy - r // 2):y + cy + r // 2 + 1, max(0, x + cx - r // 2):x + cx + r // 2 + 1]
            glyph = (inner > 150).mean() if kind == "dark" else (inner < 90).mean()
            if 0.05 < glyph < 0.6:
                out.append((x + int(cx), y + int(cy), r, kind))
    # dark discs that touch other dark UI (sliders, frames) merge into one blob above;
    # find them as circles instead: dark ring of radius r around a light glyph
    radii = [d[2] for d in out if d[3] == "dark"]
    if radii:
        r0 = int(np.median(radii))
        circles = cv2.HoughCircles(cv2.medianBlur(gray, 3), cv2.HOUGH_GRADIENT, 1, r0 * 1.6,
                                   param1=120, param2=12, minRadius=r0 - 2, maxRadius=r0 + 2)
        for cx, cy, r in ([] if circles is None else np.round(circles[0]).astype(int)):
            if any(abs(cx - d[0]) <= r0 and abs(cy - d[1]) <= r0 for d in out):
                continue
            ring = gray[max(0, cy - r):cy + r + 1, max(0, cx - r):cx + r + 1]
            yy, xx = np.mgrid[:ring.shape[0], :ring.shape[1]]
            dist = np.hypot(yy - r, xx - r)
            body = ring[(dist <= r - 1)]
            if len(body) and (body < 60).mean() > 0.55 and 0.05 < (body > 150).mean() < 0.4:
                out.append((int(cx), int(cy), int(r), "dark"))
    return out


def disc_glyph(img: np.ndarray, cx: int, cy: int, r: int, kind: str) -> np.ndarray:
    """Digit image normalised to dark-on-white, 2r x 2r."""
    crop = cv2.cvtColor(img[max(0, cy - r):cy + r + 1, max(0, cx - r):cx + r + 1], cv2.COLOR_BGR2GRAY)
    if kind == "dark":
        crop = 255 - crop
    crop = crop.astype(np.float32)
    lo, hi = np.percentile(crop, 5), np.percentile(crop, 95)
    return np.clip((crop - lo) / max(1.0, hi - lo) * 255, 0, 255).astype(np.uint8)


def read_disc_ocr(ocr, glyph: np.ndarray) -> tuple[int | None, float]:
    g = cv2.copyMakeBorder(cv2.resize(glyph, None, fx=3, fy=3, interpolation=cv2.INTER_CUBIC), 12, 12, 12, 12,
                           cv2.BORDER_CONSTANT, value=255)
    res = ocr(cv2.cvtColor(g, cv2.COLOR_GRAY2BGR), use_det=False, use_cls=False, use_rec=True)
    txt = "".join(res.txts or []) if getattr(res, "txts", None) else ""
    sc = float(min(res.scores)) if getattr(res, "scores", None) else 0.0
    d = re.sub(r"\D", "", txt.translate(str.maketrans("OoIl", "0011")))
    return (int(d) if d and 0 < int(d) <= 40 else None), sc


class DigitBook:
    """Callout digits are rendered in one fixed font: learn templates from confident OCR reads,
    then read every callout by template (the same idea as the glyph dictionary)."""

    SIZE = 24

    def __init__(self):
        self.samples: dict[int, list[np.ndarray]] = defaultdict(list)

    @classmethod
    def norm(cls, glyph: np.ndarray) -> np.ndarray:
        return cv2.resize(glyph, (cls.SIZE, cls.SIZE), interpolation=cv2.INTER_AREA).astype(np.float32)

    def add(self, n: int, glyph: np.ndarray) -> None:
        if len(self.samples[n]) < 12:
            self.samples[n].append(self.norm(glyph))

    def read(self, glyph: np.ndarray) -> tuple[int | None, float, float]:
        g = self.norm(glyph)
        scored = sorted(((max(float(cv2.matchTemplate(g, t, cv2.TM_CCOEFF_NORMED)[0, 0]) for t in ts), n)
                         for n, ts in self.samples.items()), reverse=True)
        if not scored:
            return None, 0.0, 0.0
        second = scored[1][0] if len(scored) > 1 else 0.0
        return scored[0][1], scored[0][0], scored[0][0] - second


def find_icons(img: np.ndarray, templates, threshold: float):
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    maps = {}
    hits = []
    for iid, px, t in templates:
        if px not in maps:
            maps[px] = strokes(gray, max(5, px // 2))
        m = maps[px]
        if m.shape[0] < px or m.shape[1] < px:
            continue
        r = cv2.matchTemplate(m, t, cv2.TM_CCOEFF_NORMED)
        ys, xs = np.where(r >= threshold)
        hits += [(float(r[y, x]), int(x), int(y), px, iid) for y, x in zip(ys, xs)]
    hits.sort(reverse=True)
    keep = []
    for h in hits:
        if all(abs(h[1] - k[1]) > k[3] * 0.6 or abs(h[2] - k[2]) > k[3] * 0.6 for k in keep):
            keep.append(h)
    return keep


def reading_order(discs):
    """Group callouts into rows (centres within one radius), rows top to bottom, each left to right."""
    rows: list[list] = []
    for d in sorted(discs, key=lambda d: d[1]):
        if rows and abs(d[1] - np.mean([e[1] for e in rows[-1]])) <= d[2]:
            rows[-1].append(d)
        else:
            rows.append([d])
    return [d for row in rows for d in sorted(row, key=lambda d: d[0])]


def assign(icons, discs):
    """Nearest numbered disc that sits in the icon's column or row, within a few icon sizes."""
    out = []
    used = defaultdict(list)
    for s, x, y, px, iid in icons:
        cx, cy = x + px / 2, y + px / 2
        best = None
        for n, dx, dy, r in discs:
            ax, ay = abs(dx - cx), abs(dy - cy)
            aligned = (ax <= px * 0.6 and ay <= px * 3) or (ay <= px * 0.6 and ax <= px * 3)
            if aligned:
                d = ax + ay
                if best is None or d < best[0]:
                    best = (d, n)
        if best:
            used[best[1]].append((best[0], s, iid, (x, y, px)))
    for n, lst in used.items():
        d, s, iid, box = min(lst)  # one icon per number: the closest
        out.append({"number": n, "icon": iid, "score": round(s, 3), "box": box})
    return out


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--dictionary", type=Path, default=HERE / "icon_dictionary")
    ap.add_argument("--cache", type=Path, default=HERE / "icon_dictionary" / "manual_cache")
    ap.add_argument("--threshold", type=float, default=0.8)
    ap.add_argument("--only", help="substring of page URL to process")
    args = ap.parse_args()
    (args.cache / "pages").mkdir(parents=True, exist_ok=True)
    (args.cache / "images").mkdir(parents=True, exist_ok=True)

    urls = page_urls(args.cache)
    if args.only:
        urls = [u for u in urls if args.only in u]

    def load(u):
        dst = args.cache / "pages" / (hashlib.md5(u.encode()).hexdigest()[:12] + ".htm")
        if not fetch(MANUAL + u, dst):
            return u, None, []
        title, secs = page_items(dst.read_text(encoding="utf-8", errors="replace"))
        for s in secs:
            fetch(urllib.parse.urljoin(MANUAL + u, "../images/" + s["image"]), args.cache / "images" / s["image"])
        return u, title, secs

    with cf.ThreadPoolExecutor(8) as ex:
        pages = [p for p in ex.map(load, urls) if p[1]]
    print(f"pages {len(pages)}, screenshots with numbered items {sum(len(p[2]) for p in pages)}", flush=True)

    icons = json.loads((args.dictionary / "icons.json").read_text(encoding="utf-8"))["icons"]
    masks = np.load(args.dictionary / "masks.npz")
    mono = [i for i in icons if i["mono"] and i["layout"] in ("atlas", "states")]
    templates = []
    for ic in mono:
        big = masks[f'{ic["id"]}_0' if ic["layout"] == "states" else f'{ic["id"]}_2'].astype(np.float32)
        for px in range(18, 33, 2):  # manual screenshots: UI at 90-160 %, zoom-ins at 2x of 100 %
            t = cv2.resize(big, (px, px), interpolation=cv2.INTER_AREA)
            if t.std() > 1:
                templates.append((ic["id"], px, t))

    from rapidocr import RapidOCR
    ocr = RapidOCR(params={"Global.log_level": "warning"})
    shots = []
    for u, title, secs in pages:
        for s in secs:
            p = args.cache / "images" / s["image"]
            img = cv2.imdecode(np.fromfile(str(p), np.uint8), cv2.IMREAD_COLOR) if p.exists() else None
            if img is not None:
                shots.append((u, title, s, img, [(x, y, r, disc_glyph(img, x, y, r, k)) for x, y, r, k in callout_discs(img)]))
    # pass 1: OCR the callouts, keep confident reads that the page's numbered list confirms as digit templates
    book = DigitBook()
    for u, title, s, img, discs in shots:
        for x, y, r, g in discs:
            n, sc = read_disc_ocr(ocr, g)
            if n in s["items"] and sc >= 0.9:
                book.add(n, g)
    print("digit templates:", {n: len(v) for n, v in sorted(book.samples.items())}, flush=True)

    found = defaultdict(list)
    stats = defaultdict(int)
    for u, title, s, img, discs in shots:
        if True:
            n_items = sorted(s["items"])
            if len(discs) == len(n_items) and n_items == list(range(1, len(n_items) + 1)):
                # callouts are numbered in reading order: rows top to bottom, left to right
                discs = [(n, x, y, r) for n, (x, y, r, _) in zip(n_items, reading_order(discs))]
                stats["by order"] += 1
            else:
                discs = [(n, x, y, r) for x, y, r, g in discs
                         if (rd := book.read(g))[0] in s["items"] and rd[1] >= 0.75 and rd[2] >= 0.05 and (n := rd[0])]
                stats["by digit"] += bool(discs)
            if not discs:
                continue
            hits = find_icons(img, templates, args.threshold)
            for a in assign(hits, discs):
                item = s["items"][a["number"]]
                found[a["icon"]].append({"name": item["name"], "description": item["description"],
                                         "section": s["heading"], "page": title, "url": MANUAL + u,
                                         "image": s["image"], "number": a["number"], "score": a["score"]})
            print(f"{title} / {s['image']}: discs {len(discs)}, icons {len(hits)}, linked {len(assign(hits, discs))}", flush=True)
    out = {iid: sorted(v, key=lambda e: -e["score"]) for iid, v in found.items()}
    (args.dictionary / "manual_meanings.json").write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    print("icons with a manual meaning:", len(out), dict(stats))


if __name__ == "__main__":
    main()
