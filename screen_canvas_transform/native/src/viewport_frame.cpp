#include "sct/viewport_frame.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

namespace sct {
namespace {

struct RedSeg {
  int x0, y0, x1, y1;  // inclusive endpoints in capture px
  bool horizontal = false;
  float support = 0.f;
};

inline bool IsRedPixel(const uint8_t* p) {
  const int b = p[0], g = p[1], r = p[2];
  return r >= 140 && r - g >= 40 && r - b >= 40 && r >= g + 20;
}

ViewportCompletionResult Fail(FailStatus st, const char* msg) {
  ViewportCompletionResult r;
  r.status = st;
  std::snprintf(r.message, sizeof(r.message), "%s", msg);
  return r;
}

void SetCorners(NavigatorViewportFrame& f) {
  f.semantic_corners[0] = f.origin_top_left_displayed;
  f.semantic_corners[1] = {f.origin_top_left_displayed.x + f.axis_x_displayed.x,
                           f.origin_top_left_displayed.y + f.axis_x_displayed.y};
  f.semantic_corners[2] = {f.origin_top_left_displayed.x + f.axis_x_displayed.x +
                               f.axis_y_displayed.x,
                           f.origin_top_left_displayed.y + f.axis_x_displayed.y +
                               f.axis_y_displayed.y};
  f.semantic_corners[3] = {f.origin_top_left_displayed.x + f.axis_y_displayed.x,
                           f.origin_top_left_displayed.y + f.axis_y_displayed.y};
  f.width = static_cast<float>(
      std::hypot(f.axis_x_displayed.x, f.axis_x_displayed.y));
  f.height = static_cast<float>(
      std::hypot(f.axis_y_displayed.x, f.axis_y_displayed.y));
}

}  // namespace

ViewportCompletionResult CompleteViewportFrame(const ViewportCompletionInput& in) {
  if (!in.bgra || in.width <= 0 || in.height <= 0 || !in.thumbnail_roi.valid()) {
    return Fail(FailStatus::InvalidCapture, "invalid viewport input");
  }

  const wb::IntRect roi = in.thumbnail_roi.Clamp(in.width, in.height);
  if (!roi.valid()) return Fail(FailStatus::RedFrameNotFound, "thumbnail roi empty");

  // Collect red mask bounding runs on axis-aligned sides (axis-aligned first path).
  std::vector<uint8_t> red(static_cast<size_t>(roi.width()) * roi.height(), 0);
  int red_count = 0;
  for (int y = roi.top; y < roi.bottom; ++y) {
    for (int x = roi.left; x < roi.right; ++x) {
      const uint8_t* p =
          in.bgra + static_cast<size_t>(y) * in.stride + static_cast<size_t>(x) * 4;
      if (IsRedPixel(p)) {
        red[static_cast<size_t>(y - roi.top) * roi.width() + (x - roi.left)] = 1;
        ++red_count;
      }
    }
  }
  if (red_count < 20) return Fail(FailStatus::RedFrameNotFound, "insufficient red pixels");

  // Axis-aligned red edge detection via projection peaks.
  const int rw = roi.width();
  const int rh = roi.height();
  std::vector<int> col(rw, 0), row(rh, 0);
  for (int y = 0; y < rh; ++y) {
    for (int x = 0; x < rw; ++x) {
      if (!red[static_cast<size_t>(y) * rw + x]) continue;
      ++col[x];
      ++row[y];
    }
  }

  auto find_peaks = [](const std::vector<int>& hist, int min_run) {
    std::vector<int> peaks;
    const int thr = std::max(3, min_run / 8);
    for (int i = 1; i + 1 < static_cast<int>(hist.size()); ++i) {
      if (hist[i] >= thr && hist[i] >= hist[i - 1] && hist[i] >= hist[i + 1]) peaks.push_back(i);
    }
    // Keep strongest two.
    std::sort(peaks.begin(), peaks.end(),
              [&](int a, int b) { return hist[a] > hist[b]; });
    if (peaks.size() > 2) peaks.resize(2);
    std::sort(peaks.begin(), peaks.end());
    return peaks;
  };

  auto vpeaks = find_peaks(col, rh);
  auto hpeaks = find_peaks(row, rw);

  int left = -1, right = -1, top = -1, bottom = -1;
  if (vpeaks.size() >= 2) {
    left = vpeaks.front();
    right = vpeaks.back();
  } else if (vpeaks.size() == 1) {
    left = vpeaks[0];
  }
  if (hpeaks.size() >= 2) {
    top = hpeaks.front();
    bottom = hpeaks.back();
  } else if (hpeaks.size() == 1) {
    top = hpeaks[0];
  }

  int full_edges = 0;
  if (left >= 0) ++full_edges;
  if (right >= 0) ++full_edges;
  if (top >= 0) ++full_edges;
  if (bottom >= 0) ++full_edges;

  if (full_edges == 0) {
    return Fail(FailStatus::InsufficientViewportGeometry, "no axis-aligned red edges");
  }

  NavigatorViewportFrame frame;
  frame.visible_edge_count = full_edges;
  frame.red_evidence.segment_count = static_cast<int>(std::min<size_t>(vpeaks.size() + hpeaks.size(), 32));
  frame.red_evidence.confirmed_complete_edge_count = full_edges;
  frame.red_evidence.confirmed_corner_count =
      (left >= 0 && top >= 0 ? 1 : 0) + (right >= 0 && top >= 0 ? 1 : 0) +
      (right >= 0 && bottom >= 0 ? 1 : 0) + (left >= 0 && bottom >= 0 ? 1 : 0);

  auto set_pattern = [&](ViewportCompletionPattern p) {
    frame.completion_strategy = static_cast<int>(p);
    frame.red_evidence.completion_pattern = p;
  };

  const double aspect = in.workspace_canvas_relation.canvas_aspect_ratio > 1e-6
                            ? in.workspace_canvas_relation.canvas_aspect_ratio
                            : 1.0;
  const auto& wcr = in.workspace_canvas_relation;
  const int wl = wcr.workspace_roi.left;
  const int wt = wcr.workspace_roi.top;

  auto max_horizontal_span_at = [&](int y) {
    int best = 0;
    int run = 0;
    for (int x = 0; x < rw; ++x) {
      if (red[static_cast<size_t>(y) * rw + x]) {
        best = std::max(best, ++run);
      } else {
        run = 0;
      }
    }
    return best;
  };
  auto max_vertical_span_at = [&](int x) {
    int best = 0;
    int run = 0;
    for (int y = 0; y < rh; ++y) {
      if (red[static_cast<size_t>(y) * rw + x]) {
        best = std::max(best, ++run);
      } else {
        run = 0;
      }
    }
    return best;
  };

  auto finish_ok = [&](NavigatorViewportFrame f) {
    SetCorners(f);
    // Must intersect navigator canvas bounds when provided.
    if (in.navigator_canvas_bounds.valid()) {
      const double minx =
          std::min({f.semantic_corners[0].x, f.semantic_corners[1].x, f.semantic_corners[2].x,
                    f.semantic_corners[3].x});
      const double maxx =
          std::max({f.semantic_corners[0].x, f.semantic_corners[1].x, f.semantic_corners[2].x,
                    f.semantic_corners[3].x});
      const double miny =
          std::min({f.semantic_corners[0].y, f.semantic_corners[1].y, f.semantic_corners[2].y,
                    f.semantic_corners[3].y});
      const double maxy =
          std::max({f.semantic_corners[0].y, f.semantic_corners[1].y, f.semantic_corners[2].y,
                    f.semantic_corners[3].y});
      if (maxx < in.navigator_canvas_bounds.left || minx >= in.navigator_canvas_bounds.right ||
          maxy < in.navigator_canvas_bounds.top || miny >= in.navigator_canvas_bounds.bottom) {
        return Fail(FailStatus::AmbiguousViewportGeometry, "viewport outside navigator canvas");
      }
    }
    f.confidence = std::clamp(0.25f * full_edges, 0.f, 1.f);
    ViewportCompletionResult r;
    r.status = FailStatus::Ok;
    r.frame = f;
    std::snprintf(r.message, sizeof(r.message), "ok edges=%d", full_edges);
    return r;
  };

  // Convert local peak coords to capture absolute for o_v (semantic top-left).
  auto abs_x = [&](int lx) { return roi.left + lx + 0.5; };
  auto abs_y = [&](int ly) { return roi.top + ly + 0.5; };

  if (full_edges == 4) {
    set_pattern(ViewportCompletionPattern::FourCompleteEdges);
    frame.origin_top_left_displayed = {abs_x(left), abs_y(top)};
    frame.axis_x_displayed = {abs_x(right) - abs_x(left), 0};
    frame.axis_y_displayed = {0, abs_y(bottom) - abs_y(top)};
    // Enforce workspace +X/+Y correspondence (no long/short swap).
    if (frame.axis_x_displayed.x <= 0 || frame.axis_y_displayed.y <= 0) {
      return Fail(FailStatus::AmbiguousViewportGeometry, "degenerate 4-edge axes");
    }
    return finish_ok(frame);
  }

  if (full_edges == 3) {
    set_pattern(ViewportCompletionPattern::ThreeCompleteEdges);
    if (left < 0 && right >= 0 && top >= 0 && bottom >= 0) {
      const double h = abs_y(bottom) - abs_y(top);
      const double w = h * aspect;
      frame.origin_top_left_displayed = {abs_x(right) - w, abs_y(top)};
      frame.axis_x_displayed = {w, 0};
      frame.axis_y_displayed = {0, h};
      return finish_ok(frame);
    }
    if (right < 0 && left >= 0 && top >= 0 && bottom >= 0) {
      const double h = abs_y(bottom) - abs_y(top);
      const double w = h * aspect;
      frame.origin_top_left_displayed = {abs_x(left), abs_y(top)};
      frame.axis_x_displayed = {w, 0};
      frame.axis_y_displayed = {0, h};
      return finish_ok(frame);
    }
    if (top < 0 && left >= 0 && right >= 0 && bottom >= 0) {
      const double w = abs_x(right) - abs_x(left);
      const double h = w / aspect;
      frame.origin_top_left_displayed = {abs_x(left), abs_y(bottom) - h};
      frame.axis_x_displayed = {w, 0};
      frame.axis_y_displayed = {0, h};
      return finish_ok(frame);
    }
    if (bottom < 0 && left >= 0 && right >= 0 && top >= 0) {
      const double w = abs_x(right) - abs_x(left);
      const double h = w / aspect;
      frame.origin_top_left_displayed = {abs_x(left), abs_y(top)};
      frame.axis_x_displayed = {w, 0};
      frame.axis_y_displayed = {0, h};
      return finish_ok(frame);
    }
    return Fail(FailStatus::AmbiguousViewportGeometry, "3-edge completion ambiguous");
  }

  if (full_edges == 2) {
    if (left >= 0 && top >= 0 && right < 0 && bottom < 0) {
      set_pattern(ViewportCompletionPattern::TwoIntersectingCompleteEdges);
      // Estimate size from red extent.
      int maxx = left, maxy = top;
      for (int y = 0; y < rh; ++y)
        for (int x = 0; x < rw; ++x)
          if (red[static_cast<size_t>(y) * rw + x]) {
            maxx = std::max(maxx, x);
            maxy = std::max(maxy, y);
          }
      double w = abs_x(maxx) - abs_x(left);
      double h = abs_y(maxy) - abs_y(top);
      if (w < 4 || h < 4) {
        h = std::max(8.0, static_cast<double>(rh) * 0.25);
        w = h * aspect;
      } else {
        // Resolve with workspace aspect: keep +X/+Y mapping.
        const double h2 = w / aspect;
        const double w2 = h * aspect;
        if (std::abs(h2 - h) <= std::abs(w2 - w))
          h = h2;
        else
          w = w2;
      }
      frame.origin_top_left_displayed = {abs_x(left), abs_y(top)};
      frame.axis_x_displayed = {w, 0};
      frame.axis_y_displayed = {0, h};
      return finish_ok(frame);
    }
    if (left >= 0 && right >= 0) {
      set_pattern(ViewportCompletionPattern::TwoParallelCompleteEdges);
      const double w = abs_x(right) - abs_x(left);
      const double h = w / aspect;
      double cy = roi.top + rh * 0.5;
      // Prefer vertical red mass center.
      double sum = 0, wy = 0;
      for (int y = 0; y < rh; ++y)
        if (row[y] > 0) {
          sum += row[y];
          wy += row[y] * y;
        }
      if (sum > 0) cy = abs_y(static_cast<int>(wy / sum));
      frame.origin_top_left_displayed = {abs_x(left), cy - h * 0.5};
      frame.axis_x_displayed = {w, 0};
      frame.axis_y_displayed = {0, h};
      return finish_ok(frame);
    }
    if (top >= 0 && bottom >= 0) {
      set_pattern(ViewportCompletionPattern::TwoParallelCompleteEdges);
      const double h = abs_y(bottom) - abs_y(top);
      const double w = h * aspect;
      double cx = roi.left + rw * 0.5;
      double sum = 0, wx = 0;
      for (int x = 0; x < rw; ++x)
        if (col[x] > 0) {
          sum += col[x];
          wx += col[x] * x;
        }
      if (sum > 0) cx = abs_x(static_cast<int>(wx / sum));
      frame.origin_top_left_displayed = {cx - w * 0.5, abs_y(top)};
      frame.axis_x_displayed = {w, 0};
      frame.axis_y_displayed = {0, h};
      return finish_ok(frame);
    }
    return Fail(FailStatus::AmbiguousViewportGeometry, "2-edge topology unsupported");
  }

  // 1 edge — use WorkspaceCanvasRelation to resolve workspace +X/+Y semantics.
  if (left >= 0 || right >= 0) {
    set_pattern(ViewportCompletionPattern::OneCompleteEdge);
    const int xedge = left >= 0 ? left : right;
    const float share_y = wcr.visible_canvas_workspace_fraction_y;
    const int covered_canvas_px = max_vertical_span_at(xedge);
    if (share_y <= 1e-4f || covered_canvas_px < 2) {
      return Fail(FailStatus::AmbiguousViewportGeometry,
                  "workspace canvas ratio unavailable for vertical edge");
    }
    // The red edge covers a navigator-scaled slice of the canvas.  The
    // workspace canvas/background fraction converts that slice to the full
    // viewport edge; it is not a generic canvas aspect fallback.
    double h = static_cast<double>(covered_canvas_px) / share_y;
    double w = h * aspect;
    double cy = roi.top + rh * 0.5;
    if (wcr.visible_canvas_bounds_workspace_local.height() > 0) {
      cy = wt + wcr.visible_canvas_bounds_workspace_local.top +
           wcr.visible_canvas_bounds_workspace_local.height() * 0.5;
    }
    frame.origin_top_left_displayed = {left >= 0 ? abs_x(xedge) : abs_x(xedge) - w, cy - h * 0.5};
    frame.axis_x_displayed = {w, 0};
    frame.axis_y_displayed = {0, h};
    return finish_ok(frame);
  }
  if (top >= 0 || bottom >= 0) {
    set_pattern(ViewportCompletionPattern::OneCompleteEdge);
    const int yedge = top >= 0 ? top : bottom;
    const float share_x = wcr.visible_canvas_workspace_fraction_x;
    const int covered_canvas_px = max_horizontal_span_at(yedge);
    if (share_x <= 1e-4f || covered_canvas_px < 2) {
      return Fail(FailStatus::AmbiguousViewportGeometry,
                  "workspace canvas ratio unavailable for horizontal edge");
    }
    double w = static_cast<double>(covered_canvas_px) / share_x;
    double h = w / aspect;
    double cx = roi.left + rw * 0.5;
    if (wcr.visible_canvas_bounds_workspace_local.width() > 0) {
      cx = wl + wcr.visible_canvas_bounds_workspace_local.left +
           wcr.visible_canvas_bounds_workspace_local.width() * 0.5;
    }
    frame.origin_top_left_displayed = {cx - w * 0.5, top >= 0 ? abs_y(yedge) : abs_y(yedge) - h};
    frame.axis_x_displayed = {w, 0};
    frame.axis_y_displayed = {0, h};
    return finish_ok(frame);
  }

  return Fail(FailStatus::InsufficientViewportGeometry, "cannot complete viewport");
}

}  // namespace sct
