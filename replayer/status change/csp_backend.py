"""Local CSP command backend. Binds to loopback and exposes an allowlisted JSON API."""
import json
import math
import os
import re
import secrets
import shutil
import threading
import time
import ctypes
from ctypes import wintypes
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse

HOST = "127.0.0.1"
PORT = 38765
ROOT = Path(__file__).resolve().parent
DATA_DIR = Path.home() / "Documents" / "Codex" / "CSPBackend"
SETTINGS_FILE = DATA_DIR / "csp_backend_settings.json"
LEGACY_SETTINGS_FILE = ROOT / "csp_file_creator_settings.json"
COMMANDS_FILE = ROOT / "csp_commands.json"
NUMERIC_PARAMETERS_FILE = ROOT / "csp_numeric_parameters.json"
BLANK_TEMPLATE_FILE = ROOT / "csp_blank_template.clip"
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
SW_RESTORE = 9
KEYEVENTF_KEYUP = 0x0002
VK_CONTROL, VK_SHIFT, VK_RETURN = 0x11, 0x10, 0x0D
KEYEVENTF_UNICODE = 0x0004
CSP_VALUE_POPUP_SUFFIX = "-E583090D-0E1A-43A2-977A-5266782BDE30"

COMMANDS = json.loads(COMMANDS_FILE.read_text(encoding="utf-8-sig"))
COMMANDS_BY_ID = {item["id"]: item for item in COMMANDS}
NUMERIC_PARAMETERS = json.loads(NUMERIC_PARAMETERS_FILE.read_text(encoding="utf-8-sig"))
NUMERIC_PARAMETERS_BY_ID = {item["id"]: item for item in NUMERIC_PARAMETERS}

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowThreadProcessId.restype = wintypes.DWORD
_ENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.EnumWindows.argtypes = [_ENUMPROC, wintypes.LPARAM]
user32.EnumWindows.restype = wintypes.BOOL
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
user32.ShowWindowAsync.argtypes = [wintypes.HWND, ctypes.c_int]
user32.ShowWindowAsync.restype = wintypes.BOOL
user32.BringWindowToTop.argtypes = [wintypes.HWND]
user32.BringWindowToTop.restype = wintypes.BOOL
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.SetForegroundWindow.restype = wintypes.BOOL
user32.SetActiveWindow.argtypes = [wintypes.HWND]
user32.SetActiveWindow.restype = wintypes.HWND
user32.SetFocus.argtypes = [wintypes.HWND]
user32.SetFocus.restype = wintypes.HWND
user32.AttachThreadInput.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.BOOL]
user32.AttachThreadInput.restype = wintypes.BOOL
user32.IsIconic.argtypes = [wintypes.HWND]
user32.IsIconic.restype = wintypes.BOOL
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.restype = ctypes.c_int
user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.GetWindowRect.restype = wintypes.BOOL
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetClassNameW.restype = ctypes.c_int
class GUITHREADINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD), ("flags", wintypes.DWORD),
        ("hwndActive", wintypes.HWND), ("hwndFocus", wintypes.HWND),
        ("hwndCapture", wintypes.HWND), ("hwndMenuOwner", wintypes.HWND),
        ("hwndMoveSize", wintypes.HWND), ("hwndCaret", wintypes.HWND),
        ("rcCaret", wintypes.RECT),
    ]
user32.GetGUIThreadInfo.argtypes = [wintypes.DWORD, ctypes.POINTER(GUITHREADINFO)]
user32.GetGUIThreadInfo.restype = wintypes.BOOL
user32.IsWindow.argtypes = [wintypes.HWND]
user32.IsWindow.restype = wintypes.BOOL
user32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
user32.ClientToScreen.restype = wintypes.BOOL
user32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
user32.GetCursorPos.restype = wintypes.BOOL
user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
user32.SetCursorPos.restype = wintypes.BOOL
user32.mouse_event.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_size_t]
user32.keybd_event.argtypes = [wintypes.BYTE, wintypes.BYTE, wintypes.DWORD, ctypes.c_size_t]
class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.c_size_t)]
class INPUT_UNION(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("padding", ctypes.c_byte * 32)]
class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("u", INPUT_UNION)]
user32.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
user32.SendInput.restype = wintypes.UINT
kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL
kernel32.GetCurrentThreadId.restype = wintypes.DWORD

state_lock = threading.RLock()
command_lock = threading.Lock()
last_csp_input = None


