# 屏幕—画布转换存档系统：强约束架构

> 本文是 `screen_canvas_transform` 的**增量强约束契约**，专门规定**存档（Save Archive）**的建立、持久化、选择与重算入口。  
> 未在本文明示修改的条款，继续遵守 `transfomer/SCREEN_CANVAS_TRANSFORM_ARCHITECTURE.md` 及当前 `screen_canvas_transform/**` 已实现的行为。  
> 实现 MUST 使用 `MUST / MUST NOT / SHOULD / MAY` 语义。

## 1. 目标

在现有初始化与显式重算流水线之上，引入**可复用的几何锚点存档**，使画师：

1. **首次**通过完整初始化建立一份可信存档；
2. **之后每次启动**在存档界面选定已有存档，**直接进入重算**，无需再次框选 ROI 或输入画布尺寸；
3. 初始化任一环节失败时，**不得留下半成品存档**。

存档不是矩阵快照缓存，也不是笔触数据容器。它是**系统已验证的几何约束包**，供后续会话在新鲜屏幕帧上重新观测、OCR 与求解。

```text
新建存档
→ 走完整初始化流水线
→ 成功：原子写入系统几何锚点
→ 失败：无存档、无部分写入

启动软件
→ 存档选择界面
→ 用户确认某一存档
→ 跳过初始化
→ 冻结新帧
→ 按存档锚点执行显式重算
→ 发布新 TransformSnapshot
```

## 2. 与现有系统的关系

### 2.1 不变部分

以下现有能力 MUST 保留，不得因存档系统而删除或弱化：

- C++ 几何/矩阵核心、C ABI、`CaptureId` / `Generation` / `RecomputeGeneration` / `SourceRevision`；
- 初始化状态机各阶段及其失败码体系（`TransformStage`、`PipelineFailureException`）；
- 显式「重算」按键语义：重算不是旧矩阵增量累乘，失败时不得伪装当前结果；
- 坐标空间区分：`CapturePx`、`ScreenPhysicalPx`、`WorkspaceLocalPx`、`CanvasAttachedNormalized`；
- 覆盖层代次校验（`TryShowIfCaptureMatches`、`TryShowIfGenerationMatches`）。

### 2.2 新增部分

存档系统 ONLY 增加：

- 存档实体与持久化层；
- 启动时的存档选择界面；
- 「从存档启动 → 直接重算」编排路径；
- 初始化成功后的**原子落盘**钩子。

存档系统 MUST NOT 引入：

- 自动判断平移/旋转/缩放停止后触发重算；
- 通过 `PenUp`、输入空闲定时器或连续画面变化自动触发完整重算；
- 把用户粗选 ROI 当作可复用锚点写入存档。

## 3. 术语

| 名称 | 含义 |
|---|---|
| `SaveArchive` | 一份通过初始化验证、可跨会话加载的几何锚点包 |
| `ArchiveId` | 存档唯一标识（建议 UUID） |
| `InitSuccessBundle` | 初始化流水线在 `TrackingStable` 时产生的、允许落盘的最小证据集合 |
| `SystemWorkspaceRoi` | `workspace_border_detect` 纠正后的 `WorkspaceRect`，即 `PipelineResult.WorkspaceRoiScreen` |
| `SystemNavigatorThumbnailRoi` | C-II 检测得到的 `NavigatorThumbnailRect`，即 `PipelineResult.NavigatorThumbnailRoiScreen` |
| `OcrLayout` | 由系统 ROI 推导的 OCR 搜索带，而非用户框选 |
| `UserWorkspaceRoi` | 初始化时用户粗选的工作区范围；**禁止**写入存档 |
| `UserNavigatorPanelRoi` | 初始化时用户框选的导航器面板；**禁止**写入存档 |
| `ArchiveRecompute` | 加载存档后、跳过初始化、基于新鲜 `CaptureId` 的完整证据重算 |

## 4. 存档生命周期

```text
                    ┌─────────────────┐
                    │  软件启动        │
                    └────────┬────────┘
                             │
                    ┌────────▼────────┐
                    │ 存档选择界面      │
                    └───┬─────────┬───┘
            新建存档    │         │  选择已有存档
                        │         │
              ┌─────────▼──┐   ┌──▼──────────────┐
              │ 完整初始化   │   │ ArchiveRecompute │
              │ 流水线      │   │ （跳过初始化）    │
              └───┬─────────┘   └──┬──────────────┘
                  │                │
         失败：无存档          失败：存档保留，
                  │            当前会话不进入 TrackingStable
         成功：原子写入              │
                  │                │
                  └────────┬───────┘
                           │
                  ┌────────▼────────┐
                  │ TrackingStable   │
                  │ + 显式重算可用    │
                  └─────────────────┘
```

