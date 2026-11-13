import sys,json,time,statistics,sqlite3,os,collections,re
HERE=sys.argv[1]; sys.path.insert(0,HERE); sys.stdout.reconfigure(encoding='utf-8')
from gdi_match import *
from coarse import coarse
ROOT='D:/Memo_Line/Memo_Line/'
words={}
for e in json.load(open(ROOT+'recognizer/csp_panel_validator/csp_panel_validator/data/ui_strings.json',encoding='utf-8')):
    w=e['translations'].get('chinese_sc','').strip()
    if w: words.setdefault(w,'ui_sc')
for (w,) in sqlite3.connect(ROOT+'recognizer/CSPevent/csp_strings.sqlite3').execute("select distinct text from resource_strings where language='chinese_tc' and length(text)<=12"):
    if w.strip(): words.setdefault(w.strip(),'db_tc')
for (w,) in sqlite3.connect(HERE+'/tool.todb').execute("select distinct NodeName from Node"):
    if w and w.strip(): words.setdefault(w.strip(),'subtool')
words=[w for w in words if '\n' not in w and '\r' not in w and len(w)<=12]
FACES=['宋体','Microsoft YaHei UI']
def em_for(dpi): return round(12*dpi/96)
def ink_of(g):
    t=g>0.35*g.max(); ys,xs=np.where(t); return g[ys.min():ys.max()+1,xs.min():xs.max()+1]
DICT={}   # em -> (entries, F, A, tpl list)
def build(em):
    if em in DICT: return DICT[em]
    ent=[];Fl=[];Al=[];T=[]
    for w in words:
        for f in FACES:
            g=render(w,f,em,4).astype(np.float32)
            if g.shape[0]<3 or g.max()==0: continue
            v,a=coarse(ink_of(g)); ent.append((w,f)); Fl.append(v); Al.append(a); T.append(g)
    DICT[em]=(ent,np.stack(Fl).astype(np.float32),np.array(Al,np.float32),T); return DICT[em]
def runs(p,gap):
    idx=np.where(p>0)[0]
    if not len(idx): return []
    out=[]; s=e=idx[0]
    for i in idx[1:]:
        if i-e>gap: out.append((s,e)); s=i
        e=i
    out.append((s,e)); return out
def text_box(m,em):
    """band one em tall with the most ink, then the main column cluster (stray borders dropped)"""
    hi=np.percentile(m[m>0],99) if (m>0).any() else 0
    t=m>0.35*hi
    rows=t.sum(1).astype(float); win=min(len(rows),em+2)
    cs=np.convolve(rows,np.ones(win),'valid'); y0=int(np.argmax(cs)); y1=y0+win
    rr=np.where(rows[y0:y1]>0)[0]; y0,y1=y0+rr.min(),y0+rr.max()+1
    col=t[y0:y1].sum(0); R=runs(col,max(2,round(em*0.25)))
    R=[r for r in R if (r[1]-r[0]+1)>=max(2,em*0.15) or col[r[0]:r[1]+1].sum()>em]   # drop hairline borders
    if not R: return None
    k=max(range(len(R)),key=lambda i:col[R[i][0]:R[i][1]+1].sum())
    a=b=k
    while a>0 and R[a][0]-R[a-1][1]<=em*0.7: a-=1
    while b<len(R)-1 and R[b+1][0]-R[b][1]<=em*0.7: b+=1
    return y0,y1,R[a][0],R[b][1]+1
K=8
def recognize(line,dpi):
    em=em_for(dpi); ent,F,A,T=build(em)
    m=prep(line)
    if m.max()<20: return []
    bx=text_box(m,em)
    if bx is None: return []
    y0,y1,x0,x1=bx; ink=m[y0:y1,x0:x1]
    v,a=coarse(ink); sim=F@v; sim[np.abs(A/a-1)>0.15]=-1
    top=np.argpartition(-sim,K)[:K]; top=top[np.argsort(-sim[top])]
    pad=m[max(0,y0-3):y1+3,max(0,x0-3):x1+3]
    res=[(score(pad,T[i]),ent[i][0],ent[i][1],float(sim[i])) for i in top if sim[i]>0]
    res.sort(reverse=True); return res
from rapidocr import RapidOCR
eng=RapidOCR(params={'Global.log_level':'warning'})
crops=[x for x in json.load(open(HERE+'/crops.json',encoding='utf-8')) if x[0] in ('图层','笔刷属性','工具组','色彩滑块')]
for d in {x[2] for x in crops}: t=time.perf_counter(); build(em_for(d)); print(f'dict em {em_for(d)}: {len(DICT[em_for(d)][0])} templates {time.perf_counter()-t:.1f}s',flush=True)
rows=[]; tg=[]; to=[]
norm=lambda s:''.join(s.split())
WS=set(words)
for ci,(panel,path,dpi) in enumerate(crops):
    im=cv2.imdecode(np.fromfile(path,np.uint8),1)
    r=eng(im,use_det=True,use_cls=False,use_rec=True)
    if getattr(r,'boxes',None) is None or getattr(r,'txts',None) is None: continue
    for box,txt,sc in zip(r.boxes,r.txts,r.scores):
        xs=[p[0] for p in box]; ys=[p[1] for p in box]
        x0,y0=max(0,int(min(xs))-3),max(0,int(min(ys))-5); x1,y1=int(max(xs))+4,int(max(ys))+6
        line=im[y0:y1,x0:x1]
        if line.size==0: continue
        t0=time.perf_counter(); eng(line,use_det=False,use_cls=False,use_rec=True); to.append((time.perf_counter()-t0)*1000)
        t0=time.perf_counter(); res=recognize(line,dpi); tg.append((time.perf_counter()-t0)*1000)
        rid=len(rows); cv2.imwrite(f'{HERE}/lines2/{rid}.png',line)
        o=norm(txt)
        rows.append({'id':rid,'panel':panel,'dpi':dpi,'ocr':o,'ocr_score':float(sc),'in_dict':o in WS,
            'kind':'word' if (len(o)>=2 and re.search(r'[\u4e00-\u9fff]',o)) else ('ascii' if re.fullmatch(r'[A-Za-z][A-Za-z ]+',o) else 'short/num'),
            'top':[(round(s,4),w,f,round(cs,3)) for s,w,f,cs in res[:3]]})
    if ci%100==0: print(ci,len(crops),len(rows),flush=True)
json.dump(rows,open(HERE+'/big2_rows.json','w',encoding='utf-8'),ensure_ascii=False)
print(f'lines {len(rows)}; glyph median {statistics.median(tg):.2f} ms p90 {np.percentile(tg,90):.2f}; OCR rec-only median {statistics.median(to):.2f} ms p90 {np.percentile(to,90):.2f}',flush=True)
