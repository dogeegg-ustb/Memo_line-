import re
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parents[1]
BRIDGE = HERE.parent / "Recognizer" / "src" / "BehaviorRecognizer" / "Capture" / "UpdateActivatorBridge.cs"
SKIP = {"tests", "python_libs", "transform_host", "__pycache__"}


def recorded_kinds():
    kinds = {}
    for path in HERE.rglob("*.py"):
        if SKIP.intersection(path.relative_to(HERE).parts):
            continue
        for kind in re.findall(r'\brecord\(\s*"([A-Za-z]+)"', path.read_text(encoding="utf-8")):
            kinds.setdefault(kind, path.name)
    return kinds


def accepted_kinds():
    source = BRIDGE.read_text(encoding="utf-8-sig")
    block = re.search(r'if \(kind is not \((.*?)\)\)\s*throw new InvalidDataException\(\$"Unknown update message', source, re.S)
    if block is None:
        raise AssertionError("UpdateActivatorBridge no longer has the update message whitelist this check reads")
    return set(re.findall(r'"([A-Za-z]+)"', block.group(1)))


class UpdateMessageKindsTest(unittest.TestCase):
    def test_every_recorded_kind_is_accepted_by_the_recorder(self):
        # The recorder throws on an unlisted kind, which ends its reader: initialization then never completes
        # (2026-10-25, clipWriteObserved).
        recorded = recorded_kinds()
        self.assertIn("clipParseResult", recorded)
        missing = {kind: source for kind, source in recorded.items() if kind not in accepted_kinds()}
        self.assertEqual(missing, {}, "add these kinds to UpdateActivatorBridge.ReceiveAsync's whitelist")


if __name__ == "__main__":
    unittest.main()
