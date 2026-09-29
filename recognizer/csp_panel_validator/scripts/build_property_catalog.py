"""Build a factual UI-label catalog from cached official manuals, not an internal CSP DB.

Only short control names, types, provenance and implementation capabilities are shipped.
Descriptions/HTML stay in .tmp; CSP internal identifiers and undocumented ranges stay null.
"""
import copy
import hashlib
import html
import json
import re
import sqlite3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / ".tmp" / "docs"
DATA = ROOT / "csp_panel_validator" / "data"
GROUPS = {
    "Brush size": "筆刷尺寸", "Ink": "墨水", "Color Jitter": "顏色變化",
    "Anti-aliasing": "消除鋸齒", "Brush shape": "筆刷形狀", "Brush tip": "筆刷前端",
    "Spraying effect": "散佈效果", "Stroke": "筆劃", "Texture": "紙質",
    "2 - Brush shape": "2-筆刷形狀", "Watercolor edge": "水彩邊界", "Erase": "刪除",
    "Correction": "修正", "Starting and ending": "起筆收筆", "Anti-overflow": "防止溢出",
}
SUBGROUPS = {"Angle dynamics": "影響源的設定項目（方向）", "Dynamics settings (Direction of particle)": "影響源的設定項目（粒子的方向）"}


def plain(body):
    return re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", "", body))).strip()


def slug(text):
    return re.sub(r"[^a-z0-9]+", "_", text.lower()).strip("_")


def simplified(text):
    # Build-time conversion only. Use Windows' installed language mapping, no runtime dependency.
    import ctypes
    from ctypes import wintypes
    fn = ctypes.windll.kernel32.LCMapStringEx
    fn.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.LPCWSTR, ctypes.c_int,
                   wintypes.LPWSTR, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t]
    fn.restype = ctypes.c_int
    out = ctypes.create_unicode_buffer(max(2, len(text) * 3 + 1))
    if not fn("zh-CN", 0x02000000, text, len(text), out, len(out), None, None, 0):
        raise RuntimeError("Windows simplified Chinese mapping failed")
    return out.value


def sections(manifest):
    result = {}
    for source in manifest:
        if "/810_subtools/" not in source["url"]:
            continue
        document = (CACHE / source["file"]).read_text(encoding="utf-8")
        headings = list(re.finditer(r"<(h[346])\b[^>]*>(.*?)</\1>", document, re.S))
        category = sub = None
        for index, match in enumerate(headings):
            tag, body = match.group(1), match.group(2)
            label = plain(body)
            if tag == "h3":
                category, sub = label, None
            elif tag == "h4":
                sub = label
            elif category:
                label = re.sub(r"^(?:\(\d+\)|[①-⑳])\s*", "", label)
                end = headings[index + 1].start() if index + 1 < len(headings) else len(document)
                desc = plain(document[match.end():end])
                result.setdefault((category, sub), []).append({"label": label, "description": desc, "source": source["url"], "sha256": source["sha256"]})
    return result


NUMBERS = {"Brush size", "Opacity", "Amount of paint", "Density of paint", "Color stretch", "Thickness", "Angle", "Brush density", "Particle size", "Particle density", "Spray deviation", "Direction of particle", "Texture density", "Scale ratio", "Rotation angle", "Brightness", "Contrast", "Watercolor edge", "Darkness", "Blurring width", "Stabilization", "Post correction", "Taper", "Color margin", "Starting", "Ending", "Disarray (for focus lines/speed lines only)"}
ENUMS = {"Anti-aliasing", "Blending mode", "Mixing mode", "Blending quality", "Tip shape", "Direction", "Flip horizontal", "Flip vertical", "Texture mode", "Repeat method", "Scaling mode", "Change target", "Mode", "Stabilization mode", "How to specify", "Speed and Quality"}
LABEL_ONLY = {"Brush shape preview", "Brush preview", "Brush tip icon", "Brush shape", "Brush shape preset", "Texture", "Starting and ending"}
CHECKS = {"Specify by size on screen", "At least 1 pixel", "Adjust brush density by gap", "Spraying effect", "Continuous spraying", "Correct velocity input", "Ribbon", "Blend brush tips with Darken", "Invert texture", "Emphasize density", "Apply by each plot", "Dual brush", "Apply RGB value", "Link to main brush size", "Process after brush stroke", "Refer all layers", "Erase on all layers", "Sharp angles", "Adjust by speed", "Adjust by scale", "Bezier curve", "Enable snapping", "Snap to default border", "Starting and ending by speed", "Do not cross lines of reference layer", "Fill up to vector path"}
LEGACY = {"brush_size.brush_size": "brush_size", "ink.opacity": "opacity", "correction.stabilization": "stabilization", "anti_aliasing.anti_aliasing": "antialiasing", "correction.adjust_by_speed": "adjust_stabilization_by_speed"}
EXTRA_ALIASES = {"brush_size": ["画笔大小"], "stabilization": ["手颤修正", "手顫修正"], "adjust_stabilization_by_speed": ["根据速度调整手颤", "根據速度調整手顫"], "ink.color_mixing": ["底色混合", "底色混色"], "ink.amount_of_paint": ["颜料量"], "ink.density_of_paint": ["颜料浓度"], "brush_tip.brush_density": ["笔刷浓度"], "correction.vector_magnet": ["矢量吸附"], "texture.texture": ["纹理", "材质"], "texture.texture_density": ["纹理浓度"]}


