# Auto 模式 AI 实施提示词

你正在修改一个已有的 C++/C# `screen_canvas_transform` 程序。

## 严格工作范围（最高优先级）

本任务只允许阅读、修改和验证以下三类路径：

```text
SCREEN_CANVAS_TRANSFORM_STRONG_ARCHITECTURE.md
SCREEN_CANVAS_TRANSFORM_MODIFICATION_CONTRACT.md
screen_canvas_transform/**
```

其中：

- `SCREEN_CANVAS_TRANSFORM_STRONG_ARCHITECTURE.md` 是原始架构约束；
- `SCREEN_CANVAS_TRANSFORM_MODIFICATION_CONTRACT.md` 是本轮增量修改契约；
- `screen_canvas_transform/**` 是唯一允许修改的程序目录。

除非用户明确追加授权，禁止读取、搜索、分析、修改或引用工作区中的其它文件和目录，包括但不限于：

```text
AUTO_MODE_AI_IMPLEMENTATION_PROMPT.md
behavior_recognizer/**
workspace_border_detect/**
transfomer/**
OpenTabletDriver/**
strokebin2jsonl/**
canvas_border_autocrop/**
*.md（上述两个架构文档除外）
其它项目、测试样例、旧实现、发布目录和构建产物
```

不要为了理解背景而扫描工作区根目录、全仓库搜索符号、读取无关文档或追踪其它项目。发现依赖指向允许范围之外的文件时，停止扩展阅读范围，并仅依据 `screen_canvas_transform` 中已有的接口、类型和调用关系处理；如果确实无法继续，报告缺失信息，不要自行读取额外目录。

## 必须阅读的文件

开始实施前只按以下顺序读取：

```text
1. SCREEN_CANVAS_TRANSFORM_MODIFICATION_CONTRACT.md
2. SCREEN_CANVAS_TRANSFORM_STRONG_ARCHITECTURE.md
3. screen_canvas_transform/** 中与本次修改直接相关的源文件
```

在 `screen_canvas_transform/**` 内也不得无差别读取全部文件。先读取入口、数据模型、C ABI、矩阵核心、红框检测、画布观测、C# 主窗口/流程和直接相关测试；只有当编译错误或调用关系明确要求时，才继续读取其它文件。

除上述允许范围外，不需要检查 git 历史、其它项目的实现或工作区状态。不得为了确认背景而执行全仓库 `grep`、递归全文扫描或读取其它项目文档。

`SCREEN_CANVAS_TRANSFORM_MODIFICATION_CONTRACT.md` 是本轮修改的增量强约束；原架构中未被它明确修改的约束继续有效。

## 总目标

继续完善 `screen_canvas_transform`，但不要重写成新的系统。必须保留现有 C++ 几何/矩阵核心、C ABI、CaptureId/Generation/SourceRevision 和现有失败状态体系，在其上完成契约要求。

## 必须先做的事

1. 阅读两个架构文档；
2. 仅在 `screen_canvas_transform/**` 内阅读与本次修改直接相关的 C++ 核心、C ABI、C# 初始化流程、ROI 选择、截图、OCR、标记覆盖层和相关测试；
3. 建立简短实现计划，明确允许范围内哪些文件需要修改；
4. 先确认坐标空间和矩阵来源，再修改代码；
5. 修改后只对允许范围内的最近修改文件运行诊断、构建或测试。

不要执行以下行为：

- 不要扫描或读取允许范围之外的文件；
- 不要因为发现同名类型或旧实现而打开其它目录；
- 不要读取本提示词自身作为程序依赖；
- 不要扫描全仓库来寻找测试、配置或参考实现；
- 不要修改允许范围之外的文件；
- 不要生成工作区范围外的补丁、脚本、文档或构建产物。

如果遇到允许范围之外的依赖：

```text
先尝试使用 screen_canvas_transform 内已有的接口和类型解决；
若确实无法实施，在最终报告中说明阻塞点；
不要自行扩大文件阅读范围。
```

## 不得误解的三个对象

### 红框证据

红框首先通过 `NavigatorThumbnailRoi` 内的红色/橙红色像素、线段连续性、方向和支持率检测。颜色检测只说明当前帧实际看到了哪些红框边。

红框颜色检测不得单独决定：

- 缺失红框边；
- 完整红框语义左上角；
- X/Y 语义；
- 被裁剪部分的位置。

### WorkspaceCanvasObservation

