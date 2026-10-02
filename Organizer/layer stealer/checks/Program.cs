using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using LayerStealer.Capture;
using LayerStealer.UI;
using LayerStealer.Windows;

int count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    count++;
}
void Reject(byte[] bytes, string name)
{
    try { using var image = DibDecoder.Decode(bytes); throw new Exception("Accepted invalid data: " + name); }
    catch (InvalidDataException) { count++; }
}

// DIBV5 with a bottom-up 2x2 image and explicit alpha. Pixel values intentionally distinct.
byte[] v5 = new byte[124 + 16];
Put32(v5, 0, 124); Put32(v5, 4, 2); Put32(v5, 8, 2);
Put16(v5, 12, 1); Put16(v5, 14, 32); Put32(v5, 16, 3);
Put32(v5, 40, 0x00FF0000); Put32(v5, 44, 0x0000FF00); Put32(v5, 48, 0x000000FF); Put32(v5, 52, 0xFF000000);
Put32(v5, 124, 0x8000FF00); Put32(v5, 128, 0xFF0000FF); // bottom: half-alpha green, opaque blue
Put32(v5, 132, 0xFFFF0000); Put32(v5, 136, 0x00000000); // top: opaque red, transparent
using (var image = DibDecoder.Decode(v5))
{
    Check(image.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(), "Bottom-up orientation");
    Check(image.GetPixel(1, 0).A == 0, "Transparent pixel preserved");
    Check(image.GetPixel(0, 1).A == 128 && image.GetPixel(0, 1).G == 255, "Partial alpha preserved");
    using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png);
    using var png = ClipboardImageReader.DecodePng(stream.ToArray());
    Check(png.GetPixel(0, 1).A == 128 && png.GetPixel(1, 0).A == 0, "PNG alpha round trip");
}
byte[] topDown = (byte[])v5.Clone(); Put32(topDown, 8, unchecked((uint)-2));
using (var image = DibDecoder.Decode(topDown)) Check(image.GetPixel(0, 0).G == 255, "Top-down orientation");
byte[] transparent = (byte[])v5.Clone(); Array.Clear(transparent, 124, 16);
using (var image = DibDecoder.Decode(transparent)) Check(image.GetPixel(0, 0).A == 0, "Fully transparent V5 stays transparent");

// 24-bit 1x2: each three-byte pixel has a padding byte.
byte[] rgb24 = new byte[48]; Put32(rgb24, 0, 40); Put32(rgb24, 4, 1); Put32(rgb24, 8, 2);
Put16(rgb24, 12, 1); Put16(rgb24, 14, 24); rgb24[40] = 255; rgb24[46] = 255;
using (var image = DibDecoder.Decode(rgb24))
    Check(image.GetPixel(0, 0).R == 255 && image.GetPixel(0, 1).B == 255, "24-bit padding and orientation");

byte[] rgb32 = new byte[44]; Put32(rgb32, 0, 40); Put32(rgb32, 4, 1); Put32(rgb32, 8, 1);
Put16(rgb32, 12, 1); Put16(rgb32, 14, 32); Put32(rgb32, 40, 0x00FF0000);
using (var image = DibDecoder.Decode(rgb32)) Check(image.GetPixel(0, 0).A == 255, "BI_RGB reserved alpha byte is opaque");

// Indexed 1/4/8-bit DIBs, including a row that does not align on a byte boundary.
foreach (ushort bits in new ushort[] { 1, 4, 8 })
{
    byte[] indexed = new byte[52]; Put32(indexed, 0, 40); Put32(indexed, 4, 3); Put32(indexed, 8, 1);
    Put16(indexed, 12, 1); Put16(indexed, 14, bits); Put32(indexed, 32, 2); indexed[46] = 255;
    if (bits == 1) indexed[48] = 0b10100000;
    if (bits == 4) { indexed[48] = 0x10; indexed[49] = 0x10; }
    if (bits == 8) { indexed[48] = 1; indexed[50] = 1; }
    using var image = DibDecoder.Decode(indexed);
    Check(image.GetPixel(0, 0).R == 255 && image.GetPixel(1, 0).R == 0 && image.GetPixel(2, 0).R == 255, $"{bits}-bit palette");
}

// RGB565, external BITFIELDS masks and row padding.
byte[] rgb565 = new byte[56]; Put32(rgb565, 0, 40); Put32(rgb565, 4, 1); Put32(rgb565, 8, 1);
Put16(rgb565, 12, 1); Put16(rgb565, 14, 16); Put32(rgb565, 16, 3);
Put32(rgb565, 40, 0xF800); Put32(rgb565, 44, 0x07E0); Put32(rgb565, 48, 0x001F); Put16(rgb565, 52, 0x07E0);
using (var image = DibDecoder.Decode(rgb565)) Check(image.GetPixel(0, 0).G == 255, "RGB565 masks");

Reject(v5[..^1], "Truncated V5 pixels"); Reject(new byte[20], "Truncated header");
byte[] huge = (byte[])v5.Clone(); Put32(huge, 4, int.MaxValue); Reject(huge, "Oversized allocation");
byte[] minHeight = (byte[])v5.Clone(); Put32(minHeight, 8, 0x80000000); Reject(minHeight, "Int32.MinValue height");
byte[] overlap = (byte[])v5.Clone(); Put32(overlap, 44, 0x00FF0000); Reject(overlap, "Overlapping masks");
byte[] compressed = (byte[])v5.Clone(); Put32(compressed, 16, 1); Reject(compressed, "Unsupported compression");
byte[] profile = (byte[])v5.Clone(); Put32(profile, 112, 128); Put32(profile, 116, 4); Reject(profile, "Overlapping profile");

