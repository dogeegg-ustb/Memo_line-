# Build ScreenCanvasNative.dll (MSVC) then C# WPF host.
# workspace_border_detect algorithm sources are compiled into ScreenCanvasNative
# (color/features/.../detector.cpp) — no WorkspaceBorderNative.dll, no wb_* exports.
param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Native = Join-Path $Root "native"
$App = Join-Path $Root "app"
$Build = Join-Path $Native "build_src"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere not found" }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio C++ tools not found" }

Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=x64" | Out-Null

New-Item -ItemType Directory -Force -Path $Build | Out-Null
Push-Location $Build
try {
  $srcs = @(
    "color.cpp","features.cpp","seeds.cpp","background.cpp","similarity.cpp","grower.cpp",
    "geometry.cpp","scoring.cpp","refine.cpp","validate.cpp","detector.cpp",
    "canvas_observe.cpp","workspace_canvas_relation.cpp","viewport_frame.cpp","transform_solve.cpp","sct_c_api.cpp"
  ) | ForEach-Object { Join-Path $Native "src\$_" }
  $incs = @("/I$(Join-Path $Native 'include')")
  $common = @("/nologo","/std:c++17","/O2","/EHsc","/utf-8","/MT","/DSCT_NATIVE_EXPORTS") + $incs
  foreach ($s in $srcs) {
    if (-not (Test-Path $s)) { throw "missing source: $s" }
    $name = [IO.Path]::GetFileNameWithoutExtension($s)
    & cl.exe @common /c $s /Fo"$name.obj"
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $name" }
  }
  $objs = $srcs | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) + ".obj" }
  $exports = @(
    "/EXPORT:sct_api_version",
    "/EXPORT:sct_status_name",
    "/EXPORT:sct_source_revision",
    "/EXPORT:sct_detect_workspace",
    "/EXPORT:sct_detect_navigator_thumbnail_cii",
    "/EXPORT:sct_observe_canvas",
    "/EXPORT:sct_build_workspace_canvas_relation",
    "/EXPORT:sct_complete_viewport_frame",
    "/EXPORT:sct_solve_transform"
  )
  & link.exe /nologo /DLL /OUT:ScreenCanvasNative.dll /IMPLIB:ScreenCanvasNative.lib @objs @exports
  if ($LASTEXITCODE -ne 0) { throw "link dll failed" }
} finally {
  Pop-Location
}

$dll = Join-Path $Build "ScreenCanvasNative.dll"
if (-not (Test-Path $dll)) { throw "ScreenCanvasNative.dll missing" }

$nativeOut = Join-Path $App "Native"
New-Item -ItemType Directory -Force -Path $nativeOut | Out-Null
Copy-Item $dll (Join-Path $nativeOut "ScreenCanvasNative.dll") -Force
New-Item -ItemType Directory -Force -Path (Join-Path $Native "build") | Out-Null
Copy-Item $dll (Join-Path $Native "build\ScreenCanvasNative.dll") -Force

Push-Location $App
try {
  dotnet build -c $Configuration -p:Platform=x64
  if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
} finally {
  Pop-Location
}

$exe = Get-ChildItem -Path (Join-Path $App "bin") -Recurse -Filter ScreenCanvasTransform.exe |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "ScreenCanvasTransform.exe not found" }
Copy-Item $dll (Join-Path $exe.DirectoryName "ScreenCanvasNative.dll") -Force
Write-Host "OK: $($exe.FullName)"
Write-Host "DLL: $(Join-Path $exe.DirectoryName 'ScreenCanvasNative.dll')"

# Native contract tests
$testSrc = Join-Path $Native "tests\contract_tests.cpp"
$testObjs = @(
  (Join-Path $Build "transform_solve.obj"),
  (Join-Path $Build "workspace_canvas_relation.obj"),
  (Join-Path $Build "geometry.obj")
)
& cl.exe @common /c $testSrc /Fo"$Build\contract_tests.obj"
if ($LASTEXITCODE -ne 0) { throw "contract test compile failed" }
& link.exe /nologo /OUT:$Build\contract_tests.exe "$Build\contract_tests.obj" @testObjs
if ($LASTEXITCODE -ne 0) { throw "contract test link failed" }
& $Build\contract_tests.exe
if ($LASTEXITCODE -ne 0) { throw "contract tests failed" }
