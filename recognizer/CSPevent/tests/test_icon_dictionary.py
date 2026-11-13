"""Offline regression checks; no CSP installation, OCR model or network needed."""

from copy import deepcopy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import cv2
import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import build_icon_dictionary as builder
import manual_icon_meanings as manual


def evidence(name="縮小", **changes):
    return {"name": name, "score": 0.96, "number_method": "ocr", "number_score": 0.99,
            "match": {"sheet": "atlas.png", "slot": 2}, "mask_sha256": "mask-a", **changes}


class ManualMergeTests(unittest.TestCase):
    def setUp(self):
        self.icon = {"id": "icon_99999", "sheet": "atlas.png", "slot": 2,
                     "mask_sha256": "mask-a", "copies": [{"sheet": "copy.png", "slot": 7}]}

    def test_rebuild_id_changes_do_not_change_meaning(self):
        stats = builder.merge_manual_meanings([self.icon], {"icon_00001": [evidence()]})
        self.assertEqual(self.icon["name"], "縮小")
        self.assertEqual(stats["merged"], 1)
        self.assertFalse(self.icon["manual_review_required"])

    def test_duplicate_sheet_reference_resolves(self):
        entry = evidence(match={"sheet": "copy.png", "slot": 7})
        builder.merge_manual_meanings([self.icon], {"old-id": [entry]})
        self.assertEqual(self.icon["manual_meanings"], [entry])

    def test_id_only_and_changed_pixels_are_rejected(self):
        legacy = evidence()
        del legacy["match"]
        stats = builder.merge_manual_meanings([self.icon], {self.icon["id"]: [legacy, evidence(mask_sha256="different")]})
        self.assertEqual(stats["skipped"], 2)
        self.assertNotIn("name", self.icon)

    def test_conflicting_names_remain_candidates(self):
        builder.merge_manual_meanings([self.icon], {"old": [evidence(), evidence("放大")]})
        self.assertNotIn("name", self.icon)
        self.assertEqual(set(self.icon["manual_names"]), {"縮小", "放大"})
        self.assertTrue(self.icon["manual_review_required"])

    def test_verified_label_is_kept(self):
        self.icon.update(name="人工核对名称", name_source="label")
        builder.merge_manual_meanings([self.icon], {"old": [evidence()]})
        self.assertEqual(self.icon["name"], "人工核对名称")
        self.assertEqual(self.icon["name_source"], "label")

    def test_order_and_weak_glyph_reads_cannot_name(self):
        for entry in [evidence(number_method="reading_order"), evidence(number_method="glyph", number_margin=0.01),
                      evidence(score=0.85)]:
            icon = deepcopy(self.icon)
            builder.merge_manual_meanings([icon], {"old": [entry]})
            self.assertNotIn("name", icon)
            self.assertTrue(icon["manual_review_required"])

    def test_partial_run_preserves_other_pages(self):
        old = {"old": [{"url": "a", "image": "a.png", "score": 0.9},
                       {"url": "b", "image": "b.png", "score": 0.8}]}
        new = {"new": [{"url": "a", "image": "a.png", "score": 0.95}]}
        result = manual.merge_page_results(old, new, {("a", "a.png")})
        self.assertEqual(result["old"], [old["old"][1]])
        self.assertEqual(result["new"], new["new"])


