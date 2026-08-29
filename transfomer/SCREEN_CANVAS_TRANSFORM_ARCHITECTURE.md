# 基于工作区、导航器与输入事件的屏幕—画布坐标转换：强约束架构

> 本文是 `transfomer` 的强制实现契约。实现 MUST 遵守 `MUST / MUST NOT / SHOULD / MAY`。系统面向 CSP，依赖 `workspace_border_detect` 提供矩形修正和工作区背景模型，依赖 `behavior_recognizer` 持续提供数位笔屏幕位置与接触状态。

## 1. 目标

系统建立并持续维护以下双向转换：

```text
ScreenPhysicalPx ↔ WorkspaceLocalPx ↔ CanvasAttachedNormalized
```

主要用途：

1. 将 `behavior_recognizer` 产生的数位笔屏幕坐标转换为画布附着坐标；
2. 将笔触以画布附着坐标记录，供后续事件重放；
3. 在画布平移、缩放、旋转及水平/垂直翻转后持续更新转换矩阵；
4. 重放时把已记录的画布坐标通过当前矩阵重新投影到屏幕，再交给虚拟输入驱动；
5. 通过最顶层覆盖层持续标记画布语义左下角，供人工确认坐标转换是否始终附着于画布。

系统不是一次性截图求解器，而是：

```text
用户初始化双 ROI
→ 建立初始几何和矩阵
→ 持续接收输入事件
→ 在视图操作结束后更新矩阵
→ 在笔进入导航器面板时监控可能的翻转
→ 持续发布稳定的 TransformSnapshot
```

## 2. 核心区域定义

```text
WorkspaceUserRoi
- 用户在冻结截图上粗选的工作区范围

WorkspaceRect
- 由 workspace_border_detect 修正后的完整工作区矩形
- 包含画布可见部分与工作区背景

WorkspaceCanvasObservation
- 在 WorkspaceRect 内排除工作区背景后得到的画布几何观测
- 可为完整画布、部分可见画布或旋转后的画布边界

NavigatorPanelRoi
- 用户直接框选的完整 CSP 导航器面板
- 用户框选结果本身就是面板范围，不做外边界替换
- 内部包含导航器缩略图、缩放数字、旋转数字和功能按钮

NavigatorThumbnailRect
- 导航器面板中的完整画布缩略图范围
- 通过 workspace_border_detect 的通用矩形修正能力获得

NavigatorViewportGeometry
- 导航器中的红色视口框几何
- 表示主工作区当前视口相对于完整导航器缩略图的位置和范围

ScaleTextRegion
- NavigatorPanelRoi 内上方固定数字区域
- 其数值固定解释为 CSP 缩放比例

RotationTextRegion
- NavigatorPanelRoi 内下方固定数字区域
- 其数值固定解释为 CSP 旋转角度
```

`NavigatorPanelRoi`、`NavigatorThumbnailRect` 和 `NavigatorViewportGeometry` MUST 是三个不同对象，禁止混用。

## 3. 坐标系统一约定

所有矩形使用半开区间：

```text
[left, right) × [top, bottom)
```

所有二维点和矩阵使用齐次坐标或等价的显式仿射类型。公开 API MUST 标明输入、输出坐标空间，禁止使用无空间语义的 `Point`、`Rect` 或 `Matrix`。

### 3.1 `ScreenPhysicalPx`

```text
原点：虚拟桌面矩形左上角
+X：向右
+Y：向下
单位：屏幕物理像素
```

所有截图区域、覆盖层位置和数位笔位置在参与计算前 MUST 统一到 `ScreenPhysicalPx`。

### 3.2 `WorkspaceLocalPx`

```text
原点：WorkspaceRect 自身左上角
+X：沿工作区向右
+Y：沿工作区向下
单位：屏幕物理像素
```

工作区坐标系只作为屏幕与画布之间的中间转换层，不作为笔触持久化坐标。

若 `WorkspaceRect = [Wl, Wt, Wr, Wb)`，则：

\[
x_w=x_s-W_l
\]

\[
y_w=y_s-W_t
\]

### 3.3 `CanvasAttachedNormalized`

```text
原点：画布内容自身的语义左上角
+X：从画布内容左侧指向右侧
+Y：从画布内容顶部指向底部
范围：通常为 [0,1] × [0,1]
```

它是附着于画布内容的坐标系，必须随整张画布一起平移、缩放、旋转和翻转。

四个固定语义角为：

```text
CanvasTopLeft     = (0, 0)
CanvasTopRight    = (1, 0)
CanvasBottomLeft  = (0, 1)
CanvasBottomRight = (1, 1)
```

这些名称描述画布内容自身的角身份，不描述当前屏幕上的视觉方向。

### 3.4 翻转后的画布坐标轴表现

翻转不得批量改写已经记录的画布坐标。翻转改变的是 `CanvasAttachedNormalized` 在工作区和屏幕中的方向。

| 状态 | 画布原点 `(0,0)` 的屏幕视觉位置 | 画布 +X | 画布 +Y |
|---|---|---|---|
| 无翻转 | 画布视觉左上 | 向右 | 向下 |
| 水平翻转 | 画布视觉右上 | 向左 | 向下 |
| 垂直翻转 | 画布视觉左下 | 向右 | 向上 |
| 双翻转 | 画布视觉右下 | 向左 | 向上 |

归一化翻转算子为：

\[
F_H(u,v)=(1-u,v)
\]

\[
F_V(u,v)=(u,1-v)
\]

但这些算子属于当前视图矩阵构造，不属于笔触数据改写。

## 4. 外部模块契约

### 4.1 `workspace_border_detect`

系统对两个用户 ROI 使用该模块的通用矩形修正能力：

```text
WorkspaceUserRoi
→ WorkspaceRect

NavigatorPanelRoi 内的缩略图搜索区域
→ NavigatorThumbnailRect
```

`workspace_border_detect` MUST 为工作区检测结果提供可复用的背景模型，至少包含：

```text
WorkspaceBackgroundModel
- CenterLab
- StrongDeltaE
- WeakDeltaE
- Confidence
- SourceCaptureId
```

现有原生内部 `BackgroundModel` 已具有 Lab 中心色和强/弱 ΔE 阈值；若公开 C API 尚未返回这些字段，实施时 MUST 扩展结果结构或通过与 `CaptureId` 绑定的句柄安全获取，禁止上层重新用固定 RGB 猜测背景色。

