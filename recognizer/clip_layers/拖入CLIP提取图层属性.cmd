@echo off
setlocal
if "%~1"=="" (
    echo Drag one or more .clip files onto this script.
    pause
    exit /b 1
)
:next
if "%~1"=="" goto done
python "%~dp0clip_layers.py" "%~1" --output "%~dpn1.layers.json" --force
if errorlevel 1 (
    echo Failed: %~1
    pause
)
shift
goto next
:done
echo Done. JSON files are next to the corresponding .clip files.
pause
