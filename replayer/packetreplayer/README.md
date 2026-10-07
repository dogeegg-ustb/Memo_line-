# PacketReplay

Windows x64 软件，在 StrokeReplay 的机械笔画解析、画布恢复和 PT_PEN 注入基础上，回放 **一个选定的 Memoline 聚集事件包**。共享源码直接链接到现有 StrokeReplay，并复用 Memoline 的 `BundleArchive` 验证器和 `Memoline.Core` 实时客户端，不启动额外 OCR 或录制进程。

## 使用

1. 在 CSP 打开对应尺寸的目标文档，准备好包开始时的图层内容和颜色。保持更新后的 Memoline/Recognizer 会话运行并完成初始化。历史文件对应的旧会话不能提供当前实时状态。
2. 双击 `release/win-x64/PacketReplay.exe`，打开或拖入集成 `.memoline` 文件。
3. 选择一个聚集包。包按 `fromTicks/toTicks` 排序编号，序号从 1 开始；预览显示笔画、笔刷属性、图层和视图事件。
4. 接口留空会自动发现正在运行的 Recognizer；也可填写当前 `.memoline.live.json` 或订阅管道名以定位会话。程序通过 Memoline `--shortcuts` 查询本机配置；开始复现时向同会话控制管道 `requestStates` 一次性主动索取当前配置、校准、笔刷、图层、画布及需要的颜色，建立当前状态表。可显式指定 Memoline.exe 和 CSP 配置目录。
5. 点击“从头回放选中包”连续回放；“下一笔”依次执行本笔之前的状态事件，再画一笔。**首次开始复现会通过 Memoline 自动保存当前 CSP 文档**，然后主动请求本次保存对应的图层结构和当前图层。连续点击“下一笔”沿用同次复现；换包、重新连接、停止后重试、录制会话变化或从头回放会重新保存。最后一笔后再次点击可执行剩余状态事件。支持集中/离散模式、0.1–10 倍速，以及离散模式最多 200 ms 的笔间等待。
6. 点击“停止”或关闭窗口会取消等待并释放笔和按键。中途取消后，本笔未记为完成；重试可能重画已经注入的片段，请先在 CSP 整理目标内容。程序不清空或创建目标文档，也不自动恢复包之前的像素历史。

## 接口与回放规则

