import base64
import ctypes
import io
import os
import sys
import tempfile
import unittest
from ctypes import wintypes
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import api  # noqa: E402
import virtual_display  # noqa: E402
import display_config  # noqa: E402
import guard  # noqa: E402
import panel_host  # noqa: E402
import stash  # noqa: E402
from capture import Capture  # noqa: E402
from csp_windows import CspWindow  # noqa: E402
from virtual_display import Monitor  # noqa: E402
from workspace import Workspace  # noqa: E402

PHYSICAL = Monitor(r"\\.\DISPLAY1", "GPU", (0, 0, 2560, 1600), (0, 0, 2560, 1516), True, False, 144)
VIRTUAL = Monitor(r"\\.\DISPLAY11", "GPU", (2560, 1600, 5120, 3040), (2560, 1600, 5120, 3040), False, True, 144)


def window(hwnd, left, top, width=300, height=500, title=""):
    bounds = (left, top, left + width, top + height)
    return CspWindow(hwnd, 42, title, "PaletteClass", bounds, bounds, 0x10, True)


class DisplayConfigTests(unittest.TestCase):
    def test_structures_match_the_windows_sdk(self):
        self.assertEqual(ctypes.sizeof(display_config.PathInfo), 72)
        self.assertEqual(ctypes.sizeof(display_config.ModeInfo), 64)
        self.assertEqual(ctypes.sizeof(display_config.TargetName), 420)
        self.assertEqual(ctypes.sizeof(display_config.SourceName), 84)
        self.assertEqual(ctypes.sizeof(display_config.DpiScaleGet), 32)

    def test_device_path_to_instance_id(self):
        self.assertEqual(display_config.instance_id(
            r"\\?\DISPLAY#MTT1337#1&28a6823a&0&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"),
            r"DISPLAY\MTT1337\1&28a6823a&0&UID256")

    def test_compact_drops_unreferenced_modes_and_renumbers(self):
        modes = [display_config.ModeInfo(infoType=1, id=i) for i in range(4)]
        paths = [display_config.PathInfo(), display_config.PathInfo()]
        paths[0].sourceInfo.modeInfoIdx, paths[0].targetInfo.modeInfoIdx = 2, 3
        paths[1].sourceInfo.modeInfoIdx = 2  # a clone shares its source mode
        paths[1].targetInfo.modeInfoIdx = display_config.MODE_IDX_INVALID
        kept_paths, kept_modes = display_config.compact(paths, modes)
        self.assertEqual([m.id for m in kept_modes], [2, 3])
        self.assertEqual([(p.sourceInfo.modeInfoIdx, p.targetInfo.modeInfoIdx) for p in kept_paths],
                         [(0, 1), (0, display_config.MODE_IDX_INVALID)])
        self.assertEqual(paths[0].sourceInfo.modeInfoIdx, 2)  # the input is left untouched


class PipeTests(unittest.TestCase):
    count = 0

    def start(self, handler):
        PipeTests.count += 1
        name = f"MemoLine.PanelStash.Test.{os.getpid()}.{PipeTests.count}"
        server = api.PipeServer(name, handler, lambda *a, **k: None)
        server.start()
        self.addCleanup(server.stop)
        return name

    def test_request_response_on_one_connection(self):
        name = self.start(lambda request: dict(echo=request.get("value")))
        with api.PipeClient(name) as client:
            self.assertEqual(client.request("ping", value="中文", requestId="r1"),
                             dict(success=True, echo="中文", requestId="r1"))
            self.assertEqual(client.request("ping", value=2), dict(success=True, echo=2))

    def test_errors_are_responses_not_disconnects(self):
        def handler(request):
            raise LookupError("没有这个窗口")
        name = self.start(handler)
        with api.PipeClient(name) as client:
            self.assertEqual(client.request("x", requestId=7), dict(success=False, error="没有这个窗口", requestId=7))
            data = b"not json\n"
            api.KERNEL32.WriteFile(client.handle, data, len(data), ctypes.byref(wintypes.DWORD()), None)
            response = client.request("x")  # the bad line's answer arrives first
            self.assertFalse(response["success"])
            self.assertIn("JSON", response["error"])
            self.assertEqual(client.request("x")["error"], "没有这个窗口")

    def test_large_response(self):
        blob = base64.b64encode(os.urandom(3 * 1024 * 1024)).decode("ascii")
        name = self.start(lambda request: dict(data=blob))
        with api.PipeClient(name) as client:
            self.assertEqual(client.request("big")["data"], blob)

    def test_second_server_with_the_same_name_is_refused(self):
        name = self.start(lambda request: {})
        with self.assertRaises(api.AlreadyRunning):
            api.PipeServer(name, lambda request: {}, print)


SMALL = Monitor(r"\\.\DISPLAY14", "GPU", (2560, 1600, 4096, 2560), (2560, 1600, 4096, 2560), False, True, 144)


def sync_job(work, done):
    try:
        result, error = work(), None
    except (virtual_display.VirtualDisplayMissing, OSError, TimeoutError) as ex:
        result, error = None, str(ex)
    done(result, error)


