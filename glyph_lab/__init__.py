"""Standalone, local glyph matching experiments; no OCR runtime dependency."""

from .core import GlyphMatcher, MatchResult
from .fonts import FontSpec

__all__ = ["GlyphMatcher", "MatchResult", "FontSpec"]
