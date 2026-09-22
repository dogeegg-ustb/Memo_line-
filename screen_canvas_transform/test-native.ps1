param([switch]$Incremental)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
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
  $sources = @("viewport_frame","transform_solve","workspace_canvas_relation","geometry","canvas_observe","color",
    "features","seeds","background","similarity","grower","scoring","refine","validate","detector")
  foreach ($name in $sources) {
    if ($Incremental -and (Test-Path "$name.obj") -and
        (Get-Item "$name.obj").LastWriteTime -gt (Get-Item "$root\native\src\$name.cpp").LastWriteTime) {continue}
    & $compiler /nologo /std:c++17 /O2 /EHsc /utf-8 /MT "/I$root\native\include" /c "$root\native\src\$name.cpp"
    if ($LASTEXITCODE) { throw "Compile failed: $name" }
  }
  $objects = $sources | ForEach-Object { "$_.obj" }
  $failed = $false
  foreach ($test in @("contract_tests","rotation_regression_tests","workspace_regression_tests")) {
    & $compiler /nologo /std:c++17 /O2 /EHsc /utf-8 /MT "/I$root\native\include" "$root\native\tests\$test.cpp" @objects "/Fe:$test.exe"
    if ($LASTEXITCODE) { throw "Compile failed: $test" }
    & ".\$test.exe"
    if ($LASTEXITCODE) { $failed = $true }
  }
  if ($failed) { throw "Native regression tests failed" }
} finally { Pop-Location }