### 4.1 状态约束

- 一份 `SaveArchive` 只有两种逻辑状态：`Valid`（初始化成功写入）与不存在；**禁止** `Draft`、`Partial`、`NeedsRepair` 等中间态落盘。
- 加载已有存档后，在 `ArchiveRecompute` 成功前，旧会话的 `TransformSnapshot` MUST NOT 当作当前有效矩阵发布。
- 同一会话内，`ArchiveId` 与 `CaptureId` 独立：每次重算 MUST 使用新的 `CaptureId`，但 `ArchiveId` 不变。

## 5. 新建存档：绑定初始化流水线

新建存档 MUST 与当前 `MainWindow.RunInitializationFlowAsync` + `TransformPipelineService` 完全同序，不得缩短或重排阶段。

### 5.1 强制步骤

```text
Idle
→ 用户输入 CanvasPixelWidth / CanvasPixelHeight（无效则中止，不截图）
→ CaptureFrozen
→ SelectingWorkspaceRoi          （用户粗选 UserWorkspaceRoi）
→ DetectingWorkspace           （失败 → 初始化结束，无存档）
→ SelectingNavigatorRoi        （用户粗选 UserNavigatorPanelRoi）
→ DetectingNavigatorThumbnailCII（失败 → 初始化结束，无存档）
→ ObservingWorkspaceCanvas
→ ObservingNavigatorCanvas
→ ReadingNavigatorNumbers
→ CompletingViewportFrame（若需要）
→ SolvingTransform
→ ShowingCanvasTopLeftMarker
→ TrackingStable
→ 原子写入 SaveArchive
```

### 5.2 落盘触发点

- 落盘 ONLY 允许在 `TransformStage.TrackingStable` 且 `TransformSnapshot.Status == Ok`（或等价成功码）之后触发。
- 落盘 MUST 在 UI 向用户展示成功状态**之前或与之同一原子事务**完成；若落盘失败，UI MUST 报告「初始化几何成功但存档写入失败」，且 MUST NOT 把该会话标记为可复用存档。
- 初始化过程中用户取消（画布尺寸对话框、ROI 框选 Esc）视为**用户中止**，不是流水线失败，同样不得写存档。

### 5.3 失败语义（强约束）

以下任一情况 MUST 视为「存档建立失败」，且 MUST NOT 创建或更新任何 `SaveArchive` 文件：

| 阶段 | 代表条件 |
|---|---|
| 画布尺寸无效 | `CanvasPixelWidth <= 0` 或 `CanvasPixelHeight <= 0` |
| 工作区检测失败 | `DetectWorkspace` 非成功，或缺少 `WorkspaceBackgroundModel` |
| 导航器缩略图失败 | `DetectNavigatorThumbnail` 非成功 |
| 工作区画布观测失败 | `ObserveCanvas` 歧义且无可用边界 |
| 导航器画布观测失败 | 同上 |
| OCR 失败 | 缩放置信度过低且无合法 `InjectedScalePercent` |
| 视口补全失败 | `CompleteViewportFrame` 非 `StatusOk` |
| 矩阵求解失败 | `SolveTransform` 非成功 |
| 落盘失败 | 序列化、校验或原子写入错误 |

**禁止**写入「仅含画布尺寸」「仅含用户 ROI」「仅含失败 CaptureId」的存档。

## 6. 持久化载荷：必须保存什么

初始化成功后，存档 MUST 持久化**系统产生、且足以支撑 ArchiveRecompute 的最小集合**。

所有矩形 MUST 以 `ScreenPhysicalPx` 半开区间 `[left, right) × [top, bottom)` 存储，并附带 `CoordinateConventionVersion`。

### 6.1 必填字段

