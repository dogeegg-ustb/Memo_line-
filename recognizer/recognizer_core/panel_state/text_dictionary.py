"""Text dictionary for the glyph reader, stored as rules instead of rendered images.

A rule is (word, font, size rule). Templates are rendered on demand with Windows GDI at the size the
rule gives for the capture's DPI, so one dictionary serves every display scale.

Sources of words:
  * data/text_words.json   CSP interface strings per UI language (build_text_dictionary.py, from the
                           installed CSP language resources);
  * brush packages         tool, tool-group and sub tool names from the user's EditImageTool.todb,
                           rescanned whenever the recorder loads its catalog (start and todb change)
                           and written to text_dictionary.brushes.json.
Fonts: CSP draws panel text with the Windows default GUI font, not a per-language font; the rule and
how it was verified are in data/text_fonts.json.
"""
from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path
import threading

DATA = Path(__file__).resolve().parent / "data"
FONTS_FILE = DATA / "text_fonts.json"
WORDS_FILE = DATA / "text_words.json"
BRUSHES_FILE_NAME = "text_dictionary.brushes.json"

# Windows UI language (primary language id) -> CSP resource language folder.
_LANGID = {0x04: None, 0x09: "english", 0x11: "japanese", 0x12: "korean", 0x0C: "french", 0x07: "german",
           0x0A: "spanish", 0x16: "portuguese", 0x21: "indonesian", 0x1E: "thai"}
_lock = threading.Lock()


def windows_ui_language() -> str:
    """CSP follows the Windows display language unless the user picked another in its preferences."""
    try:
        lid = ctypes.windll.kernel32.GetUserDefaultUILanguage()
    except (AttributeError, OSError):
        return "english"
    primary, sub = lid & 0x3FF, lid >> 10
    if primary == 0x04:  # Chinese: Taiwan / Hong Kong / Macau use traditional
        return "chinese_tc" if lid in (0x0404, 0x0C04, 0x1404) else "chinese_sc"
    return _LANGID.get(primary) or "english"


def ui_language(settings: dict | None = None) -> str:
    return os.environ.get("MEMOLINE_UI_LANGUAGE") or (settings or {}).get("uiLanguage") or windows_ui_language()


def system_gui_font() -> dict | None:
    """Windows DEFAULT_GUI_FONT: face name and |height| in px at 96 DPI (stock objects are not DPI scaled)."""
    class LOGFONTW(ctypes.Structure):
        _fields_ = [("height", ctypes.c_long), ("width", ctypes.c_long), ("esc", ctypes.c_long),
                    ("orient", ctypes.c_long), ("weight", ctypes.c_long)] + \
                   [(n, ctypes.c_byte) for n in ("italic", "underline", "strike", "charset", "outprec",
                                                 "clipprec", "quality", "pitch")] + [("face", ctypes.c_wchar * 32)]
    try:
        gdi = ctypes.windll.gdi32
        gdi.GetStockObject.restype = ctypes.c_void_p
        gdi.GetObjectW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
        lf = LOGFONTW()
        if not gdi.GetObjectW(gdi.GetStockObject(17), ctypes.sizeof(lf), ctypes.byref(lf)):
            return None
    except (AttributeError, OSError):
        return None
    return {"face": lf.face, "emPxAt96": abs(lf.height) or 12}


def font_rules(language: str) -> dict:
    """{"body": {face, emPxAt96}, "tab": {...}}; the face comes from this machine's GUI font (see text_fonts.json)."""
    rules = json.loads(FONTS_FILE.read_text(encoding="utf-8"))
    known = rules["knownSystemGuiFonts"]
    body = system_gui_font() or known.get(language) or known["default"]
    tab = dict(face=rules["tab"]["face"], emPxAt96=body["emPxAt96"] * rules["tab"]["emScale"])
    return {"body": body, "tab": tab}


def em_px(rule: dict, dpi: int) -> int:
    """Pixel em of a font rule at a capture DPI (rules state the size at 96 DPI)."""
    return max(6, round(rule["emPxAt96"] * dpi / 96))


def interface_words(language: str) -> list[str]:
    if not WORDS_FILE.exists():
        return []
    data = json.loads(WORDS_FILE.read_text(encoding="utf-8"))
    return data["languages"].get(language, [])


def brush_package_words(tool_catalog: dict) -> list[dict]:
    """Every named node of the tool tree: tools, tool groups (brush packages) and sub tools."""
    out, seen = [], set()
    for node in tool_catalog.get("nodes", []):
        name = (node.get("name") or "").strip()
        kind = node.get("kind")
        if not name or kind not in ("tool", "group", "subtool") or (name, kind) in seen:
            continue
        seen.add((name, kind))
        out.append({"word": name, "kind": kind, "id": node.get("id")})
    return out


def record_brush_packages(tool_catalog: dict, directory: Path) -> Path:
    """Write the brush-package words next to the recorder settings; called on every catalog load."""
    path = Path(directory) / BRUSHES_FILE_NAME
    body = {"schemaVersion": 1, "source": tool_catalog.get("sourceFile"),
            "words": brush_package_words(tool_catalog)}
    with _lock:
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(body, ensure_ascii=False, indent=1), encoding="utf-8")
        os.replace(tmp, path)
    return path


class TextDictionary:
    """Words with their font rule for one UI language: what a glyph reader renders and matches."""

    def __init__(self, language: str, directory: Path | None = None):
        self.language = language
        self.fonts = font_rules(language)
        self.words = {w: "interface" for w in interface_words(language)}
        self.directory = Path(directory) if directory else None
        self.reload_brushes()

    def reload_brushes(self) -> None:
        path = self.directory / BRUSHES_FILE_NAME if self.directory else None
        if path and path.exists():
            for entry in json.loads(path.read_text(encoding="utf-8")).get("words", []):
                self.words.setdefault(entry["word"], entry["kind"])

    def rules(self, dpi: int, role: str = "body") -> tuple[dict, int]:
        """(font rule, pixel em) for a text role: "body" (panel text) or "tab" (palette tab titles)."""
        rule = self.fonts[role] if role in self.fonts else self.fonts["body"]
        return rule, em_px(rule, dpi)
