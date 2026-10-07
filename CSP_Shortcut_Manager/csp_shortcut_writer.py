"""Provision only confirmed, unassigned CSP layer shortcuts after explicit approval."""

import argparse
import hashlib
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path

from csp_shortcuts import read_menu_shortcuts

WRITABLE_COMMANDS = {"layerdelete", "layervisiblelayer"}
VIRTUAL_KEYS = ("F9", "F10", "F11", "F12")


class ShortcutWriterError(ValueError):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def _digest(value):
    return hashlib.sha256(_canonical(value)).hexdigest()


def _required(value):
    commands = value.get("commands") if isinstance(value, dict) else value
    if not isinstance(commands, list) or not commands or any(not isinstance(c, str) or not c for c in commands):
        raise ShortcutWriterError("InvalidRequirements", "require 必须列出非空的 menucommand 字符串数组")
    return sorted(set(commands))


def _confirmed_types(value):
    types = value.get("confirmedCommandTypes", {}) if isinstance(value, dict) else {}
    if not isinstance(types, dict) or any(command not in WRITABLE_COMMANDS or not isinstance(kind, str) or not kind.strip() for command, kind in types.items()):
        raise ShortcutWriterError("InvalidRequirements", "confirmedCommandTypes 只接受已经核实的图层命令类型")
    return types


def _paths(user_dir):
    root = Path(user_dir).resolve()
    khc = root / "Shortcut/default.khc"
    tool = root / "Tool/EditImageTool.todb"
    if not khc.is_file() or not tool.is_file():
        raise ShortcutWriterError("ShortcutDatabaseMissing", "必须同时提供 default.khc 和 EditImageTool.todb，才能完整检查冲突")
    return root, khc, tool


def _read(path, query):
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as db:
        return [list(row) for row in db.execute(query)]


def _snapshot(user_dir):
    root, khc, tool = _paths(user_dir)
    menu = _read(khc, "SELECT _PW_ID,menucommandtype,menucommand,shortcut,modifier FROM shortcutmenu ORDER BY _PW_ID")
    tools = _read(tool, "SELECT _PW_ID,NodeShortCutKey FROM Node WHERE NodeShortCutKey IS NOT NULL AND NodeShortCutKey > 0 ORDER BY _PW_ID")
    return root, menu, tools


def _assigned(key):
    return key is not None and str(key).strip() != "" and str(key).upper() != "NULL"


def _key(key):
    return str(key).strip().upper()


def _sendable(key, modifier):
    key = _key(key)
    return isinstance(modifier, int) and modifier & ~7 == 0 and modifier >= 0 and bool(
        re.fullmatch(r"[A-Z0-9]|F(?:[1-9]|1[0-2])|DELETE|BACKSPACE|TAB|ENTER|ESC|ESCAPE|SPACE|UP|DOWN|LEFT|RIGHT|HOME|END|PAGEUP|PAGEDOWN|INSERT|[\[\],.\-/;='`\\]", key))


def _tool_keys(tools):
    result = set()
    for _, code in tools:
        if 1 <= code <= 26:
            result.add(chr(64 + code))
        elif 65 <= code <= 90:
            result.add(chr(code))
        elif 112 <= code <= 123:
            result.add(f"F{code - 111}")
        else:
            # Unknown tool key encodings cannot safely be declared conflict-free.
            raise ShortcutWriterError("ToolShortcutUnconfirmed", f"工具快捷键编码尚未确认：{code}")
    return result


def verify(user_dir, require):
    required = _required(require)
    root, menu, tools = _snapshot(user_dir)
    tool_keys = _tool_keys(tools)
    issues = []
    bindings = []
    for command in required:
        assigned = [row for row in menu if row[2] == command and _assigned(row[3])]
        if not assigned:
            issues.append(dict(code="ShortcutsMissing", command=command))
        for row_id, command_type, _, key, modifier in assigned:
            modifier = modifier or 0
            if not _sendable(key, modifier):
                issues.append(dict(code="ShortcutUnsendable", command=command, rowId=row_id))
                continue
            conflicts = [r[0] for r in menu if r[0] != row_id and _assigned(r[3]) and _key(r[3]) == _key(key) and (r[4] or 0) == modifier and r[2] != command]
            if conflicts or _key(key) in tool_keys:
                issues.append(dict(code="ShortcutConflict", command=command, rowId=row_id, conflicts=conflicts))
            bindings.append(dict(rowId=row_id, command=command, commandType=command_type, shortcut=_key(key), modifier=modifier))
    return dict(success=not issues, userDir=str(root), bindings=bindings, issues=issues)


