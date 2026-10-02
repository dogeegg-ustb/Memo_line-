# Recorder Update Activator Demo

BehaviorRecognizer 自动启动这个辅助进程，并在会话结束时关闭它。Python 负责配置解释、初始化选区、ROI 截图和状态核心调度；所有 `.memoline` 写入仍由 C# 的同一个 `MemolineWriter` 完成。

## 启动与状态采集

1. 打开 CSP 当前文档，保持保存的工作区布局和窗口位置不变。
2. 启动 Recorder，在初始化窗口选择当前 `.clip`（像素单位尺寸可自动填入，其他单位须填写实际像素尺寸），或直接手动填写宽高。确认尺寸与当前文档一致。
3. 选择边框闪烁开关和解析并发数（1–4，默认 2），点击开始。工作区面板 ROI、窗口客户区和 DPI 与上次一致时，直接沿用已保存的导航器数字选区。可点击“重新冻结 CSP 并框选数字”强制重选。
4. 需要重选时立即显示 CSP 客户区的冻结截图，无倒计时。在蓝框导航器内拖框，同时包含缩放比例和旋转角度的数字，选区宽高至少 32 像素，Enter 确认、Esc 返回。完整客户区截图仅用于此人工选区界面，不送入状态核心。
5. 切回 CSP 后自动截取所有面板初始状态。画布核心先 `InitializeFrameAsync`，输入同一完整虚拟桌面原始截图与初始化 ROI；成功返回的 `CanvasWindowRoiScreenPx` 和 `NavigatorThumbnailRoiScreenPx` 成为会话中的固定分析 ROI。成功初始化后调用完整帧 `RecomputeAsync`；失败的画布模块会在下一次相关操作时重试初始化。

画布与导航器绑定激活。画布分析使用 MSS 一次采集完整虚拟桌面原始帧，使工作区、导航器缩略图及数字区来自同一帧；初始化使用配置的画布视口和导航器面板 ROI，数字区使用用户勾选的 ROI。成功后核心返回的两个 ROI 保持固定，不增加导航器外扩边距，不重新检测或移动分析锚点。完整帧保留实际屏幕上下文，原生红框延伸和端点确认可读取这些真实像素；不缩放、补边或把分别采集的小图拼接成帧。其他状态核心仍只接收对应面板 ROI 截图。证据保留实际采集起止 ticks；同一帧保证空间一致，不能证明 CSP 的异步面板刷新已经全部完成。

画布任务的 `crops.__canvas_frame__` 保存完整原始帧的路径、屏幕矩形及 screenshotId；Host 的 `frame` 引用这份证据，`crops.workspace/navigator/numbers` 提供同一帧的 ROI。完整帧也以 screenshotBlob 存证，但不建立额外状态模块。各 ROI 图片逐像素裁自该帧，共享采集起止 ticks；其他核心仍接收自己的 ROI 图片。

光标悬停只做准备；按住操作在松开后更新，工具属性等待 150ms 无新操作，普通操作默认等待 CSP 响应 30ms、隐藏边框后再等 16ms 取像。PNG 编码、存盘与分发在另一线程进行，原图队列最多暂存 4 批，饱和时施加背压。100ms 是普通触发到取像完成的目标预算，不是硬实时保证；工具属性的 150ms 静默等待和画布的导航器稳定等待另计，实际证据记录完整触发延迟。图层保存保护由预热的 AHK 常驻进程发出 Ctrl+S 后立即确认并释放原操作，文件稳定等待仅在独立文件解析队列中进行。

截图先以 `screenshotBlob`（PNG/base64）立即写入 `.memoline`，再把分析任务描述追加到会话旁的 `<sessionId>.spool`。队列只加载正在分析的图像；总并发数受限，同一个核心始终串行。笔刷/图层 OCR 各限制内部线程，减少并发争用。原图和任务暂存保留在会话目录，解析失败也能保留证据。停止时等待已进入队列的任务收尾，随后提交文件 footer。

