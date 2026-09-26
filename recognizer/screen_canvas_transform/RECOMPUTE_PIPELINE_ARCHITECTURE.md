# 重算流水线：强约束修改架构

> 本文是 `screen_canvas_transform` 的**增量强约束契约**，专门修正**显式重算（Recompute）**与**从存档启动（ArchiveRecompute）**共用的证据输入语义。  
> 实现 MUST 遵守 `MUST / MUST NOT / SHOULD / MAY`。  
> 本文**不推翻**「从存档开始 = 跳过初始化、直接进入重算」的既有结论；问题在**重算吃什么输入**，不在「存档能否接着重算」。

---

## 1. 目标

把重算从「半截初始化」（新帧 + 旧导航器面板 + 再 C-II 检缩略图）改为：

```text
新鲜 CSP 线程窗口帧
+ 已固化的系统锚点矩形（ScreenPhysicalPx）原样套到当前屏幕
+ 在固化 OCR 槽位直接读数
→ 观测（仅在锚点矩形内）→ 求解 → 发布新 TransformSnapshot
```

重算 MUST 是**锚点复用 + 证据刷新 + 矩阵重解**，MUST NOT 是**锚点再发现**。

---

## 2. 非目标（明确保留）

以下能力 MUST 保留，不得因本修改而删除：

1. **从存档开始**：加载校验通过后直接进入 `ArchiveRecompute`，不弹画布尺寸、不框选 ROI；
2. **会话内显式「重算」按钮**：在已有稳定结果上可再次重算；
3. **截图方式**：继续使用 `ClipStudioCapture`（CSP UI 线程可见窗口合成到虚拟桌面尺寸帧）；本修改**不要求**按 ROI 分别截图；
4. **初始化流水线**：新建存档时仍完整走用户 ROI → 工作区纠正 → C-II 缩略图 → OCR 布局推导 → 求解 → 落盘。

本修改 ONLY 约束：**重算路径上的几何输入从哪里来、禁止再跑哪些检测。**

---

## 3. 问题陈述（当前违规）

### 3.1 已对齐契约的路径

`RecomputeFromArchiveAsync`（从存档开始）大致正确：

- 注入 `SystemWorkspaceRoiScreen` + `WorkspaceBackgroundModel`
- 注入 `SystemNavigatorThumbnailRoiScreen`（**不**跑 C-II）
- 注入固化 `OcrLayout`，`ReadWithLayoutAsync`
- 新 `CaptureId` / `Generation++` / `RecomputeGeneration++`

### 3.2 违规路径

`RecomputeAsync`（会话「重算」按钮）当前行为：

```text
注入 previous.WorkspaceRoiScreen + Background
注入 previous.NavigatorRoiScreen（用户/面板级 ROI）
→ DetectNavigatorThumbnailCII          ← 违规：锚点再发现
→ ContinueAfterThumbnailAsync
→ OCR 可能现推布局（未强制 fixedOcrLayout）← 违规风险
```

后果：

- 与 Archive 路径语义分裂；
- 无故触发 `AmbiguousCandidates` / `NavigatorThumbnailCiiFailed`；
- 把「已验证系统锚点」降级为「再猜一遍」。

### 3.3 根因一句话

**会话重算把「导航器面板 ROI」当成可再检测的输入；正确输入应是已固化的系统缩略图 ROI 与 OCR 槽位。**

---

## 4. 统一重算语义

### 4.1 两条入口，一条核心

| 入口 | UI | 锚点来源 | 核心流水线 |
|------|-----|----------|------------|
| 从存档开始 | 「从此存档开始」 | `SaveArchive` | `RecomputeCore` |
| 会话重算 | 「重算」 | 当前会话已验证的系统锚点（见 §5.2） | **同一** `RecomputeCore` |

两条入口 MUST 在注入锚点之后汇合到**同一**核心实现，MUST NOT 再维护两套互斥的缩略图获取逻辑。

### 4.2 允许的命名（实现 MAY）

```text
RecomputeFromArchiveAsync(archive)  → 映射为 AnchorSet → RecomputeCore
RecomputeAsync(previous|activeArchive) → 映射为 AnchorSet → RecomputeCore
```

或直接让 `RecomputeAsync` 在存在 `_activeArchive` 时优先使用存档锚点；但无论编排如何，§6 禁止项 MUST 对两条入口同时生效。

---

## 5. 锚点集合 `RecomputeAnchorSet`

重算的几何输入 MUST 且 ONLY 来自下列字段（坐标空间均为 `ScreenPhysicalPx`，除非另有标明）：

