from __future__ import annotations

import base64
import ctypes
from ctypes import wintypes
from pathlib import Path
from typing import Any

import cv2
from PySide6.QtCore import QTimer, Qt
from PySide6.QtGui import QIcon, QImage, QPixmap
from PySide6.QtWidgets import QCheckBox, QComboBox, QFileDialog, QHBoxLayout, QLabel, QListWidget, QMainWindow, QMessageBox, QPushButton, QSplitter, QStatusBar, QTableWidget, QTableWidgetItem, QTabWidget, QVBoxLayout, QWidget

from .capture_service import CaptureError, CaptureService
from .exporter import Exporter
from .models import Profile, RecognitionOutcome
from .ocr_engine import RapidOcrEngine
from .profile_store import ProfileStore
from .recognition_pipeline import RecognitionPipeline
from .recognition_scheduler import RecognitionScheduler
from .roi_selector import RoiSelectorDialog
from .window_service import WindowInfo, current_csp_window


def cv_to_pixmap(image: Any) -> QPixmap:
    if image is None:
        return QPixmap()
    rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
    h, w = rgb.shape[:2]
    return QPixmap.fromImage(QImage(rgb.data, w, h, w * 3, QImage.Format.Format_RGB888).copy())


def set_native_topmost(hwnd: int, enabled: bool) -> bool:
    """Change Win32 z-order directly, avoiding Qt hide/recreate on flag changes."""
    if not hasattr(ctypes, "windll"):
        return False
    try:
        set_window_pos = ctypes.windll.user32.SetWindowPos
        set_window_pos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                                   ctypes.c_int, ctypes.c_int, wintypes.UINT]
        set_window_pos.restype = wintypes.BOOL
        insert_after = wintypes.HWND(-1 if enabled else -2)  # HWND_TOPMOST / HWND_NOTOPMOST
        flags = 0x0001 | 0x0002 | 0x0010 | 0x0200  # NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER
        return bool(set_window_pos(wintypes.HWND(hwnd), insert_after, 0, 0, 0, 0, flags))
    except (AttributeError, OSError, TypeError, ValueError):
        return False