`settings.json` 是软件设置文件；初始化界面可修改 `flashBorders` 与 `analysisConcurrency`，`settleCaptureMs` 控制普通松开／快捷键操作后的渲染等待（默认 30ms）；工具属性固定等待 150ms 无新操作。`canvasSettleMinMs`／`canvasSettleQuietMs`／`canvasSettleMaxMs`／`canvasSettlePollMs`（默认 400／150／1500／30ms）控制画布取证前的导航器稳定等待，见下文。发布脚本保留已有的发布设置，缺少这些键时使用默认值。`interactionCaptureIntervalMs` 不再用于拖动期间取样，保存保护不额外等待，AHK 路径可配置。

## 图层操作与独立文件路径

图层 ROI 内的鼠标点击（以及 Windows 笔兼容鼠标消息）、已配置的图层结构快捷键进入 `LayerSaveGuard`。原始输入仍记录一次，低级回调只暂存输入，后台调用原 `CSP_SaveBeforeClick.ahk --recorder-worker` 执行 Ctrl+S，收到输入派发回执后按顺序释放原输入。注入的保存键和回放输入不会重复录入硬件记录。多个结构操作分别登记保存请求；图层选择快捷键只更新当前图层。AHK 路径自动查找用户/系统 v2 安装，也可在设置中指定。

保存请求立即分配 `saveId` 与单调时间。脚本只回执 Ctrl+S 已发出，不等待 CSP 完成保存。输入派发不是 CSP 应用级接收/锁定确认，`cspAcceptanceConfirmed` 和 `saveCompletionConfirmed` 均保持 false；AHK 初始化未就绪时不拦截；保存失败时 CSP 仍在前台则释放原始指令并记录失败，前台窗口改变时取消被暂存的操作，避免将延迟输入发送到其他程序。独立启动的旧全局左键保存脚本应退出，避免与 Recorder 的保存入口重复。

选择了 `.clip` 路径时，独立文件队列等待文件稳定并复制为带 `saveId` 的快照，再调用 `clip_layers_core`，返回 `clipParseResult`。快照与 SHA-256 保留用于追溯。稳定性检测不能证明该文件就是 CSP 对此保存请求的完成回执，记录中明确保留这一限制；未选择路径时只登记保存请求及无法解析原因。该队列不占截图分析并发槽。Demo 会话保持同一文档，切换文档需重新初始化。

## 已接入

- 复用 `csp_workspace_layout.py` 的布局解码、`csp_workspace_overlay.py` 的窗口匹配及区域计算。
- 复用最新 `CSP_Shortcut_Manager/csp_shortcuts.py` 的只读配置读取；按保存的命令标识解释快捷键，不补造默认快捷键。
- 解析 `.tomd` 中实际保存的修饰键到工具 UUID 映射，以及笔刷大小调整操作。修饰键依赖具体工具时，记录候选上下文，并保守地请求相关面板更新；尚未识别的工具上下文不被声称为已确认状态。
- 悬停、按住拖动和更新等待阶段不闪烁。实际更新取证完成后，对应面板边框以约 2 Hz 闪烁 0.8 秒；画布与导航器、工具组与笔刷属性保持联动，flashBorders 设置仍有效。
- 色彩区域按 dock 配置的当前选中标签定位，支持同一区域内的色环、色板、颜色滑块、混色、颜色历史、近似色和中间色标签。要求保存的布局中恰好有一个可见的色彩停靠区域。主／副色切换、透明色切换命令触发色彩；吸管工具快捷键及配置中的临时吸管操作也触发色彩。按实际配置匹配键位，不补默认键。输出模块为 `colorState`，仍是待识别的更新请求。
- 笔刷属性与子工具命令触发笔刷属性；视图变换触发画布视口和导航器；图层选择和结构命令触发图层。图层透明度、混合模式、锁定、可见性和像素编辑不会仅凭快捷键触发图层状态模块。
- 鼠标和数位笔事件附带 `heldKeys`，可确定动作当时是否按住空格、Ctrl、Shift 等键。系统按键重复记录为语义 `keyInput`，带 `repeat: true`，以支持持续调整属性。
- 快捷键配置文件及 WAL 发生变化时约两秒内重新读取。仅能读取 CSP 已保存到磁盘的配置。

## 输出契约

### 状态事件的逻辑顺序

