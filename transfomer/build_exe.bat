@echo off
setlocal
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
  echo [ERROR] .venv not found. Create venv with Python 3.10+ and install deps first.
  echo   Example:
  echo     py -3.10 -m venv .venv
  echo     .venv\Scripts\python.exe -m pip install -r requirements.txt pyinstaller
  exit /b 1
)

echo [0/2] Checking Python version ^(need 3.10+ for dataclass slots^)...
".venv\Scripts\python.exe" -c "import sys; raise SystemExit(0 if sys.version_info >= (3,10) else 1)"
if errorlevel 1 (
  echo [ERROR] .venv Python is older than 3.10. Rebuild venv with 3.10+.
  exit /b 1
)

echo [1/2] Ensuring PyInstaller...
".venv\Scripts\python.exe" -m pip install -q pyinstaller
if errorlevel 1 exit /b 1

echo [2/2] Building CanvasBorderDetect.exe ...
".venv\Scripts\python.exe" -m PyInstaller --noconfirm --clean CanvasBorderDetect.spec
if errorlevel 1 exit /b 1

echo.
echo Done: dist\CanvasBorderDetect.exe
endlocal
