# CSP 本地控制后端

## 结构

- `csp_file_creator.py`：调试前端，提供可搜索的命令面板。
- `csp_backend.py`：本机后台服务，负责快捷键桥接和文件操作。
- `csp_commands.json`：命令目录；可按 CSP 手册继续扩展。
- 服务仅监听 `127.0.0.1:38765`。其他本机程序可通过 HTTP 调用。
- API 令牌位于 `%USERPROFILE%\Documents\Codex\CSPBackend\csp_backend_settings.json`。命令请求通过 `X-CSP-Token` 请求头传入。

## API

### 检查服务和列出命令

- `GET http://127.0.0.1:38765/v1/health`
- `GET http://127.0.0.1:38765/v1/commands`
- `GET http://127.0.0.1:38765/v1/settings`（需要令牌）

### 执行命令

`POST http://127.0.0.1:38765/v1/command`

请求头：`X-CSP-Token: <csp_backend_settings.json 中的 api_token>`

新建图层、向下合并、剪裁到下方图层：

```json
{"command":"new_raster_layer"}
{"command":"merge_with_below"}
{"command":"clip_to_layer_below"}
```

为没有默认快捷键的蒙版命令设置覆盖快捷键（需先在 CSP 的快捷键设置里绑定同一个快捷键）：

```json
{"command":"create_layer_mask","params":{"shortcut":"Ctrl+Alt+M"}}
```

新建文件夹：

```json
{"command":"create_folder","params":{"name":"草稿"}}
```

新建可由 CSP 打开的空白工程：

```json
{"command":"create_next_file"}
```

首次运行时，调试前端会让系统保存文件对话框确定保存目录和文件名前缀。之后会自动创建递增名称的 `.clip` 文件。文件由 CSP 原生保存的 1600×1200 白底空白画布模板复制得到，保留可打开的画布结构。模板文件为 `csp_blank_template.clip`，随附许可证见 `csp_blank_template_LICENSE.txt`。更换模板时，应使用 CSP 保存的有效空白 `.clip` 文件；不能使用零字节文件。

向刚才选中的 CSP 数值框写入明确数值：

```json
{"command":"set_numeric_value","params":{"parameter_id":"brush_opacity","value":"65"}}
```

先在 CSP 的“工具属性”等调板里点击目标参数显示的数字，再从面板或其他程序调用此命令。后端会记住最近选中的数值控件、切回 CSP、重新点击该位置并确认数值编辑框打开，然后发送新值。实测 CSP 的数值编辑框使用自绘窗口，没有标准系统文本光标，后端会识别该窗口类名。画笔预览和画笔尺寸预设圆点不是数值输入处。若输入框未重新打开，将拒绝发送并返回错误。数值参数目录见 `csp_numeric_parameters.json`。

PowerShell 示例：

```powershell
$settings = Get-Content -Raw "$env:USERPROFILE\Documents\Codex\CSPBackend\csp_backend_settings.json" | ConvertFrom-Json
$headers = @{ 'X-CSP-Token' = $settings.api_token }
$body = @{ command = 'merge_with_below' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:38765/v1/command' -Headers $headers -ContentType 'application/json' -Body $body
```

## 当前命令目录

前端按官方手册主题提供可搜索目录：文件与画布、编辑、图层、图层蒙版、选区、工具、画笔控制、滤镜与校正、尺规与网格、文字与漫画、漫画与作品管理、动画与时间轴、3D 与素材、导出、颜色、窗口与调板、自动化、设置和帮助。包含你提出的“向下合并”和“剪裁到下方图层”，以及蒙版创建/应用/删除/反转/链接、空白和隐藏图层清理、滤镜、校正层、时间轴、自动动作和批处理入口等条目。

画笔控制栏提供尺寸预设逐档增减、工具不透明度增减、笔尖浓度增减、上一/下一工具切换。单支画笔可使用“选择指定画笔”：先在 CSP 的“快捷键设置 > 工具”中为该子工具设置快捷键，再把该键（支持组合键）填入命令面板并执行。尺寸和浓度按 CSP 的当前工具/预设状态逐档变化；后端不读取 CSP 当前数值，不能保证某次增减后达到指定绝对百分比或物理尺寸。

