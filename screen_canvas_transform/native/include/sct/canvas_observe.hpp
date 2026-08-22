#pragma once

#include "sct/types.hpp"
#include "wb/types.hpp"

namespace sct {

CanvasObservation ObserveCanvasExcludingBackground(
    const uint8_t* bgra, int width, int height, int stride, const wb::IntRect& roi_capture,
    int origin_x, int origin_y, const wb::BackgroundModel& model, float dpi_scale = 1.f);

}  // namespace sct
