"""Small loopback-only workbench. Screenshots never leave this machine."""
from __future__ import annotations

import base64
import io
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

from .benchmark import synthetic
from .core import GlyphMatcher, NumberMatcher
from .dictionary import load_words
from .fonts import FontSpec, make_renderer


def serve(args, spec, words, kwargs):
    state = {}

    def configure(font, lexicon, settings):
        # Build everything before replacing the working readers.
        matcher = GlyphMatcher(lexicon,font,**settings)
        numbers = NumberMatcher(font,**settings)
        state.update(matcher=matcher,numbers=numbers,font=font,settings=settings)
        return {"font":font.__dict__,"words":len(matcher.words),
                "skipped_words":matcher.requested_words-len(matcher.words),
                "build_ms":round(matcher.build_ms,2),"cache_hit":matcher.cache_hit,
                "threshold":matcher.threshold,"margin":matcher.min_margin}

    configure(spec,words,kwargs)
    state.update(word_path=str(args.words),language=args.language)

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def send(self, status, body, content_type="application/json; charset=utf-8"):
            if isinstance(body,dict): body=json.dumps(body,ensure_ascii=False).encode()
            self.send_response(status)
            self.send_header("Content-Type",content_type)
            self.send_header("Content-Length",str(len(body)))
            self.send_header("Cache-Control","no-store")
            self.send_header("X-Content-Type-Options","nosniff")
            self.end_headers(); self.wfile.write(body)

        def do_GET(self):
            if self.path == "/":
                self.send(200,Path(__file__).with_name("ui.html").read_bytes(),"text/html; charset=utf-8")
            elif self.path == "/api/config":
                matcher=state["matcher"]
                self.send(200,{"font":state["font"].__dict__,"words":len(matcher.words),
                    "word_path":state["word_path"],"language":state["language"],
                    "threshold":matcher.threshold,"margin":matcher.min_margin,
                    "build_ms":round(matcher.build_ms,2),"cache_hit":matcher.cache_hit})
            elif self.path == "/api/demo":
                renderer = make_renderer(state["font"])
                try:
                    word = "不透明度" if "不透明度" in state["matcher"].words else state["matcher"].words[0]
                    im = synthetic(renderer,word,41)
                finally:
                    renderer.close()
                ok,data=cv2.imencode(".png",im)
                self.send(200,{"image":"data:image/png;base64,"+base64.b64encode(data).decode(),
                               "truth":word,"synthetic":True})
            else:
                self.send(404,{"error":"路径不存在"})

        def do_POST(self):
            # No cross-origin access to local font/dictionary paths or screenshots.
            origin = self.headers.get("Origin")
            if origin and origin not in {f"http://127.0.0.1:{args.port}",f"http://localhost:{args.port}"}:
                self.send(403,{"error":"仅允许本地工作台访问"}); return
            try:
                length = int(self.headers.get("Content-Length","0"))
                if length<=0 or length>24*1024*1024:
                    raise ValueError("请求大小须在 24 MB 以内")
                body=json.loads(self.rfile.read(length))
                if self.path == "/api/config":
                    font=FontSpec(str(body["font"]),int(body["px"]),int(body.get("index",0)),body["backend"])
                    settings=dict(state["settings"],threshold=float(body["threshold"]),
                                  min_margin=float(body["margin"]))
                    word_path=body.get("words") or state["word_path"]
                    language=body.get("language") or state["language"]
                    lexicon=load_words(word_path,language)
                    result=configure(font,lexicon,settings)
                    state.update(word_path=str(word_path),language=language)
                    self.send(200,result)
                elif self.path == "/api/read":
                    raw=base64.b64decode(body["image"].split(",",1)[-1],validate=True)
                    with Image.open(io.BytesIO(raw)) as source:
                        if source.width*source.height>25_000_000:
                            raise ValueError("图片超过 2500 万像素，请先裁切")
                        image=cv2.cvtColor(np.asarray(source.convert("RGB")),cv2.COLOR_RGB2BGR)
                    x,y,w,h=map(int,body["roi"])
                    if min(x,y)<0 or min(w,h)<=0 or x+w>image.shape[1] or y+h>image.shape[0]:
                        raise ValueError("选框超出图片范围")
                    crop=image[y:y+h,x:x+w]
                    reader=state["numbers"] if body.get("mode")=="numbers" else state["matcher"]
                    reader.recognize(crop)  # warm execution, not an answer cache
                    result=reader.recognize(crop).to_dict()
                    truth=str(body.get("truth",""))
                    result["ms_per_char"]=result["elapsed_ms"]/len(truth) if truth else None
                    self.send(200,result)
                else:
                    self.send(404,{"error":"路径不存在"})
            except (ValueError,KeyError,OSError,TypeError,cv2.error) as exc:
                self.send(400,{"error":str(exc)})

    server=HTTPServer(("127.0.0.1",args.port),Handler)
    print(f"记艺线字形实验台：http://127.0.0.1:{args.port}",flush=True)
    print(f"词表 {len(state['matcher'].words)} 词；字体 {spec.source}，{spec.px} px。Ctrl+C 退出。",flush=True)
    if args.open:
        import webbrowser
        webbrowser.open(f"http://127.0.0.1:{args.port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
