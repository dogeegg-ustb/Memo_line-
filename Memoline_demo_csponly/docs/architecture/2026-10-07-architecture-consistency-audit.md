# 当前实现与事件编辑架构的一致性审查

日期：2026-10-07。审查对象：`D:/Memo_Line/Memo_Line` 当前源码，包括上一轮加入的独立画布预览交互。

依据：[事件编辑架构](2026-10-07-event-editing-architecture.md)，以及用户本轮明确的要求：冻结 CSP 画面，直接在 CSP 画布位置选择范围。用户尚未开始实机复现，本轮主要核对实现是否符合设计。

**结论：部分记录、合成和事务核心已接通；事件编辑的用户流程明显不符合架构，不能将当前程序描述为已经完整实现该设计。** 上一轮新增的 PictureBox 框选/圈选仍然走错了交互路径。其检查通过只证明该预览交互内部工作，不能证明架构规定的交互已经实现。

隐藏原图层由用户另行开发模块接入。本轮不评判或修改隐藏图层实现，不把眼睛定位、隐藏快捷键语义或隐藏后备路径列入本次整改范围。

本轮为源码审查，未操作真实 CSP、未执行实机复现、未改用户配置，也未修改产品源码或再次发布程序。下文的“可达风险”是代码分析结论，不是用户已遇到的问题。

## 一、交互流程的确定偏差

| 项目 | 设计要求 | 当前实现与证据 | 影响 |
| --- | --- | --- | --- |
| Alt+M 的入口 | §7.1：自动建立编辑边界，完成后开放选择 | `source/DemoApplicationContext.cs:31` 仅显示和激活面板；`source/EditingPanel.cs:64` 还要另点“建立编辑边界” | 用户必须自行理解并执行一个内部步骤，入口没有承接设计流程 |
| 直接在 CSP 画布选择 | §7.2：虚拟屏幕置顶覆盖层接收区域选择；本轮用户补充冻结 CSP 画面 | `source/EditingPanel.cs:30` 使用独立 `CanvasSelectionPreview`；`:104` 将当前整层捕获解码为面板图片；`source/CanvasSelectionPreview.cs:8` 继承 PictureBox；宿主没有 EventSelectionOverlay | 用户看到的是脱离当前 CSP 缩放、旋转和其他可见图层的单层预览 |
| 原生屏幕到画布变换 | §7.2：使用边界时的 screenToCanvas，校验会话与画布尺寸 | `source/Editing/CanvasSelectionGeometry.cs:16` 只按 PictureBox 的图片显示比例换算；`source/Editing/DemoEditingHost.cs:124` 返回序列化的 `feed.View.Current`，其 CanvasViewSnapshot 不包含正逆矩阵或 sessionId（`replayer/strokereplayer/source/RecognizerViewMonitor.cs:10`） | 现有选择接口不能承载设计要求的真实 CSP 屏幕选区，也没有相应身份校验 |
| 先选择单张脏矩阵 | §1.7、§7.2：跨图层列出与选区相交的单张图片，展示补丁缩略图、图层、时间、事件数、可选状态和原因 | `source/EditingPanel.cs:97` 只取当前层；`:99` 直接列所有合格笔画；`:127` 区域直接自动勾选笔画 | 跳过了用户辨认并选择脏矩阵这一步，列表内容和设计不同 |
| 附近白线和扫选 | §7.2：所选图片关联事件，在光标 48 屏幕像素范围用 2 px 白线显示；12 px 半径扫过整笔命中，再次扫过取消 | `source/CanvasSelectionPreview.cs:109` 只绘制区域轮廓；选择靠范围相交和列表复选框 | 没有把历史笔画与眼前画面联系起来，用户不能按矢量橡皮方式选择事件 |
| 可修改参数及子工具 | §5.5.3、§7.2：历史允许的数字项必须有唯一实时输入框，也允许可路由的子工具 | `source/EditingPanel.cs:102` 汇总当前层所有笔画的数字键；`:174` 只构造 Delete 或 Numbers；`source/Editing/ReconstructionPlanner.cs:32` 对不同 BrushName 直接拒绝；真实适配器 `WindowsCspEditingActions.cs:156` 只覆盖数字 | 不能更换子工具；参数选项没有按所选事件和当前可输入区域过滤，容易出现界面允许选、计划却拒绝的情况 |
| 计划解释 | §7.2：展示段数、每段图片、重放事件数和不可信原因 | `source/EditingPanel.cs:182` 只给涉及图层数和差异图片数；计划器已有 Segments/Reasons（`source/Editing/ReconstructionPlanner.cs:5`）但没有展示 | 用户无法审阅这次将合成什么、重放什么及其原因 |
| 执行进度与取消 | §7.2：显示当前段和笔画，保持可取消 | `source/EditingPanel.cs:192` 执行时隐藏整个面板，“取消操作”也随之不可见；进度回调交给录制窗口（`source/Editing/DemoEditingHost.cs:103`） | 编辑界面没有持续可用的进度和取消入口 |
| 精确撤销的选择解释 | §4.7、§7.2：展示当前图层、基础数量、闭包数量和加入原因，覆盖层显示这些笔画 | 核心 selector 已有 Selected/Reasons；面板 `source/EditingPanel.cs:167` 只有基础/闭包数量文字，没有当前图层名称、逐笔原因或画布白线 | 用户难以确认实际准备恢复的笔画集合 |

