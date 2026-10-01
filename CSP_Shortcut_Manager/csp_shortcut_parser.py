"""
CLIP STUDIO PAINT (CSP) 快捷键自动化定位与解析工具
无需安装任何第三方库（仅使用 Python 3 标准库：os, sys, sqlite3, json）
支持：自动定位路径、解析工具快捷键、解析手势操作、导出 Markdown/JSON 报表
"""

import os
import sys
import sqlite3
import json
from pathlib import Path
from csp_shortcuts import read_menu_shortcuts

# 保证在 Windows 控制台或各种终端下的 UTF-8 正常输出
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")


def find_csp_user_dir():
    """自动探测本机 CSP 用户数据与配置目录"""
    appdata = os.environ.get("APPDATA", "")
    userprofile = os.environ.get("USERPROFILE", "")
    
    candidates = [
        # 最常见的 CSP 1.5+ / 2.0+ / 3.0+ 存储路径
        os.path.join(appdata, "CELSYSUserData", "CELSYS", "CLIPStudioPaintVer1_5_0"),
        # 部分版本或旧版本路径
        os.path.join(appdata, "CELSYS", "CLIPStudioPaintVer1_5_0"),
        os.path.join(userprofile, "Documents", "CELSYS", "CLIPStudioPaintVer1_5_0"),
    ]
    
    # 逐一检查标准位置
    for cand in candidates:
        if os.path.exists(cand):
            return os.path.abspath(cand)

    # 深度递归探测备选（如果用户自定义移动了配置目录）
    if appdata and os.path.exists(os.path.join(appdata, "CELSYSUserData")):
        for root, dirs, _ in os.walk(os.path.join(appdata, "CELSYSUserData")):
            if "CLIPStudioPaintVer1_5_0" in dirs:
                return os.path.abspath(os.path.join(root, "CLIPStudioPaintVer1_5_0"))

    return None


def keycode_to_key(code):
    """CSP 工具键码转人类可读字符"""
    if not code:
        return ""
    if 1 <= code <= 26:
        return chr(ord("A") + code - 1)
    if code == 90:
        return "Z"
    return f"Key_{code}"


def parse_tool_shortcuts(csp_dir):
    """解析 EditImageTool.todb 获取工具与子工具快捷键"""
    todb_path = os.path.join(csp_dir, "Tool", "EditImageTool.todb")
    if not os.path.exists(todb_path):
        return None, f"未找到文件: {todb_path}"

    try:
        conn = sqlite3.connect(todb_path)
        conn.text_factory = bytes
        cur = conn.cursor()

        # 读取所有具有快捷键的工具节点
        cur.execute("""
            SELECT _PW_ID, NodeName, NodeShortCutKey, NodeDefaultIdentifier 
            FROM Node 
            WHERE NodeShortCutKey IS NOT NULL AND NodeShortCutKey > 0
            ORDER BY NodeShortCutKey, _PW_ID
        """)
        rows = cur.fetchall()
        results = []
        for pw_id, raw_name, key_code, default_id in rows:
            name = raw_name.decode("utf-8", errors="replace") if isinstance(raw_name, bytes) else str(raw_name)
            key_char = keycode_to_key(key_code)
            results.append({
                "id": pw_id,
                "shortcut": key_char,
                "tool_name": name,
                "default_id": default_id
            })
        conn.close()
        return results, None
    except Exception as e:
        return None, str(e)


def parse_gestures(csp_dir):
    """解析 Config.sqlite 获取触控与手势快捷设置"""
    config_path = os.path.join(csp_dir, "Preference", "Config.sqlite")
    if not os.path.exists(config_path):
        return None

    try:
        conn = sqlite3.connect(config_path)
        cur = conn.cursor()
        cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='Gesture'")
        if not cur.fetchone():
            conn.close()
            return None

        cur.execute("SELECT gesture, command, operation, area FROM Gesture WHERE gesture IS NOT NULL")
        rows = cur.fetchall()
        gesture_map = {
            "TwoFingerTap": "双指轻点 (Two-Finger Tap)",
            "ThreeFingerTap": "三指轻点 (Three-Finger Tap)",
            "TwoFingerSwipe": "双指滑动 (Two-Finger Swipe)",
            "Pinch": "双指捏合 (Pinch)",
            "Rotate": "双指旋转 (Rotate)",
            "Swipe": "单指滑动 (Swipe)",
            "PressAndTap": "长按并轻点 (Press & Tap)"
        }
        cmd_map = {
            "gestureassistmovecanvas": "平移画布",
            "gestureassistzoomcanvas": "缩放画布",
            "gestureassistrotatecanvas": "旋转画布",
            "gestureassistshortcutcommond": "撤销 / 重做 (Undo/Redo)",
            "gestureassistpalettebarvisible": "切换面板显隐",
            "gestureassistviewreset": "重置视图",
            "viewreset": "重置视图"
        }

        results = []
        for g, cmd, op, area in rows:
            results.append({
                "gesture_raw": g,
                "gesture_desc": gesture_map.get(g, g),
                "action": cmd_map.get(cmd, cmd),
                "scope": area if area else "画布 (Canvas)"
            })
        conn.close()
        return results
    except Exception:
        return None


