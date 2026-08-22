"""Quick diagnosis: load PNG BGRA and call sct_detect_workspace."""
import ctypes
import sys
from pathlib import Path

from PIL import Image


class SctIntRect(ctypes.Structure):
    _fields_ = [
        ("left", ctypes.c_int),
        ("top", ctypes.c_int),
        ("right", ctypes.c_int),
        ("bottom", ctypes.c_int),
    ]


class SctBackgroundModel(ctypes.Structure):
    _fields_ = [
        ("center_lab_l", ctypes.c_float),
        ("center_lab_a", ctypes.c_float),
        ("center_lab_b", ctypes.c_float),
        ("strong_delta_e", ctypes.c_float),
        ("weak_delta_e", ctypes.c_float),
        ("confidence", ctypes.c_float),
    ]


class SctDetectRequest(ctypes.Structure):
    _fields_ = [
        ("bgra", ctypes.c_void_p),
        ("width", ctypes.c_int),
        ("height", ctypes.c_int),
        ("stride", ctypes.c_int),
        ("user_roi", SctIntRect),
        ("dpi_x", ctypes.c_float),
        ("dpi_y", ctypes.c_float),
        ("origin_x", ctypes.c_int),
        ("origin_y", ctypes.c_int),
        ("capture_id", ctypes.c_char_p),
    ]


class SctDetectResult(ctypes.Structure):
    _fields_ = [
        ("status", ctypes.c_int),
        ("workspace_capture", SctIntRect),
        ("workspace_screen", SctIntRect),
        ("evidence_grade", ctypes.c_int),
        ("confidence", ctypes.c_float),
        ("message", ctypes.c_char * 256),
        ("source_capture_id", ctypes.c_char * 64),
        ("background", SctBackgroundModel),
        ("has_background", ctypes.c_int),
        ("source_backend", ctypes.c_int),
        ("source_revision", ctypes.c_char * 64),
        ("api_version", ctypes.c_int),
    ]


def detect(img_path: Path, roi, dll_path: Path):
    img = Image.open(img_path).convert("RGBA")
    w, h = img.size
    buf = img.tobytes("raw", "RGBA", 0, -1)
    stride = w * 4
    arr = (ctypes.c_ubyte * len(buf)).from_buffer_copy(buf)

    dll = ctypes.CDLL(str(dll_path))
    dll.sct_detect_workspace.argtypes = [ctypes.POINTER(SctDetectRequest), ctypes.POINTER(SctDetectResult)]
    dll.sct_detect_workspace.restype = ctypes.c_int
    dll.sct_status_name.argtypes = [ctypes.c_int]
    dll.sct_status_name.restype = ctypes.c_char_p

    req = SctDetectRequest(
        bgra=ctypes.cast(arr, ctypes.c_void_p),
        width=w,
        height=h,
        stride=stride,
        user_roi=SctIntRect(*roi),
        dpi_x=96.0,
        dpi_y=96.0,
        origin_x=0,
        origin_y=0,
        capture_id=b"diag",
    )
    res = SctDetectResult()
    rc = dll.sct_detect_workspace(ctypes.byref(req), ctypes.byref(res))
    name = dll.sct_status_name(res.status).decode("ascii", "ignore")
    msg = res.message.decode("utf-8", "ignore")
    r = res.workspace_capture
    print(f"roi={roi} rc={rc} status={res.status} ({name}) msg={msg}")
    print(f"  rect=[{r.left},{r.top},{r.right},{r.bottom}) grade={res.evidence_grade} conf={res.confidence:.3f}")
    return res.status == 0


def main():
    root = Path(__file__).resolve().parents[1]
    dll = root / "native/build_src/ScreenCanvasNative.dll"
    if len(sys.argv) < 2:
        img = root.parent / "workspace_border_detect/native/tests/case1_L.png"
    else:
        img = Path(sys.argv[1])
    if not dll.exists():
        print("missing dll:", dll)
        return 1
    if not img.exists():
        print("missing img:", img)
        return 1

    img_pil = Image.open(img)
    w, h = img_pil.size
    inset = 8
    rois = [
        ("full_inset", (inset, inset, w - inset, h - inset)),
        ("top_right_half", (w // 2, 0, w - inset, h // 2)),
        ("top_right_canvas", (int(w * 0.45), inset, w - inset, int(h * 0.55))),
        ("center_quarter", (w // 4, h // 4, 3 * w // 4, 3 * h // 4)),
    ]
    print("image:", img, f"{w}x{h}")
    for name, roi in rois:
        print(f"\n--- {name} ---")
        detect(img, roi, dll)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
