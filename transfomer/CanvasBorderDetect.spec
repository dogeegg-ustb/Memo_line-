# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for CanvasBorderDetect.exe"""

import os
import sys
from pathlib import Path

from PyInstaller.utils.hooks import collect_all

block_cipher = None

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
    "canvas_border_autocrop.transform_pipeline",
    "canvas_border_autocrop.transform_session",
    "canvas_border_autocrop.transform_types",
    "canvas_border_autocrop.transform_solver",
    "canvas_border_autocrop.transform_validate",
    "canvas_border_autocrop.navigator_canvas",
    "canvas_border_autocrop.red_frame",
    "canvas_border_autocrop.red_frame_hypothesis",
]

# Collect opencv binaries/data
tmp_ret = collect_all("cv2")
datas += tmp_ret[0]
binaries += tmp_ret[1]
hiddenimports += tmp_ret[2]

# Conda/Miniconda Python often keeps dependent DLLs in Library/bin;
# PyInstaller may not resolve them from PATH alone.
_conda_lib_bin = Path(sys.prefix) / "Library" / "bin"
if not _conda_lib_bin.is_dir():
    # venv created from conda: real base is parent of venv when python DLL is elsewhere
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
    ["canvas_border_autocrop/gui_app.py"],
    pathex=["."],
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
    console=False,  # GUI, no console window
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
