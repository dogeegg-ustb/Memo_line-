from __future__ import annotations

from dataclasses import dataclass
from typing import Any

import cv2
import numpy as np


@dataclass
class ColorReading:
    kind: str
    rgb: tuple[int, int, int] | None
    confidence: float
    detail: str
    hue: int | None = None
    sv_point: tuple[int, int] | None = None

    @property
    def hex(self) -> str | None:
        if self.kind == "transparent":
            return "#00000000"
        if self.rgb is None:
            return None
        return "#%02X%02X%02X" % self.rgb


def _to_rgb(bgr: np.ndarray) -> tuple[int, int, int]:
    b, g, r = (int(round(float(value))) for value in np.median(bgr.reshape(-1, 3), axis=0))
    return r, g, b


def _accent_mask(image: np.ndarray) -> np.ndarray:
    hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
    # The selected CSP swatch outline is a bright, saturated blue in the
    # supplied theme. Keep the hue range generous for alternate UI themes.
    return ((hsv[:, :, 1] >= 34) & (hsv[:, :, 2] >= 115)).astype(np.uint8)


def _ring_coverage(mask: np.ndarray, cx: float, cy: float, radius: float) -> float:
    height, width = mask.shape
    best = 0.0
    angles = np.linspace(0, 2 * np.pi, 144, endpoint=False)
    for offset in (-3, -1, 1, 3, 5):
        r = max(3.0, radius + offset)
        xs = np.rint(cx + r * np.cos(angles)).astype(int)
        ys = np.rint(cy + r * np.sin(angles)).astype(int)
        inside = (xs >= 0) & (xs < width) & (ys >= 0) & (ys < height)
        if inside.any():
            best = max(best, float(mask[ys[inside], xs[inside]].mean()))
    return best


def _color_swatches(image: np.ndarray) -> list[tuple[float, float, float, float]]:
    """Return plausible foreground/background color-disc centers and radii."""
    height, width = image.shape[:2]
    compact_crop = max(width, height) < 300
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    scale = min(width, height)
    min_radius = max(5, round(scale * (0.10 if compact_crop else 0.022)))
    max_radius = max(min_radius + 2, round(scale * (0.34 if compact_crop else 0.14)))
    found: list[tuple[float, float, float, float]] = []
    circles = cv2.HoughCircles(
        cv2.medianBlur(gray, 5), cv2.HOUGH_GRADIENT, dp=1.2,
        minDist=max(8, round(scale * (0.10 if compact_crop else 0.035))), param1=90, param2=18,
        minRadius=min_radius, maxRadius=max_radius,
    )
    if circles is not None:
        for cx, cy, radius in circles[0]:
            in_compact_swatch_area = cx <= width * 0.82 and cy <= height * 0.78
            in_full_palette_swatch_area = cx <= width * 0.30 and cy >= height * 0.58
            if (in_compact_swatch_area if compact_crop else in_full_palette_swatch_area):
                found.append((float(cx), float(cy), float(radius), 0.0))

    # Geometry fallback for the familiar CSP overlapping foreground and
    # background discs. Candidates still need a visible bright outline to win.
    if compact_crop:
        found.extend([
            (width * 0.42, height * 0.28, scale * 0.23, 0.0),
            (width * 0.59, height * 0.41, scale * 0.16, 0.0),
        ])
    else:
        found.extend([
            (width * 0.075, height * 0.780, height * 0.065, 0.0),
            (width * 0.125, height * 0.815, height * 0.043, 0.0),
        ])

    distinct: list[tuple[float, float, float, float]] = []
    for candidate in found:
        cx, cy, radius, _ = candidate
        if all((cx - x) ** 2 + (cy - y) ** 2 > max(radius, r) ** 2 * 0.40 for x, y, r, _ in distinct):
            distinct.append(candidate)
    mask = _accent_mask(image)
    return [(cx, cy, radius, _ring_coverage(mask, cx, cy, radius)) for cx, cy, radius, _ in distinct]