它是工作区图像中的视觉观测结果，例如画布候选边、边界支持率、连通性、填充率和歧义信息。

### WorkspaceCanvasRelation

它不是新的 ROI，不是新的截图区域，也不是当前可见非背景包围盒的别名。它是完整画布几何模型，描述：

- 完整画布语义位置和方向；
- 当前实际可见画布区域；
- 已观测的完整画布边；
- 被 UI、Workspace 边界或屏幕边界遮挡/裁剪的边；
- 画布与 WorkspaceLocalPx 的相对位置和尺度关系。

红框证据和完整画布模型的关系必须是：

```text
红框颜色/线段/直角证据
+ NavigatorCanvasObservation
+ 仅在指定补全模式中启用的 WorkspaceCanvasRelation
→ NavigatorViewportFrame 候选
→ 唯一性、一致性、有限性验证
→ 矩阵求解
```

红框补全按 `ConfirmedCompleteEdgeCount` 分类，不按直角数量分类。只有一条连续红线的两个端点都关联到已确认红框直角，并通过方向/垂直关系验证时，才是 `CompleteEdge`。

## 矩阵来源硬约束

这是最重要的约束：

```text
ScreenPhysicalPx ↔ CanvasAttachedNormalized
```

的转换矩阵只能由当前几何证据求解，包括：

- WorkspaceRoi 和截图原点关系；
- WorkspaceCanvasRelation；
- NavigatorCanvasObservation；
- 红框位置；
- 红框有向 X/Y 轴；
- 画布与工作区的相对位置；
- 当前几何旋转；
- 屏幕物理像素坐标关系。

`ScalePercent` 与该矩阵无关。绝对禁止：

- 把 `ScalePercent / 100` 乘进 `ScreenToCanvas`；
- 把 `ScalePercent / 100` 乘进 `CanvasToScreen`；
- 用 OCR 比例替代几何尺度、平移或旋转；
- 对已经由几何求出的矩阵再次做比例缩放；
- 让 C# 宿主自行修改矩阵。

CSP `ScalePercent` 只用于：

- 橙色标记的目标显示尺寸；
- 记录当前软件显示比例；
- 独立一致性诊断；
- 复现和测试时的直接注入。

旋转矩阵角度由红框和导航器画布的有向几何求解。OCR 旋转数字不是必要输入，只能用于记录、校验和诊断。

## 新增工作区完整画布模型

实现一个明确的数据结构或等价字段集合 `WorkspaceCanvasRelation`。必须区分：

```text
FullCanvasModelWorkspaceLocal
VisibleCanvasBoundsWorkspaceLocal
```

不能把当前可见画布区域直接当成完整画布。

模型至少要表达：

- 完整画布语义左上角；
- 完整画布语义 X/Y 轴；
- 完整画布范围；
- 当前可见画布范围；
- 完整画布边证据；
- 可见裁剪边；
- 被 UI、Workspace 或屏幕遮挡的边；
- 画布像素宽高；
- Workspace 宽高和相对偏移；
- 置信度、歧义原因、CaptureId、SourceRevision。

用它仅在红框证据不足时约束补全，判断红框露出的边属于工作区 +X 还是 +Y。禁止用“长边对长边、短边对短边”机械交换 X/Y。

红框模式必须实现：

```text
0.1  无完整边，存在单条红线或多条平行红线；
0.2  无完整边，存在多个方向相交红线；
1.0  一条完整边；
2.0  两条相交完整边；
2.1  两条平行完整边，套用 1.0 并用第二边验证；
3.0  三条完整边；
4.0  四条完整边。
```

`0.0` 不进入红框补全：进入该阶段前工作区背景色和四边前置验证已经排除完全没有工作区几何证据的状态。

补全目标不是绘制缺失红框，而是恢复：

```text
OriginTopLeftDisplayed
AxisXDisplayed
AxisYDisplayed
SemanticCorners
```

所有轴方向与 Windows 屏幕坐标一致：`+X` 向右，`+Y` 向下。`0.1`/`0.2`/`1.0` 需要用工作区画布关系判断语义并补全；`2.0` 直接几何补全；`2.1` 套用 `1.0` 并验证平行边；`3.0`/`4.0` 直接恢复并验证。

候选不唯一时必须返回 `AmbiguousViewportGeometry`，不能使用上一帧或默认方向兜底。

## 初始化和重算

初始化流程第一步要求用户输入：

