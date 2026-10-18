"""Read CSP's History palette: one row per undo unit, oldest first.

The palette lists the document's opening state as its first row (the document or page name), then
every undo unit. The highlighted row is the current state; the rows after it are undone and drawn
in a dimmer text colour. Rows are separated by one-pixel dark lines at a fixed pitch.

Identical entries (the same tool used for many strokes) render identical glyph masks, so each
distinct mask is read by OCR once and the text reused; repeated entries are what lets a reader
count how many of a run were undone.
"""
from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Any

import numpy as np

# A separator row is darker than both row backgrounds across most of the list width.
SEPARATOR_MAX = 45
SEPARATOR_COVERAGE = 0.8
# Text pixels differ from their row background by at least this much.
GLYPH_CONTRAST = 28
# The window frame and the selection accent occupy the first columns.
LEFT_MARGIN = 4
# Dark theme: active entries are drawn at about 200, undone ones at about 146, on a 58-62 background.
UNDONE_BRIGHTNESS = 175
# Light theme (no recording seen yet): an entry with less than this share of the strongest contrast is undone.
UNDONE_RATIO = 0.8


@dataclass
class Row:
    index: int
    top: int
    bottom: int
    background: tuple[int, int, int]
    contrast: int
    brightness: int
    mask_hash: str | None
    selected: bool


def measure_rows(image: np.ndarray) -> tuple[int, int] | None:
    """(first row top, row pitch) of a History palette image (RGB or BGR), or None when no row is found.

    The panel stash uses it to size the palette so that every undo step is listed at once.
    """
    if image is None or image.ndim != 3 or image.shape[0] < 20 or image.shape[1] < 40:
        return None
    rgb = image[:, :, :3].astype(np.int16)
    separators = HistoryStateCore._separators(rgb.mean(axis=2))
    pitch, line = HistoryStateCore._pitch(separators), None
    if pitch is None:
        # The highlight test needs RGB order; a BGR image has the blue channel first.
        for candidate in (rgb, rgb[:, :, ::-1]):
            pitch, line = HistoryStateCore._selection_pitch(candidate, separators)
            if pitch is not None:
                break
    if pitch is None:
        return None
    chain = HistoryStateCore._chain(separators, pitch)
    first = line if len(chain) == 1 and line is not None else chain[0]
    return max(0, first - pitch + 1), pitch


