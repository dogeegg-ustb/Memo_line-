import sys,json,collections,numpy as np
sys.stdout.reconfigure(encoding='utf-8')
R=json.load(open(sys.argv[1]+'/big2_rows.json',encoding='utf-8'))
T=lambda r:r['top'][0] if r['top'] else (0,None,None,0)
n=lambda s:''.join((s or '').split())
print('lines',len(R),collections.Counter((r['kind'],r['in_dict']) for r in R))
for kind in ('word','ascii','short/num'):
    ind=[r for r in R if r['kind']==kind and r['in_dict']]; out=[r for r in R if r['kind']==kind and not r['in_dict']]
    if not ind: continue
    ag=[r for r in ind if n(T(r)[1])==r['ocr']]
    print(f'\n[{kind}] in-dict {len(ind)}, top1==OCR {len(ag)} ({len(ag)/len(ind):.1%}); out-of-dict {len(out)}')
    for th in (0.6,0.7,0.75,0.8,0.85):
        a=sum(T(r)[0]>=th and n(T(r)[1])==r['ocr'] for r in ind); b=sum(T(r)[0]>=th and n(T(r)[1])!=r['ocr'] for r in ind); c=sum(T(r)[0]>=th for r in out)
        print(f'  th {th}: accept&agree {a} ({a/len(ind):.1%})  accept&disagree {b}  out-of-dict accepted {c}  -> falls back to OCR {len(ind)-a-b}')
uw=collections.Counter(r['ocr'] for r in R if r['kind']=='word' and r['in_dict'])
print('\ndistinct in-dict words',len(uw))
dis=[r for r in R if r['kind']=='word' and r['in_dict'] and n(T(r)[1])!=r['ocr']]
c=collections.Counter((r['ocr'],n(T(r)[1]),T(r)[0]>=0.75) for r in dis)
print('disagreements (ocr -> glyph, accepted@0.75):'); [print(' ',k,v) for k,v in c.most_common(30)]
out=[r for r in R if r['kind']=='word' and not r['in_dict'] and T(r)[0]>=0.75]
print('out-of-dict accepted@0.75:'); [print(' ',k,v) for k,v in collections.Counter((r['ocr'],n(T(r)[1])) for r in out).most_common(20)]
