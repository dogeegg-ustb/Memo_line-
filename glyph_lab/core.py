from __future__ import annotations

import hashlib
import json
import re
from dataclasses import asdict, dataclass
from pathlib import Path
from time import perf_counter

import cv2
import numpy as np

from .dictionary import clean_words
from .fonts import FontSpec, make_renderer

VERSION = 3


def strokes(gray: np.ndarray, px: int) -> np.ndarray:
    k = max(5, round(px * .4)) | 1
    # OpenCV medianBlur >5 only supports uint8, hence the explicit input contract.
    bg = cv2.medianBlur(gray, k)
    return cv2.absdiff(gray, bg)


def bounds(m: np.ndarray):
    peak = int(m.max()) if m.size else 0
    if peak < 15:
        return None
    ys, xs = np.nonzero(m > max(12, peak * .30))
    if not len(xs):
        return None
    return int(xs.min()), int(ys.min()), int(xs.max()+1), int(ys.max()+1)


def feature(m: np.ndarray) -> np.ndarray:
    h, w = m.shape
    m = cv2.resize(m.astype(np.float32), (max(4, round(24*w/h)), 24),
                   interpolation=cv2.INTER_AREA)
    gx = cv2.Sobel(m, cv2.CV_32F, 1, 0); gy = cv2.Sobel(m, cv2.CV_32F, 0, 1)
    magnitude, angle = cv2.cartToPolar(gx, gy)
    angle *= 4 / np.pi
    floor = np.floor(angle)
    bins = floor.astype(np.uint8) % 4; fraction = angle-floor
    channels = [cv2.resize(magnitude*((bins == k)*(1-fraction) +
                          (((bins+1) % 4) == k)*fraction), (32, 4),
                          interpolation=cv2.INTER_AREA) for k in range(4)]
    v = np.sqrt(np.stack(channels).ravel())
    return v / (np.linalg.norm(v)+1e-6)


@dataclass
class MatchResult:
    text: str | None
    accepted: bool
    score: float
    margin: float
    reason: str
    elapsed_ms: float
    box: tuple | None = None
    candidates: list | None = None
    stages_ms: dict | None = None

    def to_dict(self):
        return asdict(self)


