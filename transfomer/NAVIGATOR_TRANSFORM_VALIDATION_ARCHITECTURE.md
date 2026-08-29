# 基于 CSP 导航器的坐标转换验证器：最小强约束架构

> 本文定义一个独立的小型验证程序。它只验证一件事：**仅依据工作区、CSP 导航器缩略图、导航器红框、缩放数字、旋转数字和翻转观测所建立的屏幕—画布矩阵，能否在用户平移、缩放、旋转和翻转画布后，持续把画布语义左下角正确投影到屏幕。**
>
> 本验证器不承担正式笔触记录、文件分段、事件重放、虚拟驱动输入或生产级长期会话管理。验证通过后，其算法和接口才进入主架构。

## 1. 唯一目标

验证器建立并持续更新：

```text
ScreenPhysicalPx ↔ WorkspaceLocalPx ↔ CanvasAttachedNormalized
```

它固定选取：

```text
CanvasBottomLeft = (0, 1)
```

通过当前矩阵计算：

\[
p_{marker}=T_{C\rightarrow S}(0,1)
\]

并用最顶层透明覆盖层显示该标记。

用户在 CSP 中执行：

- 画布平移；
- 画布缩放；
- 画布旋转；
- 水平翻转；
- 垂直翻转；
- 上述操作的组合。

如果标记始终附着于同一个画布内容角，则导航器坐标转换达到本验证器的成功标准。

## 2. 非目标

本验证器 MUST NOT 实现：

- 正式笔触持久化；
- `CanvasWorkAreaSegment` 文件格式；
- 历史笔触事件重放；
- 虚拟数位笔驱动输出；
- 通用绘画软件适配；
- 自动操作 CSP；
- 自动点击缩放、旋转或翻转按钮；
- 复杂的 Level 1–4 自动视觉验收；
- 将验证结论直接作为生产重放授权。

允许保存诊断日志、截图摘要和矩阵时间线，但这些只服务于验证器调试。

## 3. 实现边界

推荐分工：

```text
C++ / HLSL
- GPU 桌面局部捕获
- workspace_border_detect GPU 后端
- 工作区背景与画布边界分析
- 导航器缩略图检测
- 导航器红框检测
- 低/高分辨率翻转分析
- OCR 前处理和轻量数字识别
- 权威矩阵构造
- GPU 覆盖图元合成

C#
- ROI 选择流程
- behavior_recognizer 事件订阅
- 视图操作防抖
- 状态机与代次
- 原生请求调度
- TransformSnapshot 原子发布
- 覆盖窗口生命周期
- 诊断 UI 和结果导出
```

C# 与 C++ 之间 MUST 使用稳定 C ABI，只传请求、ROI、代次和小型结构化结果。禁止传递完整 CPU 图像或逐像素数组。

## 4. 最小输入

### 4.1 用户输入

用户必须在同一初始化截图中依次框选：

```text
WorkspaceUserRoi
NavigatorPanelRoi
```

其中：

- `WorkspaceUserRoi` 是主工作区粗选范围；
- `NavigatorPanelRoi` 是完整 CSP 导航器面板；
- `NavigatorPanelRoi` 直接采用用户框选结果；
- 导航器缩略图必须在面板内部另行检测。

### 4.2 `workspace_border_detect` 源码继承契约

本验证器 MUST 直接参考、复制并按验证目标改写仓库现有 `workspace_border_detect` 源码，禁止脱离现有实现从零另写一个行为不同的工作区检测器。

必须首先阅读并继承以下代码：

```text
workspace_border_detect/native/include/wb/
- c_api.h
- detector.hpp
- config.hpp
- types.hpp
- background.hpp
- color.hpp
- features.hpp
- similarity.hpp
- seeds.hpp
- grower.hpp
- geometry.hpp
- refine.hpp
- scoring.hpp
- validate.hpp

workspace_border_detect/native/src/
- c_api.cpp
- detector.cpp
- background.cpp
- color.cpp
- features.cpp
- similarity.cpp
- seeds.cpp
- grower.cpp
- geometry.cpp
- refine.cpp
- scoring.cpp
- validate.cpp
```

至少继承这些既有语义：