class GuardTests(unittest.TestCase):
    AREA = (2560, 1600, 5120, 3200)

    def test_only_other_programs_centred_on_the_virtual_screen_are_intruders(self):
        windows = [guard.TopWindow(1, 10, "notepad.exe", "Notepad", (2600, 1700, 3200, 2200)),
                   guard.TopWindow(2, 11, "CLIPStudioPaint.exe", "Palette", (2600, 1700, 2900, 2200)),
                   guard.TopWindow(3, 12, "explorer.exe", "Shell_SecondaryTrayWnd", (2560, 3100, 5120, 3200)),
                   guard.TopWindow(4, 99, "python.exe", "TkTopLevel", (2560, 1600, 5120, 3200)),
                   guard.TopWindow(5, 13, "chrome.exe", "Chrome_WidgetWin_1", (2000, 1000, 2700, 1700)),
                   guard.TopWindow(6, 14, "explorer.exe", "CabinetWClass", (3000, 2000, 3800, 2600))]
        found = guard.intruders(windows, self.AREA, ["clipstudiopaint.exe"], own_pid=99)
        self.assertEqual([w.hwnd for w in found], [1, 6])

    def test_evicted_window_keeps_its_offset_inside_the_work_area(self):
        work = (0, 0, 2560, 1516)
        self.assertEqual(guard.evicted_position((2700, 1800, 3300, 2300), self.AREA, work), (140, 200))
        self.assertEqual(guard.evicted_position((4900, 3000, 5500, 3500), self.AREA, work), (1960, 1016))


class FakeRoot:
    def after(self, *args):
        pass


