"""Make the recognizer modules this probe reuses importable."""
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
RECOGNIZER = HERE.parent
# Source tree: recognizer/virtual_panel_probe. Memoline's package: Recognizer/integration/panel_stash,
# beside the recorder's private Python libraries.
for path in (RECOGNIZER.parent / "CSP_Shortcut_Manager", RECOGNIZER / "recorder_integration",
             RECOGNIZER / "csp_panel_validator", RECOGNIZER,
             RECOGNIZER / "recorder_integration" / "python_libs", RECOGNIZER / "python_libs"):
    if str(path) not in sys.path:
        sys.path.insert(0, str(path))
