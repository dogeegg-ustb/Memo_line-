# CSP Event Editing Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将后台记录、脏图像合成和 StrokeReplay 集成到 Memoline.exe，提供 Alt+M 事件编辑及当前图层最多 50 笔的精确撤销。

**Architecture:** 宿主在可重复的录制边界上建立不可变编辑索引，规划按原始时间排序的图像合成段和整矩阵重放段。现有 paster 后端接受完整层基础与明确图像清单，现有 packetreplayer 核心接受完整接触清单；宿主通过串行事务协调工作层、实际结果捕获和追加版本。没有范围、顺序或身份依据时返回具体的计划未确认原因。

**Tech Stack:** Windows x64、.NET 10 WinForms、现有 Memoline.Core、Node.js/pngjs 7.0.0、现有 Python 识别运行时及 CLIP layer bridge；不新增笔刷引擎或云服务。

**Spec:** [已批准设计](../specs/2026-10-06-csp-event-editing-integration-design.md)。以下相对文件路径均以 `D:/Memo_Line/Memo_Line` 为根。

## Global Constraints

- 系统级 `Alt+M` 呼出控制面板，录制可收起到托盘并持续运行。
- 只选择画布视口内、数位笔来源、历史笔刷状态已确认的完整接触；以 `sessionId + penBegin.appendId` 标识，operationId 仅供关联。
- 单张主差异图像才是脏矩阵；`manifest.images[]` 经 `labelIds` 关联事件，不能拿 trigger 目录或 dirtyMatrices 标签代替图像。
- 读已有像素词条存在即因果，关闭或禁用仍然因果；采用规范类别及中英文别名，保留规则版本与历史证据。
- 用户修改矩阵和受影响因果矩阵均重放全部关联合格事件，包括非因果笔刷；删除跳过、参数覆盖仅作用于所选事件。
- 非因果及不受影响矩阵走内部合成；不因性能拆细因果矩阵，也不为消除不确定性擅自扩大重放范围。
- 先统一去重、合并共享接触执行边界，再按原生 eventId 排序；样本使用原生指针顺序，状态使用因果锚点及预留顺序。
- change mask 仅表示不同像素。读取范围、完整写入范围及兼容性依据分别保存；同色覆盖、透明覆盖、混色和 Copy Stamp 必须有检查。
- 精确撤销仅取已确认当前图层表层事件，新到旧最多 `50` 笔；执行仍按原始时间，穿插的未选事件保留位置。
- 初始基础必须是原尺寸 RGBA 完整层快照，且在目标事件之前；旧 1600 边长缩略总览不放大冒充基础。
- 原始机械流和已提交包字节不变；编辑结果追加写入独立版本，封盘校验资产与事件引用。
- 工作层身份确认、实际结果捕获、持久入库、删除已确认工作层，严格按这个顺序；最终替代层保留，原始层隐藏可恢复。
- 重放状态确认继续可用；合成操作不得重新进入用户事件索引，真实用户输入导致执行中止，不能借来源隔离丢掉物理输入。
- 自动检查不向 CSP 注入；实机检查只使用 CLIP 副本，结果区分 `PlanUnconfirmed / Executing / Failed / Executed / PixelVerified`。

## Review Focus

1. 同一图像包含交错笔画且跨包共享接触：每笔只执行一次，保留矩阵内顺序及完整样本；任务 1、6、7。
2. 同色覆盖无 change mask、半透明底色变化、混色和远端 Copy Stamp 来源：不拿 mask 证明独立，证据不足不注入；任务 4、6。
3. 取消、断线或剪贴板成功但版本落盘失败：不删除工作层、不自动补画半笔，保留恢复信息；任务 8、9。
4. 同名图层、折叠父文件夹、过期 ROI 或选择在保存间变化：不得点击或删除凭猜测匹配的行；任务 5、9。
5. 首窗口无先行基础、旧缩略图、旋转视图和精确撤销中穿插未选事件：拒绝伪精确，合格后缀保持原位；任务 3、6、10。

---

## 文件边界与共用接口

宿主新增 `source/Editing/`：`EditingModels.cs` 放领域值类型，`EditIndex.cs` 放事件与图像索引，`CausalityClassifier.cs` 放词条证据，`SelectionGeometry.cs` 放坐标及空间命中，`ReconstructionPlanner.cs` 放纯计划，`RevisionStore.cs` 放持久版本，`EditingCoordinator.cs` 放串行执行，`CspEditingAutomation.cs` 放已确认状态下的 UI 操作。`EditingPanel.cs`、`EventSelectionOverlay.cs`、`GlobalHotkey.cs` 负责用户交互。检查文件与职责一一对应，不把功能堆进 DemoController。

跨程序通用重放 DTO 位于 `replayer/packetreplayer/source/ReplaySchedule.cs`，不依赖宿主 Editing 类型；paster 的新 JSON 请求版本为 `memoline.image-composition.v1`。现有 standalone API 保留，新增适配接口。

宿主领域契约（任务 1 定义，后续按同名类型消费）：

