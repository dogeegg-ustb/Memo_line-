#pragma once

#include "wb/config.hpp"
#include "wb/features.hpp"
#include "wb/types.hpp"

#include <vector>

namespace wb {

std::vector<SeedPatch> SampleBackgroundSeeds(const FeatureMaps& features, const IntRect& user_roi,
                                             const DetectorConfig& cfg, float dpi_scale);

}  // namespace wb
