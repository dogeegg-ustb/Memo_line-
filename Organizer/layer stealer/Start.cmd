@echo off
setlocal
cd /d "%~dp0"
if exist "publish\win-x64-fixed\layer stealer.exe" (
    start "" "publish\win-x64-fixed\layer stealer.exe"
) else (
    dotnet run --project "source\LayerStealer.csproj" -c Release
    if errorlevel 1 pause
)
