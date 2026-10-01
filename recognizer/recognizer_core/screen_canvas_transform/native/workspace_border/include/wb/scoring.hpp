#pragma once

#include "wb/config.hpp"
#include "wb/geometry.hpp"
#include "wb/types.hpp"

#include <string>
#include <vector>

namespace wb {

struct SelectResult {
  Hypothesis* best = nullptr;  // points into scored copy storage
  std::string reason;
  float margin = 0;
  std::vector<Hypothesis> ranked;
};

SelectResult SelectBestHypothesis(std::vector<Hypothesis> hyps,
                                  const std::vector<SideSegment>& sides,
                                  const DetectorConfig& cfg);

}  // namespace wb
