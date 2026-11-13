import sys,os,json,time,statistics; sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from gdi_match import *
ROOT='D:/Memo_Line/Memo_Line/'
CASES=[('brush_panel.png','笔刷尺寸',(33,97,113,120)),('brush_panel.png','不透明度',(33,146,114,169)),('brush_panel.png','消除锯齿',(33,202,114,226)),('brush_panel.png','手颤修正',(33,244,114,266)),('brush_panel.png','根据速度调整手颤',(57,291,210,315)),('brush_panel.png','矢量吸附',(57,340,138,365)),('selection_panel.png','创建方式',(55,231,229,285)),('selection_panel.png','取样连续的像素',(109,337,405,394)),('selection_panel.png','闭合间隙',(109,445,285,501)),('selection_panel.png','颜色容差',(53,539,231,595)),('selection_panel.png','扩缩选区',(109,642,285,701)),('selection_panel.png','多图层参照',(110,773,327,831)),('selection_panel.png','消除锯齿',(109,880,287,938))]
GH,GW=4,40   # coarse grid: 4 rows x 40 cols, 4 orientation bins
def _band(p,gap):
    """longest run of non-empty bins, bridging gaps <= gap (drops stray widget pixels at the edges)"""
    idx=np.where(p>0)[0]; runs=[]; s=idx[0]; e=idx[0]
    for i in idx[1:]:
        if i-e>gap+1: runs.append((s,e)); s=i
        e=i
    runs.append((s,e)); return max(runs,key=lambda r:p[r[0]:r[1]+1].sum())
def ink_box(m):
    t=m>0.35*m.max()
    y0,y1=_band(t.sum(1),2); x0,x1=_band(t[y0:y1+1].sum(0),2*(y1-y0+1))
    return m[y0:y1+1,x0:x1+1]
def coarse(m):
    """m: text-magnitude image cropped to ink. -> unit vector of oriented stroke energy on a low-res grid."""
    m=m.astype(np.float32)
    h,w=m.shape; s=24/h
    m=cv2.resize(m,(max(4,round(w*s)),24),interpolation=cv2.INTER_AREA)
    gx=cv2.Sobel(m,cv2.CV_32F,1,0,ksize=3); gy=cv2.Sobel(m,cv2.CV_32F,0,1,ksize=3)
    mag=np.hypot(gx,gy); ang=(np.arctan2(gy,gx)%np.pi)/np.pi*4  # 4 bins over 180°
    b0=np.floor(ang).astype(int)%4; f=ang-np.floor(ang)
    ch=[]
    for k in range(4):
        e=mag*((b0==k)*(1-f)+(((b0+1)%4)==k)*f)
        ch.append(cv2.resize(e,(GW,GH),interpolation=cv2.INTER_AREA))
    v=np.sqrt(np.stack(ch).ravel())   # sqrt tames thick-stroke dominance
    return v/ (np.linalg.norm(v)+1e-6), w/h