def load_state():
    state = {}
    try:
        state = json.loads(SETTINGS_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        try:
            legacy = json.loads(LEGACY_SETTINGS_FILE.read_text(encoding="utf-8"))
            state = {key: legacy[key] for key in ("directory", "base_name", "extension") if key in legacy}
        except (OSError, ValueError):
            state = {}
    state.setdefault("api_token", secrets.token_urlsafe(32))
    state.setdefault("directory", "")
    state.setdefault("base_name", "")
    state.setdefault("extension", "")
    state.setdefault("command_shortcuts", {})
    save_state(state)
    return state


def save_state(state):
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    SETTINGS_FILE.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")


state = load_state()


def public_settings():
    with state_lock:
        return {key: state.get(key, "") for key in ("directory", "base_name", "extension", "command_shortcuts")}


def find_csp_window(prefer_foreground=True):
    windows = []

    def visit(hwnd, _param):
        if not user32.IsWindowVisible(hwnd):
            return True
        process_id = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
        process = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, process_id.value)
        if not process:
            return True
        try:
            size = wintypes.DWORD(32768)
            image_path = ctypes.create_unicode_buffer(size.value)
            if kernel32.QueryFullProcessImageNameW(process, 0, image_path, ctypes.byref(size)):
                if Path(image_path.value).name.casefold() == "clipstudiopaint.exe":
                    title = ctypes.create_unicode_buffer(512)
                    user32.GetWindowTextW(hwnd, title, len(title))
                    rect = wintypes.RECT()
                    area = 0
                    if user32.GetWindowRect(hwnd, ctypes.byref(rect)):
                        area = max(0, rect.right - rect.left) * max(0, rect.bottom - rect.top)
                    windows.append((hwnd, process_id.value, bool(title.value), area))
        finally:
            kernel32.CloseHandle(process)
        return True

    user32.EnumWindows(_ENUMPROC(visit), 0)
    if not windows:
        return None
    foreground = user32.GetForegroundWindow()
    for hwnd, _pid, _has_title, _area in windows:
        if prefer_foreground and hwnd == foreground:
            return hwnd
    windows.sort(key=lambda item: (item[3], item[2]), reverse=True)
    return windows[0][0]


def send_key(vk, up=False):
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP if up else 0, 0)


def activate_csp(hwnd):
    target_process = wintypes.DWORD()
    target_thread = user32.GetWindowThreadProcessId(hwnd, ctypes.byref(target_process))
    # Restore asynchronously so a stalled CSP window thread cannot hold the API
    # request open while Windows waits for its message pump.
    user32.ShowWindowAsync(hwnd, SW_RESTORE)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.10)
    active = user32.GetForegroundWindow()
    active_process = wintypes.DWORD()
    if active:
        user32.GetWindowThreadProcessId(active, ctypes.byref(active_process))
    if active_process.value != target_process.value:
        user32.ShowWindowAsync(hwnd, SW_RESTORE)
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.10)
        active = user32.GetForegroundWindow()
        active_process = wintypes.DWORD()
        if active:
            user32.GetWindowThreadProcessId(active, ctypes.byref(active_process))
    if active_process.value != target_process.value:
        raise RuntimeError("Windows 未将 CSP 切到前台。请确认 CSP 已打开，并且没有系统对话框阻挡。")
    return target_process.value, target_thread


def send_shortcut(keys, confirm=False):
    hwnd = find_csp_window()
    if not hwnd:
        raise RuntimeError("没有找到可见的 CSP 绘画窗口；请先打开 CSP 和画布。")
    activate_csp(hwnd)
    for key in keys:
        send_key(key)
    for key in reversed(keys):
        send_key(key, up=True)
    if confirm:
        time.sleep(0.30)
        send_key(VK_RETURN)
        send_key(VK_RETURN, up=True)
    return True


def focused_control_class(thread_id):
    info = GUITHREADINFO()
    info.cbSize = ctypes.sizeof(info)
    if not user32.GetGUIThreadInfo(thread_id, ctypes.byref(info)) or not info.hwndFocus:
        return "", False
    class_name = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(info.hwndFocus, class_name, len(class_name))
    return class_name.value, bool(info.hwndCaret and info.hwndCaret == info.hwndFocus)