class HistoryStateCore:
    def __init__(self, ocr_engine: Any = None):
        self._engine = ocr_engine
        self._texts: dict[str, str] = {}

    def process(self, image: np.ndarray) -> dict:
        """`image`: BGR (OpenCV order) pixels of the whole History palette window."""
        if image is None or image.ndim != 3 or image.shape[0] < 20 or image.shape[1] < 40:
            return dict(status="unreadable", reason="imageTooSmall", rows=[])
        rgb = image[:, :, ::-1].astype(np.int16)
        gray = rgb.mean(axis=2)
        separators = self._separators(gray)
        if len(separators) < 1:
            return dict(status="unreadable", reason="noRowSeparators", rows=[])
        pitch, selection_line = self._pitch(separators), None
        if pitch is None:
            pitch, selection_line = self._selection_pitch(rgb, separators)
        if pitch is None:
            return dict(status="unreadable", reason="irregularRowPitch", rows=[], separators=separators)
        # The list is the longest run of separators one pitch apart (the tab bar above has lines of its
        # own). Each row ends at a separator; the first row starts one pitch above the first one.
        chain = self._chain(separators, pitch)
        if len(chain) == 1 and selection_line is not None:
            chain = [selection_line]
        bounds = [(max(0, line - pitch + 1), line) for line in chain]
        rows = [self._row(rgb, i, top, bottom) for i, (top, bottom) in enumerate(bounds)]
        if not rows:
            return dict(status="unreadable", reason="noRows", rows=[])
        # A last row cut by the window bottom has no separator below it; anything drawn there is truncation.
        below = gray[min(gray.shape[0], bounds[-1][1] + 1):]
        truncated = below.shape[0] >= pitch * 0.5 and self._has_text(rgb[bounds[-1][1] + 1:], self._background(rgb[bounds[-1][1] + 1:]))
        dark_theme = float(np.mean(rows[-1].background)) < 128
        brightest = max((r.contrast for r in rows if r.mask_hash), default=0)
        selected = [r.index for r in rows if r.selected]
        current = selected[0] if len(selected) == 1 else None
        result_rows = []
        for row in rows:
            text = self._read(rgb, row) if row.mask_hash else ""
            dim = bool(row.mask_hash) and not row.selected and (
                row.brightness < UNDONE_BRIGHTNESS if dark_theme else row.contrast < brightest * UNDONE_RATIO)
            result_rows.append(dict(index=row.index, text=text, undone=dim, selected=row.selected,
                                    bbox=[0, row.top, int(rgb.shape[1]), row.bottom - row.top], textHash=row.mask_hash,
                                    brightness=row.brightness))
        # Undone rows can only follow the current row; when every row after it is dim the reading is consistent.
        consistent = current is not None and all(r["undone"] == (r["index"] > current) for r in result_rows[1:])
        status = "ok" if consistent and not truncated else "inconsistent" if not consistent else "truncated"
        return dict(status=status, rows=result_rows, currentIndex=current,
                    activeCount=(current if current is not None else None),
                    undoneCount=sum(1 for r in result_rows if r["undone"]),
                    rowCount=len(result_rows), rowPitch=pitch, listTop=bounds[0][0], truncated=bool(truncated))

    @staticmethod
    def _separators(gray: np.ndarray) -> list[int]:
        width = gray.shape[1]
        # Ignore the scroll bar and frame at the edges.
        core = gray[:, int(width * 0.05):int(width * 0.9)]
        dark = (core <= SEPARATOR_MAX).mean(axis=1) >= SEPARATOR_COVERAGE
        lines = [y for y in range(1, gray.shape[0]) if dark[y] and not dark[y - 1]]
        return lines

    @staticmethod
    def _pitch(lines: list[int]) -> int | None:
        if len(lines) < 3:
            return None
        gaps = np.diff(lines)
        pitch = int(np.median(gaps))
        if pitch < 10:
            return None
        regular = np.abs(gaps - pitch) <= 2
        return pitch if regular.mean() >= 0.6 else None

    @staticmethod
    def _selection_pitch(rgb: np.ndarray, separators: list[int]) -> tuple[int | None, int | None]:
        """With fewer than three rows the gaps say nothing; the highlighted row spans exactly one pitch."""
        column = rgb[:, int(rgb.shape[1] * 0.6)]
        blue = (column[:, 2] - column[:, 0] >= 18) & (column[:, 2] - column[:, 1] >= 10)
        for line in separators:
            top = line - 1
            while top >= 0 and blue[top]:
                top -= 1
            if line - 1 - top >= 10:
                return line - top, line
        return None, None

    @staticmethod
    def _chain(lines: list[int], pitch: int) -> list[int]:
        best, current = [], []
        for line in lines:
            if current and abs(line - current[-1] - pitch) <= 2:
                current.append(line)
            else:
                current = [line]
            if len(current) > len(best):
                best = list(current)
        return best

    @staticmethod
    def _background(block: np.ndarray) -> tuple[int, int, int]:
        if block.size == 0:
            return (0, 0, 0)
        pixels = block.reshape(-1, 3)
        values, counts = np.unique(pixels, axis=0, return_counts=True)
        return tuple(int(v) for v in values[counts.argmax()])

    @staticmethod
    def _has_text(block: np.ndarray, background) -> bool:
        if block.size == 0:
            return False
        diff = np.abs(block - np.array(background, dtype=np.int16)).max(axis=2)
        return int((diff >= GLYPH_CONTRAST).sum()) >= 8

    def _row(self, rgb: np.ndarray, index: int, top: int, bottom: int) -> Row:
        block = rgb[top:bottom, LEFT_MARGIN: int(rgb.shape[1] * 0.95)]
        background = self._background(block)
        diff = np.abs(block - np.array(background, dtype=np.int16)).max(axis=2)
        glyphs = diff >= GLYPH_CONTRAST
        contrast = int(np.percentile(diff[glyphs], 90)) if glyphs.sum() >= 8 else 0
        brightness = int(np.percentile(block.mean(axis=2)[glyphs], 90)) if glyphs.sum() >= 8 else 0
        mask_hash = None
        if glyphs.sum() >= 8:
            ys, xs = np.nonzero(glyphs)
            trimmed = glyphs[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
            mask_hash = hashlib.sha1(np.packbits(trimmed).tobytes() + bytes(str(trimmed.shape), "ascii")).hexdigest()[:16]
        r, g, b = background
        return Row(index, top, bottom, background, contrast, brightness, mask_hash, selected=b - r >= 18 and b - g >= 10)

    def _read(self, rgb: np.ndarray, row: Row) -> str:
        if row.mask_hash in self._texts:
            return self._texts[row.mask_hash]
        block = rgb[row.top:row.bottom, LEFT_MARGIN: int(rgb.shape[1] * 0.95)]
        diff = np.abs(block - np.array(row.background, dtype=np.int16)).max(axis=2)
        # Dark glyphs on white at twice the size: the same input whatever the theme, colour or state.
        strength = np.clip(diff * (255.0 / max(row.contrast, 1)), 0, 255)
        canvas = (255 - strength).astype(np.uint8)
        canvas = np.pad(canvas, 6, constant_values=255)
        canvas = np.repeat(np.repeat(canvas, 2, axis=0), 2, axis=1)
        text = self._ocr(np.stack([canvas] * 3, axis=2))
        self._texts[row.mask_hash] = text
        return text

    def _ocr(self, image: np.ndarray) -> str:
        if self._engine is None:
            from rapidocr import RapidOCR
            self._engine = RapidOCR()
        # One entry is one line of text: recognition only. Detection split "G筆" into overlapping
        # boxes and read an extra character from the overlap.
        raw = self._engine(image, use_det=False, use_cls=False)
        result = raw[0] if isinstance(raw, tuple) else raw
        texts = getattr(result, "txts", None) if result is not None else None
        return normalize("".join(str(text) for text in texts)) if texts else ""


def normalize(text: str) -> str:
    """OCR splits a path at its separators; join the parts with single slashes."""
    parts = [part.strip() for part in text.replace("／", "/").split("/")]
    return "/".join(part for part in parts if part)
