# Pack a zip that OpenTabletDriver Plugin Manager can install.
# Native DLL must live under runtimes/ (see OpenTabletDriver DesktopPluginContext.LoadUnmanagedDll).

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root ".tools\dotnet8\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$dotnetProj = Join-Path $root "dotnet\OtdStrokeRecorder.Plugin\OtdStrokeRecorder.Plugin.csproj"
$nativeDll = Join-Path $root "build\Release\otd_stroke.dll"
$outDir = Join-Path $root "dist"
$publishDir = Join-Path $outDir "publish"
$stageDir = Join-Path $outDir "stage\ARTStrokeRecorder"
$zipPath = Join-Path $outDir "ARTStrokeRecorder.zip"

if (-not (Test-Path $nativeDll)) {
    throw "Missing native DLL: $nativeDll`nBuild C++ first: cmake --build build --config Release"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }

& $dotnet publish $dotnetProj -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item (Join-Path $publishDir "OtdStrokeRecorder.Plugin.dll") $stageDir
Copy-Item (Join-Path $root "dotnet\OtdStrokeRecorder.Plugin\metadata.json") $stageDir

# REQUIRED layout for OTD native loading:
$nativeDest = Join-Path $stageDir "runtimes\win-x64\native"
New-Item -ItemType Directory -Force -Path $nativeDest | Out-Null
Copy-Item $nativeDll (Join-Path $nativeDest "otd_stroke.dll")
# Also keep a copy at root for our explicit finder.
Copy-Item $nativeDll (Join-Path $stageDir "otd_stroke.dll")

if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath

Write-Host ""
Write-Host "Packaged: $zipPath"
Write-Host "Zip contents must include:"
Write-Host "  OtdStrokeRecorder.Plugin.dll"
Write-Host "  metadata.json"
Write-Host "  otd_stroke.dll"
Write-Host "  runtimes/win-x64/native/otd_stroke.dll"
Write-Host ""
Write-Host "Reinstall in OTD: uninstall old plugin, then Install Plugin -> this zip, Apply."
