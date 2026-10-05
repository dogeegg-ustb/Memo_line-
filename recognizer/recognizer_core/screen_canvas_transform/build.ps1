param([string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$native = Join-Path $root 'native'
$border = Join-Path $native 'workspace_border'
$build = Join-Path $native 'build_core'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (!(Test-Path -LiteralPath $vswhere)) { throw 'vswhere not found' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ tools not found' }
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
New-Item -ItemType Directory -Force -Path $build | Out-Null
Push-Location $build
try {
    $sources = @('color','features','seeds','background','similarity','grower','geometry',
        'scoring','refine','validate','detector') | ForEach-Object { Join-Path $border "src/$_.cpp" }
    $sources += @('navigator_thumbnail','canvas_observe','workspace_canvas_relation',
        'viewport_frame','transform_solve','sct_c_api') | ForEach-Object { Join-Path $native "src/$_.cpp" }
    $flags = @('/nologo','/std:c++17','/O2','/EHsc','/utf-8','/MT','/DSCT_NATIVE_EXPORTS',
        "/I$border/include", "/I$native/include")
    foreach ($source in $sources) {
        $name = [IO.Path]::GetFileNameWithoutExtension($source)
        & cl.exe @flags /c $source "/Fo$name.obj"
        if ($LASTEXITCODE) { throw "Native compile failed: $name" }
    }
    $objects = $sources | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) + '.obj' }
    & link.exe /nologo /DLL /OUT:ScreenCanvasNative.dll /IMPLIB:ScreenCanvasNative.lib @objects
    if ($LASTEXITCODE) { throw 'Native link failed' }
    if (!$SkipTests) {
        foreach ($test in @('contract_tests','rotation_regression_tests','workspace_regression_tests',
            'navigator_thumbnail_regression_tests','viewport_robustness_tests')) {
            & cl.exe @flags "$native/tests/$test.cpp" @objects "/Fe:$test.exe"
            if ($LASTEXITCODE) { throw "Test compile failed: $test" }
            & ".\$test.exe"
            if ($LASTEXITCODE) { throw "Test failed: $test" }
        }
    }
    Copy-Item -LiteralPath (Join-Path $build 'ScreenCanvasNative.dll') -Destination (Join-Path $root 'ScreenCanvasNative.dll') -Force
} finally { Pop-Location }
& dotnet build (Join-Path $root 'ScreenCanvasTransform.Core.csproj') -c $Configuration -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false --ignore-failed-sources
if ($LASTEXITCODE) { throw 'Managed core build failed' }
if (!$SkipTests) {
    & dotnet test (Join-Path $root 'Tests/ScreenCanvasTransform.Core.Tests.csproj') -c $Configuration -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:RestoreIgnoreFailedSources=true -p:NuGetAudit=false
    if ($LASTEXITCODE) { throw 'Managed core tests failed' }
}
