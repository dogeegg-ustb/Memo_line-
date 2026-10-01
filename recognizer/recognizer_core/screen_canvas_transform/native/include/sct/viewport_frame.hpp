#pragma once

#include "sct/types.hpp"
#include "wb/types.hpp"

namespace sct {

// Detect / complete NavigatorViewportFrame inside NavigatorThumbnailRoi.
//
// 强约束流水线（红框成组契约）：
//   A) 窄红核心投票；跟踪真实像素拟合直线，拒绝弯曲/漂移，保留抗锯齿轮廓
//   B) 共线合并及真实端点引导复查弱边后提取四角闭合环；剩余观测成组，保留未选证据
//   C) 组内邻边直角标注完整边（禁止掩膜 stub 作为 complete 充分条件）
//   D) 完整边使用整个实测长度；0.1 单边、0.2 邻边、0.3 对边各自恢复长度再拼框
//   E) 工作区形状校验后优先有完整边的组；0.x 碎片作后备，同等实测证据仍判歧义
//   F) 仅发布目标组的 NavigatorViewportFrame 与 CompleteEdge
//
// 几何约束：组内边彼此平行或垂直（矩形），MUST NOT 要求与屏幕坐标轴垂直。
// Red stroke: strict core votes for line direction; local coverage profiles
// grow its visible span. Geometry later rejects implausible line groups.
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
