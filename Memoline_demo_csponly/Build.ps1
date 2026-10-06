param(
    [string]$RecognizerPackage,
    [switch]$SkipChecks,
    [switch]$RebuildBridge
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$watcher = Join-Path $repository 'Organizer/canvas layer watcher'
if (-not $RecognizerPackage) {
    $RecognizerPackage = Join-Path $repository 'recognizer/Recognizer/publish/win-x64-injected-input-evidence'
}
$RecognizerPackage = (Resolve-Path -LiteralPath $RecognizerPackage).Path
if (-not (Test-Path -LiteralPath (Join-Path $RecognizerPackage 'BehaviorRecognizer.exe'))) {
    throw 'RecognizerPackage must contain BehaviorRecognizer.exe and its complete integration runtime.'
}
$bridge = Join-Path $watcher 'bridge/target/release/clip-layer-bridge.exe'
if ($RebuildBridge) {
    $portableCargo = Join-Path $watcher 'tools/cargo/bin/cargo.exe'
    if (Test-Path -LiteralPath $portableCargo) {
        $env:RUSTUP_HOME = Join-Path $watcher 'tools/rustup'
        $env:CARGO_HOME = Join-Path $watcher 'tools/cargo'
        $env:PATH = "$env:CARGO_HOME\bin;$env:PATH"
    }
    cargo build --locked --release --manifest-path (Join-Path $watcher 'bridge/Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw 'clip-layer-bridge build failed' }
}
if (-not (Test-Path -LiteralPath $bridge)) {
    $bridge = Join-Path $watcher 'publish/win-x64/clip-layer-bridge.exe'
}
if (-not (Test-Path -LiteralPath $bridge)) { throw 'Build the canvas layer watcher bridge first, or use -RebuildBridge.' }
if (-not $SkipChecks) {
    dotnet build (Join-Path $PSScriptRoot 'checks/MemolineDemo.Checks.csproj') -c Release -m:1 -p:UseSharedCompilation=false -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Memoline integration checks build failed' }
    dotnet run --project (Join-Path $PSScriptRoot 'checks/MemolineDemo.Checks.csproj') -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Memoline integration checks failed' }
}
$output = Join-Path $PSScriptRoot 'publish/win-x64'
dotnet publish (Join-Path $PSScriptRoot 'source/MemolineDemo.csproj') -c Release -r win-x64 --self-contained true -o $output -m:1 -p:UseSharedCompilation=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -nr:false
if ($LASTEXITCODE -ne 0) { throw 'MemolineDemo publish failed' }
Copy-Item -LiteralPath $bridge -Destination $output -Force
foreach ($name in @('README.md', 'FORMAT.md', 'INTERFACES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $output -Force
}
foreach ($name in @('NOTICE.md', 'DIFF_FORMAT.md')) {
    Copy-Item -LiteralPath (Join-Path $watcher $name) -Destination $output -Force
}
$licenses = Join-Path $watcher 'publish/win-x64/third-party-licenses'
if (Test-Path -LiteralPath $licenses) {
    Copy-Item -LiteralPath $licenses -Destination $output -Recurse -Force
}
# Bundle a private Recognizer without copying historical recordings. On a
# rebuild, keep this demo's calibrated settings and device configuration.
$recognizerOutput = Join-Path $output 'Recognizer'
New-Item -ItemType Directory -Path $recognizerOutput -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $RecognizerPackage -File -Recurse) {
    $relative = [System.IO.Path]::GetRelativePath($RecognizerPackage, $file.FullName)
    if ($relative -match '^(procedure[\\/])|([\\/]__pycache__[\\/])' -or $file.Extension -eq '.log') { continue }
    $destination = Join-Path $recognizerOutput $relative
    if ((Test-Path -LiteralPath $destination) -and
        ($relative -eq 'integration\settings.json' -or $relative -eq 'integration/settings.json' -or $relative -match '^config[\\/]')) { continue }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
# Compile the native recorder as well. The package supplies Python/native
# dependencies; source changes must never silently run an older recorder DLL.
$recorderStage = Join-Path $PSScriptRoot '.stage/recognizer'
dotnet publish (Join-Path $repository 'recognizer/Recognizer/src/BehaviorRecognizer/BehaviorRecognizer.csproj') -c Release -r win-x64 --self-contained true -o $recorderStage -m:1 -p:UseSharedCompilation=false -nr:false
if ($LASTEXITCODE -ne 0) { throw 'Integrated Recognizer publish failed' }
foreach ($file in Get-ChildItem -LiteralPath $recorderStage -File -Recurse) {
    $relative = [System.IO.Path]::GetRelativePath($recorderStage, $file.FullName)
    if ($relative -eq 'integration\settings.json' -or $relative -eq 'integration/settings.json' -or $relative -match '^config[\\/]') { continue }
    $destination = Join-Path $recognizerOutput $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
$entryPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Memoline.exe'))
$entryStage = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('.stage/entry-' + [Guid]::NewGuid().ToString('N') + '.exe')))
$entryBackup = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('.stage/previous-entry/Memoline-' + [Guid]::NewGuid().ToString('N') + '.exe')))
$entryRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
foreach ($entryTarget in @($entryPath, $entryStage, $entryBackup)) {
    if (-not $entryTarget.StartsWith($entryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Executable update path is outside the demo.' }
}
Copy-Item -LiteralPath (Join-Path $output 'MemolineDemo.exe') -Destination $entryStage -ErrorAction Stop
try {
    try { [System.IO.File]::Move($entryStage, $entryPath, $true) }
    catch [System.IO.IOException], [System.UnauthorizedAccessException] {
        # A running entry cannot be overwritten on Windows, but can be renamed.
        # Keep that loaded image, and atomically install the already complete new one.
        if (-not (Test-Path -LiteralPath $entryPath)) { throw }
        New-Item -ItemType Directory -Path (Split-Path -Parent $entryBackup) -Force | Out-Null
        Move-Item -LiteralPath $entryPath -Destination $entryBackup -ErrorAction Stop
        try { [System.IO.File]::Move($entryStage, $entryPath) }
        catch { Move-Item -LiteralPath $entryBackup -Destination $entryPath -ErrorAction Stop; throw }
    }
}
finally { if (Test-Path -LiteralPath $entryStage) { Remove-Item -LiteralPath $entryStage -Force } }
Copy-Item -LiteralPath $bridge -Destination (Join-Path $PSScriptRoot 'clip-layer-bridge.exe') -Force
Write-Host "Published: $PSScriptRoot\Memoline.exe"
