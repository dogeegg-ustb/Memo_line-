@echo off
setlocal EnableExtensions
cd /d "%~dp0"
chcp 65001 >nul
set "PYTHONUTF8=1"
set "PYTHONIOENCODING=utf-8"
set "PYTHONNOUSERSITE=1"
set "PIP_DISABLE_PIP_VERSION_CHECK=1"
set "PIP_NO_INPUT=1"

rem Reuse the tested OCR environment when it exists; otherwise build one here.
set "SHARED_PY=D:\Users\ZZY\Downloads\Memo_Line\Memo_Line\recognizor\csp_layer_state_reader\.venv\Scripts\python.exe"
if exist "%SHARED_PY%" goto run_shared
if exist ".venv\Scripts\python.exe" goto run_local

where py.exe >nul 2>nul
if errorlevel 1 goto use_python
py -3 -m venv .venv
if errorlevel 1 goto env_error
goto install_deps

:use_python
where python.exe >nul 2>nul
if errorlevel 1 goto no_python
python -m venv .venv
if errorlevel 1 goto env_error

:install_deps
.venv\Scripts\python.exe -m pip install -r requirements.txt
if errorlevel 1 goto deps_error

:run_local
.venv\Scripts\python.exe -X utf8 -m quick_reader.main
goto end

:run_shared
"%SHARED_PY%" -X utf8 -m quick_reader.main
goto end

:no_python
echo Python 3 and 64 bit was not found. Install Python and run start.bat again.
pause
exit /b 1
:env_error
echo Could not create a Python environment.
pause
exit /b 1
:deps_error
echo Dependency installation failed. Check network access and try again.
pause
exit /b 1
:end
if errorlevel 1 pause
