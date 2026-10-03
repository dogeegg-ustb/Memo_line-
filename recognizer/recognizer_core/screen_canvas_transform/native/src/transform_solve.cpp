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

// Navigator-route refinement by the scale reading and workspace paper edges.
constexpr double kRefineAxisSinMax = 0.0087;  // within ~0.5 degrees of a screen axis
constexpr double kNavigatorAnisotropyMax = kDirectAspectRelativeTolerance;
// Red-frame zoom error stays within ~7% per axis; a misread digit or decimal
// point does not.
constexpr double kNavigatorZoomRelativeMax = 0.10;
constexpr double kReadingPaperRelativeMax = 0.03;
constexpr double kReadingDisplayStepPercent = 0.1;  // CSP shows one decimal
constexpr double kPaperSizeSlackPx = 2.0;
// Navigator px between an unrotated red stroke's centre and the boundary it draws.
constexpr double kRedStrokeCentreOffsetPx = 0.5;
constexpr float kPaperEdgeSupportMin = 0.90f;

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

// The Navigator route measures the paper and red frame on a thumbnail several
// times smaller than the workspace: one thumbnail pixel of paper-bound or red
// line error becomes a visible offset of many screen pixels. Two independent
// measurements are far finer and replace parts of the red-frame matrix:
//
// Zoom. The scale reading is CSP's own zoom, exact up to its displayed
// decimal (CSP 100% = one canvas px per physical screen px), and isotropic.
// It replaces the red-frame zoom unless the two disagree beyond the frame's
// error, which exposes a misread. Two opposite paper edges observed in the
// workspace measure the displayed size directly and take precedence.
//
// Translation. A paper edge the workspace observed directly fixes the
// translation along its screen axis. Its canvas boundary follows from which
// side the paper lies on and the Navigator's axis direction, never from the
// Navigator translation, so a stale or misplaced red frame is corrected. An
// axis without such an edge keeps the red-frame mapping where it was
// measured: on its observed red sides, otherwise at the workspace centre.
//
// Paper edges qualify only on an axis-aligned display (a rotated paper has no
// straight bounding-box sides) and only as a clean paper rectangle clipped by
// the workspace whose size agrees with the reference zoom; anything else
// (artwork on an edge, a floating panel as largest foreground) is ignored.
// t_w_to_c is left untouched when neither measurement applies.
struct NavigatorRefinement {
  int anchor_mask = 0;  // workspace paper sides (L/T/R/B bits) used
  MatrixZoomSource zoom_source = MatrixZoomSource::NavigatorFrame;
};