### 4.2 `behavior_recognizer`

系统持续订阅：

```text
PenHover
PenMove
PenDown
PenUp
PenButtonChanged
```

每个可定位事件至少提供：

```text
Timestamp
Sequence
ScreenPhysicalPosition
ContactState
DeviceId
SessionId
```

即使数位笔处于悬浮状态，也 MUST 持续更新当前位置。

如果 `InputEvent.Position` 原本是设备坐标、逻辑桌面坐标或其他坐标，适配层 MUST 先转换为 `ScreenPhysicalPx`，禁止直接与 ROI 比较。

### 4.3 视图操作事件来源

输入支撑程序 SHOULD 汇总并输出：

```text
ViewOperationStarted
ViewOperationEnded
```

至少覆盖：

- 空格键拖动画布；
- `R` 键旋转画布；
- 鼠标滚轮缩放；
- `Ctrl +`；
- `Ctrl -`。

空格和 `R` 以按键释放作为操作结束候选。滚轮没有天然释放事件，MUST 以最后一次滚轮输入后的可配置静默窗口判定结束。键盘缩放释放后也 SHOULD 等待短暂 UI 稳定时间。

## 5. 初始化流程

```text
Idle
→ CaptureRequested
→ FrozenFrameReady
→ SelectingWorkspaceRoi
→ DetectingWorkspace
→ SelectingNavigatorPanelRoi
→ DetectingNavigatorThumbnail
→ ReadingInitialNavigatorState
→ SolvingInitialTransform
→ TrackingStable
```

强制步骤：

1. 隐藏所有覆盖层并冻结桌面截图；
2. 用户粗选 `WorkspaceUserRoi`；
3. 调用 `workspace_border_detect` 得到 `WorkspaceRect` 和 `WorkspaceBackgroundModel`；
4. 用户粗选完整 `NavigatorPanelRoi`；
5. `NavigatorPanelRoi` 直接保存为面板范围；
6. 根据 CSP 固定布局得到缩略图搜索区域、`ScaleTextRegion` 和 `RotationTextRegion`；
7. 调用通用矩形修正能力得到 `NavigatorThumbnailRect`；
8. 检测导航器红框，得到 `NavigatorViewportGeometry`；
9. OCR 读取上方缩放数字和下方旋转数字；
10. 建立初始 `CanvasViewState`；
11. 求解并原子发布初始 `TransformSnapshot`；
12. 显示画布语义左下角顶层标记。

两个用户 ROI、检测结果和矩阵 MUST 绑定同一初始化 `CaptureId`。任一 ROI 重选后，旧几何、旧矩阵和旧覆盖标记立即失效。

## 6. 工作区背景与画布观测

### 6.1 背景分类

在 `WorkspaceRect` 内，对像素与工作区背景 Lab 模型计算 ΔE：

```text
StrongBackground
WeakBackground
NonBackground
Unknown
```

MUST 使用模型提供的强/弱自适应阈值，MUST NOT 使用固定 RGB 相等或固定 RGB 距离。

### 6.2 背景连通区域

画布检测以“与工作区边缘连通的背景”作为工作区背景主体：

```text
WorkspaceBackgroundMask
→ BorderConnectedBackground
→ InteriorNonBackgroundRegion
→ CanvasGeometryCandidates
```

核心假设是：真实绘画通常不会在大范围内精确复制工作区背景，并同时构成可解释的完整矩形边界。因此，排除边缘连通的工作区背景后形成的矩形空洞或矩形主体，是确认画布范围的主要证据。

### 6.3 几何约束

不得只取所有非背景像素的总包围盒。候选至少结合：

- 候选面积；
- 矩形填充率；
- 与背景主体的分离；
- 四边或可见边的连续支持；
- 连通性；
- 抗锯齿和阴影容忍；
- 多候选歧义；
- 与导航器完整画布长宽比的一致性。

输出：

```text
WorkspaceCanvasObservation
- WorkspaceRect
- BackgroundModel
- BorderConnectedBackgroundMask
- CanvasGeometry
- VisibleCanvasEdges
- BoundarySupportBySide
- Confidence
- AmbiguityReasons
```

工作区画布观测是矩阵求解证据，不取代导航器红框对完整画布视口的描述。

## 7. CSP 导航器固定布局与 OCR

CSP 导航器内部 UI 的语义顺序固定：

```text
上方数字 = ScalePercent
下方数字 = RotationDegrees
```

系统不需要通过 OCR 推断数字含义，只读取预先确定的相对区域。具体像素区域 SHOULD 相对于 `NavigatorPanelRoi` 或内部锚点配置，以适配 DPI、主题和面板尺寸，禁止硬编码绝对屏幕坐标。

OCR 输出：

```text
NavigatorNumericReading
- ScalePercent
- ScaleRawText
- ScaleConfidence
- RotationDegrees
- RotationRawText
- RotationConfidence
- CapturedAt
```

必须处理：

- 小数点或本地化小数分隔符；
- 负旋转角；
- 百分号存在或省略；
- OCR 丢失小数点或负号；
- UI 尚未稳定时读到旧值；
- 数值超出合理范围。

OCR 只在初始化或视图操作结束并稳定后触发，不持续占用资源。

## 8.0 转换路径选择：先判断工作区证据，再决定是否使用导航器

系统必须先判断工作区背景模型是否给出了**完整四边的工作区背景/画布边界证据**。这里的“完整四边”不是要求画布内容没有绘画，而是工作区背景色沿四个边形成连续、可解释的边界，并且可从 `WorkspaceCanvasObservation` 唯一恢复当前工作区视图的四个屏幕边界。

### 8.0.1 第一大类：工作区背景四边完整

当工作区背景色具有完整四边，且四边支持率、几何闭合性和条件数通过安全检查时：

```text
WorkspaceBackgroundModel
→ 完整 WorkspaceRect / WorkspaceCanvasObservation
→ CanvasAttachedNormalized ↔ WorkspaceLocalPx
→ WorkspaceLocalPx ↔ ScreenPhysicalPx
→ 直接求解 ScreenPhysicalPx ↔ CanvasAttachedNormalized
```

