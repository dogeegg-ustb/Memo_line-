# Recorder Update Activator Demo

BehaviorRecognizer 自动启动这个辅助进程，并在会话结束时关闭它。Python 负责配置解释、初始化选区、ROI 截图和状态核心调度；所有 `.memoline` 写入仍由 C# 的同一个 `MemolineWriter` 完成。

## 启动与状态采集

1. 打开 CSP 当前文档，保持保存的工作区布局和窗口位置不变。
2. 启动 Recorder，在初始化窗口选择当前 `.clip`（像素单位尺寸可自动填入，其他单位须填写实际像素尺寸），或直接手动填写宽高。确认尺寸与当前文档一致。
3. 在“驱动配置”中选择本次使用的数位板驱动配置，或手动选择文件，也可明确选择“不使用驱动映射”。首次启动不会自动选中候选配置；后续可沿用上次选择。多屏且配置仅保存屏幕比例时，选择“映射屏幕”。驱动配置由 C# 确认并写入快照后才继续初始化。然后选择边框闪烁开关和解析并发数（1–4，默认 2），点击开始。工作区面板 ROI、窗口客户区和 DPI 与上次一致时，直接沿用已保存的导航器数字选区。可点击“重新冻结 CSP 并框选数字”强制重选。
4. 需要重选时立即显示 CSP 客户区的冻结截图，无倒计时。在蓝框导航器内拖框，同时包含缩放比例和旋转角度的数字，选区宽高至少 32 像素，Enter 确认、Esc 返回。完整客户区截图仅用于此人工选区界面，不送入状态核心。
5. 切回 CSP 后自动截取所有面板初始状态。画布核心先 `InitializeFrameAsync`，输入同一完整虚拟桌面原始截图与初始化 ROI；成功返回的 `CanvasWindowRoiScreenPx` 和 `NavigatorThumbnailRoiScreenPx` 成为会话中的固定分析 ROI。成功初始化后调用完整帧 `RecomputeAsync`；失败的画布模块会在下一次相关操作时重试初始化。

画布与导航器绑定激活。画布分析使用 MSS 一次采集完整虚拟桌面原始帧，使工作区、导航器缩略图及数字区来自同一帧；初始化使用配置的画布视口和导航器面板 ROI，数字区使用用户勾选的 ROI。成功后核心返回的两个 ROI 保持固定，不增加导航器外扩边距，不重新检测或移动分析锚点。完整帧保留实际屏幕上下文，原生红框延伸和端点确认可读取这些真实像素；不缩放、补边或把分别采集的小图拼接成帧。其他状态核心仍只接收对应面板 ROI 截图。证据保留实际采集起止 ticks；同一帧保证空间一致，不能证明 CSP 的异步面板刷新已经全部完成。

画布任务的 `crops.__canvas_frame__` 保存完整原始帧的路径、屏幕矩形及 screenshotId；Host 的 `frame` 引用这份证据，`crops.workspace/navigator/numbers` 提供同一帧的 ROI。完整帧也以 screenshotBlob 存证，但不建立额外状态模块。各 ROI 图片逐像素裁自该帧，共享采集起止 ticks；其他核心仍接收自己的 ROI 图片。

光标悬停只做准备。每次有效激活均隐藏覆盖层并在 16ms 后排队取像，包括按下、拖动中的变化、快捷键重复和松开；后续激活不会取消已有截图。各面板独立等待最后一次激活后 150ms 无新激活，随后补拍最新证据并交给对应核心解析。画布与导航器作为同一个解析单元。PNG 编码、存盘与分发在另一线程进行，原图队列最多暂存 4 批，饱和时施加背压但不因连续性丢图。100ms 是激活截图的目标预算，不是硬实时保证；静默后的补拍另含 150ms 的等待，证据保留实际耗时。图层保存保护由预热的 AHK 常驻进程发出 Ctrl+S 后立即确认并释放原操作，文件稳定等待仅在独立文件解析队列中进行。