- `DetectionInput`、`DetectionOutput` 和半开 `IntRect`；
- `WorkspaceBorderDetector` / `DetectWorkspace` 的检测阶段和状态码；
- ROI 仅用于采样、增长可以使用完整捕获范围的约定；
- Lab 背景模型、种子采样和背景聚类；
- 强/弱相似度、梯度/方差屏障和区域增长；
- 背景空洞、矩形几何、候选歧义和边界细化；
- DPI 缩放、阈值集中配置及 `CaptureId` 一致性；
- 当前 `DetectorConfig` 中已有阈值的含义。

验证器中的原生代码 SHOULD 采用以下方式之一：

1. **首选：静态库/目标库复用。** 将 `workspace_border_detect/native` 作为原生依赖，复用公共几何、类型和 CPU 参考实现；
2. **允许：有来源标记的源码复制。** 将必要文件复制到验证器的原生目录后改写为 GPU 后端，保留原始相对路径、来源提交或版本说明和对应测试；
3. **禁止：只根据文档重写。** 不得忽略现有代码中的阈值、状态、边界条件和候选规则，另造近似实现。

如果复制源码，复制后的文件头或模块清单 MUST 记录：

```text
SourceModule = workspace_border_detect
SourcePath
SourceRevision
PortKind = Unchanged | Adapted | GpuPort
BehavioralDifferences
```

GPU 改写必须是对既有阶段的等价移植或明确扩展：

```text
现有 CPU 阶段
→ 保留为参考后端与差分测试基准
→ 将逐像素、掩码、特征和归约阶段移植到 GPU
→ 保留 CPU 几何决策，或在证明等价后移植
```

不得为了 GPU 化而静默删除硬拒绝、歧义处理、DPI 逻辑、半开矩形或 `CaptureId` 契约。确需改变行为时，必须在验证器架构、诊断结果和差分测试中显式列出。

验证器需要扩展现有公开结果，使其至少输出：

```text
WorkspaceDetectionResult
- WorkspaceRectScreenPhysicalPx
- WorkspaceBackgroundModel
- Confidence
- CaptureId
- SourceBackend: CpuReference | GpuPort
- SourceRevision
```

```text
WorkspaceBackgroundModel
- CenterLab
- StrongDeltaE
- WeakDeltaE
- Confidence
```

当前 `WbDetectResult` 未公开内部 `BackgroundModel` 的全部字段，因此实现 MUST 参考并改写 `workspace_border_detect/native/include/wb/c_api.h`、`types.hpp` 和 `native/src/c_api.cpp`，通过版本化 C ABI 输出背景模型；禁止由 C# 再次采样并猜测固定 RGB 背景。

同一套被继承和改写的矩形修正能力还用于从导航器面板内部取得：

```text
NavigatorThumbnailRectScreenPhysicalPx
```

导航器缩略图允许使用独立配置配置集，但其 `IntRect`、DPI、候选歧义和坐标约定必须与继承代码一致。

### 4.3 `behavior_recognizer`

验证器只消费：

```text
PenHover
PenMove
PenDown
PenUp
```

用途仅为：

- 持续获取数位笔 `ScreenPhysicalPx`；
- 判断笔是否进入 `NavigatorPanelRoi`；
- 在笔进入导航器时启动翻转观察；
- 避免在接触笔画中途提交新矩阵。

本验证器不保存正式笔触。

### 4.4 其他输入事件

事件适配器至少产生：

```text
SpacePanStarted / SpacePanEnded
RotateStarted / RotateEnded
WheelZoomActivity / WheelZoomEnded
KeyboardZoomStarted / KeyboardZoomEnded
```

滚轮结束通过最后一次滚轮事件后的静默窗口判定。

## 5. 坐标约定

所有矩形使用：

```text
[left, right) × [top, bottom)
```

所有坐标系均采用各自矩形左上角原点，X 向右、Y 向下。

### 5.1 屏幕坐标

```text
ScreenPhysicalPx
- 原点：虚拟桌面左上角
- 单位：物理像素
```

### 5.2 工作区坐标

```text
WorkspaceLocalPx
- 原点：WorkspaceRect 左上角
- 单位：物理像素
```