```text
SaveArchive
├── ArchiveId
├── SchemaVersion
├── DisplayName                    // 用户可读名称；默认可由创建时间生成
├── CreatedAtUtc
├── CoordinateConventionVersion
│
├── CanvasPixelWidth               // 用户输入的画布像素宽
├── CanvasPixelHeight              // 用户输入的画布像素高
│
├── SystemWorkspaceRoiScreen       // 纠正后的工作区矩形（ScreenPhysicalPx）
├── WorkspaceBackgroundModel       // 与工作区检测绑定的背景模型
│   ├── CenterLabL/A/B
│   ├── StrongDeltaE / WeakDeltaE
│   └── Confidence
│
├── SystemNavigatorThumbnailRoiScreen  // C-II 得到的缩略图矩形（ScreenPhysicalPx）
│
├── OcrLayout                      // 系统推导的 OCR 区域（ScreenPhysicalPx）
│   ├── PrimarySearchBandScreen    // 缩略图下方主搜索带
│   └── LeftHalfSearchBandScreen   // 主搜索带左半（与 NavigatorOcrService 一致）
│
└── Provenance                     // 溯源，仅供诊断
    ├── InitCaptureId              // 初始化冻结帧 CaptureId
    ├── InitGeneration             // 初始化成功时的 Generation
    ├── InitSourceRevision         // 求解输出的 SourceRevision
    └── InitCompletedAtUtc
```

### 6.2 字段来源映射

实现时 MUST 从初始化成功的 `PipelineResult` 与 OCR 服务输出填充，不得手填或猜测：

| 存档字段 | 初始化来源 |
|---|---|
| `CanvasPixelWidth/Height` | `TransformPipelineService` / `TransformSnapshotDto` |
| `SystemWorkspaceRoiScreen` | `PipelineResult.WorkspaceRoiScreen` |
| `WorkspaceBackgroundModel` | `PipelineResult.Background` |
| `SystemNavigatorThumbnailRoiScreen` | `PipelineResult.NavigatorThumbnailRoiScreen` |
| `OcrLayout.*` | 初始化成功时，用**当时的** `SystemNavigatorThumbnailRoiScreen` 与**当时的**导航器面板屏幕范围，按 `NavigatorOcrService` 同一规则计算并固化 |

说明：`OcrLayout` 在初始化时 MUST 由系统 ROI 当场计算并写入存档，而不是在重算时从用户面板重新推导。重算时 MUST 优先使用存档内固化的 `OcrLayout`；仅当 `SchemaVersion` 升级且明确迁移时才允许按新规则重算布局。

### 6.3 明确禁止写入存档的对象

以下对象 MUST NOT 作为存档复用锚点持久化：

```text
UserWorkspaceRoi
UserNavigatorPanelRoi
TransformSnapshot 内的 Screen↔Canvas 矩阵
MarkerGeometry
任意历史 CaptureId 的冻结位图
OCR 读到的 ScalePercent / RotationDegrees 数值本身
RecomputeGeneration / Generation 的当前值
```

原因：

- 用户 ROI 只是采样约束，几何真值来自检测器；
- 矩阵与标记是**会话态输出**，每次重算必须重新求解；
- OCR 数值每次重算必须在新帧上重新读取；
- 位图体积大且与显示器布局强绑定，不属于存档职责。

### 6.4 可选字段（MAY）

- `LastSuccessfulRecomputeAtUtc`
- `LastSuccessfulCaptureId`
- `Notes`（用户备注）

可选字段缺失 MUST NOT 阻止 `ArchiveRecompute`。

## 7. 启动存档选择界面

每次软件启动，主界面 MUST 首先进入**存档选择界面**，而不是直接进入初始化或重算。

### 7.1 界面要求

界面 MUST 提供：

1. **已有存档列表**：显示 `DisplayName`、`CanvasPixelWidth × CanvasPixelHeight`、创建时间；
2. **「从此存档开始 / 重算」**：对选中存档执行 `ArchiveRecompute`；
3. **「新建存档」**：进入 §5 完整初始化；
4. **删除存档**（MAY）：仅删除持久化文件，不影响内存态。

### 7.2 用户确认语义

- 选择已有存档并确认 = 用户显式授权**跳过初始化**，直接基于该存档锚点重算；
- 未选中存档时，「开始」类按钮 MUST 禁用；
- 从存档启动时，MUST NOT 再次弹出画布尺寸输入或 ROI 框选。

### 7.3 加载校验

加载存档时 MUST 校验：

