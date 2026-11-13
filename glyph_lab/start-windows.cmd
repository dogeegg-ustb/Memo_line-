@echo off
setlocal
cd /d "%~dp0.."
if not exist "glyph_lab\.venv\Scripts\python.exe" (
    py -3 -m venv glyph_lab\.venv
    if errorlevel 1 goto failed
)
glyph_lab\.venv\Scripts\python.exe -c "import cv2, numpy, PIL, fontTools" 2>nul
if errorlevel 1 (
    glyph_lab\.venv\Scripts\python.exe -m pip install -r glyph_lab\requirements.txt
    if errorlevel 1 goto failed
)
glyph_lab\.venv\Scripts\python.exe -m glyph_lab serve --open %*
if errorlevel 1 goto failed
exit /b 0
:failed
echo Failed. Python 3.10+ and the listed dependencies are required.
pause
exit /b 1