def make_record(category, zh_category, index, entry, zh_entry=None):
    label = entry["label"]
    identity = f"{slug(category)}.{slug(label)}"
    if category == "Correction" and index == 6:
        identity += "_post_correction"
    key = LEGACY.get(identity, identity)
    action = label.startswith(("Add ", "Delete ", "Apply brush", "Change name", "Preset of"))
    kind = "action" if action else "label" if label in LABEL_ONLY else "number" if label in NUMBERS else "enum" if label in ENUMS else "checkbox" if label in CHECKS else "compound"
    if category == "Spraying effect" and label == "Brush Size":
        kind = "checkbox"
    zh = zh_entry["label"] if zh_entry else None
    if zh:
        zh = re.sub(r"【.*?】", "", zh)
    aliases = [label] + ([zh, simplified(zh)] if zh else []) + EXTRA_ALIASES.get(key, [])
    if category.startswith("2 -"):
        aliases = ["2-" + alias for alias in aliases]
    sources = [{"url": entry["source"], "section": category, "item": label}]
    if zh_entry:
        sources.append({"url": zh_entry["source"], "section": zh_category, "item": zh_entry["label"]})
    return {"id": identity, "key": key, "category": category, "category_zh": zh_category,
            "label_en": label, "label_zh_tw": zh, "aliases": sorted(set(aliases)),
            "value_kind": kind, "read_support": "label_only" if kind in ("label", "action") else "visible_number_checkbox_or_text_enum",
            "internal_csp_id": None, "range": None, "enum_values": [], "sources": sources,
            "type_basis": "application classification from public UI documentation", "scope": "public_brush_settings"}


