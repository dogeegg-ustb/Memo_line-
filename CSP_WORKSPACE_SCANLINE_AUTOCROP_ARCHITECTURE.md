# CSP 工作区截图扫描线自动裁切强约束架构

- 文档状态：实现基线（Normative）
- 版本：1.1.0-dev-overlay
- 目标平台：Windows 10/11
- 目标软件：CLIP STUDIO PAINT（CSP）
- 算法范围：传统计算机视觉；禁止 AI、机器学习、OCR 参与 ROI 边界判定
- 开发期结果呈现：屏幕透明淡绿色 ROI 叠加层；禁止图片与元数据落盘

本文中的“必须（MUST）”“禁止（MUST NOT）”“应当（SHOULD）”“可以（MAY）”分别表示强制约束、禁止约束、推荐约束与可选能力。任何违反 MUST 或 MUST NOT 的实现均不得标记为符合本架构。

---

## 1. 目标与非目标

### 1.1 目标

系统接收用户对 CSP 中央工作区的粗略框选截图或粗略屏幕矩形，通过多扫描线、颜色统计、梯度、鲁棒投票和矩形几何约束，自动识别工作区 ROI，并以开发期叠加层呈现结果：

1. ROI 应覆盖中央工作区中的深灰色画布背景与可见画布；
2. ROI 应排除菜单栏、命令栏、文档标签栏、左右停靠面板、工具栏、滚动条、状态栏、窗口边框及其他 UI；
3. 将算法得到的 ROI 从截图坐标准确转换为物理屏幕坐标；
4. 在屏幕对应位置自动显示透明淡绿色填充和清晰边框，方便开发人员核验；
5. 对低置信度、遮挡、无效框选或多解结果执行明确失败，禁止显示伪精确 ROI；
6. 禁止保存原始截图、裁切截图、诊断图片或结果元数据文件。

### 1.2 非目标

本阶段禁止承担以下职责：

- 不识别画作内容、人物、笔触或语义；
- 不读取 UI 文本；
- 不判断当前工具、图层名称或 CSP 功能状态；
- 不把画布从深灰工作区中二次抠出；
- 不执行透视矫正、重采样放大或图像增强；
- 不将用户粗选矩形直接视为最终 ROI；
- 不通过训练模型、视觉大模型、分类器或神经网络判定边界；
- 不将检测结果裁切、编码或写入任何文件夹。

---

## 2. 核心定义

### 2.1 坐标空间

系统必须明确区分以下坐标空间：

- `ScreenPhysicalPx`：Windows 物理屏幕像素坐标；
- `CapturePx`：原始截图像素坐标，原点为截图左上角；
- `UserRoiPx`：用户粗框在原始截图中的坐标；
- `WorkspaceRoiPx`：算法确定的中央工作区最终坐标；
- `OverlayScreenPhysicalPx`：叠加层在虚拟桌面物理像素坐标中的位置，必须由 `sourceRectScreenPhysicalPx + WorkspaceRoiPx` 得到。

所有矩形必须使用半开区间：

```text
RectI { left, top, right, bottom }
width  = right - left
height = bottom - top
有效像素：x ∈ [left, right), y ∈ [top, bottom)
```

禁止在同一接口中混用“右下角包含”和“右下角不包含”两种语义。

### 2.2 工作区

本文的“工作区”特指 CSP 中央文档视图区域：它可以包含深灰色背景、白色或其他颜色的可见画布，以及画布内容；必须排除外围 UI、文档标签栏和滚动条。

### 2.3 扫描线与扫描带

- 水平扫描线用于生成竖直边界候选；
- 垂直扫描线用于生成水平边界候选；
- 实际实现必须使用具有奇数厚度的“扫描带”，而非单像素线；
- 扫描带的一维信号必须由带内像素的中位数或截断均值生成。

### 2.4 边界支持率

候选边界的支持率定义为：

\[
S = \frac{N_{unique\ scanlines\ supporting\ boundary}}{N_{eligible\ scanlines}}
\]

