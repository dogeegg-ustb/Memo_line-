from __future__ import annotations

from collections import deque
from datetime import datetime
from pathlib import Path
from typing import Any

from PySide6.QtCore import QObject, QRunnable, QThreadPool, QTimer, Qt, QUrl, Signal
from PySide6.QtGui import QDesktopServices, QFont
from PySide6.QtWidgets import (
    QApplication,
    QComboBox,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QMainWindow,
    QMessageBox,
    QPushButton,
    QDialog,
    QInputDialog,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from .layer_reader import LayerOcrReader
from .key_events import KeyEventRecorder
from .panel_selection import LayerPanelSelectionDialog
from .window_capture import capture_client, enumerate_csp_windows


class _CaptureSignals(QObject):
    done = Signal(int, object)
    failed = Signal(int, str)


class _CaptureTask(QRunnable):
    def __init__(self, frame_id: int, window: Any, roi: tuple[int, int, int, int]):
        super().__init__()
        self.frame_id = frame_id
        self.window = window
        self.roi = roi
        self.signals = _CaptureSignals()

    def run(self) -> None:
        try:
            image = capture_client(self.window)
            x, y, width, height = self.roi
            panel = image[y:y + height, x:x + width]
            if panel.size == 0:
                raise RuntimeError("选取的图层区域为空，请重新选择。")
            self.signals.done.emit(self.frame_id, panel.copy())
        except Exception as exc:
            self.signals.failed.emit(self.frame_id, str(exc))


class _ReadSignals(QObject):
    done = Signal(int, object)
    failed = Signal(int, str)


class _ReadTask(QRunnable):
    def __init__(self, reader: LayerOcrReader, frame_id: int, panel: Any):
        super().__init__()
        self.reader = reader
        self.frame_id = frame_id
        self.panel = panel
        self.signals = _ReadSignals()

    def run(self) -> None:
        try:
            if not self.reader.has_significant_change(self.panel):
                self.signals.done.emit(self.frame_id, None)
                return
            snapshot, _ = self.reader.read(self.panel)
            self.signals.done.emit(self.frame_id, snapshot)
        except Exception as exc:
            self.signals.failed.emit(self.frame_id, str(exc))


class _EventSignals(QObject):
    done = Signal(int, object)
    failed = Signal(int, str)


class _EventTask(QRunnable):
    def __init__(
        self, generation: int, recorder: KeyEventRecorder,
        frame_id: int, panel: Any, captured_at: datetime,
    ) -> None:
        super().__init__()
        self.generation = generation
        self.recorder = recorder
        self.frame_id = frame_id
        self.panel = panel
        self.captured_at = captured_at
        self.signals = _EventSignals()

    def run(self) -> None:
        try:
            saved = self.recorder.consume(self.frame_id, self.panel, self.captured_at)
            self.signals.done.emit(self.generation, saved)
        except Exception as exc:
            self.signals.failed.emit(self.generation, str(exc))


class MainWindow(QMainWindow):
    def __init__(self) -> None:
        super().__init__()
        self.target_window = None
        self.roi: tuple[int, int, int, int] | None = None
        # Match the successful comparison run exactly: raw RapidOCR input, with
        # no global CLAHE transform. The contrast branch was a separate variant.
        self.reader = LayerOcrReader(method="rapidocr")
        self.capture_pool = QThreadPool(self)
        self.capture_pool.setMaxThreadCount(1)
        self.read_pool = QThreadPool(self)
        self.read_pool.setMaxThreadCount(1)
        self.event_pool = QThreadPool(self)
        self.event_pool.setMaxThreadCount(1)
        self.event_dir = Path(__file__).resolve().parents[1] / "关键事件记录"
        self.event_recorder = KeyEventRecorder(self.event_dir)
        self.event_generation = 0
        self.event_busy = False
        self.event_count = 0
        self.event_queue: deque[tuple[int, Any, datetime]] = deque(maxlen=40)
        self.capture_busy = False
        self.read_busy = False
        self.continuous = False
        self.frame_id = 0
        self.pending_panel: tuple[int, Any] | None = None
        self.last_rendered_frame = 0
        self.capture_timer = QTimer(self)
        self.capture_timer.timeout.connect(self._request_capture)
        self._build_ui()

    def _build_ui(self) -> None:
        self.setWindowTitle("CSP 图层识别（精简版）")
        self.resize(1120, 650)
        self.setMinimumSize(820, 420)
        self.setFont(QFont("Microsoft YaHei UI", 9))
        self.setStyleSheet("""
            QMainWindow, QWidget { background: #1f1f1f; color: #eeeeee; }
            QPushButton, QComboBox { background: #303030; color: #eeeeee; border: 1px solid #484848;
                                      border-radius: 4px; padding: 6px 10px; }
            QPushButton:hover { background: #3a3a3a; }
            QPushButton:disabled { color: #888888; }
            QTableWidget { background: #252525; alternate-background-color: #303030;
                           gridline-color: #454545; border: 1px solid #3d3d3d;
                           selection-background-color: #087bd0; }
            QHeaderView::section { background: #363636; color: #eeeeee; padding: 7px 6px;
                                   border: 0; border-right: 1px solid #484848;
                                   border-bottom: 1px solid #484848; }
            QScrollBar:vertical { background: #202020; width: 12px; }
            QScrollBar::handle:vertical { background: #555555; min-height: 25px; }
        """)

        self.window_choice = QComboBox()
        self.window_choice.setMinimumWidth(330)
        self.window_choice.setEnabled(False)
        self.window_choice.addItem("尚未连接 CSP")
        self.select_button = QPushButton("选择窗口并框选图层面板")
        self.select_button.clicked.connect(self.select_panel)
        self.read_button = QPushButton("识别 / 刷新")
        self.read_button.setEnabled(False)
        self.read_button.clicked.connect(self._request_capture)
        self.start_button = QPushButton("开始连续读取")
        self.start_button.setEnabled(False)
        self.start_button.clicked.connect(self.start_continuous)
        self.stop_button = QPushButton("停止")
        self.stop_button.setEnabled(False)
        self.stop_button.clicked.connect(self.stop_continuous)
        self.open_events_button = QPushButton("打开关键事件记录")
        self.open_events_button.clicked.connect(self._open_event_dir)
        self.interval_choice = QComboBox()
        for label, interval_ms in (("0.5 秒", 500), ("1 秒", 1000), ("2 秒", 2000)):
            self.interval_choice.addItem(label, interval_ms)
        self.status = QLabel("打开 CSP 后选择窗口和图层面板区域。")
        self.status.setStyleSheet("color: #bbbbbb; padding-left: 6px;")
        self.event_status = QLabel("关键事件：0 | 连续读取时自动保存变化前后截图")
        self.event_status.setStyleSheet("color: #aaaaaa; padding-left: 6px;")
        self.event_status.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)

        toolbar = QHBoxLayout()
        toolbar.addWidget(self.window_choice)
        toolbar.addWidget(self.select_button)
        toolbar.addWidget(self.read_button)
        toolbar.addWidget(self.start_button)
        toolbar.addWidget(self.stop_button)
        toolbar.addWidget(self.open_events_button)
        toolbar.addWidget(QLabel("截图间隔"))
        toolbar.addWidget(self.interval_choice)
        toolbar.addStretch(1)

        self.table = QTableWidget(0, 7)
        self.table.setHorizontalHeaderLabels([
            "图层名称", "图层 ID", "类型", "所在组", "可见", "不透明度", "混合模式",
        ])
        self.table.setAlternatingRowColors(True)
        self.table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        self.table.setSelectionBehavior(QTableWidget.SelectionBehavior.SelectRows)
        self.table.setSortingEnabled(False)
        header = self.table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        for column, width in ((1, 145), (2, 85), (3, 130), (4, 64), (5, 90), (6, 130)):
            self.table.setColumnWidth(column, width)
        self.table.verticalHeader().setVisible(False)

        body = QWidget()
        layout = QVBoxLayout(body)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.addLayout(toolbar)
        layout.addWidget(self.table, 1)
        layout.addWidget(self.status)
        layout.addWidget(self.event_status)
        self.setCentralWidget(body)

    def select_panel(self) -> None:
        windows = [window for window in enumerate_csp_windows() if not window.minimized]
        if not windows:
            QMessageBox.information(self, "未找到 CSP", "请先打开 CLIP STUDIO PAINT，并确认窗口可见。")
            return
        labels = [window.display_name() for window in windows]
        if len(windows) == 1:
            selected = windows[0]
        else:
            choice, ok = QInputDialog.getItem(self, "选择 CSP 窗口", "可见的 CSP 窗口：", labels, 0, False)
            if not ok:
                return
            selected = windows[labels.index(choice)]

        was_visible = self.isVisible()
        if was_visible:
            self.hide()
            QApplication.processEvents()
        dialog = None
        try:
            frozen = capture_client(selected)
            dialog = LayerPanelSelectionDialog(frozen, selected, self.roi, self)
            if dialog.exec() != QDialog.DialogCode.Accepted:
                return
            roi = dialog.roi()
            if roi is None:
                return
        except Exception as exc:
            QMessageBox.warning(self, "窗口捕获失败", str(exc))
            return
        finally:
            if dialog is not None:
                dialog.deleteLater()
            if was_visible:
                self.show()
                self.raise_()

        self.target_window = selected
        self.roi = roi
        self.event_generation += 1
        self.event_queue.clear()
        self.event_recorder = KeyEventRecorder(self.event_dir)
        self.event_count = 0
        self.event_status.setText("关键事件：0 | 连续读取时自动保存变化前后截图")
        self.window_choice.clear()
        self.window_choice.addItem(selected.display_name())
        self.window_choice.setCurrentText(selected.display_name())
        self.read_button.setEnabled(True)
        self.start_button.setEnabled(True)
        self.status.setText(f"已选取图层区域 {roi[2]} × {roi[3]} 像素。")

    def _open_event_dir(self) -> None:
        self.event_dir.mkdir(parents=True, exist_ok=True)
        QDesktopServices.openUrl(QUrl.fromLocalFile(str(self.event_dir)))

    def _request_capture(self) -> None:
        if self.capture_busy or self.target_window is None or self.roi is None:
            return
        self.capture_busy = True
        self.frame_id += 1
        task = _CaptureTask(self.frame_id, self.target_window, self.roi)
        task.signals.done.connect(self._capture_done)
        task.signals.failed.connect(self._capture_failed)
        self.capture_pool.start(task)
        self.status.setText(f"正在截图第 {self.frame_id} 帧；OCR 可并行处理上一帧。")

    def start_continuous(self) -> None:
        if self.target_window is None or self.roi is None or self.continuous:
            return
        self.continuous = True
        self.start_button.setEnabled(False)
        self.stop_button.setEnabled(True)
        self.capture_timer.start(int(self.interval_choice.currentData()))
        self.status.setText("连续读取中：截图与 OCR 分开运行。")
        self._request_capture()

    def stop_continuous(self) -> None:
        self.continuous = False
        self.capture_timer.stop()
        self.pending_panel = None
        self.start_button.setEnabled(self.target_window is not None)
        self.stop_button.setEnabled(False)
        if self.read_busy or self.capture_busy:
            self.status.setText("已停止新截图；当前识别完成后结束。")
        else:
            self.status.setText("连续读取已停止。")

    def _capture_done(self, frame_id: int, panel: Any) -> None:
        self.capture_busy = False
        self.event_queue.append((frame_id, panel, datetime.now()))
        self._start_next_event()
        self.pending_panel = (frame_id, panel)
        self._start_next_read()

    def _start_next_event(self) -> None:
        if self.event_busy or not self.event_queue:
            return
        frame_id, panel, captured_at = self.event_queue.popleft()
        self.event_busy = True
        task = _EventTask(
            self.event_generation, self.event_recorder, frame_id, panel, captured_at
        )
        task.signals.done.connect(self._event_done)
        task.signals.failed.connect(self._event_failed)
        self.event_pool.start(task)

    def _event_done(self, generation: int, saved: Any) -> None:
        self.event_busy = False
        if generation == self.event_generation and saved is not None:
            self.event_count += 1
            self.event_status.setToolTip(str(saved))
            self.event_status.setText(
                f"关键事件：{self.event_count} | 已保存前后截图：{saved}"
            )
        self._start_next_event()

    def _event_failed(self, generation: int, message: str) -> None:
        self.event_busy = False
        if generation == self.event_generation:
            self.event_status.setText(f"关键事件保存失败：{message}")
        self._start_next_event()

    def _capture_failed(self, frame_id: int, message: str) -> None:
        self.capture_busy = False
        self.status.setText(f"第 {frame_id} 帧截图失败：{message}")

    def _start_next_read(self) -> None:
        if self.read_busy or self.pending_panel is None:
            return
        frame_id, panel = self.pending_panel
        self.pending_panel = None
        self.read_busy = True
        task = _ReadTask(self.reader, frame_id, panel)
        task.signals.done.connect(self._read_done)
        task.signals.failed.connect(self._read_failed)
        self.read_pool.start(task)

    def _read_done(self, frame_id: int, snapshot: Any) -> None:
        self.read_busy = False
        if snapshot is not None and frame_id >= self.last_rendered_frame:
            self._show_result(snapshot)
            self.last_rendered_frame = frame_id
        elif snapshot is None and self.continuous and self.pending_panel is None:
            self.status.setText(f"连续读取中：第 {frame_id} 帧与上次相同，跳过 OCR。")
        self._start_next_read()

    def _read_failed(self, frame_id: int, message: str) -> None:
        self.read_busy = False
        self.status.setText(f"第 {frame_id} 帧识别失败：{message}")
        self._start_next_read()

    def _show_result(self, snapshot: Any) -> None:
        # This runs only after the backend has completed the entire panel scan.
        # Normalize the final names and emit a complete parent-group table at once.
        rows: list[tuple[Any, str, str]] = []

        def flatten(layers: list[Any], parent_name: str = "根级") -> None:
            for layer in sorted(layers, key=lambda item: item.order_index):
                final_name = self.reader._normalize_ocr_default_name(layer.name)
                rows.append((layer, parent_name, final_name))
                flatten(layer.children, final_name if layer.layer_type == "group" else parent_name)

        flatten(snapshot.layers)
        self.table.setRowCount(len(rows))
        selected_row = None
        for row, (layer, parent_name, final_name) in enumerate(rows):
            layer_type = {"group": "图层组", "paper": "纸张"}.get(layer.layer_type, "图层")
            visible = {True: "是", False: "否"}.get(layer.visible, "未知")
            opacity = "未知" if layer.opacity is None else f"{layer.opacity * 100:.0f}%"
            blend = layer.blend_mode or "未知"
            values = (final_name, layer.layer_id, layer_type, parent_name, visible, opacity, blend)
            for column, value in enumerate(values):
                item = QTableWidgetItem(str(value))
                item.setToolTip(str(value))
                if column in (4, 5):
                    item.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
                self.table.setItem(row, column, item)
            if layer.selected:
                selected_row = row
        self.table.clearSelection()
        progress = "连续读取中 | " if self.continuous else "识别完成 | "
        if selected_row is not None:
            self.table.selectRow(selected_row)
            self.table.setCurrentCell(selected_row, 0)
            selected_layer, _, selected_name = rows[selected_row]
            selected_type = "图层组" if selected_layer.layer_type == "group" else "图层"
            self.status.setText(
                f"{progress}{len(rows)} 行 | 高亮选中：{selected_name}（{selected_type}）"
            )
        else:
            self.status.setText(
                f"{progress}{len(rows)} 行 | 未检测到唯一的高亮选中项"
            )