```csharp
readonly record struct CanvasExtent(int Width, int Height);
readonly record struct LayerKey(string SessionId, long Generation, string Uuid);
readonly record struct EditableStrokeKey(string SessionId, ulong BeginAppendId);
readonly record struct DirtyImageKey(LayerKey Layer, string PacketId, string ImageId);
record PixelRegion(IReadOnlyList<PointF> Polygon); // 原尺寸画布像素坐标
record CausalityEvidence(string RuleVersion, IReadOnlyList<string> Terms, ulong StateAppendId);
record ImpactEvidence(PixelRegion? WriteRegion, PixelRegion? ReadRegion,
    IReadOnlyList<LayerKey> ReadLayers, bool WriteComplete, bool ReadComplete, string Source);
record LayerSeed(LayerKey Layer, CanvasExtent Canvas, ulong BeforeAppendId,
    string RgbaAsset, string Sha256, bool FullResolution, bool PrecedesFirstStroke);
record EditableStroke(EditableStrokeKey Key, ulong BeginEventId, ulong OperationId,
    LayerKey Layer, PacketReplay.PacketStroke Replay, ImpactEvidence Impact, CausalityEvidence Causality);
record DirtyImage(DirtyImageKey Key, long TriggerTicks, string CaptureGroup,
    Rectangle Bounds, string NowAsset, string BeforeAsset, string MaskAsset,
    IReadOnlyList<EditableStrokeKey> Events, ImpactEvidence Impact);
record EditSnapshot(string SessionId, long Generation, long Frequency, CanvasExtent Canvas,
    ulong BoundaryAppendId, long AggregationOffset, IReadOnlyList<JsonElement> Frames,
    IReadOnlyList<DirtyImage> Images, IReadOnlyList<LayerSeed> Seeds);
record EditIndex(EditSnapshot Snapshot, IReadOnlyDictionary<EditableStrokeKey, EditableStroke> Strokes,
    IReadOnlyDictionary<DirtyImageKey, DirtyImage> Images, IReadOnlyList<PlanIssue> Issues);
enum EditKind { EventEdit, PreciseUndo }
record EventChange(bool Delete, IReadOnlyDictionary<string, double> BrushNumbers);
record EditRequest(EditKind Kind, LayerKey CurrentLayer, PixelRegion Selection,
    IReadOnlyDictionary<EditableStrokeKey, EventChange> Changes);
record PlanIssue(string Code, string Message, DirtyImageKey? Image = null);
record ReconstructionPlan(Guid TransactionId, EditIndex Index, EditRequest Request,
    IReadOnlyList<LayerPlan> Layers, IReadOnlyList<PlanIssue> Issues) { bool CanExecute => Issues.Count == 0; }
record LayerPlan(LayerKey Layer, LayerSeed Seed, IReadOnlyList<ReconstructionSegment> Segments);
abstract record ReconstructionSegment(int Ordinal);
record ComposeSegment(int Ordinal, IReadOnlyList<DirtyImageKey> Images) : ReconstructionSegment(Ordinal);
record ReplaySegment(int Ordinal, IReadOnlyList<DirtyImageKey> ReplacedImages,
    PacketReplay.ReplaySchedule Schedule) : ReconstructionSegment(Ordinal);
```

`NowAsset / BeforeAsset / MaskAsset` 指向不可变资产；直接合成的 before guard 取完整历史层快照，不能取裁切图片中被清零的 unchanged 像素。索引在读入时验证引用、会话、尺寸、CRC/hash 和边界，歧义进入 Issues。

检查入口命令（下文引用简称；每个测试文件要在现有 Program 中注册）：

- **Host:** `dotnet run --project Memoline_demo_csponly/checks/MemolineDemo.Checks.csproj -c Release`，通过输出 `Memoline integration checks passed.`。
- **Native:** `dotnet test recognizer/Recognizer/tests/BehaviorRecognizer.Tests/BehaviorRecognizer.Tests.csproj -c Release`，通过为退出码 0、Failed 0。
- **Watcher:** `dotnet run --project "Organizer/canvas layer watcher/checks/CanvasLayerWatcher.Checks.csproj" -c Release`，现有 checks 全部通过。
- **Replay:** `dotnet run --project replayer/packetreplayer/checks/PacketReplayChecks.csproj -c Release`，现有及新增 checks 全部通过。
- **Python:** `py -3 -m unittest discover -s recognizer/recorder_integration/tests -p "test_*.py" -v`，通过输出 OK；如 PATH Python 缺现有 OCR 依赖，用现有 Recognizer 包的 Python，不另装依赖。
- **Node:** 在 `replayer/dirty matrix paster/backend` 运行 `node --test --test-isolation=none checks/*.test.mjs`，通过为 fail 0。

## Task 1: 完整接触索引、词条证据与源码集成边界

**Files:** Create `Memoline_demo_csponly/source/Editing/{EditingModels,EditIndex,CausalityClassifier}.cs`、`replayer/packetreplayer/source/ReplaySchedule.cs`；Modify `Memoline_demo_csponly/source/MemolineDemo.csproj`、`Memoline_demo_csponly/checks/Program.cs`；Test `Memoline_demo_csponly/checks/{EditIndexChecks,EditingFixtures}.cs`。