截图先以 `screenshotBlob`（PNG/base64）写入 `.memoline`，再把任务描述追加到会话旁的 `<sessionId>.spool`。连续变化中的图片全部存证，仅最后一个有效静默批次参与解析；保留原图和 `.evidence`／`.superseded` 任务描述。总解析并发数受限，同一个核心始终串行。笔刷/图层 OCR 各限制内部线程，减少并发争用。停止时先排入尚未执行的截图和各面板的最新证据，再等待队列收尾并提交文件 footer。

`settings.json` 是软件设置文件；初始化界面可修改 `flashBorders` 与 `analysisConcurrency`。`analysisQuietMs` 默认 150ms，只控制各面板的解析静默窗口。旧的 `settleCaptureMs`、`canvasSettle*Ms` 和 `interactionCaptureIntervalMs` 不再控制取证；旧设置文件缺少新键时也采用 150ms。发布脚本保留已有设置，保存保护不额外等待，AHK 路径可配置。

## 图层操作与独立文件路径

图层 ROI 内的鼠标点击（以及 Windows 笔兼容鼠标消息）、已配置的图层结构快捷键进入 `LayerSaveGuard`。原始输入仍记录一次，低级回调只暂存输入，后台调用原 `CSP_SaveBeforeClick.ahk --recorder-worker` 执行 Ctrl+S，收到输入派发回执后按顺序释放原输入。注入的保存键和回放输入不会重复录入硬件记录。多个结构操作分别登记保存请求；图层选择快捷键只更新当前图层。AHK 路径自动查找用户/系统 v2 安装，也可在设置中指定。

保存请求立即分配 `saveId` 与单调时间。脚本只回执 Ctrl+S 已发出，不等待 CSP 完成保存。输入派发不是 CSP 应用级接收/锁定确认，`cspAcceptanceConfirmed` 和 `saveCompletionConfirmed` 均保持 false；AHK 初始化未就绪时不拦截；保存失败时 CSP 仍在前台则释放原始指令并记录失败，前台窗口改变时取消被暂存的操作，避免将延迟输入发送到其他程序。独立启动的旧全局左键保存脚本应退出，避免与 Recorder 的保存入口重复。

选择了 `.clip` 路径时，独立文件队列等待文件稳定并复制为带 `saveId` 的快照，再调用 `clip_layers_core`，返回 `clipParseResult`。快照与 SHA-256 保留用于追溯。稳定性检测不能证明该文件就是 CSP 对此保存请求的完成回执，记录中明确保留这一限制；未选择路径时只登记保存请求及无法解析原因。该队列不占截图分析并发槽。Demo 会话保持同一文档，切换文档需重新初始化。

## 已接入

- 复用 `csp_workspace_layout.py` 的布局解码、`csp_workspace_overlay.py` 的窗口匹配及区域计算。
- 复用最新 `CSP_Shortcut_Manager/csp_shortcuts.py` 的只读配置读取；按保存的命令标识解释快捷键，不补造默认快捷键。
- 解析 `.tomd` 中实际保存的修饰键到工具 UUID 映射，以及笔刷大小调整操作。修饰键依赖具体工具时，记录候选上下文，并保守地请求相关面板更新；尚未识别的工具上下文不被声称为已确认状态。
- 悬停只准备监视。实际更新取证完成后，对应面板边框以约 2 Hz 闪烁 0.8 秒；采集期间隐藏覆盖层，连续取证时覆盖层可能保持隐藏。画布与导航器、工具组与笔刷属性保持联动，flashBorders 设置仍有效。
- 色彩区域按 dock 配置的当前选中标签定位，支持同一区域内的色环、色板、颜色滑块、混色、颜色历史、近似色和中间色标签。要求保存的布局中恰好有一个可见的色彩停靠区域。主／副色切换、透明色切换命令触发色彩；吸管工具快捷键及配置中的临时吸管操作也触发色彩。按实际配置匹配键位，不补默认键。输出模块为 `colorState`，仍是待识别的更新请求。
- 笔刷属性与子工具命令触发笔刷属性；视图变换触发画布视口和导航器；图层选择和结构命令触发图层。图层透明度、混合模式、锁定、可见性和像素编辑不会仅凭快捷键触发图层状态模块。
- 鼠标和数位笔事件附带 `heldKeys`，可确定动作当时是否按住空格、Ctrl、Shift 等键。系统按键重复记录为语义 `keyInput`，带 `repeat: true`，以支持持续调整属性。
- 快捷键配置文件及 WAL 发生变化时约两秒内重新读取。仅能读取 CSP 已保存到磁盘的配置。

