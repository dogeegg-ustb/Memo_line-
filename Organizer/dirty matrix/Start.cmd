@echo off
setlocal
cd /d "%~dp0"
if exist "publish\win-x64\dirty matrix.exe" (
    start "" "publish\win-x64\dirty matrix.exe"
) else (
    dotnet run --project "source\DirtyMatrix.csproj" -c Release
    if errorlevel 1 pause
)
