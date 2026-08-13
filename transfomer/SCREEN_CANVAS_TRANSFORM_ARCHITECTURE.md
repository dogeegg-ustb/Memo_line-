# 基于工作区与导航器的屏幕—画布坐标转换：强约束架构

> 本文是 `transfomer` 下一阶段的强制实现契约。实现 AI 必须遵守 `MUST / MUST NOT / SHOULD / MAY`。本文建立在现有 `canvas_border_autocrop` 工作区修正和透明屏幕覆盖层之上，不得破坏现有检测逻辑。

## 1. 目标与术语

程序由用户在同一冻结截图上依次建立两个粗 ROI：

1. **工作区 ROI**：调用现有 `canvas_border_autocrop` 修正为完整工作区矩形；
2. **导航器 ROI**：仅用于检测导航器中的完整画布矩形、可见红色视口框及其转角。

两个 ROI 均建立完成后，程序仍不得自动计算。只有用户主动点击“开始计算/建立坐标转换”，才执行导航器分析、红框补全、矩阵求解、独立验证与视觉化。

### 1.1 区域定义

```text
WorkspaceRect = 主界面中的整个工作区（画布 + 工作区背景）
VisibleCanvas = WorkspaceRect 内所有非工作区背景色的画布可见部分
NavigatorRoi = 用户粗框的导航器范围
NavigatorCanvasRect = 导航器中完整画布缩略图矩形
NavigatorViewportRect = 导航器中红色框代表的完整主工作区视口矩形
```

工作区背景模型 MUST 复用工作区检测成功时得到的背景颜色模型。导航器画布检测的最低要求是：在导航器 ROI 中剔除与工作区相同的背景色，取得导航器画布主体。不得要求导航器画布内容为纯色。

### 1.2 坐标系定义

所有内部图像矩形仍使用半开区间 `[left,right) × [top,bottom)`。

- `ScreenPhysicalPx`：虚拟桌面物理像素，左上原点，X 向右，Y 向下；
- `WorkspaceLocal`：工作区左下角为零点，X 向右，Y 向上；
- `CanvasLocal`：完整画布左下角为零点，X 向右，Y 向上；
- `NavigatorPx`：导航器 ROI 或冻结截图中的像素坐标，左上原点，X 向右，Y 向下；
- `CanvasNormalized`：完整画布左下角 `(0,0)`、右上角 `(1,1)`。

公开输出 MUST 明确标注输入和输出坐标空间，禁止使用无空间语义的 `Point` 或 `Matrix`。

## 2. 非目标和第一版边界

第一版 MUST 仅支持：

- 工作区和导航器画布均轴对齐；
- 画布旋转角为 `0° ± tolerance`；
- 无水平或垂直翻转；
- 导航器红框为轴对齐矩形；
- 同一冻结截图中完成两个 ROI 的建立。

检测到非零旋转、翻转或无法确认状态时，MUST 返回明确的 `UnsupportedRotationOrFlip`，不得按零旋转继续计算。

第一版 MUST NOT：

- 自动控制绘画软件、移动画布或模拟点击来校准；
- 仅凭红框颜色面积估计完整红框；
- 把用户导航器 ROI 直接当作导航器画布矩形；
- 在用户完成第二个 ROI 后自动开始矩阵计算；
- 只使用 `T⁻¹(T(p))≈p` 宣称矩阵正确；
- 验证失败后仍以绿色成功样式显示转换结果。

## 3. 强制用户交互状态机

```text
Idle
  → CaptureRequested
  → FrozenFrameReady
  → SelectingWorkspaceRoi
  → WorkspaceRoiStored
  → SelectingNavigatorRoi
  → BothRoisReady
  → [用户主动触发]
  → ComputingWorkspaceCorrection
  → ComputingNavigatorGeometry
  → SolvingTransform
  → ValidatingTransform
  → Validated | Rejected
```

### 3.1 操作顺序

1. 用户点击“新建坐标转换”；
2. 软件隐藏所有旧覆盖层并冻结一次桌面截图；
3. 用户在冻结截图上粗框工作区，蓝框显示原始 ROI；
4. 软件 MAY 立即修正工作区并缓存结果，但 MUST NOT 开始最终矩阵计算；
5. 用户在同一冻结截图上粗框导航器，建议以橙色或紫色框显示；
6. UI 显示两个 ROI 均已准备；
7. “开始计算”按钮才允许启用；
8. 用户主动点击后，程序执行完整算法；
9. 成功时使用现有屏幕覆盖系统显示验证结果；
10. 失败时不显示绿色成功覆盖，并给出可行动的失败原因。

