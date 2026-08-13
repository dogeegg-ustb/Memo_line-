# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for CanvasBorderDetect.exe (standalone autocrop GUI)."""

import sys
from pathlib import Path

from PyInstaller.utils.hooks import collect_all

block_cipher = None

# Package lives in this folder; parent must be on path for `import canvas_border_autocrop`.
_pkg_dir = Path(SPECPATH).resolve()
_root = _pkg_dir.parent

datas = []
binaries = []
hiddenimports = [
    "PIL._tkinter_finder",
    "numpy",
    "cv2",
    "canvas_border_autocrop",
    "canvas_border_autocrop.gui_app",
    "canvas_border_autocrop.capture_screen",
    "canvas_border_autocrop.overlay",
    "canvas_border_autocrop.session",
    "canvas_border_autocrop.detector",
    "canvas_border_autocrop.background",
    "canvas_border_autocrop.boundary",
    "canvas_border_autocrop.config",
    "canvas_border_autocrop.features",
    "canvas_border_autocrop.geometry",
    "canvas_border_autocrop.grower",
    "canvas_border_autocrop.hypotheses",
    "canvas_border_autocrop.refine",
    "canvas_border_autocrop.scoring",
    "canvas_border_autocrop.seeds",
    "canvas_border_autocrop.sides",
    "canvas_border_autocrop.similarity",
    "canvas_border_autocrop.types",
    "canvas_border_autocrop.validate",
    "canvas_border_autocrop._rle",
]

tmp_ret = collect_all("cv2")
datas += tmp_ret[0]
binaries += tmp_ret[1]
hiddenimports += tmp_ret[2]

# Conda/Miniconda Python often keeps dependent DLLs in Library/bin.
_conda_lib_bin = Path(sys.prefix) / "Library" / "bin"
if not _conda_lib_bin.is_dir():
    _base = Path(sys.base_prefix) / "Library" / "bin"
    if _base.is_dir():
        _conda_lib_bin = _base

_conda_dlls = [
    "liblzma.dll",
    "libbz2.dll",
    "LIBBZ2.dll",
    "libcrypto-3-x64.dll",
    "libssl-3-x64.dll",
    "ffi.dll",
    "libexpat.dll",
    "tk86t.dll",
    "tcl86t.dll",
    "zlib.dll",
]
if _conda_lib_bin.is_dir():
    seen = set()
    for name in _conda_dlls:
        path = _conda_lib_bin / name
        if path.is_file():
            key = path.name.lower()
            if key not in seen:
                binaries.append((str(path), "."))
                seen.add(key)

a = Analysis(
    [str(_pkg_dir / "gui_app.py")],
    pathex=[str(_root)],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name="CanvasBorderDetect",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