“冻结”应区分视觉与进程：架构明确要求 Memoline 覆盖层承接选择输入；冻结截图是本轮用户补充。现有独立 `CspPlacement` 已有 CSP 窗口定位和冻结画面能力（`replayer/strokereplayer/source/CspPlacement.cs:22`、`:142`），可以作为实现依据，但当前宿主项目排除了该文件。不能把固定机械流边界当作冻结 CSP 交互，也不能把会缓存并稍后回放输入的 `configureInputInterception` 当作选择期消费输入的实现。

选择操作不记成绘画有现成依据：`recognizer/Recognizer/src/BehaviorRecognizer/Capture/CspWindowProbe.cs:52` 检查指针下窗口的根窗口所属进程。由 Memoline 覆盖层接收输入时，它不是 CSP 窗口。后续实现仍需验证覆盖层的输入归属及退出恢复。

## 二、复现与索引的确定缺口

### 1. 事件编辑目标仍固定在当前层

架构允许浏览跨层图片并编辑选中的图片事件，精确撤销才必须限定 CSP 当前层。当前面板在 `source/EditingPanel.cs:177` 始终把实际 CurrentLayer 放入请求，计划器 `source/Editing/ReconstructionPlanner.cs:29` 又要求每个修改事件的层等于 request.CurrentLayer。因此当前产品路径不能在跨层图片列表中选择别层事件，也不能提交包含不同层修改事件的请求。

准确地说，计划器可以接受调用方指定的某一个其他目标层；问题是当前 UI 没有这样的图片目标选择，而且数据流没有区分事件编辑目标和精确撤销的当前层约束。不能只增加一个跨层列表就宣称这一功能接通。

### 2. 无关图层的问题会让正常请求也被拒绝

`source/Editing/EditIndex.cs:103` 为每层校验链，再于 `:110` 将全部层问题汇总到 index.Issues。`source/Editing/ReconstructionPlanner.cs:21` 无条件带入所有问题，并在 `:40` 直接拒绝请求，而架构 §4.1 说链失败使“该层”不可精确编辑。

可达例子：A 层作画后通过视图变化普通捕获；转到 B 层继续画；按 Alt+M。`Organizer/canvas layer watcher/source/WatchState.cs:225` 会跳过已经完成捕获且没有新增笔画的 A 层，边界只捕获当前层和未捕获笔画所在层。可是 `EditIndex.cs:250` 对 A 层仍要求末包是 editBoundary，于是 A 的 CaptureMismatch 被提升成全局阻塞，B 的有效请求也无法计划。

这是边界捕获策略、索引校验范围和请求规划之间的契约不一致。本轮只核对了生产代码路径，没有实机复现该例子。

### 3. Undo/Redo 的拒绝窗口过宽

架构 §4.8 规则 U 的时间窗是“最早受影响事件到边界”。`source/Editing/EditIndex.cs:200` 却以该层整个录制的最早接触为起点，将窗口内 undo/redo 在 `:221` 提升为层问题，再被计划器全局拒绝。

因此，早期发生过撤销、随后画了一组新笔画且只编辑后组的请求，也会被旧撤销命令阻塞。当前检查验证的是索引拒绝旧窗口，没有覆盖按具体请求起点判断的行为。

### 4. 精确撤销没有按设计检查撤销容量

架构 §4.7 要求可读容量至少为选笔总数 + 5；读不到时，必须提示用户确认这一条件。当前没有读取容量。`source/EditingPanel.cs:37` 的复选框确认的是“超过 50 笔的闭包扩展数量”，`source/Editing/DemoEditingHost.cs:89` 也只在总数大于 50 时检查这个布尔值。

