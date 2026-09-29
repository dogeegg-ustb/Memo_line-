#pragma once

#include "wb/config.hpp"
#include "wb/features.hpp"
#include "wb/image.hpp"
#include "wb/types.hpp"

namespace wb {

struct BackgroundSimilarity {
  ImageF32 similarity;
  ImageU8 strong_mask;
  ImageU8 weak_mask;
  ImageU8 barrier_mask;
};

BackgroundSimilarity BuildSimilarity(const FeatureMaps& features, const BackgroundModel& model,
                                     const IntRect& search_roi, const DetectorConfig& cfg);

}  // namespace wb
