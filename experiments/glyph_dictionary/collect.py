import json,glob,hashlib,collections,sys,os
sys.stdout.reconfigure(encoding='utf-8')
base='D:/Memo_Line/Memo_Line/Memoline_demo_csponly/publish/win-x64/Recognizer/procedure/stroke/'
seen={}; per=collections.Counter(); total=collections.Counter(); dpis=collections.Counter()
for d in glob.glob(base+'*.spool'):
    for f in glob.glob(d+'/*.done'):
        try: o=json.load(open(f,encoding='utf-8'))
        except Exception: continue
        dpis[o.get('dpi')]+=1
        for name,c in (o.get('crops') or {}).items():
            p=c.get('path','')
            p=d+'/'+os.path.basename(p)
            if not os.path.exists(p): continue
            total[name]+=1
            h=hashlib.md5(open(p,'rb').read()).hexdigest()
            if h not in seen: seen[h]=(name,p,o.get('dpi')); per[name]+=1
print('dpi',dpis); print('all crops',total); print('unique',per)
json.dump(list(seen.values()),open(sys.argv[1],'w',encoding='utf-8'),ensure_ascii=False)