**Interfaces:** Produces `EditIndexBuilder.Build(EditSnapshot snapshot) -> EditIndex`、`CausalityClassifier.Classify(JsonElement state, ulong stateAppendId) -> CausalityEvidence` 及上文领域类型。ReplaySchedule 定义 `ScheduledStroke(SessionId, BeginAppendId, Stroke, State, Mode, BrushOverrides)`、`ReplayLayerRoute(SourceUuid, WorkingUuid, WorkingName, CaptureFrameId)`、`ReplaySchedule(Frequency, Strokes, StateSteps, LayerRoutes)`；类型分别为 string/ulong/MemolineReplayStroke/HistoricalState/ReplayMode/IReadOnlyDictionary<string,double>、string/string/string/long、long/IReadOnlyList<ScheduledStroke>/IReadOnlyList<PacketStep>/IReadOnlyDictionary<string,ReplayLayerRoute>。

- [ ] 写 `FullContactAcrossPacketsIsIndexedOnce`、`UnknownToolMouseAndOutsideAreExcluded`、`PropertyPresenceIsCausal`、`DualBrushBlendIsNotCanvasBlend`。EditingFixtures 创建 CRC 合格的原生 begin/sample/end、状态锚点和两包多图像；关键断言：
  ```csharp
  Check(index.Strokes.Count == 1 && index.Strokes.Single().Key.BeginAppendId == 10);
  Check(index.Strokes.Single().Value.Replay.Stroke.Samples.Select(p => p.AppendId).SequenceEqual(new ulong[] {10,11,12,20}));
  Check(ClassifyDisabled("Ink", "Color mixing").Terms.Count == 1);
  Check(ClassifyDisabled("Dual brush", "Blending mode").Terms.Count == 0);
  ```
- [ ] 运行 Host，确认新增索引/分类接口缺失导致失败；保留已有基线检查结果。
- [ ] 实现上述接口；扫描截至 boundary 的完整原生接触，不使用按 packet 裁切的 Plan；保留中断/未完笔的不可执行原因。分类别名取设计文档的官方词条，版本固定 `csp-property-presence-v1`，工具资格和来源需明确确认。
- [ ] 宿主源码链接 packetreplayer 除 Program/PacketReplayWindow/AssemblyInfo 的核心，StrokeReplay 使用 PacketReplay.csproj 现有排除项；保留本地唯一 BundleArchive，禁止项目循环引用。运行 Host 和 Replay，要求均通过。
- [ ] 仅提交本任务文件：`git commit -m "feat: index complete editable contacts and causal brush evidence"`。

## Task 2: 非终止编辑边界、来源租约与一致实时前缀

**Files:** Create `recognizer/Recognizer/src/BehaviorRecognizer/Capture/ReplayCaptureLease.cs`、`Memoline_demo_csponly/source/Editing/LiveEditSnapshotReader.cs`；Modify `recognizer/Recognizer/src/BehaviorRecognizer/Capture/{RecorderInputControlServer,UnifiedInputCapture}.cs`、`recognizer/Recognizer/src/BehaviorRecognizer/Bootstrap/CapabilityOrchestrator.cs`、`recognizer/core/memoline/Realtime/RecorderRealtimeHub.cs`、`Organizer/canvas layer watcher/source/{RecorderControlClient,RecognizerMonitor,WatchState}.cs`、`Memoline_demo_csponly/source/{AggregateSession,DemoController,BundleArchive}.cs`；Test Native `InputControlTests.cs / RealtimeInputTests.cs / RecorderRealtimeTests.cs`、Host `EditBoundaryChecks.cs`。

**Interfaces:** Consumes Task 1 EditSnapshot。Produces `PrepareEditBoundaryAsync(CancellationToken) -> Task<CaptureRequest?>`、`LiveEditSnapshotReader.ReadAsync(string mechanicalPath, string aggregationPath, ulong boundaryAppendId, long aggregationOffset, CancellationToken) -> Task<EditSnapshot>`。控制请求为 `prepareEditBoundary(requestId)`、`beginReplay(transactionId,sessionId)`、`renewReplay(transactionId,epoch)`、`endReplay(transactionId,epoch)`；响应需 requestId/sessionId、boundary appendId/ticks 或 lease epoch/expiry/provider capability。

- [ ] 写 `EditBoundaryIsRepeatableAndNotFooter`、`LivePrefixStopsAtCommittedCut`、`ReplayOriginDoesNotSuppressPhysicalInput`、`LeaseExpiryStopsInjection`：
  ```csharp
  Check(first.AppendId < second.AppendId && !native.IsComplete);
  Check(snapshot.Frames.All(f => Append(f) <= boundary) && snapshot.AggregationOffset == committedCut);
  Check(userContact.Recorded && execution.Aborted && !index.Contains(syntheticContact.Key));
  Check(stateRequest.Succeeded && expiredLease.ForbiddenToInject);
  ```
