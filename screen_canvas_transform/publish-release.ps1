param(
  [string]$OutputDirectory = "",
  [switch]$SkipNativeBuild
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$App = Join-Path $Root "app"
$NativeBuild = Join-Path $Root "native\build_release"
$Project = Join-Path $App "ScreenCanvasTransform.csproj"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = Join-Path $Root "releases\ScreenCanvasTransform-20260922-precision"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

if (-not $SkipNativeBuild) {
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "build.ps1") `
    -Configuration Release -NativeBuildDirectory $NativeBuild
  if ($LASTEXITCODE -ne 0) { throw "Release build failed" }
}

$nativeDll = Join-Path $NativeBuild "ScreenCanvasNative.dll"
if (-not (Test-Path -LiteralPath $nativeDll)) {
  throw "Native release DLL missing: $nativeDll"
}
if (Test-Path -LiteralPath $OutputDirectory) {
  throw "Refusing to overwrite an existing release directory: $OutputDirectory"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

Push-Location $Root
try {
  & dotnet publish $Project -c Release -r win-x64 --self-contained true --no-restore `
    -p:Platform=x64 -o $OutputDirectory
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
} finally {
  Pop-Location
}

# Keep the native dependency at the probe root used by NativeSct.DllName.
Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $OutputDirectory "ScreenCanvasNative.dll") -Force

$required = @(
  "ScreenCanvasTransform.exe",
  "ScreenCanvasTransform.dll",
  "ScreenCanvasTransform.deps.json",
  "ScreenCanvasTransform.runtimeconfig.json",
  "ScreenCanvasNative.dll"
)
foreach ($name in $required) {
  $path = Join-Path $OutputDirectory $name
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Required release file missing: $name"
  }
}

$manifestPath = Join-Path $OutputDirectory "RELEASE-MANIFEST.txt"
$manifest = Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse |
  Where-Object { $_.FullName -ne $manifestPath } |
  Sort-Object FullName |
  ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = $_.FullName.Substring($OutputDirectory.Length).TrimStart('\','/')
    "$hash  $relative"
  }
Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding utf8

$zipPath = "$OutputDirectory.zip"
if (Test-Path -LiteralPath $zipPath) {
  throw "Refusing to overwrite an existing release archive: $zipPath"
}
Compress-Archive -Path (Join-Path $OutputDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal

$fileCount = (Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse).Count
Write-Host "Release directory: $OutputDirectory"
Write-Host "Release archive:   $zipPath"
Write-Host "Release files:     $fileCount"
