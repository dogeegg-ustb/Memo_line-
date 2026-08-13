"""Fast local run-length checks at candidate edge pixels (no full HxW RLE)."""

from __future__ import annotations

import numpy as np


def run_right(mask: np.ndarray, y: int, x: int, limit: int = 12) -> int:
    w = mask.shape[1]
    n = 0
    while x + n < w and n < limit and mask[y, x + n]:
        n += 1
    return n


def run_left(mask: np.ndarray, y: int, x: int, limit: int = 12) -> int:
    n = 0
    while x - n >= 0 and n < limit and mask[y, x - n]:
        n += 1
    return n


def run_down(mask: np.ndarray, y: int, x: int, limit: int = 12) -> int:
    h = mask.shape[0]
    n = 0
    while y + n < h and n < limit and mask[y + n, x]:
        n += 1
    return n


def run_up(mask: np.ndarray, y: int, x: int, limit: int = 12) -> int:
    n = 0
    while y - n >= 0 and n < limit and mask[y - n, x]:
        n += 1
    return n


def run_lengths_true_rows(mask2d: np.ndarray) -> np.ndarray:
    """Legacy full-map API (slow). Prefer local run_* helpers."""
    h, w = mask2d.shape
    out = np.zeros((h, w), dtype=np.int16)
    m = mask2d.astype(np.uint8)
    for y in range(h):
        row = m[y]
        if not row.any():
            continue
        padded = np.empty(w + 2, dtype=np.uint8)
        padded[0] = 0
        padded[-1] = 0
        padded[1:-1] = row
        diff = np.diff(padded.astype(np.int8))
        starts = np.flatnonzero(diff == 1)
        ends = np.flatnonzero(diff == -1)
        for s, e in zip(starts.tolist(), ends.tolist()):
            out[y, s:e] = e - s
    return out


def run_lengths_true_cols(mask2d: np.ndarray) -> np.ndarray:
    return run_lengths_true_rows(mask2d.T).T