若工作区左上角为 `(Wl,Wt)`：

\[
x_w=x_s-W_l
\]

\[
y_w=y_s-W_t
\]

### 5.3 画布附着坐标

```text
CanvasAttachedNormalized
- 原点：画布内容自身语义左上角
- +X：画布内容向右
- +Y：画布内容向下
- 范围：[0,1] × [0,1]
```

固定语义角：

```text
TL = (0,0)
TR = (1,0)
BL = (0,1)
BR = (1,1)
```

画布坐标系必须随画布一起平移、缩放、旋转和翻转。翻转不得重新定义或改写上述语义点。

| 状态 | `(0,0)` 视觉位置 | `(0,1)` 视觉位置 |
|---|---|---|
| 无翻转 | 左上 | 左下 |
| 水平翻转 | 右上 | 右下 |
| 垂直翻转 | 左下 | 左上 |
| 双翻转 | 右下 | 右上 |

## 6. 初始化检测

初始化流水线：

```text
隐藏覆盖层
→ 冻结 GPU 桌面帧
→ 用户选择 WorkspaceUserRoi
→ workspace_border_detect 修正 WorkspaceRect
→ 取得 WorkspaceBackgroundModel
→ 用户选择 NavigatorPanelRoi
→ 根据 CSP 固定布局取得缩略图搜索区和两个数字区
→ 检测 NavigatorThumbnailRect
→ 检测 NavigatorViewportGeometry
→ OCR 读取 ScalePercent 与 RotationDegrees
→ 建立初始 CanvasViewState
→ 求解初始 TransformSnapshot
→ 显示画布语义左下角标记
```

CSP 固定 UI 语义：

```text
导航器上方数字 = ScalePercent
导航器下方数字 = RotationDegrees
```

验证器不需要判断数字类型。

## 7. 工作区画布观测

在 `WorkspaceRect` 内使用 `WorkspaceBackgroundModel` 进行 GPU Lab/ΔE 分类：

```text
Workspace pixels
→ Strong/Weak Background Mask
→ Border-connected Workspace Background
→ Interior Non-background Geometry
→ Canvas Boundary Candidate
```

该观测只用于：

- 确认画布当前可见边界；
- 给顶层标记提供人工对照位置；
- 在初始化或几何失效时辅助排除错误候选。

它不得替代导航器红框对完整画布视口的描述。

禁止固定 RGB 判断，也禁止只取全部非背景像素的总包围盒。

## 7.1 两大转换类别与红框补全决策

验证器必须先分类，不能默认所有场景都经过导航器。

**类别 1：工作区背景四边完整。** 当工作区背景色沿当前工作区四边形成连续且唯一的几何证据时，直接用工作区与屏幕坐标系求解转换矩阵。此路径不得读取或依赖导航器红框；导航器只作为可选诊断。

**类别 2：工作区背景四边不完整。** 这时导航器红框是“当前工作区视图投射在完整画布缩略图上的视口坐标系”，不是完整画布边界。这里的“补全”不是补画缺失的红色像素，而是恢复完整红框的左上角坐标系原点 `o_v`，以及工作区屏幕 `+X`、`+Y` 在导航器缩略图中的有向基轴 `a_x`、`a_y`：

```text
p_n = o_v + q_x * a_x + q_y * a_y
q_x = x_w / W_w
q_y = y_w / W_h
```

其中 `o_v` 必须是完整红框的语义左上角，不是可见红线片段的包围盒左上角；旋转时也不得使用屏幕轴对齐包围盒代替它。补全结果至少必须输出 `o_v`、`a_x`、`a_y`、语义四角、补全策略以及各轴证据。

必须先补全视口坐标系，再通过工作区这个中间层取得画布与屏幕之间的矩阵：

| 完整直角边数量 | 补全方式 |
|---:|---|
| 4（或完整几何已经足够闭合） | 直接恢复红框左上角原点和两个有向基轴，检查与缩略图边界和工作区比例的一致性 |
| 3 | 依据已观测两轴方向、已有边长和直角关系恢复左上角原点与两个有向基轴 |
| 2 | 依据边的方向/语义分类，并使用工作区长宽比例恢复左上角原点和两个有向基轴；不得交换横纵轴 |
| 1 | 以唯一有向直角边为锚点，使用工作区内部长宽边的背景色与画布比例恢复左上角原点、另一有向轴及完整视口坐标系，再回到统一矩阵求解 |

