#pragma once

#include "wb/config.hpp"
#include "wb/types.hpp"

namespace wb {

class WorkspaceBorderDetector {
 public:
  explicit WorkspaceBorderDetector(DetectorConfig cfg = {}) : cfg_(std::move(cfg)) {}

  DetectionOutput Detect(const DetectionInput& in) const;

 private:
  DetectorConfig cfg_;
};

DetectionOutput DetectWorkspace(const DetectionInput& in, const DetectorConfig* cfg = nullptr);

}  // namespace wb
