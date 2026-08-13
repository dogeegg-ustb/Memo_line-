"""Main canvas border auto-crop detector pipeline."""

from __future__ import annotations

import time
from typing import Optional

import cv2
import numpy as np

from .background import estimate_background_models
from .boundary import extract_boundary_points
from .config import DEFAULT_CONFIG, DetectorConfig
from .features import CanvasFeatureMaps, downsample_bgr, extract_features
from .geometry import (
    bgra_to_bgr,
    buffer_to_bgra,
    is_cancelled,
    normalize_user_roi,
    scale_rect,
    unscale_rect,
)
from .grower import grow_background
from .hypotheses import build_hypotheses
from .refine import refine_rectangle
from .scoring import select_best_hypothesis
from .seeds import sample_background_seeds
from .sides import complete_sides_only, detect_complete_sides
from .similarity import build_similarity
from .types import (
    DetectionInput,
    DetectionOutput,
    DetectionStatus,
    Diagnostics,
    IntRect,
    SemanticSide,
)
from .validate import validate_rectangle


class CanvasBorderDetector:
    def __init__(self, config: DetectorConfig | None = None):
        self.cfg = config or DEFAULT_CONFIG

    def detect(self, inp: DetectionInput) -> DetectionOutput:
        diag = Diagnostics()
        t0 = time.perf_counter()

        def timed(name: str, t_start: float) -> float:
            now = time.perf_counter()
            diag.timings[name] = (now - t_start) * 1000.0
            return now

        if is_cancelled(inp):
            return DetectionOutput(DetectionStatus.CANCELLED, diagnostics=diag, message="cancelled")

        try:
            bgra = buffer_to_bgra(
                inp.capture_buffer,
                inp.capture_width,
                inp.capture_height,
                inp.stride,
                inp.pixel_format,
            )
        except Exception as e:
            return DetectionOutput(
                DetectionStatus.INVALID_INPUT, diagnostics=diag, message=f"buffer: {e}"
            )

        full_h, full_w = bgra.shape[:2]
        if full_w != inp.capture_width or full_h != inp.capture_height:
            return DetectionOutput(
                DetectionStatus.INVALID_INPUT, diagnostics=diag, message="dimension mismatch"
            )

        user_roi, err = normalize_user_roi(
            inp.user_roi_capture_px, full_w, full_h, self.cfg.min_roi_side_px
        )
        if err is not None or user_roi is None:
            return DetectionOutput(err or DetectionStatus.INVALID_INPUT, diagnostics=diag)

        dpi = 0.5 * (inp.dpi_scale_x + inp.dpi_scale_y)
        expand = self.cfg.search_expand_px(min(user_roi.width, user_roi.height), dpi)
        search_roi = user_roi.expand(expand, full_w, full_h)
        diag.search_roi = search_roi

        bgr_full = bgra_to_bgr(bgra)
        scale = self.cfg.coarse_scale(full_w, full_h)
        diag.scale_used = scale

        t = timed("input", t0)
        if is_cancelled(inp):
            return DetectionOutput(DetectionStatus.CANCELLED, diagnostics=diag)

        # --- Coarse path ---
        if scale < 0.999:
            bgr_c = downsample_bgr(bgr_full, scale)
            user_c = scale_rect(user_roi, scale, bgr_c.shape[1], bgr_c.shape[0])
            search_c = scale_rect(search_roi, scale, bgr_c.shape[1], bgr_c.shape[0])
        else:
            bgr_c = bgr_full
            user_c = user_roi
            search_c = search_roi

        feats_c = extract_features(bgr_c, self.cfg, scale_to_capture=scale)
        t = timed("features", t)

        seeds = sample_background_seeds(feats_c, user_c, self.cfg, dpi)
        diag.accepted_and_rejected_seeds = [
            {
                "id": s.seed_id,
                "side": s.side.value,
                "xy": (s.x, s.y),
                "accepted": s.accepted,
                "reason": s.reject_reason,
                "lab": s.mean_lab,
            }
            for s in seeds
        ]
        models = estimate_background_models(seeds, self.cfg)
        diag.background_models_and_scores = [
            {
                "cluster": m.cluster_id,
                "center": m.center_lab,
                "strong": m.strong_delta_e,
                "weak": m.weak_delta_e,
                "coverage": m.spatial_coverage,
                "confidence": m.confidence,
                "seeds": m.accepted_seed_ids,
            }
            for m in models
        ]
        t = timed("background", t)
        if not models:
            diag.rejection_reasons.append("NoStableBackgroundModel")
            return DetectionOutput(
                DetectionStatus.NO_STABLE_BACKGROUND_MODEL, diagnostics=diag, message="no bg model"
            )

        # Try models by priority until a validated rect emerges
        last_status = DetectionStatus.NO_CONNECTED_BACKGROUND
        best_failed_reasons: list[str] = []

        for model in models[:3]:
            if is_cancelled(inp):
                return DetectionOutput(DetectionStatus.CANCELLED, diagnostics=diag)

            result = self._detect_with_model(
                inp=inp,
                model=model,
                seeds=seeds,
                feats_c=feats_c,
                user_c=user_c,
                search_c=search_c,
                bgr_full=bgr_full,
                user_roi=user_roi,
                search_roi=search_roi,
                scale=scale,
                dpi=dpi,
                diag=diag,
                t=t,
            )
            if result.status == DetectionStatus.OK:
                timed("total", t0)
                return result
            last_status = result.status
            if result.message:
                best_failed_reasons.append(result.message)

        diag.rejection_reasons.extend(best_failed_reasons)
        timed("total", t0)
        return DetectionOutput(last_status, diagnostics=diag, message="; ".join(best_failed_reasons))

    def _detect_with_model(
        self,
        inp: DetectionInput,
        model,
        seeds,
        feats_c: CanvasFeatureMaps,
        user_c: IntRect,
        search_c: IntRect,
        bgr_full: np.ndarray,
        user_roi: IntRect,
        search_roi: IntRect,
        scale: float,
        dpi: float,
        diag: Diagnostics,
        t: float,
    ) -> DetectionOutput:
        cfg = self.cfg
        sim = build_similarity(feats_c, model, search_c, cfg)
        grown = grow_background(
            seeds, set(model.accepted_seed_ids), sim, search_c, cfg
        )
        if grown is None:
            return DetectionOutput(
                DetectionStatus.NO_CONNECTED_BACKGROUND,
                diagnostics=diag,
                message="grow failed",
            )

        boundary = extract_boundary_points(grown, sim, feats_c, search_c, cfg)
        sides = detect_complete_sides(
            boundary, search_c, feats_c.width, feats_c.height, cfg, dpi
        )
        diag.side_candidates = [
            {
                "side": s.semantic_side.value,
                "fixed": s.fixed_coordinate,
                "span": (s.start_coordinate, s.end_coordinate),
                "coverage": s.coverage,
                "truncated": s.is_truncated,
                "endpoints": s.endpoint_scores,
            }
            for s in sides
        ]
        complete = complete_sides_only(sides)
        if len(complete) < 2:
            # Check if truncation was the issue
            if any(s.is_truncated for s in sides) and len(sides) >= 2:
                return DetectionOutput(
                    DetectionStatus.ENDPOINT_TRUNCATED,
                    diagnostics=diag,
                    message="endpoints truncated",
                )
            return DetectionOutput(
                DetectionStatus.INSUFFICIENT_COMPLETE_SIDES,
                diagnostics=diag,
                message=f"complete_sides={len(complete)}",
            )

        def weak_support(semantic: SemanticSide, fixed: float, start: float, end: float) -> float:
            return _weak_edge_support(sim.similarity, grown.mask, semantic, fixed, start, end, search_c)

        hyps = build_hypotheses(complete, search_c, user_c, cfg, weak_support)
        diag.rectangle_hypotheses = [
            {
                "grade": h.grade.value,
                "rect": h.rect.as_tuple(),
                "observed": [s.value for s in h.observed_sides],
                "inferred": [s.value for s in h.inferred_sides],
            }
            for h in hyps
        ]
        if not hyps:
            return DetectionOutput(
                DetectionStatus.RECTANGLE_CLOSURE_FAILED,
                diagnostics=diag,
                message="no hypotheses",
            )

        best, reason, margin = select_best_hypothesis(hyps, complete, cfg)
        diag.ambiguity_margin = margin
        if best is None:
            status = (
                DetectionStatus.AMBIGUOUS_CANDIDATES
                if reason == "AmbiguousCandidates"
                else DetectionStatus.RECTANGLE_CLOSURE_FAILED
            )
            diag.rejection_reasons.append(reason)
            return DetectionOutput(status, diagnostics=diag, message=reason)

        # Map coarse rect to full resolution (floor/ceil, then refine — no final round-only)
        if scale < 0.999:
            coarse_full = IntRect(
                int(np.floor(best.rect.left / scale)),
                int(np.floor(best.rect.top / scale)),
                int(np.ceil(best.rect.right / scale)),
                int(np.ceil(best.rect.bottom / scale)),
            ).clamp(bgr_full.shape[1], bgr_full.shape[0])
        else:
            coarse_full = best.rect
        diag.coarse_rect = coarse_full

        # Full-res features for refine + validate
        feats_full = extract_features(bgr_full, cfg, scale_to_capture=1.0)
        # Remap model thresholds stay; Lab center is resolution-invariant

        refined = refine_rectangle(coarse_full, feats_full, model, cfg, dpi)
        if refined is None:
            return DetectionOutput(
                DetectionStatus.REFINEMENT_FAILED,
                diagnostics=diag,
                message="refine failed",
            )
        # Hard: refinement must not jump too far
        if (
            abs(refined.left - coarse_full.left) > cfg.refine_max_shift_px
            or abs(refined.right - coarse_full.right) > cfg.refine_max_shift_px
            or abs(refined.top - coarse_full.top) > cfg.refine_max_shift_px
            or abs(refined.bottom - coarse_full.bottom) > cfg.refine_max_shift_px
        ):
            return DetectionOutput(
                DetectionStatus.REFINEMENT_FAILED,
                diagnostics=diag,
                message="refine shift exceeded",
            )
        diag.refined_rect = refined

        # Upsample grown mask roughly for interior check
        grown_full = None
        if scale < 0.999:
            grown_full = cv2.resize(
                grown.mask.astype(np.uint8),
                (bgr_full.shape[1], bgr_full.shape[0]),
                interpolation=cv2.INTER_NEAREST,
            ).astype(bool)
        else:
            grown_full = grown.mask

        rng = np.random.default_rng(cfg.ransac_seed)
        ok, metrics, conf = validate_rectangle(
            refined, best, feats_full, model, grown_full, cfg, rng
        )
        diag.per_side_validation_metrics = metrics
        if not ok:
            return DetectionOutput(
                DetectionStatus.INDEPENDENT_VALIDATION_FAILED,
                diagnostics=diag,
                message="validation failed",
            )

        # Screen physical px: CapturePx + virtual-desktop origin (MUST NOT re-divide by DPI)
        screen = IntRect(
            refined.left + int(inp.capture_origin_screen_physical_x),
            refined.top + int(inp.capture_origin_screen_physical_y),
            refined.right + int(inp.capture_origin_screen_physical_x),
            refined.bottom + int(inp.capture_origin_screen_physical_y),
        )

        return DetectionOutput(
            status=DetectionStatus.OK,
            workspace_rect_capture_px=refined,
            workspace_rect_screen_physical_px=screen,
            evidence_grade=best.grade,
            confidence=conf,
            observed_sides=list(best.observed_sides),
            inferred_sides=list(best.inferred_sides),
            source_capture_id=inp.capture_id,
            diagnostics=diag,
            message="ok",
        )