- [ ] 运行 Native + Host，确认缺少新协议/边界导致失败。
- [ ] 在输入单消费者队列中加入可取消 idle barrier，接触结束后追加 `editBoundaryRequested`，请求 ID 幂等但不同 ID 可重复；实时 hub 发布同一原生帧。watcher 冻结同一代次证据、完成捕获，AggregateSession flush 后公布对应聚集 offset，不等待 footer。
- [ ] 实现租约 epoch 与心跳；合成来源单独归属，状态请求保持工作。真实 OTD/硬件笔永不吞掉并中止重放；被动笔来源无法可靠区分时返回 `ReplayOriginUnconfirmed`。通过实际来源能力确定可注入，不能把整个租约期间的所有笔都说成合成输入。
- [ ] 实现 bounded reader；核对 marker、session、clock、连续 appendId、聚集已提交 cut，忽略未写完尾帧但不掩盖损坏帧。运行 Native + Host；提交 `feat: add repeatable edit boundaries and scoped replay provenance`。

## Task 3: 原尺寸历史基础与初始化层基线

**Files:** Create `Organizer/canvas layer watcher/source/LayerBaselineCapture.cs`；Modify `Organizer/canvas layer watcher/source/{SnapshotHistory,SaveCapture,CanvasThumbnail}.cs`、`Memoline_demo_csponly/source/{AggregateSession,DemoController}.cs`、`Memoline_demo_csponly/source/Editing/LiveEditSnapshotReader.cs`；Test Watcher `DiffChecks.cs`、Host `FullResolutionHistoryChecks.cs`。

**Interfaces:** Consumes Task 2 markers。Produces packet manifest 的 `fullLayerBefore/fullLayerNow` 资产引用、尺寸、SHA256、captureGroup、layer UUID、before/after boundary；`LayerBaselineCapture.CaptureAsync(...现有 SaveCapture 依赖..., CancellationToken) -> Task<IReadOnlyList<LayerSeed>>` 在源码中使用既有 control/bridge 类型，返回 Task 1 LayerSeed 的等价 watcher DTO，由宿主投影。

- [ ] 写 `FullCanvasSurvivesSourceRotation`、`BaselineMustPrecedeFirstStroke`、`OldThumbnailCannotSeedExactPlan`：
  ```csharp
  Check(asset.Width == 4961 && asset.Height == 7016 && asset.Sha256 == originalHash);
  Check(!duringCapture.PrecedesFirstStroke && cleanInitial.PrecedesFirstStroke);
  Check(oldPreview.Width == 1132 && oldPreview.Height == 1600 && !oldPlan.CanExecute);
  ```
- [ ] 运行 Watcher + Host，确认原尺寸资产/先行基线缺失失败。
- [ ] 在旋转删除 SnapshotHistory 两槽源文件之前，保存 packet 所有权下的完整 before/now PNG 与 RGBA/hash 元数据；缩略图只用于显示，AggregateSession 将完整资产纳入 hash 去重。
- [ ] 为录制初始化保存可绘制层基线，使用 saved CLIP 的 ID/UUID 导出完整层。比较捕获前后原生 marker，期间有合格笔输入则不宣称首笔之前基线；新层同理。记录照常保留，缺基线的先前事件禁用精确执行，后续已确认基础可用。
- [ ] 运行 Watcher + Host；提交 `feat: persist full resolution layer history and verified baselines`。

## Task 4: 指定单图像的完整基础合成与像素兼容性检查

**Files:** Create `replayer/dirty matrix paster/backend/checks/seeded-composition.test.mjs`；Modify paster `backend/{packet,composition,engine}.mjs`、`source/Backend.cs`；Create `Memoline_demo_csponly/source/Editing/ImageCompositionClient.cs`；Test Host `ImageCompositionChecks.cs`。

**Interfaces:** Produces `loadImages(input, imageIds)`、`composeImages(request)`、`exportImageComposition(request, outputDirectory)`；新命令 `--compose-images request.json`。request schema 包括 seed 完整 RGBA/CanvasExtent/LayerKey/boundary、明确 image keys/triggerTicks/captureGroup/bounds/RGBA/changeMask、独立 beforeGuard(bounds/RGBA/domainMask/evidence)。结果 `status=ready|unconfirmed|conflict`、full-canvas RGBA、updateMask、metadata/compatibility。宿主 `ComposeAsync(LayerSeed seed, IReadOnlyList<DirtyImage> images, CancellationToken) -> Task<CompositionResult>`，CompositionResult 含 Status/FullLayer/Issues。

- [ ] 写 `EmptySelectionKeepsSeed`、`SelectedImageOnly`、`GuardAndChangeMaskHaveDifferentDomains`、`SameColorWriteAndChangedBaseAreNotCertified`、`SameTimeConflictHasNoPublishedOutput`：
  ```javascript
  assert.deepEqual(composeImages(empty).rgba, seed.rgba);
  assert.deepEqual(composeImages(oneImage).metadata.appliedKeys, [oneKey]);
  assert.equal(composeImages(transparentOverChangedBase).status, 'unconfirmed');
  assert.equal(composeImages(sameColorHiddenWrite).status, 'unconfirmed');
  assert.equal(composeImages(conflictingCaptureGroup).rgba, undefined);
  ```
