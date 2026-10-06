# Recognizer 实时状态接口（schemaVersion 1）

实现位于 `Memoline.Core.dll`，命名空间 `BehaviorRecognizer.Realtime`。Recognizer 每次录制自动建立当前 Windows 用户专用的本机命名管道。输入监听、核心解析和文件写入不等待订阅客户端。

## 连接方式

录制启动后，控制台打印 `实时订阅管道` 和 `接口描述`。接口描述文件为 `procedure/stroke/<session>.memoline.live.json`，包含 `schemaVersion`、`pipeName`、`sessionId`、`processId`、`filePath`、`liveFilePath`、`frequency` 和 `channels`。管道名为 `MemoLine.Recognizer.<sessionId>`；一条管道支持多个客户端以及不同的频道过滤。

录制初始化期间就能订阅；尚未取得的输入或核心结果没有快照。停止后描述文件保留用于检查，但该会话的管道关闭；下次录制使用新会话和新管道。

命令行订阅，无需再次启动录制或打开 HID：

```powershell
# 使用控制台打印的实际接口描述路径替换 <session>。
$recorderExe = 'D:\Memo_Line\Memo_Line\recognizer\Recognizer\publish\win-x64\BehaviorRecognizer.exe'
& $recorderExe --subscribe all --endpoint 'D:\Memo_Line\Memo_Line\recognizer\Recognizer\publish\win-x64\procedure\stroke\<session>.memoline.live.json'
& $recorderExe --subscribe keyboard,mouse --pipe 'MemoLine.Recognizer.<sessionId>'
& $recorderExe --subscribe tablet --pipe 'MemoLine.Recognizer.<sessionId>'
& $recorderExe --subscribe cores --pipe 'MemoLine.Recognizer.<sessionId>'
& $recorderExe --subscribe core.canvasViewState --pipe 'MemoLine.Recognizer.<sessionId>'
& $recorderExe --subscribe shortcuts,layers,layerstage --pipe 'MemoLine.Recognizer.<sessionId>'
```

stdout 为 UTF-8 JSONL（一行一条消息），stderr 输出连接错误，Ctrl+C 取消当前订阅。正常录制结束返回 0；连接失败、突然断开或数据缺口返回 1。`all` 展开为全部 13 个频道，`cores` 展开为 6 个解析核心频道。

`shortcuts/configurationUpdated` 发布完整的已保存快捷键与功能对照表，含配置目录、来源文件、绑定、修饰键操作、滚轮配置及 warnings；连接时回放最新配置，Recognizer 重载配置后再推送更新。独立查询可使用 `BehaviorRecognizer --shortcuts [--config-dir <directory>]`，无需开始录制。

图层状态核心 `currentLayerState` 对应 `layerstage/currentLayerUpdated`，只发布当前所处图层名称。文件图层结构核心 `clipState` 对应 `layers/layerStructureUpdated`，只发布完整父子结构、内部属性和保存时的当前图层 ID。两个接口的订阅与缓存独立。各自保留对应核心消息的 `status/state/lastConfirmedState/changedFields/evidence/error`、时间和原生帧身份；每条专用投影另占一个实时 sequence。`all` 同时包含原始核心消息与这两个图层投影，消费者可按频道筛选。完整接入说明见 [Memoline 集成接口](../../../../Memoline_demo_csponly/INTERFACES.md)。

`subtools/ocrUpdated` 是新增 `core.subtoolState/stateUpdated` 的独立投影，提供本帧子工具名称的 `data.ocrEntries[]` 和子工具组名称的 `groupEntries[]`：`name/text/kind/bbox/screenBbox/coordinateSpace/score/selectionState/selectionScore/matchStatus/matches`。`matches` 保留完整配置身份和工具/组路径，重名节点不合并。`evidence.panelRoi/captureId/capturedTicks/captureEndTicks` 关联该面板实际采集帧。名称未变化时也更新位置；截图后失败保留 ROI/采集身份并返回空位置，快照遵循现有确认结果与最新诊断的回放语义。

