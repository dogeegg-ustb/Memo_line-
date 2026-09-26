"""Detailed step-by-step diagnosis mirroring diag_roi.cpp."""
import ctypes
import struct
import sys
from pathlib import Path

from PIL import Image

# We'll compile and run a tiny native diag exe instead - Python can't call C++ internals.
# This script shells out to diag_steps.exe if present.

def main():
    print("Use diag_steps.exe")
    return 1

if __name__ == "__main__":
    raise SystemExit(main())
