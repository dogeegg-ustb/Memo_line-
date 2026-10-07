# 记艺线（Memoline）事件编辑与精确撤销：强约束架构与实施说明

日期：2026-10-07  
适用仓库根：`D:/Memo_Line/Memo_Line`（下文所有路径均相对此根）  
目标产物：`Memoline_demo_csponly/Memoline.exe`（唯一入口）

---

## 0. 本文件的地位与执行者守则（必须先读完再动手）

### 0.1 地位

- 本文件是这项工作的**唯一权威**。它取代并优先于：
  - `Memoline_demo_csponly/docs/superpowers/specs/2026-10-06-csp-event-editing-integration-design.md`
  - `Memoline_demo_csponly/docs/superpowers/plans/2026-10-07-csp-event-editing-integration.md`
- 两份旧文件只作背景参考，凡与本文件冲突之处，一律以本文件为准。
- 第 1 节是用户本人确认过的规则。执行者**无权修改、放宽、"简化"或"先做一部分"**。
- 若第 1 节规则与代码现实冲突，按以下步骤处理，不得自行改变规则：
  1. 停下该阶段。
  2. 把冲突写进 `Memoline_demo_csponly/docs/architecture/IMPLEMENTATION_NOTES.md` 的"阻塞"小节。
  3. 继续做不依赖该点的其他阶段。

### 0.2 执行者守则（反偷懒条款，全部是硬约束）

1. **完整实现。** 交付代码路径中禁止出现以下内容：
   - `TODO`、`FIXME`、`NotImplementedException`、`throw new NotSupportedException("later")`
   - 空方法体、恒返回成功的桩
   - "演示用简化版"、注释掉的逻辑
   
   测试替身（fake）只允许出现在 `checks/` 或 `tests/` 目录。
2. **先写失败测试，再实现。** 每个阶段的验收测试先写好并记录失败输出，再实现并记录通过输出。两份输出都写进 `IMPLEMENTATION_NOTES.md` 对应阶段。
3. **不得削弱已有检查。** 六套检查（host、native、watcher、replay、python、node）中，任何现有断言都不得删除或放宽。若某个旧断言因新行为必须改，在 notes 里写明旧断言、新断言和理由。
4. **每阶段结束都跑全部六套检查**（命令见 §9.0），全部通过才算阶段完成。不允许"只跑相关的"。
5. **保持独立程序可用。** 以下程序必须继续能构建、运行，各自的检查也必须继续通过：`PacketReplay.exe`、`DirtyMatrixPaster.exe`、`CanvasLayerWatcher` 独立入口、`CSP_Shortcut_Manager` 现有报表功能。
6. **"须核实"事项必须先核实。** 本文件用【须核实】标出的每一项，都要先读代码或做实验确认，把结论（文件:行号或实验数据）写进 notes 再实现。不得按假设实现。
7. **不对用户真实 CSP 注入输入。** 自动检查一律使用 fake，不得向真实 CSP 发送键盘、鼠标或笔输入。实机验证只由用户在 CLIP 副本上按 §9.11 清单执行，执行者只负责交付清单和日志工具。
8. **不得修改用户的 CSP 配置文件。** 唯一例外是 §5.7 规定的、由用户在界面上明确确认的快捷键写入流程。执行者在开发机上运行测试时，只能操作测试副本。
9. **提交纪律。** 在独立分支上工作，每阶段至少一个提交，提交信息写明阶段号。不得改写已有历史，不得推送到远端。
10. **遇到不确定就停在该点，写进 notes。** 不得猜一个看似合理的实现蒙混过去。"能编译、测试是绿的"不等于完成，与本文件逐条对照才算完成。

---

## 1. 用户确认的规则（不可改动）

以下引号内为用户原话或用户确认过的表述。

### 1.1 整体流程

- Memoline 在后台记录，组织聚集性事件包。用户按下**系统级热键 Alt+M**，呼出记艺线控制面板，提供两个功能：**事件编辑**和**精确撤销**。
- **所有复现手段的最终结果都一样**："将用户原有的图层隐藏，并在其之上多了一个记艺线新建的复现图层"。
  - 最终复现图层保留，不删除。
  - 中间用于重放的临时图层遵守"复制写入 Memoline 后用快捷键删去"的流程。

### 1.2 什么是"事件"

- "这里的事件也仅仅代表在画布视图面板内的、状态为某笔刷工具的数位板事件。"
- 一件事 = 一次完整笔接触（penBegin → penEnd/penInterrupted），不是单个采样点。跨包的同一接触是同一件事。

### 1.3 什么是"脏矩阵"

- "脏矩阵并不是指 trigger 这样的一个文件夹，而是其内部的每个单张图片和其对应的影响范围以及造成其的所有事件。"
- 对应 `manifest.json` 的 `images[]`：一张主图（`image`/`nowImage`）、`bounds`、`maskImage`、`labelIds`（通过标签找到事件）。
- `dirtyMatrices`/`labels` 是标签投影，**不等于**脏矩阵。

### 1.4 脏矩阵存的是什么

- "脏矩阵本身使用像素替换合成……千万记住，脏矩阵本身存的不是这个笔的输出结果，而是 CSP 最终的渲染结果。"
- 合成规则即 `replayer/dirty matrix paster/backend/composition.mjs` 的替换规则：
  - mask=255 处：目标 RGBA 整体替换为源 RGBA，包括 alpha=0（擦除）。
  - mask=0 处：不变。

### 1.5 因果笔刷

- "只要这个笔刷显示的笔刷属性包含这些词条，就会被认为因果笔刷。"词条存在即因果，词条关闭或禁用也算。
- 包含使用因果笔刷事件的脏矩阵 = **因果脏矩阵**。
- 橡皮擦不特殊处理：属性里有读像素词条就是因果，没有就不是。

### 1.6 可信性与调度（核心）

- "默认走脏矩阵，因为快。"
- "只要历史修改碰到了后续操作的因果影响区域，旧脏矩阵就失去可信性，对应后续事件直接进入事件重放。"
- "只要这个因果笔刷和涉及范围无关，也直接用脏矩阵——因为改变了的那个因果影响不到这个地方，当然保持原样。"
- 非因果脏矩阵（"在涉及范围内但不包含因果笔刷的脏矩阵"）在 Memoline 内部直接合成。
- **"同色覆盖的话就被认定为不可信"**：后续非因果脏矩阵的笔在历史已变区域写过，但因为颜色相同没有产生像素差异（脏矩阵里没有这些像素）时，该脏矩阵失去可信性，进入事件重放。
- **因果脏矩阵整体重放**：其关联的全部合格事件都重放，包括其中的非因果笔刷事件。
- 包含被用户修改事件的脏矩阵，必须整体重放：
  - 被删除的事件"当空气"跳过。
  - 被改参数的事件用新参数。
  - 其余事件用原参数。

### 1.7 事件编辑（用户流程）

1. 用户在画布上勾选一块区域，区域通过 ScreenCanvasTransform 核心提供的矩阵映射到画布。
2. 程序给出该区域涉及的脏矩阵，**跨图层**。
3. 用户选择一个脏矩阵。鼠标悬停在它上面时，光标一定范围内用**白线**显示事件。
4. 用户用**和矢量橡皮一样的方式**选中事件（扫过即命中整条笔画），然后决定删除或更换笔刷参数。
5. 复现："从其中最早的脏矩阵的前一个脏矩阵（同一图层）中的图层总览图作为复现基础的快照"。按 §4 算法在内部合成与 CSP 重放之间交替推进，"直到最新的该图层事件也被用脏矩阵或者 strokereplay 复现出来"。
6. 事件编辑重放使用**集中模式**（`ReplayMode.Concentrated`）。

### 1.8 精确撤销

- 用户选中一个区域。程序在 CSP **当前图层**上，从选区内**可见最上层图像**的笔画中，**按时间从新到旧取 50 笔**；不足 50 笔就取实际数量。
- "精确撤销所要干的就是把一切恢复到这个区域的 50 步之前，这个恢复的路径和事件编辑是类似的。"
- "最后的 50 步，只要它是一个在视图面板用笔刷工具的事，无论是不是因果笔刷都会重放，重放后那个图层就在那了，不被删去。"这 50 笔使用**离散模式**（`ReplayMode.Discrete`）按原始时间顺序重放。

### 1.9 历史快照

- 不另存原尺寸整层快照（用户确认"2，是对的"）。
- 任意历史时刻的整层像素，由**当前整层捕获**按时间倒序写回各张脏矩阵的 `after` 像素得到（§4.2）。
- `canvas-preview.png` 只用于显示。

### 1.10 CSP 操作方式

- CSP 内的一切操作都通过"快捷键 + 在识别器提供的 OCR 位置输入数字"完成。
- CSP 默认快捷键里不存在的命令（例如图层显示/隐藏、删除图层）："记艺线也可修改快捷键配置表，加入用户很难按出的虚拟快捷键"。通过扩展 `CSP_Shortcut_Manager` 实现（§5.7）。

### 1.11 本 demo 明确不做的（用户确认属于初版优化）

- 复现图层不继承原图层的混合模式、不透明度、剪贴、锁定、文件夹位置。原层被隐藏，复现层是新建的普通栅格层，位于原层正上方。
- CSP 往返次数、笔刷文字/图标/复选框参数还原等优化问题，留到初版处理。

---

## 2. 现有代码事实（实施依据，均已核对）

### 2.1 进程与入口

- `Memoline.exe` 由 `Memoline_demo_csponly/Build.ps1` 把 `publish/win-x64/MemolineDemo.exe` 复制到 demo 根目录得到，是单个 WinForms STA 进程。
  - 入口：`source/Program.cs:12` 的 `Main`。
  - 默认路径（`Program.cs:31-39`）：`DemoController` → watcher 的 `MainWindow` → `Application.Run(window)`。
- 宿主项目 `Memoline_demo_csponly/source/MemolineDemo.csproj`：
  - 链接编译 `Organizer/canvas layer watcher/source/*.cs`（除 Program.cs，命名空间 `CanvasLayerWatcher`）。
  - 链接编译 `Organizer/dirty matrix/source/Core/Coverage.cs`（命名空间 `DirtyMatrix.Core`）。
  - 引用 `recognizer/core/memoline/Memoline.Core.csproj`。
- `DemoController.PrepareAsync`（`source/DemoController.cs:49-100`）：
  - 启动 `<runtime>\Recognizer\BehaviorRecognizer.exe`，并重写 `Recognizer\integration\settings.json`。
  - 轮询 `Recognizer\procedure\stroke\*.memoline.live.json`，得到机械流路径和 sessionId。
- **目前没有**托盘图标、全局热键、控制面板（`NotifyIcon`、`RegisterHotKey` 在源码中均无匹配）。
- clip-layer-bridge：每次调用启动一个进程，`Bridge.RunAsync(exe, args, token)`（watcher `SaveCapture.cs:12`）。
  - `inspect <clip>` 返回 `layers[{id,name,folder,is_folder,kind,uuid,canvasId,opacity_raw,visible}]`。
  - `export-id <clip> <layerId> <png> <w> <h>` 导出整画布 RGBA，返回元数据（含 `fullCanvas`）。

### 2.2 录制与捕获

- **触发**：`WatchState.Reserve`（watcher `WatchState.cs:195-211`）。宿主模式下只有**已确认的 `core.canvasViewState` 变化**和录制结束会触发捕获。
- **捕获请求**：`CaptureRequest(long Generation, long Ticks, string LayerName, View View, long[] Operations, LayerReference? Layer, string TimeSource, string TriggerKind, bool ViewPending)`（`WatchState.cs:43-49`）。
- **捕获流程** `SaveCapture.CaptureAsync`（`SaveCapture.cs:36-85`）：
  1. 等待输入空闲。
  2. 经控制管道 `requestClipSave` 由识别器发送 CSP 保存键（Ctrl+S）。
  3. .clip 稳定后复制一份快照。
  4. `inspect`，再 `export-id` **只导出触发时的当前图层**。