```text
CanvasPixelWidth
CanvasPixelHeight
```

输入无效时不得捕获屏幕或进入 ROI。

删除或不要实现：

- 自动判断平移停止；
- 自动判断旋转停止；
- 自动判断缩放停止；
- 通过 PenUp 推断视图结束；
- 通过输入空闲定时器触发完整重算；
- 通过连续画面变化自动触发完整重算。

改为增加显式“重算”按键。重算流程：

```text
用户点击重算
→ 隐藏覆盖层
→ 获取当前屏幕帧
→ 重新确认当前 ROI 和元数据
→ 重新获取必要几何证据
→ OCR 或直接注入 ScalePercent
→ 几何求解旋转和 Screen↔Canvas 矩阵
→ 校验 CaptureId/Generation/ROI 代次
→ 原子发布新快照
→ 显示新标记
```

重算不是旧矩阵增量累乘。失败时不得把旧矩阵伪装成当前新结果。

## 画布尺寸和 L 型标记

将 `CanvasPixelWidth`、`CanvasPixelHeight` 进入结构化请求和 `TransformSnapshot`。

画布参考长度使用短边：

```text
CanvasReferencePixels = min(CanvasPixelWidth, CanvasPixelHeight)
```

CSP 比例：

```text
zoom = ScalePercent / 100.0
```

目标标记尺寸：

```text
MarkerArmDisplayPx = CanvasReferencePixels * 0.05 * zoom
MarkerStrokeDisplayPx = CanvasReferencePixels * 0.02 * zoom
```

注意：这两个尺寸是标记绘制尺寸，不是转换矩阵参数。

标记锚点和方向仍必须由当前 `CanvasToScreen` 矩阵确定：

```text
Canvas(0,0) → anchor
Canvas(ε,0) → X arm direction
Canvas(0,ε) → Y arm direction
```

不要把比例同时乘入标记尺寸和矩阵。

## 坐标和 ABI 安全

所有正式转换必须显式区分：

```text
CapturePx
ScreenPhysicalPx
WorkspaceLocalPx
CanvasAttachedNormalized
```

不得把 `bounds_capture` 直接当 `bounds_screen` 使用，必须经过捕获原点转换。

必须检查：

- CaptureId；
- Generation；
- ROI generation；
- SourceRevision；
- 矩阵行列式；
- 条件数；
- 所有矩阵元素是否有限；
- 候选是否唯一；
- 标记是否越界。

新增字段通过稳定版本化 C ABI 或等价结构化接口传递，不得让 C# 复制 C++ 坐标语义。

## 实施方式

- 先修改数据模型和 C ABI；
- 再修改 C++ 观测/关系/红框补全/矩阵核心；
- 再修改 C# 初始化、重算按键和覆盖层流程；
- 增加针对一条、两条、三条、四条红框边以及工作区遮挡画布的测试；
- 增加矩阵不变性测试，确认不同 `ScalePercent` 不改变 `ScreenToCanvas` 和 `CanvasToScreen`；
- 增加标记尺寸测试，确认比例只改变标记显示目标尺寸；
- 修改后运行相关构建、测试和诊断；
- 对最近修改文件运行 linter/diagnostics，并修复本次引入的问题。

## 范围防护检查

在每次读取或修改文件前，确认路径匹配以下允许集合：

```text
SCREEN_CANVAS_TRANSFORM_STRONG_ARCHITECTURE.md
SCREEN_CANVAS_TRANSFORM_MODIFICATION_CONTRACT.md
screen_canvas_transform/**
```

如果不匹配，跳过该文件。

最终报告必须列出实际读取或修改的允许范围内文件；不得列出或引用允许范围外文件作为依据。

## 最终汇报要求

完成后必须报告：

1. 修改了哪些文件；
2. 哪些新增了 `WorkspaceCanvasRelation` 或等价完整画布模型；
3. 红框颜色检测和完整画布模型如何协作；
4. 哪些自动停止判断被删除；
5. “重算”按键如何触发流程；
6. 画布像素尺寸在哪里输入和保存；
7. 明确说明 `ScalePercent` 没有进入转换矩阵；
8. 标记尺寸如何使用 `ScalePercent`；
9. 运行了哪些测试及结果；
10. 仍存在的限制。

不要创建与本契约冲突的替代架构，不要为了通过测试而放宽失败条件，不要在证据不足时使用旧矩阵或启发式默认值。