def main():
    manifest = json.loads((CACHE / "manifest.json").read_text(encoding="utf-8"))
    groups = sections(manifest)
    records = []
    coverage = []
    for category, zh_category in GROUPS.items():
        entries, translated = groups.get((category, None), []), groups.get((zh_category, None), [])
        if not entries:
            raise RuntimeError(f"Missing official category: {category}")
        # Traditional manual has an extra texture Delete control, absent in English.
        if category == "Texture":
            translated = [e for e in translated if e["label"] != "刪除"]
        for index, entry in enumerate(entries, 1):
            zh_entry = translated[index - 1] if index <= len(translated) else None
            records.append(make_record(category, zh_category, index, entry, zh_entry))
        coverage.append({"category": category, "documented_controls": len(entries), "imported_controls": len(entries)})
    # The official dual-brush pages explicitly refer to these four primary categories.
    for category in ("Brush tip", "Spraying effect", "Stroke", "Texture"):
        for primary in list(records):
            if primary["category"] != category:
                continue
            secondary = copy.deepcopy(primary)
            secondary.update(id="dual." + primary["id"], key="dual." + primary["key"], category="2 - " + category, category_zh="2-" + primary["category_zh"])
            secondary["aliases"] = ["2-" + name for name in primary["aliases"]]
            secondary["sources"].append({"url": "https://help.clip-studio.com/en-us/manual_en/810_subtools/Number.htm", "section": "2 - " + category, "item": "same settings as primary category"})
            records.append(secondary)
        coverage.append({"category": "2 - " + category, "documented_controls": sum(p["category"] == category for p in records), "imported_controls": sum(p["category"] == "2 - " + category for p in records)})
    # Dynamics subdialogs are cataloged separately; names require category context.
    for category, zh_category in (("Brush tip", "筆刷前端"), ("Spraying effect", "散佈效果")):
        sub = "Angle dynamics" if category == "Brush tip" else "Dynamics settings (Direction of particle)"
        en, zh = groups[(category, sub)], groups[(zh_category, SUBGROUPS[sub])]
        for index, entry in enumerate(en, 1):
            record = make_record(sub, SUBGROUPS[sub], index, entry, zh[index - 1] if index <= len(zh) else None)
            record["aliases"] = [sub + ":" + a for a in record["aliases"]]
            record["read_support"] = "label_only"
            records.append(record)
    enums = {
        "antialiasing": [["无", "無", "None"], ["弱", "Weak"], ["中", "Middle", "Medium"], ["强", "強", "Strong"]],
        "ink.mixing_mode": [["标准", "標準", "Standard"], ["感知", "知覺", "Perceptual"]],
        "brush_tip.tip_shape": [["圆形", "圓形", "Circle"], ["素材", "Material"]],
        "brush_tip.direction": [["水平", "Horizontal"], ["垂直", "Vertical"]],
    }
    for record in records:
        record["enum_values"] = enums.get(record["key"], enums.get("antialiasing", []) if record["label_en"] == "Anti-aliasing" else [])
    # Selection/fill extensions retain the previously validated tool's properties.
    for key, label, aliases, kind, source in [
        ("color_tolerance", "Tolerance", ["颜色容差", "顏色容差", "色差"], "number", "C"),
        ("close_gap", "Close gap", ["闭合间隙", "閉合間隙"], "compound", "C"),
        ("area_scaling", "Area scaling", ["扩缩选区", "擴縮選區"], "compound", "C"),
        ("creation_mode", "Selection mode", ["创建方式", "建立方法"], "enum", "S"),
    ]:
        entry = {"label": label, "source": f"https://help.clip-studio.com/en-us/manual_en/810_subtools/{source}.htm"}
        record = make_record("Selection extension", "选区扩展", 1, entry)
        record.update(key=key, aliases=[label] + aliases, value_kind=kind, scope="selection_extension")
        records.append(record)
    # These two existing UI labels have screenshot evidence, not a claim of SDK definitions.
    for key, label in (("contiguous_pixels", "取样连续的像素"), ("reference_layers", "多图层参照")):
        record = make_record("Selection extension", "选区扩展", 1, {"label": key, "source": "local:tests/fixtures/selection_panel.png"})
        record.update(key=key, aliases=[label], value_kind="checkbox", scope="observed_ui_extension")
        records.append(record)
    DATA.mkdir(parents=True, exist_ok=True)
    metadata = {"schema_version": 1, "catalog_version": "2026-09-23", "basis": "official public manual, not CSP internal database", "coverage": coverage, "sources": manifest, "property_count": len(records)}
    (DATA / "properties.json").write_text(json.dumps({"metadata": metadata, "properties": records}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    with sqlite3.connect(DATA / "properties.sqlite3") as db:
        db.executescript("DROP TABLE IF EXISTS properties; DROP TABLE IF EXISTS metadata; CREATE TABLE properties (id TEXT PRIMARY KEY, key TEXT, category TEXT, value_kind TEXT, read_support TEXT, definition_json TEXT); CREATE TABLE metadata (key TEXT PRIMARY KEY, value_json TEXT);")
        db.executemany("INSERT INTO properties VALUES (?,?,?,?,?,?)", [(r["id"], r["key"], r["category"], r["value_kind"], r["read_support"], json.dumps(r, ensure_ascii=False)) for r in records])
        db.executemany("INSERT INTO metadata VALUES (?,?)", [(k, json.dumps(v, ensure_ascii=False)) for k, v in metadata.items()])
    print(f"Built {len(records)} entries; {len(coverage)} documented brush categories")


if __name__ == "__main__":
    main()
