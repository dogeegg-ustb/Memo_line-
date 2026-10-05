$ErrorActionPreference = 'Stop'
$bridgeManifest = Join-Path $PSScriptRoot 'bridge/Cargo.toml'
$portableCargo = Join-Path $PSScriptRoot 'tools/cargo/bin/cargo.exe'
if (Test-Path -LiteralPath $portableCargo) {
    $env:RUSTUP_HOME = Join-Path $PSScriptRoot 'tools/rustup'
    $env:CARGO_HOME = Join-Path $PSScriptRoot 'tools/cargo'
    $env:PATH = "$env:CARGO_HOME\bin;$env:PATH"
}
cargo build --locked --release --manifest-path $bridgeManifest
if ($LASTEXITCODE -ne 0) { throw 'clip-layer-bridge build failed' }
$outputPath = Join-Path $PSScriptRoot 'publish/win-x64'
dotnet publish (Join-Path $PSScriptRoot 'source/CanvasLayerWatcher.csproj') -c Release -r win-x64 --self-contained true -o $outputPath -m:1 -p:UseSharedCompilation=false -nr:false
if ($LASTEXITCODE -ne 0) { throw 'CanvasLayerWatcher publish failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bridge/target/release/clip-layer-bridge.exe') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NOTICE.md') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DIFF_FORMAT.md') -Destination $outputPath
$cargoDirectory = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE '.cargo' }
$registryRoot = Join-Path $cargoDirectory 'registry/src'
if (Test-Path -LiteralPath $registryRoot) {
    $licenseRoot = Join-Path $outputPath 'third-party-licenses'
    foreach ($registry in Get-ChildItem -LiteralPath $registryRoot -Directory) {
        foreach ($crate in Get-ChildItem -LiteralPath $registry.FullName -Directory) {
            $licenses = Get-ChildItem -LiteralPath $crate.FullName -File | Where-Object { $_.Name -like 'LICENSE*' -or $_.Name -like 'COPYING*' }
            if ($licenses) {
                $destination = Join-Path $licenseRoot $crate.Name
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                foreach ($license in $licenses) { Copy-Item -LiteralPath $license.FullName -Destination $destination }
            }
        }
    }
}
Write-Host "Published: $outputPath\CanvasLayerWatcher.exe"
