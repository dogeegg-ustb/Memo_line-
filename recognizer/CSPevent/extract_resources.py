"""Extract icons and images from CLIP STUDIO PAINT installation files and resources.

Scans:
1. CSP resource files in `resource/other` and `resource/<lang>`
2. Embedded DIB, PNG, BMP, ICO, and zlib-compressed streams in Celsys resource trees
3. PE resources (.rsrc) in CLIPStudioPaint.exe and DLLs
Outputs extracted images to a dedicated directory and generates an HTML preview gallery.
"""

from __future__ import annotations

import io
import os
from pathlib import Path
import struct
import sys
import zlib

try:
    from PIL import Image
    HAS_PIL = True
except ImportError:
    HAS_PIL = False

DEFAULT_INSTALLATION = Path(r"D:\CLIP STUDIO 1.5\CLIP STUDIO PAINT")
DEFAULT_OUTPUT_DIR = Path(__file__).resolve().parent / "extracted_icons"


def is_png(data: bytes) -> int:
    idx = data.find(b"\x89PNG\r\n\x1a\n")
    return idx


def is_ico(data: bytes) -> bool:
    return len(data) >= 6 and data.startswith(b"\x00\x00\x01\x00")


def is_bmp(data: bytes) -> bool:
    return len(data) >= 14 and data.startswith(b"BM")


def is_jpeg(data: bytes) -> int:
    return data.find(b"\xff\xd8\xff")


def is_dib(data: bytes) -> tuple[int, int, int] | None:
    """Check if data starts with BITMAPINFOHEADER (biSize=40)."""
    if len(data) < 40:
        return None
    bi_size = struct.unpack_from("<I", data, 0)[0]
    if bi_size != 40:
        return None
    width, height, planes, bpp, compression, img_size = struct.unpack_from("<iiHHII", data, 4)
    if not (0 < abs(width) <= 2048 and 0 < abs(height) <= 2048):
        return None
    if planes != 1 or bpp not in (1, 4, 8, 16, 24, 32):
        return None
    if compression not in (0, 3):  # BI_RGB, BI_BITFIELDS
        return None
    return width, abs(height), bpp


def dib_to_bmp(dib_data: bytes) -> bytes:
    """Convert raw BITMAPINFOHEADER + pixel array into valid BMP file bytes."""
    width, height, planes, bpp = struct.unpack_from("<iiHH", dib_data, 4)
    palette_colors = 0
    if bpp <= 8:
        clr_used = struct.unpack_from("<I", dib_data, 32)[0]
        palette_colors = clr_used if clr_used > 0 else (1 << bpp)
    palette_size = palette_colors * 4
    header_size = 14 + len(dib_data)
    bf_off_bits = 14 + 40 + palette_size
    bf_type = b"BM"
    bf_size = header_size
    bf_reserved = 0
    bmp_header = struct.pack("<2sIHHI", bf_type, bf_size, 0, 0, bf_off_bits)
    return bmp_header + dib_data


def parse_celsys_resource(blob: bytes, start: int = 0, end: int | None = None,
                          path: tuple[int, ...] = ()) -> list[tuple[tuple[int, ...], bytes]]:
    """Traverse Celsys big-endian resource tree and return all raw leaf payloads."""
    if end is None:
        end = len(blob)
    if end - start < 4:
        return []

    count = struct.unpack_from(">I", blob, start)[0]
    directory_end = start + 4 + count * 12

    if 0 < count < 10000 and directory_end <= end:
        try:
            entries = [struct.unpack_from(">III", blob, start + 4 + i * 12)
                       for i in range(count)]
            if all(directory_end <= offset and offset + size <= end
                   for _, offset, size in entries):
                results = []
                for key, offset, size in entries:
                    results.extend(parse_celsys_resource(blob, offset, offset + size, path + (key,)))
                return results
        except Exception:
            pass

    # Leaf node
    if count == end - start - 4:
        payload = blob[start + 4:end]
        return [(path, payload)]

    return []


