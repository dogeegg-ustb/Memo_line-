# 17:01:44 笔刷确认失败：数据库调查

检查对象是本机发布版实际加载的属性库，及本机 CSP 的工具数据库。全部 SQLite 连接使用 `mode=ro`，没有修改数据库或向 CSP 注入操作。

## 检查结果

属性库：`D:\Memo_Line\Memo_Line\Memoline_demo_csponly\publish\win-x64\Recognizer\integration\recognizer_core\panel_state\data\properties.sqlite3`。

- 150 个属性定义，547 个 UI 资源文字条目，版本 `2026-09-24`。
- 发布版与源码数据库 SHA256 相同：`a7a758ccded8bf904d9d2f73f2d726b9407be9dc8b40d09a33c9f8bf9c312bd8`。
- `消除锯齿` 与 `消除鋸齒` 都精确匹配 `antialiasing`。其 `enum_values` 已有无/無/None、弱/Weak、中/Middle/Medium、强/強/Strong，不能说缺少这些枚举。
- `硬度` 精确匹配 `brush_tip.hardness`。
- `Vector eraser` 定义存在，键为 `erase.vector_eraser`，但中文别名仅有 `删除向量` / `刪除向量`，没有当前界面的 `矢量擦除`。实际调用 `PropertyCatalog.match('矢量擦除')` 返回 `unknown`，这是真实的别名缺失。
- `較硬` 存在于 `ui_strings` 的 `E79C2AC5-BC3F-4838-9E87-F49B629F84B5:1.22.1.253`。它是工具名称，不是属性名称，因此不能用 `PropertyCatalog.match('較硬')` 的未匹配结果判断工具目录缺项。

CSP 工具数据库：`C:\Users\dogeegg\AppData\Roaming\CELSYSUserData\CELSYS\CLIPStudioPaintVer1_5_0\Tool\EditImageTool.todb`。

- 264 个工具节点，无读取警告。
- 大类 `tool_87`：橡皮擦，已配置快捷键 `E`。
- 子工具组 `tool_88`：橡皮擦。
- 子工具 `tool_89`：較硬，路径为 `橡皮擦 / 橡皮擦 / 較硬`，可见，父大类和组身份完整。

详细查询结果保存在 `database-audit.json`，可通过 `audit-databases.py` 重查。

## 为什么有定义仍然失败

用发布版自带 OCR 模型、解析器及数据库处理 `requested-brush.png`，离线复现了原始返回。此过程只读取存证图像。

1. OCR 把左上角菜单图标识别成 `三`，与 `工具属性`、另一页签的 `笔刷尺寸` 处于同一文字行。属性解析器的标题正则将 `工具属性` 前面的 `三` 当成笔刷名称。实际名称 `較硬` 位于下一行，因已存在标题名称而进入未解析文字。该笔刷属性解析路径没有接入 CSP 工具目录来核验名称。
2. 消除锯齿一行 OCR 只读出了 `无`、`弱`、`强`，未读出当前高亮的 `中`。`apply_visual_values` 只检查这些 OCR 选项框的高亮；三个框都未选中，结果为 `partial`。虽然数据库里有 `中`，该路径没有使用 `enum_values` 恢复完整选项集合；存在部分 OCR 选项时也没有覆盖漏检按钮的图像分支。
3. `StateTimeline.semantic_state` 要求所有属性均为 `ok` 或 `disabled`。消除锯齿的 `partial` 触发整体拒绝，所以 `brushState` 为 `unknown`、`state=null`。尺寸 27.2、笔刷浓度 100、手颤修正 2 均已正常读取，但不能越过当前的整体确认门槛。
4. 回放器收到 `unknown` 即抛错，没有进入子工具面板 OCR 后续查找。`error=null` 使界面只显示“本次主动请求未确认笔刷”，没有显示上述原因。

`矢量擦除` 的别名应补充；修正名称提取、补齐漏检高亮选项和按数字属性范围独立确认，才是解决这次失败所需的解析改动。

离线复现输出见 `parser-reproduction.json`，复查脚本为 `reproduce-brush.py`。调查没有修改生产解析代码。