class GlyphMatcher:
    """One configured font/size and a lexicon. Input is one line, not a full panel.

    No input-result cache: every call runs preprocessing, retrieval and correlation.
    Build/cache cost is paid before recognition. Instances are read-only after build.
    """
    def __init__(self, words, font: FontSpec, *, cache_dir=None, top_k=16,
                 threshold=.84, min_margin=.04):
        self.words = clean_words(words)
        self.requested_words = len(self.words)
        if not self.words:
            raise ValueError("词表为空")
        self.font = font
        self.top_k = max(1, int(top_k))
        self.threshold = float(threshold)
        self.min_margin = float(min_margin)
        if not 0 < self.threshold <= 1 or not 0 <= self.min_margin <= 1:
            raise ValueError("threshold 须为 (0,1]，margin 须为 [0,1]")
        self.cache_hit = False
        start = perf_counter()
        identity = {"version": VERSION, "font": font.identity(), "words": self.words}
        self.cache_key = hashlib.sha256(json.dumps(identity, ensure_ascii=False,
                                          sort_keys=True).encode()).hexdigest()
        cache = Path(cache_dir)/f"{self.cache_key}.npz" if cache_dir else None
        if cache and cache.exists():
            try:
                self._load(cache)
                self.cache_hit = True
            except (ValueError, KeyError, OSError):
                self._build()
        else:
            self._build()
        if cache and not self.cache_hit:
            cache.parent.mkdir(parents=True, exist_ok=True)
            # Atomic replacement; incomplete writes never become reusable dictionaries.
            import tempfile, os
            with tempfile.NamedTemporaryFile(dir=cache.parent, suffix=".npz", delete=False) as f:
                temp = Path(f.name)
            try:
                np.savez_compressed(temp, identity=np.array(self.cache_key),
                    features=self.features, widths=self.widths, heights=self.heights,
                    pixels=np.concatenate([t.ravel() for t in self.templates]),
                    words=np.array(self.words), labels=np.array(self.labels))
                os.replace(temp, cache)
            finally:
                temp.unlink(missing_ok=True)
        # Width bucketing before the matrix multiply avoids scanning a large lexicon.
        self.width_index = {int(w): np.flatnonzero(self.widths == w)
                            for w in np.unique(self.widths)}
        # A small low-pass filter tolerates antialiasing/subpixel changes without
        # changing stroke geometry. Keep border pixels for correlation alignment.
        self.fine_templates = [cv2.GaussianBlur(np.pad(t,2).astype(np.float32), (3,3), .65)
                               for t in self.templates]
        self.small_components = []
        for label,t in zip(self.labels,self.templates):
            components = []
            if any(ch in label for ch in ".:,;!?%°。，：；！？[](){}【】（）"):
                count,labels,stats,_ = cv2.connectedComponentsWithStats((t>t.max()*.35).astype(np.uint8))
                for c in range(1,count):
                    if (stats[c,cv2.CC_STAT_AREA] <= max(12, self.font.px*.8)
                            or stats[c,cv2.CC_STAT_WIDTH] <= max(3, self.font.px*.2)):
                        ys,xs=np.nonzero(labels == c)
                        components.append((ys+2,xs+2))
                # A bracket can touch its neighboring character. Check the edge
                # strip too, instead of assuming punctuation is a separate blob.
                for edge,chars in (("left","[({【（"),("right","])}】）")):
                    if (label[0] if edge == "left" else label[-1]) in chars:
                        mask = t>t.max()*.35
                        if edge == "left": mask[:,2:] = False
                        else: mask[:,:-2] = False
                        ys,xs=np.nonzero(mask)
                        if len(xs): components.append((ys+2,xs+2))
            self.small_components.append(components)
        self.memory_bytes = (self.features.nbytes + sum(t.nbytes for t in self.templates)
                             + sum(t.nbytes for t in self.fine_templates))
        self.build_ms = (perf_counter()-start)*1000

    def _build(self):
        renderer = make_renderer(self.font)
        self.templates = []; features = []; self.labels = []
        try:
            for word in self.words:
                if not renderer.supports(word):
                    continue  # Never build identical tofu boxes as if they were text.
                for scale in (1, 2):
                    m = strokes(renderer.render(word, supersample=scale), self.font.px)
                    box = bounds(m)
                    if box is None:
                        continue
                    x0,y0,x1,y1 = box
                    t = m[y0:y1,x0:x1].copy()
                    self.templates.append(t); features.append(feature(t)); self.labels.append(word)
        finally:
            renderer.close()
        if not self.labels:
            raise ValueError("字体未产生可用字形")
        self.words = sorted(set(self.labels))
        self.features = np.stack(features).astype(np.float32)
        self.widths = np.array([t.shape[1] for t in self.templates], np.int32)
        self.heights = np.array([t.shape[0] for t in self.templates], np.int32)

    def _load(self, path):
        with np.load(path, allow_pickle=False) as data:
            if str(data["identity"]) != self.cache_key:
                raise ValueError("cache mismatch")
            self.words = data["words"].tolist()
            self.labels = data["labels"].tolist()
            self.features = data["features"]
            self.widths = data["widths"]; self.heights = data["heights"]
            pixels = data["pixels"]; offset = 0; self.templates = []
            for w,h in zip(self.widths, self.heights):
                count = int(w*h)
                self.templates.append(pixels[offset:offset+count].reshape(int(h),int(w)))
                offset += count
            if offset != len(pixels) or len(self.features) != len(self.labels):
                raise ValueError("incomplete cache")

    def prepare(self, image):
        if image is None or not image.size or image.dtype != np.uint8:
            raise ValueError("图像须为非空 uint8 数组")
        if image.ndim == 3 and image.shape[2] in (3, 4):
            gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY if image.shape[2] == 3
                                else cv2.COLOR_BGRA2GRAY)
        elif image.ndim == 2:
            gray = image
        else:
            raise ValueError("图像须为灰度/BGR/BGRA")
        px = self.font.px
        m = strokes(np.ascontiguousarray(gray), px)
        mask = (m > max(12, int(m.max())*.15)).astype(np.uint8)
        # Remove long rules and slider lines. A minus sign is shorter than 3 em.
        horizontal = cv2.morphologyEx(mask, cv2.MORPH_OPEN,
                                      np.ones((1, max(3*px, 12)), np.uint8))
        vertical = cv2.morphologyEx(mask, cv2.MORPH_OPEN,
                                    np.ones((max(round(1.8*px), 12), 1), np.uint8))
        lines = cv2.bitwise_or(horizontal, vertical)
        m[lines != 0] = 0
        box = bounds(m)
        return m, box

    def recognize(self, image: np.ndarray) -> MatchResult:
        start = perf_counter()
        stages = {}
        m, box = self.prepare(image)
        stages["prepare"] = (perf_counter()-start)*1000
        def result(text=None, score=0., margin=0., reason="no_ink", candidates=None):
            return MatchResult(text, text is not None, float(score), float(margin), reason,
                               (perf_counter()-start)*1000, box, candidates or [], stages)
        if box is None:
            return result()
        x0,y0,x1,y1 = box; w=x1-x0; h=y1-y0
        if h > self.font.px*1.6:
            return result(reason="multiple_lines_or_wrong_size")
        stage = perf_counter()
        tolerance = max(2, round(self.font.px*.12))
        groups = [self.width_index[v] for v in range(w-tolerance, w+tolerance+1)
                  if v in self.width_index]
        if not groups:
            return result(reason="no_candidate_width")
        ids = np.concatenate(groups)
        ids = ids[np.abs(self.heights[ids]-h) <= max(2, round(self.font.px*.15))]
        if not len(ids):
            return result(reason="no_candidate_height")
        v = feature(m[y0:y1,x0:x1])
        # einsum is a single-thread reduction; tiny GEMV calls can trigger expensive
        # BLAS thread scheduling. No process-wide environment/thread changes needed.
        similarities = np.einsum("ij,j->i", self.features[ids], v, optimize=False)
        n = min(self.top_k, len(ids))
        selected = np.argpartition(-similarities, n-1)[:n]
        stages["coarse"] = (perf_counter()-stage)*1000
        stage = perf_counter()
        raw_source = np.pad(m[y0:y1,x0:x1], 5).astype(np.float32)
        source = cv2.GaussianBlur(raw_source, (3,3), .65)
        dilated = cv2.dilate(raw_source,np.ones((3,3),np.uint8))
        fine = []
        for local in selected:
            i = int(ids[local]); template = self.fine_templates[i]
            corr = cv2.matchTemplate(source, template, cv2.TM_CCOEFF_NORMED)
            _, score, _, location = cv2.minMaxLoc(corr)
            # Account for all detected ink, including a suffix/punctuation outside
            # the matching rectangle; never silently accept just a substring.
            tx,ty=location; th,tw=template.shape
            # Ignore the template's two-pixel padding in this coverage check.
            outside = raw_source.copy()
            outside[ty+2:ty+th-2,tx+2:tx+tw-2] = 0
            if np.count_nonzero(outside > max(20.,float(raw_source.max())*.40)) >= 2:
                score = min(score, .5)
            for ys,xs in self.small_components[i]:
                explained = dilated[ty+ys,tx+xs] > max(15.,float(raw_source.max())*.25)
                if float(explained.mean()) < .5:
                    score = min(score,.5)
            # A long label must not hide one wrong character in a high average.
            for left in range(2, tw-2, self.font.px):
                right = min(tw-2, left+self.font.px)
                if right-left < self.font.px*.35:
                    continue
                patch = source[max(0,ty-1):ty+th+1,tx+left-1:tx+right+1]
                local_score = float(cv2.matchTemplate(patch, template[:,left:right],
                                                      cv2.TM_CCOEFF_NORMED).max())
                score = min(score, local_score)
            fine.append({"text": self.labels[i], "score": round(score, 6),
                         "coarse": round(float(similarities[local]), 6)})
        fine.sort(key=lambda x: x["score"], reverse=True)
        seen = set()
        fine = [c for c in fine if not (c["text"] in seen or seen.add(c["text"]))]
        stages["fine"] = (perf_counter()-stage)*1000
        best = fine[0]; second = fine[1]["score"] if len(fine)>1 else 0.
        margin = best["score"]-second
        if best["score"] < self.threshold:
            return result(score=best["score"], margin=margin, reason="low_score", candidates=fine)
        if margin < self.min_margin:
            return result(score=best["score"], margin=margin, reason="ambiguous", candidates=fine)
        return result(best["text"], best["score"], margin, "matched", fine)


