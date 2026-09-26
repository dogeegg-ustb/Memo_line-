param(
  [Parameter(Mandatory=$true)][string]$CapturePng,
  [Parameter(Mandatory=$true)][int[]]$Roi,
  [float]$Dpi=96
)
$ErrorActionPreference='Stop'
if($Roi.Count -ne 4) {throw 'Roi requires left,top,right,bottom'}
Add-Type -AssemblyName System.Drawing
$bitmap=[System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $CapturePng).Path)
$data=$null
$rawPath=Join-Path $PSScriptRoot '../native/build_regression/replay.bgra'
try {
  $rect=[System.Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height)
  $data=$bitmap.LockBits($rect,[System.Drawing.Imaging.ImageLockMode]::ReadOnly,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $bytes=[byte[]]::new($bitmap.Width*$bitmap.Height*4)
  for($y=0;$y -lt $bitmap.Height;$y++) {
    [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0,$y*$data.Stride),
      $bytes,$y*$bitmap.Width*4,$bitmap.Width*4)
  }
  [IO.File]::WriteAllBytes($rawPath,$bytes)
  & "$PSScriptRoot/../native/build_regression/workspace_regression_tests.exe" $rawPath $bitmap.Width $bitmap.Height @Roi $Dpi
  if($LASTEXITCODE) {throw 'Workspace replay failed; see native diagnostics'}
} finally {
  if($null -ne $data) {$bitmap.UnlockBits($data)}
  $bitmap.Dispose()
}
