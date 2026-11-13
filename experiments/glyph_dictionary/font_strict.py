"""Strict font check: OCR reads the line, only lines whose OCR text is a known label are kept,
and every candidate font renders that exact text at the size that best fits the line height.
Prints mean NCC per font per language and saves a side-by-side sample."""
import sys, os, json, collections
import numpy as np, cv2
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.join(os.path.dirname(HERE), 'glyphtest'))
sys.stdout.reconfigure(encoding='utf-8')
from gdi_match import render, score
from font_fontcheck import strokes, ink, real_face
from font_knowncheck import labels

FONTS = {
    'en': ['Tahoma', 'Segoe UI', 'Microsoft Sans Serif', 'Arial', 'Yu Gothic UI', 'MS UI Gothic'],
    'fr': ['Tahoma', 'Segoe UI', 'Microsoft Sans Serif', 'Arial', 'MS UI Gothic'],
    'de': ['Tahoma', 'Segoe UI', 'Microsoft Sans Serif', 'Arial', 'MS UI Gothic'],
    'es': ['Tahoma', 'Segoe UI', 'Microsoft Sans Serif', 'Arial', 'MS UI Gothic'],
    'ja': ['MS UI Gothic', 'MS PGothic', 'Yu Gothic UI', 'Microsoft YaHei UI'],
    'ko': ['Malgun Gothic', 'Gulim', 'SimSun', 'Microsoft YaHei UI', 'MS UI Gothic'],
    'tc': ['PMingLiU', 'Microsoft JhengHei UI', 'SimSun', 'Microsoft YaHei UI', 'MS UI Gothic'],
}


def fit(m, text, face):
    """best NCC over sizes 9-24 px; also return the size"""
    best = (-1, None)
    i = ink(m)
    for px in range(9, 25):
        t = ink(strokes(np.pad(render(text, face, px, 4), 6)))
        if t.shape[0] > m.shape[0] or t.shape[1] > m.shape[1] or abs(t.shape[0] - i.shape[0]) > 3:
            continue
        s = score(m, t)
        if s > best[0]:
            best = (s, px)
    return best


def main(langs):
    from rapidocr import RapidOCR
    ocr = RapidOCR(params={'Global.log_level': 'warning'})
    out = {}
    for lang in langs:
        labs = set(labels(lang))
        faces = [f for f in FONTS[lang] if not real_face(f).startswith('substituted')]
        scores = collections.defaultdict(list); sizes = collections.defaultdict(collections.Counter)
        n = 0
        for f in sorted(os.listdir(HERE + '/img')):
            if not f.startswith(lang + '__') or n >= 60:
                continue
            im = cv2.imdecode(np.fromfile(HERE + '/img/' + f, np.uint8), cv2.IMREAD_COLOR)
            if im is None or min(im.shape[:2]) < 30:
                continue
            r = ocr(im, use_det=True, use_cls=False, use_rec=True)
            if getattr(r, 'boxes', None) is None or getattr(r, 'txts', None) is None:
                continue
            for box, t in zip(r.boxes, r.txts):
                t = t.strip()
                if t not in labs:
                    continue
                xs = [p[0] for p in box]; ys = [p[1] for p in box]
                g = cv2.cvtColor(im[max(0, int(min(ys)) - 4):int(max(ys)) + 5, max(0, int(min(xs)) - 4):int(max(xs)) + 5], cv2.COLOR_BGR2GRAY)
                m = strokes(g)
                if m.max() < 20 or ink(m).shape[0] < 6:
                    continue
                n += 1
                for face in faces:
                    s, px = fit(m, t, face)
                    scores[face].append(max(s, 0)); sizes[face][px] += 1
        print(f'== {lang}: {n} known-label lines', flush=True)
        for face in faces:
            v = scores[face]
            print(f'  {face:24s} mean {np.mean(v) if v else 0:.3f}  p25 {np.percentile(v, 25) if v else 0:.3f}  size {sizes[face].most_common(2)}', flush=True)
        out[lang] = {face: float(np.mean(scores[face])) if scores[face] else 0 for face in faces}
        out[lang]['_lines'] = n
    json.dump(out, open(HERE + '/strict_report.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)


if __name__ == '__main__':
    main(sys.argv[1].split(','))