| 字段 | 含义 | 初始化时如何产生 | 重算时如何使用 |
|------|------|------------------|----------------|
| `CanvasPixelWidth/Height` | 画布像素尺寸 | 用户输入 | 原样注入求解 |
| `SystemWorkspaceRoiScreen` | 系统工作区矩形 | 工作区检测纠正结果 | **原样**映射到当前帧，禁止再纠正 |
| `WorkspaceBackgroundModel` | 工作区背景模型 | 检测时确认 | 原样用于观测排除背景；禁止重估 |
| `SystemNavigatorThumbnailRoiScreen` | 系统缩略图矩形 | C-II 成功结果 | **原样**映射到当前帧，禁止再 C-II |
| `OcrLayout.ScaleSlotScreen` | 缩放数字槽 | 初始化时固化 | **原样**裁切读数 |
| `OcrLayout.RotationSlotScreen` | 旋转数字槽 | 初始化时固化 | **原样**裁切读数 |

### 5.1 从存档映射

`Archive → AnchorSet` MUST 一一对应存档字段，不得用用户粗选 ROI 替换任一系统字段。

### 5.2 从会话映射

会话重算的 `AnchorSet` MUST 来自下列之一（优先级从上到下）：

1. 当前绑定的 `SaveArchive`（若 `_activeArchive` 非空）；
2. 否则 `PipelineResult` 中已验证的系统量：
   - `WorkspaceRoiScreen`（初始化/上次成功结果中的系统工作区）
   - `NavigatorThumbnailRoiScreen`（**不是** `NavigatorRoiScreen`）
   - `Background`
   - `OcrLayoutUsed`（MUST 非空；若为空则会话重算 MUST 失败并提示重新初始化/从存档开始，不得现推 OCR 布局）
   - 画布尺寸来自 pipeline 内存态（与上次成功一致）

### 5.3 明确禁止作为重算锚点的对象

下列对象 MUST NOT 作为重算几何真值输入：

- `UserWorkspaceRoi` / 用户粗选工作区
- `UserNavigatorPanelRoi` / `NavigatorRoiScreen`（面板级）
- 上一帧 `TransformSnapshot` 矩阵
- 上一帧 OCR 读到的数值本身（数值必须在新帧重读）
- 任何「局部验证性 C-II」若会**改写**缩略图矩形（见 §6）

---

## 6. 硬禁止（重算路径）

在 `RecomputeRequested` / `ArchiveRecomputeRequested` 之后、下一次用户显式「新建存档 / 完整初始化」之前，重算路径：

| # | MUST NOT |
|---|----------|
| 1 | 弹出画布尺寸对话框 |
| 2 | 弹出或要求用户框选工作区 / 导航器 ROI |
| 3 | 调用 `DetectWorkspace` / 工作区边框纠正 |
| 4 | 调用 `DetectNavigatorThumbnail` / `DetectNavigatorThumbnailCii` |
| 5 | 重新估计 `WorkspaceBackgroundModel` |
| 6 | 根据导航器面板现推 `OcrLayout` |
| 7 | 用面积、中心、IoU 等启发式**改写**锚点矩形 |
| 8 | 失败时静默回退到 C-II 或用户 ROI |
| 9 | 复用旧 `TransformSnapshot` 矩阵冒充当前结果 |

「原样套上当前屏幕」含义：

```text
screenRect_archived  --(ScreenPhysicalPx 恒等)-->  映射到当前 CaptureSession 的 capture 子矩形
```

允许的**唯一**几何变换是坐标系换算（`ScreenToCapture` / `CaptureToScreen`）与对当前 `CaptureBounds` 的相交校验；MUST NOT 平移、缩放或「贴边纠正」矩形本身。

---

## 7. 统一核心流水线 `RecomputeCore`

```text
入口已得到 RecomputeAnchorSet
→ HideOverlays（截图前隐藏覆盖层，避免进证据）
→ CaptureFrozen
    └── ClipStudioCapture：CSP UI 线程窗口 → 虚拟桌面尺寸帧；新 CaptureId
→ ValidateAnchorsOnCapture
    ├── 各锚点矩形映射后与 CaptureBounds 有有效交集
    ├── 最小尺寸约束（工作区/缩略图 ≥ MinRoi；OCR 槽 ≥ 实现规定最小值）
    └── 失败 → PipelineFailure，不得改锚点重试检测
→ ReacquiringEvidence（仅注入，不检测）
    ├── WorkspaceRoi ← SystemWorkspaceRoiScreen
    ├── ThumbnailRoi ← SystemNavigatorThumbnailRoiScreen
    ├── Background ← WorkspaceBackgroundModel
    └── OcrLayout ← 固化槽位
→ ObservingWorkspaceCanvas
    └── 仅在 WorkspaceRoi 内观测；不得扩张/收缩 WorkspaceRoi
→ ObservingNavigatorCanvas
    └── 仅在 ThumbnailRoi 内观测；不得扩张/收缩 ThumbnailRoi
→ ReadingNavigatorNumbers
    └── MUST ReadWithLayoutAsync(OcrLayout)；禁止 ReadAsync 现推布局
→ CompletingViewportFrame（仅当工作区观测四边不全且求解需要时）
    └── 视口补全 MUST 以 ThumbnailRoi 为搜索域；MUST NOT 改写 ThumbnailRoi 锚点
→ SolvingTransform
→ ShowingCanvasTopLeftMarker / 覆盖层
→ TrackingStable
→ （若绑定存档）MAY 更新 LastSuccessfulRecomputeAtUtc / LastSuccessfulCaptureId
```

