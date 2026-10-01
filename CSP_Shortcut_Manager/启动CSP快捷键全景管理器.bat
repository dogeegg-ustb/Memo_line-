@echo off
title CSP Shortcut Manager
cd /d "%~dp0"
echo Starting CSP Shortcut Manager...
python csp_explorer.py %*
if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Application exited with error code %errorlevel%.
    pause
)