def _weak_edge_support(
    similarity: np.ndarray,
    mask: np.ndarray,
    semantic: SemanticSide,
    fixed: float,
    start: float,
    end: float,
    search_roi: IntRect,
) -> float:
    """Weak transition support along a predicted edge."""
    h, w = similarity.shape
    fx = int(round(fixed))
    a0, a1 = int(round(min(start, end))), int(round(max(start, end)))
    if a1 <= a0:
        return 0.0
    hits = 0
    total = 0
    step = max(1, (a1 - a0) // 64)
    if semantic == SemanticSide.LEFT:
        if not (1 <= fx < w - 1):
            return 0.0
        for y in range(a0, a1, step):
            if y < 0 or y >= h:
                continue
            total += 1
            if similarity[y, fx - 1] > 0.4 and similarity[y, min(w - 1, fx + 1)] < 0.55:
                hits += 1
            elif mask[y, fx - 1] and not mask[y, min(w - 1, fx + 1)]:
                hits += 1
    elif semantic == SemanticSide.RIGHT:
        if not (1 <= fx < w - 1):
            return 0.0
        for y in range(a0, a1, step):
            if y < 0 or y >= h:
                continue
            total += 1
            if similarity[y, min(w - 1, fx)] > 0.4 and similarity[y, max(0, fx - 1)] < 0.55:
                hits += 1
            elif mask[y, min(w - 1, fx)] and not mask[y, max(0, fx - 1)]:
                hits += 1
    elif semantic == SemanticSide.TOP:
        if not (1 <= fx < h - 1):
            return 0.0
        for x in range(a0, a1, step):
            if x < 0 or x >= w:
                continue
            total += 1
            if similarity[fx - 1, x] > 0.4 and similarity[min(h - 1, fx + 1), x] < 0.55:
                hits += 1
            elif mask[fx - 1, x] and not mask[min(h - 1, fx + 1), x]:
                hits += 1
    else:
        if not (1 <= fx < h - 1):
            return 0.0
        for x in range(a0, a1, step):
            if x < 0 or x >= w:
                continue
            total += 1
            if similarity[min(h - 1, fx), x] > 0.4 and similarity[max(0, fx - 1), x] < 0.55:
                hits += 1
            elif mask[min(h - 1, fx), x] and not mask[max(0, fx - 1), x]:
                hits += 1
    if total == 0:
        return 0.0
    return hits / total


def detect_workspace_rect(inp: DetectionInput, config: DetectorConfig | None = None) -> DetectionOutput:
    return CanvasBorderDetector(config).detect(inp)


def detect_canvas_rect(inp: DetectionInput, config: DetectorConfig | None = None) -> DetectionOutput:
    """Alias for detect_workspace_rect (legacy name)."""
    return detect_workspace_rect(inp, config)