if __name__=='__main__':
    entries=json.load(open(ROOT+'recognizer/csp_panel_validator/csp_panel_validator/data/ui_strings.json',encoding='utf-8'))
    lex=sorted({e['translations'].get('chinese_sc','').strip() for e in entries}-{''})
    lex=[w for w in lex if '\n' not in w and len(w)<=20]
    t=time.perf_counter()
    feats=[];aspects=[]
    for w in lex:
        v,a=coarse(render(w,'宋体',18,4).astype(np.float32)); feats.append(v); aspects.append(a)
    F=np.stack(feats); A=np.array(aspects)
    print(f'dictionary {len(lex)} words, coarse vector {F.shape[1]} floats, build {(time.perf_counter()-t)*1000:.0f} ms, {F.nbytes/1024:.0f} KB')
    
    K=8
    fine_cache={}
    def tpl(w,px):
        k=(w,px)
        if k not in fine_cache: fine_cache[k]=render(w,'宋体',px,4).astype(np.float32)
        return fine_cache[k]
    def recognize(crop_bgr):
        m=prep(crop_bgr); ink=ink_box(m)
        v,a=coarse(ink)
        sim=F@v
        sim[np.abs(A/a-1)>0.15]=-1          # aspect gate (scale-free)
        top=np.argpartition(-sim,K)[:K]; top=top[np.argsort(-sim[top])]
        # fine: render candidates at the crop's scale (template ink height 18px-font ~ ratio)
        px=max(8,round(18*ink.shape[0]/max(1,ink_box(tpl(lex[top[0]],18)).shape[0])))
        best=sorted(((score(m,tpl(lex[i],px)),lex[i]) for i in top),reverse=True)
        return best,[lex[i] for i in top],px
    
    # warm
    recognize(cv2.imread(ROOT+'recognizer/csp_panel_validator/tests/fixtures/brush_panel.png')[97:120,33:113])
    print('== real crops ==')
    tc=[];tf=[]
    for f,l,(x,y,r,b) in CASES:
        im=cv2.imread(ROOT+'recognizer/csp_panel_validator/tests/fixtures/'+f)[y:b,x:r]
        ts=[]
        for _ in range(20):
            t=time.perf_counter(); best,cands,px=recognize(im); ts.append((time.perf_counter()-t)*1000)
        # coarse-only timing
        m=prep(im); ink=ink_box(m); t=time.perf_counter()
        for _ in range(200): v,a=coarse(ink); s=F@v
        tco=(time.perf_counter()-t)/200*1000
        rank=cands.index(l)+1 if l in cands else None
        print(l,f"{'OK ' if best[0][1]==l else 'BAD'} coarseRank={rank} fine={best[0][0]:.3f} 2nd={best[1][0]:.3f} px={px} total={statistics.median(ts):.2f}ms coarse={tco:.2f}ms  [{l}]")
        tc.append(tco); tf.append(statistics.median(ts))
    print(f'median total {statistics.median(tf):.2f} ms, coarse stage {statistics.median(tc):.2f} ms')
    
    # larger check: every lexicon word re-rendered with ClearType (different rasteriser) at 18px on a CSP-like grey bg, +1px jitter
    print('== synthetic self-retrieval, all words, ClearType render, gray bg ==')
    rank1=0;inK=0;miss=[]
    for i,w in enumerate(lex):
        g=render(w,'宋体',18,5).astype(np.float32)
        img=np.full((g.shape[0]+9,g.shape[1]+12),60,np.float32); img[4+(i%2):4+(i%2)+g.shape[0],5:5+g.shape[1]]+=g*0.55
        bgr=np.repeat(np.clip(img,0,255).astype(np.uint8)[:,:,None],3,2)
        best,cands,px=recognize(bgr)
        if best[0][1]==w: rank1+=1
        elif best[0][0]-best[0][0]==0 and w in cands: miss.append((w,best[0][1]))
        else: miss.append((w,best[0][1]))
        if w in cands: inK+=1
    print(f'top-{K} recall {inK}/{len(lex)}, final correct {rank1}/{len(lex)}')
    print('wrong examples:',json.dumps(miss[:15],ensure_ascii=False))
    
    # OCR baseline on same machine
    from rapidocr import RapidOCR
    eng=RapidOCR(params={'EngineConfig.onnxruntime.intra_op_num_threads':2,'EngineConfig.onnxruntime.inter_op_num_threads':1,'Global.log_level':'warning'})
    im=cv2.imread(ROOT+'recognizer/csp_panel_validator/tests/fixtures/brush_panel.png')[97:120,33:113]
    eng(im,use_det=False,use_cls=False,use_rec=True); ts=[]
    for _ in range(20):
        t=time.perf_counter(); eng(im,use_det=False,use_cls=False,use_rec=True); ts.append((time.perf_counter()-t)*1000)
    print(f'OCR rec-only same crop: {statistics.median(ts):.2f} ms')