“数值设置”页列出画笔、图层、画布/图像、变换、色调校正、滤镜、文字、尺规/网格、动画和导出中的常见数值参数，并提供“其他数值输入框”通用项。先在面板填值，再在 CSP 点击“工具属性”内对应的数值，最后点“写入”。后台会重开刚才的数值编辑框并发送；若无法识别则拒绝发送并在面板底部显示结果。它不会替用户打开各类对话框或定位各自的控件，因为 CSP 没有公开一套统一的数值参数 API。参数目录是常见入口清单，通用项可用于其余数值字段。

无默认快捷键的条目也会列出。使用前先在 CSP 的快捷键设置中给对应菜单命令或工具分配快捷键，然后在前端选中该条目、填快捷键并按“保存快捷键”。设置会保存到本地后端；调用时由后端向 CSP 前台窗口发送该按键。原生文件保存对话框仍可能按 CSP 行为出现。

## 调用范围

目录覆盖可从手册识别并能映射到快捷键/工具入口的操作类别，不代表本程序能直接操作 CSP 的每一项内部功能。滤镜强度、选区、图层目标、画布拖拽、3D 姿势/相机、素材搜索下载、时间轴帧等需要具体参数或指针轨迹的操作仍要在 CSP 内完成；菜单命令在不同版本、语言、许可档位和用户快捷键配置下可能不同。某个入口如果没有可配置的 CSP 快捷键，当前桥接无法调用它。

后端不解析或修改 CSP 的私有画布结构，也不是绘图引擎或插件 API；它是供本机其他程序调用的 loopback HTTP 命令服务。界面操作要求 CSP 窗口可见且可切换到前台。自动递增文件功能会完整复制一个 CSP 保存的空白工程；这与在 CSP 中逐次调用“新建画布”不同，创建出的文件使用同一画布尺寸与初始图层设置。

## 官方参考

- [CSP 5.0 用户指南](https://help.clip-studio.com/en-us/)
- [菜单快捷键](https://help.clip-studio.com/en-us/manual_en/780_shortcuts/Menu_Shortcuts.htm)
- [工具快捷键](https://help.clip-studio.com/en-us/manual_en/780_shortcuts/Tool_Shortcuts.htm)
- [图层操作](https://help.clip-studio.com/en-us/manual_en/180_layers/Basic_operations.htm)
- [图层的其他设置（含剪裁）](https://help.clip-studio.com/en-us/manual_en/180_layers/Other_layer_settings.htm)
- [图层蒙版](https://help.clip-studio.com/en-us/manual_en/180_layers/Layer_masks.htm)
- [滤镜](https://help.clip-studio.com/en-us/manual_en/390_filters/Filters.htm)
- [色调校正效果](https://help.clip-studio.com/en-us/manual_en/390_filters/Tonal_Correction_Effects.htm)
- [动画文件夹与赛璐璐](https://help.clip-studio.com/en-us/manual_en/600_animation/Animation_folders_and_cels.htm)
- [关键帧](https://help.clip-studio.com/en-us/manual_en/600_animation/Using_keyframes.htm)
- [素材用法](https://help.clip-studio.com/en-us/manual_en/630_material/How_to_use_materials.htm)
- [快捷键设置](https://help.clip-studio.com/en-us/manual_en/720_preferences/Shortcut_Settings.htm)
- [可选快捷键（不透明度、笔尖浓度、尺寸预设和工具切换）](https://help.clip-studio.com/en-us/manual_en/780_shortcuts/Optional_Shortcuts.htm)
- [操作中的快捷键（含 Ctrl+Alt 拖动画笔尺寸）](https://help.clip-studio.com/en-us/manual_en/780_shortcuts/Shortcuts_usable_during_operation.htm)
- [绘画工具与工具设置](https://help.clip-studio.com/en-us/manual_en/240_brushes/Where_to_find_drawing_tools.htm)
- [自定义笔刷工具（尺寸、墨水不透明度、笔尖浓度等）](https://help.clip-studio.com/en-us/manual_en/240_brushes/Customizing_brush_tools.htm)
- [笔刷尺寸预设](https://help.clip-studio.com/en-us/manual_en/240_brushes/Customizing_the_brush_size_palette.htm)
- [Tool Slider 调板](https://help.clip-studio.com/en-us/manual_en/240_brushes/Tool_Slider_palette.htm)
