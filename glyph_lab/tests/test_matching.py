import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

from glyph_lab.benchmark import synthetic
from glyph_lab.core import GlyphMatcher, NumberMatcher
from glyph_lab.dictionary import load_words
from glyph_lab.fonts import default_font, make_renderer


class MatchingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.font = default_font()
        cls.renderer = make_renderer(cls.font)
        cls.words = ["不透明度", "不透明", "笔刷尺寸", "混合模式", "混色模式", "H:", "V:"]
        cls.matcher = GlyphMatcher(cls.words, cls.font)

    @classmethod
    def tearDownClass(cls):
        cls.renderer.close()

    def test_highlight_noise_polarity_and_rule(self):
        for seed in (1,4,5,8,41,85):
            with self.subTest(seed=seed):
                r = self.matcher.recognize(synthetic(self.renderer,"不透明度",seed))
                self.assertEqual(r.text,"不透明度")

    def test_held_out_rasterization(self):
        self.assertEqual(self.matcher.recognize(synthetic(self.renderer,"不透明度",3)).text,"不透明度")

    def test_near_words_are_distinguished(self):
        for word in ("混合模式", "混色模式"):
            self.assertEqual(self.matcher.recognize(synthetic(self.renderer,word,41)).text,word)

    def test_no_prefix_or_suffix_acceptance(self):
        for word in ("不透明度甲", "不透明度.", "不透明甲", "未收录词语"):
            with self.subTest(word=word):
                self.assertFalse(self.matcher.recognize(synthetic(self.renderer,word,41)).accepted)

    def test_missing_punctuation_components_rejected(self):
        for word in ("H.", "V."):
            self.assertFalse(self.matcher.recognize(synthetic(self.renderer,word,41)).accepted)
        self.assertEqual(self.matcher.recognize(synthetic(self.renderer,"H:",41)).text,"H:")

    def test_missing_attached_bracket_rejected(self):
        matcher=GlyphMatcher(["[邊緣處理]"],self.font)
        for seed in (0,1,3,41):
            with self.subTest(seed=seed):
                self.assertIsNone(matcher.recognize(synthetic(self.renderer,"[邊緣處理",seed)).text)
                self.assertEqual(matcher.recognize(synthetic(self.renderer,"[邊緣處理]",seed)).text,"[邊緣處理]")

    def test_blank_and_wrong_size_rejected(self):
        self.assertFalse(self.matcher.recognize(np.full((30,100),60,np.uint8)).accepted)
        other = make_renderer(type(self.font)(self.font.source,36,self.font.index,self.font.backend))
        try:
            self.assertFalse(self.matcher.recognize(synthetic(other,"不透明度",41)).accepted)
        finally:
            other.close()

    def test_missing_glyph_not_built(self):
        m=GlyphMatcher(["不透明度", "\U0010ffff"], self.font)
        self.assertEqual(m.words,["不透明度"])

    def test_cache_roundtrip_and_invalidation(self):
        with tempfile.TemporaryDirectory() as folder:
            a=GlyphMatcher(self.words,self.font,cache_dir=folder)
            b=GlyphMatcher(self.words,self.font,cache_dir=folder)
            c=GlyphMatcher(self.words+["图层"],self.font,cache_dir=folder)
            self.assertFalse(a.cache_hit); self.assertTrue(b.cache_hit); self.assertFalse(c.cache_hit)
            im=synthetic(self.renderer,"笔刷尺寸",41)
            self.assertEqual(a.recognize(im).text,b.recognize(im).text)
            self.assertNotEqual(a.cache_key,c.cache_key)
            # A truncated cache must be rebuilt instead of silently returning bad labels.
            next(Path(folder).glob(b.cache_key+".npz")).write_bytes(b"broken")
            repaired=GlyphMatcher(self.words,self.font,cache_dir=folder)
            self.assertFalse(repaired.cache_hit)
            self.assertEqual(repaired.recognize(im).text,"笔刷尺寸")

    def test_dictionary_formats(self):
        with tempfile.TemporaryDirectory() as folder:
            p=Path(folder)/"words.json"
            p.write_text(json.dumps({"languages":{"chinese_sc":["图层","不透明度"]}}))
            self.assertEqual(load_words(p),["不透明度","图层"])
            p.write_text(json.dumps({"words":{"图层":{},"不透明度":{}}}))
            self.assertEqual(load_words(p),["不透明度","图层"])

    def test_numeric_values_not_in_lexicon(self):
        numeric=NumberMatcher(self.font)
        for text in ("749.5","573.1","100%","0","8","+3.0"):
            with self.subTest(text=text):
                self.assertEqual(numeric.recognize(synthetic(self.renderer,text,41)).text,text)

    def test_numeric_garbage_and_two_numbers_rejected(self):
        numeric=NumberMatcher(self.font)
        for text in ("12a3", "3..5", "12   34"):
            with self.subTest(text=text):
                self.assertFalse(numeric.recognize(synthetic(self.renderer,text,41)).accepted)

    def test_numeric_baseline_and_decimal_separator(self):
        # Regression: 21px dots were being accepted as commas; a missing/faint
        # comma tail must never silently change the value or separator.
        for px in (18,21):
            font=type(self.font)(self.font.source,px,self.font.index,self.font.backend)
            renderer=make_renderer(font)
            try:
                matcher=NumberMatcher(font)
                for text in ("3.5","156.2","156,2","699.6","-12,5"):
                    for seed in (0,1,3,41):
                        with self.subTest(px=px,text=text,seed=seed):
                            result=matcher.recognize(synthetic(renderer,text,seed))
                            self.assertIn(result.text,(None,text))
                            if seed==41:
                                self.assertEqual(result.text,text)
            finally:
                renderer.close()

    def test_numeric_faint_input_does_not_crash(self):
        image=np.zeros((24,32),np.uint8);image[10:13,10:14]=15
        self.assertFalse(NumberMatcher(self.font).recognize(image).accepted)


if __name__ == "__main__":
    unittest.main()
