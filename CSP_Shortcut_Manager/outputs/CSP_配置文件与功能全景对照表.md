# CLIP STUDIO PAINT (CSP) 配置文件与功能对照全景表

> **自动定位配置目录**：`C:\Users\dogeegg\AppData\Roaming\CELSYSUserData\CELSYS\CLIPStudioPaintVer1_5_0`

## 一、 核心配置文件与其承载的功能对照表

| 模块分类 | 配置文件名 | 承载的 CSP 核心功能 | 解析项数 | 文件大小 | 物理绝对路径 |
| :---: | :--- | :--- | :---: | :---: | :--- |
| 绘图工具箱 | `EditImageTool.todb` | 画笔工具箱与子工具快捷键核心数据库 | 27 项 | 866.0 KB | `C:\Users\dogeegg\AppData\Roaming\CELSYSUserData\CELSYS\CLIPStudioPaintVer1_5_0\Tool\EditImageTool.todb` |
| 主菜单快捷键 | `default.khc` | 主菜单与命令快捷键自定义覆盖方案 | 20 项 | 12.0 KB | `C:\Users\dogeegg\AppData\Roaming\CELSYSUserData\CELSYS\CLIPStudioPaintVer1_5_0\Shortcut\default.khc` |
| 修饰键交互 | `DefaultToolModifyKey.tomd` | 工具修饰键行为与动态交互配置 | 7 项 | 24.0 KB | `C:\Users\dogeegg\AppData\Roaming\CELSYSUserData\CELSYS\CLIPStudioPaintVer1_5_0\Shortcut\DefaultToolModifyKey.tomd` |
| 触控与全局偏好 | `Config.sqlite` | 多点触控手势、手绘板与全局首选项 | 10 项 | 152.0 KB | `C:\Users\dogeegg\AppData\Roaming\CELSYSUserData\CELSYS\CLIPStudioPaintVer1_5_0\Preference\Config.sqlite` |
| 混色调色板 | `MixPaletteTool.todb` | 混合调色板专用工具与笔刷配置 | 3 项 | 28.0 KB | `C:\Users\dogeegg\AppData\Roaming\CELSYSUserData\CELSYS\CLIPStudioPaintVer1_5_0\MixPalette\MixPaletteTool.todb` |
| 变换参数预设 | `Transform.sqlite` | 自由变换与变形设置历史配置 | 2 项 | 12.0 KB | `C:\Users\dogeegg\AppData\Roaming\CELSYSUserData\CELSYS\CLIPStudioPaintVer1_5_0\DialogBox\Transform.sqlite` |

## 二、 各配置文件解析出的快捷键与操作功能明细

