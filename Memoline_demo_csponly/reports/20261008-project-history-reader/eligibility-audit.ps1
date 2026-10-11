$ErrorActionPreference = 'Stop'
$taskAssemblyRoot = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\source\bin\Release\net10.0-windows'
$taskReport = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'reader-summary.json') -Raw | ConvertFrom-Json -Depth 30
$taskSessionRoot = Join-Path $taskReport.cache 'session-1'
Add-Type -Path (Join-Path $taskAssemblyRoot 'System.IO.Hashing.dll')
Add-Type -Path (Join-Path $taskAssemblyRoot 'Memoline.Core.dll')
Add-Type -Path (Join-Path $taskAssemblyRoot 'MemolineDemo.dll')
$taskReferences = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
Add-Type -Path (Join-Path $PSScriptRoot 'EligibilityAudit.cs') -ReferencedAssemblies $taskReferences
$taskAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $taskAssemblyRoot 'MemolineDemo.dll'))
$taskFlags = [Reflection.BindingFlags]'Static,NonPublic'
$taskRead = $taskAssembly.GetType('MemolineDemo.Editing.LiveSessionReader').GetMethod('Read',$taskFlags)
$taskBuild = $taskAssembly.GetType('MemolineDemo.Editing.EditIndexBuilder').GetMethod('Build',$taskFlags)
$taskMechanical = Join-Path $taskSessionRoot 'recording.memoline'
$taskDiff = Join-Path $taskSessionRoot 'diff'
$taskArgs = [object[]]::new(4)
$taskArgs[0] = $taskMechanical.PSObject.BaseObject
$taskArgs[1] = $taskDiff.PSObject.BaseObject
$taskArgs[2] = [ulong]0
$taskArgs[3] = [Threading.CancellationToken]::None
$taskSession = $taskRead.Invoke($null,$taskArgs)
$taskIndex = $taskBuild.Invoke($null,[object[]]@($taskSession.PSObject.BaseObject))
$taskQuick = [ordered]@{
    strokes = @($taskIndex.Strokes.Values | ForEach-Object { [ordered]@{
        key = $_.Key.ToString(); beginAppendId = $_.Key.BeginAppendId; beginEventId = $_.BeginEventId; operationId = $_.OperationId
        brush = $_.BrushName; images = @($_.Images | ForEach-Object ToString)
        write = [ordered]@{ left = $_.WriteMask.Left; top = $_.WriteMask.Top; width = $_.WriteMask.Width; height = $_.WriteMask.Height; count = $_.WriteMask.Count }
    } })
    images = @($taskIndex.Images.Values | ForEach-Object { [ordered]@{
        key = $_.Key.ToString(); isCausal = $_.IsCausal; unreplayable = $_.HasUnreplayableEvents; eventCount = $_.Events.Count
        events = @($_.Events | ForEach-Object ToString)
        mask = [ordered]@{ left = $_.Mask.Left; top = $_.Mask.Top; width = $_.Mask.Width; height = $_.Mask.Height; count = $_.Mask.Count }
    } })
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'eligibility-quick.json'), ($taskQuick | ConvertTo-Json -Depth 10))
Write-Host 'Indexed stroke/image locations saved to eligibility-quick.json; evaluating per-contact gates.'
$taskJson = [EligibilityAudit]::Run($taskSession.PSObject.BaseObject,$taskIndex.PSObject.BaseObject)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'eligibility-summary.json'),$taskJson)
$taskSummary = $taskJson | ConvertFrom-Json -Depth 100
$taskSummary.counts | ConvertTo-Json
$taskSummary.strokes | Select-Object beginAppendId,beginEventId,operationId,brush,write | ConvertTo-Json -Depth 6
$taskSummary.images | Select-Object key,isCausal,unreplayable,events,mask | ConvertTo-Json -Depth 6
