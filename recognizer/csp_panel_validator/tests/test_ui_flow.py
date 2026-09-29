import os
import unittest
from contextlib import contextmanager
from unittest.mock import Mock, patch
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

import numpy as np
from PySide6.QtCore import QPoint, Qt
from PySide6.QtTest import QTest
from PySide6.QtWidgets import QApplication

from csp_panel_validator.models import CaptureFrame, MonitorProfile, Profile, Rect, RoiDefinition, RecognitionOutcome
from csp_panel_validator.profile_store import ProfileStore
from csp_panel_validator.roi_selector import _Canvas, RoiSelectorDialog
from csp_panel_validator.ui import MainWindow
from csp_panel_validator.recognition_scheduler import RecognitionScheduler
from csp_panel_validator.window_service import WindowInfo


@contextmanager
def flow_directory():
    root = Path(__file__).resolve().parent / "_flow_profile"
    root.mkdir(exist_ok=True)
    try:
        yield root
    finally:
        profiles = root / "profiles"
        if profiles.exists():
            for path in profiles.glob("*.json"):
                path.unlink()
            profiles.rmdir()
        root.rmdir()


class UiFlowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def test_drag_preview_is_clamped_and_saved_roi_is_inside_image(self):
        canvas = _Canvas(np.zeros((100, 200, 3), dtype=np.uint8))
        canvas.resize(500, 300)
        canvas.show()
        self.app.processEvents()
        QTest.mousePress(canvas, Qt.MouseButton.LeftButton, pos=QPoint(80, 80))
        QTest.mouseMove(canvas, QPoint(600, 350), 30)
        self.assertIsNotNone(canvas._preview_rect)
        QTest.mouseRelease(canvas, Qt.MouseButton.LeftButton, pos=QPoint(600, 350))
        self.assertEqual(len(canvas.rois), 1)
        rect = canvas.rois[0].rect
        self.assertTrue(rect.contains_size(200, 100))
        self.assertGreater(rect.width * rect.height, 0)
        canvas.close()

    def test_restart_lists_only_complete_user_window_profiles(self):
        root = Path(__file__).resolve().parent / "_restart_profile"
        path = root / "user.json"
        window = WindowInfo(101, "Canvas", "CLIPStudioPaintView", "CLIPStudioPaint.exe", 10, Rect(0, 0, 900, 700), Rect(8, 40, 892, 650), 144.0)
        profile = Profile("restart", MonitorProfile("window", "Canvas", window.client_rect, window.capture_size, window.dpi, window.dpi / 96.0), [RoiDefinition("props", "props", "tool_properties", Rect(10, 10, 100, 100))], target_window=window.binding())
        store = ProfileStore(root)
        try:
            store.save(profile, path)
            restarted = ProfileStore(root)
            self.assertEqual(restarted.list_user_profiles(), [path])
            loaded = restarted.load(path)
            self.assertEqual(loaded.target_window.capture_size, (892, 650))
            self.assertIsNone(loaded.target_window.__dict__.get("hwnd"))
        finally:
            try:
                path.unlink()
                root.rmdir()
            except OSError:
                pass

    def test_second_drag_and_role_change_preserve_first_roi(self):
        target = WindowInfo(101, "Canvas", "CSP", "CLIPStudioPaint.exe", 10, Rect(0, 0, 800, 600), Rect(0, 0, 800, 600), 96)
        dialog = RoiSelectorDialog(np.zeros((600, 800, 3), dtype=np.uint8), target)
        canvas = dialog.canvas
        canvas.resize(800, 600)
        for start, end in ((QPoint(20, 20), QPoint(150, 150)), (QPoint(220, 20), QPoint(350, 150))):
            QTest.mousePress(canvas, Qt.MouseButton.LeftButton, pos=start)
            QTest.mouseMove(canvas, end)
            QTest.mouseRelease(canvas, Qt.MouseButton.LeftButton, pos=end)
            canvas.grab()  # Regression: painting after release must not access a deleted member.
            if len(canvas.rois) == 1:
                dialog.role_combo.setCurrentIndex(1)
        self.assertEqual([r.role for r in canvas.rois], ["tool_properties", "tool_list"])
        self.assertEqual(len(dialog.profile().rois), 2)
        self.assertTrue(dialog.windowFlags() & Qt.WindowType.FramelessWindowHint)
        dialog.close()

    def test_initialization_hides_before_capture_and_uses_same_frozen_frame(self):
        target = WindowInfo(101, "Canvas", "CSP", "CLIPStudioPaint.exe", 10, Rect(0, 0, 800, 600), Rect(0, 0, 800, 600), 96)
        profile = Profile("test", MonitorProfile("window", "Canvas", target.client_rect, target.capture_size, 96, 1), [RoiDefinition("props", "props", "tool_properties", Rect(10, 10, 100, 100))], target_window=target.binding())
        frame = CaptureFrame(1, "now", np.zeros((600, 800, 3), dtype=np.uint8))
        with flow_directory() as root, patch("csp_panel_validator.ui.QTimer.singleShot") as timer:
            window = MainWindow(root)
            window.show()
            window.capture.capture_window_snapshot = Mock(return_value=frame)
            window.start_recognition = Mock()
            window.reselect()
            self.assertFalse(window.isVisible())
            window.capture.capture_window_snapshot.assert_not_called()
            self.assertEqual(timer.call_args.args[0], 150)
            fake = Mock()
            fake.DialogCode.Accepted = 1
            fake.exec.return_value = 1
            fake.profile.return_value = profile
            with patch("csp_panel_validator.ui.current_csp_window", return_value=target), patch("csp_panel_validator.ui.RoiSelectorDialog", return_value=fake):
                timer.call_args.args[1]()
            window.start_recognition.assert_called_once_with(True, frozen_frame=frame)
            self.assertTrue(window.isVisible())
            self.assertTrue(window.profile_path.exists())
            window.close()

    def test_frozen_ocr_does_not_recapture_and_stop_discards_work(self):
        frame = CaptureFrame(1, "now", np.zeros((100, 100, 3), dtype=np.uint8))
        capture = Mock()
        pipeline = Mock()
        pipeline.process.return_value = RecognitionOutcome({"status": "ok"})
        scheduler = RecognitionScheduler(Mock(), capture, pipeline, frozen_frame=frame)
        outcomes = []
        scheduler.result_ready.connect(outcomes.append)
        scheduler._execute(True)
        capture.capture.assert_not_called()

        self.assertIs(pipeline.process.call_args.args[0], frame)
        self.assertEqual(len(outcomes), 1)
        scheduler.stop()
        scheduler._execute(True)
        self.assertEqual(len(outcomes), 1)
        capture.capture.assert_not_called()

    def test_error_reason_and_raw_text_are_visible(self):
        with flow_directory() as root, patch("csp_panel_validator.ui.QTimer.singleShot"):
            window = MainWindow(root)
            window.receive_outcome(RecognitionOutcome({
                "status": "unavailable", "brush": {"name": "unknown", "properties": []},
                "unresolved": [{"reason": "parse: ValueError: diagnostic test"}],
                "raw_ocr": [{"roi_id": "props", "text": "颜色容差", "score": .99}],
            }))
            self.assertIn("ValueError", window.statusBar().currentMessage())
            self.assertIn("diagnostic test", window.evidence_list.item(0).text())
            self.assertEqual(window.raw_ocr_table.item(0, 1).text(), "颜色容差")
            window.close()


if __name__ == "__main__":
    unittest.main()
