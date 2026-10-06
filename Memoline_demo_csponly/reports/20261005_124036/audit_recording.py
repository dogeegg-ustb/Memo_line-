"""Read-only audit of this recording; associations are derived, never written back."""
import base64
import bisect
import collections
import datetime as dt
import hashlib
import html
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SOURCE = ROOT.parent.parent / 'publish/win-x64/recordings/20261005_124036_1c07211005d34761b720120902a88d5f.memoline'
TZ = dt.timezone(dt.timedelta(hours=8))

def load_lines(path):
    with path.open(encoding='utf-8-sig') as f:
        for line in f:
            yield json.loads(line)

def dump(name, value):
    (ROOT / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')

aggregate = list(load_lines(ROOT / 'aggregation.jsonl'))
packets = [r for r in aggregate if r['kind'] == 'packet']
dimensions = [r for r in aggregate if r['kind'] == 'dimensionSample']
verification = json.loads((ROOT / 'verification.json').read_text(encoding='utf-8-sig'))
counts = collections.Counter()
hardware, cores, saves, shortcuts, workspaces = [], [], [], [], []
native_by_append = {}
header = footer = None
for r in load_lines(ROOT / 'native-full.jsonl'):
    counts[r['kind']] += 1
    if r['kind'] == 'header':
        header = r
    elif r['kind'] == 'footer':
        footer = r
    else:
        native_by_append[r['appendId']] = {k: r.get(k) for k in ['appendId','eventId','ticks','operationId','path','kind']}
    if r.get('path') == 'hardware':
        hardware.append(r)
    if r['kind'] == 'workspaceStatus':
        workspaces.append(r)
    if r['kind'] == 'coreStateUpdated':
        cores.append(r)
    if r['kind'] in ['clipSaveRequest', 'saveGuardResult']:
        saves.append(r)
    if r['kind'] == 'shortcutResolved':
        shortcuts.append(r)

frequency = header['frequency']
created = dt.datetime.fromisoformat(header['createdAt']).astimezone(TZ)
def secs(ticks): return ticks / frequency
def local(ticks): return (created + dt.timedelta(seconds=secs(ticks))).isoformat(timespec='milliseconds')
def clock(ticks): return (created + dt.timedelta(seconds=secs(ticks))).strftime('%H:%M:%S.%f')[:-3]
def elapsed(ticks): return f'{secs(ticks):.7f}'
def observation_ticks(r):
    e = r['data'].get('evidence') or {}
    return e.get('triggerTicks', e.get('capturedTicks', r['ticks']))

brushes = sorted([r for r in cores if r['data']['module'] == 'brushState' and r['data']['status'] in ['changed','unchanged']], key=lambda r: (observation_ticks(r), r['appendId']))
brush_ticks = [observation_ticks(r) for r in brushes]
brush_id = {r['appendId']: f'B{i+1:02d}' for i, r in enumerate(brushes)}
colors = sorted([r for r in cores if r['data']['module'] == 'colorState'], key=observation_ticks)
layers = sorted([r for r in cores if r['data']['module'] == 'currentLayerState'], key=observation_ticks)
clip_core = next(r for r in cores if r['data']['module'] == 'clipState')
initial_clip = clip_core['data']['state']
views = {r['appendId']: r for r in cores if r['data']['module'] == 'canvasViewState'}

def last_before(states, ticks):
    return next((r for r in reversed(states) if observation_ticks(r) <= ticks and r['data']['status'] in ['changed','unchanged']), None)

pointer_owner = {}
pointer_errors = []
for n, p in enumerate(packets, 1):
    for pointer in p['eventPointers']:
        a = pointer['appendId']
        native = native_by_append.get(a)
        if native is None or native['path'] != 'hardware' or any(pointer.get(k) != native.get(k) for k in ['eventId','ticks','operationId']):
            pointer_errors.append({'packet': n, 'pointer': pointer, 'native': native})
        if not pointer.get('context'):
            if a in pointer_owner:
                pointer_errors.append({'duplicateAppendId': a})
            pointer_owner[a] = n
    expected = {r['appendId'] for r in hardware if p['fromTicks'] < r['ticks'] <= p['toTicks']}
    actual = {r['appendId'] for r in p['eventPointers'] if not r.get('context')}
    if expected != actual:
        pointer_errors.append({'packet': n,'missing':sorted(expected-actual),'extra':sorted(actual-expected)})
assert not pointer_errors, pointer_errors
assert len(native_by_append) == footer['appendedRecords']
assert sorted(native_by_append) == list(range(1, footer['appendedRecords'] + 1))
assert len(hardware) == footer['hardwareEvents']
assert [r['eventId'] for r in hardware] == list(range(1, len(hardware) + 1))

pen_groups = collections.defaultdict(list)
for r in hardware:
    if r['kind'] in ['penBegin','penSample','penEnd','penInterrupted']:
        pen_groups[r['operationId']].append(r)

REGION_TYPES = [('brushProperties','笔刷属性面板','笔刷属性'), ('brushSelection','笔刷选择面板','工具组'),
                ('toolbar','工具栏面板','工具栏'), ('navigator','导航器面板','导航器'),
                ('layers','图层面板','图层'), ('canvasViewport','画布视口面板','画布视口'), ('other','其他',None)]
def historical_pen_location(first):
    w = next((r for r in reversed(workspaces) if r['appendId'] < first['appendId'] and r['ticks'] <= first['ticks']), None)
    data = w['data'] if w else {}
    regions = ((data.get('layout') or {}).get('regions') or {}) if data.get('status') == 'ready' else {}
    x,y = first['data']['x'], first['data']['y']
    match = next(((code,name,key,regions[key]) for code,name,key in REGION_TYPES if key in regions
        and regions[key][0] <= x < regions[key][0]+regions[key][2]
        and regions[key][1] <= y < regions[key][1]+regions[key][3]), None)
    return {'region':match[0] if match else 'other', 'name':match[1] if match else '其他', 'x':x,'y':y,
            'coordinateSource':'windowsCursor', 'panel':match[2] if match else None,
            'boundsXYWH':match[3] if match else None,
            'status':'matched' if match else 'outsideRegions' if regions else 'layoutUnavailable',
            'layoutAppendId':w['appendId'] if w else None,'layoutTicks':w['ticks'] if w else None,
            'historicalReplay':True}

contacts = []
for n, (operation, events) in enumerate(pen_groups.items(), 1):
    events.sort(key=lambda r: (r['ticks'], r['eventId']))
    first, last = events[0], events[-1]
    assert first['kind'] == 'penBegin' and last['kind'] == 'penEnd'
    owners = sorted({pointer_owner[r['appendId']] for r in events if r['appendId'] in pointer_owner})
    assert len(owners) <= 1
    keys = sorted({key for r in events for key in (r['data'].get('heldKeys') or [])})
    space = 32 in keys
    b_index = bisect.bisect_right(brush_ticks, first['ticks']) - 1
    brush = brushes[b_index] if b_index >= 0 else None
    mid_states = [r for r in brushes if first['ticks'] < observation_ticks(r) <= last['ticks']]
    color, layer = last_before(colors, first['ticks']), last_before(layers, first['ticks'])
    pressures = [r['data']['pressure'] for r in events if r['data'].get('pressure') is not None]
    raw_points = [r for r in events if r['kind'] in ['penBegin','penSample']]
    contacts.append({
        'number': n, 'packetNumber': owners[0] if owners else None,
        'operationId': operation, 'startTicks': first['ticks'], 'endTicks': last['ticks'],
        'startLocal':local(first['ticks']), 'endLocal':local(last['ticks']),
        'durationMs': (last['ticks']-first['ticks']) / frequency * 1000,
        'eventCount':len(events), 'pointCount':len(raw_points),
        'firstEventId':first['eventId'], 'lastEventId':last['eventId'],
        'beginAppendId':first['appendId'], 'endAppendId':last['appendId'],
        'heldVirtualKeys':keys, 'spaceHeld':space, 'penDownLocation':historical_pen_location(first),
        'classification':'Space 按下期间的笔接触（疑似画布拖动）' if space else '未按 Space 的笔接触（绘画/擦除候选，不能由输入记录证明像素变化）',
        'brushId':brush_id.get(brush['appendId']) if brush else None,
        'brushAppendId':brush['appendId'] if brush else None,
        'brushState':brush['data']['state'] if brush else None,
        'brushObservedTicks':observation_ticks(brush) if brush else None,
        'brushCapturedTicks':brush['data']['evidence'].get('capturedTicks') if brush else None,
        'brushPublishedAfterContactStart':brush['appendedTicks'] > first['ticks'] if brush else None,
        'brushCapturedAfterContactStart':brush['data']['evidence'].get('capturedTicks', brush['ticks']) > first['ticks'] if brush else None,
        'brushChangesDuringContact':[brush_id[r['appendId']] for r in mid_states],
        'colorState':color['data']['state'] if color else None,
        'colorAppendId':color['appendId'] if color else None,
        'layerInterfaceState':layer['data']['state'] if layer else None,
        'layerAppendId':layer['appendId'] if layer else None,
        'startScreenXY':[first['data']['x'],first['data']['y']],
        'endScreenXY':[last['data']['x'],last['data']['y']],
        'rawPressureMin':min(pressures) if pressures else None,
        'rawPressureMax':max(pressures) if pressures else None,
        'associationMethod':'reportDerived: latest confirmed brush evidence.triggerTicks <= penBegin.ticks; membership by original packet eventPointers.appendId',
        'nativeEventPointers':[{k:r.get(k) for k in ['appendId','eventId','operationId','ticks','kind']} for r in events]
    })

def raw_brush(r):
    raw = r['data'].get('rawResult') or {}
    return raw if isinstance(raw,dict) else {}

def brush_properties(r):
    raw = raw_brush(r)
    raw_props = {p['key']:p for p in (raw.get('brush') or {}).get('properties',[])}
    answer=[]
    for p in r['data']['state'].get('properties',[]):
        q=raw_props.get(p['key'],{})
        answer.append({**p,'label':q.get('label',p['key']),'observed':q.get('observed'),'raw_text':q.get('raw_text')})
    return answer

brush_reports=[]
wanted_images=set()
for r in brushes:
    raw=raw_brush(r)
    record={
        'id':brush_id[r['appendId']], 'appendId':r['appendId'],
        'triggerTicks':observation_ticks(r),'triggerLocal':local(observation_ticks(r)),
        'frameTicks':r['ticks'],'appendedTicks':r['appendedTicks'],
        'capturedTicks':r['data']['evidence'].get('capturedTicks'),
        'captureLocal':local(r['data']['evidence'].get('capturedTicks',r['ticks'])),
        'appendLocal':local(r['appendedTicks']),
        'status':r['data']['status'],'recognitionStatus':raw.get('status'),
        'name':r['data']['state'].get('name'),
        'properties':brush_properties(r),'unresolved':raw.get('unresolved',[]),
        'evidence':r['data']['evidence'], 'originalFrame':r,
        'contactNumbers':[c['number'] for c in contacts if c['brushId']==brush_id[r['appendId']]],
        'visualAuditNote':'B05 原始截图的硬度行有多个格子高亮，结构化接口值却为选项 1；保留原值供核对，不把它解释成硬度百分比。' if brush_id[r['appendId']]=='B05' else None
    }
    wanted_images.update(record['evidence'].get('screenshotIds') or [])
    brush_reports.append(record)
for r in layers+colors:
    wanted_images.update(r['data']['evidence'].get('screenshotIds') or [])

parts=collections.defaultdict(dict)
image_metadata={}
for r in load_lines(ROOT / 'native-full.jsonl'):
    if r['kind']!='screenshotBlob':continue
    a=r['data'];id_=a['screenshotId']
    if id_ not in wanted_images:continue
    parts[id_][a.get('partIndex',0)] = base64.b64decode(a['image'],validate=True)
    image_metadata[id_]={'appendIds':image_metadata.get(id_,{}).get('appendIds',[])+[r['appendId']],
                         **{k:v for k,v in a.items() if k!='image'}}
(ROOT/'evidence').mkdir(exist_ok=True)
for id_ in wanted_images:
    meta=image_metadata[id_]
    assert sorted(parts[id_])==list(range(meta.get('partCount',1)))
    data=b''.join(parts[id_][i] for i in sorted(parts[id_]))
    assert len(data)==meta['byteLength']
    assert hashlib.sha256(data).hexdigest()==meta['sha256']
    (ROOT/'evidence'/f'{id_}.png').write_bytes(data)
    meta['file']=f'evidence/{id_}.png'
dump('evidence-index.json',image_metadata)

packet_reports=[]
for n,p in enumerate(packets,1):
    members=[c for c in contacts if c['packetNumber']==n]
    view=views.get(p['observationAppendId'])
    requests=[r for r in saves if r['kind']=='clipSaveRequest' and r['data'].get('triggerTicks')==p['triggerTicks']]
    results=[r for r in saves if r['kind']=='saveGuardResult' and r['data'].get('triggerTicks')==p['triggerTicks']]
    packet_reports.append({
        'number':n,'id':p['id'],'status':p['status'],'reason':p['reason'],
        'fromTicks':p['fromTicks'],'toTicks':p['toTicks'],'fromLocal':local(p['fromTicks']),'toLocal':local(p['toTicks']),
        'durationSeconds':secs(p['toTicks']-p['fromTicks']),
        'mechanicalEventPointerCount':len(p['eventPointers']),
        'statePointerCount':len(p.get('statePointers', [])), 'hasStatePointersField':'statePointers' in p,
        'dirtyMatrixCount':len(p['dirtyMatrices']),'imageAssetCount':len(p['imageAssets']),
        'dirtyMatrices':p['dirtyMatrices'],'imageAssets':p['imageAssets'],
        'contactNumbers':[c['number'] for c in members],
        'nonSpaceContacts':sum(not c['spaceHeld'] for c in members),
        'spaceContacts':sum(c['spaceHeld'] for c in members),
        'boundaryViewport':view['data']['state'] if view else None,
        'saveRequests':requests,'saveResults':results,
        'originalPacket':p
    })

tail=[r for r in hardware if r['appendId'] not in pointer_owner]
source_sha=hashlib.sha256(SOURCE.read_bytes()).hexdigest()
assert hashlib.sha256((ROOT/'native.memoline').read_bytes()).hexdigest()==verification['MechanicalSha256']
assert hashlib.sha256((ROOT/'aggregation.jsonl').read_bytes()).hexdigest()==verification['AggregationSha256']
summary={
    'source':str(SOURCE),'sourceSha256':source_sha,'sessionId':header['sessionId'],
    'createdAtLocal':created.isoformat(),'endedAtLocal':local(footer['ticks']),
    'durationSeconds':secs(footer['ticks']),'frequency':frequency,'device':header['metadata'],
    'packetCount':len(packets),'emptyPacketCount':sum(p['status']=='empty' for p in packets),
    'dirtyMatrixCount':sum(len(p['dirtyMatrices']) for p in packets),
    'imageAssetCount':sum(len(p['imageAssets']) for p in packets),
    'penDownRegionCounts':{name:sum(c['penDownLocation']['region']==code for c in contacts) for code,name,_ in REGION_TYPES},
    'contactCount':len(contacts),'nonSpaceContacts':sum(not c['spaceHeld'] for c in contacts),
    'spaceContacts':sum(c['spaceHeld'] for c in contacts),
    'packagedContacts':sum(c['packetNumber'] is not None for c in contacts),
    'unpackagedContacts':sum(c['packetNumber'] is None for c in contacts),
    'mechanicalHardwareEvents':len(hardware),'packagedHardwareEvents':len(pointer_owner),
    'unpackagedHardwareEvents':len(tail),'nativeAppendedRecords':len(native_by_append),
    'nativeKindCounts':dict(counts),'confirmedBrushObservations':len(brushes),
    'exportedEvidenceImages':len(wanted_images),
    'integrity':{'bundle':verification,'pointerErrors':pointer_errors,'allHardwareEventIdsSequential':True,'allAppendIdsSequential':True},
    'notes':[
        '所有逐笔状态关联均为核对报告从原生机械时间轴重建。原聚集包未包含 statePointers 字段，也没有矩阵状态标签。',
        '下笔区域按原生 workspaceStatus.layout.regions 与 penBegin.data.x/y 重判；36 段全部位于画布视口面板，其余六类均为零。区域不等同于操作性质。',
        '笔段指原生 penBegin 到 penEnd 的一次接触；Space 段疑似画布拖动，其余也不能单靠输入记录确认产生图像变化。',
        '笔刷状态沿用最近一次已确认的 triggerTicks 记录；每笔没有独立快照。未记录或 disabled/unknown 的属性不补默认值。',
        '部分识别结果晚于笔段开始才写入，报告使用其 triggerTicks 关联并保留 capture/appended 时间。',
        '图片来自机械记录的原始工具属性/图层/颜色面板截图，不是脏矩阵或 diff 图像。',
        '名称空格已由 LayerMapping.Compact 处理，不能仅凭名称差异断定空包根因。',
        '初始 CLIP 属性记录 ID=3，后续没有刷新 clipState；当前磁盘文件 ID=19 的补充检查不是历史保存快照。'
    ]
}
dump('audit.json',{'summary':summary,'packets':packet_reports,'contacts':contacts,'brushStates':brush_reports,
                   'dimensions':dimensions,'originalCoreStates':cores,'initialClipState':initial_clip,
                   'unpackagedHardwareEvents':tail,'shortcuts':shortcuts,
                   'currentClipSupplement':json.loads((ROOT/'current-clip-inspection.json').read_text(encoding='utf-8'))})
dump('summary.json',summary)

def E(value):return html.escape(str(value),quote=True)
def table(headers,rows):
    return '<div class="scroll"><table><thead><tr>'+''.join('<th>'+E(h)+'</th>' for h in headers)+'</tr></thead><tbody>'+''.join('<tr>'+''.join('<td>'+str(c)+'</td>' for c in row)+'</tr>' for row in rows)+'</tbody></table></div>'
def value_text(p):
    v=p.get('value');observed=p.get('observed') or {}
    if p.get('type')=='highlight_index': v=f'选项 {v}'+(f'（{observed["option_text"]}）' if observed.get('option_text') else '')
    if p.get('type')=='checkbox':v={'checked':'勾选','unchecked':'未勾选'}.get(v,v)
    if p.get('unit'):v=f'{v} {p["unit"]}'
    if p.get('status')!='ok' or p.get('enabled')=='disabled':v=f'{v}；状态={p.get("status")}；启用={p.get("enabled")}'
    return str(v)
def brief(c):
    b=c.get('brushState') or {}; props={p['key']:p['value'] for p in b.get('properties',[])}
    return f'{b.get("name","未知")} · 尺寸 {props.get("brush_size","未记录")}'
def contact_rows(members):
    rows=[]
    for c in members:
        flag='Space / 疑似拖动画布' if c['spaceHeld'] else '无 Space / 绘画或擦除候选'
        if c['durationMs']<100:flag+='；短接触'
        note='triggerTicks 沿用'
        if c['brushPublishedAfterContactStart']:note+='；识别结果在起笔后写入'
        if c['brushCapturedAfterContactStart']:note+='；截图晚于起笔'
        rows.append([E(c['number']),f'<code>{c["operationId"]}</code>',
                     f'{clock(c["startTicks"])}–{clock(c["endTicks"])}<small>{secs(c["startTicks"]):.7f}–{secs(c["endTicks"]):.7f} s</small>',
                     f'{c["durationMs"]:.1f} ms',E(c['penDownLocation']['name']),E(flag),
                     f'<a href="#{c["brushId"]}">{E(c["brushId"])} · {E(brief(c))}</a><small>{E(note)}</small>',
                     f'{c["pointCount"]} 点<small>pressure {c["rawPressureMin"]}–{c["rawPressureMax"]}</small>',
                     f'eventId {c["firstEventId"]}–{c["lastEventId"]}<small>appendId {c["beginAppendId"]}–{c["endAppendId"]}</small>'])
    return table(['笔段','operationId','时间（北京时间 / 录制秒数）','时长','下笔区域','操作识别','笔刷状态（点击看全部属性）','输入采样','原生指针'],rows)

parts_html=['''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Memoline 录制核对 · 20261005_124036</title><style>
:root{color-scheme:light}*{box-sizing:border-box}body{margin:0;background:#eef1f5;color:#202b3a;font:15px/1.6 "Microsoft YaHei",sans-serif}main{max-width:1560px;margin:auto;padding:26px}h1{font-size:28px;margin:0}h2{font-size:22px}h3{font-size:18px}p{margin:8px 0}section{background:white;margin:20px 0;padding:22px;border:1px solid #dbe1e9;border-radius:10px}.alert{background:#fff5e6;border-left:5px solid #dc8420;padding:14px;margin:16px 0}.cards{display:flex;gap:12px;flex-wrap:wrap}.card{background:#f1f5fa;min-width:160px;padding:12px 20px;border-radius:8px}.card b{font-size:25px;display:block}.scroll{overflow:auto}table{width:100%;border-collapse:collapse;margin:12px 0;min-width:650px}th,td{text-align:left;vertical-align:top;border-bottom:1px solid #dfe5ed;padding:10px 12px}th{background:#f1f5fa;white-space:nowrap}tr:hover{background:#f7faff}small{display:block;color:#617187;font-size:12px}code{font-family:Consolas,monospace;font-size:13px;overflow-wrap:anywhere}a{color:#1761a5}nav{display:flex;gap:16px;flex-wrap:wrap;margin:14px 0}details{border:1px solid #dce3ed;border-radius:8px;margin:14px 0;padding:14px}summary{cursor:pointer;font-weight:700}.brush-grid{display:grid;grid-template-columns:minmax(280px,1fr) minmax(280px,380px);gap:24px}.proof{max-width:100%;height:auto;border:1px solid #ccc}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f5f7fa;padding:12px}section,details{scroll-margin-top:15px}@media(max-width:800px){main{padding:12px}.brush-grid{grid-template-columns:1fr}}@media print{body{background:white}section{break-inside:avoid}.scroll{overflow:visible}details{display:block}}
</style><main>''']
parts_html.append('<h1>Memoline 录制核对报告</h1><p><code>'+E(SOURCE.name)+'</code></p>')
parts_html.append('<p>录制：'+E(created.isoformat(timespec='milliseconds'))+' → '+E(local(footer['ticks']))+' · '+f'{secs(footer["ticks"]):.3f} 秒 · Gaomon M6 / OpenTabletDriver.HID</p>')
parts_html.append('<div class="cards">'+''.join(f'<div class="card">{E(label)}<b>{value}</b></div>' for label,value in [('聚集事件包',len(packets)),('实际脏矩阵',summary['dirtyMatrixCount']),('笔接触段',len(contacts)),('无 Space 接触段',summary['nonSpaceContacts']),('带 Space 接触段',summary['spaceContacts']),('尾部未封包笔段',summary['unpackagedContacts'])])+'</div>')
parts_html.append('<div class="alert"><b>5 个聚集包全部为空，没有 diff 图像可展示。</b><br>每个包原文均为：'+E(packets[0]['reason'])+'。<br>逐笔笔刷关联来自下方原生机械记录的时间轴重建；原聚集包没有存入状态指针或矩阵标签。</div>')
parts_html.append('<nav>'+''.join(f'<a href="#packet{p["number"]}">包 {p["number"]}</a>' for p in packet_reports)+'<a href="#tail">未封包尾部</a><a href="#brushes">全部笔刷属性与截图</a><a href="audit.json">完整解析 JSON</a><a href="report.md">文本报告</a></nav>')
parts_html.append('<section><h2>按下笔位置重新判定</h2><p>依据文件内 workspaceStatus（appendId=5）的历史屏幕面板范围；每段用 penBegin 的 Windows 屏幕坐标判定。36 段全部属于画布视口，其中 15 段带 Space。其他面板没有记录到下笔；这不等于录制期间没有通过键盘等方式改变笔刷。</p>'+table(['区域','下笔段数'],[[E(k),v] for k,v in summary['penDownRegionCounts'].items()])+'</section>')
parts_html.append('<section><h2>聚集包总表</h2>')
parts_html.append(table(['包','时间范围（录制秒数）','边界北京时间','无 Space / 带 Space 笔段','机械事件指针','脏矩阵 / 图像','状态'],[[f'<a href="#packet{p["number"]}">{p["number"]}</a>',f'({elapsed(p["fromTicks"])}, {elapsed(p["toTicks"])}]',clock(p['toTicks']),f'{p["nonSpaceContacts"]} / {p["spaceContacts"]}',p['mechanicalEventPointerCount'],f'{p["dirtyMatrixCount"]} / {p["imageAssetCount"]}',E(p['status'])] for p in packet_reports]))
parts_html.append('<p>边界采用 Recognizer 的 triggerTicks；各包包含 (fromTicks, toTicks] 的事件。所有 7,134 个包内事件指针均已与原生记录核对。画布尺寸初始记录为 4961 × 7016，300 dpi。</p></section>')
for p in packet_reports:
    members=[c for c in contacts if c['packetNumber']==p['number']]
    parts_html.append(f'<section id="packet{p["number"]}"><h2>聚集包 {p["number"]} · {p["nonSpaceContacts"]} 段无 Space，{p["spaceContacts"]} 段带 Space</h2><code>{E(p["id"])}</code>')
    parts_html.append('<p>脏矩阵：<code>[]</code>；图像资产：<code>[]</code>；状态指针：未存入（未包含该字段）。失败原文：'+E(p['reason'])+'</p>')
    v=p.get('boundaryViewport') or {};o=v.get('canvasOriginScreenPx') or {}
    parts_html.append(f'<p>封包视口：屏幕画布原点 ({o.get("x")}, {o.get("y")})，缩放 {v.get("ocrScalePercent")}% ，旋转 {v.get("ocrRotationDegrees")}°。</p>')
    if p['saveResults']:
        r=p['saveResults'][0];a=r['data'];delta=(a['saveInputDispatchedTicks']-p['toTicks'])/frequency*1000
        parts_html.append(f'<p>控制接口已派发保存输入：{clock(a["saveInputDispatchedTicks"])}（比 triggerTicks 晚 {delta:.1f} ms）；error=null。原记录 saveCompletionConfirmed=false、cspAcceptanceConfirmed=false。保存控制结果 appendId={r["appendId"]}。</p>')
    parts_html.append(contact_rows(members)+'</section>')
parts_html.append('<section id="tail"><h2>最后封包之后：2 段笔接触尚未进入聚集包</h2><p>尾部有 '+str(len(tail))+' 个机械事件（2 个键盘事件、1,295 个笔事件）。发生在最后边界之后，文件封盘没有产生第 6 个聚集包。</p>'+contact_rows([c for c in contacts if c['packetNumber'] is None])+'</section>')
parts_html.append('<section id="brushes"><h2>12 条确认过的笔刷状态 · 全部原始属性</h2><p>名称与数值保留识别原文；highlight_index 显示选项序号，不转换成百分比。属性 unit=null 时不擅自补单位。每笔沿用其起笔之前最近一次确认状态；颜色仅记录过 #000202，图层接口仅确认过「图层1」。</p>')
for b in brush_reports:
    parts_html.append(f'<details open id="{b["id"]}"><summary>{b["id"]} · {E(b["name"])} · 用于笔段 {E(", ".join(map(str,b["contactNumbers"]))) or "无"} · appendId={b["appendId"]}</summary><p>trigger={clock(b["triggerTicks"])}（{elapsed(b["triggerTicks"])} s）；截图={E(b["captureLocal"])}；写入={E(b["appendLocal"])}；识别状态={E(b["recognitionStatus"])}。</p><div class="brush-grid"><div>')
    parts_html.append(table(['属性','实际值','原键 / 读取状态'],[[E(p['label']),E(value_text(p)),f'<code>{E(p["key"])}</code><small>status={E(p.get("status"))} · enabled={E(p.get("enabled"))} · unit={E(p.get("unit"))}</small>'] for p in b['properties']]))
    if b.get('visualAuditNote'):
        parts_html.append('<div class="alert">'+E(b['visualAuditNote'])+'</div>')
    if b['unresolved']:
        parts_html.append('<div class="alert">未结构化的可见文本：'+E('；'.join(u.get('raw_text','') for u in b['unresolved']))+'。不将这些文本当作已确认属性值。</div>')
    parts_html.append('</div><div>')
    for id_ in b['evidence'].get('screenshotIds') or []:
        parts_html.append(f'<a href="evidence/{id_}.png"><img class="proof" src="evidence/{id_}.png" alt="{E(b["id"])} 原始工具属性证据截图"></a><small>机械记录原图，点击放大；不是 diff 或脏矩阵图像。</small>')
    parts_html.append('</div></div></details>')
parts_html.append('</section><section><h2>证据范围与图层匹配问题</h2>')
parts_html.append('<p>原生机械记录：12,052 条追加记录、8,431 个输入事件，包含 36 次 penBegin、8,328 个 penSample、36 次 penEnd、31 个 keyInput；另有 450 个 screenshotBlob 记录、21 条确认核心状态。聚集性维度有 6 个采样：1 个视口基线 0、5 个变化 1；其余 5 个连续性字段全部为 null。</p>')
parts_html.append('<p>初始文件解析确认「图层 1」ID=3、UUID=<code>0dd394f6cd-0249-cda8-469e-51a828baaa</code>。当前图层接口的 OCR 名称是「图层1」。代码已经忽略名称空格；记录能证明的是文件图层身份匹配失败，不能仅由名称差异推断原因。</p>')
external=json.loads((ROOT/'current-clip-inspection.json').read_text(encoding='utf-8'))
parts_html.append('<p>补充检查（'+E(external['observedAt'])+'，检查的是当前磁盘 CLIP，不属于历史 memoline 内容）：同名「图层 1」的 ID=19，UUID=<code>c7609660dc-c345-85bb-c485-8da028fa6f</code>，与初始核心不同。历史保存快照不在封盘文件内，无法仅凭这份文件确认身份在哪一次操作改变。</p>')
parts_html.append('<details><summary>初始图层与颜色面板原始证据</summary>')
for r in layers+colors:
    for id_ in r['data']['evidence'].get('screenshotIds') or []:
        parts_html.append(f'<p>{E(r["data"]["module"])} · appendId={r["appendId"]}</p><a href="evidence/{id_}.png"><img class="proof" src="evidence/{id_}.png" alt="原始面板证据"></a>')
parts_html.append('</details>')
parts_html.append('<p>完整性：封盘哈希校验通过，原生 reader 完成逐帧 CRC 与 footer 检查；聚集包指针逐一核对通过；14 张导出的原始证据图像 SHA-256 与原记录一致。原 memoline 未被修改。</p><p><a href="native-full.jsonl">全部原生机械记录 JSONL（约 100 MB，含原始截图 base64）</a> · <a href="aggregation.jsonl">原聚集包 JSONL</a> · <a href="verification.json">封盘校验结果</a> · <a href="current-clip-inspection.json">当前 CLIP 补充检查</a></p></section></main></html>')
(ROOT/'report.html').write_text('\n'.join(parts_html),encoding='utf-8')

md=[f'# Memoline 录制核对报告\n\n文件：`{SOURCE.name}`',
    f'\n录制：{created.isoformat(timespec="milliseconds")} → {local(footer["ticks"])}；{secs(footer["ticks"]):.3f} 秒。',
    '\n5 个聚集包全部 empty；脏矩阵 0，差异图像 0。失败原文：'+packets[0]['reason'],
    '\n36 段原生笔接触：21 段无 Space、15 段带 Space；34 段进入 5 个聚集包，2 段留在机械记录尾部。无 Space 只表示绘画/擦除候选，不能从输入证明产生像素。',
    '\n逐笔状态是本报告按 triggerTicks 从原生时间轴重建，原包未包含状态指针字段。原截图是工具属性证据，不是 diff。',
    '\n按下笔位置重判：36 段全部属于画布视口面板；笔刷属性、笔刷选择、工具栏、导航器、图层和其他各 0 段。使用录制内 workspaceStatus appendId=5 的历史坐标，并未使用当前工作区回填。',
    '\n## 聚集包\n\n|包|范围（秒）|边界北京时间|无 Space / 带 Space|机械事件指针|脏矩阵|\n|---|---|---|---|---|---|']
for p in packet_reports:
    md.append(f'|{p["number"]}|({elapsed(p["fromTicks"])}, {elapsed(p["toTicks"])}]|{clock(p["toTicks"])}|{p["nonSpaceContacts"]} / {p["spaceContacts"]}|{p["mechanicalEventPointerCount"]}|0|')
for group in [1,2,3,4,5,None]:
    md.append('\n## '+(f'聚集包 {group}' if group else '尾部未封包')+'\n\n|笔段|operationId|起止北京时间|时长 ms|下笔区域|Space|笔刷状态|采样点|原 eventId|\n|---|---|---|---|---|---|---|---|---|')
    for c in contacts:
        if c['packetNumber']!=group:continue
        md.append(f'|{c["number"]}|{c["operationId"]}|{clock(c["startTicks"])}–{clock(c["endTicks"])}|{c["durationMs"]:.1f}|{c['penDownLocation']['name']}|{"是（疑似拖动）" if c["spaceHeld"] else "否"}|{c["brushId"]} {brief(c)}|{c["pointCount"]}|{c["firstEventId"]}–{c["lastEventId"]}|')
md.append('\n## 笔刷状态全部属性\n')
for b in brush_reports:
    md.append(f'\n### {b["id"]} {b["name"]}（appendId={b["appendId"]}）\n\ntriggerTicks={b["triggerTicks"]} / {clock(b["triggerTicks"])}；截图={b["captureLocal"]}；写入={b["appendLocal"]}；笔段：{b["contactNumbers"]}。\n\n|属性|值|读取状态|\n|---|---|---|')
    for p in b['properties']:md.append(f'|{p["label"]} (`{p["key"]}`)|{value_text(p)}|status={p.get("status")}, enabled={p.get("enabled")}, unit={p.get("unit")}|')
    if b.get('visualAuditNote'):md.append('\n截图核对提醒：'+b['visualAuditNote'])
    if b['unresolved']:md.append('\n未结构化文本：'+ '；'.join(u.get('raw_text','') for u in b['unresolved']))
    for id_ in b['evidence'].get('screenshotIds') or []:md.append(f'\n原始工具属性证据：[截图](evidence/{id_}.png)。')
md.append('\n## 核对说明\n\n'+ '\n\n'.join(summary['notes']))
md.append('\n当前磁盘文件补充检查：同名图层 ID=19、UUID=c7609660dc-c345-85bb-c485-8da028fa6f。检查时间 '+external['observedAt']+'；不是历史保存快照。')
md.append('\n所有封盘哈希、7,134 个包内事件指针、原生逐帧 CRC/footer 和 14 张导出证据截图哈希检查通过。详细原始状态、指针和尾部事件在 `audit.json`。')
(ROOT/'report.md').write_text('\n'.join(md),encoding='utf-8')
print(json.dumps({k:summary[k] for k in ['packetCount','emptyPacketCount','dirtyMatrixCount','contactCount','nonSpaceContacts','spaceContacts','packagedContacts','unpackagedContacts','confirmedBrushObservations','exportedEvidenceImages']},ensure_ascii=True))
print('Packet rows:',json.dumps([{k:p[k] for k in ['number','nonSpaceContacts','spaceContacts','mechanicalEventPointerCount']} for p in packet_reports]))
print('Report:',str(ROOT/'report.html'))
