from __future__ import annotations

from typing import Any

import numpy as np

from PySide6.QtCore import QPoint, QRect, Qt, Signal
from PySide6.QtGui import QImage, QKeySequence, QPainter, QPen, QPixmap, QShortcut
from PySide6.QtWidgets import QDialog, QHBoxLayout, QLabel, QPushButton, QVBoxLayout, QWidget


class _FrozenCanvas(QWidget):
    selection_changed = Signal()

    def __init__(self, image: np.ndarray, initial_roi: tuple[int, int, int, int] | None = None):
        super().__init__()
        height, width = image.shape[:2]
        qimage = QImage(image.data, width, height, image.strides[0], QImage.Format.Format_BGR888).copy()
        self.pixmap = QPixmap.fromImage(qimage)
        self.image_width = width
        self.image_height = height
        self.selection = self._valid_roi(initial_roi)
        self._start: QPoint | None = None
        self._start_image: QPoint | None = None
        self._original: tuple[int, int, int, int] | None = None
        self._mode = "new"
        self._preview: tuple[int, int, int, int] | None = None
        self.setMouseTracking(True)
        self.setCursor(Qt.CursorShape.CrossCursor)

    def _valid_roi(self, roi: tuple[int, int, int, int] | None) -> tuple[int, int, int, int] | None:
        if roi is None or len(roi) != 4:
            return None
        x, y, width, height = (int(value) for value in roi)
        if x < 0 or y < 0 or width < 16 or height < 16 or x + width > self.image_width or y + height > self.image_height:
            return None
        return x, y, width, height

    def _to_image(self, point: QPoint) -> QPoint:
        sx = self.image_width / max(1, self.width())
        sy = self.image_height / max(1, self.height())
        return QPoint(round(point.x() * sx), round(point.y() * sy))

    def _to_widget_rect(self, roi: tuple[int, int, int, int]) -> QRect:
        x, y, width, height = roi
        sx = self.width() / self.image_width
        sy = self.height() / self.image_height
        return QRect(round(x * sx), round(y * sy), round(width * sx), round(height * sy))

    def paintEvent(self, event: Any) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform, False)
        painter.drawPixmap(self.rect(), self.pixmap)
        roi = self._preview or self.selection
        if roi:
            rect = self._to_widget_rect(roi)
            color = Qt.GlobalColor.yellow if self._preview else Qt.GlobalColor.green
            painter.setPen(QPen(color, 2))
            painter.setBrush(Qt.BrushStyle.NoBrush)
            painter.drawRect(rect)
            if not self._preview:
                handle = QRect(rect.right() - 9, rect.bottom() - 9, 10, 10)
                painter.fillRect(handle, color)
                painter.setPen(QPen(color, 1))
                painter.drawText(rect.topLeft() + QPoint(4, -5), "图层面板范围")

    def mousePressEvent(self, event: Any) -> None:
        if event.button() != Qt.MouseButton.LeftButton:
            return
        self._start = event.position().toPoint()
        self._start_image = self._to_image(self._start)
        self._original = self.selection
        self._preview = None
        self._mode = "new"
        if self.selection:
            rect = self._to_widget_rect(self.selection)
            if rect.contains(self._start):
                self._mode = "resize" if rect.bottomRight().x() - 18 <= self._start.x() and rect.bottomRight().y() - 18 <= self._start.y() else "move"
        self.update()

    def mouseMoveEvent(self, event: Any) -> None:
        point = event.position().toPoint()
        if self._start is None or self._start_image is None:
            if self.selection:
                rect = self._to_widget_rect(self.selection)
                if rect.bottomRight().x() - 18 <= point.x() and rect.bottomRight().y() - 18 <= point.y():
                    self.setCursor(Qt.CursorShape.SizeFDiagCursor)
                elif rect.contains(point):
                    self.setCursor(Qt.CursorShape.SizeAllCursor)
                else:
                    self.setCursor(Qt.CursorShape.CrossCursor)
            return
        image_point = self._to_image(point)
        sx, sy = self._start_image.x(), self._start_image.y()
        if self._mode == "new":
            x, y = min(sx, image_point.x()), min(sy, image_point.y())
            width, height = abs(image_point.x() - sx), abs(image_point.y() - sy)
            self._preview = (x, y, width, height) if width >= 16 and height >= 16 else None
        elif self._original:
            x, y, width, height = self._original
            if self._mode == "move":
                dx, dy = image_point.x() - sx, image_point.y() - sy
                x = max(0, min(self.image_width - width, x + dx))
                y = max(0, min(self.image_height - height, y + dy))
                self._preview = (x, y, width, height)
            else:
                width = max(16, min(self.image_width - x, image_point.x() - x))
                height = max(16, min(self.image_height - y, image_point.y() - y))
                self._preview = (x, y, width, height)
        self.update()

    def mouseReleaseEvent(self, event: Any) -> None:
        if self._start is None or event.button() != Qt.MouseButton.LeftButton:
            return
        self.mouseMoveEvent(event)
        if self._preview:
            self.selection = self._preview
            self.selection_changed.emit()
        self._start = None
        self._start_image = None
        self._original = None
        self._preview = None
        self.update()

    def delete_selection(self) -> None:
        if self.selection is not None:
            self.selection = None
            self.selection_changed.emit()
            self.update()