同一扫描线对同一位置簇最多贡献一票，禁止以一条扫描线上的多个相邻峰重复计票。

---

## 3. 总体数据流

```text
用户粗框/截图
    ↓
输入与 DPI 校验
    ↓
粗 ROI 标准化与安全扩张
    ↓
Lab 特征图 + Scharr 梯度图
    ↓
水平/垂直多扫描带采样
    ↓
单线边缘候选提取
    ↓
跨扫描线一维聚类与投票
    ↓
长边线段恢复
    ↓
四边联合矩形求解
    ↓
内外区域验证 + 置信度评估
    ↓
原始分辨率 ROI 边界精化
    ↓
CapturePx → ScreenPhysicalPx 坐标转换
    ↓
透明淡绿色置顶叠加层显示 ROI
```

任何阶段失败均必须返回结构化错误并隐藏叠加层；禁止在算法失败后退化为标注用户粗框。整个处理链只允许在内存中保存截图和诊断数据，禁止落盘。

---

## 4. 模块边界

实现至少必须包含以下逻辑模块；语言和文件组织可以不同，但职责不得混合到不可测试的单体函数中。

### 4.1 `ICaptureSource`

职责：获取原始截图并声明截图坐标空间。

必须输出：

```text
CaptureFrame
- pixels: BGRA8 或 BGR8
- width: int
- height: int
- strideBytes: int
- capturedAtUtc: timestamp
- sourceRectScreenPhysicalPx: RectI
- dpiAwareness: enum
- monitorId: string
```

约束：

- MUST 使用物理像素截图；
- MUST 在截图前使进程处于 Per-Monitor DPI Aware V2；
- MUST NOT 对截图执行系统缩放；
- SHOULD 使用 Windows Graphics Capture 或 Desktop Duplication；
- MUST 记录原始截图尺寸和来源矩形。

### 4.2 `IUserRoiProvider`

职责：接收用户粗略框选。

约束：

- 用户框选只定义搜索先验，不定义最终边界；
- 粗框允许包含相邻 UI；
- 粗框不得使用固定的横向或纵向最小尺寸同时设限；
- 粗框短边必须不少于 32 px、长边必须不少于 128 px，且面积不少于原始截图面积的 0.5%；
- 粗框纵横比不得作为拒绝条件，必须接受竖屏、横屏、超长画布和分屏窗口产生的高纵横比搜索区域；
- 必须将坐标裁剪到截图边界内；
- 空矩形、反向矩形及越界后为空的矩形必须拒绝。

### 4.3 `IFeatureExtractor`

必须生成：

- Lab 图；
- 灰度图；
- `ScharrX` 绝对梯度图；
- `ScharrY` 绝对梯度图；
- 可选局部方差图。

约束：

- MUST 在原始分辨率完成最终边界定位；
- MAY 使用 1/2 或 1/4 降采样做粗搜索；
- 若使用降采样，MUST 在原图上以粗结果为中心重新精化；
- MUST NOT 将 JPEG 压缩图作为检测中间格式。

### 4.4 `IScanlineSampler`

职责：生成水平和垂直扫描带及其质量分数。

约束：

- 每个方向至少 24 条、至多 96 条扫描带；
- 扫描带必须覆盖搜索区域有效跨度的至少 80%；
- 相邻扫描带不得全部机械固定在单一周期上，应使用确定性抖动或互质步长；
- 默认扫描带厚度为 5 px，允许按分辨率缩放至 3～11 px；
- 必须固定随机种子或使用无随机确定性序列，确保同一输入结果可复现；
- 被明显弹窗、光标或高梯度密度污染的扫描带可以降权，但不得在无记录情况下删除。

### 4.5 `IEdgeCandidateDetector`

职责：在单条扫描带上寻找区域状态变化。

每个候选必须至少包含：

```text
EdgeCandidate
- orientation: Vertical | Horizontal
- positionPx: float
- scanCoordinatePx: int
- strength: float [0,1]
- lineQuality: float [0,1]
- transition: DarkToLight | LightToDark | TextureChange | Unknown
- clusterWidthPx: float
```

