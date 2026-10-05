param(
    [Parameter(Mandatory=$true)][string]$HostDirectory,
    [Parameter(Mandatory=$true)][string[]]$JobFiles,
    [Parameter(Mandatory=$true)][int]$CanvasWidth,
    [Parameter(Mandatory=$true)][int]$CanvasHeight,
    [switch]$RecordedSequence
)
$ErrorActionPreference='Stop'
$inputs=@()
foreach ($file in $JobFiles) {
    $job=Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $payload=@{
        initialize=$(if ($RecordedSequence) { [bool]$job.initialize } else { $true }); width=$CanvasWidth; height=$CanvasHeight; dpi=$job.dpi
        frame=$job.crops.'__canvas_frame__'
        crops=@{workspace=$job.crops.'画布视口'; navigator=$job.crops.'导航器'; numbers=$job.crops.'导航器数字'}
    }
    $inputs += $payload | ConvertTo-Json -Depth 12 -Compress
    if (!$RecordedSequence) {
        $payload.initialize=$false
        $inputs += $payload | ConvertTo-Json -Depth 12 -Compress
    }
}
$lines=@($inputs | & (Join-Path $HostDirectory 'TransformHost.exe'))
if ($LASTEXITCODE -or $lines.Count -ne $inputs.Count) { throw 'Host process/JSONL response count failed' }
for ($i=0;$i -lt $lines.Count;$i++) {
    $result=$lines[$i] | ConvertFrom-Json
    if (!$result.success) { throw "Real recorder replay failed: $($lines[$i])" }
    Write-Host "Replay $i OK: origin=($($result.canvasOriginScreenPx.x),$($result.canvasOriginScreenPx.y)) scale=$($result.ocrScalePercent) rotation=$($result.ocrRotationDegrees)"
}
