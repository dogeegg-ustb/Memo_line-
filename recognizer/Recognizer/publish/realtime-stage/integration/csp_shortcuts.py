"""Read CSP shortcut databases without modifying the user's configuration."""

import sqlite3
from pathlib import Path


# CSP stores modifier keys as a bit mask: Alt=1, Shift=2, Ctrl=4.
MODIFIERS = ((4, "Ctrl"), (2, "Shift"), (1, "Alt"))
COMMAND_NAMES = {
    "undo": "撤销", "redo": "重做", "cut": "剪切", "copy": "复制",
    "paste": "粘贴", "clear": "清除", "filenew": "新建作品",
    "fileopen": "打开文件", "fileclose": "关闭文件", "filesave": "保存",
    "filesaveas": "另存为", "selectdeselect": "取消选择",
    "selectall": "全选", "applicationpreference": "首选项",
    "applicationshortcutsetting": "快捷键设置",
}


def read_menu_shortcuts(path, include_unassigned=False):
    """Return rows from shortcutmenu; an absent table means no saved mappings yet."""
    path = Path(path)
    if not path.is_file():
        raise FileNotFoundError(path)
    with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as conn:
        if not conn.execute(
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='shortcutmenu'"
        ).fetchone():
            return []
        rows = conn.execute(
            "SELECT _PW_ID, menucommandtype, menucommand, shortcut, modifier "
            "FROM shortcutmenu ORDER BY _PW_ID"
        ).fetchall()

    result = []
    for row_id, command_type, command, key, modifier in rows:
        assigned = key is not None and str(key).upper() != "NULL" and str(key) != ""
        if not assigned and not include_unassigned:
            continue
        value = str(key) if assigned else "未分配"
        mask = int(modifier or 0)
        parts = [name for bit, name in MODIFIERS if mask & bit]
        if mask & ~7:
            parts.append(f"未知修饰键({mask & ~7})")
        parts.append(value)
        result.append({
            "id": f"menu_{row_id}",
            "command_type": command_type or "",
            "command": command or "",
            "action_name": COMMAND_NAMES.get(command, command or "未知命令"),
            "shortcut": " + ".join(parts) if assigned else value,
            "scope": "菜单命令",
            "source": "用户配置文件",
            "details": f"{command_type or ''}: {command or ''}",
        })
    return result


def read_tool_shortcuts(path):
    """Return tool and subtool bindings saved in EditImageTool.todb."""
    path = Path(path)
    if not path.is_file():
        raise FileNotFoundError(path)
    with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as conn:
        rows = conn.execute(
            "SELECT _PW_ID, NodeName, NodeShortCutKey FROM Node "
            "WHERE NodeShortCutKey IS NOT NULL AND NodeShortCutKey > 0 "
            "ORDER BY NodeShortCutKey, _PW_ID"
        ).fetchall()
    result = []
    for row_id, name, code in rows:
        key = chr(64 + code) if 1 <= code <= 26 else chr(code) if 65 <= code <= 90 else f"Key_{code}"
        result.append({
            "id": f"tool_{row_id}", "action_name": name or f"工具 {row_id}",
            "shortcut": key, "scope": "工具 / 子工具", "source": "用户配置文件",
            "details": f"工具节点 ID: {row_id}",
        })
    return result