候选分数必须综合：

1. 边界两侧 Lab 中位颜色距离；
2. 对应方向 Scharr 梯度；
3. 两侧局部方差或纹理差；
4. 边界前后连续像素支持；
5. 扫描带质量。

禁止仅以单个相邻像素之差产生最终候选。

### 4.6 `IBoundaryConsensus`

职责：跨扫描线聚类、投票并恢复长边。

必须执行：

- 候选按位置的一维聚类；
- 同一扫描线簇内去重；
- 加权中位数求边界位置；
- MAD 计算位置离散度；
- 支持扫描带范围合并，恢复边界线段起止位置；
- 对小缺口容忍，对大缺口拆分。

输出：

```text
BoundarySegment
- orientation
- positionPx
- spanStartPx
- spanEndPx
- supportRatio
- weightedSupportRatio
- madPx
- meanStrength
- uniqueScanlineCount
```

### 4.7 `IWorkspaceRectSolver`

职责：从候选长边中联合选择工作区四边。

必须使用全局联合评分，禁止独立选择四个最强峰后直接组合。联合评分至少包含：

- 四边支持率；
- 边界位置离散度；
- 四边覆盖长度；
- 矩形闭合程度；
- 候选矩形面积合理性；
- 矩形内部包含画布/工作区的证据；
- 矩形外部存在 UI 状态变化的证据；
- 与用户粗框的交并和中心距离；
- 对滚动条、标签栏和窄 UI 条带的惩罚。

### 4.8 `IWorkspaceValidator`

职责：在显示叠加层前执行硬性验收。

验证失败必须终止显示并清除已有叠加层，不得仅记录警告后继续。

### 4.9 `IRoiOverlayPresenter`

职责：将最终 ROI 转换为物理屏幕坐标，并以透明淡绿色叠加层显示。

必须保证：

- 使用独立、无标题栏、无边框的分层窗口；
- 窗口样式至少包含 `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`；
- 默认填充色为 `#66FF99`，全局有效不透明度为 18%；
- 默认边框色为 `#35E878`，边框不透明度为 90%，物理宽度为 2 px；
- 叠加层必须鼠标点击穿透、不得激活、不得抢占键盘焦点、不得进入 Alt+Tab；
- 必须位于 CSP 内容之上，但不得设置为跨所有应用永久最高层；CSP 非前台或最小化时必须隐藏；
- 叠加层几何必须使用物理屏幕像素，不得再次应用 DPI 缩放；
- 必须支持负坐标虚拟桌面与跨显示器场景；
- 新结果显示前必须先替换旧结果，禁止残留多个 ROI；
- 叠加层不得被后续截图采集；采集前必须隐藏，截图完成后仅在检测成功时恢复新结果；
- 用户开始下一次框选、按 Esc、关闭开发工具、CSP 窗口移动/缩放/最小化或捕获来源失效时，必须立即隐藏或重算叠加层。

---

## 5. 多扫描线检测规范

### 5.1 搜索区域

用户粗框标准化后，算法应在四个方向增加搜索余量：

```text
marginX = clamp(round(userRoi.width  * 0.08), 16, 160)
marginY = clamp(round(userRoi.height * 0.08), 16, 160)
```

扩张后的区域必须裁剪到截图边界。若产品 UI 明确规定用户只能从内部框选，可以将扩张比例配置化，但禁止设为零。

### 5.2 分辨率归一化

参数缩放因子：

\[
s = \operatorname{clamp}\left(\frac{\min(W,H)}{1080},0.75,4.0\right)
\]

所有以像素为单位的窗口和容差应由基准值乘以 `s` 后取整，并受到明确上下限约束。

默认参数：

| 参数 | 1080p 基准 | 下限 | 上限 |
|---|---:|---:|---:|
| 扫描带厚度 | 5 px | 3 | 11 |
| 左右/上下统计窗 | 7 px | 4 | 24 |
| 非极大值抑制半径 | 3 px | 2 | 10 |
| 邻峰合并距离 | 4 px | 2 | 14 |
| 投票聚类半径 | 3 px | 2 | 12 |
| 允许的小缺口 | 12 px | 6 | 48 |
| 边界持续长度 | 7 px | 4 | 24 |

