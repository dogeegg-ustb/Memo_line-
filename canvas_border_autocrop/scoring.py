"""Candidate scoring and hard rejection."""

from __future__ import annotations

from typing import Sequence

import numpy as np

from .config import DetectorConfig
from .geometry import rect_iou
from .types import (
    CompleteSide,
    EvidenceGrade,
    RectangleHypothesis,
    SemanticSide,
)


def score_hypothesis(
    hyp: RectangleHypothesis,
    complete_sides: Sequence[CompleteSide],
    cfg: DetectorConfig,
) -> float:
    sides_by = {s.semantic_side: s for s in complete_sides}
    c_out = c_trans = c_cov = c_end = 0.0
    n = 0
    for side in hyp.observed_sides:
        s = sides_by.get(side) or hyp.side_refs.get(side.value)
        if s is None:
            continue
        c_out += s.outside_background_score
        c_trans += s.transition_score
        c_cov += s.coverage
        c_end += float(np.mean(s.endpoint_scores))
        n += 1
    if n == 0:
        return 0.0
    c_out /= n
    c_trans /= n
    c_cov /= n
    c_end /= n

    # Closure / uniformity proxies
    r = hyp.rect
    aspect = r.width / max(r.height, 1)
    c_uniform = 1.0 - min(0.5, abs(np.log(max(aspect, 1e-3))) * 0.15)
    if hyp.grade == EvidenceGrade.A:
        c_closure = 1.0
    elif hyp.grade == EvidenceGrade.B:
        c_closure = 0.85
    elif hyp.grade == EvidenceGrade.C_L:
        c_closure = 0.75
    else:
        c_closure = 0.8

    p_var = float(np.mean([sides_by[s].coordinate_mad for s in hyp.observed_sides if s in sides_by] or [1.0]))
    p_var = float(np.clip(p_var / 4.0, 0.0, 1.0))

    s = (
        cfg.weight_outside * c_out
        + cfg.weight_transition * c_trans
        + cfg.weight_coverage * c_cov
        + cfg.weight_endpoint * c_end
        + cfg.weight_closure * c_closure
        + cfg.weight_uniformity * c_uniform
        - cfg.weight_variance_penalty * p_var
    )
    # Normalize roughly to [0,1]
    denom = (
        cfg.weight_outside
        + cfg.weight_transition
        + cfg.weight_coverage
        + cfg.weight_endpoint
        + cfg.weight_closure
        + cfg.weight_uniformity
    )
    score = float(np.clip(s / max(denom, 1e-6), 0.0, 1.0))
    hyp.score = score
    hyp.confidence = float(
        np.clip(
            0.35 * score
            + 0.25 * c_end
            + 0.20 * c_trans
            + 0.20 * c_out,
            0.0,
            1.0,
        )
    )
    return score


def select_best_hypothesis(
    hyps: list[RectangleHypothesis],
    complete_sides: Sequence[CompleteSide],
    cfg: DetectorConfig,
) -> tuple[RectangleHypothesis | None, str, float]:
    if not hyps:
        return None, "no_hypotheses", 0.0

    for h in hyps:
        score_hypothesis(h, complete_sides, cfg)

    ranked = sorted(hyps, key=lambda h: h.score, reverse=True)
    best = ranked[0]
    if best.score < cfg.min_accept_score:
        return None, "score_below_threshold", 0.0

    margin = 1.0
    if len(ranked) >= 2:
        second = ranked[1]
        margin = best.score - second.score
        iou = rect_iou(best.rect, second.rect)
        if margin < cfg.ambiguity_score_margin and iou < cfg.ambiguity_iou_max:
            return None, "AmbiguousCandidates", margin

    return best, "", margin
