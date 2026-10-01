from __future__ import annotations

import sys
from pathlib import Path
from typing import Any

import cv2
import numpy as np
from PySide6.QtCore import QPoint, QRect, Qt, QTimer, Signal
from PySide6.QtGui import QColor, QImage, QKeySequence, QPainter, QPen, QPixmap, QShortcut
from PySide6.QtWidgets import QApplication, QComboBox, QFileDialog, QHBoxLayout, QLabel, QMainWindow, QMessageBox, QPushButton, QVBoxLayout, QWidget

from .color_recognition import ColorReading, recognize_color
from .window_service import enable_per_monitor_dpi_awareness


def _qimage_to_bgr(image: QImage) -> np.ndarray:
    rgb = image.convertToFormat(QImage.Format.Format_RGB888)
    width, height = rgb.width(), rgb.height()
    raw = np.frombuffer(rgb.constBits(), dtype=np.uint8, count=rgb.sizeInBytes())
    rows = raw.reshape(height, rgb.bytesPerLine())
    packed = rows[:, :width * 3].reshape(height, width, 3)
    return cv2.cvtColor(packed, cv2.COLOR_RGB2BGR).copy()


def _pixmap_to_bgr(pixmap: QPixmap) -> np.ndarray:
    return _qimage_to_bgr(pixmap.toImage())


class ScreenRegionPicker(QWidget):
    selected = Signal(object)
    closed = Signal()

    def __init__(self, screen: Any, pixmap: QPixmap):
        super().__init__(None, Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint)
        self.screen = screen
        self.pixmap = pixmap
        self.origin: QPoint | None = None
        self.current: QPoint | None = None
        self.setWindowTitle("框选 CSP 颜色面板")
        self.setGeometry(screen.geometry())
        self.setCursor(Qt.CursorShape.CrossCursor)
        self.setMouseTracking(True)
        QShortcut(QKeySequence("Return"), self).activated.connect(self.accept)
        QShortcut(QKeySequence("Enter"), self).activated.connect(self.accept)
        QShortcut(QKeySequence("Escape"), self).activated.connect(self.close)

    def showEvent(self, event: Any) -> None:
        super().showEvent(event)
        self.activateWindow()
        self.setFocus()

    def paintEvent(self, _event: Any) -> None:
        painter = QPainter(self)
        painter.drawPixmap(self.rect(), self.pixmap)
        painter.fillRect(self.rect(), QColor(0, 0, 0, 65))
        painter.setPen(QPen(QColor(255, 226, 64), 2))
        if self.origin is not None and self.current is not None:
            selection = QRect(self.origin, self.current).normalized()
            painter.drawPixmap(selection, self.pixmap, self._to_image_rect(selection))
            painter.drawRect(selection)
        painter.setPen(QColor(255, 255, 255))
        painter.drawText(24, 36, "拖框包含色轮、方形取色区和左下角颜色/透明色按钮 · Enter 确认 · Esc 取消")

    def _to_image_rect(self, rect: QRect) -> QRect:
        sx = self.pixmap.width() / max(1, self.width())
        sy = self.pixmap.height() / max(1, self.height())
        return QRect(round(rect.x() * sx), round(rect.y() * sy), round(rect.width() * sx), round(rect.height() * sy))

    def mousePressEvent(self, event: Any) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self.origin = event.position().toPoint()
            self.current = self.origin
            self.update()

    def mouseMoveEvent(self, event: Any) -> None:
        if self.origin is not None:
            self.current = event.position().toPoint()
            self.update()

    def mouseReleaseEvent(self, event: Any) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self.origin is not None:
            self.current = event.position().toPoint()
            self.update()

    def selected_rect(self) -> tuple[int, int, int, int] | None:
        if self.origin is None or self.current is None:
            return None
        logical = QRect(self.origin, self.current).normalized()
        physical = self._to_image_rect(logical)
        x = max(0, min(self.pixmap.width(), physical.x()))
        y = max(0, min(self.pixmap.height(), physical.y()))
        right = max(x, min(self.pixmap.width(), physical.x() + physical.width()))
        bottom = max(y, min(self.pixmap.height(), physical.y() + physical.height()))
        if right - x < 40 or bottom - y < 40:
            return None
        return x, y, right, bottom

    def accept(self) -> None:
        rect = self.selected_rect()
        if rect is not None:
            self.selected.emit(rect)
            self.close()

    def closeEvent(self, event: Any) -> None:
        self.closed.emit()
        super().closeEvent(event)


class ColorReaderWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("CSP 当前颜色读取器")
        self.resize(540, 430)
        self.screens = QApplication.screens()
        self.live_screen: Any = None
        self.live_rect: tuple[int, int, int, int] | None = None
        self.file_image: np.ndarray | None = None
        self.last_reading: ColorReading | None = None
        self._picker: ScreenRegionPicker | None = None
        self._build_ui()
        self.refresh_timer = QTimer(self)
        self.refresh_timer.setInterval(500)
        self.refresh_timer.timeout.connect(self._refresh_live)

    def _build_ui(self) -> None:
        self.screen_combo = QComboBox()
        for index, screen in enumerate(self.screens):
            geometry = screen.geometry()
            self.screen_combo.addItem(f"{screen.name()} · {geometry.width()}×{geometry.height()}", index)
        self.screen_combo.currentIndexChanged.connect(self._screen_changed)
        self.pick_button = QPushButton("框选屏幕区域")
        self.pick_button.clicked.connect(self.select_region)
        self.open_button = QPushButton("打开截图")
        self.open_button.clicked.connect(self.open_image)
        self.pause_button = QPushButton("暂停实时读取")
        self.pause_button.setEnabled(False)
        self.pause_button.clicked.connect(self.toggle_live)
        top = QHBoxLayout()
        top.addWidget(self.screen_combo, 1)
        top.addWidget(self.pick_button)
        top.addWidget(self.open_button)

        self.color_chip = QLabel()
        self.color_chip.setFixedSize(76, 76)
        self.color_chip.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.color_chip.setStyleSheet("background:#292929; border:2px solid #777; border-radius:8px;")
        self.result_label = QLabel("等待识别")
        self.result_label.setStyleSheet("font-size:24px; font-weight:600;")
        self.detail_label = QLabel("先框选色轮区域，或打开一张 CSP 截图。")
        self.detail_label.setWordWrap(True)
        self.detail_label.setMinimumHeight(40)
        self.copy_button = QPushButton("复制结果")
        self.copy_button.setEnabled(False)
        self.copy_button.clicked.connect(self.copy_result)
        result_row = QHBoxLayout()
        result_row.addWidget(self.color_chip)
        result_text = QVBoxLayout()
        result_text.addWidget(self.result_label)
        result_text.addWidget(self.detail_label)
        result_row.addLayout(result_text, 1)
        result_row.addWidget(self.copy_button)

        self.preview = QLabel("当前识别区域")
        self.preview.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.preview.setMinimumHeight(230)
        self.preview.setStyleSheet("background:#202124; color:#aaa; border:1px solid #555;")
        self.status_label = QLabel("实时模式每 0.5 秒读取一次。将本窗口移开识别区域。")
        self.status_label.setWordWrap(True)
        layout = QVBoxLayout()
        layout.addLayout(top)
        layout.addLayout(result_row)
        layout.addWidget(self.preview, 1)
        layout.addWidget(self.pause_button, alignment=Qt.AlignmentFlag.AlignRight)
        layout.addWidget(self.status_label)
        central = QWidget()
        central.setLayout(layout)
        self.setCentralWidget(central)

    def _screen_changed(self, _index: int) -> None:
        if self.refresh_timer.isActive():
            self.refresh_timer.stop()
        self.live_screen = None
        self.live_rect = None
        self.pause_button.setEnabled(False)
        self.pause_button.setText("暂停实时读取")
        self.status_label.setText("显示器已更改，请重新框选屏幕区域。")

    def select_region(self) -> None:
        index = self.screen_combo.currentData()
        if index is None or not (0 <= int(index) < len(self.screens)):
            QMessageBox.information(self, "选择屏幕", "没有可用显示器。")
            return
        self.refresh_timer.stop()
        self.live_screen = None
        self.live_rect = None
        self.pause_button.setEnabled(False)
        screen = self.screens[int(index)]
        self.hide()
        QTimer.singleShot(180, lambda: self._show_picker(screen))

    def _show_picker(self, screen: Any) -> None:
        try:
            pixmap = screen.grabWindow(0)
            if pixmap.isNull():
                raise RuntimeError("屏幕截图失败")
            self._picker = ScreenRegionPicker(screen, pixmap)
            self._picker.selected.connect(lambda rect: self._region_selected(screen, rect, pixmap))
            self._picker.closed.connect(self.showNormal)
            self._picker.show()
        except Exception as exc:
            self.showNormal()
            QMessageBox.warning(self, "屏幕捕获失败", str(exc))

    def _region_selected(self, screen: Any, rect: tuple[int, int, int, int], pixmap: QPixmap) -> None:
        self.showNormal()
        self.live_screen = screen
        self.live_rect = rect
        self.file_image = None
        self.pause_button.setEnabled(True)
        self.pause_button.setText("暂停实时读取")
        self._analyze(_pixmap_to_bgr(pixmap)[rect[1]:rect[3], rect[0]:rect[2]])
        self.refresh_timer.start()
        self.status_label.setText("正在读取屏幕区域；如果读数被挡住，请把本窗口移出色盘上方。")

    def _refresh_live(self) -> None:
        if self.live_screen is None or self.live_rect is None:
            return
        try:
            frame = _pixmap_to_bgr(self.live_screen.grabWindow(0))
            x0, y0, x1, y1 = self.live_rect
            if x1 > frame.shape[1] or y1 > frame.shape[0]:
                raise RuntimeError("显示器捕获尺寸变化，请重新框选区域")
            self._analyze(frame[y0:y1, x0:x1])
        except Exception as exc:
            self.refresh_timer.stop()
            self.status_label.setText(f"实时读取已停止：{exc}")

    def toggle_live(self) -> None:
        if self.refresh_timer.isActive():
            self.refresh_timer.stop()
            self.pause_button.setText("继续实时读取")
            self.status_label.setText("实时读取已暂停。")
        elif self.live_screen is not None and self.live_rect is not None:
            self.refresh_timer.start()
            self.pause_button.setText("暂停实时读取")
            self.status_label.setText("正在读取屏幕区域。")

    def open_image(self) -> None:
        path, _ = QFileDialog.getOpenFileName(self, "打开 CSP 截图", "", "图片 (*.png *.jpg *.jpeg *.bmp *.webp)")
        if not path:
            return
        try:
            image = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)
            if image is None:
                raise ValueError("图片无法解码")
            self.refresh_timer.stop()
            self.live_screen = None
            self.live_rect = None
            self.file_image = image
            self.pause_button.setEnabled(False)
            self.pause_button.setText("暂停实时读取")
            self._analyze(image)
            self.status_label.setText(f"截图模式：{Path(path).name}")
        except Exception as exc:
            QMessageBox.warning(self, "打开失败", str(exc))

    def _analyze(self, image: np.ndarray) -> None:
        reading = recognize_color(image)
        self.last_reading = reading
        self._show_preview(image)
        self.copy_button.setEnabled(reading.kind != "unknown")
        if reading.kind == "transparent":
            self.color_chip.setText("α=0")
            self.color_chip.setStyleSheet("background-color: qlineargradient(x1:0,y1:0,x2:1,y2:1,stop:0 #fff,stop:.25 #fff,stop:.25 #aaa,stop:.5 #aaa,stop:.5 #fff,stop:.75 #fff,stop:.75 #aaa); border:2px solid #8daeea; border-radius:8px;")
            self.result_label.setText("透明色  ·  #00000000")
        elif reading.rgb is not None:
            r, g, b = reading.rgb
            self.color_chip.setText("")
            self.color_chip.setStyleSheet(f"background-color: rgb({r},{g},{b}); border:2px solid #8daeea; border-radius:8px;")
            self.result_label.setText(f"{reading.hex}  ·  RGB {r}, {g}, {b}")
        else:
            self.color_chip.setText("?")
            self.color_chip.setStyleSheet("background:#292929; border:2px solid #777; border-radius:8px; color:#fff;")
            self.result_label.setText("无法确定")
        extras = []
        if reading.hue is not None:
            extras.append(f"环上 Hue≈{reading.hue}°")
        if reading.sv_point is not None:
            extras.append(f"取色点 {reading.sv_point[0]},{reading.sv_point[1]}")
        suffix = f" · {'；'.join(extras)}" if extras else ""
        self.detail_label.setText(f"{reading.detail} · 置信度 {reading.confidence:.0%}{suffix}")

    def _show_preview(self, image: np.ndarray) -> None:
        rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        height, width = rgb.shape[:2]
        qimage = QImage(rgb.data, width, height, width * 3, QImage.Format.Format_RGB888).copy()
        pixmap = QPixmap.fromImage(qimage).scaled(self.preview.size(), Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation)
        self.preview.setPixmap(pixmap)

    def resizeEvent(self, event: Any) -> None:
        super().resizeEvent(event)
        if self.live_rect is not None and self.live_screen is not None:
            self._refresh_live()

    def copy_result(self) -> None:
        if self.last_reading is None:
            return
        if self.last_reading.kind == "transparent":
            text = "透明色 RGBA(0, 0, 0, 0) #00000000"
        elif self.last_reading.rgb is not None:
            r, g, b = self.last_reading.rgb
            text = f"{self.last_reading.hex} RGB({r}, {g}, {b}) RGBA({r}, {g}, {b}, 255)"
        else:
            return
        QApplication.clipboard().setText(text)
        self.status_label.setText(f"已复制：{text}")

    def closeEvent(self, event: Any) -> None:
        self.refresh_timer.stop()
        super().closeEvent(event)


def main() -> int:
    enable_per_monitor_dpi_awareness()
    app = QApplication(sys.argv)
    window = ColorReaderWindow()
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