### 5.3 扫描带布置

对于寻找竖直边，必须在搜索区域的不同 `y` 上建立水平扫描带；寻找水平边同理。

每组扫描带必须：

- 排除搜索区域最外侧 2% 的不稳定边缘；
- 均匀覆盖主体范围；
- 使用固定的 `[-2,+2,+1,-1,0]` 类抖动循环或等价确定性策略；
- 保留扫描坐标与质量分数供诊断使用。

### 5.4 单线信号

竖直边候选位置 `x` 的基础信号：

\[
D_i(x)=d_{Lab}(median(L_i[x-w,x)),median(L_i[x,x+w)))
\]

\[
E_i(x)=normalized\_ScharrX_i(x)
\]

\[
V_i(x)=normalized\_variance\_difference_i(x)
\]

\[
Score_i(x)=q_i(\alpha D_i(x)+\beta E_i(x)+\gamma V_i(x))
\]

默认权重建议：

```text
alpha = 0.55
beta  = 0.35
gamma = 0.10
```

权重可以配置，但必须满足总和为 1，并写入元数据。

### 5.5 自适应阈值

每条扫描带必须采用鲁棒阈值：

\[
T_i=median(Score_i)+k\cdot MAD(Score_i)
\]

默认 `k = 3.0`，允许范围 `[2.0, 5.0]`。同时必须设置绝对最小边缘强度，防止近乎平坦的扫描带因 MAD 过小产生伪峰。

### 5.6 峰值处理

单线候选处理顺序固定为：

1. 小窗口中值平滑；
2. 自适应阈值；
3. 一维非极大值抑制；
4. 相邻峰合并为边缘簇；
5. 根据方向持续性验证；
6. 输出候选及强度。

边缘阴影产生多个峰时，必须作为“边缘簇”处理。最终精化必须优先定位“背景结束与目标区域开始之间的像素缝隙”，而不是盲目选择最大梯度峰。

### 5.7 跨线聚类

所有候选按边界位置排序，相距不超过聚类半径的候选归入同一位置簇。每个簇必须按扫描带去重，再计算：

- 唯一扫描带支持数；
- 支持率；
- 加权支持率；
- 加权中位位置；
- MAD；
- 平均强度；
- 支持跨度与缺口。

最低候选长边要求：

```text
supportRatio >= 0.60
weightedSupportRatio >= 0.65
madPx <= max(2.0, 1.5 * s)
spanCoverage >= 0.55
```

最终高置信度边界要求：

```text
supportRatio >= 0.80
weightedSupportRatio >= 0.85
madPx <= max(1.25, 0.9 * s)
spanCoverage >= 0.75
```

### 5.8 长边恢复

必须保留每条支持扫描带的坐标，由此恢复边界的有效跨度。短于允许缺口的中断可以合并；超过阈值的中断必须拆为不同线段。

这一步用于防止将多个互不相连、恰好共线的小面板误认为贯穿整个窗口的边界。

---

## 6. 工作区四边求解

### 6.1 候选集合

必须分别生成：

- 左侧竖边候选 `L`；
- 右侧竖边候选 `R`；
- 顶部横边候选 `T`；
- 底部横边候选 `B`。

候选分类必须结合相对位置与扫描方向，而不能只按梯度正负分类。

### 6.2 硬性几何约束

任意矩形候选必须满足：

```text
left < right
top < bottom
shortSide = min(width, height) >= max(32 px, round(0.02 * min(captureWidth, captureHeight)))
longSide  = max(width, height) >= max(128 px, round(0.10 * max(captureWidth, captureHeight)))
area >= 0.005 * captureArea
area >= 0.25 * normalizedUserRoiArea
intersection(rect, userRoi) / area(userRoi) >= 0.50
centerDistance(rect, userRoi) <= 0.35 * diagonal(userRoi)
```