即使恰好 50 笔，设计仍要求至少 55 步撤销容量。数量确认和容量确认不是同一件事。目前离散注入已经接通，但不能因此保证真实 CSP 的 Ctrl+Z 可以完整逐笔撤销。

### 5. 参考图层笔刷的真实复现未接通

`source/Editing/DemoEditingHost.cs:79` 在真实执行前拒绝读取参考层的笔刷，`source/Editing/WindowsCspEditingActions.cs:148` 再次拒绝。离线引擎可以把参考层像素交给 fake executor，不代表真实适配器能够安装历史参考画面。

这是 [当前整合说明](RGB_LIVE_EDITING_CONTINUATION.md) 已公开的范围限制，与主架构 §4.4、§9.12 的跨层因果要求仍有差距。它应写成“真实执行缺口”，不能写成“该场景已完整支持”。它与用户预留的隐藏图层模块是两个不同职责。

### 6. 边界与事务之间存在没有冻结的绘画间隙

执行前 `source/Editing/DemoEditingHost.cs:74` 会重新建立边界，以纳入用户在面板打开后新增的绘画。但 `Organizer/canvas layer watcher/source/EditingBoundaryCoordinator.cs:37` 在捕获结束时恢复普通捕获，后面还要读取索引、RGB、配置等，直到 `source/Editing/CspEditTransaction.cs:86` 才开始原生事务。

没有核对这段间隙新增的笔输入，也没有在开始重建前证明原层仍等于边界像素。若这时继续画，边界之后的新内容可能未进入复现层。这是代码可达的覆盖风险，不能表述为已经发生的实机故障。用户要求的冻结交互需要同时处理视觉冻结、输入归属和边界连续性。

### 7. 颜色前置要求扩大，状态职责也发生变化

架构 §5.2.3 仅在计划有重放且需要换颜色时要求用户切到 RGB 滑块。`source/Editing/DemoEditingHost.cs:94` 却无条件要求 RGB 页且已选主色，所以纯合成或无需换色的请求也承担这项前置操作。

此外 `recognizer/recorder_integration/pipeline.py:596` 会从新滑块 OCR 生成 core.colorState，标记 source=rgbSlider。这可以支持当前 `ColorRestorer.cs:76` 的颜色确认，因此不能声称隐藏色环时换色必然失败；但它改变了主架构规定的“色环只读识别保持不变、事务以 colorInputState 读回为准”的职责划分。该变化需要在架构与当前说明之间明确统一，实际 OCR 鲁棒性仍属于实机验收。

### 8. 日志没有冻结完整计划，恢复界面也缺失

`source/Editing/DemoEditingHost.cs:106` 序列化给日志的 frozen 对象只有 kind、currentLayer、changes，没有分段、图片、调度事件和不可信原因。`source/Editing/CspEditTransaction.cs:90` 给这个对象记 planFrozen，与 §6.4 的“完整计划及哈希”不一致。

`source/Editing/EditJournal.cs` 已有 Recover API，但编辑入口没有读取未完成事务并提供 §6.5 的“清理／保留现状”选择。涉及原层重新显示或其他隐藏动作的恢复部分属于用户预留模块；发现未完成事务并展示恢复状态仍是宿主的职责。

## 三、已接通的核心路径及其证据边界

以下不是缺失项，也不应因 UI 不合格而否认这些实现：

