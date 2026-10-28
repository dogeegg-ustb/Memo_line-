# Memoline_demo_csponly

这是 CSP 专用的集成演示程序：Recognizer 记录原生机械事件，watcher 在**已确认的画布视口变化**处划分聚集事件包，保存并解析当前图层，用脏矩阵辅助 after/now 图像比较。

2026-10-08 已将编辑交互改为 CSP 冻结画面上的选区、跨层脏矩阵图片和整笔扫选，并接入截图笔刷属性选区。当前行为、检查和剩余缺口见 [交互整改说明](docs/architecture/2026-10-08-canvas-editing-corrections.md)。CSP 实机验收仍须使用工程副本。

## 使用

1. 在 CSP 中打开已经保存的 `.clip` 文档。
2. 双击本目录的 `Memoline.exe`，在“录制设置”中选择笔输入来源、数位板、驱动配置和映射屏幕，再选择同一个 `.clip` 文件并点击“开始录制”。程序会启动配套的私有 Recognizer 副本并自动连接它的接口。
3. 完成 Recognizer 的画布和导航器设置后切回 CSP，等待窗口显示“录制开始 baseline 已建立；持续监听中”再正常作画。程序会等待初始状态采集和解析完成后建立基线；窗口随后显示解析得到的完整图层，以及实际变化图像和它在完整画布上的位置。
4. 录制中暂停作画，按 Alt+M 或从托盘打开面板，先选择「精确撤销」或「事件编辑」，再建立边界并冻结 CSP 画面。直接在原画布位置框选或圈选范围。精确撤销只涉及已确认的当前图层：选区后显示基础笔数、叠放闭包和白线路径；预览方案后确认 CSP 撤销容量至少为实际所选总笔数＋5，再执行。事件编辑先显示选区涉及的跨层脏矩阵图片；选择单张图片，在画布移动光标显示附近白线，按住鼠标像矢量橡皮一样扫选整笔，再决定让事件不再发生或更换笔刷。
   更换笔刷时点击「更换笔刷 / 属性」，保留所选事件并回到 CSP，选择目标笔刷和属性，再点击「截取并框选属性面板」，在冻结截图中框选完整的笔刷属性面板。程序识别该范围的实时笔刷和数字属性并显示所选截图；需要时进一步调整可恢复数字。重放仍使用 packetreplayer 的子工具路由及数字输入/读回。凡涉及重放，须将色彩滑块切到 RGB 页并选中主色。预览后执行，执行期间有可见进度和取消入口。Esc 取消拖选，空闲时返回作画。
5. 结束时点击“停止并封盘”。程序等已接受的保存与 diff 任务完成，再让 Recognizer 正常停止并写出原生 footer，校验并合并两份记录。
6. 最终文件位于 `publish/win-x64/recordings`，例如 `作品.clip` 对应 `作品.memoline`。再次录制同一完整路径的工程时，自动接到原文件中；历史会话的机械字节、时钟、图片和编辑日志均保留。不同目录下的同名工程使用带路径摘要的稳定文件名。封盘成功后清理本次主数据中间文件，失败保留它们供重试。

`Memoline.exe` 是 Windows 图形程序，包含 .NET 运行时，无需通过 CMD 启动或另装 .NET。请保留同目录的 `clip-layer-bridge.exe` 和 `publish` 配套目录；拷贝程序时一起拷贝整个文件夹。录制文件仍保存在 `publish/win-x64/recordings`，可点击窗口的“录制文件”打开。

首次构建或更新发布内容时，运行 `Build.ps1`；开发构建需要 .NET 10 SDK。集成程序使用独立的 Recognizer 发布副本及设置，不要求同时手动启动另一个 Recognizer。请通过“停止并封盘”正常结束会话。

色轮与 RGB 滑块复用原来的颜色区域配置。程序从当前截图识别 R/G/B 数字和位置；每次数值输入后重新读取确认。缺少可见数字、主色选中证据或新的截图时会提示切换页面并停止执行。

