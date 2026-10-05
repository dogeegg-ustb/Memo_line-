@echo off
cd /d "%~dp0"
if not exist "%~dp0publish\win-x64\MemolineDemo.exe" (
  echo Please run Build.ps1 first.
  pause
  exit /b 1
)
start "" "%~dp0publish\win-x64\MemolineDemo.exe" %*
