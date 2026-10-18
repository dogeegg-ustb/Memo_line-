# CSP 浮动面板收纳

把指定的 CSP 浮动面板收进用户看不见的虚拟屏。收纳后，其它程序可以通过本机接口按需读取这些面板的画面和名称。面板放在虚拟屏上时，CSP 会照常重绘它，所以截到的始终是最新画面。

用户这边有两种收纳方式：把面板的角拖到屏幕的角；鼠标停在屏幕左右边缘时会弹出抽拉按键，点开后可以把面板带回来。

Memoline 启动时自动以嵌入模式运行本程序（不显示控制窗口，只有抽拉按键和接口），退出时让它取回全部面板。收纳过的面板会被记住，下次启动 Memoline（或 CSP 之后才打开那个面板）时自动回到虚拟屏。录制器在撤销/重做后通过接口读取历史记录面板，所以历史面板应该收纳在这里。

## 安装虚拟显示驱动（每台机器一次，需要管理员权限）

驱动用上游原版 [Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) 25.7.23（MIT 许可，由 SignPath Foundation 签名），不依赖 Sunshine。

```powershell
# 在管理员 PowerShell 里运行
powershell -ExecutionPolicy Bypass -File recognizer\virtual_panel_probe\vdd\install-vdd.ps1
# 可选参数：-PackageZip <本地驱动包>  -Resolutions 1536x960,1920x1080,…  -RefreshRate 60  -LogPath <日志>
# 已装好后改配置：加 -Reconfigure，会改写配置并重启虚拟显示设备
```

安装脚本做了这些事：
- **下载与校验**：下载固定版本的驱动包，或者用 `-PackageZip` 指定的本地包；校验 SHA-256，确认 `.cat` 和 `.dll` 都是 SignPath Foundation 签名。
- **写配置**：在 `%ProgramData%\MemoLine\VirtualDisplayDriver\vdd_settings.xml` 写入 **1 块**虚拟屏和一组分辨率档位（1024×768 到 3840×2400 共 11 档，60Hz），并把目录登记到驱动读取的 `HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver\VDDPATH`。
- **装驱动**：用 SetupAPI 创建 `Root\MttVDD` 设备并安装驱动，C# 代码在 `vdd/VddSetup.cs`，不需要 devcon 或 nefcon。

这台机器上如果已经装有同一个驱动，脚本不重复安装；已经登记过配置目录时，沿用原来的目录和配置。`-Reconfigure` 只改写由本脚本创建的配置，别人的配置要加 `-Force`。安装时 Windows 可能会问是否安装 SignPath Foundation 发布的设备软件，选"安装"。

卸载运行 `vdd\uninstall-vdd.ps1`，会移除设备和驱动包。配置目录和注册表登记只有当初是安装脚本创建的才会删除。

只配 1 块虚拟屏是实测的结果。2026-10-17 在开发机上，同时接入两块 2560×1440 的虚拟屏时，桌面合成每隔约 1 秒就停顿近 1 秒，帧率从 165 掉到 10 左右，整个系统卡得没法用。只接一块时完全正常。

## 虚拟屏什么时候接入

驱动一加载就建好虚拟屏，运行中没有增删的命令；Windows 还可能按 Win+P 的投影方式，自己把它接成主屏的复制。复制屏和真屏一样耗资源，而且壁纸软件、任务栏等会在每块屏上都画一份。所以程序不跟驱动通信，只调整当前用户的显示配置，让虚拟屏只在收纳面板时存在。这些操作用的是 `SetDisplayConfig` 和 `ChangeDisplaySettingsEx`，不需要管理员权限：

- **平时**：虚拟屏是断开的。程序启动时如果发现它被 Windows 接上了（比如开机时接成了复制屏），而且没有收纳着的面板，就把它断开。
- **收纳时**：第一个面板要收纳时才接入成扩展屏，挂在主屏右下角，只有一个角点相接，所以光标进不去；缩放设成和主屏一致，CSP 就不会重新排版面板。分辨率取驱动档位里放得下的最小一档：宽度按收纳的最宽面板，高度按历史面板一次列出全部撤销步数所需的长度（见下文）。面板都靠左排成一列，排不下的从下往上叠放；被遮住的面板照常重绘，截图也是完整的。
- **放不下时**：再收纳的面板放不下，才调大一档。
- **取回后**：最后一个收纳的面板被取回 5 秒后断开（`detachDelaySeconds`）。
- **退出时**：先取回全部面板，再断开虚拟屏。

缩放有个限制：Windows 只在缩放后的桌面不小于 800×600 时才提供该缩放比例。主屏是 150% 时，虚拟屏至少要 1536×960。

