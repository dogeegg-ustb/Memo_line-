@echo off
setlocal
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
  echo [ERROR] .venv not found. Create venv with Python 3.10+ and install deps first.
  echo   Example ^(run from repo root, not this folder — local types.py shadows stdlib^):
  echo     "C:\Users\dogeegg\miniconda3\envs\yolo-cuda\python.exe" -m venv canvas_border_autocrop\.venv
  echo     canvas_border_autocrop\.venv\Scripts\python.exe -m pip install -r canvas_border_autocrop\requirements.txt pyinstaller
  exit /b 1
)

echo [0/2] Checking Python version ^(need 3.10+ for dataclass slots^)...
".venv\Scripts\python.exe" -c "import sys; raise SystemExit(0 if sys.version_info >= (3,10) else 1)"
if errorlevel 1 (
  echo [ERROR] .venv Python is older than 3.10. Rebuild venv with 3.10+.
  echo   Do NOT use system Python 3.8 or default 3.14 for this project.
  exit /b 1
)

echo [1/2] Ensuring PyInstaller...
REM Leave this directory: local types.py shadows stdlib "types".
pushd "%~dp0.."
"%~dp0.venv\Scripts\python.exe" -m pip install -q pyinstaller
if errorlevel 1 (
  popd
  exit /b 1
)

echo [2/2] Building CanvasBorderDetect.exe ...
"%~dp0.venv\Scripts\python.exe" -m PyInstaller --noconfirm --clean "%~dp0CanvasBorderDetect.spec" --distpath "%~dp0dist" --workpath "%~dp0build"
set ERR=%ERRORLEVEL%
popd
if not %ERR%==0 exit /b %ERR%

echo.
echo Done: dist\CanvasBorderDetect.exe
endlocal
