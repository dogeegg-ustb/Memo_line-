#pragma once

#include "wb/config.hpp"
#include "wb/features.hpp"
#include "wb/image.hpp"
#include "wb/types.hpp"

#include <map>
#include <string>

namespace wb {

struct ValidateResult {
  bool ok = false;
  float confidence = 0;
  std::map<std::string, float> metrics;
};

ValidateResult ValidateRectangle(const IntRect& rect, const Hypothesis& hyp,
                                 const FeatureMaps& features, const BackgroundModel& model,
                                 const ImageU8* grown_mask, const DetectorConfig& cfg);

}  // namespace wb