| 快捷键 / 触发手势 | 对应功能 / 工具名称 | 作用域 | 来源配置文件 | 详细机制说明 |
| :---: | :--- | :---: | :--- | :--- |
| **B** | 毛筆 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 56)，同组工具多次按键可循环轮换 |
| **B** | 噴槍 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 77)，同组工具多次按键可循环轮换 |
| **B** | 裝飾 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 151)，同组工具多次按键可循环轮换 |
| **D** | 選擇圖層 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 143)，同组工具多次按键可循环轮换 |
| **E** | 橡皮擦 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 87)，同组工具多次按键可循环轮换 |
| **G** | 漸層 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 6)，同组工具多次按键可循环轮换 |
| **G** | 填充 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 101)，同组工具多次按键可循环轮换 |
| **H** | 手掌 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 20)，同组工具多次按键可循环轮换 |
| **I** | 吸管 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 147)，同组工具多次按键可循环轮换 |
| **J** | 色彩混合 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 94)，同组工具多次按键可循环轮换 |
| **J** | 歪斜 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 407)，同组工具多次按键可循环轮换 |
| **K** | 移動圖層 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 144)，同组工具多次按键可循环轮换 |
| **L** | 透光桌 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 285)，同组工具多次按键可循环轮换 |
| **L** | 編輯時間軸 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 286)，同组工具多次按键可循环轮换 |
| **M** | 選擇範圍 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 112)，同组工具多次按键可循环轮换 |
| **O** | 物件 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 142)，同组工具多次按键可循环轮换 |
| **P** | 沾水筆 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 22)，同组工具多次按键可循环轮换 |
| **P** | 鉛筆 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 43)，同组工具多次按键可循环轮换 |
| **R** | 旋轉 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 21)，同组工具多次按键可循环轮换 |
| **T** | 文字 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 240)，同组工具多次按键可循环轮换 |
| **T** | 對白框 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 295)，同组工具多次按键可循环轮换 |
| **U** | 圖形 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 121)，同组工具多次按键可循环轮换 |
| **U** | 分格邊框 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 292)，同组工具多次按键可循环轮换 |
| **U** | 尺規 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 294)，同组工具多次按键可循环轮换 |
| **W** | 自動選擇 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 107)，同组工具多次按键可循环轮换 |
| **Y** | 線修正 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 35)，同组工具多次按键可循环轮换 |
| **Z** | 放大鏡 | 绘图工具箱 | `EditImageTool.todb` | 工具组主键 (节点ID: 2)，同组工具多次按键可循环轮换 |
| **Ctrl + Z** | 撤销 (Undo) | 编辑菜单 | `default.khc` | 撤销上一步操作 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + Y** | 重做 (Redo) | 编辑菜单 | `default.khc` | 重做恢复已撤销的操作 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + T** | 自由变换 (Free Transform) | 编辑菜单 | `default.khc` | 缩放、旋转或倾斜当前选中图像 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + S** | 保存 (Save) | 文件菜单 | `default.khc` | 保存当前画布 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + Shift + S** | 另存为 (Save As) | 文件菜单 | `default.khc` | 以新名称或格式保存副本 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + N** | 新建插画/漫画 (New) | 文件菜单 | `default.khc` | 新建画布文件 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + O** | 打开文件 (Open) | 文件菜单 | `default.khc` | 打开已有作品或图像 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + C** | 复制 (Copy) | 编辑菜单 | `default.khc` | 复制选中选区像素或图层 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + V** | 粘贴 (Paste) | 编辑菜单 | `default.khc` | 粘贴剪贴板内容到新图层 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + X** | 剪切 (Cut) | 编辑菜单 | `default.khc` | 剪切选区像素 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + A** | 全选 (Select All) | 选择菜单 | `default.khc` | 选择整个画布区域 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + D** | 取消选择 (Deselect) | 选择菜单 | `default.khc` | 消除当前选区虚线框 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + Shift + I** | 反向选择 (Invert Selection) | 选择菜单 | `default.khc` | 翻转选取范围 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + E** | 向下合并图层 (Merge Down) | 图层菜单 | `default.khc` | 将当前图层合并至下方图层 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + Shift + E** | 合并所有可见图层 | 图层菜单 | `default.khc` | 将所有眼睛点亮的图层合并 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + 0** | 全屏适应显示 (Fit Screen) | 视图菜单 | `default.khc` | 缩放至适合当前窗口完整显示 (当前使用 CSP 官方标准默认方案) |
| **Ctrl + Alt + 0** | 100% 实际像素显示 | 视图菜单 | `default.khc` | 以 100% 原始比例观察画面细节 (当前使用 CSP 官方标准默认方案) |
| **X** | 切换主色/副色 | 调色板 | `default.khc` | 在前景画笔色与背景色之间互换 (当前使用 CSP 官方标准默认方案) |
| **C** | 切换为透明色 | 调色板 | `default.khc` | 当前笔刷直接作为擦除擦除像素 (当前使用 CSP 官方标准默认方案) |
| **Tab** | 全屏无干扰/面板显隐 | 窗口菜单 | `default.khc` | 一键隐藏或还原所有浮动窗口面板 (当前使用 CSP 官方标准默认方案) |
| **Space + 鼠标拖动** | 临时手掌平移画布 | 全局画布导航 | `DefaultToolModifyKey.tomd` | 画画时按住空格可即时抓动画布，松开立刻回到原画笔 |
| **Ctrl + Space + 拖动** | 临时视图无级平滑缩放 | 全局画布导航 | `DefaultToolModifyKey.tomd` | 左右拖拽平滑拉近/推远镜头视角 |
| **Shift + Space + 拖动** | 临时旋转画布视角 | 全局画布导航 | `DefaultToolModifyKey.tomd` | 绕中心旋转画布以获得顺手的画线角度 |
| **Alt + 单击画布** | 临时吸管拾取颜色 | 画笔绘图状态 | `DefaultToolModifyKey.tomd` | 直接吸取光标所在像素的颜色，松开切回画笔 |
| **Ctrl + Alt + 左右拖动** | 实时动态调节笔刷粗细 | 画笔绘图状态 | `DefaultToolModifyKey.tomd` | 无需在面板输入数值，直接直观拖拽更改笔刷直径 |
| **Ctrl + 鼠标拖动** | 临时移动当前图层内容 | 图层编辑 | `DefaultToolModifyKey.tomd` | 快速平移图层中的图形或线稿 |
| **Shift + 画线** | 两点之间绘制绝对直线 | 画笔绘图状态 | `DefaultToolModifyKey.tomd` | 点第一下后按住 Shift 点第二下直接连出直线 |
| **单指滑动 (Swipe)** | gestureassistdigitizercoexist | 画布主界面 | `Config.sqlite` | 触控板 / 手绘屏多点手势 (内部指令: gestureassistdigitizercoexist) |
| **双指滑动 (Two-Finger Swipe)** | 平移画布视图 | 画布主界面 | `Config.sqlite` | 触控板 / 手绘屏多点手势 (内部指令: gestureassistmovecanvas) |
| **双指捏合 (Pinch)** | 缩放画布画面 | 画布主界面 | `Config.sqlite` | 触控板 / 手绘屏多点手势 (内部指令: gestureassistzoomcanvas) |
| **双指旋转 (Rotate)** | 旋转画布视角 | 画布主界面 | `Config.sqlite` | 触控板 / 手绘屏多点手势 (内部指令: gestureassistrotatecanvas) |
| **双指轻点 (Two-Finger Tap)** | 撤销 (Undo) / 重做 (Redo) | 画布主界面 | `Config.sqlite` | 触控板 / 手绘屏多点手势 (内部指令: gestureassistshortcutcommond) |
| **三指轻点 (Three-Finger Tap)** | 撤销 (Undo) / 重做 (Redo) | 画布主界面 | `Config.sqlite` | 触控板 / 手绘屏多点手势 (内部指令: gestureassistshortcutcommond) |
| **长按并轻点 (Press & Tap)** | 撤销 (Undo) / 重做 (Redo) | 画布主界面 | `Config.sqlite` | 触控板 / 手绘屏多点手势 (内部指令: gestureassistshortcutcommond) |
| **双指轻点 (Two-Finger Tap)** | 显示/隐藏面板浮动条 | palette | `Config.sqlite` | 触控板 / 手绘屏多点手势 (内部指令: gestureassistpalettebarvisible) |
| **三指轻点 (Three-Finger Tap)** | 重置画布到 100% 原始位置 | palette | `Config.sqlite` | 触控板 / 手绘屏多点手势 (内部指令: gestureassistviewreset) |
| **长按并轻点 (Press & Tap)** | 重置视图视角 | palette | `Config.sqlite` | 触控板 / 手绘屏多点手势 (内部指令: viewreset) |
| **专用面板工具** | 混色毛笔 (Blend Brush) | 混色板面板 | `MixPaletteTool.todb` | 在混色区域调和多种颜料 |
| **专用面板工具** | 调色盘吸管 (Color Pick) | 混色板面板 | `MixPaletteTool.todb` | 从混色调色盘中提取混合后的新色彩 |
| **专用面板工具** | 面板画笔清除 | 混色板面板 | `MixPaletteTool.todb` | 一键清空混色板区域的试验颜料 |
| **对话框预设** | 自由变换保持长宽比 | 变换操作 | `Transform.sqlite` | 等比例缩放图层对象 |
| **对话框预设** | 双线性/双三次插值算法 | 图像重采样 | `Transform.sqlite` | 变换时保持像素轮廓平滑清晰 |