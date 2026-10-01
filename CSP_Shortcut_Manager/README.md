# CLIP STUDIO PAINT (CSP) 快捷键与配置文件全景管理器

本项目读取当前 Windows 用户目录中的 CLIP STUDIO PAINT 快捷键配置，显示菜单命令与工具快捷键。无需安装第三方 Python 库。

菜单命令来自 `Shortcut/default.khc` 的 `shortcutmenu` 表，工具与子工具来自 `Tool/EditImageTool.todb` 的 `Node` 表。两者位于用户数据目录，但其中也包含原有默认项；程序不会把所有记录都称为“手动自定义”。未分配的菜单命令不显示。若 CSP 尚未将改动写入磁盘，重新扫描也无法读到该改动。

---

## 📁 目录结构说明

```text
CSP_Shortcut_Manager/
│
├── 启动CSP快捷键全景管理器.bat     # 【推荐】Windows 双击一键启动桌面管理界面
├── csp_explorer.py                # 主程序（含 Tkinter GUI 桌面窗口与 CLI 导出模式）
├── csp_shortcuts.py               # 共用的菜单和工具快捷键读取逻辑
├── csp_shortcut_parser.py         # 极简命令行提取脚本（适合终端自动化或批处理）
├── README.md                      # 本说明文档
│
└── outputs/                       # 【报表输出目录】存放程序自动解析生成的报表文件
    ├── CSP_已保存快捷键.html              # 网页版对照报表
    ├── CSP_已保存快捷键.md                # Markdown 对照报表
    └── csp_shortcuts_export.md            # 工具快捷键与手势提取清单
```

---

## ❓ 常见问题解答

### 问：`outputs` 目录里的 `.md` 和 `.html` 文件是什么？
**答：它们正是程序的输出结果！**  
程序读取本机 `.khc` 与 `.todb` 中已保存的键盘快捷键，并输出对照表。`Config.sqlite` 中的触控手势由极简脚本单独列出。旧文件名中带“含自定义与补充”的报表是旧版生成结果，请以新的“已保存快捷键”报表为准。

---

## 🚀 使用方法

### 方式 1：双击运行桌面图形界面（最直观）
直接双击运行：
👉 **`启动CSP快捷键全景管理器.bat`**

**功能亮点：**
- **自动定位**：启动即自动搜索锁定当前电脑的 CSP 用户配置路径。
- **上下联动双表**：
  - 点击上方某个配置文件（如 `EditImageTool.todb`、`default.khc` 等）；
  - 下方显示本机已保存的菜单命令与工具快捷键，可用“菜单命令”“工具 / 子工具”按钮过滤。
- **实时过滤**：在右上角搜索框中输入任意按键（如 `B`、`Ctrl`）或功能词（如 `水彩`、`撤销`）实时搜索。
- **一键在文件夹中定位**：点击【打开配置目录】可直接在 Windows 资源管理器中高亮选中真实文件。
- **自定义导出**：点击【导出对照表】即可按需保存为 Markdown、HTML 或 JSON。

### 方式 2：命令行静默一键导出
打开终端，进入本目录后执行：
```bash
python csp_explorer.py --export
```
程序将自动扫描并在 `outputs/` 目录生成 `CSP_已保存快捷键.html` 与 `.md`。

### 方式 3：极简脚本模式
```bash
python csp_shortcut_parser.py
```
在控制台打印本机的菜单命令、工具快捷键与手势映射。
