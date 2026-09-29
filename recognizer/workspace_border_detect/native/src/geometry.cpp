#include "wb/geometry.hpp"

#include <algorithm>
#include <cmath>
#include <map>
#include <vector>

namespace wb {
namespace {

struct BoundPt {
  int coord = 0;
  int along = 0;
};

bool IsBg(const ImageU8& mask, int x, int y) {
  return x >= 0 && y >= 0 && x < mask.width && y < mask.height && mask.At(x, y) != 0;
}

// True workspace outer: looking further outward from this bg edge does not re-enter
// the same grown background (that would mean we faced a canvas/hole).
// Also require the edge sits on the extreme outer rim of grown.bbox for that side.
bool IsOuterFacingPoint(const GrownBackground& grown, OuterSide side, int coord, int along) {
  const auto& mask = grown.mask;
  const IntRect& bb = grown.bbox;
  if (!bb.valid()) return false;

  const int band = std::max(3, std::min(bb.width(), bb.height()) / 20);

  if (side == OuterSide::Right) {
    // Re-entering bg to the right ⇒ canvas inner edge.
    for (int x = coord; x < std::min(mask.width, bb.right + band); ++x) {
      if (IsBg(mask, x, along)) return false;
    }
    // Must be near the right extremity of grown bbox (not mid-hole face).
    return coord >= bb.right - band;
  }
  if (side == OuterSide::Left) {
    for (int x = coord - 1; x >= std::max(0, bb.left - band); --x) {
      if (IsBg(mask, x, along)) return false;
    }
    return coord <= bb.left + band;
  }
  if (side == OuterSide::Bottom) {
    for (int y = coord; y < std::min(mask.height, bb.bottom + band); ++y) {
      if (IsBg(mask, along, y)) return false;
    }
    return coord >= bb.bottom - band;
  }
  // Top
  for (int y = coord - 1; y >= std::max(0, bb.top - band); --y) {
    if (IsBg(mask, along, y)) return false;
  }
  return coord <= bb.top + band;
}

std::vector<BoundPt> CollectBoundaryPoints(const GrownBackground& grown, OuterSide side,
                                           const IntRect& scan) {
  std::vector<BoundPt> pts;
  const auto& mask = grown.mask;
  const int x0 = scan.left;
  const int x1 = scan.right;
  const int y0 = scan.top;
  const int y1 = scan.bottom;

  if (side == OuterSide::Left) {
    for (int y = y0; y < y1; ++y) {
      for (int x = x0; x < x1; ++x) {
        if (!IsBg(mask, x, y)) continue;
        if (!IsBg(mask, x - 1, y)) {
          pts.push_back({x, y});
          break;
        }
      }
    }
  } else if (side == OuterSide::Right) {
    for (int y = y0; y < y1; ++y) {
      for (int x = x1 - 1; x >= x0; --x) {
        if (!IsBg(mask, x, y)) continue;
        if (!IsBg(mask, x + 1, y)) {
          pts.push_back({x + 1, y});
          break;
        }
      }
    }
  } else if (side == OuterSide::Top) {
    for (int x = x0; x < x1; ++x) {
      for (int y = y0; y < y1; ++y) {
        if (!IsBg(mask, x, y)) continue;
        if (!IsBg(mask, x, y - 1)) {
          pts.push_back({y, x});
          break;
        }
      }
    }
  } else {
    for (int x = x0; x < x1; ++x) {
      for (int y = y1 - 1; y >= y0; --y) {
        if (!IsBg(mask, x, y)) continue;
        if (!IsBg(mask, x, y + 1)) {
          pts.push_back({y + 1, x});
          break;
        }
      }
    }
  }
  return pts;
}

int PercentileCoord(std::vector<int> coords, float pct) {
  if (coords.empty()) return 0;
  pct = std::max(0.f, std::min(100.f, pct));
  const size_t i =
      static_cast<size_t>(std::round((pct / 100.f) * static_cast<float>(coords.size() - 1)));
  std::nth_element(coords.begin(), coords.begin() + static_cast<std::ptrdiff_t>(i), coords.end());
  return coords[i];
}

SideSegment FitSide(OuterSide side, const std::vector<BoundPt>& pts_all,
                    const GrownBackground& grown, const IntRect& capture_bounds,
                    const DetectorConfig& cfg, int short_side) {
  SideSegment seg;
  seg.side = side;
  if (pts_all.empty()) return seg;

  // Prefer workspace-outer facing points; fall back only for diagnostics.
  std::vector<BoundPt> outer_pts;
  std::vector<BoundPt> inner_pts;
  outer_pts.reserve(pts_all.size());
  for (const auto& p : pts_all) {
    if (IsOuterFacingPoint(grown, side, p.coord, p.along))
      outer_pts.push_back(p);
    else
      inner_pts.push_back(p);
  }
  const bool use_outer = !outer_pts.empty();
  const std::vector<BoundPt>& pts = use_outer ? outer_pts : inner_pts;
  seg.is_workspace_outer = use_outer;

  std::vector<int> coords;
  coords.reserve(pts.size());
  for (const auto& p : pts) coords.push_back(p.coord);

  // Extreme rim: Left/Top → low percentile; Right/Bottom → high. Avoids median pulled
  // toward canvas-inner jagged edges when a few outer samples remain.
  const bool high_extreme = (side == OuterSide::Right || side == OuterSide::Bottom);
  const int extreme = PercentileCoord(coords, high_extreme ? 90.f : 10.f);

  int mad_acc = 0;
  for (int c : coords) mad_acc += std::abs(c - extreme);
  const float mad = mad_acc / static_cast<float>(coords.size());

  std::vector<BoundPt> kept;
  const int tol = std::max(2, static_cast<int>(std::round(mad * 2.5f + 2.f)));
  for (const auto& p : pts) {
    if (std::abs(p.coord - extreme) <= tol) kept.push_back(p);
  }
  if (kept.empty()) kept = pts;

  int along_lo = kept.front().along;
  int along_hi = kept.front().along;
  for (const auto& p : kept) {
    along_lo = std::min(along_lo, p.along);
    along_hi = std::max(along_hi, p.along);
  }

  const bool vertical = (side == OuterSide::Left || side == OuterSide::Right);
  const int gspan = grown.bbox.valid()
                        ? (vertical ? grown.bbox.height() : grown.bbox.width())
                        : (vertical ? capture_bounds.height() : capture_bounds.width());
  const float coverage =
      gspan > 0 ? static_cast<float>(along_hi - along_lo + 1) / static_cast<float>(gspan) : 0.f;

  const int band = cfg.SafetyBandPx(1.f, short_side);
  bool truncated = false;
  if (vertical) {
    if (along_lo <= capture_bounds.top + band || along_hi >= capture_bounds.bottom - 1 - band)
      truncated = true;
  } else {
    if (along_lo <= capture_bounds.left + band || along_hi >= capture_bounds.right - 1 - band)
      truncated = true;
  }

  seg.coord = extreme;
  seg.run_start = along_lo;
  seg.run_end = along_hi + 1;
  seg.coverage = std::max(0.f, std::min(1.f, coverage));
  seg.coordinate_mad = mad;
  seg.bg_toward_interior = true;
  seg.truncated = truncated;
  seg.outside_score = std::max(0.f, std::min(1.f, 0.55f + 0.35f * seg.coverage));
  seg.transition_score =
      std::max(0.f, std::min(1.f, 0.5f + 0.4f * (1.f - std::min(mad, 6.f) / 6.f)));
  seg.endpoint_score_start = truncated ? 0.2f : 0.75f;
  seg.endpoint_score_end = truncated ? 0.2f : 0.75f;
  return seg;
}

BandCandidate MakeBand(bool vertical, int outer, int inner, int lo, int hi, int cid, bool trunc) {
  BandCandidate b;
  b.vertical = vertical;
  b.outer_coord = outer;
  b.inner_coord = inner;
  b.endpoint_lo = lo;
  b.endpoint_hi = hi;
  b.length = static_cast<float>(std::max(0, hi - lo));
  b.component_id = cid;
  b.truncated = trunc;
  return b;
}

bool RectContainsRect(const IntRect& outer, const IntRect& inner, float min_frac) {
  if (!outer.valid() || !inner.valid()) return false;
  const int x0 = std::max(outer.left, inner.left);
  const int y0 = std::max(outer.top, inner.top);
  const int x1 = std::min(outer.right, inner.right);
  const int y1 = std::min(outer.bottom, inner.bottom);
  const int inter = std::max(0, x1 - x0) * std::max(0, y1 - y0);
  const int area = std::max(1, inner.area());
  return static_cast<float>(inter) / static_cast<float>(area) >= min_frac;
}

}  // namespace

GeometryEvidence ExtractGeometry(const GrownBackground& grown, const IntRect& capture_bounds,
                                 const DetectorConfig& cfg) {
  GeometryEvidence geo;
  if (grown.pixel_count < 16 || !grown.bbox.valid()) return geo;

  const int short_side = std::min(capture_bounds.width(), capture_bounds.height());
  IntRect scan = grown.bbox.Expand(2, 2, 0).Clamp(capture_bounds.right, capture_bounds.bottom);
  if (!scan.valid()) scan = capture_bounds;

  for (OuterSide side :
       {OuterSide::Left, OuterSide::Top, OuterSide::Right, OuterSide::Bottom}) {
    auto pts = CollectBoundaryPoints(grown, side, scan);
    auto seg = FitSide(side, pts, grown, capture_bounds, cfg, short_side);
    if (seg.coverage < cfg.min_side_coverage * 0.5f) continue;
    if (seg.is_workspace_outer)
      geo.outer_sides.push_back(seg);
    else
      geo.inner_sides.push_back(seg);
  }

  // Interior non-bg hole within grown bbox (canvas).
  {
    int hx0 = grown.bbox.right, hx1 = grown.bbox.left;
    int hy0 = grown.bbox.bottom, hy1 = grown.bbox.top;
    bool any = false;
    for (int y = grown.bbox.top; y < grown.bbox.bottom; ++y) {
      for (int x = grown.bbox.left; x < grown.bbox.right; ++x) {
        if (grown.mask.At(x, y)) continue;
        any = true;
        hx0 = std::min(hx0, x);
        hx1 = std::max(hx1, x + 1);
        hy0 = std::min(hy0, y);
        hy1 = std::max(hy1, y + 1);
      }
    }
    if (any && hx1 > hx0 + 4 && hy1 > hy0 + 4) geo.holes.push_back({hx0, hy0, hx1, hy1});
  }

  std::map<OuterSide, SideSegment> by;
  for (const auto& s : geo.outer_sides) by[s.side] = s;

  auto try_pair = [&](OuterSide a, OuterSide b, bool vertical) {
    if (!by.count(a) || !by.count(b)) return;
    const auto& sa = by[a];
    const auto& sb = by[b];
    if (sa.truncated || sb.truncated) return;
    if (sa.coverage < cfg.min_side_coverage || sb.coverage < cfg.min_side_coverage) return;
    const int lo = std::max(sa.run_start, sb.run_start);
    const int hi = std::min(sa.run_end, sb.run_end);
    if (hi - lo < short_side * cfg.min_band_length_ratio) return;
    geo.bands.push_back(MakeBand(vertical, sa.coord, sb.coord, lo, hi, 0, false));
    geo.bands.push_back(MakeBand(vertical, sb.coord, sa.coord, lo, hi, 0, false));
  };
  try_pair(OuterSide::Left, OuterSide::Right, true);
  try_pair(OuterSide::Top, OuterSide::Bottom, false);

  auto try_l = [&](OuterSide vert, OuterSide horz) {
    if (!by.count(vert) || !by.count(horz)) return;
    const auto& sv = by[vert];
    const auto& sh = by[horz];
    if (sv.truncated || sh.truncated) return;
    if (sv.coverage < cfg.min_side_coverage || sh.coverage < cfg.min_side_coverage) return;
    LShapeCandidate L;
    L.arm_a = vert;
    L.arm_b = horz;
    L.shared_x = (vert == OuterSide::Left || vert == OuterSide::Right) ? sv.coord : sh.coord;
    L.shared_y = (horz == OuterSide::Top || horz == OuterSide::Bottom) ? sh.coord : sv.coord;
    if (vert == OuterSide::Left) {
      L.shared_x = sv.coord;
      if (horz == OuterSide::Top) {
        L.shared_y = sh.coord;
        L.far_y = sv.run_end;
        L.far_x = sh.run_end;
      } else {
        L.shared_y = sh.coord;
        L.far_y = sv.run_start;
        L.far_x = sh.run_end;
      }
    } else if (vert == OuterSide::Right) {
      L.shared_x = sv.coord;
      if (horz == OuterSide::Top) {
        L.shared_y = sh.coord;
        L.far_y = sv.run_end;
        L.far_x = sh.run_start;
      } else {
        L.shared_y = sh.coord;
        L.far_y = sv.run_start;
        L.far_x = sh.run_start;
      }
    }
    L.truncated = false;
    L.component_id = 0;
    if (std::abs(L.far_x - L.shared_x) > 8 && std::abs(L.far_y - L.shared_y) > 8)
      geo.l_shapes.push_back(L);
  };
  try_l(OuterSide::Left, OuterSide::Top);
  try_l(OuterSide::Left, OuterSide::Bottom);
  try_l(OuterSide::Right, OuterSide::Top);
  try_l(OuterSide::Right, OuterSide::Bottom);

  float cov = 0;
  for (const auto& s : geo.outer_sides) cov += s.coverage;
  geo.coverage = geo.outer_sides.empty() ? 0.f : cov / geo.outer_sides.size();
  return geo;
}

std::vector<Hypothesis> BuildHypotheses(const GeometryEvidence& geo, const BackgroundModel& model,
                                        int model_index, const GrownBackground& grown,
                                        const IntRect& capture_bounds, const DetectorConfig& cfg) {
  (void)model;
  std::vector<Hypothesis> hyps;
  std::map<OuterSide, SideSegment> by;
  for (const auto& s : geo.outer_sides) {
    if (!s.is_workspace_outer) continue;
    by[s.side] = s;
  }

  auto covers_workspace = [&](const IntRect& r) -> bool {
    if (!r.valid()) return false;
    // Workspace rect must contain the grown background bbox (canvas hole is inside bbox).
    if (grown.bbox.valid() && !RectContainsRect(r, grown.bbox, 0.92f)) return false;
    for (const auto& hole : geo.holes) {
      if (!RectContainsRect(r, hole, 0.90f)) return false;
    }
    return true;
  };

  auto push_if_valid = [&](Hypothesis h) {
    if (!h.rect.valid()) return;
    if (h.rect.width() < cfg.min_roi_size_px || h.rect.height() < cfg.min_roi_size_px) return;
    h.rect = h.rect.Clamp(capture_bounds.right, capture_bounds.bottom);
    if (!h.rect.valid()) return;
    if (!capture_bounds.IntersectsInterior(h.rect)) return;
    if (!covers_workspace(h.rect)) return;
    h.model_index = model_index;
    hyps.push_back(std::move(h));
  };

  // Path A: four workspace-outer sides.
  if (by.count(OuterSide::Left) && by.count(OuterSide::Right) && by.count(OuterSide::Top) &&
      by.count(OuterSide::Bottom)) {
    Hypothesis h;
    h.grade = EvidenceGrade::A;
    h.rect = {by[OuterSide::Left].coord, by[OuterSide::Top].coord, by[OuterSide::Right].coord,
              by[OuterSide::Bottom].coord};
    h.observed_sides = {OuterSide::Left, OuterSide::Top, OuterSide::Right, OuterSide::Bottom};
    h.endpoints_truncated = by[OuterSide::Left].truncated || by[OuterSide::Right].truncated ||
                            by[OuterSide::Top].truncated || by[OuterSide::Bottom].truncated;
    push_if_valid(std::move(h));
  }

  // Path B: three outer sides; close missing from grown.bbox (includes hole).
  const OuterSide sides[4] = {OuterSide::Left, OuterSide::Top, OuterSide::Right, OuterSide::Bottom};
  for (int miss = 0; miss < 4; ++miss) {
    bool ok = true;
    for (int i = 0; i < 4; ++i) {
      if (i == miss) continue;
      if (!by.count(sides[i])) {
        ok = false;
        break;
      }
    }
    if (!ok || !grown.bbox.valid()) continue;

    Hypothesis h;
    h.grade = EvidenceGrade::B;
    int L = by.count(OuterSide::Left) ? by[OuterSide::Left].coord : grown.bbox.left;
    int T = by.count(OuterSide::Top) ? by[OuterSide::Top].coord : grown.bbox.top;
    int R = by.count(OuterSide::Right) ? by[OuterSide::Right].coord : grown.bbox.right;
    int B = by.count(OuterSide::Bottom) ? by[OuterSide::Bottom].coord : grown.bbox.bottom;
    if (miss == 0) L = grown.bbox.left;
    if (miss == 1) T = grown.bbox.top;
    if (miss == 2) R = grown.bbox.right;
    if (miss == 3) B = grown.bbox.bottom;

    const int band = cfg.SafetyBandPx(1.f, std::min(capture_bounds.width(), capture_bounds.height()));
    bool closed_on_capture_edge = false;
    if (miss == 0 && L <= capture_bounds.left + band) closed_on_capture_edge = true;
    if (miss == 1 && T <= capture_bounds.top + band) closed_on_capture_edge = true;
    if (miss == 2 && R >= capture_bounds.right - band) closed_on_capture_edge = true;
    if (miss == 3 && B >= capture_bounds.bottom - band) closed_on_capture_edge = true;
    if (closed_on_capture_edge) continue;

    h.rect = {L, T, R, B};
    for (int i = 0; i < 4; ++i) {
      if (i == miss)
        h.closed_sides.push_back(sides[i]);
      else
        h.observed_sides.push_back(sides[i]);
    }
    bool trunc = false;
    for (OuterSide s : h.observed_sides)
      if (by[s].truncated) trunc = true;
    h.endpoints_truncated = trunc;
    push_if_valid(std::move(h));
  }

  // Path C-L
  for (const auto& L : geo.l_shapes) {
    Hypothesis h;
    h.grade = EvidenceGrade::C_L;
    const int left = std::min(L.shared_x, L.far_x);
    const int right = std::max(L.shared_x, L.far_x);
    const int top = std::min(L.shared_y, L.far_y);
    const int bottom = std::max(L.shared_y, L.far_y);
    h.rect = {left, top, right, bottom};
    h.observed_sides = {L.arm_a, L.arm_b};
    h.endpoints_truncated = L.truncated;
    push_if_valid(std::move(h));
  }

  // Path C-II
  if (by.count(OuterSide::Left) && by.count(OuterSide::Right)) {
    const auto& L = by[OuterSide::Left];
    const auto& R = by[OuterSide::Right];
    if (!L.truncated && !R.truncated && L.coverage >= cfg.min_side_coverage &&
        R.coverage >= cfg.min_side_coverage) {
      Hypothesis h;
      h.grade = EvidenceGrade::C_II;
      const int top = std::min(L.run_start, R.run_start);
      const int bottom = std::max(L.run_end, R.run_end);
      h.rect = {L.coord, top, R.coord, bottom};
      h.observed_sides = {OuterSide::Left, OuterSide::Right};
      push_if_valid(std::move(h));
    }
  }
  if (by.count(OuterSide::Top) && by.count(OuterSide::Bottom)) {
    const auto& T = by[OuterSide::Top];
    const auto& B = by[OuterSide::Bottom];
    if (!T.truncated && !B.truncated && T.coverage >= cfg.min_side_coverage &&
        B.coverage >= cfg.min_side_coverage) {
      Hypothesis h;
      h.grade = EvidenceGrade::C_II;
      const int left = std::min(T.run_start, B.run_start);
      const int right = std::max(T.run_end, B.run_end);
      h.rect = {left, T.coord, right, B.coord};
      h.observed_sides = {OuterSide::Top, OuterSide::Bottom};
      push_if_valid(std::move(h));
    }
  }

  return hyps;
}

}  // namespace wb
