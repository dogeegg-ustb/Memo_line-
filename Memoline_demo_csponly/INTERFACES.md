# 本机快捷键、图层、笔刷与子工具接口

`Memoline.exe` 提供 `requestStates` 主动请求接口、快捷键查询命令，以及 `shortcuts`、`layers`、`layerstage` 和 `core.brushState` 等实时订阅频道。两个图层核心各有独立接口：`layerstage` 表示当前所处图层，`layers` 表示文件解析出的图层结构。笔刷频道提供按数字、图标、文字分类的属性值区域。命名管道使用 UTF-8 JSONL，订阅接口版本为 `schemaVersion: 1`。只允许同一 Windows 用户连接。

## 主动状态请求接口

录制初始化后，可连接同一 Windows 用户的 `memoline-recorder-input-<processId>` 双向命名管道。`processId` 和 `sessionId` 从本次 `.memoline.live.json` 获取。使用 UTF-8 JSONL，每次写入并刷新一行请求，读取一行直接结果：

```json
{"command":"requestStates","sessionId":"<当前会话>","requestId":"<唯一请求编号>","modules":["brushState","subtoolState","currentLayerState","canvasViewState"]}
```

支持 `brushState`、`subtoolState`、`currentLayerState`、`colorState`、`canvasViewState`、`clipState`、`shortcuts`、`initializationConfiguration`，一次请求 1–8 个不重复模块。请求不需要键鼠触发，不返回缓存快照：图像模块主动截取当前面板并解析，`clipState` 重新解析当前配置文档的稳定副本，`shortcuts` 重新读取本机已保存配置；`initializationConfiguration` 返回当前会话的校准信息。面板采集要求 CSP 在前台、面板可见且布局仍与初始化一致。

结果为：

```json
{"success":true,"requestId":"<同一编号>","sessionId":"<同一会话>","requestedTicks":123,"results":{"brushState":{"module":"brushState","status":"unchanged","state":{},"evidence":{},"valueRegions":[]}}}
```

`results` 的键与请求模块对应；图像和文档模块保留与实时接口相同的完整解析字段、位置和采集证据。`success` 表示请求已取得各模块结果，各模块的 `status` 仍可能是 `unknown/error`，不能使用旧 `lastConfirmedState` 补造当前位置。空子工具 OCR 会返回本次采集的面板 ROI 和身份，可用于继续滚动查找。每次请求有独立状态包，其他输入的解析结果不能完成这个请求。

请求 `brushState` 时，若已校准的子工具面板可见，会在同一采集批次附带子工具截图。工具属性面板名称先与本机 `toolCatalog` 的子工具名称匹配；名称无法匹配时，使用附带截图中唯一高亮的具体子工具行，并结合大类／组排除同名候选。大类、组标题不作为子工具行；没有唯一高亮行或目录仍有歧义时保留未知状态。`rawResult.brush.name_resolution` 记录 `source`（`tool_properties/selected_subtool/unresolved`）、`rejectedNames`、选中节点和 `companionEvidence`（采集 ID、截图 ID、时间、ROI）或失败 `reason`。保存配置中的选中项不充当实时兜底。

属性库版本 `2026-10-06` 有 158 个公开属性定义：补齐本机 CSP 5.0 译名、属性含义、状态说明和配置字段关联。颜色变化新增前端／每笔触各四项数字子属性；重复的“色相”等标签必须有同帧可见的父标题才能绑定。`highlight_index` 从 1 开始，不能与 CSP 私有枚举代码混用；硬度五格控件按累积填色记录，不作为可输入数字。抗锯齿允许用已识别选项定位唯一高亮单元格，避免漏读“中”后误判。`disabled` 与数值零、属性缺失不同。回放仍只修改独立数字区域。

先通过 `requestClipSave` 保存后，查询 `clipState` 可附加 `saveId`，使文档结果保留该保存身份。`saveId` 只允许 1–128 个 ASCII 字母、数字、下划线或连字符；请求本身不额外派发保存。图层结构仍代表已保存的文件，当前图层用 `currentLayerState` 主动采集。请求最长解析时间为 30 秒，客户端应允许略长的读取超时；取消、会话结束、隐藏面板或采集失败会显式失败或返回模块错误。旧版本不支持 `requestStates`，需要重新启动此次构建的 Memoline 并开始新录制。

