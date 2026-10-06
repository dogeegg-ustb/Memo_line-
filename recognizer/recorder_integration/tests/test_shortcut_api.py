import hashlib
import json
import os
import sqlite3
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT), str(ROOT.parent.parent / "CSP_Shortcut_Manager")]
from shortcut_api import read_configuration


class ShortcutApiTests(unittest.TestCase):
    def setUp(self):
        self.temporary_parent = Path(os.environ.get("MEMOLINE_TEST_TMPDIR", tempfile.gettempdir())).resolve()
        self.temporary_root = self.temporary_parent / ("memoline-shortcuts-" + uuid.uuid4().hex)
        self.temporary_root.mkdir()
        self.addCleanup(self.cleanup)
        self.root = self.temporary_root / "CSP 配置"
        (self.root / "Shortcut").mkdir(parents=True)
        (self.root / "Tool").mkdir()
        self.menu = self.root / "Shortcut/default.khc"
        with closing(sqlite3.connect(self.menu)) as db, db:
            db.execute("CREATE TABLE shortcutmenu (_PW_ID INTEGER, menucommandtype TEXT, menucommand TEXT, shortcut TEXT, modifier INTEGER)")
            db.executemany("INSERT INTO shortcutmenu VALUES (?, ?, ?, ?, ?)", [
                (1, "basiccommand", "redo", "G", 4),
                (2, "basiccommand", "redo", "Z", 6),
                (3, "advancedcommand", "filesave", "S", 4),
                (4, "basiccommand", "undo", "NULL", 0),
            ])
        with closing(sqlite3.connect(self.root / "Tool/EditImageTool.todb")) as db, db:
            db.execute("CREATE TABLE Node (_PW_ID INTEGER, NodeName TEXT, NodeShortCutKey INTEGER, NodeDefaultIdentifier INTEGER, NodeUuid BLOB, NodeFirstChildUuid BLOB, NodeNextUuid BLOB, NodeSelectedUuid BLOB, NodeHidden INTEGER, NodeMaterialContentID TEXT, NodeMaterialUuid TEXT, NodeMaterialIDExists INTEGER)")
            uid = lambda number: number.to_bytes(16,'big') if number else b'\0'
            db.executemany("INSERT INTO Node VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", [
                (100,'',0,0,uid(100),uid(1),uid(0),uid(1),0,None,None,0),
                (1,'毛笔',2,20,uid(1),uid(11),uid(2),uid(11),0,None,None,0),
                (2,'喷枪',2,21,uid(2),uid(21),uid(0),uid(21),0,None,None,0),
                (11,'沾水笔',0,0,uid(11),uid(12),uid(14),uid(12),0,None,None,0),
                (12,'G筆',0,0,uid(12),uid(0),uid(13),uid(0),0,'asset-content','asset-uuid',1),
                (13,'圓筆',0,0,uid(13),uid(0),uid(0),uid(0),0,None,None,0),
                (14,'麦克笔',0,0,uid(14),uid(15),uid(0),uid(15),0,None,None,0),
                (15,'签字笔',0,0,uid(15),uid(0),uid(0),uid(0),0,None,None,0),
                (21,'喷枪组',0,0,uid(21),uid(22),uid(0),uid(22),0,None,None,0),
                (22,'G筆',0,0,uid(22),uid(0),uid(0),uid(0),0,None,None,0),
            ])
            db.execute('CREATE TABLE Manager (RootUuid BLOB,CurrentNodeUuid BLOB)')
            db.execute('INSERT INTO Manager VALUES (?,?)',(uid(100),uid(12)))
        with closing(sqlite3.connect(self.root / "Shortcut/DefaultToolModifyKey.tomd")) as db, db:
            db.execute("CREATE TABLE ModifyKeySetting (InputOperation INTEGER, OutputOperation INTEGER, RangeOperation INTEGER, SettingData BLOB)")

    def hashes(self):
        return {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in self.root.rglob("*") if p.is_file()}

    def cleanup(self):
        resolved = self.temporary_root.resolve()
        if resolved.parent == self.temporary_parent and resolved.name.startswith("memoline-shortcuts-"):
            shutil.rmtree(resolved)

    def test_saved_mapping_keeps_duplicates_unicode_and_configuration_unchanged(self):
        before = self.hashes()
        result = read_configuration(self.root)
        self.assertEqual(result["source"], "savedCspConfiguration")
        self.assertEqual(result["configRoot"], str(self.root.resolve()))
        self.assertEqual(result["warnings"], [])
        self.assertEqual([e["shortcut"] for e in result["bindings"]], ["Ctrl + G", "Ctrl + Shift + Z", "Ctrl + S", "B", "B"])
        self.assertEqual([e["action_name"] for e in result["bindings"]], ["重做", "重做", "保存", "毛笔", "喷枪"])
        self.assertEqual(len(result["sourceFiles"]), 3)
        self.assertTrue(all(e["sourceFile"] in result["sourceFiles"] for e in result["bindings"]))
        self.assertEqual(before, self.hashes())

    def test_auto_discovery_and_each_query_reads_the_latest_saved_config(self):
        with patch.dict(os.environ, {"MEMOLINE_CSP_USER_DIR": str(self.root)}):
            first = read_configuration()
            with closing(sqlite3.connect(self.menu)) as db, db:
                db.execute("UPDATE shortcutmenu SET shortcut='Y' WHERE _PW_ID=1")
            second = read_configuration()
        self.assertEqual(first["bindings"][0]["shortcut"], "Ctrl + G")
        self.assertEqual(second["bindings"][0]["shortcut"], "Ctrl + Y")

    def test_tool_shortcuts_expand_all_groups_and_subtools_without_assigning_fake_keys(self):
        before = self.hashes()
        result = read_configuration(self.root)
        binding = next(entry for entry in result['bindings'] if entry['id']=='tool_1')
        self.assertEqual(binding['tool']['kind'],'tool')
        self.assertEqual([node['name'] for node in binding['subtools']],['G筆','圓筆','签字笔'])
        self.assertEqual(binding['subtools'][0]['path'],['毛笔','沾水笔','G筆'])
        self.assertEqual(binding['savedSelectedSubtoolId'],'tool_12')
        self.assertTrue(all(node['shortcut'] is None for node in binding['subtools']))
        self.assertEqual(binding['subtools'][0]['material']['contentId'],'asset-content')
        self.assertEqual(result['toolCatalog']['savedCurrentNodeIds'],['tool_12'])
        self.assertEqual(result['brushPackageSource'],'installedCspToolGroups')
        self.assertEqual([group['name'] for group in result['brushPackages']],['沾水笔','麦克笔','喷枪组'])
        self.assertEqual(before,self.hashes())

    def test_a_direct_subtool_binding_retains_its_own_identity(self):
        with closing(sqlite3.connect(self.root / 'Tool/EditImageTool.todb')) as db,db:
            db.execute('UPDATE Node SET NodeShortCutKey=7 WHERE _PW_ID=12')
        result = read_configuration(self.root)
        binding = next(entry for entry in result['bindings'] if entry['id']=='tool_12')
        self.assertEqual(binding['shortcut'],'G')
        self.assertEqual(binding['tool']['kind'],'subtool')
        self.assertEqual(binding['tool']['toolId'],'tool_1')
        self.assertEqual([node['id'] for node in binding['subtools']],['tool_12'])

    def test_cyclic_sibling_links_report_partial_inventory_without_hanging(self):
        with closing(sqlite3.connect(self.root / 'Tool/EditImageTool.todb')) as db,db:
            db.execute('UPDATE Node SET NodeNextUuid=? WHERE _PW_ID=13',(int(12).to_bytes(16,'big'),))
        result = read_configuration(self.root)
        self.assertEqual(result['toolCatalog']['status'],'partial')
        self.assertTrue(any('循环' in warning for warning in result['warnings']))
        binding = next(entry for entry in result['bindings'] if entry['id']=='tool_1')
        self.assertEqual(len(binding['subtools']),3)

    def test_unavailable_inventory_does_not_create_or_invent_a_library(self):
        path = self.root / 'Tool/EditImageTool.todb'
        path.unlink()
        result = read_configuration(self.root)
        self.assertEqual(result['toolCatalog']['status'],'unavailable')
        self.assertEqual(result['toolCatalog']['nodes'],[])
        self.assertEqual(result['brushPackages'],[])
        self.assertFalse(path.exists())

    def test_missing_file_is_a_warning_without_default_bindings_or_new_files(self):
        self.menu.unlink()
        result = read_configuration(self.root)
        self.assertEqual([e["shortcut"] for e in result["bindings"]], ["B", "B"])
        self.assertTrue(any("default.khc" in warning for warning in result["warnings"]))
        self.assertFalse(self.menu.exists())

    def test_standalone_cli_emits_json_and_reports_a_missing_directory(self):
        cli = [sys.executable, "-X", "utf8", str(ROOT / "shortcut_api.py"), "--config-dir"]
        result = subprocess.run(cli + [str(self.root)], capture_output=True, text=True, encoding="utf-8", timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stderr, "")
        self.assertEqual(json.loads(result.stdout)["bindings"][0]["action_name"], "重做")
        missing = self.root / "不存在"
        result = subprocess.run(cli + [str(missing)], capture_output=True, text=True, encoding="utf-8", timeout=15)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout, "")
        self.assertFalse(json.loads(result.stderr)["success"])
        self.assertFalse(missing.exists())