- [ ] 运行 Node，确认新增接口或安全条件缺失失败。
- [ ] 复用现有 PNG 校验，以明确 ID 读取图像，缺/重复 ID 拒绝，空选择不能替换成整包。private accumulator 从 seed 复制；按 chronology replacement 保留 alpha=0，输出完整画布，不能用累计 mask 清除 seed。
- [ ] beforeGuard 与 changeMask 分开。完整已确认依赖域与 historical before 一致才可复用；未知域可用完整输入层及必要参考层完全一致作保守证明，不能靠窄 mask。底色不匹配、同色隐写无法证明、缺读取来源均给原因，不偷偷转为重放。失败不发布部分输出。
- [ ] Backend 新接口接受 backendRoot/CancellationToken；宿主客户端运行独立 Node 后端并校验 ready 资产。保留旧 ComposeAsync/composePackets。运行 Node + Host；提交 `feat: compose selected dirty images from guarded full layer seeds`。

## Task 5: 同帧图层行、眼睛位置和身份匹配

**Files:** Modify `recognizer/recognizer_core/layer_state/{core,layer_reader}.py`、`recognizer/recorder_integration/{pipeline,state_timeline}.py`；Create `Memoline_demo_csponly/source/Editing/LayerIdentityResolver.cs`；Test Python `test_layer_locator.py / test_state_request.py / test_state_timeline.py`、Host `LayerIdentityChecks.cs`。

**Interfaces:** Produces `LayerStateCore.process_snapshot(panel_image) -> dict`；rawResult 增加 `layerLocator={panelRoi,captureFrameId,rows:[{rowBounds,eyeBounds,name,selected,visible,visualId}]}`，语义 `currentLayerState` 仍为选中名称。宿主 `Resolve(JsonElement liveLayers, JsonElement savedTree, LayerKey expected, long requiredCaptureFrame) -> LayerIdentityResult`；返回 verified UUID/selected row/eye screen rectangle/visibility 或原因，visualId 不当永久 ID。

- [ ] 写 `LocatorAndStateComeFromSameFrame`、`SelectedNameWireCompatibility`、`DuplicateNamesAndCollapsedParentAreNotGuessed`、`StaleRoiCannotAuthorizeEyeClick`：
  ```python
  self.assertEqual(semantic_state("currentLayerState", result), "图层 2")
  self.assertEqual(result["layerLocator"]["captureFrameId"], result["captureFrameId"])
  ```
  Host 断言重复名称/旧帧 `Verified == false`；已隐藏行 `ToggleRequired == false`，父层隐藏仍保留 effective_visible 原因。
- [ ] 运行 Python + Host，确认缺 rich locator 或身份拒绝失败。
- [ ] 复用现有 row detection，在同一帧记录 ROI 和眼睛矩形、未知可见性；process 兼容旧 string 返回。通过 UUID、层级和当前 saved tree 匹配；必要时现有快捷键展开并重新采集，无法确认就停止。
- [ ] 运行 Python + Host；提交 `feat: publish verified same frame layer and visibility locators`。

## Task 6: 整矩阵重放计划、传播与精确撤销后缀

**Files:** Create host `source/Editing/{ReconstructionPlanner,SelectionGeometry}.cs`、`checks/{ReconstructionChecks,SelectionChecks}.cs`。

**Interfaces:** Consumes Tasks 1/3/4。Produces `ReconstructionPlanner.Build(EditIndex index, EditRequest request) -> ReconstructionPlan`、`SelectionGeometry.Map(IReadOnlyList<PointF> screenPolygon, JsonElement transform, CanvasExtent canvas) -> PixelRegion`、`SelectPreciseUndo(EditIndex, LayerKey current, PixelRegion) -> IReadOnlyList<EditableStrokeKey>`、`HitTest(EditIndex, IReadOnlySet<DirtyImageKey>, PointF cursor, float radius) -> IReadOnlyList<EditableStrokeKey>`。

- [ ] 写 `WholeCausalImageIncludesOrdinaryBrushes`、`MixedImageDeletionPreservesOrder`、`SharedContactsMergeBeforeSorting`、`FutureComposeNeverPrecedesEarlierReplay`、`CopyStampRemoteReadIsNotOutputBounds`、`PreciseUndoKeepsInterleavedSuffix`：
  ```csharp
  Check(replay.BeginAppendIds.SequenceEqual(new ulong[] {10,30,50})); // 删除20，普通30仍重放
  Check(allScheduled.Distinct().Count() == allScheduled.Count());
  Check(plan.Layers[0].Segments.Select(s => s.Ordinal).SequenceEqual(new[] {0,1,2}));
  Check(selected.Count == 50 && selected.SequenceEqual(expectedNewestFirst));
  Check(suffix.Select(s => s.BeginAppendId).SequenceEqual(expectedOriginalOrder));
  Check(!unknownStampPlan.CanExecute && unknownStampPlan.Issues.Any(i => i.Code == "ReadFootprintUnconfirmed"));
  ```
