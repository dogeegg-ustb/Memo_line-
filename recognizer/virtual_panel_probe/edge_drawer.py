"""iPadOS-style edge pull tab for the palettes stashed on the virtual screen.

Resting the cursor on the main screen's left or right edge shows a small tab there when that side
has stashed palettes. Clicking the tab slides in a drawer with the latest capture of each palette;
picking one brings that palette back to this edge.

Both popups are created once, offscreen, with WS_EX_NOACTIVATE, and afterwards are only moved:
showing them never takes keyboard focus away from CSP.
"""
from __future__ import annotations

import ctypes
import tkinter as tk
from ctypes import wintypes

from PIL import ImageTk

USER32 = ctypes.WinDLL("user32", use_last_error=True)
USER32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
USER32.SetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_long]
USER32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
USER32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                                ctypes.c_int, ctypes.c_int, wintypes.UINT]
GWL_EXSTYLE, WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE = -20, 0x80, 0x08000000
HWND_TOPMOST, SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = -1, 0x1, 0x2, 0x10
OFFSCREEN = -32000
KEY = "#010203"  # colour key: the tab's rounded-off corners are transparent and click-through
COLORS = dict(tab="#4a4e55", tabHover="#5b8def", glyph="#f2f3f5", panel="#1f2124", border="#3a3d42",
              card="#2b2e33", cardHover="#3a3f47", text="#e8eaed", muted="#9aa0a6", placeholder="#24262a")
FONT = "Microsoft YaHei UI"
TITLES = {"left": "左侧收纳", "right": "右侧收纳"}
AWAY_SECONDS = 0.7
SLIDE_FRAMES = 8


def edge_hit(physical, x: int, y: int, hover: int):
    """(side, monitor) when the cursor rests on an outer left/right edge of a physical screen."""
    for monitor in physical:
        if not monitor.contains(x, y):
            continue
        left, _, right, _ = monitor.rect
        if x < left + hover and not any(m.contains(left - 1, y) for m in physical):
            return "left", monitor
        if x >= right - hover and not any(m.contains(right, y) for m in physical):
            return "right", monitor
    return None


def cursor() -> tuple[int, int]:
    point = wintypes.POINT()
    USER32.GetCursorPos(ctypes.byref(point))
    return point.x, point.y


def rounded(canvas: tk.Canvas, x0, y0, x1, y1, r, **options):
    points = (x0 + r, y0, x1 - r, y0, x1, y0, x1, y0 + r, x1, y1 - r, x1, y1,
              x1 - r, y1, x0 + r, y1, x0, y1, x0, y1 - r, x0, y0 + r, x0, y0)
    return canvas.create_polygon(points, smooth=True, **options)