初始化包在 `afterEventId=0` 的位置预留；四个截图核心（选择了 `.clip` 时加文件核心）的初始读取返回或明确失败后，开放主硬件录制。失败/未知的初始状态原样保留并阻断严格复现；失败不再使整个更新器永久停止接收键鼠输入。未能识别的值保留 unknown，不当作已知初始值。初始化期间保持 CSP 文档不变，并等待录制开始提示。

每个可触发更新的硬件事件立即在 Writer 锁内预留 `statePackageReserved`，携带 `packageId`、`afterEventId` 和输入发生 ticks。在该输入后、后续输入前获得一个确定的逻辑位置。解析结果通过 `statePackageResult` 引用原包，记录 ticks 取预留的发生时间，截图实际采集时间与解析完成时间仍作为证据字段保留。二进制容器继续追加，消费者需要解析逻辑顺序，不能直接按物理帧位置复现。

`MemolineToJson` 保留原始 `events`，另输出 `timeline`：初始状态、输入 1、该输入导致的状态包、输入 2……。确认相同状态的更新不生成逻辑状态变化。比较笔刷名称与已读出的属性值、当前图层名称、颜色值、画布变换字段；证据 ID、时间、置信度和耗时不参与比较。未知值和分析失败不能覆盖上一份已知状态。会话结束仍未解析的包明确标记 error，保留缺口。

预留位置解决的是延迟结果的排序。取证若已经越过后续硬件事件，`causalAmbiguous=true`，结果标记 ambiguous，不能断言它是早期触发对应的精确中间状态。解析器输出 `replayBlocked` 和 `replayableThroughEventId`，复现器应停在未决/未知/有歧义的边界，避免使用旧状态继续播放。快照采集延迟及渲染时序仍需在 CSP 实机验证。

`shortcutConfiguration` 保存配置快照及解析警告；`workspaceStatus` 保存窗口匹配结果和面板屏幕矩形（物理像素 xywh）。`shortcutResolved` 通过 `relatedEventIds` 关联原始按键。`panelUpdateRequested` 的 `ticks` 保持原输入发生时间。

两条逻辑写入线：硬件仍为 `path=hardware`；触发、截图、保存请求为 `path=immediate`；`stateResult`、`analysisError`、`clipParseResult` 为 `path=delayed`，均经同一 Writer 追加。截图包含 `triggerTicks`、`capturedTicks`、`captureEndTicks` 和 `screenshotId`。`captureLatencyMs` 为触发到取像完成的实际耗时，`captureDurationMs` 为取像耗时，`captureBudgetExceeded` 标记是否超过 `captureBudgetMs`（默认 100ms）；这些数值不包含后续 PNG 编码与解析耗时。PNG 每 512KiB 切成一条 `screenshotBlob`，每段 `image` 单独 base64 编码；按同一 `screenshotId` 的 `partIndex` 顺序解码拼接，检查 `partCount`、`byteLength`、`sha256`。状态结果的记录 `ticks` 等于该批证据采集结束时间，另保留原触发时间、`screenshotIds`、`captureId`、`completedTicks`；`appendedTicks` 是真正追加时间。Python 通过 Windows QPC 与 Recorder 共用同一原点及频率，不使用墙上时间排序。

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

工具栏作为笔刷属性的关联激活区域，从保存的 `palettekindtool` 停靠列读取位置。悬停时只准备监视，鼠标/数位笔操作松开并静默 150ms 后更新笔刷状态；仅截取笔刷属性 ROI 送往笔刷核心，初始化不额外产生工具栏状态或工具栏截图。触发记录的 `activationPanels` 保留工具栏来源。

画布 Host 使用 `recognizer_core/screen_canvas_transform` 核心及其 Native DLL，完整虚拟桌面原始帧传入 `InitializeFrameAsync` / 完整帧 `RecomputeAsync`。初始化的 ROI 转为相对此帧原点的 CapturePx；返回及显示的 ROI 保持 ScreenPhysicalPx。完整帧原点来自虚拟桌面，可以为负坐标，输入 DPI 来自 CSP 主窗口；截图与分析结果保留 `captureBackend` 和 `dpi`。笔刷、当前图层和色彩核心继续使用各自面板 ROI 原图。日志输出有效状态更新触发和采集失败。

## 操作结束与工具属性合并

