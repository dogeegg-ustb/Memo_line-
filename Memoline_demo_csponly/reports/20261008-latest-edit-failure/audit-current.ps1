$ErrorActionPreference = 'Stop'
$taskSource = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\publish\win-x64\recordings\记艺线测试.memoline'
$taskCoreRoot = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\source\bin\Release\net10.0-windows'
Add-Type -Path (Join-Path $taskCoreRoot 'System.IO.Hashing.dll')
Add-Type -Path (Join-Path $taskCoreRoot 'Memoline.Core.dll')

function Export-Entry($archive, $info, [string]$destination) {
    $physical = $info.entry + $(if ($info.codec -eq 'brotli') { '.br' } else { '' })
    $entry = $archive.GetEntry($physical)
    if ($null -eq $entry) { throw "Missing entry: $physical" }
    $stored = $entry.Open()
    $decoded = if ($info.codec -eq 'brotli') { [System.IO.Compression.BrotliStream]::new($stored, [System.IO.Compression.CompressionMode]::Decompress) } else { $stored }
    $target = [System.IO.File]::Create($destination)
    try { $decoded.CopyTo($target) } finally { $target.Dispose(); $decoded.Dispose(); if ($decoded -ne $stored) { $stored.Dispose() } }
    $read = [System.IO.File]::OpenRead($destination)
    try { $sha = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($read)).ToLowerInvariant() } finally { $read.Dispose() }
    if ((Get-Item -LiteralPath $destination).Length -ne $info.size -or $sha -ne $info.sha256) { throw "Index verification failed: $physical" }
    return $sha
}

$taskArchive = [System.IO.Compression.ZipFile]::OpenRead($taskSource)
try {
    $taskIndexReader = [System.IO.StreamReader]::new($taskArchive.GetEntry('index.json').Open())
    try { $taskIndexText = $taskIndexReader.ReadToEnd() } finally { $taskIndexReader.Dispose() }
    $taskIndex = $taskIndexText | ConvertFrom-Json
    [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'index.json'), $taskIndexText)
    $taskMechanical = Join-Path $PSScriptRoot 'current-native.memoline'
    $taskAggregate = Join-Path $PSScriptRoot 'current-aggregation.jsonl'
    $taskMechanicalHash = Export-Entry $taskArchive $taskIndex.mechanical $taskMechanical
    $taskAggregateHash = Export-Entry $taskArchive $taskIndex.aggregation $taskAggregate
} finally { $taskArchive.Dispose() }

$taskCounts = @{}
$taskModuleCounts = @{}
$taskCores = [System.Collections.Generic.List[object]]::new()
$taskPackages = [System.Collections.Generic.List[object]]::new()
$taskBoundaries = [System.Collections.Generic.List[object]]::new()
$taskSelected = [System.Collections.Generic.List[string]]::new()
$taskWriter = [System.IO.StreamWriter]::new((Join-Path $PSScriptRoot 'current-native-full.jsonl'), $false, [System.Text.UTF8Encoding]::new($false))
$taskReader = [BehaviorRecognizer.Storage.Memoline.MemolineReader]::Open($taskMechanical)
try {
    while (-not $taskReader.IsComplete) {
        foreach ($frame in $taskReader.ReadAvailable()) {
            $raw = $frame.GetRawText()
            $taskWriter.WriteLine($raw)
            $record = $raw | ConvertFrom-Json -Depth 100
            $taskCounts[$record.kind]++
            if ($record.kind -eq 'header') { $taskHeader = $record }
            if ($record.kind -eq 'footer') { $taskFooter = $record }
            if ($record.kind -eq 'coreStateUpdated') {
                $key = $record.data.module + '/' + $record.data.status
                $taskModuleCounts[$key]++
                $taskCores.Add([ordered]@{appendId=$record.appendId; ticks=$record.ticks; module=$record.data.module; status=$record.data.status; state=$record.data.state; error=$record.data.error; evidence=$record.data.evidence})
                $taskSelected.Add($raw)
            }
            if ($record.kind -eq 'statePackageResult') {
                $taskPackages.Add([ordered]@{appendId=$record.appendId; ticks=$record.ticks; data=$record.data})
                $taskSelected.Add($raw)
            }
            if ($record.kind -in @('editBoundaryRequested','recordingEndRequested','initializationStatus','initializationConfiguration','workspaceStatus','clipSaveRequest','saveGuardResult')) {
                if ($record.kind -in @('editBoundaryRequested','recordingEndRequested','initializationStatus')) { $taskBoundaries.Add($record) }
                $taskSelected.Add($raw)
            }
        }
    }
} finally { $taskReader.Dispose(); $taskWriter.Dispose() }
[System.IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'current-state-evidence.jsonl'), $taskSelected, [System.Text.UTF8Encoding]::new($false))
$taskRows = @(Get-Content -LiteralPath $taskAggregate | ForEach-Object { $_ | ConvertFrom-Json -Depth 100 })
$taskPackets = @($taskRows | Where-Object kind -eq 'packet' | ForEach-Object { [ordered]@{packetId=$_.packetId;boundaryKind=$_.boundaryKind;fromTicks=$_.fromTicks;toTicks=$_.toTicks;observationAppendId=$_.observationAppendId;status=$_.status;reason=$_.reason;eventPointerCount=$_.eventPointers.Count;dirtyMatrixCount=$_.dirtyMatrices.Count;manifest=$_.manifest} })
$taskSummary = [ordered]@{
    source=$taskSource; sessionId=$taskIndex.sessionId; historicalSessions=$taskIndex.previousSessions.Count;
    mechanicalSha256=$taskMechanicalHash; aggregationSha256=$taskAggregateHash; indexHashesMatch=$true;
    nativeFramesCrcValidated=$true; header=$taskHeader; footer=$taskFooter; kindCounts=$taskCounts;
    coreCounts=$taskModuleCounts; coreUpdates=$taskCores; packageResults=$taskPackages;
    boundaries=$taskBoundaries; aggregationKinds=@($taskRows | Group-Object kind | ForEach-Object { [ordered]@{kind=$_.Name;count=$_.Count} }); packets=$taskPackets
}
$taskSummaryText = $taskSummary | ConvertTo-Json -Depth 100
[System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'current-summary.json'), $taskSummaryText)
[ordered]@{sessionId=$taskIndex.sessionId;header=$taskHeader;footer=$taskFooter;kindCounts=$taskCounts;coreCounts=$taskModuleCounts;boundaries=$taskBoundaries;packets=$taskPackets;hashesMatch=$true} | ConvertTo-Json -Depth 15
