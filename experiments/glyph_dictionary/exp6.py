import sys
HERE=sys.argv[1]
src=open(HERE+'/exp4.py',encoding='utf-8').read().split("for d in (144,168)")[0]
exec(src)
def k_for(em): k=max(5,round(em*0.4)); return k+1-k%2
def strokes(gray_u8,em):
    """thin-stroke magnitude: |pixel - local median| (flat backgrounds / highlight boxes vanish)"""
    bg=cv2.medianBlur(gray_u8,k_for(em)); return np.abs(gray_u8.astype(np.float32)-bg.astype(np.float32))
def ink_of(g):
    hi=np.percentile(g[g>0],99); t=g>0.35*hi; ys,xs=np.where(t); return g[ys.min():ys.max()+1,xs.min():xs.max()+1]
DICT={}
def build(em):
    if em in DICT: return DICT[em]
    ent=[];Fl=[];Al=[];T=[]
    for w in words:
        for f in FACES:
            g=render(w,f,em if f=='宋体' else round(em*0.78),4)
            if g.shape[0]<3 or g.max()==0: continue
            g=np.pad(g,6); s=strokes(g,em); s=ink_of(s)
            v,a=coarse(s); ent.append((w,f)); Fl.append(v); Al.append(a); T.append(s)
    DICT[em]=(ent,np.stack(Fl).astype(np.float32),np.array(Al,np.float32),T); return DICT[em]
def recognize(line,dpi):
    em=em_for(dpi); ent,F,A,T=build(em)
    m=strokes(cv2.cvtColor(line,cv2.COLOR_BGR2GRAY),em)
    if m.max()<15: return []
    c=strip_lines(m,em)
    if c.max()<15: return []
    bx=text_box(c,em)
    if bx is None: return []
    y0,y1,x0,x1=bx; bw=x1-x0
    v,a=coarse(c[y0:y1,x0:x1]); sim=F@v; sim[np.abs(A/a-1)>0.15]=-1
    top=np.argpartition(-sim,K)[:K]; top=top[np.argsort(-sim[top])]
    h=em//2; pad=c[max(0,y0-4):y1+4,max(0,x0-h):x1+h]
    res=[]
    for i in top:
        if sim[i]<=0: continue
        s=score(pad,T[i])
        if T[i].shape[1]<0.85*bw: s=min(s,0.5)   # must explain the whole text box (no sub-words)
        res.append((s,ent[i][0],ent[i][1],float(sim[i])))
    res.sort(reverse=True); return res
t=time.perf_counter()
for d in (144,168): build(em_for(d))
print(f'build {time.perf_counter()-t:.0f}s',flush=True)
R=json.load(open(HERE+'/big2_rows.json',encoding='utf-8'))
W=[r for r in R if r['kind']=='word' and r['in_dict']]; O=[r for r in R if r['kind']=='word' and not r['in_dict']]
for k in (8,32):
    K=k; ok=bad=0; ts=[]; miss=collections.Counter(); wrong=collections.Counter(); oa=collections.Counter(); scores_ok=[]
    for r in W:
        im=cv2.imread(f"{HERE}/lines2/{r['id']}.png")
        t=time.perf_counter(); res=recognize(im,r['dpi']); ts.append((time.perf_counter()-t)*1000)
        if res and res[0][0]>=0.75:
            if res[0][1]==r['ocr']: ok+=1; scores_ok.append(res[0][0])
            else: bad+=1; wrong[(r['ocr'],res[0][1])]+=1
        else: miss[r['ocr']]+=1
    oas=[]
    for r in O:
        res=recognize(cv2.imread(f"{HERE}/lines2/{r['id']}.png"),r['dpi'])
        if res: oas.append(res[0][0])
        if res and res[0][0]>=0.75: oa[(r['ocr'],res[0][1])]+=1
    print(f'K={k}: accept&agree {ok}/{len(W)} ({ok/len(W):.1%}) wrong {bad} out-of-dict accepted {sum(oa.values())}/{len(O)} median {statistics.median(ts):.2f} ms p90 {np.percentile(ts,90):.2f}')
    print('  agree score p1/p5/p50',np.percentile(scores_ok,[1,5,50]).round(3),' out-of-dict top score p95/p99/max',np.percentile(oas,[95,99,100]).round(3))
    print('  misses',miss.most_common(15)); print('  wrong',wrong.most_common(8)); print('  out-of-dict accepted',oa.most_common(10),flush=True)
