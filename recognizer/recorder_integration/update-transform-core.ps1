param([switch]$SkipCoreBuild)
$ErrorActionPreference = 'Stop'
$integration = $PSScriptRoot
$recognizer = Split-Path $integration -Parent
$core = Join-Path $recognizer 'recognizer_core/screen_canvas_transform'
if (!$SkipCoreBuild) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $core 'build.ps1')
    if ($LASTEXITCODE) { throw 'Transform core build/tests failed' }
}
$hostProject = Join-Path $integration 'transform_host/TransformHost.csproj'
$publish = Join-Path $integration 'transform_host/publish'
& dotnet publish $hostProject -c Release -r win-x64 --self-contained true --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false -o $publish
if ($LASTEXITCODE) { throw 'TransformHost publish failed' }

# OCR resources belong to the caller. Prefer the existing host's model bundle;
# the development app is only a fallback when creating a fresh installation.
$models = Join-Path $publish 'models/v5'
if (!(Test-Path -LiteralPath $models)) {
    $models = Join-Path $recognizer 'Recognizer/publish/win-x64/integration/transform_host/models/v5'
    if (!(Test-Path -LiteralPath $models)) {
        $models = Join-Path $recognizer 'screen_canvas_transform/app/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/models/v5'
    }
}
$requiredModels = @('ch_PP-OCRv5_mobile_det.onnx','ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx',
    'latin_PP-OCRv5_rec_mobile_infer.onnx','ppocrv5_latin_dict.txt')
foreach ($name in $requiredModels) {
    if (!(Test-Path -LiteralPath (Join-Path $models $name))) { throw "Missing OCR model: $name" }
}
$publishedModels = Join-Path $publish 'models/v5'
if ([IO.Path]::GetFullPath($models) -ne [IO.Path]::GetFullPath($publishedModels)) {
    New-Item -ItemType Directory -Force -Path $publishedModels | Out-Null
    foreach ($name in $requiredModels) {
        Copy-Item -LiteralPath (Join-Path $models $name) -Destination $publishedModels -Force
    }
}
$targets = @(
    (Join-Path $recognizer 'Recognizer/publish/win-x64/integration/transform_host'),
    (Join-Path $recognizer 'Recognizer/src/BehaviorRecognizer/bin/Release/net10.0/integration/transform_host')
)
foreach ($package in @('win-x64-savefix','win-x64-injected-input')) {
    $target = Join-Path $recognizer "Recognizer/publish/$package/integration/transform_host"
    if (Test-Path -LiteralPath $target) { $targets += $target }
}
$expectedNative = (Get-FileHash -LiteralPath (Join-Path $core 'ScreenCanvasNative.dll')).Hash
if ((Get-FileHash -LiteralPath (Join-Path $publish 'ScreenCanvasNative.dll')).Hash -ne $expectedNative) {
    throw 'Published native DLL differs from the rebuilt core'
}
foreach ($target in $targets) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    # Skip unchanged runtime/model files, which Windows may still map briefly
    # after a host exits. Only changed files need replacement on a repeated run.
    $publishRoot = [IO.Path]::GetFullPath($publish).TrimEnd('\','/')
    foreach ($file in Get-ChildItem -LiteralPath $publish -Recurse -File) {
        $relative = $file.FullName.Substring($publishRoot.Length).TrimStart('\','/')
        $destination = Join-Path $target $relative
        if ((Test-Path -LiteralPath $destination) -and
            (Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $destination).Hash) {
            continue
        }
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    foreach ($name in @('ScreenCanvasNative.dll','ScreenCanvasTransform.Core.dll','TransformHost.dll')) {
        if ((Get-FileHash -LiteralPath (Join-Path $publish $name)).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $target $name)).Hash) { throw "Deployed file mismatch: $name" }
    }
    Write-Host "Updated transform core: $target"
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $core 'Tests/TransformHostSmoke.ps1') -HostDirectory $targets[0]
if ($LASTEXITCODE) { throw 'Deployed TransformHost smoke failed' }
