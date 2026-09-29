"""Fetch only official documentation into an ignored local cache (never at runtime)."""
import hashlib
import json
import re
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.parse import quote, urljoin, urldefrag
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1] / ".tmp" / "docs"
BASE = "https://help.clip-studio.com/"


def fetch(url):
    url = quote(url, safe=":/#?=%")
    path = ROOT / (hashlib.sha256(url.encode()).hexdigest()[:16] + ".html")
    if not path.exists():
        path.write_bytes(urlopen(url, timeout=40).read())
    return url, path.read_text(encoding="utf-8"), path.name


def main():
    ROOT.mkdir(parents=True, exist_ok=True)
    seed_urls = [BASE + "en-us/manual_en/240_brushes/Customizing_brush_tools.htm", BASE + "zh-tw/manual_tc/240_brushes/自訂筆刷.htm"]
    seeds = list(map(fetch, seed_urls))
    urls = {BASE + f"en-us/manual_en/810_subtools/{letter}.htm" for letter in ("A", "B", "C", "E", "I", "S", "T", "W", "Number")}
    for url, html, _ in seeds:
        for href in re.findall(r'href="([^"]+)"', html):
            dest = urldefrag(urljoin(url, href))[0]
            if "/810_subtools/" in dest and dest.startswith(BASE):
                urls.add(dest)
    with ThreadPoolExecutor(max_workers=4) as pool:
        pages = seeds + list(pool.map(fetch, sorted(urls)))
    manifest = [{"url": url, "file": name, "sha256": hashlib.sha256(html.encode()).hexdigest()} for url, html, name in pages]
    (ROOT / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Fetched {len(pages)} official documentation pages")


if __name__ == "__main__":
    main()