一次请求完成后可以断开，后续请求可以重新连接；现有订阅接口继续兼容。

## 快捷键配置文件接口

在本目录运行，无需开始录制：

```powershell
.\Memoline.exe --shortcuts | Out-Host
.\Memoline.exe --shortcuts --config-dir "C:\路径\CLIPStudioPaintVer1_5_0" | Out-Host
```

返回 JSON 对象：

| 字段 | 内容 |
|---|---|
| `configRoot` | 本次读取的 CSP 用户配置目录 |
| `sourceFiles` | `Shortcut/default.khc`、`Tool/EditImageTool.todb`、`Shortcut/DefaultToolModifyKey.tomd` 的路径 |
| `bindings` | 快捷键与功能对照表，一条已保存绑定对应一行 |
| `gestures`、`wheelBindings` | 现有解析器识别出的修饰键操作及滚轮配置，含上下文与来源 |
| `warnings` | 缺失文件、解析失败、无法匹配键码等诊断信息 |
| `source` | `savedCspConfiguration` |
| `toolCatalog` | CSP 当前保存的完整工具、子工具组、子工具目录与节点身份 |
| `brushPackages` | 按已安装子工具组组织的笔刷/工具库，包含组内具体子工具 |
| `brushPackageSource` | `installedCspToolGroups` |

`bindings` 每行保留 `id`、`shortcut`、`action_name`、`scope`、`sourceFile`、`details`、`targets`；菜单命令另含 `command`、`command_type`，工具绑定可含 `pointerTargets`。同一功能绑定多个快捷键，或同一个键绑定多个工具，都保留全部记录。无法翻译的命令以 CSP 原始命令标识作为功能名称。

```json
{
  "id": "menu_83",
  "shortcut": "Ctrl + G",
  "action_name": "重做",
  "command": "redo",
  "command_type": "basiccommand",
  "scope": "菜单命令",
  "sourceFile": "C:\\路径\\Shortcut\\default.khc"
}
```

复用 `CSP_Shortcut_Manager/csp_shortcuts.py` 和 `recorder_integration/catalog.py`，数据库只读打开并及时关闭。每次查询重新读取文件。默认先使用 `MEMOLINE_CSP_USER_DIR`，未设置时依次搜索：

1. `%APPDATA%/CELSYSUserData/CELSYS/CLIPStudioPaintVer1_5_0`
2. `%APPDATA%/CELSYS/CLIPStudioPaintVer1_5_0`
3. 当前用户的 `Documents/CELSYS/CLIPStudioPaintVer1_5_0`

`--config-dir` 显式指定目录。目录无法定位时返回非零退出码并向 stderr 写错误；部分配置文件缺失时通过 `warnings` 说明。只报告实际保存的绑定，不补造默认快捷键。

录制期间可订阅同一份配置及后续重新加载结果：

```powershell
.\Memoline.exe --subscribe shortcuts --endpoint "publish\win-x64\Recognizer\procedure\stroke\<本次会话>.memoline.live.json" | Out-Host
```

消息为 `channel: "shortcuts"`、`kind: "configurationUpdated"`，`data` 结构与上述查询结果相同。连接时回放最新已加载配置，之后随 Recognizer 的配置重载推送。每次文件变更是否已重载，以收到的消息为准；一次性 `--shortcuts` 始终直接读取当前文件。

## 两个图层核心的独立接口

先在图形界面开始录制并完成初始化，再订阅对应会话：

```powershell
.\Memoline.exe --subscribe layerstage --endpoint "publish\win-x64\Recognizer\procedure\stroke\<本次会话>.memoline.live.json" | Out-Host
.\Memoline.exe --subscribe layers --endpoint "publish\win-x64\Recognizer\procedure\stroke\<本次会话>.memoline.live.json" | Out-Host
# 也可同时订阅全部三个接口，或直接指定管道名。
.\Memoline.exe --subscribe shortcuts,layers,layerstage --pipe "MemoLine.Recognizer.<sessionId>" | Out-Host
```

