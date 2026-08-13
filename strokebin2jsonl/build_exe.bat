@echo off
setlocal
cd /d "%~dp0"

echo [1/3] Creating venv...
if not exist .venv (
  python -m venv .venv
  if errorlevel 1 (
    echo Failed to create venv. Ensure Python is on PATH.
    exit /b 1
  )
)

call .venv\Scripts\activate.bat
REM Avoid broken system proxy settings for pip
set HTTP_PROXY=
set HTTPS_PROXY=
set http_proxy=
set https_proxy=
set ALL_PROXY=
set all_proxy=

echo [2/3] Installing PyInstaller...
python -m pip install -U pip
python -m pip install --trusted-host pypi.tuna.tsinghua.edu.cn -i https://pypi.tuna.tsinghua.edu.cn/simple -r requirements.txt
if errorlevel 1 (
  echo Tuna mirror failed, retrying default index...
  python -m pip install --trusted-host pypi.org --trusted-host files.pythonhosted.org -r requirements.txt
)
if errorlevel 1 exit /b 1

echo [3/3] Building onefile exe...
python -m PyInstaller --noconfirm --clean --onefile --name strokebin2jsonl --console strokebin2jsonl.py
if errorlevel 1 exit /b 1

echo.
echo Done: "%~dp0dist\strokebin2jsonl.exe"
echo Usage: strokebin2jsonl.exe path\to\file.strokebin
echo        strokebin2jsonl.exe path\to\file.strokebin -o out.jsonl
endlocal