- [ ] 运行 Host，确认 planner/selection 缺失失败。
- [ ] Map 使用 normalized screenToCanvas 六元素再乘 W/H，验证 session/generation/尺寸；保留旋转多边形真实相交。表层选择按当前层最终图像覆盖及透明像素判断，保留事件可见归属证据；只有重叠外框不够，不可确认则 Issues。
- [ ] 建立 event→image 双向索引与原始时间轴。从 earliest affected 的前置完整层 seed 开始，对 modified images + influenced causal images 取全部合格事件。共享 replay contacts 合并执行段再去重排序；不能按图像名/文件序执行；直接合成与重放同捕获组必须有独立性或先后依据，否则拒绝。跨路径共享接触无法证明原图输出归属时拒绝，不扩大重放规则。
- [ ] 传播同时使用确认读域/参考层与写域；brush override 取原/新范围并集，无法界定给原因。ComposeSegment 遇 replay 边界即停止，下一 seed 来自实际 capture。PreciseUndo 建立最早选笔之前的全层基础，然后完整 suffix 时间轴中仅所选笔离散，其余保持归属及时间位置；不足50取实际数量。
- [ ] 增加同色无差异、半透明、混色、旋转选区和缺首基线用例，运行 Host；提交 `feat: plan chronological whole matrix replay and precise undo suffixes`。

## Task 7: 任意完整事件清单重放、参数覆盖与工作层别名

**Files:** Create `replayer/packetreplayer/source/EventReplayRunner.cs`；Modify `source/{PacketArchiveReader,PacketReplayRunner,PacketReplayStateTable,PacketStateRestorer,PacketRecorderControl,ReplaySchedule}.cs`（同 packetreplayer）、`replayer/strokereplayer/source/WindowsViewInput.cs`；Test `replayer/packetreplayer/checks/EventReplayChecks.cs`、Host `ReplayScheduleChecks.cs`。

**Interfaces:** Consumes ReplaySchedule/ReplayLayerRoute from Task 1、lease Task 2。Produces `EventReplayRunner.RunAsync(ReplaySchedule schedule, LivePacketFeed feed, double speed, ReplayOptions options, Action<string> status, CancellationToken token) -> Task`；沿用 PenReplayEngine 并允许可注入 factory/delay 检查。旧 PacketReplayRunner 用 adapter 保留 API；新清单不虚构 PacketInfo。

- [ ] 写 `FullCrossPacketContactIsNotClipped`、`OverrideSurvivesPerStrokePrepare`、`HistoricalLayerRoutesToVerifiedWorkLayer`、`DiscreteSelectionRetainsOrdinaryInterleaving`、`CancellationReleasesPenAndKeys`：
  ```csharp
  Check(fake.SentAppendIds.SequenceEqual(new ulong[] {10,11,12,20}));
  Check(fake.BrushAtDown[changedKey]["Brush size"] == 12);
  Check(fake.LayerAtEverySample.All(id => id == workingUuid));
  Check(fake.TransitionsFor(selectedKey).Last() == PointerTransition.UpAndLeave);
  Check(fake.TransitionsFor(unselectedKey).Last() == PointerTransition.Up);
  Check(fake.AllReleased && fake.DeletedLayers.Count == 0);
  ```
- [ ] 运行 Replay + Host，确认 event runner/route/override 缺失失败。
- [ ] 复用历史状态解析，保留锚点预留排序，参数覆盖写入每笔 State.Brush，再 PrepareStroke。删除仅跳过所选笔。层路由在恢复和每样本实时确认均生效，保留原层身份用于历史而非注入。
- [ ] 只开放可恢复且可读回确认的名称/数字参数；色彩/参考层/锁透明等上下文无法等效就预检停止。按实际相邻笔算 gap，集中/离散逐笔选用；keyboard/mouse SendInput 采用现有 RecorderInputTag，PT_PEN 仍依靠已协商可靠来源租约。
- [ ] 运行 Replay + Host；提交 `feat: replay complete ordered event schedules into verified layer aliases`。

## Task 8: 追加编辑版本、检查点及封盘校验

**Files:** Create host `source/Editing/{RevisionStore,EditingJournalModels}.cs`、`checks/RevisionChecks.cs`；Modify `source/{BundleArchive,DemoController}.cs`、`FORMAT.md / INTERFACES.md`。

**Interfaces:** Produces `RevisionStore.AppendAsync(EditJournalRecord record, CancellationToken) -> Task<CommittedRevision>`、`ReadVerified(string workRoot) -> IReadOnlyList<CommittedRevision>`。record 含 schema=`memoline.edit-revision.v1`、parent revision hash、transaction/session/generation、original boundary、event keys/changes、source image keys、layer route、segment ordinal、PNG/RGBA hash、status。结果含 RevisionId/Sha256/Flushed/AssetPaths；seal 新 index formatVersion=3（老 v1/v2读取兼容），编辑流与资产独立 entry。

