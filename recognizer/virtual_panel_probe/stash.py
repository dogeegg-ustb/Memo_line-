"""Move floating CSP palettes whose corner is dragged onto a screen corner to the virtual screen.

CSP docks a palette dropped on a screen's left/right edge, so the gesture is corner to corner:
a top-left / bottom-left corner lists the palette in the left drawer, top-right / bottom-right in
the right one. All stashed palettes share one virtual screen, which is only as large as they need.
"""
from __future__ import annotations

import json
import time
from dataclasses import asdict, dataclass
from pathlib import Path

from csp_windows import CspWindow, describe, is_window, move_window
from virtual_display import Monitor, monitor_at

MAX_RELAYOUTS = 5
SIDES = ("left", "right")


@dataclass
class StashRecord:
    hwnd: int
    pid: int
    origin: tuple[int, int]  # visible-frame top-left before the drag
    origin_device: str
    side: str                # left / right: the drawer the palette is listed in
    stashed_at: float
    corner: str = ""
    slot: tuple[int, int] | None = None
    relayouts: int = 0


def corner_of(bounds, monitor: Monitor, threshold: int) -> str | None:
    """Screen corner the window's corner sits on (or beyond), e.g. "top-right"."""
    work = monitor.work
    horizontal = ("left" if bounds[0] <= work[0] + threshold
                  else "right" if bounds[2] >= work[2] - threshold else None)
    vertical = ("top" if bounds[1] <= work[1] + threshold
                else "bottom" if bounds[3] >= work[3] - threshold else None)
    return f"{vertical}-{horizontal}" if horizontal and vertical else None


def stash_area(screens: list[Monitor]) -> tuple[int, int, int, int] | None:
    virtual = next((m for m in screens if m.virtual), None)
    return virtual.rect if virtual else None


def pack(sizes, area, gap) -> list[tuple[int, int]]:
    """Column shelves from the area's top-left; windows keep their size. Overflow cascades from the start."""
    x, y, column, result, offset = area[0] + gap, area[1] + gap, 0, [], 0
    for width, height in sizes:
        top = area[1] + gap + offset
        if y > top and y + height > area[3] - gap:
            x, y, column = x + column + gap, top, 0
            if x + width > area[2] - gap:
                offset += 24
                x, y = area[0] + gap + offset, area[1] + gap + offset
        result.append((x, y))
        y += height + gap
        column = max(column, width)
    return result


def fits(sizes, width: int, height: int, gap: int) -> bool:
    """Whether `pack` places every window inside a width x height screen without cascading."""
    x, y, column = gap, gap, 0
    for w, h in sizes:
        if y > gap and y + h > height - gap:
            x, y, column = x + column + gap, gap, 0
        if x + w > width - gap or y + h > height - gap:
            return False
        y += h + gap
        column = max(column, w)
    return True


def physical_screen(screens: list[Monitor]) -> Monitor:
    physical = [m for m in screens if not m.virtual] or screens
    return next((m for m in physical if m.primary), physical[0])


def is_hidden(window: CspWindow, screens: list[Monitor]) -> bool:
    monitor = monitor_at(screens, *window.center)
    return bool(monitor and monitor.virtual)


def size_on(window: CspWindow, screens, target: Monitor) -> tuple[int, int]:
    """CSP is per-monitor DPI aware: it rescales a palette when it lands on another monitor."""
    current = monitor_at(screens, *window.center)
    ratio = target.dpi / current.dpi if current else 1.0
    return round(window.size[0] * ratio), round(window.size[1] * ratio)