此路径是直接路径，**不得读取、猜测或依赖导航器红框**。导航器仍可作为诊断信息，但不能参与该矩阵的决定。

### 8.0.2 第二大类：工作区四边不完整，转入导航器补全路径

此时必须把导航器红框明确解释为：**当前工作区视图投射在完整画布缩略图上的视口坐标系**，不是完整画布边界，也不是导航器面板边界。只有先在缩略图坐标中恢复这个视口坐标系，才能建立工作区到画布的中间映射。

这里的“补全红框”有严格定义：不是简单绘制或猜测缺失的红色像素边，而是恢复红框完整形态下的左上角坐标系原点 `o_v`、工作区屏幕 `+X` 在缩略图中的有向基轴 `a_x`、工作区屏幕 `+Y` 在缩略图中的有向基轴 `a_y`，以及由它们隐含的视口范围。完整结果必须满足：

```text
p_n = o_v + q_x * a_x + q_y * a_y
q_x = x_w / W_w
q_y = y_w / W_h
```

因此，补全的最小权威输出是：

```text
CompletedViewportFrame
- OriginTopLeftDisplayed = o_v
- AxisXDisplayed = a_x
- AxisYDisplayed = a_y
- Width = |a_x|
- Height = |a_y|
- SemanticCorners / optional reconstructed edges
- CompletionStrategy
- EvidenceByAxis
- Confidence
```

其中 `o_v` 必须是**完整红框左上角**，不是当前可见红线片段的左上端点，也不是导航器缩略图左上角。对于旋转红框，“左上角”必须由红框自身的有向轴语义确定，不能按屏幕轴对齐包围盒确定。

红框可见的完整直角边数量决定同一个补全器的输入模式：

| 可确认的完整直角边 | 补全规则 | 后续矩阵路径 |
|---:|---|---|
| 4（或 3 个完整角/边组合足以唯一确定第四边） | 直接采用已观测的红框有向几何；只做边界一致性检查 | 导航器缩略图画布 → 红框视口 → 工作区中间层 → 屏幕/画布矩阵 |
| 3 | 根据已确认的两轴方向、已知边长和相邻边关系补全缺失红框边；不得把缺失边当作可见数据 | 同上 |
| 2 | 先判断是对边、邻边还是同一长/短边上的两个角；使用工作区长宽比例约束补全红框，不得按“长边对应长边”交换轴 | 同上 |
| 1 | 以该完整直角边的有向语义为锚点，使用当前工作区内部长宽边的背景色与画布的比例约束，推导红框另一轴和缺失边；然后重复 3/2 边路径 | 同上 |

“补全”必须同时满足：红框与完整缩略图相交关系、红框裁剪方向、工作区的横纵轴对应和可见边支持。任意假设无法唯一满足这些约束时，返回 `InsufficientViewportGeometry` 或 `AmbiguousViewportGeometry`，禁止静默使用用户粗选框、导航器面板范围或上一帧矩阵。

特别约束：工作区背景四边不完整时，不能用工作区的可见画布包围盒直接代替完整画布；完整画布语义只来自导航器缩略图，工作区只充当屏幕与视口之间的中间层。

## 8. 导航器红框与左上原点矩阵计算

### 8.1 导航器缩略图坐标

定义 `NavigatorDisplayedNormalized`：

```text
原点：NavigatorThumbnailRect 当前显示矩形左上角
+X：向右
+Y：向下
```

若缩略图矩形为 `N=[Nl,Nt,Nr,Nb)`：

\[
d_x=\frac{x_n-N_l}{N_w}
\]

\[
d_y=\frac{y_n-N_t}{N_h}
\]

禁止沿用任何左下原点公式，也禁止使用 `N_b-y_n`。

### 8.2 红框相对位置

`NavigatorViewportGeometry` 表示主工作区视口在导航器当前显示坐标中的完整范围。零旋转且红框轴对齐时，设红框为：

```text
V = [Vl, Vt, Vr, Vb)
```

工作区局部点归一化为：

\[
q_x=\frac{x_w}{W_w}
\]

\[
q_y=\frac{y_w}{W_h}
\]

映射到导航器显示坐标：

\[
x_n=V_l+q_xV_w
\]

\[
y_n=V_t+q_yV_h
\]

这里 Y 轴同样从上向下，禁止使用旧式 `V_b-q_yV_h`。

对于任意旋转，红框 SHOULD 表示为有方向四边形或等价的局部基：

```text
NavigatorViewportGeometry
- OriginTopLeftDisplayed       # 完整红框语义左上角坐标系原点
- AxisXDisplayed              # 工作区屏幕 +X 在缩略图中的有向基轴
- AxisYDisplayed              # 工作区屏幕 +Y 在缩略图中的有向基轴
- Width
- Height
- Corners
- CompletionStrategy
- EvidenceByAxis
- Confidence
```

工作区归一化点映射为：

\[
p_d=o_v+q_xa_x+q_ya_y
\]

其中 `o_v` 是当前显示视口的视觉左上角，`a_x` 和 `a_y` 是工作区屏幕 +X、+Y 在导航器显示坐标中的完整边向量。

### 8.3 从导航器显示坐标恢复画布附着坐标

导航器缩略图中的当前显示方向可能包含旋转和翻转。系统 MUST 使用 `CanvasViewState` 将 `NavigatorDisplayedNormalized` 转换为 `CanvasAttachedNormalized`。

定义以中心 `c=(0.5,0.5)` 为基准的当前显示算子：

\[
D=R_\theta F_V F_H
\]

实际组合顺序 MUST 通过 CSP 行为测试固定，并在全部模块中保持一致；禁止不同模块各自决定顺序。

显示坐标到画布附着坐标为：

\[
p_c=D^{-1}p_d
\]

因此完整工作区到画布映射为：

\[
T_{W\rightarrow C}=T_{D\rightarrow C}\,T_{W\rightarrow D}
\]

再与屏幕到工作区平移复合：

\[
T_{S\rightarrow C}=T_{W\rightarrow C}\,T_{S\rightarrow W}
\]

反向矩阵为：

\[
T_{C\rightarrow S}=T_{S\rightarrow C}^{-1}
\]

水平翻转后，`CanvasTopLeft=(0,0)` MUST 通过矩阵落在画布当前视觉右上角；垂直翻转后必须落在视觉左下角；双翻转后必须落在视觉右下角。相同原点语义 MUST 同时用于导航器红框相对位置计算、工作区矩阵和最终屏幕矩阵。

