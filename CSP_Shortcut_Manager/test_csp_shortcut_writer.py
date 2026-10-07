import sqlite3
import sys
import json
import tempfile
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import csp_shortcut_writer as writer


class ShortcutWriterTests(unittest.TestCase):
    def setUp(self):
        fixture_root = Path(__file__).resolve().parent / "tests" / "artifacts"
        fixture_root.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=fixture_root)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "Shortcut").mkdir()
        (self.root / "Tool").mkdir()
        self.khc = self.root / "Shortcut/default.khc"
        with closing(sqlite3.connect(self.khc)) as db, db:
            db.execute("CREATE TABLE shortcutmenu (_PW_ID INTEGER PRIMARY KEY AUTOINCREMENT, menucommandtype TEXT, menucommand TEXT, shortcut TEXT, modifier INTEGER)")
            db.executemany("INSERT INTO shortcutmenu(menucommandtype,menucommand,shortcut,modifier) VALUES(?,?,?,?)", [
                ("basiccommand", "filesave", "S", 4), ("basiccommand", "paste", "V", 4),
                ("advancedcommand", "layerdelete", None, 0), ("advancedcommand", "layervisiblelayer", "", 0),
                ("basiccommand", "unrelated", "F9", 7)])
        with closing(sqlite3.connect(self.root / "Tool/EditImageTool.todb")) as db, db:
            db.execute("CREATE TABLE Node (_PW_ID INTEGER PRIMARY KEY, NodeName TEXT, NodeShortCutKey INTEGER)")
            db.execute("INSERT INTO Node VALUES(1,'tool',121)")  # F10 is reserved by a tool.
        self.required = ["filesave", "paste", "layerdelete", "layervisiblelayer"]

    def rows(self):
        with closing(sqlite3.connect(self.khc)) as db, db:
            return db.execute("SELECT * FROM shortcutmenu ORDER BY _PW_ID").fetchall()

    def apply(self, plan):
        with patch.object(writer, "csp_running", return_value=False):
            return writer.apply(self.root, plan, plan["confirmToken"])

    def test_plan_is_read_only_and_uses_free_virtual_keys(self):
        before = self.khc.read_bytes()
        plan = writer.plan(self.root, self.required)
        self.assertEqual(before, self.khc.read_bytes())
        self.assertEqual([b["shortcut"] for b in plan["bindings"]], ["F11", "F12"])
        self.assertEqual(plan["issues"], [])

    def test_apply_verify_rollback_and_existing_bindings(self):
        before = self.rows()
        plan = writer.plan(self.root, self.required)
        result = self.apply(plan)
        self.assertTrue(result["success"])
        self.assertTrue(writer.verify(self.root, self.required)["success"])
        self.assertEqual(self.rows()[0:2], before[0:2])
        backup = Path(result["backupPath"])
        self.assertTrue((backup / "default.khc").is_file())
        self.assertTrue((self.root / "Shortcut/memoline-provisioned.json").is_file())
        with patch.object(writer, "csp_running", return_value=False):
            self.assertTrue(writer.rollback(self.root)["success"])
        self.assertEqual(self.rows(), before)

    def test_running_csp_and_wrong_confirmation_cannot_write(self):
        plan = writer.plan(self.root, self.required)
        before = self.khc.read_bytes()
        with patch.object(writer, "csp_running", return_value=True):
            with self.assertRaises(writer.ShortcutWriterError):
                writer.apply(self.root, plan, plan["confirmToken"])
        with patch.object(writer, "csp_running", return_value=False):
            with self.assertRaises(writer.ShortcutWriterError):
                writer.apply(self.root, plan, "wrong")
        self.assertEqual(before, self.khc.read_bytes())

    def test_stale_plan_and_unknown_command_type_are_rejected(self):
        plan = writer.plan(self.root, self.required)
        with closing(sqlite3.connect(self.khc)) as db, db:
            db.execute("UPDATE shortcutmenu SET modifier=6 WHERE menucommand='paste'")
        with self.assertRaises(writer.ShortcutWriterError):
            self.apply(plan)
        with closing(sqlite3.connect(self.khc)) as db, db:
            db.execute("DELETE FROM shortcutmenu WHERE menucommand='layerdelete'")
        plan = writer.plan(self.root, self.required)
        self.assertIn("CommandTypeUnconfirmed", [i["code"] for i in plan["issues"]])

    def test_verify_detects_conflicts_and_bad_sendable_keys(self):
        self.apply(writer.plan(self.root, self.required))
        with closing(sqlite3.connect(self.khc)) as db, db:
            db.execute("INSERT INTO shortcutmenu(menucommandtype,menucommand,shortcut,modifier) VALUES('basiccommand','conflict','F11',7)")
        self.assertFalse(writer.verify(self.root, self.required)["success"])
        with closing(sqlite3.connect(self.khc)) as db, db:
            db.execute("UPDATE shortcutmenu SET shortcut='UNKNOWN' WHERE menucommand='paste'")
        self.assertIn("ShortcutUnsendable", [i["code"] for i in writer.verify(self.root, self.required)["issues"]])

    def test_rollback_does_not_overwrite_a_user_change(self):
        self.apply(writer.plan(self.root, self.required))
        with closing(sqlite3.connect(self.khc)) as db, db:
            db.execute("UPDATE shortcutmenu SET shortcut='F8' WHERE menucommand='layerdelete'")
        with patch.object(writer, "csp_running", return_value=False):
            with self.assertRaises(writer.ShortcutWriterError):
                writer.rollback(self.root)
        self.assertEqual(self.rows()[2][3], "F8")

    def test_readback_failure_restores_transaction(self):
        plan = writer.plan(self.root, self.required)
        before = self.rows()
        with patch.object(writer, "csp_running", return_value=False), patch.object(writer, "read_menu_shortcuts", return_value=[]):
            with self.assertRaises(writer.ShortcutWriterError):
                writer.apply(self.root, plan, plan["confirmToken"])
        self.assertEqual(self.rows(), before)

    def test_insert_requires_an_explicitly_confirmed_command_type(self):
        with closing(sqlite3.connect(self.khc)) as db, db:
            db.execute("DELETE FROM shortcutmenu WHERE menucommand='layerdelete'")
        require = dict(commands=self.required, confirmedCommandTypes={"layerdelete": "advancedcommand"})
        plan = writer.plan(self.root, require)
        binding = next(b for b in plan["bindings"] if b["command"] == "layerdelete")
        self.assertIsNone(binding["rowId"])
        self.assertTrue(self.apply(plan)["success"])
        with patch.object(writer, "csp_running", return_value=False):
            self.assertTrue(writer.rollback(self.root)["success"])
        self.assertFalse(any(row[2] == "layerdelete" for row in self.rows()))

    def test_tampered_rollback_manifest_cannot_restore_arbitrary_bindings(self):
        self.apply(writer.plan(self.root, self.required))
        path = self.root / "Shortcut/memoline-provisioned.json"
        manifest = json.loads(path.read_text(encoding="utf-8"))
        manifest["changes"][0]["before"][3] = "F8"
        path.write_text(json.dumps(manifest), encoding="utf-8")
        before = self.rows()
        with patch.object(writer, "csp_running", return_value=False):
            with self.assertRaises(writer.ShortcutWriterError):
                writer.rollback(self.root)
        self.assertEqual(self.rows(), before)


if __name__ == "__main__":
    unittest.main()
