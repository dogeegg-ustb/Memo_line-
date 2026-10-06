param([string]$Sample, [switch]$SkipChecks)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'source/PacketReplay.csproj'
if (-not $SkipChecks) {
    dotnet build (Join-Path $PSScriptRoot 'checks/PacketReplayChecks.csproj') -c Release -m:1 -nr:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'PacketReplay checks build failed' }
    $checkDll = Join-Path $PSScriptRoot 'checks/bin/Release/net10.0-windows/PacketReplayChecks.dll'
    if ($Sample) { dotnet $checkDll $Sample } else { dotnet $checkDll }
    if ($LASTEXITCODE -ne 0) { throw 'PacketReplay checks failed' }
}
$output = Join-Path $PSScriptRoot 'release/win-x64'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:UseSharedCompilation=false -o $output -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'PacketReplay publish failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output -Force
Write-Host (Join-Path $output 'PacketReplay.exe')