### 8.4 轴对应约束

永远使用明确的 X/Y 轴和有向基向量，MUST NOT 使用“长边对应长边、短边对应短边”的规则。横向、竖向、旋转后的画布都不得通过交换数组下标猜测轴。

## 9. 持续跟踪与矩阵更新

### 9.1 当前状态

```text
CanvasViewState
- ScalePercent
- RotationDegrees
- HorizontalFlip
- VerticalFlip
- NavigatorViewportGeometry
- Generation
- ObservedAt
- Confidence
```

```text
TransformSnapshot
- SnapshotId
- Generation
- WorkspaceRectScreenPhysicalPx
- NavigatorPanelRoiScreenPhysicalPx
- NavigatorThumbnailRectScreenPhysicalPx
- CanvasViewState
- ScreenPhysicalToWorkspaceLocal
- WorkspaceLocalToScreenPhysical
- WorkspaceLocalToCanvasAttached
- CanvasAttachedToWorkspaceLocal
- ScreenPhysicalToCanvasAttached
- CanvasAttachedToScreenPhysical
- ValidFrom
- Evidence
```

新快照必须完整构造后原子替换，禁止逐字段修改正在使用的矩阵。

### 9.2 视图操作开始与当前工作区域封段

同一个稳定 `TransformSnapshot` 下连续产生的全部笔触属于同一个画布工作区域段。这里的“同一画布工作区域”不是根据笔触包围盒猜测，而是指它们共享同一份稳定视口映射、导航器红框和缩略信息。

当系统确认用户开始平移、缩放、旋转或可能的翻转操作时，MUST 先结束当前工作区域段：

```text
ViewOperationStarted / ConfirmedFlipChangeStarted
→ 若有接触中的笔触则等待 PenUp，或按安全策略结束该笔
→ 禁止再向旧区域段追加新笔触
→ 将旧区域段使用的矩阵与导航器缩略信息写在该段全部笔触之后
→ 提交并刷出段尾记录
→ 标记 ViewStateDirty
```

段尾记录写入的是该组笔触实际使用的旧快照，而不是操作结束后求得的新快照。即文件逻辑顺序必须为：

```text
Stroke A
Stroke B
Stroke C
WorkAreaSnapshotFooter（描述 A/B/C 录制时的工作区域）

Stroke D
Stroke E
WorkAreaSnapshotFooter（描述 D/E 录制时的工作区域）
```

如果一个稳定工作区域内没有产生正式笔触，视图改变时 MAY 不写空段。

### 9.3 视图操作结束

```text
ViewOperationEnded
→ 等待 CSP 主画布、导航器和数字稳定
→ OCR 读取缩放和旋转
→ 重新检测导航器红框
→ 使用当前翻转奇偶状态构建 CanvasViewState
→ 求解新矩阵
→ 基础安全检查
→ 原子发布新 TransformSnapshot
→ 以新快照开始下一个画布工作区域段
→ 更新顶层角标
```

在操作进行中可以继续报告笔位置，但 MUST NOT 发布基于中间动画帧的矩阵，也不得把操作期间产生的点追加到已经封闭的旧区域段。

### 9.4 一笔期间的快照

```text
PenDown
→ 锁定当前 TransformSnapshot
→ 使用锁定快照转换本笔全部接触点

PenUp
→ 结束笔触
→ 释放快照
```

一条笔触中途不得切换矩阵。悬浮事件可使用最近一个稳定快照。

## 10. 导航器翻转监控

### 10.1 启动条件

只要数位笔坐标处于 `NavigatorPanelRoi`，无论其处于悬浮还是接触状态，系统就进入导航器监控状态：

```text
PenPosition ∈ NavigatorPanelRoi
→ NavigatorMonitoringArmed
```

进入时保存一份不含红框和覆盖层的稳定导航器缩略图基准。离开面板后 MUST 保留短暂尾随观察窗口，避免漏掉 CSP 延迟刷新的翻转结果。

### 10.2 超低分辨率触发

持续从 `NavigatorThumbnailRect` 获取超低分辨率观察图，例如最长边 64–96 像素。该阶段只判断：

```text
Stable
GlobalChangeSuspected
LocalContentChangeSuspected
ObservationInvalid
```

不得根据低分辨率变化直接宣布水平或垂直翻转。

### 10.3 稍高分辨率分类

检测到大规模变化并等待画面稳定后，获取较高分辨率缩略图，比较：

```text
Identity
HorizontalFlip
VerticalFlip
HorizontalAndVerticalFlip
```

分类 SHOULD 组合：

- 低频灰度结构；
- 梯度或边缘结构；
- 分块稳健相似度；
- 红框屏蔽；
- 局部新增笔画容忍；
- 最优与次优候选分差；
- 连续稳定帧一致性。

确认后只切换翻转奇偶状态：

```text
HorizontalFlip = !HorizontalFlip
VerticalFlip   = !VerticalFlip
```

不得改写已记录笔触。

### 10.4 歧义

空白、纯色、左右对称、上下对称或中心对称画布可能无法从图像证明翻转。此时 MUST 返回 `FlipAmbiguous`，保持上一个已确认状态，不得强猜。

如果产品交互明确保证翻转只能通过导航器按钮触发，则笔进入面板是主要监控条件。实现 MAY 保留极低频兜底观察，以覆盖其他输入设备或快捷键。

## 11. 笔触记录、工作区域分段与事件重放

### 11.1 笔触记录格式

正式笔触记录在画布附着坐标中：

```text
ReplayStrokePoint
- TimestampOffset
- CanvasAttachedPosition
- Pressure
- Tilt
- ContactState
- PenButtons
- InputSequence
```

```text
ReplayStroke
- StrokeId
- WorkAreaSegmentId
- StartedAt
- Duration
- Points[]
```

SHOULD 同时保留原始 `ScreenPhysicalPosition` 供诊断，但重放定位 MUST 使用 `CanvasAttachedPosition`。同一 `ReplayStroke` 内所有点必须使用同一锁定快照转换。

### 11.2 同一画布工作区域段

共享同一稳定视图状态的一组连续笔触保存为一个 `CanvasWorkAreaSegment`：