统一路径为：

```text
完整导航器缩略图中的画布
→ 补全后的红框视口
→ WorkspaceLocalPx（中间层）
→ ScreenPhysicalPx / CanvasAttachedNormalized
```

补全结果必须通过红框与缩略图相交、裁剪方向、轴对应和几何条件数检查。无法唯一确定时必须返回歧义，禁止退回导航器面板范围、用户粗选框或上一帧矩阵。

## 8. 导航器几何与矩阵

### 8.1 缩略图显示坐标

定义：

```text
NavigatorDisplayedNormalized
- 原点：NavigatorThumbnailRect 左上角
- +X：向右
- +Y：向下
```

若缩略图为 `N=[Nl,Nt,Nr,Nb)`：

\[
d_x=\frac{x_n-N_l}{N_w}
\]

\[
d_y=\frac{y_n-N_t}{N_h}
\]

禁止使用任何左下原点或 `Nb-yn` 公式。

### 8.2 导航器红框

零旋转且红框轴对齐时，红框为：

```text
V = [Vl,Vt,Vr,Vb)
```

工作区点归一化：

\[
q_x=\frac{x_w}{W_w},\qquad q_y=\frac{y_w}{W_h}
\]

映射到导航器显示坐标：

\[
x_n=V_l+q_xV_w
\]

\[
y_n=V_t+q_yV_h
\]

任意旋转时红框必须表示为有向几何：

```text
NavigatorViewportGeometry
- OriginTopLeftDisplayed
- AxisXDisplayed
- AxisYDisplayed
- Corners
- Confidence
```

映射为：

\[
p_d=o_v+q_xa_x+q_ya_y
\]

禁止按长边、短边交换 X/Y。

### 8.3 显示方向到画布附着方向

当前显示算子：

\[
D=R_\theta F_VF_H
\]

实际顺序必须通过 CSP 实测固定。显示坐标恢复为画布附着坐标：

\[
p_c=D^{-1}p_d
\]

完整矩阵：

\[
T_{S\rightarrow C}=T_{D\rightarrow C}T_{W\rightarrow D}T_{S\rightarrow W}
\]

\[
T_{C\rightarrow S}=T_{S\rightarrow C}^{-1}
\]

矩阵构造规则只能有一个权威实现，建议位于 C++ 原生核心中。

## 9. 持续更新

### 9.1 视图状态

```text
ValidationCanvasViewState
- ScalePercent
- RotationDegrees
- HorizontalFlip
- VerticalFlip
- NavigatorViewportGeometry
- Generation
- ObservedAt
```

```text
ValidationTransformSnapshot
- SnapshotId
- Generation
- WorkspaceRectScreenPhysicalPx
- NavigatorPanelRoiScreenPhysicalPx
- NavigatorThumbnailRectScreenPhysicalPx
- CanvasViewState
- ScreenPhysicalToWorkspaceLocal
- WorkspaceLocalToCanvasAttached
- ScreenPhysicalToCanvasAttached
- CanvasAttachedToScreenPhysical
- CreatedAt
```

新快照必须完整构造后原子替换。

### 9.2 平移、缩放和旋转

```text
ViewOperationStarted
→ 标记 ViewStateDirty
→ 保持旧稳定标记，不发布中间矩阵

ViewOperationEnded
→ 等待 CSP 稳定
→ 按事件类型读取必要信息
→ 更新红框/OCR
→ 求解新矩阵
→ 原子发布
→ 更新标记
```

按需读取：

| 操作 | 最小更新信息 |
|---|---|
| 空格平移 | 导航器红框 |
| 滚轮或键盘缩放 | 缩放 OCR + 红框 |
| `R` 旋转 | 旋转 OCR + 红框有向几何 |
| ROI/窗口几何变化 | 工作区和导航器几何重新获取 |