### 3.2 会话一致性

```text
TransformCaptureSession
- CaptureId
- FrozenCaptureBuffer
- CaptureOriginScreenPhysicalPx
- VirtualScreenBoundsPhysicalPx
- MonitorDescriptors
- WorkspaceUserRoi
- NavigatorUserRoi
- WorkspaceDetectionOutput
- TriggeredAt
- SessionGeneration
```

- 两个 ROI、工作区修正结果、导航器结果、矩阵和覆盖层 MUST 绑定同一个 `CaptureId`；
- 任一 ROI 重选后，旧矩阵和旧验证结果 MUST 立即失效；
- 新截图开始、桌面几何变化或会话取消时，所有旧覆盖层 MUST 关闭；
- 后台旧任务晚于新会话完成时，结果 MUST 丢弃；
- 覆盖层必须在截图前隐藏，禁止把自身截入输入。

## 4. 输入输出数据契约

```text
TransformRequest
- CaptureId
- FrozenCaptureBuffer
- WorkspaceUserRoiCapturePx
- NavigatorUserRoiCapturePx
- CaptureToScreenPhysicalTransform
- WorkspaceDetectionOutput
- UserTriggered: bool
- CancellationToken

TransformResult
- Status
- WorkspaceRectScreenPhysicalPx
- NavigatorCanvasRectCapturePx
- NavigatorViewportRectCapturePx
- RedFrameEvidenceGrade
- ScreenPhysicalToCanvasNormalized
- CanvasNormalizedToScreenPhysical
- WorkspaceLocalToCanvasNormalized
- CanvasNormalizedToWorkspaceLocal
- OptionalScreenPhysicalToCanvasPixels
- OptionalCanvasPixelsToScreenPhysical
- Validation
- Diagnostics
- SourceCaptureId
```

若不知道真实画布像素宽高，MUST 输出 `CanvasNormalized` 矩阵；不得伪造画布像素尺寸。只有从可信 UI/API/OCR 获得真实 `CanvasWidthPx/CanvasHeightPx` 后，才可输出画布像素矩阵。

状态至少包括：

```text
Ok
NotUserTriggered
SessionMismatch
WorkspaceDetectionFailed
NavigatorRoiInvalid
NavigatorCanvasNotFound
NavigatorCanvasAmbiguous
RedFrameNotFound
RedFrameAmbiguous
InsufficientRedFrameGeometry
UnsupportedRotationOrFlip
ScaleConstraintFailed
MatrixSingular
MatrixIllConditioned
IndependentValidationFailed
Cancelled
```

## 5. 总体算法流水线

```text
同一冻结截图 + 两个用户 ROI
  → 校验用户已主动触发
  → 校验 CaptureId 与桌面坐标元数据
  → 调用/复用现有工作区机器修正结果
  → 复用工作区背景 Lab 模型
  → 在导航器 ROI 中剔除同背景色区域
  → 提取导航器完整画布主体矩形
  → 提取红框像素、水平/垂直边段与有方向语义的 90° 角
  → 枚举完整 NavigatorViewportRect 假设
  → 3–4 角直接补全
  → 2 角结合角语义和工作区宽高比补全
  → 1 角结合角语义、两轴比例及红框—画布交集补全
  → 构建 Screen ↔ CanvasNormalized 矩阵
  → 矩阵结构检查
  → 使用未参与求解的视觉证据独立重投影验证
  → 显示验证覆盖层
```

任何强制阶段失败 MUST 停止。禁止静默切换为用户 ROI 尺寸、历史矩阵或固定缩放比。

## 6. 模块划分

### 6.1 `ITransformSessionController`

负责状态机、同帧约束、用户主动触发、取消和结果失效。算法模块不得自行截图，也不得读取动态桌面新帧。

### 6.2 `IWorkspaceObservationProvider`

复用现有 `detect_workspace_rect` 输出：

```text
WorkspaceObservation
- WorkspaceRectCapturePx
- WorkspaceRectScreenPhysicalPx
- BackgroundAppearanceModel
- VisibleCanvasMaskOrEdges
- VisibleCanvasIntersection
- EvidenceGrade
- Confidence
```

- `WorkspaceRect` MUST 是画布加背景的整个工作区；
- `VisibleCanvas` MUST 由工作区内与背景模型显著不同且符合画布主体的区域获得；
- 四侧画布边完整时，MAY 直接建立屏幕—画布矩阵；导航器仍 SHOULD 用作独立验证；
- 画布四边不完整时，MUST 进入导航器红框补全路径。

