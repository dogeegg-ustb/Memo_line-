"""Which font does CSP use for each UI language? Score candidate fonts on the official manual screenshots.

For each language: detect text lines in that language's manual screenshots, then for every candidate
font match the line against that language's CSP strings (coarse orientation prefilter -> grey NCC at
the line's scale). The font whose renders explain the lines best is the UI font.
"""
import sys, os, json, sqlite3, collections, statistics, ctypes as C, ctypes.wintypes as W
import numpy as np, cv2
HERE = os.path.dirname(os.path.abspath(__file__))
GT = os.path.join(os.path.dirname(HERE), 'glyphtest')
sys.path.insert(0, GT)
sys.stdout.reconfigure(encoding='utf-8')
import gdi_match as G
from gdi_match import render, score
from coarse import coarse

DB = 'D:/Memo_Line/Memo_Line/recognizer/CSPevent/csp_strings.sqlite3'
LANG = {'en': 'english', 'ja': 'japanese', 'ko': 'korean', 'tc': 'chinese_tc', 'fr': 'french', 'de': 'german', 'es': 'spanish'}
LATIN = ['Segoe UI', 'Tahoma', 'Microsoft Sans Serif', 'Arial', 'Yu Gothic UI']
FONTS = {'en': LATIN, 'fr': LATIN, 'de': LATIN, 'es': LATIN,
         'ja': ['MS UI Gothic', 'Yu Gothic UI', 'MS Gothic', 'Microsoft YaHei UI', 'SimSun'],
         'ko': ['Gulim', 'Malgun Gothic', 'Microsoft YaHei UI', 'SimSun'],
         'tc': ['PMingLiU', 'Microsoft JhengHei UI', 'SimSun', 'Microsoft YaHei UI']}
EM = 18

G.g.GetTextFaceW.argtypes = [W.HANDLE, C.c_int, W.LPWSTR]


def real_face(face):
    """GDI silently substitutes missing fonts (and reports some faces under localized names, 宋体 for
    SimSun), so compare the rendering with that of a font that surely does not exist."""
    probe = 'Layer 图层 レイヤー 레이어 0123'
    if face != 'SimSun' and np.array_equal(render(probe, face, EM, 4), render(probe, 'No Such Font Memoline', EM, 4)):
        G.g.SelectObject(G.dc, G.font(face, EM, 4))
        buf = C.create_unicode_buffer(64); G.g.GetTextFaceW(G.dc, 64, buf)
        return 'substituted:' + buf.value
    return face


def ink(g):
    hi = np.percentile(g[g > 0], 99) if (g > 0).any() else 1
    ys, xs = np.where(g > 0.35 * hi)
    return g[ys.min():ys.max() + 1, xs.min():xs.max() + 1]


def strokes(gray, k=7):
    return np.abs(gray.astype(np.float32) - cv2.medianBlur(gray, k).astype(np.float32))


def lexicon(lang):
    c = sqlite3.connect(f'file:{DB}?mode=ro', uri=True)
    ws = {t.strip() for (t,) in c.execute('select distinct text from resource_strings where language=? and length(text) between 2 and 24', (LANG[lang],))}
    return sorted(w for w in ws if w and '\n' not in w and '\r' not in w and '%' not in w)


def build(words, face):
    ent, F, A = [], [], []
    for w in words:
        g = render(w, face, EM, 4).astype(np.float32)
        if g.shape[0] < 3 or g.max() == 0:
            continue
        g = ink(strokes(np.pad(g.astype(np.uint8), 6)))
        v, a = coarse(g); ent.append((w, g.shape[0])); F.append(v); A.append(a)
    return ent, np.stack(F).astype(np.float32), np.array(A, np.float32)


def lines_of(lang, ocr, limit):
    out = []
    for f in sorted(os.listdir(HERE + '/img')):
        if not f.startswith(lang + '__'):
            continue
        im = cv2.imdecode(np.fromfile(HERE + '/img/' + f, np.uint8), cv2.IMREAD_GRAYSCALE)
        if im is None or im.shape[0] < 20:
            continue
        r = ocr(cv2.cvtColor(im, cv2.COLOR_GRAY2BGR), use_det=True, use_cls=False, use_rec=False)
        for box in (getattr(r, 'boxes', None) if getattr(r, 'boxes', None) is not None else []):
            xs = [p[0] for p in box]; ys = [p[1] for p in box]
            x0, y0, x1, y1 = int(min(xs)) - 3, int(min(ys)) - 3, int(max(xs)) + 4, int(max(ys)) + 4
            h = y1 - y0
            if not (14 <= h <= 40) or x1 - x0 < 2 * h:
                continue
            out.append((f, im[max(0, y0):y1, max(0, x0):x1]))
        if len(out) >= limit:
            break
    return out[:limit]


def best_for(line, ent, F, A, face, K=8):
    m = strokes(line)
    if m.max() < 20:
        return None
    i = ink(m)
    if i.shape[0] < 6:
        return None
    v, a = coarse(i); sim = F @ v; sim[np.abs(A / a - 1) > 0.15] = -1
    top = np.argpartition(-sim, K)[:K]
    best = (-1.0, None)
    for j in top:
        if sim[j] <= 0:
            continue
        w, h18 = ent[j]
        base = EM * i.shape[0] / h18
        for px in {round(base * f) for f in (0.94, 1.0, 1.06)}:
            t = ink(strokes(np.pad(render(w, face, max(8, px), 4), 6)))
            if t.shape[1] < 0.85 * i.shape[1]:
                continue
            s = score(m, t)
            if s > best[0]:
                best = (s, w)
    return best


if __name__ == '__main__':
    from rapidocr import RapidOCR
    ocr = RapidOCR(params={'Global.log_level': 'warning'})
    langs = sys.argv[1].split(',') if len(sys.argv) > 1 else list(FONTS)
    report = {}
    for lang in langs:
        words = lexicon(lang)
        lines = lines_of(lang, ocr, 160)
        print(f'== {lang}: {len(words)} words, {len(lines)} lines', flush=True)
        res = {}
        for face in FONTS[lang]:
            got = real_face(face)
            if got.lower() != face.lower():
                print(f'  {face}: NOT INSTALLED (GDI gives {got})', flush=True); continue
            ent, F, A = build(words, face)
            res[face] = [best_for(l, ent, F, A, face) for _, l in lines]
        faces = list(res)
        wins = collections.Counter(); per = {f: [] for f in faces}
        for k in range(len(lines)):
            sc = {f: (res[f][k][0] if res[f][k] else -1) for f in faces}
            if max(sc.values()) < 0.5:
                continue
            wins[max(sc, key=sc.get)] += 1
            for f in faces:
                per[f].append(sc[f])
        for f in faces:
            hi = sum(s >= 0.75 for s in per[f])
            print(f'  {f:24s} wins {wins[f]:4d}  median {statistics.median(per[f]) if per[f] else 0:.3f}  lines>=0.75 {hi}', flush=True)
        report[lang] = {f: {'wins': wins[f], 'median': statistics.median(per[f]) if per[f] else 0,
                            'examples': [(lines[k][0], res[f][k]) for k in range(len(lines)) if res[f][k] and res[f][k][0] >= 0.8][:8]}
                        for f in faces}
    json.dump(report, open(HERE + '/font_report.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
