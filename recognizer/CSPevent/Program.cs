using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CSPevent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Native.SetProcessDPIAware();
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        Mutex? singleton = null;
        if (args.Length == 0)
        {
            singleton = new Mutex(true, @"Local\CSPevent", out bool acquired);
            if (!acquired)
            {
                singleton.Dispose();
                MessageBox.Show("CSPevent 已在后台运行。请查看系统托盘图标；右键可以切换语言或退出。",
                    "CSPevent 已启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        string database = Path.Combine(AppContext.BaseDirectory, "csp_strings.sqlite3");
        try
        {
            var stringCatalog = new StringCatalog(database);
            var iconCatalog = new IconCatalog(database);
            using var ocr = new OcrReader();
            using var textDetector = new TextPresenceDetector();
            if (args.Length > 1 && args[0] == "--image-test")
            {
                using var image = new Bitmap(args[1]);
                Bitmap? after = null;
                int argOffset = 2;
                if (args.Length > 2 && File.Exists(args[2]))
                {
                    after = new Bitmap(args[2]);
                    argOffset = 3;
                }

                int cx = args.Length > argOffset && int.TryParse(args[argOffset], out int xVal) ? xVal : image.Width / 2;
                int cy = args.Length > (argOffset + 1) && int.TryParse(args[argOffset + 1], out int yVal) ? yVal : image.Height / 2;

                Highlight2DEvidence h2d = HighlightAnalyzer.Analyze2D(image, after, cx, cy);
                HighlightEvidence highlight = h2d.To1D();
                using Bitmap region = CropSelected(image, highlight, cx, cy, "chinese_sc", out Rectangle cropBounds);
                TextPresence textPresence = textDetector.Detect(region,
                    highlight.HasBounds ? null : cx - cropBounds.Left);
                using Bitmap? detectedText = textPresence.HasText ? CropDetectedText(region, textPresence) : null;
                string recognized = detectedText is not null ? ocr.Read(detectedText).Text : "";
                string? matchLabel = textPresence.HasText ? stringCatalog.Match("chinese_sc", recognized).Label : null;

                IconMatchResult iconMatch = textPresence.HasText ? IconMatchResult.NoMatch :
                    IconMatcher.Match(iconCatalog, image, after, cx, cy, h2d, "chinese_sc");
                var iconRect = iconMatch.ButtonRect ?? IconMatcher.DetermineButtonRegion(image, h2d, cx, cy);
                after?.Dispose();

                string testCropPath = Path.Combine(AppContext.BaseDirectory, "events", "latest_match.png");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(testCropPath)!);
                    using var annotated = (Bitmap)image.Clone();
                    Rectangle? box = h2d.Has2DBounds ? h2d.Bounds : iconRect;
                    if (box is not null)
                    {
                        using var g = Graphics.FromImage(annotated);
                        Color boxColor = iconMatch.IsMatched
                            ? Color.FromArgb(220, 60, 180, 255)
                            : Color.FromArgb(200, 255, 180, 60);
                        using var pen = new Pen(boxColor, 1.5f);
                        var br = box.Value;
                        br.Intersect(new Rectangle(0, 0, annotated.Width, annotated.Height));
                        if (br.Width > 0 && br.Height > 0)
                            g.DrawRectangle(pen, br.X, br.Y, br.Width - 1, br.Height - 1);
                    }
                    annotated.Save(testCropPath, ImageFormat.Png);
                }
                catch { }

                string output = $"highlight={h2d.State},reason={h2d.SelectionReason},energy={h2d.DynamicEnergy:F2},bounds={h2d.Bounds}\n" +
                    $"icon_match: id={iconMatch.IconId},label={iconMatch.Label},score={iconMatch.Score:F3},runnerUp={iconMatch.RunnerUpScore:F3},state={iconMatch.State}\n" +
                    $"button_rect={iconRect}\n" +
                    $"text_detected={textPresence.HasText},text_score={textPresence.Score:F3},text_bounds={textPresence.Bounds}\n" +
                    $"ocr={recognized},match={matchLabel}\n" +
                    $"crop={testCropPath}";

                if (args.Length > 2 && !int.TryParse(args[2], out _))
                {
                    File.WriteAllText(args[2], output);
                }
                Console.WriteLine(output);
                return;
            }
            if (args.Length > 0 && args[0] == "--self-test")
            {
                string output = args.Length > 1 ? args[1] :
                    Path.Combine(AppContext.BaseDirectory, "self-test.txt");
                string sc = TestOcr(ocr, "视图(V)", "Microsoft YaHei UI", "chinese_sc", darkMode: true);
                string cn = TestOcr(ocr, "邊界模糊", "Microsoft YaHei UI", "chinese_tc");
                string ja = TestOcr(ocr, "境界をぼかす", "Yu Gothic", "japanese");
                string en = TestOcr(ocr, "Blur border", "Segoe UI", "english");
                string highlight = TestHighlight();
                string highlightPriority = TestHighlightPriority();
                string highlightText = TestHighlightText(ocr);
                string queue = TestCaptureStore();
                string repeatedOcr = TestRepeatedOcr(ocr);
                string iconTest = TestIconMatching(iconCatalog);
                string routingTest = TestTextRouting(textDetector);
                File.WriteAllLines(output, new[] {
                    "database=ok",
                    $"chinese_sc={stringCatalog.Count("chinese_sc")},match={stringCatalog.Match("chinese_sc", sc).Label},ocr_sc={sc}",
                    $"chinese_tc={stringCatalog.Count("chinese_tc")},ocr={ocr.Availability("chinese_tc")}",
                    $"japanese={stringCatalog.Count("japanese")},ocr={ocr.Availability("japanese")}",
                    $"english={stringCatalog.Count("english")},ocr={ocr.Availability("english")}",
                    $"sample={stringCatalog.Match("chinese_tc", "邊界模糊").Label}",
                    $"ocr_cn={cn},match={stringCatalog.Match("chinese_tc", cn).Label}",
                    $"ocr_ja={ja},match={stringCatalog.Match("japanese", ja).Label}",
                    $"ocr_en={en},match={stringCatalog.Match("english", en).Label}",
                    $"highlight={highlight}",
                    $"highlight_priority={highlightPriority}",
                    $"highlight_text={highlightText}",
                    $"queue={queue}",
                    $"repeated_ocr={repeatedOcr}",
                    $"icons={iconTest}",
                    $"text_routing={routingTest}",
                });
                return;
            }
            ApplicationConfiguration.Initialize();
            using var context = new EventContext(stringCatalog, iconCatalog, ocr, textDetector);
            Application.Run(context);
        }
        catch (Exception exception)
        {
            string log = Path.Combine(AppContext.BaseDirectory, "CSPevent-error.log");
            try { File.AppendAllText(log, $"{DateTime.Now:O} {exception}\n"); } catch { }
            if (args.Length > 0 && args[0] == "--self-test")
            {
                if (args.Length > 1) File.WriteAllText(args[1], "self-test failed: " + exception);
                return;
            }
            MessageBox.Show(exception.Message, "CSPevent 启动失败", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally { singleton?.Dispose(); }
    }

    private static string TestOcr(OcrReader ocr, string label, string fontName, string language, bool darkMode = false)
    {
        using var bitmap = new Bitmap(240, 34);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font(fontName, 17))
        {
            graphics.Clear(darkMode ? Color.FromArgb(45, 45, 45) : Color.White);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            graphics.DrawString(label, font, darkMode ? Brushes.LightGray : Brushes.Black, 2, 1);
        }
        return ocr.Read(bitmap).Text;
    }

    private static string TestHighlight()
    {
        using var before = new Bitmap(180, 32);
        using var after = new Bitmap(180, 32);
        using (var graphics = Graphics.FromImage(before)) graphics.Clear(Color.FromArgb(45, 45, 45));
        using (var graphics = Graphics.FromImage(after))
        {
            graphics.Clear(Color.FromArgb(45, 45, 45));
            using var selected = new SolidBrush(Color.FromArgb(50, 110, 190));
            graphics.FillRectangle(selected, 35, 2, 90, 28);
        }
        HighlightEvidence evidence = HighlightAnalyzer.Analyze(before, after, 80);
        HighlightEvidence unchanged = HighlightAnalyzer.Analyze(before, before, 80);
        HighlightEvidence preselected = HighlightAnalyzer.Analyze(after, null, 80);
        return $"{evidence.State},span={evidence.Left}-{evidence.Right}; " +
            $"unchanged={unchanged.State}; preselected={preselected.State}";
    }

    private static string TestHighlightPriority()
    {
        using var frame = new Bitmap(180, 36);
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(Color.FromArgb(45, 45, 45));
            using var nearGray = new SolidBrush(Color.FromArgb(110, 110, 110));
            using var selectedBlue = new SolidBrush(Color.FromArgb(50, 110, 190));
            graphics.FillRectangle(nearGray, 32, 5, 26, 25);
            graphics.FillRectangle(selectedBlue, 96, 5, 30, 25);
        }
        Highlight2DEvidence evidence = HighlightAnalyzer.Analyze2D(frame, null, 45, 17);
        if (!evidence.Has2DBounds || evidence.Bounds!.Value.Left < 90)
            throw new InvalidOperationException($"高光优先级错误：{evidence}");
        return $"selected={evidence.Bounds},reason={evidence.SelectionReason}";
    }

    private static string TestCaptureStore()
    {
        string directory = Path.Combine(AppContext.BaseDirectory,
            "self-test-queue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CaptureStore(directory);
            using var bitmap = new Bitmap(80, 30);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
            for (int i = 0; i < 5; i++)
                store.SaveBefore(bitmap, new Point(i, 4), new Rectangle(0, 0, 80, 30),
                    "left", "english", IntPtr.Zero);
            var (cropPath, latestPath) = store.SaveCrop("test-crop", bitmap);
            bool cropOk = File.Exists(cropPath) && File.Exists(latestPath);
            var recovered = store.LoadPending();
            foreach (CapturedClick entry in recovered)
                store.Complete(entry, "", "", new MatchResult(null, 0, "test"),
                    new HighlightEvidence("unknown", 0, null, null), 0, null, null, cropPath);
            int recorded = File.ReadAllLines(Path.Combine(directory, "events.jsonl")).Length;
            return $"captured={recovered.Count},recorded={recorded},crop={cropOk}";
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static string TestHighlightText(OcrReader ocr)
    {
        using var before = new Bitmap(240, 34);
        using var after = new Bitmap(240, 34);
        using var font = new Font("Segoe UI", 17);
        using (var graphics = Graphics.FromImage(before))
        {
            graphics.Clear(Color.FromArgb(45, 45, 45));
            graphics.DrawString("Blur border", font, Brushes.White, 38, 1);
        }
        using (var graphics = Graphics.FromImage(after))
        {
            graphics.Clear(Color.FromArgb(45, 45, 45));
            using var selected = new SolidBrush(Color.FromArgb(50, 110, 190));
            graphics.FillRectangle(selected, 30, 2, 200, 28);
            graphics.DrawString("Blur border", font, Brushes.White, 38, 1);
        }
        HighlightEvidence evidence = HighlightAnalyzer.Analyze(before, after, 80);
        OcrReadResult text = ocr.Read(before);
        return $"raw={text.Text},span={evidence.Left}-{evidence.Right}," +
            $"selected={HighlightAnalyzer.SelectText(text, evidence)}";
    }

    private static string TestRepeatedOcr(OcrReader ocr)
    {
        using var bitmap = new Bitmap(240, 34);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", 17))
        {
            graphics.Clear(Color.White);
            graphics.DrawString("Blur border", font, Brushes.Black, 2, 1);
        }
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
            if (ocr.Read(bitmap).Text.Length == 0)
                throw new InvalidOperationException($"Repeated OCR returned no text at {i}");
        return $"50 images in {timer.ElapsedMilliseconds} ms";
    }

    private static string TestIconMatching(IconCatalog iconCatalog)
    {
        using var before = new Bitmap(120, 32);
        using var after = new Bitmap(120, 32);
        using (var g = Graphics.FromImage(before))
        {
            g.Clear(Color.FromArgb(45, 45, 45));
            using var brush = new SolidBrush(Color.FromArgb(60, 60, 60));
            g.FillRectangle(brush, 48, 4, 24, 24);
            using var pen = new Pen(Color.White, 1.5f);
            g.DrawRectangle(pen, 52, 8, 12, 16);
            g.DrawLine(pen, 60, 16, 68, 16);
            g.DrawLine(pen, 64, 12, 64, 20);
        }
        using (var g = Graphics.FromImage(after))
        {
            g.Clear(Color.FromArgb(45, 45, 45));
            using var brush = new SolidBrush(Color.FromArgb(30, 110, 190));
            g.FillRectangle(brush, 48, 4, 24, 24);
            using var pen = new Pen(Color.White, 1.5f);
            g.DrawRectangle(pen, 52, 8, 12, 16);
            g.DrawLine(pen, 60, 16, 68, 16);
            g.DrawLine(pen, 64, 12, 64, 20);
        }

        Highlight2DEvidence h2d = HighlightAnalyzer.Analyze2D(before, after, 60, 16);
        IconMatchResult result = IconMatcher.Match(iconCatalog, before, after, 60, 16, h2d, "chinese_tc");
        return $"templates={iconCatalog.Templates.Count},h2d={h2d.State},center={h2d.Center?.X}x{h2d.Center?.Y},icon={result.IconId},label={result.Label},score={result.Score}";
    }

    private static string TestTextRouting(TextPresenceDetector detector)
    {
        using var label = new Bitmap(120, 32);
        using (var graphics = Graphics.FromImage(label))
        {
            graphics.Clear(Color.FromArgb(45, 45, 45));
            using var font = new Font("Microsoft YaHei UI", 14);
            graphics.DrawString("文件", font, Brushes.White, 16, 3);
        }
        using var icon = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(icon))
        {
            graphics.Clear(Color.FromArgb(45, 45, 45));
            using var pen = new Pen(Color.White, 2);
            graphics.DrawRectangle(pen, 5, 5, 21, 21);
            graphics.DrawLine(pen, 16, 10, 16, 22);
            graphics.DrawLine(pen, 10, 16, 22, 16);
        }
        TextPresence text = detector.Detect(label);
        TextPresence symbol = detector.Detect(icon);
        if (!text.HasText || symbol.HasText)
            throw new InvalidOperationException($"文字/图标分流失败：text={text}, icon={symbol}");
        return $"text={text.Score:F2},icon={symbol.HasText}";
    }

    internal static Bitmap CropSelected(Bitmap source, HighlightEvidence highlight,
        int clickX = -1, int clickY = -1, string language = "chinese_sc")
        => CropSelected(source, highlight, clickX, clickY, language, out _);

    internal static Bitmap CropSelected(Bitmap source, HighlightEvidence highlight,
        int clickX, int clickY, string language, out Rectangle cropBounds)
    {
        // 核心依据：高光在哪为核心依据！以高光区域作为裁切和送检的核心依据
        if (highlight.HasBounds && highlight.Bounds.HasValue)
        {
            Rectangle b = highlight.Bounds.Value;
            int left = Math.Max(0, b.Left - 2);
            int right = Math.Min(source.Width, b.Right + 2);
            int top = Math.Max(0, b.Top - 1);
            int bottom = Math.Min(source.Height, b.Bottom + 1);
            var region = new Rectangle(left, top, right - left, bottom - top);
            if (region.Width >= 10 && region.Height >= 10)
            {
                cropBounds = region;
                return source.Clone(region, PixelFormat.Format32bppArgb);
            }
        }
        else if (highlight.HasSelectedSpan)
        {
            int left = Math.Max(0, highlight.Left!.Value - 2);
            int right = Math.Min(source.Width, highlight.Right!.Value + 2);
            int top = highlight.Top.HasValue ? Math.Max(0, highlight.Top.Value - 1) : 0;
            int bottom = highlight.Bottom.HasValue ? Math.Min(source.Height, highlight.Bottom.Value + 1) : source.Height;
            var region = new Rectangle(left, top, right - left, bottom - top);
            cropBounds = region;
            return source.Clone(region, PixelFormat.Format32bppArgb);
        }

        if (clickX < 0)
        {
            cropBounds = new Rectangle(0, 0, source.Width, source.Height);
            return (Bitmap)source.Clone();
        }

        // 辅助判断：若完全没有检测到高光，才以点击坐标为中心裁出局部聚焦区
        int focusWidth = language switch {
            "chinese_sc" or "chinese_tc" => 7 * 24,
            "japanese" => 8 * 22,
            _ => 14 * 14
        };
        focusWidth = Math.Min(focusWidth, source.Width);
        int focusLeft = Math.Clamp(clickX - focusWidth / 2, 0, Math.Max(0, source.Width - focusWidth));
        int focusHeight = Math.Min(32, source.Height);
        int focusTop = clickY >= 0 ? Math.Clamp(clickY - focusHeight / 2, 0, Math.Max(0, source.Height - focusHeight)) : 0;
        var focusRegion = new Rectangle(focusLeft, focusTop, Math.Min(focusWidth, source.Width - focusLeft), Math.Min(focusHeight, source.Height - focusTop));
        cropBounds = focusRegion;
        return source.Clone(focusRegion, PixelFormat.Format32bppArgb);
    }

    internal static Bitmap CropDetectedText(Bitmap source, TextPresence text)
    {
        Rectangle bounds = text.Bounds!.Value;
        // Recognition needs a little surrounding background; a tight detector box
        // clips CJK strokes and can turn otherwise readable labels into gibberish.
        bounds.Inflate(12, 5);
        bounds.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        return source.Clone(bounds, PixelFormat.Format32bppArgb);
    }
}

