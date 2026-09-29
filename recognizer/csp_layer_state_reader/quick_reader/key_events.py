"""Save panel frames around a toolbar action that changes the layer list."""

from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from time import monotonic

import cv2
import numpy as np


@dataclass
class _Frame:
    number: int
    image: np.ndarray
    captured_at: datetime
    clock: float


class KeyEventRecorder:
    """One instance is consumed by one background worker, in capture order."""

    def __init__(self, output_dir: Path, candidate_seconds: float = 4.0) -> None:
        self.output_dir = Path(output_dir)
        self.candidate_seconds = candidate_seconds
        self.previous: _Frame | None = None
        self.candidate: _Frame | None = None
        self.last_saved_at = -float("inf")

    @staticmethod
    def _gray(image: np.ndarray) -> np.ndarray:
        return cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)

    @classmethod
    def _aligned(cls, before: np.ndarray, after: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        height = min(before.shape[0], after.shape[0])
        width = min(before.shape[1], after.shape[1])
        first = before[:height, :width]
        second = after[:height, :width]
        if height < 140 or width < 170:
            return first, second
        shift, confidence = cv2.phaseCorrelate(
            cls._gray(first).astype(np.float32), cls._gray(second).astype(np.float32)
        )
        dx, dy = (round(shift[0]), round(shift[1]))
        if confidence >= 0.35 and abs(dx) <= 12 and abs(dy) <= 12:
            second = cv2.warpAffine(
                second, np.float32([[1, 0, -dx], [0, 1, -dy]]),
                (width, height), borderMode=cv2.BORDER_REPLICATE,
            )
        return first, second

    @classmethod
    def _scores(cls, before: np.ndarray, after: np.ndarray) -> dict[str, float]:
        first, second = cls._aligned(before, after)
        height, width = first.shape[:2]
        if height < 140 or width < 170:
            return dict.fromkeys((
                "toolbar_change", "list_edge_change", "icon_edge_change",
                "mask_button_change",
            ), 0.0)
        toolbar_end = min(round(width * 0.27), round(height * 0.38))
        gray_first, gray_second = cls._gray(first), cls._gray(second)
        inset = max(3, round(width * 0.02))
        top_a = gray_first[:toolbar_end, inset:-inset]
        top_b = gray_second[:toolbar_end, inset:-inset]
        list_a = gray_first[toolbar_end:, inset:-inset]
        list_b = gray_second[toolbar_end:, inset:-inset]
        top_diff = cv2.absdiff(top_a, top_b)
        list_edges_a = cv2.Canny(list_a, 60, 150)
        list_edges_b = cv2.Canny(list_b, 60, 150)
        edge_diff = cv2.absdiff(list_edges_a, list_edges_b)
        # Thumbnails and the new mask tile sit near the left side of each row.
        icon_end = min(list_a.shape[1], round(width * 0.48))
        icon_start = min(icon_end - 1, round(width * 0.13))
        icon_diff = edge_diff[:, icon_start:icon_end]
        mask_a = gray_first[round(width * 0.13):round(width * 0.22),
                            round(width * 0.65):round(width * 0.79)]
        mask_b = gray_second[round(width * 0.13):round(width * 0.22),
                             round(width * 0.65):round(width * 0.79)]
        return {
            "toolbar_change": float(np.mean(top_diff > 20)),
            "list_edge_change": float(np.mean(edge_diff > 0)),
            "icon_edge_change": float(np.mean(icon_diff > 0)),
            "mask_button_change": float(np.mean(cv2.absdiff(mask_a, mask_b) > 20)),
        }

    @staticmethod
    def _changed(scores: dict[str, float]) -> bool:
        return (
            scores["list_edge_change"] >= 0.008
            or scores["icon_edge_change"] >= 0.018
        )

    def consume(
        self, frame_number: int, image: np.ndarray,
        captured_at: datetime | None = None, clock: float | None = None,
    ) -> Path | None:
        frame = _Frame(frame_number, image.copy(), captured_at or datetime.now(),
                       monotonic() if clock is None else clock)
        previous = self.previous
        self.previous = frame
        if previous is None or previous.image.shape != image.shape:
            self.candidate = None
            return None

        if self.candidate is not None:
            if frame.clock - self.candidate.clock > self.candidate_seconds:
                self.candidate = None
            else:
                scores = self._scores(self.candidate.image, frame.image)
                if self._changed(scores):
                    before = self.candidate
                    self.candidate = None
                    self.last_saved_at = frame.clock
                    return self._save(before, frame, scores)

        if frame.clock - self.last_saved_at < 1.0:
            return None
        scores = self._scores(previous.image, frame.image)
        # A toolbar highlight, including the mask button turning gray, arms the
        # comparison. A change in the selected row by itself cannot save an event.
        if scores["toolbar_change"] >= 0.006 or scores["mask_button_change"] >= 0.12:
            if self._changed(scores):
                self.last_saved_at = frame.clock
                return self._save(previous, frame, scores)
            self.candidate = previous
        return None

    def _save(self, before: _Frame, after: _Frame, scores: dict[str, float]) -> Path:
        stamp = after.captured_at.strftime("%Y%m%d_%H%M%S_%f")[:-3]
        event_dir = self.output_dir / f"{stamp}_frame{after.number:06d}"
        event_dir.mkdir(parents=True, exist_ok=False)
        for name, frame in (("before.png", before), ("after.png", after)):
            ok, encoded = cv2.imencode(".png", frame.image)
            if not ok:
                raise RuntimeError(f"无法编码关键事件截图：{name}")
            encoded.tofile(str(event_dir / name))
        info = {
            "before_frame": before.number,
            "after_frame": after.number,
            "before_time": before.captured_at.isoformat(timespec="milliseconds"),
            "after_time": after.captured_at.isoformat(timespec="milliseconds"),
            "reason": "功能栏变化后，图层或组区域发生结构变化",
            "scores": {key: round(value, 5) for key, value in scores.items()},
        }
        (event_dir / "event.json").write_text(
            json.dumps(info, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        return event_dir
