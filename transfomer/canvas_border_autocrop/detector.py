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
from .grower import grow_background, model_geometry_plausible
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

        # Coarse features only inside search ROI — major speedup on large captures
        feats_c = extract_features(bgr_c, self.cfg, scale_to_capture=scale, roi=search_c)
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
        models = estimate_background_models(seeds, self.cfg, user_roi=user_c)
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
        t_m = time.perf_counter()
        sim = build_similarity(feats_c, model, search_c, cfg)
        grown = grow_background(
            seeds, set(model.accepted_seed_ids), sim, search_c, cfg
        )
        diag.timings["grow"] = (time.perf_counter() - t_m) * 1000.0
        if grown is None:
            return DetectionOutput(
                DetectionStatus.NO_CONNECTED_BACKGROUND,
                diagnostics=diag,
                message="grow failed",
            )
        # Reject UI chrome / solid canvas mistaken as workspace background
        if not model_geometry_plausible(grown, cfg):
            return DetectionOutput(
                DetectionStatus.NO_CONNECTED_BACKGROUND,
                diagnostics=diag,
                message=(
                    f"implausible bg geometry fill={grown.bbox_fill_ratio:.2f} "
                    f"hole={grown.hole_score:.2f} border={grown.touches_search_border:.2f}"
                ),
            )

        t_m = time.perf_counter()
        boundary = extract_boundary_points(grown, sim, feats_c, search_c, cfg)
        sides = detect_complete_sides(
            boundary, search_c, feats_c.width, feats_c.height, cfg, dpi
        )
        diag.timings["geometry"] = (time.perf_counter() - t_m) * 1000.0
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

        t_m = time.perf_counter()
        hyps = build_hypotheses(complete, search_c, user_c, cfg, weak_support)
        diag.timings["hypotheses"] = (time.perf_counter() - t_m) * 1000.0
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

        t_m = time.perf_counter()
        # Full-res features for refine + validate (reuse when no downsample)
        if scale >= 0.999:
            feats_full = feats_c
        else:
            # Only compute around coarse rect — major speedup on large captures
            band = coarse_full.expand(cfg.refine_radius_max_px + 8, bgr_full.shape[1], bgr_full.shape[0])
            feats_full = extract_features(bgr_full, cfg, scale_to_capture=1.0, roi=band)

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
        diag.timings["refine"] = (time.perf_counter() - t_m) * 1000.0

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

        t_m = time.perf_counter()
        rng = np.random.default_rng(cfg.ransac_seed)
        ok, metrics, conf = validate_rectangle(
            refined, best, feats_full, model, grown_full, cfg, rng
        )
        diag.timings["validate"] = (time.perf_counter() - t_m) * 1000.0
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
            observed_outer_sides=list(best.observed_sides),
            closed_outer_sides=list(best.inferred_sides),
            background_appearance_model=model,
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
    """Weak outer-edge support: inside≈bg, outside≠bg."""
    h, w = similarity.shape
    a0, a1 = int(round(min(start, end))), int(round(max(start, end)))
    if a1 <= a0 + 2:
        return 0.0

    span = a1 - a0
    corner = max(3, span // 8)
    samples: list[int] = []
    for a in range(a0, a0 + corner, max(1, corner // 6)):
        samples.append(a)
    for a in range(a1 - corner, a1, max(1, corner // 6)):
        samples.append(a)
    mid_step = max(1, span // 24)
    for a in range(a0 + corner, a1 - corner, mid_step):
        samples.append(a)
    samples = sorted(set(int(s) for s in samples))

    best = 0.0
    for delta in (0, -1, 1, -2, 2):
        fx = int(round(fixed)) + delta
        hits = 0
        total = 0

        if semantic == SemanticSide.LEFT:
            if not (1 <= fx < w - 1):
                continue
            for y in samples:
                if not (0 <= y < h):
                    continue
                weight = 2 if (y < a0 + corner or y >= a1 - corner) else 1
                total += weight
                out_x, in_x = fx - 1, min(w - 1, fx)
                ok = (similarity[y, in_x] > 0.35 and similarity[y, out_x] < 0.65) or (
                    mask[y, in_x] and not mask[y, out_x]
                )
                hits += weight if ok else 0
        elif semantic == SemanticSide.RIGHT:
            if not (1 <= fx < w - 1):
                continue
            for y in samples:
                if not (0 <= y < h):
                    continue
                weight = 2 if (y < a0 + corner or y >= a1 - corner) else 1
                total += weight
                in_x, out_x = max(0, fx - 1), min(w - 1, fx)
                ok = (similarity[y, in_x] > 0.35 and similarity[y, out_x] < 0.65) or (
                    mask[y, in_x] and not mask[y, out_x]
                )
                hits += weight if ok else 0
        elif semantic == SemanticSide.TOP:
            if not (1 <= fx < h - 1):
                continue
            for x in samples:
                if not (0 <= x < w):
                    continue
                weight = 2 if (x < a0 + corner or x >= a1 - corner) else 1
                total += weight
                out_y, in_y = fx - 1, min(h - 1, fx)
                ok = (similarity[in_y, x] > 0.35 and similarity[out_y, x] < 0.65) or (
                    mask[in_y, x] and not mask[out_y, x]
                )
                hits += weight if ok else 0
        else:
            if not (1 <= fx < h - 1):
                continue
            for x in samples:
                if not (0 <= x < w):
                    continue
                weight = 2 if (x < a0 + corner or x >= a1 - corner) else 1
                total += weight
                in_y, out_y = max(0, fx - 1), min(h - 1, fx)
                ok = (similarity[in_y, x] > 0.35 and similarity[out_y, x] < 0.65) or (
                    mask[in_y, x] and not mask[out_y, x]
                )
                hits += weight if ok else 0

        if total > 0:
            best = max(best, hits / total)
    return best


def detect_workspace_rect(inp: DetectionInput, config: DetectorConfig | None = None) -> DetectionOutput:
    return CanvasBorderDetector(config).detect(inp)


def detect_canvas_rect(inp: DetectionInput, config: DetectorConfig | None = None) -> DetectionOutput:
    """Alias for detect_workspace_rect (legacy name)."""
    return detect_workspace_rect(inp, config)