方向无关约束：

- 禁止规定 `width >= height`、`height >= width` 或接近特定纵横比；
- 禁止因候选为竖屏、横屏、超长条或接近正方形而直接加分、扣分或拒绝；
- 短边/长边约束只用于排除退化线段和微小 UI 控件，不得推断画布方向；
- 工作区矩形与其内部可见画布的纵横比相互独立，禁止用画布形状反向限制工作区；
- 当短边较窄导致该方向可布置的扫描带不足时，必须减少扫描带间距并按实际合格扫描带数量计算支持率，不得沿用固定 24 条的分母；
- 只要边界支持、矩形闭合、面积及粗框一致性达到要求，高纵横比候选必须与普通候选使用相同验收标准。

正常轴对齐模式下，四边最终位置必须是整数像素边界或半像素几何边界；实际裁切必须转换为确定的整数半开矩形。

### 6.3 联合评分

建议归一化评分：

\[
Q=0.30Q_{support}+0.15Q_{spread}+0.15Q_{span}+0.15Q_{inside}+0.10Q_{outside}+0.10Q_{user}+0.05Q_{geometry}-P
\]

其中 `P` 是以下惩罚项：

- 具有顶部标签条结构特征，且其长边跨度、内部重复纹理和相邻关系共同符合标签栏；
- 具有左右工具条结构特征，且其窄边、图标周期性和停靠位置共同符合工具条；
- 具有底部滚动条结构特征；
- 四边无法闭合；
- 外部与内部统计完全一致；
- 候选只由单一局部区段支持；
- 矩形接触截图边界但用户粗框未接触该边界。

禁止仅因矩形“高度较小”或“宽度较小”施加 UI 条带惩罚；条带判定必须由尺寸、位置、内部重复结构及邻接关系共同支持，以免排除竖屏、横屏或高纵横比工作区。

必须记录最高分与次高分。当：

```text
bestScore - secondBestScore < 0.08
```

结果必须标记为歧义；除非最高分同时达到 `0.90` 且所有四边均为高置信度，否则禁止显示 ROI 叠加层。

### 6.4 工作区内容验证

最终矩形内部不要求颜色均匀，但必须满足：

- 至少存在一个占合理面积的低频背景区域或画布区域；
- 内部不得呈现典型的窄工具栏形状；
- 至少三条边的内外统计特征存在可测差异；
- 用户粗框中心应位于最终矩形内，除非用户框选模式明确允许从边缘拖拽；
- 最终矩形不得包含已检测到的顶部标签栏或底部滚动条长边。

### 6.5 边界精化

粗矩形确定后，必须在每条边的 `±max(8, round(6*s)) px` 邻域内使用原始分辨率重新扫描。

精化结果应采用多扫描带加权中位数，ROI 取整规则固定为：

```text
left   = ceil(refinedLeftBoundary)
top    = ceil(refinedTopBoundary)
right  = floor(refinedRightBoundary)
bottom = floor(refinedBottomBoundary)
```

该规则确保阴影、描边或外围 UI 不进入 ROI。叠加层矩形必须严格使用该半开 ROI 转换后的物理屏幕坐标；若产品目标改为保守保留边缘，必须通过版本化配置修改，不得在运行时不确定地切换。

---

## 7. 置信度与失败策略

### 7.1 总置信度

必须输出 `[0,1]` 范围的 `confidence`，且至少综合：

- 四边最小支持率；
- 四边最大 MAD；
- 矩形联合评分；
- 最佳与次佳候选分差；
- 内外验证得分；
- 用户粗框一致性。

### 7.2 显示阈值

```text
confidence >= 0.85：自动显示透明淡绿色 ROI
0.70 <= confidence < 0.85：不显示 ROI；返回 NeedsConfirmation
confidence < 0.70：隐藏 ROI；返回 DetectionFailed
```

在无人值守模式中，`NeedsConfirmation` 必须等同于失败，禁止显示叠加层。

