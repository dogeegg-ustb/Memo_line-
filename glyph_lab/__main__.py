from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np

from .core import GlyphMatcher, NumberMatcher
from .dictionary import DEFAULT_WORDS, load_words
from .fonts import FontSpec, default_font


def options(args):
    if args.font:
        spec = FontSpec(args.font, args.px, args.face_index, args.backend)
    else:
        spec = default_font(args.px)
    return spec, load_words(args.words, args.language)


def parser():
    ap = argparse.ArgumentParser(description="记艺线独立字形匹配实验：无需 OCR 模型")
    ap.add_argument("command", choices=["serve", "read", "benchmark", "build"], nargs="?", default="serve")
    ap.add_argument("image", nargs="?", type=Path)
    ap.add_argument("--font", help="字体文件；GDI 时为字体名称")
    ap.add_argument("--backend", choices=["pillow", "gdi"], default="pillow")
    ap.add_argument("--face-index", type=int, default=0, help="TTC 中的字体序号")
    ap.add_argument("--px", type=int, default=18, help="实际像素字号（12×DPI/96 仅为 CSP Windows 已测规则）")
    ap.add_argument("--words", type=Path, default=DEFAULT_WORDS)
    ap.add_argument("--language", default="chinese_sc")
    ap.add_argument("--cache", type=Path, default=Path(__file__).parent/".cache")
    ap.add_argument("--top-k", type=int, default=16)
    ap.add_argument("--threshold", type=float, default=.84)
    ap.add_argument("--margin", type=float, default=.04)
    ap.add_argument("--numbers", action="store_true")
    ap.add_argument("--roi", nargs=4, type=int, metavar=("X", "Y", "WIDTH", "HEIGHT"))
    ap.add_argument("--port", type=int, default=8766)
    ap.add_argument("--open", action="store_true", help="启动工作台后打开浏览器")
    ap.add_argument("--samples", type=int, default=400)
    ap.add_argument("--rounds", type=int, default=3)
    ap.add_argument("--output", type=Path, default=Path(__file__).parent/"runs/latest")
    ap.add_argument("--manifest", type=Path, help="真实截图标注 JSON，格式见 README")
    return ap


def main():
    args = parser().parse_args()
    try:
        spec, words = options(args)
        kwargs = dict(cache_dir=args.cache, top_k=args.top_k,
                      threshold=args.threshold, min_margin=args.margin)
        if args.command == "serve":
            from .server import serve
            serve(args, spec, words, kwargs)
        elif args.command == "benchmark":
            from .benchmark import benchmark
            benchmark(args, spec, words, kwargs)
        else:
            matcher = NumberMatcher(spec, **kwargs) if args.numbers else GlyphMatcher(words, spec, **kwargs)
            reader = matcher.reader if args.numbers else matcher
            meta = {"words": len(reader.words), "build_ms": reader.build_ms,
                    "cache_hit": reader.cache_hit, "font": spec.__dict__}
            if args.command == "read":
                if args.image is None:
                    raise ValueError("read 需要截图路径")
                image = cv2.imdecode(np.frombuffer(args.image.read_bytes(), np.uint8), cv2.IMREAD_COLOR)
                if image is None:
                    raise ValueError("无法读取图片")
                if args.roi:
                    x,y,w,h = args.roi
                    if min(x,y)<0 or min(w,h)<=0 or x+w>image.shape[1] or y+h>image.shape[0]:
                        raise ValueError("ROI 超出图片范围")
                    image = image[y:y+h,x:x+w]
                # Explicitly warm the code paths, without caching the answer.
                matcher.recognize(image)
                meta["result"] = matcher.recognize(image).to_dict()
            print(json.dumps(meta, ensure_ascii=False, indent=2))
    except (ValueError, OSError) as e:
        print(f"错误：{e}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