如果用户仍处于 `PenDown`，新快照最早在 `PenUp` 后提交。

## 10. 水平与垂直翻转验证

### 10.1 监控启动

当数位笔处于 `NavigatorPanelRoi` 内，无论悬浮还是接触，启动局部观察：

```text
PenPosition ∈ NavigatorPanelRoi
→ SaveStableThumbnailBaseline
→ StartLowResolutionMonitoring
```

离开面板后保留有限尾随窗口。

### 10.2 两级检测

第一级使用最长边 `64–96 px` 的 GPU 观察图，只输出：

```text
Stable
GlobalChangeSuspected
ObservationInvalid
```

第二级仅在变化触发后比较：

```text
Identity
HorizontalFlip
VerticalFlip
HorizontalAndVerticalFlip
```

确认后按奇偶性更新：

```text
HorizontalFlip = !HorizontalFlip
VerticalFlip   = !VerticalFlip
```

空白、纯色或对称画布无法证明翻转时，必须返回 `FlipAmbiguous` 并保持旧状态。

## 11. 顶层验证标记

### 11.1 必选锚点

唯一验收锚点为：

```text
CanvasBottomLeft = (0,1)
```

每次发布快照后：

\[
p_{BL}=T_{C\rightarrow S}(0,1)
\]

覆盖层必须把标记绘制在 `pBL`，不得把点限制在工作区内部。如果点不在当前视口或屏幕中，显示状态 `MarkerOffscreen`，不得伪造边缘位置。

### 11.2 非对称方向臂

为了观察旋转和翻转方向，标记 SHOULD 同时投影：

```text
Anchor     = (0,1)
XAxisProbe = (ε,1)
YAxisProbe = (0,1-ε)
```

三点组成非对称 L 形标记：

- 锚点验证语义左下角位置；
- X 臂验证画布 +X 方向；
- Y 臂验证画布左边界朝向；
- 两臂长度或样式必须不同。

这仍属于同一个左下角标记，不增加第二个独立验收点。

### 11.3 覆盖层

覆盖层 MUST：

- 始终置顶；
- 透明、无边框、不激活；
- 鼠标与数位笔穿透；
- 使用 `ScreenPhysicalPx`；
- 支持负屏幕坐标和混合 DPI；
- 绑定快照代次；
- 只在新快照发布时更新；
- 在截图前隐藏，截图完成后恢复；
- 不得进入自身的图像分析输入。

## 12. GPU 与性能约束

所有图像像素处理必须在 GPU 完成：

- 桌面局部捕获；
- ROI 裁剪；
- 颜色和 Lab 转换；
- 背景分类；
- 缩放和图像金字塔；
- 红框与边缘检测；
- 翻转候选生成和相似度归约；
- OCR 前处理；
- 覆盖层合成。

标准数据路径：

```text
GPU Capture Surface
→ GPU ROI View
→ GPU Analysis
→ Small Result Readback
→ CPU State Machine
```

禁止完整图像 GPU→CPU 回读后逐像素处理。

正常状态下：

- 笔不在导航器且没有视图操作时，图像监控为 `0 Hz`；
- 输入事件线程不得等待 GPU、截图、OCR 或覆盖层；
- 导航器低分辨率观察限制在 `15–30 Hz`；
- GPU 繁忙时跳过旧帧，不积压任务；
- 每类分析最多一个执行请求和一个最新待处理请求；
- GPU 不可用时暂停自动视觉更新并进入 `GpuAnalysisUnavailable`；
- 禁止静默降级为高频 CPU 图像处理。

## 13. 状态机

```text
Idle
→ CapturingInitializationFrame
→ SelectingWorkspaceRoi
→ DetectingWorkspace
→ SelectingNavigatorPanelRoi
→ DetectingNavigatorGeometry
→ SolvingInitialTransform
→ ShowingMarker
→ TrackingStable

TrackingStable
├─ ViewOperationStarted
│  → ViewChanging
│  → ViewOperationEnded
│  → WaitingForStableUi
│  → RecomputingTransform
│  → UpdatingMarker
│  → TrackingStable | TrackingUncertain
│
├─ PenEnteredNavigatorPanel
│  → NavigatorMonitoring
│  → ChangeSuspected
│  → FlipClassifying
│  → RecomputingTransform
│  → UpdatingMarker
│  → TrackingStable | FlipAmbiguous
│
├─ GeometryInvalidated
│  → Reacquiring
│  → TrackingStable | TrackingLost
│
└─ GpuDeviceLost
   → GpuAnalysisUnavailable
   → ReinitializingGpu
   → Reacquiring
   → TrackingStable | TrackingLost
```

