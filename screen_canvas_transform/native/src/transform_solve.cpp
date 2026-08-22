#include "sct/transform_solve.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>

namespace sct {
namespace {

void CopyStr(char* dst, size_t n, const char* s) {
  if (!dst || n == 0) return;
  if (!s) {
    dst[0] = 0;
    return;
  }
  std::snprintf(dst, n, "%s", s);
}

Affine2D MakeScreenToWorkspace(const wb::IntRect& workspace_screen) {
  // x_w = x_s - Wl; y_w = y_s - Wt
  return Affine2D::Translation(-workspace_screen.left, -workspace_screen.top);
}

Affine2D MakeWorkspaceToScreen(const wb::IntRect& workspace_screen) {
  return Affine2D::Translation(workspace_screen.left, workspace_screen.top);
}

// Display operator D = R(theta) about canvas center in attached coords.
// CSP order fixed here: rotate about origin of attached space after normalize.
Affine2D MakeDisplayOperator(double rotation_degrees) {
  const double rad = rotation_degrees * 3.14159265358979323846 / 180.0;
  const double c = std::cos(rad);
  const double s = std::sin(rad);
  // Rotation about canvas center (0.5, 0.5).
  Affine2D to_origin = Affine2D::Translation(-0.5, -0.5);
  Affine2D rot;
  rot.m = {c, -s, 0, s, c, 0};
  Affine2D from_origin = Affine2D::Translation(0.5, 0.5);
  return Multiply(from_origin, Multiply(rot, to_origin));
}

SolveResult FailSolve(Stage stage, FailStatus st, const char* msg, const SolveInput& in) {
  SolveResult r;
  r.status = st;
  r.failure.stage = stage;
  r.failure.status = st;
  CopyStr(r.failure.message, sizeof(r.failure.message), msg);
  CopyStr(r.failure.capture_id, sizeof(r.failure.capture_id), in.capture_id);
  r.failure.generation = in.generation;
  CopyStr(r.failure.source_revision, sizeof(r.failure.source_revision), "sct-embedded-wb");
  CopyStr(r.failure.evidence_summary, sizeof(r.failure.evidence_summary), msg);
  return r;
}

}  // namespace

Affine2D Multiply(const Affine2D& a, const Affine2D& b) {
  // a * b
  Affine2D r;
  r.m[0] = a.m[0] * b.m[0] + a.m[1] * b.m[3];
  r.m[1] = a.m[0] * b.m[1] + a.m[1] * b.m[4];
  r.m[2] = a.m[0] * b.m[2] + a.m[1] * b.m[5] + a.m[2];
  r.m[3] = a.m[3] * b.m[0] + a.m[4] * b.m[3];
  r.m[4] = a.m[3] * b.m[1] + a.m[4] * b.m[4];
  r.m[5] = a.m[3] * b.m[2] + a.m[4] * b.m[5] + a.m[5];
  return r;
}

Affine2D InvertAffine(const Affine2D& a, bool* ok) {
  const double det = a.m[0] * a.m[4] - a.m[1] * a.m[3];
  Affine2D r;
  if (std::abs(det) < 1e-12) {
    if (ok) *ok = false;
    return r;
  }
  const double inv = 1.0 / det;
  r.m[0] = a.m[4] * inv;
  r.m[1] = -a.m[1] * inv;
  r.m[3] = -a.m[3] * inv;
  r.m[4] = a.m[0] * inv;
  r.m[2] = -(r.m[0] * a.m[2] + r.m[1] * a.m[5]);
  r.m[5] = -(r.m[3] * a.m[2] + r.m[4] * a.m[5]);
  if (ok) *ok = true;
  return r;
}

double ConditionEstimate(const Affine2D& a) {
  const double det = a.m[0] * a.m[4] - a.m[1] * a.m[3];
  const double fro =
      std::sqrt(a.m[0] * a.m[0] + a.m[1] * a.m[1] + a.m[3] * a.m[3] + a.m[4] * a.m[4]);
  if (std::abs(det) < 1e-18) return 1e18;
  return fro * fro / std::abs(det);
}

Affine2D Affine2D::FromCorners(Vec2 /*src00*/, Vec2 /*src10*/, Vec2 /*src01*/, Vec2 /*dst00*/,
                               Vec2 /*dst10*/, Vec2 /*dst01*/) {
  return Identity();
}

SolveResult SolveTransform(const SolveInput& in) {
  if (in.capture_id[0] == 0) {
    return FailSolve(Stage::SolvingTransform, FailStatus::InvalidCapture, "missing capture id",
                     in);
  }
  if (!in.workspace_roi_screen.valid()) {
    return FailSolve(Stage::SolvingTransform, FailStatus::WorkspaceDetectionFailed,
                     "invalid workspace roi", in);
  }
  if (in.numbers.scale_percent <= 0.f || in.numbers.scale_confidence < 0.2f) {
    return FailSolve(Stage::ReadingNavigatorNumbers, FailStatus::OcrScaleFailed,
                     "scale percent invalid", in);
  }
  // Rotation may be zero; require confidence when raw provided.
  if (in.numbers.rotation_confidence < 0.2f) {
    return FailSolve(Stage::ReadingNavigatorNumbers, FailStatus::OcrRotationFailed,
                     "rotation reading invalid", in);
  }

  TransformSnapshot snap;
  CopyStr(snap.capture_id, sizeof(snap.capture_id), in.capture_id);
  snap.generation = in.generation;
  std::snprintf(snap.snapshot_id, sizeof(snap.snapshot_id), "%s-%llu", in.capture_id,
                static_cast<unsigned long long>(in.generation));
  snap.workspace_roi = in.workspace_roi_screen;
  snap.navigator_roi = in.navigator_roi_screen;
  snap.navigator_thumbnail_roi = in.navigator_thumbnail_roi_screen;
  snap.workspace_canvas = in.workspace_canvas;
  snap.navigator_canvas = in.navigator_canvas;
  snap.numbers = in.numbers;
  snap.viewport = in.viewport;
  snap.rotation_degrees = in.numbers.rotation_degrees;
  CopyStr(snap.source_revision, sizeof(snap.source_revision), "sct-embedded-wb");
  snap.coordinate_convention_version = 1;

  const float cur = in.numbers.scale_percent;
  snap.scale_reference = in.initial_scale_percent > 0.f ? in.initial_scale_percent : cur;
  if (in.previous_scale_percent > 0.f) {
    snap.relative_scale = cur / in.previous_scale_percent;
  } else {
    snap.relative_scale = 1.f;
  }
  snap.cumulative_relative_scale = cur / snap.scale_reference;

  snap.screen_to_workspace = MakeScreenToWorkspace(in.workspace_roi_screen);
  snap.workspace_to_screen = MakeWorkspaceToScreen(in.workspace_roi_screen);

  const double Ww = static_cast<double>(in.workspace_roi_screen.width());
  const double Wh = static_cast<double>(in.workspace_roi_screen.height());
  if (Ww < 1 || Wh < 1) {
    return FailSolve(Stage::SolvingTransform, FailStatus::MatrixSingular, "workspace size zero",
                     in);
  }

  Affine2D t_w_to_c;
  Affine2D t_c_to_w;
  bool inv_ok = false;

  if (in.workspace_canvas.four_sides_complete && !in.workspace_canvas.ambiguous &&
      in.workspace_canvas.bounds_screen.valid()) {
    // Direct path: WorkspaceLocalPx ↔ CanvasAttachedNormalized from visible canvas.
    snap.used_direct_workspace_path = 1;
    const auto& b = in.workspace_canvas.bounds_screen;
    // Workspace-local corners of visible canvas map to (0,0)-(1,1) if four sides complete.
    const double l = b.left - in.workspace_roi_screen.left;
    const double t = b.top - in.workspace_roi_screen.top;
    const double r = b.right - in.workspace_roi_screen.left;
    const double bot = b.bottom - in.workspace_roi_screen.top;
    const double cw = r - l;
    const double ch = bot - t;
    if (cw < 1 || ch < 1) {
      return FailSolve(Stage::SolvingTransform, FailStatus::MatrixSingular,
                       "workspace canvas degenerate", in);
    }
    // p_c = ((x_w - l)/cw, (y_w - t)/ch)
    t_w_to_c.m = {1.0 / cw, 0, -l / cw, 0, 1.0 / ch, -t / ch};
    t_c_to_w = InvertAffine(t_w_to_c, &inv_ok);
    if (!inv_ok) {
      return FailSolve(Stage::SolvingTransform, FailStatus::MatrixSingular,
                       "direct path inverse failed", in);
    }
  } else {
    // Navigator path via viewport frame.
    snap.used_direct_workspace_path = 0;
    if (in.viewport.width < 1.f || in.viewport.height < 1.f) {
      return FailSolve(Stage::CompletingViewportFrame, FailStatus::InsufficientViewportGeometry,
                       "viewport frame missing", in);
    }
    // q = (x_w/Ww, y_w/Wh); p_d = o_v + q_x * a_x + q_y * a_y
    // Then map p_d into canvas attached via D^{-1} relative to navigator canvas bounds.
    if (!in.navigator_canvas.bounds_capture.valid() &&
        !in.navigator_canvas.bounds_screen.valid()) {
      return FailSolve(Stage::ObservingNavigatorCanvas, FailStatus::NavigatorCanvasAmbiguous,
                       "navigator canvas missing", in);
    }
    const wb::IntRect nc = in.navigator_canvas.bounds_screen.valid()
                               ? in.navigator_canvas.bounds_screen
                               : in.navigator_canvas.bounds_capture;
    const double nl = nc.left;
    const double nt = nc.top;
    const double nw = nc.width();
    const double nh = nc.height();
    if (nw < 1 || nh < 1) {
      return FailSolve(Stage::SolvingTransform, FailStatus::MatrixSingular,
                       "navigator canvas degenerate", in);
    }

    // T_W→D: workspace local -> display (thumbnail absolute screen/capture space used by o_v)
    // p_d.x = ov.x + (x_w/Ww)*ax.x + (y_w/Wh)*ay.x
    Affine2D t_w_to_d;
    t_w_to_d.m = {in.viewport.axis_x_displayed.x / Ww, in.viewport.axis_y_displayed.x / Wh,
                  in.viewport.origin_top_left_displayed.x,
                  in.viewport.axis_x_displayed.y / Ww, in.viewport.axis_y_displayed.y / Wh,
                  in.viewport.origin_top_left_displayed.y};

    // Display absolute -> canvas attached before rotation undo:
    // u = ((p_d.x - nl)/nw, (p_d.y - nt)/nh) in displayed attached space.
    Affine2D t_d_to_u;
    t_d_to_u.m = {1.0 / nw, 0, -nl / nw, 0, 1.0 / nh, -nt / nh};

    Affine2D D = MakeDisplayOperator(in.numbers.rotation_degrees);
    Affine2D Dinv = InvertAffine(D, &inv_ok);
    if (!inv_ok) {
      return FailSolve(Stage::SolvingTransform, FailStatus::MatrixSingular, "D inverse failed",
                       in);
    }

    // p_c = D^{-1} * u ; T_W→C = Dinv * T_D→U * T_W→D
    Affine2D t_w_to_u = Multiply(t_d_to_u, t_w_to_d);
    t_w_to_c = Multiply(Dinv, t_w_to_u);
    t_c_to_w = InvertAffine(t_w_to_c, &inv_ok);
    if (!inv_ok) {
      return FailSolve(Stage::SolvingTransform, FailStatus::MatrixSingular,
                       "navigator path inverse failed", in);
    }
  }

  if (ConditionEstimate(t_w_to_c) > 1e8) {
    return FailSolve(Stage::SolvingTransform, FailStatus::MatrixIllConditioned,
                     "ill-conditioned W→C", in);
  }

  snap.workspace_to_canvas = t_w_to_c;
  snap.canvas_to_workspace = t_c_to_w;
  snap.screen_to_canvas = Multiply(t_w_to_c, snap.screen_to_workspace);
  bool ok2 = false;
  snap.canvas_to_screen = InvertAffine(snap.screen_to_canvas, &ok2);
  if (!ok2) {
    return FailSolve(Stage::SolvingTransform, FailStatus::MatrixSingular, "S↔C inverse failed",
                     in);
  }

  // Marker at CanvasTopLeft (0,0)
  Vec2 anchor = snap.canvas_to_screen.Apply({0, 0});
  Vec2 x_end = snap.canvas_to_screen.Apply({in.marker_epsilon_canvas, 0});
  Vec2 y_end = snap.canvas_to_screen.Apply({0, in.marker_epsilon_canvas});
  snap.marker.anchor_screen = anchor;
  snap.marker.x_arm_end_screen = x_end;
  snap.marker.y_arm_end_screen = y_end;

  // Offscreen if far outside workspace (report only; still publish with flag).
  const auto& wr = in.workspace_roi_screen;
  snap.marker.offscreen =
      (anchor.x < wr.left - 8 || anchor.y < wr.top - 8 || anchor.x > wr.right + 8 ||
       anchor.y > wr.bottom + 8);

  snap.confidence = std::clamp(
      0.4f * in.workspace_canvas.confidence + 0.3f * in.navigator_canvas.confidence +
          0.2f * in.viewport.confidence + 0.1f * in.numbers.scale_confidence,
      0.f, 1.f);

  SolveResult r;
  r.status = FailStatus::Ok;
  r.snapshot = snap;
  if (snap.marker.offscreen) {
    // Publish still allowed; surface MarkerOffscreen in failure evidence but Ok status for matrix.
    r.failure.status = FailStatus::MarkerOffscreen;
    r.failure.stage = Stage::ShowingMarker;
    CopyStr(r.failure.message, sizeof(r.failure.message), "MarkerOffscreen");
    CopyStr(r.failure.capture_id, sizeof(r.failure.capture_id), in.capture_id);
    r.failure.generation = in.generation;
  }
  return r;
}

}  // namespace sct
