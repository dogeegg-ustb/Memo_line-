import sys,os,hashlib,collections,json
sys.stdout.reconfigure(encoding='utf-8')
MS='D:/Memo_Line/Memo_Line/recognizer/CSPevent/extracted_icons/master_sheets'
masters={hashlib.sha256(open(os.path.join(MS,f),'rb').read()).hexdigest():f for f in os.listdir(MS)}
roots=['D:/CLIP STUDIO 1.5/CLIP STUDIO PAINT/resource','D:/CLIP STUDIO 1.5/CLIP STUDIO PAINT/CLIPStudioPaint.exe','D:/CLIP STUDIO 1.5/Common','D:/CLIP STUDIO 1.5/CLIP STUDIO/resource']
files=[]
for r in roots:
    if os.path.isfile(r): files.append(r)
    else:
        for a,_,fs in os.walk(r): files+= [os.path.join(a,f) for f in fs]
src=collections.defaultdict(list)
for fp in files:
    try: d=open(fp,'rb').read()
    except: continue
    p=0
    while True:
        i=d.find(b'\x89PNG\r\n\x1a\n',p)
        if i<0: break
        j=d.find(b'IEND',i)
        if j<0: break
        h=hashlib.sha256(d[i:j+8]).hexdigest()
        if h in masters: src[masters[h]].append((fp.split('CLIP STUDIO 1.5/')[1],i))
        p=j+8
print('located',len(src),'of',len(masters))
print(collections.Counter(v[0][0] for v in src.values()).most_common(15))
json.dump(src,open(sys.argv[1]+'/icon_src.json','w',encoding='utf-8'),ensure_ascii=False)
