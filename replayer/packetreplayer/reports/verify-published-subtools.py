"""Verify the published Memoline OCR -> PacketReplay payload, without UI input."""
import json
from pathlib import Path
import sys
from unittest.mock import patch

sys.stdout.reconfigure(encoding="utf-8")

repo = Path(__file__).resolve().parents[3]
runtime = repo / "Memoline_demo_csponly/publish/win-x64/Recognizer/integration"
libs = runtime / "python_libs"
sys.path[:0] = [str(runtime), str(libs)]
import cv2
from rapidocr import RapidOCR
from catalog import Catalog
from recognizer_core.subtool_state import SubtoolPanelCore
from pipeline import Pipeline
from state_timeline import StateTimeline

models = libs / "rapidocr/models"
ocr = RapidOCR(params={
    "EngineConfig.onnxruntime.intra_op_num_threads": 2,
    "EngineConfig.onnxruntime.inter_op_num_threads": 1,
    "Global.log_level": "warning",
    "Det.model_path": str(models / "PP-OCRv6_det_small.onnx"),
    "Rec.model_path": str(models / "PP-OCRv6_rec_small.onnx"),
    "Cls.model_path": str(models / "ch_ppocr_mobile_v2.0_cls_mobile.onnx"),
})
image = cv2.imread(sys.argv[1])
result = SubtoolPanelCore(ocr_engine=ocr).process(image, catalog=Catalog().tool_catalog)
messages = []
pipe = Pipeline.__new__(Pipeline)
pipe.emit = messages.append
timeline = StateTimeline(lambda _: None, lambda _: None, pipe._publish_core_update)
with patch("pipeline.now_ticks", return_value=200):
    timeline.observe("subtoolState", result, dict(panelRoi=[300, 500, image.shape[1], image.shape[0]],
        captureId="published-consumer-check", capturedTicks=100, captureEndTicks=110))
Path(sys.argv[2]).write_text(json.dumps(messages[-1]["data"], ensure_ascii=False, indent=2), encoding="utf-8")
print("Published OCR payload:", [entry["name"] for entry in messages[-1]["data"]["ocrEntries"]])