- `SchemaVersion` 受支持；
- `CanvasPixelWidth/Height > 0`；
- 所有必填矩形有限、非空、宽高 ≥ 实现定义的最小 ROI（与 `CaptureSession.MinRoiSizePx` 对齐）；
- `WorkspaceBackgroundModel` 字段完整且置信度有限。

校验失败 MUST 在列表中标记该存档为不可用，并 MUST NOT 对其执行 `ArchiveRecompute`。

## 8. ArchiveRecompute：从存档直接重算

`ArchiveRecompute` 是存档系统的核心运行路径，语义上对齐现有 `TransformPipelineService.RecomputeAsync`，但输入来自 `SaveArchive` 而非内存中的 `PipelineResult`。

### 8.1 流程

```text
用户确认存档
→ HideOverlays
→ CaptureFrozen（新 CaptureId）
→ RecomputeRequested
→ ReacquiringEvidence
    ├── 注入 SystemWorkspaceRoiScreen + WorkspaceBackgroundModel
    ├── 注入 SystemNavigatorThumbnailRoiScreen（不再要求 UserNavigatorPanelRoi）
    └── 注入 OcrLayout（使用存档固化区域）
→ ObservingWorkspaceCanvas
→ ObservingNavigatorCanvas
→ ReadingNavigatorNumbers（使用 OcrLayout，禁止回退到用户面板推导）
→ CompletingViewportFrame（若需要）
→ SolvingTransform
→ ShowingCanvasTopLeftMarker
→ TrackingStable
```

### 8.2 与现有 `RecomputeAsync` 的差异

| 项目 | 现有 `RecomputeAsync` | `ArchiveRecompute` |
|---|---|---|
| 工作区 ROI | `previous.WorkspaceRoiScreen` | `SaveArchive.SystemWorkspaceRoiScreen` |
| 背景模型 | `previous.Background` | `SaveArchive.WorkspaceBackgroundModel` |
| 导航器面板 ROI | 需要 `previous.NavigatorRoiScreen` | **不需要**；禁止依赖用户面板 ROI |
| 缩略图 ROI | 每次 C-II 重新检测 | 以存档 `SystemNavigatorThumbnailRoiScreen` 为锚点；MAY 在同一屏幕区域做局部验证性 C-II，但不得要求用户框选 |
| OCR 区域 | 由当时 NavigatorRoi + Thumbnail 推导 | **必须**使用存档 `OcrLayout` |
| 画布尺寸 | `CanvasPixelWidth/Height` 内存态 | 来自存档 |

### 8.3 失败处理

`ArchiveRecompute` 任一步失败时：

- MUST 抛出或返回与初始化相同的 `PipelineFailureException` 体系；
- MUST NOT 回写或损坏原 `SaveArchive`；
- MUST NOT 自动回退到初始化流程；
- UI SHOULD 提示用户检查 CSP 窗口位置、缩放或导航器布局是否相对存档发生变化。

### 8.4 代次规则

- 每次 `ArchiveRecompute` MUST `RecomputeGeneration++` 且 `Generation++`；
- 新 `TransformSnapshot` MUST 绑定新 `CaptureId`；
- 存档文件内的 `Provenance.InitCaptureId` MUST 保持不变，仅可选更新 `LastSuccessfulCaptureId`。

## 9. 存储格式与原子性

### 9.1 位置

默认存档目录 SHOULD 为：

```text
%AppData%/ScreenCanvasTransform/archives/
```

每个存档一个文件或一个子目录，由实现选择，但 MUST 支持原子替换。

### 9.2 序列化

- SHOULD 使用 JSON 或等价的可读结构化格式；
- MUST 包含 `SchemaVersion`；
- 矩形 MUST 显式存储坐标空间标记，例如 `"space": "ScreenPhysicalPx"`；
- MUST 在写入前做 schema 内校验（必填、范围、有限性）。

### 9.3 原子写入

```text
写入 temp 文件
→ fsync / Flush(true)
→ 校验可读回
→ 原子 rename 到最终路径
```

崩溃或断电后 MUST NOT 留下可被加载的半份存档。临时文件 MAY 残留，但 MUST NOT 出现在存档列表。

## 10. 状态机扩展

在 `TransformStage` 上增加宿主编排阶段（数值由实现分配，但语义固定）：

```text
SelectingSaveArchive        // 启动后选择或新建存档
PersistingSaveArchive       // 初始化成功后落盘
LoadingSaveArchive          // 读取并校验存档
ArchiveRecomputeRequested   // 用户从存档确认重算
```

