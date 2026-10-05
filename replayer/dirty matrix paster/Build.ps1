param([switch]$Check)
$ErrorActionPreference = 'Stop'
$backend = Join-Path $PSScriptRoot 'backend'
$cache = Join-Path $PSScriptRoot '.npm-cache'
Push-Location $backend
try {
    npm.cmd ci --offline=false --cache $cache --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'PSD dependencies install failed' }
    if ($Check) {
        node --test --test-isolation=none checks/*.test.mjs
        if ($LASTEXITCODE -ne 0) { throw 'PSD checks failed' }
    }
} finally { Pop-Location }
$publish = Join-Path $PSScriptRoot 'publish/win-x64-v2'
dotnet publish (Join-Path $PSScriptRoot 'source/DirtyMatrixPaster.csproj') -c Release -r win-x64 --self-contained true -o $publish -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'DirtyMatrixPaster publish failed' }
$publishedBackend = Join-Path $publish 'backend'
New-Item -ItemType Directory -Force -Path $publishedBackend | Out-Null
Copy-Item -LiteralPath (Join-Path $backend 'engine.mjs'), (Join-Path $backend 'packet.mjs'), (Join-Path $backend 'composition.mjs'), (Join-Path $backend 'package.json'), (Join-Path $backend 'package-lock.json') -Destination $publishedBackend -Force
Copy-Item -LiteralPath (Join-Path $backend 'node_modules') -Destination $publishedBackend -Recurse -Force
$runtime = Join-Path $publish 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Copy-Item -LiteralPath (Get-Command node.exe).Source -Destination (Join-Path $runtime 'node.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $publish -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'NOTICE.md'), (Join-Path $PSScriptRoot 'CLIPBOARD_FORMAT.md') -Destination $publish -Force
Write-Output "Built: $publish/DirtyMatrixPaster.exe"