- [ ] 写 `OriginalHistoryBytesDoNotChange`、`CheckpointNeedsDurableAssets`、`BrokenRevisionReferenceFailsVerify`、`SealReadCompactPreserveRevisions`：
  ```csharp
  Check(beforeMechanicalHash == afterMechanicalHash && beforePacketHash == afterPacketHash);
  Check(committed.Flushed && committed.Sha256 == readBack.Sha256);
  Check(!halfWrittenCheckpoint.IsCommitted && corruptReference.VerifyFailed);
  Check(compacted.RevisionHashes.SequenceEqual(sealedBundle.RevisionHashes));
  ```
- [ ] 运行 Host，确认新版本记录或封盘引用缺失失败。
- [ ] 资产写临时文件、flush、原子安装、hash 校验后，追加 checkpoint 并 flushToDisk/readback。崩溃尾部不当有效 checkpoint；拒绝坏 parent/hash/原生 key。Bundle 验证、compact、extract 兼容原版并保留新编辑资产；不修改原生机械指针。
- [ ] 运行 Host 及 Replay 的旧容器检查；提交 `feat: persist append only edit revisions and durable capture checkpoints`。

## Task 9: 剪贴板捕获与严格工作层事务

**Files:** Create host `source/Editing/{ClipboardLayerCapture,CspEditingAutomation,EditingCoordinator}.cs`、`checks/{ClipboardCaptureChecks,EditingTransactionChecks}.cs`；Modify paster `source/LayerClipboard.cs`、host `source/DemoController.cs`、watcher `source/MainWindow.cs / WatcherHost.cs`。

**Interfaces:** Consumes Tasks 2/4/5/7/8。Produces `ClipboardLayerCapture.ReadAsync(CanvasExtent expected, VerifiedCopyProvenance provenance, CancellationToken) -> Task<CapturedLayer>`；`IEditingAutomation` 的 SelectAndHideOriginalAsync/InstallWorkingLayerAsync/CopyWorkingLayerAsync/DeleteVerifiedWorkingLayerAsync/InstallFinalLayerAsync 均需 transactionId 和 verified LayerKey；`EditingCoordinator.ExecuteAsync(ReconstructionPlan plan, CancellationToken) -> Task<EditExecutionResult>`。CapturedLayer 含完整 RGBA、canvas、clipboard sequence/format、verified layer/定位证据；EditExecutionResult 使用 Global Constraints 的状态枚举及最后 checkpoint。

- [ ] 写 `CaptureFailureNeverDeletes`、`DurableCommitPrecedesDeletion`、`WrongWorkLayerCannotDelete`、`CancelAfterPartialStrokeDoesNotAutoReplay`、`ClipboardRejectsCropAndUnknownAlpha`：
  ```csharp
  Check(log.SequenceEqual(new[] {"verify-work", "copy", "capture", "flush-revision", "verify-work", "delete-work"}));
  Check(failedCapture.DeletedLayers.Count == 0 && failedFlush.DeletedLayers.Count == 0);
  Check(wrongIdentity.DeletedLayers.Count == 0 && cancelled.RecoveryRequiresConfirmation);
  Check(png.Rgba.SequenceEqual(expectedRgba) && dib.Rgba.SequenceEqual(expectedRgba));
  Check(cropped.Rejected && unsupportedDibAlpha.Rejected);
  ```
- [ ] 运行 Host，确认事务/捕获缺失失败。
- [ ] 从原始 PNG/CF_DIBV5 读取拥有所有权的 bytes，解析 supported channel masks/row orientation，明确 BGRA→RGBA，拒绝未知 alpha，不用 GetImage/重绘/缩放。clipboard sequence 必须由本次 copy 更新；内容字节不能替代层身份。CSP 裁切 copy 不足全层时走既有 saved CLIP export-id 捕获，确认完整尺寸及定位，保留 copy provenance。
- [ ] 自动化仅点击 Task 5 同帧已匹配 eye；隐藏后重采并保存核 UUID/visible。paste 工作层取新 UUID/名字/位置，alias 到它；copy 捕获后交 Task 8 持久提交，重新确认相同 UUID 才使用“删除图层”快捷键。缺快捷键给具体配置缺口，不能用 Delete 清像素替代。
- [ ] 用同一 semaphore 协调编辑、后台保存、封盘；状态采集不被锁死。compose→replay→capture→durable checkpoint→delete，每轮 actual capture 成下一基础；最后安装可见替代层保留。失败释放所有输入，保留未提交工作层及 journal，恢复默认不自动补画半筆。运行 Host + Node；提交 `feat: execute verified work layer reconstruction transactions`。

## Task 10: Alt+M 面板、区域选择、白线预览和参数入口

**Files:** Create host `source/{EditingPanel,EventSelectionOverlay,GlobalHotkey,DemoApplicationContext}.cs`、`checks/EditingUiChecks.cs`；Modify host `source/{Program,DemoController}.cs`、watcher `source/MainWindow.cs`。

**Interfaces:** Consumes Tasks 2/6/9。Produces `DemoController.PrepareEditingAsync(CancellationToken) -> Task<EditIndex>`、`BuildPlan(EditRequest) -> ReconstructionPlan`、`ExecuteEditingAsync(ReconstructionPlan,CancellationToken) -> Task<EditExecutionResult>`；panel 仅通过 controller，不直接操作层。GlobalHotkey 封装 RegisterHotKey/UnregisterHotKey，MOD_ALT=0x0001、VK_M=0x4D、MOD_NOREPEAT=0x4000；overlay 命中半径默认12屏幕像素，按当前矩阵换算。