复现期间的自动画笔、快捷键及中间状态只用于事务和实时校验。完成后写入一次“事件编辑”（带修改内容）或“精确撤销”，随后用户继续作画及 Ctrl+Z 正常入库。物理输入打断会中止事务。当前编辑面板索引本次活跃会话；跨会话编辑尚未接通。历史参考像素调度和逐笔场景已接到事务，但真实 CSP 的指定参考层标记/读回和不污染撤销栈的场景清理仍未接通，实际启用参考读取的方案会在粘贴前停止。明确确认关闭的参考复选框可走普通重放路径，并在落笔前再次核验。

2026-10-22 起，事件编辑和精确撤销都按“先删后贴”原位替换目标图层：确认 CSP 当前选中的就是目标层后先删除它（CSP 随即选中下方一行），再粘贴合成图，新层正好落在原位置，一次保存同时证明删除、位置和选中；目标层是所在文件夹最底一行时退回“先贴、证明位置后再删”。原层不再隐藏。保存记录里的当前层只在 CSP 真正写盘时更新（单纯切换选中层不写盘），所以只有边界时图层面板也显示该层，才直接先删；否则先在面板确认。剪贴板只放 PNG（CSP 粘贴的就是它，实测与导出逐字节一致），重试时再补 CF_DIBV5。重放每笔最快 20 ms；离散重放（精确撤销）每笔之间等 200 ms，与 packetreplayer 的离散模式一致，间隔更短时 CSP 会把相邻几笔记成一个撤销步（2026-10-26 实测：50 ms 时 38 笔只剩 24 步，用户真笔悬停时只剩 2 步）。每个采样点都等 CSP 处理完上一个才注入：CSP 读不及的指针更新会被 Windows 合并，实测 16 笔若各压到 20 ms 只剩 33% 的墨且多成直线，带等待则与原速重放无异，速度上限是 CSP 约 2 ms 画一个采样点。各步耗时写入 `logs/edit-timing.log`。

开发检查还需要 Node（现有 backend 依赖）和带 Recognizer 依赖的 Python。host 检查会运行实际 `engine.mjs --compose` 与 C# 合成器逐字节对拍；Node 仅用于开发检查，产品发布不需要 Node 运行时。

## 运行时与封盘

运行中写两份主数据文件：

- 私有 Recognizer 的 `Recognizer/procedure/stroke/*.memoline.part`：原生机械事件与原生状态，继续使用 Recognizer 原有的单向时间轴、追加 ID 和因果引用。
- 当前工作目录 `.work/<会话工作目录>/aggregate-events.jsonl.part`：维度时间序列、固定事件包、脏矩阵、图像资产及对应的原生事件指针。

图层快照、预览和日志是计算缓存或诊断辅助文件。最终 `.memoline` 是集成 ZIP 容器，收纳原生机械文件的**完整原始字节**、聚集 JSONL 的**完整原始字节**以及校验索引。图像资产已经写入聚集流，最终读取它们不依赖工作目录中的 PNG。

新版封盘使用 `memoline-csponly/v2`：对每份完整流做 Brotli 最高级别无损压缩，跨记录使用 16 MiB 历史窗口。解压后的字节数、SHA256 和固定包哈希仍与运行时流相同。压缩仅在停止录制后的封盘阶段执行，较大文件会延长封盘时间。旧版 v1 文件继续可以读取、校验和升级。新录制按 PNG 完整字节的 SHA256 复用资产；不同路径或不同包中的相同图像只存一次，各包保留自己的图像映射、矩阵、状态和事件指针。

机械事件、已提交事件包及其脏矩阵、图像、指针都不能被维度算法升级改写。第二部分的维度可以新增算法版本；新版本只改变容器中的活动维度入口，原始记录和固定内容继续保留。

如果停止、解析或封盘失败，程序保留两份输入文件供重试或恢复。未写完 footer 的原生/聚集流不能直接封盘。

## 当前聚集行为