def _checkerboard_button(image: np.ndarray) -> tuple[tuple[int, int, int, int], float] | None:
    """Find the small checkerboard transparency button near the palette bottom."""
    height, width = image.shape[:2]
    compact_crop = max(width, height) < 300
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    box_w = max(20, round(width * (0.52 if compact_crop else 0.15)))
    box_h = max(10, round(height * (0.23 if compact_crop else 0.058)))
    x_end = max(0, round(width * (0.72 if compact_crop else 0.28)) - box_w)
    y_start = min(height - box_h, round(height * (0.52 if compact_crop else 0.74)))
    y_end = max(y_start, height - box_h)
    step_x = max(1, box_w // 12)
    step_y = max(1, box_h // 5)
    best: tuple[int, int, float] | None = None
    # A checkerboard has a strong alternating pattern after reducing each
    # small cell to its average; the surrounding UI is mostly flat gray.
    pattern = np.fromfunction(lambda y, x: (-1.0) ** (x + y), (4, 8), dtype=int)
    for y in range(y_start, y_end + 1, step_y):
        for x in range(0, x_end + 1, step_x):
            patch = gray[y:y + box_h, x:x + box_w]
            if patch.shape[0] < 8 or patch.shape[1] < 16:
                continue
            cells = cv2.resize(patch, (8, 4), interpolation=cv2.INTER_AREA).astype(np.float32)
            cells -= cells.mean()
            checker = abs(float((cells * pattern).mean()))
            variation = float(cells.std())
            score = checker * 0.75 + variation * 0.25
            if best is None or score > best[2]:
                best = (x, y, score)
    if best is None:
        return None
    x, y, score = best
    # If the image was tightly cropped or uses another layout, do not call a
    # generic textured patch a transparency button.
    if score < 10:
        return None
    rect = (x, y, min(width, x + box_w), min(height, y + box_h))
    x0, y0, x1, y1 = rect
    mask = _accent_mask(image)
    thickness = max(1, round(min(box_w, box_h) * 0.10))
    perimeter = np.zeros((y1 - y0, x1 - x0), dtype=bool)
    perimeter[:thickness] = True
    perimeter[-thickness:] = True
    perimeter[:, :thickness] = True
    perimeter[:, -thickness:] = True
    border_score = float(mask[y0:y1, x0:x1][perimeter].mean())
    return rect, border_score


def _wheel_geometry(image: np.ndarray) -> tuple[float, float, float] | None:
    hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
    saturated = ((hsv[:, :, 1] >= 95) & (hsv[:, :, 2] >= 45)).astype(np.uint8)
    count, labels, stats, centroids = cv2.connectedComponentsWithStats(saturated, 8)
    height, width = image.shape[:2]
    minimum_side = min(width, height) * 0.48
    candidates: list[tuple[int, int]] = []
    for index in range(1, count):
        x, y, w, h, area = (int(v) for v in stats[index])
        ratio = w / max(1, h)
        if w >= minimum_side and h >= minimum_side and 0.82 <= ratio <= 1.22 and area >= minimum_side * minimum_side * 0.10:
            candidates.append((area, index))
    if not candidates:
        return None
    _, index = max(candidates)
    x, y, w, h, _ = (int(v) for v in stats[index])
    cx, cy = (float(value) for value in centroids[index])
    radius = (w + h) / 4.0
    if radius < 20:
        return None
    return cx, cy, radius


def _wheel_handles(image: np.ndarray, geometry: tuple[float, float, float] | None) -> tuple[int | None, tuple[int, int] | None, tuple[int, int] | None]:
    if geometry is None:
        return None, None, None
    cx, cy, radius = geometry
    hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
    # The hue selector is a bright, low-saturation strip laid over the
    # saturated ring. Find its angular position, then sample adjacent ring
    # pixels for the hue rather than trusting a fixed wheel orientation.
    ring_radius = radius * 0.93
    radial = np.linspace(radius * 0.84, radius * 1.015, 12)
    angle_scores = np.zeros(360, dtype=np.float32)
    height, width = image.shape[:2]
    for angle in range(360):
        radians = np.deg2rad(angle)
        xs = np.rint(cx + radial * np.cos(radians)).astype(int)
        ys = np.rint(cy + radial * np.sin(radians)).astype(int)
        inside = (xs >= 0) & (xs < width) & (ys >= 0) & (ys < height)
        if not inside.any():
            continue
        samples = hsv[ys[inside], xs[inside]]
        angle_scores[angle] = float(((samples[:, 1] < 105) & (samples[:, 2] > 180)).sum())
    peak = int(np.argmax(angle_scores))
    hue: int | None = None
    if angle_scores[peak] >= 2:
        hue_samples: list[int] = []
        for angle in (peak - 8, peak - 6, peak - 4, peak + 4, peak + 6, peak + 8):
            radians = np.deg2rad(angle)
            for rr in (radius * 0.88, ring_radius, radius * 0.98):
                x = round(cx + rr * np.cos(radians))
                y = round(cy + rr * np.sin(radians))
                if 0 <= x < width and 0 <= y < height:
                    h, s, v = (int(value) for value in hsv[y, x])
                    if s >= 90 and v >= 55:
                        hue_samples.append(h)
        if hue_samples:
            radians = np.asarray(hue_samples, dtype=np.float64) * (2.0 * np.pi / 180.0)
            mean_angle = np.arctan2(np.sin(radians).mean(), np.cos(radians).mean())
            hue = int(round(np.rad2deg(mean_angle))) % 360

    # The SV handle is a small outlined circle inside the square. It is near
    # the wheel center and substantially smaller than the ring itself.
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    radius_min = max(3, round(radius * 0.025))
    radius_max = max(radius_min + 1, round(radius * 0.075))
    region = gray.copy()
    yy, xx = np.ogrid[:height, :width]
    region[(xx - cx) ** 2 + (yy - cy) ** 2 > (radius * 0.82) ** 2] = 0
    circles = cv2.HoughCircles(
        cv2.medianBlur(region, 5), cv2.HOUGH_GRADIENT, dp=1.1,
        minDist=max(8, round(radius * 0.12)), param1=80, param2=10,
        minRadius=radius_min, maxRadius=radius_max,
    )
    point: tuple[int, int] | None = None
    if circles is not None:
        possible = []
        for px, py, pr in circles[0]:
            distance = float(np.hypot(px - cx, py - cy))
            if distance < radius * 0.80:
                # The marker is the most compact high-contrast circle in the
                # picker square; the distance term avoids favoring center noise.
                possible.append((float(pr), distance, int(round(px)), int(round(py))))
        if possible:
            _, _, px, py = min(possible, key=lambda item: (abs(item[0] - radius * 0.045), -item[1]))
            point = (px, py)

    rgb: tuple[int, int, int] | None = None
    if point is not None:
        px, py = point
        # Sample the field just outside the circular handle on four sides;
        # its white/black outline should not become part of the color value.
        samples = []
        offset = max(4, round(radius * 0.055))
        for dx, dy in ((offset, 0), (-offset, 0), (0, offset), (0, -offset)):
            x, y = px + dx, py + dy
            # The SV square is centered on the wheel and about 1.1 radii wide.
            # Ignore points outside its edge so a marker in a corner does not
            # mix in the surrounding gray panel background.
            inside_square = abs(x - cx) <= radius * 0.56 and abs(y - cy) <= radius * 0.56
            if 0 <= x < width and 0 <= y < height and inside_square:
                samples.append(image[max(0, y - 1):min(height, y + 2), max(0, x - 1):min(width, x + 2)].reshape(-1, 3))
        if samples:
            rgb = _to_rgb(np.concatenate(samples, axis=0))
    return hue, point, rgb


def recognize_color(image: Any) -> ColorReading:
    """Recognize the selected CSP color from a color-wheel panel crop."""
    if image is None or not isinstance(image, np.ndarray) or image.size == 0:
        return ColorReading("unknown", None, 0.0, "没有可分析的图像")
    if image.ndim != 3 or image.shape[2] < 3:
        return ColorReading("unknown", None, 0.0, "图像格式必须是 BGR 彩色图")
    image = np.ascontiguousarray(image[:, :, :3])
    height, width = image.shape[:2]
    if min(height, width) < 40:
        return ColorReading("unknown", None, 0.0, "识别区域太小，请框选完整色盘")

    circles = _color_swatches(image)
    best_circle = max(circles, key=lambda item: item[3], default=None)
    checker = _checkerboard_button(image)
    checker_score = checker[1] if checker else 0.0
    circle_score = best_circle[3] if best_circle else 0.0
    geometry = _wheel_geometry(image)
    hue, point, point_rgb = _wheel_handles(image, geometry)

    # The checkerboard button reports alpha=0 only when its outline is the
    # strongest active accent. A checkerboard merely being visible is not enough.
    if checker and checker_score >= 0.16 and checker_score > circle_score + 0.06:
        return ColorReading("transparent", None, min(0.99, 0.60 + checker_score), "透明色棋盘格按钮的高亮边框最明显", hue, point)

    if best_circle and circle_score >= 0.12:
        cx, cy, radius, score = best_circle
        x0, x1 = max(0, round(cx - radius * 0.48)), min(width, round(cx + radius * 0.48))
        y0, y1 = max(0, round(cy - radius * 0.48)), min(height, round(cy + radius * 0.48))
        if x1 > x0 and y1 > y0:
            rgb = _to_rgb(image[y0:y1, x0:x1])
            return ColorReading("color", rgb, min(0.99, 0.60 + score), "已读取带高亮环的颜色色块", hue, point)

    if point_rgb is not None:
        detail = "由色轮亮条与方形取色点读取"
        return ColorReading("color", point_rgb, 0.68 if hue is not None else 0.54, detail, hue, point)

    if best_circle:
        cx, cy, radius, _ = best_circle
        x0, x1 = max(0, round(cx - radius * 0.45)), min(width, round(cx + radius * 0.45))
        y0, y1 = max(0, round(cy - radius * 0.45)), min(height, round(cy + radius * 0.45))
        if x1 > x0 and y1 > y0:
            return ColorReading("color", _to_rgb(image[y0:y1, x0:x1]), 0.38, "未找到明确高亮环，读取前景色色块")

    return ColorReading("unknown", None, 0.0, "没有找到高亮色块或方形取色点，请重新框选整个色盘")