internal sealed class EventContext : ApplicationContext
{
    private readonly StringCatalog _catalog;
    private readonly IconCatalog _iconCatalog;
    private readonly OcrReader _ocr;
    private readonly TextPresenceDetector _textDetector;
    private readonly StatusOverlay _overlay = new();
    private readonly HistoryFloatingPanel _historyPanel = new();
    private readonly ControlWindow _control;
    private readonly NotifyIcon _tray;
    private readonly Native.HookProc _callback;
    private readonly ConcurrentQueue<CapturedClick> _pending = new();
    private readonly List<(CapturedClick Entry, DateTime Due)> _awaitingPost = new();
    private readonly System.Windows.Forms.Timer _postTimer = new() { Interval = 30 };
    private readonly CaptureStore _store;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private IntPtr _hook;
    private string? _latestCapturedId;
    private string _language = "auto";
    private Bitmap? _lastBeforeCapture;
    private bool _disposed;

    internal EventContext(StringCatalog catalog, IconCatalog iconCatalog, OcrReader ocr,
        TextPresenceDetector textDetector)
    {
        _catalog = catalog;
        _iconCatalog = iconCatalog;
        _ocr = ocr;
        _textDetector = textDetector;
        _store = new CaptureStore(Path.Combine(AppContext.BaseDirectory, "events"));
        _control = new ControlWindow(() => ExitThread());
        _ = _overlay.Handle;
        _ = _historyPanel.Handle;
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示历史操作面板", null, (_, _) => { _historyPanel.Show(); _historyPanel.BringToFront(); });
        menu.Items.Add("查看最新匹配截图", null, (_, _) => {
            string latest = Path.Combine(AppContext.BaseDirectory, "events", "latest_match.png");
            if (File.Exists(latest)) Process.Start(new ProcessStartInfo(latest) { UseShellExecute = true });
        });
        menu.Items.Add("打开切片截图目录 (crops)", null, (_, _) => {
            string crops = Path.Combine(AppContext.BaseDirectory, "events", "crops");
            Directory.CreateDirectory(crops);
            Process.Start(new ProcessStartInfo("explorer.exe", crops) { UseShellExecute = true });
        });
        menu.Items.Add("清空操作历史", null, (_, _) => _historyPanel.ClearHistory());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("显示控制窗口", null, (_, _) => { _control.Show(); _control.BringToFront(); });
        menu.Items.Add(new ToolStripSeparator());
        foreach (var (caption, code) in new[] {
            ("自动识别系统语言", "auto"),
            ("简体中文", "chinese_sc"),
            ("繁体中文", "chinese_tc"),
            ("日本語", "japanese"),
            ("English", "english") })
        {
            string selected = code;
            menu.Items.Add(caption, null, (_, _) => { _language = selected; UpdateMenu(menu); });
            menu.Items[^1].Tag = code;
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        UpdateMenu(menu);
        _tray = new NotifyIcon {
            Icon = SystemIcons.Information,
            Text = "CSPevent：正在监听点击",
            ContextMenuStrip = menu,
            Visible = true
        };
        _callback = OnMouse;
        _hook = Native.SetWindowsHookEx(Native.WhMouseLl, _callback, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"安装鼠标监听失败：{Marshal.GetLastWin32Error()}");
        _postTimer.Tick += (_, _) => CompletePostCaptures();
        _postTimer.Start();
        _worker = Task.Run(ProcessClicks);
        foreach (CapturedClick entry in _store.LoadPending()) Enqueue(entry);
        _historyPanel.Show();
        _historyPanel.BringToFront();
    }

    private void UpdateMenu(ContextMenuStrip menu)
    {
        foreach (ToolStripItem item in menu.Items)
            if (item is ToolStripMenuItem choice && choice.Tag is string code)
                choice.Checked = code == _language;
    }

    private string CurrentLanguage()
    {
        if (_language != "auto") return _language;
        string name = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
        if (name.StartsWith("ja")) return "japanese";
        if (name.StartsWith("zh"))
        {
            if (name.Contains("tw") || name.Contains("hk") || name.Contains("mo") || name.Contains("hant"))
                return "chinese_tc";
            return "chinese_sc";
        }
        return "english";
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message.ToInt32() == Native.WmLButtonDown ||
                          message.ToInt32() == Native.WmRButtonDown ||
                          message.ToInt32() == Native.WmMButtonDown ||
                          message.ToInt32() == Native.WmXButtonDown))
        {
            try
            {
                var mouse = Marshal.PtrToStructure<Native.MouseData>(data);
                IntPtr window = Native.WindowFromPoint(mouse.Point);
                IntPtr root = Native.GetAncestor(window, Native.GaRoot);
                Native.GetWindowThreadProcessId(root, out uint processId);
                if (IsCspProcess(processId))
                {
                    _overlay.Hide();
                    string language = CurrentLanguage();
                    string button = message.ToInt32() switch {
                        Native.WmLButtonDown => "left",
                        Native.WmRButtonDown => "right",
                        Native.WmMButtonDown => "middle",
                        _ => (mouse.MouseInfo >> 16) == 2 ? "x2" : "x1"
                    };
                    var click = new Point(mouse.Point.X, mouse.Point.Y);
                    Bitmap? image = Capture(click, root, language, out Rectangle bounds);
                    if (image is not null)
                    {
                        using (image)
                        {
                            CapturedClick entry = _store.SaveBefore(image, click, bounds,
                                button, language, root);
                            _latestCapturedId = entry.Id;
                            _awaitingPost.Add((entry, DateTime.UtcNow.AddMilliseconds(120)));
                        }
                    }
                }
            }
            catch (Exception exception) { Log(exception); }
        }
        return Native.CallNextHookEx(_hook, code, message, data);
    }

    private static bool IsCspProcess(uint processId)
    {
        if (processId == 0 || processId == Environment.ProcessId) return false;
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("CLIPStudioPaint",
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static Bitmap? Capture(Point click, IntPtr window, string language,
        out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (window == IntPtr.Zero || !Native.GetWindowRect(window, out Native.Rect area)) return null;
        float dpi = Native.GetDpiForWindow(window) / 96f;
        if (dpi < 0.5f || dpi > 4f) dpi = 1f;
        int height = (int)Math.Round(54 * dpi);
        int width = (int)Math.Round((language switch {
            "chinese_sc" or "chinese_tc" => 14 * 24, "japanese" => 16 * 22, _ => 22 * 14 }) * dpi);
        bounds = new Rectangle(click.X - width / 2, click.Y - height / 2, width, height);
        var rootBounds = Rectangle.FromLTRB(area.Left, area.Top, area.Right, area.Bottom);
        bounds.Intersect(rootBounds);
        bounds.Intersect(SystemInformation.VirtualScreen);
        if (bounds.Width < 20 || bounds.Height < 12) return null;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private void CompletePostCaptures()
    {
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < _awaitingPost.Count;)
        {
            var (entry, due) = _awaitingPost[i];
            if (now < due) { i++; continue; }
            _awaitingPost.RemoveAt(i);
            try
            {
                var point = new Native.Point { X = entry.X, Y = entry.Y };
                IntPtr current = Native.GetAncestor(Native.WindowFromPoint(point), Native.GaRoot);
                if (entry.Id == _latestCapturedId &&
                    current.ToInt64() == entry.WindowHandle)
                {
                    var bounds = new Rectangle(entry.Left, entry.Top, entry.Width, entry.Height);
                    using Bitmap image = CaptureExact(bounds);
                    entry = _store.SaveAfter(entry, image);
                }
            }
            catch (Exception exception) { Log(exception); }
            Enqueue(entry);
        }
    }

    private static Bitmap CaptureExact(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private void Enqueue(CapturedClick entry)
    {
        _pending.Enqueue(entry);
        _signal.Release();
    }

    private void ProcessClicks()
    {
        long lastOcrStart = 0;
        while (!_stop.IsCancellationRequested)
        {
            try { _signal.Wait(_stop.Token); }
            catch (OperationCanceledException) { break; }
            if (!_pending.TryDequeue(out CapturedClick? entry)) continue;
            if (lastOcrStart != 0)
            {
                int delay = (int)Math.Ceiling(200 -
                    Stopwatch.GetElapsedTime(lastOcrStart).TotalMilliseconds);
                if (delay > 0 && _stop.Token.WaitHandle.WaitOne(delay)) break;
            }
            lastOcrStart = Stopwatch.GetTimestamp();
            var timer = Stopwatch.StartNew();
            Bitmap? matchCrop = null;
            string? cropPath = null;
            try
            {
                using var before = new Bitmap(entry.BeforePath);
                using var after = entry.AfterPath is not null && File.Exists(entry.AfterPath)
                    ? new Bitmap(entry.AfterPath) : null;

                Highlight2DEvidence highlight2d = HighlightAnalyzer.Analyze2D(before, after,
                    entry.X - entry.Left, entry.Y - entry.Top, _lastBeforeCapture);
                HighlightEvidence highlight = highlight2d.To1D();

                _lastBeforeCapture?.Dispose();
                _lastBeforeCapture = (Bitmap)before.Clone();

                int localX = entry.X - entry.Left;
                using Bitmap region = Program.CropSelected(before, highlight, localX,
                    entry.Y - entry.Top, entry.Language, out Rectangle cropBounds);
                TextPresence textPresence = _textDetector.Detect(region,
                    highlight.HasBounds ? null : localX - cropBounds.Left);
                using Bitmap? detectedText = textPresence.HasText
                    ? Program.CropDetectedText(region, textPresence) : null;
                OcrReadResult ocr = detectedText is not null
                    ? _ocr.Read(detectedText) : new OcrReadResult([]);
                string selectedText = ocr.Text;
                MatchResult match = textPresence.HasText
                    ? _catalog.Match(entry.Language, selectedText)
                    : new MatchResult(null, 0, "未检测到文字区域");

                IconMatchResult? iconMatch = null;
                // 文字区域由检测器独立确认；OCR 失败不能把同一区域改判为图标。
                if (!textPresence.HasText)
                {
                    iconMatch = IconMatcher.Match(_iconCatalog, before, after,
                        entry.X - entry.Left, entry.Y - entry.Top, highlight2d, entry.Language);
                }

                // 生成匹配截屏：以完整 before 截图为底，用彩色矩形标注实际匹配的子区域
                // 核心依据：高光在哪为最高优先级标注依据！
                Rectangle? annotateRect = null;
                if (highlight2d.Has2DBounds)
                {
                    annotateRect = highlight2d.Bounds;
                }
                else if (iconMatch is not null && (iconMatch.IsMatched || iconMatch.IsCandidate))
                {
                    annotateRect = iconMatch.ButtonRect ?? IconMatcher.DetermineButtonRegion(before, highlight2d, entry.X - entry.Left, entry.Y - entry.Top);
                }
                else if (match.Label is not null && highlight.HasSelectedSpan)
                {
                    int left = Math.Max(0, highlight.Left!.Value - 2);
                    int right = Math.Min(before.Width, highlight.Right!.Value + 2);
                    int top = highlight.Top ?? 0;
                    int bottom = highlight.Bottom ?? before.Height;
                    annotateRect = new Rectangle(left, top, right - left, bottom - top);
                }
                else
                {
                    annotateRect = IconMatcher.DetermineButtonRegion(before, highlight2d, entry.X - entry.Left, entry.Y - entry.Top);
                }

                matchCrop = (Bitmap)before.Clone();
                if (annotateRect is not null)
                {
                    using var g = Graphics.FromImage(matchCrop);
                    Color boxColor = iconMatch is not null && (iconMatch.IsMatched || iconMatch.IsCandidate)
                        ? Color.FromArgb(200, 60, 180, 255)   // 图标匹配：蓝色框
                        : match.Label is not null
                            ? Color.FromArgb(200, 80, 230, 120) // 文字匹配：绿色框
                            : Color.FromArgb(200, 255, 180, 60); // 未识别：橙色框
                    using var pen = new Pen(boxColor, 1.5f);
                    var r = annotateRect.Value;
                    r.Intersect(new Rectangle(0, 0, matchCrop.Width, matchCrop.Height));
                    if (r.Width > 0 && r.Height > 0)
                        g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                }

                (cropPath, _) = _store.SaveCrop(entry.Id, matchCrop);

                timer.Stop();
                _store.Complete(entry, ocr.Text, selectedText, match, highlight,
                    timer.ElapsedMilliseconds, null, iconMatch, cropPath, textPresence);

                string evidence = highlight.State switch {
                    "prehighlight" => "点击时已高亮",
                    "highlight_changed" => "2D高光确认",
                    _ => "高光未确认"
                };

                if (iconMatch is not null && (iconMatch.IsMatched || iconMatch.IsCandidate))
                {
                    string statusTag = iconMatch.IsMatched ? "图标匹配" : "图标候选";
                    string title = $"{iconMatch.Label}" + (string.IsNullOrEmpty(iconMatch.CommandHint) ? "" : $" [{iconMatch.CommandHint}]");
                    string detail = $"{evidence} · {statusTag} · 置信度: {iconMatch.Score:F2} · 耗时 {timer.ElapsedMilliseconds}ms";
                    if (!_historyPanel.IsDisposed)
                    {
                        Bitmap? img = matchCrop;
                        matchCrop = null; // transfer ownership to panel
                        _historyPanel.BeginInvoke(() => _historyPanel.AddAction("icon", title, detail, img, cropPath));
                    }
                }
                else if (match.Label is not null)
                {
                    string title = match.Label;
                    string detail = $"{evidence} · 原文: {selectedText} · 耗时 {timer.ElapsedMilliseconds}ms";
                    if (!_historyPanel.IsDisposed)
                    {
                        Bitmap? img = matchCrop;
                        matchCrop = null;
                        _historyPanel.BeginInvoke(() => _historyPanel.AddAction("text", title, detail, img, cropPath));
                    }
                }
                else
                {
                    string btn = entry.Button switch { "left" => "鼠标左键", "right" => "鼠标右键", "middle" => "鼠标中键", _ => entry.Button };
                    string title = $"{btn}点击 (X:{entry.X}, Y:{entry.Y})";
                    string detail = $"{evidence} · 未识别文字/图标 · 耗时 {timer.ElapsedMilliseconds}ms";
                    if (!_historyPanel.IsDisposed)
                    {
                        Bitmap? img = matchCrop;
                        matchCrop = null;
                        _historyPanel.BeginInvoke(() => _historyPanel.AddAction("click", title, detail, img, cropPath));
                    }
                }
            }
            catch (Exception exception)
            {
                Log(exception);
                try
                {
                    _store.Complete(entry, "", "", new MatchResult(null, 0, "error"),
                        new HighlightEvidence("unknown", 0, null, null),
                        timer.ElapsedMilliseconds, exception.Message);
                }
                catch (Exception writeError) { Log(writeError); }
                if (!_historyPanel.IsDisposed)
                    _historyPanel.BeginInvoke(() => _historyPanel.AddAction("click", "点击处理异常", $"错误: {exception.Message}", null, null, Color.FromArgb(90, 25, 25), Color.FromArgb(255, 120, 120)));
            }
            finally
            {
                matchCrop?.Dispose();
            }
        }
    }

    private static void Log(Exception exception)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "CSPevent-error.log"),
            $"{DateTime.Now:O} {exception}\n"); } catch { }
    }

    protected override void ExitThreadCore()
    {
        if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _postTimer.Stop();
        _stop.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(30)); } catch (AggregateException) { }
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _stop.Cancel();
            _lastBeforeCapture?.Dispose();
            _lastBeforeCapture = null;
            _postTimer.Dispose();
            _tray.Dispose();
            _historyPanel.Dispose();
            _overlay.Dispose();
            _control.Dispose();
            _signal.Dispose();
            _stop.Dispose();
        }
        base.Dispose(disposing);
    }
}