“录制设置”直接查询 Recognizer 的设备和驱动目录。OTD 模式可指定一块数位板；指定后只接收该设备的报告，设备未连接时会报错。Windows 笔模式使用系统笔事件。驱动选项支持自动发现、手动选择文件、明确关闭映射及选择映射屏幕；选择会交给原 Recognizer 的 DriverInitializationService 应用。画布和导航器数字选区仍使用原 Recognizer 的初始化窗口。

画布存证通知与“视口确实发生变化”是两件不同的事。本演示程序以确认的 `canvasViewState` 变化触发保存和正式聚集边界，仍使用该次存证的原始 `triggerTicks`，不会改用解析完成时间。每次确认的变化都有一个包，即使该窗口没有绘制输入、图层尚未确认或图像捕获失败，也保留带原因的空包。

“停止并封盘”还会请求 Recognizer 的原生结束边界，在控制接口关闭前保存当前图层并计算末尾差异；没有发生新的视口变化也会生成最后一个聚集包。点击停止后会尝试将 CSP 激活再派发保存输入。该包最终闭合到原生 footer，覆盖最后一次视口边界之后的所有机械事件；保存、解析或状态获取失败时也保留带原因的空包。最终保存允许读取已保存且没有改动的稳定 CLIP 文件，避免干净文档因文件时间戳不变而等待超时。

维度样本使用 Recognizer 原始 ticks：`canvasViewportChanged` 为 `0` 或 `1`；动作规律、工具、图层、区域和时间连续性目前为 `null`，不把尚未实现的算法伪装成 `0`。

解析与比较使用完整画布像素，不裁切图层源图。每个 CLIP 图层编号有独立的快照栈，最多保留两张完整源图：第一张作为 after 基准，后续 now 与该层上一张快照比较，两张源图在该层内部轮换。A → B → A 会接续上次 A 的历史，B 的栈保留。同编号图层的 UUID 改变则重建该层基准；会话、连接代次、程序运行或画布尺寸改变时重建整组基准。事件缓存清理以所有层最近快照中最早的 triggerTicks 为界，保留返回旧层后生成矩阵标签所需的输入和状态。脏矩阵优先提供精细搜索范围，其他区域使用低分辨率检查后按需要细化。展示的主 diff 图像是变化位置上的原始 now RGBA 像素；辅助 before 和 mask 用于检查擦除等变化。

当前图层核心的名称先与本次保存的 CLIP 图层列表对应（忽略名称空格），确定唯一图层编号后调用 `export-id` 读取完整画布图像。初始化阶段的编号/UUID只作为提示；同名图层重建后会使用当前文件的新编号。封包 manifest 的 `capture.layerId`、`capture.layer` 是实际读取的编号与身份，`observedLayerName` 保留触发时的核心名称；多个同名图层会明确报歧义。

数位笔每次下笔固定一个 `penDownLocation`，按当时工作区的屏幕面板范围分成笔刷属性、笔刷选择（工具组）、工具栏、导航器、图层、画布视口和其他。使用 Windows 实际光标坐标，接触过程中移动到别的面板不会改变下笔分类。分类同时存在于新录制的原生笔事件、聚集包 `penContacts` 及对应的脏矩阵标签中；画布外面板操作不生成笔画精细搜索范围。按空格等导航键的画布操作继续作为导航处理。

## 快捷键、图层、笔刷与子工具接口

本机订阅频道 `shortcuts` 提供自动读取 CSP 已保存配置得到的快捷键与功能对照表。两个图层核心分别对应独立接口：`layerstage` 提供当前所处图层状态，`layers` 提供文件解析得到的完整图层父子结构与内部属性。各接口连接时回放对应核心的最新结果，之后推送更新。

配置接口还提供 `toolCatalog` 完整工具层级和 `brushPackages` 已安装子工具组目录；工具快捷键的 `tool/subtools` 保留大类下所有具体笔刷、组路径、节点身份及保存的选中项。`subtools/ocrUpdated` 提供本帧可见子工具名称的 `ocrEntries` 和组名称的 `groupEntries`，包含面板局部及屏幕 OCR 坐标、配置身份、图片选中证据和同帧采集时间。工具切换、点击和列表滚动都会刷新位置。