`<本次会话>`、`<sessionId>` 使用本次正在录制的实际值。描述文件内的 `pipeName`、`sessionId`、`processId` 和 `channels` 可供其他程序发现接口；历史描述文件保留，但录制结束后管道关闭。

两个图层核心的频道、缓存和订阅互相独立：

| `channel` | 对应核心 | `kind` | `data.state` |
|---|---|---|---|
| `layerstage` | 图层状态核心 `currentLayerState` | `currentLayerUpdated` | 当前唯一选中图层的名称字符串；无法确认时可能为 null |
| `layers` | 图层结构核心 `clipState` | `layerStructureUpdated` | 最新解析出的完整文档结构对象，包含 `canvas`、`layer_count`、`unlinked_layer_count`、`layers[]` |

`layerstage` 的 `data.module` 为 `currentLayerState`，只推送当前图层识别结果。`layers` 的 `data.module` 为 `clipState`，只推送基于 `.clip` 文件解析的结构结果。当前图层切换不会替换结构表缓存，结构解析也不会作为当前图层状态消息推送。

结构表中每个图层保留 `id`、`parent_id`、`depth`、`name`、`uuid`、可见性、有效可见性、不透明度、混合模式、剪贴、锁定、文件夹、蒙版、偏移、选择等现有解析字段。顶层 `parent_id` 为 null；父子关系和顺序沿用解析器结果。

`layers` 中的 `data.state.canvas.current_layer_id` 是该次保存的 `.clip` 文档中记录的当前图层 ID。`layerstage/currentLayerUpdated` 来自当前图层识别核心，可独立报告图层切换；它和文件结构表的观察时间可能不同。图层重名时不要仅凭名称推断唯一 ID。

`layers` 随原有初始化、保存及解析流程更新，表示**最新完成解析的文档结构**。未保存、尚未解析的变动不会提前写入结构表。

所有图层消息还保留：

| 字段 | 含义 |
|---|---|
| `data.status` | `changed`、`unchanged`、`unknown`、`ambiguous` 或 `error` |
| `data.lastConfirmedState` | 对应模块上一次成功确认的结果；尚无成功结果时为 null |
| `data.changedFields` | 成功变更的 JSON 路径；失败尝试不声称结构已变化 |
| `data.error`、`data.evidence`、`data.rawResult` | 解析诊断、触发/保存依据及原始结果 |
| `isSnapshot` | true 为连接时回放，false 为实时更新 |
| `sequence`、`ticks`、`appendId`、`sessionId` | 发布顺序、观察时间和原生记录身份 |

每个接口默认回放其对应核心最新的消息；若最新尝试失败，还会先回放该核心最后成功消息。消费者应根据 `status` 区分当前确认结果与保留的旧结果。尚未解析时没有凭空构造的空表；成功解析得到的空表可以正常替换旧结构。新录制会话不会继承旧会话缓存。

## 配置中的笔刷包和子工具层级

配置查询和 `shortcuts/configurationUpdated` 都提供同一份完整目录。`brushPackages` 按 CSP **已安装的子工具组**组织，例如“沾水筆 → 沾水筆组 → G筆”和“沾水筆 → 麥克筆组 → 簽字筆”。其他工具组也保留，目录包含没有单独快捷键的子工具。

`toolCatalog` 为 `schemaVersion: 1`，含 `status`（`ok/partial/unavailable`）、`sourceFile`、`roots`、`nodes[]`、`savedCurrentNodeIds`、`selectionSource: "savedCspConfiguration"` 和 `warnings`。每个节点包含：

