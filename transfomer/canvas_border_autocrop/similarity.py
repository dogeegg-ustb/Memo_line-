"""Background similarity and barrier masks."""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from .config import DetectorConfig
from .features import CanvasFeatureMaps, delta_e76
from .types import BackgroundAppearanceModel, IntRect


@dataclass(slots=True)
class BackgroundSimilarity:
    similarity: np.ndarray  # float32 HxW [0,1]
    strong_mask: np.ndarray  # bool
    weak_mask: np.ndarray
    barrier_mask: np.ndarray


def build_similarity(
    features: CanvasFeatureMaps,
    model: BackgroundAppearanceModel,
    search_roi: IntRect,
    cfg: DetectorConfig,
) -> BackgroundSimilarity:
    h, w = features.height, features.width
    # Compute only inside search ROI for speed
    y0, y1 = search_roi.top, search_roi.bottom
    x0, x1 = search_roi.left, search_roi.right

    lab_roi = features.lab[y0:y1, x0:x1]
    de = delta_e76(lab_roi, model.center_lab)
    # Continuous similarity from strong/weak thresholds
    sim_roi = np.clip(1.0 - de / max(model.weak_delta_e, 1e-3), 0.0, 1.0).astype(np.float32)

    g = features.gradient_magnitude[y0:y1, x0:x1]
    v = features.local_variance[y0:y1, x0:x1]
    g_thr = float(np.percentile(g, cfg.barrier_grad_percentile))
    v_thr = float(np.percentile(v, cfg.barrier_var_percentile))
    # Down-weight high gradient / variance
    weight = np.ones_like(sim_roi, dtype=np.float32)
    weight *= np.clip(1.0 - (g / max(g_thr * 1.5, 1e-3)), 0.15, 1.0)
    weight *= np.clip(1.0 - (v / max(v_thr * 1.5, 1e-3)), 0.15, 1.0)
    sim_roi = sim_roi * weight

    barrier_roi = (g >= g_thr) & (v >= v_thr * 0.5)

    similarity = np.zeros((h, w), dtype=np.float32)
    strong = np.zeros((h, w), dtype=bool)
    weak = np.zeros((h, w), dtype=bool)
    barrier = np.zeros((h, w), dtype=bool)

    similarity[y0:y1, x0:x1] = sim_roi
    strong[y0:y1, x0:x1] = sim_roi >= cfg.strong_sim_threshold
    weak[y0:y1, x0:x1] = sim_roi >= cfg.weak_sim_threshold
    barrier[y0:y1, x0:x1] = barrier_roi

    return BackgroundSimilarity(similarity, strong, weak, barrier)