class LayerPanelSelectionDialog(QDialog):
    """Frozen screenshot overlay aligned over the original CSP client area."""

    def __init__(self, image: Any, window: Any, initial_roi: tuple[int, int, int, int] | None = None, parent=None):
        super().__init__(parent)
        self.image = image
        self.window = window
        self.setWindowTitle("冻结画面 · 框选图层面板")
        self.setWindowFlags(Qt.WindowType.Dialog | Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint)
        self.canvas = _FrozenCanvas(image, initial_roi)
        self.canvas.setParent(self)
        self.toolbar = QWidget(self)
        self.toolbar.setStyleSheet("QWidget {background: #252932; color: white;} QPushButton {padding: 7px 12px;}")
        toolbar_layout = QHBoxLayout(self.toolbar)
        toolbar_layout.setContentsMargins(10, 6, 10, 6)
        toolbar_layout.addWidget(QLabel("拖框选面板 · 拖动绿框可移动 · 右下角调整 · Delete 删除 · Enter 保存 · Esc 取消 · Tab 隐藏工具栏"))
        delete_button = QPushButton("删除选中框")
        delete_button.clicked.connect(self.canvas.delete_selection)
        toolbar_layout.addWidget(delete_button)
        save_button = QPushButton("保存并读取")
        save_button.clicked.connect(self.accept)
        toolbar_layout.addWidget(save_button)
        cancel_button = QPushButton("取消")
        cancel_button.clicked.connect(self.reject)
        toolbar_layout.addWidget(cancel_button)
        self.status_label = QLabel()
        self.status_label.setStyleSheet("color: #ffe082; background: rgba(37,41,50,210); padding: 4px 8px;")
        self.canvas.selection_changed.connect(self._update_status)
        self._update_status()
        dpr = max(float(getattr(window, "dpi", 96.0)) / 96.0, 1.0)
        height, width = image.shape[:2]
        self.resize(round(width / dpr), round(height / dpr))
        for key, action in (("Return", self.accept), ("Enter", self.accept), ("Escape", self.reject),
                            ("Delete", self.canvas.delete_selection), ("Tab", self.toggle_toolbar)):
            shortcut = QShortcut(QKeySequence(key), self)
            shortcut.activated.connect(action)

    def showEvent(self, event: Any) -> None:
        super().showEvent(event)
        if hasattr(__import__("ctypes"), "windll"):
            import ctypes
            from ctypes import wintypes

            user32 = ctypes.windll.user32
            point = wintypes.POINT(0, 0)
            user32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
            user32.ClientToScreen.restype = wintypes.BOOL
            if user32.ClientToScreen(wintypes.HWND(self.window.hwnd), ctypes.byref(point)):
                fn = user32.SetWindowPos
                fn.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                               ctypes.c_int, ctypes.c_int, wintypes.UINT]
                fn.restype = wintypes.BOOL
                height, width = self.image.shape[:2]
                fn(wintypes.HWND(int(self.winId())), wintypes.HWND(-1), point.x, point.y,
                   width, height, 0x0040 | 0x0200)
        self.activateWindow()
        self.setFocus()

    def resizeEvent(self, event: Any) -> None:
        super().resizeEvent(event)
        self.canvas.setGeometry(self.rect())
        self.toolbar.adjustSize()
        self.toolbar.move(max(0, (self.width() - self.toolbar.width()) // 2),
                          max(0, self.height() - self.toolbar.height() - 12))
        self.status_label.adjustSize()
        self.status_label.move(12, 12)
        self.toolbar.raise_()
        self.status_label.raise_()

    def _update_status(self) -> None:
        roi = self.canvas.selection
        self.status_label.setText("尚未选择区域" if roi is None else f"图层面板范围：{roi[2]} × {roi[3]} 像素")

    def toggle_toolbar(self) -> None:
        self.toolbar.setVisible(not self.toolbar.isVisible())

    def roi(self) -> tuple[int, int, int, int] | None:
        return self.canvas.selection

    def accept(self) -> None:
        if self.roi() is None:
            self.status_label.setText("请先拖拽框选有效图层区域")
            self.status_label.adjustSize()
            return
        super().accept()