性能实测（开发机，主屏 165Hz）：
- **接入或调大分辨率时**：桌面会顿约 1 秒，这是 Windows 切换显示配置本身造成的；之后 2 秒内还有一两次 20 到 40 ms 的小停顿。
- **稳定后**：上面收纳着 2 个不停刷新的面板，另开一个进程测桌面帧率，仍是 165，最长一帧 13 ms，没有长帧，和不开程序时一样。

如果不想在收纳时顿那一下，可以把 `detachDelaySeconds` 设为 `null`，第一次接入后就一直保留。

每次改分辨率，不管调大调小，桌面都会停顿 1.15 到 1.2 秒；而面积几乎不花钱。实测虚拟屏从 1536×960 换成 3840×2400（面积 6.25 倍），上面同样有 2 个在刷新的面板，GPU 总共只多了约 1.3%。DWM 只重画有变化的区域，空着的地方不耗资源。所以只在放不下时才调大，不会缩小；要一开始就留出更大的空间，设 `virtualMinimumSize`。

### 历史面板的长度

CSP 最多保留"撤销次数"那么多步（首选项 > 性能，存在用户目录 `Preference/Config.sqlite` 的 `Performance.UndoCount`，默认 30、最多 200）。历史面板收纳后被拉到能同时列出文档打开状态和全部撤销步的高度：首行位置 + (撤销次数 + 1) × 行距 + 半行。首行位置和行距从面板截图量出（与 CSP 的界面缩放有关，150% 时为 33 和 28 像素）；量不出时按显示器 DPI 从 150% 的读数换算。撤销次数 200、150% 缩放时约 5675 像素高。取回时面板恢复原来的尺寸。

这比任何横向档位都高，所以安装脚本的默认档位里有三档竖长分辨率（1280×4096、1536×6144、1920×7680）。早先装好的驱动需要用管理员身份运行一次 `install-vdd.ps1 -Reconfigure` 才有这些档位；没有时用最高的一档，历史面板只拉到屏幕高度，最早的几步要靠滚动才能看到。

## 只让 CSP 用这块屏

Windows 没有"某块显示器只允许某些程序画"的设置或 API：所有显示器拼成一整张桌面，任何程序都能把窗口放在任意位置。动态壁纸画在横跨所有显示器的桌面背景层上，系统也没法按显示器排除。`guard.py` 用两种办法尽量接近这个效果：

- **窗口巡检**：每 0.4 秒检查一次，不在白名单（`virtualScreenAllowedProcesses`，默认只有 CSP）里的程序，如果有窗口的中心落在虚拟屏上，就把这个窗口挪回主屏，保持它相对屏幕左上角的位置。同一个窗口最多挪 3 次，免得和非要待在那里的程序来回抢。任务栏和桌面这类系统外壳窗口不动，它们是静态的，几乎不耗资源。这一项不依赖其它软件的配合。
- **最大化底板**（`wallpaperBackdrop`）：虚拟屏接入期间，在所有窗口最底下铺一块带"最大化"标记的黑色窗口，CSP 面板压在它上面。Wallpaper Engine 一类壁纸软件看到这块屏被最大化窗口盖住，就会暂停在这块屏上的渲染。实测 Wallpaper Engine 停了：没有底板时它在虚拟屏上约占 2.6% 到 2.9% GPU，有底板后低于统计阈值。

  这一项**依赖壁纸软件自己的"被最大化窗口盖住就暂停"逻辑**。本机的 Wallpaper Engine 设置的是"其它程序最大化时暂停"；如果设成"继续运行"，或者换成没有这种逻辑的软件，底板就不起作用。只用普通的全屏黑窗口盖住（不带最大化标记）也没用：Wallpaper Engine 被挡住后照样渲染，照样耗 GPU。

  不依赖底板的办法，是在壁纸软件里把这块虚拟屏设成不显示壁纸；它按显示器 ID 记住设置，设一次就行。

## 运行

```powershell
py -3 recognizer/virtual_panel_probe/panel_host.py                # 控制窗口和接口（或双击 start.bat）
py -3 recognizer/virtual_panel_probe/panel_host.py --embedded --parent-pid 1234   # Memoline 的启动方式：无控制窗口，进程 1234 退出时取回面板并关闭
py -3 recognizer/virtual_panel_probe/panel_host.py --diagnose     # 只读：显示器、虚拟屏、CSP 窗口、工作区面板
py -3 recognizer/virtual_panel_probe/panel_host.py --restore-all  # 异常退出后：取回面板并断开虚拟屏
py -3 recognizer/virtual_panel_probe/panel_host.py --query listWindows
py -3 recognizer/virtual_panel_probe/panel_host.py --query "{\"command\":\"captureWindow\",\"kind\":\"历史记录\"}" --out history.png
```

