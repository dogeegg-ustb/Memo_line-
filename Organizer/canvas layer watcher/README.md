# canvas layer watcher

独立 Windows 程序，订阅 Recognizer 的实时接口：数位笔接触操作之后，收到画布 `evidenceCaptured` 存证消息就调用同一个 Recognizer 进程的一次性 `requestClipSave` 接口，不等待画布视口识别解析。保存和后续图层读取均关联该消息的 `evidence.triggerTicks`，等待 `.clip` 更新并停止写入后使用 `clipfile-rs` 读取触发时的完整画布图层，并在窗口中显示。图层图像以 `after/now` 两个快照槽轮换，复用 `dirty matrix/source/Core/Coverage.cs` 计算的脏矩阵生成带图像和 Recognizer 状态的差异包。

## 启动

双击 `Start.cmd`。发布版 `publish/win-x64/CanvasLayerWatcher.exe` 自包含，无需安装 .NET 或 Rust。

1. 启动 `recognizer/Recognizer/publish/win-x64-injected-input-evidence/BehaviorRecognizer.exe`。在 CSP 打开已经保存的 `.clip`，启动 Recognizer 录制，并完成图层、画布和数位板校准。程序自动发现活动会话，也可手工选择其他兼容版本的描述文件；Recognizer 必须支持 `requestClipSave` 命令及其可选 `triggerTicks` 参数。
2. 在本程序选择**与 Recognizer 本次录制配置相同的 `.clip` 文件**。实时接口留空会自动发现；也可选择本次会话的 `.memoline.live.json`，或填写管道名。
3. 点击「开始监听」，切回 CSP 绘画。绘画后平移、缩放或旋转画布，Recognizer 在证据像素采集完成时发送消息，本程序即请求保存并更新图层预览。新接口通过 `hello.data.evidenceCapturedNotifications=true` 声明能力；旧进程只发送 `stateUpdated` 时，会记录回退说明并沿用解析结果触发，需重启新版 Recognizer 才能提前保存。
4. 「完整图层」页显示完整画布；成功比较后自动切换到「图像差异」页。左侧放大显示当前区域的更改图像，默认直接读取 `now` 与 `after` 比较后留下的原始 `now` 像素，未更改处透明。右侧同时显示完整图层总览，用一个粉色框标出左侧图像的画布位置。默认选择第一个区域，可逐个切换，或选「全部更改」查看各补丁按原位置合并后的局部图像。脏矩阵默认隐藏，可勾选后只在总览上查看。底部显示变化像素数、标签来源、笔刷和颜色。首张基准和没有变化时清除旧差异。
5. 「修改前原像素」用于对照被改掉或擦除的旧内容；「变化遮罩」用白色标出全部变化，包括擦成透明的像素。「通道差值」是额外检查模式。「适应窗口」只改变显示比例；取消勾选以 100% 滚动查看完整图层或左侧更改图像，右侧总览始终适应窗口。「另存 PNG」保存当前页所示图像：完整图层保持完整尺寸，更改图像保持原始补丁像素，位置框不写入导出 PNG。「差异包」打开数据目录，差异页的「打开差异…」可直接查看已有 manifest.json，无需启动录制。「置顶」方便同时查看；自动更新不激活本窗口。

也可以使用 `CanvasLayerWatcher.exe --diff <manifest.json>` 直接打开已有差异包。每个新包保留一张最多 1600 像素长边的完整图层缩略图；旧的完整快照删除后，历史包仍能显示总览。旧格式包优先使用仍保留、ID 与该包相同的完整快照，否则仅在完整画布尺寸上放置更改像素定位，不混用较新的图层。左侧直接读取原始差异 PNG，按更改区域放大；两个完整图层快照和差异图像的存储分辨率保持原样。

Recognizer 在收到一次性请求后立即向 CSP 派发默认的 **Ctrl+S**，会写入当前已有的 `.clip`，无需再等待一次键鼠输入。预览程序自身不注入保存按键。程序等待修饰键／鼠标／数位笔接触释放后调用保存接口，由 Recognizer 核对前台是否属于 CSP，并返回保存结果。不主动切换前台或文档。使用自定义保存快捷键时需恢复 Ctrl+S，CSP 与 Recognizer 也需保持相同权限级别。

