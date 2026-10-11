$ErrorActionPreference = 'Stop'
$demoRoot = [IO.Path]::GetFullPath('D:\Memo_Line\Memo_Line\Memoline_demo_csponly')
$rootPrefix = $demoRoot.TrimEnd('\') + '\'
$buildExe = [IO.Path]::GetFullPath([IO.Path]::Combine($demoRoot, '.stage\live-canvas-fix-publish\MemolineDemo.exe'))
$backupRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($demoRoot, '.stage\live-canvas-fix-backup-' + [Guid]::NewGuid().ToString('N')))
$reportPath = [IO.Path]::GetFullPath([IO.Path]::Combine($demoRoot, 'reports\20261008-latest-edit-failure\installed-build.json'))
$destinations = @(
    [IO.Path]::GetFullPath([IO.Path]::Combine($demoRoot, 'Memoline.exe')),
    [IO.Path]::GetFullPath([IO.Path]::Combine($demoRoot, 'publish\win-x64\MemolineDemo.exe'))
)
foreach ($path in @($buildExe, $backupRoot, $reportPath) + $destinations) {
    if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped the intended demo directory: $path"
    }
}
if (-not [IO.File]::Exists($buildExe)) { throw 'Published executable is missing.' }
[IO.Directory]::CreateDirectory($backupRoot) | Out-Null
$buildHash = (Get-FileHash -LiteralPath $buildExe -Algorithm SHA256).Hash
$records = @()
foreach ($destination in $destinations) {
    $leaf = [IO.Path]::GetFileName($destination)
    $backup = [IO.Path]::Combine($backupRoot, 'before-' + $leaf)
    $displaced = [IO.Path]::Combine($backupRoot, 'running-' + $leaf)
    $staged = [IO.Path]::Combine([IO.Path]::GetDirectoryName($destination), '.' + $leaf + '.canvas-fix-' + [Guid]::NewGuid().ToString('N'))
    foreach ($path in @($backup, $displaced, $staged)) {
        if (-not [IO.Path]::GetFullPath($path).StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Replacement path escaped the intended demo directory: $path"
        }
    }
    $beforeHash = $null
    if ([IO.File]::Exists($destination)) {
        $beforeHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        [IO.File]::Copy($destination, $backup, $false)
    }
    [IO.File]::Copy($buildExe, $staged, $false)
    try {
        [IO.File]::Move($staged, $destination, $true)
    } catch [IO.IOException] {
        if (-not [IO.File]::Exists($destination)) { throw }
        # Windows may keep a running executable open. Keep it recoverable without stopping it.
        [IO.File]::Move($destination, $displaced, $false)
        try { [IO.File]::Move($staged, $destination, $false) }
        catch {
            if (-not [IO.File]::Exists($destination)) { [IO.File]::Move($displaced, $destination, $false) }
            throw
        }
    }
    $afterHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($afterHash -ne $buildHash) { throw "Installed hash does not match the publish output: $destination" }
    $records += [ordered]@{ path = $destination; beforeSha256 = $beforeHash; sha256 = $afterHash; backup = $backup }
}
$result = [ordered]@{
    installedAt = [DateTimeOffset]::Now.ToString('o')
    build = $buildExe
    sha256 = $buildHash
    files = $records
}
$json = $result | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($reportPath, $json, [Text.UTF8Encoding]::new($false))
$json
