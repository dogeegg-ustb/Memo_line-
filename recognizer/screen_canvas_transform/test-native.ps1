param([switch]$Incremental)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$workspaceNative = Join-Path (Split-Path -Parent $root) "workspace_border_detect\native"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw "Visual Studio C++ tools not found" }
$vars = & cmd.exe /d /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && set"
foreach ($line in $vars) {
  if ($line -match '^([^=]+)=(.*)$') {
    [Environment]::SetEnvironmentVariable($matches[1], $matches[2], "Process")
  }
}
$toolVersion = (Get-Content "$vs\VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt").Trim()
$compiler = "$vs\VC\Tools\MSVC\$toolVersion\bin\Hostx64\x64\cl.exe"
$build = Join-Path $root "native\build_regression"
New-Item -ItemType Directory -Force -Path $build | Out-Null
Push-Location $build
try {
  $sources = @("navigator_thumbnail","viewport_frame","transform_solve","workspace_canvas_relation","geometry","canvas_observe","color",
    "features","seeds","background","similarity","grower","scoring","refine","validate","detector")
  $headerTime = (Get-ChildItem "$root\native\include", "$workspaceNative\include" -Recurse -File |
    Measure-Object -Property LastWriteTime -Maximum).Maximum
  foreach ($name in $sources) {
    $sourcePath = if ($name -in @("color","features","seeds","background","similarity","grower","geometry",
        "scoring","refine","validate","detector")) {
      Join-Path $workspaceNative "src\$name.cpp"
    } else {
      Join-Path $root "native\src\$name.cpp"
    }
    if ($Incremental -and (Test-Path "$name.obj") -and
        (Get-Item "$name.obj").LastWriteTime -gt (Get-Item $sourcePath).LastWriteTime -and
        (Get-Item "$name.obj").LastWriteTime -gt $headerTime) {continue}
    & $compiler /nologo /std:c++17 /O2 /EHsc /utf-8 /MT "/I$workspaceNative\include" "/I$root\native\include" /c $sourcePath
    if ($LASTEXITCODE) { throw "Compile failed: $name" }
  }
  $objects = $sources | ForEach-Object { "$_.obj" }
  $failed = $false
  foreach ($test in @("contract_tests","rotation_regression_tests","workspace_regression_tests",
      "navigator_thumbnail_regression_tests","viewport_robustness_tests")) {
    & $compiler /nologo /std:c++17 /O2 /EHsc /utf-8 /MT "/I$workspaceNative\include" "/I$root\native\include" "$root\native\tests\$test.cpp" @objects "/Fe:$test.exe"
    if ($LASTEXITCODE) { throw "Compile failed: $test" }
    & ".\$test.exe"
    if ($LASTEXITCODE) { $failed = $true }
  }
  if ($failed) { throw "Native regression tests failed" }
} finally { Pop-Location }
