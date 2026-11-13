"""Which atlas icons appear in live CSP panel captures, and at what pixel size."""
import sys, json, collections, time
import numpy as np, cv2
sys.path.insert(0, sys.argv[1])
from atlas import load, atlas_slots
sys.stdout.reconfigure(encoding='utf-8')
HERE = sys.argv[1]
rows = json.load(open(HERE + '/atlas_rows.json', encoding='utf-8'))
sheets = {}
def big_alpha(r):
    if r['sheet'] not in sheets: sheets[r['sheet']] = load(r['sheet'])
    im = sheets[r['sheet']]; s, slots = atlas_slots(im.shape[1], im.shape[0])
    x, y, z = slots[r['slot']][2]
    return im[y:y + z, x:x + z, 3].astype(np.float32)

def strokes(gray):
    bg = cv2.medianBlur(gray, 15)
    return np.abs(gray.astype(np.float32) - bg.astype(np.float32))

SIZES = [int(a) for a in sys.argv[2].split(',')]
tpls = []
for i, r in enumerate(rows):
    a = big_alpha(r)
    for px in SIZES:
        t = cv2.resize(a, (px, px), interpolation=cv2.INTER_AREA)
        if t.std() < 1: continue
        tpls.append((i, px, t))
crops = json.load(open(HERE + '/crops.json', encoding='utf-8'))
pick = {}
for n, p, d in crops:
    if n in ('图层', '笔刷属性', '工具组', '色彩滑块', '色彩') and (n, d) not in pick: pick[(n, d)] = p
found = []
for (panel, dpi), p in pick.items():
    im = cv2.imdecode(np.fromfile(p, np.uint8), 1); g = cv2.cvtColor(im, cv2.COLOR_BGR2GRAY)
    m = strokes(g)
    t0 = time.perf_counter(); hits = []
    for i, px, t in tpls:
        res = cv2.matchTemplate(m, t, cv2.TM_CCOEFF_NORMED)
        ys, xs = np.where(res >= 0.8)
        for y, x in zip(ys, xs): hits.append((float(res[y, x]), int(x), int(y), px, i))
    # non-maximum suppression
    hits.sort(reverse=True); keep = []
    for h in hits:
        if all(abs(h[1] - k[1]) > k[3] * 0.6 or abs(h[2] - k[2]) > k[3] * 0.6 for k in keep): keep.append(h)
    print(f'{panel} dpi {dpi}: {len(keep)} icons found in {time.perf_counter() - t0:.1f}s; sizes', collections.Counter(k[3] for k in keep).most_common())
    vis = im.copy()
    for s, x, y, px, i in keep:
        cv2.rectangle(vis, (x, y), (x + px, y + px), (0, 200, 255), 1)
        found.append({'panel': panel, 'dpi': dpi, 'score': round(s, 3), 'x': x, 'y': y, 'px': px, 'sheet': rows[i]['sheet'], 'slot': rows[i]['slot']})
    cv2.imencode('.png', vis)[1].tofile(f'{HERE}/live_{panel}_{dpi}.png')
json.dump(found, open(HERE + '/live_found.json', 'w', encoding='utf-8'), ensure_ascii=False)
print('by sheet', collections.Counter(f['sheet'] for f in found).most_common(12))
