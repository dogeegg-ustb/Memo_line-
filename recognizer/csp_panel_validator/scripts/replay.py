from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import cv2

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))

from csp_panel_validator.capture_service import CaptureService
from csp_panel_validator.models import CaptureFrame
from csp_panel_validator.ocr_engine import RapidOcrEngine, SidecarOcrEngine
from csp_panel_validator.profile_store import ProfileStore
from csp_panel_validator.recognition_pipeline import RecognitionPipeline
from csp_panel_validator.models import utc_now


def main() -> int:
    parser = argparse.ArgumentParser(description="CSP Panel Validator 离线回放")
    parser.add_argument("--profile", required=True, help="ROI 配置 JSON")
    parser.add_argument("--image", required=True, help="整张显示器截图")
    parser.add_argument("--ocr-json", help="可重复的 ROI sidecar OCR JSON")
    parser.add_argument("--out", required=True, help="结果 JSON")
    args = parser.parse_args()
    profile = ProfileStore(Path(args.profile).parent).load(args.profile)
    image = cv2.imread(args.image, cv2.IMREAD_COLOR)
    if image is None:
        raise SystemExit(f"无法读取截图: {args.image}")
    frame = CaptureFrame(1, utc_now(), image)
    sidecar = SidecarOcrEngine(args.ocr_json) if args.ocr_json else None
    engine = RapidOcrEngine()
    outcome = RecognitionPipeline(engine).process(frame, profile, sidecar=sidecar)
    Path(args.out).write_text(json.dumps(outcome.result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": outcome.result["status"], "timings_ms": outcome.result["timings_ms"], "out": args.out}, ensure_ascii=False))
    return 0 if outcome.result["status"] not in ("unavailable", "needs_reselection") else 2


if __name__ == "__main__":
    raise SystemExit(main())