## 14. 验证操作流程

验证人员按以下固定流程操作：

1. 打开一张具有明显非对称内容的 CSP 画布；
2. 建立工作区 ROI 和完整导航器面板 ROI；
3. 等待初始左下角标记显示；
4. 确认无变换时标记贴合画布语义左下角；
5. 仅平移画布，观察标记是否随同一内容角移动；
6. 分别测试放大和缩小；
7. 分别测试顺时针和逆时针旋转；
8. 测试水平翻转，标记应移动到视觉右下角；
9. 测试垂直翻转，标记应移动到视觉左上角；
10. 测试双翻转，标记应移动到视觉右上角；
11. 测试平移、缩放、旋转和翻转组合；
12. 将语义左下角移出视口，确认报告 `MarkerOffscreen`；
13. 将该角移回视口，确认标记重新贴合；
14. 重复快速滚轮和连续旋转，确认任务不积压、标记最终收敛到正确位置。

每一步由验证人员记录：

```text
Pass
Fail
Ambiguous
MarkerOffscreenExpected
TrackingLost
```

## 15. 成功标准

本验证器的最终判断以人工动态观察为主。

一次验证会话通过必须满足：

1. 初始化后标记贴合画布语义左下角；
2. 平移后仍贴合相同内容角；
3. 缩放后仍贴合相同内容角；
4. 旋转后锚点和两条方向臂均随画布正确旋转；
5. 水平、垂直和双翻转后标记到达正确的语义角投影位置；
6. 连续组合操作结束后标记能收敛回正确位置；
7. 标记离开视口时不伪造位置；
8. 覆盖层不抢焦点、不阻挡 CSP 输入；
9. 正常绘画和悬浮时无明显卡顿；
10. 不出现旧 GPU 结果把标记拉回历史位置的情况。

辅助记录 SHOULD 包括：

```text
ValidationSample
- Timestamp
- OperationKind
- ScalePercent
- RotationDegrees
- HorizontalFlip
- VerticalFlip
- NavigatorViewportGeometry
- TransformSnapshotId
- MarkerScreenPosition
- MarkerVisibility
- UserVerdict
- Notes
```

## 16. 失败与歧义

以下情况不得记为通过：

- 导航器缩略图检测歧义；
- 红框几何无法唯一求解；
- OCR 缩放或旋转无效；
- 翻转分类为 `FlipAmbiguous`；
- GPU 结果代次过期；
- DPI 或桌面几何在计算中失效；
- 标记被错误限制在工作区边缘；
- 覆盖层进入截图并触发自身；
- 标记只在一个姿态正确，操作后发生稳定偏移；
- 快速操作导致旧任务覆盖新矩阵；
- 输入或 CSP 呈现出现可感知阻塞。

`FlipAmbiguous` 表示图像证据不足，不表示算法错误；应换用非对称画布内容重新测试。

## 17. 诊断输出

```text
NavigatorTransformValidationDiagnostics
- SessionGeneration
- CaptureId
- WorkspaceUserRoi
- WorkspaceRect
- WorkspaceBackgroundModel
- NavigatorPanelRoi
- NavigatorThumbnailRect
- ScaleTextRegion
- RotationTextRegion
- ScalePercent
- RotationDegrees
- NavigatorViewportGeometry
- FlipCandidateScores
- CanvasViewState
- TransformSnapshot
- MarkerCanvasPoint
- MarkerScreenPoint
- MarkerVisibility
- GpuBackend
- GpuTimings
- DroppedFrames
- StateTransitions
- UserVerdicts
- FailureReasons
```

实现 MAY 导出 JSON 诊断文件，但不得保存不必要的连续全屏截图。

## 18. 最小测试契约

