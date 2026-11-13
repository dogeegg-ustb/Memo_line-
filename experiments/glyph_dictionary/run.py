import sys,json,time,statistics; sys.path.insert(0,'C:/Users/dogeegg/AppData/Local/Temp/glyphtest')
from gdi_match import *
ROOT='D:/Memo_Line/Memo_Line/'
CASES=[('brush_panel.png','笔刷尺寸',(33,97,113,120)),('brush_panel.png','不透明度',(33,146,114,169)),('brush_panel.png','消除锯齿',(33,202,114,226)),('brush_panel.png','手颤修正',(33,244,114,266)),('brush_panel.png','根据速度调整手颤',(57,291,210,315)),('brush_panel.png','矢量吸附',(57,340,138,365)),('selection_panel.png','创建方式',(55,231,229,285)),('selection_panel.png','取样连续的像素',(109,337,405,394)),('selection_panel.png','闭合间隙',(109,445,285,501)),('selection_panel.png','颜色容差',(53,539,231,595)),('selection_panel.png','扩缩选区',(109,642,285,701)),('selection_panel.png','多图层参照',(110,773,327,831)),('selection_panel.png','消除锯齿',(109,880,287,938))]
crops=[]
for f,l,(x,y,r,b) in CASES:
    im=cv2.imread(ROOT+'recognizer/csp_panel_validator/tests/fixtures/'+f); crops.append((f,l,prep(im[y:b,x:r])))
# 1) which font/size/quality reproduces the true word best?
print('== best self-score per font (true word only) ==')
for face in ['宋体','Microsoft YaHei UI','黑体']:
  for q,qn in [(4,'AA'),(3,'mono'),(5,'CT')]:
    row=[]
    for f,l,c in crops:
        best=max(((score(c,render(l,face,px,q)),px) for px in range(10,46)),key=lambda t:t[0]); row.append(best)
    print(f'{face:20s}{qn:5s}', ' '.join(f'{s:.2f}@{p}' for s,p in row))
print('== full simplified-Chinese lexicon, SimSun AA ==')
entries=json.load(open(ROOT+'recognizer/csp_panel_validator/csp_panel_validator/data/ui_strings.json',encoding='utf-8'))
lex=sorted({e['translations'].get('chinese_sc','').strip() for e in entries}-{''})
lex=[w for w in lex if '\n' not in w and len(w)<=20]
print('words',len(lex))
for px in (18,40):
    t0=time.perf_counter(); tpls={w:render(w,'宋体',px,4).astype(np.float32) for w in lex}
    print(f'build {px}px: {(time.perf_counter()-t0)*1000:.0f} ms')
    for f,l,c in crops:
        if (px==18)!=(f=='brush_panel.png'): continue
        # ink width of the crop for a width prefilter
        cols=np.where(c.max(axis=0)>40)[0]; inkw=cols.max()-cols.min()+1
        def run():
            cand=[(w,t) for w,t in tpls.items() if abs(t.shape[1]-inkw)<=max(3,inkw*0.08)]
            sc=sorted(((score(c,t),w) for w,t in cand),reverse=True)
            return sc,len(cand)
        ts=[];
        for _ in range(5): t=time.perf_counter(); sc,n=run(); ts.append((time.perf_counter()-t)*1000)
        top=sc[0]; second=sc[1] if len(sc)>1 else (0,'-')
        ok='OK ' if top[1]==l else 'BAD'
        print(f'{ok} truth={l:10s} top={top[1]}({top[0]:.3f}) 2nd={second[1]}({second[0]:.3f}) cands={n} {statistics.median(ts):.2f} ms')
