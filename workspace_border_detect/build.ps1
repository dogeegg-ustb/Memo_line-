# Build native DLL (MSVC cl) then C# WPF exe.
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
    "geometry.cpp","scoring.cpp","refine.cpp","validate.cpp","detector.cpp","c_api.cpp"
  ) | ForEach-Object { Join-Path $Native "src\$_" }
  $incs = "/I$(Join-Path $Native 'include')"
  $common = @("/nologo","/std:c++17","/O2","/EHsc","/utf-8","/MT","/DWB_NATIVE_EXPORTS",$incs)
  foreach ($s in $srcs) {
    $name = [IO.Path]::GetFileNameWithoutExtension($s)
    & cl.exe @common /c $s /Fo"$name.obj"
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $name" }
  }
  $objs = $srcs | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) + ".obj" }
  & link.exe /nologo /DLL /OUT:WorkspaceBorderNative.dll /IMPLIB:WorkspaceBorderNative.lib @objs /EXPORT:wb_detect /EXPORT:wb_status_name
  if ($LASTEXITCODE -ne 0) { throw "link dll failed" }

  & cl.exe @common /c (Join-Path $Native "src\smoke_main.cpp") /Fosmoke_main.obj
  & link.exe /nologo /OUT:wb_smoke.exe smoke_main.obj WorkspaceBorderNative.lib
  & .\wb_smoke.exe
  if ($LASTEXITCODE -ne 0) { Write-Warning "wb_smoke failed (exit $LASTEXITCODE)" }
} finally {
  Pop-Location
}

$dll = Join-Path $Build "WorkspaceBorderNative.dll"
if (-not (Test-Path $dll)) { throw "WorkspaceBorderNative.dll missing" }

$nativeOut = Join-Path $App "Native"
New-Item -ItemType Directory -Force -Path $nativeOut | Out-Null
Copy-Item $dll (Join-Path $nativeOut "WorkspaceBorderNative.dll") -Force
Copy-Item $dll (Join-Path $Native "build\WorkspaceBorderNative.dll") -Force -ErrorAction SilentlyContinue

Push-Location $App
try {
  dotnet build -c $Configuration -p:Platform=x64
  if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
} finally {
  Pop-Location
}

$exe = Get-ChildItem -Path (Join-Path $App "bin") -Recurse -Filter WorkspaceBorderDetect.exe |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "WorkspaceBorderDetect.exe not found" }
Copy-Item $dll (Join-Path $exe.DirectoryName "WorkspaceBorderNative.dll") -Force
Write-Host "OK: $($exe.FullName)"
Write-Host "DLL: $(Join-Path $exe.DirectoryName 'WorkspaceBorderNative.dll')"
