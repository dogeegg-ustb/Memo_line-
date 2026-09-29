from __future__ import annotations

import sys

from PySide6.QtWidgets import QApplication

from .mini_ui import MainWindow


def main() -> int:
    app = QApplication(sys.argv)
    app.setApplicationName("CSP 图层识别（精简版）")
    window = MainWindow()
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
