"""Monitors, and the virtual screen of the bundled Virtual-Display-Driver.

The driver (upstream Virtual-Display-Driver, hardware ID ``Root\\MttVDD``, installed by
``vdd/install-vdd.ps1``) creates its monitor when it loads and offers the resolutions listed in its
vdd_settings.xml; it has no command to add or remove a monitor at runtime. Depending on the Win+P
projection mode, Windows attaches the monitor as a clone of the main screen, which costs as much as
a real screen and leaves no separate area to put palettes on. So this module never talks to the
driver: it detaches the monitor while nothing is stashed and, when palettes are to be stashed,
attaches it as an extended screen just large enough for them, through the user's display
configuration (no administrator rights needed).
"""
from __future__ import annotations

import ctypes
import time
from ctypes import wintypes
from dataclasses import dataclass

import display_config

USER32 = ctypes.WinDLL("user32", use_last_error=True)
SHCORE = ctypes.WinDLL("shcore")

HARDWARE_IDS = (r"Root\MttVDD",)


class DisplayDevice(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("DeviceName", wintypes.WCHAR * 32),
                ("DeviceString", wintypes.WCHAR * 128), ("StateFlags", wintypes.DWORD),
                ("DeviceID", wintypes.WCHAR * 128), ("DeviceKey", wintypes.WCHAR * 128)]


class MonitorInfoEx(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT), ("rcWork", wintypes.RECT),
                ("dwFlags", wintypes.DWORD), ("szDevice", wintypes.WCHAR * 32)]


USER32.EnumDisplayDevicesW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(DisplayDevice), wintypes.DWORD]
USER32.GetMonitorInfoW.argtypes = [wintypes.HMONITOR, ctypes.POINTER(MonitorInfoEx)]
SHCORE.GetDpiForMonitor.argtypes = [wintypes.HMONITOR, ctypes.c_int, ctypes.POINTER(wintypes.UINT),
                                    ctypes.POINTER(wintypes.UINT)]


@dataclass(frozen=True)
class Monitor:
    device: str
    adapter: str
    rect: tuple[int, int, int, int]  # left, top, right, bottom in physical pixels
    work: tuple[int, int, int, int]
    primary: bool
    virtual: bool
    dpi: int

    @property
    def width(self) -> int:
        return self.rect[2] - self.rect[0]

    @property
    def height(self) -> int:
        return self.rect[3] - self.rect[1]

    def contains(self, x: float, y: float) -> bool:
        return self.rect[0] <= x < self.rect[2] and self.rect[1] <= y < self.rect[3]

    def label(self) -> str:
        kind = "虚拟" if self.virtual else ("主屏" if self.primary else "物理")
        return f"{self.device} [{kind}] {self.width}x{self.height} @({self.rect[0]},{self.rect[1]}) DPI {self.dpi}"


def adapters() -> dict[str, str]:
    """Display output name -> adapter description, e.g. \\\\.\\DISPLAY2 -> NVIDIA GeForce …"""
    result, index = {}, 0
    device = DisplayDevice(cb=ctypes.sizeof(DisplayDevice))
    while USER32.EnumDisplayDevicesW(None, index, ctypes.byref(device), 0):
        result[device.DeviceName] = device.DeviceString
        index += 1
        device = DisplayDevice(cb=ctypes.sizeof(DisplayDevice))
    return result


def monitors(virtual_devices=frozenset()) -> list[Monitor]:
    """Monitors on the desktop; those whose GDI name is in `virtual_devices` are marked virtual."""
    names = adapters()
    found: list[Monitor] = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HMONITOR, wintypes.HDC,
                                       ctypes.POINTER(wintypes.RECT), wintypes.LPARAM)

    @callback_type
    def callback(handle, _dc, _rect, _data):
        info = MonitorInfoEx(cbSize=ctypes.sizeof(MonitorInfoEx))
        if USER32.GetMonitorInfoW(handle, ctypes.byref(info)):
            dpi_x, dpi_y = wintypes.UINT(96), wintypes.UINT(96)
            SHCORE.GetDpiForMonitor(handle, 0, ctypes.byref(dpi_x), ctypes.byref(dpi_y))
            m, w = info.rcMonitor, info.rcWork
            found.append(Monitor(info.szDevice, names.get(info.szDevice, ""), (m.left, m.top, m.right, m.bottom),
                                 (w.left, w.top, w.right, w.bottom), bool(info.dwFlags & 1),
                                 info.szDevice in virtual_devices, int(dpi_x.value)))
        return 1

    USER32.EnumDisplayMonitors(None, None, callback, 0)
    return sorted(found, key=lambda m: (not m.primary, m.virtual, m.rect))


def virtual_targets(hardware_ids=HARDWARE_IDS) -> list[display_config.Target]:
    return display_config.targets(tuple(hardware_ids))


def extended(found: list[display_config.Target]) -> set[str]:
    """GDI names of the virtual monitors that have their own desktop area."""
    return {t.device for t in found if t.active and not t.cloned and t.device}


def screens(hardware_ids=HARDWARE_IDS) -> list[Monitor]:
    return monitors(extended(virtual_targets(hardware_ids)))


