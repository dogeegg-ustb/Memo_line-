"""Font rasterizers. Rendering happens at build time, never in recognition."""
from __future__ import annotations

import ctypes as C
import hashlib
import os
import platform
from dataclasses import asdict, dataclass
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


@dataclass(frozen=True)
class FontSpec:
    source: str
    px: int = 18
    index: int = 0
    backend: str = "pillow"

    def __post_init__(self):
        if not 6 <= self.px <= 128:
            raise ValueError("字号须为 6–128 px")
        if self.backend not in {"pillow", "gdi"}:
            raise ValueError("backend 必须是 pillow 或 gdi")

    def identity(self) -> dict:
        result = asdict(self)
        if self.backend == "pillow":
            # Contents, not just the name: replacing a font must invalidate the cache.
            result["sha256"] = hashlib.sha256(Path(self.source).read_bytes()).hexdigest()
            import PIL
            result["pillow"] = PIL.__version__
        else:
            result["windows"] = platform.version()
        return result


def default_font(px: int = 18) -> FontSpec:
    if os.name == "nt":
        # A starting preset, not an inference of the user's actual CSP font.
        return FontSpec("SimSun", px, backend="gdi")
    path = "/System/Library/Fonts/Supplemental/Songti.ttc"
    if Path(path).exists():
        return FontSpec(path, px, 6)  # Songti SC Regular; face 0 is Black!
    raise ValueError("请用 --font 指定本机字体文件")


class PillowRenderer:
    def __init__(self, spec: FontSpec):
        self.spec = spec
        self.font = ImageFont.truetype(spec.source, spec.px, index=spec.index)
        self.name = " / ".join(self.font.getname())
        from fontTools.ttLib import TTFont
        with TTFont(spec.source, fontNumber=spec.index, lazy=True) as ft:
            self.codepoints = frozenset(ft.getBestCmap())

    def supports(self, text):
        return all(ord(ch) in self.codepoints for ch in text)

    def render(self, text: str, supersample: int = 1) -> np.ndarray:
        return self.render_with_baseline(text, supersample)[0]

    def render_with_baseline(self, text: str, supersample: int = 1):
        font = self.font if supersample == 1 else ImageFont.truetype(
            self.spec.source, self.spec.px * supersample, index=self.spec.index)
        left, top, right, bottom = font.getbbox(text)
        pad = 4 * supersample
        im = Image.new("L", (max(1, right-left) + 2*pad,
                             max(1, bottom-top) + 2*pad))
        ImageDraw.Draw(im).text((pad-left, pad-top), text, font=font, fill=255)
        baseline = pad-top+font.getmetrics()[0]
        if supersample != 1:
            size = (max(1, round(im.width/supersample)), max(1, round(im.height/supersample)))
            baseline *= size[1]/im.height
            im = im.resize(size, Image.Resampling.LANCZOS)
        return np.asarray(im).copy(), baseline

    def close(self):
        pass