控制管道为 `memoline-recorder-input-<processId>`，进程号来自本次实时接口描述文件。因此手工填写实时管道时，也必须能够找到与之匹配的 `.memoline.live.json`；推荐直接选择描述文件。请求包含 `expectedClipPath`、唯一 `requestId` 和本次存证的 `triggerTicks`，仅接受匹配请求且确认 `saveInputDispatched=true` 的响应，不改变 Recognizer 输入拦截策略。

Recognizer 已移除 CSP 文档标题／文件名匹配校验。请选择当前 CSP 文档对应的 `.clip`，保存后程序仍等待所选文件实际更新。

## 完整画布图像

**输出 PNG 的宽高始终为完整画布像素尺寸，而不是屏幕视口或图层内容的包围盒。** 从本次录制的 `initializationConfiguration.canvasPixelSize` 取得宽高；当 CLIP 使用像素单位时，另外与文件内 Canvas 宽高交叉核对。尺寸不一致时报告错误，请重新校准录制。

读取图层全部 render raster tiles，将整数 `LayerOffsetX/Y` 对应的像素拷贝到完整画布 RGBA 缓冲，空白处保留透明；不按可见区域、笔迹范围或非透明范围缩裁，不缩放读取结果。画布边界外的内容不属于此输出范围。源 raster 大小、画布大小和偏移在读取组件返回的 JSON 中分别记录，包含 `fullCanvas=true`。

显示与导出的都是单层存储的渲染像素；不会将其他图层、混合模式或图层蒙版合成为整幅作品。文件夹、没有 render raster 的图层、缺失外部像素块、无法确认的偏移和超过 6400 万像素的画布会明确失败，保留上次图像。非整数偏移暂不做重采样。

## 监听和保存语义

- 使用 `Memoline.Core` 的 `RecorderRealtimeClient`，订阅 `keyboard`、`mouse`、`tablet` 和全部五个 `core.*`，包括当前图层、图层属性、画布视口、笔刷、颜色；不占用 HID，不另外运行 OCR。连接时还以只读 `getInputControlStatus` 检查同进程控制管道。
- 初始／连接快照只建立基线，不触发保存。悬停、侧键状态和按住 Space／Ctrl／Alt 的常见导航接触不会标记修改。兼容只有鼠标帧的数位板／注入输入：校准画布区域内的左键按下也表示可能编辑，面板点击、中键和导航手势跳过。接口将这类帧标为鼠标，无法区分真实鼠标和数位笔；本程序不据此声称设备身份，也不能判断实际像素是否改变。
- 各核心优先使用 Recognizer 的 `evidence.triggerTicks` 排列图层、视口及笔刷状态，旧消息缺少时才回退到 `capturedTicks` 或消息 ticks，并记录时间来源。`completedTicks/publishedTicks` 不作为选择图层和数据分界的时间。`status=unknown/ambiguous/error` 不覆盖已确认值；`causalAmbiguous` 仅诊断输入归属，`supersededBeforeAnalysis` 的中间图层消息不覆盖已确认图层。
- `core.canvasViewState/evidenceCaptured` 在原始证据像素采集完成时发送，早于 PNG 编码、持久化、静默等待和核心解析。已有编辑输入且基线／目标图层可用时，本程序立刻请求保存；消息尚不能证明视口确已改变，因此可能提前保存一次后来识别为未改变的视口。初始存证及连接快照不保存；同一 triggerTicks 的中间／最终存证合并，随后 stateUpdated 只更新视口，不重复已成功保存的请求。输入仍接触或导航键未释放时，等待输入释放后派发。
- 切换时冻结当前图层 OCR 名称，并与切换前最近的图层属性核心列表对应，保存其规范名称、ID、UUID；兼容“图层1”和“图层 1”的空格差异，多个同名候选不猜测。保存后优先按 UUID 核对文件，其次按 ID 与名称核对，然后按文件中的 ID 导出图像。属性核心尚无该图层时，保存后尝试唯一名称匹配。不会用属性核心上次保存的 `canvas.current_layer_id` 替代切换时的实时选中图层；两者可能不同。已知身份与文件不符时报告失败，不换读另一个同名图层。
- 连续存证／视图更新合并为一次正在处理的请求。触发时固定图层身份、会话、控制管道、画布尺寸和取证缓存，随后保存／解析不会改用新的状态。保存、解析和差异包提交全部成功后才清除输入，分界为本次 `triggerTicks`，不是 `saveInputDispatchedTicks` 或解析完成时间；新操作以及同一笔在 triggerTicks 之后的采样保留到下一轮。失败不移动快照槽，下次触发重试。接口断开／数据缺口会重连并重建基线，不把旧会话的操作带入新会话。
- Ctrl+S 派发不代表 CSP 保存完成。必须观测文件大小或修改时间变化、至少 700 ms 稳定，且能在禁止并发写入的共享模式下复制，再通过 clipfile-rs 的容器／数据库／图层解析。等待保存上限 30 秒，读取组件上限 60 秒。没有文件更新时不展示可能过时的副本；手动已保存后 CSP 不再重写文件也会进入这一情况。triggerTicks 固定请求的时间归属；读取的是 CSP 实际写入的文件，不能追溯重建触发时刻尚未保存的历史像素。
- Recognizer 各核心异步完成；若迟到的图层观察否定了已冻结的名称，丢弃结果并等待下次切换。识别不到图层、同名图层或切换文档期间不会自动猜测目标。