`core.brushState` 的 `data.valueRegions` 提供笔刷属性值的位置，按 `number`、`icon`、`text` 分类；仅返回值区域，数字、当前文字选项、复选框和图案各自定位。`bbox` 为面板局部坐标，`screenBbox` 为同帧的屏幕坐标。属性名称和面板标题不返回 OCR 框；无法分离的属性名和值混合框标记位置未确定。

```powershell
# 在本目录查询快捷键，无需开始录制。
.\Memoline.exe --shortcuts | Out-Host
# 开始录制后，用本次实际的接口描述文件分别订阅图层状态和结构。
.\Memoline.exe --subscribe layerstage --endpoint "publish\win-x64\Recognizer\procedure\stroke\<本次会话>.memoline.live.json" | Out-Host
.\Memoline.exe --subscribe layers --endpoint "publish\win-x64\Recognizer\procedure\stroke\<本次会话>.memoline.live.json" | Out-Host
.\Memoline.exe --subscribe core.brushState --endpoint "publish\win-x64\Recognizer\procedure\stroke\<本次会话>.memoline.live.json" | Out-Host
.\Memoline.exe --subscribe subtools --endpoint "publish\win-x64\Recognizer\procedure\stroke\<本次会话>.memoline.live.json" | Out-Host
```

订阅使用已有 Recognizer 命名管道，只允许当前 Windows 用户连接。快捷键查询支持 `--config-dir` 指定目录；完整字段、快照/失败语义及 C# 接入示例见 [INTERFACES.md](INTERFACES.md)。

## 命令行工具

以下命令在发布目录中运行。输出路径必须尚不存在，工具不会覆盖输入或已有输出。

```powershell
.\MemolineDemo.exe --verify "recordings\session.memoline" | Out-Host
.\MemolineDemo.exe --extract-mechanical "recordings\session.memoline" "native.memoline" | Out-Host
.\MemolineDemo.exe --seal "native.memoline" "aggregate-events.jsonl.part" "sealed.memoline" | Out-Host
.\MemolineDemo.exe --compact "recordings\session.memoline" "recordings\compact\session.memoline" | Out-Host
.\MemolineDemo.exe --upgrade-dimensions "sealed.memoline" "dimensions-v2.jsonl" "continuity-v2" "upgraded.memoline" | Out-Host
```

`--verify` 校验两份流的哈希、会话和时钟、机械时间轴、所有事件指针、图像分块及固定内容哈希。`--seal` 可对正常完成而尚未合并的两份记录重新封盘，命令本身不删除输入。

`--compact` 为已有文件生成更小的无损副本，保留原生机械字节、原始聚集 JSONL、所有维度版本和活动版本。不会重新编写历史包或对旧图像降采样；若重压缩没有缩小体积，则复制原编码，输出不会变大。原文件不会修改。

`--extract-mechanical` 导出的文件与 Recognizer 原生记录逐字节一致，可以交给原来的 MemolineReader、Recognizer `--export` 或 `--follow`。原生 reader 不直接读取集成 ZIP 容器。

维度升级文件只能包含 header、dimensionSample、footer；使用相同的 sessionId、frequency 和会话 ticks，每个样本的 algorithmVersion 必须等于命令提供的版本。版本名只能使用字母、数字、点、下划线和连字符，最多 64 个字符，首字符必须是字母或数字。详见 [FORMAT.md](FORMAT.md)。

## 检查

```powershell
dotnet run --project checks\MemolineDemo.Checks.csproj -c Release
```

检查使用模拟机械记录和图像，不向 CSP 注入输入；覆盖原始字节保留、旧格式读取与无损压缩、图像跨路径/跨包复用及引用校验、事件指针、两份流的完整性、维度升级隔离和空包。