class CalloutTests(unittest.TestCase):
    @staticmethod
    def connected_disc(radius, center=(45, 50)):
        img = np.full((110, 180, 3), 230, np.uint8)
        x, y = center
        cv2.circle(img, center, radius, (10, 10, 10), -1)
        cv2.line(img, (x + radius - 1, y), (179, y), (10, 10, 10), 2)
        cv2.putText(img, "2", (x - radius // 2, y + radius // 2), cv2.FONT_HERSHEY_SIMPLEX,
                    radius / 15, (255, 255, 255), 1, cv2.LINE_AA)
        return img

    def test_connected_discs_without_any_isolated_seed(self):
        for radius in (10, 15, 20):
            with self.subTest(radius=radius):
                discs = manual.callout_discs(self.connected_disc(radius))
                self.assertTrue(any(np.hypot(x - 45, y - 50) < 4 and abs(r - radius) <= 2
                                    for x, y, r, k in discs if k == "dark"), discs)

    def test_blank_solid_disc_and_ui_lines_are_not_numbered(self):
        img = np.full((120, 180, 3), 230, np.uint8)
        cv2.circle(img, (40, 50), 15, (10, 10, 10), -1)
        cv2.rectangle(img, (85, 25), (150, 90), (10, 10, 10), 2)
        self.assertEqual(manual.callout_discs(img), [])

    def test_orange_disc_connected_to_frame_is_recovered(self):
        img = np.full((110, 180, 3), 230, np.uint8)
        cv2.circle(img, (45, 50), 10, (0, 160, 255), -1)
        cv2.line(img, (54, 50), (179, 50), (0, 160, 255), 2)
        cv2.putText(img, "3", (40, 55), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (25, 25, 25), 1, cv2.LINE_AA)
        discs = manual.callout_discs(img)
        self.assertTrue(any(k == "orange" and np.hypot(x - 45, y - 50) < 4 for x, y, _, k in discs), discs)

    def test_ocr_numpy_output_is_supported(self):
        class Result:
            txts = np.array(["1", "2"])
            scores = np.array([0.98, 0.95])
        n, score = manual.read_disc_ocr(lambda *a, **kw: Result(), np.full((20, 20), 255, np.uint8))
        self.assertEqual(n, 12)
        self.assertEqual(score, 0.95)

    def test_same_number_twice_abstains(self):
        g = np.full((20, 20), 255, np.uint8)
        discs = [(20, 20, 10, g, 1, 0.99), (80, 20, 10, g, 1, 0.99)]
        numbered, _ = manual.number_discs(discs, {1: {}, 2: {}}, manual.DigitBook())
        self.assertEqual(numbered, [])

    def test_reading_order_is_explicit_and_marked_unverified(self):
        g = np.full((20, 20), 255, np.uint8)
        discs = [(80, 20, 10, g, None, 0), (20, 20, 10, g, None, 0)]
        self.assertEqual(manual.number_discs(discs, {1: {}, 2: {}}, manual.DigitBook())[0], [])
        numbered, proof = manual.number_discs(discs, {1: {}, 2: {}}, manual.DigitBook(), True)
        self.assertEqual(numbered[0], (1, 20, 20, 10))
        self.assertEqual(proof[1]["number_method"], "reading_order")

    def test_matching_finds_40px_icon(self):
        alpha = np.zeros((40, 40), np.uint8)
        cv2.line(alpha, (8, 20), (32, 20), 255, 2)
        cv2.line(alpha, (20, 8), (20, 32), 255, 2)
        img = np.full((100, 120, 3), 220, np.uint8)
        img[30:70, 50:90][alpha > 0] = 20
        hits = manual.find_icons(img, [("plus", 40, alpha.astype(np.float32))], 0.9)
        self.assertTrue(any(iid == "plus" and abs(x - 50) <= 1 and abs(y - 30) <= 1
                            for _, x, y, _, iid in hits), hits)


class BuilderCommandTests(unittest.TestCase):
    def run_builder(self, *args):
        return subprocess.run([sys.executable, str(HERE / "build_icon_dictionary.py"), *map(str, args)],
                              text=True, capture_output=True)

    def test_missing_sheets_does_not_overwrite_dictionary(self):
        with tempfile.TemporaryDirectory() as root:
            out = Path(root)
            target = out / "icons.json"
            target.write_text("keep this dictionary", encoding="utf-8")
            result = self.run_builder("--sheets", out / "missing", "--out", out)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("no master-sheet PNGs", result.stderr)
            self.assertEqual(target.read_text(encoding="utf-8"), "keep this dictionary")

    def test_rebuild_merges_meanings_and_labels_win(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            sheets, out = root / "sheets", root / "out"
            sheets.mkdir(); out.mkdir()
            rgba = np.zeros((80, 420, 4), np.uint8)
            for x, y, size in builder.atlas_slots(420, 80)[1][0]:
                a = rgba[y:y + size, x:x + size, 3].copy()
                cv2.line(a, (size // 4, size // 2), (3 * size // 4, size // 2), 255, max(1, size // 10))
                rgba[y:y + size, x:x + size, 3] = a
            Image.fromarray(rgba).save(sheets / "atlas.png")
            first = self.run_builder("--sheets", sheets, "--out", out)
            self.assertEqual(first.returncode, 0, first.stderr)
            ic = json.loads((out / "icons.json").read_text(encoding="utf-8"))["icons"][0]
            entry = evidence(match={"sheet": ic["sheet"], "slot": ic["slot"]}, mask_sha256=ic["mask_sha256"])
            (out / "manual_meanings.json").write_text(json.dumps({"different-build-id": [entry]}))
            (out / "labels.json").write_text(json.dumps([{"match": entry["match"], "name": "人工<标签>",
                                                         "name_en": "manual", "category": "test"}]))
            result = self.run_builder("--sheets", sheets, "--out", out)
            self.assertEqual(result.returncode, 0, result.stderr)
            ic = json.loads((out / "icons.json").read_text(encoding="utf-8"))["icons"][0]
            self.assertEqual(ic["name"], "人工<标签>")
            self.assertEqual(ic["name_source"], "label")
            self.assertEqual(ic["manual_meanings"], [entry])
            self.assertIn("人工&lt;标签&gt;", (out / "index.html").read_text(encoding="utf-8"))
            with np.load(out / "masks.npz") as masks:
                self.assertIn(ic["id"] + "_2", masks)


if __name__ == "__main__":
    unittest.main()
