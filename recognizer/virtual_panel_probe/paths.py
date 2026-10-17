"""Make the recognizer modules this probe reuses importable."""
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
RECOGNIZER = HERE.parent
for path in (RECOGNIZER.parent / "CSP_Shortcut_Manager", RECOGNIZER / "recorder_integration",
             RECOGNIZER / "csp_panel_validator", RECOGNIZER):
    if str(path) not in sys.path:
        sys.path.insert(0, str(path))
