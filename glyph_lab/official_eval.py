"""Evaluate the unchanged glyph readers on annotated official CSP screenshots.

Font selection reads calibration images only. Test labels never build templates
or enter the lexicon: the evaluation uses the project's CSP translation database.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict
import hashlib
import json
from pathlib import Path
import platform
import sys

import cv2
import numpy as np

from .benchmark import measure
from .core import GlyphMatcher, NumberMatcher
from .dictionary import load_words
from .fonts import FontSpec, default_font
from .official_csp import DATA, HERE

LANGUAGES = {"en-us":"english","zh-tw":"chinese_tc","ja-jp":"japanese"}
DATABASE = HERE.parent/"recognizer/CSPevent/csp_strings.sqlite3"


def inputs(split):
    sources=json.loads((DATA/"sources.json").read_text(encoding="utf-8"))
    source_map={s["id"]:s for s in sources["images"]}
    annotations=json.loads((DATA/"annotations.json").read_text(encoding="utf-8"))["samples"]
    calibration_ids={s["source_id"] for s in annotations if s["split"]=="calibration"}
    test_ids={s["source_id"] for s in annotations if s["split"]=="test"}
    if calibration_ids & test_ids:
        raise ValueError("Calibration and test images must be disjoint")
    calibration_hashes={source_map[s]["sha256"] for s in calibration_ids}
    if calibration_hashes & {source_map[s]["sha256"] for s in test_ids}:
        raise ValueError("Identical images appear in calibration and test")
    images={}; rows=[]
    for row in annotations:
        if row["split"] != split: continue
        source=source_map[row["source_id"]]
        if row["source_id"] not in images:
            data=(DATA/source["image"]).read_bytes()
            if hashlib.sha256(data).hexdigest()!=source["sha256"]:
                raise ValueError(f'Changed fixture: {row["source_id"]}')
            images[row["source_id"]]=cv2.imdecode(np.frombuffer(data,np.uint8),cv2.IMREAD_COLOR)
        image=images[row["source_id"]]
        x,y,w,h=row["roi"]
        if min(x,y)<0 or min(w,h)<=0 or x+w>image.shape[1] or y+h>image.shape[0]:
            raise ValueError(f'Invalid ROI: {row["id"]}')
        crop=image[y:y+h,x:x+w].copy()
        rows.append((row,crop))
    return rows


def candidate_fonts():
    font_files=[(default_font().source,default_font().index),
                ("/System/Library/Fonts/Supplemental/Tahoma.ttf",0),
                ("/System/Library/Fonts/Supplemental/Arial.ttf",0),
                ("/System/Library/Fonts/Supplemental/Arial Unicode.ttf",0),
                ("/System/Library/Fonts/Hiragino Sans GB.ttc",0)]
    font_files.extend((str(p),0) for p in Path("/System/Library/Fonts").glob("* W3.ttc")
                      if "ヒラ" in p.name)
    seen=set()
    for source,index in font_files:
        if (source,index) in seen or not Path(source).is_file(): continue
        seen.add((source,index))
        for px in range(10,16):
            yield FontSpec(source,px,index)


def calibrate():
    samples=inputs("calibration")
    output={"method":"Choose installed font/size using calibration images only; not proof of the screenshot's actual font.",
            "profiles":{},"scores":{}}
    fonts=list(candidate_fonts())
    for language in LANGUAGES:
        for mode in ("words","numbers"):
            rows=[(r,im) for r,im in samples if r["language"]==language and r["mode"]==mode]
            # Calibration-only labels may be used to compare font geometry.
            # Evaluation below always loads the external full CSP lexicon.
            words=sorted({r["text"] for r,_ in rows})
            scores=[]
            for spec in fonts:
                try:
                    matcher=GlyphMatcher(words,spec) if mode=="words" else NumberMatcher(spec)
                except ValueError as exc:
                    if "字体未产生可用字形" not in str(exc): raise
                    continue
                results=[matcher.recognize(im) for _,im in rows]
                correct=sum(result.text==row["text"] for (row,_),result in zip(rows,results))
                wrong=sum(result.accepted and result.text!=row["text"] for (row,_),result in zip(rows,results))
                true_scores=[max([c["score"] for c in (r.candidates or []) if c["text"]==row["text"]],default=0.)
                             for (row,_),r in zip(rows,results)]
                scores.append({"font":asdict(spec),"samples":len(rows),"correct":correct,
                               "wrong":wrong,"mean_true_score":float(np.mean(true_scores))})
            scores.sort(key=lambda s:(s["correct"]-s["wrong"],s["mean_true_score"]),reverse=True)
            key=language+"/"+mode
            chosen=scores[0]["font"]
            # With no accepted number, no numeric candidate is validated. Use
            # the word-size candidate explicitly as an unverified fallback.
            if mode=="numbers" and scores[0]["correct"]==0:
                chosen=output["profiles"][language+"/words"]
            output["profiles"][key]=chosen
            output["scores"][key]=scores
            print(key,json.dumps(scores[0],ensure_ascii=False),flush=True)
    (DATA/"calibration.json").write_text(json.dumps(output,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")


def evaluate(rounds, profiles_path=None):
    samples=inputs("test")
    selection=json.loads((profiles_path or DATA/"calibration.json").read_text(encoding="utf-8"))
    report={"environment":{"platform":platform.platform(),"python":sys.version.split()[0],
                           "opencv":cv2.__version__,"numpy":np.__version__},
            "annotation_sha256":hashlib.sha256((DATA/"annotations.json").read_bytes()).hexdigest(),
            "matcher_sha256":hashlib.sha256((HERE/"core.py").read_bytes()).hexdigest(),
            "database_sha256":hashlib.sha256(DATABASE.read_bytes()).hexdigest(),
            "test_regions":len(samples),"test_images":len({r["source_id"] for r,_ in samples}),
            "unique_visual_crops":len({(im.shape,hashlib.sha256(im.tobytes()).hexdigest()) for _,im in samples}),
            "unique_texts":len({r["text"] for r,_ in samples}),
            "scope":"Fixed font candidates and unchanged matcher thresholds; current image-collection smoke test, not deployment acceptance.",
            "timing":"Warm complete recognize() calls; ground-truth characters incl. punctuation; excludes image I/O, manual ROI selection, font calibration and template initialization.",
            "limitation":"Assistant visually transcribed ROIs; not independently human-reviewed. Some UI regions repeat across languages. MS UI Gothic from prior experiments is unavailable on this Mac; the actual font of this collected set remains unverified.",
            "runs":{}}
    details={}
    for variant in ("previous_default","calibrated_available_font"):
        groups={};details[variant]={}
        for language,db_language in LANGUAGES.items():
            words=load_words(DATABASE,db_language)
            for mode in ("words","numbers"):
                key=language+"/"+mode
                spec=default_font(18) if variant=="previous_default" else FontSpec(**selection["profiles"][key])
                print(f'Building {variant} {key} {Path(spec.source).name} {spec.px}px',flush=True)
                matcher=GlyphMatcher(words,spec,cache_dir=HERE/".cache") if mode=="words" else NumberMatcher(spec,cache_dir=HERE/".cache")
                reader=matcher if mode=="words" else matcher.reader
                available=set(reader.words)
                rows=[(r,im) for r,im in samples if r["language"]==language and r["mode"]==mode]
                cases=[(r["text"],im,mode=="numbers" or r["text"] in available,r.get("kind","body")) for r,im in rows]
                summary,results=measure(matcher,cases,rounds)
                summary.update(font=asdict(spec),dictionary_words=len(available),
                               requested_words=reader.requested_words,
                               exact_correct=sum(x["truth"]==x["predicted"] for x in results),
                               safe_rejections=sum(x["predicted"] is None for x in results),
                               wrong_predictions=sum(x["predicted"] is not None and x["predicted"]!=x["truth"] for x in results))
                summary["by_kind"]={}
                for kind in sorted({r["kind"] for r in results}):
                    subset=[r for r in results if r["kind"]==kind]
                    summary["by_kind"][kind]={"samples":len(subset),
                        "known":sum(r["known"] for r in subset),
                        "correct":sum(r["predicted"]==r["truth"] for r in subset),
                        "wrong":sum(r["predicted"] is not None and r["predicted"]!=r["truth"] for r in subset)}
                for (row,im),result in zip(rows,results):
                    result.update(id=row["id"],source_id=row["source_id"],roi=row["roi"])
                groups[key]=summary;details[variant][key]=results
                print(key,{k:summary[k] for k in ["samples","known","exact_correct","wrong_predictions","safe_rejections","ms_per_char_p95"]},flush=True)
                del matcher
        report["runs"][variant]=groups
    output=DATA/"runs";output.mkdir(exist_ok=True)
    (output/"cases.json").write_text(json.dumps(details,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    (DATA/"report.json").write_text(json.dumps(report,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print("Saved",DATA/"report.json",flush=True)


def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument("command",choices=["calibrate","evaluate"])
    ap.add_argument("--rounds",type=int,default=5)
    ap.add_argument("--profiles",type=Path,help="JSON with profiles mapping language/mode to FontSpec; permits verified Windows GDI fonts")
    args=ap.parse_args()
    if args.rounds<1:ap.error("rounds must be positive")
    if args.command=="calibrate":calibrate()
    else:evaluate(args.rounds,args.profiles)


if __name__=="__main__":main()
