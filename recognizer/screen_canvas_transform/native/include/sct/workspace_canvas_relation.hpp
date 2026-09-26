#pragma once

#include "sct/types.hpp"
#include "wb/types.hpp"

namespace sct {

// Build full canvas geometry model from workspace observation + user canvas pixel size.
struct WorkspaceCanvasRelationInput {
  wb::IntRect workspace_roi_screen{};
  CanvasObservation workspace_canvas{};
  int canvas_pixel_width = 0;
  int canvas_pixel_height = 0;
  const char* capture_id = nullptr;
};

struct WorkspaceCanvasRelationResult {
  FailStatus status = FailStatus::Ok;
  WorkspaceCanvasRelation relation{};
  char message[128] = {};
};

WorkspaceCanvasRelationResult BuildWorkspaceCanvasRelation(const WorkspaceCanvasRelationInput& in);

}  // namespace sct