| 字段 | 含义 |
|---|---|
| `id`、`nodeId`、`uuid` | 配置节点身份；`id` 如 `tool_24`，UUID 来自 CSP 数据库 |
| `name`、`kind` | 保存的实际名称；`kind` 为 `root/tool/group/subtool` |
| `parentId`、`children[]` | UUID 链解析出的父子关系；子节点顺序沿用配置 |
| `toolId`、`groupId` | 所属工具大类、最近的子工具组；无法确认时为 null |
| `path`、`pathIds` | 工具 → 组 → 子工具的名称与节点 ID 路径 |
| `shortcut` | 该节点自己保存的快捷键；未分配为 null，不给子工具补造继承快捷键 |
| `savedSelectedChildId` | 配置中保存的直接选中子节点，不代表实时屏幕选择 |
| `hidden`、`defaultIdentifier` | 保存的隐藏标记与 CSP 内部默认类别标识 |
| `material` | 可读的素材 `contentId/uuid/hasMaterialId`；缺失标识保留 null |

`brushPackages[]` 含组的 `id/name/toolId/path/subtools[]/savedSelectedSubtoolId`。没有单独组节点但直接含子工具的大类，以该工具节点组织一项。子工具列表保留完整身份和路径，重名条目不会合并。

每条工具快捷键 `bindings[]` 另有：

- `tool`：实际绑定节点的上述完整信息，可能是大类、组或具体子工具。
- `subtools[]`：该绑定节点下可到达的全部具体子工具，跨多个子工具组；直接绑定子工具时只包含自身。
- `savedSelectedSubtoolId`：沿保存的选中链得到的具体子工具；无法确认时为 null。

例如本机 `P → 沾水筆` 的 `tool.id` 为 `tool_22`，G筆路径是 `['沾水筆','沾水筆','G筆']`，节点为 `tool_24`；同一大类还包含“麥克筆”组。`P` 另有“鉛筆”的独立绑定，应保留两条记录。快捷键切换后实际选择由笔刷状态核心和本帧 OCR 确认，不能把保存的选中项当成当前观察。

数据库以只读方式加载，循环、缺失节点等通过 `toolCatalog.warnings` 和顶层 `warnings` 报告，缺失目录不生成默认笔刷库。一次性查询重新读取配置；录制中的目录随原有配置重载更新。

## 子工具面板 OCR 接口：`subtools`

先开始录制，使用本次实际会话订阅：

```powershell
.\Memoline.exe --subscribe subtools --pipe "MemoLine.Recognizer.<sessionId>" | Out-Host
.\Memoline.exe --subscribe shortcuts,subtools,core.brushState --endpoint "publish\win-x64\Recognizer\procedure\stroke\<本次会话>.memoline.live.json" | Out-Host
```

消息为 `channel: "subtools"`、`kind: "ocrUpdated"`、`data.module: "subtoolState"`。它是新增核心 `core.subtoolState/stateUpdated` 的独立投影，使用自己的订阅和最新结果缓存，同时保留原生身份与时间。C# 可使用 `RecorderRealtimeClient.FollowSubtoolsAsync(pipeName, token)` 或进程内 `hub.SubscribeSubtools()`。

`data.ocrEntries[]` 返回**当前截图里可见的具体子工具名称**，名称与配置目录匹配；面板标题、按钮和“添加下载的素材”不作为子工具位置输出。同帧可识别的、位于列表上方的子工具组名称另在 `data.groupEntries[]` 返回，字段相同、`kind: "group"`，便于结合目录定位组。匹配到的大类名称在 `data.toolEntries[]` 中另行提供，`kind: "tool"`。

| 区域字段 | 含义 |
|---|---|
| `kind` | `subtool` 或 `group` |
| `text`、`name` | 实际 OCR 文字、匹配的配置名称；常见简繁字形与空白差异归一后匹配 |
| `bbox`、`coordinateSpace` | 面板局部 `[x,y,width,height]`、`"panel"`；拆开的名称词段仅在完整名称匹配时合并 |
| `screenBbox` | 同帧 Windows 屏幕像素 `[x,y,width,height]`；支持负坐标 |
| `score` | OCR 置信度，可为 null |
| `selectionState`、`selectionScore` | 当前图片背景启发式的 `selected/unknown` 和分数；缺少证据保持 unknown |
| `matchStatus`、`matches[]` | `unique/ambiguous`；全部候选的 `id/uuid/name/toolId/groupId/path/pathIds` |

重名笔刷可能匹配多个节点，`matches` 会保留全部候选。配置中的保存选中项不用于伪造 `selectionState`。当前笔刷名称仍通过 `core.brushState.data.state.name` 获取。

