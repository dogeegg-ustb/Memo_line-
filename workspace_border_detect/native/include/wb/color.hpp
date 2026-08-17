#pragma once

#include "wb/image.hpp"
#include "wb/types.hpp"

namespace wb {

Lab BgrToLab(uint8_t b, uint8_t g, uint8_t r);
Lab RgbToLab(uint8_t r, uint8_t g, uint8_t b);

float DeltaE76(const Lab& a, const Lab& b);
float DeltaE76(const float* lab3, const Lab& center);

void BgrImageToLab(const ImageBGR& bgr, ImageF32x3& lab_out);
void BgrImageToGray(const ImageBGR& bgr, ImageF32& gray_out);

}  // namespace wb