### 6.3 `INavigatorCanvasDetector`

只在用户导航器 ROI 内工作。其核心职责是剔除与工作区背景相同的颜色并找到完整导航器画布。

约束：

- MUST 使用工作区背景 Lab 模型及自适应 ΔE，不得使用固定 RGB；
- MUST 允许导航器画布内部为复杂绘画内容；
- MUST 通过连通性、矩形边界、长宽比一致性和 ROI 主体覆盖选择画布；
- MUST 排除导航器标题栏、按钮栏、缩放控件和边框；
- MUST NOT 把整个用户 NavigatorRoi 当作画布；
- 导航器画布矩形必须完整可见，否则返回失败或歧义。

输出：

```text
NavigatorCanvasObservation
- CanvasRectCapturePx
- CanvasMask
- BackgroundRejectedMask
- BoundaryConfidenceBySide
- AspectRatio
- Confidence
```

### 6.4 `INavigatorRedFrameDetector`

提取导航器红框。不得仅按“红色”判定，必须联合：

- 红色/高色度外观模型；
- 近水平或近垂直的细线；
- 线宽一致性；
- 90° 连通转角；
- 与导航器画布的裁剪关系；
- 矩形可解释性。

```text
DirectedRedCorner
- PositionCapturePx
- HorizontalRay: Left | Right
- VerticalRay: Up | Down
- Semantic: LT | RT | LB | RB
- HorizontalSupport
- VerticalSupport
- RightAngleError
- Thickness
- ColorScore
- Confidence
```

角语义 MUST 由两条红线臂的朝向直接确定：

```text
Right + Down → LT
Left  + Down → RT
Right + Up   → LB
Left  + Up   → RB
```

不得根据角点位于 ROI 哪一侧猜测语义。

输出还必须包含未组成角的红色边段，供独立验证使用：

```text
RedFrameObservation
- DirectedCorners
- HorizontalSegments
- VerticalSegments
- PixelMask
- RejectedRedComponents
- Confidence
```

### 6.5 `IRedFrameHypothesisBuilder`

统一生成完整红框候选，不得将 4/3/2/1 角写成彼此不兼容的最终输出逻辑。

#### 3–4 个角

- 4 个语义不同且位置一致的角直接确定矩形；
- 3 个角通过轴对齐矩形关系补全第四角；
- MUST 验证对应边水平/垂直、角语义不冲突、边长一致。

#### 2 个角

必须先分类：

- 对角：直接确定完整矩形，比例只用于验证；
- 同一水平边的相邻角：已知宽度，通过红框宽高比推导高度；
- 同一垂直边的相邻角：已知高度，通过红框宽高比推导宽度。

不得只按“两点距离”而忽略角语义和延伸方向。

#### 1 个角

一个可靠、有方向语义的 90° 转角，加上红框完整宽高即可唯一补全。设角点为 `(x,y)`：

```text
LT → [x,       y,        x+W, y+H]
RT → [x-W,     y,        x,   y+H]
LB → [x,       y-H,      x+W, y]
RB → [x-W,     y-H,      x,   y]
```

红框尺寸 MUST 从两轴比例推导，并由红框与导航器画布实际交集验证，不能从可见短臂长度直接外推。

### 6.6 `IViewportScaleSolver`

禁止使用“长边/短边”描述轴对应。永远使用横轴对横轴、纵轴对纵轴。

设：

- 工作区尺寸为 `Ww × Wh`；
- 导航器完整画布为 `Nw × Nh`；
- 完整红框为 `Vw × Vh`。

在零旋转、等比例画布显示下，红框代表工作区视口，因此基本约束为：

\[
\frac{V_w}{V_h} \approx \frac{W_w}{W_h}
\]

更一般地，横纵轴分别建立比例，不得因画布为竖向而交换 X/Y：

\[
V_w = k_x W_w,\qquad V_h = k_y W_h
\]

等比例、无旋转时应有 `kx ≈ ky`。导航器画布尺寸用于将红框位置和跨度归一化到完整画布，而不是把导航器像素直接当作画布像素。

对 1 角情况，还必须利用红框与导航器画布的交集：预测交集的可见红边、裁剪方向和角数量必须与真实观测一致。

### 6.7 `ITransformSolver`

建立以下矩阵：

