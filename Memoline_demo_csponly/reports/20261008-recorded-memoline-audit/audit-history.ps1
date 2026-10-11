$ErrorActionPreference = 'Stop'
$taskSource = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\publish\win-x64\recordings\记艺线测试.memoline'
$taskCoreRoot = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\source\bin\Release\net10.0-windows'
Add-Type -Path (Join-Path $taskCoreRoot 'System.IO.Hashing.dll')
Add-Type -Path (Join-Path $taskCoreRoot 'Memoline.Core.dll')

function Read-Index($archive) {
    $reader = [System.IO.StreamReader]::new($archive.GetEntry('index.json').Open())
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
}
function Copy-Entry($archive, $info, [string]$destination) {
    $physical = $info.entry + $(if ($info.codec -eq 'brotli') { '.br' } else { '' })
    $stored = $archive.GetEntry($physical).Open()
    $decoded = if ($info.codec -eq 'brotli') { [System.IO.Compression.BrotliStream]::new($stored, [System.IO.Compression.CompressionMode]::Decompress) } else { $stored }
    $target = [System.IO.File]::Create($destination)
    try { $decoded.CopyTo($target) } finally { $target.Dispose(); $decoded.Dispose(); if ($decoded -ne $stored) { $stored.Dispose() } }
    $read = [System.IO.File]::OpenRead($destination)
    try { $sha = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($read)).ToLowerInvariant() } finally { $read.Dispose() }
    if ((Get-Item -LiteralPath $destination).Length -ne $info.size -or $sha -ne $info.sha256) { throw "Index verification failed: $physical" }
}

$taskSummaries = [System.Collections.Generic.List[object]]::new()
$taskArchive = [System.IO.Compression.ZipFile]::OpenRead($taskSource)
try {
    $taskIndex = Read-Index $taskArchive
    $taskOrdinal = 0
    foreach ($taskPrevious in $taskIndex.previousSessions) {
        $taskPrefix = 'history-' + $taskOrdinal
        $taskBundlePath = Join-Path ([System.IO.Path]::GetTempPath()) ('memoline-recorded-audit-' + [Guid]::NewGuid().ToString('N') + '.zip')
        $taskNativePath = $taskBundlePath + '.memoline'
        $taskPriorArchive = $null
        $taskNativeReader = $null
        $taskEvidenceWriter = $null
        try {
            Copy-Entry $taskArchive $taskPrevious $taskBundlePath
            $taskPriorArchive = [System.IO.Compression.ZipFile]::OpenRead($taskBundlePath)
            $taskPriorIndex = Read-Index $taskPriorArchive
            Copy-Entry $taskPriorArchive $taskPriorIndex.mechanical $taskNativePath
            $taskCounts = @{}
            $taskCoreCounts = @{}
            $taskCanvas = [System.Collections.Generic.List[object]]::new()
            $taskNativeReader = [BehaviorRecognizer.Storage.Memoline.MemolineReader]::Open($taskNativePath)
            $taskEvidenceWriter = [System.IO.StreamWriter]::new((Join-Path $PSScriptRoot ($taskPrefix + '-canvas-evidence.jsonl')), $false, [System.Text.UTF8Encoding]::new($false))
            while (-not $taskNativeReader.IsComplete) {
                foreach ($frame in $taskNativeReader.ReadAvailable()) {
                    $record = $frame.GetRawText() | ConvertFrom-Json -Depth 100
                    $taskCounts[$record.kind]++
                    if ($record.kind -eq 'header') { $taskHeader = $record }
                    if ($record.kind -eq 'footer') { $taskFooter = $record }
                    if ($record.kind -eq 'coreStateUpdated') {
                        $taskCoreCounts[($record.data.module + '/' + $record.data.status)]++
                        if ($record.data.module -eq 'canvasViewState') {
                            $taskEvidenceWriter.WriteLine($frame.GetRawText())
                            $taskCanvas.Add([ordered]@{appendId=$record.appendId;ticks=$record.ticks;status=$record.data.status;state=$record.data.state;error=$record.data.error;evidence=$record.data.evidence})
                        }
                    }
                }
            }
            $taskSummary = [ordered]@{ordinal=$taskOrdinal;entry=$taskPrevious.entry;sessionId=$taskPriorIndex.sessionId;createdAt=$taskHeader.createdAt;frequency=$taskHeader.frequency;durationSeconds=$taskFooter.ticks/$taskHeader.frequency;statePersistence=$taskHeader.statePersistence;mechanicalSha256=$taskPriorIndex.mechanical.sha256;indexHashesMatch=$true;nativeFramesCrcValidated=$true;kindCounts=$taskCounts;coreCounts=$taskCoreCounts;canvasUpdates=$taskCanvas}
            [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot ($taskPrefix + '-summary.json')), ($taskSummary | ConvertTo-Json -Depth 100))
            $taskSummaries.Add([ordered]@{ordinal=$taskOrdinal;sessionId=$taskPriorIndex.sessionId;createdAt=$taskHeader.createdAt;durationSeconds=$taskFooter.ticks/$taskHeader.frequency;statePersistence=$taskHeader.statePersistence;coreCounts=$taskCoreCounts;confirmedCanvasUpdates=@($taskCanvas | Where-Object status -in @('changed','unchanged')).Count;firstCanvas=$taskCanvas | Select-Object -First 1;lastCanvas=$taskCanvas | Select-Object -Last 1;hashesMatch=$true;nativeFramesCrcValidated=$true})
            Write-Output ($taskSummaries[$taskSummaries.Count-1] | ConvertTo-Json -Depth 8)
        } finally {
            if ($taskEvidenceWriter) { $taskEvidenceWriter.Dispose() }
            if ($taskNativeReader) { $taskNativeReader.Dispose() }
            if ($taskPriorArchive) { $taskPriorArchive.Dispose() }
            if (Test-Path -LiteralPath $taskNativePath) { Remove-Item -LiteralPath $taskNativePath }
            if (Test-Path -LiteralPath $taskBundlePath) { Remove-Item -LiteralPath $taskBundlePath }
        }
        $taskOrdinal++
    }
} finally { $taskArchive.Dispose() }
[System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'history-summary.json'), ($taskSummaries | ConvertTo-Json -Depth 100))