def plan(user_dir, require):
    required = _required(require)
    confirmed_types = _confirmed_types(require)
    root, menu, tools = _snapshot(user_dir)
    occupied = {(_key(row[3]), row[4] or 0) for row in menu if _assigned(row[3])}
    tool_keys = _tool_keys(tools)
    bindings = []
    check = verify(root, required)
    issues = [i for i in check["issues"] if i["code"] != "ShortcutsMissing"]
    for command in required:
        rows = [r for r in menu if r[2] == command]
        if any(_assigned(r[3]) for r in rows):
            continue
        if command not in WRITABLE_COMMANDS:
            issues.append(dict(code="ShortcutsMissing", command=command))
            continue
        types = {r[1] for r in rows if r[1]}
        if command in confirmed_types:
            types.add(confirmed_types[command])
        if len(types) != 1:
            issues.append(dict(code="CommandTypeUnconfirmed", command=command))
            continue
        available = next((key for key in VIRTUAL_KEYS if (key, 7) not in occupied and key not in tool_keys), None)
        if available is None:
            issues.append(dict(code="VirtualKeysExhausted", command=command))
            continue
        row = next((r for r in rows if not _assigned(r[3])), None)
        occupied.add((available, 7))
        bindings.append(dict(command=command, commandType=next(iter(types)), rowId=row[0] if row else None,
                             shortcut=available, modifier=7, original=row))
    result = dict(schema="memoline-shortcut-plan/v1", userDir=str(root), require=required,
                  snapshotHash=_digest(dict(menu=menu, tools=tools)), bindings=bindings, issues=issues)
    if confirmed_types:
        result["require"] = dict(commands=required, confirmedCommandTypes=confirmed_types)
    result["confirmToken"] = _digest(result)
    return result