依赖：Python 3.10+、Pillow，以及同仓库 `recognizer/` 下的 `csp_workspace_overlay`、`csp_workspace_layout`、`csp_panel_validator`。同一时间只能运行一个实例，第二个实例发现接口管道被占用就会退出。

## 收纳与取回

- **角对角**：CSP 会把拖到屏幕左右边缘的面板吸进停靠栏，所以收纳手势是"窗口的角对准屏幕的角"。窗口的角在工作区对应的角 32 px 内，或者超出屏幕，都算。左上、左下角收到**左侧**抽屉，右上、右下角收到**右侧**抽屉；所有面板都在同一块虚拟屏上，"侧"只决定它出现在哪个抽屉里。只贴左右边缘不会收纳。松手后 150 ms 到 1.5 s 内会检查几次，因为 CSP 撕出的面板要松手后几百毫秒才出现。
- **只收纳认得出的面板**：拖到角落的窗口必须能从工作区配置认出是哪个面板（或在控制窗口里指定过名称），认不出的不收纳。
- **控制窗口**：选中一行，点"收纳到左侧"、"收纳到右侧"、"取回"或"全部取回"。
- **接口**：`stashWindow` 和 `restoreWindow`，见下文。
- **抽拉按键**：鼠标在主屏左或右边缘停约 0.15 秒，并且那一侧有收纳的面板时，边缘会弹出按键。按键上显示面板数量，会跟着光标上下移动。点开抽屉能看到这一侧每个面板的实时缩略图，每秒刷新一次。点选一个面板，它会回到这一侧的边缘，并避开角落区，免得下次小幅拖动又被收纳。按键和抽屉都不抢 CSP 的焦点。
- **取回与退出**：取回会把面板放回拖动前的位置。退出程序时先取回全部面板，再断开虚拟屏。收纳记录同时写在 `state.json`；程序异常退出后重新启动会接管这些记录，也可以用 `--restore-all` 直接恢复。
- **记住收纳的面板**：`state.json` 的 `remembered` 按面板 ID（同一窗口里的全部标签）记下收纳过的面板和抽屉侧。程序退出时取回的面板仍记着，下次启动、或 CSP 之后才打开这个面板时，自动收纳回去（每个窗口自动收纳一次，失败不反复重试）。用户自己取回（抽屉、"取回"按钮、`restoreWindow`、"全部取回"）的面板不再记着。

面板名称只来自 CSP 的工作区配置（`Placement/dock`），按浮动窗口保存的位置或窗口标题匹配。CSP 运行中新撕出的窗口在配置保存前认不出名字，可以在控制窗口里用"指定名称"手动选择。

鼠标按键用 `GetAsyncKeyState` 轮询，不装低级鼠标钩子。这样即使 Python 在忙（比如在编码截图），也不会拖慢 CSP 的输入。

## 接口

- **管道**：`\\.\pipe\MemoLine.PanelStash`，名称可在 `settings.json` 的 `pipeName` 修改。只有当前 Windows 用户能连接，拒绝远程连接。
- **格式**：字节流，UTF-8，不带 BOM。客户端每行写一个 JSON 请求，每个请求读回一行 JSON 响应。一个连接上可以连续发任意多个请求。
- **请求**：`{"command": "...", "requestId": 任意值（可选，原样返回）, ...}`，不认识的字段会报错。
- **响应**：成功是 `{"success": true, ...}`，失败是 `{"success": false, "error": "原因"}`。

选择窗口时，`hwnd` 和 `kind` 必须且只能给一个：
- `hwnd`：整数，取自 `listWindows`。
- `kind`：工作区里的面板 ID（如 `palettekindhistory`）或显示名称（如 `历史记录`）。一个浮动窗口里有多个标签页时，优先选当前显示该面板的窗口。

| 命令 | 参数 | 返回 |
|---|---|---|
| `getStatus` | 无 | `schemaVersion`、`pipeName`、`processId`、`csp{running, mainHwnd, processId}`、`virtualDisplay{attached, changing, error, monitors[{device, rect, dpi}]}`、`windows`、`stashed`、`remembered[{kinds, side}]` |
| `listWindows` | `stashedOnly`（可选，布尔） | `windows[]`：窗口对象 |
| `captureWindow` | `hwnd` 或 `kind`；`format`：`png`（默认）或 `bgrx` | `window`、`format`、`width`、`height`、`rect`、`capturedAt`、`data` |
| `stashWindow` | `hwnd` 或 `kind`；`side`：`left` / `right`（可选，不给时选离窗口较近的一侧） | `window`；需要接入或调大虚拟屏时，约 1 到 2 秒后才返回 |
| `restoreWindow` | `hwnd` 或 `kind` | `window`；窗口没收纳、也不在虚拟屏上时报错。之后不再自动收纳它 |
| `shutdown` | 无 | `closing: true`；随后取回全部面板（仍记着）、断开虚拟屏并退出。Memoline 退出时调用 |

