$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'source/LayerStealer.csproj'
$publishPath = Join-Path $PSScriptRoot 'publish/win-x64-fixed'
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'layer stealer publish failed.' }
$legacySettings = Join-Path $PSScriptRoot 'publish/win-x64/layer-stealer-settings.json'
$currentSettings = Join-Path $publishPath 'layer-stealer-settings.json'
if ((Test-Path -LiteralPath $legacySettings) -and -not (Test-Path -LiteralPath $currentSettings)) {
    Copy-Item -LiteralPath $legacySettings -Destination $currentSettings
}
Write-Host "Published: $publishPath\layer stealer.exe"