## 输出契约

### 状态事件的逻辑顺序

初始化包在 `afterEventId=0` 的位置预留；四个截图核心（选择了 `.clip` 时加文件核心）的初始读取返回或明确失败后，开放主硬件录制。Recognizer 的 memoline 仅保存解析成功的模块；失败/未知的初始结果写入同名 `*.diagnostics.jsonl` 日志，不生成文件中的失败状态或缺口。失败不再使整个更新器永久停止接收键鼠输入。初始化期间保持 CSP 文档不变，并等待录制开始提示。

每个可触发更新的硬件事件立即在 Writer 锁内预留内存中的状态位置，携带 `packageId`、`afterEventId`、输入发生 ticks 和 `reservationOrder`。取得成功结果后才一起追加 `statePackageReserved` 与 `statePackageResult`；只保留包内 changed/unchanged 且有确认值的模块，包状态按保留结果重新计算。全失败、无状态激活或停止时仍未完成的包不写入 memoline。相同锚点下按 reservationOrder 恢复原预留次序，记录 ticks 取原发生时间，截图实际采集时间与解析完成时间仍作为证据字段保留。消费者需要解析逻辑顺序，不能直接按物理帧位置复现。

`MemolineToJson` 保留原始 `events`，另输出 `timeline`：初始状态、输入 1、该输入导致的成功状态包、输入 2……。确认相同状态的更新不生成逻辑状态变化。比较笔刷名称与已读出的属性值、当前图层名称、颜色值、画布变换字段；证据 ID、时间、置信度和耗时不参与比较。未知值和分析失败不能覆盖上一份已知状态，只留在诊断日志中。

预留位置解决的是延迟结果的排序。解析成功的最新截图状态直接标记 changed/unchanged，并更新已确认状态；取证越过后续硬件事件时仍保留 `causalAmbiguous=true` 和 `observedAfterEventId` 作为时序诊断，不因此降级识别结果或阻断复现。该状态代表实际截图时的观测值，原触发位置与取证时间均保留，不保证恢复每个中间输入的独立状态。解析器仍兼容旧文件里的失败缺口；新录制不因诊断日志中的失败产生 replayBlocked。

`shortcutConfiguration` 保存配置快照及解析警告；`workspaceStatus` 保存窗口匹配结果和面板屏幕矩形（物理像素 xywh）。`shortcutResolved` 通过 `relatedEventIds` 关联原始按键。`panelUpdateRequested` 的 `ticks` 保持原输入发生时间。

两条逻辑写入线：硬件仍为 `path=hardware`；触发、截图、保存请求为 `path=immediate`；成功的 `coreStateUpdated`、`clipParseResult` 为 `path=delayed`。原始 `stateResult`、`analysisError`、`captureUnavailable`、`clipParseError`、更新器与初始化失败只追加到诊断日志；成功输出仍保存在 `coreStateUpdated.rawResult`。截图包含 `triggerTicks`、`capturedTicks`、`captureEndTicks` 和 `screenshotId`。`captureLatencyMs` 为触发到取像完成的实际耗时，`captureDurationMs` 为取像耗时，`captureBudgetExceeded` 标记是否超过 `captureBudgetMs`（默认 100ms）；这些数值不包含后续 PNG 编码与解析耗时。PNG 每 512KiB 切成一条 `screenshotBlob`，每段 `image` 单独 base64 编码；按同一 `screenshotId` 的 `partIndex` 顺序解码拼接，检查 `partCount`、`byteLength`、`sha256`。状态结果的记录 `ticks` 等于该批证据采集结束时间，另保留原触发时间、`screenshotIds`、`captureId`、`completedTicks`；`appendedTicks` 是真正追加时间。Python 通过 Windows QPC 与 Recorder 共用同一原点及频率，不使用墙上时间排序。

