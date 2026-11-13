#!/bin/zsh
set -e
cd "${0:A:h}/.."
if [[ ! -x glyph_lab/.venv/bin/python ]]; then
  glyph_python="${MEMOLINE_PYTHON:-$HOME/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/bin/python3}"
  if [[ ! -x "$glyph_python" ]]; then glyph_python="$(command -v python3)"; fi
  "$glyph_python" -c 'import sys; assert sys.version_info >= (3,10), "需要 Python 3.10 或更新版本"'
  "$glyph_python" -m venv glyph_lab/.venv
fi
if ! glyph_lab/.venv/bin/python -c 'import cv2, numpy, PIL, fontTools' 2>/dev/null; then
  glyph_lab/.venv/bin/python -m pip install -r glyph_lab/requirements.txt
fi
exec glyph_lab/.venv/bin/python -m glyph_lab serve --open "$@"