```text
ScreenPhysicalToWorkspaceLocal
WorkspaceLocalToScreenPhysical
NavigatorPxToCanvasNormalized
CanvasNormalizedToNavigatorPx
WorkspaceLocalToCanvasNormalized
CanvasNormalizedToWorkspaceLocal
ScreenPhysicalToCanvasNormalized
CanvasNormalizedToScreenPhysical
```

工作区局部坐标：

\[
x_w=x_s-W_l
\]

\[
y_w=W_b-y_s
\]

导航器像素到归一化画布：

\[
u=\frac{x_n-N_l}{N_w}
\]

\[
v=\frac{N_b-y_n}{N_h}
\]

补全红框 `V` 表示整个工作区视口在导航器中的范围。工作区局部到导航器的轴对齐映射为：

\[
x_n=V_l+\frac{x_w}{W_w}V_w
\]

\[
y_n=V_b-\frac{y_w}{W_h}V_h
\]

再与 `NavigatorPxToCanvasNormalized` 复合得到最终矩阵。

若存在翻转或旋转，本阶段不得自行猜测；必须由受支持的状态模块显式提供，否则拒绝。

## 7. 矩阵正确性验证

### 7.1 禁止循环自证

以下只证明代码代数自洽，不能证明矩阵对应真实画面：

\[
T^{-1}T\approx I
\]

它 MAY 作为基础检查，但 MUST NOT 作为成功的唯一或主要依据。

### 7.2 Level 1：矩阵结构硬检查

必须验证：

- 所有元素有限；
- 线性部分行列式绝对值大于阈值；
- 条件数不超过配置上限；
- 零旋转时串轴项接近零；
- 无剪切时两轴近似正交；
- 等比例显示时横纵尺度相对误差低于阈值；
- X/Y 方向符号符合左下原点和屏幕 Y 向下约定；
- 正反矩阵数值往返误差达标。

### 7.3 Level 2：导航器视觉重投影

必须使用未参与红框求解的像素段验证：

1. 由完整红框与导航器画布求理论可见交集；
2. 预测哪些红边和红角应可见；
3. 与实际 `RedFrameObservation` 对比；
4. 检查预测可见角数量与语义；
5. 检查每条预测红边的独立覆盖率；
6. 检查超出导航器画布的裁剪方向；
7. 检查不应可见的角确实没有可靠观测。

参与求解的角点邻域必须从验证样本中排除一个安全半径，防止自证。

### 7.4 Level 3：主工作区独立视觉证据

将完整画布四边通过矩阵投影到屏幕，与工作区中实际可见的画布—背景边进行比较。

- 只验证当前真实可见的画布边；
- 用于求解的边不得重复作为唯一验证证据；
- 每条边使用长条带覆盖率和稳健距离，不得只比一个点；
- 至少需要一条独立画布边或多个分离的内部特征对应关系；
- 若完全没有主工作区独立证据，只能返回 `NavigatorConsistent`，不得返回最高验证等级。

边误差：

\[
e_{side}=\operatorname{median}_{p\in observedSide} d(p,predictedSide)
\]

### 7.5 Level 4：可选跨帧动态验证

后续版本 MAY 在用户主动请求时，通过下一帧的平移/缩放变化验证比例和方向。第一版不得强制用户移动画布，也不得模拟输入。

### 7.6 验证结果

```text
TransformValidationResult
- Status: Validated | NavigatorConsistent | Rejected
- MatrixFinite
- IsInvertible
- Determinant
- ConditionNumber
- ScaleX, ScaleY
- ScaleRelativeError
- RotationDegrees
- ShearError
- AxisDirectionsValid
- PredictedVisibleCorners
- ObservedVisibleCorners
- RedEdgeCoverageBySide
- NavigatorReprojectionMedianPx
- NavigatorReprojectionP95Px
- WorkspaceCanvasEdgeMedianScreenPx
- WorkspaceCanvasEdgeP95ScreenPx
- IndependentEvidenceCount
- Confidence
- FailureReasons
```

建议初始阈值必须集中配置并通过数据集校准：

- 导航器重投影中位误差 `≤ 1 px`；
- 导航器 P95 `≤ 2 px`；
- 屏幕物理像素边误差中位数 `≤ 2 px`；
- 屏幕 P95 `≤ 4 px`；
- 等比例缩放相对误差 `≤ 1%`；
- 条件数接近 1，超过硬上限直接拒绝。

导航器 1 px 可能对应屏幕多个像素，因此 MUST 同时报告导航器像素误差、屏幕物理像素误差和归一化画布误差。

## 8. 矩阵正确性的屏幕视觉化