悬停触发没有硬件输入事件引用，不会生成鼠标悬空移动硬件记录。`recognitionStatus: pendingModule` 表示触发本身不是识别结果，识别结果随后独立追加。ColorStateCore 仍要求色环及下方色块/透明色控制区域；若选择其他色彩标签，保留原图并允许返回 unknown，不能声称已经识别。Visual Watchdog 尚未接入。

## 窗口与运行条件

启动时须让 CSP 主窗口可见，并保持保存的布局、窗口位置、最大化状态和 DPI 一致。继续使用原区域计算器支持的五列停靠布局；不支持的布局会明确报告并隐藏边框，不能当作任意工作区定位器。会话中改变窗口矩形会暂停覆盖层；恢复原位置可以恢复。触发面板显示/隐藏快捷键会冻结区域，需在保存新布局后重启记录器。

边框为窄条窗口，使用 `WS_EX_NOACTIVATE`、`WS_EX_TRANSPARENT`、`WS_EX_LAYERED`、`WS_DISABLED` 和 `SWP_NOACTIVATE`。边框不获取焦点/鼠标捕获；截图前暂时隐藏边框。初始化设置和冻结选区窗口是用户主动操作的正常窗口。保存与回放仅由独立保存保护路径处理。

需要 Python 3（含 Tkinter），默认 `py -3`。当前发布依赖按本机 Python 3.14 x64 安装到 `python_libs`；更换 Python 版本需重装匹配的二进制依赖。`MEMOLINE_PYTHON` 指定解释器，`MEMOLINE_CSP_USER_DIR` 和 `MEMOLINE_CSP_DOCK` 指定配置。独立画布宿主含 .NET 8 运行时、Native DLL 和 PP-OCRv5 后备模型，可用 `MEMOLINE_OCR_MODELS` 覆盖模型目录。`publish.ps1` 编译并同步发布目录，保留已存在的发布设置。

诊断配置及区域（不显示覆盖层）：

```powershell
py -3 recognizer/recorder_integration/runtime.py --diagnose
```

触发规则检查：

```powershell
py -3 recognizer/recorder_integration/tests/test_engine.py
```

保存保护通过 `saveInputDispatchedTicks`、`releasedTicks` 和 `interceptionDurationMs` 记录发出保存输入与回放的时刻和实际拦截延迟。`saveSettleMs` 为兼容旧配置保留为 0，Recorder 不再使用此项增加延迟；2–3ms 需要实机测量，线程调度和 IPC 不提供硬实时保证。

工具栏作为笔刷属性的关联激活区域，从保存的 `palettekindtool` 停靠列读取位置。悬停时只准备监视，鼠标/数位笔操作期间照常取证，静默 150ms 后解析最新笔刷状态；仅截取笔刷属性 ROI 送往笔刷核心，初始化不额外产生工具栏状态或工具栏截图。触发记录的 `activationPanels` 保留工具栏来源。

画布 Host 使用 `recognizer_core/screen_canvas_transform` 核心及其 Native DLL，完整虚拟桌面原始帧传入 `InitializeFrameAsync` / 完整帧 `RecomputeAsync`。初始化的 ROI 转为相对此帧原点的 CapturePx；返回及显示的 ROI 保持 ScreenPhysicalPx。完整帧原点来自虚拟桌面，可以为负坐标，输入 DPI 来自 CSP 主窗口；截图与分析结果保留 `captureBackend` 和 `dpi`。笔刷、当前图层和色彩核心继续使用各自面板 ROI 原图。日志输出有效状态更新触发和采集失败。

## 连续取证与解析静默窗口

数位笔在画布视口内的普通落笔、移动、抬笔和悬停不触发画布解析。画布上的指针手势必须在当前输入中按住空格（平移）或 R（旋转），才为手势变化持续取证；操作中途按下这两个键也由笔点或光标状态检测。松开时再取证，静默 150ms 后解析最新批次。仅选择 R/H/Z 等导航工具不解析视口，松开键后后续笔划不继承上次工具的触发目标。配置中的视图快捷键、滚轮缩放、导航器操作，以及鼠标在配置画布 ROI 与核心纠正视口之间的滚动条操作仍可触发更新。更新始终绑定画布与导航器；完整原始帧中的分析锚点沿用核心初始化返回的 ROI。

