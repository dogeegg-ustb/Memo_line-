param([Parameter(Mandatory=$true)][string]$HostDirectory)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $HostDirectory 'TransformHost.exe'
if (!(Test-Path -LiteralPath $exe)) { throw "Missing host: $exe" }
Add-Type -AssemblyName System.Drawing
$fixture = Join-Path ([IO.Path]::GetTempPath()) ("sct-host-smoke-" + [Guid]::NewGuid().ToString('N') + '.png')
$bitmap = New-Object System.Drawing.Bitmap 256,256
try {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try { $graphics.Clear([System.Drawing.Color]::FromArgb(40,40,40)) } finally { $graphics.Dispose() }
    $bitmap.Save($fixture, [System.Drawing.Imaging.ImageFormat]::Png)
    $request = @{
        initialize=$true; width=2000; height=1000
        frame=@{path=$fixture; roi=@(-1920,-100,256,256)}
        crops=@{
            workspace=@{roi=@(-1900,-80,200,200)}
            navigator=@{roi=@(-1880,-60,96,96)}
            numbers=@{roi=@(-1880,40,64,64)}
        }
    }
    $lines = @('{invalid', ($request | ConvertTo-Json -Compress -Depth 8))
    $request.initialize=$false
    $lines += $request | ConvertTo-Json -Compress -Depth 8
    $output = @($lines | & $exe)
    if ($LASTEXITCODE) { throw 'Host process failed' }
    if ($output.Count -ne 3) { throw "Expected three JSONL responses, got $($output.Count)" }
    $responses = @($output | ForEach-Object { $_ | ConvertFrom-Json })
    if ($responses[0].success -ne $false) { throw 'Malformed input must return a failure response' }
    # A uniform frame cannot identify a canvas. It must yield a structured
    # pipeline failure rather than a missing DLL, ABI or runtime exception.
    if ($responses[1].success -ne $false -or !$responses[1].failedStage -or !$responses[1].status) {
        throw "Blank-frame pipeline did not return stage/status: $($output[1])"
    }
    if ($responses[2].success -ne $false -or $responses[2].message -notmatch 'Initialize successfully') {
        throw "Failed initialization must not allow recompute: $($output[2])"
    }
    Write-Host "Host JSONL smoke passed: $exe"
} finally {
    $bitmap.Dispose()
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture }
}