```text
CanvasWorkAreaSegment
- SegmentId
- StartedAt
- EndedAt
- StrokeCount
- FirstStrokeId
- LastStrokeId
- Footer
```

段内采用“笔触在前、区域信息在后”的流式布局。区域信息只写一次，避免每个点或每条笔触重复保存完整矩阵：

```text
WorkAreaSnapshotFooter
- SegmentId
- SnapshotId
- Generation
- WorkspaceRectScreenPhysicalPx
- NavigatorPanelRoiScreenPhysicalPx
- NavigatorThumbnailRectScreenPhysicalPx
- NavigatorViewportGeometry
- ThumbnailToCanvasScaleX
- ThumbnailToCanvasScaleY
- ScalePercent
- RotationDegrees
- HorizontalFlip
- VerticalFlip
- ScreenPhysicalToCanvasAttached
- CanvasAttachedToScreenPhysical
- ViewportCenterCanvasAttached
- VisibleCanvasRegionAttached
- CapturedAt
- CoordinateConventionVersion
- IntegrityCheck
```

其中“缩略信息”至少包括：

- 导航器完整缩略图矩形；
- 红框相对于完整缩略图的位置或有向几何；
- 缩略图到画布归一化坐标的横纵比例；
- 视口中心和可见画布区域；
- OCR 缩放比例、旋转角度和翻转状态。

`WorkAreaSnapshotFooter` MUST 描述它之前、直到上一个段尾之后的全部笔触。解析器不得假设先读到快照才能解释点；它 MUST 暂存当前未封段笔触，在读到段尾后把该 Footer 绑定到整段。

### 11.3 封段与持久化时机

以下任一事件确认将改变画布工作区域时，MUST 封闭当前段：

- 空格平移开始；
- 滚轮或 `Ctrl +` / `Ctrl -` 缩放开始；
- `R` 旋转开始；
- 水平或垂直翻转变化开始；
- 工作区、导航器面板、显示器布局或 DPI 即将失效；
- 录制正常停止；
- 文件轮转或会话关闭。

封段顺序：

```text
完成当前接触笔触
→ 停止向该段追加
→ 写入该段实际使用的旧 TransformSnapshot 和缩略信息
→ 写入完整性校验
→ 刷出段尾
→ 再允许建立新视图状态和新区域段
```

正常停止录制时，即使用户没有调整视图，也 MUST 为最后一组笔触写入段尾。异常退出恢复时，如果文件末尾存在没有 Footer 的笔触，它们 MUST 标记为 `UnsealedSegment`，不得假装拥有上一段或下一段的矩阵。

### 11.4 去重与快照引用

若文件格式支持独立快照表，段尾 MAY 只保存 `SnapshotId` 和必要的缩略摘要，并引用文件中的不可变完整快照记录；但必须满足：

- 引用目标与段尾在同一文件或同一原子事务中持久化；
- 不允许悬空引用；
- 段尾仍能明确确定它所封闭的笔触范围；
- 重放所需的矩阵和缩略信息不得依赖运行时内存。

简单实现 SHOULD 直接把完整 `WorkAreaSnapshotFooter` 写在笔触后面，以符合顺序写入和崩溃恢复需求。

### 11.5 重放投影

重放时：

\[
p_s=T_{C\rightarrow S,current}p_c
\]

再把得到的 `ScreenPhysicalPx` 坐标交给虚拟输入驱动。

第一阶段重放契约：

- 录制和重放之间 MAY 改变画布平移；
- 录制和重放之间 MAY 改变画布缩放；
- 默认 MUST 保持相同旋转角度；
- 默认 MUST 保持相同水平翻转状态；
- 默认 MUST 保持相同垂直翻转状态。

系统已有能力后 MAY 放宽旋转和翻转一致性限制，但不得改变笔触存储坐标定义。

### 11.6 重放期间

一条笔触重放开始前锁定一个 `TransformSnapshot`。重放期间如果检测到平移、缩放、旋转、翻转或矩阵失效：

```text
立即发送安全 PenUp
→ 中止当前重放
→ 等待新矩阵稳定
```

禁止一条虚拟笔触中途切换矩阵。

## 12. 顶层画布角标验证

旧的开发阶段 Level 1–4 自动视觉验证、多等级绿色/黄色/红色成功语义和独立重投影验收全部取消，不属于运行时架构。

新的人工动态确认方式是固定标记画布语义左下角：

```text
CanvasBottomLeft = (0,1)
```

每个稳定快照发布后计算：

\[
p_{marker}=T_{C\rightarrow S}(0,1)
\]

并在最顶层透明覆盖层绘制。

### 12.1 标记语义

该点始终代表同一个画布内容角：

| 状态 | `CanvasBottomLeft=(0,1)` 的屏幕视觉位置 |
|---|---|
| 无翻转 | 左下 |
| 水平翻转 | 右下 |
| 垂直翻转 | 左上 |
| 双翻转 | 右上 |

旋转时标记随该画布角一起旋转。只要用户无论如何平移、缩放、旋转和翻转，标记都持续落在同一个画布语义角，人工验收即认为坐标跟踪成功。

### 12.2 推荐非对称角标

为同时观察局部轴方向，标记 SHOULD 包含：

```text
Anchor = (0,1)
XAxisProbe = (ε,1)
YAxisProbe = (0,1-ε)
```

将三个点投影后绘制一个非对称 L 形角标：

- 锚点显示画布语义左下角位置；
- X 短臂显示画布 +X 方向；
- Y 短臂显示画布向上的边界方向，即 `-Y`；
- 两条短臂 SHOULD 使用不同长度或样式，避免对称歧义。

### 12.3 覆盖层约束

覆盖层 MUST：

- 始终置于最顶层；
- 无边框、透明、不激活；
- 鼠标和数位笔输入穿透；
- 使用 `ScreenPhysicalPx`；
- 支持负虚拟桌面坐标与混合 DPI；
- 绑定 `TransformSnapshot.Generation`；
- 拒绝旧异步结果；
- 在任何截图和导航器观察前隐藏；
- 截图完成后恢复；
- 不得被自身的低分辨率监控捕获。

若画布语义左下角位于当前视口或屏幕外，系统 MUST 标记为 `MarkerOffscreen`，不得把标记钳制到工作区边缘冒充真实位置。