### 7.1 观测与「不矫正」的边界

- **矫正（禁止）**：改变系统工作区矩形、缩略图矩形、OCR 槽位几何。
- **观测（允许）**：在**已固定**矩形内，从当前像素估计可见画布几何、视口框等，作为求解证据。

若未来产品决定连 `ObserveCanvas` / `CompletingViewportFrame` 也省略，MUST 另开架构修订；**本文默认保留观测与必要视口补全，但锚点矩形冻结。**

### 7.2 代次

每次成功进入 `RecomputeCore` MUST：

- `RecomputeGeneration++`
- `Generation++`
- 新 `TransformSnapshot` 绑定新 `CaptureId`

存档的 `Provenance.InitCaptureId` MUST NOT 被重算改写。

---

## 8. 对现有代码的强制修改点

### 8.1 `TransformPipelineService.RecomputeAsync`

MUST 删除或旁路：

- `TrySetRoi(Navigator, previous.NavigatorRoiScreen)` 作为 C-II 前置；
- `DetectNavigatorThumbnail(...)` 调用。

MUST 改为：

- 构造 `RecomputeAnchorSet`（§5.2）；
- 若缺少 `OcrLayoutUsed` 且无绑定存档 → 失败；
- 调用与 `RecomputeFromArchiveAsync` 相同的核心（注入 thumbnail + `fixedOcrLayout`）。

### 8.2 `RecomputeFromArchiveAsync`

保持「注入存档锚点、不跑 C-II」；SHOULD 重构为调用同一 `RecomputeCore`，避免双实现漂移。

### 8.3 `MainWindow.RunRecomputeAsync`

编排可保留（藏窗、截图、进度、状态文案）；MUST NOT 在 UI 层自行触发缩略图检测。  
当 `_activeArchive` 非空时，SHOULD 优先走存档锚点（与「从存档开始」一致），以保证会话重算与存档锚点不漂移。

### 8.4 测试 MUST 覆盖

1. `RecomputeAsync` 在假环境中**零次**调用 C-II / `DetectNavigatorThumbnail`；
2. OCR 使用的矩形与锚点 `OcrLayout` 逐像素一致；
3. 缩略图矩形与 `SystemNavigatorThumbnailRoiScreen` / `NavigatorThumbnailRoiScreen` 一致；
4. Archive 入口与 Session 入口在相同 `AnchorSet` + 相同帧上得到同阶段序列（至 `SolvingTransform`）；
5. 故意制造多缩略图候选的导航器画面：Session 重算仍成功（因不跑 C-II），而不得再出现因重算触发的 `AmbiguousCandidates`。

---

## 9. 失败语义

| 条件 | 状态语义 | 行为 |
|------|----------|------|
| 锚点映射后无有效交集 / 过小 | `ReacquiringEvidence` 失败 | 提示检查 CSP 窗口位置/缩放/布局相对存档是否变化 |
| OCR 槽读数失败 | `OcrScaleFailed` 等 | 不得回退现推布局或改槽位 |
| 观测/视口/求解失败 | 与现有 `PipelineFailureException` 一致 | MUST NOT 损坏 `SaveArchive` |
| 会话无 `OcrLayoutUsed` 且无存档 | 重算拒绝启动 | 要求从存档开始或重新初始化 |

失败时 MUST NOT 自动打开 ROI 框选。

---

## 10. 与初始化的对比（验收用）

| 步骤 | 初始化（新建存档） | 重算（本文） |
|------|-------------------|--------------|
| 截图 | CSP 线程帧 | 同左 |
| 画布尺寸 | 用户输入 | 锚点 / 存档 |
| 工作区矩形 | 用户粗选 → **检测纠正** | 锚点原样套屏 |
| 缩略图矩形 | 用户面板 → **C-II** | 锚点原样套屏 |
| OCR 槽 | 由面板+缩略图**推导并固化** | 固化槽原样读数 |
| 背景模型 | 检测确认并写入存档 | 存档/上次原样 |
| 矩阵 | 求解 | 新帧再求解 |

---

## 11. 非协商条款

1. **从存档开始可以接着重算；本修改不削弱该路径。**
2. **重算禁止 C-II、禁止工作区再纠正、禁止现推 OCR 布局。**
3. **系统锚点矩形只做 Screen↔Capture 映射，不做几何矫正。**
4. **会话「重算」与「从存档开始」必须共享同一套锚点注入核心。**
5. **截图方式可保持 CSP 线程整帧；本契约约束的是算法输入，不是截图像素来源策略。**
6. **旧矩阵与旧 OCR 数值不得冒充当前结果。**

---

*文档版本：1.0 · 适用于修正 `RecomputeAsync` 与统一 `RecomputeCore` · 增量于存档系统契约*