### 7.3 强制失败条件

出现任一情况必须失败：

- 输入截图为空或解码失败；
- DPI/坐标空间未知；
- 粗框无效；
- 找不到四条可闭合边界；
- 任一最终边界支持率低于 0.60；
- 最终矩形尺寸或面积不满足约束；
- 结果存在高歧义；
- 截图来源到物理屏幕坐标的映射不可证明；
- CSP 来源窗口已移动、缩放、最小化、关闭或失去有效句柄；
- 叠加层窗口创建或更新失败。

禁止使用“上一次成功 ROI”无提示替代本次失败结果。若产品以后引入历史 ROI，必须显式标记为 `HistoricalFallback`，并要求用户确认。

### 7.4 错误码

至少定义：

```text
CAPTURE_INVALID
DPI_CONTEXT_UNKNOWN
USER_ROI_INVALID
FEATURE_EXTRACTION_FAILED
INSUFFICIENT_VERTICAL_BOUNDARIES
INSUFFICIENT_HORIZONTAL_BOUNDARIES
RECTANGLE_NOT_CLOSED
RECTANGLE_GEOMETRY_INVALID
DETECTION_AMBIGUOUS
CONFIDENCE_TOO_LOW
OUTPUT_DIRECTORY_FAILED
IMAGE_ENCODE_FAILED
ATOMIC_WRITE_FAILED
METADATA_WRITE_FAILED
```

---

## 8. 输出规范

### 8.1 输出目录

所有自动裁切截图必须写入：

```text
<ART_LINE_ROOT>/memoline/
```

约束：

- 程序启动时必须解析 ART_LINE 根目录为绝对规范路径；
- `memoline` 不存在时必须递归创建；
- 禁止回退到当前工作目录、桌面、临时目录或用户图片目录；
- 禁止允许 `..` 或符号链接逃逸根目录；
- 临时文件必须与最终文件位于同一文件系统，以保证原子重命名；
- 截图输出不得写入源码目录、`bin`、`obj`、`publish` 或 `build`。

### 8.2 文件格式

默认必须输出无损 PNG：

```text
memoline/csp_workspace_<UTC时间>_<短序号>.png
```

示例：

```text
memoline/csp_workspace_20260722T143015.284Z_0001.png
```

约束：

- 时间必须使用 UTC；
- 同毫秒冲突时必须递增短序号；
- 禁止覆盖已存在文件；
- PNG 必须保留 sRGB 色彩，不得改变尺寸；
- 禁止默认输出 JPEG；
- PNG 编码失败不得回退到有损格式。

### 8.3 元数据

每张成功截图必须有同名 JSON：

```text
memoline/csp_workspace_20260722T143015.284Z_0001.json
```

最低字段：

```json
{
  "schemaVersion": "1.0.0",
  "algorithm": "scanline-workspace-autocrop",
  "algorithmVersion": "1.0.0",
  "capturedAtUtc": "2026-07-22T14:30:15.284Z",
  "sourceImageSizePx": { "width": 1024, "height": 768 },
  "sourceRectScreenPhysicalPx": { "left": 0, "top": 0, "right": 1024, "bottom": 768 },
  "userRoiCapturePx": { "left": 100, "top": 50, "right": 900, "bottom": 730 },
  "workspaceRoiCapturePx": { "left": 124, "top": 52, "right": 836, "bottom": 706 },
  "outputSizePx": { "width": 712, "height": 654 },
  "confidence": 0.93,
  "decision": "AutoAccepted",
  "boundaries": {
    "left": { "support": 0.91, "madPx": 0.5 },
    "top": { "support": 0.88, "madPx": 0.7 },
    "right": { "support": 0.94, "madPx": 0.4 },
    "bottom": { "support": 0.86, "madPx": 0.8 }
  },
  "parameters": {
    "scanlineCountHorizontal": 48,
    "scanlineCountVertical": 48,
    "scanBandThicknessPx": 5,
    "windowSizePx": 7,
    "madThresholdFactor": 3.0
  },
  "imageSha256": "<64 lowercase hex characters>"
}
```

