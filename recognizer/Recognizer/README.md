# BehaviorRecognizer

面向 CLIP STUDIO PAINT 的键盘、鼠标和数位板输入采集软件。内嵌 OpenTabletDriver 采集核心，**用户无需单独安装 OpenTabletDriver**。

## 能力

- 启动时自动探测环境（Windows Ink / vMulti / 权限 / 数位板）
- 自动加载内置笔配置
- CSP 是活动窗口时记录键盘按键及组合键；仅记录实际按键语义，不保存每个低级按键边沿
- 光标实时位于 CSP 窗口时记录鼠标按下、按住移动、释放，以及数位板接触笔迹
- 默认沿用原有 OTD 笔报告采集，保留压力和倾斜值；可选 Windows 被动笔模式，该路径可能无法提供压力和倾斜值
- 按发生顺序给硬件输入分配事件 ID；统一使用会话单调时钟写入追加式 `.memoline`
- 延迟解释的状态记录可追加到同一文件，并通过硬件事件 ID 关联原输入
- 自动读取已保存的 CSP 面板布局与快捷键，悬停或相关快捷键使对应面板边框闪烁，并追加状态更新请求；详见 [更新触发原型](../recorder_integration/README.md)
- vMulti 缺失时仅引导安装，**不阻塞基础采集**

## 构建

需要 .NET SDK 10：

工作区覆盖层辅助进程另需 Python 3（含 Tkinter，默认 `py -3`；可通过 `MEMOLINE_PYTHON` 指定解释器）。辅助进程失败会写入明确错误，硬件会话仍可正常停止并落盘。

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

# 列出未完成的 .memoline.part
BehaviorRecognizer --recover
```

录制中输入 `V` + Enter 可打开 vMulti 安装引导。

## 输入控制接口

初始化完成后，可通过 `CapabilityOrchestrator.InputControl`（`IRecorderInputControl`）或启动日志中的本地命名管道 `memoline-recorder-input-<PID>` 控制输入保护。管道为 UTF-8 JSONL，一条请求对应一条响应，仅接受当前 Windows 用户的连接。

```json
{"command":"getInputControlStatus"}
{"command":"configureInputInterception","enabled":true}
{"command":"configureInputInterception","enabled":false}
{"command":"restoreAutomaticInputProtection"}
{"command":"requestClipSave","expectedClipPath":"D:\\art\\drawing.clip","requestId":"viewport-change-001"}
```

`requestClipSave` 在初始化完成后立即请求一次保存，无需等下一次键鼠输入，不改变输入拦截策略。也可调用 `IRecorderInputControl.RequestClipSaveAsync`。请求由常驻保存线程执行，与原保存队列串行；工作线程忙、前台不属于 CSP 或标题与预期文档不匹配时，返回 `success=false`。路径必须是绝对 `.clip` 路径，`requestId` 必须非空且不超过 128 字符。标题核对只能匹配文档名称，不能区分不同目录中的同名文档。

一次性回执包含 `success`、`requestId`、`saveInputDispatched`、`saveInputDispatchedTicks`、`saveCompletionConfirmed=false` 和 `error`。这表示保存按键已派发，并非文件写入完成；调用方必须继续检查指定文件的更新与稳定性，然后读取保存副本。`clipSaveRequest` / `saveGuardResult` 保存相同请求 ID，并使用 `reason=externalControl`。

控制范围为 CSP 前台时的键盘（含修饰键及驱动转换按键）、鼠标移动／按钮／滚轮。键鼠输入进入同一队列，先直接派发 Ctrl+S，再按队列顺序释放原输入；Recorder 自己的注入带专用标记，不会再次记录或拦截。新控制模式放行带笔签名的鼠标兼容消息，原始笔压、倾斜及 Windows Ink/Wintab 包不在该接口的拦截范围内；OTD 原始报告继续用于记录。

控制策略不会被工作区辅助进程的周期配置覆盖。关闭控制模式停止新的拦截，已经排队的输入及其配对释放继续排空；`restoreAutomaticInputProtection` 恢复现有图层操作保护。保存失败时，只要原 CSP 仍在前台就释放原指令；切换前台窗口时取消向其他程序回放。响应的 `enabled` 为配置意图，实际是否能够拦截还要看 `saveWorkerReady`、录制初始化状态和 CSP 前台状态。

新控制模式采用常驻专用线程、进程内 `SendInput` 和队列信号，省去 AHK 的 IPC 往返，不增加固定 sleep，也不为每次输入启动工作线程。保存派发前临时释放用户已按住的修饰键，派发 Ctrl+S 后恢复，再释放原键鼠事件。已有自动图层保护仍使用原 AHK 通道。`clipSaveRequest` / `saveGuardResult` 在独立日志队列中写入，退出时排空，避免阻塞后续输入释放；日志保留 `interceptionDurationMs` 和新增的 `saveDispatchToReleaseMs`。这条快路径确认保存输入已派发，`saveCompletionConfirmed` 仍为 false；没有 CSP 文件保存完成回执，不能承诺固定毫秒数或已经完成写盘。

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
| 存储 | `Storage/Memoline/` | 追加事件容器、JSONL 导出和未完成帧读取 |
| 契约 | `Abstractions/` | 规范要求的全部可替换接口 |

## 笔迹回放原型

`tools/StrokeReplay/` 是旧版 STRO v1 `.strokebin` 笔迹回放原型；新的 `.memoline` 文件可通过 `--export` 导出 JSONL，回放工具尚未适配新容器。

`../MemolineToJson/` 提供独立的 `.memoline` 转结构化 JSON 解析器；详见 [解析器 README](../MemolineToJson/README.md)。

## `.memoline` 事件

文件头为 `MEMOLINE` 加 32 位版本号 1，之后每帧为 32 位载荷长度、32 位 CRC32 和 UTF-8 JSON。帧按追加顺序保存。`header` 给出 UTC 创建时间、`Stopwatch` 频率和单调时钟原点；`footer` 表示完整关闭。未完整关闭的 `.part` 文件可读取到最后一个完整且校验通过的帧。

硬件帧的 `path` 为 `hardware`，`eventId` 唯一递增，`ticks` 是会话单调时钟偏移。`operationId` 把一次鼠标按住或一笔笔迹关联起来。每条硬件帧的 `deviceSource` 标出设备类别、采集接口、设备 ID 和识别精度。OTD 笔报告提供数位板 ID；Windows 低级鼠标和键盘钩子不提供物理设备 ID，因此其 `deviceId` 为 `null`、`identification` 为 `classOnly`。被动笔只由 Windows 笔事件签名识别，标为 `penSignature`。键盘 `keyInput` 包含主键、修饰键、虚拟键码和扫描码；`shortcutMatch` 预留为空，待 CSP 快捷键配置文档接入。状态帧的 `path` 为 `state`，`relatedEventIds` 关联原硬件帧，`ticks` 表示状态发生时间，`appendedTicks` 表示实际写入时间。两类帧共享递增的 `appendId`。

## 许可证

本项目链接 OpenTabletDriver（LGPL-3.0-or-later）。详见 [NOTICE.md](NOTICE.md)。
