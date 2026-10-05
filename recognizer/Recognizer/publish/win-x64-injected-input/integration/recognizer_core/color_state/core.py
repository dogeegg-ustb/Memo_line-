from __future__ import annotations

from dataclasses import asdict
from typing import Any

import numpy as np

from .color_recognition import recognize_color


class ColorStateCore:
    """Read the current CSP color from one caller-supplied color-panel crop."""

    def process(self, color_panel_image: np.ndarray) -> dict[str, Any]:
        if not isinstance(color_panel_image, np.ndarray):
            raise TypeError("color_panel_image must be a NumPy array")
        if color_panel_image.size == 0:
            raise ValueError("color_panel_image must not be empty")
        if (color_panel_image.ndim != 3 or color_panel_image.shape[2] != 3
                or color_panel_image.dtype != np.uint8):
            raise ValueError("color_panel_image must be a uint8 BGR image with shape (height, width, 3)")
        reading = recognize_color(color_panel_image)
        return {**asdict(reading), "hex": reading.hex}