元数据不得保存完整原始截图像素，也不得包含无法解释的模型输出。

### 8.4 原子写入

写入顺序必须为：

1. 在 `memoline` 创建唯一 `.png.tmp`；
2. 编码 PNG；
3. flush 并关闭；
4. 重新解码校验宽高与可读性；
5. 计算 SHA-256；
6. 写入唯一 `.json.tmp`；
7. flush 并关闭；
8. 将 PNG 临时文件原子重命名为最终名；
9. 将 JSON 临时文件原子重命名为最终名。

若第 9 步失败，必须删除或隔离第 8 步生成的孤立 PNG，并返回失败。程序启动时应清理超过 24 小时的 `.tmp` 文件，但不得删除正式输出。

---

## 9. API 契约

建议顶层接口：

```text
AutoCropRequest
- frame: CaptureFrame
- userRoiCapturePx: RectI
- outputRoot: absolute path
- mode: Interactive | Unattended
- configVersion: string

AutoCropResult
- status: Saved | NeedsConfirmation | Failed
- workspaceRoiCapturePx: RectI?
- outputImagePath: absolute path?
- outputMetadataPath: absolute path?
- confidence: float
- diagnostics: DetectionDiagnostics
- errorCode: string?
- errorMessage: string?
```

强约束：

- `Saved` 时两个输出路径、ROI 和置信度必须存在；
- `Failed` 时不得留下正式 PNG/JSON；
- `NeedsConfirmation` 时不得自动写入正式 PNG/JSON；
- 所有路径必须为绝对路径；
- 所有公共接口必须支持取消令牌；
- 同一请求只能产生一次最终保存结果。

---

## 10. 并发、性能与资源约束

### 10.1 并发

- 多请求可以并行检测；
- 文件名分配与最终重命名必须线程安全；
- 同一捕获帧对象在检测期间必须不可变；
- 禁止复用仍被其他请求写入的像素缓冲区；
- 取消请求后不得继续写正式文件。

### 10.2 性能目标

以 1920×1080 截图、桌面级 x64 CPU 为基准：

```text
P50 检测时间 <= 80 ms
P95 检测时间 <= 200 ms
PNG 编码不计入检测时间，但 P95 <= 500 ms
峰值临时内存 <= 原始 BGRA 图像大小的 8 倍
```

性能优化不得降低最终原图精化或跳过验证。

### 10.3 确定性

同一输入像素、同一粗框、同一配置版本必须产生相同：

- 最终 ROI；
- 置信度（允许浮点末位差异，但序列化前必须固定舍入）；
- 决策状态；
- 诊断边界集合排序。

---

## 11. 诊断与可观测性

正常输出目录只保存最终 PNG 和 JSON。调试可视化必须由显式开关控制，并写入：

```text
memoline/diagnostics/<request-id>/
```

允许的诊断内容：

- 用户粗框叠加图；
- 扫描带位置图；
- 边缘候选图；
- 边界投票直方图；
- 最终矩形叠加图；
- 各阶段耗时和候选评分 JSON。

生产默认必须关闭诊断图输出。日志不得逐像素记录，不得输出完整图像为 Base64。

---

## 12. 测试架构与验收门槛

### 12.1 单元测试

必须覆盖：

- 半开矩形与坐标转换；
- DPI 物理像素契约；
- 扫描带中位信号；
- MAD 自适应阈值；
- 非极大值抑制；
- 邻峰合并；
- 扫描线去重投票；
- 加权中位数；
- MAD 离群剔除；
- 长边缺口合并与拆分；
- 四边联合评分；
- 裁切取整规则；
- 文件名冲突；
- 原子写入失败回滚。

### 12.2 合成图测试

必须程序化生成以下场景：

