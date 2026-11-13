import sys, re, urllib.request, urllib.parse, os, concurrent.futures as cf, collections
sys.stdout.reconfigure(encoding='utf-8')
D = os.path.dirname(os.path.abspath(__file__))
roots = {'en': 'en-us', 'ja': 'ja-jp', 'ko': 'ko-kr', 'tc': 'zh-tw', 'fr': 'fr-fr', 'de': 'de-de', 'es': 'es-es'}


def get(u):
    try:
        return urllib.request.urlopen(urllib.parse.quote(u, safe=':/%#'), timeout=30).read()
    except Exception:
        return None


jobs = []
for k, root in roots.items():
    for l in open(f'{D}/links_{k}.txt', encoding='utf-8').read().split():
        if re.search(r'/(150_tools|180_layers|270_canvas|300_color|690_interface)/', l):
            jobs.append((k, root, l))
os.makedirs(D + '/img', exist_ok=True)


def run(j):
    k, root, l = j
    h = get(f'https://help.clip-studio.com/{root}/{l}')
    if not h:
        return 0
    n = 0
    for s in re.findall(r'<img[^>]*src="([^"]+)"', h.decode('utf-8', 'replace')):
        full = urllib.parse.urljoin(f'https://help.clip-studio.com/{root}/{l}', s)
        if '/images/' not in full:
            continue
        dst = f'{D}/img/{k}__{os.path.basename(full)}'
        if os.path.exists(dst):
            continue
        b = get(full)
        if b:
            open(dst, 'wb').write(b); n += 1
    return n


with cf.ThreadPoolExecutor(8) as ex:
    print(sum(ex.map(run, jobs)), 'new images')
print(collections.Counter(f.split('__')[0] for f in os.listdir(D + '/img')))
