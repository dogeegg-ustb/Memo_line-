"""Find highlighted list entries before OCR; never OCR the rest of a sub-tool ROI."""
from __future__ import annotations

import cv2
import numpy as np

from .models import Rect


def selected_regions(image: np.ndarray) -> list[Rect]:
    height, width = image.shape[:2]
    if min(height, width) < 8:
        return []
    hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
    # A colored background must occupy most of a row, unlike a colored icon.
    colored = (hsv[:, :, 1] >= 28) & (hsv[:, :, 2] >= 35)
    left, right = int(width * .08), max(1, int(width * .92))
    active = colored[:, left:right].mean(axis=1) >= .60
    medians = np.median(image[:, left:right], axis=1)
    spans = []
    start = None
    for y in range(height + 1):
        keep = y < height and active[y]
        split = start is not None and keep and np.linalg.norm(medians[y] - medians[y - 1]) > 12
        if start is not None and (not keep or split):
            if y - start >= 8:
                spans.append((start, y))
            start = None
        if keep and start is None:
            start = y
    boxes = []
    for top, bottom in spans:
        columns = np.flatnonzero(colored[top:bottom].mean(axis=0) >= .45)
        if not len(columns):
            continue
        x0, x1 = int(columns[0]), int(columns[-1]) + 1
        if x1 - x0 < .60 * width:
            continue
        boxes.append(Rect(x0, top, x1 - x0, bottom - top))
    return boxes
