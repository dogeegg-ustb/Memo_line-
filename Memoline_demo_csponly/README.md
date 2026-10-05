# Memoline_demo_csponly

这是 CSP 专用的集成演示程序：Recognizer 记录原生机械事件，watcher 在**已确认的画布视口变化**处划分聚集事件包，保存并解析当前图层，用脏矩阵辅助 after/now 图像比较。

## 使用

1. 在 CSP 中打开已经保存的 `.clip` 文档。
2. 运行本目录的 `Start.cmd`，选择同一个 `.clip` 文件，点击“开始录制”。程序会启动本目录的私有 Recognizer 副本并自动连接它的接口。
3. 等 Recognizer 初始化完成，切回 CSP 正常操作。窗口显示解析得到的完整图层，以及实际变化图像和它在完整画布上的位置。
4. 结束时点击“停止并封盘”。程序等已接受的保存与 diff 任务完成，再让 Recognizer 正常停止并写出原生 footer，校验并合并两份记录。
5. 最终文件保存在程序旁边的 `recordings` 目录。封盘成功后，这一会话的两份主数据中间文件会被删除。

首次构建或更新发布内容时，运行 `Build.ps1`；开发构建需要 .NET 10 SDK。集成程序使用独立的 Recognizer 发布副本及设置，不要求同时手动启动另一个 Recognizer。请通过“停止并封盘”正常结束会话。

## 运行时与封盘

运行中写两份主数据文件：

- 私有 Recognizer 的 `Recognizer/procedure/stroke/*.memoline.part`：原生机械事件与原生状态，继续使用 Recognizer 原有的单向时间轴、追加 ID 和因果引用。
- 当前工作目录 `.work/<会话工作目录>/aggregate-events.jsonl.part`：维度时间序列、固定事件包、脏矩阵、图像资产及对应的原生事件指针。

图层快照、预览和日志是计算缓存或诊断辅助文件。最终 `.memoline` 是集成 ZIP 容器，收纳原生机械文件的**完整原始字节**、聚集 JSONL 的**完整原始字节**以及校验索引。图像资产已经写入聚集流，最终读取它们不依赖工作目录中的 PNG。

机械事件、已提交事件包及其脏矩阵、图像、指针都不能被维度算法升级改写。第二部分的维度可以新增算法版本；新版本只改变容器中的活动维度入口，原始记录和固定内容继续保留。

如果停止、解析或封盘失败，程序保留两份输入文件供重试或恢复。未写完 footer 的原生/聚集流不能直接封盘。

## 当前聚集行为

画布存证通知与“视口确实发生变化”是两件不同的事。本演示程序以确认的 `canvasViewState` 变化触发保存和正式聚集边界，仍使用该次存证的原始 `triggerTicks`，不会改用解析完成时间。每次确认的变化都有一个包，即使该窗口没有绘制输入、图层尚未确认或图像捕获失败，也保留带原因的空包。

维度样本使用 Recognizer 原始 ticks：`canvasViewportChanged` 为 `0` 或 `1`；动作规律、工具、图层、区域和时间连续性目前为 `null`，不把尚未实现的算法伪装成 `0`。

解析与比较使用完整画布像素，不裁切图层源图。第一张图作为 after 基准；后续 now 与上一张 after 比较，两张源图轮换保留。脏矩阵优先提供精细搜索范围，其他区域使用低分辨率检查后按需要细化。展示的主 diff 图像是变化位置上的原始 now RGBA 像素；辅助 before 和 mask 用于检查擦除等变化。

## 命令行工具

以下命令在发布目录中运行。输出路径必须尚不存在，工具不会覆盖输入或已有输出。

```powershell
.\MemolineDemo.exe --verify "recordings\session.memoline" | Out-Host
.\MemolineDemo.exe --extract-mechanical "recordings\session.memoline" "native.memoline" | Out-Host
.\MemolineDemo.exe --seal "native.memoline" "aggregate-events.jsonl.part" "sealed.memoline" | Out-Host
.\MemolineDemo.exe --upgrade-dimensions "sealed.memoline" "dimensions-v2.jsonl" "continuity-v2" "upgraded.memoline" | Out-Host
```

`--verify` 校验两份流的哈希、会话和时钟、机械时间轴、所有事件指针、图像分块及固定内容哈希。`--seal` 可对正常完成而尚未合并的两份记录重新封盘，命令本身不删除输入。

`--extract-mechanical` 导出的文件与 Recognizer 原生记录逐字节一致，可以交给原来的 MemolineReader、Recognizer `--export` 或 `--follow`。原生 reader 不直接读取集成 ZIP 容器。

维度升级文件只能包含 header、dimensionSample、footer；使用相同的 sessionId、frequency 和会话 ticks，每个样本的 algorithmVersion 必须等于命令提供的版本。版本名只能使用字母、数字、点、下划线和连字符，最多 64 个字符，首字符必须是字母或数字。详见 [FORMAT.md](FORMAT.md)。

## 检查

```powershell
dotnet run --project checks\MemolineDemo.Checks.csproj -c Release
```

检查使用模拟机械记录和图像，不向 CSP 注入输入；覆盖原始字节保留、事件指针、两份流的完整性、维度升级隔离、空包与图像资产校验。