`captureWindow` 的返回字段说明：
- **`data`**：Base64 编码。`png` 是 PNG 文件；`bgrx` 是从上到下的原始像素，每像素 4 字节，第 4 字节为 0，行宽 `width*4`。
- **`rect`**：截图对应的屏幕矩形。
- **`capturedAt`**：截图时间，Unix 秒。

每次请求都在当时重新截图（`PrintWindow`），面板被遮住或在虚拟屏上都能截到。

窗口对象的字段：

| 字段 | 含义 |
|---|---|
| `hwnd` | 窗口句柄（整数） |
| `name` | 显示名称，如 `图层 | 图层属性`；认不出时用窗口标题，或"未命名面板" |
| `kinds`、`currentKind` | 窗口里的全部面板 ID，以及工作区保存时显示的那一个 |
| `nameSource` | 名称来源：工作区浮动窗口 N、窗口标题、手动指定，或未识别 |
| `title`、`className` | 窗口标题和类名 |
| `stashed`、`side` | 是否被本程序收纳，以及在哪一侧的抽屉 |
| `onVirtualScreen` | 窗口中心是否在虚拟屏上 |
| `rect`、`width`、`height`、`dpi` | 可见边框（物理像素）及所在显示器的 DPI |

C# 调用示例：

```csharp
using var pipe = new NamedPipeClientStream(".", "MemoLine.PanelStash", PipeDirection.InOut);
pipe.Connect(2000);
using var reader = new StreamReader(pipe, new UTF8Encoding(false));
using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
writer.WriteLine("""{"command":"captureWindow","kind":"palettekindhistory"}""");
using var response = JsonDocument.Parse(reader.ReadLine()!);
byte[] png = Convert.FromBase64String(response.RootElement.GetProperty("data").GetString()!);
```

## 设置（`settings.json`）

| 键 | 默认 | 含义 |
|---|---|---|
| `virtualHardwareIds` | `["Root\\MttVDD"]` | 虚拟显示器所属设备的硬件 ID |
| `matchMainScale` | `true` | 把虚拟屏缩放设成与主屏一致；CSP 按显示器 DPI 缩放，缩放一致时面板布局和截图都与在主屏上相同 |
| `detachOnExit` | `true` | 退出时断开虚拟屏 |
| `detachDelaySeconds` | `5` | 最后一个收纳的面板取回后多久断开虚拟屏；`null` 表示一直保留 |
| `virtualMinimumSize` | `null` | 虚拟屏的最小分辨率，如 `[1920, 1200]`；`null` 表示只按面板需要选档位 |
| `historyFullHeight` | `true` | 把收纳的历史面板拉到能列出全部撤销步数的高度 |
| `virtualScreenAllowedProcesses` | CSP 的三种 exe 名 | 允许把窗口放在虚拟屏上的程序，其它程序的窗口会被挪回主屏 |
| `wallpaperBackdrop` | `true` | 虚拟屏接入期间铺最大化底板，让壁纸软件在这块屏上暂停 |
| `pipeName` | `MemoLine.PanelStash` | 接口管道名 |
| `cornerThresholdPx` | `32` | 角落判定距离 |
| `edgeDrawer`、`edgeHoverPx`、`edgeDwellMs` | `true`、`2`、`150` | 抽拉按键开关、边缘宽度、停留时间 |
| `stashGapPx` | `8` | 虚拟屏上面板之间的间距 |

## 文件

| 文件 | 作用 |
|---|---|
| `panel_host.py` | 主程序：窗口跟踪、收纳手势、按需接入虚拟屏、控制窗口和接口命令 |
| `api.py` | 命名管道服务端，以及测试和 `--query` 用的客户端 |
| `display_config.py` | Windows 显示拓扑：识别虚拟显示器、接入、断开、分辨率、摆放和缩放 |
| `virtual_display.py` | 显示器列表；按面板选分辨率、接入和断开虚拟屏 |
| `stash.py` | 角落判定、虚拟屏上的排布与"放不放得下"、取回位置，以及跨启动记住的面板 |
| `history_size.py` | CSP 的撤销次数，以及历史面板列出全部步数所需的高度 |
| `edge_drawer.py` | 抽拉按键和抽屉（Tkinter） |
| `capture.py` | 单个窗口截图 |
| `guard.py` | 虚拟屏的窗口巡检和最大化底板 |
| `csp_windows.py`、`workspace.py` | CSP 顶层窗口，以及工作区里的面板名称 |
| `vdd/` | 驱动安装和卸载脚本、上游许可证 |