def export_markdown_report(csp_dir, tools, gestures, output_file=None, menu_shortcuts=None):
    """导出为 Markdown 报告"""
    if output_file is None:
        script_dir = os.path.dirname(os.path.abspath(__file__))
        out_dir = os.path.join(script_dir, "outputs")
        os.makedirs(out_dir, exist_ok=True)
        output_file = os.path.join(out_dir, "csp_shortcuts_export.md")
    lines = [
        "# CLIP STUDIO PAINT (CSP) 本机快捷键配置清单\n",
        f"> **自动检测到配置文件根目录**：`{csp_dir}`\n",
        "> **数据说明**：用户文件中也含原有默认项，不代表每项都曾手动修改。\n",
        "## 1. 菜单命令快捷键（提取自 `Shortcut/default.khc`）\n",
        "| 快捷键 | 功能 / 命令 | CSP 命令标识 |",
        "| :---: | :--- | :--- |",
    ]
    for item in menu_shortcuts or []:
        lines.append(f"| **{item['shortcut']}** | {item['action_name']} | {item['details']} |")
    lines += [
        "",
        "## 2. 工具快捷键（提取自 `Tool/EditImageTool.todb`）\n",
        "| 快捷键 | 工具名称 | 节点 ID | 说明 |",
        "| :---: | :--- | :---: | :--- |"
    ]
    for item in tools or []:
        lines.append(f"| **{item['shortcut']}** | {item['tool_name']} | {item['id']} | 循环切换组 |")

    if gestures:
        lines.append("\n## 3. 触控与手势快捷操作（提取自 `Preference/Config.sqlite`）\n")
        lines.append("| 手势操作 | 触发功能 | 作用区域 |")
        lines.append("| :--- | :--- | :--- |")
        for g in gestures:
            lines.append(f"| {g['gesture_desc']} | **{g['action']}** | {g['scope']} |")

    content = "\n".join(lines)
    with open(output_file, "w", encoding="utf-8") as f:
        f.write(content)
    return output_file


def main():
    print("=" * 60)
    print("   CLIP STUDIO PAINT (CSP) 快捷键自动化提取工具")
    print("=" * 60)

    # 1. 自动寻找路径
    csp_dir = find_csp_user_dir()
    if not csp_dir:
        print("[错误] 未能在本机常见路径中定位到 CSP 用户数据目录。")
        print("请确认本机已安装运行过 CLIP STUDIO PAINT。")
        return

    print(f"[+] 成功定位 CSP 用户目录:\n    {csp_dir}\n")

    # 2. 检查关键配置文件存在性
    key_files = {
        "工具快捷键文件": os.path.join(csp_dir, "Tool", "EditImageTool.todb"),
        "快捷键方案与菜单覆盖": os.path.join(csp_dir, "Shortcut", "default.khc"),
        "修饰键设置文件": os.path.join(csp_dir, "Shortcut", "DefaultToolModifyKey.tomd"),
        "触控与偏好设置": os.path.join(csp_dir, "Preference", "Config.sqlite"),
    }

    print("[+] 配置文件状态检测:")
    for label, path in key_files.items():
        status = "存在 (OK)" if os.path.exists(path) else "未找到"
        print(f"  - {label:<16}: {status} -> {path}")
    print()

    # 3. 解析工具快捷键
    tools, err = parse_tool_shortcuts(csp_dir)
    if err:
        print(f"[!] 解析工具快捷键出错: {err}")
    elif tools:
        print(f"[+] 成功解析出 {len(tools)} 个工具快捷键映射:")
        print(f"    {'快捷键':<8} | {'工具名称':<16} | {'节点 ID'}")
        print("    " + "-" * 36)
        for t in tools:
            print(f"    {t['shortcut']:<8} | {t['tool_name']:<16} | {t['id']}")
    print()

    # 4. 解析菜单命令快捷键
    try:
        menu_shortcuts = read_menu_shortcuts(os.path.join(csp_dir, "Shortcut", "default.khc"))
        print(f"[+] 读取到 {len(menu_shortcuts)} 条已分配的菜单命令快捷键")
        for item in menu_shortcuts:
            if item["command"] == "redo":
                print(f"    重做: {item['shortcut']}")
    except (OSError, sqlite3.DatabaseError, ValueError) as exc:
        menu_shortcuts = []
        print(f"[!] 菜单快捷键读取失败: {exc}")
    print()

    # 5. 解析触控手势
    gestures = parse_gestures(csp_dir)
    if gestures:
        print(f"[+] 成功解析出 {len(gestures)} 项触控/手势映射:")
        for g in gestures:
            print(f"    - {g['gesture_desc']:<26} -> {g['action']}")
    print()

    # 6. 自动导出文件
    out_md = export_markdown_report(csp_dir, tools, gestures, menu_shortcuts=menu_shortcuts)
    print(f"[+] 导出完成！已生成 Markdown 清单: {os.path.abspath(out_md)}")


if __name__ == "__main__":
    main()
