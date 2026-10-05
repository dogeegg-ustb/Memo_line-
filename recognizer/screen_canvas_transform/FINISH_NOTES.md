# 2026-10-03 收尾记录

## 2026-10-04 独立软件：不确定对边退回 0.1

- 两条平行红边的 0.3 补全失败时，逐条尝试现有 0.1 路径；仅剩一条能与工作区裁切对应并完成恢复的红边时，使用这条边补全。两条都能独立恢复但互相冲突时仍报告歧义。0.2 及完整边路径保留原行为。
- 红线中心可能落在纸张检测排除的红色边界像素中；单边回退可在法向上读取固定 2 像素边缘范围，切向纸张跨度保持原值。补全仅导出选定的真实红段，不将推断边伪报成完整观测边，日志标记 `fallback01=1`。
- 与用户截图一致的 18.2% 原始失败帧（capture `9ee56a16bda7454eb3401837dc07a4c4`）由 `no group completed via pattern` 改为成功的 `pattern=1`；补出导航器视口 566.20 × 411.47 px。
- 新增四个方向 × 三种线宽的 12 个夹具，以及两条有接触证据但互相冲突的拒绝用例；五组原生测试和 33 项 C# 测试通过。
- 已更新上面运行路径对应的独立应用和 Native DLL。本次修改限于独立软件，Recognizer 核心仍使用此前版本。

原始帧可在保存为 BGRA 后用 `native/build_src/viewport_robustness_tests.exe <bgra> <width> <height> <workspace L T R B> <navigator L T R B> <canvas width height> <rotation> [scale percent]` 回放；传入缩放时继续检查变换矩阵。

## 运行

主目录已重新构建 Release 版本，可直接运行：

`app/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/ScreenCanvasTransform.exe`

请保留同目录的 DLL 和其他依赖文件。该构建使用本机 .NET 8 Windows Desktop Runtime，不是独立发布包。

## 本轮修改

- 将 Claude 工作目录 `fix/canvas-origin-precision` 的最后一批原生修复同步回主目录：导航器纸张边界排除视图边框、红线中心到工作区边界的半像素修正，以及纸张旁侧红线的补全和工作区一致性检查。
- 修复 L 形红框共享拐角的端点丢失：直线提取会因另一条红线使拐角轮廓变宽而提前停止；长度恢复现在使用另一条实测直线的位置补回已确认的近端拐角。未放宽测试容差，增加四个朝向的回归测试。
- 修正旋转测试夹具：完整重叠场景显式提供重叠比例；单边测试的红线位置与输入的工作区纸张偏移保持一致，并验证原点。
- `test-native.ps1` 覆盖全部五组原生测试，增量编译检查头文件更新时间。
- 三个修改的原生源文件同步到 `recognizer_core/screen_canvas_transform`，原生 DLL 同步并验证一致；重新构建 TransformHost。

## 验证

- `powershell -NoProfile -ExecutionPolicy Bypass -File recognizer/screen_canvas_transform/build.ps1`：Release 应用构建成功；合同、旋转、工作区、导航器缩略图、视口鲁棒性五组测试全部通过。
- `powershell -NoProfile -ExecutionPolicy Bypass -File recognizer/screen_canvas_transform/test-native.ps1 -Incremental`：五组全部通过。
- `dotnet test recognizer/screen_canvas_transform/tests/ScreenCanvasTransform.Tests/ScreenCanvasTransform.Tests.csproj -c Release -p:Platform=x64 --no-restore`：33/33 通过。
- `dotnet build recognizer/recorder_integration/transform_host/TransformHost.csproj -c Release --no-restore`：成功，0 错误；NuGet 漏洞数据源因网络不可用产生两个 NU1900 警告。
- 主应用原生 DLL、应用输出 DLL、核心 DLL、TransformHost 输出 DLL 的 SHA-256 一致。
- L 形夹具恢复尺寸为 90.000000 × 79.003098 px（独立真值 90 × 80 px，原容差小于 1 px）；旋转回归最大几何误差 0.266434 px，最大屏幕变换误差 3.462655 px。这些数值属于测试夹具，不能代表所有真实截图。

## 验证边界

本轮未执行 CSP 实机交互验收，也未重新执行 Claude 外部临时目录中的整批截图扫描。粘贴记录提到旋转读数 `-10.6` 被 OCR 读为 `10.6` 的个例；本轮没有针对该原始截图验证或修改 OCR 图像识别。2026-09-22 的 precision_report.md 是历史报告，本轮结果以本记录为准。

Recorder 已有发布目录未重新发布；上述核心修改已进入源码、捆绑原生 DLL 和 TransformHost 的 Release 构建输出。
