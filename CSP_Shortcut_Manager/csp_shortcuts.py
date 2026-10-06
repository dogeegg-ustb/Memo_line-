"""Read CSP shortcut databases without modifying the user's configuration."""

import sqlite3
from contextlib import closing
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
    with closing(sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)) as conn:
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
    with closing(sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)) as conn:
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


def read_tool_inventory(path):
    """Read installed tool/group/subtool identities and saved selection links.

    Packages here are the groups installed in CSP's tool library, not guessed
    .sut archives. All parent links follow the database's UUID sibling lists.
    """
    path = Path(path)
    with closing(sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)) as db:
        db.row_factory = sqlite3.Row
        rows = [dict(row) for row in db.execute("SELECT * FROM Node ORDER BY _PW_ID")]
        has_manager = db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='Manager'").fetchone()
        managers = [dict(row) for row in db.execute("SELECT * FROM Manager")] if has_manager else []

    def uuid(value):
        return bytes(value).hex() if isinstance(value, (bytes, bytearray)) and len(value) == 16 else None

    rows_by_id = {row['_PW_ID']:row for row in rows}
    warnings = []
    if rows and not all(key in rows[0] for key in ('NodeUuid', 'NodeFirstChildUuid', 'NodeNextUuid')):
        warnings.append("工具数据库缺少 UUID 层级字段，无法还原完整子工具关系")
    nodes = {f"tool_{row['_PW_ID']}": dict(id=f"tool_{row['_PW_ID']}", nodeId=row['_PW_ID'],
        uuid=uuid(row.get('NodeUuid')), name=row.get('NodeName') or '',
        parentId=None, children=[], hidden=bool(row.get('NodeHidden', 0)),
        defaultIdentifier=row.get('NodeDefaultIdentifier'),
        shortcut=(chr(64+row['NodeShortCutKey']) if 1 <= (row.get('NodeShortCutKey') or 0) <= 26
                  else chr(row['NodeShortCutKey']) if 65 <= (row.get('NodeShortCutKey') or 0) <= 90
                  else f"Key_{row['NodeShortCutKey']}" if (row.get('NodeShortCutKey') or 0) > 0 else None),
        material=dict(contentId=row.get('NodeMaterialContentID'), uuid=row.get('NodeMaterialUuid'),
                      hasMaterialId=bool(row.get('NodeMaterialIDExists', 0)))) for row in rows}
    by_uuid = {node['uuid']:node for node in nodes.values() if node['uuid']}
    for row in rows:
        owner = nodes[f"tool_{row['_PW_ID']}"]
        current = uuid(row.get('NodeFirstChildUuid'))
        seen = set()
        while current:
            if current in seen:
                warnings.append(f"子工具同级链存在循环: {owner['id']}")
                break
            seen.add(current)
            child = by_uuid.get(current)
            if child is None:
                warnings.append(f"子工具节点缺失: {owner['id']} -> {current}")
                break
            if child is owner or child['parentId'] not in (None, owner['id']):
                warnings.append(f"子工具父节点冲突: {child['id']}")
                break
            child['parentId'] = owner['id']
            owner['children'].append(child['id'])
            child_row = rows_by_id[child['nodeId']]
            current = uuid(child_row.get('NodeNextUuid'))
    root_ids = [by_uuid[key]['id'] for manager in managers if (key := uuid(manager.get('RootUuid'))) in by_uuid]
    if not root_ids:
        root_ids = [n['id'] for n in nodes.values() if not n['parentId'] and not n['name'] and n['children']]
    for node in nodes.values():
        node['kind'] = ('root' if node['id'] in root_ids else 'tool'
                        if node['parentId'] in root_ids or (not node['parentId'] and node['children'])
                        else 'group' if node['children'] else 'subtool')
    for row in rows:
        node = nodes[f"tool_{row['_PW_ID']}"]
        chain, seen = [], set()
        current = node
        while current and current['id'] not in seen:
            seen.add(current['id'])
            chain.append(current)
            current = nodes.get(current['parentId'])
        if current:
            warnings.append(f"子工具父链存在循环: {node['id']}")
        chain.reverse()
        node['path'] = [n['name'] for n in chain if n['kind'] != 'root']
        node['pathIds'] = [n['id'] for n in chain if n['kind'] != 'root']
        node['toolId'] = next((n['id'] for n in chain if n['kind'] == 'tool'), None)
        node['groupId'] = next((n['id'] for n in reversed(chain) if n['kind'] == 'group'), None)
        selected = by_uuid.get(uuid(row.get('NodeSelectedUuid')))
        node['savedSelectedChildId'] = selected['id'] if selected and selected['id'] in node['children'] else None
    return dict(schemaVersion=1, status='partial' if warnings else 'ok', sourceFile=str(path),
                roots=root_ids, nodes=list(nodes.values()),
                savedCurrentNodeIds=[by_uuid[key]['id'] for manager in managers
                                     if (key := uuid(manager.get('CurrentNodeUuid'))) in by_uuid],
                selectionSource='savedCspConfiguration', warnings=warnings)


def tool_descendants(catalog, node_id):
    """Flatten subtools without losing their group paths or duplicate names."""
    by_id = {node['id']:node for node in catalog.get('nodes', [])}
    result, seen = [], set()
    def visit(identity):
        if identity in seen or identity not in by_id:
            return
        seen.add(identity)
        node = by_id[identity]
        if node['kind'] == 'subtool':
            result.append(node)
        for child in node['children']:
            visit(child)
    visit(node_id)
    return result


def saved_selected_subtool(catalog, node_id):
    by_id = {node['id']:node for node in catalog.get('nodes', [])}
    seen = set()
    while node_id in by_id and node_id not in seen:
        seen.add(node_id)
        node = by_id[node_id]
        if node['kind'] == 'subtool':
            return node_id
        node_id = node.get('savedSelectedChildId')
    return None
