#pragma once

#include "sct/types.hpp"
#include "wb/types.hpp"

namespace sct {

// Detect / complete NavigatorViewportFrame inside NavigatorThumbnailRoi.
struct ViewportCompletionInput {
  const uint8_t* bgra = nullptr;
  int width = 0;
  int height = 0;
  int stride = 0;
  wb::IntRect thumbnail_roi{};
  wb::IntRect navigator_canvas_bounds{};
  WorkspaceCanvasRelation workspace_canvas_relation{};
  float dpi_scale = 1.f;
};

struct ViewportCompletionResult {
  FailStatus status = FailStatus::Ok;
  NavigatorViewportFrame frame{};
  char message[128] = {};
};

ViewportCompletionResult CompleteViewportFrame(const ViewportCompletionInput& in);

}  // namespace sct