class NumberMatcher:
    """Individual glyph matching with a shared baseline and numeric grammar."""
    PATTERN = re.compile(r"[+−-]?\d+(?:[.,]\d+)?[%°]?")

    def __init__(self, font, **kwargs):
        self.reader = GlyphMatcher(list("0123456789.,-+%°"), font, **kwargs)
        self.templates = []
        renderer = make_renderer(font)
        try:
            for label in self.reader.words:
                for scale in (1,2):
                    image,baseline = renderer.render_with_baseline(label,scale)
                    m = strokes(image,font.px)
                    box = bounds(m)
                    if box is None: continue
                    x0,y0,x1,y1 = box
                    raw = m[y0:y1,x0:x1]
                    # Fractional positioning inside a string can spread a glyph
                    # across one extra column. Preserve its vertical geometry.
                    widths = {raw.shape[1],raw.shape[1]+1}
                    if label in ".,": widths.add(max(1,raw.shape[1]-1))
                    for width in sorted(widths):
                        raster = cv2.resize(raw,(width,raw.shape[0]),interpolation=cv2.INTER_LINEAR)
                        template = cv2.GaussianBlur(np.pad(raster,2).astype(np.float32),
                                                   (3,3),.65)[:,2:-2]
                        self.templates.append((label,template,baseline-y0+2))
        finally:
            renderer.close()

    def recognize(self, image):
        start = perf_counter()
        m, box = self.reader.prepare(image)
        if box is None:
            return MatchResult(None, False, 0., 0., "no_ink", (perf_counter()-start)*1000)
        x0,y0,x1,y1 = box
        # Retain faint descenders beyond the main digit box without changing
        # horizontal segmentation to include antialiasing fringes.
        y0=max(0,y0-2); y1=min(m.shape[0],y1+2)
        ink = m[y0:y1,x0:x1]
        cutoff = max(15, int(m.max())*.30)
        tail_cutoff = max(12, int(m.max())*.20)
        if ink.shape[0] > self.reader.font.px*1.6 or ink.shape[1] > self.reader.font.px*32:
            return MatchResult(None,False,0.,0.,"invalid_numeric_region",(perf_counter()-start)*1000,box)
        raw_source = np.pad(ink, ((4,4),(2,2))).astype(np.float32)
        source = cv2.GaussianBlur(raw_source,(3,3),.65)
        col = (raw_source > cutoff).any(axis=0)
        if not col.any():
            return MatchResult(None,False,0.,0.,"low_contrast",(perf_counter()-start)*1000,box)
        integral = cv2.integral((raw_source>cutoff).astype(np.uint8))
        width = source.shape[1]
        first = int(np.flatnonzero(col)[0]); last = int(np.flatnonzero(col)[-1])+1
        edges = {}
        # Match each character across the line once. Unlike splitting connected
        # components, this also handles adjacent digits whose antialiasing touches.
        for label,template,baseline in self.templates:
            th,tw = template.shape
            if th <= source.shape[0] and tw <= source.shape[1]:
                corr = cv2.matchTemplate(source,template,cv2.TM_CCOEFF_NORMED)
                # Reject partial-height matches *before* choosing the vertical
                # offset, so the best valid placement can still be found.
                full = integral[-1,tw:] - integral[-1,:-tw]
                inside = (integral[th-2:raw_source.shape[0]-1,tw:] -
                          integral[2:raw_source.shape[0]-th+3,tw:] -
                          integral[th-2:raw_source.shape[0]-1,:-tw] +
                          integral[2:raw_source.shape[0]-th+3,:-tw])
                corr[(full[None,:]-inside)>=2] = -1
                yy,xx = np.nonzero(corr>=self.reader.threshold)
                for y,begin in zip(yy.tolist(),xx.tolist()):
                    end = begin+tw
                    line = baseline+y
                    if label in ".,":
                        descender = (raw_source[max(0,round(line)):,begin:end]>tail_cutoff).any()
                        if (label == ",") != bool(descender): continue
                    next_pos=end
                    while next_pos<width and not col[next_pos]: next_pos+=1
                    if next_pos-end>self.reader.font.px*.5: continue
                    edges.setdefault(begin,[]).append((label,tw,float(corr[y,begin]),line,next_pos))
        # Grammar states: start, integer, separator, fraction, suffix.
        def transition(state,ch):
            if ch.isdigit(): return 3 if state in (2,3) else (1 if state in (0,1,5) else None)
            if ch in "+-" and state == 0: return 5
            if ch in ".," and state == 1: return 2
            if ch in "%°" and state in (1,3): return 4
            return None
        paths = {first:[(0.,"",1.,0,[],None)]}
        complete = []
        for pos in range(width):
            for cost,text,lowest,state,tokens,line_baseline in paths.get(pos,[])[:12]:
                starts = [pos] + ([pos-1] if pos>0 and not col[pos-1] else [])
                for begin in starts:
                    for label,tw,score,line,next_pos in edges.get(begin,[]):
                        next_state = transition(state,label)
                        if next_state is None: continue
                        if line_baseline is not None and abs(line-line_baseline)>1.25: continue
                        item = (cost+(1-score)*tw, text+label,min(lowest,score),next_state,
                                tokens+[(label,tw,score)],
                                line if line_baseline is None else line_baseline)
                        if next_pos >= last and next_state in (1,3,4):
                            complete.append(item)
                        elif next_pos > pos:
                            bucket = paths.setdefault(next_pos,[])
                            # Multiple raster variants must not fill the beam with
                            # copies of one spelling and hide an ambiguous reading.
                            key = (item[1],round(item[-1]*2))
                            duplicate = next((i for i,p in enumerate(bucket)
                                              if (p[1],round(p[-1]*2)) == key),None)
                            if duplicate is not None:
                                if bucket[duplicate][0] <= item[0]: continue
                                bucket.pop(duplicate)
                            bucket.append(item)
                            bucket.sort(key=lambda x:x[0]); del bucket[12:]
        complete.sort(key=lambda x:x[0])
        unique = {}
        for path in complete:
            unique.setdefault(path[1],path)
        best = list(unique.values())
        if not best:
            return MatchResult(None,False,0.,0.,"unknown_numeric_glyph",(perf_counter()-start)*1000,box)
        cost,text,lowest,_,tokens,_ = best[0]
        if len(best)>1:
            other = best[1][4]
            different_width = sum(a[1] for a,b in zip(tokens,other) if a[0] != b[0])
            if len(tokens) != len(other): different_width = width
            margin = (best[1][0]-cost)/max(1,different_width)
        else:
            margin = 1.
        valid = bool(self.PATTERN.fullmatch(text)) and margin >= self.reader.min_margin
        return MatchResult(text if valid else None,valid,lowest,margin,
            "matched" if valid else "ambiguous_number",(perf_counter()-start)*1000,box,
            [{"text":p[1],"cost":p[0],"score":p[2]} for p in best[:3]])