- 支持集成容器 `memoline-csponly/v1` 和 `v2`，包括 Brotli entry。加载时验证 decoded SHA256、固定包哈希、原生 CRC、会话和所有事件指针。不接受裸机械文件作为聚集包文件。
- 使用包顶层 `eventPointers` 中的非 `context` 指针选择原生事件，不从脏矩阵和图像重复推导笔画。没有差异图的包仍可能包含可回放笔画。跨包接触仅绘制本包样本，片段末尾抬笔，不补画其他包的起笔。
- 异步解析结果按 `statePackageReserved.afterEventId` 和 `reservationOrder` 归位，不按完成时间或文件追加顺序推断历史笔刷、图层和视图。
- **累积状态表**：从文件初始化记录建立历史状态表，按因果顺序应用后续模块更新。单个包的起始状态也继承之前包已发生的更新。“下一笔”的游标保留先前事件产生的状态，不重复初始化。每笔将文件里的累积状态与已确认的当前状态在本地比较；连续同状态笔画不发出状态请求，也不发送恢复操作。
- **主动索取状态**：本次复现开始建立当前状态表，之后仅在确实切换快捷键、OCR 点击、滚轮、数字修改或画布恢复后，使用 `requestStates` 主动取得受影响模块的新采集结果，直接确认这次操作。校验请求编号、会话、模块及采集时间，不用操作前的缓存作为操作后的确认。颜色变化会独立核对；撤销/重做后主动刷新可能受影响的状态。采集与 OCR 耗时由本次 RPC 承担，最长 30 秒；模块未确认会明确停止。订阅承担会话发现、断线检测和回放期间的异常监测。
- 笔刷、图层、颜色及画布状态变更作为独立事件保留，即使后面没有绘画。仅对需要变化的模块执行恢复和确认；无变化的模块沿用当前状态表。支持已识别的撤销/重做，通过当前配置中对应的命令快捷键执行；任意其他编辑命令会标出未支持并阻止注入。无映射的原始键鼠输入不直接发送。
- **更换笔刷**：从 `shortcuts.toolCatalog`、`brushPackages` 和 `bindings[].tool/subtools` 找到目标子工具所属大类及组，发送大类快捷键；多个大类共用快捷键时有界循环。每次先读取 `core.brushState` 当前名称，已匹配则完成切换，否则使用新帧 `subtools.ocrEntries` 中目标的唯一 OCR 位置点击。不同组且组页签可定位时，先用 `groupEntries` 点击目标组。无需为 G笔等每个子工具单独绑定快捷键；所属大类需有可发送的配置绑定。
- **未显示的子工具**：移动光标到本次 `evidence.panelRoi` 内滚动，每次主动请求 `subtoolState` 的新采集与 OCR 结果。先向下搜索，遇到重复可见列表边界后向上搜索；搜索预算由目录条目数限定，取消和断线立即终止。空/未知 OCR 帧可继续滚动，但不沿用旧条目的点击位置。找到后点击并主动请求 `brushState` 确认目标名称，再调整数值。目标跨不可见组、面板隐藏、OCR 持续失败或同名身份无法唯一匹配时停止。
- **切换图层**：复现开始通过当前会话控制管道 `requestClipSave` 自动保存校准文档路径，随后主动请求 `clipState/currentLayerState`，要求 `evidence.saveId` 精确对应本次保存、`observedTicks` 晚于派发时间，再核对结构中的 `canvas.current_layer_id` 与当前图层。不会把其他保存的结构当成本次结果。`saveInputDispatched` 仅表示保存按键已派发；Memoline 未确认 CSP 落盘完成时，日志保留这个区别。名称在结构表内必须唯一。根据结构顺序优先选择配置中的 `layerselectupperlayer` / `layerselectlowerlayer` 快捷键，每次操作后主动请求当前图层；遇到边界可换方向。折叠文件夹里的目标可能需要先展开。稳定结构保留本次复现开始时主动取得的版本。
- **笔刷数字属性**：只处理历史 `type: "number"` 且正常启用的属性。当前位置来自本次主动请求的 `brushState.valueRegions` 中 `category: "number"` 的唯一值区域，直接使用 `screenBbox`，或用同消息 `evidence.panelRoi` 加 `bbox` 换算。沿用导航器的点击、Ctrl+A、数字输入、回车及数字区刷新流程，然后主动请求新的笔刷状态确认。
- **文字/图标属性不修改**：`highlight_index` 的数字只是选项序号，不当作数字编辑器。消除锯齿“弱／中／强”、复选框、图案和禁用/未知项会跳过，不从属性名框、历史 OCR 或整个面板估计点击位置。
- **画布**：使用原 StrokeReplay 的精确导航器数字位置恢复缩放/旋转，以空格拖动恢复原点；确认后回放 Windows 屏幕坐标、已归一化压力、倾斜和笔内采样时间。画布尺寸、前台 CSP、可见输入区域和实时连接均由原流程检查。
- **颜色**：初始化取得当前颜色；文件中的颜色变化作为独立事件更新状态表，需要时主动请求 `colorState` 核对。同颜色笔画沿用已确认值，不逐笔请求；不自动修改颜色面板，颜色不符时停止，需准备匹配颜色再重试。
- 笔刷、当前图层和画布确认使用本次主动请求的身份与 Stopwatch 采集时间屏障；排队中的旧截图、其他核心更新和 unknown/error 结果不能确认本次状态修改。子工具请求可返回新的空 OCR 帧，仅用于继续搜索。订阅快照不承担回放前或操作后的状态确认；断线或会话结束会清空笔刷、图层、子工具位置和保存控制身份，并停止正在回放的操作。
- 延续原 StrokeReplay 的导航、画布外操作和未确认历史视图跳过规则，界面与检查输出显示数量。已确认笔画若缺少历史笔刷/图层，或接触过程中发生对应状态切换，则阻止注入。