必须复用现有 `WorkspaceOverlayController` 的屏幕透明覆盖能力，但扩展为多图元验证覆盖，不另建互相冲突的覆盖系统。

### 8.1 成功视觉化

当 `Validation.Status == Validated`：

- 使用现有淡绿色半透明矩形覆盖修正后的整个工作区；
- 在矩形内部叠加由矩阵投影得到的画布可见边界；
- 投影画布边使用更亮的绿色细线；
- 在独立观测到的画布边位置绘制对照线或短刻度；
- 预测线和观测线重合时，用户应能直观看到单一贴合线；
- MAY 在不遮挡关键区域的位置显示 `矩阵已验证`、误差和置信度。

### 8.2 仅导航器一致

当只有 Level 1–2 通过而缺少主工作区独立证据：

- 不得使用与完全验证相同的纯绿色成功语义；
- SHOULD 使用淡黄色/黄绿色覆盖或虚线边框；
- 标签必须明确显示“导航器一致，缺少独立屏幕边验证”；
- 矩阵可以输出给调试层，但默认不得用于高风险自动操作。

### 8.3 验证失败视觉化

当验证失败：

- MUST NOT 显示淡绿色成功填充；
- MAY 以淡红色或橙色显示预测工作区/画布边；
- 必须用不同图元显示预测边与实际观测边；
- SHOULD 在最大误差位置绘制短连接线；
- 标签显示失败阶段、最大误差及原因；
- 用户可隐藏覆盖或重新框选任一 ROI。

### 8.4 多图元覆盖接口

现有仅矩形接口 SHOULD 扩展为：

```text
OverlayScene
- CaptureId
- StatusStyle
- FilledRects[]
- Lines[]
- Polylines[]
- CrossMarkers[]
- ErrorVectors[]
- Labels[]
- LifetimePolicy
```

所有图元：

- MUST 使用 `ScreenPhysicalPx`；
- MUST 置顶、无边框、不激活、鼠标穿透；
- MUST 绑定会话并拒绝过期结果；
- MUST 在下一次截图前隐藏；
- 不得因视觉线宽改变算法矩形坐标；
- 负屏幕坐标和混合 DPI 必须正确显示。

### 8.5 推荐显示内容

```text
淡绿色填充矩形：修正后的 WorkspaceRect
亮绿色实线：矩阵预测的当前可见画布边
青色短刻度：图像实际检测到的画布边
小十字：用于独立验证的对应点
红/橙短线：预测与观测之间的误差向量
标签：验证等级、Navigator P95、Screen P95、置信度
```

覆盖层只用于显示，不得参与同一帧矩阵验证。截图输入必须来自覆盖层显示之前的冻结帧。

## 9. 硬拒绝条件

任何一项成立都 MUST 拒绝矩阵：

1. 用户未主动触发计算；
2. 两个 ROI 不属于同一冻结截图；
3. 工作区修正失败或背景模型不可用；
4. 导航器完整画布无法唯一检测；
5. 红框角语义冲突；
6. 1 角时两条臂方向或最小支持长度不足；
7. 2 角时角点组合与同边/对角语义不一致；
8. 补全红框不能解释实际红边与裁剪方向；
9. 红框宽高比与工作区横纵比例冲突；
10. 检测到非零旋转或翻转但实现不支持；
11. 矩阵奇异、病态、含异常剪切或方向错误；
12. 第一、第二红框或导航器画布候选分数接近但几何显著不同；
13. 独立视觉重投影超过阈值；
14. 检测期间会话、DPI、显示器布局或截图几何失效。

禁止以高总分抵消硬拒绝。

## 10. 性能与并发约束

- 用户触发后的几何补全和矩阵求解为 `O(1)`；
- 导航器图像分析 MUST 只处理 NavigatorRoi；
- 工作区特征和背景模型 MUST 复用，不得重新全屏计算；
- 红框检测 SHOULD 使用小 ROI 连续数组和预分配缓冲；
- UI 线程只负责状态和覆盖绘制，检测在后台线程运行；
- 每个阶段必须检查取消和 `SessionGeneration`；
- 推荐端到端 P95：1080p/1440p `≤ 150 ms`，4K `≤ 250 ms`；
- 覆盖层首次呈现 SHOULD `≤ 16 ms`；
- 性能计时从用户点击“开始计算”到验证覆盖首次显示。

## 11. 诊断输出

