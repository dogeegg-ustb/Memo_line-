# BehaviorRecognizer

面向 CLIP STUDIO PAINT 的键盘、鼠标和数位板输入采集软件。内嵌 OpenTabletDriver 采集核心，**用户无需单独安装 OpenTabletDriver**。

## 能力

- 启动时自动探测环境（Windows Ink / vMulti / 权限 / 数位板）
- 自动加载内置笔配置
- 初始化时让用户选择数位板驱动配置，支持自动发现、手动选文件和关闭映射；读取共享的 `../recognizer_core/driver_reader` 核心。
- 按所选驱动配置计算压力曲线与数位板物理坐标到屏幕物理像素的映射，保留原始笔点并将配置快照写入 `.memoline`。
- CSP 是活动窗口时记录键盘按键及组合键；仅记录实际按键语义，不保存每个低级按键边沿
- 光标实时位于 CSP 窗口时记录鼠标按下、按住移动、释放，以及数位板接触笔迹
- 外部程序通过 SendInput 等方式注入的键盘、鼠标点击、拖拽和滚轮也会进入相同的激活流程；程序自身带标记的保存/重放输入不重复激活。
- 默认沿用原有 OTD 笔报告采集，保留压力和倾斜值；可选 Windows 被动笔模式，该路径可能无法提供压力和倾斜值
- 按发生顺序给硬件输入分配事件 ID；统一使用会话单调时钟写入追加式 `.memoline`
- 延迟解释的状态记录可追加到同一文件，并通过硬件事件 ID 关联原输入
- 自动读取已保存的 CSP 面板布局与快捷键，悬停或相关快捷键使对应面板边框闪烁，并追加状态更新请求；详见 [更新触发原型](../recorder_integration/README.md)
- vMulti 缺失时仅引导安装，**不阻塞基础采集**

## 构建

需要 .NET SDK 10：

工作区覆盖层辅助进程另需 Python 3（含 Tkinter，默认 `py -3`；可通过 `MEMOLINE_PYTHON` 指定解释器）。辅助进程失败记入控制台和会话同名的 `*.diagnostics.jsonl` 日志，硬件会话仍可正常停止并落盘。

新录制的 memoline 只计入解析成功的核心状态（changed/unchanged）。同一状态包里的成功模块照常写入；失败、未知、有歧义的结果与未完成的包只留在诊断日志中。原始解析尝试 `stateResult` 也归入诊断日志，成功输出仍在 `coreStateUpdated.rawResult` 中保存。输入、配置、触发和截图沿用原记录规则。

项目引用仓库已附带的 `publish/win-x64/` 中 OTD 0.6.7 与相关托管依赖。当前检出无需额外的 OTD 源码目录或联网还原包。

```powershell
cd Recognizer
dotnet build .\src\BehaviorRecognizer\BehaviorRecognizer.csproj -c Release
```

## 运行

推荐直接运行**自包含发布版**（无需安装 .NET 10）：

```powershell
.\publish\win-x64\BehaviorRecognizer.exe
```

初始化界面的“驱动配置”必须由用户确认后才会开始录制。首次启动不自动选择候选；之后可沿用上次选择，也可更换或关闭映射。多屏且配置仅保存屏幕比例时，可在“映射屏幕”中明确选择显示器。详细坐标、压力约定见 [DriverReader.Core](../recognizer_core/driver_reader/README.md)。

只读驱动配置诊断（不启动 HID、输入钩子或录制界面）：

```powershell
.\publish\win-x64\BehaviorRecognizer.exe --diagnose-driver
.\publish\win-x64\BehaviorRecognizer.exe --diagnose-driver --driver-config "C:\path\to\EKeySetting.dt"
```

笔点 `pressure`、`tabletX/Y` 仍为原始数据；新增 `normalizedPressure`、`mappedPressure`、`physicalX/Y`、`screenX/Y`、`driverSnapshotId` 和映射状态。`x/y` 使用 Windows 实际光标位置判断 CSP 点击、拖拽和更新目标，`interactionCoordinateSource` 为 `windowsCursor`。`screenX/Y` 保存驱动映射后的屏幕坐标，映射不可用时使用 Windows 光标位置；`screenCoordinateSource` 标记该数据的来源。笔接触状态沿用 Recognizer 的接触阈值，压力曲线不改变点击/抬笔事件；压力归零时，即使报告没有接近标志，也会结束上一笔。

