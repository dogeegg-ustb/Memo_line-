"""End-to-end screen ↔ canvas transform pipeline (§5). User-triggered only."""

from __future__ import annotations

import time

from .detector import detect_workspace_rect
from .navigator_canvas import detect_navigator_canvas
from .red_frame import detect_red_frame
from .red_frame_hypothesis import build_viewport_hypotheses
from .transform_solver import solve_axis_aligned_transforms
from .transform_types import (
    TransformDiagnostics,
    TransformRequest,
    TransformResult,
    TransformStatus,
    ValidationStatus,
)
from .transform_validate import build_overlay_scene, predict_workspace_canvas_edges, validate_transform
from .types import DetectionInput, DetectionStatus, PixelFormat


def run_transform(req: TransformRequest) -> TransformResult:
    diag = TransformDiagnostics(
        capture_id=req.capture_id,
        workspace_user_roi=req.workspace_user_roi_capture_px,
        navigator_user_roi=req.navigator_user_roi_capture_px,
    )
    t0 = time.perf_counter()

    def cancelled() -> bool:
        return bool(req.cancellation_token and req.cancellation_token())

    if not req.user_triggered:
        diag.rejection_reasons.append("NotUserTriggered")
        return TransformResult(
            status=TransformStatus.NOT_USER_TRIGGERED,
            diagnostics=diag,
            source_capture_id=req.capture_id,
            message="user must click Start Compute",
        )

    if cancelled():
        return TransformResult(TransformStatus.CANCELLED, diagnostics=diag, source_capture_id=req.capture_id)

    bgra = req.frozen_capture_buffer
    if bgra is None:
        return TransformResult(
            TransformStatus.SESSION_MISMATCH,
            diagnostics=diag,
            message="missing frozen buffer",
            source_capture_id=req.capture_id,
        )

    # --- Workspace correction (reuse if provided) ---
    t = time.perf_counter()
    ws_out = req.workspace_detection_output
    if ws_out is None or getattr(ws_out, "status", None) != DetectionStatus.OK:
        h, w = bgra.shape[:2]
        ws_out = detect_workspace_rect(
            DetectionInput(
                capture_buffer=bgra,
                capture_width=w,
                capture_height=h,
                stride=w * 4,
                user_roi_capture_px=req.workspace_user_roi_capture_px,
                pixel_format=PixelFormat.BGRA,
                capture_id=req.capture_id,
                capture_origin_screen_physical_x=req.capture_origin_screen_physical_x,
                capture_origin_screen_physical_y=req.capture_origin_screen_physical_y,
                cancellation_token=req.cancellation_token,
            )
        )
    diag.timings["workspace"] = (time.perf_counter() - t) * 1000.0

    if ws_out.status != DetectionStatus.OK or ws_out.workspace_rect_screen_physical_px is None:
        diag.rejection_reasons.append(f"Workspace:{ws_out.status.value}")
        return TransformResult(
            status=TransformStatus.WORKSPACE_DETECTION_FAILED,
            diagnostics=diag,
            source_capture_id=req.capture_id,
            message=ws_out.message or ws_out.status.value,
        )

    bg_model = ws_out.background_appearance_model
    if bg_model is None:
        diag.rejection_reasons.append("BackgroundModelUnavailable")
        return TransformResult(
            status=TransformStatus.WORKSPACE_DETECTION_FAILED,
            diagnostics=diag,
            message="background model unavailable",
            source_capture_id=req.capture_id,
        )

    ws_cap = ws_out.workspace_rect_capture_px
    ws_scr = ws_out.workspace_rect_screen_physical_px
    assert ws_cap is not None and ws_scr is not None
    diag.corrected_workspace_rect = ws_cap
    diag.workspace_background_model = bg_model

    if cancelled():
        return TransformResult(TransformStatus.CANCELLED, diagnostics=diag, source_capture_id=req.capture_id)

    # --- Navigator canvas ---
    t = time.perf_counter()
    st, nav_obs, msg = detect_navigator_canvas(bgra, req.navigator_user_roi_capture_px, bg_model)
    diag.timings["navigator_canvas"] = (time.perf_counter() - t) * 1000.0
    if st != TransformStatus.OK or nav_obs is None:
        diag.rejection_reasons.append(msg)
        if nav_obs is not None:
            diag.navigator_canvas_candidates = nav_obs.candidates
        return TransformResult(
            status=st,
            diagnostics=diag,
            message=msg,
            source_capture_id=req.capture_id,
        )
    diag.navigator_canvas_candidates = nav_obs.candidates
    diag.selected_navigator_canvas_rect = nav_obs.canvas_rect_capture_px

    # --- Red frame ---
    t = time.perf_counter()
    st, red_obs, msg = detect_red_frame(
        bgra, req.navigator_user_roi_capture_px, nav_obs.canvas_rect_capture_px
    )
    diag.timings["red_frame"] = (time.perf_counter() - t) * 1000.0
    if red_obs is not None:
        diag.directed_red_corners = [
            {
                "semantic": c.semantic.value,
                "xy": c.position_capture_px,
                "conf": c.confidence,
                "h": c.horizontal_ray.value,
                "v": c.vertical_ray.value,
            }
            for c in red_obs.directed_corners
        ]
        diag.rejected_corners_and_reasons = red_obs.rejected_red_components
    if st != TransformStatus.OK or red_obs is None:
        diag.rejection_reasons.append(msg)
        return TransformResult(
            status=st,
            diagnostics=diag,
            message=msg,
            source_capture_id=req.capture_id,
            navigator_canvas_rect_capture_px=nav_obs.canvas_rect_capture_px,
        )

    # --- Viewport hypothesis ---
    t = time.perf_counter()
    st, hyp, all_hyps, msg = build_viewport_hypotheses(
        red_obs, ws_cap, nav_obs.canvas_rect_capture_px
    )
    diag.timings["hypothesis"] = (time.perf_counter() - t) * 1000.0
    diag.red_frame_hypotheses = [
        {
            "grade": h.grade.value,
            "rect": h.rect.as_tuple(),
            "score": h.score,
            "semantics": h.used_semantics,
        }
        for h in all_hyps
    ]
    if st != TransformStatus.OK or hyp is None:
        diag.rejection_reasons.append(msg)
        return TransformResult(
            status=st,
            diagnostics=diag,
            message=msg,
            source_capture_id=req.capture_id,
            navigator_canvas_rect_capture_px=nav_obs.canvas_rect_capture_px,
        )
    diag.selected_viewport_rect = hyp.rect

    # --- Solve matrices ---
    t = time.perf_counter()
    st, solved, msg = solve_axis_aligned_transforms(
        ws_scr,
        nav_obs.canvas_rect_capture_px,
        hyp.rect,
        capture_origin_x=req.capture_origin_screen_physical_x,
        capture_origin_y=req.capture_origin_screen_physical_y,
    )
    diag.timings["solve"] = (time.perf_counter() - t) * 1000.0
    if st != TransformStatus.OK or solved is None:
        diag.rejection_reasons.append(msg)
        return TransformResult(
            status=st,
            diagnostics=diag,
            message=msg,
            source_capture_id=req.capture_id,
            workspace_rect_screen_physical_px=ws_scr,
            navigator_canvas_rect_capture_px=nav_obs.canvas_rect_capture_px,
            navigator_viewport_rect_capture_px=hyp.rect,
            red_frame_evidence_grade=hyp.grade,
        )

    diag.scale_constraints = {"kx": solved.scale_kx, "ky": solved.scale_ky}
    diag.matrices = {
        "ScreenPhysicalToCanvasNormalized": solved.screen_to_canvas.matrix.as_rows(),
        "CanvasNormalizedToScreenPhysical": solved.canvas_to_screen.matrix.as_rows(),
    }

    # --- Validate ---
    t = time.perf_counter()
    solver_pts = [c.position_capture_px for c in red_obs.directed_corners if c.semantic.value in hyp.used_semantics]
    validation = validate_transform(
        solved,
        ws_scr,
        nav_obs.canvas_rect_capture_px,
        hyp.rect,
        red_obs,
        capture_id=req.capture_id,
        solver_corner_positions=solver_pts,
    )
    diag.timings["validate"] = (time.perf_counter() - t) * 1000.0
    diag.matrix_structure_metrics = {
        "det": validation.determinant,
        "cond": validation.condition_number,
        "scale_rel": validation.scale_relative_error,
        "nav_p95": validation.navigator_reprojection_p95_px,
    }
    diag.reprojection_errors = {
        "nav_median": validation.navigator_reprojection_median_px,
        "nav_p95": validation.navigator_reprojection_p95_px,
    }

    edges = predict_workspace_canvas_edges(solved, nav_obs.canvas_rect_capture_px, hyp.rect)
    scene = build_overlay_scene(
        req.capture_id,
        validation,
        ws_scr,
        predicted_canvas_edges_screen=edges,
    )
    diag.overlay_scene = scene
    diag.timings["total"] = (time.perf_counter() - t0) * 1000.0

    if validation.status == ValidationStatus.REJECTED:
        diag.rejection_reasons.extend(validation.failure_reasons)
        return TransformResult(
            status=TransformStatus.INDEPENDENT_VALIDATION_FAILED,
            workspace_rect_screen_physical_px=ws_scr,
            navigator_canvas_rect_capture_px=nav_obs.canvas_rect_capture_px,
            navigator_viewport_rect_capture_px=hyp.rect,
            red_frame_evidence_grade=hyp.grade,
            screen_physical_to_canvas_normalized=solved.screen_to_canvas,
            canvas_normalized_to_screen_physical=solved.canvas_to_screen,
            workspace_local_to_canvas_normalized=solved.workspace_to_canvas,
            canvas_normalized_to_workspace_local=solved.canvas_to_workspace,
            validation=validation,
            diagnostics=diag,
            source_capture_id=req.capture_id,
            message="; ".join(validation.failure_reasons) or "validation rejected",
            overlay_scene=scene,
        )

    return TransformResult(
        status=TransformStatus.OK,
        workspace_rect_screen_physical_px=ws_scr,
        navigator_canvas_rect_capture_px=nav_obs.canvas_rect_capture_px,
        navigator_viewport_rect_capture_px=hyp.rect,
        red_frame_evidence_grade=hyp.grade,
        screen_physical_to_canvas_normalized=solved.screen_to_canvas,
        canvas_normalized_to_screen_physical=solved.canvas_to_screen,
        workspace_local_to_canvas_normalized=solved.workspace_to_canvas,
        canvas_normalized_to_workspace_local=solved.canvas_to_workspace,
        validation=validation,
        diagnostics=diag,
        source_capture_id=req.capture_id,
        message=validation.status.value,
        overlay_scene=scene,
    )