所有按住操作记录起点、涉及面板与输入 ID。30Hz 光标观测中的实际位置变化激活已关联面板并取证，固定位置的轮询不会重置静默窗口；按钮或笔尚未抬起也不阻止取证和静默后的解析。拖出区域后在 CSP 内松开仍更新起始面板。工具组从保存的 palettekindsubtool 区域读取，与工具属性双向联动；两种关联工具区域都只把工具属性 ROI 送入笔刷核心。

笔刷、图层、色彩和画布各自计算 150ms 静默窗口，只有激活该面板才重置其计时。每次激活的截图独立保留，最后一次激活的静默补拍才用于解析。中间包没有单独解析时明确标记 unknown，证据含 `supersededBeforeAnalysis` 和 `deferredToPackageId`；截图包含自己的 `statePackageId`，仍可单独追溯。最终状态写在最后一次激活的位置，不把它伪装成早期操作的中间状态。严格逐事件复现仍会停在未单独解析的中间边界。

键盘 hook 接受驱动转换的注入输入，沿用相同的语义按键／组合键、CSP 前台判断与快捷键匹配。硬件类型仍为 keyboard，injected 字段描述 Windows 输入属性，不推断它来自哪块数位板。仅排除带 RecorderInputTag 的记录器保存及回放；AHK 保存 worker 与 C# 回放使用相同标记，独立启动的 AHK 脚本不使用此专用标记。

## 输入旁路与保护范围

普通键盘及驱动转换的键盘输入只监视、记录，继续通过 Windows Hook 链交给 CSP；更新激活器不发送、重放或阻止这些快捷键。唯一键盘拦截入口为配置中的图层结构快捷键。保护忙碌期间只暂存该按键的配对释放，不暂存笔刷快捷键或 Ctrl/Shift/Alt 等修饰键；回放时以被拦截指令当时的修饰键组合执行，再恢复当前修饰键状态。鼠标仅暂存受保护图层按钮的延续消息，其他按钮和滚轮放行。

AHK 必须已经预热成功才允许拦截。保存回执等待最多 250ms；失败后 CSP 仍在前台则释放原始图层指令并标记保存失败，防止吞键。250ms 是故障超时，不是正常操作等待时间。SendInput 在缓冲锁之外调用，避免与低级 Hook 相互等待。键盘记录增加 guardIntercepted 与 extraInfo，控制台打印相同信息；图层保护失败包含初始化阶段。

中断的笔／鼠标操作依据实时 cursor 消息中的有效 operation ID 清除。隐藏边框期间再次激活不会取消旧截图；已排队或执行中的旧解析不能覆盖最新激活后的状态。已执行旧解析的原始诊断以 `stateResult.superseded=true` 保留，但不发布为最新已确认状态或覆盖画布覆盖层。

## 滚轮、转盘键码与边框反馈

已读取 DefaultToolModifyKey.tomd 中 mask=0x1000（滚轮）及其修饰键组合。kind=4 的非零操作码触发画布／导航器重算，保留 operationCode 原值，不猜测它的具体动作名称；无配置或不匹配修饰键时，画布滚轮不凭默认行为激活。当前全局配置包含普通滚轮与 Shift+滚轮。水平滚轮只记录轴与 delta，尚无对应配置解码，不作为纵向滚轮匹配。

mouseWheel 是独立有效输入，包含坐标、带符号的 delta、轴和 heldKeys，经同一 Writer 分配事件 ID 与单调时间；无需按住鼠标。采集包括非 Recorder 标记的驱动注入滚轮，保持输入正常传给 CSP。光标在导航器或其他已知面板时，滚轮按该面板的操作路径更新；光标在画布时依据读取的滚轮配置触发。按住期间的每次滚轮激活也单独取证。

已配置 viewzoomin/viewzoomout 时，NUM+ 与 OEM_PLUS（VK_BB）、NUM- 与 OEM_MINUS（VK_BD）使用相同修饰键匹配；精确配置优先，其他命令不使用此兼容映射。日志显示 OEM_PLUS/OEM_MINUS/NUM+/NUM-，匹配证据含 matchSource=equivalentZoomKey。没有已配置缩放命令时不会补造 Ctrl+/- 默认快捷键。