1. 均匀深灰工作区 + 白画布；
2. 工作区带 1～5 px 阴影/描边；
3. 画布内存在贯穿长黑线；
4. 左右 UI 与工作区颜色接近；
5. 顶部标签栏与底部滚动条；
6. 局部遮挡与鼠标指针；
7. 125%、150%、200% DPI 的物理像素截图；
8. 1080p、1440p、4K；
9. 用户粗框向四边偏移 10～100 px；
10. 无合法工作区与双候选歧义场景。

### 12.3 金标截图测试

必须建立人工标注的真实 CSP 截图集，覆盖不同主题、面板布局、画布颜色和作品内容。金标使用物理像素半开矩形。

自动接受样本验收指标：

```text
四边绝对误差 P95 <= 2 px
四边绝对误差最大值 <= 4 px
ROI IoU P95 >= 0.995
错误自动接受率 <= 0.1%
低置信度样本必须拒绝，不得以提高召回率牺牲错误接受率
```

### 12.4 回归要求

- 每个已修复误裁切必须添加回归样本；
- 算法参数变更必须生成新旧版本指标对比；
- 若错误自动接受率恶化，禁止发布；
- 仅提高平均 IoU 但增加灾难性误裁切的版本禁止发布。

---

## 13. 安全与隐私约束

- 原始全屏截图应仅存在于内存，除非用户显式开启诊断保存；
- 最终只保存裁切后的工作区截图；
- 检测失败时不得保存原始截图；
- 日志不得记录画面像素、窗口标题中的用户文档名或个人路径；
- 元数据中的路径若上传或共享，必须先转换为相对路径或删除；
- 任何遥测必须默认不包含图片。

---

## 14. 禁止实现清单

以下实现明确不符合本架构：

1. 直接使用用户框作为最终裁切框；
2. 只用一条水平线和一条垂直线定位边界；
3. 只寻找固定 RGB 深灰值；
4. 只选择全图最大梯度；
5. 只做 Hough 直线检测而不做跨线支持和矩形验证；
6. 把四个方向最强边缘独立组合而不做联合评分；
7. 置信度不足时仍自动保存；
8. 检测失败后悄悄使用历史 ROI；
9. 对裁切结果缩放或 JPEG 压缩；
10. 将输出写入 `bin`、`obj`、`publish`、`build` 或随机当前目录；
11. 在文件写完前暴露正式文件名；
12. 使用 AI、机器学习、OCR 或远程识别服务参与边界判定。

---

## 15. 推荐实现阶段

### 阶段 1：确定性轴对齐 MVP

- 物理像素截图；
- 用户粗框；
- Lab + Scharr；
- 双向多扫描线；
- 一维聚类与鲁棒投票；
- 轴对齐四边联合求解；
- PNG + JSON 原子输出；
- 金标回归测试。

### 阶段 2：边缘簇与复杂主题增强

- 阴影/描边精化；
- 扫描带质量建模；
- 颜色相近 UI 的纹理差；
- 多尺度粗搜与原图精化；
- 诊断可视化工具。

### 阶段 3：主动传统 CV 兜底

仅当产品允许自动操作 CSP 时，可加入背景色切换或双帧差分。该能力必须是独立策略：

```text
PassiveScanlineStrategy
ActiveBackgroundDifferenceStrategy
```

主动策略不得覆盖或污染用户作品；状态恢复失败必须立即告警。主动策略仍禁止使用 AI。

---

## 16. 最终验收定义

实现只有同时满足以下条件才能标记为“完成”：

- 用户无需像素级精确框选；
- 最终 ROI 来自多扫描线共识与四边联合验证；
- 在原始物理像素上完成边界精化；
- 低置信度与歧义结果不会自动落盘；
- 成功输出严格位于 `<ART_LINE_ROOT>/memoline/`；
- 每个 PNG 都存在可验证的同名 JSON；
- 写入过程具备原子性和失败回滚；
- 金标数据达到四边误差、IoU 和错误接受率门槛；
- 实现中不存在 AI、机器学习或 OCR 边界判定路径。

本架构以“宁可拒绝，不可误裁”为最高优先级。任何为了提高自动成功率而绕过边界支持、几何验证、置信度门槛或原子写入的实现，均应视为架构违规。
