@echo off
chcp 65001 >nul
cd /d "%~dp0"
py -3 panel_host.py %*
if errorlevel 1 pause