NavigatorRefinement RefineNavigatorMatrix(const SolveInput& in, float scale_percent,
                                          const wb::IntRect& nav_canvas, Affine2D& t_w_to_c) {
  NavigatorRefinement out;
  const auto& ws = in.workspace_roi_screen;
  const auto& obs = in.workspace_canvas;
  bool ok = false;
  const Affine2D c2w = InvertAffine(t_w_to_c, &ok);
  if (!ok || !ws.valid() || !nav_canvas.valid()) return out;

  // Workspace px per normalized unit along canvas u (k=0) and v (k=1).
  const Vec2 col[2] = {{c2w.m[0], c2w.m[3]}, {c2w.m[1], c2w.m[4]}};
  const double canvas_px[2] = {double(in.canvas_pixel_width), double(in.canvas_pixel_height)};
  double len[2], zoom[2];
  for (int k = 0; k < 2; ++k) {
    len[k] = std::hypot(col[k].x, col[k].y);
    if (!(len[k] > 1e-6)) return out;
    zoom[k] = len[k] / canvas_px[k];
  }
  const double navigator_zoom = std::sqrt(zoom[0] * zoom[1]);
  const double reading = scale_percent / 100.0;
  const bool reading_ok = std::isfinite(reading) && reading > 0 &&
                          std::abs(zoom[0] / reading - 1.0) <= kNavigatorZoomRelativeMax &&
                          std::abs(zoom[1] / reading - 1.0) <= kNavigatorZoomRelativeMax;
  if (!reading_ok && std::abs(zoom[0] / zoom[1] - 1.0) > kNavigatorAnisotropyMax) return out;
  const double reference_zoom = reading_ok ? reading : navigator_zoom;

  auto screen_axis_of = [](const Vec2& c, double l) {
    if (std::abs(c.y) <= kRefineAxisSinMax * l) return 0;
    if (std::abs(c.x) <= kRefineAxisSinMax * l) return 1;
    return -1;
  };
  const int axis_of[2] = {screen_axis_of(col[0], len[0]), screen_axis_of(col[1], len[1])};
  const bool axis_aligned = axis_of[0] >= 0 && axis_of[1] >= 0 && axis_of[0] != axis_of[1];
  // Canvas axis driving screen axis a on an axis-aligned display.
  auto canvas_axis_on = [&](int a) { return axis_of[0] == a ? 0 : 1; };
  auto component = [](const Vec2& p, int a) { return a == 0 ? p.x : p.y; };

  // Workspace paper edges per screen axis: position and canvas boundary (0/1).
  double edge_pos[2][2] = {}, edge_boundary[2][2] = {};
  int edge_count[2] = {0, 0};
  if (axis_aligned && !obs.ambiguous && obs.bounds_screen.valid()) {
    // Bounds are pixel edges in the direct-path convention: the first paper
    // column is canvas coordinate 0 at its left edge.
    const double position[4] = {double(obs.bounds_screen.left - ws.left),
                                double(obs.bounds_screen.top - ws.top),
                                double(obs.bounds_screen.right - ws.left),
                                double(obs.bounds_screen.bottom - ws.top)};
    const int cropped = in.workspace_canvas_relation.canvas_crop_sides;
    bool paper = true;
    int mask = 0;
    for (int side = 0; side < 4 && paper; ++side) {
      const int bit = 1 << side;
      if (cropped & bit) continue;
      // Every side is either a measured straight paper edge or clipped by the
      // workspace. A side that is neither is no axis-aligned paper rectangle.
      if (!(obs.visible_edges_mask & bit) || !(obs.boundary_support[side] >= kPaperEdgeSupportMin)) {
        paper = false;
        break;
      }
      const int a = side % 2;
      // L/T have the paper after them, R/B before. Under a flipped axis (e.g.
      // a 180-degree display) the paper-start side is canvas coordinate 1.
      const bool forward = component(col[canvas_axis_on(a)], a) > 0;
      edge_pos[a][edge_count[a]] = position[side];
      edge_boundary[a][edge_count[a]] = (side < 2) == forward ? 0.0 : 1.0;
      ++edge_count[a];
      mask |= bit;
    }
    // The paper must have the size the reference zoom predicts: complete on an
    // axis with both edges, at most that size where the workspace clips it.
    const double tolerance =
        reading_ok ? kReadingPaperRelativeMax + 0.5 * kReadingDisplayStepPercent / scale_percent
                   : kNavigatorZoomRelativeMax;
    for (int a = 0; a < 2 && paper; ++a) {
      const double expected = reference_zoom * canvas_px[canvas_axis_on(a)];
      const double visible = a == 0 ? obs.bounds_screen.width() : obs.bounds_screen.height();
      paper = edge_count[a] == 2
                  ? std::abs(visible - expected) <= tolerance * expected + kPaperSizeSlackPx
                  : visible <= expected * (1.0 + tolerance) + kPaperSizeSlackPx;
    }
    if (paper) {
      out.anchor_mask = mask;
    } else {
      edge_count[0] = edge_count[1] = 0;
    }
  }

  double new_zoom = 0;
  int measured = 0;
  for (int a = 0; a < 2; ++a) {
    if (edge_count[a] != 2) continue;
    new_zoom += (edge_pos[a][1] - edge_pos[a][0]) / canvas_px[canvas_axis_on(a)];
    ++measured;
  }
  if (measured) {
    new_zoom /= measured;
    out.zoom_source = MatrixZoomSource::WorkspacePaperEdges;
  } else if (reading_ok) {
    new_zoom = reading;
    out.zoom_source = MatrixZoomSource::ScaleReading;
  } else {
    new_zoom = navigator_zoom;
  }
  if (out.zoom_source == MatrixZoomSource::NavigatorFrame && !out.anchor_mask) return out;
  if (!std::isfinite(new_zoom) || !(new_zoom > 0)) return {};

  // Linear part: Navigator axis directions (snapped when axis aligned), one zoom.
  Affine2D refined;
  refined.m = {0, 0, 0, 0, 0, 0};
  for (int k = 0; k < 2; ++k) {
    Vec2 dir{col[k].x / len[k], col[k].y / len[k]};
    if (axis_aligned) {
      dir = axis_of[k] == 0 ? Vec2{std::copysign(1.0, col[k].x), 0}
                            : Vec2{0, std::copysign(1.0, col[k].y)};
    }
    refined.m[k] = dir.x * new_zoom * canvas_px[k];
    refined.m[3 + k] = dir.y * new_zoom * canvas_px[k];
  }
  auto linear = [&](const Vec2& u) {
    return Vec2{refined.m[0] * u.x + refined.m[1] * u.y, refined.m[3] * u.x + refined.m[4] * u.y};
  };
  auto nav_to_canvas = [&](const Vec2& p) {
    return Vec2{(p.x - nav_canvas.left) / nav_canvas.width(),
                (p.y - nav_canvas.top) / nav_canvas.height()};
  };

  for (int a = 0; a < 2; ++a) {
    double sum = 0;
    int n = 0;
    for (int e = 0; e < edge_count[a]; ++e) {
      Vec2 u{0, 0};
      (canvas_axis_on(a) == 0 ? u.x : u.y) = edge_boundary[a][e];
      sum += edge_pos[a][e] - component(linear(u), a);
      ++n;
    }
    if (n == 0) {
      // Each observed red side is a workspace side: vertical ones fix x,
      // horizontal ones y. Map each one onto the workspace side it draws.
      const double length = a == 0 ? ws.width() : ws.height();
      for (int i = 0; i < in.viewport.observed_red_edge_export_count; ++i) {
        const auto& red = in.viewport.observed_red_edges[i];
        Vec2 p0 = red.p0, p1 = red.p1;
        const double ndx = std::abs(p1.x - p0.x), ndy = std::abs(p1.y - p0.y);
        if (axis_aligned && std::min(ndx, ndy) <= kRefineAxisSinMax * std::max(ndx, ndy)) {
          // CSP draws an unrotated viewport side on the Navigator pixel the
          // boundary rounds to; its centre lies half a pixel past the
          // boundary towards +x/+y, which the workspace zoom magnifies.
          p0 = {p0.x - kRedStrokeCentreOffsetPx, p0.y - kRedStrokeCentreOffsetPx};
          p1 = {p1.x - kRedStrokeCentreOffsetPx, p1.y - kRedStrokeCentreOffsetPx};
        }
        const Vec2 u0 = nav_to_canvas(p0), u1 = nav_to_canvas(p1);
        const Vec2 w0 = c2w.Apply(u0), w1 = c2w.Apply(u1);
        const double dx = std::abs(w1.x - w0.x), dy = std::abs(w1.y - w0.y);
        if (std::max(dx, dy) < 1.0 || (a == 0) != (dx < dy)) continue;
        const Vec2 um{0.5 * (u0.x + u1.x), 0.5 * (u0.y + u1.y)};
        const double side = component(c2w.Apply(um), a) < 0.5 * length ? 0.0 : length;
        sum += side - component(linear(um), a);
        ++n;
      }
    }
    if (n == 0) {
      const Vec2 centre{0.5 * ws.width(), 0.5 * ws.height()};
      sum = component(centre, a) - component(linear(t_w_to_c.Apply(centre)), a);
      n = 1;
    }
    refined.m[a * 3 + 2] = sum / n;
  }
  const Affine2D t = InvertAffine(refined, &ok);
  if (!ok) return {};
  t_w_to_c = t;
  return out;
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
    const auto refinement = RefineNavigatorMatrix(in, scale_percent, nc, t_w_to_c);
    snap.workspace_edge_anchor_mask = refinement.anchor_mask;
    snap.matrix_zoom_source = refinement.zoom_source;

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

  // Published geometry zoom versus the reading; near zero once the reading set the zoom.
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