画布只由配置中的视图快捷键／临时导航手势、导航器操作，以及配置画布视口 ROI 减去核心纠正后的视口 ROI（滚动条所在区域）触发。导航工具的 pointerTargets 也来自快捷键配置。更新始终绑定画布与导航器；完整原始帧中的分析锚点沿用核心初始化返回的 ROI。

所有按住操作记录起点、涉及面板与输入 ID；中途滑动不截图，最后一个按钮／笔抬起后才请求更新。拖出区域后在 CSP 内松开仍更新起始面板。工具组从保存的 palettekindsubtool 区域读取，与工具属性双向联动；两种关联工具区域都只把工具属性 ROI 送入笔刷核心。

工具属性的点按、按键重复及快捷键共享 150ms 静默窗口，新操作重置等待，最后一次更新引用整组触发 ID。合并掉的中间触发没有截图，其状态包明确标记 unknown，证据含 debouncedIntermediateState 和 deferredToPackageId；最终状态写在最后一次操作的位置，不把它伪装成早期各次操作的中间状态。因此严格逐事件复现仍会在未取证的中间边界停止。

键盘 hook 接受驱动转换的注入输入，沿用相同的语义按键／组合键、CSP 前台判断与快捷键匹配。硬件类型仍为 keyboard，injected 字段描述 Windows 输入属性，不推断它来自哪块数位板。仅排除带 RecorderInputTag 的记录器保存及回放；AHK 保存 worker 与 C# 回放使用相同标记，独立启动的 AHK 脚本不使用此专用标记。

## 输入旁路与保护范围

普通键盘及驱动转换的键盘输入只监视、记录，继续通过 Windows Hook 链交给 CSP；更新激活器不发送、重放或阻止这些快捷键。唯一键盘拦截入口为配置中的图层结构快捷键。保护忙碌期间只暂存该按键的配对释放，不暂存笔刷快捷键或 Ctrl/Shift/Alt 等修饰键；回放时以被拦截指令当时的修饰键组合执行，再恢复当前修饰键状态。鼠标仅暂存受保护图层按钮的延续消息，其他按钮和滚轮放行。

AHK 必须已经预热成功才允许拦截。保存回执等待最多 250ms；失败后 CSP 仍在前台则释放原始图层指令并标记保存失败，防止吞键。250ms 是故障超时，不是正常操作等待时间。SendInput 在缓冲锁之外调用，避免与低级 Hook 相互等待。键盘记录增加 guardIntercepted 与 extraInfo，控制台打印相同信息；图层保护失败包含初始化阶段。

中断的笔／鼠标操作依据实时 cursor 消息中的有效 operation ID 清除，避免遗留按住状态阻塞更新。工具属性截图在 150ms 等待后的隐藏边框阶段再次检查操作版本；如果又有新操作，旧任务作废，不覆盖新的等待包。

## 滚轮、转盘键码与边框反馈

已读取 DefaultToolModifyKey.tomd 中 mask=0x1000（滚轮）及其修饰键组合。kind=4 的非零操作码触发画布／导航器重算，保留 operationCode 原值，不猜测它的具体动作名称；无配置或不匹配修饰键时，画布滚轮不凭默认行为激活。当前全局配置包含普通滚轮与 Shift+滚轮。水平滚轮只记录轴与 delta，尚无对应配置解码，不作为纵向滚轮匹配。

mouseWheel 是独立有效输入，包含坐标、带符号的 delta、轴和 heldKeys，经同一 Writer 分配事件 ID 与单调时间；无需按住鼠标。采集包括非 Recorder 标记的驱动注入滚轮，保持输入正常传给 CSP。光标在导航器或其他已知面板时，滚轮按该面板的操作路径更新；光标在画布时依据读取的滚轮配置触发。按住期间的滚轮激活合并到松开后的更新。

已配置 viewzoomin/viewzoomout 时，NUM+ 与 OEM_PLUS（VK_BB）、NUM- 与 OEM_MINUS（VK_BD）使用相同修饰键匹配；精确配置优先，其他命令不使用此兼容映射。日志显示 OEM_PLUS/OEM_MINUS/NUM+/NUM-，匹配证据含 matchSource=equivalentZoomKey。没有已配置缩放命令时不会补造 Ctrl+/- 默认快捷键。

