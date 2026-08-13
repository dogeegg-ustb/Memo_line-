# 绘画软件工作区（画布 + 背景）矩形检测：强约束架构

> 本文是交给实现 AI 的强制实现契约，不是讨论稿。`MUST / MUST NOT / SHOULD / MAY` 分别表示必须、禁止、建议和可选。实现不得为了提高表面成功率而静默放宽硬约束。

## 1. 目标定义

输入绘画软件截图和用户粗略框选的 ROI，检测**整个工作区矩形**。

本文中的工作区定义为：

```text
Workspace = Canvas + WorkspaceBackground
```

也就是说，输出目标包含：

- 画布本身；
- 画布四周当前可见的工作区背景；
- 被画布分割成 L 形、条带形或多个残缺块的同色背景区域。

输出目标不包含：

- 菜单栏、工具栏、停靠面板、状态栏；
- 工作区外的软件窗口装饰；
- 属于 UI 控件的滚动条和分隔条（除非产品明确把它们定义为工作区内部，本实现默认不包含）。

**画布不是待检测的外矩形。画布是工作区内部的异色区域、遮挡区域或大孔洞。工作区背景颜色用于恢复工作区的外边界。**

用户 ROI 只用于提供大致位置和限制搜索范围，不是最终裁剪框，也不要求精确贴合工作区。

### 1.1 强制运行方式

本功能 MUST 按以下交互闭环运行：

```text
用户启动工作区识别
  → 软件进入截图选区模式
  → 冻结当前桌面截图
  → 用户在截图上拖拽粗略 ROI
  → 用户确认 ROI
  → 软件对冻结截图执行检测与修正
  → CapturePx 转换为屏幕物理像素
  → 在真实屏幕对应位置显示透明淡绿色矩形覆盖层
```

强制约束：

- 检测输入 MUST 是用户框选时冻结的同一帧截图；
- 检测期间真实绘画软件窗口发生变化时，不得偷偷改用新截图；
- 用户 ROI MUST 在冻结截图坐标系中记录；
- 检测输出不是自动裁剪图片，也不是修改绘画软件窗口；
- 成功后的主要可视反馈 MUST 是覆盖在屏幕上的透明淡绿色矩形；
- 覆盖矩形表示修正后的整个工作区，即“画布 + 工作区背景”；
- 检测失败时 MUST NOT 显示绿色成功覆盖层，应显示明确失败状态并允许重新截图框选。

## 2. 核心几何假设

本版本仅处理满足以下条件的工作区：

1. 工作区外边界为轴对齐矩形；
2. 工作区背景在感知颜色空间中整体稳定，允许轻微渐变、压缩噪声和阴影；
3. 画布可以覆盖工作区背景的大部分面积，并把背景切割成残缺矩形、L 形、U 形或条带；
4. 至少存在足够的可见工作区背景，能够形成 A、B、C-L 或 C-II 之一；
5. 工作区外侧与相邻 UI 在颜色、纹理、梯度、连通性或边界连续性上具有可验证差异。

本版本 MUST NOT：

- 把画布矩形输出为工作区矩形；
- 用画布宽高或画布边缘替代工作区外边缘；
- 推断旋转、透视或任意四边形工作区；
- 依赖固定 RGB、固定主题或固定软件布局；
- 仅凭历史尺寸、预设宽高比或用户 ROI 尺寸补齐边界；
- 将最大同色块的包围盒直接作为工作区；
- 失败时静默回退到固定阈值、单扫描线或单 Hough 线方案；
- 为保证总有返回值而输出证据不足的矩形。

## 3. A/B/C 证据等级

A、B、C 描述的是**工作区背景对工作区外矩形的几何支撑方式**，不是画布边数。

### 3.1 A 级：四侧外边界直接可验证

工作区背景或其可靠延伸证据直接支持工作区的左、上、右、下四条外边界。四边在原图中均可精修并独立验证。

### 3.2 B 级：三侧外边界直接可验证