class HostRequestTests(unittest.TestCase):
    """Host.handle_request against fake windows; Tk is not involved."""

    def setUp(self):
        self.moves = []
        original = stash.move_window
        stash.move_window = lambda w, x, y: self.moves.append((w.hwnd, x, y))
        self.addCleanup(setattr, stash, "move_window", original)
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        host = panel_host.Host.__new__(panel_host.Host)
        host.settings = dict(panel_host.DEFAULTS)
        host.closing = host.display_busy = host.provide_again = host.virtual_active = False
        host.virtual_error = host.empty_since = None
        host.virtual_devices, host.pending = {VIRTUAL.device}, {}
        host.display_job = sync_job
        host.hardware = tuple(host.settings["virtualHardwareIds"])
        host.settings["virtualMinimumSize"] = None
        host.main = None
        host.root = FakeRoot()
        host.workspace = Workspace()
        host.screens = [PHYSICAL, VIRTUAL]
        host.windows = {11: window(11, 100, 100, title="历史记录"), 12: window(12, 1800, 200)}
        host.labels = {11: panel_host.WindowLabel(("palettekindhistory",), "palettekindhistory", "窗口标题"),
                       12: panel_host.WindowLabel(("palettekindlayerorder", "palettekindproperty"),
                                                  "palettekindproperty", "工作区浮动窗口1")}
        host.stash = stash.StashManager(Path(directory.name) / "state.json", 32, 8, lambda *a, **k: None)
        host.call = lambda function: function()
        host.log = lambda *a, **k: None
        self.host = host

    def request(self, command, **fields):
        return self.host.handle_request(dict(command=command, **fields))

    def test_list_windows_with_names(self):
        windows = self.request("listWindows")["windows"]
        self.assertEqual([(w["hwnd"], w["name"], w["stashed"]) for w in windows],
                         [(11, "历史记录", False), (12, "图层 | 图层属性", False)])
        self.assertEqual(windows[1]["kinds"], ["palettekindlayerorder", "palettekindproperty"])
        self.assertEqual(windows[1]["dpi"], 144)
        self.assertEqual(self.request("listWindows", stashedOnly=True)["windows"], [])

    def test_lookup_by_kind_or_display_name(self):
        self.assertEqual(self.host.lookup(dict(kind="历史记录")), 11)
        self.assertEqual(self.host.lookup(dict(kind="palettekindlayerorder")), 12)
        self.assertEqual(self.host.lookup(dict(hwnd=12)), 12)
        with self.assertRaises(LookupError):
            self.host.lookup(dict(kind="palettekindnavigator"))
        with self.assertRaises(LookupError):
            self.host.lookup(dict(hwnd=99))

    def test_requests_are_validated(self):
        with self.assertRaises(ValueError):
            self.request("captureWindow", hwnd=11, kind="历史记录")
        with self.assertRaises(ValueError):
            self.request("captureWindow", hwnd=11, colour="red")
        with self.assertRaises(ValueError):
            self.request("captureWindow", hwnd=11, format="jpeg")
        with self.assertRaises(ValueError):
            self.request("stashWindow", hwnd=11, side="top")
        with self.assertRaises(ValueError):
            self.request("deleteWindow", hwnd=11)

    def test_stash_and_restore(self):
        stashed = self.request("stashWindow", kind="palettekindhistory", side="right")["window"]
        self.assertEqual((stashed["stashed"], stashed["side"]), (True, "right"))
        self.assertEqual(self.moves[-1], (11, 2568, 1608))
        self.assertEqual([w["hwnd"] for w in self.request("listWindows", stashedOnly=True)["windows"]], [11])
        self.host.windows[11] = window(11, 3848, 1608)
        restored = self.request("restoreWindow", hwnd=11)["window"]
        self.assertFalse(restored["stashed"])
        self.assertEqual(self.moves[-1], (11, 100, 100))
        with self.assertRaises(LookupError):
            self.request("restoreWindow", hwnd=12)  # neither stashed nor on the virtual screen

    def patch(self, name, value):
        original = getattr(virtual_display, name)
        setattr(virtual_display, name, value)
        self.addCleanup(setattr, virtual_display, name, original)

    def test_stash_reports_a_missing_driver(self):
        self.host.screens = [PHYSICAL]

        def missing(*args, **kwargs):
            raise virtual_display.VirtualDisplayMissing("没有找到虚拟显示器")
        self.patch("provide", missing)
        with self.assertRaisesRegex(RuntimeError, "没有找到虚拟显示器"):
            self.request("stashWindow", hwnd=11)
        self.assertEqual(self.host.pending, {})

    def test_stash_attaches_a_screen_sized_for_the_palettes(self):
        self.host.screens = [PHYSICAL]
        asked = []

        def provide(fits, hardware, match_scale):
            asked.append((fits((1536, 960)), fits((2560, 1600)), match_scale))
            return SMALL, ["接入虚拟屏", "1536×960"]
        self.patch("provide", provide)
        window = self.request("stashWindow", kind="历史记录")["window"]
        # Room to spare: at least the main screen's size, although one 300x500 palette fits on 1536x960.
        self.assertEqual(asked, [(False, True, True)])
        self.assertTrue(window["stashed"] and window["onVirtualScreen"] is False)  # the fake window did not move
        self.assertEqual(self.moves[-1], (11, 2568, 1608))
        self.assertEqual(self.host.virtual_devices, {SMALL.device})

    def test_screen_grows_only_when_the_palettes_no_longer_fit(self):
        self.host.screens = [PHYSICAL, SMALL]
        self.host.windows[13] = window(13, 500, 100, 450, 1000)
        self.host.labels[13] = panel_host.WindowLabel((), None, "未识别")
        asked = []

        def provide(fits, hardware, match_scale):
            asked.append((fits((1536, 960)), fits((2560, 1600))))
            return VIRTUAL, ["2560×1600"]
        self.patch("provide", provide)
        self.request("stashWindow", hwnd=11)  # 300x500 fits on 1536x960: no display change
        self.assertEqual(asked, [])
        self.request("stashWindow", hwnd=13)  # 450x1000 is taller than 1536x960 allows
        self.assertEqual(asked, [(False, True)])
        self.assertEqual(set(self.host.stash.records), {11, 13})
        self.assertIn(VIRTUAL, self.host.screens)

    def test_idle_screen_is_detached_after_the_delay(self):
        self.host.virtual_active = True
        released = []
        self.patch("release", lambda hardware: released.append(True) or True)
        self.host.detach_when_idle(100.0)
        self.host.detach_when_idle(104.0)
        self.assertEqual(released, [])
        self.host.detach_when_idle(105.5)
        self.assertEqual(released, [True])
        self.assertFalse(self.host.virtual_active)
        self.host.stash.records[11] = None  # anything stashed keeps it attached
        self.host.virtual_active = True
        self.host.detach_when_idle(200.0)
        self.host.detach_when_idle(300.0)
        self.assertEqual(released, [True])

    def test_capture_formats(self):
        pixels = bytes([10, 20, 30, 0]) * 6
        original = panel_host.capture
        panel_host.capture = lambda hwnd, bounds: Capture(hwnd, (100, 100, 103, 102), 3, 2, pixels, 1.5)
        self.addCleanup(setattr, panel_host, "capture", original)
        response = self.request("captureWindow", kind="历史记录")
        image = Image.open(io.BytesIO(base64.b64decode(response["data"])))
        self.assertEqual((response["format"], image.size, image.getpixel((0, 0))), ("png", (3, 2), (30, 20, 10)))
        self.assertEqual((response["width"], response["height"], response["rect"]), (3, 2, [100, 100, 103, 102]))
        raw = self.request("captureWindow", hwnd=11, format="bgrx")
        self.assertEqual(base64.b64decode(raw["data"]), pixels)

    def test_status(self):
        status = self.request("getStatus")
        self.assertTrue(status["virtualDisplay"]["attached"])
        self.assertEqual(status["virtualDisplay"]["monitors"], [dict(device=VIRTUAL.device, rect=list(VIRTUAL.rect),
                                                                     dpi=144)])
        self.assertEqual((status["windows"], status["stashed"], status["csp"]["running"]), (2, 0, False))


if __name__ == "__main__":
    unittest.main()
