$ErrorActionPreference = 'Stop'
$taskAssemblyRoot = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\source\bin\Release\net10.0-windows'
$taskReport = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'reader-summary.json') -Raw | ConvertFrom-Json -Depth 30
$taskSessionRoot = Join-Path $taskReport.cache 'session-1'
Add-Type -Path (Join-Path $taskAssemblyRoot 'System.IO.Hashing.dll')
Add-Type -Path (Join-Path $taskAssemblyRoot 'Memoline.Core.dll')
Add-Type -Path (Join-Path $taskAssemblyRoot 'MemolineDemo.dll')
$taskAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $taskAssemblyRoot 'MemolineDemo.dll'))
$taskRead = $taskAssembly.GetType('MemolineDemo.Editing.LiveSessionReader').GetMethod('Read',[Reflection.BindingFlags]'Static,NonPublic')
$taskMechanical = Join-Path $taskSessionRoot 'recording.memoline'
$taskDiff = Join-Path $taskSessionRoot 'diff'
$taskArgs = [object[]]::new(4)
$taskArgs[0] = $taskMechanical.PSObject.BaseObject
$taskArgs[1] = $taskDiff.PSObject.BaseObject
$taskArgs[2] = [ulong]0
$taskArgs[3] = [Threading.CancellationToken]::None
$taskSession = $taskRead.Invoke($null,$taskArgs)
$taskCatalogFrame = $taskSession.Frames | Where-Object { $_.GetProperty('kind').GetString() -eq 'shortcutConfiguration' } | Select-Object -First 1
$taskCatalog = $taskCatalogFrame.GetProperty('data').GetRawText() | ConvertFrom-Json -Depth 100
$taskToolCatalog = $taskCatalog.toolCatalog
$taskChild = @($taskToolCatalog.nodes | Where-Object { $_.kind -eq 'subtool' -and $_.name -eq 'G筆' })
if ($taskChild.Count -ne 1) { throw 'Recorded G pen node is ambiguous.' }
$taskIds = [Collections.Generic.HashSet[string]]::new()
$taskNode = $taskChild[0]
while ($taskNode) {
    [void]$taskIds.Add($taskNode.id)
    if (-not $taskNode.parentId) { break }
    $taskParentId = $taskNode.parentId
    $taskNode = $taskToolCatalog.nodes | Where-Object id -eq $taskParentId | Select-Object -First 1
}
$taskToolCatalog.nodes = @($taskToolCatalog.nodes | Where-Object { $taskIds.Contains($_.id) })
$taskCatalogJson = $taskCatalog | ConvertTo-Json -Depth 100 -Compress
$taskBrushFrame = $taskSession.Frames | Where-Object {
    if ($_.GetProperty('kind').GetString() -ne 'coreStateUpdated') { return $false }
    $taskData = $_.GetProperty('data')
    return $taskData.GetProperty('module').GetString() -eq 'brushState' -and $taskData.GetProperty('state').GetProperty('name').GetString() -eq 'G筆'
} | Select-Object -First 1
if (-not $taskBrushFrame) { throw 'No actual recorded G pen brush core frame found.' }
$taskBrushJson = $taskBrushFrame.GetProperty('data').GetRawText()
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'recorded-brush-catalog.json'),$taskCatalogJson)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'recorded-brush-state.json'),$taskBrushJson)
[pscustomobject]@{ nativeSession = $taskSession.SessionId; catalogAppendId = $taskCatalogFrame.GetProperty('appendId').GetUInt64(); brushAppendId = $taskBrushFrame.GetProperty('appendId').GetUInt64(); nodes = $taskToolCatalog.nodes; catalogLength = $taskCatalogJson.Length; brushLength = $taskBrushJson.Length } | ConvertTo-Json -Depth 6
