#pragma once

#include "wb/config.hpp"
#include "wb/features.hpp"
#include "wb/types.hpp"

namespace wb {

/// Refine coarse workspace rectangle on original-resolution features.
/// Returns pointer to `out` on success, nullptr on failure.
IntRect* RefineRectangle(const IntRect& coarse, const FeatureMaps& features,
                         const BackgroundModel& model, const DetectorConfig& cfg, float dpi_scale,
                         IntRect& out);

}  // namespace wb