快捷键配置新增 `toolCatalog` 全部工具/组/子工具节点及 `brushPackages` 已安装子工具组列表，工具绑定另有 `tool/subtools/savedSelectedSubtoolId`。保存选中项的 `selectionSource=savedCspConfiguration` 与 OCR 当前图片选择证据独立。接入细节见 [子工具及配置目录](../../../../Memoline_demo_csponly/INTERFACES.md#子工具面板-ocr-接口subtools)。C# 新增 `FollowSubtoolsAsync` 与 `SubscribeSubtools`。

外部 C# 程序引用 `Memoline.Core.csproj` 或程序集：

```csharp
using BehaviorRecognizer.Realtime;

await foreach (var message in RecorderRealtimeClient.FollowKeyboardAsync(pipeName, token))
    Handle(message);
// 另外提供 FollowMouseAsync、FollowTabletAsync、FollowCoreAsync(pipeName, module, token)、FollowCoresAsync、FollowShortcutsAsync、FollowLayersAsync、FollowLayerStageAsync。
// 可同时过滤多个频道，并关闭连接时的历史快照：
await foreach (var message in RecorderRealtimeClient.SubscribeAsync(pipeName,
    ["keyboard", "core.colorState"], includeSnapshot: false, cancellationToken: token))
    Handle(message);
```

进程内可以直接创建 `RecorderRealtimeHub(writer)`，然后 `SubscribeKeyboard()`、`SubscribeMouse()`、`SubscribeTablet()`、`SubscribeCore(module)`、`SubscribeCores()`、`SubscribeShortcuts()`、`SubscribeLayers()`、`SubscribeLayerStage()`。返回 `RecorderRealtimeSubscription`，通过 `ReadAllAsync(token)` 消费，`DisposeAsync()` 退订。通用 `Subscribe(topics, includeSnapshot: true, capacity: 2048)` 可设置队列大小（最小 8）。先关闭 writer，再关闭 hub；若有 pipe server，最后关闭 server，以便发出 `sessionEnded`。

其他语言可直接使用 `\\.\pipe\MemoLine.Recognizer.<sessionId>`：双向字节管道，UTF-8 无 BOM。连接后 5 秒内发送一行请求并刷新：

```json
{"schemaVersion":1,"channels":["keyboard","core.colorState"],"includeSnapshot":true}
```

之后逐行读取 JSONL。未知频道或 schema 会返回 `system/error`。程序仅限同一 Windows 用户连接。

## 所有消息的公共字段

| 字段 | 信息 |
|---|---|
| `schemaVersion`, `sessionId` | 接口版本与录制会话 ID |
| `sequence` | 会话内实时数据发布序号；各频道共享递增序号。过滤频道造成的跳号是正常现象 |
| `channel`, `kind` | 频道与事件类型 |
| `isSnapshot` | `true` 表示连接时补发的最后一条已知消息；保留其原序号和时间 |
| `appendId` | 对应 `.memoline` 持久化帧的追加 ID；系统消息及仅留在诊断日志的失败状态为 0 |
| `eventId`, `operationId`, `relatedEventIds` | 硬件帧 ID、一次点击/拖拽/笔迹的操作 ID、解析或中断所关联的硬件帧。状态帧 `eventId=0` |
| `ticks`, `appendedTicks`, `publishedTicks` | 采样/观察时间、帧入队时间、实时消息发布时间；均为从录制开始计的 Stopwatch ticks。秒数=`ticks / frequency`，不是 Unix 时间 |
| `deviceSource` | 若原帧提供：`deviceType`、`captureApi`、`deviceId`、`identification`；状态帧可以为 null |
| `data` | 各接口的具体数据，见下面各表 |

快照补发每个频道最后一条状态消息，不补发历史 `keyInput` 或 `shortcutResolved`；数位板另补发已知驱动配置和各设备信息。快照是已有事件，并非凭空生成尚未检测到的状态。消费者应按 `kind` 处理不同结构。

实时消息在帧入队后发布，**不代表磁盘已经完成刷盘**。持久化屏障仍为 `MemolineWriter.FlushAsync()`。完整历史、截图 PNG 和因果状态包从 `MemolineReader` 读取；实时订阅不传截图 blob。

## 键盘接口：`keyboard`

| `kind` | `data` 信息 |
|---|---|
| `keyDown`, `keyUp` | `action`、`key`（键名）、`vk`（虚拟键码）、`scanCode`、`extended`、`pressed`、`repeat`、`isModifier`、`heldKeys`（全部已知按住的 vk）、`modifiers`（Ctrl/Shift/Alt/Win）、`injected`、`extraInfo`、`guardIntercepted`、`nextHookResult`、`deviceSource` |
| `reset` | `action=reset`、`reason=cspLostFocus`、空 `heldKeys/modifiers`、`deviceSource`；清除失去焦点时的按键状态 |
| `keyInput` | 原有操作激活帧：主键、vk/扫描码、修饰键、`combination`、`repeat`、`heldKeys`、注入/Hook 信息、`shortcutMatch` 预留字段 |
| `shortcutResolved` | `resolution=matched/unmapped`、`matches`（快捷键目录匹配，包含命令和目标面板等）、`shortcutMatchPending=false`；通过 `relatedEventIds` 关联原操作 |

键盘更新只在 CSP 前台时采集；新按下/释放状态不会重复触发原有硬件操作。低级钩子不提供物理键盘 ID，来源精度为 `classOnly`。

## 鼠标接口：`mouse`

| `kind` | `data` 信息 |
|---|---|
| `cursor` | `x/y`（Windows 实际屏幕像素）、`inCsp`、`foreground`、`heldButtons`、`heldKeys`、`penContact`、`source=windowsCursor` |
| `mouseDown`, `mouseDrag`, `mouseUp` | `button=left/right/middle/x1/x2`、`x/y`、`heldButtons`、`heldKeys`；`operationId` 关联一次按住操作 |
| `mouseWheel` | `x/y`、`delta`（带符号的原始滚轮增量）、`axis=vertical/horizontal`、`heldButtons`、`heldKeys` |
| `mouseInterrupted` | `reason=cursorLeftCsp/penContact`、空 `heldButtons`、`heldKeys`；`relatedEventIds` 指向被中断操作 |
| `shortcutResolved` | 滚轮匹配结果，另含 `inputType=mouseWheel`、`axis`、`delta`；其余字段与键盘快捷键解析相同 |

光标观察最多约 30Hz、只在变化时发布，进入/离开 CSP 或前台变化优先发布；按钮、拖拽和滚轮保持原有事件路径。光标可以由数位笔移动，`cursor` 不代表检测到了某个物理鼠标。低级鼠标钩子同样没有物理设备 ID。

## 数位板接口：`tablet`

| `kind` | `data` 信息 |
|---|---|
| `penBegin`, `penSample`, `penEnd` | 单个笔点，字段见下；`operationId` 关联同一笔 |
| `hover`, `buttons`, `leave`, `outOfRange` | `action`、`inCsp`、`sample`（单个笔点）；悬停变化、侧键数组变化、离开 CSP、超出感应范围 |
| `penInterrupted` | `reason`；`relatedEventIds` 关联该笔已记录的硬件帧 |
| `tabletDeviceChanged` | `deviceId`、`name`、`vendor`、`vendorId/productId`、`width/height`（设备尺寸）、`maxX/maxY`（报告坐标范围）、`maxPressure`；设备发现接口提供的信息 |
| `driverConfiguration` | 用户选定后的配置快照：`snapshotId`、`createdAt`、`status`、`selectionReason`、`device`、`displays`、`selectedProfile`、`discoveredProfiles`、`pressureMappingStatus`、`coordinateMappingStatus`、`targetScreenArea`、`physicalToScreen`、`screenToPhysical`、`warnings` |

OTD 单个笔点字段：

- `deviceId`、`contactState`（Contact/Hover/OutOfRange）、`penButtons`、`heldKeys`、`reason`。
- `tabletX/tabletY`：原始报告坐标；`pressure`：原始压力；`tiltX/tiltY`：报告倾斜。
- `normalizedPressure`、`mappedPressure`：归一化压力和按所选驱动曲线映射的压力。
- `physicalX/physicalY`：驱动物理坐标；`screenX/screenY`：驱动映射后的屏幕坐标，映射不可用时回退到 Windows 光标。
- `x/y`：实际 Windows 光标，用于 CSP 交互；`interactionCoordinateSource=windowsCursor`；`screenCoordinateSource=driverMapping/windowsCursor`。
- `driverSnapshotId`、`pressureMappingStatus`、`coordinateMappingStatus`：对应配置和可用性。字段不可得时为 null 或状态码，不填假数据。

`physicalToScreen/screenToPhysical` 包含 `m0..m5` 及 `matrix`（行优先 3×3，对列向量 `(x,y,1)` 操作）。单位、压力范围和驱动配置约定见 [DriverReader.Core](../../../recognizer_core/driver_reader/README.md)。悬停连续变化最多约 30Hz，接触笔点沿用原采样/去重规则，不额外降到 30Hz。侧键变化不另行生成笔划。

`--passive-pen` 使用 Windows 笔兼容事件，只有 `x/y`、`heldKeys`、`source/deviceId` 或抬笔原因等字段；压力可以为 null，不提供 OTD 侧键、悬停、倾斜和物理映射数据。

## 各解析核心的状态更新接口

以下每个频道独立使用 `kind=stateUpdated`。一个核心完成便发布，无需等同一状态包的其他核心。截图面板的每次有效激活都取证，各自静默 150ms 后补拍并解析最新证据；连续激活仅延后解析，不取消截图。画布与导航器共用一个计时单元，Clip 仍使用独立保存解析路径。

| `channel` / `module` | `data.state` 的信息 |
|---|---|
| `core.brushState` / `brushState` | `name`（笔刷名）、`properties[]`，每项含 `key/value/type/unit/enabled/status` 中该属性实际提供的字段；包括笔刷大小、透明度、硬度等被面板识别到的属性 |
| `core.subtoolState` / `subtoolState` | `entries[]` 和 `groups[]` 的可见名称、候选节点 ID、图片选择状态；位置和 OCR 置信度单独在 `ocrEntries/groupEntries/rawResult` 中提供 |
| `core.currentLayerState` / `currentLayerState` | 当前唯一选中图层的名称字符串；识别不到时为 null，状态为 unknown |
| `core.colorState` / `colorState` | `kind`（色彩/透明等类型）、`rgb`、`hex` |
| `core.canvasViewState` / `canvasViewState` | `canvasOriginScreenPx`、`ocrScalePercent`、`ocrRotationDegrees`；`transform` 内含 `canvasPixelWidth/Height`、`scaleReference`、`cumulativeRelativeScale`、`rotationDegrees`、`scaleGeometryEstimate`；`rawResult` 另保留屏幕 ROI、视口边界、完整变换快照、置信度和失败阶段 |
| `core.clipState` / `clipState` | `.clip` 的 `canvas`（`width_raw/height_raw/unit_raw/resolution/current_layer_id`）、`layer_count`、`unlinked_layer_count`、`layers[]`。图层含 id/parent_id/depth/name、透明度、可见性/有效可见性、混合模式、剪贴/锁定/文件夹/蒙版、偏移、选择和 UUID 等可读取元数据 |

每条核心消息 `data` 的公共字段：

| 字段 | 信息 |
|---|---|
| `module`, `packageId`, `initial` | 核心名、关联状态包 ID、是否初始化结果；独立保存后的 Clip 解析可以没有 packageId |
| `status` | `changed`、`unchanged`、`unknown`、`ambiguous`、`error` |
| `state` | 用于状态比较的值，剔除时间、OCR 置信度等诊断差异；不确定或错误时可能为 null |
| `changedFields` | changed 时返回发生变化的顶层 JSON Pointer，如 `/rgb`、`/properties`；首次确认或整体字符串变化为 `/`，其他状态为空数组 |
| `lastConfirmedState` | 最近一次 changed/unchanged 确认的值；unknown/ambiguous/error 不覆盖此值；此前未确认过为 null |
| `rawResult`, `observedState` | 完整核心原始输出；未能确认的部分观察还可在 observedState 中查看 |
| `evidence` | 核心实际提供的截图 ID、采样/触发/完成 ticks、相关硬件 ID、`causalAmbiguous`、`observedAfterEventId`；Clip 还可有 saveId、observedTicks 等 |
| `error` | 失败信息，正常情况为 null |

笔刷频道额外提供 `data.valueRegions[]`，只返回已识别属性值的位置，`category` 为 `number`（数字）、`icon`（复选框/图标/图案）或 `text`（当前文字选项）。区域含 `propertyKey/propertyIndex/value/status/source/bbox/coordinateSpace`，OCR 区域可含 `score`；`bbox` 为面板局部 `[x,y,width,height]`，同帧截图原点可用时附 `screenBbox`（屏幕像素）。`evidence.panelRoi` 与 `captureId` 给出同帧采集范围和身份。位置变化不会单独触发属性值 `changed`，即使 `unchanged` 也应读取本次区域。失败更新区域为空，快照保留其原观察帧的位置。

笔刷 `rawResult.schema_version` 升级为 4：`value_regions` 及每个属性的 `value_category/value_regions/value_location_status` 记录分类和定位结果；旧的 `raw_ocr` 和属性 `evidence[].bbox` 只含值区域，名称与标题诊断不再返回框。无法拆分的属性名+值 OCR 混合框不导出，值可以保留并将定位标为 `unresolved`。命名管道外层版本保持 1。完整字段说明见 [笔刷位置接口](../../../../Memoline_demo_csponly/INTERFACES.md#笔刷属性值的位置接口)。

OCR 值区域另含实际值文字 `text`，可读单位通过 `unit` 保留；文字选项的语义值可能是序号，`text` 仍保留面板上的当前选项文字。即时取证通知的 `evidence.roi` 标记为 `roiRole: "panelCapture"`，表示截图范围，OCR 值位置应读取解析完成的 `valueRegions`。

截图核心的最终证据新增 `analysisStartedTicks`（核心开始执行时间）、`analysisQuietMs`（默认 150）、`analysisToken`（该面板的激活版本）和 `finalEvidence`。未单独解析的中间包标记 `unknown`，`evidence.reason=supersededBeforeAnalysis`；对应图片仍以 `screenshotBlob.statePackageId` 保留在文件中。执行期间再次激活的旧结果只保留为带 `superseded=true` 的原始 `stateResult` 诊断，不覆盖频道的最新已确认状态。Recognizer 将这些诊断和失败状态保存到会话同名的 `*.diagnostics.jsonl`，memoline 只保存成功状态。失败更新仍实时发布，`appendId=0`；从 memoline 恢复历史时仅可恢复成功状态。

`changed` 表示已确认有变化；`unchanged` 表示解析成功且值相同；`unknown` 表示识别不出完整状态；`ambiguous` 表示结果本身仍有歧义；`error` 表示解析失败。截图取证期间有后续输入只记入 evidence 的时序诊断，解析成功的观测值仍更新已确认状态。部分字段不全的笔刷不会被当成完整已确认状态。Clip 解析的是保存文件的稳定副本，不代表尚未保存到文件的屏幕改动，`saveCompletionConfirmed=false` 保留该限制。

示例核心消息（序号和时钟为示意）：

```json
{"schemaVersion":1,"sessionId":"session1","sequence":12,"channel":"core.colorState","kind":"stateUpdated","isSnapshot":false,"appendId":30,"eventId":0,"ticks":100,"appendedTicks":180,"publishedTicks":185,"operationId":null,"relatedEventIds":[5],"deviceSource":null,"data":{"module":"colorState","packageId":"package1","initial":false,"status":"changed","state":{"kind":"color","rgb":[255,0,0],"hex":"#FF0000"},"changedFields":["/hex","/rgb"],"lastConfirmedState":{"kind":"color","rgb":[255,0,0],"hex":"#FF0000"},"rawResult":{"kind":"color","rgb":[255,0,0],"hex":"#FF0000","confidence":0.9},"evidence":{"relatedEventIds":[5],"capturedTicks":100,"completedTicks":175},"error":null}}
```

## 系统消息与断线处理

所有管道订阅先返回 `channel=system, kind=hello`：所选频道、`frequency`、文件路径、管道名、`delivery=inMemoryBeforeDurableFlush`、`scope=CSP`。进程内 hub 不发送 hello。

`hello.data.evidenceCapturedNotifications=true` 表示支持存证即时消息。同一 `core.*` 频道新增 `kind=evidenceCaptured`：ROI 原始像素采集完成时发布，早于 PNG 编码、文件存证和核心解析，后续的 `kind=stateUpdated` 仍单独发布。数据包含 `module/packageId/initial` 和 `evidence.triggerTicks/capturedTicks/captureEndTicks/captureId/relatedEventIds/triggerReason/roi/analysisToken/finalEvidence/encodingPending/analysisPending`。`triggerTicks` 是触发这次证据的原始 Recorder Stopwatch 时间，`captureId` 与后续 screenshotBlob、解析结果关联。该消息不包含识别后的 state，也不声称状态确已变化；它不替换最后确认状态，不作为连接时的快照重放。`coreEvidenceCaptured` 原始记录在精简存储模式下留在诊断日志，实时消息仍正常发送。

`evidence.analysisQuietMs` 表示解析静默窗口。画布移动／松开时立即取证并发出通知，静止 150ms 后解析已有的最新证据；再次移动会替换 `analysisToken` 与候选证据，重新计时。画布即时证据的 `finalEvidence=true` 表示具备静默后解析资格，不能据此判断已经解析或静默结束；其他面板仍保留中间存证与静默补拍。旧版本可能不提供 `analysisQuietMs`。

正常关闭发出 `system/sessionEnded`，带 `filePath` 和 `lastSequence`。慢客户端超过 2048 条队列后停止接收，已排队消息读完后会收到 `system/streamGap`，包含 `reason=clientTooSlow`、`lastDeliveredSequence`、触发溢出时的 `lastAvailableSequence`、`liveFilePath`，随后关闭。C# 客户端会抛 `RecorderStreamGapException`；恢复历史使用消息的 appendId 和 [MemolineReader 增量读取接口](../README.md)。它不会悄悄丢掉事件继续伪装成完整流。

异常关闭、连接取消或进程崩溃可能没有 sessionEnded；C# 客户端在提前 EOF 时抛 `EndOfStreamException`。取消某一个订阅不停止录制或其他客户端。需要落盘确认或断线补读时，以 `.memoline/.part` 的完整帧为准。

## 轻量设备元数据订阅

`tablet.metadata` 是订阅过滤器，只发送 `tablet` 频道的 `driverConfiguration` 和 `tabletDeviceChanged`，含初始化快照及后续更新。消息的 `channel` 仍为 `tablet`；不会传送高频笔点和悬停事件。可与 `core.canvasViewState` 一起订阅。`hello.data.originTicks` 给出当前录制的 Stopwatch 原点，客户端不必等待文件刷盘来初始化录制时钟。

核心最新消息未确认时，订阅快照会按序先发送最近完整确认结果（含其 rawResult），再发送最新未确认消息，两者都带 isSnapshot=true。核心最新消息已确认时只发送一次，不重复。
