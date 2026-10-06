"""Offline fixture verification; never sends input to CSP."""
import json
from pathlib import Path
import sqlite3
import sys

ROOT=Path(__file__).resolve().parents[3]
OUT=Path(__file__).resolve().parent
runtime=ROOT/'Memoline_demo_csponly/publish/win-x64/Recognizer/integration'
libs=runtime/'python_libs'
published='--published' in sys.argv
sys.path[:0]=[str(runtime) if published else str(ROOT/'recognizer'),
             str(runtime) if published else str(ROOT/'recognizer/recorder_integration'),
             str(ROOT/'CSP_Shortcut_Manager'),str(libs)]
sys.stdout.reconfigure(encoding='utf-8')
import cv2
from rapidocr import RapidOCR
from recognizer_core.panel_state import PanelStateCore
from recognizer_core.subtool_state import SubtoolPanelCore
from recognizer_core.clip_layers_core import read_clip_layers,_read_db
from state_timeline import semantic_state
from csp_shortcuts import read_tool_inventory

models=libs/'rapidocr/models'
ocr=RapidOCR(params={'EngineConfig.onnxruntime.intra_op_num_threads':2,
    'EngineConfig.onnxruntime.inter_op_num_threads':1,'Global.log_level':'warning',
    'Det.model_path':str(models/'PP-OCRv6_det_small.onnx'),
    'Rec.model_path':str(models/'PP-OCRv6_rec_small.onnx'),
    'Cls.model_path':str(models/'ch_ppocr_mobile_v2.0_cls_mobile.onnx')})
tool_db=Path(r'C:\Users\dogeegg\AppData\Roaming\CELSYSUserData\CELSYS\CLIPStudioPaintVer1_5_0\Tool\EditImageTool.todb')
catalog=read_tool_inventory(tool_db)
brush_path=ROOT/'replayer/packetreplayer/reports/20261006-170144/requested-brush.png'
subtool_path=Path(r'C:\Users\dogeegg\AppData\Local\Temp\codex-clipboard-87dcc4ce-a0ab-446d-8be8-b2f6f09e2e02.png')
brush=cv2.imread(str(brush_path))
subtool=cv2.imread(str(subtool_path))
assert brush is not None and subtool is not None
subtools=SubtoolPanelCore(ocr_engine=ocr).process(subtool,catalog=catalog)
result=PanelStateCore(ocr_engine=ocr).process(brush,tool_catalog=catalog,subtool_image=subtool,
    subtool_evidence=dict(source='offlineFixtures',note='历史属性截图与用户另附子工具截图用于验证识别，未宣称为历史同次采集'))
state=semantic_state('brushState',result)
suffix='published' if published else 'source'
(OUT/f'ocr-verification-{suffix}.json').write_text(json.dumps(dict(
    fixtureMode='Two independent user-provided images, offline only',subtools=subtools,brush=result,
    semanticState=state),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(dict(runtime=suffix,name=result['brush']['name'],
    resolution=result['brush']['name_resolution']['source'],
    properties=[{k:p.get(k) for k in ('key','type','value','status')} for p in result['brush']['properties']],
    confirmed=state is not None,subtools=[e['name'] for e in subtools['entries']],
    groups=[e['name'] for e in subtools['groupEntries']]),ensure_ascii=False))
assert result['brush']['name']=='較硬'
assert result['brush']['name_resolution']['source']=='selected_subtool'
assert state is not None
assert next(p for p in result['brush']['properties'] if p['key']=='antialiasing')['value']==3
vector=next(p for p in result['brush']['properties'] if p['key']=='erase.vector_eraser')
assert vector['status']=='disabled' and vector['value']=='unknown'

clip=Path(r'D:\感官解冻原画及参考\插画6.clip')
if clip.exists():
    data=read_clip_layers(clip)
    with sqlite3.connect(':memory:') as c:
        c.deserialize(_read_db(clip))
        c.execute('PRAGMA query_only=ON')
        columns=[r[1] for r in c.execute('PRAGMA table_info(Layer)')]
        used=[k for k in columns if any(part.casefold() in k.casefold() for part in ('color','effect','tone','reference','mask','lock','draft','opacity','composite'))]
    (OUT/'layer-configuration-audit.json').write_text(json.dumps(dict(
        sourceFile=str(clip),source='Saved file; no forced save performed',snapshot=data,
        layerColumns=columns,relevantColumns=used),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(savedClipLayers=len(data['layers']),layerColumns=len(columns)),ensure_ascii=False))
