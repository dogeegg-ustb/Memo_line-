"""Font check with known text: labels whose wording is fixed (Brush Size, Opacity, ...).

Seed labels are taken in English and translated through csp_strings.sqlite3 (same resource node),
so every language is checked on the same controls. Each detected text line in that language's
manual screenshots is matched against the seed labels in every candidate font; a line counts when
some (label, font) explains it; then every font is scored on that same label.
"""
import sys, os, json, sqlite3, collections, statistics
import numpy as np, cv2
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), 'glyphtest')); sys.path.insert(0, HERE)
sys.stdout.reconfigure(encoding='utf-8')
import gdi_match as G
from gdi_match import render, score
from font_fontcheck import real_face, strokes, ink, LANG

SEED = ['Brush Size', 'Opacity', 'Anti-aliasing', 'Stabilization', 'Adjust by speed', 'Vector magnet', 'Add sub tool',
        'Calligraphy', 'Marker', 'Pen', 'Pencil', 'Brush', 'Airbrush', 'Eraser', 'Blend', 'Fill', 'Gradient', 'Text',
        'Layer', 'Navigator', 'Tool property', 'Sub Tool', 'Blending mode', 'Normal', 'Multiply', 'Paper texture',
        'Texture density', 'Watercolor edge', 'Brush density', 'Hardness', 'Thickness', 'Direction', 'Spray size',
        'Color mixing', 'Amount of paint', 'Density of paint', 'Color stretch', 'Paper', 'Layer 1', 'Color Wheel',
        'Color Slider', 'Color Set', 'Hue', 'Saturation', 'Luminosity', 'Brightness', 'Color Circle', 'Zoom in',
        'Zoom out', 'Reset rotation', 'Flip horizontal', 'Flip vertical', 'Rotate left', 'Rotate right', 'Raster layer',
        'Vector layer', 'Lock transparent pixels', 'Clip at layer below', 'Set as reference layer', 'Draft layer']
FONTS = {
    'en': ['Segoe UI', 'Tahoma', 'Microsoft Sans Serif', 'Arial', 'Verdana', 'Yu Gothic UI'],
    'fr': ['Segoe UI', 'Tahoma', 'Microsoft Sans Serif', 'Arial', 'Verdana', 'Yu Gothic UI'],
    'de': ['Segoe UI', 'Tahoma', 'Microsoft Sans Serif', 'Arial', 'Verdana', 'Yu Gothic UI'],
    'es': ['Segoe UI', 'Tahoma', 'Microsoft Sans Serif', 'Arial', 'Verdana', 'Yu Gothic UI'],
    'ja': ['MS UI Gothic', 'MS PGothic', 'Yu Gothic UI', 'Meiryo UI', 'Microsoft YaHei UI', 'SimSun'],
    'ko': ['Gulim', 'Dotum', 'Malgun Gothic', 'Microsoft YaHei UI', 'SimSun'],
    'tc': ['PMingLiU', 'MingLiU', 'Microsoft JhengHei UI', 'Microsoft YaHei UI', 'SimSun'],
}
DB = 'D:/Memo_Line/Memo_Line/recognizer/CSPevent/csp_strings.sqlite3'


def labels(lang):
    c = sqlite3.connect(f'file:{DB}?mode=ro', uri=True)
    out = set()
    for s in SEED:
        for (f, p) in c.execute("select resource_file,node_path from resource_strings where language='english' and text=?", (s,)):
            for (t,) in c.execute('select text from resource_strings where resource_file=? and node_path=? and language=?', (f, p, LANG[lang])):
                if t and '\n' not in t and len(t.strip()) >= 2:
                    out.add(t.strip())
    return sorted(out)


def lines_of(lang, ocr):
    out = []
    for f in sorted(os.listdir(HERE + '/img')):
        if not f.startswith(lang + '__'):
            continue
        im = cv2.imdecode(np.fromfile(HERE + '/img/' + f, np.uint8), cv2.IMREAD_GRAYSCALE)
        if im is None or im.shape[0] < 20 or im.shape[1] < 40:
            continue
        r = ocr(cv2.cvtColor(im, cv2.COLOR_GRAY2BGR), use_det=True, use_cls=False, use_rec=False)
        boxes = getattr(r, 'boxes', None)
        for box in (boxes if boxes is not None else []):
            xs = [p[0] for p in box]; ys = [p[1] for p in box]
            x0, y0, x1, y1 = int(min(xs)) - 3, int(min(ys)) - 3, int(max(xs)) + 4, int(max(ys)) + 4
            if 10 <= y1 - y0 <= 40:
                out.append((f, (x0, y0), im[max(0, y0):y1, max(0, x0):x1]))
    return out


def main(langs):
    from rapidocr import RapidOCR
    ocr = RapidOCR(params={'Global.log_level': 'warning'})
    report = {}
    for lang in langs:
        labs = labels(lang)
        faces = []
        for f in FONTS[lang]:
            got = real_face(f)
            if got.lower() == f.lower():
                faces.append(f)
            else:
                print(f'  {f}: not installed (GDI gives {got})')
        refs = {}
        for f in faces:
            for s in labs:
                g = ink(strokes(np.pad(render(s, f, 18, 4), 6)))
                refs[(f, s)] = (g.shape[1] / g.shape[0], g.shape[0])
        lines = lines_of(lang, ocr)
        print(f'== {lang}: {len(labs)} labels, {len(lines)} lines, fonts {faces}', flush=True)
        per = collections.defaultdict(list); wins = collections.Counter(); ex = collections.defaultdict(list)
        cache = {}
        for fname, pos, line in lines:
            m = strokes(line)
            if m.max() < 20:
                continue
            i = ink(m)
            if i.shape[0] < 6:
                continue
            a = i.shape[1] / i.shape[0]
            scores = {}
            for (f, s), (ra, h18) in refs.items():
                if abs(ra / a - 1) > 0.18:
                    continue
                best = -1
                for px in {round(18 * i.shape[0] / h18 * k) for k in (0.95, 1.0, 1.05)}:
                    key = (f, s, px)
                    if key not in cache:
                        cache[key] = ink(strokes(np.pad(render(s, f, max(7, px), 4), 6)))
                    t = cache[key]
                    if t.shape[0] <= m.shape[0] and t.shape[1] <= m.shape[1] and t.shape[1] >= 0.85 * i.shape[1]:
                        best = max(best, score(m, t))
                scores[(f, s)] = best
            if not scores:
                continue
            (bf, bs), bv = max(scores.items(), key=lambda kv: kv[1])
            if bv < 0.7:
                continue
            wins[bf] += 1
            for f in faces:
                per[f].append(max(scores.get((f, bs), -1), 0))
            ex[bs].append((fname, round(bv, 3), bf))
        for f in faces:
            v = per[f]
            print(f'  {f:24s} wins {wins[f]:4d}  mean {np.mean(v) if v else 0:.3f}  median {statistics.median(v) if v else 0:.3f}', flush=True)
        print('  labels found:', {k: len(v) for k, v in sorted(ex.items(), key=lambda kv: -len(kv[1]))[:20]}, flush=True)
        report[lang] = {'wins': dict(wins), 'mean': {f: float(np.mean(per[f])) if per[f] else 0 for f in faces},
                        'examples': {k: v[:5] for k, v in ex.items()}}
    json.dump(report, open(HERE + '/known_report.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)


if __name__ == '__main__':
    main(sys.argv[1].split(','))
