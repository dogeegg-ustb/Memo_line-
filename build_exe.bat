@echo off
setlocal
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
  echo [ERROR] .venv not found. Create venv and install deps first.
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
