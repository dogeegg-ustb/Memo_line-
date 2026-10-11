$ErrorActionPreference = 'Stop'
$taskSource = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\publish\win-x64\recordings\记艺线测试.memoline'
$taskCoreRoot = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\source\bin\Release\net10.0-windows'
Add-Type -Path (Join-Path $taskCoreRoot 'System.IO.Hashing.dll')
Add-Type -Path (Join-Path $taskCoreRoot 'Memoline.Core.dll')
function Read-Index($archive) {
    $reader = [IO.StreamReader]::new($archive.GetEntry('index.json').Open())
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
}
function Export-Entry($archive, $info, [string]$destination) {
    $name = $info.entry + $(if ($info.codec -eq 'brotli') { '.br' } else { '' })
    $stored = $archive.GetEntry($name).Open()
    $decoded = if ($info.codec -eq 'brotli') { [IO.Compression.BrotliStream]::new($stored, [IO.Compression.CompressionMode]::Decompress) } else { $stored }
    $target = [IO.File]::Create($destination)
    try { $decoded.CopyTo($target) } finally { $target.Dispose(); $decoded.Dispose(); if ($decoded -ne $stored) { $stored.Dispose() } }
    $sha = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Item -LiteralPath $destination).Length -ne $info.size -or $sha -ne $info.sha256) { throw "Index verification failed: $name" }
}
function Read-Session($archive, $index, [int]$ordinal, [bool]$current) {
    $nativePath = Join-Path $PSScriptRoot ('native-' + $ordinal + '.memoline')
    $aggregatePath = Join-Path $PSScriptRoot ('aggregation-' + $ordinal + '.jsonl')
    Export-Entry $archive $index.mechanical $nativePath
    Export-Entry $archive $index.aggregation $aggregatePath
    $counts = @{}
    $penOperations = [Collections.Generic.HashSet[ulong]]::new()
    $taskReader = [BehaviorRecognizer.Storage.Memoline.MemolineReader]::Open($nativePath)
    try {
        while (-not $taskReader.IsComplete) {
            foreach ($frame in $taskReader.ReadAvailable()) {
                $record = $frame.GetRawText() | ConvertFrom-Json -Depth 100
                $counts[$record.kind]++
                if ($record.kind -eq 'header') { $header = $record }
                if ($record.kind -eq 'penBegin') { $penOperations.Add([ulong]$record.operationId) | Out-Null }
            }
        }
    } finally { $taskReader.Dispose() }
    $rows = @(Get-Content -LiteralPath $aggregatePath | ForEach-Object { $_ | ConvertFrom-Json -Depth 100 })
    $packets = @($rows | Where-Object kind -eq 'packet')
    $captured = @($packets | Where-Object status -eq 'captured')
    $pointers = @($packets | ForEach-Object { $_.eventPointers } | Where-Object { $_ })
    $matrices = @($packets | ForEach-Object { $_.dirtyMatrices } | Where-Object { $_ })
    $imageEntries = @($archive.Entries | Where-Object { $_.FullName -match '\.png(?:\.br)?$' })
    $result = [ordered]@{
        ordinal = $ordinal; isCurrent = $current; sessionId = $index.sessionId; createdAt = $header.createdAt
        penBegin = [int]$counts['penBegin']; penEnd = [int]$counts['penEnd']; uniquePenOperations = $penOperations.Count
        packets = $packets.Count; capturedPackets = $captured.Count; eventPointers = $pointers.Count
        dirtyMatrices = $matrices.Count; pngEntries = $imageEntries.Count; frameCounts = $counts
        mechanicalSha256 = $index.mechanical.sha256; aggregationSha256 = $index.aggregation.sha256
        hashesVerified = $true; nativeFramesCrcValidated = $true
    }
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot ('session-' + $ordinal + '.json')), ($result | ConvertTo-Json -Depth 8))
    return [pscustomobject]$result
}
$taskSummaries = @()
$taskArchive = [IO.Compression.ZipFile]::OpenRead($taskSource)
try {
    $taskIndex = Read-Index $taskArchive
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'index.json'), ($taskIndex | ConvertTo-Json -Depth 100))
    $ordinal = 0
    foreach ($prior in $taskIndex.previousSessions) {
        $bundlePath = Join-Path $PSScriptRoot ('session-' + $ordinal + '.memoline')
        Export-Entry $taskArchive $prior $bundlePath
        $priorArchive = [IO.Compression.ZipFile]::OpenRead($bundlePath)
        try {
            $priorIndex = Read-Index $priorArchive
            if ($priorIndex.previousSessions.Count -ne 0) { throw 'Nested history is unsupported.' }
            $taskSummaries += Read-Session $priorArchive $priorIndex $ordinal $false
        } finally { $priorArchive.Dispose() }
        # Delete only this script's generated private container, after verifying its exact report directory.
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($bundlePath)) -ne [IO.Path]::GetFullPath($PSScriptRoot)) { throw 'Temporary container path escaped report directory.' }
        [IO.File]::Delete($bundlePath)
        $ordinal++
    }
    $taskSummaries += Read-Session $taskArchive $taskIndex $ordinal $true
} finally { $taskArchive.Dispose() }
$summary = [ordered]@{
    source = $taskSource; inspectedAt = [DateTimeOffset]::Now.ToString('o'); projectPath = $taskIndex.projectPath
    previousSessions = $taskIndex.previousSessions.Count
    historicalPenBegin = ($taskSummaries | Where-Object { -not $_.isCurrent } | Measure-Object -Property penBegin -Sum).Sum
    currentPenBegin = ($taskSummaries | Where-Object isCurrent).penBegin; sessions = $taskSummaries
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'summary.json'), ($summary | ConvertTo-Json -Depth 10))
$taskSummaries | Select-Object ordinal,isCurrent,sessionId,createdAt,penBegin,penEnd,capturedPackets,eventPointers,dirtyMatrices,pngEntries | Format-Table -AutoSize
[pscustomobject]$summary | Select-Object source,projectPath,previousSessions,historicalPenBegin,currentPenBegin | Format-List