三条工作区外边界由背景区域直接支持；第四条由矩形闭合得到，并且预测位置存在独立的 UI 分界、颜色转换或结构证据。

### 3.3 C-L 级：相邻两侧构成完整 L 形

可见工作区背景形成一个完整 L 形，其两条外臂分别贴合工作区的两条相邻外边界：

- 两臂共享一个真实工作区外角；
- 两臂的另一端分别到达工作区另外两个外角；
- 横臂完整跨度给出工作区宽度；
- 竖臂完整跨度给出工作区高度；
- 第四角由轴对齐矩形关系补全。

L 形内部可以被画布占据；检测目标仍是 L 形的**外包工作区矩形**，不是 L 形本身，也不是画布矩形。

### 3.4 C-II 级：两条相对完整背景带（`I※I`）

工作区背景在画布两侧形成两条相对的完整条带；`※` 表示中间可为画布、绘画内容或其他工作区内部区域。

若为左右两条竖带：

- 两条带的外侧边界确定 `left` 与 `right`；
- 两条外侧边界的距离确定工作区宽度；
- 条带的完整上下端点确定 `top` 与 `bottom`；
- 端点跨度确定工作区高度。

若为上下两条横带，使用完全对称的逻辑：

- 外侧边界距离确定工作区高度；
- 完整左右端点跨度确定工作区宽度。

### 3.5 “完整”的强制定义

“至少两边可见”MUST 解释为至少满足 C-L 或 C-II 的**完整几何证据**，不得解释成“ROI 中有两处背景颜色”。

一条完整外边或完整背景带必须满足：

- 主体跨度连续且覆盖率达标；
- 必需端点均被检测到；
- 端点不被截图边界或 SearchRoi 边界截断；
- 端点附近存在符合矩形外角的结构变化；
- 与同一背景模型连通或具有可靠的同源证明；
- 不是工具栏、状态栏、停靠面板或滚动条造成的伪条带。

只有中段可见、端点触边或长度未知时，严禁利用可见长度反推完整工作区尺寸。

## 4. 输入输出与坐标契约

```text
WorkspaceDetectionInput
- CaptureBuffer: BGRA/RGBA bytes
- CaptureWidth, CaptureHeight, Stride
- UserRoiCapturePx: IntRect
- DpiScaleX, DpiScaleY
- OptionalCancellationToken

WorkspaceDetectionOutput
- Status
- WorkspaceRectCapturePx
- WorkspaceRectScreenPhysicalPx
- EvidenceGrade: A | B | C_L | C_II
- Confidence: [0,1]
- ObservedOuterSides
- ClosedOuterSides
- BackgroundModel
- SourceCaptureId
- Diagnostics
```

### 4.1 截图会话契约

```text
WorkspaceCaptureSession
- CaptureId
- CapturedAt
- VirtualScreenBoundsPhysicalPx
- MonitorDescriptors
- FrozenCaptureBuffer
- UserRoiCapturePx
- CaptureToScreenTransform
```

- `CaptureId` MUST 唯一标识冻结帧，并贯穿截图、ROI、检测结果和覆盖层显示；
- 检测结果的 `SourceCaptureId` MUST 与活动截图会话一致，否则结果必须丢弃；
- 多显示器环境 MUST 保存虚拟桌面原点，允许负屏幕坐标；
- 必须逐显示器记录物理像素边界和 DPI，禁止假设所有显示器 DPI 相同；
- 若截图缓冲只覆盖单显示器，则 ROI 和输出不得跨越该截图范围；
- 截图选区 UI 必须显示冻结帧，不得让用户在动态桌面上框选后再截另一帧。

强制规则：

1. 所有检测几何 MUST 在截图物理像素 `CapturePx` 中完成。
2. 矩形 MUST 使用半开区间 `[left,right) × [top,bottom)`。
3. `UserRoi` MUST 标准化、裁剪到截图范围并验证最小尺寸。
4. `SearchRoi` MAY 在用户 ROI 外扩，但必须受截图范围和最大外扩比例限制。
5. 下采样结果只能用于产生粗候选；最终四边 MUST 回到原始分辨率精修。
6. 必须显式处理 stride、像素格式、DPI、整数溢出和坐标转换。
7. 输出 `WorkspaceRect` 必须包含识别出的画布主体及工作区背景主体。

