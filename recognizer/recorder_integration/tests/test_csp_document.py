import os
import sqlite3
import sys
import tempfile
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(HERE)]
import csp_document


class CspDocumentTests(unittest.TestCase):
    """CSP's own record of its last document warns when Memoline watches another .clip (2026-10-24)."""

    def user_dir(self, directory, path):
        folder = Path(directory) / "PreviousDocument"
        folder.mkdir()
        with closing(sqlite3.connect(folder / "lastmodifiedcanvaspath")) as db:
            db.execute("create table lastmodifiedcanvaspathcategory (_PW_ID integer primary key autoincrement, "
                       "lastmodifiedcanvaspath text, lastmodifiedcanvaspathbookmark blob)")
            db.execute("insert into lastmodifiedcanvaspathcategory (lastmodifiedcanvaspath) values (?)", (path,))
            db.commit()
        return {"MEMOLINE_CSP_USER_DIR": directory}

    def test_another_document_is_reported(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.dict(os.environ, self.user_dir(directory, r"D:\art\memolinetest10.clip")):
                warning = csp_document.mismatch(r"D:\art\memoline test9.clip")
        self.assertEqual(warning["cspLastDocument"], r"D:\art\memolinetest10.clip")
        self.assertIn("memoline test9.clip", warning["message"])

    def test_the_same_document_in_another_spelling_is_not(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.dict(os.environ, self.user_dir(directory, r"D:\Art\MemolineTest10.clip")):
                self.assertIsNone(csp_document.mismatch(r"d:\art\memolinetest10.clip"))

    def test_no_record_says_nothing(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.dict(os.environ, {"MEMOLINE_CSP_USER_DIR": directory}):
                self.assertIsNone(csp_document.mismatch(r"D:\art\a.clip"))


if __name__ == "__main__":
    unittest.main()