## 13. 性能、并发与 GPU 图像处理

### 13.1 总体原则

所有截图后的图像处理工作 MUST 由 GPU 执行。CPU 只负责：

- 输入事件接收；
- ROI 和状态机管理；
- 矩阵构造及少量标量计算；
- GPU 命令提交；
- 结果读取与快照原子发布；
- 笔触编码和异步持久化。

下列工作均属于 GPU 图像处理范围：

- BGRA/RGBA 到 Lab、灰度或其他特征空间的转换；
- 工作区背景 ΔE 分类；
- 强/弱背景掩码生成；
- 形态学、连通性传播和边缘连通背景提取；
- 矩形边界、梯度、边缘和候选评分；
- 导航器缩略图裁剪、缩放和多分辨率图像金字塔；
- 导航器红框颜色、线段、转角和掩码分析；
- 超低分辨率变化检测；
- `Identity/H/V/HV` 翻转候选生成与相似度计算；
- OCR 前处理及可使用 GPU 的 OCR 推理；
- 覆盖层图元的最终合成。

MUST NOT 在正常运行路径中把完整工作区或导航器图像下载到 CPU 后逐像素处理。CPU 只允许读取尺寸固定、数量受限的归约结果，例如候选矩形、角点、分数、直方图摘要和 OCR 数值。

### 13.2 GPU 处理流水线

推荐流水线：

```text
Desktop Capture GPU Surface
→ GPU ROI View
→ GPU Color/Feature Conversion
→ GPU Masks and Reduction
→ GPU Geometry/Similarity Kernels
→ Small Structured Result Readback
→ CPU State Machine and Matrix Solver
```

截图表面 SHOULD 在 GPU 内存中直接作为后续计算输入，优先使用共享纹理、资源视图或等价的零拷贝机制。禁止为了得到低分辨率观察图而执行：

```text
全屏 GPU→CPU 拷贝
→ CPU 裁剪
→ CPU 缩放
→ CPU 像素比较
```

正确路径必须是：

```text
GPU 桌面表面
→ 仅建立 NavigatorThumbnailRect 的 GPU 视图
→ GPU 直接下采样到观察纹理
→ GPU 归约为少量变化指标
```

`workspace_border_detect` 的背景估计、掩码和几何候选阶段 MUST 提供 GPU 后端。既有 CPU 实现 MAY 保留为离线测试参考或显式兼容后端，但持续跟踪和正式 CSP 运行不得默认使用 CPU 像素流水线。

### 13.3 与 CSP 的 GPU 资源隔离

图像分析不得通过争抢 GPU 影响 CSP 绘画。实现 MUST：

- 使用独立命令队列、上下文或等价的隔离机制；
- 使用低于前台绘画渲染的调度优先级；
- 不对 CSP 资源创建写访问；
- 不等待或阻塞 CSP 的呈现线程；
- 不使用全设备同步、全局 `Flush` 或无界 fence 等待；
- 限制中间纹理尺寸和生命周期；
- 复用纹理、缓冲区、描述符和命令对象；
- 在 GPU 压力升高时主动降低监控频率，而不是影响数位笔输入；
- 把输入记录正确性置于图像分析实时性之上。

若无法在预算内完成一次观察，该帧 MAY 被跳过。禁止为了追赶旧帧而累积图像任务。

### 13.4 连续绘画热路径

画师在工作区连续绘画时，CPU 热路径只允许：

```text
接收 InputEvent
→ 读取不可变 TransformSnapshot
→ 执行一次仿射坐标变换
→ 写入有界内存队列
→ 返回
```

输入线程 MUST NOT：

- 截图；
- 等待 GPU；
- 执行 OCR；
- 检测红框或翻转；
- 更新覆盖层窗口；
- 同步写盘；
- 等待图像分析锁。

普通 `PenUp` 只作为笔触结束和安全提交边界，MUST NOT 无条件触发截图、OCR或矩阵重算。只有已知视图操作或翻转观察使 `ViewStateDirty=true` 时，才安排后台更新。

### 13.5 局部捕获与监控频率

- 正常绘画且笔不在导航器面板时，导航器持续图像监控 MUST 为 `0 Hz`；
- 笔进入导航器面板后，超低分辨率观察 SHOULD 限制在 `15–30 Hz`；
- 低分辨率最长边 SHOULD 为 `64–96 px`；
- 稍高分辨率翻转分类只在 `GlobalChangeSuspected` 后执行；
- OCR 只处理两个固定数字小区域；
- 红框分析只处理 `NavigatorThumbnailRect`；
- 工作区画布分析只在初始化、重新获取或明确失效时执行；
- 禁止持续全屏采集后再裁剪；
- 离开导航器后的尾随监控必须有明确时间或稳定帧上限。

### 13.6 防抖、任务合并与过期丢弃

滚轮、连续键盘缩放和连续旋转事件 MUST 合并：

```text
重复输入
→ 重置静默计时器
→ 仅在最终稳定后生成一次分析任务
```

每类图像任务最多保留：

- 一个正在执行的任务；
- 一个代表最新代次的待处理请求。

新请求到达后，尚未执行的旧请求 MUST 被替换；正在执行的旧请求完成后若代次过期，其结果 MUST 丢弃。禁止建立无界截图、OCR 或翻转分类队列。

### 13.7 GPU—CPU 同步和结果读取

GPU 结果读取 MUST 异步进行，并仅复制小型结构化结果。CPU 不得在输入线程上轮询 fence 或同步等待。

每份 GPU 结果必须携带：

```text
GpuAnalysisResultHeader
- SessionGeneration
- CaptureId
- InputFrameId
- RoiGeneration
- PipelineKind
- CompletedAt
```

只有全部代次匹配时才能进入矩阵求解。覆盖层隐藏、截图提交和覆盖层恢复之间也必须使用明确的帧标识，防止分析到自身。

### 13.8 GPU 不可用与降级策略

GPU 初始化失败、设备丢失或资源预算不足时：

- 输入事件记录和已有稳定矩阵 MUST 继续工作；
- 持续导航器监控 MUST 暂停；
- 新矩阵和翻转状态不得依据缺失图像证据更新；
- 系统进入 `GpuAnalysisUnavailable` 或 `TrackingDegraded`；
- MAY 提供用户明确触发的低频 CPU 兼容分析；
- CPU 兼容分析 MUST NOT 在连续绘画时自动运行；
- 设备恢复后必须重建 GPU 资源并重新获取稳定状态。

