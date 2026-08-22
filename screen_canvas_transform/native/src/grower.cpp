#include "wb/grower.hpp"

#include <algorithm>
#include <queue>
#include <unordered_set>

namespace wb {
namespace {

bool ContainsId(const std::vector<int>& ids, int id) {
  return std::find(ids.begin(), ids.end(), id) != ids.end();
}

void BridgeGaps(ImageU8& growable, const ImageU8& weak, const ImageU8& strong, const IntRect& roi,
                int gap) {
  if (gap <= 0) return;
  const int x0 = roi.left, x1 = roi.right, y0 = roi.top, y1 = roi.bottom;
  ImageU8 dilated;
  dilated.Allocate(growable.width, growable.height, 0);
  for (int y = y0; y < y1; ++y) {
    for (int x = x0; x < x1; ++x) {
      if (!growable.At(x, y)) continue;
      for (int dy = -gap; dy <= gap; ++dy) {
        for (int dx = -gap; dx <= gap; ++dx) {
          const int xx = x + dx;
          const int yy = y + dy;
          if (xx < x0 || xx >= x1 || yy < y0 || yy >= y1) continue;
          dilated.At(xx, yy) = 1;
        }
      }
    }
  }
  for (int y = y0; y < y1; ++y) {
    for (int x = x0; x < x1; ++x) {
      const bool keep = dilated.At(x, y) && (weak.At(x, y) || strong.At(x, y));
      growable.At(x, y) = keep ? 1 : 0;
    }
  }
}

}  // namespace

GrownBackground* GrowBackground(const std::vector<SeedPatch>& seeds,
                                const std::vector<int>& model_seed_ids,
                                const BackgroundSimilarity& similarity, const IntRect& grow_roi,
                                const DetectorConfig& cfg, GrownBackground& out) {
  const int h = similarity.similarity.height;
  const int w = similarity.similarity.width;
  const int x0 = grow_roi.left;
  const int x1 = grow_roi.right;
  const int y0 = grow_roi.top;
  const int y1 = grow_roi.bottom;
  if (x1 <= x0 + 2 || y1 <= y0 + 2) return nullptr;

  std::vector<const SeedPatch*> start_seeds;
  for (const auto& s : seeds) {
    if (s.accepted && ContainsId(model_seed_ids, s.seed_id) && grow_roi.ContainsPoint(s.x, s.y))
      start_seeds.push_back(&s);
  }
  if (start_seeds.empty()) {
    for (const auto& s : seeds) {
      if (ContainsId(model_seed_ids, s.seed_id) && grow_roi.ContainsPoint(s.x, s.y))
        start_seeds.push_back(&s);
    }
  }
  if (start_seeds.empty()) return nullptr;

  ImageU8 growable;
  growable.Allocate(w, h, 0);
  for (int y = y0; y < y1; ++y) {
    for (int x = x0; x < x1; ++x) {
      // Keep weak/strong background pixels growable even on gradient ridges so the
      // outer rim (bg↔UI transition) is not shaved off. Barriers still stop leakage
      // into non-background neighbors during flood fill below.
      const bool g = similarity.weak_mask.At(x, y) != 0 || similarity.strong_mask.At(x, y) != 0;
      growable.At(x, y) = g ? 1 : 0;
    }
  }
  BridgeGaps(growable, similarity.weak_mask, similarity.strong_mask, grow_roi,
             std::max(0, std::min(cfg.grow_gap_bridge_px, 2)));

  std::vector<const SeedPatch*> valid;
  for (auto* s : start_seeds) {
    if (growable.At(s->x, s->y)) valid.push_back(s);
  }
  if (valid.empty()) return nullptr;

  out.mask.Allocate(w, h, 0);
  out.source_label.Allocate(w, h, 0);
  ImageU8 visited;
  visited.Allocate(w, h, 0);

  // No hard area cap vs full capture: stop via barriers / non-bg. Soft safety only.
  const int roi_area = std::max(1, (x1 - x0) * (y1 - y0));
  const int max_pixels = (cfg.max_grown_fraction > 0.f)
                             ? static_cast<int>(roi_area * cfg.max_grown_fraction)
                             : roi_area;
  int count = 0;

  // Grow components from each seed; merge overlapping
  struct Comp {
    int pixels = 0;
    int side_vote[5] = {};
  };
  std::vector<Comp> comps;
  ImageU8 labels;
  labels.Allocate(w, h, 0);
  int next_label = 1;

  auto flood = [&](int sx, int sy, int side_idx) {
    if (!growable.At(sx, sy) || labels.At(sx, sy)) return;
    Comp c;
    std::queue<std::pair<int, int>> q;
    q.push({sx, sy});
    labels.At(sx, sy) = static_cast<uint8_t>(next_label);
    c.side_vote[side_idx]++;
    c.pixels = 1;
    static const int dxs[4] = {1, -1, 0, 0};
    static const int dys[4] = {0, 0, 1, -1};
    while (!q.empty()) {
      const auto p = q.front();
      q.pop();
      for (int k = 0; k < 4; ++k) {
        const int nx = p.first + dxs[k];
        const int ny = p.second + dys[k];
        if (nx < x0 || nx >= x1 || ny < y0 || ny >= y1) continue;
        if (!growable.At(nx, ny) || labels.At(nx, ny)) continue;
        // Do not leak across strong barriers into non-strong background.
        if (similarity.barrier_mask.At(nx, ny) && !similarity.strong_mask.At(nx, ny) &&
            !similarity.weak_mask.At(nx, ny))
          continue;
        labels.At(nx, ny) = static_cast<uint8_t>(next_label);
        ++c.pixels;
        q.push({nx, ny});
      }
    }
    comps.push_back(c);
    ++next_label;
  };

  for (auto* s : valid) {
    flood(s->x, s->y, SideIndex(s->side) + 1);
  }
  if (comps.empty()) return nullptr;

  // Keep seed-touched labels in order of size
  std::vector<int> order(comps.size());
  for (size_t i = 0; i < comps.size(); ++i) order[i] = static_cast<int>(i);
  std::sort(order.begin(), order.end(),
            [&](int a, int b) { return comps[a].pixels > comps[b].pixels; });

  std::unordered_set<int> keep;
  for (int i : order) {
    if (count + comps[i].pixels > max_pixels && count > 0) continue;
    keep.insert(i + 1);
    count += comps[i].pixels;
  }
  if (count < 16) return nullptr;

  int bx0 = w, bx1 = 0, by0 = h, by1 = 0;
  for (int y = y0; y < y1; ++y) {
    for (int x = x0; x < x1; ++x) {
      const int lid = labels.At(x, y);
      if (!lid || !keep.count(lid)) continue;
      out.mask.At(x, y) = 1;
      int maj = 1, majv = -1;
      for (int s = 1; s <= 4; ++s) {
        if (comps[lid - 1].side_vote[s] > majv) {
          majv = comps[lid - 1].side_vote[s];
          maj = s;
        }
      }
      out.source_label.At(x, y) = static_cast<uint8_t>(maj);
      bx0 = std::min(bx0, x);
      bx1 = std::max(bx1, x + 1);
      by0 = std::min(by0, y);
      by1 = std::max(by1, y + 1);
    }
  }

  out.pixel_count = count;
  out.bbox = {bx0, by0, bx1, by1};
  const int bbox_area = std::max(1, (bx1 - bx0) * (by1 - by0));
  out.bbox_fill_ratio = static_cast<float>(count) / static_cast<float>(bbox_area);
  out.hole_score = std::max(0.f, std::min(1.f, 1.f - out.bbox_fill_ratio));

  int border_hits = 0, border_tot = 0;
  for (int y : {y0, y1 - 1}) {
    if (y < 0 || y >= h) continue;
    for (int x = x0; x < x1; ++x) {
      ++border_tot;
      if (out.mask.At(x, y)) ++border_hits;
    }
  }
  for (int x : {x0, x1 - 1}) {
    if (x < 0 || x >= w) continue;
    for (int y = y0; y < y1; ++y) {
      ++border_tot;
      if (out.mask.At(x, y)) ++border_hits;
    }
  }
  out.touches_capture_border = border_hits / static_cast<float>(std::max(border_tot, 1));
  return &out;
}

bool IsWorkspaceBackgroundModel(const GrownBackground& grown, const BackgroundModel& model,
                                const DetectorConfig& cfg) {
  // Solid canvas-like blob: high fill, almost no interior hole.
  if (grown.bbox_fill_ratio >= 0.85f && grown.hole_score < 0.12f) return false;
  if (grown.bbox_fill_ratio > cfg.max_bg_bbox_fill_ratio &&
      grown.hole_score < cfg.min_bg_hole_score) {
    return false;
  }
  // Weak multi-side rectangular support → likely interior canvas color.
  if (model.rectangular_support_score < cfg.min_model_rect_support) return false;

  if (grown.hole_score >= cfg.min_bg_hole_score) return true;
  if (grown.bbox_fill_ratio <= cfg.max_bg_bbox_fill_ratio &&
      grown.touches_capture_border < 0.55f) {
    return true;
  }
  // Touching capture border heavily without a hole looks like leaked UI / solid panel.
  if (grown.touches_capture_border >= cfg.max_bg_capture_border_touch) return false;
  return grown.hole_score >= cfg.min_bg_hole_score * 0.5f;
}

}  // namespace wb