字段定义见仓库 `Memoline_demo_csponly/INTERFACES.md`。需要此次更新后的 Memoline `requestStates` 主动请求接口；请重新启动 Memoline 并开始新录制。旧文件可提供历史数字目标，实时点击位置来自当前会话的新请求结果。目标文档路径来自当前初始化配置，保存控制进程绑定同一录制的 `.live.json`，不使用历史录制内的文档路径。

## 2026-10-07：完整事件清单回放核心

`source/EventReplayRunner.cs` 提供可被宿主链接编译的内部 API。调用方准备完整笔接触和历史状态，接口为：

```csharp
ScheduledStroke(
    (string SessionId, ulong BeginAppendId) EditableKey,
    MemolineReplayStroke Stroke,
    HistoricalState State,
    BrushSnapshot? OverrideBrush = null)

EventReplaySchedule(
    long Frequency,
    IReadOnlyList<ScheduledStroke> Strokes,
    string WorkLayerName,
    string WorkLayerUuid)

Task<EventReplayResult> EventReplayRunner.RunAsync(
    EventReplaySchedule schedule, IReplayEnvironment env,
    double speed, ReplayOptions options,
    Action<string> status, CancellationToken token)
```

清单中的 `Stroke` 使用机械流解析得到的完整接触，包括跨包的全部采样。调用方按原生 penBegin eventId 排序；Runner 再检查严格递增、同一会话、事件键唯一、appendId 顺序及接触完整性。`EventReplayResult` 返回完成笔数和完成事件键。原聚集包界面的包内片段选择规则仍由 `PacketArchiveReader.Plan` 处理。

所有清单事件画到调用方已确认的工作层，不按历史图层名导航。调用方负责核实工作层 UUID、名称唯一性及事务身份；Runner 每笔开始和每个采样核对当前工作层名称，并检查实时笔刷、颜色、视图和输入位置。历史图层名保留在状态和日志说明中。

`OverrideBrush` 必须包含完整历史数值属性表，键集合保持一致且值有限。每笔分别恢复覆盖后的快照或原历史快照，避免上一笔的改值残留。需要输入的数值使用当前 OCR 唯一数字框，写入后请求状态读回。子工具切换沿用 `ShortcutConfiguration.RouteForBrush` 和现有反馈流程。集中模式每笔以 `Up` 结束；离散模式以 `UpAndLeave` 结束，间隔取当前笔的原始结束 ticks 和清单中下一笔的原始开始 ticks，并受 `StrokeGapSeconds` 上限约束。

`IReplayEnvironment` 提供 `Feed`、`Input`、`ViewFeed`、`ViewInput`、`CurrentView`、`CreateInjector`、`Idle`、`EnsureCanvasPoint` 和 `ReleaseInputs`。`ViewFeed` 用于主动请求的视图恢复反馈；`CurrentView` 用于采样时检查最新实时视图。`WindowsReplayEnvironment(LivePacketFeed, WindowsViewInput)` 适配现有 Windows 实现；检查项目使用 fake。`PacketReplayRunner.RunAsync` 保留原签名，执行部分委托给可注入的 `RunCoreAsync`。Runner 在结束、失败和取消时释放已持有的输入；笔引擎释放活动接触。

`WindowsViewInput` 的键盘、Unicode 数字、鼠标按钮、滚轮和虚拟桌面绝对光标移动均经带 `dwExtraInfo = 0x4D4C5243`（MLRC）的 `SendInput` 发送。识别器可以据此忽略这些合成键鼠输入。PT_PEN 注入使用单独的 Windows 指针 API，其录制隔离由调用方的编辑事务负责。

`ColorRestorer` 当前只做颜色验证：颜色不同则主动请求新的 `colorState`，仍不匹配时报 `ReplayStateException.Code == "ColorUnrestorable"`。滑块面板 OCR、主色输入位置及色环过时状态尚待核实，因此当前核心不会点击颜色面板或写入 RGB。工作层漂移、笔刷恢复失败和视图漂移分别以 `LayerIdentityUnconfirmed`、`BrushUnrestorable`、`ViewUnrestorable` 停止。

