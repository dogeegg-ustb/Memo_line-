#pragma once

#include "sct/types.hpp"
#include "wb/types.hpp"

namespace sct {

// Detect / complete NavigatorViewportFrame inside NavigatorThumbnailRoi.
//
// 强约束流水线（红框成组契约）：
//   A) 色稳轴向红段观测（连续性 + 方向 + 支撑率）
//   B) 按几何约束 + 空间相近枚举合法 RedFrameEdgeGroup
//   C) 组内 GroupRightAngle 标注完整边（禁止掩膜 stub 作为 complete 充分条件）
//   D) 证据足够的组按 ViewportCompletionPattern 补全
//   E) if-else 硬消歧选出唯一目标组（禁止打分排序）
//   F) 仅发布目标组的 NavigatorViewportFrame 与 CompleteEdge
//
// Red stroke: chroma gate + dilate for segment continuity; geometry from raw-mask CoM.
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