def send_unicode_text(value):
    events = []
    for char in value:
        events.extend((INPUT(1, INPUT_UNION(ki=KEYBDINPUT(0, ord(char), KEYEVENTF_UNICODE, 0, 0))),
                       INPUT(1, INPUT_UNION(ki=KEYBDINPUT(0, ord(char), KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, 0, 0)))))
    packet = (INPUT * len(events))(*events)
    if user32.SendInput(len(events), packet, ctypes.sizeof(INPUT)) != len(events):
        raise RuntimeError("Windows 未能发送数值文本。请检查 CSP 与后台进程的权限级别。")


def is_csp_window(hwnd):
    if not hwnd:
        return False
    process_id = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
    process = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, process_id.value)
    if not process:
        return False
    try:
        size = wintypes.DWORD(32768)
        image_path = ctypes.create_unicode_buffer(size.value)
        return bool(kernel32.QueryFullProcessImageNameW(process, 0, image_path, ctypes.byref(size))) and Path(image_path.value).name.casefold() == "clipstudiopaint.exe"
    finally:
        kernel32.CloseHandle(process)


def editable_focus(thread_id):
    info = GUITHREADINFO()
    info.cbSize = ctypes.sizeof(info)
    if not user32.GetGUIThreadInfo(thread_id, ctypes.byref(info)) or not info.hwndFocus:
        return None
    control_class, has_caret = focused_control_class(thread_id)
    is_value_popup = control_class.casefold().endswith(CSP_VALUE_POPUP_SUFFIX.casefold())
    if not is_value_popup and not has_caret and not any(token in control_class.casefold() for token in ("edit", "spin", "numeric", "number", "lineedit", "textfield")):
        return None
    focus_rect = wintypes.RECT()
    if not user32.GetWindowRect(info.hwndFocus, ctypes.byref(focus_rect)):
        return None
    point = ((focus_rect.left + focus_rect.right) // 2, (focus_rect.top + focus_rect.bottom) // 2)
    if info.hwndCaret:
        caret_point = wintypes.POINT(info.rcCaret.left, info.rcCaret.top)
        if user32.ClientToScreen(info.hwndCaret, ctypes.byref(caret_point)):
            point = (caret_point.x, caret_point.y)
    if is_value_popup:
        pointer = wintypes.POINT()
        if user32.GetCursorPos(ctypes.byref(pointer)):
            point = (pointer.x, pointer.y)
    return {"focus": info.hwndFocus, "class": control_class, "point": point,
            "kind": "popup" if is_value_popup else "edit"}


def watch_csp_focus():
    global last_csp_input
    while True:
        hwnd = user32.GetForegroundWindow()
        if is_csp_window(hwnd):
            process_id = wintypes.DWORD()
            thread_id = user32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
            target = editable_focus(thread_id)
            if target:
                main_hwnd = find_csp_window(prefer_foreground=False) if target["kind"] == "popup" else hwnd
                window_rect = wintypes.RECT()
                if main_hwnd and user32.GetWindowRect(main_hwnd, ctypes.byref(window_rect)):
                    with state_lock:
                        if not (target["kind"] == "popup" and last_csp_input and last_csp_input.get("focus") == target["focus"]):
                            last_csp_input = {"window": main_hwnd, "thread": thread_id, "process": process_id.value,
                                              "window_rect": (window_rect.left, window_rect.top, window_rect.right, window_rect.bottom),
                                              "captured": time.monotonic(), **target}
        time.sleep(0.05)


def send_numeric_value(value):
    with state_lock:
        target = None if last_csp_input is None else dict(last_csp_input)
    if not target or not is_csp_window(target["window"]):
        raise RuntimeError("没有记录到 CSP 数值输入处。请点击“工具属性”中的具体数值，再点“写入”。")
    if time.monotonic() - target["captured"] > 30:
        raise RuntimeError("记录的 CSP 数值控件已过期。请重新点击对应参数的数字后再点“写入”。")
    rect = wintypes.RECT()
    if not user32.GetWindowRect(target["window"], ctypes.byref(rect)) or (rect.left, rect.top, rect.right, rect.bottom) != target["window_rect"]:
        raise RuntimeError("CSP 窗口位置已变化，数值没有发送。请重新点选目标数值框后再点“写入”。")
    if target["kind"] == "popup":
        point = target["point"]
        if not (rect.left <= point[0] < rect.right and rect.top <= point[1] < rect.bottom):
            raise RuntimeError("CSP 数值输入位置已变化；数值没有发送。")
        activate_csp(target["window"])
        old_cursor = wintypes.POINT()
        cursor_saved = bool(user32.GetCursorPos(ctypes.byref(old_cursor)))
        user32.SetCursorPos(*point)
        time.sleep(0.05)
        user32.mouse_event(0x0002, 0, 0, 0, 0)
        time.sleep(0.03)
        user32.mouse_event(0x0004, 0, 0, 0, 0)
        if cursor_saved:
            user32.SetCursorPos(old_cursor.x, old_cursor.y)
        current = None
        for _ in range(15):
            time.sleep(0.04)
            foreground = user32.GetForegroundWindow()
            if not is_csp_window(foreground):
                continue
            process_id = wintypes.DWORD()
            popup_thread = user32.GetWindowThreadProcessId(foreground, ctypes.byref(process_id))
            current = editable_focus(popup_thread)
            if current and current["kind"] == "popup" and current["class"] == target["class"]:
                break
        else:
            raise RuntimeError("CSP 数值输入框未重新打开；数值没有发送。")
        send_unicode_text(value)
        time.sleep(0.05)
        send_key(VK_RETURN)
        send_key(VK_RETURN, up=True)
        return current["class"]
    focus_valid = bool(user32.IsWindow(target["focus"]))
    if focus_valid:
        process_id = wintypes.DWORD()
        focus_thread = user32.GetWindowThreadProcessId(target["focus"], ctypes.byref(process_id))
        focus_valid = process_id.value == target["process"] and focus_thread == target["thread"]
    activate_csp(target["window"])
    current = editable_focus(target["thread"])
    if focus_valid and (not current or current["focus"] != target["focus"]):
        caller_thread = kernel32.GetCurrentThreadId()
        attached = bool(user32.AttachThreadInput(caller_thread, target["thread"], True))
        if not attached:
            raise RuntimeError("Windows 未允许恢复 CSP 数值框焦点；数值没有发送。")
        try:
            user32.SetFocus(target["focus"])
        finally:
            user32.AttachThreadInput(caller_thread, target["thread"], False)
        time.sleep(0.06)
        current = editable_focus(target["thread"])
    if not focus_valid or not current or current["focus"] != target["focus"]:
        point = target["point"]
        if not (rect.left <= point[0] < rect.right and rect.top <= point[1] < rect.bottom):
            raise RuntimeError("记录的数值框位置已失效；数值没有发送。")
        old_cursor = wintypes.POINT()
        cursor_saved = bool(user32.GetCursorPos(ctypes.byref(old_cursor)))
        user32.SetCursorPos(*point)
        user32.mouse_event(0x0002, 0, 0, 0, 0)
        user32.mouse_event(0x0004, 0, 0, 0, 0)
        if cursor_saved:
            user32.SetCursorPos(old_cursor.x, old_cursor.y)
        time.sleep(0.08)
        current = editable_focus(target["thread"])
    if user32.GetForegroundWindow() != target["window"] or not current or current["class"] != target["class"]:
        raise RuntimeError("CSP 数值框未能恢复焦点；数值没有发送。请重新点选该数值框后再点“写入”。")
    send_key(VK_CONTROL)
    send_key(ord("A"))
    send_key(ord("A"), up=True)
    send_key(VK_CONTROL, up=True)
    send_unicode_text(value)
    send_key(VK_RETURN)
    send_key(VK_RETURN, up=True)
    return current["class"]


def parse_shortcut_key(value):
    key = str(value or "").strip().upper()
    if len(key) == 1 and key.isalnum():
        return ord(key)
    if key.startswith("F") and key[1:].isdigit() and 1 <= int(key[1:]) <= 12:
        return 0x70 + int(key[1:]) - 1
    raise ValueError("画笔快捷键只支持单个字母、数字或 F1-F12。")


def parse_shortcut(value):
    key_codes = {
        "CTRL": VK_CONTROL, "CONTROL": VK_CONTROL, "SHIFT": VK_SHIFT, "ALT": 0x12,
        "TAB": 0x09, "DELETE": 0x2E, "DEL": 0x2E, "BACKSPACE": 0x08,
        "SPACE": 0x20, "ENTER": VK_RETURN, "RETURN": VK_RETURN,
        ";": 0xBA, "=": 0xBB, "-": 0xBD, ".": 0xBE, "/": 0xBF,
        "[": 0xDB, "]": 0xDD, "\\": 0xDC, "'": 0xDE, ",": 0xBC,
    }
    parts = [part.strip().upper() for part in str(value or "").split("+") if part.strip()]
    if not parts:
        raise ValueError("此命令没有默认快捷键；请先在 CSP 快捷键设置中配置，再填入快捷键。")
    keys = []
    for part in parts:
        if part in key_codes:
            keys.append(key_codes[part])
        elif len(part) == 1 and part.isalnum():
            keys.append(ord(part))
        elif part.startswith("F") and part[1:].isdigit() and 1 <= int(part[1:]) <= 12:
            keys.append(0x70 + int(part[1:]) - 1)
        else:
            raise ValueError(f"不支持的快捷键部分：{part}")
    return keys


def validate_leaf_name(name):
    name = str(name or "").strip()
    invalid = '<>:"/\\|?*'
    if not name or name in {".", ".."} or any(ch in invalid for ch in name) or name.endswith((".", " ")):
        raise ValueError("名称为空或包含 Windows 不允许的字符。")
    if name.split(".")[0].upper() in {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}:
        raise ValueError("名称是 Windows 保留名称。")
    return name


def next_file_path(directory, base_name, extension):
    base_name = validate_leaf_name(base_name)
    extension = str(extension or "")
    candidate = directory / f"{base_name}{extension}"
    if not candidate.exists():
        return candidate
    number = 2
    while True:
        candidate = directory / f"{base_name} ({number}){extension}"
        if not candidate.exists():
            return candidate
        number += 1


def create_csp_file(path):
    if path.suffix.casefold() != ".clip":
        raise ValueError("目前只支持创建 CSP 的 .clip 文件。请把首次保存文件名设为 .clip。")
    if not BLANK_TEMPLATE_FILE.is_file() or BLANK_TEMPLATE_FILE.stat().st_size < 1024:
        raise RuntimeError("缺少有效的 CSP 空白画布模板，请重新安装 csp_blank_template.clip。")
    with BLANK_TEMPLATE_FILE.open("rb") as source:
        if source.read(8) != b"CSFCHUNK":
            raise RuntimeError("CSP 空白画布模板格式无效。")
        source.seek(0)
        created = False
        try:
            with path.open("xb") as target:
                created = True
                shutil.copyfileobj(source, target)
        except OSError:
            if created:
                path.unlink(missing_ok=True)
            raise
    return str(path)


def execute(command, params):
    if command == "set_numeric_value":
        parameter_id = str(params.get("parameter_id", ""))
        parameter = NUMERIC_PARAMETERS_BY_ID.get(parameter_id)
        if not parameter:
            raise ValueError("找不到该数值参数。")
        raw = str(params.get("value", "")).strip()
        if not re.fullmatch(r"-?\d+(?:\.\d+)?", raw):
            raise ValueError("请输入有效数字，可包含负号和小数点。")
        value = float(raw)
        if not math.isfinite(value) or not parameter["min"] <= value <= parameter["max"]:
            raise ValueError(f"{parameter['name']}的可输入范围为 {parameter['min']} 到 {parameter['max']} {parameter['unit']}。")
        control_class = send_numeric_value(raw)
        return {"parameter": parameter["name"], "value": raw, "control_class": control_class,
                "note": f"已向 CSP 输入 {parameter['name']}：{raw}。"}

    if command == "set_command_shortcut":
        command_id = str(params.get("command_id", ""))
        if command_id not in COMMANDS_BY_ID:
            raise ValueError("找不到要配置的命令。")
        shortcut = str(params.get("shortcut", "")).strip()
        parse_shortcut(shortcut)
        with state_lock:
            state["command_shortcuts"][command_id] = shortcut
            save_state(state)
        return {"command_id": command_id, "shortcut": shortcut}

    if command == "configure_output":
        selected = Path(str(params.get("path", "")).strip().strip('"')).expanduser()
        if not selected.name or not selected.parent.is_dir() or selected.suffix.casefold() != ".clip":
            raise ValueError("保存路径无效。")
        with state_lock:
            state["directory"] = str(selected.parent)
            state["base_name"] = selected.stem
            state["extension"] = selected.suffix
            save_state(state)
            path = next_file_path(selected.parent, selected.stem, selected.suffix)
            created = create_csp_file(path)
        return {"created": created, "settings": public_settings()}

    if command == "create_next_file":
        with state_lock:
            directory = Path(state.get("directory", ""))
            base_name = validate_leaf_name(state.get("base_name", ""))
            if not directory.is_dir():
                raise ValueError("自动保存位置无效，请重新设置。")
            created = create_csp_file(next_file_path(directory, base_name, state.get("extension", "")))
        return {"created": created}

    if command == "create_folder":
        name = validate_leaf_name(params.get("name"))
        with state_lock:
            directory = Path(state.get("directory", ""))
            if not directory.is_dir():
                raise ValueError("自动保存位置无效，请重新设置。")
            target = directory / name
            target.mkdir()
        return {"created": str(target)}

    definition = COMMANDS_BY_ID.get(command)
    if not definition:
        raise ValueError(f"未支持的命令：{command}")
    if command == "select_brush_shortcut":
        keys = parse_shortcut(params.get("key") or params.get("shortcut"))
        shortcut = params.get("key") or params.get("shortcut")
    else:
        with state_lock:
            configured_shortcut = state.get("command_shortcuts", {}).get(command, "")
        shortcut = params.get("shortcut") or configured_shortcut or definition.get("shortcut", "")
        keys = parse_shortcut(shortcut)
    confirm = bool(definition.get("confirm", False))
    send_shortcut(keys, confirm)
    return {"sent": command, "shortcut": shortcut, "note": "已发送快捷键；CSP 最终状态取决于当前画布和快捷键配置。"}


class Handler(BaseHTTPRequestHandler):
    server_version = "CSPBackend/1.8"

    def log_message(self, _format, *_args):
        return

    def reply(self, status, payload):
        raw = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(raw)

    def authorized(self):
        return secrets.compare_digest(self.headers.get("X-CSP-Token", ""), state.get("api_token", ""))

    def do_GET(self):
        route = urlparse(self.path).path
        if route == "/v1/health":
            self.reply(200, {"ok": True, "service": "csp-backend", "version": "1.8", "pid": os.getpid(),
                             "desktop_accessible": bool(user32.GetForegroundWindow())})
        elif route == "/v1/commands":
            with state_lock:
                configured = dict(state.get("command_shortcuts", {}))
            commands = []
            for item in COMMANDS:
                row = dict(item)
                row["default_shortcut"] = item.get("shortcut", "")
                row["shortcut"] = configured.get(item["id"], item.get("shortcut", ""))
                row["customized"] = item["id"] in configured
                commands.append(row)
            self.reply(200, {"commands": commands})
        elif route == "/v1/settings":
            if not self.authorized():
                self.reply(401, {"error": "unauthorized"})
            else:
                self.reply(200, public_settings())
        else:
            self.reply(404, {"error": "not_found"})

    def do_POST(self):
        route = urlparse(self.path).path
        if route != "/v1/command":
            self.reply(404, {"error": "not_found"})
            return
        host = self.headers.get("Host", "").split(":", 1)[0].lower()
        if host not in {"127.0.0.1", "localhost"} or not self.authorized():
            self.reply(403, {"error": "forbidden"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length < 1 or length > 16384:
                raise ValueError("请求内容大小无效。")
            body = json.loads(self.rfile.read(length).decode("utf-8"))
            command = str(body.get("command", ""))
            params = body.get("params") or {}
            if not isinstance(params, dict):
                raise ValueError("params 必须是对象。")
            if command not in COMMANDS_BY_ID and command not in {"configure_output", "create_next_file", "create_folder", "set_command_shortcut", "set_numeric_value"}:
                raise ValueError(f"未支持的命令：{command}")
            if not command_lock.acquire(timeout=12):
                self.reply(409, {"ok": False, "error": "上一条命令仍未完成，本次没有执行。请查看 CSP 当前状态后再操作。"})
                return
            try:
                result = execute(command, params)
            finally:
                command_lock.release()
            self.reply(200, {"ok": True, "command": command, "result": result})
        except FileExistsError:
            self.reply(409, {"ok": False, "error": "同名文件或文件夹已存在。"})
        except (ValueError, OSError, RuntimeError, json.JSONDecodeError) as exc:
            self.reply(400, {"ok": False, "error": str(exc)})


def main():
    threading.Thread(target=watch_csp_focus, daemon=True, name="csp-focus-watcher").start()
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    server.daemon_threads = True
    server.serve_forever()


if __name__ == "__main__":
    main()
