#pragma once

#include "wb/config.hpp"
#include "wb/types.hpp"

#include <vector>

namespace wb {

std::vector<BackgroundModel> EstimateBackgroundModels(const std::vector<SeedPatch>& seeds,
                                                      const DetectorConfig& cfg,
                                                      const IntRect* user_roi);

}  // namespace wb