def inspect_and_save_image(payload: bytes, tag: str, out_dir: Path) -> dict | None:
    """Test payload against known image formats and save as image."""
    if len(payload) < 8:
        return None

    # Try zlib decompressed first
    try_zlib = False
    if payload[:2] in (b"\x78\x9c", b"\x78\x01", b"\x78\xda"):
        try:
            decompressed = zlib.decompress(payload)
            if len(decompressed) > len(payload):
                res = inspect_and_save_image(decompressed, tag + "_zlib", out_dir)
                if res:
                    return res
        except Exception:
            pass

    # 1. Direct PNG or embedded PNG
    png_offset = is_png(payload)
    if png_offset >= 0:
        png_data = payload[png_offset:]
        out_file = out_dir / f"{tag}.png"
        out_file.write_bytes(png_data)
        w, h = 0, 0
        if len(png_data) >= 24:
            w, h = struct.unpack(">II", png_data[16:24])
        return {"file": out_file.name, "type": "PNG", "width": w, "height": h, "size": len(png_data)}

    # 2. Direct BMP
    if is_bmp(payload):
        out_file = out_dir / f"{tag}.bmp"
        out_file.write_bytes(payload)
        w, h = 0, 0
        if len(payload) >= 26:
            w, h = struct.unpack("<ii", payload[18:26])
        return {"file": out_file.name, "type": "BMP", "width": abs(w), "height": abs(h), "size": len(payload)}

    # 3. Direct ICO
    if is_ico(payload):
        out_file = out_dir / f"{tag}.ico"
        out_file.write_bytes(payload)
        return {"file": out_file.name, "type": "ICO", "width": 0, "height": 0, "size": len(payload)}

    # 4. Direct DIB
    dib_info = is_dib(payload)
    if dib_info is not None:
        w, h, bpp = dib_info
        bmp_bytes = dib_to_bmp(payload)
        out_file = out_dir / f"{tag}.bmp"
        out_file.write_bytes(bmp_bytes)
        return {"file": out_file.name, "type": f"DIB ({bpp}bpp)", "width": w, "height": h, "size": len(bmp_bytes)}

    # 5. JPEG
    jpg_offset = is_jpeg(payload)
    if jpg_offset >= 0 and jpg_offset < 16:
        jpg_data = payload[jpg_offset:]
        out_file = out_dir / f"{tag}.jpg"
        out_file.write_bytes(jpg_data)
        return {"file": out_file.name, "type": "JPEG", "width": 0, "height": 0, "size": len(jpg_data)}

    # 6. Try PIL raw decode if square or standard dimensions
    if HAS_PIL and len(payload) in (256, 576, 1024, 4096, 16384, 65536):
        dim = int(len(payload) ** 0.5)
        if dim * dim == len(payload):  # Square 8-bit grayscale
            try:
                img = Image.frombytes("L", (dim, dim), payload)
                out_file = out_dir / f"{tag}_gray_{dim}x{dim}.png"
                img.save(out_file)
                return {"file": out_file.name, "type": "RAW Gray", "width": dim, "height": dim, "size": len(payload)}
            except Exception:
                pass

    return None


def scan_celsys_resource_files(installation: Path, out_dir: Path) -> list[dict]:
    """Scan all resource packages in installation/resource."""
    resource_dir = installation / "resource"
    if not resource_dir.is_dir():
        print(f"Resource directory not found: {resource_dir}")
        return []

    extracted = []
    out_dir.mkdir(parents=True, exist_ok=True)

    # Prioritize 'other' as it contains language-neutral UI bitmaps and geometry
    subdirs = sorted(resource_dir.iterdir(), key=lambda p: (0 if p.name == "other" else 1, p.name))

    for lang_dir in subdirs:
        if not lang_dir.is_dir():
            continue
        print(f"Scanning directory: {lang_dir.name}...")
        for source_file in sorted(lang_dir.iterdir()):
            if not source_file.is_file():
                continue

            try:
                blob = source_file.read_bytes()
            except Exception as e:
                print(f"  Cannot read {source_file.name}: {e}")
                continue

            # First check if the entire file is a PNG/BMP/etc. directly
            direct_img = inspect_and_save_image(blob, f"{lang_dir.name}_{source_file.name}", out_dir)
            if direct_img:
                direct_img["source"] = f"{lang_dir.name}/{source_file.name}"
                direct_img["path"] = "root"
                extracted.append(direct_img)
                continue

            # Otherwise, traverse the nested Celsys tree
            leaves = parse_celsys_resource(blob)
            for path_tuple, payload in leaves:
                # Skip pure printable utf-8 text strings
                try:
                    text = payload.decode("utf-8")
                    if text.isprintable() and len(text.strip()) > 0:
                        continue
                except UnicodeError:
                    pass

                path_str = ".".join(map(str, path_tuple))
                tag = f"{lang_dir.name}_{source_file.name}_{path_str}"
                res = inspect_and_save_image(payload, tag, out_dir)
                if res:
                    res["source"] = f"{lang_dir.name}/{source_file.name}"
                    res["path"] = path_str
                    extracted.append(res)

    return extracted