本节交付的是源码核心和检查。宿主事务接线、容器 v3、编辑 UI 与真实 CSP 验收的完成情况，应查阅宿主实施记录。

## 命令行

```powershell
.\release\win-x64\PacketReplay.exe inspect 'D:\notes\session.memoline'
.\release\win-x64\PacketReplay.exe inspect 'D:\notes\session.memoline' --packet 3
# 不加 --inject 只检查和预览
.\release\win-x64\PacketReplay.exe replay 'D:\notes\session.memoline' --packet 3 --speed 1 --mode discrete --inject
.\release\win-x64\PacketReplay.exe replay 'D:\notes\session.memoline' --packet 3 --endpoint '当前会话.memoline.live.json' --memoline 'Memoline.exe' --inject
.\release\win-x64\PacketReplay.exe shortcuts --memoline 'Memoline.exe'
```

界面设置保存在 `%APPDATA%/PacketReplay/settings.json`，与 StrokeReplay 分开。

## 构建和检查

需要 .NET 10 SDK；发布产物为自包含单文件，使用者不需要另装 .NET。

```powershell
.\Build.ps1
.\Build.ps1 -Sample 'D:\notes\session.memoline'
```

检查只使用模拟输入和临时本机命名管道，不向 CSP 注入操作。它验证数字/选项区别、坐标换算、因果顺序、包边界、工具大类与子工具组映射、OCR 点击与滚动查找、保存控制协议、保存结构身份、主动请求响应、无状态推送时启动、错误身份/旧采集拒绝、笔刷与图层反馈、断线和取消；也验证连续同状态多笔零请求、独立模块更新的请求次数、跨包累积状态和连续“下一笔”复用初始化。可用检查程序 `--render-ui <录制文件> <输出.png>` 离屏渲染界面。

当前验证结果和实际文件包统计见 `reports/20261006-validation.md`。构建和协议检查通过不等同于已经在 CSP 中实测绘画效果。

2026-10-07 完整 fake 检查通过 166 项断言，保留原 131 项，新增完整事件回放 27 项和共享剪贴板编码 8 项。新增检查包括跨包采样、覆盖值恢复、工作层和实时视图漂移、两种抬笔模式、间隔上限、取消释放、颜色拒绝及旧 Runner 的可注入执行。codec 检查包含 alpha=0 隐藏 RGB、半透明 PNG、非预乘 DIBV5 和 STA 要求，不写系统剪贴板。实际红绿输出见 `Memoline_demo_csponly/docs/architecture/IMPLEMENTATION_NOTES.md`。

```powershell
dotnet build checks/PacketReplayChecks.csproj -c Release --no-restore -m:1
dotnet checks/bin/Release/net10.0-windows/PacketReplayChecks.dll
# 仅运行新增切片检查
dotnet checks/bin/Release/net10.0-windows/PacketReplayChecks.dll --event-checks
dotnet checks/bin/Release/net10.0-windows/PacketReplayChecks.dll --codec-checks
```

完整套件的命名管道由测试自身创建，不连接真实 CSP。受限运行环境若拒绝命名管道访问，需在允许本机测试 IPC 的环境中运行；不能把该权限错误计为通过。

## 2026-10-06 的笔刷确认兼容

与本次构建的 Memoline 配合使用：属性面板名称无法匹配目录时，Memoline 从同次采集的高亮子工具行兜底，排除大类和组标题。回放器兼容“較硬／较硬”等繁简名称，并在确认失败时显示具体原因或未确认的属性。抗锯齿选项、累积硬度和灰显开关仍属于非数字输入状态，回放器不修改它们。

历史状态表与主动索取策略保持一致：初始化一次，文件记录的状态变化需要操作时索取确认；状态未变的连续笔触复用已确认状态。属性库和本机配置的详细对照见 `Memoline_demo_csponly/reports/20261006-csp-catalog/`。
