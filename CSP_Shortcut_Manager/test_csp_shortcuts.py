import sqlite3
import unittest
from pathlib import Path
from unittest.mock import patch

from csp_shortcuts import read_menu_shortcuts, read_tool_shortcuts


class ShortcutReaderTests(unittest.TestCase):
    def test_menu_shortcuts_keep_saved_keys_and_multiple_bindings(self):
        conn = sqlite3.connect(":memory:")
        conn.execute(
            "CREATE TABLE shortcutmenu (_PW_ID INTEGER, menucommandtype TEXT, "
            "menucommand TEXT, shortcut TEXT, modifier INTEGER)"
        )
        conn.executemany(
            "INSERT INTO shortcutmenu VALUES (?, ?, ?, ?, ?)",
            [
                (1, "basiccommand", "redo", "G", 4),
                (2, "basiccommand", "redo", "Z", 6),
                (3, "basiccommand", "undo", "NULL", 0),
            ],
        )
        with patch.object(Path, "is_file", return_value=True), patch("csp_shortcuts.sqlite3.connect", return_value=conn):
            rows = read_menu_shortcuts("test.khc")
            self.assertEqual([row["shortcut"] for row in rows], ["Ctrl + G", "Ctrl + Shift + Z"])
            self.assertEqual([row["action_name"] for row in rows], ["重做", "重做"])
            self.assertEqual(len(read_menu_shortcuts("test.khc", include_unassigned=True)), 3)
        conn.close()

    def test_tool_shortcuts_read_node_keys(self):
        conn = sqlite3.connect(":memory:")
        conn.execute("CREATE TABLE Node (_PW_ID INTEGER, NodeName TEXT, NodeShortCutKey INTEGER)")
        conn.execute("INSERT INTO Node VALUES (7, '画笔', 2)")
        with patch.object(Path, "is_file", return_value=True), patch("csp_shortcuts.sqlite3.connect", return_value=conn):
            self.assertEqual(read_tool_shortcuts("test.todb")[0]["shortcut"], "B")
        conn.close()


if __name__ == "__main__":
    unittest.main()