约束：

- `SelectingSaveArchive` MUST 在首次 `CaptureFrozen` 之前；
- `PersistingSaveArchive` MUST 仅在 `TrackingStable` 之后；
- `ArchiveRecomputeRequested` 之后 MUST NOT 出现 `SelectingWorkspaceRoi` 或 `SelectingNavigatorRoi`。

## 11. 服务层契约

建议新增 `SaveArchiveService`（名称 MAY 不同，职责 MUST 一致）：

```text
ListArchives() → IEnumerable<SaveArchiveSummary>
TryLoad(ArchiveId) → SaveArchive | Error
TryCreateFromInitSuccess(InitSuccessBundle, DisplayName) → SaveArchive | Error
TryDelete(ArchiveId) → bool
```

`TransformPipelineService` 建议新增：

```text
Task<PipelineResult> RecomputeFromArchiveAsync(
    CaptureSession session,
    SaveArchive archive,
    IProgress<TransformStage>? progress = null,
    CancellationToken cancellationToken = default)
```

该方法 MUST：

- 不读取 `UserWorkspaceRoi` / `UserNavigatorPanelRoi`；
- 使用存档内 `OcrLayout` 调用 OCR；
- 在失败时保持与 `RecomputeAsync` 相同的阶段报告。

## 12. 运行时安全检查

除基架构 §14 外，存档路径额外 MUST 检查：

- 存档内矩形映射到当前 `CaptureSession` 后仍与 `CaptureBounds` 有有效交集；
- `WorkspaceBackgroundModel.SourceCaptureId` 与当前 `CaptureId` 不一致是**预期行为**，不得因此拒绝重算；
- OCR 使用存档区域时，若区域映射后小于最小尺寸，MUST 失败并报告 `OcrScaleFailed`，不得静默改用用户 ROI；
- 求解前 MUST 校验 `CanvasPixelWidth/Height` 与存档一致，不得使用内存中其他值覆盖。

## 13. 测试契约

必须增加的测试（单元或契约级）：

1. **初始化成功 → 落盘内容**：断言含 `SystemWorkspaceRoiScreen`、`SystemNavigatorThumbnailRoiScreen`、`OcrLayout`、`WorkspaceBackgroundModel`，且不含用户 ROI；
2. **初始化失败 → 无文件**：覆盖工作区失败、缩略图失败、OCR 失败、求解失败；
3. **落盘中断**：模拟 rename 前崩溃，列表中不可见；
4. **加载校验**：缺字段、非法矩形、不支持版本 → 不可用；
5. **ArchiveRecompute 路径**：给定存档 + 合成帧，能走到 `SolvingTransform` 且 OCR 被调用在固化 `OcrLayout` 上；
6. **ArchiveRecompute 失败不损坏存档**：故意 OCR 失败，原文件 hash 不变；
7. **代次**：`ArchiveRecompute` 后 `Generation` 递增，`ArchiveId` 不变。

## 14. 推荐实施顺序

1. 定义 `SaveArchive` 数据模型与 `SchemaVersion`；
2. 实现 `SaveArchiveService` 原子读写与列表；
3. 在初始化 `TrackingStable` 后挂接 `PersistingSaveArchive`；
4. 实现启动存档选择界面；
5. 实现 `RecomputeFromArchiveAsync` 与 OCR 固化区域注入；
6. 接显式重算按钮：会话内重算仍走 `RecomputeAsync`，从存档启动走 `RecomputeFromArchiveAsync`；
7. 补齐 §13 测试。

## 15. 最终不可违反的原则

1. **存档只存系统几何锚点，不存用户粗选 ROI，不存矩阵。**
2. **初始化任一环节失败，绝不创建存档。**
3. **从存档启动 = 跳过初始化，直接重算；不得偷偷插入 ROI 框选。**
4. **每次重算必须新 `CaptureId`、新 OCR、新求解；不得复用初始化时的矩阵伪装当前结果。**
5. **落盘必须原子；宁可无存档，不可有半份存档。**
6. **存档内的 `OcrLayout` 在初始化成功时一次性固化，重算时原样使用（除非 schema 迁移）。**

---

*文档版本：1.0 · 适用于 `screen_canvas_transform` 存档系统增量实现*
