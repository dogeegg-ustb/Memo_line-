"""Analyze saved sct_live_debug capture with native detect + seed heuristics."""
import ctypes
import math
import sys
from pathlib import Path

from PIL import Image


class SctIntRect(ctypes.Structure):
    _fields_ = [("left", ctypes.c_int), ("top", ctypes.c_int), ("right", ctypes.c_int), ("bottom", ctypes.c_int)]


class SctBackgroundModel(ctypes.Structure):
    _fields_ = [
        ("center_lab_l", ctypes.c_float), ("center_lab_a", ctypes.c_float), ("center_lab_b", ctypes.c_float),
        ("strong_delta_e", ctypes.c_float), ("weak_delta_e", ctypes.c_float), ("confidence", ctypes.c_float),
    ]


class SctDetectRequest(ctypes.Structure):
    _fields_ = [
        ("bgra", ctypes.c_void_p), ("width", ctypes.c_int), ("height", ctypes.c_int), ("stride", ctypes.c_int),
        ("user_roi", SctIntRect), ("dpi_x", ctypes.c_float), ("dpi_y", ctypes.c_float),
        ("origin_x", ctypes.c_int), ("origin_y", ctypes.c_int), ("capture_id", ctypes.c_char_p),
    ]


class SctDetectResult(ctypes.Structure):
    _fields_ = [
        ("status", ctypes.c_int), ("workspace_capture", SctIntRect), ("workspace_screen", SctIntRect),
        ("evidence_grade", ctypes.c_int), ("confidence", ctypes.c_float),
        ("message", ctypes.c_char * 256), ("source_capture_id", ctypes.c_char * 64),
        ("background", SctBackgroundModel), ("has_background", ctypes.c_int),
        ("source_backend", ctypes.c_int), ("source_revision", ctypes.c_char * 64), ("api_version", ctypes.c_int),
    ]


def rgb_to_lab(r, g, b):
    r, g, b = r / 255.0, g / 255.0, b / 255.0
    def lin(c):
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    r, g, b = lin(r), lin(g), lin(b)
    x = r * 0.4124564 + g * 0.3575761 + b * 0.1804375
    y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750
    z = r * 0.0193339 + g * 0.1191920 + b * 0.9503041
    x, y, z = x / 0.95047, y / 1.0, z / 1.08883
    def f(t):
        return t ** (1 / 3) if t > 0.008856 else (7.787 * t + 16 / 116)
    fx, fy, fz = f(x), f(y), f(z)
    return 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)


def delta_e(l1, l2):
    return math.sqrt(sum((a - b) ** 2 for a, b in zip(l1, l2)))


def edge_seed_stats(img, roi, inset_ratio=0.15):
    w, h = img.size
    l, t, r, b = roi
    inset_x = max(7, int(round((r - l) * inset_ratio)))
    inset_y = max(7, int(round((b - t) * inset_ratio)))
    bands = {
        "left": [(l + inset_x, y) for y in range(t + inset_y, b - inset_y, max(1, (b - t - 2 * inset_y) // 8))],
        "right": [(r - inset_x - 1, y) for y in range(t + inset_y, b - inset_y, max(1, (b - t - 2 * inset_y) // 8))],
        "top": [(x, t + inset_y) for x in range(l + inset_x, r - inset_x, max(1, (r - l - 2 * inset_x) // 8))],
        "bottom": [(x, b - inset_y - 1) for x in range(l + inset_x, r - inset_x, max(1, (r - l - 2 * inset_x) // 8))],
    }
    px = img.load()
    out = {}
    for side, pts in bands.items():
        labs = [rgb_to_lab(*px[x, y][:3]) for x, y in pts if 0 <= x < w and 0 <= t < h]
        if not labs:
            out[side] = "empty"
            continue
        med = tuple(sorted(v[i] for v in labs)[len(labs) // 2] for i in range(3))
        out[side] = f"n={len(labs)} Lab=({med[0]:.1f},{med[1]:.1f},{med[2]:.1f})"
    return out


def detect(png, roi, dpi, dll):
    img = Image.open(png).convert("RGBA")
    w, h = img.size
    buf = img.tobytes("raw", "RGBA", 0, -1)
    arr = (ctypes.c_ubyte * len(buf)).from_buffer_copy(buf)
    req = SctDetectRequest(
        bgra=ctypes.cast(arr, ctypes.c_void_p), width=w, height=h, stride=w * 4,
        user_roi=SctIntRect(*roi), dpi_x=dpi, dpi_y=dpi, origin_x=0, origin_y=0, capture_id=b"diag",
    )
    res = SctDetectResult()
    rc = dll.sct_detect_workspace(ctypes.byref(req), ctypes.byref(res))
    return rc, res


def main():
    root = Path(__file__).resolve().parents[1]
    dll = ctypes.CDLL(str(root / "native/build_src/ScreenCanvasNative.dll"))
    dll.sct_detect_workspace.argtypes = [ctypes.POINTER(SctDetectRequest), ctypes.POINTER(SctDetectResult)]
    dll.sct_detect_workspace.restype = ctypes.c_int
    dll.sct_status_name.argtypes = [ctypes.c_int]
    dll.sct_status_name.restype = ctypes.c_char_p

    debug_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(r"C:\Users\dogeegg\AppData\Local\Temp\sct_live_debug\cba79eb280504f2b9c3b64538c232d40")
    png = debug_dir / "fail_InsufficientGeometry_capture.png"
    meta = (debug_dir / "fail_InsufficientGeometry_meta.txt").read_text(encoding="utf-8")
    roi = None
    dpi = 144.0
    for line in meta.splitlines():
        if line.startswith("userRoi="):
            nums = line.split("[")[1].split(")")[0].split(",")
            roi = tuple(int(x) for x in nums)
        if line.startswith("dpi=("):
            dpi = float(line.split("(")[1].split(",")[0])

    img = Image.open(png)
    print("capture:", png.name, img.size, "dpi=", dpi, "userRoi=", roi)
    print("\n[edge seed bands @15% inset]")
    for side, s in edge_seed_stats(img, roi).items():
        print(f"  {side}: {s}")

    rc, res = detect(png, roi, dpi, dll)
    name = dll.sct_status_name(res.status).decode()
    msg = res.message.decode("utf-8", "ignore")
    print(f"\n[native] rc={rc} status={res.status} ({name}) msg={msg}")
    print(f"  grade={res.evidence_grade} conf={res.confidence:.3f} hasBg={res.has_background}")

    # Compare ROI sizes at different insets
    w, h = img.size
    tests = [
        ("user_roi", roi),
        ("inset_4", (4, 4, w - 4, h - 4)),
        ("center_80pct", (int(w * 0.1), int(h * 0.1), int(w * 0.9), int(h * 0.9))),
    ]
    print("\n[roi sweep]")
    for label, r in tests:
        _, res2 = detect(png, r, dpi, dll)
        name2 = dll.sct_status_name(res2.status).decode()
        msg2 = res.message.decode("utf-8", "ignore") if res2 is res else res2.message.decode("utf-8", "ignore")
        msg2 = res2.message.decode("utf-8", "ignore")
        print(f"  {label} {r} -> {name2}: {msg2}")


if __name__ == "__main__":
    main()