失败状态至少包括：

```text
InvalidInput
RoiTooSmall
NoStableWorkspaceBackground
NoConnectedBackgroundEvidence
InsufficientGeometry
EndpointTruncated
AmbiguousCandidates
OuterBoundaryNotSeparable
RectangleClosureFailed
RefinementFailed
IndependentValidationFailed
Cancelled
```

## 5. 总体处理流水线

```text
截图 + 用户粗框
  → 输入、坐标、DPI 与缓冲区校验
  → 构造 SearchRoi
  → 提取 Lab / 灰度 / 方向梯度 / 局部方差
  → 在 ROI 内寻找大块、低方差、矩形残缺的颜色簇
  → 生成多个工作区背景模型候选
  → 构造连续相似度、强弱掩膜和屏障掩膜
  → 对每个候选执行受约束连通生长
  → 将画布视为背景连通域内部的大孔洞/遮挡，不填平其颜色
  → 提取背景连通域的外侧轮廓、条带和端点
  → 构建 A / B / C-L / C-II 工作区外矩形假设
  → 联合评分并拒绝歧义
  → 在原始分辨率精修工作区四条外边
  → 使用独立采样验证外边与包含关系
  → 输出整个 WorkspaceRect
```

任何强制阶段失败 MUST 立即返回明确状态。禁止显示或输出未经原图精修和独立验证的候选作为成功结果。

## 6. 模块划分与强制接口

### 6.1 `IWorkspaceFeatureExtractor`

```text
WorkspaceFeatureMaps
- Lab
- Gray
- GradientX
- GradientY
- GradientMagnitude
- LocalVariance
- Width, Height
- ScaleToCapture
```

约束：

- MUST 使用 CIE Lab 或经验证等价的感知颜色空间；
- MUST NOT 仅使用 RGB 欧氏距离；
- SHOULD 使用原图半径不超过 2 px 的中值或轻微高斯滤波；
- MUST 保留原始分辨率特征供最终精修；
- 梯度只作为边界、屏障和验证证据，不得单独决定矩形。

### 6.2 `IWorkspaceBackgroundCandidateSampler`

采样范围是用户 ROI 及其有限外扩区。采样器 MUST 寻找：

- 低局部方差的大块颜色；
- 沿水平或垂直方向具有长跨度的颜色区域；
- 被画布切割后仍能解释为 L、U、条带或矩形外框残片的颜色簇；
- 在空间上分离但颜色一致、可共同解释同一工作区的背景块。

不得假设工作区背景一定贴着用户 ROI 四边。用户粗框可能同时包含工作区外 UI，也可能落在工作区内部。

每个采样块必须保存位置、Lab 中位数、MAD、方差、梯度密度和邻接关系。高方差画作内容、文字、图标和细长控件必须降权或拒绝。

### 6.3 `IWorkspaceBackgroundEstimator`

```text
WorkspaceBackgroundModel
- CenterLab
- RobustScaleOrCovariance
- StrongDeltaE
- WeakDeltaE
- AcceptedSampleIds
- SpatialDistribution
- RectangularSupportScore
- Confidence
```

约束：

- MUST 使用中位数、MAD、截断均值或 M-estimator；
- MUST 聚类并保留多个初始候选，不能只保留面积最大簇；
- 模型选择必须同时考虑颜色稳定性、连通性、长跨度和 A/B/C 几何解释能力；
- 阈值必须根据簇内离散度自适应并设安全上下限；
- 深色与浅色主题必须走同一逻辑；
- 画布颜色即使面积更大，也不能仅凭面积被误选为工作区背景。

### 6.4 `IWorkspaceBackgroundSimilarityBuilder`

```text
WorkspaceBackgroundSimilarity
- Similarity: float [0,1]
- StrongMask
- WeakMask
- BarrierMask
```

