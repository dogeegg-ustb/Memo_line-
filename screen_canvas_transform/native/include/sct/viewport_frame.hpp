#pragma once

#include "sct/types.hpp"
#include "wb/types.hpp"

namespace sct {

// Detect / complete NavigatorViewportFrame inside NavigatorThumbnailRoi.
//
// 强约束流水线（红框成组契约）：
//   A) 色稳定向红段观测（窄色域 + 固定朝向；朝向可为任意角，不要求贴屏幕轴）
//   B) 共线合并后按相交/平行邻接连通分量成组（一条边默认只进一组；禁止子集枚举多假设）
//   C) 组内邻边直角标注完整边（禁止掩膜 stub 作为 complete 充分条件）
//   D) 证据足够的组按 ViewportCompletionPattern 补全
//   E) if-else 硬消歧：多组时仅用显示画布形状 + 窄红色度（禁止打分排序）
//   F) 仅发布目标组的 NavigatorViewportFrame 与 CompleteEdge
//
// 几何约束：组内边彼此平行或垂直（矩形），MUST NOT 要求与屏幕坐标轴垂直。
// Red stroke: chroma gate + dilate for continuity; geometry from raw-mask CoM.
struct ViewportCompletionInput {
  const uint8_t* bgra = nullptr;
  int width = 0;
  int height = 0;
  int stride = 0;
  wb::IntRect thumbnail_roi{};
  wb::IntRect navigator_canvas_bounds{};
  WorkspaceCanvasRelation workspace_canvas_relation{};
  float dpi_scale = 1.f;
  // 显示角：仅用于切割对应路径的逆向旋转加速（不得单独判边）。
  float display_rotation_degrees = 0.f;
  float display_rotation_confidence = 0.f;
};

struct ViewportCompletionResult {
  FailStatus status = FailStatus::Ok;
  NavigatorViewportFrame frame{};
  char message[128] = {};
  bool used_crop_correspondence = false;
};

ViewportCompletionResult CompleteViewportFrame(const ViewportCompletionInput& in);

}  // namespace sct