边框只在成功取得更新截图后闪烁；连续工具属性操作先等待 150ms 静默，不因每次点按／悬停闪烁。初始化的有效截图同样作为首次状态取证反馈。截图失败不闪烁。

画布原点以直径 28 屏幕像素的蓝色圆点显示，来自核心的 CanvasOriginScreenPx，初始化及重算成功后更新。蓝点独立于边框闪烁开关，CSP 在前台且工作区有效时持续显示，不因用户停止输入而隐藏。圆形窗口禁用输入、穿透且不激活；采集证据前隐藏，解析失败时清除旧原点。

画布核心返回的工作区 ROI（画布和深灰背景）以橙色常驻边框显示，导航器缩略图 ROI 以紫色常驻边框显示，均为 4 屏幕像素宽。独立于更新闪烁开关，CSP 前台且工作区有效时显示；全部覆盖层在取证前隐藏，不进入核心输入。Native DLL 已与原程序 app/Native/ScreenCanvasNative.dll 对齐，格式转换使用 DrawImageUnscaled 和 SourceCopy，不经过缩放绘制。

画布取证失败或红框补全失败时，允许最多一次新的完整帧取证与重算，不用旧图重复调用核心。首批证据与失败结果保留，重取结果的 retryOfCaptureId 引用首批，状态包在最后尝试完成后解析；保持原触发时刻及逻辑位置，若已越过后续输入仍标记 causalAmbiguous。重取可能超过 100ms 目标预算，实际延迟原样记录。红框种子检测使用固定导航器缩略图 ROI，延伸和端点确认可读取完整帧中的真实上下文；不能把人为填充的黑色间隙当作证据。原算法要求部分红边与白纸接触，缺少端点或有效接触证据时仍可能明确失败。

CSP 先重绘画布视图，导航器红框与缩放／旋转数字要晚约 150–400ms 才刷新；中间时刻的完整帧把新工作区和旧红框配在一起，核心会以补全冲突拒绝。因此画布取证先只采样固定的缩略图 ROI 与数字 ROI（不看工作区，因为其中有 CSP 自绘的笔刷光标）：至少等到触发后 `canvasSettleMinMs`，并且这两个 ROI 连续 `canvasSettleQuietMs` 不变后，才截取完整帧；完整帧中的这两块像素还须与最后一次采样相同，否则继续等待。最短等待不可省略，因为尚未刷新的导航器同样“不变”。连续的请求（如滚轮）沿用上一次的观察，但静默窗口不早于各自的触发时刻。超过 `canvasSettleMaxMs` 仍未稳定时照常存证，但 `canvasCaptureValidation.stable=false`，分析阶段拒绝送入核心并按上述规则重取一次。`canvasCaptureValidation` 记录 `stable`、`timedOut`、`attempts`、`samples`、`settleWaitMs`、`quietObservedMs`，以及 `navigatorChangeAfterTriggerMs`（观察到导航器变化时距触发的毫秒数），可据此按实机延迟调整设置。画布取证因此通常超过 100ms 目标预算，并会推迟采集队列中排在其后的截图；批次的 `capturedTicks` 与 `captureDurationMs` 只描述完整帧本身。

画布宿主异常退出后，用会话中成功初始化的完整原始帧与初始化 ROI 证据恢复核心，再分析当前帧，并检查恢复出的两个固定 ROI 是否一致；不会把旧初始化图作为当前状态结果写入。恢复失败明确报错，不把未初始化的 Host 当作已初始化。

停止时，在两个解析执行器全部排空后，将仍等待取证或重试的状态包以 sessionStoppedBeforeStateResolved 明确封为 error，避免留下永久 pending。

导航器缩略图 ROI 完全沿用初始化返回值，紫色边框显示相同范围。没有外扩配置；它是完整原始帧中的固定分析区域，画布核心可在保留的真实帧上下文中确认红边端点。

核心成功补全的导航器视口红框以 4 屏幕像素宽的红色常驻覆盖层显示，使用四个 CornerCapture 加上 ScreenCoordinateOriginScreenPx 得到真实屏幕位置，保留旋转形状。未走红框补全路径或分析失败时清除旧框；屏幕外部分裁去，边框不抢焦点、不拦截输入，并在所有截图前隐藏。
