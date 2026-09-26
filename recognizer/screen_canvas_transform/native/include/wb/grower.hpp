#pragma once

#include "wb/config.hpp"
#include "wb/image.hpp"
#include "wb/similarity.hpp"
#include "wb/types.hpp"

#include <vector>

namespace wb {

struct GrownBackground {
  ImageU8 mask;          // 0/1
  ImageU8 source_label;  // 0=none, 1..4 = sides
  int pixel_count = 0;
  float bbox_fill_ratio = 1.f;
  float hole_score = 0.f;
  float touches_capture_border = 0.f;
  IntRect bbox{};
};

GrownBackground* GrowBackground(const std::vector<SeedPatch>& seeds,
                                const std::vector<int>& model_seed_ids,
                                const BackgroundSimilarity& similarity, const IntRect& grow_roi,
                                const DetectorConfig& cfg, GrownBackground& out);

// Hard gate: reject solid canvas-like blobs before hypothesis building.
bool IsWorkspaceBackgroundModel(const GrownBackground& grown, const BackgroundModel& model,
                                const DetectorConfig& cfg);

}  // namespace wb
