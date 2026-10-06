"""Re-run the shipped OCR and parser on recorded pixels, without controlling CSP."""
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
INTEGRATION = ROOT / 'Memoline_demo_csponly/publish/win-x64/Recognizer/integration'
LIBS = INTEGRATION / 'python_libs'
sys.path[:0] = [str(INTEGRATION), str(LIBS)]
sys.stdout.reconfigure(encoding='utf-8')
import cv2
from rapidocr import RapidOCR
from recognizer_core.panel_state import PanelStateCore
from recognizer_core.panel_state.models import RoiRecognition
from recognizer_core.panel_state.visual_value_extraction import _row_options, _highlight_index_from_visual, apply_visual_values
from state_timeline import semantic_state

models = LIBS / 'rapidocr/models'
ocr = RapidOCR(params={
    'EngineConfig.onnxruntime.intra_op_num_threads': 2,
    'EngineConfig.onnxruntime.inter_op_num_threads': 1,
    'Global.log_level': 'warning',
    'Det.model_path': str(models / 'PP-OCRv6_det_small.onnx'),
    'Rec.model_path': str(models / 'PP-OCRv6_rec_small.onnx'),
    'Cls.model_path': str(models / 'ch_ppocr_mobile_v2.0_cls_mobile.onnx'),
})
image = cv2.imread(str(OUT / 'requested-brush.png'))
core = PanelStateCore(ocr_engine=ocr)
items = core._normalize(core._recognize(image))
states = core.visual.analyze(image, items)
recognition = core.parser.parse('tool_properties', 'tool_properties', items, states)
antialias = next(p for p in recognition.parameters if p['key'] == 'antialiasing')
label = next(e['bbox'] for e in antialias['evidence'] if e.get('bbox'))
options = _row_options(recognition, label)
lookup = {id(i): s for i, s in zip(items, states)}
apply_visual_values(image, recognition)
assembled = core.assembler.assemble(0, 'recorded-pixels', 'offline-reproduction', [recognition], {})
report = dict(ocrWords=[dict(text=i.text, bbox=i.bbox(), selected=s.selected, score=s.selected_score) for i,s in zip(items,states)],
              brushName=assembled['brush']['name'], properties=[{k:p.get(k) for k in ('key','type','value','status')} for p in assembled['brush']['properties']],
              unresolved=[u.get('raw_text') for u in assembled['unresolved']], antialiasLabelBox=label,
              antialiasOptions=[dict(text=i.text,bbox=i.bbox(),selected=lookup[id(i)].selected,score=lookup[id(i)].selected_score) for i in options],
              antialiasHighlightedIndex=_highlight_index_from_visual(options,lookup), semanticState=semantic_state('brushState',assembled),
              knownAntialiasEnums=core.catalog.by_key['antialiasing']['enum_values'])
(OUT / 'parser-reproduction.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
