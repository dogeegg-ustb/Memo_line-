using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using CanvasLayerWatcher;

namespace MemolineDemo.Editing;

/// <summary>
/// Offline timing of the pixel work around an edit (--bench-png PNG…): GDI+ against FastPng for decoding,
/// encoding and the clipboard image, each checked to give identical pixels.
/// </summary>
internal static partial class PerformanceProbe
{
    internal static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args[0] == "--live-replay-test") return LiveReplayTest(args);
        if (args[0] == "--live-transaction-test") return LiveTransactionTest(args);
        if (args[0] == "--live-shield-test") return LiveShieldTest(args);
        if (args[0] == "--bench-diff") return BenchDiff(args);
        bool ok = true;
        foreach (string path in args.Skip(1))
        {
            Console.WriteLine("== " + path);
            byte[] png = File.ReadAllBytes(path);
            var gdi = Time("GDI+ 解码", () => DecodeGdi(png, out _, out _));
            var fast = Time("FastPng 解码", () => FastPng.TryDecode(png, bgra: true, out _, out _) ?? throw new InvalidDataException("FastPng 不支持该格式"));
            ok &= Same("解码像素一致", gdi, fast);
            FastPng.TryReadSize(png, out int width, out int height);
            var rgba = fast.ToArray(); FastPng.SwapRedBlue(rgba);
            var gdiPng = Time("GDI+ 编码", () => ClipboardGdiPng(width, height, rgba));
            var fastPng = Time("FastPng 编码", () => FastPng.Encode(width, height, rgba, bgra: false));
            Console.WriteLine($"  PNG 大小：GDI+ {gdiPng.Length >> 10} KB，FastPng {fastPng.Length >> 10} KB");
            ok &= Same("FastPng 编码后 GDI+ 解码一致", gdi, DecodeGdi(fastPng, out _, out _));
            ok &= Same("FastPng 编码后自解码一致", rgba, FastPng.TryDecode(fastPng, bgra: false, out _, out _)!);
            Time("剪贴板 DIBV5 编码", () => DirtyMatrixPaster.ClipboardImageCodec.EncodeDibV5(width, height, rgba));
            Time("CRC/块校验", () => FastPng.ChunksValid(png));
            using (var original = Image.FromStream(new MemoryStream(png)))
            {
                var size = CanvasThumbnail.SizeFor(new(width, height));
                var thumbGdi = Time("缩略图（GDI+ 解码源）", () => Thumb(original, size));
                var handlePixels = fast.ToArray();
                using var wrapped = FastPng.Wrap(handlePixels, width, height, out var handle);
                try
                {
                    var thumbFast = Time("缩略图（FastPng 像素源）", () => Thumb(wrapped, size));
                    ok &= Same("缩略图一致", thumbGdi, thumbFast);
                }
                finally { handle.Free(); }
            }
        }
        Console.WriteLine(ok ? "全部一致。" : "存在不一致！");
        return ok ? 0 : 2;
    }

    /// <summary>
    /// --live-replay-test CLIP 录制.memoline 笔数 左 上 [每笔毫秒…]：在 CSP 当前打开的 CLIP（必须是可以随意修改的副本）
    /// 上，每种回放速度各粘贴一个空图层并重放同一批录制笔画（平移到屏幕上 左,上 处），保存后导出逐像素比较。
    /// 0 表示按录制时间（旧行为，笔间 30 ms）；其他值为每笔时长（笔间 15 ms）。
    /// </summary>
    private static int LiveReplayTest(string[] args)
    {
        // Screen coordinates as the recorder and the editing transaction use them (the app sets this in its UI start-up).
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        string clip = Path.GetFullPath(args[1]);
        var document = StrokeReplay.MemolineReplayReader.Read(Path.GetFullPath(args[2]));
        int count = int.Parse(args[3]);
        int left = int.Parse(args[4]), top = int.Parse(args[5]);
        // 变体：0 = 录制时间；N = 每笔 N ms；N/M = 每笔 N ms 且采样点至少相隔 M ms；N/M/T/S/R = 另按 T 像素简化笔迹、落笔后等待 S ms、每点后与 CSP 同步 R 轮。
        string[] variants = args.Length > 6 ? args.Skip(6).ToArray() : ["0", "20"];
        static string Label(string variant) => variant == "0" ? "录制时间" : variant + " ms";
        static StrokeReplay.ReplayOptions Options(string variant)
        {
            var parts = variant.Split('/').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return parts[0] <= 0 ? new(StrokeReplay.ReplayMode.Discrete, .03, FixedGap: true)
                : new(StrokeReplay.ReplayMode.Discrete, .015, FixedGap: true, parts[0] / 1000, parts.Length > 1 ? parts[1] / 1000 : 0,
                    parts.Length > 2 ? parts[2] : 0, parts.Length > 3 ? parts[3] / 1000 : 0, parts.Length > 4 ? (int)parts[4] : 0);
        }
        var injectorWatch = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) StrokeReplay.PenReplayEngine.CreateSyntheticInjector().Dispose();
        Console.WriteLine($"创建+释放合成笔设备：每次 {injectorWatch.Elapsed.TotalMilliseconds / 10:0.0} ms");
        var strokes = document.Strokes.Where(s => s.Samples.Count >= 2).Take(count).ToArray();
        var contacts = strokes.SelectMany(s => s.Samples.Where(p => p.InContact)).ToArray();
        double dx = left - contacts.Min(p => p.X), dy = top - contacts.Min(p => p.Y);
        strokes = strokes.Select(s => s with { Samples = s.Samples.Select(p => p with { X = p.X + dx, Y = p.Y + dy }).ToArray() }).ToArray();
        Console.WriteLine($"{strokes.Length} 笔，范围 {left},{top} – {contacts.Max(p => p.X) + dx:0},{contacts.Max(p => p.Y) + dy:0}，" +
            $"录制时长合计 {strokes.Sum(s => (s.EndTicks - s.StartTicks) / (double)document.Frequency):0.00} s");
        var csp = LiveCsp.Find();
        string bridge = Path.Combine(AppContext.BaseDirectory, "clip-layer-bridge.exe");
        if (!File.Exists(bridge)) bridge = @"D:\Memo_Line\Memo_Line\Memoline_demo_csponly\clip-layer-bridge.exe";
        var (width, height) = (4961, 7016);
        byte[] emptyPng = FastPng.Encode(width, height, new byte[width * height * 4], bgra: false);
        LiveCsp.Activate(csp);
        HashSet<string> Uuids()
        {
            string snapshot = Path.Combine(Path.GetDirectoryName(clip)!, "replay-test-" + Guid.NewGuid().ToString("N")[..8] + ".clip");
            File.Copy(clip, snapshot);
            using var parsed = System.Text.Json.JsonDocument.Parse(LiveCsp.Run(bridge, "inspect", snapshot));
            return parsed.RootElement.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("uuid").GetString()!).ToHashSet();
        }
        var existing = Uuids();
        foreach (string variant in variants)
        {
            LiveCsp.Activate(csp);
            DirtyMatrixPaster.ClipboardImageCodec.Publish(emptyPng, null);
            LiveCsp.Combo(csp, Keys.ControlKey, Keys.D); LiveCsp.Combo(csp, Keys.ControlKey, Keys.V);
            LiveCsp.Save(csp, clip);
            var options = Options(variant);
            int kept = strokes.Sum(s => options.Simplify(s).Samples.Count(p => p.InContact)), total = strokes.Sum(s => s.Samples.Count(p => p.InContact));
            Console.WriteLine($"  [{Label(variant)}] 接触采样 {kept}/{total}，录制采样平均间隔 {strokes.Sum(s => (s.EndTicks - s.StartTicks) / (double)document.Frequency) * 1000 / total:0.00} ms");
            var watch = Stopwatch.StartNew();
            var each = new List<double>();
            for (int i = 0; i < strokes.Length; i++)
            {
                var gap = options.GapBetween(strokes[i].EndTicks, i + 1 < strokes.Length ? strokes[i + 1].StartTicks : null, document.Frequency);
                var one = Stopwatch.StartNew();
                StrokeReplay.PenReplayEngine.ReplayStrokeAsync(strokes[i], document.Frequency, 1, _ =>
                {
                    if (!LiveCsp.Foreground(csp)) throw new InvalidOperationException("CSP 不在前台，停止重放。");
                    return Task.CompletedTask;
                }, CancellationToken.None, options, followingGap: gap).GetAwaiter().GetResult();
                each.Add(one.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"[{Label(variant)}] 重放 {watch.Elapsed.TotalMilliseconds:0} ms，" +
                $"每笔含间隔 最短 {each.Min():0} / 平均 {each.Average():0} / 最长 {each.Max():0} ms");
        }
        Thread.Sleep(500);
        LiveCsp.Save(csp, clip);
        string copy = Path.Combine(Path.GetDirectoryName(clip)!, "replay-test-" + Guid.NewGuid().ToString("N")[..8] + ".clip");
        File.Copy(clip, copy);
        using var inspection = System.Text.Json.JsonDocument.Parse(LiveCsp.Run(bridge, "inspect", copy));
        // Each paste lands above the previous one: the first variant's layer is the lowest of the new ones.
        var rows = inspection.RootElement.GetProperty("layers").EnumerateArray()
            .Where(l => !existing.Contains(l.GetProperty("uuid").GetString()!))
            .OrderBy(l => l.TryGetProperty("stackOrder", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number ? s.GetInt64() : long.MaxValue)
            .Reverse().ToArray();
        if (rows.Length != variants.Length) throw new InvalidDataException($"新增了 {rows.Length} 个图层，应为 {variants.Length} 个（空图层粘贴可能被 CSP 忽略）。");
        var images = rows.Select(row =>
        {
            string png = Path.ChangeExtension(copy, null) + "-" + row.GetProperty("id").GetInt64() + ".png";
            LiveCsp.Run(bridge, "export-id", copy, row.GetProperty("id").GetInt64().ToString(), png, width.ToString(), height.ToString());
            return FastPng.TryDecodeFile(png, false, out _, out _)!;
        }).ToArray();
        int x0 = Math.Max(0, width), y0 = height, x1 = 0, y1 = 0;
        long inked = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            if (images[0][(y * width + x) * 4 + 3] != 0) { inked++; x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        Console.WriteLine($"基准层（{Label(variants[0])}）有墨像素 {inked}，范围 {x0},{y0}–{x1},{y1}");
        for (int v = 1; v < images.Length; v++)
        {
            long differ = 0, alphaOnlyA = 0, alphaOnlyB = 0, inkB = 0; int maxDelta = 0;
            for (int i = 0; i < images[0].Length; i += 4)
            {
                byte aa = images[0][i + 3], ab = images[v][i + 3];
                if (ab != 0) inkB++;
                if (aa == ab && (aa == 0 || images[0].AsSpan(i, 3).SequenceEqual(images[v].AsSpan(i, 3)))) continue;
                differ++; maxDelta = Math.Max(maxDelta, Math.Abs(aa - ab));
                if (aa != 0 && ab == 0) alphaOnlyA++; else if (aa == 0 && ab != 0) alphaOnlyB++;
            }
            Console.WriteLine($"  对比 {Label(variants[v])}：有墨像素 {inkB}（{(inked == 0 ? 0 : 100.0 * inkB / inked):0.0}%），不同像素 {differ}（{(inked == 0 ? 0 : 100.0 * differ / inked):0.0}% of 基准墨），" +
                $"只在基准有墨 {alphaOnlyA}，只在新层有墨 {alphaOnlyB}，最大 alpha 差 {maxDelta}");
        }
        string output = Path.Combine(Path.GetDirectoryName(clip)!, "replay-test-crops");
        Directory.CreateDirectory(output);
        int cw = Math.Max(1, x1 - x0 + 41), ch = Math.Max(1, y1 - y0 + 41), cx = Math.Max(0, x0 - 20), cy = Math.Max(0, y0 - 20);
        cw = Math.Min(cw, width - cx); ch = Math.Min(ch, height - cy);
        for (int v = 0; v < images.Length; v++)
        {
            var crop = new byte[cw * ch * 4];
            for (int y = 0; y < ch; y++)
            {
                images[v].AsSpan(((cy + y) * width + cx) * 4, cw * 4).CopyTo(crop.AsSpan(y * cw * 4));
                for (int x = 0; x < cw; x++) // composite over white so the crop is readable
                {
                    int p = (y * cw + x) * 4; int a = crop[p + 3];
                    for (int c = 0; c < 3; c++) crop[p + c] = (byte)((crop[p + c] * a + 255 * (255 - a)) / 255);
                    crop[p + 3] = 255;
                }
            }
            File.WriteAllBytes(Path.Combine(output, $"variant-{v}-{variants[v].Replace('/', '_')}.png"), FastPng.Encode(cw, ch, crop, bgra: false));
        }
        Console.WriteLine("裁切图：" + output);
        return 0;
    }

    private static class LiveCsp
    {
        internal static IntPtr Find()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var text = new StringBuilder(256); GetWindowText(h, text, 256);
                if (!text.ToString().Contains("CLIP STUDIO PAINT")) return true;
                GetWindowThreadProcessId(h, out uint pid);
                try { if (Process.GetProcessById((int)pid).ProcessName.Equals("CLIPStudioPaint", StringComparison.OrdinalIgnoreCase)) { found = h; return false; } }
                catch (ArgumentException) { }
                return true;
            }, IntPtr.Zero);
            return found != IntPtr.Zero ? found : throw new InvalidOperationException("找不到 CSP 窗口。");
        }
        internal static bool Foreground(IntPtr csp)
        { GetWindowThreadProcessId(GetForegroundWindow(), out uint a); GetWindowThreadProcessId(csp, out uint b); return a == b; }
        internal static void Activate(IntPtr csp)
        {
            if (Foreground(csp)) return;
            SetForegroundWindow(csp);
            for (int i = 0; i < 50 && !Foreground(csp); i++) Thread.Sleep(20);
            if (!Foreground(csp)) throw new InvalidOperationException("CSP 不在前台。");
            Thread.Sleep(150);
        }
        internal static void Combo(IntPtr csp, params Keys[] keys)
        {
            if (!Foreground(csp)) throw new InvalidOperationException("CSP 不在前台，拒绝发送按键。");
            var input = new StrokeReplay.WindowsViewInput();
            input.SendShortcutAsync(keys.Select(k => (ushort)k).ToArray(), CancellationToken.None).GetAwaiter().GetResult();
        }
        internal static void Save(IntPtr csp, string clip)
        {
            var before = new FileInfo(clip).LastWriteTimeUtc;
            Combo(csp, Keys.ControlKey, Keys.S);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 8000 && new FileInfo(clip).LastWriteTimeUtc == before) Thread.Sleep(10);
            Thread.Sleep(100);
        }
        internal static string Run(string exe, params string[] args)
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            string output = p.StandardOutput.ReadToEnd(); string error = p.StandardError.ReadToEnd(); p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(exe + "：" + error);
            return output;
        }
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    }

    private static T Time<T>(string label, Func<T> action)
    {
        action(); // warm up
        var watch = Stopwatch.StartNew();
        var result = action();
        Console.WriteLine($"  {label,-22} {watch.Elapsed.TotalMilliseconds,8:0.0} ms");
        return result;
    }

    private static bool Same(string label, byte[] a, byte[] b)
    {
        bool same = a.AsSpan().SequenceEqual(b);
        Console.WriteLine($"  {label}：{(same ? "是" : "否")}");
        return same;
    }

    internal static byte[] DecodeGdi(byte[] png, out int width, out int height)
    {
        using var bitmap = new Bitmap(new MemoryStream(png));
        width = bitmap.Width; height = bitmap.Height;
        var bytes = new byte[width * height * 4];
        var data = bitmap.LockBits(new(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < height; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * width * 4, width * 4); }
        finally { bitmap.UnlockBits(data); }
        return bytes;
    }

    private static byte[] ClipboardGdiPng(int width, int height, byte[] rgba)
    {
        var bgra = rgba.ToArray(); FastPng.SwapRedBlue(bgra);
        using var bitmap = FastPng.Wrap(bgra, width, height, out var handle);
        try { using var output = new MemoryStream(); bitmap.Save(output, ImageFormat.Png); return output.ToArray(); }
        finally { handle.Free(); }
    }

    private static byte[] Thumb(Image source, Size size)
    {
        using var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(Point.Empty, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
        var bytes = new byte[size.Width * size.Height * 4];
        var data = image.LockBits(new(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < size.Height; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * size.Width * 4, size.Width * 4); }
        finally { image.UnlockBits(data); }
        return bytes;
    }
}