## 测试

```powershell
py -3 -m unittest discover -s recognizer/virtual_panel_probe/tests
```

单元测试共 54 项，覆盖：历史面板按撤销次数拉高、不超过虚拟屏、取回时恢复尺寸，重启后按记忆自动收纳、用户取回后不再自动收纳、按住按键或 CSP 有对话框时不动、认不出的面板不收纳，以及角落判定、排布、"放不放得下"、按面板选分辨率和缩放限制、取回位置、显示配置结构体与模式表压缩、命名管道的往返与错误处理（含 3 MB 响应）、接口命令和请求校验、按需接入/调大/空闲断开的流程，以及巡检时哪些窗口要挪、挪到哪里。

2026-10-17 在本机做了端到端测试：一个假 CSP 进程配合真实驱动，28 项全部通过。覆盖内容如下：
- **启动**：不接入虚拟屏。
- **第一次收纳**：才接入，直接用与主屏同尺寸的 2560×1600，缩放与主屏一致，只在角点接触。
- **放得下时**：不改分辨率，再收纳一个 450×1000 的高面板也不用调。
- **只让 CSP 用这块屏**：虚拟屏上铺了最大化底板，Wallpaper Engine 不在上面渲染；把记事本挪到虚拟屏上，1.5 秒内被挪回主屏。
- **全部取回后**：5 秒内自动断开；再次收纳会重新接入；退出时取回面板并断开虚拟屏。
- **接口**：`getStatus`、`listWindows`、`stashWindow`、`restoreWindow`。在虚拟屏上连续截两次图，画面不同，说明面板在持续重绘。
- **收纳手势**：角对角拖放能收纳，只贴边缘不收纳。
- **抽拉按键**：按键弹出、抽屉缩略图、点选带回。

这个测试没有注入真实的鼠标按键。另外用真实光标确认过：虚拟屏接入后，主屏右边缘停在 x=2559，下边缘停在 y=1599，光标进不了虚拟屏。帧率测量见上文"虚拟屏什么时候接入"。

## 开源组件

| 部分 | 来源 | 许可 |
|---|---|---|
| 虚拟显示驱动 | [Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) 25.7.23，原版未修改，SignPath Foundation 签名 | MIT（`vdd/LICENSE-VirtualDisplayDriver.txt`） |
| 图像 | Pillow | MIT-CMU |
| 界面 | Tkinter | PSF |

## 已知限制

- **没用真 CSP 验证过**：本模块还没在真 CSP 上做过整套流程，验证用的是假 CSP 进程。还要确认两件事：CSP 的面板在虚拟屏上是否照常刷新；CSP 重新排布面板时会不会把它拉回主屏。
- **接入时的停顿**：每次接入或调大分辨率，桌面会顿约 1 秒，见上文。
- **CSP 在面板收纳期间退出**：CSP 关闭时会把面板位置存进工作区配置，也就是虚拟屏上的坐标。下次 CSP 启动时虚拟屏是断开的，CSP 怎么处理这些坐标还没验证。目前建议关 CSP 前先"全部取回"。
- **运行中改了投影方式**：程序运行时如果按 Win+P 改成"复制"，虚拟屏会变成复制屏，收纳着的面板会被 Windows 挪回主屏，程序不会自动恢复。没有收纳的面板时，这块复制屏会在 5 秒后被断开。
- **Sunshine-Foundation**：它的 `install-vdd.bat` 安装或升级时会删除 `Root\MttVDD` 设备，之后需要重新运行 `install-vdd.ps1`。
- **拖动时经过停靠栏**：拖向屏幕角时，如果光标经过右侧（或左侧）停靠栏上方，CSP 仍可能显示停靠提示并把面板吸进去，此时就没有浮动窗口可收纳了。抓住面板标题栏靠外的一端拖，光标离停靠栏远一些。
- **数位笔**：按键状态来自 `GetAsyncKeyState`。数位笔的接触被驱动转成鼠标消息时能检测到；只发 `WM_POINTER` 的驱动检测不到。
- **改分辨率档位**：可选的分辨率在安装时决定。之后要改，用 `install-vdd.ps1 -Reconfigure -Resolutions …` 重新配置，需要管理员权限。
