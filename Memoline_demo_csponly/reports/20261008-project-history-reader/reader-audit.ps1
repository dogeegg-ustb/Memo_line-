$ErrorActionPreference = 'Stop'
$taskSource = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\publish\win-x64\recordings\记艺线测试.memoline'
$taskAssemblyRoot = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\source\bin\Release\net10.0-windows'
$taskCache = Join-Path $PSScriptRoot ('reader-cache-' + [Guid]::NewGuid().ToString('N'))
$taskSourceBefore = (Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash
Add-Type -Path (Join-Path $taskAssemblyRoot 'System.IO.Hashing.dll')
Add-Type -Path (Join-Path $taskAssemblyRoot 'Memoline.Core.dll')
Add-Type -Path (Join-Path $taskAssemblyRoot 'MemolineDemo.dll')
Add-Type -TypeDefinition @'
public static class HistoryPixelAudit {
    public static long Difference(byte[] first, byte[] second) {
        if (first == null || second == null || first.Length != second.Length) return -1;
        long count = 0;
        for (int i = 0; i < first.Length; i += 4)
            if (first[i] != second[i] || first[i+1] != second[i+1] || first[i+2] != second[i+2] || first[i+3] != second[i+3]) count++;
        return count;
    }
    public static string Hash(byte[] bytes) {
        return bytes == null ? null : System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
'@
$taskAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $taskAssemblyRoot 'MemolineDemo.dll'))
$taskFlags = [Reflection.BindingFlags]'Static,NonPublic'
$taskRead = $taskAssembly.GetType('MemolineDemo.Editing.ProjectSessionReader').GetMethod('ReadProject', $taskFlags)
$taskBuild = $taskAssembly.GetType('MemolineDemo.Editing.EditIndexBuilder').GetMethod('Build', $taskFlags)
$taskTimer = [Diagnostics.Stopwatch]::StartNew()
try {
    $taskArguments = [object[]]::new(3)
    $taskArguments[0] = $taskSource.PSObject.BaseObject
    $taskArguments[1] = $taskCache.PSObject.BaseObject
    $taskArguments[2] = [Threading.CancellationToken]::None
    $taskSessions = $taskRead.Invoke($null, $taskArguments)
} catch {
    $taskFailure = $_.Exception
    while ($taskFailure.InnerException) { $taskFailure = $taskFailure.InnerException }
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'reader-failure.txt'), $taskFailure.ToString())
    throw $taskFailure
}
$taskReadSeconds = $taskTimer.Elapsed.TotalSeconds
$taskResults = @()
$taskLayerTimeline = @{}
foreach ($taskSession in $taskSessions) {
    $taskIndex = $taskBuild.Invoke($null, [object[]]@($taskSession.PSObject.BaseObject))
    $taskLayers = @()
    foreach ($taskLayer in $taskIndex.Layers.Values) {
        $taskPackets = @($taskLayer.Packets | Sort-Object TriggerTicks)
        $taskBaseline = ($taskPackets | Where-Object { $null -ne $_.Before } | Select-Object -First 1).Before
        $taskFinal = ($taskPackets | Where-Object { $null -ne $_.Current } | Select-Object -Last 1).Current
        $taskUuid = $taskLayer.Layer.Uuid
        $taskPrevious = $taskLayerTimeline[$taskUuid]
        $taskDifference = if ($taskPrevious -and $taskBaseline) { [HistoryPixelAudit]::Difference($taskPrevious.Bytes, $taskBaseline.Bytes) } else { $null }
        $taskLayers += [ordered]@{
            sourceLayer = $taskLayer.Layer.ToString(); packetCount = $taskPackets.Count
            sourceStrokeCount = $taskLayer.Strokes.Count; chainValidWithinSession = $taskLayer.ChainValid
            baselinePresent = $null -ne $taskBaseline; finalPresent = $null -ne $taskFinal
            firstCaptureTicks = $taskPackets[0].TriggerTicks; finalCaptureTicks = $taskPackets[-1].TriggerTicks
            finalIsBoundary = $taskPackets[-1].IsBoundary
            baselineRgbaSha256 = if ($taskBaseline) { [HistoryPixelAudit]::Hash($taskBaseline.Bytes) } else { $null }
            finalRgbaSha256 = if ($taskFinal) { [HistoryPixelAudit]::Hash($taskFinal.Bytes) } else { $null }
            differencePixelsFromPreviousFinalToThisBaseline = $taskDifference
            issues = @($taskLayer.Issues | ForEach-Object { [ordered]@{ code = $_.Code.ToString(); message = $_.Message } })
        }
        if ($taskFinal) { $taskLayerTimeline[$taskUuid] = $taskFinal }
    }
    $taskPenCount = @($taskSession.Frames | Where-Object { $_.GetProperty('kind').GetString() -eq 'penBegin' }).Count
    $taskResults += [ordered]@{
        sessionId = $taskSession.SessionId; frequency = $taskSession.Frequency
        boundaryAppendId = $taskSession.BoundaryAppendId; boundaryTicks = $taskSession.BoundaryTicks
        rawPenBegin = $taskPenCount; indexedStrokes = $taskIndex.Strokes.Count
        indexedImages = $taskIndex.Images.Count; layers = $taskLayers
        nativeMechanicalSha256 = (Get-FileHash -LiteralPath $taskSession.MechanicalPath -Algorithm SHA256).Hash
        issues = @($taskIndex.Issues | ForEach-Object { [ordered]@{ code = $_.Code.ToString(); message = $_.Message } })
    }
    Write-Host ('Session ' + $taskSession.SessionId + ': rawPen=' + $taskPenCount + ', indexed=' + $taskIndex.Strokes.Count + ', images=' + $taskIndex.Images.Count)
}
$taskSourceAfter = (Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash
if ($taskSourceBefore -ne $taskSourceAfter) { throw 'Sealed input changed during diagnostic.' }
$taskReport = [ordered]@{
    source = $taskSource; sourceSha256 = $taskSourceBefore; sourceUnchanged = $true
    cache = $taskCache; readSeconds = $taskReadSeconds; totalSeconds = $taskTimer.Elapsed.TotalSeconds
    sessions = $taskResults
    note = 'A historical Before reconstructed by reversing complete difference patches is derived evidence; it is not an independent recording-start full snapshot. Cross-session equality compares that baseline with the preceding captured full layer.'
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'reader-summary.json'), ($taskReport | ConvertTo-Json -Depth 16))
$taskResults | ForEach-Object { [pscustomobject]@{ sessionId = $_.sessionId; rawPen = $_.rawPenBegin; indexed = $_.indexedStrokes; images = $_.indexedImages; layers = $_.layers.Count } } | Format-Table
Write-Host ('Saved reader summary; native sessions=' + $taskSessions.Count + ', readerSeconds=' + [Math]::Round($taskReadSeconds, 2))
