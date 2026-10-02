# layer stealer

独立 Windows 程序：在 CSP 绘图区中心执行 **按住 Ctrl → 右键按下／松开 → C 按下／松开 → 松开 Ctrl**，读取此次复制产生的系统剪贴板图像，再在自己的窗口里呈现。

## 启动和使用

双击 `Start.cmd`，或运行 `publish/win-x64-fixed/layer stealer.exe`。发布版自包含，无需安装 .NET。
修正版使用新发布目录，避免正在运行的旧版锁住文件。更新后先关闭旧窗口，再从 `Start.cmd` 启动修正版。

1. 打开 CLIP STUDIO PAINT 和画布，选中需要复制的图层。
2. 默认点击位置为 CSP 客户区中央。如果中央落在面板上，点击「校准绘图区中心」，在 CSP 绘图区点一个位置。
3. 点击「复制并呈现」，或在 CSP 中按 **Ctrl + Alt + F8**，松开快捷键后程序会执行脚本并显示图像。
4. 预览支持「适应窗口」、「100%」滚动查看和「置顶预览」；棋盘格表示透明背景。「保存 PNG」导出原始像素尺寸。
5. 已经手动复制时，使用「读取现有剪贴板」或 `Ctrl + V`。`Ctrl + S` 保存 PNG。

Ctrl 在右键与 C 之间始终保持按下，按用户指定的操作顺序执行。「右键后等待」默认 180 ms，可在 CSP 响应较慢时增加。
程序会临时隐藏预览、激活 CSP、验证点击位置，并在操作结束后恢复鼠标原位置（用户已经移动鼠标时不再移动它）。
脚本执行期间按目标 CSP 进程验证前台归属，允许 CSP 自己的绘图窗口和右键弹窗切换句柄。
前台离开目标 CSP 进程时停止，并释放已按下的按键。菜单激活瞬间允许短暂等待，不重新激活主窗口打断菜单。
校准位置按 CSP 客户区的相对坐标保存，支持窗口移动和副屏负坐标；面板布局改变后需要重新校准。
设置保存在程序旁的 `layer-stealer-settings.json`。
操作步骤及失败原因记录在同目录 `layer-stealer.log`，不记录剪贴板图像内容。

复制会覆盖系统剪贴板。程序保持 CSP 本身的图层、对象及选区复制语义，不自动全选、取消选区或向 CSP 粘贴。
CSP 官方说明：无选区时复制整层／图层文件夹；有选区时复制选区内内容；多个选中图层和选中对象也会影响结果。
参见 [Cut/Copy/Paste](https://help.clip-studio.com/en-us/manual_en/270_canvas/Cut__47_Copy__47_Paste.htm) 和 [复制（对象与整层说明）](https://www.clip-studio.com/site/gd/csp/manual/userguide/csp_userguide/500_menu/500_edit_copy.htm)。
本程序将图像粘贴为自己的预览，不会在 CSP 新建图层。

## 图像读取

优先读取剪贴板 `PNG`／`image/png`，其次 `CF_DIBV5`、`CF_DIB`，最后 `CF_BITMAP`。
PNG 和声明 Alpha 掩码的 DIB 保留透明度；普通无 Alpha 的 DIB／Bitmap 作为不透明图像读取，无法恢复源数据里没有的透明度。
支持未压缩 1／4／8／16／24／32 位 DIB、BITFIELDS、上下行方向和行尾对齐。
单张图像最大 6400 万像素，剪贴板数据块最大 256 MB。
格式依据：[Windows 剪贴板格式](https://learn.microsoft.com/en-us/windows/win32/dataxchg/standard-clipboard-formats)、[BITMAPV5HEADER](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-bitmapv5header)。

自动操作只接受剪贴板序列号改变且所有者为目标 CSP 进程的新图像，等待上限 10 秒。
失败时保留上次预览，提示检查图层、选区、中心位置或手动复制。
热键被占用时可使用按钮。CSP 以管理员权限运行时，本程序也需匹配权限，否则 Windows 可能阻止输入。

## Git 来源与独立性

已检查当前各分支、stash、reflog 和 Git 中残留的提交。
找到的是 `943d046`（2026-08-29）中 `screen_canvas_transform/app/Capture/ClipStudioCapture.cs` 的 CSP 窗口定位／PrintWindow 冻结画面流程，
以及 `RoiSelectWindow.cs` 的冻结图像呈现；没有找到提交过的“Ctrl + 右键 + C → 剪贴板图层预览”源码。
因此本程序按用户补充的操作顺序重新实现该功能，未将截图流程当作图层提取。

窗口进程判断参考上述历史代码；Win32 INPUT 布局参考 `cbb42c1` 的 `recognizer/Recognizer/src/BehaviorRecognizer/Capture/LayerSaveGuard.cs`；
中心校准界面参考 `Organizer/dirty matrix/source/UI/ScreenPicker.cs`。
所有必要源码均在本目录，不依赖 recognizer、变换 DLL、OCR、Python 或 AutoHotkey。

## 构建与验证

源码构建需要 .NET SDK 10：

```powershell
dotnet run --project './Organizer/layer stealer/source/LayerStealer.csproj' -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File './Organizer/layer stealer/Build.ps1'
dotnet run --project './Organizer/layer stealer/checks/LayerStealer.Checks.csproj' -c Release
# 渲染空预览、透明图像和最小窗口布局；不操作 CSP，不写系统剪贴板
& './Organizer/layer stealer/publish/win-x64-fixed/layer stealer.exe' --smoke './Organizer/layer stealer/checks/artifacts'
```

检查覆盖图像透明度、PNG 往返、DIB 上下行／调色板／掩码／越界、旧剪贴板与其他进程过滤、负坐标与相对位置，
以及指定输入顺序、右键后前台切到 CSP 弹窗仍完成 C、前台切到其他进程时中止、异常／取消时释放按键。
实际 CSP 的右键操作效果仍取决于当前工具、修饰键设置和画布状态。
