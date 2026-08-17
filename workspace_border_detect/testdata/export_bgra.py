"""Convert ROI PNGs to raw BGRA for native diag."""
from PIL import Image
from pathlib import Path

root = Path(r"d:\ART_line A\ART_line\workspace_border_detect\testdata")
for i in range(1, 4):
    p = root / f"roi{i}.png"
    im = Image.open(p).convert("RGBA")
    # Pillow RGBA -> BGRA bytes
    r, g, b, a = im.split()
    bgra = Image.merge("RGBA", (b, g, r, a)).tobytes()
    out = root / f"roi{i}.bgra"
    meta = root / f"roi{i}.meta.txt"
    out.write_bytes(bgra)
    meta.write_text(f"{im.width} {im.height} {im.width * 4}\n", encoding="utf-8")
    print(f"wrote {out.name} {im.width}x{im.height}")
