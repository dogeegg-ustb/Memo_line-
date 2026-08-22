#pragma once

#include "wb/config.hpp"
#include "wb/types.hpp"

namespace wb {

class WorkspaceBorderDetector {
 public:
  explicit WorkspaceBorderDetector(DetectorConfig cfg = {}) : cfg_(std::move(cfg)) {}

  DetectionOutput Detect(const DetectionInput& in) const;

  /* Skip seed/model re-estimation; force C-II hypotheses inside search_roi. */
  DetectionOutput DetectCiiWithExternalBackground(const DetectionInput& in,
                                                  const BackgroundModel& model) const;

 private:
  DetectorConfig cfg_;
};

DetectionOutput DetectWorkspace(const DetectionInput& in, const DetectorConfig* cfg = nullptr);

DetectionOutput DetectNavigatorThumbnailCii(const DetectionInput& in, const BackgroundModel& model,
                                            const DetectorConfig* cfg = nullptr);

}  // namespace wb
