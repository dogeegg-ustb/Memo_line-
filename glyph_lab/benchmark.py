"""Reproducible quality + latency measurement; synthetic results are labeled."""
from __future__ import annotations

import json
import platform
import random
import statistics
import sys
from pathlib import Path
from time import perf_counter

import cv2
import numpy as np
from PIL import Image, ImageDraw

from .core import GlyphMatcher, NumberMatcher
from .fonts import FontSpec, make_renderer


def synthetic(renderer, word, seed=0, *, stressed=True):
    rng = np.random.default_rng(seed)
    # Alternate rasterization is deliberately more demanding than template replay.
    ink = renderer.render(word, supersample=3 if stressed and seed % 3 == 0 else 1)
    h,w = ink.shape
    pad = 7
    yy,xx = np.mgrid[:h+2*pad,:w+2*pad]
    bg = (48 if seed%2 else 207) + xx*.015 + yy*.03
    if stressed:
        bg += rng.normal(0, 1.2, bg.shape)
    image = bg.copy()
    sign = 1 if seed%2 else -1
    contrast = (95 + seed % 100) if stressed else 150
    image[pad:pad+h,pad:pad+w] += sign*ink.astype(np.float32)*contrast/255
    if stressed and seed%4 == 0:
        # Selection background changes abruptly across the text; local median should remove it.
        image[:,image.shape[1]//2:] += 12*sign
    if stressed and seed%5 == 0:
        image[-2,:] = 105  # a panel rule, not a character
    return np.clip(image,0,255).astype(np.uint8)


def measure(matcher, cases, rounds=3):
    if not cases:
        return {"samples": 0}, []
    for _,image,_,_ in cases[:min(20,len(cases))]:
        matcher.recognize(image)
    rows = []
    # Interleave different inputs, not a long loop over one cached image.
    times = [[] for _ in cases]
    last = [None]*len(cases)
    wall = 0.
    for repeat in range(rounds):
        indices = list(range(len(cases))); random.Random(700+repeat).shuffle(indices)
        for i in indices:
            _, image, _, _ = cases[i]
            start = perf_counter(); r = matcher.recognize(image)
            elapsed = (perf_counter()-start)*1000
            times[i].append(elapsed); last[i] = r; wall += elapsed
    correct = wrong = rejected = oov_rejected = oov_accepted = known = 0
    for (truth,_,in_dict,kind), ts,r in zip(cases,times,last):
        if in_dict:
            known += 1
            if r.accepted and r.text == truth: correct += 1
            elif r.accepted: wrong += 1
            else: rejected += 1
        elif r.accepted:
            oov_accepted += 1
        else:
            oov_rejected += 1
        rows.append({"truth": truth, "known": in_dict, "kind": kind,
            "predicted": r.text, "score": r.score, "reason": r.reason,
            "line_ms": statistics.median(ts),
            "ms_per_char": statistics.median(ts)/max(1,len(truth)),
            "candidates": (r.candidates or [])[:3], "stages_ms": r.stages_ms})
    line_times = [v for ts in times for v in ts]
    char_times = [v/max(1,len(cases[i][0])) for i,ts in enumerate(times) for v in ts]
    chars = sum(max(1,len(c[0])) for c in cases)*rounds
    return {
        "samples": len(cases), "known": known, "correct": correct, "wrong_accepts": wrong,
        "known_rejected": rejected, "oov_rejected": oov_rejected, "oov_accepted": oov_accepted,
        "correct_coverage": correct/max(1,known),
        "accepted_precision": correct/max(1,correct+wrong),
        "line_ms_p50": float(np.median(line_times)), "line_ms_p95": float(np.percentile(line_times,95)),
        "ms_per_char_weighted": wall/chars,
        "ms_per_char_p50": float(np.median(char_times)),
        "ms_per_char_p95": float(np.percentile(char_times,95)),
        "within_1ms_per_char": sum(t<=1 for t in char_times)/len(char_times),
        "rounds": rounds, "timed_calls": len(line_times),
    }, rows


def benchmark(args, spec, words, kwargs):
    if args.samples < 1 or args.rounds < 1:
        raise ValueError("samples 和 rounds 须为正数")
    args.output.mkdir(parents=True, exist_ok=True)
    matcher = GlyphMatcher(words, spec, **kwargs)
    renderer = make_renderer(spec)
    rng = random.Random(20261113)
    selected = rng.sample(matcher.words, min(args.samples,len(matcher.words)))
    cases = [(w, synthetic(renderer,w,i), True,
              "alternate_raster" if i%3 == 0 else "native_raster_perturbed")
             for i,w in enumerate(selected)]
    summary, rows = measure(matcher, cases, args.rounds)
    groups = {"synthetic_words": summary}
    all_rows = {"synthetic_words": rows}
    # Separate same-raster control from perturbed and alternate-raster tests.
    for kind in ("native_raster_perturbed", "alternate_raster"):
        subset = [row for row in rows if row["kind"] == kind]
        groups[kind] = {"samples":len(subset),
            "correct":sum(r["predicted"] == r["truth"] for r in subset),
            "wrong_accepts":sum(r["predicted"] is not None and r["predicted"] != r["truth"] for r in subset)}
    # Near negatives: append punctuation or remove one character from known labels.
    # A prefix matcher would return a high score on many of these.
    word_set = set(matcher.words)
    negative_words = []
    for w in selected:
        for negative in (w+"甲", w+".", w[:-1].strip(), "未收录"+w[-2:]):
            if negative and negative not in word_set:
                negative_words.append(negative)
    negative_words = list(dict.fromkeys(negative_words))[:args.samples]
    negatives = [(w,synthetic(renderer,w,i+19),False,"near_negative")
                 for i,w in enumerate(negative_words)]
    groups["synthetic_oov"], all_rows["synthetic_oov"] = measure(matcher, negatives, args.rounds)
    # Numeric paths are evaluated on unseen combinations of single glyphs.
    numeric = NumberMatcher(spec, **kwargs)
    # Independent seed: changing the word lexicon cannot change numeric tests.
    numeric_rng = random.Random(91234)
    numbers = [f"{numeric_rng.randint(0,999)}{separator}{numeric_rng.randint(0,9)}"
               for separator in (".",",") for _ in range(min(args.samples,200))]
    numbers += ["0", "1", "8", "100%", "-12.5", "-12,5", "+3.0", "360°",
                "3.5", "156.2", "156,2", "699.6"]
    nc = [(w,synthetic(renderer,w,i),True,"number") for i,w in enumerate(numbers)]
    groups["synthetic_numbers"], all_rows["synthetic_numbers"] = measure(numeric,nc,args.rounds)
    invalid_numbers = ["12a3","3..5","12   34","1,2.3","--5","5-","+",".",
                       "1%%","1°2","abc","3:5","1/2","12.5甲"]
    invalid_cases = [(w,synthetic(renderer,w,seed),False,"invalid_number")
                     for w in invalid_numbers for seed in (0,1,3,41)]
    groups["invalid_numbers"], all_rows["invalid_numbers"] = measure(numeric,invalid_cases,args.rounds)
    renderer.close()
    if args.manifest:
        data = json.loads(args.manifest.read_text(encoding="utf-8"))
        real = []
        for row in data:
            path = args.manifest.parent / row["image"]
            image = cv2.imdecode(np.frombuffer(path.read_bytes(),np.uint8), cv2.IMREAD_COLOR)
            if image is None:
                raise ValueError(f"无法读取 {path}")
            x,y,w,h = row["roi"]
            if min(x,y)<0 or min(w,h)<=0 or x+w>image.shape[1] or y+h>image.shape[0]:
                raise ValueError(f"标注 ROI 超出图片范围：{path}")
            real.append((row["text"],image[y:y+h,x:x+w],row["text"] in word_set,"real"))
        groups["real_manifest"], all_rows["real_manifest"] = measure(matcher,real,args.rounds)
    report = {
        "environment": {"platform": platform.platform(),"machine":platform.machine(),
            "python":sys.version.split()[0],"opencv":cv2.__version__,"numpy":np.__version__},
        "font": spec.identity(), "dictionary_words":len(matcher.words),
        "requested_dictionary_words":matcher.requested_words,
        "unsupported_dictionary_words":matcher.requested_words-len(matcher.words),
        "build_ms": matcher.build_ms, "cache_hit":matcher.cache_hit,
        "template_memory_bytes": matcher.memory_bytes,
        "settings": {"top_k": matcher.top_k,"threshold":matcher.threshold,"margin":matcher.min_margin},
        "timing": "Warm full recognize() calls incl. preprocessing, retrieval, exact matching; "
                  "excludes disk I/O, rendering, dictionary initialization, UI and IPC. "
                  "Denominator is ground-truth input length, incl. rejected cases. No answer cache.",
        "limitation": "Synthetic screenshots are not actual CSP/PS screenshots. "
                      "Alternate raster uses 3x rendering then downsampling from the same font file.",
        "groups":groups,
    }
    (args.output/"report.json").write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
    (args.output/"cases.json").write_text(json.dumps(all_rows,ensure_ascii=False,indent=2),encoding="utf-8")
    # Visually inspect successes and failures without opening hundreds of files.
    failed = [(w,im) for (w,im,_,_),r in zip(cases,rows) if r["predicted"] != w][:12]
    examples = failed or [(w,im) for w,im,_,_ in cases[:12]]
    tile_w = min(800,max(320,max(im.shape[1] for _,im in examples)+20))
    contact = Image.new("RGB",(tile_w,len(examples)*65),(245,245,245))
    draw = ImageDraw.Draw(contact)
    for i,(_,im) in enumerate(examples):
        draw.text((6,i*65+2),f"case {i+1} ({'rejected or wrong' if failed else 'example'})",fill=(30,30,30))
        contact.paste(Image.fromarray(im).convert("RGB"),(6,i*65+20))
    contact.save(args.output/"samples.png")
    print(json.dumps(report,ensure_ascii=False,indent=2))
    print(f"\n详细报告：{args.output.resolve() / 'report.json'}")