重新发布：

```powershell
dotnet publish .\src\BehaviorRecognizer\BehaviorRecognizer.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

开发调试：

```powershell
dotnet run --project .\src\BehaviorRecognizer\BehaviorRecognizer.csproj -c Release
```

常用命令：

```powershell
# 导出会话为 JSONL
BehaviorRecognizer --export .\procedure\stroke\xxx.memoline

# 持续读取正在录制的文件（stdout 为 JSONL，Ctrl+C 停止）
BehaviorRecognizer --follow .\procedure\stroke\xxx.memoline.part

# 订阅当前会话的实时输入和解析状态（stdout 为 JSONL）
BehaviorRecognizer --subscribe all --endpoint .\procedure\stroke\xxx.memoline.live.json

# 为旧会话创建压缩副本，保留原文件
BehaviorRecognizer --compact .\procedure\stroke\xxx.memoline .\procedure\stroke\xxx.compressed.memoline

# 列出未完成的 .memoline.part
BehaviorRecognizer --recover
```

录制中输入 `V` + Enter 可打开 vMulti 安装引导。

默认沿用原有 OTD 笔报告采集。若设备环境不适合 OTD 直接采集，可用 `BehaviorRecognizer --passive-pen` 改用 Windows 笔兼容事件；这一模式不打开数位板 HID，压力与倾斜值可能不可用。系统输入钩子仅排队通知，窗口判断与写盘在后台执行。

## 目录布局（自动创建）

运行后写入程序所在目录下的 `procedure\`：

- `config/`
- `cache/`
- `sessions/`
- `stroke/`（`.memoline` 会话）
- `exports/`
- `logs/`
- `drivers/`
- `bootstrap/`

## 架构分层

| 层 | 目录 | 职责 |
|---|---|---|
| 启动编排 | `Bootstrap/` | 环境探测、配置装载、管道组装 |
| 采集核心 | `Capture/` | OTD 内嵌设备发现与报告读取 |
| 归一化 / 总线 | `Capture/` | `InputEventNormalizer` + `InputEventBus` |
| 会话 | `Session/` | 会话状态机与应用目录 |
| 记录器 | `Recording/` | 记录器总线与默认 / 扩展记录器 |
| 存储 | `../core/memoline/` | 追加事件容器、刷盘屏障、增量读取和独立帧压缩 |
| 契约 | `Abstractions/` | 规范要求的全部可替换接口 |

## 笔迹回放原型

`tools/StrokeReplay/` 是旧版 STRO v1 `.strokebin` 笔迹回放原型；新的 `.memoline` 文件可通过 `--export` 导出 JSONL，回放工具尚未适配新容器。

`../MemolineToJson/` 提供独立的 `.memoline` 转结构化 JSON 解析器；详见 [解析器 README](../MemolineToJson/README.md)。

## `.memoline` 事件

新录制使用 v2 格式，每帧独立 Brotli 压缩并校验 CRC32，仍按追加顺序保存。读取器同时兼容旧的 v1 文件。`header` 给出 UTC 创建时间、`Stopwatch` 频率和单调时钟原点；`footer` 表示完整关闭。未完整关闭的 `.part` 文件可读取到最后一个完整且校验通过的帧，不需要先停止录制。

后台写入在空闲批次立即刷新到操作系统缓存，并默认每 100ms 请求硬件刷盘。`MemolineWriter.FlushAsync()` 提供明确的刷盘完成屏障；`MemolineReader.Open(...).ReadAvailable()` 和 `FollowAsync(...)` 提供增量与持续读取，已打开的 reader 可在录制停止、文件改名后继续读到 footer。完整 API、格式和时延约定见 [Memoline.Core](../core/memoline/README.md)。

硬件帧的 `path` 为 `hardware`，`eventId` 唯一递增，`ticks` 是会话单调时钟偏移。`operationId` 把一次鼠标按住或一笔笔迹关联起来。每条硬件帧的 `deviceSource` 标出设备类别、采集接口、设备 ID 和识别精度。OTD 笔报告提供数位板 ID；Windows 低级鼠标和键盘钩子不提供物理设备 ID，因此其 `deviceId` 为 `null`、`identification` 为 `classOnly`。被动笔只由 Windows 笔事件签名识别，标为 `penSignature`。键盘 `keyInput` 包含主键、修饰键、虚拟键码和扫描码；`shortcutMatch` 预留为空，待 CSP 快捷键配置文档接入。状态帧的 `path` 为 `state`，`relatedEventIds` 关联原硬件帧，`ticks` 表示状态发生时间，`appendedTicks` 表示实际写入时间。两类帧共享递增的 `appendId`。

## 实时状态订阅

每次录制自动建立当前 Windows 用户专用的命名管道，并打印 `*.memoline.live.json` 描述文件路径。可独立订阅 `keyboard`、`mouse`、`tablet`、`core.brushState`、`core.currentLayerState`、`core.colorState`、`core.canvasViewState`、`core.clipState`，支持多客户端和连接时的最新消息快照。

键盘新增按下、重复、释放、修饰键和焦点复位消息；鼠标新增最多约 30Hz 的变化光标消息；OTD 数位板新增悬停、側键和感应范围消息。接触笔点、原有快捷键与面板激活路径保持原规则。每个核心解析完成便发布 changed/unchanged/unknown/ambiguous/error、变化字段和最近已确认状态。实时通知不等于已完成磁盘刷盘；完整历史和截图仍从文件读取。

C# 进程内订阅、外部客户端、JSONL 协议和全部输出字段见 [实时状态接口文档](../core/memoline/Realtime/README.md)。

## 许可证

本项目链接 OpenTabletDriver（LGPL-3.0-or-later）。详见 [NOTICE.md](NOTICE.md)。
## 一次性 CSP 保存控制接口

实时画布／图层订阅与保存控制由同一个 Recognizer 进程提供。使用实时接口描述文件的 `processId` 连接当前用户命名管道 `memoline-recorder-input-<processId>`，UTF-8 JSONL，一行请求对应一行响应：

```json
{"command":"requestClipSave","expectedClipPath":"C:\\art\\drawing.clip","requestId":"viewport-unique-id","triggerTicks":952451354}
```

初始化完成后，无需新的键鼠事件即可派发 Ctrl+S。该命令不改变输入拦截配置；工作线程忙、尚未完成初始化或前台不是 CSP 时返回 `success=false` 和 `error`。按调用方要求已移除文档标题／文件名匹配校验；调用方负责选择 CSP 当前文档的 `.clip`。调用方应等待笔接触及导航按键释放，再发起请求。

`triggerTicks` 是可选的非负 64 位整数，取自同一录制会话的 `data.evidence.triggerTicks`，不得晚于该会话当前 ticks；省略或传 `null` 兼容旧请求。服务端将其传入保存工作线程并在响应中原样返回。外部保存的 `clipSaveRequest/saveGuardResult` 以该 triggerTicks 标记时间归属，同时单独记录 `requestReceivedTicks` 和实际 `saveInputDispatchedTicks`。

成功响应包含 `success=true`、原 `requestId`、`triggerTicks`、`saveInputDispatched=true`、`saveInputDispatchedTicks`、`saveCompletionConfirmed=false`。它只确认保存输入已派发，调用方仍须等待目标 `.clip` 改变并稳定后读取。对应 C# 接口是 `IRecorderInputControl.RequestClipSaveAsync`，原键鼠输入控制命令亦可经此管道调用。

实时 `core.*` 频道现在另发送 `kind=evidenceCaptured`，在证据原始像素采集完成后立即发布，不等待 PNG 编码或状态解析。消息携带 `data.evidence.triggerTicks`，以及采集时间、captureId、ROI、来源和解析待完成标记；解析完成后仍发送原来的 `stateUpdated`。`hello.data.evidenceCapturedNotifications=true` 可用于检测这项能力。证据事件不替换最后确认状态或初始快照，也不能提前断言视口值已改变。监听程序可在 `core.canvasViewState/evidenceCaptured` 到达时调用 `requestClipSave`，使用 triggerTicks 关联保存与随后读取的图层；CSP 文件内容仍需等待保存写入后才能读取。

画布移动／松开时立即取证并发送上述通知，最后一次画布激活后静止 150ms 才解析这份证据。期间再次移动会更新证据和激活版本，重新计时，并跳过旧证据解析；画布不再在静默结束后补拍。其他面板的输入不会延长画布等待。`evidence.analysisQuietMs=150`，画布即时证据的 `finalEvidence=true` 表示可以在静默后解析，仍须匹配最新 `analysisToken`。
