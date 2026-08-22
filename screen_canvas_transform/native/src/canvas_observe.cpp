#include "sct/canvas_observe.hpp"

#include "wb/color.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <queue>
#include <vector>

namespace sct {
namespace {

inline void BgraAt(const uint8_t* bgra, int stride, int x, int y, uint8_t& b, uint8_t& g,
                   uint8_t& r) {
  const uint8_t* p = bgra + static_cast<size_t>(y) * stride + static_cast<size_t>(x) * 4;
  b = p[0];
  g = p[1];
  r = p[2];
}

}  // namespace

CanvasObservation ObserveCanvasExcludingBackground(
    const uint8_t* bgra, int width, int height, int stride, const wb::IntRect& roi_capture,
    int origin_x, int origin_y, const wb::BackgroundModel& model, float /*dpi_scale*/) {
  CanvasObservation out;
  if (!bgra || width <= 0 || height <= 0 || !roi_capture.valid()) {
    out.ambiguous = true;
    std::snprintf(out.ambiguity_reason, sizeof(out.ambiguity_reason), "invalid input");
    return out;
  }

  wb::IntRect roi = roi_capture.Clamp(width, height);
  if (!roi.valid()) {
    out.ambiguous = true;
    std::snprintf(out.ambiguity_reason, sizeof(out.ambiguity_reason), "roi empty");
    return out;
  }

  const int rw = roi.width();
  const int rh = roi.height();
  std::vector<uint8_t> non_bg(static_cast<size_t>(rw) * rh, 0);

  for (int y = roi.top; y < roi.bottom; ++y) {
    for (int x = roi.left; x < roi.right; ++x) {
      uint8_t b, g, r;
      BgraAt(bgra, stride, x, y, b, g, r);
      wb::Lab lab = wb::BgrToLab(b, g, r);
      float de = wb::DeltaE76(lab, model.center_lab);
      const int ix = x - roi.left;
      const int iy = y - roi.top;
      if (de > model.weak_delta_e) {
        non_bg[static_cast<size_t>(iy) * rw + ix] = 1;
      }
    }
  }

  // Largest 4-connected non-background component.
  std::vector<int> labels(static_cast<size_t>(rw) * rh, 0);
  int best_label = 0;
  int best_count = 0;
  int next = 1;
  for (int y = 0; y < rh; ++y) {
    for (int x = 0; x < rw; ++x) {
      size_t i = static_cast<size_t>(y) * rw + x;
      if (!non_bg[i] || labels[i]) continue;
      int count = 0;
      int minx = x, maxx = x, miny = y, maxy = y;
      std::queue<std::pair<int, int>> q;
      q.push({x, y});
      labels[i] = next;
      while (!q.empty()) {
        auto [cx, cy] = q.front();
        q.pop();
        ++count;
        minx = std::min(minx, cx);
        maxx = std::max(maxx, cx);
        miny = std::min(miny, cy);
        maxy = std::max(maxy, cy);
        const int nbs[4][2] = {{1, 0}, {-1, 0}, {0, 1}, {0, -1}};
        for (auto& d : nbs) {
          int nx = cx + d[0];
          int ny = cy + d[1];
          if (nx < 0 || ny < 0 || nx >= rw || ny >= rh) continue;
          size_t ni = static_cast<size_t>(ny) * rw + nx;
          if (!non_bg[ni] || labels[ni]) continue;
          labels[ni] = next;
          q.push({nx, ny});
        }
      }
      if (count > best_count) {
        best_count = count;
        best_label = next;
        out.bounds_capture = {roi.left + minx, roi.top + miny, roi.left + maxx + 1,
                              roi.top + maxy + 1};
      }
      ++next;
    }
  }

  if (best_count <= 0 || !out.bounds_capture.valid()) {
    out.ambiguous = true;
    std::snprintf(out.ambiguity_reason, sizeof(out.ambiguity_reason), "no non-background canvas");
    return out;
  }

  // Recompute bounds for best_label precisely.
  int minx = rw, maxx = -1, miny = rh, maxy = -1;
  int fill = 0;
  for (int y = 0; y < rh; ++y) {
    for (int x = 0; x < rw; ++x) {
      if (labels[static_cast<size_t>(y) * rw + x] != best_label) continue;
      ++fill;
      minx = std::min(minx, x);
      maxx = std::max(maxx, x);
      miny = std::min(miny, y);
      maxy = std::max(maxy, y);
    }
  }
  out.bounds_capture = {roi.left + minx, roi.top + miny, roi.left + maxx + 1, roi.top + maxy + 1};
  out.bounds_screen = {out.bounds_capture.left + origin_x, out.bounds_capture.top + origin_y,
                       out.bounds_capture.right + origin_x, out.bounds_capture.bottom + origin_y};

  const int bw = out.bounds_capture.width();
  const int bh = out.bounds_capture.height();
  if (bw <= 0 || bh <= 0) {
    out.ambiguous = true;
    std::snprintf(out.ambiguity_reason, sizeof(out.ambiguity_reason), "empty bounds");
    return out;
  }

  out.aspect_ratio = static_cast<float>(bw) / static_cast<float>(bh);
  const float fill_ratio = static_cast<float>(fill) / static_cast<float>(bw * bh);

  // Edge support: fraction of boundary pixels belonging to best component.
  auto edge_support = [&](int side) -> float {
    int hit = 0, total = 0;
    if (side == 0) {  // left
      for (int y = miny; y <= maxy; ++y) {
        ++total;
        if (labels[static_cast<size_t>(y) * rw + minx] == best_label) ++hit;
      }
    } else if (side == 1) {  // top
      for (int x = minx; x <= maxx; ++x) {
        ++total;
        if (labels[static_cast<size_t>(miny) * rw + x] == best_label) ++hit;
      }
    } else if (side == 2) {  // right
      for (int y = miny; y <= maxy; ++y) {
        ++total;
        if (labels[static_cast<size_t>(y) * rw + maxx] == best_label) ++hit;
      }
    } else {
      for (int x = minx; x <= maxx; ++x) {
        ++total;
        if (labels[static_cast<size_t>(maxy) * rw + x] == best_label) ++hit;
      }
    }
    return total > 0 ? static_cast<float>(hit) / static_cast<float>(total) : 0.f;
  };

  for (int s = 0; s < 4; ++s) {
    out.boundary_support[s] = edge_support(s);
    if (out.boundary_support[s] >= 0.55f) out.visible_edges_mask |= (1 << s);
  }

  // Completeness: edges not touching ROI rim (visible canvas edge inside workspace).
  int complete = 0;
  const int band = 2;
  if ((out.visible_edges_mask & 1) && minx > band) ++complete;
  if ((out.visible_edges_mask & 2) && miny > band) ++complete;
  if ((out.visible_edges_mask & 4) && maxx < rw - 1 - band) ++complete;
  if ((out.visible_edges_mask & 8) && maxy < rh - 1 - band) ++complete;
  out.four_sides_complete = (complete == 4) && fill_ratio >= 0.35f;

  out.confidence = std::clamp(0.35f * fill_ratio + 0.15f * complete +
                                  0.1f * (out.boundary_support[0] + out.boundary_support[1] +
                                          out.boundary_support[2] + out.boundary_support[3]),
                              0.f, 1.f);

  if (fill_ratio < 0.08f) {
    out.ambiguous = true;
    std::snprintf(out.ambiguity_reason, sizeof(out.ambiguity_reason), "low fill ratio");
  }
  return out;
}

}  // namespace sct
