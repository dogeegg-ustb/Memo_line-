"""Query saved CSP shortcut configuration without opening the recorder or its UI."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

# Source checkout and published integration directory share the existing catalog.
HERE = Path(__file__).resolve().parent
SOURCE_READERS = HERE.parent.parent / "CSP_Shortcut_Manager"
if SOURCE_READERS.is_dir():
    sys.path.insert(0, str(SOURCE_READERS))

from catalog import Catalog


def read_configuration(config_dir=None):
    root = Path(config_dir).expanduser().resolve() if config_dir else None
    if root is not None and not root.is_dir():
        raise FileNotFoundError(f"CSP 配置目录不存在: {root}")
    return Catalog(root).snapshot()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config-dir", help="CSP 用户配置目录；默认自动定位本机配置")
    args = parser.parse_args(argv)
    try:
        result = read_configuration(args.config_dir)
    except Exception as ex:
        print(json.dumps(dict(success=False, error=str(ex)), ensure_ascii=False), file=sys.stderr)
        return 1
    print(json.dumps(result, ensure_ascii=False, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    raise SystemExit(main())
