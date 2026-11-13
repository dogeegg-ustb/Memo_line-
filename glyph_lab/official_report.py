"""Render a self-contained, visually reviewable report of official UI test crops."""
from __future__ import annotations

import base64
import hashlib
import html
import json

import cv2

from .official_csp import DATA
from .official_eval import inputs


def main():
    report=json.loads((DATA/"report.json").read_text(encoding="utf-8"))
    details=json.loads((DATA/"runs/cases.json").read_text(encoding="utf-8"))
    current_hash=hashlib.sha256((DATA/"annotations.json").read_bytes()).hexdigest()
    if report["annotation_sha256"]!=current_hash:
        raise ValueError("Annotations changed: run evaluation before rendering")
    sources=json.loads((DATA/"sources.json").read_text(encoding="utf-8"))
    source_map={s["id"]:s for s in sources["images"]}
    selected=details["calibrated_available_font"]
    results={r["id"]:r for rows in selected.values() for r in rows}
    cards=[]; failures=[]
    for row,image in inputs("test"):
        result=results[row["id"]]
        predicted=result["predicted"]
        status="rejected" if predicted is None else "correct" if predicted==row["text"] else "wrong"
        status_label={"correct":"正确","wrong":"误识","rejected":"拒识"}[status]
        _,encoded=cv2.imencode(".png",image)
        data="data:image/png;base64,"+base64.b64encode(encoded).decode()
        source=source_map[row["source_id"]]
        esc=html.escape
        card=f'''<article class="{status}" data-status="{status}" data-language="{row['language']}" data-mode="{row['mode']}">
        <div class="top"><b>{esc(row['id'])}</b><span>{status_label}</span></div>
        <div class="crop"><img src="{data}" width="{image.shape[1]*2}" height="{image.shape[0]*2}" alt="截图裁切"></div>
        <div>标注：<b>{esc(row['text'])}</b></div><div>输出：<b>{esc(predicted or '—')}</b></div>
        <div class="muted">{result['line_ms']:.3f} ms/区域 · {result['ms_per_char']:.3f} ms/字</div>
        <div class="muted">{esc(row.get('kind','body'))} · {'词表内/数字' if result['known'] else '词表外'} · {esc(result['reason'])}</div>
        <details><summary>来源与候选</summary><a href="{esc(source['page_url'],quote=True)}">官方页面</a> · <a href="{esc(source['image_url'],quote=True)}">原图</a>
        <p>ROI：{row['roi']}；原图 {source['width']} × {source['height']}</p><pre>{esc(json.dumps(result['candidates'],ensure_ascii=False,indent=2))}</pre></details></article>'''
        cards.append(card)
        if status=="wrong":
            failures.append({"id":row["id"],"source_id":row["source_id"],"truth":row["text"],
                             "predicted":predicted,"roi":row["roi"],"image_sha256":source["sha256"],
                             "font":report["runs"]["calibrated_available_font"][row["language"]+"/"+row["mode"]]["font"],
                             "score":result["score"],"fixed":False})
    document='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
    <title>CSP 官网 UI 实测 · 记艺线</title><style>
    body{margin:0;background:#f4f6f7;color:#23313a;font:15px/1.6 system-ui,sans-serif}main{max-width:1280px;margin:30px auto;padding:0 24px}
    h1{font-size:27px;margin-bottom:8px}.lead{max-width:950px}nav{display:flex;gap:14px;flex-wrap:wrap;align-items:center;margin:24px 0;position:sticky;top:0;background:#f4f6f7;padding:12px 0}
    select{font:inherit;padding:6px;border:1px solid #c7d4da;border-radius:5px}#grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(285px,1fr));gap:16px}article{background:white;border:1px solid #d6dfe3;border-top:4px solid #9daab1;border-radius:9px;padding:16px;min-width:0}.correct{border-top-color:#248468}.wrong{border-top-color:#bd473c}.top{display:flex;justify-content:space-between;font-size:13px}.crop{height:70px;overflow:auto;display:flex;align-items:center;margin:10px 0;background:#edf1f3}.crop img{image-rendering:pixelated;flex:none}.muted{font-size:12px;color:#647782;margin-top:7px}details{font-size:12px;margin-top:12px}pre{white-space:pre-wrap}a{color:#176b87}article[hidden]{display:none}
    </style><main><h1>CSP 官网 UI 实测 · 记艺线</h1>
    <p class="lead">56 张官方原图，267 处目视标注：50 处用于字体候选筛选，217 处用于测试。测试来自 18 张图，有 144 种不同像素裁切、125 种文本；不能把跨语言重复的零值当成独立证据。</p>
    <p class="lead"><b>默认配置：0 正确、0 误识、217 拒识。现有字体候选：10 正确、6 误识、201 拒识。</b>10 个正确结果全是单个英文字母，整词与数值尚未通过。下列展示现有字体候选配置的结果，未降低阈值，未更改识别算法。</p>
    <p class="lead">原图直接裁切、不缩放后送入识别器；这里放大两倍仅便于核对。耗时包含完整匹配调用，不含下载、人工框选、字体选择和建库。大量快速拒识不构成 1 ms/字可用性达标。标注由助手目视核对，仍需独立人工复核。</p>
    <nav><label>结果 <select id="status"><option value="all">全部</option><option value="wrong">误识</option><option value="correct">正确</option><option value="rejected">拒识</option></select></label>
    <label>语言 <select id="language"><option value="all">全部</option><option value="en-us">英文</option><option value="zh-tw">繁体中文</option><option value="ja-jp">日文</option></select></label>
    <label>类型 <select id="mode"><option value="all">全部</option><option value="words">文字</option><option value="numbers">数字</option></select></label><span id="count"></span></nav><section id="grid">'''+"\n".join(cards)+'''</section></main><script>
    function filter(){let count=0;for(const card of document.querySelectorAll('article')){card.hidden=['status','language','mode'].some(key=>document.getElementById(key).value!=='all'&&card.dataset[key]!==document.getElementById(key).value);if(!card.hidden)count++}document.getElementById('count').textContent=count+' 个区域'}
    for(const key of ['status','language','mode']){const el=document.getElementById(key);el.onchange=filter;const value=new URLSearchParams(location.search).get(key);if(value&&[...el.options].some(o=>o.value===value))el.value=value}filter();</script></html>'''
    (DATA/"results.html").write_text(document,encoding="utf-8")
    (DATA/"failures.json").write_text(json.dumps({"note":"Known failures kept for future regression; repeated crops are not independent failures.","cases":failures},ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print(DATA/"results.html")


if __name__=="__main__":main()
