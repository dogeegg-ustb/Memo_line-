"""Startup selection must be confirmed by the recorder before any capture starts."""
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "recorder_integration"), str(ROOT), str(ROOT.parent / "CSP_Shortcut_Manager")]
from setup_ui import SetupDialog


def variable(value):
    return SimpleNamespace(get=lambda: value, set=Mock())


class DriverSelectionTests(unittest.TestCase):
    def dialog(self, choice="driver"):
        dialog = SetupDialog.__new__(SetupDialog)
        dialog.pending = False
        dialog.width, dialog.height, dialog.workers = variable("1000"), variable("800"), variable(2)
        dialog.driver_choice = variable(choice)
        dialog.driver_choices = {"driver": "driver.json", "disabled": None}
        dialog.screen_choice = variable("automatic")
        dialog.screen_choices = {"automatic": None}
        dialog.driver_applied_selection = None
        dialog.driver_request_id = None
        dialog.capture_button = Mock()
        dialog.window = Mock()
        dialog.status = variable("")
        dialog.runtime = SimpleNamespace(engine=SimpleNamespace(regions={"canvas": [0, 0, 100, 100]}),
            settings={}, request_driver_configuration=Mock())
        return dialog

    @patch("setup_ui.messagebox.showerror")
    def test_missing_selection_does_not_start_or_request_a_mapping(self, error):
        dialog = self.dialog("请选择")
        dialog.begin()
        dialog.runtime.request_driver_configuration.assert_not_called()
        error.assert_called_once()

    def test_start_waits_for_driver_confirmation(self):
        dialog = self.dialog()
        dialog.begin(True)
        self.assertTrue(dialog.pending)
        dialog.runtime.request_driver_configuration.assert_called_once_with(dialog.driver_request_id, "driver.json", None)
        self.assertEqual(dialog.runtime.settings, {})

    def test_explicit_disable_is_also_confirmed(self):
        dialog = self.dialog("disabled")
        dialog.begin()
        dialog.runtime.request_driver_configuration.assert_called_once_with(dialog.driver_request_id, None, None)

    @patch("setup_ui.messagebox.showerror")
    def test_invalid_config_returns_to_selection_without_continuing(self, error):
        dialog = self.dialog(); dialog.begin()
        request = dialog.driver_request_id
        dialog.begin = Mock()
        dialog.driver_configuration_result(dict(requestId=request, data=dict(status="unavailable", warnings=["bad config"])))
        self.assertFalse(dialog.pending)
        self.assertIsNone(dialog.driver_applied_selection)
        dialog.begin.assert_not_called()
        error.assert_called_once()

    def test_successful_confirmation_resumes_once_and_saves_user_choice(self):
        dialog = self.dialog(); dialog.begin(True)
        request = dialog.driver_request_id
        dialog.begin = Mock()
        reply = dict(requestId=request, data=dict(status="selected"))
        dialog.driver_configuration_result(reply)
        dialog.driver_configuration_result(reply)
        dialog.begin.assert_called_once_with(True)
        self.assertEqual(dialog.runtime.settings["driverConfigPath"], "driver.json")
        self.assertFalse(dialog.runtime.settings["driverMappingDisabled"])


if __name__ == "__main__":
    unittest.main()
