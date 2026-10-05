# 原始 Recorder 输入回放

回放 `.spool/*.done` 中的画布任务时，保持完整帧路径、屏幕原点、三个 ROI 和 DPI 原样，画布尺寸使用该次运行 `initializationConfiguration.canvasPixelSize` 的值。

`replay-host.ps1 -HostDirectory <宿主目录> -JobFiles <画布.done文件数组> -CanvasWidth <宽> -CanvasHeight <高>` 会通过真实 JSONL 宿主分别初始化和重算同一帧，任意失败都会报错。

添加 `-RecordedSequence` 时，按文件顺序沿用各任务的 `initialize` 值。用于重放同一会话的初始化及后续重算；放大的画布任务需要使用该会话先前冻结的锚点，不能把每帧都重新初始化。

TransformReplay.csproj 是分阶段诊断入口，用于显示工作区、导航器缩略图和纸张边界。它当前针对 2026-10-03 两条 4961×7016 文档记录；生产输入由上述脚本或 Recorder 自身提供。
