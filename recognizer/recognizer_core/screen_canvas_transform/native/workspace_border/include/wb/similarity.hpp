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

// Gradient / variance percentiles over the search ROI. They depend only on the
// features, so callers trying several models on one feature map compute them once.
struct BarrierThresholds {
  float gradient = 0.f;
  float variance = 0.f;
};

BarrierThresholds ComputeBarrierThresholds(const FeatureMaps& features, const IntRect& search_roi,
                                           const DetectorConfig& cfg);

BackgroundSimilarity BuildSimilarity(const FeatureMaps& features, const BackgroundModel& model,
                                     const IntRect& search_roi, const DetectorConfig& cfg,
                                     const BarrierThresholds& barriers);

BackgroundSimilarity BuildSimilarity(const FeatureMaps& features, const BackgroundModel& model,
                                     const IntRect& search_roi, const DetectorConfig& cfg);

}  // namespace wb
