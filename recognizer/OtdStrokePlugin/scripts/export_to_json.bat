@echo off
chcp 65001 >nul
setlocal

set "EXPORT_EXE=%~dp0..\build\Release\otd_stroke_export.exe"
if not exist "%EXPORT_EXE%" (
  echo Missing: %EXPORT_EXE%
  echo Build the project first.
  pause
  exit /b 1
)

if "%~1"=="" (
  echo Drag and drop a .strokebin file onto this bat, or run:
  echo   export_to_json.bat "C:\path\to\file.strokebin"
  echo.
  echo Default input folder:
  echo   %LOCALAPPDATA%\OpenTabletDriver\stroke
  echo.
  pause
  exit /b 1
)

set "INPUT=%~1"
set "OUTPUT=%~dpn1.json"

echo Input : %INPUT%
echo Output: %OUTPUT%
"%EXPORT_EXE%" "%INPUT%" "%OUTPUT%"
set "ERR=%ERRORLEVEL%"
echo.
if not "%ERR%"=="0" (
  echo Export failed, code=%ERR%
) else (
  echo Done.
)
pause
exit /b %ERR%