- 相似度 MUST 来源于背景模型；
- 高梯度、高方差区域必须降权；
- 持续强梯度可阻止错误泄漏到 UI 或画布；
- 必须保留连续相似度供评分，不能只保留二值图；
- 禁止固定灰度阈值。

### 6.5 `IWorkspaceBackgroundGrower`

对每个可靠背景模型执行多源连通生长：

- MUST 从多个高纯度背景种子开始；
- MUST 默认使用 4 邻域；
- MUST 保留种子来源和连通分量身份；
- MUST 在持续强梯度或明显非背景处停止；
- MAY 跨越不超过原图 2 px 的孤立压缩噪声；
- MUST NOT 把画布区域改写成背景像素；
- MUST NOT 用大半径闭运算填满画布形成的大孔洞；
- MAY 在几何推理阶段把画布孔洞视作允许存在的内部遮挡；
- 不得把颜色相似但没有同源几何支持的外部 UI 区域强行合并。

输出：

```text
WorkspaceBackgroundComponents
- ComponentMasks
- SeedSources
- BoundingGeometry
- HoleCandidates
- LongRuns
- ConnectivityScores
```

### 6.6 `IBackgroundGeometryExtractor`

从背景连通分量提取：

- 水平与垂直长运行段；
- 工作区外侧候选轮廓；
- L 形共享角及两臂端点；
- C-II 相对背景带及其四个端点；
- 被画布造成的大孔洞；
- 外边与内部画布边之间的拓扑关系。

```text
BackgroundGeometryEvidence
- OuterSideSegments
- BandCandidates
- CornerCandidates
- EndpointCandidates
- HoleCandidates
- SourceComponentIds
- Coverage
- CoordinateMad
- TruncationFlags
```

外侧边判定必须使用“背景区域的外边界”，不能使用背景与画布之间的内边界。实现 MUST 对每条候选边记录边的哪一侧属于背景：

```text
Outer boundary: background lies toward Workspace interior
Inner canvas boundary: background lies away from Canvas interior
```

若无法区分工作区外边与画布内边，必须返回歧义或失败，不得猜测。

### 6.7 `IWorkspaceHypothesisBuilder`

#### A 级构建

四条背景外边直接构成工作区矩形。四角闭合、顺序、包含关系和外侧 UI 转换必须一致。

#### B 级构建

三条背景外边确定矩形的三个边界坐标和至少三个外角；第四边由矩形闭合补全。补全边预测位置必须存在独立的 UI 分界、背景终止或长距离梯度证据。

#### C-L 构建

两条相邻完整背景臂必须共享工作区外角。以左上 L 为例：

```text
left   = verticalArm.outerX
right  = horizontalArm.farEndpointX
top    = horizontalArm.outerY
bottom = verticalArm.farEndpointY
```

其余三个方向使用对称逻辑。两臂远端必须是真实工作区外角，不得是被画布切断的内角或被 ROI 截断的端点。

#### C-II 构建

左右背景带时：

```text
left   = leftBand.outerX
right  = rightBand.outerX
top    = robustAlign(leftBand.topEndpoint, rightBand.topEndpoint)
bottom = robustAlign(leftBand.bottomEndpoint, rightBand.bottomEndpoint)
```

上下背景带使用对称逻辑。两带必须同源、相对、长度一致、对应端点对齐，并且中间区域允许由画布占据。

### 6.8 `IWorkspaceOuterBoundaryRefiner`

每条粗外边必须在原图有限窗口内精修，建议半径为 4–10 px 并按 DPI 有界缩放。

对于每个候选坐标，使用宽条带统计：

- 矩形内侧条带应与工作区背景模型一致，或被已识别的内部遮挡解释；
- 矩形外侧条带应脱离该工作区背景模型，或出现稳定 UI 分界；
- 沿边方向必须有足够转换覆盖率；
- 坐标应形成长距离稳定峰；
- 不能把背景—画布内边界精修成工作区外边界。

