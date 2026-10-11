# Alt+M 入口焦点修复

Alt+M 入口使用不激活的置顶工具浮窗，显示时不调用 Activate，入口和忙碌状态下的 WM_MOUSEACTIVATE 返回 MA_NOACTIVATE，保留鼠标点击。完成冻结后移除不激活属性，保留数字输入的键盘操作。

开始捕获前隐藏面板；宿主恢复 CSP 前台，主动请求当前会话的 clipState、currentLayerState、canvasViewState、shortcuts，再建立原生编辑边界。捕获期间再次 Alt+M 取消任务，不显示新的窗口抢回前台。

新增回归先在旧代码上失败：Opening the Alt+M floating entry must preserve CSP foreground focus。修复后面板选择检查从 32 项增加到 41 项，全部通过；还覆盖隐藏后调用宿主、冻结编辑页恢复键盘属性、延迟捕获取消等行为。

Release 检查构建及 win-x64 单文件发布均为 0 警告、0 错误。完整 MemolineDemo.Checks 通过，包含面板、画布交互、宿主契约、编辑边界、CSP 模拟事务、重建、封盘、续写等现有检查。首次完整检查受临时文件移动权限限制；将测试临时目录放到项目内后，命名管道检查仍被沙箱限制；获准在沙箱外运行同一套离线检查后全部通过。未向真实 CSP 注入输入。

已更新 `D:/Memo_Line/Memo_Line/Memoline_demo_csponly/Memoline.exe` 和 `publish/win-x64/MemolineDemo.exe`，两者与发布构建的 SHA256 均为 `375ACB0A702252F24426F50344B7E95EEE43E7A3D74FC937048E242D3594472F`。原可执行文件保存在 `.stage/foreground-fix-backup-ec18c9a1ad914758a2a55dcfb467715a`。

验证范围：离线窗口属性／消息契约和模拟编辑流程。真实 CSP 前台焦点、截图无遮挡及原生状态刷新仍需实机验证；未改变核心的前台校验或承诺后台截图。
