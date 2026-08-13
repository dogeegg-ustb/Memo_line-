"""Lightweight stage tracer for hang diagnosis. Enable with ARTLINE_DEBUG=1."""

from __future__ import annotations

import os
import sys
import time
from pathlib import Path

_ENABLED = os.environ.get("ARTLINE_DEBUG", "").strip() not in ("", "0", "false", "False")
_LOG_PATH = Path(__file__).resolve().parent.parent / "debug_runtime.log"
_t0 = time.perf_counter()
_last = _t0


def enabled() -> bool:
    return _ENABLED


def reset_log() -> None:
    if not _ENABLED:
        return
    _LOG_PATH.write_text("", encoding="utf-8")


def stage(name: str, **fields: object) -> None:
    if not _ENABLED:
        return
    global _last
    now = time.perf_counter()
    abs_ms = (now - _t0) * 1000.0
    delta_ms = (now - _last) * 1000.0
    _last = now
    extra = " ".join(f"{k}={v}" for k, v in fields.items())
    line = f"[{abs_ms:8.0f}ms +{delta_ms:7.0f}ms] {name}"
    if extra:
        line += f"  {extra}"
    line += "\n"
    try:
        with _LOG_PATH.open("a", encoding="utf-8") as f:
            f.write(line)
            f.flush()
    except Exception:
        pass
    try:
        sys.stderr.write(line)
        sys.stderr.flush()
    except Exception:
        pass
