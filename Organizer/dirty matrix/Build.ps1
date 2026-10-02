$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'source/DirtyMatrix.csproj'
$publishPath = Join-Path $PSScriptRoot 'publish/win-x64'
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'dirty matrix publish failed.' }
Write-Host "Published: $publishPath\dirty matrix.exe"