临时 CLIP 副本读取后删除，暂存的 PNG 在差异包提交后移入 `layer-diffs/snapshots/`。状态和失败原因写入 `watcher.log`，选择路径保存在 `watcher-settings.json`。

`watcher.log` 现在同时记录每条视口消息的状态、实际视口值、待保存输入数、图层和触发／跳过原因，以及控制请求发送确认、完整响应和本次订阅计数。可据此区分「没有视口事件」「没有绘画输入」「任务已经在处理」「接口拒绝」「保存文件未更新」。

## after/now、脏矩阵和图像差异

每个 CLIP 图层编号独立维护快照栈。该层第一次成功解析的完整图层图像写入 `after`，暂时没有 `now` 或图像差异；第二次写入 `now`，比较 `after → now`；以后先将该层旧 `now` 移为 `after`，新图写入 `now`，提交成功后删除该层再前一张完整图层 PNG。切换到 B 保留 A 的两个槽，返回 A 接续 A 的最近图像。事件缓存保留至所有层最近快照中最早的 triggerTicks。同编号 UUID 改变时仅重建该层基准；重启监听、Recognizer 重连、更换会话或画布尺寸变化时重建整组基准。

两张源 PNG 始终保持完整画布尺寸。输入屏幕坐标结合输入发生前的已确认视口原点和缩放映射为画布像素；当前接口的 `canvasWindowRoiScreenPx` 及旧版 ROI 字段都支持。优先采用 CSP 使用的 Windows 光标 `x/y`，缺少时使用数位板 `screenX/screenY`；保留原始压力、驱动映射、设备来源等数据。笔刷大小、px/mm、DPI 和屏幕固定大小选项来自 Recognizer；大小未知暂用 20 px，缺少单位按 px，并在标签内记录对应说明。沿接触折线扩展笔刷半径、抗锯齿／安全余量和 12 屏幕像素附加余量，再使用原脏矩阵的稀疏行区间算法，单元尺寸为 64 画布像素。

脏单元内逐像素比较 RGBA；范围外将整个画布缩至约 1/8 宽高作简单检查，检测到变化的单元再回到完整分辨率比较。两边 alpha 都为零时忽略隐藏 RGB。低分辨率检查是近似筛选，可能漏掉范围外很小的变化。缺少输入历史或可靠坐标变换时记录原因，仍进行低分辨率检查。坐标映射优先采用接口实际提供的归一化 `rawResult.snapshot.screenToCanvas.m0..m5`；当前版本未序列化这组矩阵时，使用画布原点、缩放以及 `state.transform.rotationDegrees` 原生几何旋转建立逆变换。旋转时如果连几何旋转也缺失，则使用低分辨率检查，不凭 OCR 角度猜测方向。