class GDIRenderer:
    """Windows' actual GDI rasterizer, with explicit 64-bit signatures and cleanup."""
    def __init__(self, spec: FontSpec, scale: int = 1):
        if os.name != "nt":
            raise ValueError("GDI 渲染器仅在 Windows 上可用；Mac 请指定字体文件")
        from ctypes import wintypes as W
        self.g = g = C.WinDLL("gdi32", use_last_error=True)
        self.spec = spec
        self.name = spec.source
        signatures = {
            "CreateCompatibleDC": ([W.HDC], W.HDC),
            "CreateFontW": ([C.c_int]*5 + [W.DWORD]*8 + [W.LPCWSTR], W.HFONT),
            "SelectObject": ([W.HDC, W.HANDLE], W.HANDLE),
            "DeleteObject": ([W.HANDLE], W.BOOL), "DeleteDC": ([W.HDC], W.BOOL),
            "GetTextFaceW": ([W.HDC, C.c_int, W.LPWSTR], C.c_int),
            "GetTextExtentPoint32W": ([W.HDC, W.LPCWSTR, C.c_int, C.c_void_p], W.BOOL),
            "CreateDIBSection": ([W.HDC, C.c_void_p, W.UINT, C.c_void_p, W.HANDLE, W.DWORD], W.HBITMAP),
            "TextOutW": ([W.HDC, C.c_int, C.c_int, W.LPCWSTR, C.c_int], W.BOOL),
            "SetBkColor": ([W.HDC, W.DWORD], W.DWORD),
            "SetTextColor": ([W.HDC, W.DWORD], W.DWORD),
            "SetBkMode": ([W.HDC, C.c_int], C.c_int),
            "GdiFlush": ([], W.BOOL),
            "GetGlyphIndicesW": ([W.HDC, W.LPCWSTR, C.c_int, C.c_void_p, W.DWORD], W.DWORD),
            "GetTextMetricsW": ([W.HDC, C.c_void_p], W.BOOL),
        }
        for name, (args, result) in signatures.items():
            fn = getattr(g, name); fn.argtypes = args; fn.restype = result
        class Header(C.Structure):
            _fields_ = [("size", W.DWORD), ("width", W.LONG), ("height", W.LONG),
                        ("planes", W.WORD), ("bits", W.WORD), ("compression", W.DWORD),
                        ("image_size", W.DWORD), ("xppm", W.LONG), ("yppm", W.LONG),
                        ("used", W.DWORD), ("important", W.DWORD)]
        self.Header = Header
        self.dc = g.CreateCompatibleDC(None)
        self.font = g.CreateFontW(-spec.px*scale, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, spec.source)
        if not self.dc or not self.font:
            self.close(); raise OSError("GDI 创建字体失败")
        self.old_font = g.SelectObject(self.dc, self.font)
        face = C.create_unicode_buffer(128)
        g.GetTextFaceW(self.dc, 128, face)
        # GDI silently substitutes missing fonts, which would poison the dictionary.
        aliases = {"simsun": "宋体", "ms ui gothic": "ms ui gothic"}
        wanted = aliases.get(spec.source.casefold(), spec.source.casefold())
        actual = aliases.get(face.value.casefold(), face.value.casefold())
        if wanted != actual:
            self.close(); raise ValueError(f"GDI 替换了字体：{spec.source} → {face.value}")

    def supports(self, text):
        length = len(text.encode("utf-16-le"))//2
        indices = (C.c_ushort*length)()
        result = self.g.GetGlyphIndicesW(self.dc,text,length,indices,1)
        return result != 0xFFFFFFFF and all(i != 0xFFFF for i in indices)

    def render(self, text: str, supersample: int = 1) -> np.ndarray:
        return self.render_with_baseline(text, supersample)[0]

    def render_with_baseline(self, text: str, supersample: int = 1):
        if supersample != 1:
            renderer = GDIRenderer(self.spec, scale=supersample)
            try:
                a, baseline = renderer.render_with_baseline(text)
            finally:
                renderer.close()
            size = (max(1, round(a.shape[1]/supersample)), max(1, round(a.shape[0]/supersample)))
            return (np.asarray(Image.fromarray(a).resize(size, Image.Resampling.LANCZOS)).copy(),
                    baseline*size[1]/a.shape[0])
        length = len(text.encode("utf-16-le")) // 2
        size = (C.c_long*2)()
        self.g.GetTextExtentPoint32W(self.dc, text, length, C.byref(size))
        # TEXTMETRICW starts with tmHeight, tmAscent. Reserve ample aligned space.
        metrics = (C.c_long*32)()
        if not self.g.GetTextMetricsW(self.dc, C.byref(metrics)):
            raise OSError("GDI 字体基线读取失败")
        baseline = 4+metrics[1]
        w, h = size[0]+8, size[1]+8
        header = self.Header(C.sizeof(self.Header), w, -h, 1, 32, 0, 0, 0, 0, 0, 0)
        bits = C.c_void_p()
        bmp = self.g.CreateDIBSection(self.dc, C.byref(header), 0, C.byref(bits), None, 0)
        if not bmp:
            raise OSError("GDI 创建位图失败")
        old = self.g.SelectObject(self.dc, bmp)
        try:
            C.memset(bits, 0, w*h*4)
            self.g.SetBkColor(self.dc, 0); self.g.SetTextColor(self.dc, 0xFFFFFF)
            self.g.SetBkMode(self.dc, 2)
            if not self.g.TextOutW(self.dc, 4, 4, text, length):
                raise OSError("GDI 绘制失败")
            self.g.GdiFlush()
            a = np.ctypeslib.as_array(C.cast(bits, C.POINTER(C.c_uint8)), shape=(h,w,4))
            return a[:,:,:3].max(axis=2).copy(), baseline
        finally:
            self.g.SelectObject(self.dc, old); self.g.DeleteObject(bmp)

    def close(self):
        if getattr(self, "dc", None):
            if getattr(self, "old_font", None):
                self.g.SelectObject(self.dc, self.old_font)
            if getattr(self, "font", None):
                self.g.DeleteObject(self.font)
            self.g.DeleteDC(self.dc)
            self.dc = None


def make_renderer(spec: FontSpec):
    return GDIRenderer(spec) if spec.backend == "gdi" else PillowRenderer(spec)