- **快照历史** `SnapshotHistory`（`SnapshotHistory.cs:48-140`）：
  - 按图层 id 维护两槽 `(After, Now)`。
  - **某图层第一次捕获是 baseline，没有 diff。这个窗口内该层的笔画没有任何脏矩阵图片。**
  - 之后每次调用 `LayerDiff.Compare(previous.png, new.png)`，产出 `packets/<id>/`。
  - 过时的整层 PNG 会被删除。
- **差异比较** `LayerDiff.Compare`（`LayerDiff.cs:74-175`）：
  - `TileSize=64`。
  - 预测笔迹覆盖范围内的格子做全分辨率逐像素比较：`Different(a,b) = (a.A!=0 || b.A!=0) && *(uint*)a != *(uint*)b`。
  - **预测范围外的格子先 8 倍缩小（`CoarseFactor=8`）再比较，可能漏掉细小或很淡的变化。**
  - 变化格子按 4 邻接组成连通分量，每个分量一张图片，输出 `-after/-now/-mask/-difference.png`，均为原分辨率裁切。
  - 图片的 `labelIds` = 覆盖范围落在该分量格子里的标签。
- **manifest.json**（schema `dirty-matrix-image-diff/v1`）字段：
  - `after` / `now` 描述符：`id, sessionId, generation, triggerTicks, savedTicks, width, height, layer{id,name,uuid}, rasterMetadata, recognizerStates`。
  - `labels[]`：`id, source, operationId, fromTicks, toTicks, stateIds[]`，`coverage.segments[{start{x,y},end{x,y}}]`（画布像素），以及半径/影响范围字段和 `imageIds`。
  - `images[]`：`id, bounds{left,top,right,bottom,width,height}, changedPixels, afterImage, nowImage, maskImage, differenceImage, labelIds, image`。
  - `canvasPreviewImage`：缩略图，最长边 1600。
- **没有任何代码校验图层链连续性**（本包 `after.id` 是否等于上一包 `now.id`）。
- 画布坐标：watcher `RecognitionEvidence.cs:237-243` 已按识别器的几何旋转把视图映射到画布（正旋转为顺时针）。标签覆盖段即由此算出的画布像素坐标。
- `AggregateSession`：单个 JSONL，记录 `header/dimensionSample/packet/asset/footer`。图片以 base64 资产分块存储，按 sha256 去重。
- `BundleArchive`：
  - `.memoline` 是 ZIP，schema `memoline-csponly/v2`。
  - 条目：`index.json`、`mechanical/recording.memoline`、`aggregation/events.jsonl(.br)`、`dimensions/*`。
  - 公开 API：`Seal/Verify/ReadMechanical/ReadDimensions/UpgradeDimensions/Compact/ExtractMechanical`。**没有读取包或资产的 API。**

### 2.3 识别器（BehaviorRecognizer + Memoline.Core + recorder_integration）

- **机械流帧**：`MemolineEvent(AppendId, Path, Kind, EventId, Ticks, AppendedTicks, OperationId, RelatedEventIds, DeviceSource, Data)`（`recognizer/core/memoline/MemolineWriter.cs:8-13`）。
  - appendId：每个追加帧递增。
  - eventId：只对 hardware 帧递增。
  - operationId：一次笔画内取其 penBegin 的 eventId。
  - 笔帧 kind 为 `penBegin/penSample/penEnd`；中断用 state 帧 `penInterrupted`，`relatedEventIds` 列出该笔所有帧。
- **笔帧数据**（`UnifiedInputCapture.cs:215-229`）：`x,y`（屏幕光标）、`pressure, tiltX, tiltY, normalizedPressure, contactState, deviceId, heldKeys, penDownLocation` 等。
- **落笔区域**：`penDownLocation{region,name,status,...}` 在 penBegin 时分类并冻结（`recognizer/core/memoline/PenDownLocation.cs`）。画布内的判据：`region=="canvasViewport" && status=="matched"`。
- **设备来源** `deviceSource.deviceType` 取值 `pen`、`mouse`、`keyboard`：
  - OTD 笔：`("pen","OpenTabletDriver.HID",deviceId,"deviceId")`。
  - 被动笔：`("pen","Windows.LowLevelMouseHook",null,"penSignature")`。
- **笔帧里没有"当前工具是笔刷"的标记**，必须由 `core.brushState` 与工具目录推断（§5.4.3）。
- **控制管道** `memoline-recorder-input-{pid}`（`BehaviorRecognizer/Capture/RecorderInputControlServer.cs:66-149`），每行一个 JSON 请求、一个 JSON 响应。命令：
  - `requestStates{requestId,sessionId,modules[],saveId?}`，可请求模块：`brushState, subtoolState, currentLayerState, colorState, canvasViewState, clipState, shortcuts, initializationConfiguration`。
  - `getInputControlStatus`。
  - `prepareRecordingEnd`：追加 `recordingEndRequested`，返回 `sessionId, triggerTicks, appendId`。
  - `configureInputInterception{enabled}`。
  - `restoreAutomaticInputProtection`。
  - `requestClipSave{expectedClipPath, requestId, triggerTicks?, activateCsp?}`。
- **实时管道** `\\.\pipe\MemoLine.Recognizer.<sessionId>`，频道：`keyboard, mouse, tablet, shortcuts, layers, layerstage, subtools, core.*`。
- **合成输入标记**：`RecorderInputTag = 0x4D4C5243`（"MLRC"，`Capture/WindowsInputHooks.cs:10`），放在 `dwExtraInfo`。键盘钩子和鼠标路由**忽略**带该标记的输入：既不记录，也不触发保护。
- **`core.brushState`**：`data.state` = `{ name, properties:[{key,value,type,unit,enabled,status}] }`。
  - `name` 是**子工具名**（如 "G筆"），不含父工具。
  - `key` 是语言无关的目录键，例如 `brush_size, opacity, antialiasing, stabilization, ink.color_mixing, ink.blending_mode, anti_overflow.do_not_cross_lines_of_reference_layer`。
  - 只列出**工具属性面板上可见**的属性。
  - `type` 取值 `number, checkbox, compound, highlight_index, enum, label`。
  - 实时数据另附 `valueRegions[]`（数值框的屏幕矩形）。
  - 词汇表：`recognizer/recognizer_core/panel_state` 的 `properties.sqlite3` 目录（150 个属性），键名参见 `configuration_audit.py:21-113`。
- **`core.currentLayerState`**：state 是选中图层名（字符串）。完整行信息由 `LayerOcrReader.read`（`recognizer/recognizer_core/layer_state/layer_reader.py:43`）产出 `LayerSnapshot`，含每行 `visible`、`selected`，但**不发布**。
- **`core.clipState`**：解析**最近一次保存的 .clip**，包含 `layers[{id,parent_id,depth,name,visible,effective_visible,uuid,is_folder,...}]` 与 `canvas{...,current_layer_id}`。
- **`core.canvasViewState`**：state 为 `canvasOriginScreenPx{x,y}, ocrScalePercent, ocrRotationDegrees, transform{canvasPixelWidth, canvasPixelHeight, scaleReference, cumulativeRelativeScale, rotationDegrees, scaleGeometryEstimate}`。
  - **六元素仿射矩阵没有发布**：`rawResult.snapshot.raw = {}`，原因是 `recognizer/recorder_integration/transform_host/Program.cs` 的序列化不包含字段。
  - 原生约定：`SctAffine2D` 为 `M0..M5`，`x'=M0·x+M1·y+M2`，`y'=M3·x+M4·y+M5`。
  - 画布坐标归一化为 `[0,1]`，原点在左上，+Y 向下。画布像素 = `ScreenToCanvas.Apply(x,y) × (canvasPixelWidth, canvasPixelHeight)`。

### 2.4 回放器（packetreplayer + strokereplayer）

- **读取**：`PacketArchiveReader.Read(path)`（`replayer/packetreplayer/source/PacketArchiveReader.cs:35`）**只读已封盘 bundle**。
  - `document.Mechanical.Strokes` 是**完整、未裁切**的笔画。
  - `StateHistory.At(history, eventId)` 给出该 eventId 之前的历史状态。
  - `Plan(document, packet)` 按包裁切笔画（`Clipped`），不能直接用于跨包事件清单。
