from __future__ import annotations

from typing import Any

from PySide6.QtCore import Qt
from PySide6.QtWidgets import QDialog, QDialogButtonBox, QLabel, QListWidget, QListWidgetItem, QMessageBox, QPushButton, QVBoxLayout

from .models import TargetWindowProfile
from .window_service import WindowInfo, enumerate_csp_windows, match_windows


class WindowSelectorDialog(QDialog):
    """Explicitly binds one concrete CSP top-level HWND for the current run."""

    def __init__(self, expected: TargetWindowProfile | None = None, parent: Any = None):
        super().__init__(parent)
        self.setWindowTitle("选择目标 CSP 窗口")
        self.resize(820, 430)
        self.expected = expected
        self.windows: list[WindowInfo] = []
        self.info_label = QLabel()
        self.info_label.setWordWrap(True)
        self.list = QListWidget()
        self.refresh_button = QPushButton("重新查找")
        self.refresh_button.clicked.connect(self.refresh)
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout = QVBoxLayout(self)
        layout.addWidget(QLabel("请选择具体的 CSP 顶层窗口。窗口句柄只在本次运行使用，配置会保存进程名、窗口类、标题和客户区尺寸用于下次重新匹配。仅捕获所选窗口的客户区；如果独立浮动面板属于另一个顶层窗口，请另选该窗口，不能用整屏裁剪代替。"))
        layout.addWidget(self.info_label)
        layout.addWidget(self.list, 1)
        layout.addWidget(self.refresh_button)
        layout.addWidget(buttons)
        self.refresh()

    def refresh(self) -> None:
        self.windows = enumerate_csp_windows()
        self.list.clear()
        if not self.windows:
            self.info_label.setText("未找到可用的 CLIP STUDIO PAINT 窗口。请确认 CSP 已启动且存在可见顶层窗口，然后点击“重新查找”。不会退回整屏识别。")
            return
        self.info_label.setText(f"找到 {len(self.windows)} 个候选窗口；必须确认具体窗口，多个窗口不会按进程名自动合并。")
        preferred = {item.hwnd for item in match_windows(self.expected, self.windows)} if self.expected else set()
        preferred_index = -1
        for index, window in enumerate(self.windows):
            item = QListWidgetItem(window.display_name())
            item.setData(Qt.ItemDataRole.UserRole, window)
            self.list.addItem(item)
            if window.hwnd in preferred:
                item.setSelected(True)
                preferred_index = index
        if preferred_index >= 0:
            self.list.setCurrentRow(preferred_index)
        elif self.list.currentRow() < 0:
            self.list.setCurrentRow(0)

    def selected_window(self) -> WindowInfo | None:
        item = self.list.currentItem()
        return item.data(Qt.ItemDataRole.UserRole) if item else None

    def accept(self) -> None:
        window = self.selected_window()
        if window is None:
            QMessageBox.warning(self, "未选择窗口", "请选择一个具体 CSP 窗口；没有候选窗口时请点击“重新查找”。")
            return
        if window.minimized:
            QMessageBox.warning(self, "窗口不可捕获", "所选 CSP 窗口已最小化，请恢复窗口后再确认。")
            return
        super().accept()
