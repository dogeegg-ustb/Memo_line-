"""Slice CSP icon atlases (each icon stored at s, 2s and 4s) and check the slicing by cross-scale agreement."""
import os, re, sys, json, collections
import numpy as np, cv2
from PIL import Image
sys.stdout.reconfigure(encoding='utf-8')
MS = 'D:/Memo_Line/Memo_Line/recognizer/CSPevent/extracted_icons/master_sheets'


def load(f):
    return np.asarray(Image.open(os.path.join(MS, f)).convert('RGBA'))


def atlas_slots(w, h):
    """21s x (4k)s layout: column of s, two columns of 2s, four columns of 4s."""
    for s in (20, 30, 40, 16, 24, 32):
        if w == 21 * s and h % (4 * s) == 0:
            k = h // s
            out = []
            for i in range(k):
                out.append([(0, i * s, s),
                            (s + (i % 2) * 2 * s, (i // 2) * 2 * s, 2 * s),
                            (5 * s + (i % 4) * 4 * s, (i // 4) * 4 * s, 4 * s)])
            return s, out
    return None, None


def norm_alpha(a, n=32):
    return cv2.resize(a.astype(np.float32), (n, n), interpolation=cv2.INTER_AREA)


def ncc(a, b):
    a = a - a.mean(); b = b - b.mean()
    d = np.sqrt((a * a).sum() * (b * b).sum())
    return float((a * b).sum() / d) if d > 0 else 0.0


if __name__ == '__main__':
    rows = []
    stats = collections.Counter()
    for f in sorted(os.listdir(MS)):
        im = load(f); h, w = im.shape[:2]
        s, slots = atlas_slots(w, h)
        if s is None:
            stats['not_atlas'] += 1; continue
        stats['atlas'] += 1
        for i, variants in enumerate(slots):
            crops = [im[y:y + z, x:x + z] for x, y, z in variants]
            alphas = [c[:, :, 3] for c in crops]
            if max(int((a > 16).sum()) for a in alphas) < 6:
                stats['empty_slot'] += 1; continue
            big = norm_alpha(alphas[2]); agree = min(ncc(norm_alpha(alphas[0]), big), ncc(norm_alpha(alphas[1]), big))
            rgb = crops[2][:, :, :3][alphas[2] > 128]
            mono = bool(len(rgb) == 0 or rgb.std(axis=0).max() < 8)
            rows.append({'sheet': f, 'slot': i, 'base': s, 'agree': round(agree, 3), 'mono': mono})
    print(dict(stats), 'icons', len(rows))
    ag = np.array([r['agree'] for r in rows])
    print('cross-scale agreement p1/p5/p50', np.percentile(ag, [1, 5, 50]).round(3), 'below 0.8:', int((ag < 0.8).sum()))
    print('mono', sum(r['mono'] for r in rows))
    json.dump(rows, open(sys.argv[1] + '/atlas_rows.json', 'w', encoding='utf-8'), ensure_ascii=False)