class StashManager:
    def __init__(self, state_path: Path, corner: int, gap: int, log):
        self.state_path, self.corner, self.gap, self.log = state_path, corner, gap, log
        self.records: dict[int, StashRecord] = {}
        self.press_bounds: dict[int, tuple] | None = None
        self.press_id = 0

    def begin_press(self, windows) -> None:
        self.press_bounds = {w.hwnd: w.bounds for w in windows}
        self.press_id += 1

    def end_press(self) -> None:
        self.press_bounds = None

    def dropped(self, windows, screens) -> list[tuple[CspWindow, str, tuple | None]]:
        """Floating windows moved (or torn off) since the press whose corner sits on a screen corner.

        Returns (window, corner, bounds at the press); corner is "virtual" for a window dragged
        straight onto a virtual screen. Called several times after a release because CSP creates a
        torn-off palette's window only some hundred milliseconds after the button goes up.
        """
        before = self.press_bounds
        if before is None:
            return []
        dropped = []
        for window in windows:
            prior = before.get(window.hwnd)
            if window.hwnd in self.records or prior == window.bounds:
                continue
            monitor = monitor_at(screens, *window.center)
            if monitor is None:
                continue
            corner = "virtual" if monitor.virtual else corner_of(window.bounds, monitor, self.corner)
            if corner:
                dropped.append((window, corner, prior))
        return dropped

    def side_for(self, window: CspWindow, screens, corner: str | None) -> str:
        if corner and corner.endswith(SIDES):
            return corner.rsplit("-", 1)[1]
        anchor = physical_screen(screens).rect
        return "left" if window.center[0] < (anchor[0] + anchor[2]) / 2 else "right"

    def sizes(self, windows: dict[int, CspWindow], extra=()) -> list[tuple[int, int]]:
        """Sizes in layout order: the stashed palettes, then `extra` windows about to be stashed."""
        records = sorted((r for r in self.records.values() if r.hwnd in windows), key=lambda r: r.stashed_at)
        return [windows[r.hwnd].size for r in records] + [w.size for w in extra]

    def stash(self, window: CspWindow, screens, windows: dict[int, CspWindow], prior=None, corner="",
              side: str | None = None) -> bool:
        if stash_area(screens) is None:
            self.log("没有可用的虚拟显示器，未收纳", hwnd=window.hwnd)
            return False
        origin = prior or window.bounds
        monitor = monitor_at(screens, (origin[0] + origin[2]) / 2, (origin[1] + origin[3]) / 2)
        if monitor is None or monitor.virtual:
            monitor = physical_screen(screens)
        self.records[window.hwnd] = StashRecord(window.hwnd, window.pid, (origin[0], origin[1]), monitor.device,
                                                side or self.side_for(window, screens, corner), time.time(), corner)
        self.layout(windows, screens)
        self.save()
        return True

    def layout(self, windows: dict[int, CspWindow], screens) -> None:
        area = stash_area(screens)
        if area is None:
            return
        records = sorted((r for r in self.records.values() if r.hwnd in windows), key=lambda r: r.stashed_at)
        for record, slot in zip(records, pack([windows[r.hwnd].size for r in records], area, self.gap)):
            window = windows[record.hwnd]
            if slot != record.slot:
                record.slot, record.relayouts = slot, 0
            if max(abs(window.bounds[0] - slot[0]), abs(window.bounds[1] - slot[1])) > 2:
                if record.relayouts >= MAX_RELAYOUTS:
                    continue  # CSP keeps putting it elsewhere; do not fight it
                record.relayouts += 1
                move_window(window, *slot)

    def maintain(self, windows: dict[int, CspWindow], screens) -> list[int]:
        """Re-pack after CSP rescales windows for the virtual screen DPI; forget closed windows."""
        closed = [h for h in self.records if h not in windows and not is_window(h)]
        for hwnd in closed:
            del self.records[hwnd]
        if closed:
            self.save()
        self.layout(windows, screens)
        return closed

    def on_side(self, side: str, windows: dict[int, CspWindow]) -> list[StashRecord]:
        """The side's stashed palettes, most recently stashed first."""
        return sorted((r for r in self.records.values() if r.side == side and r.hwnd in windows),
                      key=lambda r: r.stashed_at, reverse=True)

    def restore(self, hwnd: int, screens, window: CspWindow | None = None) -> bool:
        record = self.records.pop(hwnd, None)
        window = window or (describe(hwnd) if is_window(hwnd) else None)
        self.save()
        if record is None or window is None:
            return False
        monitor = next((m for m in screens if m.device == record.origin_device and not m.virtual), None)
        monitor = monitor or physical_screen(screens)
        width, height = size_on(window, screens, monitor)
        work = monitor.work
        x = min(max(record.origin[0], work[0]), max(work[0], work[2] - width))
        y = min(max(record.origin[1], work[1]), max(work[1], work[3] - height))
        move_window(window, x, y)
        return True

    def bring_here(self, hwnd: int, screens, window: CspWindow, monitor: Monitor, side: str, y: int,
                   margin: int = 0) -> bool:
        """Drawer pick: put the palette against `side` of `monitor`, centred on `y`.

        It is kept clear of the corner zone so the next small drag does not stash it again.
        """
        if self.records.pop(hwnd, None) is None:
            return False
        self.save()
        width, height = size_on(window, screens, monitor)
        work, inset = monitor.work, self.corner + margin
        x = work[0] + inset if side == "left" else work[2] - width - inset
        x = min(max(x, work[0]), max(work[0], work[2] - width))
        top = min(max(y - height // 2, work[1] + inset), work[3] - height - inset)
        move_window(window, x, max(top, work[1]))
        return True

    def restore_all(self, screens, windows: dict[int, CspWindow]) -> int:
        count = 0
        for hwnd in list(self.records):
            count += self.restore(hwnd, screens, windows.get(hwnd))
        return count

    def save(self) -> None:
        data = dict(savedAt=time.time(), records=[asdict(r) for r in self.records.values()])
        self.state_path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")

    def load(self) -> list[StashRecord]:
        try:
            data = json.loads(self.state_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return []
        return [StashRecord(**{**r, "origin": tuple(r["origin"]), "slot": None}) for r in data.get("records", [])]


def bring_back(window: CspWindow, screens, index: int = 0) -> None:
    """Return a window with no stash record (e.g. moved by hand) to the main physical screen."""
    work = physical_screen(screens).work
    offset = 60 + 40 * index
    move_window(window, min(work[0] + offset, work[2] - window.size[0]), min(work[1] + offset, work[3] - window.size[1]))
