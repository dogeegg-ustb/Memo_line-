from __future__ import annotations

import json
import re
import unicodedata
from difflib import SequenceMatcher
from functools import lru_cache
from pathlib import Path
from typing import Any


def normalize(text: str) -> str:
    return re.sub(r"[^\w]", "", unicodedata.normalize("NFKC", text).casefold())


class LayerCatalog:
    """Curated CSP layer-panel vocabulary, modeled after csp_panel_validator's catalog."""

    def __init__(self, path: Path | None = None):
        path = path or Path(__file__).with_name("data") / "layer_catalog.json"
        payload = json.loads(path.read_text(encoding="utf-8"))
        self.metadata = payload["metadata"]
        self.blend_modes = payload["blend_modes"]
        self.aliases: dict[str, str] = {}
        for entry in self.blend_modes:
            for alias in entry["aliases"]:
                key = normalize(alias)
                if key:
                    self.aliases[key] = entry["canonical"]

    def match_blend_mode(self, text: str) -> dict[str, Any]:
        """Return only unique catalog matches; uncertain OCR remains unresolved."""
        query = normalize(text)
        if not query:
            return {"status": "unknown", "score": 0.0, "method": "empty"}
        exact = self.aliases.get(query)
        if exact:
            return {"status": "matched", "canonical": exact, "score": 1.0,
                    "method": "exact", "matched_alias": query}

        scored: list[tuple[float, str, str]] = []
        for alias, canonical in self.aliases.items():
            # OCR confusions are usually one glyph in a short, fixed vocabulary.
            # Compare similarly sized strings only; never match arbitrary names by prefix.
            if abs(len(query) - len(alias)) > 1 or min(len(query), len(alias)) < 2:
                continue
            score = SequenceMatcher(None, query, alias).ratio()
            if score >= 0.82:
                scored.append((score, alias, canonical))
        scored.sort(reverse=True)
        if not scored or (len(scored) > 1 and scored[0][0] - scored[1][0] < 0.12):
            return {"status": "unknown", "score": scored[0][0] if scored else 0.0,
                    "method": "ambiguous" if scored else "no_match",
                    "candidates": [item[1] for item in scored[:3]]}
        score, alias, canonical = scored[0]
        return {"status": "matched", "canonical": canonical, "score": score,
                "method": "fuzzy", "matched_alias": alias}


@lru_cache(maxsize=1)
def get_layer_catalog() -> LayerCatalog:
    return LayerCatalog()