四边 MUST 联合精修，并保持 `left < right`、`top < bottom`、外角闭合及工作区主体包含关系。

### 6.9 `IWorkspaceValidator`

验证阶段 MUST 使用与拟合阶段不同的分层重采样位置，至少验证：

- 输出矩形包含所识别的画布主体和背景主体；
- 输出边是背景对外 UI 的边界，而不是背景对内画布的边界；
- A/B/C-L/C-II 对应几何约束成立；
- 所需端点没有被截图或 SearchRoi 截断；
- 四条外边在预测位置有足够独立证据；
- 工作区内部允许存在大画布孔洞，且该孔洞不得导致验证失败；
- 坐标扰动 ±1/±2 px 时当前外边具有局部最优性；
- 第一、第二候选不存在几何显著不同但得分接近的歧义。

## 7. 候选评分与不可抵消的硬拒绝

候选评分 SHOULD 综合：

\[
S=w_bC_{background}+w_gC_{geometry}+w_eC_{endpoint}+w_oC_{outerTransition}
+w_iC_{interiorContainment}+w_rC_{closure}-w_tP_{truncation}-w_aP_{ambiguity}
\]

以下条件是硬拒绝，任何高总分都不得抵消：

1. 输出矩形主要包围的是画布而非“画布 + 背景”；
2. 将背景—画布内边误作工作区外边；
3. 任一等级必需端点触碰截图边界或 SearchRoi 安全带；
4. C-L 缺少共享外角或两个远端外角；
5. C-II 两带不同源、长度不一致或对应端点未对齐；
6. 输出矩形未包含已识别的主要画布/内部主体；
7. 候选超出截图或尺寸低于配置下限；
8. 外侧 UI 与工作区背景不可分，无法确认外边界；
9. 第一、第二候选分差小于歧义阈值且矩形差异显著；
10. 精修偏移超过粗检测允许范围；
11. 独立验证失败。

所有阈值 MUST 集中配置并注明单位；禁止散落魔法数字。阈值应按原图短边、DPI 和稳健统计量有界缩放。

## 8. 画布与内部遮挡的处理原则

画布可以是白色、深色、透明棋盘格、带绘画内容或与背景近色。无论何种情况：

- 画布 MUST 被视为工作区内部，而不是输出目标本身；
- 画布 MAY 将背景分割成多个连通分量；
- 算法 MAY 通过颜色模型和矩形拓扑证明多个背景分量同源；
- 不要求穿过画布进行像素级区域生长；
- 可以跨越画布做几何关联，但不可把画布像素标记为背景；
- 最终工作区矩形必须覆盖画布及其外围背景。

当画布与工作区背景完全同色且没有任何可辨边界时，算法仍可依靠工作区外边检测；不得反过来要求必须检测到画布。画布检测只作为内部包含与孔洞辅助证据，不是成功的必要条件。

## 9. 歧义处理

常见伪候选包括：

- 停靠面板的大块同色背景；
- 菜单栏或状态栏形成的长条；
- 画布本身的大块纯色；
- 滚动条与分隔线组成的伪矩形；
- 工作区外另一个颜色相同的 UI 区域。

消歧必须联合使用：

- 用户 ROI 的主体重叠率；
- 对画布/内部主体的包含关系；
- 背景颜色稳定性；
- L 或 C-II 拓扑；
- 外边对外转换；
- 四角闭合；
- 长跨度覆盖率；
- 与 UI 细长控件模型的排斥。

若两个候选均满足硬约束且分数接近，必须返回 `AmbiguousCandidates`，不得默认选面积更大、面积更小或更靠中心者。

## 10. 屏幕覆盖层显示契约

检测成功后，软件 MUST 创建一个独立、无边框、透明、置顶且不激活的屏幕覆盖窗口，在 `WorkspaceRectScreenPhysicalPx` 上显示淡绿色矩形。

### 10.1 视觉规范

```text
WorkspaceOverlayStyle
- FillColor: light green
- FillOpacity: 0.12–0.20
- BorderColor: light/medium green
- BorderOpacity: 0.75–0.95
- BorderThicknessPhysicalPx: 1–2
- CornerRadiusPhysicalPx: 0–3
```

