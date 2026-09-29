@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Keep console output and Python streams independent of the Windows locale.
chcp 65001 >nul
set "PYTHONUTF8=1"
set "PYTHONIOENCODING=utf-8"
set "PYTHONNOUSERSITE=1"
set "PIP_DISABLE_PIP_VERSION_CHECK=1"
set "PIP_NO_INPUT=1"

rem Prefer the Python Launcher, then fall back to python.exe on PATH.
where py.exe >nul 2>nul
if not errorlevel 1 goto check_launcher
goto check_python

:check_launcher
py -3 -c "import sys; raise SystemExit(0 if (3, 10) <= sys.version_info[:2] < (3, 15) and sys.maxsize > 2**32 else 1)" >nul 2>nul
if not errorlevel 1 goto use_launcher
goto check_python

:use_launcher
set "PYTHON_CMD=py -3"
goto prepare_environment

:check_python
where python.exe >nul 2>nul
if errorlevel 1 goto no_python
python -c "import sys; raise SystemExit(0 if (3, 10) <= sys.version_info[:2] < (3, 15) and sys.maxsize > 2**32 else 1)" >nul 2>nul
if errorlevel 1 goto no_python
set "PYTHON_CMD=python"

:prepare_environment
if not exist ".venv\Scripts\python.exe" goto create_environment

rem A copied venv may still point to another user's Python installation.
".venv\Scripts\python.exe" -c "import sys; raise SystemExit(0 if (3, 10) <= sys.version_info[:2] < (3, 15) and sys.maxsize > 2**32 else 1)" >nul 2>nul
if not errorlevel 1 goto check_dependencies
echo Repairing the project Python environment for this computer...
%PYTHON_CMD% -m venv --upgrade ".venv"
if errorlevel 1 goto venv_error
goto check_dependencies

:create_environment
echo Creating the project Python environment...
%PYTHON_CMD% -m venv ".venv"
if errorlevel 1 goto venv_error

:check_dependencies
".venv\Scripts\python.exe" -c "import importlib.util,sys; modules=('PySide6','numpy','cv2','dxcam','rapidocr','onnxruntime'); raise SystemExit(0 if all(importlib.util.find_spec(name) for name in modules) else 1)" >nul 2>nul
if not errorlevel 1 goto run_application

echo Installing required packages from PyPI. This is only needed when packages are missing.
".venv\Scripts\python.exe" -m pip install --index-url https://pypi.org/simple -r requirements.txt
if errorlevel 1 goto dependency_error

:run_application
".venv\Scripts\python.exe" -X utf8 -m csp_panel_validator.main
set "APP_EXIT=%ERRORLEVEL%"
if not "%APP_EXIT%"=="0" pause
exit /b %APP_EXIT%

:no_python
echo Python 3.10 to 3.14, 64-bit, was not found.
echo Install a compatible Python runtime. Windows display language settings are not required.
pause
exit /b 1

:venv_error
echo Could not create or repair the project Python environment.
echo Check that the selected Python runtime is writable and try again.
pause
exit /b 1

:dependency_error
echo Package installation failed. Check the network connection or PyPI access, then run start.bat again.
pause
exit /b 1