必须覆盖：

- 复制/改写代码清单可以追溯到 `workspace_border_detect` 的源路径和版本；
- 相同输入、配置和捕获元数据下，GPU 移植结果与原 CPU 参考后端进行差分测试；
- 工作区矩形、状态码、背景模型和置信度在允许误差内一致；
- 半开矩形、DPI、负坐标、候选歧义和硬拒绝行为没有在移植中丢失；
- GPU 后端新增行为差异均有显式测试和诊断；
- 所有坐标系左上原点；
- 导航器 Y 映射使用 `top + ratio × height`；
- 零旋转红框矩阵；
- 任意旋转的有向红框矩阵；
- 水平、垂直和双翻转；
- 翻转与旋转组合顺序；
- `CanvasBottomLeft=(0,1)` 在所有状态下的投影；
- 快速连续事件的防抖合并；
- 过期 GPU 结果丢弃；
- 覆盖层截图前隐藏；
- `MarkerOffscreen`；
- GPU 设备丢失与恢复；
- 负虚拟桌面坐标与混合 DPI；
- 输入线程不等待 GPU；
- 正常稳定状态图像监控为 `0 Hz`。

## 19. 推荐最小实施顺序

1. 建立 C# 验证器外壳、ROI 选择和状态机；
2. 逐文件阅读并登记 `workspace_border_detect/native/include/wb` 与 `native/src` 的复用、复制和改写清单；
3. 先以现有 `WorkspaceBorderDetector` 建立可运行的 CPU 参考后端和固定回归数据集；
4. 扩展版本化 C ABI，公开背景模型、后端类型和来源版本；
5. 建立 C++ GPU 捕获表面和局部 ROI 视图；
6. 逐阶段将既有颜色、特征、相似度、增长和归约逻辑改写为 GPU 后端；
7. 建立 CPU 参考与 GPU 移植的逐阶段及端到端差分测试；
8. 使用继承后的检测器取得工作区、背景模型和导航器缩略图；
9. 检测导航器轴对齐红框并实现左上原点的零旋转矩阵；
10. 显示画布语义左下角顶层标记；
11. 接入空格平移和滚轮缩放事件；
12. 接入固定区域缩放、旋转 OCR；
13. 实现有向红框和任意旋转矩阵；
14. 接入导航器低分辨率触发和翻转分类；
15. 实现水平、垂直和双翻转坐标系；
16. 加入 GPU 代次、任务合并、设备恢复和性能诊断；
17. 按固定验证流程在真实 CSP 中验收。

## 20. 最终不可违反的原则

1. **本程序只验证基于导航器的坐标转换，不扩展为正式记录或重放系统。**
2. **工作区和导航器缩略图检测必须直接参考、复用、复制并按需改写仓库现有 `workspace_border_detect` 源码，禁止脱离既有实现从零另写。**
3. **CPU 参考后端必须保留用于 GPU 移植差分测试；复制或改写代码必须记录源路径、源版本和行为差异。**
4. **屏幕、工作区、导航器和画布均采用各自矩形左上角原点，X 向右、Y 向下。**
5. **画布坐标系附着于画布内容，并随平移、缩放、旋转和翻转。**
6. **导航器红框和缩略图的相对位置必须使用相同的左上原点语义。**
7. **唯一必选验收锚点是画布语义左下角 `(0,1)`。**
8. **水平翻转后该锚点显示于视觉右下，垂直翻转后显示于视觉左上，双翻转后显示于视觉右上。**
9. **覆盖标记必须最顶层、输入穿透，并在截图前隐藏。**
10. **所有图像像素处理必须由 GPU 完成，CPU 只读取小型结构化结果。**
11. **普通数位笔事件不触发图像分析，输入线程不得等待 GPU。**
12. **低分辨率观察只触发翻转分类，不直接决定翻转。**
13. **无法区分翻转时必须返回歧义，禁止强猜。**
14. **新矩阵必须完整构造并原子发布，旧代次结果必须丢弃。**
15. **标记超出视口时必须报告不可见，不得伪造位置。**
16. **验证成功以标记在全部规定视图操作后持续附着于同一个画布内容角为准。**