```json
{
  "kind": "subtool",
  "text": "G筆",
  "name": "G筆",
  "bbox": [292, 39, 36, 26],
  "screenBbox": [592, 539, 36, 26],
  "coordinateSpace": "panel",
  "selectionState": "unknown",
  "matchStatus": "unique",
  "matches": [{"id":"tool_24","toolId":"tool_22","groupId":"tool_23","path":["沾水筆","沾水筆","G筆"]}]
}
```

`data.evidence.panelRoi` 是本次子工具面板截图的屏幕范围。坐标换算为 `screenBbox = [panelRoi[0]+bbox[0], panelRoi[1]+bbox[1], bbox[2], bbox[3]]`，可直接用于同帧区域定位。`data.rawResult.entries/groupEntries` 保留核心输出的面板局部位置，未匹配文字只在 `unmatchedText` 中作为无位置诊断保留。

时序信息位于 `data.evidence`：`captureId`、`screenshotIds`、`triggerTicks`、`capturedTicks`（该面板实际采集起点）、`captureEndTicks`、`analysisStartedTicks`、`completedTicks`、`relatedEventIds`。ticks 使用该会话 Recorder 时钟，频率和原点来自 `system/hello`。接入端等待操作后刷新时，应核对新的截图身份及 `capturedTicks` 晚于目标输入的 ticks，不能仅凭消息完成时间或笔刷名是否变化判断。

录制初始化在子工具面板可见时采集；配置中的工具切换快捷键、工具栏/子工具面板点击，以及子工具面板滚动会分别触发取证并在静默后解析最新截图。每次解析完成都会更新 OCR 位置，即使可见名称和选择状态相同、`status: "unchanged"`。面板隐藏时不会凭空构造位置，也不会新增一个永远无法完成的初始化依赖。

连接时回放该核心的最新结果。若最新结果未知或失败，还会先回放最后成功结果；每条快照保留原帧时间和坐标。消费者应以 `status` 和采集身份区分旧确认结果与本次尝试。截图后未匹配/解析失败时 `ocrEntries/groupEntries` 可为空，但仍保留本次截图的 `panelRoi` 和时间；旧 `lastConfirmedState` 不产生新的位置。原生成功结果存入 Memoline，失败诊断沿用现有诊断日志路径。

## 笔刷属性值的位置接口

订阅已有频道 `core.brushState`，读取 `kind: "stateUpdated"` 的 `data.valueRegions[]`：

```powershell
.\Memoline.exe --subscribe core.brushState --pipe "MemoLine.Recognizer.<sessionId>" | Out-Host
```

位置只属于当前观察到的**属性值**，不包含属性名称、面板标题、笔刷名称或未选中的其他选项。`category` 按值在面板上的实际表现分三类：

| `category` | 含义 | 示例 |
|---|---|---|
| `number` | 数字值 | 笔刷尺寸 5.4、透明度 100 |
| `icon` | 图像控件的值 | 复选框的勾选状态、选中的图标单元格、选中图案 |
| `text` | 文字值 | 混合模式“正常”、消除锯齿选项“弱” |

`category` 与旧的解析 `type` 独立：文字选项可保留 `type: "highlight_index"`、`value: 2`，其位置分类仍是 `text`；图标选项的序号也是 `icon`。原有属性键、数值和单位继续使用。

每个区域提供 `propertyKey`、`propertyIndex`（对应本帧 `rawResult.brush.properties[]`）、`category`、`value`、`status`、`source`（`ocr` 或 `image`）、`bbox`、`coordinateSpace: "panel"`；OCR 值另含实际值文字 `text`，可含 `score`，单位可读时附 `unit`。`bbox` 是相对本帧笔刷面板截图的 `[x,y,width,height]`。本帧截图原点可用时另提供 `screenBbox`，同样为 `[x,y,width,height]`，使用 Windows 实际屏幕像素，支持负坐标。