- 矩形内部 MUST 使用均匀半透明淡绿色填充，底下绘画软件仍应清晰可见；
- MUST 绘制清晰但不过度遮挡的绿色边框；
- 禁止使用完全不透明填充；
- 禁止使用会改变目标含义的外扩阴影；
- 边框几何 MUST 与检测矩形一致，不得为视觉效果擅自扩大或缩小检测结果；
- MAY 在矩形附近显示等级和置信度，但标签不得覆盖关键边界，且不得改变矩形几何。

### 10.2 窗口行为

覆盖窗口必须：

- 置顶显示，但 MUST NOT 抢夺绘画软件键盘焦点；
- 默认鼠标穿透，不能阻挡用户继续操作绘画软件；
- 不出现在任务栏、Alt+Tab 和窗口捕获候选中（平台允许时）；
- 不拥有标题栏、系统边框、缩放边框或背景色；
- 使用每像素 Alpha，而不是色键透明；
- 在多显示器和混合 DPI 环境中按物理像素准确定位；
- 支持负虚拟桌面坐标；
- 覆盖矩形不得被客户区边框、DPI 虚拟化或窗口阴影偏移；
- 在目标显示器断开、分辨率变化或 DPI 变化时立即隐藏并要求重新截图。

Windows 实现 SHOULD 使用不激活、工具窗口、分层窗口和鼠标穿透的扩展窗口样式；具体 API 可按 UI 框架封装，但行为契约不得改变。

### 10.3 生命周期与陈旧结果

- 新截图会话开始时 MUST 立即关闭旧覆盖层；
- 用户取消、重新框选或检测失败时 MUST 关闭覆盖层；
- 只有 `Status == Success` 且独立验证通过时才能显示绿色覆盖层；
- 覆盖层 MUST 绑定 `CaptureId`，过期检测任务完成后不得覆盖当前会话；
- 用户 MUST 能通过明确命令隐藏覆盖层；
- SHOULD 提供重新检测入口；
- 若检测期间桌面几何变化，结果 MUST 作废；
- 覆盖层本身 MUST 在后续截图前隐藏，避免被截入输入形成反馈污染。

### 10.4 坐标转换

```text
CapturePx
  → 加上截图在虚拟桌面中的物理像素原点
  → WorkspaceRectScreenPhysicalPx
  → 覆盖窗口物理像素边界
```

- 禁止直接把逻辑像素当作物理像素；
- 禁止只乘一个全局 DPI 比例处理跨显示器场景；
- 若 ROI 或结果跨越不同 DPI 显示器，必须使用分段变换，或明确拒绝跨屏会话；
- 坐标转换单元测试必须覆盖主屏非左上、负坐标、125%/150%/200% DPI 和混合 DPI。

## 11. 确定性、性能和线程安全

- 相同输入与配置 MUST 产生逐像素一致结果；
- RANSAC 必须使用固定种子或确定性采样；
- 检测不得依赖全局可变状态；
- 大图 MAY 下采样生成候选，但原图精修与验证不可省略；
- 长循环 SHOULD 支持取消；
- 禁止为每个像素创建对象；缓冲区必须有内存上限并尽量复用；
- 日志不得保存完整截图，仅记录数值摘要、候选和失败原因。

## 12. 诊断输出

```text
Diagnostics
- UserRoi
- SearchRoi
- BackgroundModelCandidates
- AcceptedAndRejectedSamples
- BackgroundComponents
- HoleAndCanvasCandidates
- LShapeCandidates
- OppositeBandCandidates
- WorkspaceHypotheses
- RejectionReasons
- CoarseWorkspaceRect
- RefinedWorkspaceRect
- PerOuterSideValidation
- AmbiguityMargin
- Timings
```

每次失败必须能定位到阶段和硬约束。禁止只返回 `false` 或“未找到”。

## 13. 测试契约

### 12.1 必须成功的合成与真实测试

