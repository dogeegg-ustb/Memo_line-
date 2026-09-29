# CSPevent：点击文字识别与词条数据库

## 后台程序

`CSPevent.exe` 在 Windows 后台监听鼠标左键、右键、中键和侧键按下，**只处理 `CLIPStudioPaint.exe` 的窗口**。监听回调在点击被 CSP 处理前，立即在鼠标位置附近截取一行文字高的小区域（在窗口和虚拟桌面边界内）；宽度按当前语言设置：繁体中文约 6 字、日文约 10 字、英文约 12 字。截图立刻保存为 PNG，事件元数据进入队列。约 120 毫秒后，对同一窗口位置再截一张图，只用于比较高亮变化，不送入 OCR。

OCR 在**单个后台工作线程**上逐张处理点击前截图；上一张识别、匹配和记录完毕，才开始下一张，并确保两次 OCR 启动至少间隔 200 毫秒。连续点击不会丢弃旧事件；队列只保存截图路径，位图不堆在内存。退出时未处理完的 `events/pending` 记录会在下次启动时继续处理。`events/events.jsonl` 按完成顺序保存时间、鼠标按键、点击坐标、截图路径、OCR 原文、所选文字、词库匹配及高亮证据。截图保存在 `events/screenshots`，方便复核。悬浮窗不抢焦点、允许鼠标穿透，约 2.6 秒后消失。

高亮判定比较点击前后截图中的颜色变化，并找出包含点击横坐标的连续变化区；OCR 文字位于该区时优先用于词库匹配。能检测到变化时显示“高亮变化”，不能确认时明确显示“高亮未确认”。菜单点击后立即关闭、未保持在同一窗口的场景无法取得点击后的高亮，仍保留点击前截图和 OCR 结果。

默认按 Windows 界面语言选择词库。系统托盘图标的右键菜单可以手动选择繁体中文、日文或英文，也可以退出程序。自动选择只依据系统界面语言；若 CSP 使用另一种语言，请手动选择。点击区域先做文字检测，再分别走文字或图标识别。文字检测为阳性时，OCR 或词库匹配失败不会将目标改判为图标；没有可靠文字框时才尝试图标。无法确认时显示“未识别”或“候选”。

首次启动会显示持续可见的状态窗口。关闭它只是隐藏到托盘；托盘菜单可以再次打开状态窗口。再次双击 EXE 时会提示“已在后台运行”。运行时需要保留整个 `publish` 文件夹，EXE 旁边的数据库、DLL 和 `models` 目录都是必需文件。

```powershell
dotnet build .\recognizer\CSPevent\CSPevent.csproj -c Release -p:NuGetAudit=false
& '.\recognizer\CSPevent\bin\Release\net8.0-windows10.0.19041.0\win-x64\CSPevent.exe'
```

需要独立的输出目录时执行 `dotnet publish .\recognizer\CSPevent\CSPevent.csproj -c Release -r win-x64 --self-contained false -p:NuGetAudit=false -o .\recognizer\CSPevent\publish`，然后运行 `publish\CSPevent.exe`。

`--self-test <输出文件>` 会检查数据库、三种语言的离线 OCR 推理、文字/图标分流样例、样例词条匹配、截图队列恢复及高亮筛选。程序依赖 .NET 8 Windows Desktop Runtime。文字检测使用 RapidOcrNet 附带的 PP-OCRv5 mobile 模型，OCR 使用 PP-OCRv6 small **单行识别模型**；无需方向分类模型。识别在本机离线运行。模型字典从 ONNX 的 `character` 元数据提取，和识别模型保持一致。长时间运行时的性能和内存仍需现场观察。

截图来源是点击时可见的屏幕像素，因此目标被其他窗口覆盖或菜单在截图前变化时可能漏识别。资源词条标识文字，不直接标识 CSP 命令；遇到同名按钮，悬浮窗只显示文字候选，后续可再结合记录器的点击位置与界面状态。

## 词条数据库

`csp_strings.sqlite3` 从本机 CLIP STUDIO PAINT 安装目录下的 `resource` 语言文件生成。保留每条文字的资源文件名、树节点路径、语言、原文和规范化文本；`source_files` 保存原文件 SHA-256，便于识别安装版本变化。构建过程只读取 CSP 安装文件。

```powershell
python .\recognizer\CSPevent\build_database.py 'D:\CLIP STUDIO 1.5\CLIP STUDIO PAINT' --version 2.0.0
python .\recognizer\CSPevent\lookup.py '笔刷' --language chinese_tc
```

SQLite 表：

- `resource_strings`：完整的可解析、非空翻译词条；主键 `(resource_file, node_path, language)`。即使不同控件文字相同，也保留独立记录。
- `source_files`：语言资源文件及哈希；`string_count` 是该文件解析出的叶节点数（包括空文本）。部分 `other` 文件不是这种文本树，计数为 0，仍保留文件哈希。
- `metadata`：来源安装路径和构建规模。
- `string_keys`：每个资源编号在多少种语言里存在。
- `icon_templates`：纯图标按钮模板数据库，包含 CSP 图层面板工具栏的标准图标（新建栅格/矢量图层、图层组、向下转写/合并、图层蒙版、剪裁、锁定、眼睛可见性、删除等），保留中繁日英四语名称、分类及命令代号。

## 图标匹配与高光确认机制

纯图标按钮（如图层面板工具栏按钮）在点击时无文字供 OCR 提取。系统新增了 2D 高光分析与图标模板匹配引擎：

1. **2D 高光重心与空间锁定**：
   - 比较点击前后截图（差分阈值 $\ge 45$）或检测蓝色/亮灰预高亮。
   - 提取高光连通区域，优先比较蓝色/灰色高光及前后帧变化；鼠标位置用于限定行区域和处理证据接近的候选。
   - 深色界面只有相对相邻行更暗的窄条才作为分隔线，避免把整块背景误当作分隔线。
2. **图标特征匹配**：
   - 按钮切片进行背景均值中值分离，提取前景图标轮廓。
   - 与 `icon_templates` 中的归一化特征进行多尺度相关度比对，结合前景召回率与背景抑制率。
   - 得分至少 0.78 且领先第二名至少 0.12 时才确认目标图标；得分至少 0.64 且领先至少 0.08 时只给出候选。高光用于定位点击区域，并不能证明图标的功能。结果持久化到 `events/events.jsonl`。

```powershell
python .\recognizer\CSPevent\build_icons.py --list
python .\recognizer\CSPevent\lookup.py --icons
```

匹配时先用点击坐标和当前界面状态限定候选，再从点击附近的小截图取得文字片段或图标切片，通过 `normalized_text` 或 `icon_templates` 进行比对。数据库保留这些歧义，供后续事件映射层处理。

该安装包含繁体中文 `chinese_tc`，没有单独的简体中文资源目录。重装或升级 CSP 后重新运行构建脚本；不要直接修改安装资源。

当前构建依据 CSP 2.0.0：11 种语言、353 个资源文件、173,538 条非空语言词条、15,778 个不同资源编号。数据库包含长说明文本；用于点击附近的短词匹配时应先限制候选长度和当前界面范围。