| 核心路径 | 当前实现证据 | 可得出的结论 |
| --- | --- | --- |
| 机械流边界、完整笔接触、合格事件过滤 | `source/Editing/LiveSessionReader.cs`、`EditIndex.cs`；原生 prepareEditBoundary | 存在完整索引链路，过滤与跨包接触有离线检查 |
| 全分辨率差异、历史倒推与 RGBA 替换 | watcher LayerDiff、`source/Editing/LayerTimeline.cs`、`ImageCompositor.cs` | 合成核心存在，alpha=0 和 C#/Node 对拍有离线证据 |
| 默认合成，不可信整矩阵重放 | `source/Editing/ReconstructionPlanner.cs:101`、`ReconstructionEngine.cs` | 修改事件、因果相交、同色覆盖和共享接触进入相应调度；在线精确差异来自 executor 捕获 |
| 集中重放 | `source/Editing/CspEditTransaction.cs:103` | 中间重放使用 Concentrated |
| 中间捕获、持久检查点、再删除 | `source/Editing/CspEditTransaction.cs:104`、`:109`、`:122` | 生产事务有入库后再删的顺序和身份守卫；实机效果未验收 |
| 保留最终复现层，精确撤销离散重放 | `source/Editing/CspEditTransaction.cs:131`、`:139`、`:143` | 离散重放在最终层，之后捕获并登记 baseline；不是把最终层当临时层删除 |
| 最新 50 笔及叠放闭包 | `source/Editing/PreciseUndoSelector.cs:53`、`:60`、`:67` | 选择算法已接通；覆盖层解释、容量防护、真实撤销次数仍不足 |
| 注入模式、工作层路由、参数逐笔恢复 | `replayer/packetreplayer/source/EventReplayRunner.cs`、`replayer/strokereplayer/source/PenReplayEngine.cs` | 有 fake 注入检查；集中 Up、离散 UpAndLeave 不等于真实 CSP 撤销栈已验证 |
| 追加日志和编辑资产封盘 | `source/Editing/EditJournal.cs`、`BundleArchive.cs`、`BundleArchive.Project.cs` | 资产/字节/尾部恢复检查存在；不能替代交互和真实像素验收 |

最终层不继承原层混合模式、不透明度、剪贴和锁定，是主架构 §1.11 明确允许的 demo 范围，不应把这一点误列为缺陷。

## 四、需要与主架构统一的已公开行为

1. §5.1.2 规定事务物理输入告警后“照常记录”；当前 `recognizer/Recognizer/src/BehaviorRecognizer/Capture/UnifiedInputCapture.cs:149` 告警后返回，事务状态也由 `recognizer/core/memoline/MemolineWriter.cs:126` 转入诊断流。后续整合说明明确描述了这项排除策略。现有实现与主架构文字不同，应统一规则和验证目标，不把后续说明当作未公开故障。
2. 同工程续写保留了历史会话，编辑索引仍只读取活跃会话，当前说明已经公开这一点。主架构没有定义完整的跨会话时间/身份合并契约，本轮不把新增跨会话编辑列为必修任务。
3. 独立快捷键 writer 已有工具和检查，录制设置页的 plan/apply/verify 接线及发布打包仍未完成。它是宿主外部能力接入缺口；其中隐藏图层相关职责按用户最新指示留给独立模块，本轮不改动。

## 五、设计对应的正确用户流程

```text
后台录制
  → Alt+M 自动建立完整编辑边界
  → 验证 CSP 窗口、会话、画布尺寸和当前正逆变换矩阵
  → 冻结 CSP 画面，由 Memoline 覆盖层接收区域选择
  → 直接在当前画布位置框选或圈选

事件编辑：
  → 跨图层的单张脏矩阵列表
  → 选中一张图片，画布显示外框和光标附近白线
  → 用矢量橡皮式扫选命中整条事件
  → 删除或修改允许的参数／子工具

精确撤销：
  → 确认当前图层
  → 选择最新 50 笔及必要闭包，展示白线、数量和原因
  → 核对撤销容量

两条路径：
  → 展示合成／重放的完整计划和拒绝原因
  → 执行前释放选择覆盖层，保持可见进度和取消入口
  → 按设计执行合成与重放，接入用户的隐藏图层模块
  → 保留最终复现层，记录实际结果并恢复正常录制
```

## 六、测试结论应如何表述

上一轮 26 项选择检查和既有 host 集成检查通过，证明了独立预览的几何、列表勾选、部分合成与 fake 事务行为。`checks/EditingSelectionChecks.cs:23`、`:60`、`:86` 检查的正是 PictureBox 选择和区域自动勾事件；其 fake host `:168` 不经过真实计划器。这些检查没有覆盖本架构的原生矩阵、CSP 覆盖层、跨层图片、附近白线和扫选，不能作为阶段 10 完成证据。

本轮未改变代码，因此没有把既有测试重复运行并冒充新的架构验收。真实像素一致性、CSP 图层安装、真实 Ctrl+Z 次数、OCR 输入与物理打断依然需要按 [实机清单](LIVE_CHECKLIST.md) 在工程副本由用户验收。

后续修正应先补主架构对应的失败测试：真实屏幕矩阵与会话校验、Alt+M 状态流、跨层单张图片查询、48 px 白线、12 px 整笔扫选、无关坏层不阻塞有效请求、请求范围内 undo/redo、撤销容量、完整计划冻结与可见取消。隐藏图层模块独立接入，不与这些缺口混为一项。
