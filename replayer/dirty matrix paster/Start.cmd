@echo off
cd /d "%~dp0"
if not exist "publish\win-x64-v2\DirtyMatrixPaster.exe" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
  if errorlevel 1 exit /b 1
)
start "" "%~dp0publish\win-x64-v2\DirtyMatrixPaster.exe" %*