```json
{
  "propertyKey": "brush_size",
  "propertyIndex": 0,
  "category": "number",
  "value": 5.4,
  "bbox": [251, 103, 51, 26],
  "screenBbox": [551, 603, 51, 26],
  "coordinateSpace": "panel",
  "source": "ocr",
  "status": "ok",
  "score": 0.99876
}
```

`data.evidence.panelRoi` 保存同一次解析截图的屏幕面板范围，`captureId` 关联该帧；直接使用 `screenBbox` 不需要等待或缓存之前的即时取证通知。值相同但位置改变时，`status` 仍可为 `unchanged`，`valueRegions` 会随本次解析更新。连接时的快照保留其对应帧的区域；它不是对屏幕的实时重新测量。

坐标换算为 `screenBbox = [panelRoi[0] + bbox[0], panelRoi[1] + bbox[1], bbox[2], bbox[3]]`。`text` 保留实际 OCR 值文字，例如文字选项的语义 `value` 为序号 2 时，`text` 可为“弱”；图标类没有伪造的 OCR 文字。仅连接回放时也能使用该消息自带的 `screenBbox`，无需寻找更早的截图通知。

笔刷核心输出升级为 `data.rawResult.schema_version: 4`（命名管道外层仍为 `schemaVersion: 1`）：

- `rawResult.value_regions[]` 是同一份值区域，字段使用 Python 风格 `property_key/property_index/coordinate_space/roi_id`；区域里的 `bbox` 是面板局部坐标。
- `rawResult.brush.properties[]` 增加 `value_category`、`value_regions` 和 `value_location_status`（`located` 或 `unresolved`）。属性的 `evidence[].bbox` 只保留值的位置。
- 兼容字段 `rawResult.raw_ocr[]` 只保留已绑定属性值的数字和当前文字选项，不再是完整面板 OCR；图标位置使用 `value_regions`。笔刷名称、候选、未匹配文字诊断中不再返回位置框。

OCR 将属性名和值合并为一个框且无法独立定位时，保留解析出的值并标记 `value_location_status: "unresolved"`，不返回这个混合框。识别失败返回空 `valueRegions`；部分识别只返回本帧已可靠定位的值。`lastConfirmedState` 保留旧值时不能据此补造当前的位置。

`kind: "evidenceCaptured"` 中的 `evidence.roi` 是整块面板的**截图范围**，`roiRole: "panelCapture"`；它不属于 OCR 值位置，通知发布时解析尚未完成。原始截图存证仍保留完整面板。

## 程序接入

C# 引用 `Memoline.Core.csproj` 或 `Memoline.Core.dll`，命名空间为 `BehaviorRecognizer.Realtime`：

```csharp
// 多频道订阅；includeSnapshot=false 可只接收连接后的更新。
await foreach (var message in RecorderRealtimeClient.SubscribeAsync(pipeName,
    ["shortcuts", "layers", "layerstage", "subtools"], includeSnapshot: true, cancellationToken: token))
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(message));

// 单频道也可使用以下独立入口：
// RecorderRealtimeClient.FollowShortcutsAsync(pipeName, token)
// RecorderRealtimeClient.FollowLayersAsync(pipeName, token)
// RecorderRealtimeClient.FollowLayerStageAsync(pipeName, token)
// RecorderRealtimeClient.FollowSubtoolsAsync(pipeName, token)
```

其他语言打开 `\\.\pipe\MemoLine.Recognizer.<sessionId>`，使用双向字节管道，在连接后 5 秒内发送并刷新一行：

```json
{"schemaVersion":1,"channels":["shortcuts","layers","layerstage"],"includeSnapshot":true}
```

随后逐行读取 JSON。会先收到 `system/hello`，正常结束收到 `system/sessionEnded`。订阅取消不会停止录制。慢客户端溢出会得到显式 `streamGap`，不能将缺口视为完整历史。

`layers` 是已有 `core.clipState` 的专用投影，`layerstage` 是已有 `core.currentLayerState` 的专用投影；各自保留对应核心的原生身份和解析数据，实时发布序号不同。`cores` 为原有 6 个核心频道；`all` 现在包含 13 个频道，会同时包含原始核心消息和专用投影。详细公共协议见 `recognizer/core/memoline/Realtime/README.md`。