def csp_running():
    if os.name != "nt":
        raise ShortcutWriterError("ProcessCheckUnavailable", "此写入流程要求 Windows CSP 进程检查")
    result = subprocess.run(["tasklist.exe", "/FI", "IMAGENAME eq CLIPStudioPaint.exe", "/FO", "CSV", "/NH"],
                            capture_output=True, text=True, check=False,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode != 0:
        raise ShortcutWriterError("ProcessCheckUnavailable", "无法检查 CSP 是否退出")
    return "clipstudiopaint.exe" in result.stdout.lower()


def _ensure_stopped():
    if csp_running():
        raise ShortcutWriterError("CspRunning", "请关闭 CSP 后再写入或回滚快捷键")


def _write_manifest(path, manifest):
    temp = path.with_suffix(".json.tmp")
    with temp.open("wb") as stream:
        stream.write(_canonical(manifest) + b"\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temp, path)


def _restore(db, changes, allow_original=False):
    active = []
    for change in changes:
        current = db.execute("SELECT _PW_ID,menucommandtype,menucommand,shortcut,modifier FROM shortcutmenu WHERE _PW_ID=?", (change["after"][0],)).fetchone()
        if allow_original and (list(current) if current else None) == change["before"]:
            continue
        if current is None or list(current) != change["after"]:
            raise ShortcutWriterError("ProvisionedBindingChanged", "已写入行被修改，不能覆盖用户的新配置")
        active.append(change)
    for change in active:
        before = change["before"]
        if before is None:
            db.execute("DELETE FROM shortcutmenu WHERE _PW_ID=?", (change["after"][0],))
        else:
            db.execute("UPDATE shortcutmenu SET menucommandtype=?,menucommand=?,shortcut=?,modifier=? WHERE _PW_ID=?", (*before[1:], before[0]))


def apply(user_dir, proposed, confirm_token):
    _ensure_stopped()
    root, khc, _ = _paths(user_dir)
    if not isinstance(proposed, dict) or proposed.get("userDir") != str(root):
        raise ShortcutWriterError("InvalidPlan", "计划与目标用户目录不一致")
    expected = plan(root, proposed.get("require"))
    if proposed != expected or not confirm_token or confirm_token != expected["confirmToken"]:
        raise ShortcutWriterError("StalePlan", "计划已变化或缺少匹配的确认令牌，请重新检查")
    if proposed["issues"]:
        raise ShortcutWriterError("PlanUnconfirmed", "计划包含未确认项目，不能写入")
    if not proposed["bindings"]:
        return dict(success=True, changed=0, verification=verify(root, proposed["require"]))
    manifest_path = khc.parent / "memoline-provisioned.json"
    if manifest_path.exists():
        existing = json.loads(manifest_path.read_text(encoding="utf-8"))
        if existing.get("status") != "rolledBack":
            raise ShortcutWriterError("ProvisioningAlreadyActive", "已有未回滚写入清单，请先处理该清单")
    backup = khc.parent / ("memoline-backup-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ"))
    backup.mkdir()
    backup_hashes = {}
    for suffix in ("", "-wal", "-journal"):
        source = Path(str(khc) + suffix)
        if source.exists():
            target = backup / source.name
            shutil.copy2(source, target)
            with target.open("r+b") as stream:
                os.fsync(stream.fileno())
            if source.read_bytes() != target.read_bytes():
                raise ShortcutWriterError("BackupMismatch", "快捷键备份读回不一致")
            backup_hashes[target.name] = hashlib.sha256(target.read_bytes()).hexdigest()
    changes = []
    manifest = dict(schema="memoline-shortcut-provisioned/v1", status="pending", userDir=str(root),
                    backupPath=str(backup), backupHashes=backup_hashes, require=proposed["require"],
                    planHash=proposed["confirmToken"], changes=changes)
    with closing(sqlite3.connect(khc)) as db:
        db.execute("BEGIN IMMEDIATE")
        try:
            for binding in proposed["bindings"]:
                if binding["rowId"] is not None:
                    row = db.execute("SELECT _PW_ID,menucommandtype,menucommand,shortcut,modifier FROM shortcutmenu WHERE _PW_ID=?", (binding["rowId"],)).fetchone()
                    if row is None or list(row) != binding["original"] or _assigned(row[3]):
                        raise ShortcutWriterError("StalePlan", "目标行已经变化，不能覆盖")
                    db.execute("UPDATE shortcutmenu SET shortcut=?,modifier=? WHERE _PW_ID=?", (binding["shortcut"], binding["modifier"], binding["rowId"]))
                    identity = binding["rowId"]
                else:
                    identity = db.execute("INSERT INTO shortcutmenu(menucommandtype,menucommand,shortcut,modifier) VALUES(?,?,?,?)", (binding["commandType"], binding["command"], binding["shortcut"], binding["modifier"])).lastrowid
                after = [identity, binding["commandType"], binding["command"], binding["shortcut"], binding["modifier"]]
                changes.append(dict(before=binding["original"], after=after))
            _write_manifest(manifest_path, manifest)
            db.commit()
        except Exception:
            db.rollback()
            manifest["status"] = "rolledBack"
            _write_manifest(manifest_path, manifest)
            raise
        try:
            rows = read_menu_shortcuts(khc)
            for change in changes:
                row = change["after"]
                if not any(r["id"] == f"menu_{row[0]}" and r["command"] == row[2] and r["shortcut"] == f"Ctrl + Shift + Alt + {row[3]}" for r in rows):
                    raise ShortcutWriterError("ReadbackMismatch", "现有快捷键读取器未确认新绑定")
            verification = verify(root, proposed["require"])
            if not verification["success"]:
                raise ShortcutWriterError("ReadbackMismatch", "写入后快捷键校验失败")
        except Exception:
            db.execute("BEGIN IMMEDIATE")
            try:
                _restore(db, changes)
                db.commit()
            except Exception:
                db.rollback()
                raise
            manifest["status"] = "rolledBack"
            _write_manifest(manifest_path, manifest)
            raise
    manifest["status"] = "applied"
    _write_manifest(manifest_path, manifest)
    return dict(success=True, changed=len(changes), backupPath=str(backup), verification=verification)


def rollback(user_dir):
    _ensure_stopped()
    root, khc, _ = _paths(user_dir)
    path = khc.parent / "memoline-provisioned.json"
    if not path.is_file():
        raise ShortcutWriterError("ProvisioningManifestMissing", "找不到快捷键写入清单")
    manifest = json.loads(path.read_text(encoding="utf-8"))
    if manifest.get("schema") != "memoline-shortcut-provisioned/v1" or manifest.get("userDir") != str(root):
        raise ShortcutWriterError("InvalidManifest", "清单与用户目录不一致")
    if manifest.get("status") == "rolledBack":
        return dict(success=True, changed=0)
    backup = Path(manifest.get("backupPath", "")).resolve()
    if backup.parent != khc.parent or not backup.name.startswith("memoline-backup-"):
        raise ShortcutWriterError("InvalidManifest", "备份目录必须位于该用户的 Shortcut 目录内")
    hashes = manifest.get("backupHashes", {})
    if "default.khc" not in hashes or any(name not in ("default.khc", "default.khc-wal", "default.khc-journal") for name in hashes):
        raise ShortcutWriterError("InvalidManifest", "清单缺少有效的备份哈希")
    for name, expected in hashes.items():
        source = backup / name
        if not source.is_file() or hashlib.sha256(source.read_bytes()).hexdigest() != expected:
            raise ShortcutWriterError("BackupMismatch", "快捷键备份校验失败，不能回滚")
    originals = {row[0]: row for row in _read(backup / "default.khc", "SELECT _PW_ID,menucommandtype,menucommand,shortcut,modifier FROM shortcutmenu")}
    changes = manifest.get("changes")
    if not isinstance(changes, list) or len(changes) > len(WRITABLE_COMMANDS):
        raise ShortcutWriterError("InvalidManifest", "清单中的写入行无效")
    seen = set()
    confirmed_types = _confirmed_types(manifest.get("require"))
    for change in changes:
        before, after = change.get("before"), change.get("after")
        if not isinstance(after, list) or len(after) != 5 or not isinstance(after[0], int) or after[0] in seen or after[2] not in WRITABLE_COMMANDS or after[3] not in VIRTUAL_KEYS or after[4] != 7:
            raise ShortcutWriterError("InvalidManifest", "清单包含非记艺线写入行")
        seen.add(after[0])
        if before is None:
            if after[0] in originals or confirmed_types.get(after[2]) != after[1]:
                raise ShortcutWriterError("InvalidManifest", "插入行缺少已确认的命令类型")
        elif originals.get(after[0]) != before or _assigned(before[3]) or before[0:3] != after[0:3]:
            raise ShortcutWriterError("InvalidManifest", "清单原值与备份的空绑定不一致")
    with closing(sqlite3.connect(khc)) as db:
        db.execute("BEGIN IMMEDIATE")
        try:
            _restore(db, manifest["changes"], allow_original=manifest.get("status") == "pending")
            db.commit()
        except Exception:
            db.rollback()
            raise
    manifest["status"] = "rolledBack"
    _write_manifest(path, manifest)
    return dict(success=True, changed=len(manifest["changes"]))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    for action in ("plan", "apply", "verify", "rollback"):
        command = sub.add_parser(action)
        command.add_argument("--user-dir", required=True)
        if action in ("plan", "verify"):
            command.add_argument("--require", required=True, help="JSON menucommand 数组")
        if action == "apply":
            command.add_argument("--plan", required=True, help="plan 返回的完整 JSON")
            command.add_argument("--confirm-token", required=True)
    args = parser.parse_args(argv)
    try:
        if args.action == "apply":
            result = apply(args.user_dir, json.loads(args.plan), args.confirm_token)
        elif args.action == "rollback":
            result = rollback(args.user_dir)
        else:
            result = globals()[args.action](args.user_dir, json.loads(args.require))
        print(json.dumps(result, ensure_ascii=False, separators=(",", ":")))
        return 0 if result.get("success", not result.get("issues")) else 1
    except (ShortcutWriterError, sqlite3.Error, OSError, json.JSONDecodeError) as ex:
        print(json.dumps(dict(success=False, code=getattr(ex, "code", "ShortcutWriterFailure"), message=str(ex)), ensure_ascii=False, separators=(",", ":")))
        return 1


if __name__ == "__main__":
    sys.exit(main())
