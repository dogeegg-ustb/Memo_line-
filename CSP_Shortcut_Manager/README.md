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
├── csp_shortcut_writer.py          # 记艺线图层快捷键 plan/apply/verify/rollback CLI
├── test_csp_shortcut_writer.py     # 临时测试副本上的写入与回滚检查
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

## 记艺线快捷键写入 CLI（2026-10-07）

`csp_shortcut_writer.py` 使用 Python 标准库 `sqlite3`，每次有效子命令调用输出一行 JSON。现有 GUI、读取器和报表功能保持原用法。writer 要求显式传入用户数据目录，并同时读取 `Shortcut/default.khc` 与 `Tool/EditImageTool.todb` 来检查绑定和冲突。

| 子命令 | 参数 | 行为 |
| --- | --- | --- |
| `plan` | `--user-dir`、`--require` | 只读，返回已有问题、拟写绑定、数据库快照哈希和 `confirmToken` |
| `verify` | `--user-dir`、`--require` | 只读，检查所需命令是否有可发送、无冲突的绑定 |
| `apply` | `--user-dir`、`--plan`、`--confirm-token` | CSP 退出且用户确认完整计划后，备份、写入、读回验证并记录清单 |
| `rollback` | `--user-dir` | CSP 退出后，按清单恢复原值；保留后来被用户修改的行并报错 |

`--require` 接受 menucommand 字符串的 JSON 数组，或带 `commands` 数组的 JSON 对象。命令名使用实际数据库中的值；例如当前读取器将 `filesave` 显示为“保存”。只有缺失的 `layerdelete`、`layervisiblelayer` 可由 writer 配置；其他缺失命令只报告 `ShortcutsMissing`。

虚拟键按 `Ctrl+Alt+Shift+F9` 至 `F12` 顺序分配，检查整个菜单表及工具快捷键。未知工具键编码会报告 `ToolShortcutUnconfirmed`；不使用未经核实的 F13 及以上候选。已有非空绑定不改。命令类型优先取同名已有行；缺少类型时报 `CommandTypeUnconfirmed`。只有已经独立核实类型的调用方，才能在要求对象中传入 `confirmedCommandTypes` 映射，以允许插入没有现有行的命令；该字段不承担核实过程。

下面的 PowerShell 示例使用一份测试副本目录。真实用户配置的 `apply` 应由用户审阅计划并明确确认后调用，且先关闭 CSP。

```powershell
$userDir = 'D:\CSP-test-copy'
$require = '["filesave","paste","layerdelete","layervisiblelayer"]'
$planJson = python csp_shortcut_writer.py plan --user-dir $userDir --require $require
$plan = $planJson | ConvertFrom-Json
# 先审阅 $plan.bindings 与 $plan.issues；CSP 必须已退出。
python csp_shortcut_writer.py apply --user-dir $userDir --plan $planJson --confirm-token $plan.confirmToken
python csp_shortcut_writer.py verify --user-dir $userDir --require $require
python csp_shortcut_writer.py rollback --user-dir $userDir
```

`apply` 检查 `CLIPStudioPaint.exe` 不存在，并要求计划、确认令牌及当前菜单/工具快照完全匹配。配置改变后需重新 plan。备份目录为 `Shortcut/memoline-backup-<UTC时间>`，包含 `default.khc` 和存在的 `-wal`、`-journal` 文件；读回哈希后写入。新增或更新绑定通过现有 `read_menu_shortcuts` 再次读取；验证失败会恢复本次修改。

`Shortcut/memoline-provisioned.json` 记录原值、新值、备份位置、哈希和状态。回滚只处理清单中与实际新值一致的行，并校验备份与原值；遇到用户后续修改、篡改的清单或备份不一致会停止。`layervisiblelayer` 是否切换选中层的显示状态仍需用户在 CSP 副本上核实；绑定校验不会证明该命令的操作语义。

## writer 检查与发布限制

```powershell
python -m unittest test_csp_shortcut_writer test_csp_shortcuts -v
```

2026-10-07 检查通过 11 个测试（9 个 writer、2 个既有 reader）。writer 使用 `tests/artifacts` 下临时数据库，mock CSP 进程状态，覆盖 plan 的只读性、菜单/工具冲突、apply/verify/rollback、非空绑定保护、运行中的 CSP 拒绝、过期计划、读回失败恢复、明确类型的插入及篡改清单拒绝。测试未写真实用户目录，也未向 CSP 注入输入。实际失败与通过记录见 `Memoline_demo_csponly/docs/architecture/IMPLEMENTATION_NOTES.md`。

writer 的 Python 参考实现已可用；宿主设置页和发布调用流程尚未接线。当前 Recognizer 发布目录未找到内置 `python.exe`，检查使用的是开发环境 Python，不能据此宣称发布包具备解释器。发布集成仍需验证可用 Python runtime，或实现架构要求的 C# `winsqlite3.dll` 适配。