边框只在成功取得更新截图后闪烁；连续取证期间覆盖层保持隐藏以免进入证据。悬停不闪烁。初始化的有效截图同样作为首次状态取证反馈，截图失败不闪烁。

画布原点以直径 28 屏幕像素的蓝色圆点显示，来自核心的 CanvasOriginScreenPx，初始化及重算成功后更新。蓝点独立于边框闪烁开关，CSP 在前台且工作区有效时持续显示，不因用户停止输入而隐藏。圆形窗口禁用输入、穿透且不激活；采集证据前隐藏，解析失败时清除旧原点。

画布核心返回的工作区 ROI（画布和深灰背景）以橙色常驻边框显示，导航器缩略图 ROI 以紫色常驻边框显示，均为 4 屏幕像素宽。独立于更新闪烁开关，CSP 前台且工作区有效时显示；全部覆盖层在取证前隐藏，不进入核心输入。Native DLL 已与原程序 app/Native/ScreenCanvasNative.dll 对齐，格式转换使用 DrawImageUnscaled 和 SourceCopy，不经过缩放绘制。

画布取证失败或红框补全失败时，允许最多一次新的完整帧取证与重算，不用旧图重复调用核心。首批证据与失败结果保留，重取结果的 retryOfCaptureId 引用首批，状态包在最后尝试完成后解析；保持原触发时刻及逻辑位置，若已越过后续输入仍在证据中标记 causalAmbiguous，成功结果直接采用。重取可能超过 100ms 目标预算，实际延迟原样记录。红框种子检测使用固定导航器缩略图 ROI，延伸和端点确认可读取完整帧中的真实上下文；不能把人为填充的黑色间隙当作证据。原算法要求部分红边与白纸接触，缺少端点或有效接触证据时仍可能明确失败。

CSP 的导航器可能晚于画布重绘。现在持续保存真实完整帧，截图前不再等 400ms 或轮询像素稳定性；静默 150ms 后补拍并解析最新一帧。`canvasCaptureValidation` 保留 `sameFrame=true`、`stabilityChecked=false`、`settleWaitMs=0` 与 `policy=latestEvidenceAfterQuietPeriod`，不会把静默误称为渲染完成。核心若检测到补全冲突，仍保留失败及原证据并重取一次。每张证据包含 `analysisToken`、`finalEvidence` 和实际采集时间，状态结果另含 `analysisStartedTicks`、`analysisQuietMs`、`superseded`。取像、PNG 写入和核心执行各自耗时，不承诺结果在触发后 150ms 内完成。

画布宿主异常退出后，用会话中成功初始化的完整原始帧与初始化 ROI 证据恢复核心，再分析当前帧，并检查恢复出的两个固定 ROI 是否一致；不会把旧初始化图作为当前状态结果写入。恢复失败明确报错，不把未初始化的 Host 当作已初始化。

停止时，在两个解析执行器全部排空后，将仍等待取证或重试的状态包以 sessionStoppedBeforeStateResolved 明确封为 error 并写入诊断日志，不向 memoline 追加失败结果或永久 pending。

导航器缩略图 ROI 完全沿用初始化返回值，紫色边框显示相同范围。没有外扩配置；它是完整原始帧中的固定分析区域，画布核心可在保留的真实帧上下文中确认红边端点。

核心成功补全的导航器视口红框以 4 屏幕像素宽的红色常驻覆盖层显示，使用四个 CornerCapture 加上 ScreenCoordinateOriginScreenPx 得到真实屏幕位置，保留旋转形状。未走红框补全路径或分析失败时清除旧框；屏幕外部分裁去，边框不抢焦点、不拦截输入，并在所有截图前隐藏。

## 实时核心结果

每个解析核心完成后，Pipeline 立即写出 `coreStateUpdated`，包含 module、packageId、initial、status、语义状态、原始结果和证据；整个因果状态包仍按原有规则等待全部结果再完成。保存后的 Clip 解析也发布独立更新。Recognizer 将这些结果分发到 5 个核心频道；完整接入方式见 [实时状态接口](../core/memoline/Realtime/README.md)。
