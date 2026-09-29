from __future__ import annotations

import sys
from pathlib import Path

from PySide6.QtWidgets import QApplication

from .ui import MainWindow
from .window_service import enable_per_monitor_dpi_awareness


def main() -> int:
    enable_per_monitor_dpi_awareness()
    app = QApplication(sys.argv)
    window = MainWindow(Path(__file__).resolve().parents[1])
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
