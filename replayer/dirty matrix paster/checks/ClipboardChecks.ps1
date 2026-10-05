param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/clipboard-smoke'))
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PasterClipboardProbe {
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr memory);
    public static byte[] Read(uint format) {
        if (!OpenClipboard(IntPtr.Zero)) throw new Exception("Cannot open clipboard");
        try {
            var handle=GetClipboardData(format); if(handle==IntPtr.Zero) throw new Exception("Missing native format "+format);
            var count=checked((int)GlobalSize(handle).ToUInt64()); var pointer=GlobalLock(handle);
            if(pointer==IntPtr.Zero) throw new Exception("Cannot lock clipboard memory");
            try { var bytes=new byte[count]; Marshal.Copy(pointer,bytes,0,count); return bytes; }
            finally { GlobalUnlock(handle); }
        } finally { CloseClipboard(); }
    }
    public static void Equal(byte[] actual, byte[] expected) {
        if(actual.Length!=expected.Length) throw new Exception("Clipboard byte length differs");
        for(int i=0;i<actual.Length;i++) if(actual[i]!=expected[i]) throw new Exception("Clipboard bytes differ at "+i);
    }
    public static void Pixel(byte[] dib,int offset,byte[] rgba,int source) {
        if(dib[offset]!=rgba[source+2] || dib[offset+1]!=rgba[source+1] || dib[offset+2]!=rgba[source] || dib[offset+3]!=rgba[source+3])
            throw new Exception("DIBV5 RGBA or coordinate mismatch");
    }
}
'@
$directory = [System.IO.Path]::GetFullPath($OutputDirectory)
$report = Get-Content -Raw -LiteralPath (Join-Path $directory 'layer.json') | ConvertFrom-Json
$envelope = [PasterClipboardProbe]::Read([PasterClipboardProbe]::RegisterClipboardFormat('MemoLine.DirtyMatrix.Layer.v1'))
[PasterClipboardProbe]::Equal($envelope,[IO.File]::ReadAllBytes((Join-Path $directory 'layer.dmlayer')))
if ($report.psdPath) {
    $psd = [PasterClipboardProbe]::Read([PasterClipboardProbe]::RegisterClipboardFormat('image/vnd.adobe.photoshop'))
    [PasterClipboardProbe]::Equal($psd,[IO.File]::ReadAllBytes($report.psdPath))
}
$png = [PasterClipboardProbe]::Read([PasterClipboardProbe]::RegisterClipboardFormat('PNG'))
[PasterClipboardProbe]::Equal($png,[IO.File]::ReadAllBytes($report.clipboardImagePath))
$dib = [PasterClipboardProbe]::Read(17)
if ([BitConverter]::ToUInt32($dib,0) -ne 124 -or [BitConverter]::ToInt32($dib,4) -ne $report.canvas.width -or [BitConverter]::ToInt32($dib,8) -ne -$report.canvas.height -or $dib.Length -ne 124 + $report.canvas.width * $report.canvas.height * 4) { throw 'Invalid native CF_DIBV5 header or canvas size' }
$rgba = [IO.File]::ReadAllBytes($report.rgbaPath)
$mask = [IO.File]::ReadAllBytes($report.maskPath)
$samples = @([Array]::IndexOf($mask,[byte]255),[Array]::LastIndexOf($mask,[byte]255),[Array]::IndexOf($mask,[byte]0))
foreach ($pixel in $samples) {
    if ($pixel -lt 0) { continue }
    $x = $pixel % $report.width; $y = [int][Math]::Floor($pixel / $report.width)
    $offset = 124 + (($y + $report.bounds.top) * $report.canvas.width + $x + $report.bounds.left) * 4
    [PasterClipboardProbe]::Pixel($dib,$offset,$rgba,$pixel*4)
}
Write-Output 'PASS: native clipboard envelope, PNG, CF_DIBV5 (17), composed RGBA and full-canvas coordinates; writer process has already exited.'