def monitor_at(screens: list[Monitor], x: float, y: float) -> Monitor | None:
    hit = next((m for m in screens if m.contains(x, y)), None)
    if hit is not None:
        return hit
    # Off-screen points belong to the nearest monitor, as MonitorFromPoint(NEAREST) does.
    def distance(m):
        dx = max(m.rect[0] - x, 0, x - m.rect[2] + 1)
        dy = max(m.rect[1] - y, 0, y - m.rect[3] + 1)
        return dx * dx + dy * dy
    return min(screens, key=distance, default=None)


def shares_edge(a: Monitor, b: Monitor) -> bool:
    """True when the cursor can cross from one monitor to the other (a common edge, not just a corner)."""
    (al, at, ar, ab), (bl, bt, br, bb) = a.rect, b.rect
    vertical = (ar == bl or br == al) and min(ab, bb) > max(at, bt)
    horizontal = (ab == bt or bb == at) and min(ar, br) > max(al, bl)
    return vertical or horizontal


def corner_position(screens: list[Monitor]) -> tuple[int, int]:
    """Bottom-right corner of the physical screens' bounding box.

    A virtual screen placed there touches them only at that point, so their outer edges keep
    stopping the cursor (the edge pull tab relies on it) and the user cannot wander onto it.
    """
    physical = [m for m in screens if not m.virtual] or screens
    return max(m.rect[2] for m in physical), max(m.rect[3] for m in physical)


def scale_allowed(size: tuple[int, int], percent: int) -> bool:
    """Windows offers a display scale only while the scaled desktop stays at least 800x600."""
    return size[0] * 100 >= 800 * percent and size[1] * 100 >= 600 * percent


def choose_mode(modes: list[tuple[int, int]], fits, percent: int | None) -> tuple[int, int]:
    """Smallest mode that holds the palettes (`fits(size)`) and allows the main screen's scale.

    Failing that, the scale wins over room (the largest mode with that scale; palettes that do not
    fit cascade), and without a usable scale the smallest mode that fits, or the largest mode.
    """
    by_area = sorted(modes, key=lambda size: (size[0] * size[1], size))
    scaled = [size for size in by_area if not percent or scale_allowed(size, percent)]
    roomy = [size for size in by_area if fits(size)]
    both = [size for size in roomy if size in scaled]
    if both:
        return both[0]
    if scaled:
        return scaled[-1]
    return roomy[0] if roomy else by_area[-1]


class VirtualDisplayMissing(RuntimeError):
    pass


def attached(hardware_ids=HARDWARE_IDS) -> Monitor | None:
    """The virtual screen on the desktop (with its own area, not a clone), if any."""
    return next((m for m in screens(hardware_ids) if m.virtual), None)


def provide(fits, hardware_ids=HARDWARE_IDS, match_scale=True, timeout=5.0) -> tuple[Monitor, list[str]]:
    """Make sure one virtual screen is on the desktop, large enough for `fits((width, height))`.

    It is attached on demand (from detached or cloned), takes the smallest driver mode that holds
    the palettes, sits at the physical screens' bottom-right corner and gets the main screen's
    scale, so CSP does not re-layout palettes moved onto it. Further virtual monitors are detached:
    two attached at once stalled desktop composition for ~1 s every second on the development
    machine. Returns the screen and what was done.
    """
    found = virtual_targets(hardware_ids)
    if not found:
        raise VirtualDisplayMissing("没有找到虚拟显示器：驱动未安装或未启动（以管理员身份运行 vdd\\install-vdd.ps1）")
    steps = []
    own = next((t for t in found if t.active and not t.cloned), found[0])
    extra = [t for t in found if t.active and t is not own]
    if extra:
        display_config.detach(extra)
    if not own.active or own.cloned:
        display_config.attach([own])
        deadline = time.monotonic() + timeout
        while not extended(virtual_targets(hardware_ids)):
            if time.monotonic() >= deadline:
                raise TimeoutError(f"虚拟显示器在 {timeout:.0f} 秒内没有接入桌面")
            time.sleep(0.05)
        steps.append("接入虚拟屏" + ("（原为复制主屏）" if own.cloned else ""))
    current = screens(hardware_ids)
    screen = next(m for m in current if m.virtual)
    main = next((m for m in current if m.primary), None)
    percent = None
    if match_scale and main:
        state = display_config.scale_percent(main.device)
        percent = state[0] if state else None
    size = choose_mode(display_config.modes(screen.device), fits, percent)
    position = corner_position(current)
    if (screen.width, screen.height) != size or screen.rect[:2] != position:
        display_config.set_mode(screen.device, *size, position)
        steps.append(f"{size[0]}×{size[1]}")
    if percent:
        state = display_config.scale_percent(screen.device)
        if state and state[0] != percent:
            if display_config.set_scale_percent(screen.device, percent):
                steps.append(f"缩放 {percent}%")
            else:
                steps.append(f"不支持 {percent}% 缩放，保持 {state[0]}%")
    # A scale change can make Windows re-arrange the layout; put the screen back on the corner.
    screen = next(m for m in screens(hardware_ids) if m.virtual)
    if screen.rect[:2] != position:
        display_config.move_sources({screen.device: position})
        screen = next(m for m in screens(hardware_ids) if m.virtual)
    return screen, steps


def release(hardware_ids=HARDWARE_IDS) -> bool:
    """Detach the virtual monitors (extended or cloned) from the desktop. Returns whether any was attached."""
    active = [t for t in virtual_targets(hardware_ids) if t.active]
    if active:
        display_config.detach(active)
    return bool(active)
