"""Collect reproducible UI screenshot fixtures from CSP's official web manual.

HTML is parsed as data only. Recognition and annotation are separate from fetching.
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
from html.parser import HTMLParser
import io
import json
from pathlib import Path
import re
import ssl
import urllib.parse
import urllib.request

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
DATA = HERE / "official"
RAW = DATA / "raw"
ROOT = "https://help.clip-studio.com/"
PAGES = {
    "en-us": ["How_to_use_tools.htm", "180_layers/Using_layers.htm", "Color_Slider_palette.htm", "Navigating_the_canvas.htm"],
    "zh-tw": ["工具屬性面板.htm", "工具詳細面板【PRO／EX】.htm", "圖層面板.htm", "顏色滑桿面板.htm", "導航器面板.htm"],
    "ja-jp": ["ツールプロパティパレット.htm", "ツール詳細パレット【PRO__47_EX】.htm", "レイヤーパレット.htm", "カラースライダーパレット.htm", "ナビゲーターパレット.htm"],
}


def fetch(url, path):
    if path.exists():
        return path.read_bytes()
    path.parent.mkdir(parents=True, exist_ok=True)
    url = urllib.parse.quote(url, safe=":/%()")
    error = None
    for attempt in range(6):
        try:
            request = urllib.request.Request(url)
            context = ssl.create_default_context()
            if attempt % 2:
                context.maximum_version = ssl.TLSVersion.TLSv1_2
            with urllib.request.urlopen(request, timeout=20, context=context) as response:
                if urllib.parse.urlparse(response.url).hostname != "help.clip-studio.com":
                    raise ValueError("Unexpected manual redirect")
                content = response.read(25 * 1024 * 1024)
            path.write_bytes(content)
            return content
        except OSError as exc:
            error = exc
    raise OSError(f"Cannot fetch {url}: {error}")


class PageImages(HTMLParser):
    def __init__(self):
        super().__init__()
        self.images = []

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "img" and "/images/" in attrs.get("src", ""):
            self.images.append(attrs)


def collect():
    RAW.mkdir(parents=True, exist_ok=True)
    pages = []; errors = []
    for language, names in PAGES.items():
        index_url = ROOT+language+"/whxdata/brsdata.js"
        try:
            text = fetch(index_url, RAW/language/"brsdata.js").decode("utf-8-sig")
        except OSError as exc:
            errors.append(str(exc)); print(str(exc),flush=True); continue
        links = sorted(set(re.findall(r'"link":"([^"#]+\.htm)', text)))
        for name in names:
            matched = [link for link in links if link.endswith("/"+name)]
            if len(matched) != 1:
                errors.append(f"Manual index does not uniquely resolve {language}: {name}");continue
            pages.append((language, ROOT+language+"/"+matched[0]))

    def page_images(job):
        language,url = job
        key = hashlib.sha256(url.encode()).hexdigest()[:16]
        try:
            html = fetch(url, RAW/language/"pages"/(key+".html")).decode("utf-8-sig")
        except OSError as exc:
            errors.append(str(exc)); return []
        parser = PageImages(); parser.feed(html)
        print(f"Page: {language} / {url.rsplit('/',1)[-1]} ({len(parser.images)} images)",flush=True)
        return [(language,url,attrs) for attrs in parser.images]

    with ThreadPoolExecutor(max_workers=3) as pool:
        jobs = [item for group in pool.map(page_images,pages) for item in group]

    def screenshot(job):
        language,page,attrs = job
        url = urllib.parse.urljoin(page,attrs["src"])
        name = urllib.parse.urlparse(url).path.split("/")[-1]
        path = RAW/language/"images"/name
        try:
            content = fetch(url,path)
        except OSError as exc:
            errors.append(str(exc));return None
        with Image.open(io.BytesIO(content)) as im:
            width,height = im.size
        return {"id":language+"/"+name,"language":language,
                "page_url":page,"image_url":url,"image":str(path.relative_to(DATA)),
                "width":width,"height":height,
                "html_width":attrs.get("width"),"html_height":attrs.get("height"),
                "sha256":hashlib.sha256(content).hexdigest(),
                "bytes":len(content)}

    with ThreadPoolExecutor(max_workers=3) as pool:
        images = list(pool.map(screenshot,jobs))
    unique = {row["id"]:row for row in images if row is not None}
    manifest = {"collected_at_utc":datetime.now(timezone.utc).isoformat(),
                "source":"CSP official online manual; original image bytes, no resizing",
                "pages":[url for _,url in pages],"images":list(unique.values()),"errors":errors}
    (DATA/"sources.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"pages":len(pages),"images":len(unique),"bytes":sum(r["bytes"] for r in unique.values())}))
    return manifest


def contact_sheets(manifest):
    font = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf",14) if Path("/System/Library/Fonts/Supplemental/Arial.ttf").exists() else ImageFont.load_default()
    for language in PAGES:
        rows = [r for r in manifest["images"] if r["language"] == language]
        for offset in range(0,len(rows),12):
            subset=rows[offset:offset+12]
            sheet=Image.new("RGB",(1200,((len(subset)+3)//4)*350),"#e8eaed")
            draw=ImageDraw.Draw(sheet)
            for i,row in enumerate(subset):
                x=(i%4)*300; y=(i//4)*350
                draw.text((x+8,y+5),row["id"],font=font,fill="black")
                draw.text((x+8,y+24),f'{row["width"]} x {row["height"]}',font=font,fill="black")
                with Image.open(DATA/row["image"]) as im:
                    im=im.convert("RGB");im.thumbnail((284,294))
                    sheet.paste(im,(x+8,y+48))
            sheet.save(RAW/f"contact-{language}-{offset//12+1}.png")


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command",choices=["collect","contact"])
    args=parser.parse_args()
    manifest=collect() if args.command == "collect" else json.loads((DATA/"sources.json").read_text())
    contact_sheets(manifest)


if __name__ == "__main__":
    main()
