#pragma once

#include "wb/background.hpp"
#include "wb/config.hpp"
#include "wb/grower.hpp"
#include "wb/types.hpp"

#include <vector>

namespace wb {

struct SideSegment {
  OuterSide side = OuterSide::Left;
  int coord = 0;          // x for L/R, y for T/B
  int run_start = 0;
  int run_end = 0;
  float coverage = 0;
  float outside_score = 0;
  float transition_score = 0;
  float endpoint_score_start = 0;
  float endpoint_score_end = 0;
  float coordinate_mad = 0;
  bool bg_toward_interior = true;
  bool is_workspace_outer = false;  // false = canvas/inner edge
  bool truncated = false;
};

struct BandCandidate {
  bool vertical = true;  // left/right bands
  int outer_coord = 0;
  int inner_coord = 0;
  int endpoint_lo = 0;
  int endpoint_hi = 0;
  float length = 0;
  int component_id = -1;
  bool truncated = false;
};

struct LShapeCandidate {
  OuterSide arm_a = OuterSide::Left;  // vertical-ish
  OuterSide arm_b = OuterSide::Top;   // horizontal-ish
  int shared_x = 0;
  int shared_y = 0;
  int far_x = 0;
  int far_y = 0;
  bool truncated = false;
  int component_id = -1;
};

struct GeometryEvidence {
  std::vector<SideSegment> outer_sides;  // workspace outer only
  std::vector<SideSegment> inner_sides;  // bg↔canvas (diagnostic)
  std::vector<BandCandidate> bands;
  std::vector<LShapeCandidate> l_shapes;
  std::vector<IntRect> holes;
  float coverage = 0;
};

GeometryEvidence ExtractGeometry(const GrownBackground& grown, const IntRect& capture_bounds,
                                 const DetectorConfig& cfg);

std::vector<Hypothesis> BuildHypotheses(const GeometryEvidence& geo, const BackgroundModel& model,
                                        int model_index, const GrownBackground& grown,
                                        const IntRect& capture_bounds, const DetectorConfig& cfg);

}  // namespace wb
