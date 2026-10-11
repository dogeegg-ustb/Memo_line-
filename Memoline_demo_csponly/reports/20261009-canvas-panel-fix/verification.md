# Alt+M 画布选择异常与焦点修复

后续单差异包越界修复及当前发布版本见 `../20261010-single-packet-canvas-fix/verification.md`。

实际日志中的 canvasViewState 刷新和原生编辑边界均成功完成。工程历史缓存中的 PNG 路径达到 304 个字符；直接执行检查 EXE 时，长路径回归复现 `System.ArgumentException: Parameter is not valid.`，堆栈指向 `LiveSessionReader.LoadRgba` 中的 `Bitmap(string filename)`。同一 DLL 通过 dotnet 宿主执行则通过，说明原先的 DLL 检查无法覆盖发布 EXE 的原生长路径行为。

PNG 读取现使用托管 FileStream，并在 IHDR 校验后回到流起点交给 GDI+；流保持打开直到像素读取结束。没有改变 RGBA8 校验、尺寸校验、透明像素 RGB 或像素格式。新增回归分别覆盖超长路径下的差异图片与完整 baseline。

焦点回归另行复现：取消画布捕获后，已设置的编辑模式导致面板移除 WS_EX_NOACTIVATE，随后 Activate 抢走 CSP 前台。面板现在仅在已有冻结选区且未忙碌、未选择实时笔刷时允许激活；失败、取消、复现结束后均保留不激活浮窗。捕获失败错误仍可见，重试按钮仍可用，冻结编辑期间仍可键盘输入。

验证使用直接执行的 `checks/bin/Release/net10.0-windows/MemolineDemo.Checks.exe`：

- `--live-reader-only`：24 项通过；修复前在长路径 PNG 回归中抛出上述异常。
- `--selection-only`：画布状态 16、撤销容量 7、宿主契约 6、画布交互 25、笔刷参数 16、面板选择 50 项通过；修复前新增取消焦点回归失败。
- `--read-edit-sources`：实际失败会话 96410b40bd8549039a50b208f3084bfa 的 3 个差异包读取成功，既有工程 1 个历史会话读取成功，组合索引包含 259 笔和 1 张脏矩阵。输入工程和录制均只读。
- 真实 CSP 只读冻结诊断通过：虚拟屏幕 2560×1600，客户区 2560×1527。没有向真实 CSP 注入绘画或回放输入。
- Release 检查构建 0 警告、0 错误；win-x64 自包含单文件发布成功。

已更新根目录 Memoline.exe 与 publish/win-x64/MemolineDemo.exe，两者与阶段发布的 SHA256 一致：`F13D62CB1E6CB1B65C7DE825A5C5D09CD644158AC2099903AD64F8C973658D8D`。

原启动文件保留在 `.stage/canvas-panel-fix/previous-4cac3882ff064fb3a7f3416a01e26883`。运行中的旧进程需要完全退出后重新启动。未重启当前应用，未修改现有录制、失败封盘恢复资料或 Recognizer 校准配置。完整的真实录制初始化到选区交互流程尚未重新操作。