class MainWindow(QMainWindow):
    def __init__(self, app_root: str | Path):
        super().__init__()
        self.app_root = Path(app_root)
        self.store = ProfileStore(self.app_root / "profiles")
        self.capture = CaptureService()
        self.profile: Profile | None = None
        self.profile_path: Path | None = None
        self.target_window: WindowInfo | None = None
        self.scheduler: RecognitionScheduler | None = None
        self.last_outcome: RecognitionOutcome | None = None
        self._selecting = False
        self._recognition_generation = 0
        self._engine = None
        self.setWindowTitle("CSP Panel Validator")
        self.resize(1320, 840)
        self._build_ui()
        self._refresh_profiles()
        QTimer.singleShot(0, self._bootstrap_flow)

    def _build_ui(self) -> None:
        self.profile_combo = QComboBox()
        self.profile_combo.currentIndexChanged.connect(self.load_selected_profile)
        self.window_label = QLabel("尚未框选面板")
        self.window_label.setMinimumWidth(260)
        self.find_window_button = QPushButton("初始化 / 重新框选")
        self.find_window_button.clicked.connect(self.reselect)
        self.save_button = QPushButton("保存配置")
        self.save_button.clicked.connect(self.save_profile)
        self.once_button = QPushButton("识别一次")
        self.once_button.clicked.connect(lambda: self.start_recognition(True))
        self.start_button = QPushButton("开始持续识别")
        self.start_button.clicked.connect(lambda: self.start_recognition(False))
        self.stop_button = QPushButton("停止")
        self.stop_button.clicked.connect(self.stop_recognition)
        self.export_button = QPushButton("导出当前 JSON")
        self.export_button.clicked.connect(self.export_json)
        self.jsonl_button = QPushButton("追加会话 JSONL")
        self.jsonl_button.clicked.connect(self.export_jsonl)
        top = QHBoxLayout()
        top.addWidget(QLabel("用户配置")); top.addWidget(self.profile_combo, 1)
        top.addWidget(self.window_label, 2)
        for button in (self.find_window_button, self.save_button, self.once_button, self.start_button, self.stop_button, self.export_button, self.jsonl_button):
            top.addWidget(button)

        self.brush_label = QLabel("当前笔刷：unknown")
        self.status_label = QLabel("状态：未初始化")
        self.timing_label = QLabel("耗时：—")
        summary = QHBoxLayout()
        summary.addWidget(self.brush_label, 2); summary.addWidget(self.status_label, 1); summary.addWidget(self.timing_label, 1)
        self.pin_checkbox = QCheckBox("窗口置顶")
        self.pin_checkbox.setToolTip("保持 CSP Panel Validator 主窗口显示在其他窗口上方")
        self.pin_checkbox.toggled.connect(self.set_always_on_top)
        summary.addWidget(self.pin_checkbox)

        self.parameter_table = QTableWidget(0, 7)
        self.parameter_table.setHorizontalHeaderLabels(["key", "标签", "类型", "值", "单位", "enabled", "status"])
        self.parameter_table.cellClicked.connect(self.highlight_parameter)
        self.evidence_list = QListWidget()
        self.raw_ocr_table = QTableWidget(0, 3)
        self.raw_ocr_table.setHorizontalHeaderLabels(["ROI", "OCR 原始文字", "分数"])
        self.raw_ocr_table.horizontalHeader().setStretchLastSection(True)
        self.preview_label = QLabel("暂无目标窗口客户区截图")
        self.preview_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.preview_label.setMinimumSize(520, 380)
        self.overlay_label = QLabel("暂无识别证据叠加图")
        self.overlay_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.overlay_label.setMinimumSize(520, 380)
        self.roi_combo = QComboBox()
        self.roi_combo.currentIndexChanged.connect(self.show_selected_roi)
        tabs = QTabWidget()
        self.tabs_widget = tabs
        tabs.addTab(self.preview_label, "当前 ROI 截图")
        tabs.addTab(self.overlay_label, "识别证据叠加图")
        tabs.addTab(self.raw_ocr_table, "OCR 原始文字")
        self.catalog_tab = QWidget()
        self.catalog_loaded = False
        catalog_layout = QVBoxLayout(self.catalog_tab)
        self.catalog_placeholder = QLabel("打开此标签时加载属性库")
        catalog_layout.addWidget(self.catalog_placeholder, alignment=Qt.AlignmentFlag.AlignCenter)
        tabs.addTab(self.catalog_tab, '属性库')
        tabs.currentChanged.connect(self._load_catalog_tab)
        preview_widget = QWidget()
        preview_layout = QVBoxLayout(preview_widget)
        preview_layout.addWidget(self.roi_combo)
        preview_layout.addWidget(tabs)
        right_layout = QVBoxLayout()
        right_layout.addWidget(QLabel("当前笔刷属性（由工具属性 ROI 读取）"))
        right_layout.addWidget(self.parameter_table, 2)
        right_layout.addWidget(QLabel("证据摘要"))
        right_layout.addWidget(self.evidence_list, 1)
        right_widget = QWidget(); right_widget.setLayout(right_layout)
        splitter = QSplitter()
        splitter.addWidget(preview_widget); splitter.addWidget(right_widget); splitter.setSizes([680, 580])
        central = QWidget()
        layout = QVBoxLayout(central)
        layout.addLayout(top); layout.addLayout(summary); layout.addWidget(splitter, 1)
        self.setCentralWidget(central)
        self.setStatusBar(QStatusBar())

    def _load_catalog_tab(self, index: int) -> None:
        if self.catalog_loaded or self.tabs_widget.widget(index) is not self.catalog_tab:
            return
        try:
            from .property_catalog import get_catalog

            catalog = get_catalog()
            table = QTableWidget(len(catalog.definitions), 4)
            table.setHorizontalHeaderLabels(['属性', '分类', '读取范围', '匹配译名'])
            for row, definition in enumerate(catalog.definitions):
                support = '仅标签，值待支持' if definition['read_support'] == 'label_only' else '可见数字 / 勾选 / 文字选项'
                values = (definition.get('label_zh_tw') or definition['label_en'], definition['category_zh'], support, ' / '.join(definition['aliases']))
                for col, value in enumerate(values):
                    item = QTableWidgetItem(value)
                    item.setToolTip(value)
                    table.setItem(row, col, item)
            table.resizeColumnsToContents()
            self.catalog_tab.layout().replaceWidget(self.catalog_placeholder, table)
            self.catalog_placeholder.deleteLater()
            self.catalog_table = table
            self.tabs_widget.setTabText(index, f'属性库（{len(catalog.definitions)} 项）')
            self.catalog_loaded = True
        except Exception as exc:
            self.catalog_placeholder.setText(f"属性库加载失败：{exc}")

    def set_always_on_top(self, enabled: bool) -> None:
        if set_native_topmost(int(self.winId()), enabled):
            return
        if hasattr(ctypes, "windll"):
            # Keep the checkbox aligned with the last successfully applied
            # state; never recreate a Windows window as a fallback.
            self.pin_checkbox.blockSignals(True)
            self.pin_checkbox.setChecked(not enabled)
            self.pin_checkbox.blockSignals(False)
            self.statusBar().showMessage("窗口置顶设置失败，请重试")
            return
        # Non-Windows fallback. The shipped CSP tool uses the native path above.
        self.setWindowFlag(Qt.WindowType.WindowStaysOnTopHint, enabled)
        self.show()
        if enabled:
            self.raise_()

    def _bootstrap_flow(self) -> None:
        if self.profile is None:
            self.reselect()
        else:
            self.target_window = current_csp_window(self.profile.target_window)
            self.statusBar().showMessage("配置已加载；点击识别一次，或初始化 / 重新框选")
            self.window_label.setText("已加载面板配置")

    def _refresh_profiles(self, select_path: Path | None = None) -> None:
        self.profile_combo.blockSignals(True)
        self.profile_combo.clear()
        paths = self.store.list_user_profiles()
        for path in paths:
            self.profile_combo.addItem(path.stem, str(path))
        self.profile_combo.blockSignals(False)
        if not paths:
            self.profile = None
            self.profile_path = None
            return
        index = 0
        if select_path:
            for candidate in range(self.profile_combo.count()):
                if Path(self.profile_combo.itemData(candidate)) == select_path:
                    index = candidate
                    break
        self.profile_combo.setCurrentIndex(index)
        self.load_selected_profile(index)

    def load_selected_profile(self, index: int) -> None:
        if index < 0:
            return
        self.stop_recognition()
        path = Path(self.profile_combo.itemData(index))
        try:
            profile = self.store.load(path)
            if profile.profile_kind != "user" or profile.target_window is None:
                raise ValueError("该文件不是完整的用户窗口配置")
        except Exception as exc:
            self.profile = None
            self.profile_path = None
            self.statusBar().showMessage(f"配置不可用：{exc}")
            return
        same_profile_runtime = self.profile_path == path and self.target_window is not None
        self.profile, self.profile_path = profile, path
        if not same_profile_runtime:
            self.target_window = None
            self.window_label.setText("已加载面板配置")
            self.status_label.setText("状态：待识别")

    def reselect(self) -> None:
        if self._selecting:
            return
        self.stop_recognition()
        self._selecting = True
        self.hide()
        # Let Windows remove our window before acquiring the frozen frame.
        QTimer.singleShot(150, self._open_roi_selector)

    def _open_roi_selector(self) -> None:
        frame = None
        saved = False
        previous_target = self.target_window
        error = None
        try:
            target = current_csp_window(self.profile.target_window if self.profile else None)
            if target is None:
                raise CaptureError("请先打开 CSP 并保持窗口可见，再点击初始化 / 重新框选。")
            self.target_window = target
            frame = self.capture.capture_window_snapshot(target)
            compatible = self.profile is not None and not self.store.window_compatibility(self.profile, target.as_dict())
            dialog = RoiSelectorDialog(
                frame.image, target, self.profile.name if self.profile else "CSP 面板配置",
                existing_rois=self.profile.rois if compatible else None,
            )
            try:
                if dialog.exec() == dialog.DialogCode.Accepted:
                    profile = dialog.profile()
                    self.store.validate(profile)
                    self.profile_path = self.store.save(profile, self.profile_path)
                    self.profile = profile
                    self._refresh_profiles(self.profile_path)
                    self.target_window = target
                    saved = True
            finally:
                dialog.deleteLater()
        except Exception as exc:
            error = str(exc)
            self.show()
            QMessageBox.warning(self, "无法冻结画面", str(exc))
        finally:
            self._selecting = False
            self.show()
            self.raise_()
            self.activateWindow()
        if saved:
            self.window_label.setText("已保存面板范围")
            self.statusBar().showMessage("已保存，正在识别刚才框选的同一张冻结截图")
            self.start_recognition(True, frozen_frame=frame)
        else:
            self.target_window = previous_target
            self.statusBar().showMessage(error or "已取消框选，原配置保持不变")

    def save_profile(self) -> None:
        if self.profile is None or self.profile.target_window is None:
            QMessageBox.information(self, "需要初始化", "请先初始化并框选面板。")
            self.reselect()
            return
        try:
            self.profile_path = self.store.save(self.profile, self.profile_path)
            self._refresh_profiles(self.profile_path)
            self.statusBar().showMessage(f"已保存用户窗口配置：{self.profile_path.name}")
        except Exception as exc:
            QMessageBox.critical(self, "保存失败", str(exc))

    def start_recognition(self, once: bool, frozen_frame: Any = None) -> None:
        if self.profile is None or self.profile.target_window is None:
            QMessageBox.information(self, "需要初始化", "请先初始化并框选面板。")
            self.reselect()
            return
        if not any(r.role == 'tool_properties' for r in self.profile.rois):
            self.statusBar().showMessage('配置缺少工具属性区域，请重新框选工具属性面板')
            self.reselect()
            return
        if self.target_window is None:
            self.target_window = current_csp_window(self.profile.target_window)
        if self.target_window is None:
            self.clear_current_result("请打开 CSP 并保持窗口可见")
            return
        self.stop_recognition()
        if self._engine is None:
            self._engine = RapidOcrEngine()
        if not self._engine.available:
            self.clear_current_result("RapidOCR 不可用：" + self._engine.error)
            return
        self.scheduler = RecognitionScheduler(self.profile, self.capture, RecognitionPipeline(self._engine), self.target_window, frozen_frame=frozen_frame)
        generation = self._recognition_generation
        self.scheduler.result_ready.connect(lambda result: self.receive_outcome(result) if generation == self._recognition_generation else None)
        self.scheduler.message.connect(lambda message: self.statusBar().showMessage(message) if generation == self._recognition_generation else None)
        if once:
            self.scheduler.request(True)
        else:
            self.scheduler.start()
            self.start_button.setEnabled(False)

    def stop_recognition(self) -> None:
        self._recognition_generation += 1
        if self.scheduler:
            self.scheduler.stop()
        self.start_button.setEnabled(True)

    def clear_current_result(self, message: str) -> None:
        self.last_outcome = None
        self.brush_label.setText("当前笔刷：unknown")
        self.status_label.setText("状态：unavailable")
        self.timing_label.setText("耗时：—")
        self.parameter_table.setRowCount(0)
        self.evidence_list.clear()
        self.raw_ocr_table.setRowCount(0)
        self.preview_label.setPixmap(QPixmap()); self.preview_label.setText("当前没有可用目标窗口截图")
        self.overlay_label.setPixmap(QPixmap()); self.overlay_label.setText("当前没有识别证据")
        self.statusBar().showMessage(message)

    def receive_outcome(self, outcome: RecognitionOutcome) -> None:
        self.last_outcome = outcome
        result = outcome.result
        brush = result.get("brush", {})
        self.brush_label.setText(f"当前工具 / 笔刷：{brush.get('name', 'unknown')}")
        self.status_label.setText(f"状态：{result.get('status', 'unknown')} | 读取：{'仅刷新数值' if result.get('recognition_mode') == 'values_only' else '完整识别'} | 后端：{result.get('capture_backend', 'unknown')}")
        timings = result.get("timings_ms", {})
        self.timing_label.setText(f"耗时：总 {timings.get('total', 0)} ms / OCR {timings.get('ocr', 0)} ms")
        properties = brush.get("properties", [])
        self.parameter_table.setRowCount(0)
        for parameter in properties:
            row = self.parameter_table.rowCount(); self.parameter_table.insertRow(row)
            values = [parameter.get("key"), parameter.get("label"), parameter.get("type"), parameter.get("value"), parameter.get("unit") or "", parameter.get("enabled"), parameter.get("status")]
            for column, value in enumerate(values):
                if column == 3 and isinstance(value, dict) and value.get('mime_type') == 'image/png':
                    item = QTableWidgetItem('图案 PNG')
                    try:
                        pixmap = QPixmap()
                        pixmap.loadFromData(base64.b64decode(value['png_base64']))
                        if not pixmap.isNull():
                            item.setIcon(QIcon(pixmap))
                            item.setToolTip(f'当前高亮图案，{pixmap.width()} × {pixmap.height()} 像素；导出 JSON 包含完整 PNG')
                    except (KeyError, ValueError):
                        item.setText('图案读取失败')
                else:
                    item = QTableWidgetItem(str(value))
                item.setData(Qt.ItemDataRole.UserRole, parameter); self.parameter_table.setItem(row, column, item)
        self.evidence_list.clear()
        evidence_items = brush.get("evidence", []) + [entry for parameter in properties for entry in parameter.get("evidence", [])]
        for evidence in evidence_items:
            self.evidence_list.addItem(f"{evidence.get('type')} | ROI {evidence.get('roi_id')} | bbox={evidence.get('bbox')} | {evidence.get('reason')}")
        unresolved = result.get("unresolved", [])
        for item in unresolved:
            self.evidence_list.addItem(f"未解析 / 错误：{item.get('reason') or item.get('raw_text', '')}")
        raw_items = result.get("raw_ocr", [])
        self.raw_ocr_table.setRowCount(len(raw_items))
        for row, item in enumerate(raw_items):
            for column, value in enumerate((item.get("roi_id"), item.get("text"), item.get("score"))):
                self.raw_ocr_table.setItem(row, column, QTableWidgetItem(str(value)))
        self.raw_ocr_table.resizeColumnsToContents()
        self.parameter_table.resizeColumnsToContents()
        if result.get("status") == "unavailable":
            self.statusBar().showMessage("识别失败：" + "；".join(str(item.get("reason", "")) for item in unresolved))
        else:
            self.statusBar().showMessage(f"识别完成：{len(raw_items)} 个文字块，{len(properties)} 个笔刷属性；状态 {result.get('status')}")
        if outcome.roi_images:
            selected = self.roi_combo.currentData()
            self.roi_combo.blockSignals(True)
            self.roi_combo.clear()
            names = {r.id: r.name for r in self.profile.rois} if self.profile else {}
            for roi_id in outcome.roi_images:
                self.roi_combo.addItem(names.get(roi_id, roi_id), roi_id)
            index = self.roi_combo.findData(selected)
            self.roi_combo.setCurrentIndex(max(0, index))
            self.roi_combo.blockSignals(False)
            self.show_selected_roi()
        elif result.get("status") in ("unavailable", "needs_reselection"):
            self.preview_label.setPixmap(QPixmap()); self.preview_label.setText("当前没有可用目标窗口截图")
            self.overlay_label.setPixmap(QPixmap()); self.overlay_label.setText("当前没有识别证据")

    def show_selected_roi(self, *_args) -> None:
        if not self.last_outcome:
            return
        roi_id = self.roi_combo.currentData()
        for label, images in ((self.preview_label, self.last_outcome.roi_images), (self.overlay_label, self.last_outcome.overlays)):
            label.setText('')
            label.setPixmap(cv_to_pixmap(images.get(roi_id)).scaled(label.size(), Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation))

    def highlight_parameter(self, row: int, column: int) -> None:
        properties = self.last_outcome.result.get("brush", {}).get("properties", []) if self.last_outcome else []
        if not self.last_outcome or row < 0 or row >= len(properties):
            return
        parameter = properties[row]
        evidence = parameter.get("evidence", [])
        if evidence:
            self.statusBar().showMessage(f"已选中证据：ROI {evidence[0].get('roi_id')} bbox={evidence[0].get('bbox')}")

    def export_json(self) -> None:
        if not self.last_outcome:
            QMessageBox.information(self, "没有结果", "请先识别一次")
            return
        path, _ = QFileDialog.getSaveFileName(self, "导出当前 JSON", str(self.app_root / "exports" / "current.json"), "JSON (*.json)")
        if path:
            Exporter.write_json(self.last_outcome.result, path); self.statusBar().showMessage(f"已导出 {path}")

    def export_jsonl(self) -> None:
        if not self.last_outcome:
            QMessageBox.information(self, "没有结果", "请先识别一次")
            return
        path, _ = QFileDialog.getSaveFileName(self, "追加会话 JSONL", str(self.app_root / "exports" / "session.jsonl"), "JSON Lines (*.jsonl)")
        if path:
            Exporter.append_jsonl(self.last_outcome.result, path); self.statusBar().showMessage(f"已追加 {path}")

    def closeEvent(self, event: Any) -> None:
        self.stop_recognition(); self.capture.close(); event.accept()