```text
TransformDiagnostics
- CaptureId
- WorkspaceUserRoi
- NavigatorUserRoi
- CorrectedWorkspaceRect
- WorkspaceBackgroundModel
- NavigatorCanvasCandidates
- SelectedNavigatorCanvasRect
- RedPixelComponents
- DirectedRedCorners
- RejectedCornersAndReasons
- RedFrameHypotheses
- SelectedViewportRect
- ScaleConstraints
- Matrices
- MatrixStructureMetrics
- IndependentValidationSamples
- ReprojectionErrors
- OverlayScene
- RejectionReasons
- Timings
```

每次失败必须返回阶段和硬约束原因，不得只返回 `False` 或“矩阵错误”。

## 12. 测试契约

### 12.1 单元测试

必须覆盖：

- 四种角朝向到 `LT/RT/LB/RB` 的映射；
- 4、3、2 对角、2 同边和 1 角矩形补全；
- 横向工作区配竖向画布，确保不交换 X/Y；
- 屏幕 Y 向下与画布 Y 向上的转换；
- 半开矩形边界；
- 正反矩阵复合；
- 奇异矩阵和高条件数拒绝；
- 负虚拟桌面坐标及混合 DPI；
- CaptureId 和旧异步结果失效。

### 12.2 合成视觉测试

必须生成可控导航器和工作区图像，覆盖：

- 红框 4、3、2、1 个可见角；
- 四种单角语义；
- 红框超过导航器画布的每种裁剪组合；
- 复杂绘画中存在红色 L 形干扰；
- 红框贴近画布边、抗锯齿、1–3 px 线宽；
- 工作区和导航器背景近色；
- 画布为横向、纵向和正方形；
- 画布仅有一条主工作区边可用于独立验证；
- 导航器按钮和标题栏位于用户粗 ROI 内。

### 12.3 必须失败的反例

- 用户未点击开始计算；
- 两个 ROI 来自不同截图；
- 只有红点而没有可辨向 90° 两臂；
- 红色 L 来自画作且无法解释完整视口交集；
- 一个角补全后理论上应出现第二角但截图中不存在；
- 红框比例与工作区比例冲突；
- 导航器画布不完整或多候选歧义；
- 非零旋转或翻转；
- 仅往返矩阵通过但独立重投影失败；
- 陈旧任务试图显示绿色覆盖。

### 12.4 端到端验收

- 用户依次建立工作区 ROI 和导航器 ROI；
- 第二次框选完成后矩阵不得自动计算；
- 只有主动触发后才开始计算；
- 工作区 ROI 被机器修正；
- 导航器画布通过剔除同背景色得到；
- 成功时显示淡绿色工作区和预测/观测画布边；
- 覆盖层坐标与 `ScreenPhysicalPx` 结果误差 `≤ 1 px`；
- 验证失败时绝不显示绿色成功样式；
- 重新框选任一 ROI 后旧矩阵和覆盖层立即失效。

## 13. 推荐实施顺序

1. 扩展会话状态机，支持同帧双 ROI 和用户主动触发；
2. 保持现有工作区修正逻辑不变并暴露背景模型；
3. 实现导航器画布检测；
4. 实现红框边段、方向角和语义；
5. 实现 4/3 角，再实现 2 角和 1 角补全；
6. 实现明确坐标空间的矩阵类型和求解器；
7. 实现 Level 1–3 验证；
8. 将现有覆盖层扩展为 `OverlayScene`；
9. 添加合成、反例和端到端测试；
10. 在真实绘画软件截图上校准阈值。

## 14. 最终不可违反的原则

1. **用户必须分别框选工作区和导航器，且二者来自同一冻结截图。**
2. **两个 ROI 就绪后仍不得自动计算；只有用户主动触发才开始。**
3. **工作区 ROI 必须机器修正；导航器 ROI 必须自行分离完整画布，不能直接当画布。**
4. **导航器画布通过剔除工作区同背景色获得，但必须再做矩形和连通性验证。**
5. **一个完整有向 90° 红角已经包含角语义；补全方向必须由两臂朝向决定。**
6. **永远横轴对横轴、纵轴对纵轴，不得因横画布或竖画布交换轴。**
7. **红框补全必须解释红框与导航器画布的实际交集、可见边和裁剪方向。**
8. **矩阵正反可逆只证明自洽；正确性必须由未参与求解的视觉证据验证。**
9. **成功、部分验证和失败必须使用不同覆盖视觉语义；失败不得显示淡绿色成功框。**
10. **所有覆盖图元复用现有屏幕覆盖基础设施，使用物理像素、鼠标穿透、不抢焦点并绑定 CaptureId。**
