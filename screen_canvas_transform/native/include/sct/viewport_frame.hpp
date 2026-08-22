#pragma once

#include "sct/types.hpp"
#include "wb/types.hpp"

namespace sct {

// Detect / complete NavigatorViewportFrame inside NavigatorThumbnailRoi.
// Uses red-channel evidence; completes o_v / a_x / a_y for 4/3/2/1 full right-angle edges.
struct ViewportCompletionInput {
  const uint8_t* bgra = nullptr;
  int width = 0;
  int height = 0;
  int stride = 0;
  wb::IntRect thumbnail_roi{};
  wb::IntRect navigator_canvas_bounds{};
  float workspace_aspect = 1.f;  // W_w / W_h
  float dpi_scale = 1.f;
};

struct ViewportCompletionResult {
  FailStatus status = FailStatus::Ok;
  NavigatorViewportFrame frame{};
  char message[128] = {};
};

ViewportCompletionResult CompleteViewportFrame(const ViewportCompletionInput& in);

}  // namespace sct