Check(!ClipboardImageReader.IsFresh(42, 100, 42, 100), "Reject stale clipboard");
Check(!ClipboardImageReader.IsFresh(43, 101, 42, 100), "Reject another application's clipboard");
Check(ClipboardImageReader.IsFresh(43, 100, 42, 100), "Accept fresh CSP clipboard");
Check(ClipboardImageReader.IsFresh(0, 100, uint.MaxValue, 100), "Sequence wraparound");
Check(ClipboardImageReader.IsFresh(42, 101, null, null), "Manual read accepts existing clipboard");
Check(System.Runtime.InteropServices.Marshal.SizeOf<Native.Input>() == 40, "Win32 x64 INPUT layout");
Check(CspTarget.IsInScope(10, 100, true, 20, 100), "CSP popup HWND accepted");
Check(CspTarget.IsInScope(10, 100, true, 30, 100), "CSP owned drawing window accepted");
Check(!CspTarget.IsInScope(10, 100, true, 20, 101), "Other process rejected");
Check(!CspTarget.IsInScope(10, 100, true, 0, 0), "Missing foreground rejected");
Check(!CspTarget.IsInScope(10, 100, false, 20, 100), "Closed target rejected");

var settings = new Settings();
Check(settings.CopyPoint(new(-1800, -200, 1200, 800)) == new Point(-1200, 200), "Negative-coordinate monitor center");
Check(new Settings(.25, .75, true).CopyPoint(new(100, 200, 401, 401)) == new Point(200, 500), "Relative calibration");

// Record the real script executor's calls without emitting OS input or touching CSP.
List<ScriptStep> sent = [];
await CopyScript.ExecuteAsync(CopyScript.Steps(180), sent.Add, CancellationToken.None,
    (_, _) => Task.CompletedTask, _ => throw new Exception("Unexpected release"));
Check(sent.Select(s => (s.Key, s.Down)).SequenceEqual(new[] {
    (ScriptKey.Control, true), (ScriptKey.RightButton, true), (ScriptKey.RightButton, false),
    (ScriptKey.C, true), (ScriptKey.C, false), (ScriptKey.Control, false)
}), "Ctrl held across right-click then C");
List<ScriptKey> released = [];
try
{
    await CopyScript.ExecuteAsync(CopyScript.Steps(180), step =>
    {
        if (step.Key == ScriptKey.RightButton && !step.Down) throw new InvalidOperationException("Injected failure");
    }, CancellationToken.None, (_, _) => Task.CompletedTask, released.Add);
    throw new Exception("Expected script failure");
}
catch (InvalidOperationException)
{ Check(released.Contains(ScriptKey.Control) && released.Contains(ScriptKey.RightButton), "Failure releases held Ctrl and right button"); }
using var cancellation = new CancellationTokenSource(); released.Clear();
try
{
    await CopyScript.ExecuteAsync(CopyScript.Steps(180), _ => { }, cancellation.Token,
        (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }, released.Add);
    throw new Exception("Expected script cancellation");
}
catch (OperationCanceledException) { Check(released.SequenceEqual(new[] { ScriptKey.Control }), "Cancellation releases Ctrl"); }

// Reproduce CSP moving foreground ownership to a new popup after the right-click.
// Exercise the whole executor through its per-step foreground check.
nint foreground = 10;
uint foregroundProcess = 100;
sent.Clear(); released.Clear();
await CopyScript.ExecuteAsync(CopyScript.Steps(180), step =>
{
    sent.Add(step);
    if (step is { Key: ScriptKey.RightButton, Down: false }) foreground = 20;
}, CancellationToken.None, (_, _) => Task.CompletedTask, released.Add, _ =>
{
    if (!CspTarget.IsInScope(10, 100, true, foreground, foregroundProcess)) throw new InvalidOperationException("Foreground left target");
    return Task.CompletedTask;
});
Check(sent.Count == 6 && sent.Any(step => step is { Key: ScriptKey.C, Down: true }), "Popup transition completes C and Ctrl release");
Check(released.Count == 0, "Popup transition needs no emergency key release");

foreground = 10; foregroundProcess = 100; sent.Clear(); released.Clear();
try
{
    await CopyScript.ExecuteAsync(CopyScript.Steps(180), step =>
    {
        sent.Add(step);
        if (step is { Key: ScriptKey.RightButton, Down: false }) { foreground = 20; foregroundProcess = 101; }
    }, CancellationToken.None, (_, _) => Task.CompletedTask, released.Add, _ =>
    {
        if (!CspTarget.IsInScope(10, 100, true, foreground, foregroundProcess)) throw new InvalidOperationException("Foreground left target");
        return Task.CompletedTask;
    });
    throw new Exception("Expected foreground rejection");
}
catch (InvalidOperationException)
{
    Check(!sent.Any(step => step.Key == ScriptKey.C) && released.SequenceEqual(new[] { ScriptKey.Control }),
        "Switch to another process stops before C and releases Ctrl");
}

Console.WriteLine($"PASS: {count} checks. No CSP input or system clipboard writes.");
static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
