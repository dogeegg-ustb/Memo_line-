from __future__ import annotations

import json
import re
import sqlite3
import unicodedata
from collections import defaultdict
from contextlib import closing
from difflib import SequenceMatcher
from functools import lru_cache
from pathlib import Path


def normalize(text: str) -> str:
    return re.sub(r"[^\w]", "", unicodedata.normalize("NFKC", text).casefold())


class PropertyCatalog:
    """Read SQLite once; use a prefix trie and length buckets during frame parsing."""

    def __init__(self, path: Path | None = None):
        path = path or Path(__file__).with_name("data") / "properties.sqlite3"
        with closing(sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)) as db:
            self.definitions = [json.loads(row[0]) for row in db.execute("SELECT definition_json FROM properties ORDER BY id")]
            self.metadata = {row[0]: json.loads(row[1]) for row in db.execute("SELECT key,value_json FROM metadata")}
        self.by_key = {d["key"]: d for d in self.definitions}
        self.aliases = defaultdict(list)
        self.lengths = defaultdict(set)
        self.trie = {}
        self.categories = {}
        for definition in self.definitions:
            for category in (definition["category"], definition["category_zh"]):
                self.categories[normalize(category)] = definition["category"]
            for alias in definition["aliases"]:
                word = normalize(alias)
                if not word:
                    continue
                if definition not in self.aliases[word]:
                    self.aliases[word].append(definition)
                self.lengths[len(word)].add(word)
                node = self.trie
                for char in word:
                    node = node.setdefault(char, {})
                node[""] = word

    @lru_cache(maxsize=2048)
    def match(self, text: str, category: str | None = None, value_kind: str | None = None) -> dict:
        word = normalize(text)
        node = self.trie
        matched = None
        for char in word:
            if char not in node:
                break
            node = node[char]
            if "" in node:
                matched = node[""]
        method, score = "exact" if word == matched else "prefix", 1.0
        if matched is None:
            # Short labels and close ties are too risky to auto-correct.
            query = normalize(re.split(r"\s*[-+]?\d+(?:[.,]\d+)?", text, maxsplit=1)[0])
            if len(query) < 5:
                return {"status": "unknown", "candidates": []}
            scored = []
            for length in range(len(query) - 1, len(query) + 2):
                for alias in self.lengths[length]:
                    score = SequenceMatcher(None, query, alias).ratio()
                    if score >= .82:
                        scored.append((score, alias))
            scored.sort(reverse=True)
            if not scored or (len(scored) > 1 and scored[0][0] - scored[1][0] < .12):
                return {"status": "unknown", "candidates": [a for _, a in scored[:3]]}
            score, matched = scored[0]
            method = "fuzzy"
        definitions = self.aliases[matched]
        if value_kind:
            compatible = [d for d in definitions if d['value_kind'] == value_kind or d['value_kind'] == 'compound']
            if compatible:
                definitions = compatible
        if category:
            contextual = [d for d in definitions if d["category"] == category]
            if contextual:
                definitions = contextual
        if len(definitions) != 1:
            # Opacity is a visible scalar with a shared public name. Preserve all
            # possible contexts; never claim an internal parameter identity.
            if {d['key'] for d in definitions} == {'opacity', 'watercolor_edge.opacity'}:
                return {'status': 'matched', 'definition': self.by_key['opacity'],
                        'definition_candidates': [d['id'] for d in definitions],
                        'matched_alias': matched, 'method': 'shared_visible_label', 'score': score}
            return {"status": "ambiguous", "candidates": [d["id"] for d in definitions], "matched_alias": matched}
        return {"status": "matched", "definition": definitions[0], "matched_alias": matched, "method": method, "score": score}

    def describe(self, key: str) -> dict:
        definition = self.by_key[key]
        return {"property_id": definition["id"], "category": definition["category"],
                "value_kind": definition["value_kind"], "read_support": definition["read_support"],
                "sources": definition["sources"], "catalog_version": self.metadata["catalog_version"]}


@lru_cache(maxsize=1)
def get_catalog() -> PropertyCatalog:
    return PropertyCatalog()
