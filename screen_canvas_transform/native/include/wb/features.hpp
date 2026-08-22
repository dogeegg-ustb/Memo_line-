#pragma once

#include "wb/config.hpp"
#include "wb/image.hpp"
#include "wb/types.hpp"

namespace wb {

struct FeatureMaps {
  ImageF32x3 lab;
  ImageF32 gray;
  ImageF32 gradient_x;
  ImageF32 gradient_y;
  ImageF32 gradient_magnitude;
  ImageF32 local_variance;
  int width = 0;
  int height = 0;
  float scale_to_capture = 1.f;
};

FeatureMaps ExtractFeatures(const ImageBGR& bgr, const DetectorConfig& cfg, float dpi_scale,
                            const IntRect* roi_hint);

ImageBGR DownsampleBgrBilinear(const ImageBGR& src, float scale);
IntRect ScaleRect(const IntRect& r, float scale);
IntRect UnscaleRectFloorCeil(const IntRect& r, float scale);

}  // namespace wb