禁止静默切换到高频 CPU 图像处理。

### 13.9 内存、存储与覆盖层

- GPU 中间资源 MUST 预分配并复用；
- CPU 笔触队列必须有界；
- 接触笔触数据优先级高于悬浮诊断数据；
- 悬浮位置需要持续关注，但不要求每个悬浮点永久写盘；
- 文件编码和段尾持久化在独立后台线程批量完成；
- 顶层角标只在 `TransformSnapshot` 或显示几何变化时更新，不持续高帧率重绘；
- 覆盖层合成应使用 GPU，但截图前隐藏和恢复不得阻塞输入线程。

### 13.10 性能预算

实现 SHOULD 以以下预算为目标，并在诊断中分别报告 P50、P95 和 P99：

```text
输入事件 CPU 热路径 P95       ≤ 100 μs
输入事件 CPU 热路径 P99       ≤ 500 μs
导航器低分辨率 GPU 观察 P95   ≤ 2 ms GPU time
较高分辨率翻转分类 P95        ≤ 8 ms GPU time
固定数字区域 OCR 与红框更新 P95 ≤ 30 ms wall time
新 TransformSnapshot 原子发布 ≤ 100 μs
覆盖角标几何更新               ≤ 1 ms CPU time
```

预算是性能目标，不得通过降低笔触记录完整性来满足。GPU 繁忙时优先跳过观察帧、降低监控频率或延后非关键分析。

## 14. 运行时安全检查

取消开发阶段自动视觉验证不等于允许发布非法矩阵。每个快照发布前仍 MUST 检查：

- 所有矩阵元素有限；
- 正反矩阵可逆；
- 行列式绝对值高于最小阈值；
- 条件数低于硬上限；
- OCR 缩放和旋转在合理范围；
- `CaptureId`、会话和状态代次一致；
- `ScreenPhysicalPx`、截图坐标和 ROI 没有混用；
- 桌面布局或 DPI 未在计算期间失效；
- 导航器缩略图与红框几何足以唯一求解；
- 翻转状态不是未经确认的强猜结果；
- 旧任务不能覆盖新状态。

这些是生产安全约束，不称为开发阶段验证。

## 15. 状态机

```text
Idle
→ InitializingRois
→ SolvingInitialTransform
→ TrackingStable

TrackingStable
├─ ViewOperationStarted
│  → ClosingWorkAreaSegment
│  → WorkAreaSnapshotFooterPersisted
│  → ViewChanging
│  → ViewOperationEnded
│  → WaitingForStableUi
│  → RecomputingTransform
│  → OpeningNewWorkAreaSegment
│  → TrackingStable | TrackingUncertain
│
├─ PenEnteredNavigatorPanel
│  → NavigatorMonitoring
│  → ChangeSuspected
│  → WaitingForStableThumbnail
│  → FlipClassifying
│  → RecomputingTransform
│  → TrackingStable | FlipAmbiguous
│
├─ PenDown
│  → StrokeSnapshotLocked
│  → RecordingStroke
│  → PenUp
│  → TrackingStable
│
├─ GpuBudgetExceeded
│  → DroppingObservationFrames
│  → ReducedMonitoringRate
│  → TrackingStable
│
├─ GpuDeviceLost
│  → GpuAnalysisUnavailable
│  → TrackingDegraded
│  → ReinitializingGpuResources
│  → Reacquiring
│  → TrackingStable | TrackingLost
│
└─ GeometryInvalidated
   → Reacquiring
   → TrackingStable | TrackingLost
```

`TrackingUncertain`、`FlipAmbiguous`、`GpuAnalysisUnavailable`、`TrackingDegraded` 和 `TrackingLost` 状态不得启动新的虚拟驱动重放。

## 16. 诊断输出

```text
TransformDiagnostics
- SessionId
- CaptureId
- WorkspaceUserRoi
- WorkspaceRect
- WorkspaceBackgroundModel
- WorkspaceCanvasObservation
- NavigatorPanelRoi
- NavigatorThumbnailRect
- ScaleTextRegion
- RotationTextRegion
- NavigatorNumericReading
- NavigatorViewportGeometry
- FlipObservationBefore
- FlipObservationAfter
- FlipCandidateScores
- CanvasViewState
- TransformSnapshot
- ActiveWorkAreaSegmentId
- SealedWorkAreaSegments
- WorkAreaSnapshotFooters
- UnsealedSegmentState
- GpuBackend
- GpuDeviceState
- GpuFrameId
- GpuTimingsByStage
- GpuQueueDepth
- DroppedObservationFrames
- CpuReadbackBytes
- InputHotPathLatency
- MarkerScreenPosition
- MarkerVisibility
- StateTransitions
- RejectionReasons
- Timings
```

失败必须包含阶段和明确原因，不得只返回 `False` 或“矩阵错误”。

## 17. 测试契约

### 17.1 坐标与矩阵测试

必须覆盖：

- 屏幕左上原点到工作区左上原点的平移；
- 工作区到导航器红框的左上原点映射；
- 禁止出现旧式 Y 轴反转公式；
- 画布四个语义角的映射；
- 水平、垂直和双翻转后的原点位置与轴方向；
- 任意旋转角下画布坐标系随画布旋转；
- 翻转与旋转组合顺序；
- 横向、竖向画布不交换 X/Y；
- 负虚拟桌面坐标；
- 混合 DPI；
- 半开矩形边界；
- 正反矩阵复合；
- 奇异和病态矩阵拒绝。

### 17.2 检测与跟踪测试

必须覆盖：

- 工作区背景模型输出和复用；
- 根据边缘连通背景提取内部矩形画布主体；
- 导航器固定上方缩放、下方旋转 OCR；
- 红框 4、3、2、1 个可见角的补全；
- 数位笔悬浮进入和离开导航器面板；
- 离开后的尾随观察窗口；
- 低分辨率变化触发但不直接判定翻转；
- 稍高分辨率 `I/H/V/HV` 分类；
- 空白和对称图像返回 `FlipAmbiguous`；
- 操作结束后稳定等待和矩阵原子发布；
- 一笔期间锁定快照；
- 旧异步结果失效。

