#include "sct/transform_solve.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>

namespace sct {
namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kRotationAxisToleranceDeg = 5.0;
// Must match TransformPipelineService's direct-workspace aspect gate.
constexpr double kDirectAspectRelativeTolerance = 0.04;

bool DirectCanvasAspectMatches(const CanvasObservation& canvas, int canvas_pixel_width,
                               int canvas_pixel_height) {
  if (!canvas.bounds_screen.valid() || canvas_pixel_width <= 0 || canvas_pixel_height <= 0)
    return false;
  const double observed = static_cast<double>(canvas.bounds_screen.width()) /
                          static_cast<double>(canvas.bounds_screen.height());
  const double archived = static_cast<double>(canvas_pixel_width) /
                          static_cast<double>(canvas_pixel_height);
  return std::isfinite(observed) && std::isfinite(archived) && archived > 0 &&
         std::abs(observed / archived - 1.0) <= kDirectAspectRelativeTolerance;
}

// A 1.0 frame supplies one genuine viewport side, but that side can be clipped
// by the Navigator's paper rectangle. In that case its tangential endpoints
// describe the visible paper, not the complete workspace viewport. Mapping the
// whole workspace to that clipped segment loses the paper's offset inside the
// workspace and produces a translation error in canvas coordinates.
//
// The workspace relation already contains the missing fact: at least one
// complete displayed paper axis plus the archived paper aspect. Build W->C
// from that anchored full-paper model when it is trustworthy. This deliberately
// leaves the viewport frame untouched: it remains factual red-frame evidence
// for the overlay/UI, while this matrix describes the actual canvas coordinates.
bool TryBuildOneCompleteEdgeWorkspaceTransform(const SolveInput& in,
                                               double rotation_geometry,
                                               Affine2D* workspace_to_canvas) {
  if (!workspace_to_canvas ||
      in.viewport.completion_strategy !=
          static_cast<int>(ViewportCompletionPattern::OneCompleteEdge) ||
      !std::isfinite(rotation_geometry) || std::abs(rotation_geometry) > 0.05) {
    return false;
  }

  const auto& rel = in.workspace_canvas_relation;
  const auto& full = rel.full_canvas_model_workspace_local;
  const auto& visible = rel.visible_canvas_bounds_workspace_local;
  if (rel.ambiguous || rel.confidence < 0.5f || !rel.workspace_roi.valid() ||
      !full.valid() || !visible.valid() || full.width() < 1 || full.height() < 1 ||
      visible.left < full.left || visible.top < full.top || visible.right > full.right ||
      visible.bottom > full.bottom) {
    return false;
  }

  constexpr int kEdgeL = 1;
  constexpr int kEdgeT = 2;
  constexpr int kEdgeR = 4;
  constexpr int kEdgeB = 8;
  const int crop = rel.canvas_crop_sides;
  const bool complete_x = (crop & (kEdgeL | kEdgeR)) == 0;
  const bool complete_y = (crop & (kEdgeT | kEdgeB)) == 0;
  if (!complete_x && !complete_y) return false;

  // A non-cropped axis must be represented verbatim by the full model. This
  // prevents an aspect-only extrapolation from being treated as an anchor.
  if ((complete_x && std::abs(full.width() - visible.width()) > 2) ||
      (complete_y && std::abs(full.height() - visible.height()) > 2)) {
    return false;
  }

  const double archived_aspect =
      static_cast<double>(in.canvas_pixel_width) / static_cast<double>(in.canvas_pixel_height);
  const double modeled_aspect = static_cast<double>(full.width()) / full.height();
  if (!std::isfinite(modeled_aspect) || !std::isfinite(archived_aspect) ||
      archived_aspect <= 0 || std::abs(modeled_aspect / archived_aspect - 1.0) > 0.02) {
    return false;
  }

  workspace_to_canvas->m = {
      1.0 / full.width(), 0, -static_cast<double>(full.left) / full.width(),
      0, 1.0 / full.height(), -static_cast<double>(full.top) / full.height()};
  return true;
}

void CopyStr(char* dst, size_t n, const char* s) {
  if (!dst || n == 0) return;
  if (!s) {
    dst[0] = 0;
    return;
  }
  std::snprintf(dst, n, "%s", s);
}

Affine2D MakeScreenToWorkspace(const wb::IntRect& workspace_screen) {
  return Affine2D::Translation(-workspace_screen.left, -workspace_screen.top);
}

Affine2D MakeWorkspaceToScreen(const wb::IntRect& workspace_screen) {
  return Affine2D::Translation(workspace_screen.left, workspace_screen.top);
}

Affine2D MakeDisplayOperator(double rotation_degrees) {
  const double rad = rotation_degrees * kPi / 180.0;
  const double c = std::cos(rad);
  const double s = std::sin(rad);
  Affine2D to_origin = Affine2D::Translation(-0.5, -0.5);
  Affine2D rot;
  rot.m = {c, -s, 0, s, c, 0};
  Affine2D from_origin = Affine2D::Translation(0.5, 0.5);
  return Multiply(from_origin, Multiply(rot, to_origin));
}

double NormalizeAngleDeg(double deg) {
  while (deg > 180.0) deg -= 360.0;
  while (deg < -180.0) deg += 360.0;
  return deg;
}

double AngleBetweenAxes(const Vec2& a, const Vec2& b) {
  const double dot = a.x * b.x + a.y * b.y;
  const double cross = a.x * b.y - a.y * b.x;
  return std::atan2(cross, dot) * 180.0 / kPi;
}

double SolveGeometryRotationDegrees(const NavigatorViewportFrame& viewport) {
  const Vec2 c_x{1, 0};
  const Vec2 c_y{0, 1};
  const double ax_len =
      std::hypot(viewport.axis_x_displayed.x, viewport.axis_x_displayed.y);
  const double ay_len =
      std::hypot(viewport.axis_y_displayed.x, viewport.axis_y_displayed.y);
  if (ax_len < 1e-6 || ay_len < 1e-6) return 0.0;

  const Vec2 a_x{viewport.axis_x_displayed.x / ax_len, viewport.axis_x_displayed.y / ax_len};
  const Vec2 a_y{viewport.axis_y_displayed.x / ay_len, viewport.axis_y_displayed.y / ay_len};
  const double theta_x = AngleBetweenAxes(c_x, a_x);
  const double theta_y = AngleBetweenAxes(c_y, a_y);
  if (std::abs(NormalizeAngleDeg(theta_x - theta_y)) > kRotationAxisToleranceDeg) {
    return std::numeric_limits<double>::quiet_NaN();
  }
  return theta_x;
}

double ScreenLengthFromCanvasDelta(const Affine2D& c2s, double dx, double dy) {
  const Vec2 p0 = c2s.Apply({0, 0});
  const Vec2 p1 = c2s.Apply({dx, dy});
  return std::hypot(p1.x - p0.x, p1.y - p0.y);
}

double CanvasEpsilonForTargetScreenLength(const Affine2D& c2s, double target_px) {
  if (target_px <= 0) return 0.04;
  double lo = 1e-6;
  double hi = 1.0;
  while (ScreenLengthFromCanvasDelta(c2s, hi, 0) < target_px && hi < 4.0) hi *= 2.0;
  for (int i = 0; i < 40; ++i) {
    const double mid = 0.5 * (lo + hi);
    if (ScreenLengthFromCanvasDelta(c2s, mid, 0) < target_px)
      lo = mid;
    else
      hi = mid;
  }
  return 0.5 * (lo + hi);
}

MarkerGeometry BuildMarkerGeometry(const Affine2D& canvas_to_screen, int canvas_pixel_width,
                                   int canvas_pixel_height, float scale_percent) {
  MarkerGeometry mg;
  const int ref_px = std::max(1, std::min(canvas_pixel_width, canvas_pixel_height));
  const float zoom = scale_percent > 0.f ? scale_percent / 100.f : 1.f;
  mg.target_arm_display_px = static_cast<float>(ref_px) * 0.05f * zoom;
  mg.target_stroke_display_px = static_cast<float>(ref_px) * 0.02f * zoom;
  mg.arm_length_canvas =
      CanvasEpsilonForTargetScreenLength(canvas_to_screen, mg.target_arm_display_px);

  // The normalized canvas coordinate system is top-left based: (0, 0) is the
  // canvas origin and +Y points down on screen.  Keep the diagnostic marker on
  // that same origin instead of drawing the former bottom-left marker at y=1.
  mg.anchor_screen = canvas_to_screen.Apply({0, 0});
  mg.x_arm_end_screen = canvas_to_screen.Apply({mg.arm_length_canvas, 0});
  const double y_length = ScreenLengthFromCanvasDelta(canvas_to_screen, 0, 1);
  const double y_epsilon = y_length > 0 ? mg.target_arm_display_px * 0.7 / y_length : 0;
  mg.y_arm_end_screen = canvas_to_screen.Apply({0, y_epsilon});
  return mg;
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
  if (!std::all_of(a.m.begin(), a.m.end(), [](double v) { return std::isfinite(v); }) ||
      !std::isfinite(det) || std::abs(det) < 1e-12) {
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

Affine2D Affine2D::FromCorners(Vec2 src00, Vec2 src10, Vec2 src01, Vec2 dst00,
                               Vec2 dst10, Vec2 dst01) {
  Affine2D src, dst;
  src.m = {src10.x-src00.x, src01.x-src00.x, src00.x,
           src10.y-src00.y, src01.y-src00.y, src00.y};
  dst.m = {dst10.x-dst00.x, dst01.x-dst00.x, dst00.x,
           dst10.y-dst00.y, dst01.y-dst00.y, dst00.y};
  bool ok = false;
  const auto inverse = InvertAffine(src, &ok);
  if (!ok) { src.m.fill(std::numeric_limits<double>::quiet_NaN()); return src; }
  return Multiply(dst, inverse);
}

SolveResult SolveTransform(const SolveInput& in) {
  if (in.capture_id[0] == 0) {
    return FailSolve(Stage::SolvingTransform, FailStatus::InvalidCapture, "missing capture id",
                     in);
  }
  if (in.canvas_pixel_width <= 0 || in.canvas_pixel_height <= 0) {
    return FailSolve(Stage::SolvingTransform, FailStatus::InvalidCanvasPixelSize,
                     "canvas pixel size missing", in);
  }
  if (!in.workspace_roi_screen.valid()) {
    return FailSolve(Stage::SolvingTransform, FailStatus::WorkspaceDetectionFailed,
                     "invalid workspace roi", in);
  }

  const float scale_percent = in.injected_scale_percent > 0.f
                                  ? in.injected_scale_percent
                                  : in.numbers.scale_percent;
  if (!std::isfinite(scale_percent) || scale_percent <= 0.f ||
      (in.injected_scale_percent <= 0.f &&
       (in.numbers.scale_confidence < 0.2f || in.numbers.scale_percent <= 0))) {
    return FailSolve(Stage::ReadingNavigatorNumbers, FailStatus::OcrScaleFailed,
                     "scale percent invalid", in);
  }

  const double rotation_geometry = SolveGeometryRotationDegrees(in.viewport);
  if (!std::isfinite(rotation_geometry)) {
    return FailSolve(Stage::CompletingViewportFrame, FailStatus::AmbiguousViewportGeometry,
                     "rotation axes inconsistent", in);
  }

  if (in.require_ocr_rotation != 0 && in.numbers.rotation_confidence < 0.2f) {
    return FailSolve(Stage::ReadingNavigatorNumbers, FailStatus::OcrRotationFailed,
                     "rotation reading invalid", in);
  }

  // Geometry is rotation authority. OCR rotation is diagnostic only — never fail the
  // solve on OCR↔geometry mismatch (viewport may be incomplete / no red frame).

  TransformSnapshot snap;
  CopyStr(snap.capture_id, sizeof(snap.capture_id), in.capture_id);
  snap.generation = in.generation;
  snap.recompute_generation = in.recompute_generation;
  snap.canvas_pixel_width = in.canvas_pixel_width;
  snap.canvas_pixel_height = in.canvas_pixel_height;
  std::snprintf(snap.snapshot_id, sizeof(snap.snapshot_id), "%s-%llu", in.capture_id,
                static_cast<unsigned long long>(in.generation));
  snap.workspace_roi = in.workspace_roi_screen;
  snap.navigator_roi = in.navigator_roi_screen;
  snap.navigator_thumbnail_roi = in.navigator_thumbnail_roi_screen;
  snap.workspace_canvas = in.workspace_canvas;
  snap.navigator_canvas = in.navigator_canvas;
  snap.workspace_canvas_relation = in.workspace_canvas_relation;
  snap.numbers = in.numbers;
  snap.viewport = in.viewport;
  snap.rotation_degrees_geometry = static_cast<float>(-rotation_geometry);
  snap.rotation_degrees_ocr_or_injected = in.numbers.rotation_degrees;
  snap.rotation_degrees = snap.rotation_degrees_geometry;
  snap.scale_percent_ocr_or_injected = scale_percent;
  CopyStr(snap.source_revision, sizeof(snap.source_revision), "sct-embedded-wb");
  snap.coordinate_convention_version = 1;

  const float cur = scale_percent;
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
      std::all_of(std::begin(in.workspace_canvas.boundary_support),
                  std::end(in.workspace_canvas.boundary_support),
                  [](float v) { return std::isfinite(v) && v >= 0.95f; }) &&
      in.workspace_canvas.bounds_screen.valid() &&
      DirectCanvasAspectMatches(in.workspace_canvas, in.canvas_pixel_width,
                                in.canvas_pixel_height) &&
      in.viewport.width < 1.f && in.viewport.height < 1.f &&
      in.numbers.rotation_confidence >= 0.2f &&
      std::isfinite(in.numbers.rotation_degrees) &&
      std::abs(std::remainder(in.numbers.rotation_degrees, 360.0)) < 0.05) {
    snap.used_direct_workspace_path = 1;
    const auto& b = in.workspace_canvas.bounds_screen;
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
    t_w_to_c.m = {1.0 / cw, 0, -l / cw, 0, 1.0 / ch, -t / ch};
    t_c_to_w = InvertAffine(t_w_to_c, &inv_ok);
    if (!inv_ok) {
      return FailSolve(Stage::SolvingTransform, FailStatus::MatrixSingular,
                       "direct path inverse failed", in);
    }
  } else {
    snap.used_direct_workspace_path = 0;
    if (in.viewport.width < 1.f || in.viewport.height < 1.f) {
      return FailSolve(Stage::CompletingViewportFrame, FailStatus::InsufficientViewportGeometry,
                       "viewport frame missing", in);
    }
    if (!in.navigator_canvas.bounds_capture.valid() &&
        !in.navigator_canvas.bounds_screen.valid()) {
      return FailSolve(Stage::ObservingNavigatorCanvas, FailStatus::NavigatorCanvasAmbiguous,
                       "navigator canvas missing", in);
    }
    const bool relation_anchored =
        TryBuildOneCompleteEdgeWorkspaceTransform(in, rotation_geometry, &t_w_to_c);
    if (!relation_anchored) {
      // Completion exports CapturePx. Never subtract a ScreenPx origin from it:
      // on a negative-origin desktop that introduces a monitor-sized translation.
      const wb::IntRect nc = in.navigator_canvas.bounds_capture.valid()
                                 ? in.navigator_canvas.bounds_capture
                                 : in.navigator_canvas.bounds_screen;
      const double nl = nc.left;
      const double nt = nc.top;
      const double nw = nc.width();
      const double nh = nc.height();
      if (nw < 1 || nh < 1) {
        return FailSolve(Stage::SolvingTransform, FailStatus::MatrixSingular,
                         "navigator canvas degenerate", in);
      }

      Affine2D t_w_to_d;
      t_w_to_d.m = {in.viewport.axis_x_displayed.x / Ww, in.viewport.axis_y_displayed.x / Wh,
                    in.viewport.origin_top_left_displayed.x,
                    in.viewport.axis_x_displayed.y / Ww, in.viewport.axis_y_displayed.y / Wh,
                    in.viewport.origin_top_left_displayed.y};

      Affine2D t_d_to_u;
      t_d_to_u.m = {1.0 / nw, 0, -nl / nw, 0, 1.0 / nh, -nt / nh};

      // The navigator canvas is unrotated. The directed red-frame axes already
      // map screen +X/+Y into it. Applying another inverse rotation here cancels
      // the real rotation (and distorts a non-square normalized canvas).
      t_w_to_c = Multiply(t_d_to_u, t_w_to_d);
    }
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

  // Scale consistency diagnostic only — never applied to matrix.
  {
    const double sx = std::hypot(snap.canvas_to_screen.m[0], snap.canvas_to_screen.m[3])
                      / in.canvas_pixel_width;
    const double sy = std::hypot(snap.canvas_to_screen.m[1], snap.canvas_to_screen.m[4])
                      / in.canvas_pixel_height;
    snap.scale_geometry_estimate = static_cast<float>(100.0 * std::sqrt(sx * sy));
    snap.scale_consistency_error =
        std::abs(snap.scale_geometry_estimate - scale_percent) / std::max(scale_percent, 1.f);
  }

  snap.marker = BuildMarkerGeometry(snap.canvas_to_screen, in.canvas_pixel_width,
                                    in.canvas_pixel_height, scale_percent);

  const auto& wr = in.workspace_roi_screen;
  snap.marker.offscreen =
      (snap.marker.anchor_screen.x < wr.left - 8 ||
       snap.marker.anchor_screen.y < wr.top - 8 ||
       snap.marker.anchor_screen.x > wr.right + 8 ||
       snap.marker.anchor_screen.y > wr.bottom + 8);

  snap.confidence = std::clamp(
      0.4f * in.workspace_canvas.confidence + 0.3f * in.navigator_canvas.confidence +
          0.2f * in.viewport.confidence + 0.1f * in.numbers.scale_confidence,
      0.f, 1.f);

  SolveResult r;
  r.status = FailStatus::Ok;
  r.snapshot = snap;
  if (snap.marker.offscreen) {
    r.failure.status = FailStatus::MarkerOffscreen;
    r.failure.stage = Stage::ShowingMarker;
    CopyStr(r.failure.message, sizeof(r.failure.message), "MarkerOffscreen");
    CopyStr(r.failure.capture_id, sizeof(r.failure.capture_id), in.capture_id);
    r.failure.generation = in.generation;
  }
  return r;
}

}  // namespace sct
