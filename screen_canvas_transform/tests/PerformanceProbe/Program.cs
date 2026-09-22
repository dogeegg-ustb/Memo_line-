using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Interop;
using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Ocr;

internal class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Observe(in NativeSct.SctCanvasObserveRequest request,
        ref NativeSct.SctCanvasObservation result);

    static async Task Main(string[] args)
    {
        if (args[0] == "native") { ProbeNative(args.Skip(1).ToArray()); return; }
        if (args[0] == "ocr-image")
        {
            NavigatorOcrService.DebugEnabled = false;
            var reader = new NavigatorOcrService();
            using var image = new Bitmap(args[1]);
            int[] c = args.Skip(2).Select(int.Parse).ToArray();
            var layout = new OcrLayoutScreen(new IntRect(c[0], c[1], c[2], c[3]),
                new IntRect(c[4], c[5], c[6], c[7]));
            string? expected = null;
            for (int repeat=0; repeat<3; ++repeat)
            {
                using var session = new CaptureSession(Guid.NewGuid().ToString("N"),
                    (Bitmap)image.Clone(), new IntRect(0,0,image.Width,image.Height),96,96);
                var sw=Stopwatch.StartNew();
                var reading=await reader.ReadWithLayoutAsync(session,layout);
                string signature=$"{reading.ScalePercent}|{reading.RotationDegrees}|{reading.ScaleConfidence}|{reading.RotationConfidence}|{reading.ScaleRawText}|{reading.RotationRawText}";
                expected ??= signature;
                if(signature!=expected || reading.SourceCaptureId!=session.CaptureId) throw new Exception("Screenshot replay differs");
                Console.WriteLine($"REAL OCR repeat={repeat} {signature} {sw.Elapsed.TotalMilliseconds:F2}ms");
            }
            return;
        }
        // Exercise unchanged slots, a change in each number, changed ROI origin,
        // blank OCR failure and return to an earlier image. Fresh services are the
        // uncached reference for each reading; metadata must always be current.
        NavigatorOcrService.DebugEnabled = false;
        var shared = new NavigatorOcrService();
        foreach (var (scale, rotation, left) in new[] {
            ("27.5", "0.0", 0), ("27.5", "0.0", 0),
            ("66.7", "0.0", 0), ("66.7", "15.0", 0),
            ("66.7", "15.0", 3), ("", "", 0), ("27.5", "0.0", 0) })
        {
            using var frame = new Bitmap(160, 76, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(frame))
            using (var font = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.Clear(Color.FromArgb(60, 60, 60));
                g.DrawString(scale, font, Brushes.White, 10 + left, 4);
                g.DrawString(rotation, font, Brushes.White, 10 + left, 42);
            }
            using var session = new CaptureSession(Guid.NewGuid().ToString("N"),
                (Bitmap)frame.Clone(), new IntRect(-200, 30, -40, 106), 96, 96);
            var layout = new OcrLayoutScreen(new IntRect(-200 + left, 30, -70, 68),
                new IntRect(-200 + left, 68, -70, 106));
            var sw = Stopwatch.StartNew();
            var expected = await new NavigatorOcrService().ReadWithLayoutAsync(session, layout);
            double referenceMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var actual = await shared.ReadWithLayoutAsync(session, layout);
            double sharedMs = sw.Elapsed.TotalMilliseconds;
            string Signature(NavigatorNumericReadingDto r) =>
                $"{r.ScalePercent}|{r.RotationDegrees}|{r.ScaleConfidence}|{r.RotationConfidence}|{r.ScaleRawText}|{r.RotationRawText}";
            if (Signature(expected) != Signature(actual) || actual.SourceCaptureId != session.CaptureId)
                throw new Exception("OCR fresh/shared result or capture binding differs");
            Console.WriteLine($"OCR [{scale},{rotation},left={left}] {Signature(actual)} reference={referenceMs:F1}ms shared={sharedMs:F1}ms");
        }
    }

    static void ProbeNative(string[] paths)
    {
        var modules = paths.Select(NativeLibrary.Load).ToArray();
        try
        {
            foreach (bool noise in new[] { false, true })
            foreach (bool navigator in new[] { false, true })
            {
                int width = navigator ? 340 : 1765, height = navigator ? 330 : 1282;
                byte[] pixels = new byte[width * height * 4];
                var rng = new Random(4621);
                for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    int i = (y * width + x) * 4;
                    byte c = x >= width / 4 && y >= height / 5 ? (byte)255 : (byte)45;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = c;
                    if (noise && c == 255) { pixels[i] = (byte)rng.Next(256); pixels[i+1] = (byte)rng.Next(256); pixels[i+2] = (byte)rng.Next(256); }
                    pixels[i + 3] = 255;
                }
                var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    var request = new NativeSct.SctCanvasObserveRequest {
                        Bgra = pinned.AddrOfPinnedObject(), Width = width, Height = height, Stride = width*4,
                        RoiCapture = new NativeSct.SctIntRect { Right=width, Bottom=height }, OriginX=-200, OriginY=30,
                        Background = new NativeSct.SctBackgroundModel { CenterLabL=18.47f, WeakDeltaE=12, StrongDeltaE=6, Confidence=1 }, DpiScale=1 };
                    string? reference = null;
                    for(int m=0; m<modules.Length; ++m)
                    {
                        var fn = Marshal.GetDelegateForFunctionPointer<Observe>(NativeLibrary.GetExport(modules[m], navigator ? "sct_observe_navigator_canvas" : "sct_observe_canvas"));
                        var result = new NativeSct.SctCanvasObservation { AmbiguityReason="" };
                        fn(in request, ref result);
                        var samples = new List<double>();
                        for(int k=0; k<5; ++k) { var sw=Stopwatch.StartNew(); fn(in request, ref result); samples.Add(sw.Elapsed.TotalMilliseconds); }
                        string signature = JsonSerializer.Serialize(result, new JsonSerializerOptions { IncludeFields=true });
                        reference ??= signature;
                        if(signature != reference) throw new Exception("Native observation changed: " + signature);
                        samples.Sort();
                        Console.WriteLine($"NATIVE noise={noise} navigator={navigator} dll={m} median={samples[2]:F2}ms identical=True");
                    }
                }
                finally { pinned.Free(); }
            }
        }
        finally { foreach(var module in modules) NativeLibrary.Free(module); }
    }
}
