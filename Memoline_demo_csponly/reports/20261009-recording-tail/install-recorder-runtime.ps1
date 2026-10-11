$ErrorActionPreference = 'Stop'
$demoRoot = [IO.Path]::GetFullPath('D:\Memo_Line\Memo_Line\Memoline_demo_csponly')
$demoPrefix = $demoRoot.TrimEnd('\') + '\'
$stageRoot = [IO.Path]::Combine($demoRoot, '.stage\recording-end-recognizer')
$targetRoot = [IO.Path]::Combine($demoRoot, 'publish\win-x64\Recognizer')
$backupRoot = [IO.Path]::Combine($demoRoot, '.stage\recording-end-runtime-backup-' + [Guid]::NewGuid().ToString('N'))
$reportPath = [IO.Path]::Combine($demoRoot, 'reports\20261009-recording-tail\installed-recorder-runtime.json')
function Assert-DemoPath([string] $path) {
    if (-not [IO.Path]::GetFullPath($path).StartsWith($demoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime update path escaped the demo directory: $path"
    }
}
foreach ($path in @($stageRoot, $targetRoot, $backupRoot, $reportPath)) { Assert-DemoPath $path }
if (-not [IO.File]::Exists([IO.Path]::Combine($stageRoot, 'BehaviorRecognizer.dll'))) {
    throw 'Verified recorder publish output is missing.'
}
[IO.Directory]::CreateDirectory($backupRoot) | Out-Null
$installed = @()
# Install published runtime files and application Python code. User settings,
# calibration, extra Python libraries and recorded data are excluded. The two
# bundled read-only catalogs belong to the application version, not user settings.
$runtimeFiles = @(Get-ChildItem -LiteralPath $stageRoot -File | Where-Object {
    $_.Extension -in @('.dll', '.exe', '.json', '.pdb')
})
$applicationCode = @(Get-ChildItem -LiteralPath ([IO.Path]::Combine($stageRoot, 'integration')) -File -Recurse -Filter '*.py' |
    Where-Object { $_.FullName -notmatch '[\\/]__pycache__[\\/]' })
$catalogFiles = @(
    Get-Item -LiteralPath ([IO.Path]::Combine($stageRoot, 'integration\recognizer_core\layer_state\data\layer_catalog.json'))
    Get-Item -LiteralPath ([IO.Path]::Combine($stageRoot, 'integration\recognizer_core\panel_state\data\properties.sqlite3'))
)
foreach ($file in $runtimeFiles + $applicationCode + $catalogFiles) {
    $relative = [IO.Path]::GetRelativePath($stageRoot, $file.FullName)
    $destination = [IO.Path]::Combine($targetRoot, $relative)
    $backup = [IO.Path]::Combine($backupRoot, 'before', $relative)
    $displaced = [IO.Path]::Combine($backupRoot, 'running', $relative)
    $staged = [IO.Path]::Combine([IO.Path]::GetDirectoryName($destination), '.' + $file.Name + '.tail-fix-' + [Guid]::NewGuid().ToString('N'))
    foreach ($path in @($file.FullName, $destination, $backup, $displaced, $staged)) { Assert-DemoPath $path }
    $publishedHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $previousHash = $null
    if ([IO.File]::Exists($destination)) {
        $previousHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($previousHash -eq $publishedHash) { continue }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
        [IO.File]::Copy($destination, $backup, $false)
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($file.FullName, $staged, $false)
    try { [IO.File]::Move($staged, $destination, $true) }
    catch [IO.IOException], [UnauthorizedAccessException] {
        if (-not [IO.File]::Exists($destination)) { throw }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($displaced)) | Out-Null
        [IO.File]::Move($destination, $displaced, $false)
        try { [IO.File]::Move($staged, $destination, $false) }
        catch {
            if (-not [IO.File]::Exists($destination)) { [IO.File]::Move($displaced, $destination, $false) }
            throw
        }
    }
    $installedHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($installedHash -ne $publishedHash) { throw "Installed runtime hash mismatch: $destination" }
    $installed += [ordered]@{
        path = $destination; beforeSha256 = $previousHash; sha256 = $installedHash; backup = $backup
    }
}
$result = [ordered]@{
    installedAt = [DateTimeOffset]::Now.ToString('o'); source = $stageRoot; files = $installed
}
$json = $result | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($reportPath, $json, [Text.UTF8Encoding]::new($false))
$json