class EdgeDrawer:
    """`count(side)` -> stashed palettes on that side; `items(side)` -> [dict(hwnd, name, image)];
    `bring(hwnd, side, monitor, y)` moves one back; `enabled()` gates the whole feature."""

    def __init__(self, root: tk.Tk, px, physical, count, items, bring, enabled, hover_px=2, dwell=0.15):
        self.root, self.px = root, px
        self.physical, self.count, self.items, self.bring, self.enabled = physical, count, items, bring, enabled
        self.hover, self.dwell = hover_px, dwell
        self.tab_size = (px(18), px(76))
        self.tab = self.popup(KEY, transparent=True)
        self.tab_canvas = tk.Canvas(self.tab, width=self.tab_size[0], height=self.tab_size[1], bg=KEY,
                                    highlightthickness=0, cursor="hand2")
        self.tab_canvas.pack()
        self.tab_canvas.bind("<Enter>", lambda _e: self.paint_tab(True))
        self.tab_canvas.bind("<Leave>", lambda _e: self.paint_tab(False))
        self.tab_canvas.bind("<ButtonRelease-1>", lambda _e: self.open())
        self.drawer = self.popup(COLORS["panel"])
        self.drawer.configure(highlightthickness=1, highlightbackground=COLORS["border"])
        self.drawer.bind("<Leave>", lambda e: self.highlight(None) if e.widget is self.drawer else None)
        self.tab_side = self.tab_monitor = None
        self.tab_rect = self.drawer_rect = None
        self.tab_y = self.anchor = 0
        self.drawer_side = self.drawer_monitor = None
        self.edge_side, self.edge_since, self.away_since = None, None, None
        self.next_poll = self.next_refresh = 0.0
        self.cards: dict[int, dict] = {}
        self.order: list[int] = []
        self.hot: int | None = None
        self.slide_job = None

    # ---------- popups ----------
    def popup(self, background: str, transparent=False) -> tk.Toplevel:
        window = tk.Toplevel(self.root, bg=background)
        window.overrideredirect(True)
        window.attributes("-topmost", True)
        if transparent:
            window.attributes("-transparentcolor", KEY)
        window.geometry(f"1x1+{OFFSCREEN}+{OFFSCREEN}")
        window.update_idletasks()
        hwnd = int(window.wm_frame(), 16)
        style = USER32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        USER32.SetWindowLongW(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
        return window

    @staticmethod
    def place(window: tk.Toplevel, width: int, height: int, x: int, y: int) -> None:
        window.geometry(f"{width}x{height}+{x}+{y}")
        # Apply the move now: Tk applies geometry when idle, and the z-order change below would make
        # it take the window's current (offscreen) position as the user's choice.
        window.update_idletasks()
        # Keep it above CSP's own topmost windows without activating it.
        USER32.SetWindowPos(int(window.wm_frame(), 16), HWND_TOPMOST, 0, 0, 0, 0,
                            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE)

    @property
    def tab_shown(self) -> bool:
        return self.tab_rect is not None

    @property
    def drawer_open(self) -> bool:
        return self.drawer_rect is not None

    # ---------- tab ----------
    def paint_tab(self, hover=False) -> None:
        canvas, (width, height) = self.tab_canvas, self.tab_size
        canvas.delete("all")
        radius, fill = width - self.px(3), COLORS["tabHover" if hover else "tab"]
        if self.tab_side == "right":  # flat against the screen edge, rounded on the inner side
            rounded(canvas, 0, 0, width + radius, height, radius, fill=fill, outline="")
        else:
            rounded(canvas, -radius, 0, width, height, radius, fill=fill, outline="")
        cx, cy, size = width / 2, height / 2 - self.px(8), self.px(4)
        tip = -size / 2 if self.tab_side == "right" else size / 2  # the chevron points into the screen
        canvas.create_line(cx - tip, cy - size, cx + tip, cy, cx - tip, cy + size, fill=COLORS["glyph"],
                           width=max(2, self.px(2)), capstyle="round", joinstyle="round")
        canvas.create_text(cx, cy + self.px(20), text=str(self.count(self.tab_side)), fill=COLORS["glyph"],
                           font=(FONT, 8))

    def show_tab(self, side: str, monitor, y: int) -> None:
        self.tab_side, self.tab_monitor, self.away_since = side, monitor, None
        self.paint_tab()
        self.move_tab(y)

    def move_tab(self, y: int) -> None:
        width, height = self.tab_size
        monitor = self.tab_monitor
        x = monitor.rect[2] - width if self.tab_side == "right" else monitor.rect[0]
        top = min(max(y - height // 2, monitor.work[1]), monitor.work[3] - height)
        if self.tab_rect != (x, top, x + width, top + height):
            self.place(self.tab, width, height, x, top)
            self.tab_rect, self.tab_y = (x, top, x + width, top + height), top + height // 2

    def hide_tab(self) -> None:
        if self.tab_shown:
            self.tab.geometry(f"+{OFFSCREEN}+{OFFSCREEN}")
        self.tab_rect, self.edge_since, self.away_since = None, None, None

    def near_tab(self, x: int, y: int) -> bool:
        left, top, right, bottom = self.tab_rect
        reach = self.px(28)
        return left - reach <= x < right + reach and top - reach // 2 <= y < bottom + reach // 2

    # ---------- per-frame ----------
    def tick(self, now: float, buttons_down: bool) -> None:
        if self.drawer_open:
            if not self.enabled():
                self.close()
            elif now >= self.next_refresh:
                self.next_refresh = now + 1.0
                self.refresh_cards()
            return
        if now < self.next_poll:
            return
        self.next_poll = now + 0.03
        if not self.enabled():
            self.hide_tab()
            return
        x, y = cursor()
        hit = None if buttons_down else edge_hit(self.physical(), x, y, self.hover)
        if self.tab_shown:
            if not self.count(self.tab_side):
                self.hide_tab()
            elif self.near_tab(x, y) and not buttons_down:
                self.away_since = None
            elif hit and hit[0] == self.tab_side:
                self.away_since = None
                self.move_tab(y)  # slide along the edge with the cursor
            else:
                self.away_since = self.away_since or now
                if now - self.away_since > AWAY_SECONDS:
                    self.hide_tab()
            return
        if hit is None or not self.count(hit[0]):
            self.edge_since = None
            return
        if self.edge_since is None or self.edge_side != hit[0]:
            self.edge_side, self.edge_since = hit[0], now
        elif now - self.edge_since >= self.dwell:
            self.show_tab(hit[0], hit[1], y)

    def on_press(self, x: int, y: int) -> None:
        """Any mouse press anywhere (from the global hook): a press outside the open drawer closes it."""
        if self.drawer_open:
            left, top, right, bottom = self.drawer_rect
            if not (left <= x < right and top <= y < bottom):
                self.close()

    # ---------- drawer ----------
    def open(self) -> None:
        if not self.tab_shown:
            return
        side, monitor, anchor = self.tab_side, self.tab_monitor, self.tab_y
        self.hide_tab()
        items = self.items(side)
        if not items:
            return
        self.drawer_side, self.drawer_monitor, self.anchor = side, monitor, anchor
        width, height, x, top = self.build(items)
        start = monitor.rect[2] if side == "right" else monitor.rect[0] - width
        self.drawer_rect = (x, top, x + width, top + height)
        self.next_refresh = 0.0
        self.slide(start, x, width, height, top, 1)

    def slide(self, start: int, end: int, width: int, height: int, top: int, frame: int) -> None:
        t = frame / SLIDE_FRAMES
        x = round(start + (end - start) * (1 - (1 - t) ** 3))
        self.place(self.drawer, width, height, x, top)
        self.slide_job = self.root.after(15, self.slide, start, end, width, height, top, frame + 1) \
            if frame < SLIDE_FRAMES else None

    def build(self, items) -> tuple[int, int, int, int]:
        """Fill the drawer with one card per palette; returns its final width, height, x and top."""
        for child in self.drawer.winfo_children():
            child.destroy()
        self.cards, self.order, self.hot = {}, [item["hwnd"] for item in items], None
        px, monitor, side = self.px, self.drawer_monitor, self.drawer_side
        width, pad = px(244), px(10)
        work = monitor.work
        available = work[3] - work[1] - 2 * pad
        header = tk.Frame(self.drawer, bg=COLORS["panel"])
        header.pack(fill="x", padx=pad, pady=(pad, px(4)))
        tk.Label(header, text=f"{TITLES[side]} · {len(items)}", bg=COLORS["panel"], fg=COLORS["text"],
                 font=(FONT, 10, "bold")).pack(side="left")
        close = tk.Label(header, text="✕", bg=COLORS["panel"], fg=COLORS["muted"], font=(FONT, 10), cursor="hand2")
        close.pack(side="right")
        close.bind("<ButtonRelease-1>", lambda _e: self.close())
        tk.Label(self.drawer, text="点选面板，把它带回这一侧", bg=COLORS["panel"], fg=COLORS["muted"],
                 font=(FONT, 8), anchor="w").pack(fill="x", padx=pad, pady=(0, px(6)))
        chrome = px(64) + len(items) * (px(30) + pad)
        thumb_w = width - 4 * pad
        thumb_h = max(px(48), min(px(220), (available - chrome) // max(1, len(items))))
        for item in items:
            card = tk.Frame(self.drawer, bg=COLORS["card"], padx=pad // 2, pady=pad // 2, cursor="hand2")
            card.pack(fill="x", padx=pad, pady=(0, pad))
            picture = tk.Label(card, bg=COLORS["placeholder"], fg=COLORS["muted"], font=(FONT, 8))
            picture.pack(fill="x")
            name = tk.Label(card, text=item["name"], bg=COLORS["card"], fg=COLORS["text"], font=(FONT, 9),
                            anchor="w")
            name.pack(fill="x", pady=(px(4), 0))
            entry = dict(card=card, picture=picture, name=name, image=None, photo=None, size=(thumb_w, thumb_h))
            self.cards[item["hwnd"]] = entry
            self.set_picture(entry, item["image"])
            for widget in (card, picture, name):
                widget.bind("<Enter>", lambda _e, h=item["hwnd"]: self.highlight(h))
                widget.bind("<ButtonRelease-1>", lambda _e, h=item["hwnd"]: self.pick(h))
        self.drawer.update_idletasks()
        height = min(self.drawer.winfo_reqheight(), available)
        top = min(max(self.anchor - height // 2, work[1] + pad), work[3] - height - pad)
        x = monitor.rect[2] - width - pad if side == "right" else monitor.rect[0] + pad
        return width, height, x, top

    def set_picture(self, entry: dict, image) -> None:
        if image is entry["image"] and entry["photo"] is not None:
            return
        entry["image"] = image
        if image is None:
            entry["photo"] = None
            entry["picture"].configure(image="", text="暂无截图", height=3)
            return
        thumb = image.copy()
        thumb.thumbnail(entry["size"])
        entry["photo"] = ImageTk.PhotoImage(thumb, master=self.drawer)
        entry["picture"].configure(image=entry["photo"], text="", height=0)

    def refresh_cards(self) -> None:
        """Keep the thumbnails live while the drawer is open; rebuild if palettes came or went."""
        items = self.items(self.drawer_side)
        if not items:
            self.close()
            return
        if [item["hwnd"] for item in items] != self.order:
            width, height, x, top = self.build(items)
            self.drawer_rect = (x, top, x + width, top + height)
            self.place(self.drawer, width, height, x, top)
            return
        for item in items:
            self.set_picture(self.cards[item["hwnd"]], item["image"])

    def highlight(self, hwnd: int | None) -> None:
        if hwnd == self.hot:
            return
        for key, color in ((self.hot, COLORS["card"]), (hwnd, COLORS["cardHover"])):
            entry = self.cards.get(key)
            if entry:
                entry["card"].configure(bg=color)
                entry["name"].configure(bg=color)
        self.hot = hwnd

    def pick(self, hwnd: int) -> None:
        entry, side, monitor = self.cards.get(hwnd), self.drawer_side, self.drawer_monitor
        if entry is None:
            return
        card = entry["card"]
        y = card.winfo_rooty() + card.winfo_height() // 2
        self.close()
        self.bring(hwnd, side, monitor, y)

    def close(self) -> None:
        if self.slide_job is not None:
            self.root.after_cancel(self.slide_job)
            self.slide_job = None
        self.drawer.geometry(f"+{OFFSCREEN}+{OFFSCREEN}")
        for child in self.drawer.winfo_children():
            child.destroy()
        self.cards, self.order, self.hot, self.drawer_rect = {}, [], None, None