- [ ] 写 `AltMRegistersAndUnregisters`、`ClosingPanelKeepsRecording`、`HoverShowsOnlyNearbySelectedImageContacts`、`PreciseUndoUsesCurrentLayer`、`UnsupportedParametersAreNotOffered`：
  ```csharp
  Check(fakeHotkey.Modifiers == 0x4001 && fakeHotkey.Key == 0x4D && fakeHotkey.UnregisteredOnExit);
  Check(controller.Recording && panel.Hidden && tray.Available);
  Check(hover.Keys.SequenceEqual(expectedNearbyKeys) && hover.Color == Color.White);
  Check(precise.Request.CurrentLayer == confirmedLayer && precise.SelectedCount <= 50);
  Check(!parameterChoices.Contains("unsupported checkbox"));
  ```
- [ ] 运行 Host，确认集成 UI 行为缺失失败。
- [ ] 创建 application context 管理原 watcher 录制窗口、托盘和控制面板。Alt+M 先建非终止编辑边界再开放选择；冲突显示 Win32 实际错误。支持矩形/多边形区域，跨图层脏图像列表、hover 白线及扫选完整笔，删除/可恢复数字参数覆盖。
- [ ] 精确撤销使用已确认当前层与 Task 6 selection；显示实际选笔数、PlanIssue 和执行状态；执行期间禁重复操作，取消走 coordinator。保留原录制窗口与命令行/smoke入口。运行 Host；提交 `feat: add global Alt M event editing and precise undo controls`。

## Task 11: 发布依赖、整链检查与 CLIP 副本实机验收

**Files:** Modify `Memoline_demo_csponly/Build.ps1 / README.md / INTERFACES.md`；Create `Memoline_demo_csponly/checks/EditingEndToEndChecks.cs`、`Memoline_demo_csponly/docs/superpowers/verification/2026-10-07-csp-event-editing.md`。

**Interfaces:** Consumes完整链路；produces 可从 demo 根目录启动的 `Memoline.exe`，私有 Recognizer 的新 native/Python 源码、paster/backend 与 Node runtime，及可复查验收记录。

- [ ] 用 fake CSP 构造多层/多图片/跨包/两次重放 capture fixture，写 `EndToEndPreservesChronologyAndOriginalBytes`：断言原文件 hash不变、唯一事件执行序列等于预期、第二 seed 等于第一实际 capture、最终层保留且中间层只在 commit 后删除。运行 Host，确认尚未连接的整链失败。
- [ ] 接好 controller/资产/版本链，更新 Build 复制 paster 核心、锁定 node_modules 和现有 Node，复制修改后的 Python integration/core 后核 hash，不运行旧包代码。保留已校准 settings/config，所有 staging 移动/删除在 demo 内核路径，不含历史录制。运行 Host、Native、Watcher、Replay、Python、Node，要求各退出0。
- [ ] 执行 `& ./Memoline_demo_csponly/Build.ps1`；核根入口及 publish 输出、依赖哈希和启动 smoke，无需启动两个独立 replayer 窗口。确认发布失败不会替换成功入口。
- [ ] 仅在 CLIP 副本验证：Alt+M后台呼出、旋转框选、跨层列表、eye隐藏、UUID路由、粘贴位置、复制全尺寸捕获、commit后删临时层、最终层保留；检查删除/数字参数覆盖及50笔离散撤销实际撤销次数，比较整个目标层 RGBA。另做捕获失败/用户笔打断演练。记录数据、结果和限制；没有真实撤销及像素依据只报 Executed，不能报 PixelVerified。若当前工具无法控制 CSP，完成离线及发布检查并明确保留此项待实机验证。
- [ ] Fresh reviewer 做整分支设计与安全审查，修复发现问题并运行对应检查；提交 `feat: publish integrated CSP event reconstruction workflow`。只有全部适用检查有证据后才汇报完成。

## 执行顺序与自审

依赖顺序：1 → 2 → 3；4、5 在其契约确定后可独立实施；6 依赖1/3/4；7依赖1/2/5；8依赖1/3；9依赖2/4/5/7/8；10依赖2/6/9；11接全链。每次只让一个任务修改共同宿主接线，避免并行覆盖。

执行开始时按 using-git-worktrees 检查已有附件并选择隔离工作树；当前用户工作区先保留，最终更新到明确的 Memoline_demo_csponly 路径。每任务先新增有意义的行为失败检查再实现，提交只 stage 本任务文件；若保护的 .git 需要升级权限，使用精确 git 操作。所有真实输入注入只在已确认的副本文档中进行。

自审完成：设计各段已映射到任务，五条用户补充约束分别落到上述测试；共享 DTO 与 runner 无宿主循环依赖；原始 API、旧容器及语义状态兼容有检查；直接合成未确认条件不伪装已解决。任务3的 watcher基线DTO与宿主 LayerSeed通过明确投影连接，避免共享源码反向依赖。