### 17.3 GPU、性能与并发测试

必须覆盖：

- 正式运行路径的像素分类、缩放、特征和翻转相似度均由 GPU 后端执行；
- GPU 截图表面到 ROI 分析保持在 GPU 内存，不发生完整图像回读；
- CPU 只读取有界的小型归约结果；
- 正常绘画且笔不在导航器时导航器监控为 `0 Hz`；
- 普通 `PenUp` 不触发 OCR 或图像分析；
- 输入线程不等待 GPU、截图、OCR、覆盖层或磁盘；
- 滚轮和连续键盘输入被防抖合并为一次最终分析；
- 每类分析任务只有一个执行项和一个最新待处理项；
- 过期 GPU 结果按 `Generation/CaptureId/FrameId` 丢弃；
- GPU 繁忙时跳帧或降频，不积压旧观察任务；
- GPU 设备丢失时进入 `GpuAnalysisUnavailable`，且不静默启用高频 CPU 分析；
- GPU 恢复后重建资源并重新获取稳定状态；
- 覆盖层 GPU 合成不会被自己的截图捕获；
- 连续绘画输入热路径和各 GPU 阶段满足性能预算。

### 17.4 分段存储、重放与覆盖层测试

必须覆盖：

- 多条笔触共享一个工作区域段；
- 平移或缩放开始前，在全部旧笔触之后写入旧矩阵和缩略信息；
- 新矩阵不得错误绑定到旧区域段；
- 无笔触的空区域段可以省略；
- 正常停止时为最后一组笔触写入段尾；
- 文件末尾缺少段尾时识别为 `UnsealedSegment`；
- 解析器在读到 Footer 后将其绑定到正确的前置笔触范围；
- 快照引用不得悬空，完整重放信息不依赖运行时内存；
- 录制后改变平移再重放；
- 录制后改变缩放再重放；
- 重放期间视图变化触发安全 `PenUp`；
- 画布语义左下角在所有翻转组合下的正确视觉位置；
- L 形标记的两条方向臂随旋转和翻转变化；
- 标记位于视口外时报告 `MarkerOffscreen`；
- 覆盖层不抢焦点、不拦截输入；
- 截图前隐藏覆盖层，避免监控自触发。

## 18. 推荐实施顺序

1. 统一所有坐标类型为左上原点、X 向右、Y 向下；
2. 扩展 `workspace_border_detect` 输出可复用背景 Lab 模型；
3. 建立 `ScreenPhysicalPx ↔ WorkspaceLocalPx`；
4. 实现工作区内部背景排除和画布几何观测；
5. 实现 `NavigatorPanelRoi` 固定布局和缩略图检测；
6. 实现缩放、旋转 OCR；
7. 实现导航器红框有向几何；
8. 实现附着画布坐标、旋转和翻转矩阵组合；
9. 接入 `behavior_recognizer` 悬浮和接触位置；
10. 接入视图操作结束事件和稳定等待；
11. 建立共享 GPU 捕获表面、ROI 视图和预分配资源池；
12. 将背景分类、画布检测、缩放、红框、变化检测和翻转分类实现为 GPU 流水线；
13. 实现异步小结果回读、任务合并、代次取消和 GPU 设备恢复；
14. 实现不可变 `TransformSnapshot` 原子发布；
15. 实现画布语义左下角顶层 GPU 合成非对称角标；
16. 实现 `CanvasWorkAreaSegment`、段尾快照及崩溃恢复；
17. 接入画布坐标笔触记录与虚拟驱动重放；
18. 完成性能测试和真实 CSP 行为校准。

## 19. 最终不可违反的原则

1. **屏幕、工作区和导航器显示坐标均以各自矩形左上角为原点，X 向右、Y 向下。**
2. **画布坐标系以画布内容自身语义左上角为原点，并作为画布的一部分随平移、缩放、旋转和翻转。**
3. **水平翻转后画布原点显示在右上角；垂直翻转后显示在左下角；双翻转后显示在右下角。**
4. **已记录笔触始终保存为画布附着坐标，翻转和旋转不得批量改写历史笔触。**
5. **工作区坐标只作为屏幕到画布的中间转换层。**
6. **导航器红框与完整缩略图的相对位置计算必须使用相同的左上原点和画布附着坐标语义。**
7. **导航器 Y 轴映射使用 `top + ratio × height`，禁止恢复任何左下原点公式。**
8. **工作区画布检测必须复用 `workspace_border_detect` 的 Lab 背景模型，以边缘连通背景排除后的矩形主体作为主要证据。**
9. **CSP 导航器上方数字固定为缩放比例，下方数字固定为旋转角度。**
10. **视图操作结束并稳定后才更新矩阵；一笔记录或重放期间锁定单一快照。**
11. **共享同一稳定视图的一组笔触必须组成一个工作区域段；视图改变前必须在这些笔触之后写入它们实际使用的旧矩阵与缩略信息。**
12. **段尾必须明确封闭的笔触范围；正常停止必须封闭末段，缺少段尾的异常末段必须标记为 `UnsealedSegment`。**
13. **数位笔进入导航器面板后启动翻转监控；低分辨率只触发，高一级分辨率才分类。**
14. **翻转分类不明确时保持旧状态并返回歧义，禁止强猜。**
15. **旧开发阶段自动视觉验证体系全部取消，运行时仅保留矩阵安全检查。**
16. **画布语义左下角 `(0,1)` 必须通过最顶层、鼠标穿透覆盖层实时投影显示。**
17. **只要该语义角标在平移、缩放、旋转和翻转后始终附着于同一个画布内容角，即作为人工动态验收结果。**
18. **覆盖层在截图和导航器观察前必须隐藏，禁止系统观察到自身。**
19. **重放期间任何视图变化都必须安全抬笔并中止，不得中途替换矩阵。**
20. **所有正式运行路径的图像像素处理必须在 GPU 上完成；CPU 只读取有界的小型归约结果。**
21. **输入线程不得等待 GPU、截图、OCR、图像分析、覆盖层或磁盘。**
22. **GPU 图像分析必须局部捕获、资源复用、低优先级调度并允许跳帧，禁止通过任务积压或设备全局同步影响 CSP。**
23. **GPU 不可用时必须显式降级并暂停自动图像跟踪，禁止静默切换到高频 CPU 像素处理。**
