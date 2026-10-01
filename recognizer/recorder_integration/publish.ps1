$ErrorActionPreference = 'Stop'
$integrationRoot = $PSScriptRoot
$recognizerRoot = Split-Path $integrationRoot -Parent
$project = Join-Path $recognizerRoot 'Recognizer/src/BehaviorRecognizer/BehaviorRecognizer.csproj'
$destination = Join-Path $recognizerRoot 'Recognizer/publish/win-x64'
$build = Join-Path $recognizerRoot 'Recognizer/src/BehaviorRecognizer/bin/Release/net10.0'
$transformPublish = Join-Path $integrationRoot 'transform_host/publish'
dotnet build $project -c Release --no-restore
if ($LASTEXITCODE) { throw 'Recorder build failed' }
dotnet publish (Join-Path $integrationRoot 'transform_host/TransformHost.csproj') -c Release -r win-x64 --self-contained true --no-restore -o $transformPublish
if ($LASTEXITCODE) { throw 'Transform host publish failed' }
$models = Join-Path $recognizerRoot 'screen_canvas_transform/app/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/models/v5'
if (!(Test-Path -LiteralPath $models)) { throw 'Missing local PP-OCRv5 model directory' }
New-Item -ItemType Directory -Path (Join-Path $transformPublish 'models/v5') -Force | Out-Null
Copy-Item -Path "$models/*" -Destination (Join-Path $transformPublish 'models/v5') -Force
foreach ($target in @($build,$destination)) {
    $helper = Join-Path $target 'integration'
    New-Item -ItemType Directory -Path $helper -Force | Out-Null
    # Remove the retired whole-window capture helper from previous builds.
    $retiredCapture = Join-Path $helper 'csp_capture.py'
    if (Test-Path -LiteralPath $retiredCapture) {
        Remove-Item -LiteralPath $retiredCapture -Force
    }
    if ($target -eq $destination) {
        Copy-Item -LiteralPath (Join-Path $build 'BehaviorRecognizer.dll'),(Join-Path $build 'BehaviorRecognizer.pdb') -Destination $target -Force
        # Preserve the user's editable settings across publish operations.
        Get-ChildItem -LiteralPath (Join-Path $build 'integration') | Where-Object Name -ne 'settings.json' | Copy-Item -Destination $helper -Recurse -Force
        if (!(Test-Path -LiteralPath (Join-Path $helper 'settings.json'))) {
            Copy-Item -LiteralPath (Join-Path $integrationRoot 'settings.json') -Destination $helper
        }
    }
    New-Item -ItemType Directory -Path (Join-Path $helper 'transform_host') -Force | Out-Null
    Copy-Item -Path "$transformPublish/*" -Destination (Join-Path $helper 'transform_host') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $integrationRoot 'python_libs') -Destination $helper -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $integrationRoot 'README.md') -Destination $helper -Force
}
