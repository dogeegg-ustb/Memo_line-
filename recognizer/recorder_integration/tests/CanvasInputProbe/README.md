# 画布核心输入对照（2026-10-01）

本目录是用户授权的离线诊断，未操作运行中的 CSP，未修改附图。输入为原始附件 `codex-clipboard-6b98953a-f6ca-4b08-811b-6d1ff32f2a9f.png`，2152×1345 像素，PNG DPI=143.9926。坐标均为附件本地像素，原点 (0,0)。附件已裁去部分 CSP 窗口，不能等同于完整虚拟桌面截图。

## 已确定的转换问题

旧 `ToArgb32` 使用 `Graphics.DrawImageUnscaled` 将约 144 DPI 的源绘制到默认 96 DPI Bitmap，实际改变了像素。它并不保证这里需要的逐像素复制。

| 检查 | 修前 | 修后 |
| --- | --- | --- |
| 整图像素不同数量 | 2,134,704 | 0 |
| 三裁片重组的工作区像素不同数量 | 1,588,728 | 0 |
| 三裁片重组的导航器像素不同数量 | 111,429 | 0 |
| 三裁片重组的数字区像素不同数量 | 7,547 | 0 |
| CoreNative 对原像素检测工作区 | 成功 | 成功 |
| CoreNative 对转换后像素检测工作区 | status 11 | 成功 |

修后使用像素矩形 `Bitmap.Clone(..., Format32bppArgb)`，重组使用明确的源/目标像素矩形。整图原始 BGRA SHA-256 为 `ED35F768B3F90170E489397284A24478EC75B75FF27726BA8894BD25FA90EF6A`，修后输出与之完全相同。上述比较包含全部 BGRA 字节，未忽略 alpha。

该结论直接适用于本附件。实时 MSS 保存的 PNG 可能没有相同 DPI 元数据，不能据此把所有实时失败都归因于 DPI。

## 初始化逐阶段对照

基准参数：工作区采样 ROI `[0,29,1790,1301]`；导航器粗 ROI `[1810,0,342,438]`；数字 ROI `[1810,365,133,70]`；文档 4961×7016；求解 DPI=96。

| 路径 | 结果 |
| --- | --- |
| 原版 app 完整附件＋ROI | 工作区成功，纠正 ROI `[4,38,1765,1282]`；C-II 缩略图初始化 status 104，两个候选冲突 |
| 修前剥离 core 完整附件＋ROI | 工作区就失败，status 11；来自像素转换改变 |
| 修前剥离 core 三裁片 | 工作区就失败，status 11 |
| 修后剥离 core 完整附件＋ROI | 工作区成功；随后 C-II 与原版一样 status 104 |
| 修后剥离 core 三裁片 | 工作区成功；随后 C-II 与原版一样 status 104 |

C-II 错误是 `AmbiguousCandidates [1813,34,2131,322] vs [1813,327,2152,360]`。本附件已存在导航器红线，不能把这里重新初始化 C-II 的表现直接当作使用已冻结 ROI 的重算表现。

变化参数见额外 JSON：

- `after144.json`：DPI=144，手动冻结缩略图 ROI `[1813,30,339,332]`；初始化 C-II 仍与原版同样失败。
- `after_tight.json`：DPI=144，将初始化导航器粗 ROI 高度改成 326；原版、修后完整图、修后三裁片都通过 C-II/OCR，但导航器画布观察失败，status 105：`navigator canvas aspect mismatch without unique supported edge`。该 ROI 截短了缩略图，因此不作为正式配置建议。

## 已冻结 ROI 的 native 对照

独立诊断跳过 C-II，手动指定缩略图 ROI `[1810,30,342,332]`，将同一附件、同一工作区背景模型和几何关系分别传给：

1. 原版 `CompleteViewportFrame` 的完整附件路径。
2. 原版 `CompleteViewportFrame` 仅保留该缩略图像素的路径。
3. 剥离 core 的 `CompleteViewportFrame` 路径。

导航器 `ObserveCanvas` 明确传入文档尺寸 4961×7016，与真实管线一致；工作区观察与当前管线相同使用默认尺寸参数，随后 `BuildWorkspaceCanvasRelation` 明确传文档尺寸。旋转角度为截图可读的 0°，置信度传 1，仅用于隔离红框补全的图像输入变量，未冒充 OCR 结果。

补齐上述参数后，基准 ROI、144 DPI 及 ROI 左边界移动 3 像素的对照都返回 status 111：`0.2 independent side recovery conflicts with workspace contact`。144 DPI 的导航器白纸观察变为 `[1866,30,233,332]`，不再是缺少文档比例时的 289 像素宽。

因此，这张裁过的附件不能证明“输入完整图一定能让红框补全成功”。它也不能排除真实完整 CSP 窗口含有附件之外的有效红线端点。所有完整输入、窗口原点、ROI、背景模型、观察和 native 返回值均保存在 JSON 中。初始化未成功，所以自动运行的完整 `RecomputeAsync`/精确三 ROI `RecomputeAsync` 分支本次没有执行；不得把 native 手动锚点对照称为已完成真实初始化后的端到端重算测试。

## 证据和复跑

- `before.json`：使用修前 DLL，含完整像素和逐阶段证据。
- `after.json`：使用修后 DLL，含同参数对照。
- `after144.json`、`after_tight.json`：参数变体。
- `host_after.json`：主任务另外保存的实际 Host 对照。

报告同时记录运行时实际加载的 `ScreenCanvasNative.dll` 路径和 SHA-256；本次为 `C621E19CF962EF50D467FFFAF7C82BA55206A8024B16E0D06DDCA72D88C77A2E`。修前 core 为 `0132F1BDD4D10115CD99C2353B3A89636CA299C02BAD60CF450692407AC45C4A`；修后为 `3C4949D40CF77F03101A665C5567888407296D022A8AF2FB1743FC4D3A650B37`。只记录输出目录 DLL 哈希而不检查实际加载模块不足以验证 native 版本，本项目两者都记录。

新核心需先正常编译。Probe 默认引用编译后的新核心；`CoreSnapshot=true` 使用本地保存的旧 DLL。旧 DLL 与生成目录已由 `.gitignore` 忽略，保留在本机供复跑。

```powershell
dotnet restore recognizer/recorder_integration/tests/CanvasInputProbe/CanvasInputProbe.csproj -p:NuGetAudit=false --source C:/Users/dogeegg/.nuget/packages
dotnet build recognizer/recorder_integration/tests/CanvasInputProbe/CanvasInputProbe.csproj -c Release -p:CoreSnapshot=false -o recognizer/recorder_integration/tests/CanvasInputProbe/artifacts/after --no-restore
dotnet recognizer/recorder_integration/tests/CanvasInputProbe/artifacts/after/CanvasInputProbe.dll <原始PNG路径> <输出JSON路径> after
```

可选参数依次是工作区 ROI、导航器粗 ROI、数字 ROI、DPI、手动冻结缩略图 ROI，每个 ROI 使用 `x,y,width,height`。JSON 的 elapsedMs 包括本机初始化、OCR、调试证据写盘等成本；参数变体部分并行运行，不能用这些数值比较正式程序的稳定吞吐量。
