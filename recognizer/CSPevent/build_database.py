"""Build a read-only catalog of installed CLIP STUDIO PAINT language resources.

The resource path identifies a string, not a CSP command or an event ID.
"""

from __future__ import annotations

import argparse
from contextlib import closing
import hashlib
import os
from pathlib import Path
import sqlite3
import struct
import unicodedata


DEFAULT_INSTALLATION = Path(r"D:\CLIP STUDIO 1.5\CLIP STUDIO PAINT")
DEFAULT_OUTPUT = Path(__file__).with_name("csp_strings.sqlite3")


def normalize(value: str) -> str:
    return " ".join(unicodedata.normalize("NFKC", value).casefold().split())


def parse_node(blob: bytes, start: int = 0, end: int | None = None,
               path: tuple[int, ...] = ()) -> dict[tuple[int, ...], str]:
    """Decode CSP's nested big-endian resource tree, preserving leaf paths."""
    if end is None:
        end = len(blob)
    if end - start < 4:
        return {}
    count = struct.unpack_from(">I", blob, start)[0]
    directory_end = start + 4 + count * 12
    if 0 < count < 10000 and directory_end <= end:
        entries = [struct.unpack_from(">III", blob, start + 4 + i * 12)
                   for i in range(count)]
        if all(directory_end <= offset and offset + size <= end
               for _, offset, size in entries):
            result: dict[tuple[int, ...], str] = {}
            for key, offset, size in entries:
                result.update(parse_node(blob, offset, offset + size, path + (key,)))
            return result
    if count == end - start - 4:
        try:
            return {path: blob[start + 4:end].decode("utf-8")}
        except UnicodeError:
            pass
    return {}


def create_schema(db: sqlite3.Connection) -> None:
    db.executescript("""
        PRAGMA foreign_keys = ON;
        CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE source_files (
            language TEXT NOT NULL,
            resource_file TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            byte_count INTEGER NOT NULL,
            string_count INTEGER NOT NULL,
            PRIMARY KEY (language, resource_file)
        );
        CREATE TABLE resource_strings (
            resource_file TEXT NOT NULL,
            node_path TEXT NOT NULL,
            language TEXT NOT NULL,
            text TEXT NOT NULL,
            normalized_text TEXT NOT NULL,
            PRIMARY KEY (resource_file, node_path, language),
            FOREIGN KEY (language, resource_file)
                REFERENCES source_files (language, resource_file)
        );
        CREATE INDEX strings_language_text
            ON resource_strings (language, normalized_text);
        CREATE INDEX strings_text
            ON resource_strings (normalized_text);
        CREATE VIEW string_keys AS
            SELECT resource_file, node_path, COUNT(*) AS language_count
            FROM resource_strings GROUP BY resource_file, node_path;
    """)


def build(installation: Path, output: Path, version: str = "unknown") -> tuple[int, int, int]:
    resource_root = installation / "resource"
    if not resource_root.is_dir():
        raise FileNotFoundError(f"CSP resource directory not found: {resource_root}")
    languages = sorted(p for p in resource_root.iterdir() if p.is_dir())
    if not languages:
        raise ValueError(f"No language directories in {resource_root}")
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(output.name + ".tmp")
    if temporary.exists():
        temporary.unlink()
    source_count = string_count = 0
    try:
        with closing(sqlite3.connect(temporary)) as db:
            create_schema(db)
            executable_stat = (installation / "CLIPStudioPaint.exe").stat() if (
                installation / "CLIPStudioPaint.exe").is_file() else None
            metadata = {
                "installation": str(installation.resolve()),
                "csp_version": version,
                "format": "CSP nested UTF-8 resource strings",
                "note": "Resource paths identify strings, not click events or command IDs.",
                "executable_size": str(executable_stat.st_size) if executable_stat else "unknown",
            }
            db.executemany("INSERT INTO metadata VALUES (?, ?)", metadata.items())
            for language in languages:
                for source in sorted(p for p in language.iterdir() if p.is_file()):
                    blob = source.read_bytes()
                    entries = parse_node(blob)
                    db.execute("INSERT INTO source_files VALUES (?, ?, ?, ?, ?)", (
                        language.name, source.name, hashlib.sha256(blob).hexdigest(),
                        len(blob), len(entries)))
                    db.executemany("INSERT INTO resource_strings VALUES (?, ?, ?, ?, ?)",
                        ((source.name, ".".join(map(str, path)), language.name,
                          value, normalize(value))
                         for path, value in entries.items() if value.strip()))
                    source_count += 1
                    string_count += sum(bool(value.strip()) for value in entries.values())
            db.execute("INSERT INTO metadata VALUES ('language_count', ?)",
                       (str(len(languages)),))
            db.execute("INSERT INTO metadata VALUES ('source_file_count', ?)",
                       (str(source_count),))
            db.execute("INSERT INTO metadata VALUES ('string_row_count', ?)",
                       (str(string_count),))

            try:
                from build_icons import populate_icons
                icon_count = populate_icons(db)
                db.execute("INSERT INTO metadata VALUES ('icon_count', ?)", (str(icon_count),))
            except ImportError:
                pass

            db.commit()
        os.replace(temporary, output)
    finally:
        if temporary.exists():
            temporary.unlink()
    return len(languages), source_count, string_count


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("installation", nargs="?", type=Path,
                        default=DEFAULT_INSTALLATION)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--version", default="unknown",
                        help="Version verified from the installed executable")
    args = parser.parse_args()
    languages, sources, rows = build(args.installation, args.output, args.version)
    print(f"Built {args.output}: {languages} languages, {sources} files, "
          f"{rows} nonempty string rows")


if __name__ == "__main__":
    main()