每组连通的变化单元提供一张主更改图像 `images[].image`，指向只保留更改像素的原始 `now` 补丁。另保留 `after/mask/difference` 作为前后对照、擦除定位和通道检查的辅助数据；四张 PNG 均使用完整画布坐标标明放置范围，保持原像素分辨率。完整源图层仍由两个快照槽保存。独立 mask 区分「没有变化」与「擦成透明」。每份更改图像关联一个或多个脏矩阵标签；范围外发现的变化使用 `low-resolution-fallback` 标签。标签包含影响范围、输入操作、时间和状态 ID，反向记录对应图像 ID。

`layer-diffs/current.json` 的 `layerStacks` 按图层编号保存各层 `after/now`，顶层 `after/now` 指向当前栈。`layer-diffs/packets/<id>/manifest.json` 和 PNG 保存历次差异包。删除旧的完整快照不会删除差异补丁。包内同时保留 Recognizer 各核心原始状态／识别状态／证据、配置、驱动、键盘、输入点和保存控制响应。状态按取证时间排列；各核心异步解析，包记录提交时已收到的观察，不声称它们都在保存时同步完成。首张基准之前未监听到的输入标记为不完整。详细结构见 [差异包格式](DIFF_FORMAT.md)。

## 构建与检查

需要 .NET SDK 10、Rust 和 Windows MSVC 工具链。此机器已有项目内便携 Rust，Build.ps1 自动使用；其他机器使用 PATH 中的 cargo。依赖版本锁定于 `bridge/Cargo.lock`。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File './Organizer/canvas layer watcher/Build.ps1'
dotnet build './Organizer/canvas layer watcher/checks/CanvasLayerWatcher.Checks.csproj' -c Release -m:1 -p:UseSharedCompilation=false -nr:false
dotnet run --project './Organizer/canvas layer watcher/checks/CanvasLayerWatcher.Checks.csproj' -c Release --no-build
cargo test --locked --manifest-path './Organizer/canvas layer watcher/bridge/Cargo.toml'
& './Organizer/canvas layer watcher/publish/win-x64/CanvasLayerWatcher.exe' --smoke './Organizer/canvas layer watcher/checks/artifacts/ui'
```

检查包含触发状态机、失败重试／会话隔离、保存文件稳定性、实际当前用户命名管道与录制配置绑定、真实空白 CLIP 的透明 PNG、合成彩色 tile 的逐像素往返、完整画布边缘及正负偏移，以及快照轮换、取消／失效后的提交保护、图层隔离、矩阵内精细比较、范围外低分辨率补查、擦除 mask、半透明 RGBA 保真和状态打包。差异显示检查还覆盖原像素读取与合并、前后图／mask、完整画布定位、重叠透明补丁、总览大小上限、完整快照删除后的历史总览和旧包兼容。管道检查需要本机当前用户权限，限制命名管道的沙箱中无法运行。UI smoke 验证双区域显示、原像素默认模式、区域选择与位置框对应、前后图、mask、快速切换、基准／无变化提示及最小窗口布局，不派发 CSP 输入。`--smoke <输出目录> <manifest.json>` 可额外渲染一个实际差异包进行检查。

`checks --replay-dirty <录制文件>` 只读回放真实 Recognizer 帧，输出每次切换的输入、状态与矩阵；`checks --diff-recorded <回放 capture-N.json> <完整 PNG> <输出目录>` 使用同一份真实图层导出验证全尺寸比较，两者均不调用保存控制接口。

图层读取基于 [clipfile-rs 1.2.0](https://github.com/Aodaruma/clipfile-rs)，使用其公开 `read_document`、`open_database`、`layer_raster_source`、`decode_raster` 和只读 schema／connection 接口。上游 `.clip` 解析仍属于独立格式实现，实际 CSP 的保存与复杂图层效果尚需实机验证。