- **历史状态**：`HistoricalState(BrushSnapshot? Brush, string? Layer, JsonElement Color, CanvasViewSnapshot? View)`（`ReplayState.cs:75`）。`BrushSnapshot(Name, Numbers, IgnoredProperties)` 只保留 `type=="number"`、未禁用、非 icon/text 的属性。
- **状态还原**（`PacketStateRestorer.cs`、`PacketReplayStateTable.cs`）：
  - 笔刷：工具快捷键切换，再在子工具面板 OCR 定位后点击。数值项流程：点击 `valueRegions` 框 → Ctrl+A → 输入数字 → 回车 → 校验。
  - 图层：用 `layerselectupperlayer/lowerlayer` 快捷键（用户绑定 Alt+] / Alt+[）按**图层名**逐步移动，要求名称唯一。
  - 颜色：**只校验不设置**，不一致就抛异常。
  - 视图：缩放、旋转在 OCR 框输入数字；平移用按住空格拖动。
- **笔注入** `PenReplayEngine`：使用 `InjectSyntheticPointerInput` 和 `PT_PEN`，坐标是**录制时的屏幕坐标**，依赖视图已还原为录制时的视图。
  - `ReplayMode.Concentrated`：抬笔为 Up，不留间隔。
  - `ReplayMode.Discrete`：抬笔为 UpAndLeave，笔间间隔 `min(录制间隔, 0.2s)`。
- `PacketReplayRunner.RunAsync` 写死了 `WindowsViewInput`、`PacketRecorderControl` 和真实注入器，不可注入替身。
- `WindowsViewInput` 的 SendInput **不带** `RecorderInputTag`。
- 命名空间为 `PacketReplay`、`StrokeReplay`。两边各有一个 `internal static class J`，分别位于 `PacketReplay` 和 `CanvasLayerWatcher` 命名空间；同一文件不同时 using 两者即不冲突。

### 2.5 dirty matrix paster

- `backend/composition.mjs` 的 `composePackets`：
  - 按 `triggerTicks` 再按 id 排序。
  - 逐包对 mask≠0 的像素做 RGBA 整体复制（含 alpha=0）。
  - 同 `triggerTicks` 的重叠像素必须相等，否则抛异常。
  - 输出裁到并集外框，未知像素为透明。
- `backend/packet.mjs` 的 `loadPacket` 做严格校验：
  - PNG 必须是 8 位 RGBA，尺寸等于 bounds。
  - mask 只允许透明或纯白。
  - `changedPixels` 等于 mask 计数。
  - 变化像素必须落在关联标签的 impactRange 内。
- `source/LayerClipboard.cs` 写入三种格式：私有 `MemoLine.DirtyMatrix.Layer.v1`、`PNG`（整画布）、`CF_DIBV5`（自上而下、BGRA、非预乘）。
- `source/Backend.cs` 通过 node 运行 `engine.mjs --compose`。

### 2.6 CSP 快捷键

- 用户数据目录：`C:/Users/dogeegg/AppData/Roaming/CELSYSUserData/CELSYS/CLIPStudioPaintVer1_5_0\`。搜索顺序见识别器 `catalog.py:24-34`，支持环境变量 `MEMOLINE_CSP_USER_DIR`。
- `Shortcut/default.khc` 是 SQLite，表 `shortcutmenu(_PW_ID INTEGER PK AUTOINCREMENT, menucommandtype TEXT, menucommand TEXT, shortcut TEXT, modifier INTEGER)`。
  - `modifier` 是位掩码：Alt=1、Shift=2、Ctrl=4。
  - `shortcut` 是键名文本：大写字母、`F<n>`、`DELETE`、`UP`、符号等。
- 用户当前已有的绑定：

  | 命令 | 绑定 |
  | --- | --- |
  | copy | Ctrl+C / F3 |
  | paste | Ctrl+V / F4 |
  | layernew | Ctrl+Shift+N |
  | layermergelowerlayer | Ctrl+E |
  | layerselectupperlayer | Alt+] |
  | layerselectlowerlayer | Alt+[ |
  | selectall | Ctrl+A |
  | selectdeselect | Ctrl+D |
  | redo | Ctrl+G / Ctrl+Shift+Z |

- **未绑定：`layerdelete`、`layervisiblelayer`。**
- `CSP_Shortcut_Manager` 目前**只读**：以 `mode=ro` 打开 SQLite，没有任何写入代码。
- `replayer/status change/csp_backend.py` 是独立的 HTTP 后端：用 `keybd_event` 发送按键，没有 OCR，也没有被任何程序调用。它的 `csp_commands.json` 与用户 khc 有冲突（Ctrl+G）。**本项目不集成它，也不以它作为快捷键来源。**

---

## 3. 总体架构

### 3.1 程序角色与组织

```
Memoline.exe（宿主，唯一入口，WinForms STA 进程）
 ├─ 链接编译（同一程序集）：
 │   ├─ Organizer/canvas layer watcher/source/*.cs（录制、捕获、差异，已有）
 │   ├─ Organizer/dirty matrix/source/Core/Coverage.cs（已有）
 │   ├─ replayer/packetreplayer/source/*.cs（新增链接；排除 Program.cs、PacketReplayWindow.cs、AssemblyInfo.cs）
 │   ├─ replayer/strokereplayer/source/*.cs（新增链接；排除列表与 PacketReplay.csproj 完全一致）
 │   └─ replayer/dirty matrix paster/source/ClipboardImageCodec.cs（新增，由 LayerClipboard.cs 拆出）
 ├─ source/Editing/*（新增：索引、分类、时间轴、合成、重建引擎、事务、日志）
 ├─ source/*（新增 UI：托盘、Alt+M、控制面板、选区覆盖层）
 ├─ 子进程 BehaviorRecognizer.exe（已有；修改：编辑边界、编辑事务、物理输入检测、发布变换矩阵）
 ├─ 子进程 clip-layer-bridge.exe（已有；用于捕获与校验）
 └─ 子进程 python ShortcutManager/csp_shortcut_writer.py（新增；仅在用户确认写入快捷键时运行）

开发期参考实现（不进运行时）：
 └─ replayer/dirty matrix paster/backend（Node）—— 宿主 C# 合成器的对拍基准（§5.6）
```

规则：

- 宿主**不得**通过启动 `PacketReplay.exe` 或 `DirtyMatrixPaster.exe` 窗口来实现功能，只复用它们的源码核心。
- 宿主**不得**为读取 bundle 再复制一份 `BundleArchive.cs`。`PacketReplay.csproj` 链接的就是宿主这一份，不得重复编译。
- 合成在宿主内用 C# 实现。原因：每段 RGBA 约 139 MB（4961×7016×4），不宜经文件交给 Node。Node 版保留为对拍基准和独立工具。

### 3.2 运行时数据流

```
录制中：
  BehaviorRecognizer ──机械流(.memoline.part)──► 磁盘
                     ──实时管道──► RecognizerMonitor/WatchState ──CaptureRequest──► SaveCapture ─► SnapshotHistory ─► packets/<id>/
                                                                                    └► AggregateSession(.jsonl.part)
Alt+M：
  GlobalHotkey ─► DemoController.BeginEditSessionAsync
     1. prepareEditBoundary（识别器追加边界帧，等笔抬起）
     2. 边界捕获：一次保存，导出所有"自上次捕获后有笔画"的图层 → 各自产出 packet
     3. LiveSessionReader 读取 ≤ 边界的机械帧 + 全部 packets → EditIndex
     4. 面板：选区、脏矩阵列表、白线、选择、参数
     5. ReconstructionEngine（§4）+ CspEditTransaction（§6）执行
     6. 复现层登记为该层新 baseline；结束事务；恢复后台记录
```

### 3.3 并发与串行

- 编辑执行与后台捕获、封盘互斥：把 `MainWindow._captureSerial`（watcher `MainWindow.cs`）提升为宿主可访问的 `CaptureCoordinator`，内部仍是同一个 `SemaphoreSlim`。
- 编辑事务期间设置 `monitor.CaptureRequestsPaused = true`。事务结束、复现层 baseline 登记完成后再恢复。
- 状态请求（`requestStates`）在事务期间**必须可用**，不得被锁死。

---

## 4. 核心算法（必须逐条实现）

### 4.1 每层的历史时间轴

对图层 L（以 clip 层 UUID 标识，不用名称），收集其所有 diff 包，按 `now.triggerTicks` 升序排列。同一包内的多张图片属于同一"捕获组"，它们是互不相交的连通分量，合成顺序无关。

**链完整性校验（必须实现；失败则该层不可精确编辑）：**

1. 第一个 diff 包的 `after` 必须是该层的 baseline 快照，来源为以下之一：
   - §5.3.2 规定的录制开始 baseline；
   - "录制中新建且确认为空"的层，此时 baseline 为全透明。
2. 对每个 k>1，`P_k.after.id == P_{k-1}.now.id`，且 `width/height/layer.uuid/sessionId/generation` 全部一致。
3. 编辑边界捕获（§5.3.3）的包必须是链上最后一个包，其 `now` 即"当前整层捕获" `C_L`（原尺寸 RGBA，来自 `export-id`）。
4. `LayerDiff` 必须已改为全分辨率比较（§5.3.1）。旧录制（未改前）的包标记为 `ChainLossy`，拒绝精确编辑，并在界面说明原因。

### 4.2 历史整层的倒推（取代原尺寸快照）

```
H_after(P_n) = C_L                          // 边界捕获
for k = n down to 1:
    H_before(P_k) = H_after(P_k) 在 P_k 所有图片的 mask 像素上写回 after 像素
    H_after(P_{k-1}) = H_before(P_k)
```

- after 图片在 mask 外的像素为 0，不可读，只按 mask 写回。
- 自检（必须实现为测试）：从 `H_before(P_1)` 正向依次写入各图片的 now 像素，结果必须逐字节等于 `C_L`。
- 内存：
  - 整层 RGBA 用 `byte[]`，4961×7016 时约 139 MB。
  - 差异集 `D` 用位集，约 4.4 MB。
  - 最多同时持有 A（实际层）、H（历史游标）、D、捕获缓冲四份。超过即视为实现错误。

### 4.3 合格事件与分类

一个"合格事件"（EditableStroke）必须同时满足：

1. 是完整笔接触：存在 penBegin，并以 penEnd 或 penInterrupted 结束；所有帧的 appendId ≤ 编辑边界。
2. `deviceSource.deviceType == "pen"`。
3. penBegin 的 `penDownLocation.region == "canvasViewport"` 且 `status == "matched"`。
4. 笔画开始时（`StateHistory.At(history, beginEventId)`）的 `core.brushState` 已确认：状态为 changed/unchanged，name 不是 unknown/ambiguous；且 name 在工具目录中解析到笔刷类工具组（§5.4.3）。
5. 笔画开始时的 `core.currentLayerState` 已确认，并能在相应保存的 clipState 中唯一映射到图层 UUID。
6. 不在任何编辑事务区间内（§5.1.2）。
7. 不受撤销/重做影响（§4.8 规则 U）。

不满足的事件不可被选择。它们所在的脏矩阵若必须重放，整个计划停下并报告具体原因（§4.8）。

- **身份**：`EditableStrokeKey = (sessionId, penBegin.appendId)`。operationId 只用于和标签关联（`label.operationId`）。
- **事件 → 图片**：图片 `labelIds` → 标签 → `operationId` → 事件。
  - **只保留历史图层等于该图片图层的事件。** 窗口里其他图层的笔画可能因为覆盖范围重叠被错误关联，必须按图层过滤。
  - 同一事件可能关联多个包的多张图片，全部记录（双向索引）。
- **因果笔刷**：事件开始时，brushState.properties 中**任意一项的 key 属于因果词条表**（不论 status、enabled、value），该事件即为因果事件。词条表见 §5.4.4。
- **因果脏矩阵**：图片关联的合格事件中至少有一个因果事件。图片若关联了不合格事件，另记 `HasUnreplayableEvents`。

### 4.4 几何量（全部以画布像素、像素级位集表示）

对事件 s：

- **写入范围 W(s)**：
  - 取 s 的画布折线：跨包合并该 operationId 所有标签的 `coverage.segments`，按时间顺序排列。
  - 每段膨胀为胶囊，半径用该标签记录的覆盖半径，与 `LayerDiff`、`Coverage.cs` 的预测一致。
  - 修改了 `brush_size` 的事件，新半径用 `CoverageSettings` 的同一公式以新尺寸重算。
  - 参数修改事件的写入范围 = 旧 W ∪ 新 W。
- **探针 Probe(s)**：用于检测同色覆盖。
  - 取 s 折线上**接触中的采样点**连成中心线，把像素中心到折线距离 ≤ 0.75 px 的像素栅格化，得到单像素宽的中心线。
  - 只有当 s 的属性 key **全部**属于"必然落墨白名单"（§5.4.5）时，Probe(s) 才取这条中心线；否则 Probe(s) = W(s)（保守）。
- **影响区域 I(s)**（只对因果事件定义）：
  - 默认 I(s) = W(s)。这覆盖混色、模糊、合成模式等在笔尖下读取已有像素的情形。
  - 属性含复制图章类词条，或读取来源无法界定：I(s) = 整层。
  - 属性含 `anti_overflow.*`：I(s) 在本层取 W(s)，同时作用于**本次请求涉及的所有其他图层**的同一像素范围。参考图层身份未知，按保守处理。

对图片 m：

- `Mask(m)`：maskImage 中值为 255 的像素（画布坐标）。
- `Events(m)`：m 关联的合格事件。
- `W(m) = ∪ W(s)`，`Probe(m) = ∪ Probe(s)`，`I(m) = ∪ I(s)`（s 为 m 中的因果事件）。

### 4.5 可信性判定（每张图片，在时间轴推进中判定）

设 `D` 为"历史已变区域"：在历史 `H_before(m)` 对应时刻，当前实际层 A 与历史游标 H 不相等的像素集合。

图片 m **不可信**（必须整矩阵重放），当且仅当满足以下任一条：

1. **被修改**：Events(m) 中含有被用户删除或改参数的事件。
2. **因果受影响**：m 是因果脏矩阵，且 `I(m) ∩ D_eff ≠ ∅`。
3. **同色覆盖**：m 是非因果脏矩阵，且 `(Probe(m) ∩ D_eff) \ Mask(m) ≠ ∅`。
   - 含义：m 的笔在已变区域写过（探针命中），但历史上那里没有变化，说明写入颜色与当时底色相同；现在底色变了，旧脏矩阵缺少这些像素。
4. **共享接触闭包**：m 与一张已判为不可信的图片共享同一事件（同一笔跨图片或跨包）。处理方法见 §4.6 的两遍法。

其余图片可信，直接合成。

`D_eff = D ∪ F_P`。F_P 是尚未执行的待重放批次的**预计改变区域**（§4.6）。

### 4.6 两遍法：静态计划 + 在线精确执行

**第一遍：静态计划（纯函数，可完全离线测试）**

- 输入：EditIndex、EditRequest。
- 用估计的已变区域 R_est 代替 D，按时间轴推进判定。R_est 的增长规则：
  - 删除事件 a：`R_est ∪= W(a) ∩ ⋃Mask(images(a))`。
  - 改参数事件 a：`R_est ∪= (旧W(a) ∩ ⋃Mask(images(a))) ∪ 新W(a)`。
  - 被重放的因果事件 e：`R_est ∪= W(e)`。它的输出依赖底色，底色变了，输出可能变。
  - 被重放的非因果、未修改事件不增长 R_est：输出与底色无关，与历史相同。
- 可信合成的图片 m：`R_est := R_est \ Mask(m)`。被 m 覆盖的像素重新与历史一致。
- 共享接触闭包要做到不动点：若某不可信图片的某个事件也关联了更早、已判可信的图片，就把那张也改为不可信，并从最早受影响的位置重算，直到不再变化。
- 输出：
  - 每层的分段序列 `[Compose(images...), Replay(events...), Compose(...), ...]`。
  - 每段的原因：触发了哪条规则、哪些像素样本。
  - 供界面预览。

**第二遍：在线执行（按时间轴推进，使用精确 D）**

```
A := H := H_before(最早受影响图片)        // §4.2 倒推得到；"最早的脏矩阵的前一个脏矩阵"的总览
D := ∅ ; P := [] ; F_P := ∅
for m in 时间轴（从最早受影响图片到最后一张）:
    untrusted := 静态计划判 m 不可信
                 || 规则2/3 用 D_eff = D ∪ F_P 判为不可信
    if untrusted:
        P += Events(m)（按 EditableStrokeKey 去重）
        F_P ∪= m 的预计改变区域（同 R_est 增长规则）
        H := H 在 Mask(m) 上写 now 像素
        continue
    // m 可信
    if P 非空:
        A := Flush(A, P)                  // §6.2：在 CSP 中粘贴 A、重放 P、捕获
        D := { p : A(p) ≠ H(p) }          // 精确
        P := [] ; F_P := ∅
        if 规则2/3 用精确 D 判 m 不可信:
            if m 与已合成图片共享事件: 停止，报告 RuntimeClosureViolation（不得硬贴）
            P += Events(m); F_P ∪= ...; H := H⊕now(m); continue
    A := A 在 Mask(m) 上写 now 像素
    H := H 在 Mask(m) 上写 now 像素
    D := D \ Mask(m)
end
if P 非空: A := Flush(A, P)
最终：InstallReproductionLayer(A)        // §6.3
```

强制要求：

- 在线执行只能把静态判为可信的图片改判为不可信，**不得反向改判**。
- Flush 的事件清单 P：
  - 按原生 penBegin eventId 升序排序，同一事件只出现一次。
  - 每笔的采样按原生 appendId 顺序注入。
  - **不得按图片、文件名或包分组后再拼接。**
- 合成严格按时间轴：不得先合成未来的图片，再回放更早的事件。
- 同一捕获组中既有合成又有重放时：组内图片互不相交，因此把该组中可信图片的合成延后，放到组内所有不可信图片的事件 Flush 完成之后、下一组之前执行。可信图片的 mask 与不可信图片不相交，结果等价。必须有测试覆盖。

### 4.7 精确撤销

1. **确认 CSP 当前图层**：`currentLayerState` 名称 → 保存后的 clipState 中唯一 UUID。不唯一则停止。
2. **选区 Rsel**：按 §7.2 映射为画布像素位集。
3. **可见表层图片**：该层时间轴上，`Mask(m) ∩ Rsel` 中至少有一个像素未被更晚图片的 mask 覆盖的图片 m。
4. **选出 S**：可见表层图片的合格事件中，取 `W(s) ∩ Rsel ≠ ∅` 的事件，按 penBegin eventId **从新到旧**取前 50 个。不足 50 取全部。
5. **叠放闭包**：若某个未选事件 u 比 S 中某事件 s 晚，且 `W(u) ∩ W(s) ≠ ∅`，就把 u 加入 S，重复到不动点，以保持原有叠放顺序。界面显示"50 + k"以及额外加入的原因。
6. **基础重建**：以 `EditRequest{ 对 S 中每个事件 Delete }` 运行 §4.6 引擎，得到"去掉 S 后的整层" A。
7. **离散重放**：安装复现层（§6.3）后，在**该复现层**上以 `ReplayMode.Discrete` 按原始顺序、用原参数重放 S 的全部事件。之后不删除该层，并做最终捕获和日志（§6.3 第 6 步）。
8. **停止条件**：
   - S 中出现不合格事件（候选只取合格事件，正常不应出现）：停止并报告。
   - 撤销次数上限可读，且小于 |S| + 5：停止并报告。CSP 撤销次数设置的存储位置【须核实】。读不到时，在界面提示用户检查"撤销次数 ≥ |S|+5"，由用户确认后继续。

### 4.8 一律停止并报告的情形（不得绕过）

每种情形都要有具体错误码、界面中文说明和测试：

| 错误码 | 条件 |
| --- | --- |
| `ChainBroken` / `ChainLossy` | §4.1 链校验失败，或旧录制使用了粗略比较 |
| `NoBaseline` | 受影响图层没有先于其首个事件的 baseline |
| `UnreplayableEventInMatrix` | 必须重放的图片含不合格事件（鼠标笔画、非笔刷工具、画布外、未确认状态、填充/粘贴/变换/滤镜等非笔画修改），或含 `low-resolution-fallback` 标签（变化无法由笔画解释） |
| `UndoRedoInRange`（规则 U） | 从最早受影响事件到边界之间，该层时间窗内出现撤销/重做命令（`PacketStep.Kind=="command"`）。被撤销的笔画是否生效无法确定，不得猜 |
| `RuntimeClosureViolation` | 见 §4.6 |
| `UnsupportedOverride` | 用户改了执行器不能还原、不能读回确认的参数 |
| `ColorUnrestorable` | 历史颜色无法在 CSP 中设置并确认（§5.5.4） |
| `ViewUnrestorable` / `BrushUnrestorable` / `LayerIdentityUnconfirmed` | 回放器还原失败 |
| `PhysicalInputDuringTransaction` | 事务中检测到物理输入 |
| `PasteNotExact` / `CaptureMismatch` | 粘贴后导出与 A 不逐字节相等，或捕获的尺寸、身份不符 |

---

## 5. 各程序修改清单

### 5.1 识别器 `recognizer/Recognizer/src/BehaviorRecognizer`（原生）

#### 5.1.1 可重复的编辑边界

- 修改文件：`Capture/RecorderInputControlServer.cs`、`Capture/UnifiedInputCapture.cs`；发布频道还需修改 `recognizer/core/memoline/Realtime/RecorderRealtimeHub.cs`。
- 新命令 `prepareEditBoundary{requestId}`：
  1. 在输入单消费者队列中等待当前笔接触结束，不得截断正在进行的笔画。可取消，超时 30 s。
  2. 追加 state 帧 `editBoundaryRequested{requestId}`。
  3. 返回 `{success, sessionId, requestId, appendId, triggerTicks}`。
- 同一 requestId 的请求是幂等的，返回同一结果；不同 requestId 可多次请求。
- **不得**复用 `prepareRecordingEnd`，不得写 footer。
- 实时 hub 在 `system` 频道发布 kind `editBoundaryRequested`。

#### 5.1.2 编辑事务

- 新命令 `beginEditTransaction{transactionId}` / `endEditTransaction{transactionId, outcome}`：
  - begin：追加 state 帧 `editTransactionBegan`，内部进入事务模式，并启用输入拦截（等价于 `configureInputInterception{enabled:true}`）。
  - end：追加 `editTransactionEnded`，恢复拦截设置。
  - 两者都返回 appendId。
- 事务模式下的物理输入判定：
  - OTD 模式：任何 OTD HID 笔帧都是物理输入，因为合成指针注入不经过 OTD。
  - 被动笔模式：鼠标钩子收到笔签名消息，且 `LLMHF_INJECTED` 未置位，即为物理输入。
  - 键盘、鼠标：既不带 `RecorderInputTag`，`LLMHF_INJECTED/LLKHF_INJECTED` 也未置位，即为物理输入。
- 检测到物理输入时：立即在 `system` 频道发布 `physicalInputDuringTransaction{transactionId, deviceType, appendId}`，并照常记录该输入。
- 被动笔帧和鼠标帧的数据新增 `injected: bool`（来自 `LLMHF_INJECTED`），供索引排除合成输入。【须核实】`WindowsMouseInputRouter.cs:13-29` 的现有逻辑，确认修改点。
- 事务区间内的所有原生笔帧，不论来源，一律不进入可编辑索引（§4.3 第 6 条）。

#### 5.1.3 测试（`recognizer/Recognizer/tests/BehaviorRecognizer.Tests`）

- 边界可重复、幂等；不截断进行中的笔画；不写 footer。
- 事务帧成对出现；事务内的物理笔、键输入产生事件；带标记的合成输入不产生事件。
- `injected` 字段正确。

### 5.2 `recognizer/recorder_integration`（Python）与变换宿主

#### 5.2.1 发布变换矩阵（必须）

- 修改 `recognizer/recorder_integration/transform_host/Program.cs` 的序列化，在 `core.canvasViewState` 的 `state.transform` 下新增：
  ```json
  "matrices": { "screenToCanvas": [m0,m1,m2,m3,m4,m5], "canvasToScreen": [m0,m1,m2,m3,m4,m5],
                "convention": "sct-affine-row-major/v1", "canvasSpace": "normalized" }
  ```
  数据取自 `TransformSnapshot.ScreenToCanvas/CanvasToScreen`。
- 对应修改 `state_timeline.py:35-42`，把 matrices 纳入语义状态。
- 一致性测试：
  - 已知视图下，`canvasToScreen(0,0)` 等于 `canvasOriginScreenPx`。
  - 与 watcher `RecognitionEvidence.cs:237-243` 的旋转映射在 ±0.5 px 内一致。

#### 5.2.2 图层行定位器（隐藏眼睛图标的后备路径）

- 修改 `recognizer/recognizer_core/layer_state/core.py` 与 `layer_reader.py`：`process` 除了原有字符串结果，还在 `rawResult.layerLocator` 中发布：
  ```
  {panelRoi, captureFrameId, rows:[{rowBounds, eyeBounds, name, selected, visible}]}
  ```
- 所有字段必须来自同一帧。
- `currentLayerState` 的语义值仍为选中名称，保持兼容。
- 新建测试 `recognizer/recorder_integration/tests/test_layer_locator.py`。

#### 5.2.3 颜色输入定位（颜色还原必需）

**用户确认（2026-10-07）：颜色涉及两个不同的面板，职责必须分开。**

| 面板 | 外观 | 用途 |
| --- | --- | --- |
| **色环面板**（现有识别对象） | 色相环 + 中间饱和度/明度方块；左下角主色/副色/透明色圆；底部一行小字 `■0 ■0 ■0` 显示 RGB | **只读识别**。Memoline 现有的 `core.colorState` 识别的就是它，保持不变 |
| **色彩滑块面板**（新增操作对象） | 左侧竖排页签 `RGB / HSV / CMYK`；R、G、B 三行横向滑条，每行右侧是数字框和上下箭头；下方是主色/副色/透明色的胶囊形色块 | **写入颜色**。记艺线把 RGB 数值点进右侧数字框输入 |

- 这两个是 CSP 工作区中**不同的面板**，通常位于同一面板组的不同页签，同一时刻可能只有一个可见。
- 不得用色环底部的小字 RGB 作为输入目标；它只用于显示。
- 不得把颜色识别迁移到滑块面板；识别仍走色环。

**新状态模块 `colorInputState`**（只针对色彩滑块面板）：

- 用 OCR 定位面板，判据为左侧竖排页签文字 `RGB`、`HSV`、`CMYK`，以及 R/G/B 三个行标签。
- 输出：
  ```
  { panelRoi, captureFrameId, activeTab: "rgb|hsv|cmyk|unknown", activeColorSlot: "main|sub|transparent|unknown",
    valueRegions: [{channel:"r|g|b", value, screenBbox, status}], tabRegions: [{tab, screenBbox}], status }
  ```
  - `screenBbox` 是行右侧数字框的屏幕矩形，**不是**滑条，也不是上下箭头。
  - `activeColorSlot` 根据下方三个胶囊色块中哪一个带高亮边框来判定。
- 纳入 `requestStates` 的可请求模块，服务端 `RecorderStateRequest.cs:13` 同步修改。
- 实现方式参照 `recognizer/recognizer_core/panel_state/value_regions.py`。
- 状态取值：
  - `ok`
  - `panelNotVisible`：滑块面板所在页签未被选中，或面板不在工作区。
  - `tabNotRgb`：当前页签为 HSV 或 CMYK。
  - `ambiguous`

**颜色写入流程**（`ColorRestorer`，见 §5.5.4）：

**用户确认：平时录制用色环面板；使用编辑功能时，由用户手动切换到色彩滑块面板。程序不自动切换面板页签。**

1. **执行前检查（在 §6.1 事务开始之前）**：若计划中有任何重放段的历史颜色与当前颜色不同，执行前先请求 `colorInputState`。
   - 若不是 `ok`（`panelNotVisible` 或 `tabNotRgb`）：面板提示"请把颜色面板切换到【色彩滑块 · RGB】页，然后点继续"，并在覆盖层上标出面板组的大致位置。
   - 用户点"继续"后再次请求，直到 `ok` 或用户取消。
   - 这一步不发送任何点击。
2. **事务中**，每次需要换颜色时：
   1. 再请求一次 `colorInputState`，必须是 `ok`。否则停止，报 `ColorUnrestorable`（原因"颜色滑块面板已不可见"），不自动切换。
   2. 若 `activeColorSlot != main`：点击主色胶囊，然后重新请求确认。这一步是在面板内点击，不是切换面板。
   3. 依次对 R、G、B 数字框执行 `SetNumberAsync`，然后再次请求 `colorInputState`，读回三个值。
3. **颜色确认**：事务期间色环面板不可见，`core.colorState` 可能不更新。因此颜色以 `colorInputState` 读回的 RGB 为准。`core.colorState` 若有新的已确认结果，也必须一致，不一致就停止。
4. **事务结束后**：提示用户"可切回色环面板继续录制"。录制期间色环不可见时，`core.colorState` 缺失或未确认属于预期情况；此时开始的笔画按 §4.3 第 4 条规则处理（状态未确认，判为不合格）。界面在托盘提示中说明这一点。

**【须核实】**

- 滑块面板的工作区布局 ROI 是否已在识别器的工作区布局状态中注册；未注册则需新增。
- 用户切回色环后，色环识别能否恢复。
- 笔刷状态 `core.colorState` 在色环不可见时的行为：是报未知，还是保留旧值。如果保留旧值，必须标记为过时，不能当作已确认。

未完成核实之前，`ColorRestorer` 只做校验，颜色不同即报 `ColorUnrestorable`。

### 5.3 watcher `Organizer/canvas layer watcher/source`

#### 5.3.1 全分辨率比较（必须）

- `LayerDiff.Compare`：删除预测范围外格子的 8 倍缩小粗比较，**所有格子**都用同一 `Different` 判据做全分辨率逐像素比较。
- 保留 `search` 元数据字段，但含义改为全分辨率。新增 `"comparison": "fullResolution/v2"` 写入 manifest。
- 性能要求：4961×7016 全比较，单次 < 400 ms（Release，开发机）。

#### 5.3.2 录制开始的全层 baseline（必须）

- 录制开始时（`MainWindow.StartAsync` 中，`PrepareAsync` 之后、首次捕获之前）：
  1. 保存一次。
  2. 对 `inspect` 得到的**每个非文件夹栅格图层**执行 `export-id`，作为该层 baseline。
  3. 通过新增的 `SnapshotHistory.SeedBaseline(LayerSnapshot)` 写入层栈，**不生成包**。
- 必须确认这次保存早于任何合格笔输入：比较保存前后的原生 appendId，期间有 penBegin 就整体重做。最多重试 3 次，仍失败则提示用户。
- 录制中出现的新图层 UUID（不在开始结构中）：
  - 首次捕获时，若 inspect 显示它由新建空层产生，baseline 记为全透明，并标记 `BaselineSource:"createdEmpty"`。
  - 【须核实】inspect/clipState 能否区分新建空层与粘贴、复制得到的层。不能区分就不建 baseline，该层首窗口的事件标记 `NoBaseline`。
- 改完后，已有 baseline 的层在首次捕获时产生 diff 包，而不是 baseline 包。

#### 5.3.3 多图层编辑边界捕获

- 新增 `WatchState.ReserveEditBoundary(string requestId, IReadOnlyList<LayerReference> layers)`，以及 `TriggerKind = "editBoundary"`。
- `SaveCapture` 新增多层变体：一次保存，对每层执行 `export-id`，再对每层调用 `SnapshotHistory.Advance`。所有层的包共享同一 `triggerTicks`，即边界帧的 ticks。
- 需要捕获的层 = 当前层，加上自上次捕获以来有笔画落在其上的所有层（不论笔画是否合格；按笔画开始时的历史图层判断）。
- 通过 `CaptureCoordinator` 串行执行，与普通捕获互斥。

#### 5.3.4 链校验与复现层 baseline

- `SnapshotHistory` 新增 `VerifyChain(layerUuid)`：规则同 §4.1，返回具体断点。
- `SnapshotHistory` 新增 `RegisterBaseline(LayerReference layer, string fullPngPath, long triggerTicks)`：复现层安装后，把 Memoline 已知且导出核对过的最终 RGBA 登记为该新层的 baseline。此后该层上的新笔画会正常产生 diff。

#### 5.3.5 测试（`Organizer/canvas layer watcher/checks`）

- 预测范围外 1 像素、alpha=1 的变化必须被检出。
- 开始 baseline 使首窗口的笔画产生 diff 图片。
- 多层边界捕获为每层各产出一个包。
- 能检测出链断点。
- 复现层 baseline 登记后，下一次捕获产生 diff。

### 5.4 宿主 `Memoline_demo_csponly/source`

#### 5.4.1 项目链接（`MemolineDemo.csproj`）

```xml
<Compile Include="../../replayer/packetreplayer/source/*.cs"
         Exclude="../../replayer/packetreplayer/source/Program.cs;../../replayer/packetreplayer/source/PacketReplayWindow.cs;../../replayer/packetreplayer/source/AssemblyInfo.cs"
         Link="PacketReplay/%(Filename)%(Extension)" />
<Compile Include="../../replayer/strokereplayer/source/*.cs"
         Exclude="（与 replayer/packetreplayer/source/PacketReplay.csproj 第 14 行完全相同的排除列表）"
         Link="StrokeReplay/%(Filename)%(Extension)" />
<Compile Include="../../replayer/dirty matrix paster/source/ClipboardImageCodec.cs" Link="Paster/ClipboardImageCodec.cs" />
```

- 【须核实】实际文件名（AssemblyInfo.cs、PacketReplayWindow.cs 是否存在）。
- 类型冲突只能用命名空间或 `using` 别名解决，**不得**复制粘贴源码。
- 宿主检查项目 `checks/MemolineDemo.Checks.csproj` 继续引用宿主项目，不另行链接。

#### 5.4.2 `source/Editing/` 新文件与职责（每个文件一项职责）

| 文件 | 职责与主要接口 |
| --- | --- |
| `EditingModels.cs` | 值类型：`LayerKey(SessionId, Generation, Uuid)`、`EditableStrokeKey(SessionId, ulong BeginAppendId)`、`DirtyImageKey(LayerKey, PacketId, ImageId)`、`PixelMask`（带外框的位集，支持并、交、差、计数、判空）、`EventChange(bool Delete, string? BrushName, IReadOnlyDictionary<string,double> Numbers)`、`EditRequest(EditKind, LayerKey? CurrentLayer, PixelMask Selection, IReadOnlyDictionary<EditableStrokeKey,EventChange> Changes)`、`PlanIssue(Code, Message, DirtyImageKey?, EditableStrokeKey?)` |
| `LiveSessionReader.cs` | `ReadAsync(mechanicalPartPath, diffRoot, ulong boundaryAppendId, CancellationToken) -> SessionSnapshot`。用 `MemolineReader` 读机械流，校验 CRC、appendId 连续、session、频率；忽略未写完的尾帧，但不掩盖损坏帧；只取 ≤ boundary 的帧。只读方式读取 `packets/*/manifest.json` 与 PNG，不移动文件。用 `StateHistory.Resolve` 得到状态历史，用 `MemolineReplayReader.Parse` 得到完整笔画 |
| `BrushClassifier.cs` | `Classify(JsonElement brushState) -> BrushClass{ Eligible, Causal, CausalTerms[], ProbeGuaranteed, Reasons[] }`。词条表与白名单从嵌入资源 JSON 读取（§5.4.4–5.4.5），带版本号 |
| `ToolCatalog.cs` | 读取识别器发布的 `toolCatalog`（来自 `shortcuts/configurationUpdated` 或 `requestStates: shortcuts`），按工具树把子工具名解析到父工具组。【须核实】`recognizer/recorder_integration/catalog.py:120-147` 的数据形状 |
| `EditIndex.cs` | `EditIndexBuilder.Build(SessionSnapshot) -> EditIndex`：合格事件、图片、双向关联、每层时间轴、链校验结果、`PlanIssue[]` |
| `Footprints.cs` | W(s)、Probe(s)、I(s) 的像素栅格化（胶囊、单像素中心线）。半径公式复用 `Coverage.cs`，不另写一份 |
| `LayerTimeline.cs` | §4.2 的倒推与正向游标；按 mask 写回或写入；自检 |
| `ImageCompositor.cs` | 实现 §1.4 的替换规则；输入校验与 `packet.mjs` 等价（PNG 格式、尺寸、mask 取值、changedPixels 计数、同组重叠像素相等） |
| `ReconstructionPlanner.cs` | §4.6 第一遍，纯函数 `Build(EditIndex, EditRequest) -> ReconstructionPlan` |
| `ReconstructionEngine.cs` | §4.6 第二遍，`RunAsync(plan, ICspReconstructionExecutor executor, CancellationToken) -> ReconstructionResult`。所有 CSP 动作都经接口完成，便于 fake |
| `PreciseUndoSelector.cs` | §4.7 第 3–5 步 |
| `SelectionGeometry.cs` | 屏幕多边形 → 画布像素位集（使用 §5.2.1 的矩阵）；悬停命中；矢量橡皮式扫选 |
| `EditJournal.cs` | 追加式 `edit-journal.jsonl` 与资产目录（§6.4） |
| `CspEditTransaction.cs` | 实现 `ICspReconstructionExecutor`：粘贴、隐藏、删除、身份确认、捕获（§6） |
| `LayerCapture.cs` | 保存后用 `export-id` 捕获，并与期望 RGBA 逐字节比较 |
| `EditingCoordinator.cs` | 串行化、取消、错误码到界面文案的映射、状态枚举 `PlanUnconfirmed / Executing / Failed / Executed / PixelVerified` |

#### 5.4.3 "笔刷工具"判定

- brushState.name 经 `ToolCatalog` 解析到的父工具组，必须属于集合 `{笔, 铅笔, 毛笔, 喷枪, 装饰, 橡皮擦, 色彩混合}`。同时接受简繁体和英文写法：`{Pen, Pencil, Brush, Airbrush, Decoration, Eraser, Blend}`。
- 解析失败、重名、有歧义时判为不合格，并说明原因。
- 以下工具一律不合格：填充、渐变、选区、移动、文字、图形、尺规等。

#### 5.4.4 因果词条表 `Editing/Resources/causal-terms.v1.json`

- **来源**：官方子工具说明（链接见旧设计文档"官方资料与词条识别"），包括三类：
  - Ink：Color mixing、Amount of paint、Density of paint、Color stretch、Intensity of blur、Blending quality、Blending mode。
  - Anti-overflow：Do not cross lines of reference layer 及其参考范围设置。
  - Copy stamp：来源与参考位置设置。
- **键名必须从 `recognizer/recognizer_core/panel_state` 的 `properties.sqlite3` 目录中查得**：按中文、繁体、英文标签逐一匹配，把匹配到的目录 key 写入 JSON。
  - 已知：`ink.color_mixing, ink.mixing_mode, ink.amount_of_paint, ink.density_of_paint, ink.color_stretch, ink.blending_quality, ink.blending_mode, anti_overflow.do_not_cross_lines_of_reference_layer, anti_overflow.color_margin, anti_overflow.area_scaling, anti_overflow.scaling_mode`。
  - 模糊强度、复制图章类的 key【须核实】。
- **JSON 格式**：
  ```json
  { "version": "csp-property-presence-v1",
    "terms": [ { "key": "ink.color_mixing", "category": "ink", "read": "underTip" },
               { "key": "...copy_stamp...", "category": "copyStamp", "read": "unknown" },
               { "key": "anti_overflow.do_not_cross_lines_of_reference_layer", "category": "antiOverflow", "read": "referenceLayers" } ] }
  ```
  `read` 决定 §4.4 的 I(s)：`underTip` → W(s)；`unknown` → 整层；`referenceLayers` → 跨层。
- **排除**：名称相近但**不读取画布**的类别，例如双重笔刷内部混合、纹理合成模式、主色与副色混合（`2_brush_shape.*` 下的合成模式、`texture.*` 下的合成模式）。按目录类别区分，每个排除项都要有测试。
- 判定只看 key 是否出现，不看 value、enabled、status（§1.5）。

#### 5.4.5 必然落墨白名单 `Editing/Resources/probe-guaranteed.v1.json`

- 属性 key 全部属于白名单时，中心线探针才成立；否则探针退化为 W(s)。
- 初始白名单：`brush_size, opacity, antialiasing, stabilization, adjust_stabilization_by_speed, brush_density, correction.*, starting_and_ending.*`。`correction.*` 中除 `correction.vector_magnet` 以外的矢量项是否属于白名单【须核实】。
- 明确不在白名单的：`spraying_effect.*`、`texture.*`、`stroke.gap` 等会产生间断或散布的项、`2_brush_shape.*`、`watercolor_edge.*`，以及所有未知 key。
- 每一项都要有测试。

#### 5.4.6 宿主接线

- `DemoController` 新增三个方法：
  - `BeginEditSessionAsync(CancellationToken) -> Task<EditSession>`，依次执行：
    1. `prepareEditBoundary`。
    2. 边界捕获（§5.3.3）。
    3. `LiveSessionReader`。
    4. `EditIndexBuilder`。
    5. 返回 `EditSession{ Index, LiveView, CurrentLayer }`。
  - `BuildPlan(EditSession, EditRequest) -> ReconstructionPlan`。
  - `ExecuteAsync(EditSession, ReconstructionPlan, IProgress<EditProgress>, CancellationToken) -> Task<ReconstructionResult>`。
- 面板只调用 controller，不直接操作 CSP 或文件。
- 封盘：
  - `BundleArchive.Seal` 把 `edit-journal.jsonl` 与编辑资产作为新的可选条目写入，schema 升为 `memoline-csponly/v3`。
  - v1、v2 继续可读。
  - `Verify` 校验日志所引用资产的哈希。
  - `PacketArchiveReader` 必须能读 v3。
  - 原始机械流与聚集流字节不变，用测试比对哈希。

### 5.5 回放器 `replayer/packetreplayer/source` + `replayer/strokereplayer/source`

#### 5.5.1 任意事件清单回放

- 新文件 `EventReplayRunner.cs`：
  ```
  RunAsync(EventReplaySchedule schedule, IReplayEnvironment env, double speed, ReplayOptions options,
           Action<string> status, CancellationToken token) -> Task<EventReplayResult>
  EventReplaySchedule(long Frequency, IReadOnlyList<ScheduledStroke> Strokes, string WorkLayerName, string WorkLayerUuid)
  ScheduledStroke(EditableKey: (string SessionId, ulong BeginAppendId), MemolineReplayStroke Stroke,
                  HistoricalState State, BrushSnapshot? OverrideBrush)
  IReplayEnvironment: IPacketStateFeed Feed, IPacketInput Input, IViewInput ViewInput, Func<IPenInjector> CreateInjector,
                      Func<TimeSpan,CancellationToken,Task> Idle
  ```
- 笔画取自 `document.Mechanical.Strokes`，或 `LiveSessionReader` 的等价结果，必须**完整、未裁切**。状态取 `StateHistory.At(history, stroke.Samples[0].EventId)`。
- 顺序由调用方保证（按 penBegin eventId 升序），Runner 再校验严格递增、无重复。
- 笔间间隔：`ReplayOptions.GapBetween` 用该笔原始结束 ticks 与清单中**下一笔的原始开始 ticks** 计算；两笔在原记录中不连续时，以 `StrokeGapSeconds` 为上限。
- 拆分 `PacketReplayRunner.RunAsync` 中写死的 `WindowsViewInput / PacketRecorderControl / SyntheticPenInjector`，使其可注入。旧 API 签名保持不变，通过适配器调用新核心。
- 顺带修复 `plan.Strokes.ToList().IndexOf(drawing)` 的 O(n²) 写法。

#### 5.5.2 图层路由

- 所有笔都注入到工作层（当前层），不调用按历史图层名导航的 `RestoreLayerAsync`。
- 每笔开始前的检查和每个接触采样的漂移守卫，都改为校验"当前层名 == WorkLayerName"。名称唯一性由事务保证（§6.1）。
- 历史图层名只用于日志。

#### 5.5.3 参数覆盖

- `OverrideBrush` 由**完整的历史 BrushSnapshot** 加上被修改的数值项构造，避免一笔的覆盖残留到下一笔。
- 允许修改的只有两类：
  - `type=="number"`、且在实时 `valueRegions` 中有唯一输入框的数值属性；
  - 子工具名（要求 `ShortcutConfiguration.RouteForBrush` 能路由）。
- 写入后必须读回确认，沿用 `PacketStateRestorer` 的 brushState 校验。

#### 5.5.4 颜色还原

- 新文件 `ColorRestorer.cs`。历史 colorState 与实时不同时：
  1. 用 §5.2.3 的 `colorInputState` 框，依次点击 R、G、B 并输入数值（复用 `WindowsViewInput.SetNumberAsync`）。
  2. 请求 colorState，校验 `kind` 与 `rgb`。
- 面板不可见或校验失败时报 `ColorUnrestorable`。不得跳过颜色检查。

#### 5.5.5 合成输入标记

- `WindowsViewInput` 的所有 `SendInput` 键盘、鼠标事件都设置 `dwExtraInfo = 0x4D4C5243`（RecorderInputTag），使识别器不记录、不拦截。
- 笔注入不经过 SendInput，由事务区间（§5.1.2）隔离。

#### 5.5.6 测试（`replayer/packetreplayer/checks`，新文件 `EventReplayChecks.cs`）

- 跨包完整笔不被裁切：注入的 appendId 序列完整。
- 参数覆盖在每笔落笔时生效，下一笔恢复原值。
- 每个采样都校验工作层。
- 离散模式下每笔都以 UpAndLeave 结束；集中模式以 Up 结束。
- 取消时释放笔与按键。
- 颜色不同时调用 ColorRestorer，失败时抛出带错误码的异常。
- SendInput 带标记。
- 旧 `PacketReplayRunner` 的全部检查继续通过。

### 5.6 dirty matrix paster `replayer/dirty matrix paster`

- 把 `source/LayerClipboard.cs` 中与 `ExportResult` 无关的部分拆到新文件 `source/ClipboardImageCodec.cs`，提供：
  - `EncodePng(int w, int h, ReadOnlySpan<byte> rgba) -> byte[]`
  - `EncodeDibV5(int w, int h, ReadOnlySpan<byte> rgba) -> byte[]`
  - `SetImage(int w, int h, ReadOnlySpan<byte> rgba)`：在 STA 中写入 PNG 与 CF_DIBV5，并读回校验。
  
  `LayerClipboard.cs` 改为调用它，独立程序行为不变。
- 宿主用 `ClipboardImageCodec.SetImage` 把 A（整画布 RGBA）放入剪贴板，供 CSP 粘贴。
- **对拍测试**（宿主 checks 新文件 `CompositionParityChecks.cs`）：
  1. 用 `Organizer/canvas layer watcher` 的检查夹具，或新造夹具，生成多个包。
  2. 宿主 `ImageCompositor` 从全透明种子开始，逐包合成全部图片。
  3. 同时运行 `node backend/engine.mjs --compose`。
  4. 在 Node 输出的外框内逐字节比较 RGBA 与 mask，必须相同。
  
  开发机必须有 node。找不到 node 时，检查失败并给出明确信息，不得静默跳过。
- 新增 Node 检查 `backend/checks/alpha-zero.test.mjs`，锁定 alpha=0 替换与同组重叠相等两条规则。

### 5.7 CSP_Shortcut_Manager（虚拟快捷键写入）

#### 5.7.1 需要的命令与绑定

| 用途 | CSP 命令（menucommand） | 当前状态 | 处理 |
| --- | --- | --- | --- |
| 保存 | `save`【须核实命令名】 | Ctrl+S（识别器使用） | 只校验存在 |
| 复制 | `copy` | Ctrl+C / F3 | 只校验 |
| 粘贴 | `paste` | Ctrl+V / F4 | 只校验 |
| 全选 / 取消选区 | `selectall` / `selectdeselect` | Ctrl+A / Ctrl+D | 只校验 |
| 上 / 下一层 | `layerselectupperlayer` / `layerselectlowerlayer` | Alt+] / Alt+[ | 只校验 |
| 删除图层 | `layerdelete` | 未绑定 | 写入虚拟键 |
| 显示/隐藏图层 | `layervisiblelayer`【须核实语义】 | 未绑定 | 写入虚拟键 |

- `layervisiblelayer` 的语义（是否为"切换选中图层的显示状态"）必须先由用户在 CSP 中试验确认。确认前，隐藏原层只能走 §6.2 的眼睛图标后备路径。
- 虚拟键按以下顺序取第一个未被占用的：`Ctrl+Alt+Shift+F9`、`Ctrl+Alt+Shift+F10`、`Ctrl+Alt+Shift+F11`、`Ctrl+Alt+Shift+F12`。
  - 冲突检查覆盖 `shortcutmenu` 全表，以及工具快捷键（`EditImageTool.todb` 的 `Node.NodeShortCutKey`）。
  - F13 及以上在 CSP 中是否可用【须核实】，核实前不使用。

#### 5.7.2 新文件 `CSP_Shortcut_Manager/csp_shortcut_writer.py`（仅用标准库 sqlite3）

CLI 子命令，每个都输出一行 JSON：

- `plan --user-dir <dir> --require <json>`：列出缺失的绑定和拟用的虚拟键，**不写入**。
- `apply --user-dir <dir> --plan <json> --confirm-token <token>`：
  1. 检查 `CLIPStudioPaint.exe` 进程**不存在**，否则拒绝。CSP 运行时写入可能被它覆盖。
  2. 把 `default.khc`（连同可能存在的 `-wal`、`-journal` 文件）备份到 `<user-dir>/Shortcut/memoline-backup-<UTC时间>/`。
  3. 在一个事务内写入：若该 `menucommand` 已有空绑定行（shortcut 为 NULL 或空），就 UPDATE 该行；否则 INSERT `(menucommandtype, menucommand, shortcut, modifier)`。`menucommandtype` 取同名命令已有行的值；没有已有行时，该命令的类型（如 `advancedcommand`）【须核实】。
  4. 用现有的 `csp_shortcuts.read_menu_shortcuts` 读回校验。
  5. 写清单 `<user-dir>/Shortcut/memoline-provisioned.json`，记录写入的行、原值和备份路径。
- `verify --user-dir <dir> --require <json>`：所需绑定全部存在、可发送、无冲突。
- `rollback --user-dir <dir>`：按清单恢复原值，同样要求 CSP 未运行。
- **绝不修改**用户已有的任何非空绑定。
- 新增测试 `CSP_Shortcut_Manager/test_csp_shortcut_writer.py`：
  - 在临时目录中复制夹具 khc。
  - 覆盖 plan/apply/verify/rollback、冲突、CSP 运行中拒绝、已有绑定不被修改、读回校验。
  - **测试不得触碰真实用户目录。**

#### 5.7.3 宿主集成

- 宿主"录制设置"页新增"CSP 快捷键检查"：
  1. 启动时用宿主内的 `--shortcuts` 读取逻辑校验。packetreplayer 的 `ShortcutConfiguration` 使用的就是这份数据。
  2. 若有缺失，显示需要写入的绑定，由用户点击"写入"。
  3. 提示用户关闭 CSP，确认 CSP 已退出后调用 writer `apply`。
  4. 完成后提示用户重新打开 CSP，并再次 `verify`。
- Python 解释器【须核实】：Recognizer 发布包是否自带 python.exe（`Build.ps1` 第 64 行附近称 Recognizer 包"提供 Python/原生依赖"）。
  - 有：`Build.ps1` 把 `CSP_Shortcut_Manager/*.py` 复制到 `publish/win-x64/ShortcutManager/`，用该解释器运行。
  - 没有：在宿主中用 C# 加 `winsqlite3.dll` P/Invoke 实现等价的 plan/apply/verify/rollback。Python 版保留为参考，两者共用同一份测试夹具与期望结果。
  - 结论写入 notes。

### 5.8 构建与发布 `Memoline_demo_csponly/Build.ps1`

- 发布物包含：
  - 链接后的宿主（已含回放器与 paster codec）
  - `clip-layer-bridge.exe`
  - 修改后的 Recognizer（原生与 Python 集成部分）
  - `ShortcutManager/`（若走 Python 路线）
  - 嵌入资源 JSON
- 发布物**不需要** node 运行时，Node 只用于检查。
- 构建前运行全部检查（指定 `-SkipChecks` 时除外），node 对拍是必过项。
- 保留现有规则：不覆盖 `integration/settings.json` 和 `config/*`；`Memoline.exe` 原子替换。

---

## 6. CSP 事务（每一步都先确认再执行）

### 6.1 事务开始（每个编辑请求一次）

1. 发送 `beginEditTransaction{transactionId}`，设置 `monitor.CaptureRequestsPaused = true`，事务期间持有 `CaptureCoordinator`。
2. 校验 §5.7.1 的快捷键全部可用。否则报 `ShortcutsMissing`，并指向设置页。
3. 对请求涉及的每个图层 L：
   1. 保存并读取 clipState，按 UUID 找到 L 的名称。名称必须唯一，否则报 `LayerIdentityUnconfirmed`。
   2. 用 `layerselectupperlayer/lowerlayer` 把当前层切到 L（复用 `PacketStateRestorer.RestoreLayerAsync` 的导航逻辑），再用 `currentLayerState` 确认。

### 6.2 Flush(A, P)：一次重放段

1. **确认并隐藏原层**：确认当前层是原层 L（OCR 名称 + 唯一）。第一次 Flush 前隐藏 L：
   - 首选：`layervisiblelayer` 虚拟键，前提是 §5.7.1 的语义已确认。
   - 后备：点击 §5.2.2 同帧定位的眼睛矩形。点击前校验该行名称 == L 且 selected；点击后重新采集，确认眼睛已关闭。
   - 两种方式之后都要保存，并校验 clipState 中 L 的 `visible == false`。
2. **粘贴**：`ClipboardImageCodec.SetImage(A)`，然后发送 `paste`。CSP 会在 L 之上新建一层并选中它。
3. **确认新层身份**：保存并读取 clipState，比较前后结构，必须**恰好新增一个**非文件夹栅格层 T（新 UUID）；且 `currentLayerState` 名称 == T 的名称，并且唯一。否则报 `LayerIdentityUnconfirmed`，之后不再执行任何动作。
4. **校验粘贴保真**：`export-id` T，与 A 逐字节比较。不相等则报 `PasteNotExact`，事务失败；T 未入库，保留不删。
5. **重放**：`EventReplayRunner.RunAsync(schedule(P, WorkLayer=T), ReplayMode.Concentrated)`。运行期间监听 `physicalInputDuringTransaction`，收到即取消，并释放笔与按键。
6. **捕获**：保存后 `export-id` T，得到原尺寸、`fullCanvas==true` 的 A'。
   - 用户最初描述的是"复制粘贴当前图层"。本实现以保存 + export-id 作为权威捕获，因为 CSP 剪贴板的 alpha 保真度未经证实。
   - 剪贴板捕获（`selectall` + `copy`，读取 PNG/CF_DIBV5）可作为可选实现，默认关闭；只有在 §9.11 实机自检中逐字节一致后才允许启用。
7. **入库**：写入日志检查点（§6.4），内容包括 A' 资产、P 的事件键与参数、T 的 UUID。**刷盘并读回校验后**，才能进行下一步。
8. **删除工作层**：再次确认当前层仍是 T（UUID + 名称），然后发送 `layerdelete`。
9. **校验删除**：保存并读取 clipState，确认 T 已不存在，L 仍存在且仍隐藏。
10. 返回 A'。

### 6.3 安装复现层（每层一次，最后执行）

1. 确认当前层为 L（已隐藏）。
2. `SetImage(A_final)` 后 `paste`，得到新层 F。按 §6.2 第 3–4 步校验。
3. 精确撤销：在 F 上执行 §4.7 第 7 步的离散重放。事件编辑：不再做任何操作。
4. F **保留**，不删除。
5. 最终捕获：保存后 `export-id` F，写入日志 `final` 记录。若做过离散重放，这次捕获就是 F 的实际最终像素。
6. `SnapshotHistory.RegisterBaseline(F, 导出 PNG)`。
7. 所有层完成后发送 `endEditTransaction{outcome}`，恢复捕获，界面显示结果状态：
   - 全部图片按规则执行完毕：`Executed`。
   - 只有离线可证明结果等于目标时（例如纯合成、无任何重放的请求），才允许显示 `PixelVerified`。
   - 有重放的请求不得显示 `PixelVerified`。

### 6.4 编辑日志 `edit-journal.jsonl`（工作目录内，追加式）

- 记录种类：
  - `transactionBegan`
  - `planFrozen`（完整计划及其哈希）
  - `segmentStarted`
  - `checkpoint`（资产 sha256、尺寸、层 UUID、事件键列表、参数覆盖）
  - `layerHidden`
  - `workLayerDeleted`
  - `reproductionInstalled`
  - `transactionEnded`
  - `failure`（错误码、最后一个检查点）
- 资产写入：临时文件 → flush → 原子改名 → 校验哈希，然后追加记录并 fsync。崩溃时，尾部不完整的记录视为无效。
- **原生机械流与已提交的包，字节永远不改。**

### 6.5 失败与恢复

- 任何失败都必须：
  - 立即释放笔与按键；
  - 不再向 CSP 发送任何删除操作；
  - 保留未入库的工作层；
  - 发送 `endEditTransaction{outcome:"failed"}`；
  - 恢复捕获。
- 下次按 Alt+M 时，若发现未完成事务，面板显示"未完成事务"，提供两个选项：
  - "清理"：确认身份后，删除日志中记录的未入库工作层，并恢复原层显示。
  - "保留现状"。
- **不自动补画**半途中断的笔画。

---

## 7. 用户界面

### 7.1 托盘与 Alt+M

- **新文件 `source/DemoApplicationContext.cs`**：用 `Application.Run(new DemoApplicationContext(...))` 取代 `Application.Run(window)`。
  - 管理三样东西：录制窗口（watcher MainWindow）、`NotifyIcon` 托盘（显示 / 开始停止 / 退出）、控制面板。
  - 录制窗口关闭时改为最小化到托盘，录制继续。"退出"走原有的停止与封盘流程。
- **新文件 `source/GlobalHotkey.cs`**：
  - 注册 `RegisterHotKey(hwnd, id, MOD_ALT|MOD_NOREPEAT (0x0001|0x4000), VK_M (0x4D))`，退出时 `UnregisterHotKey`。
  - 注册失败时显示 Win32 错误码与说明。
  - 启动时用快捷键数据检查 CSP 中是否有 Alt+M 绑定。若有，警告用户：系统热键会抢走它。
- Alt+M 只在录制中有效。按下后，面板显示"正在建立编辑边界…"，`BeginEditSessionAsync` 完成后才开放操作。

### 7.2 控制面板 `source/EditingPanel.cs` 与覆盖层 `source/EventSelectionOverlay.cs`

**覆盖层**：

- 是一个覆盖虚拟屏幕的置顶分层窗口（`WS_EX_LAYERED|WS_EX_TOPMOST|WS_EX_TOOLWINDOW`）。选择状态下接收输入；其他时候设 `WS_EX_TRANSPARENT`，让输入穿透。
- 覆盖层属于 Memoline 进程，而识别器只记录落在 CSP 窗口上的笔，所以选择操作不会被录成笔画。这一点必须写测试，或在 notes 中引用 `CspWindowProbe.cs:45-56` 证明。

**区域选择**：

- 支持矩形与自由多边形。
- 用编辑边界时请求到的 `canvasViewState.transform.matrices.screenToCanvas`（§5.2.1）把屏幕点映射为归一化坐标，乘以 `canvasPixelWidth/Height` 得到画布像素，再栅格化为位集。
- 必须校验矩阵所属的 sessionId 和画布尺寸与当前会话一致，否则拒绝。

**事件编辑**：

1. **列出图片**：列出所有图层中 `Mask(m) ∩ Rsel ≠ ∅` 的图片，按图层、时间分组。每项显示缩略图（now 补丁）、所在图层、时间、事件数，以及能否选择和原因（如 `ChainLossy`、`NoBaseline`）。
2. **白线显示**：选中一张图片后，覆盖层显示其外框。鼠标悬停时，在光标 48 屏幕像素范围内，用**白色**（`Color.White`，线宽 2 px）画出该图片关联的合格事件折线，坐标经 `canvasToScreen` 映射到当前视图。
3. **扫选**：与矢量橡皮的"擦过即整条命中"一致。
   - 按住拖动形成轨迹，命中半径为 12 屏幕像素（换算到画布）。
   - 轨迹与事件折线相交即选中整条事件；再次扫过已选事件则取消选中。
   - 已选事件高亮显示。
4. **操作**：
   - "删除"。
   - "更换参数"：对话框列出该事件历史 BrushSnapshot 中允许修改的数值项（§5.5.3），也可以换子工具。不支持的项不显示。
5. **预览计划**：显示 §4.6 第一遍的结果，包括段数、各段图片、重放事件数、不可信原因。有 PlanIssue 时禁用"执行"并显示原因。
6. **执行**：进度显示当前段和当前笔画，可取消。

**精确撤销**：

1. 区域选择。
2. 面板显示已确认的当前图层名与选出的事件数（"50"或实际数量，加上 k 个闭包事件），覆盖层用白线显示这些事件。
3. 预览、执行。

---

## 8. 输入输出数据格式（新增）

| 项目 | 定义位置 |
| --- | --- |
| `prepareEditBoundary` 请求/响应 | §5.1.1 |
| `beginEditTransaction` / `endEditTransaction` | §5.1.2 |
| `core.canvasViewState.state.transform.matrices` | §5.2.1 |
| `rawResult.layerLocator` | §5.2.2 |
| `colorInputState` | §5.2.3 |

- manifest 新字段：
  - `"comparison":"fullResolution/v2"`
  - baseline 来源：`"baselineSource":"recordingStart|createdEmpty|reproduction"`
- bundle v3 新条目：`edits/journal.jsonl(.br)`、`edits/assets/<sha256>.png`。`index.json` 新增 `edits{entry, sha256, size, codec, assetCount}`。
- 以上每一项都要同步更新：`Memoline_demo_csponly/FORMAT.md`、`INTERFACES.md`；新频道和新 kind 还要补充到 `recognizer/core/memoline/Realtime/README.md`。

---

## 9. 实施阶段与验收

### 9.0 检查命令（每阶段结束全部运行，全部通过）

```bash
dotnet run --project Memoline_demo_csponly/checks/MemolineDemo.Checks.csproj -c Release            # 输出 "Memoline integration checks passed."
dotnet test recognizer/Recognizer/tests/BehaviorRecognizer.Tests/BehaviorRecognizer.Tests.csproj -c Release   # Failed 0
dotnet run --project "Organizer/canvas layer watcher/checks/CanvasLayerWatcher.Checks.csproj" -c Release     # 全部通过
dotnet run --project replayer/packetreplayer/checks/PacketReplayChecks.csproj -c Release            # 全部通过
py -3 -m unittest discover -s recognizer/recorder_integration/tests -p "test_*.py" -v               # OK
(cd "replayer/dirty matrix paster/backend" && node --test --test-isolation=none checks/*.test.mjs)   # fail 0
py -3 -m unittest CSP_Shortcut_Manager/test_csp_shortcuts.py CSP_Shortcut_Manager/test_csp_shortcut_writer.py
```

- 当前基线（2026-10-07）：

  | 套件 | 结果 |
  | --- | --- |
  | host | 通过（bundle 65、aggregate 47、lifecycle 10） |
  | native | 110/110 |
  | watcher | 171 |
  | replay | 131 项断言 |
  | python | 128 |
  | node | 23 |

- 运行依赖（均已在仓库工作区中）：
  - watcher 检查需要 `Organizer/canvas layer watcher/bridge/target/release/clip-layer-bridge.exe` 与 `replayer/status change/csp_blank_template.clip`。
  - paster 检查需要 `backend/node_modules`。

### 9.1–9.10 阶段（按顺序执行；每阶段列出必须新增的测试）

| 阶段 | 内容 | 必须新增并通过的测试（至少） |
| --- | --- | --- |
| 1 | §5.3.1 全分辨率比较；§5.3.2 开始 baseline；§5.3.4 链校验 | 预测外 1 px 变化被检出；首窗口产生 diff；链断点；4961×7016 性能 |
| 2 | §5.1 编辑边界与事务；§5.3.3 多层边界捕获 | 边界可重复、不写 footer；进行中的笔画不截断；多层各一包；事务内物理输入告警；带标记的合成输入不记录 |
| 3 | §5.2.1 矩阵发布；§5.2.2 图层定位器；§5.2.3 颜色输入定位 | 矩阵与 origin 一致；与 watcher 旋转映射一致；locator 与状态同帧；旧字符串结果兼容 |
| 4 | §5.4.1 链接；`LiveSessionReader`；`ToolCatalog`；`BrushClassifier`（§5.4.3–5.4.5）；`EditIndex` | 跨包完整接触只索引一次；鼠标、画布外、未知工具、事务内的事件被排除；他层笔画不被错误关联；词条存在即因果（含 disabled）；同名但非画布类别不算因果；白名单判定 |
| 5 | `LayerTimeline`；`ImageCompositor`；`ClipboardImageCodec` 拆分；Node 对拍 | 倒推再正推逐字节等于 C_L；alpha=0 替换；同组重叠冲突；对拍一致；paster 独立程序的剪贴板检查不变 |
| 6 | `Footprints`；`ReconstructionPlanner`；`ReconstructionEngine`（fake executor） | §9.12 场景表的全部场景 |
| 7 | §5.5 回放器改造 | §5.5.6 全部 |
| 8 | §5.7 快捷键写入；§5.4.6 封盘 v3；`EditJournal` | writer 全部测试；v1/v2/v3 读取；原始字节哈希不变；日志崩溃尾部；资产缺失时 Verify 失败 |
| 9 | §6 事务（`CspEditTransaction`、`LayerCapture`，fake CSP） | 动作顺序日志严格为 `verify-L → hide → paste → verify-T → export==A → replay → export → checkpoint-flushed → verify-T → delete → verify-gone`；捕获失败不删层；身份不符不删层；取消后不自动补画；物理输入中止 |
| 10 | §7 UI；§4.7 精确撤销；`DemoController` 接线；`Build.ps1` | 热键注册与注销；关闭面板后录制继续；白线只显示光标附近、所选图片的事件；扫选命中整条；精确撤销取当前层、新到旧 50 笔、含闭包；不支持的参数不显示；发布物冒烟启动 |

### 9.11 实机验收清单（交付给用户，在 CLIP 副本上执行）

执行者把清单写成 `Memoline_demo_csponly/docs/architecture/LIVE_CHECKLIST.md`，并说明日志收集方法（`.work/<session>/edit-journal.jsonl`、`recognizer.log`）。

1. **快捷键写入**：关闭 CSP → 写入 → 打开 CSP → `verify` 通过。确认 `layervisiblelayer` 的语义，确认 `layerdelete` 删除的是当前层。
2. **粘贴保真**：在副本中粘贴一张测试图（含半透明像素和 alpha=0 但带 RGB 的像素），导出后逐字节比较。宿主提供"粘贴自检"按钮。剪贴板捕获自检同理。
3. **录制基础**：录制开始的 baseline、全分辨率 diff，以及在后台按 Alt+M 呼出面板。
4. **选区**：旋转视图下框选，跨层图片列表正确。
5. **删除一笔的三种情形**：
   - 被普通笔同色覆盖：该矩阵进入重放，结果无缺口。
   - 被因果笔覆盖：该矩阵重放。
   - 与修改区域不相交的因果矩阵：仍走合成。
6. **改参数**：改笔刷尺寸后重放；改子工具后重放。
7. **图层结果**：原层隐藏，复现层位于其上并保留；中间工作层在入库后被删除。
8. **精确撤销**：执行后在 CSP 中连续按 Ctrl+Z，撤销的恰好是被选中的那些笔画，次数等于界面显示的数量。
9. **故障演练**：
   - 执行中用真实笔触碰数位板：应立即中止，且不删除未入库的层。
   - 拔掉笔，或强制结束 Recognizer：应停止并保留日志。

### 9.12 重建引擎必须覆盖的场景（fake executor + 合成数据，逐字节断言）

| # | 场景 | 期望 |
| --- | --- | --- |
| 1 | 删除 A；后续非因果 B 与 A 不重叠 | B 合成；只有 A 所在图片重放 |
| 2 | 删除 A；非因果 B 以不同颜色覆盖 A | B 合成（mask 覆盖），结果中 B 完整 |
| 3 | 删除 A（红）；非因果 B 用同样的红色覆盖 A 的一部分 | B 判为不可信（规则 3），整矩阵重放；结果中 B 无缺口 |
| 4 | 删除 A；后续因果 C 的 I(C) 与 A 相交 | C 整矩阵重放（含 C 中的普通笔） |
| 5 | 删除 A；后续因果 C 与已变区域不相交 | C 合成 |
| 6 | 被重放的因果 C 影响更晚的因果 E（传播） | E 重放 |
| 7 | 同一图片含 A（删除）、B（普通）、C（普通） | 重放清单为 B、C，按原 eventId，不含 A |
| 8 | 一笔跨两包两图，其中一图不可信 | 两图都不可信（闭包）；该笔只注入一次，采样完整 |
| 9 | 同一捕获组中一张合成、一张重放 | 合成延后到 Flush 之后，结果等价 |
| 10 | 在线精确 D 使静态判为可信的图片改判不可信 | 加入批次；若需要反向闭包，报 `RuntimeClosureViolation` |
| 11 | 重放结果与历史存在像素差（fake 返回偏差） | D 精确更新，后续判定使用实际 D |
| 12 | 复制图章类词条（read 为 unknown） | I = 整层；D 非空即重放 |
| 13 | anti_overflow 跨层 | 编辑 X 层导致 Y 层相应矩阵重放 |
| 14 | 撤销命令位于范围内 | `UndoRedoInRange` |
| 15 | 必须重放的图片含鼠标笔画、填充或 fallback 标签 | `UnreplayableEventInMatrix` |
| 16 | 旧录制（粗略比较）或缺 baseline | `ChainLossy` / `NoBaseline` |
| 17 | 精确撤销有 60 个候选 | 取最新 50；闭包加入与之重叠的更晚未选笔；按原始顺序重放 |
| 18 | 精确撤销候选少于 50 | 取实际数量 |
| 19 | 种子取"最早受影响图片的前一状态" | 与倒推得到的 `H_before` 逐字节相等 |
| 20 | 所有图片都可信（例如删除的那一笔所在图片只含这一笔） | 该图片重放后批次为空，跳过这次空重放，直接以 `H_before` 合成后续图片；状态可为 `PixelVerified` |

---

## 10. 明确禁止

- 不得用 `trigger-*` 目录或 `labels/dirtyMatrices` 代替单张图片作为脏矩阵。
- 不得用 change mask 冒充写入范围、读取范围或探针。
- 不得把因果矩阵拆成"只重放其中的因果笔"。
- 不得因为性能或不确定，把非因果、可信的矩阵改为重放（规则 3 的同色覆盖除外）。
- 不得把 `canvas-preview.png` 放大后当作恢复基础。
- 结果未入库前，不得删除任何工作层；不得用 Delete 清除像素代替删除图层。
- 不得在 CSP 运行时写 khc；不得修改用户已有的非空绑定。
- 不得通过启动 PacketReplay 或 DirtyMatrixPaster 窗口来"集成"。
- 不得把向真实 CSP 注入输入当作自动测试。
- 不得以"演示版"为理由跳过本文件的任何一节。

---

## 11. 须核实事项汇总（开工第一步；结论写入 IMPLEMENTATION_NOTES.md）

1. `WindowsMouseInputRouter.cs` 中被动笔与注入标志的现有处理，以及修改点（§5.1.2）。
2. 色彩滑块面板（R/G/B 数字框，与色环面板是不同面板，由用户手动切换）的 OCR 定位，以及切回后色环识别能否恢复（§5.2.3）。
3. inspect/clipState 能否区分新建空层与粘贴、复制得到的层（§5.3.2）。
4. 回放器源码的实际文件名与排除列表（§5.4.1）。
5. `toolCatalog` 的数据形状，以及父工具组的解析方法（§5.4.2–5.4.3）。
6. 因果词条中模糊强度、复制图章类的目录 key；白名单中 `correction.*` 的子项（§5.4.4–5.4.5）。
7. CSP 撤销次数设置的存储位置（§4.7）。
8. `save` 的 menucommand 名；`layervisiblelayer` 的语义；新插入行的 `menucommandtype`；F13 及以上是否可用（§5.7.1–5.7.2）。
9. Recognizer 发布包是否自带 Python 解释器（§5.7.3）。
10. 标签覆盖半径字段的确切名称，以及它与 `Coverage.cs` 公式的对应关系（§4.4）。