必须覆盖：

- 深色、浅色及轻微渐变工作区背景；
- 白色、深色、棋盘格和复杂绘画画布；
- 画布居中、偏移、部分遮挡背景；
- 工作区背景形成 A、B、四个方向 C-L；
- 左右 C-II 与上下 C-II；
- 画布占据工作区绝大部分、仅留下窄背景带；
- 1–3 px UI 分隔线、阴影、抗锯齿和压缩噪声；
- 工作区内存在滚动条、光标和局部浮窗；
- 不同 DPI、stride 和像素格式；
- 用户截图、冻结帧框选、修正及覆盖层完整交互链路；
- 覆盖层透明度、边框位置、鼠标穿透和不抢焦点；
- 多显示器负坐标、混合 DPI 和主屏非虚拟桌面原点；
- 旧检测任务晚于新会话完成时不得显示陈旧覆盖层；
- 截图前覆盖层必须隐藏，截图中不得包含自身绿色矩形。

每个成功用例 MUST 断言输出是整个工作区矩形，并断言输出同时包含画布与背景；不得只对画布矩形做断言。端到端测试还必须断言屏幕覆盖层的物理像素边界与 `WorkspaceRectScreenPhysicalPx` 一致，允许误差不得超过 1 个物理像素。

### 12.2 必须失败的反例

- 仅两段背景中段可见且所有必要端点均被截断；
- 无法区分工作区外边与画布内边；
- 伪 L 的远端实际终止于画布边，而非工作区外角；
- 两条不对齐、不同源或长度明显不同的伪 C-II；
- 最大同色区域是停靠面板，且无法可靠消歧；
- 多个工作区候选近似等价；
- 工作区外侧 UI 与背景完全同色且不存在任何外边界证据。

### 12.3 精度验收

在工作区轴对齐、必要外边证据可见且独立验证通过的数据集上：

- 四条工作区外边的绝对误差中位数 SHOULD ≤ 1 px；
- 每边 P95 SHOULD ≤ 2 px；
- 必须分别统计 A、B、C-L、C-II；
- 必须报告误接受率、拒绝率、歧义率和画布误裁率；
- `CanvasRect` 被错误当成 `WorkspaceRect` 的次数 MUST 为 0；
- 禁止通过统一扩大矩形掩盖定位误差。

## 14. 推荐实施顺序

1. 输入、坐标和特征图；
2. 工作区背景候选采样与稳健聚类；
3. 背景相似度和受约束连通分量；
4. 背景外轮廓、孔洞、条带与端点；
5. 先实现 A，再实现 B；
6. 实现四方向 C-L；
7. 实现左右及上下 C-II；
8. 工作区外边原图联合精修；
9. 独立验证、歧义拒绝和诊断；
10. 合成测试与真实截图回归。

每阶段必须有独立测试。MUST NOT 将全部逻辑写入单一巨型检测函数后再补测试。

## 15. 最终不可违反的原则

1. **输出目标是整个工作区：画布 + 工作区背景。**
2. **画布是工作区内部区域，不是待输出外矩形。**
3. **背景—画布边界是内边；背景—外部 UI 边界才可能是工作区外边。**
4. **至少两边意味着满足完整 C-L 或 C-II 几何，不是仅有两处同色背景。**
5. **C-L 用两条完整背景臂的外跨度确定工作区宽高。**
6. **C-II 用相对背景带的外边间距和完整端点跨度确定工作区宽高。**
7. **画布造成的大孔洞允许存在，禁止为了区域生长而把画布填成背景。**
8. **所有成功结果必须回到原图精修，并通过独立重采样验证。**
9. **无法区分内边与外边时必须拒绝，绝不静默猜测。**
10. **运行方式必须是冻结截图框选和检测，不得在框选后换帧检测。**
11. **成功结果必须通过不抢焦点、鼠标穿透的透明淡绿色屏幕矩形表示。**
12. **覆盖层必须绑定截图会话；失败、取消、过期或桌面几何变化时不得继续显示。**