def scan_pe_resources(installation: Path, out_dir: Path) -> list[dict]:
    """Scan PE resources in CLIPStudioPaint.exe and DLLs."""
    extracted = []
    binaries = [installation / "CLIPStudioPaint.exe"]
    binaries.extend(installation.glob("*.dll"))

    for binary in binaries:
        if not binary.is_file():
            continue
        try:
            data = binary.read_bytes()
        except Exception:
            continue

        # Fast scan for embedded PNG signatures in the binary
        offset = 0
        png_sig = b"\x89PNG\r\n\x1a\n"
        sig_len = len(png_sig)
        count = 0

        while True:
            offset = data.find(png_sig, offset)
            if offset == -1:
                break
            # Find IEND chunk
            iend = data.find(b"IEND\xaeB`\x82", offset)
            if iend != -1 and iend - offset < 2_000_000:
                png_len = (iend + 8) - offset
                png_bytes = data[offset:offset + png_len]
                w, h = 0, 0
                if len(png_bytes) >= 24:
                    w, h = struct.unpack(">II", png_bytes[16:24])
                if 12 <= w <= 256 and 12 <= h <= 256:
                    count += 1
                    tag = f"pe_{binary.stem}_{offset:08x}_{w}x{h}"
                    out_file = out_dir / f"{tag}.png"
                    out_file.write_bytes(png_bytes)
                    extracted.append({
                        "file": out_file.name,
                        "type": "PE Embedded PNG",
                        "width": w,
                        "height": h,
                        "size": len(png_bytes),
                        "source": binary.name,
                        "path": f"0x{offset:08x}"
                    })
                offset += png_len
            else:
                offset += sig_len

        if count > 0:
            print(f"Found {count} embedded PNGs in {binary.name}")

    return extracted


def generate_html_gallery(extracted: list[dict], html_path: Path):
    """Generate an interactive HTML gallery to inspect extracted icons."""
    rows = []
    for item in sorted(extracted, key=lambda x: (x.get("width", 0), x.get("height", 0)), reverse=True):
        fname = item["file"]
        w = item.get("width", 0)
        h = item.get("height", 0)
        t = item.get("type", "")
        src = item.get("source", "")
        p = item.get("path", "")
        rows.append(f"""
        <div class="card">
            <div class="img-wrap">
                <img src="{fname}" alt="{fname}" title="{fname}">
            </div>
            <div class="meta">
                <strong>{fname}</strong><br>
                <span>尺寸: {w} × {h}</span><br>
                <span>格式: {t}</span><br>
                <small title="{src} ({p})">{src}<br>{p}</small>
            </div>
        </div>
        """)

    html = f"""<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>CSP 提取图标与图像预览集</title>
<style>
body {{ background: #1e1e24; color: #e0e0e0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; padding: 20px; }}
h1 {{ color: #4da6ff; margin-bottom: 6px; }}
p {{ color: #aaa; margin-top: 0; }}
.grid {{ display: grid; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr)); gap: 14px; margin-top: 20px; }}
.card {{ background: #2b2b36; border: 1px solid #3e3e4f; border-radius: 8px; padding: 12px; display: flex; flex-direction: column; align-items: center; }}
.img-wrap {{ width: 96px; height: 96px; display: flex; align-items: center; justify-content: center; background: repeating-conic-gradient(#3a3a4a 0% 25%, #2a2a38 0% 50%) 50% / 16px 16px; border-radius: 4px; margin-bottom: 10px; border: 1px solid #4a4a5a; }}
.img-wrap img {{ max-width: 80px; max-height: 80px; image-rendering: pixelated; }}
.meta {{ font-size: 12px; line-height: 1.4; width: 100%; word-break: break-all; color: #ccc; }}
.meta strong {{ color: #fff; }}
.meta small {{ color: #888; font-size: 11px; }}
</style>
</head>
<body>
<h1>CLIP STUDIO PAINT 资源库提取结果</h1>
<p>已从 CSP 安装目录与资源包中提取出 <strong>{len(extracted)}</strong> 个图像/图标资产。</p>
<div class="grid">
{"".join(rows)}
</div>
</body>
</html>
"""
    html_path.write_text(html, encoding="utf-8")
    print(f"Gallery written to: {html_path}")


def main():
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("installation", nargs="?", type=Path, default=DEFAULT_INSTALLATION)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT_DIR)
    args = parser.parse_args()

    print(f"Scanning CSP installation at: {args.installation}")
    print(f"Extracting to: {args.output}")

    args.output.mkdir(parents=True, exist_ok=True)
    all_extracted = []

    # 1. Scan Celsys resource files (especially 'other')
    celsys_items = scan_celsys_resource_files(args.installation, args.output)
    all_extracted.extend(celsys_items)
    print(f"Extracted {len(celsys_items)} images from Celsys resource trees.")

    # 2. Scan PE binaries for embedded icons / PNGs
    pe_items = scan_pe_resources(args.installation, args.output)
    all_extracted.extend(pe_items)
    print(f"Extracted {len(pe_items)} images from PE binaries.")

    print(f"Total extracted: {len(all_extracted)} images.")

    # 3. Generate HTML Gallery
    gallery_file = args.output / "index.html"
    generate_html_gallery(all_extracted, gallery_file)


if __name__ == "__main__":
    main()
