from __future__ import annotations

import uuid
from typing import Any

import cv2
import numpy as np

from .models import MonitorProfile, Profile, Rect, RoiDefinition
from .window_service import WindowInfo

try:
    from PySide6.QtCore import QPoint, QRect, Qt, Signal
    from PySide6.QtGui import QImage, QPainter, QPen, QPixmap
    from PySide6.QtWidgets import QComboBox, QDialog, QDialogButtonBox, QFormLayout, QHBoxLayout, QLabel, QLineEdit, QListWidget, QListWidgetItem, QMessageBox, QPushButton, QSpinBox, QVBoxLayout, QWidget
except ImportError:  # pragma: no cover
    QDialog = object  # type: ignore


def bgr_to_pixmap(image: np.ndarray) -> Any:
    rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
    height, width = rgb.shape[:2]
    qimage = QImage(rgb.data, width, height, width * 3, QImage.Format.Format_RGB888).copy()
    return QPixmap.fromImage(qimage)


if QDialog is not object:
    class _Canvas(QWidget):
        changed = Signal()

        def __init__(self, image: np.ndarray):
            super().__init__()
            self.image = image
            self.pixmap = bgr_to_pixmap(image)
            self.rois: list[RoiDefinition] = []
            self.active = -1
            self.drag_start = None
            self.drag_mode = "new"
            self.drag_original = None
            self._preview_rect = None
            self.new_role = "tool_properties"
            self.setMinimumSize(1, 1)
            self.setCursor(Qt.CursorShape.CrossCursor)
            self.setMouseTracking(True)

        def _scale(self) -> tuple[float, float, float]:
            scale = min(self.width() / self.pixmap.width(), self.height() / self.pixmap.height())
            ox = (self.width() - self.pixmap.width() * scale) / 2
            oy = (self.height() - self.pixmap.height() * scale) / 2
            return scale, ox, oy

        def _to_image(self, point: QPoint) -> tuple[int, int]:
            scale, ox, oy = self._scale()
            x = round((point.x() - ox) / scale)
            y = round((point.y() - oy) / scale)
            return max(0, min(self.image.shape[1], x)), max(0, min(self.image.shape[0], y))

        def _inside_image(self, point: QPoint) -> bool:
            scale, ox, oy = self._scale()
            return ox <= point.x() <= ox + self.pixmap.width() * scale and oy <= point.y() <= oy + self.pixmap.height() * scale

        def _to_widget(self, point: tuple[int, int]) -> QPoint:
            scale, ox, oy = self._scale()
            return QPoint(round(point[0] * scale + ox), round(point[1] * scale + oy))

        def paintEvent(self, event: Any) -> None:
            painter = QPainter(self)
            scale, ox, oy = self._scale()
            painter.drawPixmap(QRect(round(ox), round(oy), round(self.pixmap.width() * scale), round(self.pixmap.height() * scale)), self.pixmap)
            for index, roi in enumerate(self.rois):
                top_left = self._to_widget((roi.rect.x, roi.rect.y))
                bottom_right = self._to_widget((roi.rect.x + roi.rect.width, roi.rect.y + roi.rect.height))
                color = Qt.GlobalColor.green if roi.role == "tool_properties" else Qt.GlobalColor.cyan
                if index == self.active:
                    color = Qt.GlobalColor.yellow
                pen = QPen(color, 3 if index == self.active else 2)
                painter.setPen(pen)
                painter.drawRect(QRect(top_left, bottom_right))
                painter.drawText(top_left + QPoint(4, 18), f"{index + 1}: {roi.name} [{roi.role}]")
            if self._preview_rect:
                top_left = self._to_widget((self._preview_rect.x, self._preview_rect.y))
                bottom_right = self._to_widget((self._preview_rect.x + self._preview_rect.width, self._preview_rect.y + self._preview_rect.height))
                painter.setPen(QPen(Qt.GlobalColor.yellow, 2, Qt.PenStyle.DashLine))
                painter.drawRect(QRect(top_left, bottom_right))

        def mousePressEvent(self, event: Any) -> None:
            if event.button() != Qt.MouseButton.LeftButton:
                return
            if not self._inside_image(event.position().toPoint()):
                return
            point = self._to_image(event.position().toPoint())
            self._preview_rect = None
            self.drag_start = point
            self.drag_mode = "new"
            self.drag_original = None
            for index, roi in enumerate(self.rois):
                r = roi.rect
                if r.x <= point[0] <= r.x + r.width and r.y <= point[1] <= r.y + r.height:
                    self.active = index
                    self.drag_original = r
                    self.drag_mode = "resize" if point[0] >= r.x + r.width - 20 and point[1] >= r.y + r.height - 20 else "move"
                    break
            self.update()

        def mouseMoveEvent(self, event: Any) -> None:
            if self.drag_start is None:
                return
            point = self._to_image(event.position().toPoint())
            x0, y0 = self.drag_start
            if self.drag_mode == "new":
                rect = Rect(min(x0, point[0]), min(y0, point[1]), abs(point[0] - x0), abs(point[1] - y0))
                self._preview_rect = rect if rect.width >= 8 and rect.height >= 8 else None
            elif self.active >= 0 and self.drag_original:
                original = self.drag_original
                if self.drag_mode == "move":
                    nx = max(0, min(self.image.shape[1] - original.width, original.x + point[0] - x0))
                    ny = max(0, min(self.image.shape[0] - original.height, original.y + point[1] - y0))
                    self.rois[self.active].rect = Rect(nx, ny, original.width, original.height)
                else:
                    width = max(5, min(self.image.shape[1] - original.x, point[0] - original.x))
                    height = max(5, min(self.image.shape[0] - original.y, point[1] - original.y))
                    self.rois[self.active].rect = Rect(original.x, original.y, width, height)
                self.changed.emit()
            self.update()

        def mouseReleaseEvent(self, event: Any) -> None:
            if self.drag_start is None:
                return
            self.mouseMoveEvent(event)
            if self.drag_mode == "new" and self._preview_rect is not None:
                rect = self._preview_rect
                if rect.width > 0 and rect.height > 0:
                    self.rois.append(RoiDefinition(str(uuid.uuid4())[:8], "工具属性" if self.new_role == "tool_properties" else "子工具列表", self.new_role, rect))
                self.active = len(self.rois) - 1
                self._preview_rect = None
                self.changed.emit()
            self.drag_start = None
            self.drag_original = None
            self.update()


    class RoiSelectorDialog(QDialog):
        """Frozen, borderless selection at the original CSP client-area position."""
        def __init__(self, image: np.ndarray, window: WindowInfo, profile_name: str = "CSP 面板配置", parent: Any = None, existing_rois: list[RoiDefinition] | None = None):
            super().__init__(parent)
            self.setWindowFlags(Qt.WindowType.Dialog | Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint)
            self.setWindowTitle("冻结画面 · 框选面板")
            self.image = image
            self.window = window
            self.profile_name = profile_name
            self.canvas = _Canvas(image)
            self.canvas.setParent(self)
            # New profiles use only the properties region. Drop the obsolete
            # tool-list ROI when reopening a legacy profile for reselection.
            self.canvas.rois = [RoiDefinition(r.id, r.name, r.role, Rect(r.rect.x, r.rect.y, r.rect.width, r.rect.height)) for r in (existing_rois or []) if r.role == "tool_properties" and r.rect.contains_size(image.shape[1], image.shape[0])]
            self.canvas.active = len(self.canvas.rois) - 1
            self.toolbar = QWidget(self)
            self.toolbar.setStyleSheet("QWidget {background: #252932; color: white;} QPushButton, QComboBox {padding: 7px;}")
            layout = QHBoxLayout(self.toolbar)
            layout.addWidget(QLabel("拖框选面板 · Enter 保存 · Esc 取消 · Tab 隐藏工具栏"))
            self.role_combo = QComboBox()
            self.role_combo.addItem("新框：工具属性", "tool_properties")
            self.role_combo.currentIndexChanged.connect(self.change_role)
            layout.addWidget(self.role_combo)
            delete = QPushButton("删除选中框")
            delete.clicked.connect(self.delete_selected)
            layout.addWidget(delete)
            save = QPushButton("保存并识别")
            save.clicked.connect(self.accept)
            layout.addWidget(save)
            cancel = QPushButton("取消")
            cancel.clicked.connect(self.reject)
            layout.addWidget(cancel)
            from PySide6.QtGui import QShortcut, QKeySequence
            for key, action in (("Return", self.accept), ("Enter", self.accept), ("Escape", self.reject), ("Delete", self.delete_selected), ("Tab", self.toggle_toolbar)):
                shortcut = QShortcut(QKeySequence(key), self)
                shortcut.activated.connect(action)
            dpr = max(window.dpi / 96.0, 1.0)
            self.resize(round(image.shape[1] / dpr), round(image.shape[0] / dpr))

        def showEvent(self, event: Any) -> None:
            super().showEvent(event)
            # Native physical coordinates avoid assuming Qt screen origins scale with DPI.
            from .window_service import IS_WINDOWS
            if IS_WINDOWS:
                import ctypes
                from ctypes import wintypes
                fn = ctypes.windll.user32.SetWindowPos
                fn.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.UINT]
                r = self.window.client_rect
                fn(int(self.winId()), wintypes.HWND(-1), r.x, r.y, r.width, r.height, 0x0040)
            self.activateWindow()
            self.setFocus()

        def resizeEvent(self, event: Any) -> None:
            super().resizeEvent(event)
            self.canvas.setGeometry(self.rect())
            self.toolbar.adjustSize()
            self.toolbar.move(max(0, (self.width() - self.toolbar.width()) // 2), max(0, self.height() - self.toolbar.height() - 12))
            self.toolbar.raise_()

        def toggle_toolbar(self) -> None:
            self.toolbar.setVisible(not self.toolbar.isVisible())

        def change_role(self) -> None:
            role = self.role_combo.currentData()
            self.canvas.new_role = role
            self.canvas.active = -1
            self.canvas.update()

        def delete_selected(self) -> None:
            i = self.canvas.active
            if 0 <= i < len(self.canvas.rois):
                self.canvas.rois.pop(i)
                self.canvas.active = len(self.canvas.rois) - 1
                self.canvas.update()

        def profile(self) -> Profile:
            if not any(roi.role == "tool_properties" for roi in self.canvas.rois):
                raise ValueError("至少需要一个工具属性 ROI")
            monitor = MonitorProfile(
                screen_id=f"window:{self.window.exe_name}:{self.window.class_name}",
                name=self.window.title or self.window.exe_name,
                geometry=self.window.client_rect,
                capture_size=self.window.capture_size,
                dpi=self.window.dpi,
                device_pixel_ratio=self.window.dpi / 96.0,
                output_index=self.window.output_index,
            )
            return Profile(
                self.profile_name,
                monitor,
                self.canvas.rois,
                profile_kind="user",
                target_window=self.window.binding(),
            )

        def accept(self) -> None:
            try:
                self.profile()
            except ValueError as exc:
                QMessageBox.warning(self, "配置不完整", str(exc))
                return
            super().accept()
else:
    class RoiSelectorDialog:  # type: ignore
        pass
